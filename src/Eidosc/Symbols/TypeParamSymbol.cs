namespace Eidosc.Symbols;

/// <summary>
/// 类型参数符号
/// </summary>
public sealed record TypeParamSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.TypeParameter;

    /// <summary>
    /// Kind（类型参数的 Kind）
    /// </summary>
    public string KindAnnotation { get; init; } = "kind1";

    /// <summary>
    /// Whether this parameter was declared with the comptime generic marker.
    /// </summary>
    public bool IsComptime { get; init; }

    /// <summary>
    /// Source-level type annotation for comptime generic parameters.
    /// </summary>
    public string? ComptimeTypeAnnotation { get; init; }

    /// <summary>
    /// Trait 约束
    /// </summary>
    public List<SymbolId> TraitConstraints { get; init; } = [];
}
