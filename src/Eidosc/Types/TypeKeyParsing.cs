using Eidosc.Symbols;

namespace Eidosc.Types;

/// <summary>
/// 共享的类型键解析工具方法。
/// 合并 TypeLowering、MirBuilder.PatternLowering、MirGenericSpecializer.TypeParsing
/// 中的重复 TryParse* 实现。
/// </summary>
public static class TypeKeyParsing
{
    public static bool TryParseTypeVariableKey(string typeKey, out int typeVariableIndex)
    {
        typeVariableIndex = -1;
        return typeKey.StartsWith("TyVar_", StringComparison.Ordinal) &&
               int.TryParse(typeKey["TyVar_".Length..], out typeVariableIndex);
    }

    public static bool TryParseConstructorVariableDescriptor(string constructorDescriptor, out int constructorVariableIndex)
    {
        constructorVariableIndex = -1;
        if (!TypeConstructorKey.TryParse(constructorDescriptor, out var key) ||
            key.Kind != TypeConstructorKeyKind.Variable)
        {
            return false;
        }

        constructorVariableIndex = key.Id;
        return true;
    }

    public static bool TryParseConstructorKey(string constructorDescriptor, out TypeConstructorKey constructorKey)
    {
        return TypeConstructorKey.TryParse(constructorDescriptor, out constructorKey);
    }

    public static bool TryParseTypeDescriptor(string typeKey, out TypeDescriptor descriptor)
    {
        descriptor = null!;

        if (TryParseTypeVariableKey(typeKey, out var typeVariableIndex))
        {
            descriptor = new TypeDescriptor.TypeVar(typeVariableIndex);
            return true;
        }

        if (TryParseFunctionTypeKey(typeKey, out var parameterTypes, out var resultType, out var abilities))
        {
            descriptor = new TypeDescriptor.Function([.. parameterTypes], resultType, abilities);
            return true;
        }

        if (TryParseTupleTypeKey(typeKey, out var elementTypes))
        {
            descriptor = new TypeDescriptor.Tuple([.. elementTypes]);
            return true;
        }

        if (TryParseTyConTypeKey(typeKey, out var constructorKey, out var typeArguments))
        {
            descriptor = new TypeDescriptor.TyCon(constructorKey, [.. typeArguments]);
            return true;
        }

        if (TryParseWrappedIntTypeKey(typeKey, "Ref(", out var referenceInner))
        {
            descriptor = new TypeDescriptor.Ref(referenceInner);
            return true;
        }

        if (TryParseWrappedIntTypeKey(typeKey, "MRef(", out var mutableReferenceInner))
        {
            descriptor = new TypeDescriptor.MutRef(mutableReferenceInner);
            return true;
        }

        if (TryParseWrappedIntTypeKey(typeKey, "Shared(", out var sharedInner))
        {
            descriptor = new TypeDescriptor.Shared(sharedInner);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析带分隔符的 TypeId 列表（如 "Fun(T1,T2)->T3" 中的参数列表）。
    /// </summary>
    /// <param name="text">输入文本</param>
    /// <param name="prefix">前缀（如 "Fun("）</param>
    /// <param name="suffixDelimiter">后缀分隔符（如 ")->"）</param>
    /// <param name="parsedTypes">解析出的 TypeId 列表</param>
    /// <param name="remaining">剩余文本</param>
    public static bool TryParseDelimitedTypeIds(
        string text,
        string prefix,
        string suffixDelimiter,
        out List<TypeId> parsedTypes,
        out string remaining)
    {
        parsedTypes = [];
        remaining = string.Empty;

        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var start = prefix.Length;
        var suffixIndex = text.IndexOf(suffixDelimiter, start, StringComparison.Ordinal);
        if (suffixIndex < 0)
        {
            return false;
        }

        var body = text[start..suffixIndex];
        remaining = text[(suffixIndex + suffixDelimiter.Length)..];
        if (body.Length == 0)
        {
            return true;
        }

        foreach (var segment in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseTypeIdToken(segment, out var parsedTypeId))
            {
                parsedTypes = [];
                remaining = string.Empty;
                return false;
            }

            parsedTypes.Add(parsedTypeId);
        }

        return true;
    }

    /// <summary>
    /// 解析函数类型键（如 "Fun(T1,T2)->T3"）。
    /// </summary>
    public static bool TryParseFunctionTypeKey(
        string typeKey,
        out List<TypeId> parameterTypes,
        out TypeId resultType)
    {
        parameterTypes = [];
        resultType = default;

        if (!TryParseDelimitedTypeIds(typeKey, "Fun(", ")->", out parameterTypes, out var resultAndAbilities))
        {
            return false;
        }

        // 结果类型可能带有 effect 注解（如 "T5[io]"），只取 T5 部分
        // 使用逐字符扫描，找到第一个非 T+数字 的字符为止
        var tokenLength = 0;
        if (resultAndAbilities.StartsWith('T'))
            tokenLength = 1;
        while (tokenLength < resultAndAbilities.Length && char.IsDigit(resultAndAbilities[tokenLength]))
            tokenLength++;

        if (tokenLength == 0 ||
            (tokenLength == 1 && resultAndAbilities[0] == 'T') ||
            !TryParseTypeIdToken(resultAndAbilities[..tokenLength], out resultType))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 解析函数类型键（含 abilities 注解）。
    /// </summary>
    public static bool TryParseFunctionTypeKey(
        string typeKey,
        out List<TypeId> parameterTypes,
        out TypeId resultType,
        out string? abilities)
    {
        abilities = null;
        parameterTypes = [];
        resultType = default;

        if (!TryParseDelimitedTypeIds(typeKey, "Fun(", ")->", out parameterTypes, out var resultAndAbilities))
        {
            return false;
        }

        // 结果类型可能带有 effect 注解（如 "T5[io]"）
        // 使用逐字符扫描，找到第一个非 T+数字 的字符为止
        var tokenLength = 0;
        if (resultAndAbilities.StartsWith('T'))
            tokenLength = 1;
        while (tokenLength < resultAndAbilities.Length && char.IsDigit(resultAndAbilities[tokenLength]))
            tokenLength++;

        if (tokenLength == 0 ||
            (tokenLength == 1 && resultAndAbilities[0] == 'T') ||
            !TryParseTypeIdToken(resultAndAbilities[..tokenLength], out resultType))
        {
            return false;
        }

        if (tokenLength < resultAndAbilities.Length)
        {
            abilities = resultAndAbilities[tokenLength..];
        }

        return true;
    }

    /// <summary>
    /// 解析元组类型键（如 "Tuple(T1,T2,T3)"）。
    /// </summary>
    public static bool TryParseTupleTypeKey(string typeKey, out List<TypeId> elementTypes)
    {
        return TryParseDelimitedTypeIds(typeKey, "Tuple(", ")", out elementTypes, out _);
    }

    /// <summary>
    /// 解析 TyCon 类型键（如 "TyCon(sym:42;T1,T2)"）。
    /// </summary>
    public static bool TryParseTyConTypeKey(
        string typeKey,
        out TypeConstructorKey constructorKey,
        out List<TypeId> typeArguments)
    {
        constructorKey = default;
        typeArguments = [];

        if (!typeKey.StartsWith("TyCon(", StringComparison.Ordinal))
        {
            return false;
        }

        var semiIndex = typeKey.IndexOf(';', 6);
        if (semiIndex < 0)
        {
            return false;
        }

        if (!TypeConstructorKey.TryParse(typeKey[6..semiIndex], out constructorKey))
        {
            return false;
        }

        var closingParen = typeKey.LastIndexOf(')');
        if (closingParen <= semiIndex)
        {
            return false;
        }

        var argsBody = typeKey[(semiIndex + 1)..closingParen];
        if (argsBody.Length == 0)
        {
            return true;
        }

        foreach (var segment in argsBody.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseTypeIdToken(segment, out var parsedTypeId))
            {
                return false;
            }

            typeArguments.Add(parsedTypeId);
        }

        return true;
    }

    /// <summary>
    /// 解析 TypeId token（如 "T5" → TypeId(5)）。
    /// </summary>
    public static bool TryParseTypeIdToken(string token, out TypeId typeId)
    {
        typeId = default;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var span = token.Trim();
        if (span.Length == 0 || span[0] != 'T')
        {
            return false;
        }

        if (!int.TryParse(span[1..], out var typeIdValue))
        {
            return false;
        }

        typeId = new TypeId(typeIdValue);
        return typeId.IsValid;
    }

    private static bool TryParseWrappedIntTypeKey(string typeKey, string prefix, out TypeId innerType)
    {
        innerType = TypeId.None;

        if (!typeKey.StartsWith(prefix, StringComparison.Ordinal) ||
            !typeKey.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(typeKey[prefix.Length..^1], out var value) &&
               (innerType = new TypeId(value)).IsValid;
    }

}
