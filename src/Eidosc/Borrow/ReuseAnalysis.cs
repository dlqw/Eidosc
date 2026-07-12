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
/// Phase 1: 块内配对（前向扫描，维护可用复用槽栈）
/// Phase 2: 跨块配对（数据流分析传播未配对 drop 到后继块）
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

        var slotCounter = 0;

        // Phase 1: Within-block pairing
        foreach (var block in _function.BasicBlocks)
        {
            AnalyzeBlock(block, ref slotCounter);
        }

        // Phase 2: Cross-block pairing for unpaired constructors
        AnalyzeCrossBlock(ref slotCounter);

        Hints.SlotCount = slotCounter;
    }

    // ---- Phase 1: Within-block pairing ----

    private void AnalyzeBlock(MirBasicBlock block, ref int slotCounter)
    {
        var available = new Stack<(int Slot, TypeId TypeId)>();

        for (int i = 0; i < block.Instructions.Count; i++)
        {
            var instr = block.Instructions[i];

            if (instr is MirDrop drop)
            {
                var dropTypeId = drop.Value.TypeId;
                if (!TypeSemantics.IsManagedType(dropTypeId))
                    continue;

                if (_perceusHints != null &&
                    _perceusHints.OmitDrop.Contains((block.Id, i)))
                {
                    continue;
                }

                var slot = slotCounter++;
                Hints.DropReuseSites[(block.Id, i)] = slot;
                available.Push((slot, dropTypeId));
            }
            else if (IsHeapAllocatingConstructorCall(instr, out var targetTypeId))
            {
                if (TryMatchSlot(available, targetTypeId, out var matchedSlot))
                {
                    Hints.AllocReuseSites[(block.Id, i)] = matchedSlot;
                }
            }
        }
    }

    // ---- Phase 2: Cross-block pairing ----

    private void AnalyzeCrossBlock(ref int slotCounter)
    {
        // Collect unpaired drops per block (drops that weren't matched in Phase 1)
        var pairedSlots = new HashSet<int>(Hints.AllocReuseSites.Values);
        var unpairedDropsByBlock = new Dictionary<BlockId, List<(int Slot, TypeId TypeId)>>();

        foreach (var block in _function.BasicBlocks)
        {
            var unpaired = new List<(int Slot, TypeId TypeId)>();
            foreach (var ((blockId, index), slot) in Hints.DropReuseSites)
            {
                if (blockId.Equals(block.Id) && !pairedSlots.Contains(slot))
                {
                    var dropInstr = (MirDrop)block.Instructions[index];
                    unpaired.Add((slot, dropInstr.Value.TypeId));
                }
            }
            unpairedDropsByBlock[block.Id] = unpaired;
        }

        // Dataflow: propagate unpaired drops across block boundaries
        var predecessors = BuildPredecessorMap();
        var inheritedDrops = ComputeInheritedDrops(predecessors, unpairedDropsByBlock);

        // Pair unpaired constructors with inherited drops
        foreach (var block in _function.BasicBlocks)
        {
            if (!inheritedDrops.TryGetValue(block.Id, out var available) || available.Count == 0)
                continue;

            var availableStack = new Stack<(int Slot, TypeId TypeId)>(available);

            for (int i = 0; i < block.Instructions.Count; i++)
            {
                // Skip already-paired constructors
                if (Hints.AllocReuseSites.ContainsKey((block.Id, i)))
                    continue;

                // Push local unpaired drops onto stack (they might be usable by later constructors)
                if (block.Instructions[i] is MirDrop drop &&
                    Hints.DropReuseSites.TryGetValue((block.Id, i), out var dropSlot) &&
                    !pairedSlots.Contains(dropSlot))
                {
                    availableStack.Push((dropSlot, drop.Value.TypeId));
                }

                if (IsHeapAllocatingConstructorCall(block.Instructions[i], out var targetTypeId))
                {
                    if (TryMatchSlot(availableStack, targetTypeId, out var matchedSlot))
                    {
                        Hints.AllocReuseSites[(block.Id, i)] = matchedSlot;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Dataflow analysis: propagate unpaired drop slots across blocks.
    /// IN[B] = ∪ OUT[P] for all predecessors P of B.
    /// OUT[B] = IN[B] ∪ gen[B]  (gen = unpaired drops in B).
    /// </summary>
    private Dictionary<BlockId, List<(int Slot, TypeId TypeId)>> ComputeInheritedDrops(
        Dictionary<BlockId, List<BlockId>> predecessors,
        Dictionary<BlockId, List<(int Slot, TypeId TypeId)>> unpairedDropsByBlock)
    {
        // Initialize OUT sets with local unpaired drops
        var outSets = new Dictionary<BlockId, HashSet<(int Slot, TypeId TypeId)>>();
        foreach (var block in _function.BasicBlocks)
        {
            outSets[block.Id] = new HashSet<(int, TypeId)>(
                unpairedDropsByBlock.GetValueOrDefault(block.Id, [])
                    .Select(d => (d.Slot, d.TypeId)));
        }

        // Fixed-point iteration
        bool changed;
        do
        {
            changed = false;
            foreach (var block in _function.BasicBlocks)
            {
                // IN = ∪ OUT[P]
                var inSet = new HashSet<(int, TypeId)>();
                if (predecessors.TryGetValue(block.Id, out var preds))
                {
                    foreach (var pred in preds)
                    {
                        if (outSets.TryGetValue(pred, out var predOut))
                            inSet.UnionWith(predOut);
                    }
                }

                // OUT = IN ∪ gen
                var newOut = new HashSet<(int, TypeId)>(inSet);
                foreach (var drop in unpairedDropsByBlock.GetValueOrDefault(block.Id, []))
                    newOut.Add((drop.Slot, drop.TypeId));

                if (!newOut.SetEquals(outSets[block.Id]))
                {
                    outSets[block.Id] = newOut;
                    changed = true;
                }
            }
        } while (changed);

        // Result: IN set for each block (inherited drops at block entry)
        var result = new Dictionary<BlockId, List<(int Slot, TypeId TypeId)>>();
        foreach (var block in _function.BasicBlocks)
        {
            var inSet = new HashSet<(int, TypeId)>();
            if (predecessors.TryGetValue(block.Id, out var preds))
            {
                foreach (var pred in preds)
                {
                    if (outSets.TryGetValue(pred, out var predOut))
                        inSet.UnionWith(predOut);
                }
            }
            result[block.Id] = inSet.ToList();
        }

        return result;
    }

    private Dictionary<BlockId, List<BlockId>> BuildPredecessorMap()
    {
        var predecessors = new Dictionary<BlockId, List<BlockId>>();
        foreach (var block in _function.BasicBlocks)
        {
            predecessors[block.Id] = [];
        }

        foreach (var block in _function.BasicBlocks)
        {
            if (block.Terminator == null) continue;

            switch (block.Terminator)
            {
                case MirGoto gotoTerm:
                    if (predecessors.TryGetValue(gotoTerm.Target, out var preds1))
                        preds1.Add(block.Id);
                    break;
                case MirSwitch sw:
                    foreach (var branch in sw.Branches)
                    {
                        if (predecessors.TryGetValue(branch.Target, out var preds2))
                            preds2.Add(block.Id);
                    }
                    if (sw.DefaultTarget.HasValue &&
                        predecessors.TryGetValue(sw.DefaultTarget.Value, out var preds3))
                    {
                        preds3.Add(block.Id);
                    }
                    break;
            }
        }

        return predecessors;
    }

    // ---- Shared helpers ----

    /// <summary>
    /// Try to find and remove a matching slot from the available stack.
    /// Returns true if a match was found.
    /// </summary>
    private static bool TryMatchSlot(
        Stack<(int Slot, TypeId TypeId)> available,
        TypeId targetTypeId,
        out int matchedSlot)
    {
        matchedSlot = -1;
        var matchIndex = -1;
        var items = available.ToArray();

        for (int j = 0; j < items.Length; j++)
        {
            if (items[j].TypeId.Equals(targetTypeId))
            {
                matchIndex = j;
                break;
            }
        }

        if (matchIndex < 0)
            return false;

        matchedSlot = items[matchIndex].Slot;

        // Remove matched item, preserve order
        var remaining = new Stack<(int, TypeId)>();
        for (int j = items.Length - 1; j >= 0; j--)
        {
            if (j != matchIndex)
                remaining.Push(items[j]);
        }

        // Replace the stack contents
        available.Clear();
        while (remaining.Count > 0)
            available.Push(remaining.Pop());

        return true;
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
