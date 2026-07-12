using Eidosc.Utils;

namespace Eidosc.Symbols;

/// <summary>
/// 符号定义基类
/// </summary>
public abstract record Symbol
{
    /// <summary>
    /// 符号唯一标识
    /// </summary>
    public SymbolId Id { get; init; } = SymbolId.None;

    /// <summary>
    /// 符号名称
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 符号种类
    /// </summary>
    public abstract SymbolKind Kind { get; }

    /// <summary>
    /// 源代码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// 是否已解析类型
    /// </summary>
    public bool IsTypeResolved { get; set; }

    /// <summary>
    /// 是否是模块级别的符号
    /// </summary>
    public bool IsModuleLevel { get; init; }

    /// <summary>
    /// 是否可从外部访问（公开）
    /// </summary>
    public bool IsPublic { get; init; } = true;

    /// <summary>
    /// 类型 ID（仅对类型符号有效）
    /// 用于 ADT、Trait、Effect 等类型定义
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    public override string ToString() => $"{Kind}:{Name}${Id.Value}";
}
