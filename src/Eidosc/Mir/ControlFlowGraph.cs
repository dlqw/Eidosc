namespace Eidosc.Mir;

/// <summary>
/// 控制流图 - 表示函数的控制流结构
/// </summary>
public sealed class ControlFlowGraph
{
    private readonly MirFunc _function;
    private readonly Dictionary<BlockId, HashSet<BlockId>> _predecessors = new();
    private readonly Dictionary<BlockId, HashSet<BlockId>> _successors = new();
    private readonly Dictionary<BlockId, HashSet<BlockId>> _dominators = new();
    private readonly Dictionary<BlockId, HashSet<BlockId>> _postDominators = new();
    private readonly Dictionary<BlockId, HashSet<BlockId>> _dominanceFrontier = new();
    private bool _dominatorsComputed;
    private bool _postDominatorsComputed;
    private bool _dominanceFrontierComputed;

    private static readonly HashSet<BlockId> EmptySet = new();

    public ControlFlowGraph(MirFunc function)
    {
        _function = function;
        ComputePredecessorsAndSuccessors();
    }

    /// <summary>
    /// 获取基本块的前驱块
    /// </summary>
    public IReadOnlySet<BlockId> GetPredecessors(BlockId blockId)
    {
        return _predecessors.TryGetValue(blockId, out var preds) ? preds : EmptySet;
    }

    /// <summary>
    /// 获取基本块的后继块
    /// </summary>
    public IReadOnlySet<BlockId> GetSuccessors(BlockId blockId)
    {
        return _successors.TryGetValue(blockId, out var succs) ? succs : EmptySet;
    }

    /// <summary>
    /// 获取基本块的支配块（包括自身）
    /// </summary>
    public IReadOnlySet<BlockId> GetDominators(BlockId blockId)
    {
        EnsureDominatorsComputed();
        return _dominators.TryGetValue(blockId, out var doms) ? doms : EmptySet;
    }

    /// <summary>
    /// 获取基本块的后支配块（包括自身）
    /// </summary>
    public IReadOnlySet<BlockId> GetPostDominators(BlockId blockId)
    {
        EnsurePostDominatorsComputed();
        return _postDominators.TryGetValue(blockId, out var pdoms) ? pdoms : EmptySet;
    }

    /// <summary>
    /// 获取基本块的支配边界
    /// </summary>
    public IReadOnlySet<BlockId> GetDominanceFrontier(BlockId blockId)
    {
        EnsureDominanceFrontierComputed();
        return _dominanceFrontier.TryGetValue(blockId, out var df) ? df : EmptySet;
    }

    /// <summary>
    /// 获取直接支配者（immediate dominator）
    /// </summary>
    public BlockId? GetImmediateDominator(BlockId blockId)
    {
        EnsureDominatorsComputed();
        if (!_dominators.TryGetValue(blockId, out var doms))
            return null;

        // 直接支配者是支配 blockId 但不支配其他支配者的最近块
        var otherDoms = doms.Where(d => !d.Equals(blockId)).ToList();
        if (otherDoms.Count == 0)
            return null;

        // 找到最近的支配者
        foreach (var candidate in otherDoms)
        {
            var isImmediate = true;
            foreach (var other in otherDoms)
            {
                if (!other.Equals(candidate) && _dominators.TryGetValue(candidate, out var candidateDoms) && candidateDoms.Contains(other))
                {
                    isImmediate = false;
                    break;
                }
            }
            if (isImmediate)
                return candidate;
        }

        return otherDoms.FirstOrDefault();
    }

    private void EnsureDominatorsComputed()
    {
        if (_dominatorsComputed)
        {
            return;
        }

        ComputeDominators();
        _dominatorsComputed = true;
    }

    private void EnsurePostDominatorsComputed()
    {
        if (_postDominatorsComputed)
        {
            return;
        }

        ComputePostDominators();
        _postDominatorsComputed = true;
    }

    private void EnsureDominanceFrontierComputed()
    {
        if (_dominanceFrontierComputed)
        {
            return;
        }

        EnsureDominatorsComputed();
        ComputeDominanceFrontier();
        _dominanceFrontierComputed = true;
    }

    private void ComputePredecessorsAndSuccessors()
    {
        // 初始化
        foreach (var block in _function.BasicBlocks)
        {
            _predecessors[block.Id] = new HashSet<BlockId>();
            _successors[block.Id] = new HashSet<BlockId>();
        }

        // 计算前驱和后继
        foreach (var block in _function.BasicBlocks)
        {
            var successorIds = GetSuccessorBlockIds(block);
            foreach (var succId in successorIds)
            {
                _successors[block.Id].Add(succId);
                if (_predecessors.ContainsKey(succId))
                {
                    _predecessors[succId].Add(block.Id);
                }
            }
        }
    }

    private List<BlockId> GetSuccessorBlockIds(MirBasicBlock block)
    {
        var result = new List<BlockId>();

        if (block.Terminator == null)
            return result;

        switch (block.Terminator)
        {
            case MirGoto gotoTerm:
                result.Add(gotoTerm.Target);
                break;

            case MirSwitch switchTerm:
                foreach (var branch in switchTerm.Branches)
                {
                    result.Add(branch.Target);
                }
                if (switchTerm.DefaultTarget != null)
                {
                    result.Add(switchTerm.DefaultTarget.Value);
                }
                break;

            case MirReturn:
            case MirUnreachable:
                // 没有后继块
                break;
        }

        return result;
    }

    private void ComputeDominators()
    {
        _dominators.Clear();
        var allBlocks = _function.BasicBlocks.Select(b => b.Id).ToHashSet();

        // 入口块的支配者只有自己
        _dominators[_function.EntryBlockId] = [_function.EntryBlockId];

        // 其他块的初始支配者是所有块
        foreach (var block in _function.BasicBlocks)
        {
            if (!block.Id.Equals(_function.EntryBlockId))
            {
                _dominators[block.Id] = new HashSet<BlockId>(allBlocks);
            }
        }

        // 迭代计算支配者
        bool changed;
        do
        {
            changed = false;
            foreach (var block in _function.BasicBlocks)
            {
                if (block.Id.Equals(_function.EntryBlockId))
                    continue;

                var preds = GetPredecessors(block.Id);
                if (preds.Count == 0)
                    continue;

                // 新的支配者集 = 并集(所有前驱的支配者) ∩ 当前块
                var newDoms = new HashSet<BlockId>(allBlocks);
                foreach (var pred in preds)
                {
                    if (_dominators.TryGetValue(pred, out var predDoms))
                    {
                        newDoms.IntersectWith(predDoms);
                    }
                }
                newDoms.Add(block.Id);

                if (!_dominators[block.Id].SetEquals(newDoms))
                {
                    _dominators[block.Id] = newDoms;
                    changed = true;
                }
            }
        } while (changed);
    }

    private void ComputePostDominators()
    {
        _postDominators.Clear();
        // 找到所有出口块（return 或 unreachable）
        var exitBlocks = _function.BasicBlocks
            .Where(b => b.Terminator is MirReturn or MirUnreachable)
            .Select(b => b.Id)
            .ToHashSet();

        var allBlocks = _function.BasicBlocks.Select(b => b.Id).ToHashSet();

        // 出口块的后支配者只有自己
        foreach (var exitBlock in exitBlocks)
        {
            _postDominators[exitBlock] = [exitBlock];
        }

        // 其他块的初始后支配者是所有块
        foreach (var block in _function.BasicBlocks)
        {
            if (!exitBlocks.Contains(block.Id))
            {
                _postDominators[block.Id] = new HashSet<BlockId>(allBlocks);
            }
        }

        // 迭代计算后支配者
        bool changed;
        do
        {
            changed = false;
            foreach (var block in _function.BasicBlocks)
            {
                if (exitBlocks.Contains(block.Id))
                    continue;

                var succs = GetSuccessors(block.Id);
                if (succs.Count == 0)
                    continue;

                // 新的后支配者集 = 并集(所有后继的后支配者) ∩ 当前块
                var newPdoms = new HashSet<BlockId>(allBlocks);
                foreach (var succ in succs)
                {
                    if (_postDominators.TryGetValue(succ, out var succPdoms))
                    {
                        newPdoms.IntersectWith(succPdoms);
                    }
                }
                newPdoms.Add(block.Id);

                if (!_postDominators[block.Id].SetEquals(newPdoms))
                {
                    _postDominators[block.Id] = newPdoms;
                    changed = true;
                }
            }
        } while (changed);
    }

    private void ComputeDominanceFrontier()
    {
        _dominanceFrontier.Clear();
        // 支配边界: 块 A 的支配边界包含块 B，当且仅当:
        // 1. A 不是 B 的严格支配者
        // 2. A 支配 B 的某个前驱

        foreach (var block in _function.BasicBlocks)
        {
            _dominanceFrontier[block.Id] = new HashSet<BlockId>();
        }

        foreach (var block in _function.BasicBlocks)
        {
            var preds = GetPredecessors(block.Id);
            if (preds.Count < 2)
                continue;

            foreach (var pred in preds)
            {
                var runner = pred;
                while (runner.IsValid && !runner.Equals(GetImmediateDominator(block.Id) ?? BlockId.None))
                {
                    _dominanceFrontier[runner].Add(block.Id);
                    runner = GetImmediateDominator(runner) ?? BlockId.None;
                }
            }
        }
    }

    /// <summary>
    /// 生成 DOT 格式的控制流图
    /// </summary>
    public string ToDot()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("digraph CFG {");
        sb.AppendLine("  node [shape=box];");

        // 输出基本块
        foreach (var block in _function.BasicBlocks)
        {
            var label = $"bb{block.Id.Value}";
            var instrs = string.Join("\\l", block.Instructions.Select(i => i.ToString()));
            var term = block.Terminator?.ToString() ?? "";
            sb.AppendLine($"  \"{label}\" [label=\"{label}:\\l{instrs}\\l{term}\\l\"];");
        }

        // 输出边
        foreach (var block in _function.BasicBlocks)
        {
            var from = $"bb{block.Id.Value}";
            foreach (var succ in GetSuccessors(block.Id))
            {
                var to = $"bb{succ.Value}";
                sb.AppendLine($"  \"{from}\" -> \"{to}\";");
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }
}
