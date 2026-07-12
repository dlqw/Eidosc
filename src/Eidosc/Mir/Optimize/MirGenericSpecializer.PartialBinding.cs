using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private bool TryRewriteImmediatePartialApplicationChain(
        MirFunc function,
        MirBasicBlock block,
        int instructionIndex,
        MirCall resolvedPartialCall,
        MirFunctionRef sourceFunctionRef,
        TemplateInfo template,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        if (resolvedPartialCall.Target is not { Kind: PlaceKind.Local } partialTarget ||
            instructionIndex + 1 >= block.Instructions.Count)
        {
            return false;
        }

        if (!TryCollectDeferredLocalIdsForImmediateChainStart(
                function,
                block,
                instructionIndex,
                resolvedPartialCall.Arguments,
                localTypes,
                IsCopySafeType,
                out var deferredLocalIds))
        {
            return false;
        }

        var partialInstructions = new List<(int InstructionIndex, MirPlace Target)>
        {
            (instructionIndex, partialTarget)
        };
        var currentInstructionIndex = instructionIndex;
        var currentPartialTarget = partialTarget;
        var currentArguments = CloneOperands(resolvedPartialCall.Arguments);

        while (true)
        {
            if (!TryFindDeferredPartialApplication(
                    block,
                    currentInstructionIndex,
                    currentPartialTarget.Local,
                    deferredLocalIds,
                    out var applicationInstructionIndex,
                    out var applicationCall))
            {
                return false;
            }

            if (HasNonDeferredUses(
                    function,
                    block.Id,
                    currentInstructionIndex,
                    applicationInstructionIndex,
                    currentPartialTarget.Local))
            {
                return false;
            }

            var combinedArguments = CombineBoundArguments(currentArguments, applicationCall.Arguments);
            var templateParameterCount = GetTemplateParameterCount(template.TemplateSource);
            if (combinedArguments.Count > templateParameterCount)
            {
                return false;
            }

            var combinedCall = applicationCall with
            {
                Function = sourceFunctionRef,
                Arguments = combinedArguments
            };
            var templateRewrittenCombinedCall = RewriteCallArgumentFunctionValuesFromTemplate(
                combinedCall,
                template.TemplateSource,
                localTypes,
                workingFunctions,
                queue);
            if (templateRewrittenCombinedCall != combinedCall)
            {
                combinedCall = templateRewrittenCombinedCall;
            }

            if (TryResolveSignature(combinedCall, template.TemplateSource, localTypes, out var signature))
            {
                if (!HasMeaningfulSpecializationSignature(template, signature))
                {
                    return false;
                }

                if (!IsMonomorphicSignature(signature))
                {
                    if (SignatureContainsOpenConstructorBinding(signature))
                    {
                        RecordRejectedSpecialization(
                            template,
                            signature,
                            SpecializationFailureReason.UnresolvedConstructorBinding);
                    }

                    return false;
                }

                if (!TryGetOrCreateSpecialization(template, signature, workingFunctions, queue, out var specializedFunction))
                {
                    return false;
                }

                var rewrittenFunctionRef = RewriteFunctionReference(
                    sourceFunctionRef,
                    specializedFunction,
                    signature.ReturnType);

                foreach (var (partialInstruction, partialValueTarget) in partialInstructions)
                {
                    block.Instructions[partialInstruction] = new MirAssign
                    {
                        Target = partialValueTarget,
                        Source = sourceFunctionRef,
                        Span = block.Instructions[partialInstruction].Span
                    };
                }

                block.Instructions[applicationInstructionIndex] = RewriteCallArgumentFunctionValues(
                    combinedCall with
                    {
                        Function = rewrittenFunctionRef
                    },
                    signature.ParameterTypes,
                    workingFunctions,
                    queue);
                return true;
            }

            if (applicationCall.Target is not { Kind: PlaceKind.Local } nextPartialTarget)
            {
                return false;
            }

            partialInstructions.Add((applicationInstructionIndex, nextPartialTarget));
            currentInstructionIndex = applicationInstructionIndex;
            currentPartialTarget = nextPartialTarget;
            currentArguments = combinedArguments;
        }
    }

    private bool TryRewriteImmediateDirectPartialApplication(
        MirFunc function,
        MirBasicBlock block,
        int instructionIndex,
        MirCall partialCall,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        if (partialCall is not
            {
                Function: MirFunctionRef functionRef,
                Target: { Kind: PlaceKind.Local } partialTarget
            } ||
            TryResolveTemplateKey(functionRef, out _) ||
            !TryResolveCallableParameterTypes(functionRef, localTypes, out var parameterTypes) ||
            partialCall.Arguments.Count == 0 ||
            partialCall.Arguments.Count >= GetDirectPartialApplicationParameterLimit(functionRef, parameterTypes.Count))
        {
            return false;
        }

        var parameterLimit = GetDirectPartialApplicationParameterLimit(functionRef, parameterTypes.Count);

        if (!TryCollectDeferredLocalIdsForImmediateChainStart(
                function,
                block,
                instructionIndex,
                partialCall.Arguments,
                localTypes,
                IsCopySafeType,
                out var deferredLocalIds))
        {
            return false;
        }

        var partialInstructions = new List<(int InstructionIndex, MirPlace Target)>
        {
            (instructionIndex, partialTarget)
        };
        var currentInstructionIndex = instructionIndex;
        var currentPartialTarget = partialTarget;
        var currentArguments = CloneOperands(partialCall.Arguments);

        while (true)
        {
            if (!TryFindDeferredPartialApplication(
                    block,
                    currentInstructionIndex,
                    currentPartialTarget.Local,
                    deferredLocalIds,
                    out var applicationInstructionIndex,
                    out var applicationCall))
            {
                return false;
            }

            if (HasNonDeferredUses(
                    function,
                    block.Id,
                    currentInstructionIndex,
                    applicationInstructionIndex,
                    currentPartialTarget.Local))
            {
                return false;
            }

            var combinedArguments = CombineBoundArguments(currentArguments, applicationCall.Arguments);
            if (combinedArguments.Count == parameterLimit)
            {
                foreach (var (partialInstructionIndex, partialValueTarget) in partialInstructions)
                {
                    block.Instructions[partialInstructionIndex] = new MirAssign
                    {
                        Target = partialValueTarget,
                        Source = functionRef,
                        Span = block.Instructions[partialInstructionIndex].Span
                    };
                }

                block.Instructions[applicationInstructionIndex] = applicationCall with
                {
                    Function = functionRef,
                    Arguments = RewriteFunctionValueOperands(
                        combinedArguments,
                        parameterTypes.Take(parameterLimit).ToList(),
                        workingFunctions,
                        queue)
                };
                return true;
            }

            if (combinedArguments.Count > parameterLimit ||
                applicationCall.Target is not { Kind: PlaceKind.Local } nextPartialTarget)
            {
                return false;
            }

            partialInstructions.Add((applicationInstructionIndex, nextPartialTarget));
            currentInstructionIndex = applicationInstructionIndex;
            currentPartialTarget = nextPartialTarget;
            currentArguments = combinedArguments;
        }
    }

    private int GetDirectPartialApplicationParameterLimit(
        MirFunctionRef functionRef,
        int fallbackParameterCount)
    {
        if (functionRef.SymbolId.IsValid &&
            _functionBySymbol.TryGetValue(functionRef.SymbolId, out var function))
        {
            var declaredParameterCount = function.Locals.Count(static local => local.IsParameter);
            if (declaredParameterCount > 0)
            {
                return declaredParameterCount;
            }
        }

        return fallbackParameterCount;
    }

    private static bool TryCollectDeferredLocalIdsForImmediateChainStart(
        MirFunc function,
        MirBasicBlock block,
        int instructionIndex,
        IReadOnlyList<MirOperand> boundArguments,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        Func<TypeId, bool> isCopySafeType,
        out HashSet<LocalId> deferredLocalIds)
    {
        deferredLocalIds = [];

        if (boundArguments.Count != 1 ||
            boundArguments[0] is not MirPlace { Kind: PlaceKind.Local } boundLocal)
        {
            return boundArguments.Count == 1 && boundArguments[0] is MirFunctionRef;
        }

        if (instructionIndex == 0)
        {
            // 在指令 0 处，绑定参数必定是函数参数（无前驱指令），
            // 仅当参数类型为 copy-safe 时才允许即时链重写，
            // 否则可能内联一个非 copy 语义的值导致重复消费。
            var boundLocalType = boundLocal.TypeId.IsValid
                ? boundLocal.TypeId
                : (localTypes.TryGetValue(boundLocal.Local, out var localType) ? localType : TypeId.None);
            return boundLocalType.IsValid && isCopySafeType(boundLocalType);
        }

        var precedingInstruction = block.Instructions[instructionIndex - 1];
        var canDefer = precedingInstruction switch
        {
            MirMove move when move.Target.Local.Equals(boundLocal.Local) => true,
            MirCopy copy when copy.Target.Local.Equals(boundLocal.Local) => true,
            MirLoad load when load.Target.Local.Equals(boundLocal.Local) => true,
            _ => false
        };
        if (!canDefer)
        {
            return false;
        }

        deferredLocalIds.Add(boundLocal.Local);
        return true;
    }

    private static bool TryFindDeferredPartialApplication(
        MirBasicBlock block,
        int partialInstructionIndex,
        LocalId partialLocalId,
        IReadOnlySet<LocalId> deferredLocalIds,
        out int applicationInstructionIndex,
        out MirCall applicationCall)
    {
        applicationInstructionIndex = -1;
        applicationCall = default!;

        var currentLocalId = partialLocalId;

        for (var instructionIndex = partialInstructionIndex + 1; instructionIndex < block.Instructions.Count; instructionIndex++)
        {
            var instruction = block.Instructions[instructionIndex];
            if (deferredLocalIds.Any(localId => InstructionUsesLocal(instruction, localId)))
            {
                return false;
            }

            if (!InstructionUsesLocal(instruction, currentLocalId))
            {
                continue;
            }

            if (instruction is MirMove { Target: var moveTarget } move &&
                moveTarget.Kind == PlaceKind.Local &&
                move.Source is MirPlace { Kind: PlaceKind.Local, Local: var moveSource } &&
                moveSource.Equals(currentLocalId))
            {
                currentLocalId = moveTarget.Local;
                continue;
            }

            if (instruction is MirCall call &&
                call.Function is MirPlace { Kind: PlaceKind.Local } functionLocal &&
                functionLocal.Local.Equals(currentLocalId) &&
                !InstructionUsesLocalOutsideImmediateFunctionOperand(call, currentLocalId))
            {
                applicationInstructionIndex = instructionIndex;
                applicationCall = call;
                return true;
            }

            return false;
        }

        return false;
    }

    private bool IsMonomorphicSignature(SpecializationSignature signature)
    {
        if (ContainsOpenTypeVariable(signature.ReturnType))
        {
            return false;
        }

        foreach (var parameterType in signature.ParameterTypes)
        {
            if (ContainsOpenTypeVariable(parameterType))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryPreparePartialBindingArguments(
        MirFunc function,
        IReadOnlyList<MirOperand> combinedArguments,
        Dictionary<LocalId, TypeId> localTypes,
        SourceSpan span,
        out IReadOnlyList<MirOperand> preparedArguments,
        out List<MirInstruction> captureInstructions)
    {
        var prepared = new List<MirOperand>(combinedArguments.Count);
        captureInstructions = [];

        for (var index = 0; index < combinedArguments.Count; index++)
        {
            var argument = combinedArguments[index];
            if (IsInlineSafeBoundArgument(argument, localTypes))
            {
                prepared.Add(CloneOperand(argument));
                continue;
            }

            if (!TryCaptureBoundArgument(
                    function,
                    argument,
                    localTypes,
                    span,
                    out var capturedArgument,
                    out var captureInstruction))
            {
                preparedArguments = [];
                captureInstructions = [];
                return false;
            }

            prepared.Add(capturedArgument);
            captureInstructions.Add(captureInstruction);
        }

        preparedArguments = prepared;
        return true;
    }

    private bool TryCaptureBoundArgument(
        MirFunc function,
        MirOperand operand,
        Dictionary<LocalId, TypeId> localTypes,
        SourceSpan span,
        out MirOperand capturedOperand,
        out MirInstruction captureInstruction)
    {
        capturedOperand = default!;
        captureInstruction = default!;

        if (operand is not MirPlace place)
        {
            return false;
        }

        var placeType = ResolvePlaceType(place, localTypes);
        if (!IsCopySafeType(placeType))
        {
            return false;
        }

        var captureLocal = AllocateCaptureLocal(function, placeType, span);
        localTypes[captureLocal.Local] = captureLocal.TypeId;
        captureInstruction = new MirLoad
        {
            Target = captureLocal,
            Source = (MirPlace)CloneOperand(place),
            Span = span
        };
        capturedOperand = captureLocal;
        return true;
    }

    private static MirPlace AllocateCaptureLocal(MirFunc function, TypeId typeId, SourceSpan span)
    {
        var nextLocalId = function.Locals
            .Select(local => local.Id.Value)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var localId = new LocalId { Value = nextLocalId };
        function.Locals.Add(new MirLocal
        {
            Id = localId,
            Name = $"{WellKnownStrings.InternalNames.SpecCapturePrefix}{nextLocalId}",
            TypeId = typeId,
            IsMutable = false,
            IsParameter = false,
            Span = span
        });

        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = localId,
            TypeId = typeId,
            Span = span
        };
    }

    private static bool TryLowerGenericPartialCallToAssign(
        MirCall call,
        MirFunctionRef sourceFunctionRef,
        out MirInstruction loweredInstruction)
    {
        loweredInstruction = default!;

        if (call.Target is not { Kind: PlaceKind.Local } targetLocal)
        {
            return false;
        }

        loweredInstruction = new MirAssign
        {
            Target = targetLocal,
            Source = sourceFunctionRef,
            Span = call.Span
        };
        return true;
    }

    private bool TryResolveTemplateCallTarget(
        MirOperand functionOperand,
        IReadOnlyDictionary<LocalId, LocalCallBinding> localCallBindings,
        out MirFunctionRef sourceFunctionRef,
        out IReadOnlyList<MirOperand> boundArguments,
        out TemplateInfo template)
    {
        sourceFunctionRef = default!;
        boundArguments = [];
        template = default!;

        if (functionOperand is MirFunctionRef directFunctionRef &&
            TryResolveTemplateKey(directFunctionRef, out var directTemplateKey) &&
            _templateRegistry.ByKeyDict.TryGetValue(directTemplateKey, out var directTemplate))
        {
            template = directTemplate;
            sourceFunctionRef = directFunctionRef;
            boundArguments = [];
            return true;
        }
        if (functionOperand is MirPlace { Kind: PlaceKind.Local } localFunction &&
            localCallBindings.TryGetValue(localFunction.Local, out var aliasedBinding) &&
            aliasedBinding.SupportsDirectApplication &&
            TryResolveTemplateKey(aliasedBinding.FunctionRef, out var aliasTemplateKey) &&
            _templateRegistry.ByKeyDict.TryGetValue(aliasTemplateKey, out var aliasedTemplate))
        {
            template = aliasedTemplate;
            sourceFunctionRef = aliasedBinding.FunctionRef;
            boundArguments = aliasedBinding.BoundArguments;
            return true;
        }

        return false;
    }

    private static void UpdateLocalCallBindings(
        MirInstruction instruction,
        Dictionary<LocalId, LocalCallBinding> localCallBindings)
    {
        switch (instruction)
        {
            case MirAssign { Target.Kind: PlaceKind.Local } assign:
                UpdateLocalCallBinding(assign.Target.Local, assign.Source, localCallBindings);
                break;
            case MirStore { Target.Kind: PlaceKind.Local } store:
                UpdateLocalCallBinding(store.Target.Local, store.Value, localCallBindings);
                break;
            case MirLoad { Target.Kind: PlaceKind.Local } load:
                UpdateLocalCallBinding(load.Target.Local, load.Source, localCallBindings);
                break;
            case MirCopy copy:
                UpdateLocalCallBinding(copy.Target.Local, copy.Source, localCallBindings);
                break;
            case MirMove move:
                UpdateLocalCallBinding(move.Target.Local, move.Source, localCallBindings);
                localCallBindings.Remove(move.Source.Local);
                break;
            case MirCall { Target: { Kind: PlaceKind.Local } target }:
                localCallBindings.Remove(target.Local);
                break;
            case MirAlloc { Target.Kind: PlaceKind.Local } alloc:
                localCallBindings.Remove(alloc.Target.Local);
                break;
        }
    }

    private static void UpdateLocalCallBinding(
        LocalId targetLocal,
        MirOperand source,
        Dictionary<LocalId, LocalCallBinding> localCallBindings)
    {
        if (TryExtractCallBinding(source, localCallBindings, out var binding))
        {
            localCallBindings[targetLocal] = binding;
            return;
        }

        localCallBindings.Remove(targetLocal);
    }

    private static bool TryExtractCallBinding(
        MirOperand operand,
        IReadOnlyDictionary<LocalId, LocalCallBinding> localCallBindings,
        out LocalCallBinding binding)
    {
        binding = default!;

        if (operand is MirFunctionRef directFunctionRef)
        {
            binding = CreateLocalCallBinding(directFunctionRef, []);
            return true;
        }

        if (operand is MirPlace { Kind: PlaceKind.Local } localPlace &&
            localCallBindings.TryGetValue(localPlace.Local, out var aliasedBinding))
        {
            binding = CloneLocalCallBinding(aliasedBinding);
            return true;
        }

        return false;
    }

    private bool TryRecordGenericPartialBinding(
        MirPlace? callTarget,
        MirFunctionRef sourceFunctionRef,
        MirFunc template,
        IReadOnlyList<MirOperand> combinedArguments,
        bool supportsDirectApplication,
        out RecordedPartialBinding recordedBinding)
    {
        recordedBinding = default;

        if (callTarget is not { Kind: PlaceKind.Local } targetLocal)
        {
            return false;
        }

        var parameterCount = GetTemplateParameterCount(template);
        if (combinedArguments.Count == 0 || combinedArguments.Count >= parameterCount)
        {
            return false;
        }

        var binding = CreateLocalCallBinding(sourceFunctionRef, combinedArguments, supportsDirectApplication);
        recordedBinding = new RecordedPartialBinding(targetLocal.Local, binding);
        return true;
    }

    private static LocalCallBinding CreateLocalCallBinding(
        MirFunctionRef functionRef,
        IReadOnlyList<MirOperand> boundArguments,
        bool supportsDirectApplication = true)
    {
        var normalizedArguments = CloneOperandsUntracked(boundArguments);
        return new LocalCallBinding(
            functionRef with { },
            normalizedArguments,
            BuildBoundArgumentKey(normalizedArguments),
            supportsDirectApplication);
    }

    private void PruneDeadPartialTemplateCalls(MirFunc function, MirBasicBlock block)
    {
        Dictionary<LocalId, int>? localReadCounts = null;
        for (var instructionIndex = block.Instructions.Count - 1; instructionIndex >= 0; instructionIndex--)
        {
            var instruction = block.Instructions[instructionIndex];
            if (!IsPotentialDeadPartialTemplateInstruction(instruction))
            {
                continue;
            }

            localReadCounts ??= BuildLocalReadCounts(function);
            if (TryPruneDeadPartialTemplateCall(block, instructionIndex, instruction, localReadCounts) ||
                TryPruneDeadTemplateFunctionRefAssign(block, instructionIndex, instruction, localReadCounts))
            {
                continue;
            }
        }
    }

    private bool TryPruneDeadPartialTemplateCall(
        MirBasicBlock block,
        int instructionIndex,
        MirInstruction instruction,
        Dictionary<LocalId, int> localReadCounts)
    {
        if (instruction is not MirCall
            {
                Function: MirFunctionRef functionRef,
                Target: { Kind: PlaceKind.Local } target
            } call ||
            !TryResolveTemplateKey(functionRef, out var templateKey) ||
            !_templateRegistry.ByKeyDict.TryGetValue(templateKey, out var template) ||
            call.Arguments.Count >= GetTemplateParameterCount(template.TemplateSource) ||
            IsLocalReferencedElsewhere(instruction, target.Local, localReadCounts))
        {
            return false;
        }

        DecrementLocalReadCounts(localReadCounts, instruction);
        block.Instructions.RemoveAt(instructionIndex);
        return true;
    }

    private bool TryPruneDeadTemplateFunctionRefAssign(
        MirBasicBlock block,
        int instructionIndex,
        MirInstruction instruction,
        Dictionary<LocalId, int> localReadCounts)
    {
        if (instruction is not MirAssign
            {
                Target: { Kind: PlaceKind.Local } target,
                Source: MirFunctionRef functionRef
            } assign ||
            IsGeneratedSpecialization(functionRef.Name) ||
            IsLocalReferencedElsewhere(instruction, target.Local, localReadCounts))
        {
            return false;
        }

        DecrementLocalReadCounts(localReadCounts, instruction);
        block.Instructions.RemoveAt(instructionIndex);
        return true;
    }

    private bool IsPotentialDeadPartialTemplateInstruction(MirInstruction instruction)
    {
        return instruction switch
        {
            MirCall { Function: MirFunctionRef functionRef, Target.Kind: PlaceKind.Local } call
                when TryResolveTemplateKey(functionRef, out var templateKey) &&
                     _templateRegistry.ByKeyDict.TryGetValue(templateKey, out var template) &&
                     call.Arguments.Count < GetTemplateParameterCount(template.TemplateSource) => true,
            MirAssign { Target.Kind: PlaceKind.Local, Source: MirFunctionRef functionRef }
                when !IsGeneratedSpecialization(functionRef.Name) => true,
            _ => false
        };
    }

    private static bool IsLocalReferencedElsewhere(
        MirInstruction definingInstruction,
        LocalId localId,
        IReadOnlyDictionary<LocalId, int> localReadCounts)
    {
        var totalReads = localReadCounts.TryGetValue(localId, out var count) ? count : 0;
        return totalReads > CountInstructionLocalReads(definingInstruction, localId);
    }

    private static Dictionary<LocalId, int> BuildLocalReadCounts(MirFunc function)
    {
        var counts = new Dictionary<LocalId, int>();
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                AddInstructionLocalReads(counts, instruction);
            }

            if (block.Terminator != null)
            {
                AddTerminatorLocalReads(counts, block.Terminator);
            }
        }

        return counts;
    }

    private static void DecrementLocalReadCounts(
        Dictionary<LocalId, int> counts,
        MirInstruction instruction)
    {
        AddInstructionLocalReads(counts, instruction, -1);
    }

    private static int CountInstructionLocalReads(MirInstruction instruction, LocalId localId)
    {
        return instruction switch
        {
            MirAssign assign => CountOperandLocalReads(assign.Source, localId),
            MirCall call => CountOperandLocalReads(call.Function, localId) +
                            CountOperandListLocalReads(call.Arguments, localId),
            MirBinOp binOp => CountOperandLocalReads(binOp.Left, localId) +
                              CountOperandLocalReads(binOp.Right, localId),
            MirUnaryOp unaryOp => CountOperandLocalReads(unaryOp.Operand, localId),
            MirLoad load => CountOperandLocalReads(load.Source, localId),
            MirStore store => CountOperandLocalReads(store.Value, localId),
            MirDrop drop => CountOperandLocalReads(drop.Value, localId),
            MirCopy copy => CountPlaceLocalReads(copy.Source, localId),
            MirMove move => CountPlaceLocalReads(move.Source, localId),
            _ => 0
        };
    }

    private static void AddInstructionLocalReads(
        Dictionary<LocalId, int> counts,
        MirInstruction instruction,
        int delta = 1)
    {
        switch (instruction)
        {
            case MirAssign assign:
                AddOperandLocalReads(counts, assign.Source, delta);
                break;
            case MirCall call:
                AddOperandLocalReads(counts, call.Function, delta);
                AddOperandListLocalReads(counts, call.Arguments, delta);
                break;
            case MirBinOp binOp:
                AddOperandLocalReads(counts, binOp.Left, delta);
                AddOperandLocalReads(counts, binOp.Right, delta);
                break;
            case MirUnaryOp unaryOp:
                AddOperandLocalReads(counts, unaryOp.Operand, delta);
                break;
            case MirLoad load:
                AddOperandLocalReads(counts, load.Source, delta);
                break;
            case MirStore store:
                AddOperandLocalReads(counts, store.Value, delta);
                break;
            case MirDrop drop:
                AddOperandLocalReads(counts, drop.Value, delta);
                break;
            case MirCopy copy:
                AddPlaceLocalReads(counts, copy.Source, delta);
                break;
            case MirMove move:
                AddPlaceLocalReads(counts, move.Source, delta);
                break;
        }
    }

    private static void AddTerminatorLocalReads(
        Dictionary<LocalId, int> counts,
        MirTerminator terminator)
    {
        switch (terminator)
        {
            case MirReturn { Value: { } value }:
                AddOperandLocalReads(counts, value);
                break;
            case MirSwitch sw:
                AddOperandLocalReads(counts, sw.Discriminant);
                foreach (var branch in sw.Branches)
                {
                    AddOperandLocalReads(counts, branch.Value);
                }
                break;
        }
    }

    private static void AddOperandListLocalReads(
        Dictionary<LocalId, int> counts,
        IReadOnlyList<MirOperand> operands,
        int delta = 1)
    {
        for (var i = 0; i < operands.Count; i++)
        {
            AddOperandLocalReads(counts, operands[i], delta);
        }
    }

    private static void AddOperandLocalReads(
        Dictionary<LocalId, int> counts,
        MirOperand operand,
        int delta = 1)
    {
        if (operand is MirPlace place)
        {
            AddPlaceLocalReads(counts, place, delta);
        }
    }

    private static void AddPlaceLocalReads(
        Dictionary<LocalId, int> counts,
        MirPlace place,
        int delta = 1)
    {
        if (place.Kind == PlaceKind.Local)
        {
            AddLocalRead(counts, place.Local, delta);
        }

        if (place.Base != null)
        {
            AddPlaceLocalReads(counts, place.Base, delta);
        }

        if (place.Index != null)
        {
            AddOperandLocalReads(counts, place.Index, delta);
        }
    }

    private static void AddLocalRead(Dictionary<LocalId, int> counts, LocalId localId, int delta)
    {
        if (!localId.IsValid)
        {
            return;
        }

        if (!counts.TryGetValue(localId, out var current))
        {
            if (delta > 0)
            {
                counts[localId] = delta;
            }

            return;
        }

        var next = current + delta;
        if (next > 0)
        {
            counts[localId] = next;
        }
        else
        {
            counts.Remove(localId);
        }
    }

    private static int CountOperandListLocalReads(IReadOnlyList<MirOperand> operands, LocalId localId)
    {
        var count = 0;
        for (var i = 0; i < operands.Count; i++)
        {
            count += CountOperandLocalReads(operands[i], localId);
        }

        return count;
    }

    private static int CountOperandLocalReads(MirOperand operand, LocalId localId)
    {
        return operand is MirPlace place ? CountPlaceLocalReads(place, localId) : 0;
    }

    private static int CountPlaceLocalReads(MirPlace place, LocalId localId)
    {
        var count = place.Kind == PlaceKind.Local && place.Local.Equals(localId) ? 1 : 0;
        if (place.Base != null)
        {
            count += CountPlaceLocalReads(place.Base, localId);
        }

        if (place.Index != null)
        {
            count += CountOperandLocalReads(place.Index, localId);
        }

        return count;
    }

    private static bool InstructionReadsLocal(MirInstruction instruction, LocalId localId)
    {
        return instruction switch
        {
            MirAssign assign => OperandUsesLocal(assign.Source, localId),
            MirCall call => OperandUsesLocal(call.Function, localId) ||
                            call.Arguments.Any(argument => OperandUsesLocal(argument, localId)),
            MirBinOp binOp => OperandUsesLocal(binOp.Left, localId) ||
                              OperandUsesLocal(binOp.Right, localId),
            MirUnaryOp unaryOp => OperandUsesLocal(unaryOp.Operand, localId),
            MirLoad load => OperandUsesLocal(load.Source, localId),
            MirStore store => OperandUsesLocal(store.Value, localId),
            MirDrop drop => OperandUsesLocal(drop.Value, localId),
            MirCopy copy => PlaceUsesLocal(copy.Source, localId),
            MirMove move => PlaceUsesLocal(move.Source, localId),
            _ => false
        };
    }

    private static bool TerminatorReadsLocal(MirTerminator terminator, LocalId localId)
    {
        return terminator switch
        {
            MirReturn { Value: { } value } => OperandUsesLocal(value, localId),
            MirSwitch sw => OperandUsesLocal(sw.Discriminant, localId) ||
                            sw.Branches.Any(branch => OperandUsesLocal(branch.Value, localId)),
            _ => false
        };
    }

    private List<MirOperand> CombineBoundArguments(
        IReadOnlyList<MirOperand> boundArguments,
        IReadOnlyList<MirOperand> currentArguments)
    {
        _stats.CombineBoundArgumentLists++;
        var combined = new List<MirOperand>(boundArguments.Count + currentArguments.Count);
        combined.AddRange(CloneOperands(boundArguments));
        combined.AddRange(CloneOperands(currentArguments));
        return combined;
    }

    private List<MirOperand> CloneOperands(IReadOnlyList<MirOperand> operands)
    {
        _stats.CloneOperandLists++;
        _stats.CloneOperandListItems += operands.Count;
        var cloned = new List<MirOperand>(operands.Count);
        for (var index = 0; index < operands.Count; index++)
        {
            cloned.Add(CloneOperand(operands[index]));
        }

        return cloned;
    }

    private static List<MirOperand> CloneOperandsUntracked(IReadOnlyList<MirOperand> operands)
    {
        var cloned = new List<MirOperand>(operands.Count);
        for (var index = 0; index < operands.Count; index++)
        {
            cloned.Add(CloneOperand(operands[index]));
        }

        return cloned;
    }
}
