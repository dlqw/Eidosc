using Eidosc.Symbols;
using Eidosc.Semantic;

namespace Eidosc;

/// <summary>
/// 结构化函数标识 —— 替代原始字符串函数名，消除 Contains/EndsWith 模糊匹配。
/// 每个字段提供不同粒度的标识，适用于不同场景。
/// </summary>
public sealed record FunctionId
{
    /// <summary>
    /// 符号 ID（编译期唯一标识符）。
    /// 用于精确匹配，O(1) 整数比较。
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 符号类别（Function, Constructor, Module 等）。
    /// </summary>
    public SymbolKind Kind { get; init; } = SymbolKind.Function;

    /// <summary>
    /// 模块全名（如 "Std::Math"）。
    /// 空字符串表示当前模块或无法确定模块。
    /// </summary>
    public string Module { get; init; } = "";

    /// <summary>
    /// Structured module identity key. Includes package instance identity when known.
    /// </summary>
    public string ModuleIdentityKey { get; init; } = "";

    /// <summary>
    /// Stable declaration identity across compilation sessions.
    /// </summary>
    public string StableIdentityKey { get; init; } = "";

    /// <summary>
    /// 函数名（如 "abs"）。
    /// 不含模块路径前缀。
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 源码级符号全名（如 "Std::Math::abs"）。
    /// 含 package 和模块路径前缀，使用 :: 分隔符连接最终符号名。
    /// </summary>
    public string QualifiedName { get; init; } = "";

    /// <summary>
    /// LLVM 级混淆名（如 "eidos_Std__Math__abs"）。
    /// 仅作为后端分配结果的调试/cross-check 元数据；不得作为语义身份或解析 key。
    /// </summary>
    public string MangledName { get; init; } = "";

    /// <summary>
    /// 是否为有效标识（SymbolId 有效）。
    /// </summary>
    public bool IsValid => SymbolId.IsValid;

    public override string ToString() => IsValid ? QualifiedName : Name;
}
