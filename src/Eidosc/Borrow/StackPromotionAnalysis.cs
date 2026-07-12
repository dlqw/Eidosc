using Eidosc.Mir;

namespace Eidosc.Borrow;

/// <summary>
/// 栈分配提升信息
/// </summary>
public sealed record StackAllocInfo(int FieldCount, int TypeId, long PayloadSize);

/// <summary>
/// 栈分配提升提示
/// </summary>
public sealed class StackPromotionHints
{
    public HashSet<(BlockId Block, int Index)> StackAllocSites { get; } = [];
    public Dictionary<LocalId, StackAllocInfo> StackAllocInfoByLocal { get; } = [];
    public HashSet<LocalId> PromotedLocals { get; } = [];
}

/// <summary>
/// 栈分配提升分析器 —— 通过逃逸分析识别可以提升到栈的构造器分配。
/// </summary>
public sealed class StackPromotionAnalyzer
{
    private readonly MirFunc _function;

    public StackPromotionHints Hints { get; } = new();

    public StackPromotionAnalyzer(MirFunc function)
    {
        _function = function;
    }

    public void Analyze()
    {
        Hints.StackAllocSites.Clear();
        Hints.StackAllocInfoByLocal.Clear();
        Hints.PromotedLocals.Clear();

        var escaped = CollectEscapedLocals();
        IdentifyPromotableConstructorCalls(escaped);
    }

    public static bool MayHavePromotableConstructorCalls(MirFunc function)
    {
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (ReuseAnalyzer.IsHeapAllocatingConstructorCall(instruction, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private HashSet<LocalId> CollectEscapedLocals()
    {
        var escaped = new HashSet<LocalId>();
        var aliases = new Dictionary<LocalId, HashSet<LocalId>>();

        foreach (var block in _function.BasicBlocks)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];
                CollectEscapedFromInstruction(instr, i, escaped, aliases);
            }

            if (block.Terminator is MirReturn { Value: not null } ret)
            {
                CollectLocalsFromOperand(ret.Value, escaped);
            }

            if (block.Terminator is MirSwitch { Discriminant: var disc })
            {
                CollectLocalsFromOperand(disc, escaped);
            }
        }

        PropagateEscapesThroughAliases(escaped, aliases);

        return escaped;
    }

    private static void PropagateEscapesThroughAliases(
        HashSet<LocalId> escaped,
        Dictionary<LocalId, HashSet<LocalId>> aliases)
    {
        if (aliases.Count == 0) return;

        var queue = new Queue<LocalId>(escaped);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!aliases.TryGetValue(current, out var currentAliases)) continue;
            foreach (var alias in currentAliases)
            {
                if (escaped.Add(alias))
                {
                    queue.Enqueue(alias);
                }
            }
        }
    }

    private void CollectEscapedFromInstruction(
        MirInstruction instr,
        int instructionIndex,
        HashSet<LocalId> escaped,
        Dictionary<LocalId, HashSet<LocalId>> aliases)
    {
        switch (instr)
        {
            case MirCall call:
                foreach (var arg in call.Arguments)
                {
                    CollectLocalsFromOperand(arg, escaped);
                }
                CollectLocalsFromOperand(call.Function, escaped);
                break;

            case MirStore store:
                CollectLocalsFromOperand(store.Value, escaped);
                break;

            case MirCopy copy:
                AddAlias(copy.Source, copy.Target, aliases);
                break;

            case MirMove move:
                AddAlias(move.Source, move.Target, aliases);
                break;

            // Transitive escape: MirLoad establishes an alias between source and target.
            // If the load target later escapes (via MirCall, MirReturn, etc.),
            // the source is also marked as escaped through alias propagation.
            case MirLoad load:
                AddAliasFromOperand(load.Source, load.Target, aliases);
                break;
        }
    }

    private static void AddAlias(MirOperand? source, MirOperand? target, Dictionary<LocalId, HashSet<LocalId>> aliases)
    {
        if (source is not MirPlace { Kind: PlaceKind.Local, Local: var srcLocal }) return;
        if (target is not MirPlace { Kind: PlaceKind.Local, Local: var tgtLocal }) return;

        if (!aliases.TryGetValue(srcLocal, out var srcSet))
        {
            srcSet = [];
            aliases[srcLocal] = srcSet;
        }
        srcSet.Add(tgtLocal);

        if (!aliases.TryGetValue(tgtLocal, out var tgtSet))
        {
            tgtSet = [];
            aliases[tgtLocal] = tgtSet;
        }
        tgtSet.Add(srcLocal);
    }

    private static void AddAliasFromOperand(MirOperand source, MirPlace target, Dictionary<LocalId, HashSet<LocalId>> aliases)
    {
        if (source is not MirPlace { Kind: PlaceKind.Local, Local: var srcLocal }) return;
        if (target is not { Kind: PlaceKind.Local, Local: var tgtLocal }) return;

        if (!aliases.TryGetValue(srcLocal, out var srcSet))
        {
            srcSet = [];
            aliases[srcLocal] = srcSet;
        }
        srcSet.Add(tgtLocal);

        if (!aliases.TryGetValue(tgtLocal, out var tgtSet))
        {
            tgtSet = [];
            aliases[tgtLocal] = tgtSet;
        }
        tgtSet.Add(srcLocal);
    }

    private static void CollectLocalsFromOperand(MirOperand? operand, HashSet<LocalId> result)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var localId })
        {
            result.Add(localId);
        }
    }

    private void IdentifyPromotableConstructorCalls(HashSet<LocalId> escaped)
    {
        foreach (var block in _function.BasicBlocks)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var instr = block.Instructions[i];

                if (!ReuseAnalyzer.IsHeapAllocatingConstructorCall(instr, out _))
                {
                    continue;
                }

                var call = (MirCall)instr;
                if (call.Target is not MirPlace { Kind: PlaceKind.Local, Local: var targetLocal })
                {
                    continue;
                }

                if (!escaped.Contains(targetLocal))
                {
                    if (call.Function is not MirFunctionRef { Name: var ctorName } ctorRef)
                    {
                        continue;
                    }

                    if (call.Arguments.Any(arg => TypeSemantics.IsManagedType(arg.TypeId)))
                    {
                        continue;
                    }

                    var fieldCount = call.Arguments.Count;
                    var typeId = AdtConstructorTypeId.Compute(ctorRef.FunctionId, ctorRef.SymbolId, ctorName);
                    var payloadSize = Math.Max(8L, fieldCount * 8L);

                    Hints.StackAllocSites.Add((block.Id, i));
                    Hints.StackAllocInfoByLocal[targetLocal] = new StackAllocInfo(fieldCount, typeId, payloadSize);
                    Hints.PromotedLocals.Add(targetLocal);
                }
            }
        }
    }
}
