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

    /// <summary>
    /// The module that originally declared this symbol. Imports and re-exports must not change it.
    /// </summary>
    public SymbolId DefinitionModuleId { get; init; } = SymbolId.None;

    public GeneratedDeclarationOrigin? GeneratedOrigin { get; init; }

    public override string ToString() => $"{Kind}:{Name}${Id.Value}";
}

public sealed record GeneratedDeclarationOrigin
{
    public required string StableIdentity { get; init; }

    public required string GenerationSlotIdentity { get; init; }

    public required string GeneratorIdentity { get; init; }

    public required string TargetIdentity { get; init; }

    public SymbolId GeneratorSymbolId { get; init; } = SymbolId.None;

    public SymbolId TargetSymbolId { get; init; } = SymbolId.None;

    public int ClauseOccurrenceIndex { get; init; }

    public string ClauseOccurrenceIdentity { get; init; } = string.Empty;

    public int ClauseArgumentSubIndex { get; init; } = -1;

    public int ExpansionOutputIndex { get; init; }

    public string CanonicalArgumentsHash { get; init; } = string.Empty;

    public int MetaSchemaVersion { get; init; }

    public SourceSpan ClauseSpan { get; init; }

    public string VirtualDocumentPath { get; init; } = string.Empty;
}
