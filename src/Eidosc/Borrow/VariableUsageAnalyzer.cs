using Eidosc.Mir;
using Eidosc.Utils;

namespace Eidosc.Borrow;

/// <summary>
/// 变量使用分析器 - 分析 MIR 中每个变量的使用情况
/// </summary>
public sealed class VariableUsageAnalyzer
{
    private readonly MirFunc _function;
    private readonly Dictionary<LocalId, List<UseSiteRecord>> _useSites = new();
    private readonly Dictionary<LocalId, (BlockId Block, int Index)> _firstDef = new();
    private readonly Dictionary<LocalId, (BlockId Block, int Index)> _lastUse = new();

    public VariableUsageAnalyzer(MirFunc function)
    {
        _function = function;
    }

    /// <summary>
    /// 执行使用分析
    /// </summary>
    public void Analyze()
    {
        // 初始化所有局部变量的使用点列表
        foreach (var local in _function.Locals)
        {
            _useSites[local.Id] = [];
        }

        // 遍历所有基本块和指令
        foreach (var block in _function.BasicBlocks)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                AnalyzeInstruction(instr, block.Id, i);
            }

            // 分析终止符
            if (block.Terminator != null)
            {
                AnalyzeTerminator(block.Terminator, block.Id, block.Instructions.Count);
            }
        }

        // 计算最后使用点
        ComputeLastUse();
    }

    /// <summary>
    /// 获取变量的所有使用点
    /// </summary>
    public List<UseInfo> GetUseSites(LocalId variable)
    {
        if (!_useSites.TryGetValue(variable, out var sites) || sites.Count == 0)
        {
            return [];
        }

        var result = new List<UseInfo>(sites.Count);
        foreach (var site in sites)
        {
            result.Add(new UseInfo
            {
                Variable = variable,
                Kind = site.Kind,
                BlockId = site.BlockId,
                InstructionIndex = site.InstructionIndex,
                Span = site.Span
            });
        }

        return result;
    }

    /// <summary>
    /// 获取变量的首次定义点
    /// </summary>
    public (BlockId Block, int Index)? GetFirstDef(LocalId variable)
    {
        return _firstDef.TryGetValue(variable, out var def) ? def : null;
    }

    /// <summary>
    /// 获取变量的最后使用点
    /// </summary>
    public (BlockId Block, int Index)? GetLastUse(LocalId variable)
    {
        return _lastUse.TryGetValue(variable, out var use) ? use : null;
    }

    /// <summary>
    /// 检查变量是否在指定点之前被移动
    /// </summary>
    public bool IsMovedBefore(LocalId variable, BlockId block, int index)
    {
        if (!_useSites.TryGetValue(variable, out var sites))
            return false;

        foreach (var site in sites)
        {
            if (site.Kind == UseKind.Move)
            {
                // 检查是否在当前点之前
                if (IsBefore(site.BlockId, site.InstructionIndex, block, index))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void AnalyzeInstruction(MirInstruction instr, BlockId blockId, int index)
    {
        switch (instr)
        {
            case MirAssign assign:
                // 目标是定义
                RecordDefinition(assign.Target, blockId, index);
                // 源是使用
                AnalyzeOperand(assign.Source, UseKind.Read, blockId, index);
                break;

            case MirCaseInject injection:
                AnalyzeOperand(injection.Operand, UseKind.Read, blockId, index);
                if (injection.Target is MirPlace target)
                {
                    RecordDefinition(target, blockId, index);
                }
                break;

            case MirCall call:
                // 函数是使用
                AnalyzeOperand(call.Function, UseKind.Read, blockId, index);
                // 参数是使用（可能是移动）
                foreach (var arg in call.Arguments)
                {
                    AnalyzeOperand(arg, UseKind.Move, blockId, index);
                }
                // 目标是定义
                if (call.Target != null)
                {
                    RecordDefinition(call.Target, blockId, index);
                }
                break;

            case MirBinOp binOp:
                AnalyzeOperand(binOp.Left, UseKind.Read, blockId, index);
                AnalyzeOperand(binOp.Right, UseKind.Read, blockId, index);
                if (binOp.Target != null) RecordDefinitionFromOperand(binOp.Target, blockId, index);
                break;

            case MirUnaryOp unaryOp:
                AnalyzeOperand(unaryOp.Operand, UseKind.Read, blockId, index);
                if (unaryOp.Target != null) RecordDefinitionFromOperand(unaryOp.Target, blockId, index);
                break;

            case MirLoad load:
                AnalyzeOperand(load.Source, UseKind.Read, blockId, index);
                RecordDefinition(load.Target, blockId, index);
                break;

            case MirStore store:
                AnalyzeOperand(store.Value, UseKind.Move, blockId, index);
                // 目标地址是写入
                if (store.Target is MirPlace place)
                {
                    RecordUse(place, UseKind.Write, blockId, index);
                }
                break;

            case MirDrop drop:
                AnalyzeOperand(drop.Value, UseKind.Move, blockId, index);
                break;

            case MirCopy copy:
                RecordUse(copy.Source, UseKind.Copy, blockId, index);
                RecordDefinition(copy.Target, blockId, index);
                break;

            case MirMove move:
                RecordUse(move.Source, UseKind.Move, blockId, index);
                RecordDefinition(move.Target, blockId, index);
                break;

            case MirAlloc alloc:
                RecordDefinition(alloc.Target, blockId, index);
                break;
        }
    }

    private void AnalyzeTerminator(MirTerminator terminator, BlockId blockId, int index)
    {
        switch (terminator)
        {
            case MirReturn ret:
                if (ret.Value != null)
                {
                    AnalyzeOperand(ret.Value, UseKind.Move, blockId, index);
                }
                break;

            case MirSwitch sw:
                AnalyzeOperand(sw.Discriminant, UseKind.Read, blockId, index);
                break;

            // MirGoto 和 MirUnreachable 没有操作数
        }
    }

    private void AnalyzeOperand(MirOperand operand, UseKind defaultKind, BlockId blockId, int index)
    {
        if (operand is MirPlace place)
        {
            RecordUse(place, defaultKind, blockId, index);
        }
    }

    private void RecordDefinition(MirPlace place, BlockId blockId, int index)
    {
        if (place.Kind == PlaceKind.Local)
        {
            if (!_firstDef.ContainsKey(place.Local))
            {
                _firstDef[place.Local] = (blockId, index);
            }
        }
    }

    private void RecordDefinitionFromOperand(MirOperand operand, BlockId blockId, int index)
    {
        if (operand is MirPlace place)
        {
            RecordDefinition(place, blockId, index);
        }
    }

    private void RecordUse(MirPlace place, UseKind kind, BlockId blockId, int index)
    {
        if (place.Kind == PlaceKind.Local)
        {
            _useSites[place.Local].Add(new UseSiteRecord(kind, blockId, index, place.Span));
        }
        else if (place.Base != null)
        {
            // 递归处理基础位置
            RecordUse(place.Base, kind, blockId, index);
        }

        // 处理索引
        if (place.Index != null)
        {
            AnalyzeOperand(place.Index, UseKind.Read, blockId, index);
        }
    }

    private void ComputeLastUse()
    {
        foreach (var (local, uses) in _useSites)
        {
            if (uses.Count == 0)
                continue;

            // 找到最后一个使用点
            UseSiteRecord? last = null;
            foreach (var use in uses)
            {
                if (last == null || IsAfter(use.BlockId, use.InstructionIndex, last.Value.BlockId, last.Value.InstructionIndex))
                {
                    last = use;
                }
            }

            if (last != null)
            {
                _lastUse[local] = (last.Value.BlockId, last.Value.InstructionIndex);
            }
        }
    }

    private static bool IsBefore(BlockId block1, int index1, BlockId block2, int index2)
    {
        if (block1.Value < block2.Value)
            return true;
        if (block1.Value == block2.Value)
            return index1 < index2;
        return false;
    }

    private static bool IsAfter(BlockId block1, int index1, BlockId block2, int index2)
    {
        return IsBefore(block2, index2, block1, index1);
    }

    private readonly record struct UseSiteRecord(
        UseKind Kind,
        BlockId BlockId,
        int InstructionIndex,
        SourceSpan Span);
}
