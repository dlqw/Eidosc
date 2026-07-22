using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

// Local type refinement, concretization, and inference
public sealed partial class MirGenericSpecializer
{
    private bool RefineLocalTypesFromInstruction(
        MirFunc function,
        MirInstruction instruction,
        Dictionary<LocalId, TypeId> localTypes)
    {
        _ = function;
        return TryConcretizeLocalTypesFromInstruction(instruction, localTypes);
    }


    private void ConcretizeFunctionLocalTypes(MirFunc function, Dictionary<LocalId, TypeId> localTypes)
    {
        _stats.LocalTypeConcretizeCalls++;
        var changed = true;
        var anyChanged = false;
        var refinementIterations = 0;
        while (changed && refinementIterations++ < 64)
        {
            changed = false;

            foreach (var block in function.BasicBlocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (TryConcretizeLocalTypesFromInstruction(instruction, localTypes))
                    {
                        changed = true;
                        anyChanged = true;
                    }
                }
            }
        }

        if (!anyChanged && !HasLocalTypeDifferences(function, localTypes))
        {
            return;
        }

        RefreshFunctionMetadata(function, localTypes);
    }

    private static bool HasLocalTypeDifferences(
        MirFunc function,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        for (var i = 0; i < function.Locals.Count; i++)
        {
            var local = function.Locals[i];
            if (localTypes.TryGetValue(local.Id, out var refinedType) &&
                refinedType.IsValid &&
                refinedType != local.TypeId)
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshFunctionLocalTypes(MirFunc function, IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        _stats.LocalRefreshes++;
        var useDirtyFilter = _dirtyLocalTypeIds.Count > 0;
        for (var i = 0; i < function.Locals.Count; i++)
        {
            var local = function.Locals[i];
            if (useDirtyFilter && !_dirtyLocalTypeIds.Contains(local.Id))
            {
                continue;
            }

            if (localTypes.TryGetValue(local.Id, out var refinedType) &&
                refinedType.IsValid &&
                refinedType != local.TypeId)
            {
                function.Locals[i] = new MirLocal
                {
                    Id = local.Id,
                    Name = local.Name,
                    TypeId = refinedType,
                    IsMutable = local.IsMutable,
                    IsParameter = local.IsParameter,
                    BindingMode = local.BindingMode,
                    Span = local.Span
                };
            }
        }
    }

    private bool TryConcretizeLocalTypesFromInstruction(MirInstruction instruction, Dictionary<LocalId, TypeId> localTypes)
    {
        return instruction switch
        {
            MirAssign assign => TryConcretizeLocalType(assign.Target, ResolveOperandType(assign.Source, localTypes), localTypes),
            MirCaseInject { Target: MirPlace target } injection =>
                TryConcretizeLocalType(target, injection.TargetTypeId, localTypes),
            MirLoad load => TryConcretizeLocalType(load.Target, ResolveOperandType(load.Source, localTypes), localTypes),
            MirStore store => TryConcretizeLocalType(store.Target, ResolveOperandType(store.Value, localTypes), localTypes),
            MirCopy copy => TryConcretizeLocalType(copy.Target, ResolvePlaceType(copy.Source, localTypes), localTypes) |
                            TryConcretizeLocalType(copy.Source, ResolvePlaceType(copy.Target, localTypes), localTypes) |
                            TryConcretizeFunctionValueLocalType(copy.Source, ResolvePlaceType(copy.Target, localTypes), localTypes),
            MirMove move => TryConcretizeLocalType(move.Target, ResolvePlaceType(move.Source, localTypes), localTypes) |
                            TryConcretizeLocalType(move.Source, ResolvePlaceType(move.Target, localTypes), localTypes) |
                            TryConcretizeFunctionValueLocalType(move.Source, ResolvePlaceType(move.Target, localTypes), localTypes),
            MirCall call => TryConcretizeLocalTypesFromCall(call, localTypes),
            _ => false
        };
    }

    private bool TryConcretizeLocalTypesFromCall(MirCall call, Dictionary<LocalId, TypeId> localTypes)
    {
        var changed = false;

        // Standard path: resolve result type from function type
        if (call.Target != null &&
            TryResolveCallResultType(call, localTypes, out var resultType) &&
            !ContainsOpenTypeVariable(resultType) &&
            TryAssignCallTargetType(call.Target, resultType, localTypes))
        {
            changed = true;
        }
        // Fallback: resolve result type by inferring function type variables from concrete arguments
        else if (call.Target != null &&
                 TryInferResultTypeFromArguments(call, localTypes, out resultType) &&
                 !ContainsOpenTypeVariable(resultType) &&
                 TryAssignCallTargetType(call.Target, resultType, localTypes))
        {
            changed = true;
        }

        if (TryInferCallableParameterTypesFromCall(call, localTypes, out var parameterTypes) ||
            TryResolveCallableParameterTypes(call.Function, localTypes, out parameterTypes))
        {
            for (var i = 0; i < call.Arguments.Count && i < parameterTypes.Count; i++)
            {
                if (call.Arguments[i] is MirPlace argumentPlace &&
                    !ContainsOpenTypeVariable(parameterTypes[i]) &&
                    TryConcretizeCallArgumentType(argumentPlace, parameterTypes[i], localTypes))
                {
                    changed = true;
                }
            }
        }

        return changed;
    }

    private bool TryInferCallableParameterTypesFromCall(
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out List<TypeId> parameterTypes)
    {
        parameterTypes = [];

        var functionTypeId = ResolveOperandType(call.Function, localTypes);
        if (!functionTypeId.IsValid ||
            !TryResolveFlattenedFunctionType(functionTypeId, out var declaredParameterTypes, out var declaredResultType))
        {
            return false;
        }

        var inferenceBindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());

        if (call.Target != null && declaredResultType.IsValid)
        {
            var targetType = ResolvePlaceType(call.Target, localTypes);
            if (targetType.IsValid &&
                !TryCollectTypeBindingsForInference(declaredResultType, targetType, inferenceBindings))
            {
                return false;
            }
        }

        for (var index = 0; index < call.Arguments.Count && index < declaredParameterTypes.Count; index++)
        {
            var argumentType = ResolveOperandType(call.Arguments[index], localTypes);
            if (!ShouldUseArgumentTypeForTemplateInference(
                    call.Arguments[index],
                    argumentType,
                    declaredParameterTypes[index]))
            {
                continue;
            }

            if (!TryCollectTypeBindingsForInference(declaredParameterTypes[index], argumentType, inferenceBindings))
            {
                if (!ShouldDeferFunctionValuePlaceInference(call.Arguments[index], declaredParameterTypes[index]))
                {
                    return false;
                }
            }
        }

        if (inferenceBindings.Count == 0)
        {
            return false;
        }

        foreach (var declaredParameterType in declaredParameterTypes)
        {
            var resolvedParameterType = SubstituteTypeId(declaredParameterType, inferenceBindings);
            if (!resolvedParameterType.IsValid)
            {
                return false;
            }

            parameterTypes.Add(resolvedParameterType);
        }

        return parameterTypes.Count > 0;
    }

    private bool ShouldDeferFunctionValuePlaceInference(MirOperand operand, TypeId declaredType)
    {
        return operand is MirPlace &&
               declaredType.IsValid &&
               ContainsFunctionType(declaredType);
    }

    private bool ShouldUseExpectedFunctionValueTypeForArgument(MirOperand operand, TypeId expectedType)
    {
        return operand is MirPlace or MirFunctionRef &&
               expectedType.IsValid &&
               !ContainsOpenTypeVariable(expectedType) &&
               ContainsFunctionType(expectedType);
    }

    /// <summary>
    /// When a function type has unresolved internal type variables (e.g., from trait methods),
    /// resolve the result type by matching declared parameter types against concrete argument types.
    /// </summary>
    private bool TryInferResultTypeFromArguments(
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out TypeId resultType)
    {
        resultType = TypeId.None;

        var functionTypeId = ResolveOperandType(call.Function, localTypes);
        if (!functionTypeId.IsValid ||
            !TryResolveFlattenedFunctionType(functionTypeId, out var declaredParamTypes, out var declaredResultType))
        {
            return false;
        }

        // No open variables in result → standard path handles it
        if (!ContainsOpenTypeVariable(declaredResultType))
        {
            return false;
        }

        // Build inference bindings by matching declared params against concrete args
        var inferenceBindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());

        for (var i = 0; i < call.Arguments.Count && i < declaredParamTypes.Count; i++)
        {
            var concreteArgType = ResolveOperandType(call.Arguments[i], localTypes);
            if (concreteArgType.IsValid &&
                !TryCollectTypeBindingsForInference(declaredParamTypes[i], concreteArgType, inferenceBindings))
            {
                return false;
            }
        }

        var resolvedResult = SubstituteTypeId(declaredResultType, inferenceBindings);
        if (!resolvedResult.IsValid || ContainsOpenTypeVariable(resolvedResult))
        {
            return false;
        }

        resultType = resolvedResult;
        return true;
    }

    private bool TryResolveCallableResultType(
        MirOperand functionOperand,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out TypeId resultType)
    {
        resultType = TypeId.None;
        var functionTypeId = ResolveOperandType(functionOperand, localTypes);
        if (TryResolveFlattenedFunctionType(functionTypeId, out _, out resultType) &&
            resultType.IsValid)
        {
            return true;
        }

        if (functionOperand is MirFunctionRef { TypeId: { IsValid: true } functionRefTypeId } &&
            !ContainsOpenTypeVariable(functionRefTypeId))
        {
            resultType = functionRefTypeId;
            return true;
        }

        return false;
    }

    private bool TryResolveCallResultType(
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out TypeId resultType)
    {
        resultType = TypeId.None;
        var functionTypeId = ResolveOperandType(call.Function, localTypes);
        if (!TryResolveFlattenedFunctionType(functionTypeId, out var parameterTypes, out var finalResultType) ||
            !finalResultType.IsValid)
        {
            return TryResolveCallableResultType(call.Function, localTypes, out resultType);
        }

        if (call.Arguments.Count < parameterTypes.Count)
        {
            return TryRebuildFunctionValueReturnType(
                TypeId.None,
                parameterTypes.Skip(call.Arguments.Count).ToList(),
                finalResultType,
                out resultType);
        }

        resultType = finalResultType;
        return true;
    }

    private bool TryResolveCallableParameterTypes(
        MirOperand functionOperand,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out List<TypeId> parameterTypes)
    {
        parameterTypes = [];
        var functionTypeId = ResolveOperandType(functionOperand, localTypes);
        return TryResolveFlattenedFunctionType(functionTypeId, out parameterTypes, out _);
    }

    private bool TryAssignCallTargetType(
        MirPlace targetPlace,
        TypeId candidateType,
        Dictionary<LocalId, TypeId> localTypes)
    {
        if (targetPlace.Kind != PlaceKind.Local ||
            !targetPlace.Local.IsValid ||
            !candidateType.IsValid ||
            ContainsOpenTypeVariable(candidateType))
        {
            return false;
        }

        if (localTypes.TryGetValue(targetPlace.Local, out var existingType) &&
            existingType.IsValid &&
            !ContainsOpenTypeVariable(existingType))
        {
            if (existingType == candidateType)
            {
                return false;
            }

            if (!CanRefineErasedFunctionCarrierType(existingType, candidateType))
            {
                return false;
            }
        }

        localTypes[targetPlace.Local] = candidateType;
        _dirtyLocalTypeIds.Add(targetPlace.Local);
        return true;
    }

    private bool TryConcretizeLocalType(
        MirPlace targetPlace,
        TypeId candidateType,
        Dictionary<LocalId, TypeId> localTypes)
    {
        if (targetPlace.Kind != PlaceKind.Local)
        {
            return TryConcretizeAggregateProjectionBaseType(targetPlace, candidateType, localTypes);
        }

        if (!targetPlace.Local.IsValid ||
            !candidateType.IsValid ||
            ContainsOpenTypeVariable(candidateType))
        {
            return false;
        }

        if (localTypes.TryGetValue(targetPlace.Local, out var existingType) &&
            existingType.IsValid &&
            !ContainsOpenTypeVariable(existingType))
        {
            if (existingType == candidateType)
            {
                return false;
            }

            return false;
        }

        localTypes[targetPlace.Local] = candidateType;
        _dirtyLocalTypeIds.Add(targetPlace.Local);
        return true;
    }

    private bool TryConcretizeAggregateProjectionBaseType(
        MirPlace targetPlace,
        TypeId candidateType,
        Dictionary<LocalId, TypeId> localTypes)
    {
        if (targetPlace.Kind != PlaceKind.Index ||
            targetPlace.Base is not { Kind: PlaceKind.Local } basePlace ||
            !basePlace.Local.IsValid ||
            !candidateType.IsValid ||
            ContainsOpenTypeVariable(candidateType) ||
            !TryResolveConstantIndex(targetPlace.Index, out var index) ||
            !localTypes.TryGetValue(basePlace.Local, out var baseType) ||
            !TryGetTypeDescriptor(baseType, out var descriptor) ||
            descriptor is not TypeDescriptor.Tuple tuple ||
            index < 0 ||
            index >= tuple.FieldTypes.Length)
        {
            return false;
        }

        var existingFieldType = tuple.FieldTypes[index];
        if (existingFieldType == candidateType ||
            !ContainsOpenTypeVariable(existingFieldType))
        {
            return false;
        }

        var fields = tuple.FieldTypes.ToArray();
        fields[index] = candidateType;
        localTypes[basePlace.Local] = GetOrCreateDynamicTypeId(new TypeDescriptor.Tuple(fields));
        _dirtyLocalTypeIds.Add(basePlace.Local);
        return true;
    }

    private bool TryConcretizeCallArgumentType(
        MirPlace argumentPlace,
        TypeId candidateType,
        Dictionary<LocalId, TypeId> localTypes)
    {
        if (TryConcretizeLocalType(argumentPlace, candidateType, localTypes))
        {
            return true;
        }

        if (TryConcretizeFunctionValueLocalType(argumentPlace, candidateType, localTypes))
        {
            return true;
        }

        return false;
    }

    private bool TryConcretizeFunctionValueLocalType(
        MirPlace targetPlace,
        TypeId candidateType,
        Dictionary<LocalId, TypeId> localTypes)
    {
        if (TryConcretizeLocalType(targetPlace, candidateType, localTypes))
        {
            return true;
        }

        if (targetPlace.Kind != PlaceKind.Local ||
            !targetPlace.Local.IsValid ||
            !candidateType.IsValid ||
            ContainsOpenTypeVariable(candidateType) ||
            !localTypes.TryGetValue(targetPlace.Local, out var existingType) ||
            !existingType.IsValid ||
            existingType == candidateType ||
            ContainsOpenTypeVariable(existingType) ||
            !ContainsFunctionType(candidateType) ||
            (!ContainsFunctionType(existingType) &&
             !CanRefineErasedFunctionCarrierType(existingType, candidateType)))
        {
            return false;
        }

        localTypes[targetPlace.Local] = candidateType;
        _dirtyLocalTypeIds.Add(targetPlace.Local);
        return true;
    }

    private bool CanRefineErasedFunctionCarrierType(TypeId existingType, TypeId candidateType)
    {
        return CanRefineErasedFunctionCarrierType(existingType, candidateType, []);
    }

    private bool CanRefineErasedFunctionCarrierType(
        TypeId existingType,
        TypeId candidateType,
        HashSet<(TypeId Existing, TypeId Candidate)> visited)
    {
        if (!existingType.IsValid ||
            !candidateType.IsValid ||
            existingType == candidateType ||
            !visited.Add((existingType, candidateType)) ||
            !TryGetTypeDescriptor(candidateType, out var candidateDescriptor))
        {
            return false;
        }

        if (existingType.Value == BaseTypes.RawPtrId &&
            candidateDescriptor is TypeDescriptor.Function)
        {
            return true;
        }

        if (!TryGetTypeDescriptor(existingType, out var existingDescriptor))
        {
            return false;
        }

        return existingDescriptor switch
        {
            TypeDescriptor.Tuple existingTuple when candidateDescriptor is TypeDescriptor.Tuple candidateTuple =>
                CanRefineErasedFunctionCarrierTypes(existingTuple.FieldTypes, candidateTuple.FieldTypes, visited),

            TypeDescriptor.TyCon existingTyCon when candidateDescriptor is TypeDescriptor.TyCon candidateTyCon =>
                AreConstructorKeysEquivalent(existingTyCon.Constructor, candidateTyCon.Constructor) &&
                CanRefineErasedFunctionCarrierTypes(existingTyCon.TypeArgs, candidateTyCon.TypeArgs, visited),

            TypeDescriptor.Ref existingRef when candidateDescriptor is TypeDescriptor.Ref candidateRef =>
                CanRefineErasedFunctionCarrierType(existingRef.Inner, candidateRef.Inner, visited),

            TypeDescriptor.MutRef existingRef when candidateDescriptor is TypeDescriptor.MutRef candidateRef =>
                CanRefineErasedFunctionCarrierType(existingRef.Inner, candidateRef.Inner, visited),

            _ => false
        };
    }

    private bool CanRefineErasedFunctionCarrierTypes(
        IReadOnlyList<TypeId> existingTypes,
        IReadOnlyList<TypeId> candidateTypes,
        HashSet<(TypeId Existing, TypeId Candidate)> visited)
    {
        if (existingTypes.Count != candidateTypes.Count)
        {
            return false;
        }

        var hasRefinement = false;
        for (var index = 0; index < existingTypes.Count; index++)
        {
            if (existingTypes[index] == candidateTypes[index])
            {
                continue;
            }

            if (!CanRefineErasedFunctionCarrierType(existingTypes[index], candidateTypes[index], visited))
            {
                return false;
            }

            hasRefinement = true;
        }

        return hasRefinement;
    }

    private bool ContainsFunctionType(TypeId typeId)
    {
        return ContainsFunctionType(typeId, []);
    }

    private bool ContainsFunctionType(TypeId typeId, HashSet<TypeId> visited)
    {
        if (!typeId.IsValid || !visited.Add(typeId) || !TryGetTypeDescriptor(typeId, out var descriptor))
        {
            return false;
        }

        return descriptor switch
        {
            TypeDescriptor.Function => true,
            TypeDescriptor.Tuple tuple => tuple.FieldTypes.Any(field => ContainsFunctionType(field, visited)),
            TypeDescriptor.TyCon tyCon => tyCon.TypeArgs.Any(typeArg => ContainsFunctionType(typeArg, visited)),
            TypeDescriptor.Ref reference => ContainsFunctionType(reference.Inner, visited),
            TypeDescriptor.MutRef reference => ContainsFunctionType(reference.Inner, visited),
            _ => false
        };
    }

    private bool IsFunctionType(TypeId typeId)
    {
        return typeId.IsValid &&
               TryGetTypeDescriptor(typeId, out var descriptor) &&
               descriptor is TypeDescriptor.Function;
    }

    private static bool IsGeneratedSpecialization(string functionName)
    {
        return !string.IsNullOrWhiteSpace(functionName) &&
               functionName.Contains("__spec_", StringComparison.Ordinal);
    }
}
