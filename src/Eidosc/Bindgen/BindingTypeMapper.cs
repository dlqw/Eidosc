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
    private readonly IReadOnlyDictionary<string, CBindingStruct> _structsByName;

    public BindingTypeMapper(CHeaderIr ir)
    {
        _structNames = ir.Structs.Select(static st => st.Name).ToHashSet(StringComparer.Ordinal);
        _enumNames = ir.Enums.Select(static en => en.Name).ToHashSet(StringComparer.Ordinal);
        _typedefs = (ir.TypedefsSafe ?? [])
            .ToDictionary(static t => t.Name, StringComparer.Ordinal);
        _structsByName = ir.Structs.ToDictionary(static st => st.Name, StringComparer.Ordinal);
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
            "float" => new("Float32", BindingTypeCategory.Direct),
            "double" => new("Float", BindingTypeCategory.Direct),
            // E5337 收口后 extern(c) 以原生位宽过 FFI 边界：窄整数与 float 直接映射
            // Eidos 窄标量，不再经 shim 窄化。仅 enum（下方 EnumAsInt → Int）和
            // 无布局事实的残留路径仍由 BindingCShimGenerator 宽化。
            // clang 提取器把 char 家族规整为 "char" + IsUnsigned 标志，需按符号分流。
            "char" => new(type.IsUnsigned ? "UInt8" : "Int8", BindingTypeCategory.Direct),
            "signed char" or "int8_t" => new("Int8", BindingTypeCategory.Direct),
            "unsigned char" or "uint8_t" => new("UInt8", BindingTypeCategory.Direct),
            "short" or "int16_t" => new("Int16", BindingTypeCategory.Direct),
            "unsigned short" or "uint16_t" => new("UInt16", BindingTypeCategory.Direct),
            "int" or "int32_t" => new("Int32", BindingTypeCategory.Direct),
            "unsigned int" or "uint32_t" => new("UInt32", BindingTypeCategory.Direct),
            "int64_t" or "long long" => new("Int", BindingTypeCategory.Direct),
            "unsigned long long" or "uint64_t" or "size_t" or "uintptr_t" =>
                new("UInt64", BindingTypeCategory.Direct),
            // long 的宽度随平台（LP64=8、LLP64=4），有布局事实按尺寸映射，无则保守 64 位。
            "long" => type.Size == 4
                ? new("Int32", BindingTypeCategory.Direct)
                : new("Int", BindingTypeCategory.Direct),
            "unsigned long" => type.Size == 4
                ? new("UInt32", BindingTypeCategory.Direct)
                : new("UInt64", BindingTypeCategory.Direct),
            _ => new("RawPtr", BindingTypeCategory.Unsupported, $"unknown type: {type.Spelling}")
        };
    }

    /// <summary>
    /// 函数指针 → Eidos <c>Cfn[参数..., 返回]</c>（任意 arity、零捕获）。
    /// 包含不可映射类型的回调降级为 Unsupported（由 shim 兜底）。
    /// </summary>
    /// <summary>
    /// 将 struct 按值参数递归展开为叶子字段类型（供 Eidos 签名使用，全部 64 位）。
    /// 含 union/数组/未知结构等不可拆分字段时返回 false（该函数应 SKIP 而非 void* 兜底——
    /// Eidos 无法构造任意结构体内存）。
    /// </summary>
    public bool TryFlattenStructFields(CBindingType type, out IReadOnlyList<CBindingType> leafTypes)
    {
        var leaves = new List<CBindingType>();
        var ok = FlattenInto(type, leaves, 0, out var contributed);
        leafTypes = leaves;
        return ok && contributed;
    }

    private bool FlattenInto(CBindingType type, List<CBindingType> leaves, int depth, out bool contributed)
    {
        contributed = false;
        if (depth > 16)
            return false;

        if (type.Kind == CBindingTypeKind.Struct)
        {
            if (!_structsByName.TryGetValue(type.Name, out var st) || st.Fields.Count == 0)
                return false;

            var any = false;
            foreach (var field in st.Fields)
            {
                if (!FlattenInto(field.Type, leaves, depth + 1, out var fieldContributed))
                    return false;
                any |= fieldContributed;
            }

            contributed = any;
            return true;
        }

        if (type.Kind == CBindingTypeKind.Typedef && _typedefs.TryGetValue(type.Name, out var typedef))
            return FlattenInto(typedef.UnderlyingType ?? new CBindingType(typedef.UnderlyingKind, typedef.Underlying, typedef.Underlying), leaves, depth + 1, out contributed);

        if (type.Kind is CBindingTypeKind.Union or CBindingTypeKind.Array)
            return false;

        leaves.Add(type);
        contributed = true;
        return true;
    }

    private BindingTypeMapping MapFunctionPointer(CBindingType type)
    {
        if (type.FunctionPointerArity < 0)
            return new("RawPtr", BindingTypeCategory.Unsupported, $"callback arity {type.FunctionPointerArity} is invalid");

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

