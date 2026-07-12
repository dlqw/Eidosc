using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// MIR 终止符 - 基本块的结束指令
/// </summary>
public abstract record MirTerminator
{
    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }
}

/// <summary>
/// 返回终止符
/// </summary>
public sealed record MirReturn : MirTerminator
{
    /// <summary>
    /// 返回值（可选，Unit 返回为 null）
    /// </summary>
    public MirOperand? Value { get; init; }

    public override string ToString() => Value != null ? $"return {Value}" : WellKnownStrings.Keywords.Return;
}

/// <summary>
/// 无条件跳转
/// </summary>
public sealed record MirGoto : MirTerminator
{
    /// <summary>
    /// 目标基本块 ID
    /// </summary>
    public BlockId Target { get; init; }

    public override string ToString() => $"goto bb{Target.Value}";
}

/// <summary>
/// 条件分支（用于 match/if）
/// </summary>
public sealed record MirSwitch : MirTerminator
{
    /// <summary>
    /// 被检查的值
    /// </summary>
    public MirOperand Discriminant { get; init; } = null!;

    /// <summary>
    /// 分支列表（值 -> 目标块）
    /// </summary>
    public List<MirSwitchBranch> Branches { get; init; } = [];

    /// <summary>
    /// 默认分支（else 分支）
    /// </summary>
    public BlockId? DefaultTarget { get; init; }

    public override string ToString()
    {
        var branches = string.Join(", ", Branches.Select(b => $"{b.Value} => bb{b.Target.Value}"));
        var defaultStr = DefaultTarget != null ? $", _ => bb{DefaultTarget.Value.Value}" : "";
        return $"switch {Discriminant} [{branches}{defaultStr}]";
    }
}

/// <summary>
/// Switch 分支
/// </summary>
public sealed record MirSwitchBranch
{
    /// <summary>
    /// 匹配值（常量或构造器）
    /// </summary>
    public MirConstant Value { get; init; } = null!;

    /// <summary>
    /// 目标基本块
    /// </summary>
    public BlockId Target { get; init; }

    /// <summary>
    /// 绑定的变量（用于模式匹配中的变量绑定）
    /// </summary>
    public LocalId? BoundVariable { get; init; }
}

/// <summary>
/// 不可达代码
/// </summary>
public sealed record MirUnreachable : MirTerminator
{
    public override string ToString() => "unreachable";
}

/// <summary>
/// 基本块 ID
/// </summary>
public readonly record struct BlockId
{
    /// <summary>
    /// 块编号
    /// </summary>
    public int Value { get; init; }

    /// <summary>
    /// 无效块 ID
    /// </summary>
    public static BlockId None => new() { Value = 0 };

    /// <summary>
    /// 是否有效
    /// </summary>
    public bool IsValid => Value > 0;

    public override string ToString() => $"bb{Value}";
}
