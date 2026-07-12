namespace Eidosc.Pipeline;

using Eidosc.Symbols;

public sealed record TypesSymbolStatePayload(
    string SchemaVersion,
    IReadOnlyList<TypesSymbolStateEntryPayload> Entries,
    string Hash)
{
    public const string CurrentSchemaVersion = "types-symbol-state-payload-v1";

    public static TypesSymbolStatePayload Create(
        SymbolTable? symbolTable,
        SymbolTablePayload? namerSymbolTable,
        IReadOnlySet<int>? allowedSymbolIds = null)
    {
        if (symbolTable == null)
        {
            return Empty();
        }

        var namerSymbols = namerSymbolTable?.Symbols.ToDictionary(static symbol => symbol.Id) ?? [];
        var entries = symbolTable.Symbols.Values
            .OrderBy(static symbol => symbol.Id.Value)
            .Where(symbol => allowedSymbolIds == null || allowedSymbolIds.Contains(symbol.Id.Value))
            .Where(symbol => HasTypesStateChange(symbol, namerSymbols.GetValueOrDefault(symbol.Id.Value)))
            .Select(TypesSymbolStateEntryPayload.Create)
            .ToArray();
        return FromEntries(entries);
    }

    internal static TypesSymbolStatePayload FromEntries(
        IEnumerable<TypesSymbolStateEntryPayload> entries)
    {
        var payload = new TypesSymbolStatePayload(
            CurrentSchemaVersion,
            entries.OrderBy(static entry => entry.SymbolId).ToArray(),
            "");
        return payload with { Hash = ComputeHash(payload) };
    }

    public bool HasValidHash() =>
        SchemaVersion == CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ComputeHash(this), StringComparison.Ordinal);

    private static TypesSymbolStatePayload Empty()
    {
        var payload = new TypesSymbolStatePayload(CurrentSchemaVersion, [], "");
        return payload with { Hash = ComputeHash(payload) };
    }

    private static bool HasTypesStateChange(Symbol symbol, SymbolPayload? namerSymbol)
    {
        if (namerSymbol == null)
        {
            return true;
        }

        var current = SymbolPayload.Create(symbol);
        if (current.IsTypeResolved != namerSymbol.IsTypeResolved ||
            current.TypeId != namerSymbol.TypeId)
        {
            return true;
        }

        return symbol switch
        {
            FuncSymbol => HasDifferentFact(current, namerSymbol, "paramTypes") ||
                          HasDifferentFact(current, namerSymbol, "returnType") ||
                          HasDifferentFact(current, namerSymbol, "cStructFieldTypeId"),
            VarSymbol => HasDifferentFact(current, namerSymbol, "type") ||
                         HasDifferentFact(current, namerSymbol, "scheme"),
            CtorSymbol => HasDifferentFact(current, namerSymbol, "positionalArgs") ||
                          HasDifferentFact(current, namerSymbol, "signatureText"),
            FieldSymbol => HasDifferentFact(current, namerSymbol, "fieldType"),
            TypeParamSymbol => HasDifferentFact(current, namerSymbol, "kindAnnotation"),
            _ => false
        };
    }

    private static bool HasDifferentFact(SymbolPayload current, SymbolPayload previous, string name) =>
        !string.Equals(
            current.Facts.GetValueOrDefault(name, ""),
            previous.Facts.GetValueOrDefault(name, ""),
            StringComparison.Ordinal);

    private static string ComputeHash(TypesSymbolStatePayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" });
}

public sealed record TypesSymbolStateEntryPayload(
    int SymbolId,
    string SymbolKind,
    bool IsTypeResolved,
    int TypeId,
    IReadOnlyList<int> FunctionParameterTypeIds,
    int FunctionReturnTypeId,
    int FunctionCStructFieldTypeId,
    int VariableTypeId,
    TypeSchemePayload? VariableScheme,
    IReadOnlyList<int> ConstructorPositionalArgumentTypeIds,
    string? ConstructorSignatureText,
    int FieldTypeId,
    string? TypeParameterKindAnnotation)
{
    public static TypesSymbolStateEntryPayload Create(Symbol symbol) =>
        new(
            symbol.Id.Value,
            symbol.Kind.ToString(),
            symbol.IsTypeResolved,
            symbol.TypeId.Value,
            symbol is FuncSymbol function ? ToInts(function.ParamTypes) : [],
            symbol is FuncSymbol functionReturn ? functionReturn.ReturnType.Value : 0,
            symbol is FuncSymbol functionCStruct ? functionCStruct.CStructFieldTypeId.Value : 0,
            symbol is VarSymbol variable ? variable.Type.Value : 0,
            symbol is VarSymbol { Scheme: { } scheme } ? TypeSchemePayload.Create(scheme) : null,
            symbol is CtorSymbol constructor ? ToInts(constructor.PositionalArgs) : [],
            symbol is CtorSymbol constructorSignature ? constructorSignature.SignatureText : null,
            symbol is FieldSymbol field ? field.FieldType.Value : 0,
            symbol is TypeParamSymbol typeParameter ? typeParameter.KindAnnotation : null);

    private static int[] ToInts(IEnumerable<TypeId> typeIds) =>
        typeIds.Select(static typeId => typeId.Value).ToArray();
}
