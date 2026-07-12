using Eidosc.Mir;

namespace Eidosc.Borrow;

/// <summary>
/// Perceus 分析结果
/// </summary>
public sealed class PerceusHints
{
    /// <summary>
    /// 可以省略的 dup 操作位置
    /// </summary>
    public HashSet<(BlockId Block, int Index)> OmitDup { get; } = [];

    /// <summary>
    /// 可以省略的 drop 操作位置
    /// </summary>
    public HashSet<(BlockId Block, int Index)> OmitDrop { get; } = [];
}

/// <summary>
/// Perceus 分析器 - 分析引用计数优化机会
/// </summary>
public sealed class PerceusAnalyzer
{
    private readonly MirFunc _function;
    private readonly LivenessAnalyzer _livenessAnalyzer;
    private readonly VariableUsageAnalyzer _usageAnalyzer;

    /// <summary>
    /// 分析结果
    /// </summary>
    public PerceusHints Hints { get; } = new();

    public PerceusAnalyzer(
        MirFunc function,
        LivenessAnalyzer livenessAnalyzer,
        VariableUsageAnalyzer usageAnalyzer)
    {
        _function = function;
        _livenessAnalyzer = livenessAnalyzer;
        _usageAnalyzer = usageAnalyzer;
    }

    /// <summary>
    /// 执行 Perceus 分析
    /// </summary>
    public void Analyze()
    {
        Hints.OmitDup.Clear();
        Hints.OmitDrop.Clear();

        // 1. 识别可省略的 dup 操作（使用活跃性分析）
        IdentifyOmitDup();

        // 2. 识别可省略的 drop 操作（使用活跃性分析）
        IdentifyOmitDrop();
    }

    /// <summary>
    /// 识别可省略的 dup 操作。
    /// MirCopy(source, target) 语义：source 在 copy 后仍然有效（与 MirMove 不同）。
    /// 因此 dup（incref）可以被省略，当且仅当：
    /// - source 在 copy 指令之后不再活跃（即这是 source 在此执行路径上的最后一次使用）
    ///
    /// 使用块内逐指令活跃性分析确保正确性。
    /// </summary>
    private void IdentifyOmitDup()
    {
        foreach (var block in _function.BasicBlocks)
        {
            // 获取块末尾的 LiveOut 集合作为初始状态
            if (!_livenessAnalyzer.TryGetLiveOutSet(block.Id, out var liveOut))
            {
                continue;
            }

            // 反向遍历指令，计算每条指令后的活跃集合
            var liveAfter = new HashSet<LocalId>(liveOut);

            for (int i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = block.Instructions[i];

                // 在处理指令之前，liveAfter 代表指令 i 之后的活跃集合
                if (instr is MirCopy copy &&
                    MirLocalTransferAnalysis.TryGetBinding(copy, out var copyBinding))
                {
                    var sourceVar = copyBinding.Source;

                    // source 在 copy 之后不再活跃 → 可以省略 dup
                    if (!liveAfter.Contains(sourceVar))
                    {
                        Hints.OmitDup.Add((block.Id, i));
                    }
                }

                // 更新活跃集合：从 liveAfter 推导 liveBefore
                UpdateLivenessForInstruction(instr, liveAfter);
            }
        }
    }

    /// <summary>
    /// 识别可省略的 drop 操作。
    /// MirDrop(value) 的 decref 可以被省略，当且仅当：
    /// - value 所代表的变量在 drop 之前已经被移动（MirMove 已转移所有权）
    /// - 或者 value 在此点不活跃（已被释放或从未被定义）
    ///
    /// 使用块内逐指令活跃性分析。
    /// </summary>
    private void IdentifyOmitDrop()
    {
        foreach (var block in _function.BasicBlocks)
        {
            if (!_livenessAnalyzer.TryGetLiveOutSet(block.Id, out var liveOut))
            {
                continue;
            }

            var liveAfter = new HashSet<LocalId>(liveOut);

            for (int i = block.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = block.Instructions[i];

                // liveAfter 现在代表指令 i 之后的活跃集合

                if (instr is MirDrop drop)
                {
                    if (drop.Value is MirPlace { Kind: PlaceKind.Local, Local: var dropLocal })
                    {
                        // 变量在 drop 指令之前不活跃 → 值已被移动/释放，drop 可省略
                        if (!liveAfter.Contains(dropLocal))
                        {
                            Hints.OmitDrop.Add((block.Id, i));
                        }
                    }
                }

                UpdateLivenessForInstruction(instr, liveAfter);
            }
        }
    }

    /// <summary>
    /// 更新活跃集合：从 liveAfter 推导 liveBefore。
    /// liveAfter 会原地修改为 liveBefore（即指令之前的活跃集合）。
    ///
    /// 标准活跃性公式：liveBefore = (liveAfter - def) ∪ use
    /// </summary>
    private void UpdateLivenessForInstruction(MirInstruction instr, HashSet<LocalId> live)
    {
        // 先移除 def（被此指令定义的变量）
        var definedVar = GetDefinedVariable(instr);
        if (definedVar != null)
        {
            live.Remove(definedVar.Value);
        }

        // 再添加 use（此指令使用的变量）
        AddUsedVariables(instr, live);
    }

    private LocalId? GetDefinedVariable(MirInstruction instr)
    {
        return instr switch
        {
            MirAssign assign => assign.Target is MirPlace { Kind: PlaceKind.Local, Local: var assignLocal } ? assignLocal : null,
            MirCall call => call.Target is MirPlace { Kind: PlaceKind.Local, Local: var callLocal } ? callLocal : null,
            MirBinOp binOp => binOp.Target is MirPlace { Kind: PlaceKind.Local, Local: var binOpLocal } ? binOpLocal : null,
            MirUnaryOp unaryOp => unaryOp.Target is MirPlace { Kind: PlaceKind.Local, Local: var unaryOpLocal } ? unaryOpLocal : null,
            MirLoad load when MirLocalTransferAnalysis.TryGetBinding(load, out var loadBinding) => loadBinding.Target,
            MirLoad load => load.Target is MirPlace { Kind: PlaceKind.Local, Local: var loadLocal } ? loadLocal : null,
            MirCopy copy when MirLocalTransferAnalysis.TryGetBinding(copy, out var copyBinding) => copyBinding.Target,
            MirCopy copy => copy.Target is MirPlace { Kind: PlaceKind.Local, Local: var copyLocal } ? copyLocal : null,
            MirMove move when MirLocalTransferAnalysis.TryGetBinding(move, out var moveBinding) => moveBinding.Target,
            MirMove move => move.Target is MirPlace { Kind: PlaceKind.Local, Local: var moveLocal } ? moveLocal : null,
            MirAlloc alloc => alloc.Target is MirPlace { Kind: PlaceKind.Local, Local: var allocLocal } ? allocLocal : null,
            _ => null
        };
    }

    private void AddUsedVariables(MirInstruction instr, HashSet<LocalId> result)
    {
        switch (instr)
        {
            case MirAssign assign:
                CollectUsedVariables(assign.Source, result);
                break;

            case MirCall call:
                CollectUsedVariables(call.Function, result);
                foreach (var arg in call.Arguments)
                {
                    CollectUsedVariables(arg, result);
                }
                break;

            case MirBinOp binOp:
                CollectUsedVariables(binOp.Left, result);
                CollectUsedVariables(binOp.Right, result);
                break;

            case MirUnaryOp unaryOp:
                CollectUsedVariables(unaryOp.Operand, result);
                break;

            case MirLoad load:
                if (MirLocalTransferAnalysis.TryGetBinding(load, out var loadBinding))
                {
                    result.Add(loadBinding.Source);
                }
                else
                {
                    CollectUsedVariables(load.Source, result);
                }
                break;

            case MirStore store:
                CollectUsedVariables(store.Value, result);
                if (store.Target?.Kind == PlaceKind.Local)
                {
                    result.Add(store.Target.Local);
                }
                break;

            case MirDrop drop:
                CollectUsedVariables(drop.Value, result);
                break;

            case MirCopy copy:
                if (MirLocalTransferAnalysis.TryGetBinding(copy, out var copyBinding))
                {
                    result.Add(copyBinding.Source);
                }
                else if (copy.Source?.Kind == PlaceKind.Local)
                {
                    result.Add(copy.Source.Local);
                }
                break;

            case MirMove move:
                if (MirLocalTransferAnalysis.TryGetBinding(move, out var moveBinding))
                {
                    result.Add(moveBinding.Source);
                }
                else if (move.Source?.Kind == PlaceKind.Local)
                {
                    result.Add(move.Source.Local);
                }
                break;

        }
    }

    private void CollectUsedVariables(MirOperand? operand, HashSet<LocalId> result)
    {
        if (operand is MirPlace place && place.Kind == PlaceKind.Local)
        {
            result.Add(place.Local);
        }
    }
}
