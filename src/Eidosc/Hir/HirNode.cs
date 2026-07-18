using Eidosc.Utils;
using Eidosc.Symbols;

namespace Eidosc.Hir;

/// <summary>
/// HIR 节点类型
/// </summary>
public enum HirKind
{
    /// <summary>模块</summary>
    Module,
    /// <summary>函数</summary>
    Func,
    /// <summary>不可变绑定 (let)</summary>
    Val,
    /// <summary>可变绑定 (let mut)</summary>
    Var,
    /// <summary>表达式</summary>
    Expr,
    /// <summary>类型声明</summary>
    Type,
    /// <summary>模式</summary>
    Pattern
}

/// <summary>
/// HIR 节点抽象基类
/// 所有 HIR 节点都应继承此类
/// </summary>
public abstract record HirNode
{
    /// <summary>
    /// 源码位置信息
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// 节点类型
    /// </summary>
    public HirKind Kind { get; init; }

    /// <summary>
    /// 类型 ID（类型推断阶段填充）
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// 符号 ID（可选，用于引用已解析的符号）
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    public IReadOnlyList<GeneratedDeclarationOrigin> GeneratedOriginChain { get; internal set; } = [];

    protected HirNode(HirKind kind)
    {
        Kind = kind;
    }

    /// <summary>
    /// 检查是否有有效的类型 ID
    /// </summary>
    public bool HasType => TypeId.IsValid;

    /// <summary>
    /// 检查是否有有效的符号 ID
    /// </summary>
    public bool HasSymbol => SymbolId.IsValid;

    public override string ToString() => $"{Kind}:{GetType().Name}";
}
