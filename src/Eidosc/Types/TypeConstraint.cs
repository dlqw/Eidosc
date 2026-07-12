using Eidosc.Symbols;
using Eidosc.Utils;
using Eidosc.Semantic;

namespace Eidosc.Types;

/// <summary>
/// 类型约束基类
/// </summary>
public abstract record TypeConstraint
{
    /// <summary>
    /// 约束的源代码位置
    /// </summary>
    public SourceSpan Span { get; init; }
}

/// <summary>
/// Trait 约束: T : Trait
/// 表示类型 T 必须实现 Trait
/// </summary>
public sealed record TraitConstraint : TypeConstraint
{
    /// <summary>
    /// 被约束的类型
    /// </summary>
    public required Type Type { get; init; }

    /// <summary>
    /// Trait 符号 ID
    /// </summary>
    public required SymbolId Trait { get; init; }

    /// <summary>
    /// Trait 类型参数（对于带参数的 Trait）
    /// </summary>
    public List<Type> TraitArgs { get; init; } = [];

    /// <summary>
    /// Trait 类型参数的结构化查找键。
    /// </summary>
    public List<ImplTypeRefKey> TraitArgKeys { get; init; } = [];

    /// <summary>
    /// Trait 名称（用于错误信息）
    /// </summary>
    public string TraitName { get; init; } = "";

    public override string ToString()
    {
        if (TraitArgs.Count == 0)
            return $"{Type} : {TraitName}";
        var args = string.Join(", ", TraitArgs);
        return $"{Type} : {TraitName}<{args}>";
    }
}

/// <summary>
/// 相等约束: T1 = T2
/// 表示两个类型必须相等
/// </summary>
public sealed record EqualityConstraint : TypeConstraint
{
    /// <summary>
    /// 左边类型
    /// </summary>
    public required Type Left { get; init; }

    /// <summary>
    /// 右边类型
    /// </summary>
    public required Type Right { get; init; }

    public override string ToString() => $"{Left} = {Right}";
}

/// <summary>
/// Kind 约束: T : Kind
/// 表示类型 T 必须具有特定的 Kind
/// </summary>
public sealed record KindConstraint : TypeConstraint
{
    /// <summary>
    /// 被约束的类型
    /// </summary>
    public required Type Type { get; init; }

    /// <summary>
    /// 期望的 Kind
    /// </summary>
    public required string ExpectedKind { get; init; }

    public override string ToString() => $"{Type} : {ExpectedKind}";
}
