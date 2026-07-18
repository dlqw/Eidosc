using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// MIR 指令基类
/// </summary>
public abstract record MirInstruction
{
    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }
}

/// <summary>
/// 赋值指令（目标 + 源操作数）
/// </summary>
public sealed record MirAssign : MirInstruction
{
    /// <summary>
    /// 目标位置
    /// </summary>
    public MirPlace Target { get; init; } = null!;

    /// <summary>
    /// 源操作数
    /// </summary>
    public MirOperand Source { get; init; } = null!;

    public override string ToString() => $"{Target} = {Source}";
}

public sealed record MirCaseInject : MirInstruction
{
    public MirOperand Target { get; init; } = null!;
    public MirOperand Operand { get; init; } = null!;
    public SymbolId SourceCase { get; init; } = SymbolId.None;
    public SymbolId TargetAncestor { get; init; } = SymbolId.None;
    public TypeId SourceTypeId { get; init; } = TypeId.None;
    public TypeId TargetTypeId { get; init; } = TypeId.None;

    public override string ToString() => $"{Target} = case_inject {SourceTypeId} -> {TargetTypeId} {Operand}";
}

/// <summary>
/// 函数调用（目标 + 函数 + 参数）
/// </summary>
public sealed record MirCall : MirInstruction
{
    /// <summary>
    /// 目标位置（存储返回值）
    /// </summary>
    public MirPlace? Target { get; init; }

    /// <summary>
    /// 被调用的函数
    /// </summary>
    public MirOperand Function { get; init; } = null!;

    /// <summary>
    /// 参数列表
    /// </summary>
    public List<MirOperand> Arguments { get; init; } = [];

    /// <summary>
    /// 是否为尾调用（由 TailCallOptimization pass 设置）。
    /// 自递归尾调用会被转换为循环；此标记用于非自递归尾调用，
    /// 提示 LLVM codegen 发射 tail call 指令。
    /// </summary>
    public bool IsTailCall { get; init; }

    public override string ToString()
    {
        var args = string.Join(", ", Arguments.Select(a => a.ToString()));
        var targetStr = Target != null ? $"{Target} = " : "";
        var tailStr = IsTailCall ? "tail " : "";
        return $"{targetStr}{tailStr}call {Function}({args})";
    }
}

/// <summary>
/// 二元运算（目标 + 左操作数 + 右操作数 + 运算符）
/// </summary>
public sealed record MirBinOp : MirInstruction
{
    /// <summary>
    /// 目标位置（可以是 MirPlace 或 MirTemp）
    /// </summary>
    public MirOperand Target { get; init; } = null!;

    /// <summary>
    /// 运算符
    /// </summary>
    public BinaryOp Operator { get; init; }

    /// <summary>
    /// 左操作数
    /// </summary>
    public MirOperand Left { get; init; } = null!;

    /// <summary>
    /// 右操作数
    /// </summary>
    public MirOperand Right { get; init; } = null!;

    public override string ToString() => $"{Target} = {Left} {Operator.ToSymbol()} {Right}";
}

/// <summary>
/// 一元运算
/// </summary>
public sealed record MirUnaryOp : MirInstruction
{
    /// <summary>
    /// 目标位置（可以是 MirPlace 或 MirTemp）
    /// </summary>
    public MirOperand Target { get; init; } = null!;

    /// <summary>
    /// 运算符
    /// </summary>
    public UnaryOp Operator { get; init; }

    /// <summary>
    /// 操作数
    /// </summary>
    public MirOperand Operand { get; init; } = null!;

    public override string ToString() => $"{Target} = {Operator.ToSymbol()}{Operand}";
}

/// <summary>
/// 加载值（目标 + 常量/变量）
/// </summary>
public sealed record MirLoad : MirInstruction
{
    /// <summary>
    /// 目标位置
    /// </summary>
    public MirPlace Target { get; init; } = null!;

    /// <summary>
    /// 源操作数
    /// </summary>
    public MirOperand Source { get; init; } = null!;

    /// <summary>
    /// 是否为可变借用加载（用于模式绑定/借用语义）。
    /// </summary>
    public bool IsMutableBorrow { get; init; }

    /// <summary>
    /// 是否在 borrow/loan 语义上创建新的借用别名。
    /// 按值 materialize 读取会关闭该标记，只保留真实借用绑定/传播路径。
    /// </summary>
    public bool CreatesBorrowAlias { get; init; } = true;

    public override string ToString() => IsMutableBorrow
        ? $"{Target} = load_mut {Source}"
        : $"{Target} = load {Source}";
}

/// <summary>
/// 存储值（目标地址 + 值）
/// </summary>
public sealed record MirStore : MirInstruction
{
    /// <summary>
    /// 目标地址
    /// </summary>
    public MirPlace Target { get; init; } = null!;

    /// <summary>
    /// 要存储的值
    /// </summary>
    public MirOperand Value { get; init; } = null!;

    public override string ToString() => $"store {Target}, {Value}";
}

/// <summary>
/// 丢弃值（用于 Perceus RC）
/// </summary>
public sealed record MirDrop : MirInstruction
{
    /// <summary>
    /// 要丢弃的值
    /// </summary>
    public MirOperand Value { get; init; } = null!;

    public override string ToString() => $"drop {Value}";
}

/// <summary>
/// 复制值（非仿射类型）
/// </summary>
public sealed record MirCopy : MirInstruction
{
    /// <summary>
    /// 目标位置
    /// </summary>
    public MirPlace Target { get; init; } = null!;

    /// <summary>
    /// 源位置
    /// </summary>
    public MirPlace Source { get; init; } = null!;

    public override string ToString() => $"{Target} = copy {Source}";
}

/// <summary>
/// 移动值（仿射类型）
/// </summary>
public sealed record MirMove : MirInstruction
{
    /// <summary>
    /// 目标位置
    /// </summary>
    public MirPlace Target { get; init; } = null!;

    /// <summary>
    /// 源位置
    /// </summary>
    public MirPlace Source { get; init; } = null!;

    public override string ToString() => $"{Target} = move {Source}";
}

/// <summary>
/// 分配局部变量
/// </summary>
public sealed record MirAlloc : MirInstruction
{
    /// <summary>
    /// 目标位置
    /// </summary>
    public MirPlace Target { get; init; } = null!;

    /// <summary>
    /// 分配的类型
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    public override string ToString() => $"{Target} = alloc";
}

/// <summary>
/// 二元运算符
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
/// 一元运算符
/// </summary>
public enum UnaryOp
{
    Neg,
    Not
}

/// <summary>
/// BinaryOp 扩展方法
/// </summary>
public static class BinaryOpExtensions
{
    public static string ToSymbol(this BinaryOp op) => op switch
    {
        BinaryOp.Add => WellKnownStrings.Operators.Add,
        BinaryOp.Sub => WellKnownStrings.Operators.Subtract,
        BinaryOp.Mul => WellKnownStrings.Operators.Multiply,
        BinaryOp.Div => WellKnownStrings.Operators.Divide,
        BinaryOp.Mod => WellKnownStrings.Operators.Modulo,
        BinaryOp.Eq => WellKnownStrings.Operators.Equal,
        BinaryOp.Ne => WellKnownStrings.Operators.NotEqual,
        BinaryOp.Lt => WellKnownStrings.Operators.Less,
        BinaryOp.Le => WellKnownStrings.Operators.LessEqual,
        BinaryOp.Gt => WellKnownStrings.Operators.Greater,
        BinaryOp.Ge => WellKnownStrings.Operators.GreaterEqual,
        BinaryOp.And => WellKnownStrings.Operators.And,
        BinaryOp.Or => WellKnownStrings.Operators.Or,
        BinaryOp.Concat => WellKnownStrings.Operators.Concat,
        _ => "?"
    };
}

/// <summary>
/// UnaryOp 扩展方法
/// </summary>
public static class UnaryOpExtensions
{
    public static string ToSymbol(this UnaryOp op) => op switch
    {
        UnaryOp.Neg => WellKnownStrings.Operators.Subtract,
        UnaryOp.Not => WellKnownStrings.Operators.Not,
        _ => "?"
    };
}
