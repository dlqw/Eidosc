namespace Eidosc.Mir;

/// <summary>
/// MIR 控制流节点集合
/// </summary>
/// <remarks>
/// 包含控制流相关的 MIR 节点，如分支、跳转、Phi 节点等。
/// </remarks>

/// <summary>
/// MIR Phi 节点 - SSA 形式的控制流汇合点
/// </summary>
/// <remarks>
/// MirPhi 用于在 SSA（静态单赋值）形式中表示控制流汇合点的值选择。
/// 在多个前驱基本块可能产生不同值的情况下，Phi 节点根据控制流来源
/// 选择相应的值。
/// <para>
/// 工作原理：
/// <list type="number">
///   <item><description>每个 Phi 节点有一组输入，每个输入关联一个前驱块</description></item>
///   <item><description>运行时，根据实际执行路径的前驱块选择对应的值</description></item>
///   <item><description>所有输入的类型必须相同</description></item>
/// </list>
/// </para>
/// <para>
/// 示例：
/// <code>
/// bb1:
///   %x1 = 1
///   goto bb3
/// bb2:
///   %x2 = 2
///   goto bb3
/// bb3:
///   %x = phi [%x1, bb1], [%x2, bb2]  ; %x 的值取决于从哪个块跳转过来
/// </code>
/// </para>
/// </remarks>
public sealed class MirPhi : MirNode
{
    /// <summary>
    /// Phi 输入列表
    /// </summary>
    /// <remarks>
    /// 每个输入包含一个值和对应的前驱基本块 ID。
    /// 前驱块 ID 用于在运行时确定选择哪个值。
    /// </remarks>
    public List<MirPhiInput> Inputs { get; init; } = [];

    /// <summary>
    /// 结果存储的目标位置
    /// </summary>
    public MirOperand Target { get; init; } = null!;

    /// <summary>
    /// Phi 节点的类型
    /// </summary>
    /// <remarks>
    /// 所有输入值的类型必须与此类型兼容。
    /// </remarks>
    public TypeId PhiType { get; init; } = TypeId.None;

    public override string ToString()
    {
        var inputs = string.Join(", ", Inputs.Select(i => $"[{i.Value}, bb{i.Predecessor.Value}]"));
        return $"{Target} = phi {inputs}";
    }
}

/// <summary>
/// Phi 节点输入
/// </summary>
/// <remarks>
/// 表示 Phi 节点的一个输入，包含值和对应的前驱基本块。
/// </remarks>
public sealed class MirPhiInput
{
    /// <summary>
    /// 输入值
    /// </summary>
    public MirOperand Value { get; init; } = null!;

    /// <summary>
    /// 前驱基本块 ID
    /// </summary>
    /// <remarks>
    /// 当控制流从这个块到达时，选择此输入的值。
    /// </remarks>
    public BlockId Predecessor { get; init; }

    public override string ToString() => $"{Value} from bb{Predecessor.Value}";
}

/// <summary>
/// MIR 条件分支节点 - 基于条件的二路分支
/// </summary>
/// <remarks>
/// MirBranch 表示基于布尔条件的二路分支。根据条件值，
/// 控制流转移到 Then 或 Else 块。
/// <para>
/// 这是比 MirSwitch 更简单的分支形式，适用于 if 表达式。
/// </para>
/// </remarks>
public sealed class MirBranch : MirNode
{
    /// <summary>
    /// 条件值（必须是布尔类型）
    /// </summary>
    public MirOperand Condition { get; init; } = null!;

    /// <summary>
    /// 条件为真时的目标块
    /// </summary>
    public BlockId ThenBlock { get; init; }

    /// <summary>
    /// 条件为假时的目标块
    /// </summary>
    public BlockId ElseBlock { get; init; }

    /// <summary>
    /// 分支提示（用于优化）
    /// </summary>
    public BranchHint Hint { get; init; }

    public override string ToString() => $"br {Condition} ? bb{ThenBlock.Value} : bb{ElseBlock.Value}";
}

/// <summary>
/// 分支提示
/// </summary>
/// <remarks>
/// 用于指导编译器优化，提示哪个分支更可能执行。
/// </remarks>
public enum BranchHint
{
    /// <summary>
    /// 无提示
    /// </summary>
    None,

    /// <summary>
    /// Then 分支更可能执行
    /// </summary>
    LikelyThen,

    /// <summary>
    /// Else 分支更可能执行
    /// </summary>
    LikelyElse,

    /// <summary>
    /// 分支概率相等
    /// </summary>
    Unlikely
}

/// <summary>
/// MIR 无条件跳转节点 - 直接跳转到目标块
/// </summary>
/// <remarks>
/// MirJump 表示无条件跳转到另一个基本块。
/// 这是 MirGoto 的别名，提供更清晰的语义。
/// </remarks>
public sealed class MirJump : MirNode
{
    /// <summary>
    /// 目标基本块
    /// </summary>
    public BlockId Target { get; init; }

    public override string ToString() => $"jump bb{Target.Value}";
}

/// <summary>
/// MIR 返回节点 - 从函数返回
/// </summary>
/// <remarks>
/// MirReturn 结束函数执行并返回到调用者。
/// 如果函数有返回值，则包含返回值；否则返回 Unit。
/// </remarks>
public sealed class MirReturnNode : MirNode
{
    /// <summary>
    /// 返回值（可选）
    /// </summary>
    /// <remarks>
    /// 如果函数返回 Unit，此值为 null。
    /// </remarks>
    public MirOperand? Value { get; init; }

    /// <summary>
    /// 返回类型
    /// </summary>
    public TypeId ReturnType { get; init; } = TypeId.None;

    public override string ToString() => Value != null ? $"return {Value}" : WellKnownStrings.Keywords.Return;
}

/// <summary>
/// MIR 类型转换节点 - 在不同类型之间转换
/// </summary>
/// <remarks>
/// MirCast 用于在不同类型之间进行转换。转换类型由 CastKind 指定。
/// <para>
/// 支持的转换类型：
/// <list type="bullet">
///   <item><description>整数扩展/截断</description></item>
///   <item><description>浮点转换</description></item>
///   <item><description>整数与浮点之间的转换</description></item>
///   <item><description>指针转换</description></item>
///   <item><description>枚举/ADT 转换</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class MirCast : MirNode
{
    /// <summary>
    /// 源操作数
    /// </summary>
    public MirOperand Source { get; init; } = null!;

    /// <summary>
    /// 源类型
    /// </summary>
    public TypeId SourceType { get; init; } = TypeId.None;

    /// <summary>
    /// 目标类型
    /// </summary>
    public TypeId TargetType { get; init; } = TypeId.None;

    /// <summary>
    /// 转换类型
    /// </summary>
    public CastKind Kind { get; init; }

    /// <summary>
    /// 结果存储位置
    /// </summary>
    public MirOperand Target { get; init; } = null!;

    public override string ToString() => $"{Target} = cast<{Kind}> {Source} : {SourceType} -> {TargetType}";
}

/// <summary>
/// 类型转换类型
/// </summary>
/// <remarks>
/// 定义了 MIR 中支持的所有类型转换方式。
/// </remarks>
public enum CastKind
{
    #region 整数转换

    /// <summary>
    /// 零扩展 - 无符号整数扩展到更大的类型
    /// </summary>
    /// <remarks>
    /// 高位填充零。例如：i8 -> i32 (0xFF -> 0x000000FF)
    /// </remarks>
    ZeroExtend,

    /// <summary>
    /// 符号扩展 - 有符号整数扩展到更大的类型
    /// </summary>
    /// <remarks>
    /// 高位填充符号位。例如：i8 -> i32 (0xFF -> 0xFFFFFFFF)
    /// </remarks>
    SignExtend,

    /// <summary>
    /// 截断 - 整数缩小到更小的类型
    /// </summary>
    /// <remarks>
    /// 丢弃高位。例如：i32 -> i8 (0x12345678 -> 0x78)
    /// </remarks>
    Truncate,

    #endregion

    #region 浮点转换

    /// <summary>
    /// 浮点扩展 - 浮点数扩展到更大的类型
    /// </summary>
    /// <remarks>
    /// 例如：f32 -> f64
    /// </remarks>
    FloatExtend,

    /// <summary>
    /// 浮点截断 - 浮点数缩小到更小的类型
    /// </summary>
    /// <remarks>
    /// 例如：f64 -> f32
    /// </remarks>
    FloatTruncate,

    #endregion

    #region 整数与浮点转换

    /// <summary>
    /// 整数转浮点 - 将整数转换为浮点数
    /// </summary>
    /// <remarks>
    /// 例如：i32 -> f32
    /// </remarks>
    IntToFloat,

    /// <summary>
    /// 浮点转整数（向零取整）- 将浮点数转换为整数
    /// </summary>
    /// <remarks>
    /// 例如：f32 -> i32 (1.9 -> 1, -1.9 -> -1)
    /// </remarks>
    FloatToInt,

    /// <summary>
    /// 浮点转无符号整数 - 将浮点数转换为无符号整数
    /// </summary>
    FloatToUInt,

    /// <summary>
    /// 无符号整数转浮点 - 将无符号整数转换为浮点数
    /// </summary>
    UIntToFloat,

    #endregion

    #region 指针转换

    /// <summary>
    /// 指针转整数 - 将指针转换为整数
    /// </summary>
    PtrToInt,

    /// <summary>
    /// 整数转指针 - 将整数转换为指针
    /// </summary>
    IntToPtr,

    /// <summary>
    /// 指针类型转换 - 在不同指针类型之间转换
    /// </summary>
    BitCast,

    #endregion

    #region ADT/枚举转换

    /// <summary>
    /// 枚举转整数 - 获取枚举的标记值
    /// </summary>
    EnumToInt,

    /// <summary>
    /// ADT 上转型 - 将 ADT 变体转换为其基础类型
    /// </summary>
    Upcast,

    /// <summary>
    /// ADT 下转型 - 将基础类型转换为特定变体（需运行时检查）
    /// </summary>
    Downcast,

    #endregion
}

/// <summary>
/// CastKind 扩展方法
/// </summary>
public static class CastKindExtensions
{
    /// <summary>
    /// 检查是否是安全的转换（不丢失信息）
    /// </summary>
    /// <param name="kind">转换类型</param>
    /// <returns>如果是安全转换返回 true</returns>
    public static bool IsSafeCast(this CastKind kind) => kind switch
    {
        CastKind.ZeroExtend => true,
        CastKind.SignExtend => true,
        CastKind.FloatExtend => true,
        CastKind.IntToFloat => false, // 可能丢失精度
        CastKind.FloatToInt => false, // 可能溢出
        _ => false
    };

    /// <summary>
    /// 获取转换的简短名称
    /// </summary>
    /// <param name="kind">转换类型</param>
    /// <returns>简短名称</returns>
    public static string ToShortName(this CastKind kind) => kind switch
    {
        CastKind.ZeroExtend => "zext",
        CastKind.SignExtend => "sext",
        CastKind.Truncate => "trunc",
        CastKind.FloatExtend => "fpext",
        CastKind.FloatTruncate => "fptrunc",
        CastKind.IntToFloat => "sitofp",
        CastKind.FloatToInt => "fptosi",
        CastKind.FloatToUInt => "fptoui",
        CastKind.UIntToFloat => "uitofp",
        CastKind.PtrToInt => "ptrtoint",
        CastKind.IntToPtr => "inttoptr",
        CastKind.BitCast => "bitcast",
        CastKind.EnumToInt => "enum2int",
        CastKind.Upcast => "upcast",
        CastKind.Downcast => "downcast",
        _ => "unknown"
    };
}
