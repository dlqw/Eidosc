using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

// Function value rewriting, template call inference, operand helpers, key builders
public sealed partial class MirGenericSpecializer
{
    private bool TryRewriteFunctionValueInstruction(
        MirInstruction instruction,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue,
        out MirInstruction rewrittenInstruction)
    {
        rewrittenInstruction = instruction;

        switch (instruction)
        {
            case MirAssign assign when
                TryRewriteFunctionValueOperand(assign.Source, ResolvePlaceType(assign.Target, localTypes), workingFunctions, queue, out var rewrittenAssignSource):
                rewrittenInstruction = assign with { Source = rewrittenAssignSource };
                return true;

            case MirStore store when
                TryRewriteFunctionValueOperand(store.Value, ResolvePlaceType(store.Target, localTypes), workingFunctions, queue, out var rewrittenStoreValue):
                rewrittenInstruction = store with { Value = rewrittenStoreValue };
                return true;

            default:
                return false;
        }
    }

    private MirCall RewriteCallArgumentFunctionValues(
        MirCall call,
        IReadOnlyList<TypeId> parameterTypeIds,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        if (call.Arguments.Count == 0 || parameterTypeIds.Count == 0)
        {
            return call;
        }

        var rewrittenArguments = RewriteFunctionValueOperands(call.Arguments, parameterTypeIds, workingFunctions, queue);
        if (ReferenceEquals(rewrittenArguments, call.Arguments))
        {
            return call;
        }

        return call with { Arguments = rewrittenArguments };
    }

    private MirCall RewriteCallArgumentFunctionValues(
        MirBasicBlock block,
        int instructionIndex,
        MirCall call,
        IReadOnlyList<TypeId> parameterTypeIds,
        Dictionary<LocalId, LocalCallBinding> localCallBindings,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue,
        out bool partialFunctionValueBackpatched)
    {
        partialFunctionValueBackpatched = TryBackpatchLocalPartialFunctionValueArguments(
            block,
            instructionIndex,
            call.Arguments,
            parameterTypeIds,
            localCallBindings,
            localTypes,
            workingFunctions,
            queue);

        return RewriteCallArgumentFunctionValues(call, parameterTypeIds, workingFunctions, queue);
    }

    private bool TryBackpatchLocalPartialFunctionValueArguments(
        MirBasicBlock block,
        int instructionIndex,
        IReadOnlyList<MirOperand> operands,
        IReadOnlyList<TypeId> expectedTypeIds,
        Dictionary<LocalId, LocalCallBinding> localCallBindings,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        var changed = false;
        for (var index = 0; index < operands.Count && index < expectedTypeIds.Count; index++)
        {
            changed |= TryBackpatchLocalPartialFunctionValueArgument(
                block,
                instructionIndex,
                operands[index],
                expectedTypeIds[index],
                localCallBindings,
                localTypes,
                workingFunctions,
                queue);
        }

        return changed;
    }

    private bool TryBackpatchLocalPartialFunctionValueArgument(
        MirBasicBlock block,
        int instructionIndex,
        MirOperand operand,
        TypeId expectedFunctionTypeId,
        Dictionary<LocalId, LocalCallBinding> localCallBindings,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        if (operand is not MirPlace { Kind: PlaceKind.Local } localFunctionValue ||
            !expectedFunctionTypeId.IsValid ||
            !TryResolveFlattenedFunctionType(expectedFunctionTypeId, out _, out _) ||
            !localCallBindings.TryGetValue(localFunctionValue.Local, out var binding) ||
            !TryResolveTemplateKey(binding.FunctionRef, out var templateKey) ||
            !_templateRegistry.ByKeyDict.TryGetValue(templateKey, out var template) ||
            !TryResolvePartialBindingFunctionValueSignature(
                binding.FunctionRef,
                template.TemplateSource,
                binding.BoundArguments,
                expectedFunctionTypeId,
                localTypes,
                out var signature) ||
            !HasMeaningfulSpecializationSignature(template, signature) ||
            !IsMonomorphicSignature(signature) ||
            !TryGetOrCreateSpecialization(template, signature, workingFunctions, queue, out var specializedFunction))
        {
            return false;
        }

        var rewrittenFunctionRef = RewriteFunctionReference(
            binding.FunctionRef,
            specializedFunction,
            expectedFunctionTypeId);
        if (AreSameFunctionRef(binding.FunctionRef, rewrittenFunctionRef))
        {
            return false;
        }

        if (!TryBackpatchLocalCallBindingDefinition(
                block,
                instructionIndex,
                binding,
                rewrittenFunctionRef,
                localCallBindings))
        {
            return false;
        }

        UpdateMatchingLocalCallBindings(localCallBindings, binding, rewrittenFunctionRef);
        return true;
    }

    private static bool TryBackpatchLocalCallBindingDefinition(
        MirBasicBlock block,
        int instructionIndex,
        LocalCallBinding binding,
        MirFunctionRef rewrittenFunctionRef,
        IReadOnlyDictionary<LocalId, LocalCallBinding> localCallBindings)
    {
        for (var candidateIndex = instructionIndex - 1; candidateIndex >= 0; candidateIndex--)
        {
            var instruction = block.Instructions[candidateIndex];
            switch (instruction)
            {
                case MirCall { Target: { Kind: PlaceKind.Local } target, Function: MirFunctionRef functionRef } call
                    when IsDefinitionForLocalCallBinding(target.Local, functionRef, call.Arguments, binding, localCallBindings):
                    block.Instructions[candidateIndex] = call with { Function = rewrittenFunctionRef };
                    return true;

                case MirAssign { Target.Kind: PlaceKind.Local, Source: MirFunctionRef functionRef } assign
                    when IsDefinitionForLocalCallBinding(targetLocal: assign.Target.Local, functionRef, binding, localCallBindings):
                    block.Instructions[candidateIndex] = assign with { Source = rewrittenFunctionRef };
                    return true;

                case MirStore { Target.Kind: PlaceKind.Local, Value: MirFunctionRef functionRef } store
                    when IsDefinitionForLocalCallBinding(targetLocal: store.Target.Local, functionRef, binding, localCallBindings):
                    block.Instructions[candidateIndex] = store with { Value = rewrittenFunctionRef };
                    return true;
            }
        }

        return false;
    }

    private static bool IsDefinitionForLocalCallBinding(
        LocalId targetLocal,
        MirFunctionRef functionRef,
        IReadOnlyList<MirOperand> arguments,
        LocalCallBinding binding,
        IReadOnlyDictionary<LocalId, LocalCallBinding> localCallBindings)
    {
        return AreSameFunctionRef(functionRef, binding.FunctionRef) &&
               string.Equals(BuildBoundArgumentKey(arguments), binding.BoundArgumentKey, StringComparison.Ordinal) &&
               (!localCallBindings.TryGetValue(targetLocal, out var targetBinding) ||
                AreSameLocalCallBinding(targetBinding, binding));
    }

    private static bool IsDefinitionForLocalCallBinding(
        LocalId targetLocal,
        MirFunctionRef functionRef,
        LocalCallBinding binding,
        IReadOnlyDictionary<LocalId, LocalCallBinding> localCallBindings)
    {
        return AreSameFunctionRef(functionRef, binding.FunctionRef) &&
               localCallBindings.TryGetValue(targetLocal, out var targetBinding) &&
               AreSameLocalCallBinding(targetBinding, binding);
    }

    private static void UpdateMatchingLocalCallBindings(
        Dictionary<LocalId, LocalCallBinding> localCallBindings,
        LocalCallBinding binding,
        MirFunctionRef rewrittenFunctionRef)
    {
        List<LocalId>? localIdsToUpdate = null;
        foreach (var (localId, currentBinding) in localCallBindings)
        {
            if (AreSameLocalCallBinding(currentBinding, binding))
            {
                localIdsToUpdate ??= [];
                localIdsToUpdate.Add(localId);
            }
        }

        if (localIdsToUpdate == null)
        {
            return;
        }

        foreach (var localId in localIdsToUpdate)
        {
            var currentBinding = localCallBindings[localId];
            localCallBindings[localId] = CreateLocalCallBinding(
                rewrittenFunctionRef,
                currentBinding.BoundArguments,
                currentBinding.SupportsDirectApplication);
        }
    }

    private MirCall RewriteCallArgumentFunctionValuesFromTemplate(
        MirCall call,
        MirFunc template,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        if (!TryResolveExpectedTemplateArgumentTypes(call, template, localTypes, out var expectedTypeIds))
        {
            return call;
        }

        return RewriteCallArgumentFunctionValues(call, expectedTypeIds, workingFunctions, queue);
    }

    private bool TryResolveExpectedTemplateArgumentTypes(
        MirCall call,
        MirFunc template,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out List<TypeId> expectedTypeIds)
    {
        expectedTypeIds = [];
        var templateParameters = GetCachedTemplateParameters(template);
        if (call.Arguments.Count == 0 || templateParameters.Count < call.Arguments.Count)
        {
            return false;
        }

        if (!TryCreateTemplateCallInferenceBindings(
                call,
                templateParameters,
                template.ReturnType,
                localTypes,
                out var inferenceBindings))
        {
            return false;
        }
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            var parameterType = templateParameters[index].TypeId;
            if (!parameterType.IsValid)
            {
                return false;
            }

            var substitutedType = SubstituteTypeId(parameterType, inferenceBindings);
            expectedTypeIds.Add(substitutedType.IsValid ? substitutedType : parameterType);
        }

        return expectedTypeIds.Count == call.Arguments.Count;
    }

    private bool TryCreateTemplateCallInferenceBindings(
        MirCall call,
        IReadOnlyList<MirLocal> templateParameters,
        TypeId templateReturnType,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out SpecializationBindings inferenceBindings)
    {
        inferenceBindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());

        for (var index = 0; index < call.Arguments.Count && index < templateParameters.Count; index++)
        {
            var resolvedArgumentType = ResolveOperandType(call.Arguments[index], localTypes);
            if (!ShouldUseArgumentTypeForTemplateInference(
                    call.Arguments[index],
                    resolvedArgumentType,
                    templateParameters[index].TypeId))
            {
                continue;
            }

            if (!TryCollectTypeBindingsForInference(templateParameters[index].TypeId, resolvedArgumentType, inferenceBindings))
            {
                return false;
            }
        }

        return TryCollectTargetBindingsForTemplateCall(
            call,
            templateParameters,
            templateReturnType,
            localTypes,
            ref inferenceBindings);
    }

    private bool TryCollectTargetBindingsForTemplateCall(
        MirCall call,
        IReadOnlyList<MirLocal> templateParameters,
        TypeId templateReturnType,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        ref SpecializationBindings inferenceBindings)
    {
        var candidateBindings = CloneBindings(inferenceBindings);
        if (!TryCollectTargetBindingsForTemplateCallCore(
                call,
                templateParameters,
                templateReturnType,
                localTypes,
                candidateBindings))
        {
            return false;
        }

        inferenceBindings = candidateBindings;
        return true;
    }

    private bool TryCollectTargetBindingsForTemplateCallCore(
        MirCall call,
        IReadOnlyList<MirLocal> templateParameters,
        TypeId templateReturnType,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        SpecializationBindings inferenceBindings)
    {
        if (call.Target == null)
        {
            return true;
        }

        var targetType = ResolvePlaceType(call.Target, localTypes);
        if (!targetType.IsValid)
        {
            return true;
        }
        var effectiveTargetType = targetType;
        if (IsOpenInferenceTypeVariable(effectiveTargetType))
        {
            return true;
        }

        if (call.Arguments.Count == templateParameters.Count)
        {
            if (templateReturnType.IsValid)
            {
                var ok = TryCollectTypeBindingsForInference(templateReturnType, effectiveTargetType, inferenceBindings);
                if (!ok &&
                    !BaseTypes.IsBuiltIn(templateReturnType) &&
                    effectiveTargetType.IsValid &&
                    !ContainsOpenTypeVariable(effectiveTargetType))
                {
                    inferenceBindings.TypeBindings[templateReturnType.Value] = effectiveTargetType;
                    return true;
                }

                return ok;
            }

            return true;
        }

        if (call.Target is not { Kind: PlaceKind.Local } ||
            !TryResolveFlattenedFunctionType(targetType, out var remainingParameterTypes, out var remainingResultType) ||
            call.Arguments.Count + remainingParameterTypes.Count != templateParameters.Count)
        {
            return true;
        }

        for (var parameterIndex = call.Arguments.Count; parameterIndex < templateParameters.Count; parameterIndex++)
        {
            var remainingIndex = parameterIndex - call.Arguments.Count;
            if (remainingIndex >= remainingParameterTypes.Count)
            {
                return true;
            }

            if (!TryCollectTypeBindingsForInference(
                    templateParameters[parameterIndex].TypeId,
                    remainingParameterTypes[remainingIndex],
                    inferenceBindings))
            {
                return false;
            }
        }

        if (templateReturnType.IsValid && remainingResultType.IsValid)
        {
            return TryCollectTypeBindingsForInference(
                templateReturnType,
                remainingResultType,
                inferenceBindings);
        }

        return true;
    }

    private bool ShouldUseArgumentTypeForTemplateInference(MirOperand operand, TypeId resolvedArgumentType)
    {
        return ShouldUseArgumentTypeForTemplateInference(operand, resolvedArgumentType, TypeId.None);
    }

    private bool ShouldUseArgumentTypeForTemplateInference(
        MirOperand operand,
        TypeId resolvedArgumentType,
        TypeId declaredType)
    {
        if (!resolvedArgumentType.IsValid)
        {
            return false;
        }

        if (operand is MirPlace &&
            declaredType.IsValid &&
            ContainsOpenTypeVariable(declaredType) &&
            ContainsFunctionType(declaredType) &&
            !ContainsFunctionType(resolvedArgumentType))
        {
            return false;
        }

        if (operand is MirFunctionRef &&
            ContainsOpenTypeVariable(resolvedArgumentType))
        {
            return false;
        }

        if (!ContainsOpenTypeVariable(resolvedArgumentType))
        {
            return true;
        }

        if (!declaredType.IsValid ||
            !CanUsePartiallyOpenArgumentForInference(declaredType, resolvedArgumentType))
        {
            return false;
        }

        return true;
    }

    private bool CanUsePartiallyOpenArgumentForInference(TypeId declaredType, TypeId concreteType)
    {
        if (!declaredType.IsValid ||
            !concreteType.IsValid ||
            !TryGetTypeDescriptor(declaredType, out var declaredDescriptor) ||
            !TryGetTypeDescriptor(concreteType, out var concreteDescriptor))
        {
            return false;
        }

        return CanUsePartiallyOpenArgumentForInference(declaredDescriptor, concreteDescriptor);
    }

    private bool CanUsePartiallyOpenArgumentForInference(
        TypeDescriptor declaredDescriptor,
        TypeDescriptor concreteDescriptor)
    {
        switch (declaredDescriptor)
        {
            case TypeDescriptor.TyCon declaredTyCon when concreteDescriptor is TypeDescriptor.TyCon concreteTyCon:
                if (TryParseConstructorVarIndex(declaredTyCon.Constructor, out _))
                {
                    return true;
                }

                if (!AreConstructorKeysEquivalent(
                        declaredTyCon.Constructor,
                        concreteTyCon.Constructor) ||
                    declaredTyCon.TypeArgs.Length != concreteTyCon.TypeArgs.Length)
                {
                    return false;
                }

                for (var index = 0; index < declaredTyCon.TypeArgs.Length; index++)
                {
                    if (CanUsePartiallyOpenArgumentForInference(
                            declaredTyCon.TypeArgs[index],
                            concreteTyCon.TypeArgs[index]))
                    {
                        return true;
                    }
                }

                return false;
            case TypeDescriptor.Function declaredFunction when concreteDescriptor is TypeDescriptor.Function concreteFunction:
                if (declaredFunction.ParamTypes.Length != concreteFunction.ParamTypes.Length)
                {
                    return false;
                }

                for (var index = 0; index < declaredFunction.ParamTypes.Length; index++)
                {
                    if (CanUsePartiallyOpenArgumentForInference(
                            declaredFunction.ParamTypes[index],
                            concreteFunction.ParamTypes[index]))
                    {
                        return true;
                    }
                }

                return CanUsePartiallyOpenArgumentForInference(declaredFunction.ReturnType, concreteFunction.ReturnType);
            case TypeDescriptor.Tuple declaredTuple when concreteDescriptor is TypeDescriptor.Tuple concreteTuple:
                if (declaredTuple.FieldTypes.Length != concreteTuple.FieldTypes.Length)
                {
                    return false;
                }

                for (var index = 0; index < declaredTuple.FieldTypes.Length; index++)
                {
                    if (CanUsePartiallyOpenArgumentForInference(
                            declaredTuple.FieldTypes[index],
                            concreteTuple.FieldTypes[index]))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private bool TryBindConstructorBindingSignature(
        SpecializationSignature signature,
        out SpecializationSignature boundSignature)
    {
        boundSignature = signature;

        var bindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());
        var changed = false;
        foreach (var parameterType in signature.ParameterTypes)
        {
            changed |= TryBindOpenConstructorsFromConcreteCounterpart(
                parameterType,
                signature.ReturnType,
                bindings);
        }

        var returnType = SubstituteTypeId(signature.ReturnType, bindings);
        var parameterTypes = signature.ParameterTypes
            .Select(parameterType => SubstituteTypeId(parameterType, bindings))
            .ToList();

        if (!changed &&
            returnType == signature.ReturnType &&
            parameterTypes.SequenceEqual(signature.ParameterTypes))
        {
            return false;
        }

        boundSignature = new SpecializationSignature(returnType, parameterTypes);
        return IsMonomorphicSignature(boundSignature);
    }

    private bool TryBindOpenConstructorsFromConcreteCounterpart(
        TypeId openType,
        TypeId concreteType,
        SpecializationBindings bindings)
    {
        if (!openType.IsValid ||
            !concreteType.IsValid ||
            !TryGetTypeDescriptor(openType, out var openDescriptor) ||
            !TryGetTypeDescriptor(concreteType, out var concreteDescriptor))
        {
            return false;
        }

        return TryBindOpenConstructorsFromConcreteCounterpart(
            openDescriptor,
            concreteDescriptor,
            bindings);
    }

    private bool TryBindOpenConstructorsFromConcreteCounterpart(
        TypeDescriptor openDescriptor,
        TypeDescriptor concreteDescriptor,
        SpecializationBindings bindings)
    {
        switch (openDescriptor)
        {
            case TypeDescriptor.TyCon openTyCon when concreteDescriptor is TypeDescriptor.TyCon concreteTyCon:
                var changed = false;
                if (TryParseConstructorVarIndex(openTyCon.Constructor, out var constructorVarIndex) &&
                    !TryParseConstructorVarIndex(concreteTyCon.Constructor, out _) &&
                    BindConstructorVariable(
                        constructorVarIndex,
                        openTyCon.TypeArgs,
                        concreteTyCon.Constructor,
                        concreteTyCon.TypeArgs,
                        bindings))
                {
                    changed = true;
                }

                var pairedArgCount = Math.Min(openTyCon.TypeArgs.Length, concreteTyCon.TypeArgs.Length);
                for (var index = 0; index < pairedArgCount; index++)
                {
                    changed |= TryBindOpenConstructorsFromConcreteCounterpart(
                        openTyCon.TypeArgs[index],
                        concreteTyCon.TypeArgs[index],
                        bindings);
                }

                return changed;
            case TypeDescriptor.Function openFunction when concreteDescriptor is TypeDescriptor.Function concreteFunction:
                changed = false;
                var pairedParameterCount = Math.Min(openFunction.ParamTypes.Length, concreteFunction.ParamTypes.Length);
                for (var index = 0; index < pairedParameterCount; index++)
                {
                    changed |= TryBindOpenConstructorsFromConcreteCounterpart(
                        openFunction.ParamTypes[index],
                        concreteFunction.ParamTypes[index],
                        bindings);
                }

                return changed |
                       TryBindOpenConstructorsFromConcreteCounterpart(
                           openFunction.ReturnType,
                           concreteFunction.ReturnType,
                           bindings);
            case TypeDescriptor.Tuple openTuple when concreteDescriptor is TypeDescriptor.Tuple concreteTuple:
                changed = false;
                var pairedFieldCount = Math.Min(openTuple.FieldTypes.Length, concreteTuple.FieldTypes.Length);
                for (var index = 0; index < pairedFieldCount; index++)
                {
                    changed |= TryBindOpenConstructorsFromConcreteCounterpart(
                        openTuple.FieldTypes[index],
                        concreteTuple.FieldTypes[index],
                        bindings);
                }

                return changed;
            default:
                return false;
        }
    }

    private List<MirOperand> RewriteFunctionValueOperands(
        IReadOnlyList<MirOperand> operands,
        IReadOnlyList<TypeId> expectedTypeIds,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        for (var index = 0; index < operands.Count; index++)
        {
            var operand = operands[index];
            var expectedTypeId = index < expectedTypeIds.Count
                ? expectedTypeIds[index]
                : TypeId.None;
            if (!TryRewriteFunctionValueOperand(operand, expectedTypeId, workingFunctions, queue, out var rewrittenOperand))
            {
                continue;
            }

            var rewrittenOperands = new List<MirOperand>(operands.Count);
            for (var previous = 0; previous < index; previous++)
            {
                rewrittenOperands.Add(operands[previous]);
            }

            rewrittenOperands.Add(rewrittenOperand);
            for (var remaining = index + 1; remaining < operands.Count; remaining++)
            {
                operand = operands[remaining];
                expectedTypeId = remaining < expectedTypeIds.Count
                    ? expectedTypeIds[remaining]
                    : TypeId.None;
                rewrittenOperands.Add(TryRewriteFunctionValueOperand(
                    operand,
                    expectedTypeId,
                    workingFunctions,
                    queue,
                    out rewrittenOperand)
                        ? rewrittenOperand
                        : operand);
            }

            return rewrittenOperands;
        }

        return operands as List<MirOperand> ?? operands.ToList();
    }

    private bool TryRewriteFunctionValueOperand(
        MirOperand operand,
        TypeId expectedFunctionTypeId,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue,
        out MirOperand rewrittenOperand)
    {
        rewrittenOperand = operand;

        if (operand is not MirFunctionRef functionRef)
        {
            return false;
        }

        var resolvedFunctionTypeId = expectedFunctionTypeId.IsValid ? expectedFunctionTypeId : functionRef.TypeId;
        if (!resolvedFunctionTypeId.IsValid)
        {
            return false;
        }

        if (!TryResolveTemplateKey(functionRef, out var templateKey) ||
            !_templateRegistry.ByKeyDict.TryGetValue(templateKey, out var template))
        {
            return false;
        }

        if (!TryResolveFunctionValueSignature(template.TemplateSource, resolvedFunctionTypeId, out var signature))
        {
            return false;
        }

        if (!HasMeaningfulSpecializationSignature(template, signature) ||
            !IsMonomorphicSignature(signature))
        {
            return false;
        }

        if (!TryGetOrCreateSpecialization(template, signature, workingFunctions, queue, out var specializedFunction))
        {
            return false;
        }

        rewrittenOperand = RewriteFunctionReference(
            functionRef,
            specializedFunction,
            resolvedFunctionTypeId);
        return true;
    }


    private bool CanInlineBoundArguments(
        IReadOnlyList<MirOperand> boundArguments,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        for (var index = 0; index < boundArguments.Count; index++)
        {
            if (!IsInlineSafeBoundArgument(boundArguments[index], localTypes))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsInlineSafeBoundArgument(
        MirOperand operand,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        return operand switch
        {
            MirConstant => true,
            MirFunctionRef => true,
            MirPlace place => IsInlineSafePlace(place, localTypes),
            MirTemp temp => IsCopySafeType(temp.TypeId),
            _ => false
        };
    }

    private bool IsInlineSafePlace(
        MirPlace place,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        var placeType = place.TypeId.IsValid
            ? place.TypeId
            : (place.Kind == PlaceKind.Local && localTypes.TryGetValue(place.Local, out var localType)
                ? localType
                : TypeId.None);
        if (!IsCopySafeType(placeType))
        {
            return false;
        }

        return IsStablePlacePath(place, localTypes);
    }

    private bool IsStablePlacePath(
        MirPlace place,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        switch (place.Kind)
        {
            case PlaceKind.Local:
                return true;
            case PlaceKind.Field:
                return place.Base != null && IsStablePlacePath(place.Base, localTypes);
            case PlaceKind.Index:
                return place.Base != null &&
                       place.Index != null &&
                       IsStablePlacePath(place.Base, localTypes) &&
                       IsStableBoundIndexOperand(place.Index);
            case PlaceKind.Deref:
                return place.Base != null && IsStablePlacePath(place.Base, localTypes);
            default:
                return false;
        }
    }

    private static bool IsStableBoundIndexOperand(MirOperand operand)
    {
        // 对 dynamic-symbolic index 保守处理：仅允许常量下标进入提前重写，
        // 避免 partial-call 降级后出现“绑定值延后读取”语义漂移。
        return operand is MirConstant;
    }

    private bool IsCopySafeType(TypeId typeId)
    {
        return typeId.IsValid &&
               (_extraCopyLikeTypeIds.Contains(typeId.Value) ||
                CopyTypeSemantics.IsCopyType(typeId, _hasCopyImplResolver, _dynamicTypes.DescriptorByIdDict, _dynamicTypes.KeyByIdDict));
    }

    private static string BuildBoundArgumentKey(IReadOnlyList<MirOperand> boundArguments)
    {
        if (boundArguments.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(WellKnownStrings.Punctuation.Pipe, boundArguments.Select(BuildOperandKey));
    }

    private static string BuildOperandKey(MirOperand operand)
    {
        return operand switch
        {
            MirFunctionRef functionRef =>
                $"fn:{MirFunctionIdentity.GetStableKey(functionRef)}:{functionRef.SymbolKind}:{functionRef.TypeId.Value}:{functionRef.SignatureTypeId.Value}:{BuildTypeArgumentKey(functionRef.TypeArgumentIds)}:{functionRef.Name}",
            MirPlace place => BuildPlaceKey(place),
            MirConstant constant =>
                $"const:{constant.TypeId.Value}:{BuildConstantValueKey(constant.Value)}",
            MirTemp temp => $"tmp:{temp.Id.Value}:{temp.TypeId.Value}",
            _ => $"{operand.GetType().Name}:{operand.TypeId.Value}"
        };
    }

    private static string BuildTypeArgumentKey(IReadOnlyList<TypeId> typeArgumentIds)
    {
        return typeArgumentIds.Count == 0
            ? "[]"
            : $"[{string.Join(",", typeArgumentIds.Select(static typeId => typeId.Value.ToString()))}]";
    }

    private static string BuildPlaceKey(MirPlace place)
    {
        return place.Kind switch
        {
            PlaceKind.Local => $"local:{place.Local.Value}:{place.TypeId.Value}",
            PlaceKind.Field =>
                $"field:{place.TypeId.Value}:{BuildPlaceKey(place.Base!)}:{place.FieldName}",
            PlaceKind.Index =>
                $"index:{place.TypeId.Value}:{place.IndexAccessKind}:{BuildPlaceKey(place.Base!)}:{BuildOperandKey(place.Index!)}",
            PlaceKind.Deref =>
                $"deref:{place.TypeId.Value}:{BuildPlaceKey(place.Base!)}",
            _ => $"place:{place.Kind}:{place.TypeId.Value}"
        };
    }

    private static string BuildConstantValueKey(MirConstantValue value)
    {
        return value switch
        {
            MirConstantValue.IntValue intValue => $"int:{intValue.Value}",
            MirConstantValue.FloatValue floatValue => $"float:{floatValue.Value}",
            MirConstantValue.StringValue stringValue => $"string:{stringValue.Value}",
            MirConstantValue.CharValue charValue => $"char:{charValue.Value}",
            MirConstantValue.BoolValue boolValue => $"bool:{boolValue.Value}",
            MirConstantValue.UnitValue => "unit",
            _ => value.ToString() ?? value.GetType().Name
        };
    }
}
