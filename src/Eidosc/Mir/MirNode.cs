using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// MIR 节点基类 - 所有 MIR 节点的公共基类
/// </summary>
/// <remarks>
/// MirNode 是中间表示（MIR）中所有节点的基类。它提供了：
/// <list type="bullet">
///   <item><description>NodeId - 节点的唯一标识符</description></item>
///   <item><description>TypeId - 节点结果的类型信息</description></item>
///   <item><description>SourceSpan - 对应源代码的位置信息，用于错误报告和调试</description></item>
/// </list>
/// </remarks>
public abstract class MirNode
{
    /// <summary>
    /// 节点的唯一标识符
    /// </summary>
    /// <remarks>
    /// 每个节点在 MIR 中都有唯一的 ID，用于在优化和代码生成阶段引用节点。
    /// </remarks>
    public NodeId Id { get; init; } = NodeId.None;

    /// <summary>
    /// 节点结果的类型 ID
    /// </summary>
    /// <remarks>
    /// 表示此节点产生的值的类型。对于不产生值的节点（如存储指令），
    /// 此属性可能为 TypeId.None 或 Unit 类型。
    /// </remarks>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// 源代码位置信息
    /// </summary>
    /// <remarks>
    /// 用于错误报告、调试信息生成和源码映射。
    /// 如果节点是编译器生成的（无对应源码），则可能为空。
    /// </remarks>
    public SourceSpan Span { get; init; }
}

/// <summary>
/// MIR 节点唯一标识符
/// </summary>
/// <remarks>
/// 用于在 MIR 中唯一标识一个节点。节点 ID 在单个函数内是唯一的。
/// </remarks>
public readonly record struct NodeId
{
    /// <summary>
    /// 节点编号
    /// </summary>
    public int Value { get; init; }

    /// <summary>
    /// 无效/空节点 ID
    /// </summary>
    public static NodeId None => new() { Value = 0 };

    /// <summary>
    /// 检查是否是有效的节点 ID
    /// </summary>
    public bool IsValid => Value > 0;

    public override string ToString() => IsValid ? $"n{Value}" : "n<none>";
}
