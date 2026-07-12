using Eidosc.Symbols;
using System.Collections.Immutable;
using Eidosc.Semantic;

namespace Eidosc.Types;

/// <summary>
/// Copy 语义判定辅助。
/// </summary>
public static class CopyTypeSemantics
{
    /// <summary>
    /// 内建可复制类型（当前规则）：所有内建类型，除了 String。
    /// </summary>
    public static bool IsIntrinsicCopyType(TypeId typeId)
    {
        return typeId.IsValid &&
               BaseTypes.IsBuiltIn(typeId) &&
               typeId.Value != BaseTypes.StringId;
    }

    /// <summary>
    /// 统一 copy 判定入口：
    /// 1) 内建 intrinsic copy 直接放行；
    /// 2) 函数类型（闭包）始终为 copy — 运行时表示为指针；
    /// 3) 否则可选通过外部 resolver（例如 trait/impl）判定。
    /// </summary>
    public static bool IsCopyType(
        TypeId typeId,
        Func<TypeId, bool>? hasCopyImplResolver = null,
        IReadOnlyDictionary<int, string>? dynamicTypeKeys = null)
    {
        return IsCopyType(typeId, hasCopyImplResolver, null, dynamicTypeKeys);
    }

    /// <summary>
    /// 统一 copy 判定入口，优先使用结构化类型描述表。
    /// </summary>
    public static bool IsCopyType(
        TypeId typeId,
        Func<TypeId, bool>? hasCopyImplResolver,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        IReadOnlyDictionary<int, string>? dynamicTypeKeys = null)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (IsIntrinsicCopyType(typeId))
        {
            return true;
        }

        if (IsFunctionType(typeId, typeDescriptors, dynamicTypeKeys))
        {
            return true;
        }

        return hasCopyImplResolver?.Invoke(typeId) ?? false;
    }

    /// <summary>
    /// MIR 生成阶段的 copy 判定：额外将未解析的类型变量视为 Copy。
    /// 泛型模板中的参数类型尚未确定，MIR builder 应生成 MirCopy 而非 MirMove，
    /// 具体的 move 检查在特化版本中完成。
    /// </summary>
    public static bool IsCopyTypeForMirBuilding(
        TypeId typeId,
        Func<TypeId, bool>? hasCopyImplResolver = null,
        IReadOnlyDictionary<int, string>? dynamicTypeKeys = null)
    {
        return IsCopyTypeForMirBuilding(typeId, hasCopyImplResolver, null, dynamicTypeKeys);
    }

    /// <summary>
    /// MIR 生成阶段的 copy 判定，优先使用结构化类型描述表。
    /// </summary>
    public static bool IsCopyTypeForMirBuilding(
        TypeId typeId,
        Func<TypeId, bool>? hasCopyImplResolver,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        IReadOnlyDictionary<int, string>? dynamicTypeKeys = null)
    {
        if (IsCopyType(typeId, hasCopyImplResolver, typeDescriptors, dynamicTypeKeys))
        {
            return true;
        }

        return IsTypeVariable(typeId, typeDescriptors, dynamicTypeKeys);
    }

    /// <summary>
    /// 判断是否为函数/闭包类型。函数类型的 type key 以 "Fun(" 开头。
    /// </summary>
    public static bool IsFunctionType(TypeId typeId, IReadOnlyDictionary<int, string>? dynamicTypeKeys)
    {
        return IsFunctionType(typeId, null, dynamicTypeKeys);
    }

    /// <summary>
    /// 判断是否为函数/闭包类型，优先使用结构化类型描述表。
    /// </summary>
    public static bool IsFunctionType(
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        IReadOnlyDictionary<int, string>? dynamicTypeKeys = null)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (typeDescriptors != null &&
            typeDescriptors.TryGetValue(typeId.Value, out var descriptor))
        {
            return descriptor is TypeDescriptor.Function;
        }

        if (dynamicTypeKeys == null ||
            !dynamicTypeKeys.TryGetValue(typeId.Value, out var typeKey))
        {
            return false;
        }

        return TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor) &&
               descriptor is TypeDescriptor.Function;
    }

    /// <summary>
    /// 判断是否为未解析的类型变量（如 "TyVar_N"）。
    /// 在泛型模板中，类型参数尚未替换为具体类型，affine 检查应将它们视为 Copy，
    /// 因为真正的 move 检查会在具体特化版本中完成。
    /// </summary>
    public static bool IsTypeVariable(TypeId typeId, IReadOnlyDictionary<int, string>? dynamicTypeKeys)
    {
        return IsTypeVariable(typeId, null, dynamicTypeKeys);
    }

    /// <summary>
    /// 判断是否为未解析的类型变量，优先使用结构化类型描述表。
    /// </summary>
    public static bool IsTypeVariable(
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        IReadOnlyDictionary<int, string>? dynamicTypeKeys = null)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (typeDescriptors != null &&
            typeDescriptors.TryGetValue(typeId.Value, out var descriptor))
        {
            return descriptor is TypeDescriptor.TypeVar;
        }

        return dynamicTypeKeys != null &&
               dynamicTypeKeys.TryGetValue(typeId.Value, out var typeKey) &&
               TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor) &&
               descriptor is TypeDescriptor.TypeVar;
    }

    /// <summary>
    /// 基于符号表创建 copy-impl 判定器（优先 Copy trait，回退 Clone）。
    /// </summary>
    public static Func<TypeId, bool> CreateSymbolTableCopyResolver(
        SymbolTable symbolTable,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors = null)
    {
        ArgumentNullException.ThrowIfNull(symbolTable);

        var copyLikeTraits = ResolveCopyLikeTraits(symbolTable);
        if (copyLikeTraits.Count == 0)
        {
            return _ => false;
        }

        return typeId => typeId.IsValid &&
                          (HasCopyLikeTypeParameterConstraint(symbolTable, typeId, copyLikeTraits) ||
                           HasCopyLikeImpl(symbolTable, typeId, copyLikeTraits, typeDescriptors));
    }

    private static IReadOnlyList<SymbolId> ResolveCopyLikeTraits(SymbolTable symbolTable)
    {
        var traits = new List<SymbolId>(2);
        var seen = new HashSet<int>();

        AddIfPresent(LookupTrait(symbolTable, "Copy"));
        AddIfPresent(LookupTrait(symbolTable, BuiltinTraits.TraitNames.Clone));
        return traits;

        void AddIfPresent(SymbolId? traitId)
        {
            if (!traitId.HasValue || !seen.Add(traitId.Value.Value))
            {
                return;
            }

            traits.Add(traitId.Value);
        }
    }

    private static bool HasCopyLikeTypeParameterConstraint(
        SymbolTable symbolTable,
        TypeId typeId,
        IReadOnlyList<SymbolId> copyLikeTraits)
    {
        var typeParam = symbolTable.GetSymbol<TypeParamSymbol>(new SymbolId(typeId.Value));
        return typeParam != null &&
               typeParam.TraitConstraints.Any(copyLikeTraits.Contains);
    }

    private static bool HasCopyLikeImpl(
        SymbolTable symbolTable,
        TypeId typeId,
        IReadOnlyList<SymbolId> copyLikeTraits,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors)
    {
        if (!TryBuildCopyImplLookup(
                symbolTable,
                typeId,
                typeDescriptors,
                out var lookupTypeId,
                out var implementingTypeKey))
        {
            return false;
        }

        for (var i = 0; i < copyLikeTraits.Count; i++)
        {
            if (symbolTable.LookupImplForTraitByKeys(
                    lookupTypeId,
                    copyLikeTraits[i],
                    implementingTypeKey,
                    traitTypeArgKeys: null) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryBuildCopyImplLookup(
        SymbolTable symbolTable,
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        out TypeId lookupTypeId,
        out ImplTypeRefKey implementingTypeKey)
    {
        lookupTypeId = TypeId.None;
        implementingTypeKey = ImplTypeRefKey.Empty;

        if (!typeId.IsValid)
        {
            return false;
        }

        if (typeDescriptors != null &&
            typeDescriptors.TryGetValue(typeId.Value, out var descriptor))
        {
            implementingTypeKey = BuildCopyImplTypeRefKey(symbolTable, descriptor, typeDescriptors);
            lookupTypeId = ResolveCopyImplLookupTypeId(symbolTable, descriptor);
            return lookupTypeId.IsValid && !implementingTypeKey.IsEmpty;
        }

        if (TryResolveTypeConstructorSymbol(symbolTable, typeId, out var symbolId, out var declaredTypeId, out var name))
        {
            lookupTypeId = declaredTypeId;
            implementingTypeKey = new ImplTypeRefKey(symbolId, declaredTypeId, name, []);
            return true;
        }

        if (BaseTypes.IsBuiltIn(typeId))
        {
            lookupTypeId = typeId;
            implementingTypeKey = BuildBuiltinTypeRefKey(typeId);
            return !implementingTypeKey.IsEmpty;
        }

        return false;
    }

    private static TypeId ResolveCopyImplLookupTypeId(SymbolTable symbolTable, TypeDescriptor descriptor)
    {
        return descriptor switch
        {
            TypeDescriptor.TyCon tyCon => TryResolveConstructorIdentity(
                symbolTable,
                tyCon.Constructor,
                out _,
                out var constructorTypeId,
                out _)
                ? constructorTypeId
                : TypeId.None,
            TypeDescriptor.Builtin builtin => new TypeId(builtin.TypeIdValue),
            _ => TypeId.None
        };
    }

    private static ImplTypeRefKey BuildCopyImplTypeRefKey(
        SymbolTable symbolTable,
        TypeDescriptor descriptor,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors)
    {
        return descriptor switch
        {
            TypeDescriptor.Builtin builtin => BuildBuiltinTypeRefKey(new TypeId(builtin.TypeIdValue)),
            TypeDescriptor.TyCon tyCon => BuildTyConTypeRefKey(symbolTable, tyCon, typeDescriptors),
            _ => ImplTypeRefKey.Empty
        };
    }

    private static ImplTypeRefKey BuildTyConTypeRefKey(
        SymbolTable symbolTable,
        TypeDescriptor.TyCon tyCon,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors)
    {
        if (!TryResolveConstructorIdentity(
                symbolTable,
                tyCon.Constructor,
                out var symbolId,
                out var typeId,
                out var name))
        {
            return ImplTypeRefKey.Empty;
        }

        return new ImplTypeRefKey(
            symbolId,
            typeId,
            name,
            tyCon.TypeArgs
                .Select(typeArg => TryBuildCopyImplLookup(
                    symbolTable,
                    typeArg,
                    typeDescriptors,
                    out _,
                    out var typeArgKey)
                    ? typeArgKey
                    : ImplTypeRefKey.Empty)
                .Where(static key => !key.IsEmpty)
                .ToImmutableArray());
    }

    private static ImplTypeRefKey BuildBuiltinTypeRefKey(TypeId typeId)
    {
        var name = ImplLookupCanonicalizer.ResolveBuiltinCanonicalTypeName(typeId);
        return string.IsNullOrWhiteSpace(name)
            ? ImplTypeRefKey.Empty
            : new ImplTypeRefKey(SymbolId.None, typeId, name, []);
    }

    private static bool TryResolveConstructorIdentity(
        SymbolTable symbolTable,
        TypeConstructorKey constructor,
        out SymbolId symbolId,
        out TypeId typeId,
        out string name)
    {
        symbolId = SymbolId.None;
        typeId = TypeId.None;
        name = string.Empty;

        if (constructor.Kind == TypeConstructorKeyKind.Symbol &&
            symbolTable.GetSymbol(new SymbolId(constructor.Id)) is { TypeId.IsValid: true } symbol &&
            !string.IsNullOrWhiteSpace(symbol.Name))
        {
            symbolId = symbol.Id;
            typeId = symbol.TypeId;
            name = symbol.Name;
            return symbolId.IsValid && typeId.IsValid;
        }

        if (constructor.Kind is TypeConstructorKeyKind.TypeId or TypeConstructorKeyKind.Builtin)
        {
            return TryResolveTypeConstructorSymbol(symbolTable, new TypeId(constructor.Id), out symbolId, out typeId, out name);
        }

        return false;
    }

    private static bool TryResolveTypeConstructorSymbol(
        SymbolTable symbolTable,
        TypeId requestedTypeId,
        out SymbolId symbolId,
        out TypeId declaredTypeId,
        out string name)
    {
        symbolId = SymbolId.None;
        declaredTypeId = TypeId.None;
        name = string.Empty;

        var matches = symbolTable.Symbols.Values
            .Where(symbol => symbol is AdtSymbol or TraitSymbol or EffectSymbol)
            .Where(symbol => symbol.TypeId == requestedTypeId)
            .ToList();
        if (matches.Count != 1 ||
            !matches[0].Id.IsValid ||
            !matches[0].TypeId.IsValid ||
            string.IsNullOrWhiteSpace(matches[0].Name))
        {
            return false;
        }

        symbolId = matches[0].Id;
        declaredTypeId = matches[0].TypeId;
        name = matches[0].Name;
        return true;
    }

    private static SymbolId? LookupTrait(SymbolTable symbolTable, string traitName)
    {
        var traitId = symbolTable.LookupType(traitName);
        if (traitId == null || !traitId.Value.IsValid)
        {
            return null;
        }

        return symbolTable.GetSymbol<TraitSymbol>(traitId.Value) != null
            ? traitId.Value
            : null;
    }
}
