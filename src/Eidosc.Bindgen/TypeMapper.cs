using Eidosc.Bindgen.Models;

namespace Eidosc.Bindgen;

public enum EidosTypeCategory
{
    Direct,       // Maps directly to a primitive FFI type
    RawPtr,       // C pointer → RawPtr
    StructByValue,// C struct passed by value — needs C shim
    EnumAsInt,    // C enum → Int32
    Unsupported   // Can't map (e.g., function pointers, arrays)
}

public sealed record EidosTypeMapping(
    string EidosType,
    EidosTypeCategory Category,
    string? ShimType = null,
    string? Notes = null
);

public static class TypeMapper
{
    public static readonly HashSet<string> KnownStructNames = [];

    public static void RegisterStructNames(IEnumerable<CStructInfo> structs)
    {
        foreach (var st in structs)
        {
            KnownStructNames.Add(st.Name);
            if (st.TypedefName != null)
                KnownStructNames.Add(st.TypedefName);
        }
    }

    public static EidosTypeMapping MapCType(CTypeInfo type)
    {
        if (type.Kind == CTypeKind.Void)
            return new("Unit", EidosTypeCategory.Direct);

        if (type.Kind == CTypeKind.Array || type.Kind == CTypeKind.FunctionPtr)
            return new("RawPtr", EidosTypeCategory.Unsupported, Notes: "Array or function pointer");

        if (type.Kind == CTypeKind.Pointer)
        {
            return new("RawPtr", EidosTypeCategory.RawPtr);
        }

        if (type.Kind == CTypeKind.Enum)
            return new("Int", EidosTypeCategory.EnumAsInt);

        if (type.Kind == CTypeKind.Struct || type.Kind == CTypeKind.Typedef)
        {
            var spelling = type.Spelling;

            // Known typedef primitives
            switch (spelling)
            {
                case "bool" or "_Bool":
                    return new("Bool", EidosTypeCategory.Direct);
                case "size_t" or "uintptr_t":
                    return new("Int", EidosTypeCategory.Direct);
                case "int8_t":
                    return new("Int", EidosTypeCategory.Direct);
                case "int16_t":
                    return new("Int", EidosTypeCategory.Direct);
                case "int32_t":
                    return new("Int", EidosTypeCategory.Direct);
                case "int64_t":
                    return new("Int", EidosTypeCategory.Direct);
                case "uint8_t":
                    return new("Int", EidosTypeCategory.Direct);
                case "uint16_t":
                    return new("Int", EidosTypeCategory.Direct);
                case "uint32_t":
                    return new("Int", EidosTypeCategory.Direct);
                case "uint64_t":
                    return new("Int", EidosTypeCategory.Direct);
            }

            if (KnownStructNames.Contains(spelling))
                return new("RawPtr", EidosTypeCategory.StructByValue, ShimType: spelling);

            // Unknown typedef — try to map the base name
            return spelling switch
            {
                "float" => new("Float", EidosTypeCategory.Direct),
                "double" => new("Float", EidosTypeCategory.Direct),
                _ => new("RawPtr", EidosTypeCategory.Unsupported, Notes: $"Unknown typedef: {spelling}")
            };
        }

        // Primitive types
        if (type.Kind == CTypeKind.Primitive)
        {
            return type.Name switch
            {
                "bool" or "_Bool" => new("Bool", EidosTypeCategory.Direct),
                "char" or "unsigned char" => new("Int", EidosTypeCategory.Direct),
                "short" => new("Int", EidosTypeCategory.Direct),
                "unsigned short" => new("Int", EidosTypeCategory.Direct),
                "int" => new("Int", EidosTypeCategory.Direct),
                "unsigned int" => new("Int", EidosTypeCategory.Direct),
                "long" => new("Int", EidosTypeCategory.Direct),
                "unsigned long" => new("Int", EidosTypeCategory.Direct),
                "long long" => new("Int", EidosTypeCategory.Direct),
                "unsigned long long" => new("Int", EidosTypeCategory.Direct),
                "float" => new("Float", EidosTypeCategory.Direct),
                "double" => new("Float", EidosTypeCategory.Direct),
                _ => new("RawPtr", EidosTypeCategory.Unsupported, Notes: $"Unknown primitive: {type.Name}")
            };
        }

        return new("RawPtr", EidosTypeCategory.Unsupported, Notes: $"Unknown type kind: {type.Kind}");
    }

    public static string EidosFunctionName(string cName)
    {
        // Convert PascalCase/camelCase C function name to snake_case Eidos name
        if (string.IsNullOrEmpty(cName)) return cName;

        var result = new System.Text.StringBuilder();
        for (int i = 0; i < cName.Length; i++)
        {
            char c = cName[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(cName[i - 1]) ||
                    (i + 1 < cName.Length && char.IsLower(cName[i + 1]))))
                {
                    result.Append('_');
                }
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }
}
