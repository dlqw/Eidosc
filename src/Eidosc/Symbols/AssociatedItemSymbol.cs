namespace Eidosc.Symbols;

public abstract record AssociatedItemSymbol : Symbol
{
    public SymbolId OwnerTrait { get; init; } = SymbolId.None;

    public SymbolId OwnerImpl { get; init; } = SymbolId.None;
}

public sealed record AssociatedTypeSymbol : AssociatedItemSymbol
{
    public override SymbolKind Kind => SymbolKind.AssociatedType;

    public List<SymbolId> TypeParams { get; init; } = [];
}

public sealed record AssociatedConstSymbol : AssociatedItemSymbol
{
    public override SymbolKind Kind => SymbolKind.AssociatedConst;

    public TypeId ValueType { get; set; } = TypeId.None;
}
