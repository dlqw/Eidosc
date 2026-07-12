using Eidosc.Types;

namespace Eidosc.Mir;

public enum MirGenericLocalScope
{
    ParametersOnly,
    AllLocals
}

public static class MirGenericAnalysis
{
    public static bool IsGenericSignature(
        this MirFunc function,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        MirGenericLocalScope localScope = MirGenericLocalScope.ParametersOnly,
        Func<TypeId, bool>? isOpenUninternedType = null)
    {
        if (function.GenericParameterCount > 0)
        {
            return true;
        }

        if (!function.ReturnType.IsValid ||
            ContainsOpenTypeVariable(function.ReturnType, typeDescriptors, dynamicTypeKeys, isOpenUninternedType))
        {
            return true;
        }

        return function.Locals.Any(local =>
            ShouldInspectLocal(local, localScope) &&
            (!local.TypeId.IsValid ||
             ContainsOpenTypeVariable(local.TypeId, typeDescriptors, dynamicTypeKeys, isOpenUninternedType)));
    }

    public static bool ContainsOpenTypeVariable(
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        Func<TypeId, bool>? isOpenUninternedType = null)
    {
        return typeId.IsValid &&
               ContainsOpenTypeVariable(typeId, typeDescriptors, dynamicTypeKeys, [], isOpenUninternedType);
    }

    public static bool ContainsOpenConstructorVariable(
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys)
    {
        return typeId.IsValid &&
               ContainsOpenConstructorVariable(typeId, typeDescriptors, dynamicTypeKeys, []);
    }

    public static void CollectOpenTypeVariables(
        TypeId typeId,
        ISet<int> openTypeVariables,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        Func<TypeId, bool>? isOpenUninternedType = null)
    {
        CollectOpenTypeVariables(typeId, openTypeVariables, typeDescriptors, dynamicTypeKeys, [], isOpenUninternedType);
    }

    private static bool ShouldInspectLocal(MirLocal local, MirGenericLocalScope localScope)
    {
        return localScope == MirGenericLocalScope.AllLocals || local.IsParameter;
    }

    private static bool ContainsOpenTypeVariable(
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        HashSet<int> visitedTypeIds,
        Func<TypeId, bool>? isOpenUninternedType)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (!visitedTypeIds.Add(typeId.Value))
        {
            return false;
        }

        if (TryGetDescriptor(typeId, typeDescriptors, dynamicTypeKeys, out var descriptor))
        {
            return ContainsOpenTypeVariable(descriptor, typeDescriptors, dynamicTypeKeys, visitedTypeIds, isOpenUninternedType);
        }

        return isOpenUninternedType?.Invoke(typeId) ?? false;
    }

    private static bool ContainsOpenTypeVariable(
        TypeDescriptor descriptor,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        HashSet<int> visitedTypeIds,
        Func<TypeId, bool>? isOpenUninternedType)
    {
        return descriptor switch
        {
            TypeDescriptor.TypeVar => true,
            TypeDescriptor.Function function =>
                function.ParamTypes.Any(parameterType => ContainsOpenTypeVariable(
                    parameterType,
                    typeDescriptors,
                    dynamicTypeKeys,
                    visitedTypeIds,
                    isOpenUninternedType)) ||
                ContainsOpenTypeVariable(function.ReturnType, typeDescriptors, dynamicTypeKeys, visitedTypeIds, isOpenUninternedType),
            TypeDescriptor.Tuple tuple =>
                tuple.FieldTypes.Any(fieldType => ContainsOpenTypeVariable(
                    fieldType,
                    typeDescriptors,
                    dynamicTypeKeys,
                    visitedTypeIds,
                    isOpenUninternedType)),
            TypeDescriptor.TyCon tyCon =>
                tyCon.Constructor.Kind == TypeConstructorKeyKind.Variable ||
                tyCon.TypeArgs.Any(typeArgument => ContainsOpenTypeVariable(
                    typeArgument,
                    typeDescriptors,
                    dynamicTypeKeys,
                    visitedTypeIds,
                    isOpenUninternedType)),
            TypeDescriptor.Ref reference =>
                ContainsOpenTypeVariable(reference.Inner, typeDescriptors, dynamicTypeKeys, visitedTypeIds, isOpenUninternedType),
            TypeDescriptor.MutRef reference =>
                ContainsOpenTypeVariable(reference.Inner, typeDescriptors, dynamicTypeKeys, visitedTypeIds, isOpenUninternedType),
            TypeDescriptor.Shared shared =>
                ContainsOpenTypeVariable(shared.Inner, typeDescriptors, dynamicTypeKeys, visitedTypeIds, isOpenUninternedType),
            _ => false
        };
    }

    private static bool ContainsOpenConstructorVariable(
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        HashSet<int> visitedTypeIds)
    {
        if (!typeId.IsValid || !visitedTypeIds.Add(typeId.Value))
        {
            return false;
        }

        return TryGetDescriptor(typeId, typeDescriptors, dynamicTypeKeys, out var descriptor) &&
               ContainsOpenConstructorVariable(descriptor, typeDescriptors, dynamicTypeKeys, visitedTypeIds);
    }

    private static bool ContainsOpenConstructorVariable(
        TypeDescriptor descriptor,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        HashSet<int> visitedTypeIds)
    {
        return descriptor switch
        {
            TypeDescriptor.Function function =>
                function.ParamTypes.Any(parameterType => ContainsOpenConstructorVariable(
                    parameterType,
                    typeDescriptors,
                    dynamicTypeKeys,
                    visitedTypeIds)) ||
                ContainsOpenConstructorVariable(function.ReturnType, typeDescriptors, dynamicTypeKeys, visitedTypeIds),
            TypeDescriptor.Tuple tuple =>
                tuple.FieldTypes.Any(fieldType => ContainsOpenConstructorVariable(
                    fieldType,
                    typeDescriptors,
                    dynamicTypeKeys,
                    visitedTypeIds)),
            TypeDescriptor.TyCon tyCon =>
                tyCon.Constructor.Kind == TypeConstructorKeyKind.Variable ||
                tyCon.TypeArgs.Any(typeArgument => ContainsOpenConstructorVariable(
                    typeArgument,
                    typeDescriptors,
                    dynamicTypeKeys,
                    visitedTypeIds)),
            TypeDescriptor.Ref reference =>
                ContainsOpenConstructorVariable(reference.Inner, typeDescriptors, dynamicTypeKeys, visitedTypeIds),
            TypeDescriptor.MutRef reference =>
                ContainsOpenConstructorVariable(reference.Inner, typeDescriptors, dynamicTypeKeys, visitedTypeIds),
            TypeDescriptor.Shared shared =>
                ContainsOpenConstructorVariable(shared.Inner, typeDescriptors, dynamicTypeKeys, visitedTypeIds),
            _ => false
        };
    }

    private static void CollectOpenTypeVariables(
        TypeId typeId,
        ISet<int> openTypeVariables,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        HashSet<int> visitedTypeIds,
        Func<TypeId, bool>? isOpenUninternedType)
    {
        if (!typeId.IsValid || !visitedTypeIds.Add(typeId.Value))
        {
            return;
        }

        if (!TryGetDescriptor(typeId, typeDescriptors, dynamicTypeKeys, out var descriptor))
        {
            if (isOpenUninternedType?.Invoke(typeId) == true)
            {
                openTypeVariables.Add(typeId.Value);
            }
            return;
        }

        switch (descriptor)
        {
            case TypeDescriptor.TypeVar:
                openTypeVariables.Add(typeId.Value);
                break;
            case TypeDescriptor.Function function:
                foreach (var parameterType in function.ParamTypes)
                {
                    CollectOpenTypeVariables(
                        parameterType,
                        openTypeVariables,
                        typeDescriptors,
                        dynamicTypeKeys,
                        visitedTypeIds,
                        isOpenUninternedType);
                }
                CollectOpenTypeVariables(
                    function.ReturnType,
                    openTypeVariables,
                    typeDescriptors,
                    dynamicTypeKeys,
                    visitedTypeIds,
                    isOpenUninternedType);
                break;
            case TypeDescriptor.Tuple tuple:
                foreach (var fieldType in tuple.FieldTypes)
                {
                    CollectOpenTypeVariables(
                        fieldType,
                        openTypeVariables,
                        typeDescriptors,
                        dynamicTypeKeys,
                        visitedTypeIds,
                        isOpenUninternedType);
                }
                break;
            case TypeDescriptor.TyCon tyCon:
                if (tyCon.Constructor.Kind == TypeConstructorKeyKind.Variable)
                {
                    openTypeVariables.Add(typeId.Value);
                }
                foreach (var typeArgument in tyCon.TypeArgs)
                {
                    CollectOpenTypeVariables(
                        typeArgument,
                        openTypeVariables,
                        typeDescriptors,
                        dynamicTypeKeys,
                        visitedTypeIds,
                        isOpenUninternedType);
                }
                break;
            case TypeDescriptor.Ref reference:
                CollectOpenTypeVariables(reference.Inner, openTypeVariables, typeDescriptors, dynamicTypeKeys, visitedTypeIds, isOpenUninternedType);
                break;
            case TypeDescriptor.MutRef reference:
                CollectOpenTypeVariables(reference.Inner, openTypeVariables, typeDescriptors, dynamicTypeKeys, visitedTypeIds, isOpenUninternedType);
                break;
            case TypeDescriptor.Shared shared:
                CollectOpenTypeVariables(shared.Inner, openTypeVariables, typeDescriptors, dynamicTypeKeys, visitedTypeIds, isOpenUninternedType);
                break;
        }
    }

    private static bool TryGetDescriptor(
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys,
        out TypeDescriptor descriptor)
    {
        if (typeDescriptors.TryGetValue(typeId.Value, out descriptor!))
        {
            return true;
        }

        if (dynamicTypeKeys.TryGetValue(typeId.Value, out var typeKey) &&
            TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor!))
        {
            return true;
        }

        descriptor = null!;
        return false;
    }
}
