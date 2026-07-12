namespace Eidosc.Mir;

/// <summary>
/// MIR 内存操作节点集合
/// </summary>
/// <remarks>
/// 包含内存分配、访问和操作相关的 MIR 节点。
/// </remarks>

/// <summary>
/// MIR 获取元素指针节点 - 用于计算结构体字段或数组元素的地址
/// </summary>
/// <remarks>
/// MirGetElementPtr (GEP) 用于计算复合类型（结构体、数组、元组）中
/// 某个元素的地址，而不实际加载该元素的值。
/// <para>
/// 使用场景：
/// <list type="bullet">
///   <item><description>访问结构体字段</description></item>
///   <item><description>访问数组元素</description></item>
///   <item><description>访问元组元素</description></item>
///   <item><description>多层嵌套访问</description></item>
/// </list>
/// </para>
/// <para>
/// 注意：GEP 不实际访问内存，只计算地址。要获取值，需要配合 Load 指令使用。
/// </para>
/// </remarks>
public sealed class MirGetElementPtr : MirNode
{
    /// <summary>
    /// 基础指针（指向结构体、数组或元组）
    /// </summary>
    public MirOperand Base { get; init; } = null!;

    /// <summary>
    /// 索引路径
    /// </summary>
    /// <remarks>
    /// 用于定位目标元素的索引序列。例如：
    /// <list type="bullet">
    ///   <item><description>访问结构体字段：[字段索引]</description></item>
    ///   <item><description>访问数组元素：[元素索引]</description></item>
    ///   <item><description>访问嵌套结构：[外层索引, 内层索引]</description></item>
    /// </list>
    /// </remarks>
    public List<MirOperand> Indices { get; init; } = [];

    /// <summary>
    /// 源类型（基础指针指向的类型）
    /// </summary>
    public TypeId SourceType { get; init; } = TypeId.None;

    /// <summary>
    /// 结果元素类型
    /// </summary>
    public TypeId ElementType { get; init; } = TypeId.None;

    /// <summary>
    /// 是否是可变访问
    /// </summary>
    /// <remarks>
    /// 如果为 true，则通过此指针可以进行修改操作。
    /// </remarks>
    public bool IsMutable { get; init; }

    public override string ToString()
    {
        var indices = string.Join(", ", Indices.Select(i => i.ToString()));
        return $"gep {Base}, [{indices}]";
    }
}

/// <summary>
/// MIR 内存分配节点 - 在堆或栈上分配内存
/// </summary>
/// <remarks>
/// MirMemoryAlloc 用于在运行时分配内存。分配的位置（堆或栈）
/// 由 AllocationKind 决定。
/// </remarks>
public sealed class MirMemoryAlloc : MirNode
{
    /// <summary>
    /// 分配类型
    /// </summary>
    public AllocationKind Kind { get; init; }

    /// <summary>
    /// 要分配的类型
    /// </summary>
    public TypeId AllocatedType { get; init; } = TypeId.None;

    /// <summary>
    /// 分配的元素数量（用于数组分配）
    /// </summary>
    /// <remarks>
    /// 对于单元素分配，此值为 null 或常量 1。
    /// </remarks>
    public MirOperand? Count { get; init; }

    /// <summary>
    /// 对齐要求（字节）
    /// </summary>
    /// <remarks>
    /// 如果为 null，使用类型的自然对齐。
    /// </remarks>
    public int? Alignment { get; init; }

    public override string ToString()
    {
        var countStr = Count != null ? $", {Count}" : "";
        return $"alloc {Kind} {AllocatedType}{countStr}";
    }
}

/// <summary>
/// 内存分配类型
/// </summary>
public enum AllocationKind
{
    /// <summary>
    /// 栈分配 - 在当前函数的栈帧上分配
    /// </summary>
    /// <remarks>
    /// 栈分配的内存在函数返回时自动释放。
    /// </remarks>
    Stack,

    /// <summary>
    /// 堆分配 - 在堆上分配
    /// </summary>
    /// <remarks>
    /// 堆分配的内存需要显式释放或由 GC 管理。
    /// </remarks>
    Heap
}

/// <summary>
/// MIR 内存加载节点 - 从内存地址加载值
/// </summary>
/// <remarks>
/// MirMemoryLoad 从给定地址加载一个值。地址通常由 MirGetElementPtr
/// 或 MirMemoryAlloc 产生。
/// </remarks>
public sealed class MirMemoryLoad : MirNode
{
    /// <summary>
    /// 源地址
    /// </summary>
    public MirOperand Address { get; init; } = null!;

    /// <summary>
    /// 要加载的类型
    /// </summary>
    public TypeId LoadType { get; init; } = TypeId.None;

    /// <summary>
    /// 是否是易失加载
    /// </summary>
    /// <remarks>
    /// 易失加载不会被编译器优化掉或重排序。
    /// </remarks>
    public bool IsVolatile { get; init; }

    /// <summary>
    /// 对齐要求（字节）
    /// </summary>
    public int? Alignment { get; init; }

    public override string ToString() => $"load {LoadType} from {Address}";
}

/// <summary>
/// MIR 内存存储节点 - 将值存储到内存地址
/// </summary>
/// <remarks>
/// MirMemoryStore 将一个值存储到给定的内存地址。
/// </remarks>
public sealed class MirMemoryStore : MirNode
{
    /// <summary>
    /// 目标地址
    /// </summary>
    public MirOperand Address { get; init; } = null!;

    /// <summary>
    /// 要存储的值
    /// </summary>
    public MirOperand Value { get; init; } = null!;

    /// <summary>
    /// 存储的类型
    /// </summary>
    public TypeId StoreType { get; init; } = TypeId.None;

    /// <summary>
    /// 是否是易失存储
    /// </summary>
    /// <remarks>
    /// 易失存储不会被编译器优化掉或重排序。
    /// </remarks>
    public bool IsVolatile { get; init; }

    /// <summary>
    /// 对齐要求（字节）
    /// </summary>
    public int? Alignment { get; init; }

    public override string ToString() => $"store {Value} to {Address}";
}

/// <summary>
/// MIR 内存复制节点 - 批量内存复制
/// </summary>
/// <remarks>
/// MirMemoryCopy 用于批量复制内存块，通常用于大对象或数组的复制。
/// </remarks>
public sealed class MirMemoryCopy : MirNode
{
    /// <summary>
    /// 目标地址
    /// </summary>
    public MirOperand Destination { get; init; } = null!;

    /// <summary>
    /// 源地址
    /// </summary>
    public MirOperand Source { get; init; } = null!;

    /// <summary>
    /// 复制大小（字节）
    /// </summary>
    public MirOperand Size { get; init; } = null!;

    /// <summary>
    /// 是否是易失复制
    /// </summary>
    public bool IsVolatile { get; init; }

    public override string ToString() => $"memcpy {Destination}, {Source}, {Size}";
}
