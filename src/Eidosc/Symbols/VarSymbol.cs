namespace Eidosc.Symbols;

/// <summary>
/// 变量符号（let 绑定）
/// </summary>
public sealed record VarSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Variable;

    /// <summary>
    /// 是否可变
    /// </summary>
    public bool IsMutable { get; init; }

    /// <summary>
    /// True when the variable is a compile-time binding.
    /// </summary>
    public bool IsComptime { get; init; }

    /// <summary>
    /// 变量类型（类型推断后填充）
    /// </summary>
    public TypeId Type { get; set; } = TypeId.None;

    /// <summary>
    /// Inferred type scheme for module-level value references that are resolved outside
    /// the local type environment where the binding was inferred.
    /// </summary>
    public Eidosc.Types.TypeScheme? Scheme { get; set; }

    /// <summary>
    /// 是否是函数参数
    /// </summary>
    public bool IsParameter { get; init; }

    /// <summary>
    /// 是否是模式匹配绑定的变量
    /// </summary>
    public bool IsPatternBound { get; init; }

    /// <summary>
    /// 模式绑定方式（仅对 IsPatternBound 生效）。
    /// </summary>
    public PatternBindingMode BindingMode { get; init; } = PatternBindingMode.ByValue;
}
