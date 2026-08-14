using Eidosc.Mir;

namespace Eidosc.Borrow;

/// <summary>
/// Reuse 分析结果 —— 告知 codegen 哪些 drop/alloc 可以复用内存块。
/// 遵循 PerceusHints 的 Hint 模式，不引入新 MIR 指令类型。
/// </summary>
public sealed class ReuseHints
{
    /// <summary>
    /// MirDrop 位置 → 复用槽编号。
    /// Codegen 在这些位置发 eidos_drop_reuse 而非 eidos_decref。
    /// </summary>
    public Dictionary<(BlockId Block, int Index), int> DropReuseSites { get; } = [];

    /// <summary>
    /// 构造器 MirCall 位置 → 复用槽编号。
    /// Codegen 在这些位置发 eidos_alloc_reuse 而非 eidos_alloc。
    /// </summary>
    public Dictionary<(BlockId Block, int Index), int> AllocReuseSites { get; } = [];

    /// <summary>
    /// 函数内复用槽总数。Codegen 为每个槽发 alloca。
    /// </summary>
    public int SlotCount { get; set; }
}

/// <summary>
/// Reuse 分析器 —— 识别 drop-then-alloc 模式。
///
/// 块内和跨块使用同一前向 must-dataflow：只有一个 drop 槽在到达构造器的
/// 每条控制流路径上都可用时，才允许跨基本块复用。分支独占的构造器可以
/// 共享同一上游槽；不同路径各自产生的槽不会在 join 处被错误合并。
///
/// 已被 Perceus 标记为 OmitDrop 的 drop 不参与复用（no-op 无内存可复用）。
/// </summary>
public sealed class ReuseAnalyzer
{
    private readonly MirFunc _function;
    private readonly PerceusHints? _perceusHints;

    /// <summary>
    /// 分析结果
    /// </summary>
    public ReuseHints Hints { get; } = new();

    public ReuseAnalyzer(MirFunc function, PerceusHints? perceusHints = null)
    {
        _function = function;
        _perceusHints = perceusHints;
    }

    /// <summary>
    /// 执行 Reuse 分析
    /// </summary>
    public void Analyze()
    {
        Hints.DropReuseSites.Clear();
        Hints.AllocReuseSites.Clear();
        Hints.SlotCount = 0;

        var dropSlots = AssignDropSlots(out var slotCounter);
        Hints.SlotCount = slotCounter;
        if (dropSlots.Count == 0 || _function.BasicBlocks.Count == 0)
            return;

        var predecessors = BuildPredecessorMap();
        var entryStates = ComputeEntryStates(dropSlots, predecessors);
        var pairedAllocations = new Dictionary<(BlockId Block, int Index), int>();

        foreach (var block in _function.BasicBlocks)
        {
            var available = entryStates.GetValueOrDefault(block.Id, [])
                .OrderBy(static item => item.Slot)
                .ToList();
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                if (dropSlots.TryGetValue((block.Id, index), out var drop))
                {
                    available.Add(drop);
                    continue;
                }

                if (!IsHeapAllocatingConstructorCall(block.Instructions[index], out var targetTypeId) ||
                    !TryMatchSlot(available, targetTypeId, out var matchedSlot))
                {
                    continue;
                }

                pairedAllocations[(block.Id, index)] = matchedSlot;
            }
        }

        var pairedSlots = pairedAllocations.Values.ToHashSet();
        foreach (var (site, drop) in dropSlots)
        {
            if (pairedSlots.Contains(drop.Slot))
                Hints.DropReuseSites[site] = drop.Slot;
        }

        foreach (var (site, slot) in pairedAllocations)
            Hints.AllocReuseSites[site] = slot;
    }

    private Dictionary<(BlockId Block, int Index), (int Slot, TypeId TypeId)> AssignDropSlots(
        out int slotCounter)
    {
        slotCounter = 0;
        var slots = new Dictionary<(BlockId Block, int Index), (int Slot, TypeId TypeId)>();
        foreach (var block in _function.BasicBlocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                if (block.Instructions[index] is not MirDrop drop ||
                    !TypeSemantics.IsManagedType(drop.Value.TypeId) ||
                    _perceusHints?.OmitDrop.Contains((block.Id, index)) == true)
                {
                    continue;
                }

                slots[(block.Id, index)] = (slotCounter++, drop.Value.TypeId);
            }
        }

        return slots;
    }

    private Dictionary<BlockId, HashSet<(int Slot, TypeId TypeId)>> ComputeEntryStates(
        IReadOnlyDictionary<(BlockId Block, int Index), (int Slot, TypeId TypeId)> dropSlots,
        IReadOnlyDictionary<BlockId, List<BlockId>> predecessors)
    {
        var entryStates = _function.BasicBlocks.ToDictionary(
            static block => block.Id,
            _ => new HashSet<(int Slot, TypeId TypeId)>());
        var exitStates = _function.BasicBlocks.ToDictionary(
            static block => block.Id,
            _ => new HashSet<(int Slot, TypeId TypeId)>());

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in _function.BasicBlocks)
            {
                var incoming = predecessors.GetValueOrDefault(block.Id, []);
                var nextEntry = block.Id == _function.EntryBlockId || incoming.Count == 0
                    ? new HashSet<(int Slot, TypeId TypeId)>()
                    : IntersectPredecessorStates(incoming, exitStates);
                var nextExit = Transfer(block, nextEntry, dropSlots);

                if (!entryStates[block.Id].SetEquals(nextEntry))
                {
                    entryStates[block.Id] = nextEntry;
                    changed = true;
                }

                if (!exitStates[block.Id].SetEquals(nextExit))
                {
                    exitStates[block.Id] = nextExit;
                    changed = true;
                }
            }
        }

        return entryStates;
    }

    private static HashSet<(int Slot, TypeId TypeId)> IntersectPredecessorStates(
        IReadOnlyList<BlockId> predecessors,
        IReadOnlyDictionary<BlockId, HashSet<(int Slot, TypeId TypeId)>> exitStates)
    {
        HashSet<(int Slot, TypeId TypeId)>? result = null;
        foreach (var predecessor in predecessors)
        {
            if (!exitStates.TryGetValue(predecessor, out var state))
                continue;

            result ??= new HashSet<(int Slot, TypeId TypeId)>(state);
            result.IntersectWith(state);
        }

        return result ?? [];
    }

    private static HashSet<(int Slot, TypeId TypeId)> Transfer(
        MirBasicBlock block,
        HashSet<(int Slot, TypeId TypeId)> entry,
        IReadOnlyDictionary<(BlockId Block, int Index), (int Slot, TypeId TypeId)> dropSlots)
    {
        var available = entry.OrderBy(static item => item.Slot).ToList();
        for (var index = 0; index < block.Instructions.Count; index++)
        {
            if (dropSlots.TryGetValue((block.Id, index), out var drop))
            {
                available.Add(drop);
                continue;
            }

            if (IsHeapAllocatingConstructorCall(block.Instructions[index], out var targetTypeId))
                TryMatchSlot(available, targetTypeId, out _);
        }

        return available.ToHashSet();
    }

    private Dictionary<BlockId, List<BlockId>> BuildPredecessorMap()
    {
        var predecessors = _function.BasicBlocks.ToDictionary(static block => block.Id, _ => new List<BlockId>());
        foreach (var block in _function.BasicBlocks)
        {
            switch (block.Terminator)
            {
                case MirGoto gotoTerm when predecessors.TryGetValue(gotoTerm.Target, out var gotoPreds):
                    gotoPreds.Add(block.Id);
                    break;
                case MirSwitch sw:
                    foreach (var branch in sw.Branches)
                    {
                        if (predecessors.TryGetValue(branch.Target, out var branchPreds))
                            branchPreds.Add(block.Id);
                    }

                    if (sw.DefaultTarget is { } defaultTarget &&
                        predecessors.TryGetValue(defaultTarget, out var defaultPreds))
                    {
                        defaultPreds.Add(block.Id);
                    }
                    break;
            }
        }

        return predecessors;
    }

    // ---- Shared helpers ----

    /// <summary>
    /// Try to find and remove the latest matching slot from the available list.
    /// Returns true if a match was found. Removing the latest slot preserves
    /// the previous block-local LIFO preference while keeping dataflow states
    /// deterministic.
    /// </summary>
    private static bool TryMatchSlot(
        List<(int Slot, TypeId TypeId)> available,
        TypeId targetTypeId,
        out int matchedSlot)
    {
        matchedSlot = -1;
        for (var index = available.Count - 1; index >= 0; index--)
        {
            if (!available[index].TypeId.Equals(targetTypeId))
                continue;

            matchedSlot = available[index].Slot;
            available.RemoveAt(index);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断 MirCall 是否为堆分配的构造器调用。
    /// </summary>
    internal static bool IsHeapAllocatingConstructorCall(
        MirInstruction instr,
        out TypeId targetTypeId)
    {
        return TypeSemantics.IsHeapAllocatingConstructorCall(instr, out targetTypeId);
    }
}
