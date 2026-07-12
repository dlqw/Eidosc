using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private static bool TryParseConstructorVarIndex(TypeConstructorKey constructor, out int constructorVarIndex)
    {
        constructorVarIndex = -1;
        if (constructor.Kind != TypeConstructorKeyKind.Variable)
        {
            return false;
        }

        constructorVarIndex = constructor.Id;
        return true;
    }

    private TypeId ResolvePlaceType(MirPlace place, IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        if (place.Kind == PlaceKind.Local &&
            localTypes.TryGetValue(place.Local, out var localType) &&
            localType.IsValid)
        {
            return localType;
        }

        if (place.TypeId.IsValid &&
            !ContainsOpenTypeVariable(place.TypeId))
        {
            return place.TypeId;
        }

        var inferredType = ResolveDerivedPlaceType(place, localTypes);
        if (inferredType.IsValid)
        {
            return inferredType;
        }

        if (place.TypeId.IsValid)
        {
            return place.TypeId;
        }

        return TypeId.None;
    }

    private TypeId ResolveDerivedPlaceType(MirPlace place, IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        if (place.Base == null)
        {
            return TypeId.None;
        }

        var baseType = ResolvePlaceType(place.Base, localTypes);
        if (!baseType.IsValid ||
            !TryGetTypeDescriptor(baseType, out var baseTypeDescriptor))
        {
            return TypeId.None;
        }

        switch (place.Kind)
        {
            case PlaceKind.Index:
                if (baseTypeDescriptor is TypeDescriptor.Tuple tuple &&
                    TryResolveConstantIndex(place.Index, out var tupleIndex) &&
                    tupleIndex >= 0 &&
                    tupleIndex < tuple.FieldTypes.Length)
                {
                    return tuple.FieldTypes[tupleIndex];
                }

                if (place.IndexAccessKind == MirIndexAccessKind.RuntimeArray &&
                    baseTypeDescriptor is TypeDescriptor.TyCon arrayTyCon &&
                    arrayTyCon.TypeArgs.Length > 0)
                {
                    return arrayTyCon.TypeArgs[0];
                }

                if (baseTypeDescriptor is TypeDescriptor.TyCon aggregateTyCon &&
                    aggregateTyCon.TypeArgs.Length == 1)
                {
                    return aggregateTyCon.TypeArgs[0];
                }

                return TypeId.None;

            default:
                return TypeId.None;
        }
    }

    private static bool TryResolveConstantIndex(MirOperand? indexOperand, out int index)
    {
        index = -1;
        return indexOperand is MirConstant { Value: MirConstantValue.IntValue { Value: var value } } &&
               value >= 0 &&
               value <= int.MaxValue &&
               (index = (int)value) >= 0;
    }

    private TypeId ResolveReturnType(MirCall call, MirFunc template, IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        if (call.Target != null)
        {
            var targetType = ResolvePlaceType(call.Target, localTypes);
            if (targetType.IsValid)
            {
                // Even if target type is valid, try the function signature if it has open vars.
                if (!ContainsOpenTypeVariable(targetType))
                {
                    return targetType;
                }

                if (TryInferReturnTypeFromFunctionSignature(call, localTypes, out var inferredFromTarget))
                {
                    return inferredFromTarget;
                }

                return targetType;
            }
        }

        if (template.ReturnType.IsValid)
        {
            if (!ContainsOpenTypeVariable(template.ReturnType))
            {
                return template.ReturnType;
            }

            if (TryInferReturnTypeFromFunctionSignature(call, localTypes, out var inferredFromTemplate))
            {
                return inferredFromTemplate;
            }

            return template.ReturnType;
        }

        var returnType = call.Function.TypeId;

        // If the return type has open type variables, try to resolve them via
        // the function signature. This handles generic functions whose flattened
        // function type encoding carries the parameter/return type-variable relation.
        if (returnType.IsValid &&
            ContainsOpenTypeVariable(returnType) &&
            TryInferReturnTypeFromFunctionSignature(call, localTypes, out var inferredReturnType))
        {
            return inferredReturnType;
        }

        return returnType;
    }

    /// <summary>
    /// When a function call's return type contains unresolved type variables,
    /// resolve it by matching the function signature parameter types against
    /// concrete argument types, then substituting the declared return type.
    /// </summary>
    private bool TryInferReturnTypeFromFunctionSignature(
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out TypeId resultType)
    {
        resultType = TypeId.None;

        var functionTypeId = ResolveOperandType(call.Function, localTypes);
        if (TryResolveFlattenedFunctionType(functionTypeId, out var parameterTypes, out var returnType) &&
            TryInferReturnTypeFromSignature(call, localTypes, parameterTypes, returnType, out resultType))
        {
            return true;
        }

        return false;
    }

    private bool TryInferReturnTypeFromSignature(
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        IReadOnlyList<TypeId> parameterTypes,
        TypeId returnType,
        out TypeId resultType)
    {
        resultType = TypeId.None;

        if (!returnType.IsValid || parameterTypes.Count == 0)
        {
            return false;
        }

        if (!ContainsOpenTypeVariable(returnType))
        {
            resultType = returnType;
            return true;
        }

        var inferenceBindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());

        for (var i = 0; i < call.Arguments.Count && i < parameterTypes.Count; i++)
        {
            var concreteArgType = ResolveOperandType(call.Arguments[i], localTypes);
            if (concreteArgType.IsValid &&
                !TryCollectTypeBindingsForInference(parameterTypes[i], concreteArgType, inferenceBindings))
            {
                return false;
            }
        }

        if (inferenceBindings.TypeBindings.Count == 0 && inferenceBindings.ConstructorBindings.Count == 0)
        {
            return false;
        }

        var resolved = SubstituteTypeId(returnType, inferenceBindings);
        if (!resolved.IsValid || ContainsOpenTypeVariable(resolved))
        {
            return false;
        }

        resultType = resolved;
        return true;
    }

    private bool TryInferCallArgumentTypeFromSignature(
        MirCall call,
        int argumentIndex,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out TypeId resultType)
    {
        resultType = TypeId.None;

        if (argumentIndex < 0)
        {
            return false;
        }

        var functionTypeId = ResolveOperandType(call.Function, localTypes);
        if (!TryResolveFlattenedFunctionType(functionTypeId, out var parameterTypes, out var returnType) ||
            argumentIndex >= parameterTypes.Count)
        {
            return false;
        }

        var declaredArgumentType = parameterTypes[argumentIndex];
        if (!declaredArgumentType.IsValid)
        {
            return false;
        }

        if (!ContainsOpenTypeVariable(declaredArgumentType))
        {
            resultType = declaredArgumentType;
            return true;
        }

        var inferenceBindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());

        for (var i = 0; i < call.Arguments.Count && i < parameterTypes.Count; i++)
        {
            if (i == argumentIndex)
            {
                continue;
            }

            var concreteArgumentType = ResolveOperandType(call.Arguments[i], localTypes);
            if (!concreteArgumentType.IsValid)
            {
                continue;
            }

            var beforeCount = inferenceBindings.Count;
            var probeBindings = CloneBindings(inferenceBindings);
            if (!TryCollectTypeBindingsForInference(parameterTypes[i], concreteArgumentType, probeBindings))
            {
                continue;
            }

            if (ContainsOpenTypeVariable(concreteArgumentType) && probeBindings.Count == beforeCount)
            {
                continue;
            }

            MergeBindings(inferenceBindings, probeBindings);
        }

        if (call.Target != null &&
            returnType.IsValid)
        {
            var concreteReturnType = ResolvePlaceType(call.Target, localTypes);
            if (concreteReturnType.IsValid)
            {
                var probeBindings = CloneBindings(inferenceBindings);
                if (TryCollectTypeBindingsForInference(returnType, concreteReturnType, probeBindings))
                {
                    MergeBindings(inferenceBindings, probeBindings);
                }
            }
        }

        if (inferenceBindings.Count == 0)
        {
            if (TryInferCallArgumentTypeFromTemplateSignature(call, argumentIndex, localTypes, out resultType))
            {
                return true;
            }

            return false;
        }

        var resolved = SubstituteTypeId(declaredArgumentType, inferenceBindings);
        if (!resolved.IsValid || ContainsOpenTypeVariable(resolved))
        {
            return false;
        }

        resultType = resolved;
        return true;
    }

    private bool TryInferCallArgumentTypeFromTemplateSignature(
        MirCall call,
        int argumentIndex,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out TypeId resultType)
    {
        resultType = TypeId.None;

        if (call.Function is not MirFunctionRef { SymbolId.IsValid: true } functionRef ||
            !_functionBySymbol.TryGetValue(functionRef.SymbolId, out var template))
        {
            return false;
        }

        var templateParameters = GetCachedTemplateParameters(template);
        if (argumentIndex < 0 ||
            argumentIndex >= templateParameters.Count ||
            argumentIndex >= call.Arguments.Count)
        {
            return false;
        }

        var declaredArgumentType = templateParameters[argumentIndex].TypeId;
        if (!declaredArgumentType.IsValid)
        {
            return false;
        }

        var bindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());

        var functionTypeId = ResolveOperandType(call.Function, localTypes);
        if (TryResolveFlattenedFunctionType(functionTypeId, out var concreteParameterTypes, out var concreteFunctionReturnType))
        {
            for (var i = 0; i < concreteParameterTypes.Count && i < templateParameters.Count; i++)
            {
                if (i == argumentIndex)
                {
                    continue;
                }

                var declaredParameterType = templateParameters[i].TypeId;
                var concreteParameterType = concreteParameterTypes[i];
                if (!declaredParameterType.IsValid ||
                    !concreteParameterType.IsValid)
                {
                    continue;
                }

                var probe = CloneBindings(bindings);
                if (TryCollectTypeBindingsForInference(declaredParameterType, concreteParameterType, probe))
                {
                    MergeBindings(bindings, probe);
                }
            }

            if (template.ReturnType.IsValid &&
                concreteFunctionReturnType.IsValid)
            {
                var probe = CloneBindings(bindings);
                if (TryCollectTypeBindingsForInference(template.ReturnType, concreteFunctionReturnType, probe))
                {
                    MergeBindings(bindings, probe);
                }
            }
        }

        for (var i = 0; i < call.Arguments.Count && i < templateParameters.Count; i++)
        {
            if (i == argumentIndex)
            {
                continue;
            }

            var declaredParameterType = templateParameters[i].TypeId;
            var concreteArgumentType = ResolveOperandType(call.Arguments[i], localTypes);
            if (!declaredParameterType.IsValid ||
                !concreteArgumentType.IsValid)
            {
                continue;
            }

            var probe = CloneBindings(bindings);
            if (TryCollectTypeBindingsForInference(declaredParameterType, concreteArgumentType, probe))
            {
                MergeBindings(bindings, probe);
            }
        }

        if (call.Target != null &&
            template.ReturnType.IsValid)
        {
            var concreteReturnType = ResolvePlaceType(call.Target, localTypes);
            if (concreteReturnType.IsValid)
            {
                var probe = CloneBindings(bindings);
                if (TryCollectTypeBindingsForInference(template.ReturnType, concreteReturnType, probe))
                {
                    MergeBindings(bindings, probe);
                }
            }
        }

        if (bindings.Count == 0)
        {
            return false;
        }

        var resolved = SubstituteTypeId(declaredArgumentType, bindings);
        if (!resolved.IsValid || ContainsOpenTypeVariable(resolved))
        {
            return false;
        }

        resultType = resolved;
        return true;
    }

    private bool TryResolveFunctionRefType(MirFunctionRef functionRef, out TypeId functionTypeId)
    {
        functionTypeId = TypeId.None;

        if (functionRef.SignatureTypeId.IsValid)
        {
            functionTypeId = functionRef.SignatureTypeId;
            return true;
        }

        if (functionRef.SymbolId.IsValid)
        {
            if (_functionTypeIdBySymbol.TryGetValue(functionRef.SymbolId, out var bySymbol))
            {
                functionTypeId = bySymbol;
                return true;
            }

            return false;
        }

        if (!string.IsNullOrWhiteSpace(functionRef.Name) && _functionTypeIdByName.TryGetValue(functionRef.Name, out var byName))
        {
            functionTypeId = byName;
            return true;
        }

        return false;
    }

    private bool TryResolveConcreteFunctionRefType(MirFunctionRef functionRef, out TypeId functionTypeId)
    {
        functionTypeId = TypeId.None;

        if (functionRef.SignatureTypeId.IsValid &&
            !ContainsOpenTypeVariable(functionRef.SignatureTypeId) &&
            TryResolveFlattenedFunctionType(functionRef.SignatureTypeId, out _, out _))
        {
            functionTypeId = functionRef.SignatureTypeId;
            return true;
        }

        if (functionRef.TypeId.IsValid &&
            !ContainsOpenTypeVariable(functionRef.TypeId) &&
            TryResolveFlattenedFunctionType(functionRef.TypeId, out _, out _))
        {
            functionTypeId = functionRef.TypeId;
            return true;
        }

        return false;
    }

    private bool TryResolveFlattenedFunctionType(
        TypeId functionTypeId,
        out List<TypeId> parameterTypes,
        out TypeId resultType)
    {
        parameterTypes = [];
        resultType = TypeId.None;

        if (!functionTypeId.IsValid)
        {
            return false;
        }

        if (_flattenedFunctionTypesByTypeId.TryGetValue(functionTypeId.Value, out var cached))
        {
            if (!cached.IsFunction)
            {
                return false;
            }

            parameterTypes = cached.ParameterTypes;
            resultType = cached.ResultType;
            return true;
        }

        if (!TryGetTypeDescriptor(functionTypeId, out var descriptor) ||
            descriptor is not TypeDescriptor.Function functionDescriptor)
        {
            _flattenedFunctionTypesByTypeId[functionTypeId.Value] = new FlattenedFunctionType(false, [], TypeId.None);
            return false;
        }

        var visitedNestedFunctionTypeIds = new HashSet<int>();
        parameterTypes.AddRange(functionDescriptor.ParamTypes);
        resultType = functionDescriptor.ReturnType;

        while (resultType.IsValid &&
               visitedNestedFunctionTypeIds.Add(resultType.Value) &&
               TryGetTypeDescriptor(resultType, out var nestedDescriptor) &&
               nestedDescriptor is TypeDescriptor.Function nestedFunction)
        {
            parameterTypes.AddRange(nestedFunction.ParamTypes);
            resultType = nestedFunction.ReturnType;
        }

        _flattenedFunctionTypesByTypeId[functionTypeId.Value] = new FlattenedFunctionType(true, parameterTypes, resultType);
        return true;
    }

    private bool TryResolveFlattenedFunctionDescriptor(
        TypeDescriptor.Function functionDescriptor,
        out List<TypeId> parameterTypes,
        out TypeId resultType)
    {
        parameterTypes = [.. functionDescriptor.ParamTypes];
        resultType = functionDescriptor.ReturnType;

        var visitedNestedFunctionTypeIds = new HashSet<int>();
        while (resultType.IsValid)
        {
            if (!visitedNestedFunctionTypeIds.Add(resultType.Value) ||
                !TryGetTypeDescriptor(resultType, out var nestedDescriptor) ||
                nestedDescriptor is not TypeDescriptor.Function nestedFunction)
            {
                break;
            }

            parameterTypes.AddRange(nestedFunction.ParamTypes);
            resultType = nestedFunction.ReturnType;
        }

        return resultType.IsValid;
    }

    private TypeId ResolveOperandType(MirOperand operand, IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        if (operand is MirPlace place)
        {
            var resolvedPlaceType = ResolvePlaceType(place, localTypes);
            if (resolvedPlaceType.IsValid)
            {
                return resolvedPlaceType;
            }
        }

        if (operand.TypeId.IsValid)
        {
            if (operand is MirFunctionRef functionRef && TryResolveFunctionRefType(functionRef, out var concreteFunctionType))
            {
                return concreteFunctionType;
            }

            return operand.TypeId;
        }

        return operand switch
        {
            MirFunctionRef functionRef when TryResolveFunctionRefType(functionRef, out var functionType) => functionType,
            _ => TypeId.None
        };
    }

}
