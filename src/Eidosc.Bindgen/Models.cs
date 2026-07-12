using System.Text.Json.Serialization;

namespace Eidosc.Bindgen.Models;

public enum CTypeKind
{
    Void,
    Primitive,
    Pointer,
    ConstQualified,
    Struct,
    Enum,
    Typedef,
    Array,
    FunctionPtr,
    Unknown
}

public sealed record CTypeInfo
{
    [JsonPropertyName("kind")]
    public CTypeKind Kind { get; init; }
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("spelling")]
    public string Spelling { get; init; } = "";
    [JsonPropertyName("is_const")]
    public bool IsConst { get; init; }
    [JsonPropertyName("is_unsigned")]
    public bool IsUnsigned { get; init; }
    [JsonPropertyName("pointer_depth")]
    public int PointerDepth { get; init; }
    [JsonPropertyName("array_size")]
    public int ArraySize { get; init; }

    public bool IsPointer => Kind == CTypeKind.Pointer || PointerDepth > 0;
    public bool IsCharPointer => IsPointer && (Spelling.Contains("char") || Spelling.Contains("Char"));
    public bool IsVoidPointer => IsPointer && Spelling.Contains("void");
    public bool IsStructValue => Kind == CTypeKind.Struct || (Kind == CTypeKind.Typedef &&
        Spelling != "bool" && Spelling != "size_t" && Spelling != "uintptr_t" &&
        Spelling != "int8_t" && Spelling != "int16_t" && Spelling != "int32_t" && Spelling != "int64_t" &&
        Spelling != "uint8_t" && Spelling != "uint16_t" && Spelling != "uint32_t" && Spelling != "uint64_t" &&
        Spelling != "float" && Spelling != "double" && Spelling != "int" && Spelling != "unsigned int" &&
        Spelling != "long" && Spelling != "unsigned long" && Spelling != "short" && Spelling != "unsigned short" &&
        Spelling != "char" && Spelling != "unsigned char" && Spelling != "void");
    public bool IsEnumValue => Kind == CTypeKind.Enum;
}

public sealed record CParamInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("type")]
    public CTypeInfo Type { get; init; } = new();
}

public sealed record CFunctionInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("return_type")]
    public CTypeInfo ReturnType { get; init; } = new();
    [JsonPropertyName("is_variadic")]
    public bool IsVariadic { get; init; }
    [JsonPropertyName("params")]
    public List<CParamInfo> Params { get; init; } = [];
}

public sealed record CFieldInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("type")]
    public CTypeInfo Type { get; init; } = new();
    [JsonPropertyName("offset")]
    public int Offset { get; init; }
    [JsonPropertyName("size")]
    public int Size { get; init; }
}

public sealed record CStructInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("typedef_name")]
    public string? TypedefName { get; init; }
    [JsonPropertyName("fields")]
    public List<CFieldInfo> Fields { get; init; } = [];
    [JsonPropertyName("size")]
    public int Size { get; init; }
    [JsonPropertyName("alignment")]
    public int Alignment { get; init; }

    public string EffectiveName => TypedefName ?? Name;
}

public sealed record CEnumValueInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("value")]
    public long Value { get; init; }
}

public sealed record CEnumInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("typedef_name")]
    public string? TypedefName { get; init; }
    [JsonPropertyName("values")]
    public List<CEnumValueInfo> Values { get; init; } = [];
    [JsonPropertyName("underlying_is_signed")]
    public bool UnderlyingIsSigned { get; init; }

    public string EffectiveName => TypedefName ?? Name;
}

public sealed record CTypedefInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    [JsonPropertyName("underlying")]
    public string Underlying { get; init; } = "";
}

public sealed record CHeaderIr
{
    [JsonPropertyName("header")]
    public string Header { get; init; } = "";
    [JsonPropertyName("functions")]
    public List<CFunctionInfo> Functions { get; init; } = [];
    [JsonPropertyName("structs")]
    public List<CStructInfo> Structs { get; init; } = [];
    [JsonPropertyName("enums")]
    public List<CEnumInfo> Enums { get; init; } = [];
    [JsonPropertyName("typedefs")]
    public List<CTypedefInfo> Typedefs { get; init; } = [];
}
