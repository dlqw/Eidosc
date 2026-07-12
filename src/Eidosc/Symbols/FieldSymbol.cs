namespace Eidosc.Symbols;

/// <summary>
/// 字段符号
/// </summary>
public sealed record FieldSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Field;

    /// <summary>
    /// 字段类型
    /// </summary>
    public TypeId FieldType { get; set; } = TypeId.None;

    /// <summary>
    /// 所属类型（ADT 或构造器）
    /// </summary>
    public SymbolId OwnerType { get; init; } = SymbolId.None;

    /// <summary>
    /// 字段在构造器中的索引（位置参数）
    /// </summary>
    public int Index { get; init; } = -1;
}
