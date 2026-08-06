using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Keeps consuming sequence slices as compiler-internal read-only ranges when
/// the same tail is later rebuilt by the fused shift/prepend operation.
/// </summary>
public sealed class RuntimeArrayRangeSpecializationPass : IMirOptimizationPass
{
    private const int MaxSpecializedVariants = 256;

    public string Name => "RuntimeArrayRangeSpecialization";

    public MirModule Run(MirModule module)
    {
        var templates = module.Functions
            .Where(static function => !function.IsExternal && function.BasicBlocks.Count > 0)
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var variants = new Dictionary<RangeVariantKey, MirFunc>();
        var addedVariants = new HashSet<MirFunc>();

        foreach (var function in module.Functions.ToArray())
        {
            foreach (var sliceSite in FindSliceSites(function).ToArray())
            {
                if (!TryBuildFusionPlan(
                        module,
                        function,
                        sliceSite,
                        templates,
                        variants,
                        out var plan))
                {
                    continue;
                }

                foreach (var variant in plan.RangeCalls.Select(static call => call.Variant).Distinct())
                {
                    if (addedVariants.Add(variant))
                    {
                        module.Functions.Add(variant);
                    }
                }

                ApplyPlan(plan);
            }
        }

        // The module is mutated in place; wrap it in a fresh object when
        // variants were created so the optimizer's reference-identity change
        // detection reports the specialization.
        return addedVariants.Count > 0 ? module.WithFunctions(module.Functions) : module;
    }

    private static IEnumerable<SliceSite> FindSliceSites(MirFunc function)
    {
        foreach (var block in function.BasicBlocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                if (block.Instructions[index] is MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } target,
                        Function: MirFunctionRef functionRef,
                        Arguments.Count: 3
                    } call &&
                    IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArraySlice) &&
                    call.Arguments[0] is MirPlace { Kind: PlaceKind.Local } source &&
                    IsIntegerConstant(call.Arguments[1], 1) &&
                    IsIntegerConstant(call.Arguments[2], 0))
                {
                    yield return new SliceSite(block, call, source, target);
                }
            }
        }
    }

    private static bool TryBuildFusionPlan(
        MirModule module,
        MirFunc function,
        SliceSite slice,
        IReadOnlyDictionary<string, MirFunc> templates,
        Dictionary<RangeVariantKey, MirFunc> variants,
        out FusionPlan plan)
    {
        plan = null!;
        if (CountReadUses(function, slice.Source.Local) != 1)
        {
            return false;
        }

        var rangeCalls = new List<RangeCallPlan>();
        ShiftUse? shift = null;
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (ReferenceEquals(instruction, slice.Call))
                {
                    continue;
                }

                if (instruction is not MirCall call)
                {
                    if (InstructionReadsLocal(instruction, slice.Target.Local))
                    {
                        return false;
                    }
                    continue;
                }

                if (call.Target != null && OperandContainsLocal(call.Target, slice.Target.Local) ||
                    call.RecordUpdate != null && OperandContainsLocal(call.RecordUpdate.Source, slice.Target.Local))
                {
                    return false;
                }

                var matchingArguments = call.Arguments
                    .Select((argument, index) => (argument, index))
                    .Where(pair => OperandContainsLocal(pair.argument, slice.Target.Local))
                    .ToArray();
                if (matchingArguments.Length == 0)
                {
                    continue;
                }
                if (matchingArguments.Length != 1 ||
                    !IsExactLocal(matchingArguments[0].argument, slice.Target.Local) ||
                    call.Function is not MirFunctionRef calledFunction)
                {
                    return false;
                }

                var argumentIndex = matchingArguments[0].index;
                if (argumentIndex == 0 &&
                    IsArrayIntrinsic(calledFunction, WellKnownStrings.InternalNames.ArrayShiftPrepend))
                {
                    if (shift != null || call.Arguments.Count < 5)
                    {
                        return false;
                    }

                    shift = new ShiftUse(block, call);
                    continue;
                }

                var templateKey = MirFunctionIdentity.GetStableKey(calledFunction);
                if (!templates.TryGetValue(templateKey, out var template) ||
                    !IsSharedBorrowParameter(module, template, argumentIndex))
                {
                    return false;
                }

                var variantKey = new RangeVariantKey(templateKey, argumentIndex);
                if (!variants.TryGetValue(variantKey, out var variant))
                {
                    if (variants.Count >= MaxSpecializedVariants ||
                        !TryCreateRangeVariant(module, template, argumentIndex, out variant))
                    {
                        return false;
                    }
                    variants[variantKey] = variant;
                }

                rangeCalls.Add(new RangeCallPlan(block, call, argumentIndex, variant));
            }

            if (TerminatorReadsLocal(block.Terminator, slice.Target.Local))
            {
                return false;
            }
        }

        if (shift == null)
        {
            return false;
        }

        plan = new FusionPlan(slice, shift, rangeCalls);
        return true;
    }

    private static bool IsSharedBorrowParameter(MirModule module, MirFunc function, int parameterIndex)
    {
        if (parameterIndex < function.OwnershipContract.Parameters.Count)
        {
            return function.OwnershipContract.GetParameter(parameterIndex).Projection.Kind ==
                   OwnershipPassingKind.SharedBorrow;
        }

        var parameters = function.Locals.Where(static local => local.IsParameter).ToArray();
        return parameterIndex >= 0 &&
               parameterIndex < parameters.Length &&
               OwnershipProjection.FromType(parameters[parameterIndex].TypeId, module.TypeDescriptors).Kind ==
               OwnershipPassingKind.SharedBorrow;
    }

    private static void ApplyPlan(FusionPlan plan)
    {
        foreach (var rangeCall in plan.RangeCalls)
        {
            var index = rangeCall.Block.Instructions.IndexOf(rangeCall.Call);
            if (index < 0 || rangeCall.Call.Function is not MirFunctionRef functionRef)
            {
                continue;
            }

            var arguments = rangeCall.Call.Arguments.ToList();
            arguments[rangeCall.ArgumentIndex] = plan.Slice.Source;
            arguments.Add(plan.Slice.Call.Arguments[1]);
            arguments.Add(plan.Slice.Call.Arguments[2]);
            rangeCall.Block.Instructions[index] = rangeCall.Call with
            {
                Function = RewriteFunctionRef(functionRef, rangeCall.Variant),
                Arguments = arguments,
                BorrowedArgumentIndices = rangeCall.Call.BorrowedArgumentIndices
                    .Append(rangeCall.ArgumentIndex)
                    .ToHashSet()
            };
        }

        var shiftIndex = plan.Shift.Block.Instructions.IndexOf(plan.Shift.Call);
        if (shiftIndex >= 0)
        {
            var shift = plan.Shift.Call;
            plan.Shift.Block.Instructions[shiftIndex] = shift with
            {
                Function = MirRuntimeFunctions.CreateFunctionRef(
                    WellKnownStrings.InternalNames.ArrayTailShiftPrepend,
                    shift.Target?.TypeId ?? TypeId.None,
                    shift.Span),
                Arguments =
                [
                    plan.Slice.Source,
                    shift.Arguments[1],
                    shift.Arguments[3],
                    shift.Arguments[4]
                ]
            };
        }

        plan.Slice.Block.Instructions.Remove(plan.Slice.Call);
    }

    private static bool TryCreateRangeVariant(
        MirModule module,
        MirFunc template,
        int parameterIndex,
        out MirFunc variant)
    {
        variant = null!;
        var parameters = template.Locals.Where(static local => local.IsParameter).ToArray();
        if (parameterIndex < 0 || parameterIndex >= parameters.Length)
        {
            return false;
        }
        if (!TryAnalyzeRangeConsumer(template, parameters[parameterIndex].Id, out var analysis))
        {
            return false;
        }

        var borrowedIndexBases = FindBorrowedIndexBaseRewrites(template, analysis);
        var removableBorrowLoads = borrowedIndexBases.Values
            .Select(static rewrite => rewrite.Definition)
            .ToHashSet();

        var nextLocal = template.Locals.Count == 0
            ? 1
            : template.Locals.Max(static local => local.Id.Value) + 1;
        var startLocal = new MirLocal
        {
            Id = new LocalId { Value = nextLocal++ },
            Name = $"__range_start_{parameterIndex}",
            TypeId = new TypeId(BaseTypes.IntId),
            IsParameter = true,
            Span = template.Span
        };
        var suffixLocal = new MirLocal
        {
            Id = new LocalId { Value = nextLocal++ },
            Name = $"__range_suffix_{parameterIndex}",
            TypeId = new TypeId(BaseTypes.IntId),
            IsParameter = true,
            Span = template.Span
        };
        var locals = template.Locals.ToList();
        locals.Add(startLocal);
        locals.Add(suffixLocal);
        var blocks = new List<MirBasicBlock>(template.BasicBlocks.Count);
        foreach (var block in template.BasicBlocks)
        {
            var instructions = new List<MirInstruction>(block.Instructions.Count);
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                var site = new InstructionSite(block.Id, index);
                if (removableBorrowLoads.Contains(site))
                {
                    continue;
                }
                if (analysis.LengthCalls.Contains(site) && instruction is MirCall lengthCall)
                {
                    instructions.Add(lengthCall with
                    {
                        Function = MirRuntimeFunctions.CreateFunctionRef(
                            WellKnownStrings.InternalNames.ArrayRangeLength,
                            lengthCall.Target?.TypeId ?? new TypeId(BaseTypes.IntId),
                            lengthCall.Span),
                        Arguments =
                        [
                            lengthCall.Arguments[0],
                            LocalPlace(startLocal),
                            LocalPlace(suffixLocal)
                        ],
                        BorrowedArgumentIndices = new HashSet<int> { 0 }
                    });
                    continue;
                }

                if (analysis.IndexLoads.Contains(site) &&
                    instruction is MirLoad
                    {
                        Source: MirPlace
                        {
                            Kind: PlaceKind.Index,
                            IndexAccessKind: MirIndexAccessKind.RuntimeArray,
                            Base: not null,
                            Index: not null
                        } indexSource
                    } load)
                {
                    var rawLocal = new MirLocal
                    {
                        Id = new LocalId { Value = nextLocal++ },
                        Name = $"__range_element_{parameterIndex}",
                        TypeId = new TypeId(BaseTypes.RawPtrId),
                        Span = load.Span
                    };
                    locals.Add(rawLocal);
                    var rawPlace = LocalPlace(rawLocal);
                    instructions.Add(new MirCall
                    {
                        Target = rawPlace,
                        Function = MirRuntimeFunctions.CreateFunctionRef(
                            WellKnownStrings.InternalNames.ArrayRangeGet,
                            rawLocal.TypeId,
                            load.Span),
                        Arguments =
                        [
                            borrowedIndexBases.TryGetValue(site, out var borrowedBase)
                                ? borrowedBase.Base
                                : indexSource.Base,
                            LocalPlace(startLocal),
                            LocalPlace(suffixLocal),
                            indexSource.Index
                        ],
                        BorrowedArgumentIndices = new HashSet<int> { 0 },
                        Span = load.Span
                    });
                    instructions.Add(load with
                    {
                        Source = new MirPlace
                        {
                            Kind = PlaceKind.Deref,
                            Base = rawPlace,
                            TypeId = load.Target.TypeId,
                            Span = load.Source.Span
                        }
                    });
                    continue;
                }

                instructions.Add(CloneInstruction(instruction));
            }

            blocks.Add(new MirBasicBlock
            {
                Id = block.Id,
                Instructions = instructions,
                Terminator = block.Terminator,
                Span = block.Span,
                IsEntry = block.IsEntry
            });
        }

        var suffix = $"__range_{parameterIndex}";
        var sourceName = string.IsNullOrWhiteSpace(template.SourceName) ? template.Name : template.SourceName;
        var functionId = template.FunctionId with
        {
            SymbolId = SymbolId.None,
            StableIdentityKey = $"{MirFunctionIdentity.GetStableKey(template)}|range:{parameterIndex}",
            Name = $"{template.FunctionId.Name}{suffix}",
            QualifiedName = $"{template.FunctionId.QualifiedName}{suffix}",
            MangledName = string.Empty
        };
        variant = new MirFunc
        {
            Name = $"{template.Name}{suffix}",
            SourceName = $"{sourceName}{suffix}",
            Locals = locals,
            BasicBlocks = blocks,
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
        variant.OwnershipContract = OwnershipContract.Create(
            SymbolId.None,
            variant.Name,
            locals.Where(static local => local.IsParameter).Select(static local => (local.Name, local.TypeId)).ToArray(),
            variant.ReturnType,
            module.TypeDescriptors);
        return true;
    }

    private static Dictionary<InstructionSite, BorrowedIndexBaseRewrite> FindBorrowedIndexBaseRewrites(
        MirFunc function,
        RangeConsumerAnalysis analysis)
    {
        var indexSitesByBase = new Dictionary<LocalId, List<InstructionSite>>();
        foreach (var site in analysis.IndexLoads)
        {
            var block = function.BasicBlocks.First(candidate => candidate.Id == site.Block);
            if (block.Instructions[site.Index] is not MirLoad
                {
                    Source: MirPlace
                    {
                        Kind: PlaceKind.Index,
                        Base: MirPlace { Kind: PlaceKind.Local, Local: var baseLocal }
                    }
                })
            {
                continue;
            }

            if (!indexSitesByBase.TryGetValue(baseLocal, out var sites))
            {
                sites = [];
                indexSitesByBase[baseLocal] = sites;
            }
            sites.Add(site);
        }

        var rewrites = new Dictionary<InstructionSite, BorrowedIndexBaseRewrite>();
        foreach (var (baseLocal, indexSites) in indexSitesByBase)
        {
            if (CountReadUses(function, baseLocal) != indexSites.Count)
            {
                continue;
            }

            var definitions = function.BasicBlocks
                .SelectMany(block => block.Instructions.Select(
                    (instruction, index) => (block.Id, index, instruction)))
                .Where(candidate => candidate.instruction is MirLoad
                {
                    Target: MirPlace { Kind: PlaceKind.Local, Local: var targetLocal },
                    Source: MirPlace { Kind: PlaceKind.Deref, Base: not null },
                    MovesOutOfSource: false
                } && targetLocal == baseLocal)
                .ToArray();
            if (definitions.Length != 1 ||
                definitions[0].instruction is not MirLoad
                {
                    Source: MirPlace { Base: not null } source
                })
            {
                continue;
            }

            var definition = new InstructionSite(definitions[0].Id, definitions[0].index);
            foreach (var indexSite in indexSites)
            {
                rewrites[indexSite] = new BorrowedIndexBaseRewrite(definition, source.Base);
            }
        }

        return rewrites;
    }

    private static bool TryAnalyzeRangeConsumer(
        MirFunc function,
        LocalId parameter,
        out RangeConsumerAnalysis analysis)
    {
        analysis = null!;
        var cfg = new ControlFlowGraph(function);
        var inStates = function.BasicBlocks.ToDictionary(static block => block.Id, static _ => (HashSet<PlaceSlot>?)null);
        var outStates = function.BasicBlocks.ToDictionary(static block => block.Id, static _ => (HashSet<PlaceSlot>?)null);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in function.BasicBlocks)
            {
                var incoming = new List<HashSet<PlaceSlot>>();
                if (block.Id == function.EntryBlockId)
                {
                    incoming.Add([new PlaceSlot(parameter, string.Empty)]);
                }
                foreach (var predecessor in cfg.GetPredecessors(block.Id))
                {
                    if (outStates.GetValueOrDefault(predecessor) is { } predecessorState)
                    {
                        incoming.Add(predecessorState);
                    }
                }
                if (incoming.Count == 0)
                {
                    continue;
                }

                var nextIn = new HashSet<PlaceSlot>(incoming[0]);
                foreach (var state in incoming.Skip(1))
                {
                    nextIn.IntersectWith(state);
                }
                var nextOut = new HashSet<PlaceSlot>(nextIn);
                foreach (var instruction in block.Instructions)
                {
                    ApplyRangeTransfer(instruction, nextOut);
                }

                if (inStates[block.Id] == null || !inStates[block.Id]!.SetEquals(nextIn))
                {
                    inStates[block.Id] = nextIn;
                    changed = true;
                }
                if (outStates[block.Id] == null || !outStates[block.Id]!.SetEquals(nextOut))
                {
                    outStates[block.Id] = nextOut;
                    changed = true;
                }
            }
        }

        var lengthCalls = new HashSet<InstructionSite>();
        var indexLoads = new HashSet<InstructionSite>();
        foreach (var block in function.BasicBlocks)
        {
            if (inStates[block.Id] is not { } incoming)
            {
                continue;
            }

            var state = new HashSet<PlaceSlot>(incoming);
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                var site = new InstructionSite(block.Id, index);
                if (!ValidateRangeUse(instruction, state, site, lengthCalls, indexLoads))
                {
                    return false;
                }
                ApplyRangeTransfer(instruction, state);
            }

            if (TerminatorUsesRange(block.Terminator, state))
            {
                return false;
            }
        }

        if (lengthCalls.Count == 0 || indexLoads.Count == 0)
        {
            return false;
        }

        analysis = new RangeConsumerAnalysis(lengthCalls, indexLoads);
        return true;
    }

    private static bool ValidateRangeUse(
        MirInstruction instruction,
        HashSet<PlaceSlot> state,
        InstructionSite site,
        HashSet<InstructionSite> lengthCalls,
        HashSet<InstructionSite> indexLoads)
    {
        switch (instruction)
        {
            case MirCall call:
                var rangeArguments = call.Arguments
                    .Select((argument, index) => (argument, index))
                    .Where(pair => OperandOverlapsRange(pair.argument, state))
                    .ToArray();
                if (rangeArguments.Length == 0)
                {
                    return true;
                }
                if (rangeArguments.Length == 1 &&
                    rangeArguments[0].index == 0 &&
                    call.Arguments.Count == 1 &&
                    call.Function is MirFunctionRef functionRef &&
                    IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArrayLength))
                {
                    lengthCalls.Add(site);
                    return true;
                }
                return false;
            case MirLoad
            {
                Source: MirPlace
                {
                    Kind: PlaceKind.Index,
                    IndexAccessKind: MirIndexAccessKind.RuntimeArray,
                    Base: not null,
                    Index: not null
                } index,
                MovesOutOfSource: false
            }:
                if (OperandOverlapsRange(index.Base, state))
                {
                    if (OperandOverlapsRange(index.Index, state))
                    {
                        return false;
                    }
                    indexLoads.Add(site);
                }
                return true;
            case MirLoad
            {
                Source: MirPlace
                {
                    Kind: PlaceKind.Index,
                    IndexAccessKind: MirIndexAccessKind.RuntimeArray,
                    Base: not null
                } index
            }:
                return !OperandOverlapsRange(index.Base, state);
            case MirLoad { Source: MirPlace { Kind: PlaceKind.Deref, Base: not null } deref }:
                return OperandOverlapsRange(deref.Base, state) || !OperandOverlapsRange(deref, state);
            case MirLoad load:
                return !ProjectsInsideRange(load.Source, state);
            case MirStore store:
                return !ProjectsInsideRange(store.Target, state);
            case MirBinOp binary:
                return !OperandOverlapsRange(binary.Left, state) && !OperandOverlapsRange(binary.Right, state);
            case MirUnaryOp unary:
                return !OperandOverlapsRange(unary.Operand, state);
            case MirSelect select:
                return !OperandOverlapsRange(select.Condition, state) &&
                       !OperandOverlapsRange(select.TrueValue, state) &&
                       !OperandOverlapsRange(select.FalseValue, state);
            case MirCaseInject injection:
                return !OperandOverlapsRange(injection.Operand, state);
            default:
                return true;
        }
    }

    private static void ApplyRangeTransfer(MirInstruction instruction, HashSet<PlaceSlot> state)
    {
        switch (instruction)
        {
            case MirAlloc alloc:
                ClearPlace(alloc.Target, state);
                break;
            case MirAssign assign when assign.Target is MirPlace target:
                CopyRange(assign.Source, target, state, consumeSource: false);
                break;
            case MirCaseInject injection when injection.Target is MirPlace target:
                ClearPlace(target, state);
                break;
            case MirCopy copy:
                CopyRange(copy.Source, copy.Target, state, consumeSource: false);
                break;
            case MirMove move:
                CopyRange(move.Source, move.Target, state, consumeSource: true);
                break;
            case MirLoad load:
                if (load.Source is MirPlace { Kind: PlaceKind.Index, IndexAccessKind: MirIndexAccessKind.RuntimeArray })
                {
                    ClearPlace(load.Target, state);
                }
                else if (load.Source is MirPlace { Kind: PlaceKind.Deref, Base: not null } deref &&
                         OperandOverlapsRange(deref.Base, state))
                {
                    ClearPlace(load.Target, state);
                    AddPlace(load.Target, state);
                }
                else
                {
                    CopyRange(load.Source, load.Target, state, load.MovesOutOfSource);
                }
                break;
            case MirStore store:
                CopyRange(store.Value, store.Target, state, consumeSource: true);
                break;
            case MirDrop drop:
                RemoveOperand(drop.Value, state);
                break;
            case MirCall call:
                if (call.Target != null)
                {
                    ClearPlace(call.Target, state);
                }
                break;
            case MirBinOp binary when binary.Target is MirPlace target:
                ClearPlace(target, state);
                break;
            case MirUnaryOp unary when unary.Target is MirPlace target:
                ClearPlace(target, state);
                break;
            case MirSelect select:
                ClearPlace(select.Target, state);
                break;
        }
    }

    private static void CopyRange(
        MirOperand source,
        MirPlace target,
        HashSet<PlaceSlot> state,
        bool consumeSource)
    {
        ClearPlace(target, state);
        if (source is not MirPlace sourcePlace ||
            !TryGetSlot(sourcePlace, out var sourceSlot) ||
            !TryGetSlot(target, out var targetSlot))
        {
            return;
        }

        var copied = state.Where(candidate =>
                candidate.Root == sourceSlot.Root &&
                (candidate.Path == sourceSlot.Path ||
                 candidate.Path.StartsWith($"{sourceSlot.Path}/", StringComparison.Ordinal)))
            .ToArray();
        foreach (var candidate in copied)
        {
            var suffix = candidate.Path[sourceSlot.Path.Length..];
            state.Add(targetSlot with { Path = $"{targetSlot.Path}{suffix}" });
        }
        if (consumeSource)
        {
            RemoveOperand(source, state);
        }
    }

    private static void AddPlace(MirPlace place, HashSet<PlaceSlot> state)
    {
        if (TryGetSlot(place, out var slot))
        {
            state.Add(slot);
        }
    }

    private static void ClearPlace(MirPlace place, HashSet<PlaceSlot> state)
    {
        if (TryGetSlot(place, out var slot))
        {
            state.RemoveWhere(candidate => SlotsOverlap(candidate, slot));
        }
    }

    private static void RemoveOperand(MirOperand operand, HashSet<PlaceSlot> state)
    {
        if (operand is MirPlace place)
        {
            ClearPlace(place, state);
        }
    }

    private static bool OperandOverlapsRange(MirOperand operand, IReadOnlySet<PlaceSlot> state)
    {
        if (operand is not MirPlace place)
        {
            return false;
        }
        if (place.Kind == PlaceKind.Deref && place.Base != null)
        {
            return OperandOverlapsRange(place.Base, state);
        }
        return TryGetSlot(place, out var slot) && state.Any(candidate => SlotsOverlap(candidate, slot));
    }

    private static bool ProjectsInsideRange(MirOperand operand, IReadOnlySet<PlaceSlot> state)
    {
        if (operand is not MirPlace place || !TryGetSlot(place, out var slot))
        {
            return false;
        }
        return state.Any(candidate => candidate.Root == slot.Root &&
            candidate.Path.Length < slot.Path.Length &&
            slot.Path.StartsWith($"{candidate.Path}/", StringComparison.Ordinal));
    }

    private static bool SlotsOverlap(PlaceSlot left, PlaceSlot right) =>
        left.Root == right.Root &&
        (left.Path == right.Path ||
         left.Path.Length == 0 ||
         right.Path.Length == 0 ||
         left.Path.StartsWith($"{right.Path}/", StringComparison.Ordinal) ||
         right.Path.StartsWith($"{left.Path}/", StringComparison.Ordinal));

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

    private static bool TerminatorUsesRange(MirTerminator? terminator, IReadOnlySet<PlaceSlot> state) => terminator switch
    {
        MirReturn { Value: not null } result => OperandOverlapsRange(result.Value, state),
        MirSwitch branch => OperandOverlapsRange(branch.Discriminant, state),
        _ => false
    };

    private static bool IsArrayIntrinsic(MirFunctionRef functionRef, string name) =>
        MirRuntimeFunctions.HasIdentity(functionRef, name) ||
        MirBuiltinFunctions.TryGetIntrinsicName(functionRef, out var intrinsic) &&
        string.Equals(intrinsic, name, StringComparison.Ordinal);

    private static bool IsIntegerConstant(MirOperand operand, long value) =>
        operand is MirConstant { Value: MirConstantValue.IntValue(var integer) } && integer == value;

    private static MirPlace LocalPlace(MirLocal local) => new()
    {
        Kind = PlaceKind.Local,
        Local = local.Id,
        TypeId = local.TypeId,
        Span = local.Span
    };

    private static MirInstruction CloneInstruction(MirInstruction instruction) => instruction switch
    {
        MirCall call => call with
        {
            Arguments = call.Arguments.ToList(),
            BorrowedArgumentIndices = call.BorrowedArgumentIndices.ToHashSet(),
            RecordUpdate = call.RecordUpdate == null
                ? null
                : call.RecordUpdate with { UpdatedFieldIndices = call.RecordUpdate.UpdatedFieldIndices.ToList() }
        },
        _ => instruction
    };

    private static MirFunctionRef RewriteFunctionRef(MirFunctionRef functionRef, MirFunc target) => functionRef with
    {
        Name = target.Name,
        SymbolId = target.SymbolId,
        FunctionId = target.FunctionId
    };

    private static int CountReadUses(MirFunc function, LocalId local)
    {
        var count = 0;
        foreach (var block in function.BasicBlocks)
        {
            count += block.Instructions.Sum(instruction => CountInstructionReadUses(instruction, local));
            count += CountTerminatorUses(block.Terminator, local);
        }
        return count;
    }

    private static bool InstructionReadsLocal(MirInstruction instruction, LocalId local) =>
        CountInstructionReadUses(instruction, local) > 0;

    private static int CountInstructionReadUses(MirInstruction instruction, LocalId local) => instruction switch
    {
        MirAssign assign => CountOperandUses(assign.Source, local),
        MirCaseInject injection => CountOperandUses(injection.Operand, local),
        MirCall call => CountOperandUses(call.Function, local) +
                        call.Arguments.Sum(argument => CountOperandUses(argument, local)) +
                        (call.RecordUpdate == null ? 0 : CountOperandUses(call.RecordUpdate.Source, local)),
        MirBinOp binary => CountOperandUses(binary.Left, local) + CountOperandUses(binary.Right, local),
        MirUnaryOp unary => CountOperandUses(unary.Operand, local),
        MirSelect select => CountOperandUses(select.Condition, local) +
                            CountOperandUses(select.TrueValue, local) +
                            CountOperandUses(select.FalseValue, local),
        MirLoad load => CountOperandUses(load.Source, local),
        MirStore store => CountOperandUses(store.Target, local) + CountOperandUses(store.Value, local),
        MirDrop drop => CountOperandUses(drop.Value, local),
        MirCopy copy => CountOperandUses(copy.Source, local),
        MirMove move => CountOperandUses(move.Source, local),
        _ => 0
    };

    private static bool TerminatorReadsLocal(MirTerminator? terminator, LocalId local) =>
        CountTerminatorUses(terminator, local) > 0;

    private static int CountTerminatorUses(MirTerminator? terminator, LocalId local) => terminator switch
    {
        MirReturn { Value: not null } result => CountOperandUses(result.Value, local),
        MirSwitch branch => CountOperandUses(branch.Discriminant, local),
        _ => 0
    };

    private static int CountOperandUses(MirOperand operand, LocalId local) => operand switch
    {
        MirPlace { Kind: PlaceKind.Local, Local: var candidate } => candidate == local ? 1 : 0,
        MirPlace place =>
            (place.Base == null ? 0 : CountOperandUses(place.Base, local)) +
            (place.Index == null ? 0 : CountOperandUses(place.Index, local)),
        _ => 0
    };

    private static bool OperandContainsLocal(MirOperand operand, LocalId local) => CountOperandUses(operand, local) > 0;

    private static bool IsExactLocal(MirOperand operand, LocalId local) =>
        operand is MirPlace { Kind: PlaceKind.Local, Local: var candidate } && candidate == local;

    private sealed record SliceSite(MirBasicBlock Block, MirCall Call, MirPlace Source, MirPlace Target);

    private sealed record ShiftUse(MirBasicBlock Block, MirCall Call);

    private sealed record RangeCallPlan(MirBasicBlock Block, MirCall Call, int ArgumentIndex, MirFunc Variant);

    private sealed record FusionPlan(SliceSite Slice, ShiftUse Shift, IReadOnlyList<RangeCallPlan> RangeCalls);

    private sealed record RangeConsumerAnalysis(
        IReadOnlySet<InstructionSite> LengthCalls,
        IReadOnlySet<InstructionSite> IndexLoads);

    private sealed record BorrowedIndexBaseRewrite(InstructionSite Definition, MirOperand Base);

    private readonly record struct RangeVariantKey(string TemplateKey, int ParameterIndex);

    private readonly record struct InstructionSite(BlockId Block, int Index);

    private readonly record struct PlaceSlot(LocalId Root, string Path);
}
