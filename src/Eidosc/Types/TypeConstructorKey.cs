using Eidosc.Symbols;

namespace Eidosc.Types;

public enum TypeConstructorKeyKind
{
    Symbol,
    TypeId,
    Variable,
    Builtin
}

public readonly record struct TypeConstructorKey(TypeConstructorKeyKind Kind, int Id)
{
    public static TypeConstructorKey FromSymbol(SymbolId symbolId) => new(TypeConstructorKeyKind.Symbol, symbolId.Value);

    public static TypeConstructorKey FromTypeId(TypeId typeId) => new(TypeConstructorKeyKind.TypeId, typeId.Value);

    public static TypeConstructorKey FromVariable(int variableIndex) => new(TypeConstructorKeyKind.Variable, variableIndex);

    public static TypeConstructorKey FromBuiltin(TypeId typeId) => new(TypeConstructorKeyKind.Builtin, typeId.Value);

    public SymbolId SymbolId => Kind == TypeConstructorKeyKind.Symbol ? new SymbolId(Id) : SymbolId.None;

    public TypeId TypeId => Kind is TypeConstructorKeyKind.TypeId or TypeConstructorKeyKind.Builtin
        ? new TypeId(Id)
        : TypeId.None;

    public int? VariableIndex => Kind == TypeConstructorKeyKind.Variable ? Id : null;

    public string ToDescriptorString() => Kind switch
    {
        TypeConstructorKeyKind.Symbol => $"sym:{Id}",
        TypeConstructorKeyKind.TypeId => $"type:{Id}",
        TypeConstructorKeyKind.Variable => $"var:{Id}",
        TypeConstructorKeyKind.Builtin => $"builtin:{Id}",
        _ => throw new InvalidOperationException($"Unsupported type constructor key kind: {Kind}")
    };

    public static bool TryParse(string descriptor, out TypeConstructorKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return false;
        }

        if (descriptor.StartsWith("sym:", StringComparison.Ordinal) &&
            int.TryParse(descriptor["sym:".Length..], out var symbolId) &&
            symbolId >= 0)
        {
            key = new TypeConstructorKey(TypeConstructorKeyKind.Symbol, symbolId);
            return true;
        }

        if (descriptor.StartsWith("type:", StringComparison.Ordinal) &&
            int.TryParse(descriptor["type:".Length..], out var typeId) &&
            typeId >= 0)
        {
            key = new TypeConstructorKey(TypeConstructorKeyKind.TypeId, typeId);
            return true;
        }

        if (descriptor.StartsWith("var:", StringComparison.Ordinal) &&
            int.TryParse(descriptor["var:".Length..], out var variableIndex) &&
            variableIndex >= 0)
        {
            key = new TypeConstructorKey(TypeConstructorKeyKind.Variable, variableIndex);
            return true;
        }

        if (descriptor.StartsWith("builtin:", StringComparison.Ordinal) &&
            int.TryParse(descriptor["builtin:".Length..], out var builtinId) &&
            builtinId >= 0)
        {
            key = new TypeConstructorKey(TypeConstructorKeyKind.Builtin, builtinId);
            return true;
        }

        return false;
    }

    public static TypeConstructorKey Parse(string descriptor)
    {
        if (TryParse(descriptor, out var key))
        {
            return key;
        }

        throw new FormatException($"Invalid type constructor descriptor: {descriptor}");
    }

    public override string ToString() => ToDescriptorString();
}
