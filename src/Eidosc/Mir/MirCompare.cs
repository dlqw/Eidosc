namespace Eidosc.Mir;

/// <summary>
/// MIR 比较操作节点 - 表示两个值的比较操作
/// </summary>
/// <remarks>
/// MirCompare 用于表示各种比较操作，包括整数比较、浮点数比较和引用比较。
/// 比较结果总是一个布尔值。
/// <para>
/// 支持的比较类型：
/// <list type="bullet">
///   <item><description>相等性比较（==, !=）</description></item>
///   <item><description>有序比较（&lt;, &lt;=, &gt;, &gt;=）</description></item>
///   <item><description>浮点特殊比较（isNaN, isInfinite）</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class MirCompare : MirNode
{
    /// <summary>
    /// 比较操作符
    /// </summary>
    public CompareOp Operator { get; init; }

    /// <summary>
    /// 左操作数
    /// </summary>
    public MirOperand Left { get; init; } = null!;

    /// <summary>
    /// 右操作数
    /// </summary>
    public MirOperand Right { get; init; } = null!;

    /// <summary>
    /// 结果存储的目标位置
    /// </summary>
    /// <remarks>
    /// 比较结果（布尔值）将存储在此位置。
    /// </remarks>
    public MirOperand Target { get; init; } = null!;

    public override string ToString() => $"{Target} = cmp {Operator.ToSymbol()}({Left}, {Right})";
}

/// <summary>
/// 比较操作符
/// </summary>
/// <remarks>
/// 定义了 MIR 中支持的所有比较操作。
/// </remarks>
public enum CompareOp
{
    #region 相等性比较

    /// <summary>
    /// 相等（==）
    /// </summary>
    Eq,

    /// <summary>
    /// 不相等（!=）
    /// </summary>
    Ne,

    #endregion

    #region 有序比较（整数和浮点）

    /// <summary>
    /// 小于（&lt;）
    /// </summary>
    Lt,

    /// <summary>
    /// 小于等于（&lt;=）
    /// </summary>
    Le,

    /// <summary>
    /// 大于（&gt;）
    /// </summary>
    Gt,

    /// <summary>
    /// 大于等于（&gt;=）
    /// </summary>
    Ge,

    #endregion

    #region 浮点特殊比较

    /// <summary>
    /// 有序比较（两个操作数都不是 NaN）
    /// </summary>
    /// <remarks>
    /// 如果两个操作数都不是 NaN，返回 true。
    /// </remarks>
    Ordered,

    /// <summary>
    /// 无序比较（至少一个操作数是 NaN）
    /// </summary>
    /// <remarks>
    /// 如果至少一个操作数是 NaN，返回 true。
    /// </remarks>
    Unordered,

    #endregion

    #region 引用比较

    /// <summary>
    /// 引用相等（同一对象）
    /// </summary>
    RefEq,

    /// <summary>
    /// 类型相等（同一类型）
    /// </summary>
    TypeEq,

    #endregion
}

/// <summary>
/// CompareOp 扩展方法
/// </summary>
public static class CompareOpExtensions
{
    /// <summary>
    /// 获取比较操作符的符号表示
    /// </summary>
    /// <param name="op">比较操作符</param>
    /// <returns>符号字符串</returns>
    public static string ToSymbol(this CompareOp op) => op switch
    {
        CompareOp.Eq => WellKnownStrings.Operators.Equal,
        CompareOp.Ne => WellKnownStrings.Operators.NotEqual,
        CompareOp.Lt => WellKnownStrings.Operators.Less,
        CompareOp.Le => WellKnownStrings.Operators.LessEqual,
        CompareOp.Gt => WellKnownStrings.Operators.Greater,
        CompareOp.Ge => WellKnownStrings.Operators.GreaterEqual,
        CompareOp.Ordered => "ord",
        CompareOp.Unordered => "unord",
        CompareOp.RefEq => "ref_eq",
        CompareOp.TypeEq => "type_eq",
        _ => "?"
    };

    /// <summary>
    /// 检查是否是有序比较操作符
    /// </summary>
    /// <param name="op">比较操作符</param>
    /// <returns>如果是有序比较返回 true</returns>
    public static bool IsOrderedComparison(this CompareOp op) =>
        op is CompareOp.Lt or CompareOp.Le or CompareOp.Gt or CompareOp.Ge;

    /// <summary>
    /// 检查是否是相等性比较操作符
    /// </summary>
    /// <param name="op">比较操作符</param>
    /// <returns>如果是相等性比较返回 true</returns>
    public static bool IsEqualityComparison(this CompareOp op) =>
        op is CompareOp.Eq or CompareOp.Ne;

    /// <summary>
    /// 获取相反的比较操作符
    /// </summary>
    /// <param name="op">比较操作符</param>
    /// <returns>相反的操作符</returns>
    public static CompareOp Negate(this CompareOp op) => op switch
    {
        CompareOp.Eq => CompareOp.Ne,
        CompareOp.Ne => CompareOp.Eq,
        CompareOp.Lt => CompareOp.Ge,
        CompareOp.Le => CompareOp.Gt,
        CompareOp.Gt => CompareOp.Le,
        CompareOp.Ge => CompareOp.Lt,
        CompareOp.Ordered => CompareOp.Unordered,
        CompareOp.Unordered => CompareOp.Ordered,
        _ => op
    };
}
