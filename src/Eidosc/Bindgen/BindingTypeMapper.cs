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
    private readonly HashSet<string> _structNames;
    private readonly HashSet<string> _enumNames;

    public BindingTypeMapper(CHeaderIr ir)
    {
        _structNames = ir.Structs.Select(static st => st.Name).ToHashSet(StringComparer.Ordinal);
        _enumNames = ir.Enums.Select(static en => en.Name).ToHashSet(StringComparer.Ordinal);
    }

    public BindingTypeMapping Map(CBindingType type)
    {
        if (type.Kind == CBindingTypeKind.Void)
            return new("Unit", BindingTypeCategory.Direct);

        if (type.Kind is CBindingTypeKind.Array or CBindingTypeKind.FunctionPointer)
            return new("RawPtr", BindingTypeCategory.Unsupported, "array or function pointer");

        if (type.Kind == CBindingTypeKind.Pointer || type.PointerDepth > 0)
            return new("RawPtr", BindingTypeCategory.RawPtr);

        if (type.Kind == CBindingTypeKind.Enum || _enumNames.Contains(type.Name))
            return new("Int", BindingTypeCategory.EnumAsInt);

        if (type.Kind == CBindingTypeKind.Struct || _structNames.Contains(type.Name))
            return new("RawPtr", BindingTypeCategory.StructByValue, $"struct by value: {type.Name}");

        return type.Name switch
        {
            "_Bool" or "bool" => new("Bool", BindingTypeCategory.Direct),
            "float" or "double" => new("Float", BindingTypeCategory.Direct),
            "char" or "signed char" or "unsigned char" or "int8_t" or "uint8_t" => new("Int8", BindingTypeCategory.Direct),
            "short" or "unsigned short" or "int16_t" or "uint16_t" => new("Int16", BindingTypeCategory.Direct),
            "int" or "unsigned int" or "int32_t" or "uint32_t" => new("Int32", BindingTypeCategory.Direct),
            "long" or "unsigned long" or "long long" or "unsigned long long" or "int64_t" or "uint64_t" or
                "size_t" or "uintptr_t" => new("Int64", BindingTypeCategory.Direct),
            _ => new("RawPtr", BindingTypeCategory.Unsupported, $"unknown type: {type.Spelling}")
        };
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

        return result.ToString();
    }
}
