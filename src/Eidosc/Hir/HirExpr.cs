using Eidosc.Utils;

namespace Eidosc.Hir;

#region 字面量

/// <summary>
/// HIR error expression produced by recovery/fallback lowering.
/// </summary>
public sealed record HirError : HirNode
{
    public HirError() : base(HirKind.Expr) { }

    public string Reason { get; init; } = "";

    public bool IsRecovered { get; init; }

    public override string ToString() => string.IsNullOrWhiteSpace(Reason)
        ? "<hir-error>"
        : $"<hir-error: {Reason}>";
}

/// <summary>
/// 字面量表达式
/// </summary>
public sealed record HirLiteral : HirNode
{
    public HirLiteral() : base(HirKind.Expr) { }

    /// <summary>
    /// 字面量值类型
    /// </summary>
    public LiteralKind LiteralKind { get; init; }

    /// <summary>
    /// 字面量值
    /// </summary>
    public object? Value { get; init; }

    public override string ToString() => $"Literal({LiteralKind}: {Value})";
}

/// <summary>
/// 字面量类型
/// </summary>
public enum LiteralKind
{
    Int,
    Float,
    String,
    Char,
    Bool,
    Unit
}

#endregion

#region 变量引用

/// <summary>
/// 变量引用表达式
/// </summary>
public sealed record HirVar : HirNode
{
    public HirVar() : base(HirKind.Expr) { }

    /// <summary>
    /// 变量名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 解析后的符号 ID（必须有效）
    /// </summary>
    public new SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 显式类型实参（如 f[Int] 中的 Int），以 TypeId 结构化承载。
    /// </summary>
    public List<TypeId> TypeArgumentIds { get; init; } = [];

    public override string ToString() => $"Var({Name})";
}

#endregion

#region 二元运算

/// <summary>
/// 二元运算符类型
/// </summary>
public enum BinaryOp
{
    Add,
    Sub,
    Mul,
    Div,
    Mod,
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    And,
    Or,
    Concat
}

/// <summary>
/// 二元运算表达式
/// </summary>
public sealed record HirBinOp : HirNode
{
    public HirBinOp() : base(HirKind.Expr) { }

    /// <summary>
    /// 运算符
    /// </summary>
    public BinaryOp Operator { get; init; }

    /// <summary>
    /// 左操作数
    /// </summary>
    public HirNode Left { get; init; } = null!;

    /// <summary>
    /// 右操作数
    /// </summary>
    public HirNode Right { get; init; } = null!;

    public override string ToString() => $"BinOp({Operator})";
}

#endregion

#region 一元运算

/// <summary>
/// 一元运算符类型
/// </summary>
public enum UnaryOp
{
    Neg,
    Not,
    Deref,
    AddressOf,
    Ref,
    MRef
}

/// <summary>
/// 一元运算表达式
/// </summary>
public sealed record HirUnaryOp : HirNode
{
    public HirUnaryOp() : base(HirKind.Expr) { }

    /// <summary>
    /// 运算符
    /// </summary>
    public UnaryOp Operator { get; init; }

    /// <summary>
    /// 操作数
    /// </summary>
    public HirNode Operand { get; init; } = null!;

    public override string ToString() => $"UnaryOp({Operator})";
}

#endregion

#region 函数调用

/// <summary>
/// 调用约定
/// </summary>
public enum CallConvention
{
    /// <summary>普通函数调用</summary>
    Normal,
    /// <summary>构造器调用</summary>
    Constructor
}

/// <summary>
/// 源码层调用表面写法。
/// HIR 统一 callable application 的同时保留来源，避免后续阶段重新依赖 AST 分叉。
/// </summary>
public enum HirCallSurfaceSyntax
{
    Direct,
    Method,
    Infix,
    Pipe,
    OperatorDesugaring
}

/// <summary>
/// 函数调用表达式
/// </summary>
public sealed record HirCall : HirNode
{
    public HirCall() : base(HirKind.Expr) { }

    /// <summary>
    /// 被调用的函数
    /// </summary>
    public HirNode Function { get; init; } = null!;

    /// <summary>
    /// 参数列表
    /// </summary>
    public List<HirNode> Arguments { get; init; } = [];

    /// <summary>
    /// 调用约定
    /// </summary>
    public CallConvention Convention { get; init; } = CallConvention.Normal;

    /// <summary>
    /// 记录该调用来自哪种源码表面写法。
    /// </summary>
    public HirCallSurfaceSyntax SurfaceSyntax { get; init; } = HirCallSurfaceSyntax.Direct;

    /// <summary>
    /// 记录调用 owner（模块 / trait / ability）的符号 ID；无 owner 时为 None。
    /// </summary>
    public SymbolId OwnerSymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 记录结构化 owner 的可读路径；无 owner 时为空。
    /// </summary>
    public string OwnerPath { get; init; } = "";

    /// <summary>
    /// 是否显式写出了 owner 路径。
    /// </summary>
    public bool HasExplicitOwner { get; init; }

    /// <summary>
    /// receiver 在参数列表中的位置；没有 receiver 时为 null。
    /// </summary>
    public int? ReceiverArgumentIndex { get; init; }

    /// <summary>
    /// 因 method / pipe 等 sugar 注入的参数个数。
    /// </summary>
    public int InjectedArgumentCount { get; init; }

    public override string ToString() => $"Call({Function}, {Arguments.Count} args)";
}

#endregion

#region 条件表达式

/// <summary>
/// if 条件表达式
/// </summary>
public sealed record HirIf : HirNode
{
    public HirIf() : base(HirKind.Expr) { }

    /// <summary>
    /// 条件表达式
    /// </summary>
    public HirNode Condition { get; init; } = null!;

    /// <summary>
    /// then 分支
    /// </summary>
    public HirNode ThenBranch { get; init; } = null!;

    /// <summary>
    /// else 分支（可选）
    /// </summary>
    public HirNode? ElseBranch { get; init; }

    public override string ToString() => ElseBranch != null ? "If(..., ..., ...)" : "If(..., ...)";
}

#endregion

#region 循环控制

/// <summary>
/// loop 循环表达式
/// </summary>
public sealed record HirLoop : HirNode
{
    public HirLoop() : base(HirKind.Expr) { }

    /// <summary>
    /// 循环体
    /// </summary>
    public HirNode Body { get; init; } = null!;

    public override string ToString() => "Loop(...)";
}

/// <summary>
/// break 表达式
/// </summary>
public sealed record HirBreak : HirNode
{
    public HirBreak() : base(HirKind.Expr) { }

    /// <summary>
    /// break 返回值（可选）
    /// </summary>
    public HirNode? Value { get; init; }

    public override string ToString() => Value != null ? "Break(...)" : "Break";
}

/// <summary>
/// return 表达式
/// </summary>
public sealed record HirReturn : HirNode
{
    public HirReturn() : base(HirKind.Expr) { }

    /// <summary>
    /// return 返回值（可选）
    /// </summary>
    public HirNode? Value { get; init; }

    public override string ToString() => Value != null ? "Return(...)" : "Return";
}

/// <summary>
/// continue 表达式
/// </summary>
public sealed record HirContinue : HirNode
{
    public HirContinue() : base(HirKind.Expr) { }

    public override string ToString() => "Continue";
}

/// <summary>
/// Explicit unreachable expression.
/// </summary>
public sealed record HirUnreachable : HirNode
{
    public HirUnreachable() : base(HirKind.Expr) { }

    public override string ToString() => WellKnownStrings.Keywords.Unreachable;
}

#endregion

#region 模式匹配

/// <summary>
/// 模式守卫：`pattern <- source`。
/// 仅用于 match/function 分支 guard。
/// </summary>
public sealed record HirPatternGuard : HirNode
{
    public HirPatternGuard() : base(HirKind.Expr) { }

    /// <summary>
    /// 守卫模式。
    /// </summary>
    public HirPattern Pattern { get; init; } = null!;

    /// <summary>
    /// 守卫源表达式。
    /// </summary>
    public HirNode SourceExpression { get; init; } = null!;

    public override string ToString() => "PatternGuard(<-)";
}

/// <summary>
/// 顺序守卫链。
/// </summary>
public sealed record HirSequentialGuard : HirNode
{
    public HirSequentialGuard() : base(HirKind.Expr) { }

    public List<HirNode> Guards { get; } = [];

    public override string ToString() => $"SequentialGuard({Guards.Count})";
}

/// <summary>
/// match 分支
/// </summary>
public sealed record HirMatchBranch
{
    /// <summary>
    /// 模式
    /// </summary>
    public HirPattern Pattern { get; init; } = null!;

    /// <summary>
    /// 分支守卫条件（可选）
    /// </summary>
    public HirNode? Guard { get; init; }

    /// <summary>
    /// 分支体
    /// </summary>
    public HirNode Body { get; init; } = null!;
}

/// <summary>
/// match 表达式
/// </summary>
public sealed record HirMatch : HirNode
{
    public HirMatch() : base(HirKind.Expr) { }

    /// <summary>
    /// 被匹配的表达式
    /// </summary>
    public HirNode Scrutinee { get; init; } = null!;

    /// <summary>
    /// 分支列表
    /// </summary>
    public List<HirMatchBranch> Branches { get; init; } = [];

    public bool IsExhaustive { get; init; }

    public override string ToString() => $"Match({Branches.Count} branches)";
}

#endregion

#region Lambda 表达式

/// <summary>
/// Lambda 捕获项
/// </summary>
public sealed record HirCapture
{
    /// <summary>
    /// 捕获的变量名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 捕获值类型
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// 是否是可变捕获
    /// </summary>
    public bool IsMutable { get; init; }
}

/// <summary>
/// Lambda 参数
/// </summary>
public sealed record HirParam
{
    /// <summary>
    /// 参数名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 参数类型
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// 符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// True when this parameter binding can be assigned inside the function body.
    /// </summary>
    public bool IsMutable { get; init; }
}

/// <summary>
/// Lambda 表达式
/// </summary>
public sealed record HirLambda : HirNode
{
    public HirLambda() : base(HirKind.Expr) { }

    /// <summary>
    /// 参数列表
    /// </summary>
    public List<HirParam> Parameters { get; init; } = [];

    /// <summary>
    /// 返回类型
    /// </summary>
    public TypeId ReturnType { get; init; } = TypeId.None;

    /// <summary>
    /// 函数体
    /// </summary>
    public HirNode Body { get; init; } = null!;

    /// <summary>
    /// 捕获列表
    /// </summary>
    public List<HirCapture> Captures { get; init; } = [];

    public override string ToString() => $"Lambda({Parameters.Count} params)";
}

#endregion

#region 代码块

/// <summary>
/// 代码块表达式
/// </summary>
public sealed record HirBlock : HirNode
{
    public HirBlock() : base(HirKind.Expr) { }

    /// <summary>
    /// 语句列表
    /// </summary>
    public List<HirStatement> Statements { get; init; } = [];

    /// <summary>
    /// 最终表达式（块的值）
    /// </summary>
    public HirNode? Result { get; init; }

    public override string ToString() => $"Block({Statements.Count} stmts)";
}

/// <summary>
/// 语句
/// </summary>
public abstract record HirStatement
{
    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }
}

/// <summary>
/// 声明语句
/// </summary>
public sealed record HirDeclStatement : HirStatement
{
    /// <summary>
    /// 声明
    /// </summary>
    public HirDecl Declaration { get; init; } = null!;
}

/// <summary>
/// 表达式语句
/// </summary>
public sealed record HirExprStatement : HirStatement
{
    /// <summary>
    /// 表达式
    /// </summary>
    public HirNode Expression { get; init; } = null!;
}

/// <summary>
/// 赋值语句
/// </summary>
public sealed record HirAssignStatement : HirStatement
{
    /// <summary>
    /// 目标（左值）
    /// </summary>
    public HirNode Target { get; init; } = null!;

    /// <summary>
    /// 值
    /// </summary>
    public HirNode Value { get; init; } = null!;
}

#endregion

#region 其他表达式

/// <summary>
/// 元组表达式
/// </summary>
public sealed record HirTuple : HirNode
{
    public HirTuple() : base(HirKind.Expr) { }

    /// <summary>
    /// 元素列表
    /// </summary>
    public List<HirNode> Elements { get; init; } = [];

    public override string ToString() => $"Tuple({Elements.Count} elems)";
}

/// <summary>
/// 列表表达式
/// </summary>
public sealed record HirList : HirNode
{
    public HirList() : base(HirKind.Expr) { }

    /// <summary>
    /// 元素列表
    /// </summary>
    public List<HirNode> Elements { get; init; } = [];

    /// <summary>
    /// 最后一个元素是否为 spread（..rest），需要展开/拼接
    /// </summary>
    public bool HasRest { get; init; }

    public override string ToString() => $"List({Elements.Count} elems{(HasRest ? ", +rest" : "")})";
}

/// <summary>
/// 列表推导式限定符类型
/// </summary>
public enum HirQualifierKind
{
    Generator,
    Guard
}

/// <summary>
/// 列表推导式限定符
/// </summary>
public sealed record HirQualifier
{
    /// <summary>
    /// 限定符类型
    /// </summary>
    public HirQualifierKind Kind { get; init; }

    /// <summary>
    /// 生成器模式（Kind=Generator 时有效）
    /// </summary>
    public HirPattern? GeneratorPattern { get; init; }

    /// <summary>
    /// 生成器来源表达式（Kind=Generator 时有效）
    /// </summary>
    public HirNode? GeneratorSource { get; init; }

    /// <summary>
    /// 守卫表达式（Kind=Guard 时有效）
    /// </summary>
    public HirNode? GuardExpression { get; init; }

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }
}

/// <summary>
/// 列表推导式表达式
/// </summary>
public sealed record HirListComprehension : HirNode
{
    public HirListComprehension() : base(HirKind.Expr) { }

    /// <summary>
    /// 输出表达式
    /// </summary>
    public HirNode Output { get; init; } = null!;

    /// <summary>
    /// 限定符列表
    /// </summary>
    public List<HirQualifier> Qualifiers { get; init; } = [];

    public override string ToString() => $"ListComprehension({Qualifiers.Count} qualifiers)";
}

/// <summary>
/// 字段访问表达式
/// </summary>
public sealed record HirFieldAccess : HirNode
{
    public HirFieldAccess() : base(HirKind.Expr) { }

    /// <summary>
    /// 目标表达式
    /// </summary>
    public HirNode Target { get; init; } = null!;

    /// <summary>
    /// 字段名
    /// </summary>
    public string FieldName { get; init; } = "";

    /// <summary>
    /// 字段符号 ID
    /// </summary>
    public SymbolId FieldSymbolId { get; init; } = SymbolId.None;

    public override string ToString() => $"FieldAccess({FieldName})";
}

/// <summary>
/// 索引访问表达式
/// </summary>
public enum HirIndexAccessKind
{
    Unknown,
    Aggregate,
    RuntimeArray
}

/// <summary>
/// 索引访问表达式
/// </summary>
public sealed record HirIndexAccess : HirNode
{
    public HirIndexAccess() : base(HirKind.Expr) { }

    /// <summary>
    /// 目标表达式
    /// </summary>
    public HirNode Target { get; init; } = null!;

    /// <summary>
    /// 索引表达式
    /// </summary>
    public HirNode Index { get; init; } = null!;

    /// <summary>
    /// 索引目标语义（用于 MIR list/aggregate 分派）
    /// </summary>
    public HirIndexAccessKind TargetKind { get; init; } = HirIndexAccessKind.Unknown;

    public override string ToString() => "IndexAccess(...)";
}

#endregion

#region 模式

/// <summary>
/// 模式抽象基类
/// </summary>
public abstract record HirPattern
{
    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// 模式类型
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;
}

/// <summary>
/// HIR error pattern produced by recovery/fallback lowering.
/// </summary>
public sealed record HirErrorPattern : HirPattern
{
    public string Reason { get; init; } = "";

    public bool IsRecovered { get; init; }

    public override string ToString() => string.IsNullOrWhiteSpace(Reason)
        ? "<hir-error-pattern>"
        : $"<hir-error-pattern: {Reason}>";
}

/// <summary>
/// 变量模式
/// </summary>
public sealed record HirVarPattern : HirPattern
{
    /// <summary>
    /// 变量名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 是否是通配符 (_)
    /// </summary>
    public bool IsWildcard { get; init; }

    /// <summary>
    /// 绑定模式（按值/借用）。
    /// </summary>
    public PatternBindingMode BindingMode { get; init; } = PatternBindingMode.ByValue;

    /// <summary>
    /// True when this by-value pattern binding is mutable.
    /// </summary>
    public bool IsMutableBinding { get; init; }

    public override string ToString()
    {
        if (IsWildcard)
        {
            return "_";
        }

        var prefix = BindingMode switch
        {
            PatternBindingMode.SharedBorrow => "ref ",
            PatternBindingMode.MutableBorrow => "mref ",
            _ => IsMutableBinding ? "mut " : string.Empty
        };

        return $"{prefix}{Name}";
    }
}

/// <summary>
/// 字面量模式
/// </summary>
public sealed record HirLiteralPattern : HirPattern
{
    /// <summary>
    /// 字面量值
    /// </summary>
    public object? Value { get; init; }

    public override string ToString() => Value?.ToString() ?? "null";
}

/// <summary>
/// 构造器模式
/// </summary>
public sealed record HirCtorPattern : HirPattern
{
    /// <summary>
    /// 构造器名
    /// </summary>
    public string ConstructorName { get; init; } = "";

    /// <summary>
    /// 构造器符号 ID
    /// </summary>
    public SymbolId ConstructorSymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 字段模式列表
    /// </summary>
    public List<HirFieldPattern> Fields { get; init; } = [];

    public override string ToString() => $"{ConstructorName}({Fields.Count} fields)";
}

/// <summary>
/// 字段模式
/// </summary>
public sealed record HirFieldPattern
{
    /// <summary>
    /// 字段名
    /// </summary>
    public string FieldName { get; init; } = "";

    /// <summary>
    /// 字段模式
    /// </summary>
    public HirPattern Pattern { get; init; } = null!;
}

/// <summary>
/// 元组模式
/// </summary>
public sealed record HirTuplePattern : HirPattern
{
    /// <summary>
    /// 元素模式列表
    /// </summary>
    public List<HirPattern> Elements { get; init; } = [];

    public override string ToString() => $"({string.Join(", ", Elements)})";
}

/// <summary>
/// 列表模式（[a, b, ..rest] / [head, ..middle, last]）
/// </summary>
public sealed record HirListPattern : HirPattern
{
    /// <summary>
    /// 前缀元素模式列表
    /// </summary>
    public List<HirPattern> Elements { get; init; } = [];

    /// <summary>
    /// 是否包含剩余模式标记（..）
    /// </summary>
    public bool HasRest { get; init; }

    /// <summary>
    /// 剩余模式（可选）
    /// </summary>
    public HirPattern? RestPattern { get; init; }

    /// <summary>
    /// 剩余模式后的后缀元素模式列表
    /// </summary>
    public List<HirPattern> SuffixElements { get; init; } = [];

    public override string ToString()
    {
        var prefix = string.Join(", ", Elements);
        var suffix = string.Join(", ", SuffixElements);
        if (!HasRest)
        {
            return $"[{prefix}]";
        }

        var restText = RestPattern == null ? ".." : $"..{RestPattern}";
        if (Elements.Count == 0 && SuffixElements.Count == 0)
        {
            return $"[{restText}]";
        }

        if (Elements.Count == 0)
        {
            return $"[{restText}, {suffix}]";
        }

        if (SuffixElements.Count == 0)
        {
            return $"[{prefix}, {restText}]";
        }

        return $"[{prefix}, {restText}, {suffix}]";
    }
}

/// <summary>
/// 或模式 (pattern1 | pattern2)
/// </summary>
public sealed record HirOrPattern : HirPattern
{
    /// <summary>
    /// 左模式
    /// </summary>
    public HirPattern Left { get; init; } = null!;

    /// <summary>
    /// 右模式
    /// </summary>
    public HirPattern Right { get; init; } = null!;

    public override string ToString() => $"{Left} | {Right}";
}

/// <summary>
/// 与模式 (pattern1 & pattern2)
/// </summary>
public sealed record HirAndPattern : HirPattern
{
    /// <summary>
    /// 左模式
    /// </summary>
    public HirPattern Left { get; init; } = null!;

    /// <summary>
    /// 右模式
    /// </summary>
    public HirPattern Right { get; init; } = null!;

    public override string ToString() => $"{Left} & {Right}";
}

/// <summary>
/// 否定模式 (!pattern)
/// </summary>
public sealed record HirNotPattern : HirPattern
{
    /// <summary>
    /// 被否定的内部模式
    /// </summary>
    public HirPattern InnerPattern { get; init; } = null!;

    public override string ToString() => $"!{InnerPattern}";
}

/// <summary>
/// 范围模式（start .. end）
/// </summary>
public sealed record HirRangePattern : HirPattern
{
    /// <summary>
    /// 起始边界（字面量）
    /// </summary>
    public HirLiteralPattern Start { get; init; } = new();

    /// <summary>
    /// 结束边界（字面量）
    /// </summary>
    public HirLiteralPattern End { get; init; } = new();

    public override string ToString() => $"{Start}..{End}";
}

/// <summary>
/// View 模式（viewExpr -> innerPattern）
/// </summary>
public sealed record HirViewPattern : HirPattern
{
    /// <summary>
    /// View 函数表达式
    /// </summary>
    public HirNode View { get; init; } = null!;

    /// <summary>
    /// View 调用结果类型（`view(scrutinee)` 的结果类型）。
    /// </summary>
    public TypeId ViewResultTypeId { get; init; } = TypeId.None;

    /// <summary>
    /// View 结果匹配模式
    /// </summary>
    public HirPattern InnerPattern { get; init; } = null!;

    public override string ToString() => $"({View} -> {InnerPattern})";
}

/// <summary>
/// As 模式 (pattern as name)
/// </summary>
public sealed record HirAsPattern : HirPattern
{
    /// <summary>
    /// 内部模式
    /// </summary>
    public HirPattern InnerPattern { get; init; } = null!;

    /// <summary>
    /// 绑定名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 绑定模式（按值/借用）。
    /// </summary>
    public PatternBindingMode BindingMode { get; init; } = PatternBindingMode.ByValue;

    public bool IsMutableBinding { get; init; }

    public override string ToString()
    {
        var bindingText = BindingMode switch
        {
            PatternBindingMode.SharedBorrow => $"ref {Name}",
            PatternBindingMode.MutableBorrow => $"mref {Name}",
            _ => IsMutableBinding ? $"mut {Name}" : Name
        };

        return $"{InnerPattern} as {bindingText}";
    }
}

#endregion

