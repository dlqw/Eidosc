using System.Text;

namespace Eidosc.Bindgen;

public enum BindingTypeCategory
{
    Direct,
    RawPtr,
    StructByValue,
    EnumAsInt,
    Unsupported
}

public sealed record BindingTypeMapping(
    string EidosType,
    BindingTypeCategory Category,
    string? Note = null);

public sealed class BindingTypeMapper
{
    private const int MaxTypedefDepth = 8;

    /// <summary>
    /// Eidos 关键字表（docs/reference/grammar/Eidos-BNF.md keyword 产生式）。
    /// 生成标识符与之冲突时追加 <c>_</c> 后缀转义（C 符号名经 extern name 保持原样）。
    /// </summary>
    internal static readonly HashSet<string> EidosKeywords = new(StringComparer.Ordinal)
    {
        "module", "import", "let", "mut", "func", "effect", "effects", "need",
        "type", "trait", "instance", "export", "given", "comptime",
        "if", "else", "then", "decide", "loop", "while", "match", "when",
        "return", "break", "continue", "ref", "mref", "true", "false", "as"
    };

    internal static bool IsEidosKeyword(string name) => EidosKeywords.Contains(name);

    private readonly HashSet<string> _structNames;
    private readonly HashSet<string> _enumNames;
    private readonly IReadOnlyDictionary<string, CBindingTypedef> _typedefs;

    public BindingTypeMapper(CHeaderIr ir)
    {
        _structNames = ir.Structs.Select(static st => st.Name).ToHashSet(StringComparer.Ordinal);
        _enumNames = ir.Enums.Select(static en => en.Name).ToHashSet(StringComparer.Ordinal);
        _typedefs = (ir.TypedefsSafe ?? [])
            .ToDictionary(static t => t.Name, StringComparer.Ordinal);
    }

    public BindingTypeMapping Map(CBindingType type) => Map(type, 0);

    private BindingTypeMapping Map(CBindingType type, int depth)
    {
        if (depth > MaxTypedefDepth)
            return new("RawPtr", BindingTypeCategory.Unsupported, $"typedef chain too deep at: {type.Name}");

        if (type.Kind == CBindingTypeKind.Void)
            return new("Unit", BindingTypeCategory.Direct);

        if (type.Kind == CBindingTypeKind.FunctionPointer)
            return MapFunctionPointer(type);

        if (type.Kind is CBindingTypeKind.Array or CBindingTypeKind.Union)
            return new("RawPtr", BindingTypeCategory.Unsupported,
                type.Kind == CBindingTypeKind.Union ? "union" : "array");

        if (type.Kind == CBindingTypeKind.Pointer || type.PointerDepth > 0)
            return new("RawPtr", BindingTypeCategory.RawPtr);

        if (type.Kind == CBindingTypeKind.Enum || _enumNames.Contains(type.Name))
            return new("Int", BindingTypeCategory.EnumAsInt);

        if (type.Kind == CBindingTypeKind.Struct || _structNames.Contains(type.Name))
            return new("RawPtr", BindingTypeCategory.StructByValue, $"struct by value: {type.Name}");

        // typedef 链解析：typedef 名未直接命中 struct/enum 表时，按底层类型递归映射。
        if (type.Kind == CBindingTypeKind.Typedef && _typedefs.TryGetValue(type.Name, out var typedef))
            return Map(typedef.UnderlyingType ?? new CBindingType(typedef.UnderlyingKind, typedef.Underlying, typedef.Underlying), depth + 1);

        return type.Name switch
        {
            "_Bool" or "bool" => new("Bool", BindingTypeCategory.Direct),
            "float" => new("Float", BindingTypeCategory.Direct),
            "double" => new("Float", BindingTypeCategory.Direct),
            // 当前 MIR/backend 边界只支持 64 位标量（Int/Float）；窄整数与 float
            // 由自动 shim 做 ABI 窄化（BindingCShimGenerator），Eidos 侧统一 64 位。
            "char" or "signed char" or "unsigned char" or "int8_t" or "uint8_t" or
                "short" or "unsigned short" or "int16_t" or "uint16_t" or
                "int" or "unsigned int" or "int32_t" or "uint32_t" or
                "long" or "unsigned long" => new("Int", BindingTypeCategory.Direct),
            "int64_t" => new("Int", BindingTypeCategory.Direct),
            "long long" or "unsigned long long" or "uint64_t" or
                "size_t" or "uintptr_t" => new("Int64", BindingTypeCategory.Direct),
            _ => new("RawPtr", BindingTypeCategory.Unsupported, $"unknown type: {type.Spelling}")
        };
    }

    /// <summary>
    /// 函数指针 → Eidos <c>Cfn[参数..., 返回]</c>（1..6 参、零捕获）。
    /// 超出 Cfn 能力或包含不可映射类型的回调降级为 Unsupported（由 shim 兜底）。
    /// </summary>
    private BindingTypeMapping MapFunctionPointer(CBindingType type)
    {
        if (type.FunctionPointerArity is < 1 or > 6)
            return new("RawPtr", BindingTypeCategory.Unsupported, $"callback arity {type.FunctionPointerArity} exceeds Cfn 1..6");

        var parameterTypes = type.FunctionPointerParameterTypes;
        var returnType = type.FunctionPointerReturnType;
        if (parameterTypes == null || returnType == null)
            return new("RawPtr", BindingTypeCategory.Unsupported, "function pointer signature unavailable");

        var parts = new List<string>(parameterTypes.Count + 1);
        foreach (var parameterType in parameterTypes)
        {
            var mapping = Map(parameterType);
            if (mapping.Category is not (BindingTypeCategory.Direct or BindingTypeCategory.EnumAsInt or BindingTypeCategory.RawPtr))
                return new("RawPtr", BindingTypeCategory.Unsupported, $"unsupported callback parameter {parameterType.Spelling}");
            parts.Add(mapping.EidosType);
        }

        var returnMapping = Map(returnType);
        if (returnMapping.Category is not (BindingTypeCategory.Direct or BindingTypeCategory.EnumAsInt or BindingTypeCategory.RawPtr))
            return new("RawPtr", BindingTypeCategory.Unsupported, $"unsupported callback return {returnType.Spelling}");
        parts.Add(returnMapping.EidosType);

        return new($"Cfn[{string.Join(", ", parts)}]", BindingTypeCategory.Direct, $"callback: {type.Spelling}");
    }

    public static string ToEidosFunctionName(string cName)
    {
        if (string.IsNullOrWhiteSpace(cName))
            return cName;

        var result = new StringBuilder();
        for (var i = 0; i < cName.Length; i++)
        {
            var c = cName[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(cName[i - 1]) ||
                              (i + 1 < cName.Length && char.IsLower(cName[i + 1]))))
                {
                    result.Append('_');
                }

                result.Append(char.ToLowerInvariant(c));
            }
            else if (c is '-' or ' ')
            {
                result.Append('_');
            }
            else
            {
                result.Append(c);
            }
        }

        var eidosName = result.ToString();
        return IsEidosKeyword(eidosName) ? eidosName + "_" : eidosName;
    }
}
