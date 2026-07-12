namespace Eidosc.Symbols;

/// <summary>
/// 构造器符号
/// </summary>
public sealed record CtorSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Constructor;

    /// <summary>
    /// 所属 ADT 类型
    /// </summary>
    public SymbolId OwnerAdt { get; init; } = SymbolId.None;

    /// <summary>
    /// 类型参数
    /// </summary>
    public List<SymbolId> TypeParams { get; init; } = [];

    /// <summary>
    /// 位置参数类型
    /// </summary>
    public List<TypeId> PositionalArgs { get; set; } = [];

    public string? SignatureText { get; init; }

    /// <summary>
    /// 命名字段
    /// </summary>
    public List<SymbolId> NamedFields { get; init; } = [];

    /// <summary>
    /// 是否是无参构造器（如 None）
    /// </summary>
    public bool IsNullary => PositionalArgs.Count == 0 && NamedFields.Count == 0;
}
