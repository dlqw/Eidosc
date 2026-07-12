namespace Eidosc.Symbols;

public sealed record RestoredScopeBinding(
    int Index,
    int ParentIndex,
    ScopeKind Kind,
    IReadOnlyDictionary<string, SymbolId> Bindings,
    IReadOnlyDictionary<string, IReadOnlyList<SymbolId>> FunctionOverloads,
    IReadOnlyDictionary<string, SymbolId> Types,
    IReadOnlyDictionary<string, SymbolId> Traits,
    IReadOnlyDictionary<string, SymbolId> Effects,
    IReadOnlyDictionary<string, SymbolId> Constructors);
