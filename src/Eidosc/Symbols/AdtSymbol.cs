using Eidosc.Semantic;
namespace Eidosc.Symbols;

/// <summary>
/// ADT 类型符号
/// </summary>
public sealed record AdtSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Adt;

    /// <summary>
    /// 类型参数
    /// </summary>
    public List<SymbolId> TypeParams { get; init; } = [];

    /// <summary>
    /// 构造器列表
    /// </summary>
    public List<SymbolId> Constructors { get; init; } = [];

    /// <summary>
    /// 字段列表（如果是积类型）
    /// </summary>
    public List<SymbolId> Fields { get; init; } = [];

    /// <summary>
    /// Direct lexical case types in source order. An empty list denotes a
    /// product or a leaf case; a non-empty list makes this node a sealed sum.
    /// </summary>
    public List<SymbolId> DirectCases { get; init; } = [];

    /// <summary>
    /// Lexical parent for an exact case type. This is not general-purpose
    /// inheritance: only compiler-declared closed case edges populate it.
    /// </summary>
    public SymbolId ParentAdt { get; init; } = SymbolId.None;

    public SymbolId CaseConstructor { get; init; } = SymbolId.None;

    /// <summary>
    /// Canonical direct-parent specialization for a closed case edge. This is
    /// recorded for default and explicit GADT edges and survives symbol-cache
    /// restoration; it is not a general inheritance contract.
    /// </summary>
    public string CanonicalParentSpecialization { get; init; } = string.Empty;

    public bool IsCaseType => ParentAdt.IsValid;

    public bool IsClosedSum => DirectCases.Count > 0;

    /// <summary>
    /// 是否是类型别名
    /// </summary>
    public bool IsTypeAlias => AliasTarget != null;

    /// <summary>
    /// 类型别名目标
    /// </summary>
    public TypeId? AliasTarget { get; init; }

    /// <summary>
    /// 是否是 @cstruct 声明的 C 结构体
    /// </summary>
    public bool IsCStruct { get; set; }

    /// <summary>
    /// C 结构体布局信息（仅当 IsCStruct 为 true 时有效）
    /// </summary>
    public CStructLayout? CStructLayoutInfo { get; set; }
}
