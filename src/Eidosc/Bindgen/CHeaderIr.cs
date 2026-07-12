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
    Unknown
}

public sealed record CBindingType(
    CBindingTypeKind Kind,
    string Name,
    string Spelling,
    bool IsUnsigned = false,
    bool IsConst = false,
    int PointerDepth = 0);

public sealed record CBindingParameter(string Name, CBindingType Type);

public sealed record CBindingFunction(
    string Name,
    CBindingType ReturnType,
    IReadOnlyList<CBindingParameter> Parameters,
    bool IsVariadic = false);

public sealed record CBindingEnumValue(string Name, long Value);

public sealed record CBindingEnum(string Name, IReadOnlyList<CBindingEnumValue> Values);

public sealed record CBindingField(string Name, CBindingType Type, int Offset = 0, int Size = 0);

public sealed record CBindingStruct(string Name, IReadOnlyList<CBindingField> Fields, int Size = 0, int Alignment = 0);

public sealed record CHeaderIr(
    string Header,
    IReadOnlyList<CBindingFunction> Functions,
    IReadOnlyList<CBindingStruct> Structs,
    IReadOnlyList<CBindingEnum> Enums);
