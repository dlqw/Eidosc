namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private bool RewriteBlockCallsAndTrackBindings(
        MirFunc function,
        MirBasicBlock block,
        Dictionary<LocalId, LocalCallBinding> localCallBindings,
        Dictionary<LocalId, TypeId> localTypes,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        var localTypesChanged = false;
        for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
        {
            var instruction = block.Instructions[instructionIndex];
            if (instruction is not MirCall call)
            {
                if (TryRewriteFunctionValueInstruction(
                        instruction,
                        localTypes,
                        workingFunctions,
                        queue,
                        out var rewrittenInstruction))
                {
                    instruction = rewrittenInstruction;
                    block.Instructions[instructionIndex] = rewrittenInstruction;
                }

                UpdateLocalCallBindings(instruction, localCallBindings);
                localTypesChanged |= RefineLocalTypesFromInstruction(function, instruction, localTypes);
                continue;
            }

            if (call.Function is MirFunctionRef builtinTraitRef &&
                ShouldKeepBuiltinTraitCall(function, builtinTraitRef, call, localTypes))
            {
                call = MarkBuiltinShowTraitCall(call, builtinTraitRef);
                instruction = call;
                block.Instructions[instructionIndex] = call;
                UpdateLocalCallBindings(instruction, localCallBindings);
                localTypesChanged |= RefineLocalTypesFromInstruction(function, instruction, localTypes);
                continue;
            }

            if (TryRewriteBuiltinEqTraitCall(call, localTypes, out var builtinEqInstruction))
            {
                instruction = builtinEqInstruction;
                block.Instructions[instructionIndex] = builtinEqInstruction;
                UpdateLocalCallBindings(instruction, localCallBindings);
                localTypesChanged |= RefineLocalTypesFromInstruction(function, instruction, localTypes);
                continue;
            }

            if (TryRewriteBuiltinCloneTraitCall(function, call, localTypes, out var builtinCloneInstruction))
            {
                instruction = builtinCloneInstruction;
                block.Instructions[instructionIndex] = builtinCloneInstruction;
                UpdateLocalCallBindings(instruction, localCallBindings);
                localTypesChanged |= RefineLocalTypesFromInstruction(function, instruction, localTypes);
                continue;
            }

            if (TryRewriteBoundBuiltinEqTraitCall(call, localCallBindings, localTypes, out var boundBuiltinEqInstruction))
            {
                instruction = boundBuiltinEqInstruction;
                block.Instructions[instructionIndex] = boundBuiltinEqInstruction;
                UpdateLocalCallBindings(instruction, localCallBindings);
                localTypesChanged |= RefineLocalTypesFromInstruction(function, instruction, localTypes);
                continue;
            }

            if (TryLowerBuiltinEqPartialCall(call, localTypes, out var loweredBuiltinEqPartial, out var builtinEqPartialBinding))
            {
                instruction = loweredBuiltinEqPartial;
                block.Instructions[instructionIndex] = loweredBuiltinEqPartial;
                if (loweredBuiltinEqPartial is MirAssign { Target.Kind: PlaceKind.Local } loweredAssign)
                {
                    localCallBindings[loweredAssign.Target.Local] = builtinEqPartialBinding;
                }

                localTypesChanged |= RefineLocalTypesFromInstruction(function, instruction, localTypes);
                continue;
            }

            if (TryRewriteImmediateDirectPartialApplication(
                    function,
                    block,
                    instructionIndex,
                    call,
                    localTypes,
                    workingFunctions,
                    queue))
            {
                if (call.Target is { Kind: PlaceKind.Local } rewrittenPartialTarget)
                {
                    localCallBindings.Remove(rewrittenPartialTarget.Local);
                }

                localTypesChanged = true;
                continue;
            }

            if (TryRewriteResultCarrierTraitCallFromConsumer(
                    block,
                    instructionIndex,
                    call,
                    localTypes,
                    out var consumerRewrittenTraitCall))
            {
                call = consumerRewrittenTraitCall;
                instruction = consumerRewrittenTraitCall;
                block.Instructions[instructionIndex] = consumerRewrittenTraitCall;
                UpdateLocalCallBindings(instruction, localCallBindings);
                localTypesChanged = true;
                localTypesChanged |= RefineLocalTypesFromInstruction(function, instruction, localTypes);
            }

            if (TryRewriteTraitMethodCall(function, call, localTypes, out var rewrittenTraitCall))
            {
                call = rewrittenTraitCall;
                instruction = rewrittenTraitCall;
                block.Instructions[instructionIndex] = rewrittenTraitCall;
                UpdateLocalCallBindings(instruction, localCallBindings);
                localTypesChanged = true;
                localTypesChanged |= RefineLocalTypesFromInstruction(function, instruction, localTypes);
                // Fall through to TryResolveTemplateCallTarget so that generic
                // trait impl methods (e.g. pure[A]) are also specialized.
            }

            if (!TryResolveTemplateCallTarget(
                    call.Function,
                    localCallBindings,
                    out var sourceFunctionRef,
                    out var boundArguments,
                    out var template))
            {
                UpdateLocalCallBindings(instruction, localCallBindings);
                localTypesChanged |= RefineLocalTypesFromInstruction(function, instruction, localTypes);
                continue;
            }

            // 判断已绑定参数是否可安全内联；非内联安全时跳过直接内联路径，
            // 但仍尝试捕获路径（TryPreparePartialBindingArguments 会创建副本）。
            var canInlineBoundArgs = boundArguments.Count == 0 ||
                                     CanInlineBoundArguments(boundArguments, localTypes);

            var combinedArguments = CombineBoundArguments(boundArguments, call.Arguments);
            var resolvedCall = call with
            {
                Function = sourceFunctionRef,
                Arguments = combinedArguments
            };
            RefineCallTargetTypeFromImmediateUse(block, instructionIndex, resolvedCall, localTypes);
            if (TryRewriteMonomorphicRecursiveCall(
                    function,
                    resolvedCall,
                    sourceFunctionRef,
                    template,
                    localTypes,
                    out var recursiveCall))
            {
                block.Instructions[instructionIndex] = recursiveCall;
                UpdateLocalCallBindings(recursiveCall, localCallBindings);
                localTypesChanged = true;
                localTypesChanged |= RefineLocalTypesFromInstruction(function, recursiveCall, localTypes);
                continue;
            }

            var templateRewrittenResolvedCall = RewriteCallArgumentFunctionValuesFromTemplate(
                resolvedCall,
                template.TemplateSource,
                localTypes,
                workingFunctions,
                queue);
            if (templateRewrittenResolvedCall != resolvedCall)
            {
                resolvedCall = templateRewrittenResolvedCall;
            }

            IReadOnlyList<TypeId> partialParameterTypeIds = [];

            var signatureResolved =
                TryResolveSignature(resolvedCall, template.TemplateSource, localTypes, out var signature) ||
                TryResolveConcreteCallShapeSignature(resolvedCall, template.TemplateSource, localTypes, out signature);
            if (!signatureResolved)
            {
                if (IsCompleteTemplateApplication(template.TemplateSource, combinedArguments))
                {
                    RecordRejectedSpecialization(
                        template,
                        BuildUnresolvedCallSignatureKey(resolvedCall, combinedArguments, localTypes),
                        BuildUnresolvedCallSignatureDisplay(resolvedCall, combinedArguments, localTypes),
                        SpecializationFailureReason.TypeInferenceFailed);
                }

                var partialFunctionRef = sourceFunctionRef;
                if (TryResolvePartialSignature(resolvedCall, template.TemplateSource, combinedArguments, localTypes, out var partialSignature) &&
                    HasMeaningfulSpecializationSignature(template, partialSignature) &&
                    IsMonomorphicSignature(partialSignature))
                {
                    if (TryGetOrCreateSpecialization(template, partialSignature, workingFunctions, queue, out var partialSpecialization))
                    {
                        partialParameterTypeIds = partialSpecialization.Locals
                            .Where(local => local.IsParameter)
                            .Select(local => local.TypeId)
                            .ToList();
                        partialFunctionRef = RewriteFunctionReference(
                            sourceFunctionRef,
                            partialSpecialization,
                            partialSpecialization.ReturnType);
                    }
                }

                if (TryRewriteImmediatePartialApplicationChain(
                        function,
                        block,
                        instructionIndex,
                        resolvedCall,
                        partialFunctionRef,
                        template,
                        localTypes,
                        workingFunctions,
                        queue))
                {
                    if (call.Target is { Kind: PlaceKind.Local } rewrittenPartialTarget)
                    {
                        localCallBindings.Remove(rewrittenPartialTarget.Local);
                    }

                    localTypesChanged = true;
                    continue;
                }

                if (canInlineBoundArgs &&
                    IsPartialTemplateApplication(template.TemplateSource, combinedArguments) &&
                    resolvedCall.Arguments.Count == 0 &&
                    TryLowerGenericPartialCallToAssign(resolvedCall, partialFunctionRef, out var loweredInstruction))
                {
                    block.Instructions[instructionIndex] = loweredInstruction;
                    UpdateLocalCallBindings(loweredInstruction, localCallBindings);
                    continue;
                }

                if (TryPreparePartialBindingArguments(
                        function,
                        combinedArguments,
                        localTypes,
                        call.Span,
                        out var preparedArguments,
                        out var captureInstructions) &&
                    TryRecordGenericPartialBinding(
                        call.Target,
                        partialFunctionRef,
                        template.TemplateSource,
                        RewriteFunctionValueOperands(
                            preparedArguments,
                            partialParameterTypeIds,
                            workingFunctions,
                            queue),
                        supportsDirectApplication: true,
                        out var recordedBinding))
                {
                    MirInstruction partialInstruction;
                    if (captureInstructions.Count == 0)
                    {
                        partialInstruction = resolvedCall with
                        {
                            Function = partialFunctionRef,
                            Arguments = recordedBinding.Binding.BoundArguments
                        };
                        block.Instructions[instructionIndex] = partialInstruction;
                    }
                    else
                    {
                        // 捕获路径：将原始 partial call 降级为 MirAssign（仅记录函数引用），
                        // 绑定参数已通过 capture instructions 复制到新的 local 中，
                        // 并由 localCallBindings 记录供后续 call 解析。
                        var partialTarget = resolvedCall.Target!;
                        partialInstruction = new MirAssign
                        {
                            Target = partialTarget,
                            Source = partialFunctionRef,
                            Span = resolvedCall.Span
                        };
                        var replacementInstructions = new List<MirInstruction>(captureInstructions.Count + 1);
                        replacementInstructions.AddRange(captureInstructions);
                        replacementInstructions.Add(partialInstruction);
                        block.Instructions.RemoveAt(instructionIndex);
                        block.Instructions.InsertRange(instructionIndex, replacementInstructions);
                        for (var captureIndex = 0; captureIndex < captureInstructions.Count; captureIndex++)
                        {
                            UpdateLocalCallBindings(captureInstructions[captureIndex], localCallBindings);
                        }

                        instructionIndex += captureInstructions.Count;
                    }

                    localCallBindings[recordedBinding.TargetLocal] = recordedBinding.Binding;
                    continue;
                }

                if (IsPartialTemplateApplication(template.TemplateSource, combinedArguments) &&
                    TryRecordGenericPartialBinding(
                        call.Target,
                        partialFunctionRef,
                        template.TemplateSource,
                        combinedArguments,
                        supportsDirectApplication: false,
                        out var deferredRecordedBinding))
                {
                    block.Instructions[instructionIndex] = resolvedCall with
                    {
                        Function = partialFunctionRef,
                        Arguments = deferredRecordedBinding.Binding.BoundArguments
                    };
                    localCallBindings[deferredRecordedBinding.TargetLocal] = deferredRecordedBinding.Binding;
                    localTypesChanged |= RefineLocalTypesFromInstruction(function, block.Instructions[instructionIndex], localTypes);
                    continue;
                }

                if (IsPartialTemplateApplication(template.TemplateSource, combinedArguments))
                {
                    RecordRejectedSpecialization(
                        template,
                        BuildUnresolvedCallSignatureKey(resolvedCall, combinedArguments, localTypes),
                        BuildUnresolvedCallSignatureDisplay(resolvedCall, combinedArguments, localTypes),
                        SpecializationFailureReason.PartialBindingIncomplete);
                }

                UpdateLocalCallBindings(instruction, localCallBindings);
                RefineLocalTypesFromInstruction(function, instruction, localTypes);
                continue;
            }

            if (TryRewriteImmediatePartialApplicationChain(
                    function,
                    block,
                    instructionIndex,
                    resolvedCall,
                    sourceFunctionRef,
                    template,
                    localTypes,
                    workingFunctions,
                    queue))
            {
                if (call.Target is { Kind: PlaceKind.Local } rewrittenPartialTarget)
                {
                    localCallBindings.Remove(rewrittenPartialTarget.Local);
                }

                localTypesChanged = true;
                continue;
            }

            var rewrittenResolvedCall = RewriteCallArgumentFunctionValues(
                block,
                instructionIndex,
                resolvedCall,
                signature.ParameterTypes,
                localCallBindings,
                localTypes,
                workingFunctions,
                queue,
                out var partialFunctionValueBackpatched);
            localTypesChanged |= partialFunctionValueBackpatched;
            if (rewrittenResolvedCall != resolvedCall)
            {
                resolvedCall = rewrittenResolvedCall;
                if (!TryResolveSignature(resolvedCall, template.TemplateSource, localTypes, out signature) &&
                    !TryResolveConcreteCallShapeSignature(resolvedCall, template.TemplateSource, localTypes, out signature))
                {
                    block.Instructions[instructionIndex] = resolvedCall;
                    UpdateLocalCallBindings(resolvedCall, localCallBindings);
                    localTypesChanged |= RefineLocalTypesFromInstruction(function, resolvedCall, localTypes);
                    continue;
                }
            }

            if (!HasMeaningfulSpecializationSignature(template, signature))
            {
                block.Instructions[instructionIndex] = resolvedCall;
                UpdateLocalCallBindings(resolvedCall, localCallBindings);
                localTypesChanged |= RefineLocalTypesFromInstruction(function, resolvedCall, localTypes);
                continue;
            }

            if (!IsMonomorphicSignature(signature) &&
                SignatureContainsOpenConstructorBinding(signature) &&
                TryBindConstructorBindingSignature(signature, out var boundConstructorBindingSignature))
            {
                signature = boundConstructorBindingSignature;
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

                var rewrittenGenericCall = RewriteCallArgumentFunctionValues(
                    block,
                    instructionIndex,
                    resolvedCall,
                    signature.ParameterTypes,
                    localCallBindings,
                    localTypes,
                    workingFunctions,
                    queue,
                    out var genericBackpatched);
                localTypesChanged |= genericBackpatched;
                block.Instructions[instructionIndex] = rewrittenGenericCall;
                UpdateLocalCallBindings(rewrittenGenericCall, localCallBindings);
                localTypesChanged |= RefineLocalTypesFromInstruction(function, rewrittenGenericCall, localTypes);
                continue;
            }

            if (!TryGetOrCreateSpecialization(template, signature, workingFunctions, queue, out var specializedFunction))
            {
                var rewrittenGenericCall = RewriteCallArgumentFunctionValues(
                    block,
                    instructionIndex,
                    resolvedCall,
                    signature.ParameterTypes,
                    localCallBindings,
                    localTypes,
                    workingFunctions,
                    queue,
                    out var genericBackpatched);
                localTypesChanged |= genericBackpatched;
                block.Instructions[instructionIndex] = rewrittenGenericCall;
                UpdateLocalCallBindings(rewrittenGenericCall, localCallBindings);
                localTypesChanged |= RefineLocalTypesFromInstruction(function, rewrittenGenericCall, localTypes);
                continue;
            }

            if (sourceFunctionRef.SymbolId.Equals(specializedFunction.SymbolId) &&
                string.Equals(sourceFunctionRef.Name, specializedFunction.Name, StringComparison.Ordinal) &&
                sourceFunctionRef.TypeId.Equals(signature.ReturnType))
            {
                block.Instructions[instructionIndex] = resolvedCall;
                UpdateLocalCallBindings(resolvedCall, localCallBindings);
                localTypesChanged |= RefineLocalTypesFromInstruction(function, resolvedCall, localTypes);
                continue;
            }

            var rewrittenFunctionRef = RewriteFunctionReference(
                sourceFunctionRef,
                specializedFunction,
                specializedFunction.ReturnType);

            instruction = RewriteCallArgumentFunctionValues(
                block,
                instructionIndex,
                resolvedCall with { Function = rewrittenFunctionRef },
                signature.ParameterTypes,
                localCallBindings,
                localTypes,
                workingFunctions,
                queue,
                out var specializedBackpatched);
            localTypesChanged |= specializedBackpatched;
            block.Instructions[instructionIndex] = instruction;
            _stats.TemplateCallRewrites++;
            UpdateLocalCallBindings(instruction, localCallBindings);
            localTypesChanged |= RefineLocalTypesFromInstruction(function, instruction, localTypes);
        }

        PruneDeadPartialTemplateCalls(function, block);
        return localTypesChanged;
    }

    private bool TryRewriteMonomorphicRecursiveCall(
        MirFunc containingFunction,
        MirCall call,
        MirFunctionRef sourceFunctionRef,
        TemplateInfo template,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out MirCall rewrittenCall)
    {
        rewrittenCall = null!;
        if (!IsGeneratedSpecialization(containingFunction.Name) ||
            !IsSpecializationOfTemplate(containingFunction, template.TemplateSource))
        {
            return false;
        }

        var parameterTypes = containingFunction.Locals
            .Where(static local => local.IsParameter)
            .Select(static local => local.TypeId)
            .ToArray();
        if (parameterTypes.Length != call.Arguments.Count)
        {
            return false;
        }

        for (var index = 0; index < parameterTypes.Length; index++)
        {
            var argumentType = ResolveOperandType(call.Arguments[index], localTypes);
            if (!argumentType.IsValid ||
                ContainsOpenTypeVariable(argumentType) ||
                argumentType != parameterTypes[index])
            {
                return false;
            }
        }

        rewrittenCall = call with
        {
            Function = RewriteFunctionReference(
                sourceFunctionRef,
                containingFunction,
                containingFunction.ReturnType)
        };
        return true;
    }

    private static bool IsSpecializationOfTemplate(MirFunc specialization, MirFunc template)
    {
        if (MirFunctionIdentity.TryGetStableKey(specialization.FunctionId, out var specializationKey) &&
            MirFunctionIdentity.TryGetStableKey(template.FunctionId, out var templateKey))
        {
            var specializationPrefix = $"{templateKey}\0specialization\0";
            return specializationKey.StartsWith(specializationPrefix, StringComparison.Ordinal);
        }

        return string.Equals(
            specialization.SourceName,
            string.IsNullOrWhiteSpace(template.SourceName) ? template.Name : template.SourceName,
            StringComparison.Ordinal) &&
               string.Equals(specialization.FunctionId.ModuleIdentityKey, template.FunctionId.ModuleIdentityKey, StringComparison.Ordinal) &&
               string.Equals(specialization.FunctionId.Module, template.FunctionId.Module, StringComparison.Ordinal);
    }
}
