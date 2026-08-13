namespace Eidosc.Bindgen;

public enum CBindingTypeKind
{
    Void,
    Primitive,
    Pointer,
    Struct,
    Enum,
    Typedef,
    Array,
    FunctionPointer,
    Union,
    Unknown
}

public sealed record CBindingType(
    CBindingTypeKind Kind,
    string Name,
    string Spelling,
    bool IsUnsigned = false,
    bool IsConst = false,
    int PointerDepth = 0,
    int FunctionPointerArity = 0,
    int ArraySize = 0,
    CBindingType? FunctionPointerReturnType = null,
    IReadOnlyList<CBindingType>? FunctionPointerParameterTypes = null,
    int Size = 0);

public sealed record CBindingParameter(string Name, CBindingType Type);

public sealed record CBindingFunction(
    string Name,
    CBindingType ReturnType,
    IReadOnlyList<CBindingParameter> Parameters,
    bool IsVariadic = false,
    bool IsInline = false);

public sealed record CBindingEnumValue(string Name, long Value);

public sealed record CBindingEnum(string Name, IReadOnlyList<CBindingEnumValue> Values);

public sealed record CBindingField(string Name, CBindingType Type, int Offset = 0, int Size = 0);

public sealed record CBindingStruct(string Name, IReadOnlyList<CBindingField> Fields, int Size = 0, int Alignment = 0);

public sealed record CBindingUnion(string Name, IReadOnlyList<CBindingField> Fields, int Size = 0, int Alignment = 0);

public sealed record CBindingTypedef(string Name, string Underlying, CBindingTypeKind UnderlyingKind, CBindingType? UnderlyingType = null);

public sealed record CBindingConstant(string Name, string Value, bool IsString);

public sealed record CBindingGlobal(string Name, CBindingType Type);

/// <summary>
/// C 头文件提取中间表示。clang 模式（<see cref="Clang.ClangHeaderParser"/>）与
/// 正则模式（<see cref="SimpleCHeaderParser"/>）共用；后补的集合参数保持可选，
/// 简单模式只填充 Functions/Structs/Enums。
/// </summary>
public sealed record CHeaderIr(
    string Header,
    IReadOnlyList<CBindingFunction> Functions,
    IReadOnlyList<CBindingStruct> Structs,
    IReadOnlyList<CBindingEnum> Enums,
    IReadOnlyList<CBindingUnion>? Unions = null,
    IReadOnlyList<CBindingTypedef>? Typedefs = null,
    IReadOnlyList<CBindingConstant>? Constants = null,
    IReadOnlyList<CBindingGlobal>? Globals = null)
{
    public IReadOnlyList<CBindingUnion> UnionsSafe => Unions ?? [];
    public IReadOnlyList<CBindingTypedef> TypedefsSafe => Typedefs ?? [];
    public IReadOnlyList<CBindingConstant> ConstantsSafe => Constants ?? [];
    public IReadOnlyList<CBindingGlobal> GlobalsSafe => Globals ?? [];
}
