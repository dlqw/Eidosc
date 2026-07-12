using Eidosc.Mir;

namespace Eidosc.Borrow;

/// <summary>
/// 可提升分配的类型：ADT 构造器或闭包。
/// </summary>
public enum PromotableAllocationKind
{
    AdtConstructor,
    Closure
}

/// <summary>
/// 统一栈分配信息（ADT 构造器 + 闭包共用）。
/// </summary>
public sealed record UnifiedStackAllocInfo(
    PromotableAllocationKind Kind,
    (BlockId Block, int Index) Site,
    LocalId TargetLocal,
    List<int> RcFieldIndices)
{
    // ADT-specific
    public int TypeId { get; init; }
    public int FieldCount { get; init; }
    public long PayloadSize { get; init; }

    // Closure-specific
    public string? InvokeFunctionName { get; init; }
    public string? ReleaseFunctionName { get; init; }
    public List<TypeId>? CapturedTypeIds { get; init; }
}

/// <summary>
/// 统一栈提升提示。
/// </summary>
public sealed class UnifiedStackPromotionHints
{
    public Dictionary<LocalId, UnifiedStackAllocInfo> AllocInfoByLocal { get; } = [];
    public HashSet<LocalId> PromotedLocals { get; } = [];
}
