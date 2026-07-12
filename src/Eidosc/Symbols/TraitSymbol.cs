namespace Eidosc.Symbols;

/// <summary>
/// Indicates where the Self type appears in trait method signatures.
/// Used by the monomorphization specializer to determine dispatch strategy.
/// </summary>
public enum SelfPosition
{
    /// <summary>Self appears only in parameter position (e.g., eq: Self -> Self -> Bool)</summary>
    InParameter,

    /// <summary>Self appears only in return position (e.g., pure: A -> F[A] where Self=F)</summary>
    InResult,

    /// <summary>Self appears in both parameter and return positions</summary>
    Both,

    /// <summary>Could not be determined (default/fallback)</summary>
    Unknown
}

/// <summary>
/// Trait 符号
/// </summary>
public sealed record TraitSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Trait;

    /// <summary>
    /// 类型参数
    /// </summary>
    public List<SymbolId> TypeParams { get; init; } = [];

    /// <summary>
    /// Trait 方法列表
    /// </summary>
    public List<SymbolId> Methods { get; init; } = [];

    /// <summary>
    /// 关联类型
    /// </summary>
    public List<SymbolId> AssociatedTypes { get; init; } = [];

    /// <summary>
    /// 父 Trait（继承）
    /// </summary>
    public List<SymbolId> ParentTraits { get; init; } = [];

    /// <summary>
    /// Indicates where Self appears in this trait's method signatures.
    /// </summary>
    public SelfPosition SelfPosition { get; init; } = SelfPosition.Unknown;
}
