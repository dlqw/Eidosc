namespace Eidosc.Mir;

/// <summary>
/// MIR 变量引用节点 - 表示对局部变量或参数的引用
/// </summary>
/// <remarks>
/// MirVariable 用于表示在 MIR 中对变量的引用。与 MirPlace 不同，
/// MirVariable 是一个更简单的表示形式，专门用于引用已声明的变量。
/// <para>
/// 使用场景：
/// <list type="bullet">
///   <item><description>引用函数参数</description></item>
///   <item><description>引用局部变量</description></item>
///   <item><description>引用闭包捕获的变量</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class MirVariable : MirNode
{
    /// <summary>
    /// 变量名（用于调试和错误报告）
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 局部变量 ID
    /// </summary>
    /// <remarks>
    /// 指向函数局部变量列表中的变量定义。
    /// </remarks>
    public LocalId LocalId { get; init; } = LocalId.None;

    /// <summary>
    /// 是否是可变变量
    /// </summary>
    public bool IsMutable { get; init; }

    /// <summary>
    /// 是否是函数参数
    /// </summary>
    public bool IsParameter { get; init; }

    /// <summary>
    /// 变量的借用状态
    /// </summary>
    /// <remarks>
    /// 用于借用检查器跟踪变量的当前借用状态。
    /// </remarks>
    public BorrowState BorrowState { get; init; } = BorrowState.None;

    public override string ToString() => $"${Name}:%{LocalId.Value}";
}

/// <summary>
/// 借用状态
/// </summary>
/// <remarks>
/// 跟踪变量在借用检查期间的当前状态。
/// </remarks>
public enum BorrowState
{
    /// <summary>
    /// 无借用 - 变量可以被移动或借用
    /// </summary>
    None,

    /// <summary>
    /// 已共享借用 - 存在一个或多个不可变引用
    /// </summary>
    /// <remarks>
    /// 在此状态下，变量可以被读取但不能被修改或移动。
    /// </remarks>
    SharedBorrow,

    /// <summary>
    /// 已可变借用 - 存在一个可变引用
    /// </summary>
    /// <remarks>
    /// 在此状态下，变量只能通过可变引用访问。
    /// </remarks>
    MutableBorrow,

    /// <summary>
    /// 已移动 - 变量的所有权已转移
    /// </summary>
    /// <remarks>
    /// 在此状态下，变量不能再被使用。
    /// </remarks>
    Moved
}
