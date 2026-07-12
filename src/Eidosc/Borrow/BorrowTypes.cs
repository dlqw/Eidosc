using Eidosc.Mir;
using Eidosc.Utils;

namespace Eidosc.Borrow;

/// <summary>
/// 生命周期变量
/// </summary>
public readonly record struct LifetimeId
{
    public int Value { get; init; }
    public static LifetimeId None => new() { Value = 0 };
    public bool IsValid => Value > 0;
    public override string ToString() => $"'{Value}";
}

/// <summary>
/// 生命周期
/// </summary>
public sealed class Lifetime
{
    public LifetimeId Id { get; init; }
    public string? Name { get; init; }
    public SourceSpan Span { get; init; }

    public override string ToString() => Name ?? $"'{Id.Value}";
}

/// <summary>
/// 生命周期约束（'a: 'b 表示 'a 至少和 'b 一样长）
/// </summary>
public sealed record LifetimeConstraint
{
    /// <summary>
    /// 子生命周期（必须活得够久）
    /// </summary>
    public LifetimeId Sub { get; init; }

    /// <summary>
    /// 父生命周期（作为下界）
    /// </summary>
    public LifetimeId Sup { get; init; }

    /// <summary>
    /// 约束来源位置
    /// </summary>
    public SourceSpan Span { get; init; }

    public override string ToString() => $"{Sub}: {Sup}";
}

/// <summary>
/// 借用信息
/// </summary>
public sealed class BorrowInfo
{
    /// <summary>
    /// 借用 ID
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// 借用者（借用发生的位置）
    /// </summary>
    public MirPlace Borrower { get; init; } = null!;

    /// <summary>
    /// 被借用的位置
    /// </summary>
    public MirPlace Borrowee { get; init; } = null!;

    /// <summary>
    /// 是否可变借用
    /// </summary>
    public bool IsMutable { get; init; }

    /// <summary>
    /// 借用生命周期
    /// </summary>
    public LifetimeId Lifetime { get; init; }

    /// <summary>
    /// 借用发生的位置
    /// </summary>
    public SourceSpan Span { get; init; }

    public override string ToString() => IsMutable ? $"&mut {Borrowee}" : $"&{Borrowee}";
}

/// <summary>
/// 移动信息
/// </summary>
public sealed class MoveInfo
{
    /// <summary>
    /// 移动的源位置
    /// </summary>
    public MirPlace Source { get; init; } = null!;

    /// <summary>
    /// 移动的目标位置
    /// </summary>
    public MirPlace Destination { get; init; } = null!;

    /// <summary>
    /// 移动发生的指令位置
    /// </summary>
    public int InstructionIndex { get; init; }

    /// <summary>
    /// 移动发生的基本块
    /// </summary>
    public BlockId BlockId { get; init; }

    /// <summary>
    /// 移动位置
    /// </summary>
    public SourceSpan Span { get; init; }
}

/// <summary>
/// 使用类型
/// </summary>
public enum UseKind
{
    /// <summary>
    /// 读取值（不消费）
    /// </summary>
    Read,

    /// <summary>
    /// 写入值（不消费）
    /// </summary>
    Write,

    /// <summary>
    /// 移动值（消费所有权）
    /// </summary>
    Move,

    /// <summary>
    /// 复制值（不消费，用于 Copy 类型）
    /// </summary>
    Copy,

    /// <summary>
    /// 借用值（不可变引用）
    /// </summary>
    BorrowShared,

    /// <summary>
    /// 借用值（可变引用）
    /// </summary>
    BorrowMutable
}

/// <summary>
/// 使用信息
/// </summary>
public sealed class UseInfo
{
    /// <summary>
    /// 使用的变量
    /// </summary>
    public LocalId Variable { get; init; }

    /// <summary>
    /// 使用类型
    /// </summary>
    public UseKind Kind { get; init; }

    /// <summary>
    /// 使用发生的基本块
    /// </summary>
    public BlockId BlockId { get; init; }

    /// <summary>
    /// 使用发生的指令索引
    /// </summary>
    public int InstructionIndex { get; init; }

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    public override string ToString() => $"{Variable}@{BlockId}:{InstructionIndex} ({Kind})";
}

/// <summary>
/// 活跃范围
/// </summary>
public sealed class LiveRange
{
    /// <summary>
    /// 变量
    /// </summary>
    public LocalId Variable { get; init; }

    /// <summary>
    /// 定义点（基本块，指令索引）
    /// </summary>
    public (BlockId Block, int Index) Definition { get; init; }

    /// <summary>
    /// 最后使用点列表
    /// </summary>
    public List<(BlockId Block, int Index)> LastUses { get; init; } = [];

    /// <summary>
    /// 活跃的基本块集合
    /// </summary>
    public IReadOnlySet<BlockId> LiveBlocks { get; init; } = CompactBlockIdSet.Empty;
}

internal sealed class CompactBlockIdSet : IReadOnlySet<BlockId>
{
    private readonly int[] _values;

    public static CompactBlockIdSet Empty { get; } = new(Array.Empty<int>());

    public int Count => _values.Length;

    public CompactBlockIdSet(IEnumerable<BlockId> values)
    {
        _values = Normalize(values.Select(value => value.Value));
    }

    public CompactBlockIdSet(IReadOnlyList<int> values)
    {
        _values = Normalize(values);
    }

    public bool Contains(BlockId item)
    {
        return Array.BinarySearch(_values, item.Value) >= 0;
    }

    public IEnumerator<BlockId> GetEnumerator()
    {
        foreach (var value in _values)
        {
            yield return new BlockId { Value = value };
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public bool IsProperSubsetOf(IEnumerable<BlockId> other) => ToHashSet().IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<BlockId> other) => ToHashSet().IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<BlockId> other) => ToHashSet().IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<BlockId> other) => ToHashSet().IsSupersetOf(other);
    public bool Overlaps(IEnumerable<BlockId> other) => ToHashSet().Overlaps(other);
    public bool SetEquals(IEnumerable<BlockId> other) => ToHashSet().SetEquals(other);

    private HashSet<BlockId> ToHashSet()
    {
        var result = new HashSet<BlockId>(_values.Length);
        foreach (var value in _values)
        {
            result.Add(new BlockId { Value = value });
        }

        return result;
    }

    private static int[] Normalize(IEnumerable<int> values)
    {
        return Normalize(values.ToArray());
    }

    private static int[] Normalize(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var normalized = new int[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            normalized[i] = values[i];
        }

        Array.Sort(normalized);

        int uniqueCount = 1;
        for (int i = 1; i < normalized.Length; i++)
        {
            if (normalized[i] != normalized[uniqueCount - 1])
            {
                normalized[uniqueCount++] = normalized[i];
            }
        }

        if (uniqueCount == normalized.Length)
        {
            return normalized;
        }

        Array.Resize(ref normalized, uniqueCount);
        return normalized;
    }
}

/// <summary>
/// 借用检查错误
/// </summary>
public enum BorrowErrorKind
{
    /// <summary>
    /// 移动后使用（use-after-move）
    /// </summary>
    UseAfterMove,

    /// <summary>
    /// 重复移动（double-move）
    /// </summary>
    DoubleMove,

    /// <summary>
    /// 部分移动后使用
    /// </summary>
    UseAfterPartialMove,

    /// <summary>
    /// 借用期间修改（cannot mutate while borrowed）
    /// </summary>
    MutateWhileBorrowed,

    /// <summary>
    /// 借用期间再次可变借用（cannot reborrow as mutable）
    /// </summary>
    ReborrowAsMutable,

    /// <summary>
    /// 借用生命周期不足
    /// </summary>
    LifetimeTooShort,

    /// <summary>
    /// 仿射类型重复使用
    /// </summary>
    AffineReuse,

    /// <summary>
    /// 多个可变借用
    /// </summary>
    MultipleMutableBorrows,

    /// <summary>
    /// 存在可变借用时创建不可变借用
    /// </summary>
    ImmutableWhileMutableBorrowed,

    /// <summary>
    /// 存在不可变借用时创建可变借用
    /// </summary>
    MutableWhileImmutableBorrowed,

    /// <summary>
    /// 生命周期过长
    /// </summary>
    LifetimeTooLong,

    /// <summary>
    /// 返回值仍被借用
    /// </summary>
    BorrowedWhileReturned,

    /// <summary>
    /// 读取能力不足
    /// </summary>
    ReadCapabilityDenied,

    /// <summary>
    /// 写入能力不足
    /// </summary>
    WriteCapabilityDenied,

    /// <summary>
    /// 移动能力不足
    /// </summary>
    MoveCapabilityDenied
}

/// <summary>
/// 借用检查诊断
/// </summary>
public sealed class BorrowDiagnostic
{
    public BorrowErrorKind Kind { get; init; }
    public string Message { get; init; } = "";
    public SourceSpan Span { get; init; }
    public SourceSpan? RelatedSpan { get; init; }
    public string? Hint { get; init; }
    public List<string> AliasTrace { get; init; } = [];
    public List<string> RelatedAliasTrace { get; init; } = [];
    public string? RelatedAliasTraceId { get; init; }

    /// <summary>
    /// 错误位置（块 ID 和指令索引）
    /// </summary>
    public (BlockId Block, int Index) Location { get; init; }

    /// <summary>
    /// 相关位置
    /// </summary>
    public (BlockId Block, int Index) RelatedLocation { get; init; }

    public override string ToString() => $"{Kind}: {Message}";
}

/// <summary>
/// Perceus 优化提示
/// </summary>
public sealed class PerceusHint
{
    /// <summary>
    /// 可以省略 dup 的位置
    /// </summary>
    public List<(BlockId Block, int Index)> OmitDup { get; init; } = [];

    /// <summary>
    /// 可以省略 drop 的位置
    /// </summary>
    public List<(BlockId Block, int Index)> OmitDrop { get; init; } = [];

    /// <summary>
    /// 可以提前释放的值
    /// </summary>
    public List<LocalId> EarlyDropCandidates { get; init; } = [];

    /// <summary>
    /// 最后使用点映射
    /// </summary>
    public Dictionary<LocalId, (BlockId Block, int Index)> LastUsePoints { get; init; } = new();
}
