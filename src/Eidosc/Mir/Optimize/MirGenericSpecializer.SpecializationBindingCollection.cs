using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private SpecializationBindings CollectTypeBindings(MirFunc template, SpecializationSignature signature)
    {
        return new SpecializationBindingCollectorService(this).Collect(template, signature);
    }

    private SpecializationBindings CollectTypeBindings(TemplateInfo template, SpecializationSignature signature)
    {
        var key = CreateSpecializationCacheKey(template, signature);
        if (_typeBindingsByTemplateAndSignature.TryGetValue(key, out var cached))
        {
            _stats.TypeBindingCacheHits++;
            return cached;
        }

        _stats.TypeBindingCacheMisses++;
        var bindings = CollectTypeBindings(template.TemplateSource, signature);
        _typeBindingsByTemplateAndSignature[key] = bindings;
        return bindings;
    }

    private void CollectTypeBindings(TypeId templateType, TypeId concreteType, SpecializationBindings bindings)
    {
        TryCollectTypeBindings(templateType, concreteType, bindings);
    }

    private bool TryCollectTypeBindings(TypeId templateType, TypeId concreteType, SpecializationBindings bindings)
    {
        if (!templateType.IsValid || !concreteType.IsValid || templateType.Equals(concreteType))
        {
            return true;
        }

        if (!TryGetTypeDescriptor(templateType, out var templateDescriptor))
        {
            if (BaseTypes.IsBuiltIn(templateType))
            {
                return templateType.Equals(concreteType);
            }

            return !BaseTypes.IsBuiltIn(templateType) &&
                   IsOpenInferenceTypeVariable(templateType)
                ? TryBindTypeVariable(templateType, concreteType, bindings)
                : true;
        }

        if (templateDescriptor is TypeDescriptor.TypeVar)
        {
            return TryBindTypeVariable(templateType, concreteType, bindings);
        }

        if (!TryGetTypeDescriptor(concreteType, out var concreteDescriptor))
        {
            return templateDescriptor switch
            {
                TypeDescriptor.Ref reference => TryCollectTypeBindings(reference.Inner, concreteType, bindings),
                TypeDescriptor.MutRef reference => TryCollectTypeBindings(reference.Inner, concreteType, bindings),
                _ => false
            };
        }

        if (templateDescriptor is TypeDescriptor.Ref templateReference && concreteDescriptor is not TypeDescriptor.Ref)
        {
            return TryCollectTypeBindings(templateReference.Inner, concreteType, bindings);
        }

        if (templateDescriptor is TypeDescriptor.MutRef templateMutableReference && concreteDescriptor is not TypeDescriptor.MutRef)
        {
            return TryCollectTypeBindings(templateMutableReference.Inner, concreteType, bindings);
        }

        return TryCollectTypeBindings(templateDescriptor, concreteDescriptor, bindings);
    }

    private bool TryCollectTypeBindings(
        TypeDescriptor templateDescriptor,
        TypeDescriptor concreteDescriptor,
        SpecializationBindings bindings)
    {
        switch (templateDescriptor)
        {
            case TypeDescriptor.TypeVar:
                return false;
            case TypeDescriptor.Function templateFunction when concreteDescriptor is TypeDescriptor.Function concreteFunction:
                if (TryCollectFlattenedFunctionTypeBindings(templateFunction, concreteFunction, bindings))
                {
                    return true;
                }

                return templateFunction.ParamTypes.Length == concreteFunction.ParamTypes.Length &&
                       TryCollectTypeBindings(templateFunction.ParamTypes, concreteFunction.ParamTypes, bindings) &&
                       TryCollectTypeBindings(templateFunction.ReturnType, concreteFunction.ReturnType, bindings);
            case TypeDescriptor.Tuple templateTuple when concreteDescriptor is TypeDescriptor.Tuple concreteTuple:
                return templateTuple.FieldTypes.Length == concreteTuple.FieldTypes.Length &&
                       TryCollectTypeBindings(templateTuple.FieldTypes, concreteTuple.FieldTypes, bindings);
            case TypeDescriptor.TyCon templateTyCon when concreteDescriptor is TypeDescriptor.TyCon concreteTyCon:
                if (TryParseConstructorVarIndex(templateTyCon.Constructor, out var constructorVarIndex))
                {
                    return BindConstructorVariable(
                        constructorVarIndex,
                        templateTyCon.TypeArgs,
                        concreteTyCon.Constructor,
                        concreteTyCon.TypeArgs,
                        bindings);
                }

                return AreConstructorKeysEquivalent(templateTyCon.Constructor, concreteTyCon.Constructor) &&
                       templateTyCon.TypeArgs.Length == concreteTyCon.TypeArgs.Length &&
                       TryCollectTypeBindings(templateTyCon.TypeArgs, concreteTyCon.TypeArgs, bindings);
            case TypeDescriptor.Ref templateRef when concreteDescriptor is TypeDescriptor.Ref concreteRef:
                return TryCollectTypeBindings(templateRef.Inner, concreteRef.Inner, bindings);
            case TypeDescriptor.MutRef templateRef when concreteDescriptor is TypeDescriptor.MutRef concreteRef:
                return TryCollectTypeBindings(templateRef.Inner, concreteRef.Inner, bindings);
            case TypeDescriptor.Builtin templateBuiltin when concreteDescriptor is TypeDescriptor.Builtin concreteBuiltin:
                return templateBuiltin.TypeIdValue == concreteBuiltin.TypeIdValue;
            default:
                return false;
        }
    }

    private bool TryCollectTypeBindings(
        IReadOnlyList<TypeId> templateTypes,
        IReadOnlyList<TypeId> concreteTypes,
        SpecializationBindings bindings)
    {
        if (templateTypes.Count != concreteTypes.Count)
        {
            return false;
        }

        for (var i = 0; i < templateTypes.Count; i++)
        {
            if (!TryCollectTypeBindings(templateTypes[i], concreteTypes[i], bindings))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryCollectFlattenedFunctionTypeBindings(
        TypeDescriptor.Function templateFunction,
        TypeDescriptor.Function concreteFunction,
        SpecializationBindings bindings)
    {
        if (!TryResolveFlattenedFunctionDescriptor(templateFunction, out var templateParameters, out var templateReturn) ||
            !TryResolveFlattenedFunctionDescriptor(concreteFunction, out var concreteParameters, out var concreteReturn) ||
            templateParameters.Count == templateFunction.ParamTypes.Length && concreteParameters.Count == concreteFunction.ParamTypes.Length ||
            templateParameters.Count != concreteParameters.Count)
        {
            return false;
        }

        return TryCollectTypeBindings(templateParameters, concreteParameters, bindings) &&
               TryCollectTypeBindings(templateReturn, concreteReturn, bindings);
    }

    private bool TryBindTypeVariable(TypeId templateType, TypeId concreteType, SpecializationBindings bindings)
    {
        var bindingKeys = GetTypeVariableBindingKeys(templateType);
        var substitutedConcreteType = SubstituteTypeId(concreteType, bindings);
        if (substitutedConcreteType.IsValid &&
            !IsSameLogicalTypeVariable(templateType, substitutedConcreteType) &&
            !TypeContainsAnyTypeVariable(substitutedConcreteType, bindingKeys, bindings, []))
        {
            concreteType = substitutedConcreteType;
        }

        if (IsSameLogicalTypeVariable(templateType, concreteType))
        {
            return true;
        }

        if (TypeContainsAnyTypeVariable(concreteType, bindingKeys, bindings, []))
        {
            return false;
        }

        var preferredConcreteType = concreteType;
        foreach (var bindingKey in bindingKeys)
        {
            if (!bindings.TypeBindings.TryGetValue(bindingKey, out var existingBinding) ||
                existingBinding == concreteType)
            {
                continue;
            }

            if (!TryMergeExistingTypeVariableBinding(existingBinding, concreteType, bindings))
            {
                return false;
            }

            if (!ContainsOpenTypeVariable(existingBinding) &&
                ContainsOpenTypeVariable(preferredConcreteType))
            {
                preferredConcreteType = existingBinding;
            }
        }

        concreteType = preferredConcreteType;
        var resolvedConcreteType = SubstituteTypeId(concreteType, bindings);
        if (resolvedConcreteType.IsValid &&
            !TypeContainsAnyTypeVariable(resolvedConcreteType, bindingKeys, bindings, []))
        {
            concreteType = resolvedConcreteType;
        }

        foreach (var bindingKey in bindingKeys)
        {
            bindings.TypeBindings[bindingKey] = concreteType;
        }

        return true;
    }

    private bool IsSameLogicalTypeVariable(TypeId left, TypeId right)
    {
        if (left == right)
        {
            return true;
        }

        return TryGetTypeDescriptor(left, out var leftDescriptor) &&
               leftDescriptor is TypeDescriptor.TypeVar leftTypeVariable &&
               TryGetTypeDescriptor(right, out var rightDescriptor) &&
               rightDescriptor is TypeDescriptor.TypeVar rightTypeVariable &&
               leftTypeVariable.Index == rightTypeVariable.Index;
    }

    private bool TypeContainsAnyTypeVariable(
        TypeId typeId,
        IReadOnlyCollection<int> typeVariableKeys,
        SpecializationBindings bindings,
        HashSet<int> visited)
    {
        if (!typeId.IsValid || !visited.Add(typeId.Value))
        {
            return false;
        }

        if (typeVariableKeys.Contains(typeId.Value))
        {
            return true;
        }

        if (bindings.TypeBindings.TryGetValue(typeId.Value, out var boundType) &&
            TypeContainsAnyTypeVariable(boundType, typeVariableKeys, bindings, visited))
        {
            return true;
        }

        if (!TryGetTypeDescriptor(typeId, out var descriptor))
        {
            return false;
        }

        if (descriptor is TypeDescriptor.TypeVar typeVariable)
        {
            var indexKey = GetTypeVariableIndexBindingKey(typeVariable.Index);
            return typeVariableKeys.Contains(indexKey) ||
                   bindings.TypeBindings.TryGetValue(indexKey, out boundType) &&
                   TypeContainsAnyTypeVariable(boundType, typeVariableKeys, bindings, visited);
        }

        return descriptor switch
        {
            TypeDescriptor.Function function =>
                function.ParamTypes.Any(parameter => TypeContainsAnyTypeVariable(parameter, typeVariableKeys, bindings, visited)) ||
                TypeContainsAnyTypeVariable(function.ReturnType, typeVariableKeys, bindings, visited),
            TypeDescriptor.Tuple tuple =>
                tuple.FieldTypes.Any(field => TypeContainsAnyTypeVariable(field, typeVariableKeys, bindings, visited)),
            TypeDescriptor.TyCon tyCon =>
                tyCon.TypeArgs.Any(argument => TypeContainsAnyTypeVariable(argument, typeVariableKeys, bindings, visited)),
            TypeDescriptor.Ref reference =>
                TypeContainsAnyTypeVariable(reference.Inner, typeVariableKeys, bindings, visited),
            TypeDescriptor.MutRef reference =>
                TypeContainsAnyTypeVariable(reference.Inner, typeVariableKeys, bindings, visited),
            _ => false
        };
    }

    private bool TryMergeExistingTypeVariableBinding(
        TypeId existingBinding,
        TypeId concreteType,
        SpecializationBindings bindings)
    {
        var existingIsOpen = CanParticipateAsOpenInferenceType(existingBinding);
        var concreteIsOpen = CanParticipateAsOpenInferenceType(concreteType);
        if (existingIsOpen == concreteIsOpen)
        {
            return false;
        }

        var merged = CloneBindings(bindings);
        var mergedSuccessfully = existingIsOpen
            ? TryCollectTypeBindings(existingBinding, concreteType, merged)
            : TryCollectTypeBindings(concreteType, existingBinding, merged);
        if (mergedSuccessfully)
        {
            MergeBindings(bindings, merged);
            return true;
        }

        return false;
    }

    private List<int> GetTypeVariableBindingKeys(TypeId typeId)
    {
        if (!TryGetTypeDescriptor(typeId, out var descriptor) ||
            descriptor is not TypeDescriptor.TypeVar typeVariable ||
            typeVariable.Index == typeId.Value)
        {
            return [typeId.Value];
        }

        return [typeId.Value, GetTypeVariableIndexBindingKey(typeVariable.Index)];
    }

    private static int GetTypeVariableIndexBindingKey(int typeVariableIndex)
    {
        return -typeVariableIndex - 2;
    }

    private bool CanParticipateAsOpenInferenceType(TypeId typeId)
    {
        return ContainsOpenTypeVariable(typeId) || IsOpenInferenceTypeVariable(typeId);
    }

    private bool IsOpenInferenceTypeVariable(TypeId typeId)
    {
        if (!typeId.IsValid || BaseTypes.IsBuiltIn(typeId))
        {
            return false;
        }

        if (IsMirGenericTypeParameter(typeId))
        {
            return true;
        }

        if (TryGetTypeDescriptor(typeId, out var descriptor))
        {
            return descriptor is TypeDescriptor.TypeVar;
        }

        return !_typeConstructorInfoByTypeId.ContainsKey(typeId.Value);
    }

    private static SpecializationBindings CloneBindings(SpecializationBindings source)
    {
        return new SpecializationBindings(
            new Dictionary<int, TypeId>(source.TypeBindings),
            source.ConstructorBindings.ToDictionary(
                entry => entry.Key,
                entry => new ConstructorBinding(
                    entry.Value.Constructor,
                    entry.Value.Slots
                        .Select(slot => new ConstructorArgSlot(slot.PlaceholderIndex, slot.FixedTypeId))
                        .ToList())));
    }
}
