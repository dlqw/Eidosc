using Eidosc.Symbols;

namespace Eidosc.Types;

/// <summary>
/// 能力上下文 - 跟踪推断过程中可用和必需的能力
/// Tracks available and required abilities during inference.
/// </summary>
public sealed class EffectContext
{
    /// <summary>
    /// 作用域栈 - 每个作用域可以有自己的能力绑定
    /// </summary>
    private readonly Stack<EffectScope> _scopeStack = new();

    /// <summary>
    /// 当前必需的能力集合
    /// </summary>
    public EffectRow CurrentRequirements { get; private set; } = EffectRow.Pure;

    /// <summary>
    /// 当前作用域深度
    /// </summary>
    public int Depth => _scopeStack.Count;

    /// <summary>
    /// 进入新作用域
    /// </summary>
    public void EnterScope()
    {
        _scopeStack.Push(new EffectScope());
    }

    /// <summary>
    /// 退出当前作用域
    /// </summary>
    public void ExitScope()
    {
        if (_scopeStack.Count > 0)
        {
            _scopeStack.Pop();
        }
    }

    /// <summary>
    /// 向当前作用域添加可用能力
    /// </summary>
    public void AddEffect(EffectTag ability)
    {
        if (_scopeStack.Count > 0)
        {
            var scope = _scopeStack.Peek();
            scope.AvailableAbilities.Add(ability);
        }
    }

    /// <summary>
    /// 检查当前作用域链中是否有指定能力
    /// </summary>
    public bool HasEffect(EffectTag ability)
    {
        foreach (var scope in _scopeStack)
        {
            if (scope.AvailableAbilities.Contains(ability))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 检查当前作用域链中是否有指定名称的能力
    /// </summary>
    public bool HasEffect(string abilityName)
    {
        foreach (var scope in _scopeStack)
        {
            if (scope.AvailableAbilities.Any(a => a.Name == abilityName))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 添加能力需求
    /// </summary>
    public void AddRequirement(EffectTag ability)
    {
        // 如果能力已经在作用域中可用，则不需要添加需求
        if (!HasEffect(ability))
        {
            CurrentRequirements = CurrentRequirements.Add(ability);
        }
    }

    /// <summary>
    /// 添加多个能力需求
    /// </summary>
    public void AddRequirements(EffectRow abilities)
    {
        CurrentRequirements = CurrentRequirements.Union(abilities);
    }

    /// <summary>
    /// 移除能力需求
    /// </summary>
    public void RemoveRequirement(EffectTag ability)
    {
        CurrentRequirements = CurrentRequirements.Remove(ability);
    }

    /// <summary>
    /// 消除被处理的能力需求（在 with 子句或 handler 中）
    /// </summary>
    public void EliminateRequirements(EffectRow handledAbilities)
    {
        CurrentRequirements = CurrentRequirements.Difference(handledAbilities);
    }

    /// <summary>
    /// 消除单个能力需求
    /// </summary>
    public void EliminateRequirement(EffectTag handledEffect)
    {
        CurrentRequirements = CurrentRequirements.Remove(handledEffect);
    }

    /// <summary>
    /// 获取当前所有可用能力
    /// </summary>
    public HashSet<EffectTag> GetAllAvailableAbilities()
    {
        var result = new HashSet<EffectTag>();
        foreach (var scope in _scopeStack)
        {
            result.UnionWith(scope.AvailableAbilities);
        }
        return result;
    }

    /// <summary>
    /// 重置上下文
    /// </summary>
    public void Reset()
    {
        _scopeStack.Clear();
        CurrentRequirements = EffectRow.Pure;
    }

    /// <summary>
    /// 创建当前上下文的快照
    /// </summary>
    public EffectContextSnapshot CreateSnapshot()
    {
        return new EffectContextSnapshot(
            CurrentRequirements,
            GetAllAvailableAbilities(),
            _scopeStack.Count);
    }

    /// <summary>
    /// 恢复到指定快照
    /// </summary>
    public void RestoreSnapshot(EffectContextSnapshot snapshot)
    {
        CurrentRequirements = snapshot.Requirements;

        // Restore scope depth: pop any scopes pushed after the snapshot was taken.
        while (_scopeStack.Count > snapshot.ScopeDepth)
        {
            _scopeStack.Pop();
        }
    }
}

/// <summary>
/// 能力作用域
/// </summary>
internal sealed class EffectScope
{
    /// <summary>
    /// 此作用域中可用的能力
    /// </summary>
    public HashSet<EffectTag> AvailableAbilities { get; } = [];
}

/// <summary>
/// 能力上下文快照
/// </summary>
public readonly record struct EffectContextSnapshot(
    EffectRow Requirements,
    HashSet<EffectTag> AvailableAbilities,
    int ScopeDepth)
{
    /// <summary>
    /// 创建空快照
    /// </summary>
    public static readonly EffectContextSnapshot Empty = new(EffectRow.Pure, [], 0);
}
