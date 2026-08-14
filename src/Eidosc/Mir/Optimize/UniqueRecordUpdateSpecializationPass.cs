using Eidosc.Borrow;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Specializes direct calls whose managed arguments have a single live owner,
/// then marks consuming record updates in those variants for direct mutation.
/// General call sites keep the original COW implementation.
/// </summary>
public sealed class UniqueRecordUpdateSpecializationPass : IMirOptimizationPass
{
    private const int MaxSpecializedVariants = 256;

    public string Name => "UniqueRecordUpdateSpecialization";

    public MirModule Run(MirModule module)
    {
        var originals = module.Functions
            .Where(static function => !function.IsExternal && function.BasicBlocks.Count > 0)
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        if (originals.Count == 0)
        {
            return module;
        }

        var specializationCandidates = FindSpecializationCandidates(module, originals);
        var analysisKeys = CollectAnalysisKeys(originals, specializationCandidates);

        var variants = analysisKeys.ToDictionary(
            static key => key,
            key => new Variant(
                key,
                originals[key],
                new HashSet<int>(),
                FunctionAnalysisContext.Create(originals[key])),
            StringComparer.Ordinal);
        var variantsByTemplate = analysisKeys.ToDictionary(
            static key => key,
            key => new Dictionary<string, Variant>(StringComparer.Ordinal)
            {
                [BuildAssumptionKey([])] = variants[key]
            },
            StringComparer.Ordinal);
        var returnIsUnique = variants.Keys.ToDictionary(static key => key, static _ => false, StringComparer.Ordinal);

        var changed = true;
        var iteration = 0;
        while (changed && iteration++ < MaxSpecializedVariants)
        {
            changed = false;
            var variantSnapshot = variants.Values.ToArray();
            var analyses = new Dictionary<string, AnalysisResult>(variantSnapshot.Length, StringComparer.Ordinal);

            foreach (var variant in variantSnapshot)
            {
                var analysis = Analyze(
                    module,
                    variant.Function,
                    variant.UniqueParameterIndices,
                    returnIsUnique,
                    variant.AnalysisContext);
                var variantKey = MirFunctionIdentity.GetStableKey(variant.Function);
                analyses[variantKey] = analysis;
                if (analysis.ReturnIsUnique && !returnIsUnique.GetValueOrDefault(variantKey))
                {
                    returnIsUnique[variantKey] = true;
                    changed = true;
                }
            }

            foreach (var variant in variantSnapshot)
            {
                var variantKey = MirFunctionIdentity.GetStableKey(variant.Function);
                var analysis = analyses[variantKey];
                foreach (var fact in analysis.RecordRebuilds
                             .Where(static fact => fact.SourceIsUnique)
                             .OrderByDescending(static fact => fact.InstructionIndex))
                {
                    if (!TryFuseKnownUniqueRecordRebuild(fact))
                    {
                        continue;
                    }

                    changed = true;
                }

                foreach (var fact in analysis.RecordUpdates.Where(static fact => fact.SourceIsUnique))
                {
                    var currentIndex = fact.Block.Instructions.IndexOf(fact.Call);
                    if (currentIndex < 0)
                    {
                        continue;
                    }
                    var call = (MirCall)fact.Block.Instructions[currentIndex];
                    if (call.RecordUpdate is not { IsKnownUnique: false } update)
                    {
                        continue;
                    }

                    fact.Block.Instructions[currentIndex] = call with
                    {
                        RecordUpdate = update with { IsKnownUnique = true }
                    };
                    changed = true;
                }

                foreach (var fact in analysis.Calls)
                {
                    if (!fact.RuntimeUniqueArgumentIndices.Contains(0) ||
                        fact.Call.Function is not MirFunctionRef arrayFunction ||
                        !IsArrayIntrinsic(arrayFunction, WellKnownStrings.InternalNames.ArrayTailShiftPrepend))
                    {
                        continue;
                    }

                    var currentIndex = fact.Block.Instructions.IndexOf(fact.Call);
                    if (currentIndex < 0)
                    {
                        continue;
                    }

                    fact.Block.Instructions[currentIndex] = fact.Call with
                    {
                        Function = MirRuntimeFunctions.CreateFunctionRef(
                            WellKnownStrings.InternalNames.ArrayTailShiftPrependUnique,
                            fact.Call.Target?.TypeId ?? TypeId.None,
                            fact.Call.Span)
                    };
                    changed = true;
                }

                foreach (var fact in analysis.Calls)
                {
                    if (fact.Call.Function is not MirFunctionRef functionRef ||
                        !TryResolveTemplate(functionRef, variants, originals, out var templateKey) ||
                        !specializationCandidates.Contains(templateKey) ||
                        !originals.TryGetValue(templateKey, out var template) ||
                        template.IsExternal)
                    {
                        continue;
                    }

                    var parameterCount = template.Locals.Count(static local => local.IsParameter);
                    if (fact.Call.Arguments.Count != parameterCount)
                    {
                        continue;
                    }

                    var uniqueParameters = fact.UniqueArgumentIndices
                        .Where(index => index >= 0 && index < parameterCount)
                        .Where(index => TypeSemantics.IsManagedType(
                            template.Locals.Where(static local => local.IsParameter).ElementAt(index).TypeId))
                        .Order()
                        .ToArray();
                    if (uniqueParameters.Length == 0)
                    {
                        continue;
                    }

                    var assumptionKey = BuildAssumptionKey(uniqueParameters);
                    if (!variantsByTemplate[templateKey].TryGetValue(assumptionKey, out var specialized))
                    {
                        if (variants.Count - analysisKeys.Count >= MaxSpecializedVariants)
                        {
                            continue;
                        }

                        specialized = CreateVariant(templateKey, template, uniqueParameters);
                        variantsByTemplate[templateKey][assumptionKey] = specialized;
                        variants[MirFunctionIdentity.GetStableKey(specialized.Function)] = specialized;
                        returnIsUnique[MirFunctionIdentity.GetStableKey(specialized.Function)] = false;
                        module.Functions.Add(specialized.Function);
                        changed = true;
                    }

                    var specializedRef = RewriteFunctionRef(functionRef, specialized.Function);
                    if (MirFunctionIdentity.GetStableKey(functionRef) == MirFunctionIdentity.GetStableKey(specializedRef))
                    {
                        continue;
                    }

                    var currentIndex = fact.Block.Instructions.IndexOf(fact.Call);
                    if (currentIndex < 0)
                    {
                        continue;
                    }

                    fact.Block.Instructions[currentIndex] = fact.Call with { Function = specializedRef };
                    changed = true;
                }
            }
        }

        // The module is mutated in place; wrap it in a fresh object when
        // variants were created so the optimizer's reference-identity change
        // detection reports the specialization.
        return module.Functions.Count > originals.Count ? module.WithFunctions(module.Functions) : module;
    }

    private static HashSet<string> FindSpecializationCandidates(
        MirModule module,
        IReadOnlyDictionary<string, MirFunc> originals)
    {
        var candidates = originals
            .Where(pair => pair.Value.BasicBlocks
                .SelectMany(static block => block.Instructions)
                .OfType<MirCall>()
                .Any(static call => call.RecordUpdate != null) ||
                ContainsFullRecordRebuild(module, pair.Value))
            .Select(static pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (key, function) in originals)
            {
                if (candidates.Contains(key))
                {
                    continue;
                }

                var callsCandidate = function.BasicBlocks
                    .SelectMany(static block => block.Instructions)
                    .OfType<MirCall>()
                    .Select(static call => call.Function)
                    .OfType<MirFunctionRef>()
                    .Any(functionRef => candidates.Contains(MirFunctionIdentity.GetStableKey(functionRef)));
                changed |= callsCandidate && candidates.Add(key);
            }
        }

        return candidates;
    }

    private static HashSet<string> CollectAnalysisKeys(
        IReadOnlyDictionary<string, MirFunc> originals,
        IReadOnlySet<string> specializationCandidates)
    {
        var result = new HashSet<string>(specializationCandidates, StringComparer.Ordinal);
        var pending = new Queue<string>(specializationCandidates);
        while (pending.Count > 0)
        {
            var key = pending.Dequeue();
            if (!originals.TryGetValue(key, out var function))
            {
                continue;
            }

            foreach (var calleeKey in function.BasicBlocks
                         .SelectMany(static block => block.Instructions)
                         .OfType<MirCall>()
                         .Select(static call => call.Function)
                         .OfType<MirFunctionRef>()
                         .Select(MirFunctionIdentity.GetStableKey)
                         .Distinct(StringComparer.Ordinal))
            {
                if (!originals.TryGetValue(calleeKey, out var callee) ||
                    !TypeSemantics.IsManagedType(callee.ReturnType) ||
                    !result.Add(calleeKey))
                {
                    continue;
                }

                pending.Enqueue(calleeKey);
            }
        }

        return result;
    }

    private static bool ContainsFullRecordRebuild(MirModule module, MirFunc function)
    {
        foreach (var block in function.BasicBlocks)
        {
            for (var index = 0; index < block.Instructions.Count - 1; index++)
            {
                if (TryGetFullRecordRebuild(module, block, index, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MayContainFullRecordRebuild(MirFunc function)
    {
        foreach (var block in function.BasicBlocks)
        {
            for (var index = 0; index + 1 < block.Instructions.Count; index++)
            {
                if (block.Instructions[index] is MirDrop
                    {
                        Value: MirPlace { Kind: PlaceKind.Local } source
                    } &&
                    block.Instructions[index + 1] is MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } target,
                        Function: MirFunctionRef constructor,
                        RecordUpdate: null
                    } &&
                    source.TypeId == target.TypeId &&
                    TypeSemantics.IsAdtConstructorCall(constructor))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetFullRecordRebuild(
        MirModule module,
        MirBasicBlock block,
        int instructionIndex,
        out FullRecordRebuild rebuild)
    {
        rebuild = default;
        if (instructionIndex < 0 || instructionIndex + 1 >= block.Instructions.Count ||
            block.Instructions[instructionIndex] is not MirDrop
            {
                Value: MirPlace { Kind: PlaceKind.Local } source
            } ||
            block.Instructions[instructionIndex + 1] is not MirCall
            {
                Target: MirPlace { Kind: PlaceKind.Local } target,
                Function: MirFunctionRef constructor,
                RecordUpdate: null
            } call ||
            source.TypeId != target.TypeId ||
            !TypeSemantics.IsManagedType(target.TypeId) ||
            !TypeSemantics.IsAdtConstructorCall(constructor) ||
            !TryResolveConstructorFieldCount(
                module,
                target.TypeId,
                constructor.Name,
                call.Arguments.Count,
                out var fieldCount))
        {
            return false;
        }

        rebuild = new FullRecordRebuild(call, source, fieldCount);
        return true;
    }

    private static bool TryResolveConstructorFieldCount(
        MirModule module,
        TypeId typeId,
        string constructorName,
        int argumentCount,
        out int fieldCount)
    {
        fieldCount = 0;
        if (!module.ConstructorLayouts.TryGetValue(typeId.Value, out var layouts))
        {
            return false;
        }

        var layout = layouts.FirstOrDefault(candidate =>
            candidate.FieldTypeIds.Count == argumentCount &&
            (string.Equals(candidate.ConstructorName, constructorName, StringComparison.Ordinal) ||
             constructorName.EndsWith($"__{candidate.ConstructorName}", StringComparison.Ordinal) ||
             constructorName.EndsWith($".{candidate.ConstructorName}", StringComparison.Ordinal)));
        if (layout == null)
        {
            return false;
        }

        fieldCount = layout.FieldTypeIds.Count;
        return true;
    }

    private static bool TryFuseKnownUniqueRecordRebuild(RecordRebuildFact fact)
    {
        var dropIndex = fact.InstructionIndex;
        if (dropIndex < 0 || dropIndex + 1 >= fact.Block.Instructions.Count ||
            !ReferenceEquals(fact.Block.Instructions[dropIndex + 1], fact.Call) ||
            fact.Block.Instructions[dropIndex] is not MirDrop)
        {
            return false;
        }

        fact.Block.Instructions[dropIndex + 1] = fact.Call with
        {
            Arguments = [fact.Source, .. fact.Call.Arguments],
            BorrowedArgumentIndices = fact.Call.BorrowedArgumentIndices
                .Select(static index => index + 1)
                .ToHashSet(),
            RecordUpdate = new MirRecordUpdateInfo
            {
                Source = fact.Source,
                UpdatedFieldIndices = Enumerable.Range(0, fact.FieldCount).ToList(),
                IsKnownUnique = true
            }
        };
        fact.Block.Instructions.RemoveAt(dropIndex);
        return true;
    }

    private static bool IsArrayIntrinsic(MirFunctionRef functionRef, string name) =>
        MirRuntimeFunctions.HasIdentity(functionRef, name) ||
        MirBuiltinFunctions.TryGetIntrinsicName(functionRef, out var intrinsic) &&
        string.Equals(intrinsic, name, StringComparison.Ordinal);

    private static bool IsConsumingArrayIntrinsic(MirFunctionRef functionRef) =>
        IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArrayTailShiftPrepend) ||
        IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArrayTailShiftPrependUnique);

    private static bool HasBorrowAliasUseAfter(
        MirBasicBlock block,
        int callIndex,
        LocalId source)
    {
        var aliases = new HashSet<LocalId> { source };
        for (var index = 0; index < callIndex; index++)
        {
            switch (block.Instructions[index])
            {
                case MirLoad { CreatesBorrowAlias: true } load
                    when OperandUsesAnyLocal(load.Source, aliases):
                    aliases.Add(load.Target.Local);
                    break;
                case MirAssign assign when OperandUsesAnyLocal(assign.Source, aliases):
                    aliases.Add(assign.Target.Local);
                    break;
                case MirMove move when OperandUsesAnyLocal(move.Source, aliases):
                    aliases.Add(move.Target.Local);
                    break;
            }
        }

        for (var index = callIndex + 1; index < block.Instructions.Count; index++)
        {
            if (InstructionReadsAnyLocal(block.Instructions[index], aliases))
            {
                return true;
            }
        }

        return block.Terminator switch
        {
            MirReturn { Value: not null } result => OperandUsesAnyLocal(result.Value, aliases),
            MirSwitch branch => OperandUsesAnyLocal(branch.Discriminant, aliases),
            _ => false
        };
    }

    private static bool InstructionReadsAnyLocal(MirInstruction instruction, IReadOnlySet<LocalId> locals) =>
        instruction switch
        {
            MirAssign assign => OperandUsesAnyLocal(assign.Source, locals),
            MirCaseInject injection => OperandUsesAnyLocal(injection.Operand, locals),
            MirCall call => OperandUsesAnyLocal(call.Function, locals) ||
                            call.Arguments.Any(argument => OperandUsesAnyLocal(argument, locals)) ||
                            call.RecordUpdate != null && OperandUsesAnyLocal(call.RecordUpdate.Source, locals),
            MirBinOp binary => OperandUsesAnyLocal(binary.Left, locals) || OperandUsesAnyLocal(binary.Right, locals),
            MirUnaryOp unary => OperandUsesAnyLocal(unary.Operand, locals),
            MirSelect select => OperandUsesAnyLocal(select.Condition, locals) ||
                                OperandUsesAnyLocal(select.TrueValue, locals) ||
                                OperandUsesAnyLocal(select.FalseValue, locals),
            MirLoad load => OperandUsesAnyLocal(load.Source, locals),
            MirStore store => OperandUsesAnyLocal(store.Target, locals) || OperandUsesAnyLocal(store.Value, locals),
            MirDrop drop => OperandUsesAnyLocal(drop.Value, locals),
            MirCopy copy => OperandUsesAnyLocal(copy.Source, locals),
            MirMove move => OperandUsesAnyLocal(move.Source, locals),
            _ => false
        };

    private static bool OperandUsesAnyLocal(MirOperand operand, IReadOnlySet<LocalId> locals) => operand switch
    {
        MirPlace { Kind: PlaceKind.Local, Local: var local } => locals.Contains(local),
        MirPlace place =>
            place.Base != null && OperandUsesAnyLocal(place.Base, locals) ||
            place.Index != null && OperandUsesAnyLocal(place.Index, locals),
        _ => false
    };

    private static bool TryResolveTemplate(
        MirFunctionRef functionRef,
        IReadOnlyDictionary<string, Variant> variants,
        IReadOnlyDictionary<string, MirFunc> originals,
        out string templateKey)
    {
        var functionKey = MirFunctionIdentity.GetStableKey(functionRef);
        if (variants.TryGetValue(functionKey, out var variant))
        {
            templateKey = variant.TemplateKey;
            return true;
        }

        if (originals.ContainsKey(functionKey))
        {
            templateKey = functionKey;
            return true;
        }

        templateKey = string.Empty;
        return false;
    }

    private static Variant CreateVariant(string templateKey, MirFunc template, IReadOnlyList<int> uniqueParameters)
    {
        var suffix = $"__unique_{string.Join('_', uniqueParameters)}";
        var sourceName = string.IsNullOrWhiteSpace(template.SourceName) ? template.Name : template.SourceName;
        var functionId = template.FunctionId with
        {
            SymbolId = SymbolId.None,
            StableIdentityKey = $"{MirFunctionIdentity.GetStableKey(template)}|unique:{string.Join(',', uniqueParameters)}",
            Name = $"{template.FunctionId.Name}{suffix}",
            QualifiedName = $"{template.FunctionId.QualifiedName}{suffix}",
            MangledName = string.Empty
        };
        var clone = new MirFunc
        {
            Name = $"{template.Name}{suffix}",
            SourceName = $"{sourceName}{suffix}",
            Locals = template.Locals.ToList(),
            BasicBlocks = template.BasicBlocks.Select(CloneBlock).ToList(),
            EntryBlockId = template.EntryBlockId,
            ReturnType = template.ReturnType,
            GenericParameterCount = template.GenericParameterCount,
            GenericParameters = template.GenericParameters.ToList(),
            GenericTypeParameterIds = template.GenericTypeParameterIds.ToList(),
            IsRuntimeWordAbi = template.IsRuntimeWordAbi,
            IsExternal = false,
            Span = template.Span,
            SymbolId = SymbolId.None,
            FunctionId = functionId,
            IsEntry = false,
            TraitInvokeHelper = template.TraitInvokeHelper,
            TraitInvokeHelperTraitId = template.TraitInvokeHelperTraitId,
            IntrinsicName = template.IntrinsicName,
            BuiltinIntrinsicRole = template.BuiltinIntrinsicRole
        };
        clone.OwnershipContract = template.OwnershipContract;
        return new Variant(
            templateKey,
            clone,
            uniqueParameters.ToHashSet(),
            FunctionAnalysisContext.Create(clone));
    }

    private static MirBasicBlock CloneBlock(MirBasicBlock block) => new()
    {
        Id = block.Id,
        Instructions = block.Instructions.Select(CloneInstruction).ToList(),
        Terminator = block.Terminator,
        Span = block.Span,
        IsEntry = block.IsEntry
    };

    private static MirInstruction CloneInstruction(MirInstruction instruction) => instruction switch
    {
        MirCall call => call with
        {
            Arguments = call.Arguments.ToList(),
            BorrowedArgumentIndices = call.BorrowedArgumentIndices.ToHashSet(),
            RecordUpdate = call.RecordUpdate == null
                ? null
                : call.RecordUpdate with
                {
                    UpdatedFieldIndices = call.RecordUpdate.UpdatedFieldIndices.ToList()
                }
        },
        _ => instruction
    };

    private static MirFunctionRef RewriteFunctionRef(MirFunctionRef functionRef, MirFunc target) => functionRef with
    {
        Name = target.Name,
        SymbolId = target.SymbolId,
        FunctionId = target.FunctionId
    };

    private static string BuildAssumptionKey(IEnumerable<int> indices) => string.Join(',', indices.Order());

    private static AnalysisResult Analyze(
        MirModule module,
        MirFunc function,
        IReadOnlySet<int> uniqueParameterIndices,
        IReadOnlyDictionary<string, bool> returnIsUnique,
        FunctionAnalysisContext context)
    {
        var assumedEntry = function.Locals
            .Where(static local => local.IsParameter)
            .Select((local, index) => (local, index))
            .Where(pair => uniqueParameterIndices.Contains(pair.index) && TypeSemantics.IsManagedType(pair.local.TypeId))
            .Select(pair => new PlaceSlot(pair.local.Id, string.Empty))
            .ToHashSet();
        // Must-unique analysis starts at lattice top. Represent top as null
        // instead of materializing the complete place universe for every
        // block: large specialized functions can contain tens of thousands
        // of MIR locals, and cloning that universe per block dominates both
        // memory and transfer time.
        var inStates = function.BasicBlocks.ToDictionary(
            static block => block.Id,
            static _ => (HashSet<PlaceSlot>?)null);
        var outStates = function.BasicBlocks.ToDictionary(
            static block => block.Id,
            static _ => (HashSet<PlaceSlot>?)null);
        var runtimeUniqueInStates = context.RequiresRuntimeOwnershipFacts
            ? function.BasicBlocks.ToDictionary(
                static block => block.Id,
                static _ => (HashSet<PlaceSlot>?)null)
            : null;
        var runtimeUniqueOutStates = context.RequiresRuntimeOwnershipFacts
            ? function.BasicBlocks.ToDictionary(
                static block => block.Id,
                static _ => (HashSet<PlaceSlot>?)null)
            : null;

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in function.BasicBlocks)
            {
                var incoming = new List<IReadOnlySet<PlaceSlot>>();
                if (block.Id == function.EntryBlockId)
                {
                    incoming.Add(assumedEntry);
                }

                foreach (var predecessor in context.ControlFlow.GetPredecessors(block.Id))
                {
                    if (outStates[predecessor] is { } predecessorState)
                    {
                        incoming.Add(predecessorState);
                    }
                }

                if (incoming.Count == 0)
                {
                    continue;
                }

                var nextIn = new HashSet<PlaceSlot>(incoming[0]);
                for (var index = 1; index < incoming.Count; index++)
                {
                    nextIn.IntersectWith(incoming[index]);
                }

                var nextOut = new HashSet<PlaceSlot>(nextIn);
                foreach (var instruction in block.Instructions)
                {
                    ApplyTransfer(instruction, nextOut, returnIsUnique);
                }

                HashSet<PlaceSlot>? nextRuntimeIn = null;
                HashSet<PlaceSlot>? nextRuntimeOut = null;
                if (runtimeUniqueInStates != null && runtimeUniqueOutStates != null)
                {
                    var runtimeIncoming = new List<IReadOnlySet<PlaceSlot>>();
                    if (block.Id == function.EntryBlockId)
                    {
                        runtimeIncoming.Add(assumedEntry);
                    }
                    foreach (var predecessor in context.ControlFlow.GetPredecessors(block.Id))
                    {
                        if (runtimeUniqueOutStates[predecessor] is { } predecessorState)
                        {
                            runtimeIncoming.Add(predecessorState);
                        }
                    }
                    if (runtimeIncoming.Count == 0)
                    {
                        continue;
                    }
                    nextRuntimeIn = new HashSet<PlaceSlot>(runtimeIncoming[0]);
                    for (var index = 1; index < runtimeIncoming.Count; index++)
                    {
                        nextRuntimeIn.IntersectWith(runtimeIncoming[index]);
                    }
                    nextRuntimeOut = new HashSet<PlaceSlot>(nextRuntimeIn);
                    foreach (var instruction in block.Instructions)
                    {
                        ApplyTransfer(instruction, nextRuntimeOut, returnIsUnique, preserveBorrowedOwner: true);
                    }
                }

                if (inStates[block.Id] is not { } currentIn || !currentIn.SetEquals(nextIn))
                {
                    inStates[block.Id] = nextIn;
                    changed = true;
                }

                if (outStates[block.Id] is not { } currentOut || !currentOut.SetEquals(nextOut))
                {
                    outStates[block.Id] = nextOut;
                    changed = true;
                }
                if (nextRuntimeIn != null &&
                    (runtimeUniqueInStates![block.Id] is not { } currentRuntimeIn ||
                     !currentRuntimeIn.SetEquals(nextRuntimeIn)))
                {
                    runtimeUniqueInStates[block.Id] = nextRuntimeIn;
                    changed = true;
                }
                if (nextRuntimeOut != null &&
                    (runtimeUniqueOutStates![block.Id] is not { } currentRuntimeOut ||
                     !currentRuntimeOut.SetEquals(nextRuntimeOut)))
                {
                    runtimeUniqueOutStates[block.Id] = nextRuntimeOut;
                    changed = true;
                }
            }
        }

        var recordUpdates = new List<RecordUpdateFact>();
        var recordRebuilds = new List<RecordRebuildFact>();
        var calls = new List<CallFact>();
        foreach (var block in function.BasicBlocks)
        {
            var state = inStates[block.Id] is { } blockIn
                ? new HashSet<PlaceSlot>(blockIn)
                : [];
            var runtimeUniqueState = runtimeUniqueInStates == null
                ? state
                : runtimeUniqueInStates[block.Id] is { } runtimeBlockIn
                    ? new HashSet<PlaceSlot>(runtimeBlockIn)
                    : [];
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                if (block.Instructions[index] is MirCall call)
                {
                    var uniqueArguments = call.Arguments
                        .Select((argument, argumentIndex) => (argument, argumentIndex))
                        .Where(pair => IsUnique(pair.argument, state))
                        .Select(static pair => pair.argumentIndex)
                        .ToHashSet();
                    var runtimeUniqueArguments = call.Arguments
                        .Select((argument, argumentIndex) => (argument, argumentIndex))
                        .Where(pair => IsUnique(pair.argument, runtimeUniqueState))
                        .Select(static pair => pair.argumentIndex)
                        .ToHashSet();
                    calls.Add(new CallFact(
                        block,
                        index,
                        call,
                        uniqueArguments,
                        runtimeUniqueArguments));
                    if (call.RecordUpdate is { } update)
                    {
                        recordUpdates.Add(new RecordUpdateFact(block, call, IsUnique(update.Source, state)));
                    }
                }

                if (TryGetFullRecordRebuild(module, block, index, out var rebuild))
                {
                    recordRebuilds.Add(new RecordRebuildFact(
                        block,
                        index,
                        rebuild.Call,
                        rebuild.Source,
                        rebuild.FieldCount,
                        IsUnique(rebuild.Source, runtimeUniqueState) &&
                        !HasBorrowAliasUseAfter(block, index + 1, rebuild.Source.Local)));
                }

                ApplyTransfer(block.Instructions[index], state, returnIsUnique);
                if (!ReferenceEquals(state, runtimeUniqueState))
                {
                    ApplyTransfer(
                        block.Instructions[index],
                        runtimeUniqueState,
                        returnIsUnique,
                        preserveBorrowedOwner: true);
                }
            }
        }

        var managedReturns = function.BasicBlocks
            .Where(block => context.ReachableBlocks.Contains(block.Id))
            .Where(static block => block.Terminator is MirReturn { Value: not null })
            .Select(block => (Block: block, Return: (MirReturn)block.Terminator!))
            .Where(pair => TypeSemantics.IsManagedType(pair.Return.Value!.TypeId))
            .ToArray();
        var uniqueReturn = managedReturns.Length > 0 && managedReturns.All(pair =>
            outStates[pair.Block.Id] is { } blockOut &&
            IsUnique(pair.Return.Value!, blockOut));

        return new AnalysisResult(uniqueReturn, recordUpdates, recordRebuilds, calls);
    }

    private static void ApplyTransfer(
        MirInstruction instruction,
        HashSet<PlaceSlot> state,
        IReadOnlyDictionary<string, bool> returnIsUnique,
        bool preserveBorrowedOwner = false)
    {
        switch (instruction)
        {
            case MirAssign assign:
                SetUnique(assign.Target, TakeUnique(assign.Source, state), state);
                break;
            case MirCaseInject injection when injection.Target is MirPlace target:
                SetUnique(target, TakeUnique(injection.Operand, state), state);
                break;
            case MirMove move:
                SetUnique(move.Target, TakeUnique(move.Source, state), state);
                break;
            case MirCopy copy:
                Remove(copy.Target, state);
                if (TypeSemantics.IsManagedType(copy.Source.TypeId))
                {
                    InvalidateAliasedPlace(copy.Source, state);
                }
                break;
            case MirLoad load:
                var loadedUnique = load.MovesOutOfSource && TakeUnique(load.Source, state);
                if (!preserveBorrowedOwner &&
                    !load.MovesOutOfSource &&
                    TypeSemantics.IsManagedType(load.Source.TypeId))
                {
                    InvalidateAliasedPlace(load.Source, state);
                }
                SetUnique(load.Target, loadedUnique, state);
                break;
            case MirStore store:
                SetUnique(store.Target, TakeUnique(store.Value, state), state);
                break;
            case MirDrop drop:
                Remove(drop.Value, state);
                break;
            case MirAlloc alloc:
                SetUnique(alloc.Target, TypeSemantics.IsManagedType(alloc.Target.TypeId), state);
                break;
            case MirCall call:
                var producesUniqueArray = call.Arguments.Count > 0 &&
                                          IsUnique(call.Arguments[0], state) &&
                                          call.Function is MirFunctionRef arrayFunction &&
                                          IsConsumingArrayIntrinsic(arrayFunction);
                for (var index = 0; index < call.Arguments.Count; index++)
                {
                    if (!call.BorrowedArgumentIndices.Contains(index))
                    {
                        Remove(call.Arguments[index], state);
                    }
                }

                if (call.Target is { } callTarget)
                {
                    var producesUnique = call.RecordUpdate != null ||
                                         producesUniqueArray ||
                                         call.Function is MirFunctionRef constructor &&
                                         TypeSemantics.IsAdtConstructorCall(constructor) ||
                                         call.Function is MirFunctionRef functionRef &&
                                         returnIsUnique.GetValueOrDefault(MirFunctionIdentity.GetStableKey(functionRef));
                    SetUnique(callTarget, producesUnique, state);
                }
                break;
            case MirBinOp binary when binary.Target is MirPlace binaryTarget:
                Remove(binaryTarget, state);
                break;
            case MirUnaryOp unary when unary.Target is MirPlace unaryTarget:
                Remove(unaryTarget, state);
                break;
            case MirSelect select:
                Remove(select.Target, state);
                break;
        }
    }

    private static bool TakeUnique(MirOperand operand, HashSet<PlaceSlot> state)
    {
        if (!TryGetSlot(operand, out var slot) || !ContainsUnique(slot, state))
        {
            Remove(operand, state);
            return false;
        }

        Remove(slot, state);
        return true;
    }

    private static bool IsUnique(MirOperand operand, IReadOnlySet<PlaceSlot> state) =>
        TryGetSlot(operand, out var slot) && ContainsUnique(slot, state);

    private static bool ContainsUnique(PlaceSlot slot, IReadOnlySet<PlaceSlot> state)
    {
        if (state.Contains(slot) || state.Contains(new PlaceSlot(slot.Root, string.Empty)))
        {
            return true;
        }

        var ancestorEnd = slot.Path.LastIndexOf('/');
        while (ancestorEnd > 0)
        {
            if (state.Contains(new PlaceSlot(slot.Root, slot.Path[..ancestorEnd])))
            {
                return true;
            }
            ancestorEnd = slot.Path.LastIndexOf('/', ancestorEnd - 1);
        }

        return false;
    }

    private static void SetUnique(MirPlace target, bool isUnique, HashSet<PlaceSlot> state)
    {
        Remove(target, state);
        if (isUnique && TypeSemantics.IsManagedType(target.TypeId) && TryGetSlot(target, out var slot))
        {
            state.Add(slot);
        }
    }

    private static void Remove(MirOperand operand, HashSet<PlaceSlot> state)
    {
        if (!TypeSemantics.IsManagedType(operand.TypeId))
        {
            return;
        }

        if (TryGetSlot(operand, out var slot))
        {
            Remove(slot, state);
        }
    }

    private static void InvalidateAliasedPlace(MirOperand operand, HashSet<PlaceSlot> state)
    {
        if (!TryGetSlot(operand, out var slot))
        {
            return;
        }

        state.RemoveWhere(candidate => candidate.Root == slot.Root &&
            (candidate.Path == slot.Path ||
             candidate.Path.Length == 0 ||
             slot.Path.Length == 0 ||
             candidate.Path.StartsWith($"{slot.Path}/", StringComparison.Ordinal) ||
             slot.Path.StartsWith($"{candidate.Path}/", StringComparison.Ordinal)));
    }

    private static void Remove(PlaceSlot slot, HashSet<PlaceSlot> state)
    {
        state.RemoveWhere(candidate => candidate.Root == slot.Root &&
            (candidate.Path == slot.Path ||
             slot.Path.Length == 0 ||
             candidate.Path.StartsWith($"{slot.Path}/", StringComparison.Ordinal)));
    }

    private static HashSet<PlaceSlot> CollectPlaceSlots(MirFunc function)
    {
        var result = new HashSet<PlaceSlot>();
        foreach (var local in function.Locals)
        {
            result.Add(new PlaceSlot(local.Id, string.Empty));
        }

        foreach (var place in function.BasicBlocks
                     .SelectMany(static block => block.Instructions)
                     .SelectMany(EnumeratePlaces))
        {
            if (TryGetSlot(place, out var slot))
            {
                result.Add(slot);
            }
        }

        return result;
    }

    private static IEnumerable<MirPlace> EnumeratePlaces(MirInstruction instruction)
    {
        IEnumerable<MirOperand?> operands = instruction switch
        {
            MirAssign assign => [assign.Target, assign.Source],
            MirCaseInject injection => [injection.Target, injection.Operand],
            MirCall call => [call.Target, call.Function, .. call.Arguments, call.RecordUpdate?.Source],
            MirBinOp binary => [binary.Target, binary.Left, binary.Right],
            MirUnaryOp unary => [unary.Target, unary.Operand],
            MirSelect select => [select.Target, select.Condition, select.TrueValue, select.FalseValue],
            MirLoad load => [load.Target, load.Source],
            MirStore store => [store.Target, store.Value],
            MirDrop drop => [drop.Value],
            MirCopy copy => [copy.Target, copy.Source],
            MirMove move => [move.Target, move.Source],
            MirAlloc alloc => [alloc.Target],
            _ => []
        };
        return operands.OfType<MirPlace>();
    }

    private static bool TryGetSlot(MirOperand operand, out PlaceSlot slot)
    {
        if (operand is not MirPlace place)
        {
            slot = default;
            return false;
        }

        return TryGetSlot(place, out slot);
    }

    private static bool TryGetSlot(MirPlace place, out PlaceSlot slot)
    {
        switch (place.Kind)
        {
            case PlaceKind.Local:
                slot = new PlaceSlot(place.Local, string.Empty);
                return true;
            case PlaceKind.Field when place.Base != null && TryGetSlot(place.Base, out var fieldBase):
                slot = fieldBase with { Path = $"{fieldBase.Path}/f:{place.FieldName}" };
                return true;
            case PlaceKind.Index when place.IndexAccessKind == MirIndexAccessKind.Aggregate &&
                                      place.Base != null &&
                                      place.Index is MirConstant { Value: MirConstantValue.IntValue(var index) } &&
                                      TryGetSlot(place.Base, out var indexBase):
                slot = indexBase with { Path = $"{indexBase.Path}/i:{index}" };
                return true;
            default:
                slot = default;
                return false;
        }
    }

    private static HashSet<BlockId> CollectReachableBlocks(MirFunc function)
    {
        var reachable = new HashSet<BlockId>();
        var pending = new Stack<BlockId>();
        pending.Push(function.EntryBlockId);
        var blocks = function.BasicBlocks.ToDictionary(static block => block.Id);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!reachable.Add(id) || !blocks.TryGetValue(id, out var block))
            {
                continue;
            }

            switch (block.Terminator)
            {
                case MirGoto branch:
                    pending.Push(branch.Target);
                    break;
                case MirSwitch branch:
                    foreach (var target in branch.Branches.Select(static item => item.Target))
                    {
                        pending.Push(target);
                    }
                    if (branch.DefaultTarget is { } defaultTarget)
                    {
                        pending.Push(defaultTarget);
                    }
                    break;
            }
        }

        return reachable;
    }

    private sealed record Variant(
        string TemplateKey,
        MirFunc Function,
        IReadOnlySet<int> UniqueParameterIndices,
        FunctionAnalysisContext AnalysisContext);

    private sealed record AnalysisResult(
        bool ReturnIsUnique,
        IReadOnlyList<RecordUpdateFact> RecordUpdates,
        IReadOnlyList<RecordRebuildFact> RecordRebuilds,
        IReadOnlyList<CallFact> Calls);

    private sealed record RecordUpdateFact(MirBasicBlock Block, MirCall Call, bool SourceIsUnique);

    private sealed record RecordRebuildFact(
        MirBasicBlock Block,
        int InstructionIndex,
        MirCall Call,
        MirPlace Source,
        int FieldCount,
        bool SourceIsUnique);

    private sealed record CallFact(
        MirBasicBlock Block,
        int InstructionIndex,
        MirCall Call,
        IReadOnlySet<int> UniqueArgumentIndices,
        IReadOnlySet<int> RuntimeUniqueArgumentIndices);

    private readonly record struct PlaceSlot(LocalId Root, string Path);

    private readonly record struct FullRecordRebuild(MirCall Call, MirPlace Source, int FieldCount);

    private sealed record FunctionAnalysisContext(
        ControlFlowGraph ControlFlow,
        IReadOnlySet<BlockId> ReachableBlocks,
        bool RequiresRuntimeOwnershipFacts)
    {
        public static FunctionAnalysisContext Create(MirFunc function) => new(
            new ControlFlowGraph(function),
            CollectReachableBlocks(function),
            MayContainFullRecordRebuild(function) ||
            function.BasicBlocks.SelectMany(static block => block.Instructions).Any(instruction =>
                instruction is MirCall { Function: MirFunctionRef functionRef } &&
                (IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArrayTailShiftPrepend) ||
                 IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArrayTailShiftPrependUnique))));
    }
}
