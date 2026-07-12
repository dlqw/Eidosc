using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Semantic;

/// <summary>
/// 已解析的引用信息
/// </summary>
public sealed class ResolvedReference
{
    /// <summary>
    /// 解析到的符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 引用的源代码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// 引用类型
    /// </summary>
    public ReferenceKind Kind { get; init; }
}

/// <summary>
/// 引用类型
/// </summary>
public enum ReferenceKind
{
    /// <summary>
    /// 变量引用
    /// </summary>
    Variable,

    /// <summary>
    /// 函数调用
    /// </summary>
    FunctionCall,

    /// <summary>
    /// 类型引用
    /// </summary>
    Type,

    /// <summary>
    /// 构造器
    /// </summary>
    Constructor,

    /// <summary>
    /// 效应引用
    /// </summary>
    Effect,

    /// <summary>
    /// 模块引用
    /// </summary>
    Module
}

/// <summary>
/// 带有解析信息的 AST 节点包装
/// </summary>
public interface IResolvable
{
    /// <summary>
    /// 解析后的符号 ID
    /// </summary>
    SymbolId? ResolvedSymbol { get; set; }

    /// <summary>
    /// 是否已解析
    /// </summary>
    bool IsResolved => ResolvedSymbol?.IsValid == true;
}
