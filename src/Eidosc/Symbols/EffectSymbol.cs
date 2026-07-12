namespace Eidosc.Symbols;

/// <summary>
/// Nominal compile-time effect tag symbol.
/// </summary>
public sealed record EffectSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Effect;
}
