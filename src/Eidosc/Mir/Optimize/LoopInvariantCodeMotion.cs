namespace Eidosc.Mir.Optimize;

public sealed class LoopInvariantCodeMotion : IMirOptimizationPass, IFunctionOptimizationSummaryConsumer
{
    private FunctionOptimizationSummaryIndex? _functionSummaries;
    private IReadOnlyDictionary<string, int> _parameterCounts =
        new Dictionary<string, int>(StringComparer.Ordinal);

    FunctionOptimizationSummaryIndex IFunctionOptimizationSummaryConsumer.FunctionSummaries
    {
        set => _functionSummaries = value;
    }

    public string Name => "LoopInvariantCodeMotion";

    public MirModule Run(MirModule module)
    {
        _parameterCounts = MirCallOptimization.BuildParameterCounts(module);
        List<MirFunc>? functions = null;
        for (var i = 0; i < module.Functions.Count; i++)
        {
            var original = module.Functions[i];
            var optimized = OptimizeFunction(original);
            if (functions != null)
            {
                functions.Add(optimized);
            }
            else if (!ReferenceEquals(original, optimized))
            {
                functions = new List<MirFunc>(module.Functions.Count);
                functions.AddRange(module.Functions.Take(i));
                functions.Add(optimized);
            }
        }

        return functions == null ? module : MirOptimizationCloner.WithFunctions(module, functions);
    }

    private MirFunc OptimizeFunction(MirFunc function)
    {
        if (function.IsExternal || function.BasicBlocks.Count < 2)
        {
            return function;
        }

        var current = function;
        var changed = false;
        while (TryHoistOneLoop(current, out var optimized))
        {
            current = optimized;
            changed = true;
        }

        return changed ? current : function;
    }

    private bool TryHoistOneLoop(MirFunc function, out MirFunc optimized)
    {
        optimized = function;
        var cfg = new ControlFlowGraph(function);
        var blocksById = function.BasicBlocks.ToDictionary(static block => block.Id);

        foreach (var headerId in FindLoopHeaders(function, cfg).OrderBy(static id => id.Value))
        {
            var loopBlocks = CollectNaturalLoop(function, cfg, headerId);
            var outsidePredecessors = cfg.GetPredecessors(headerId)
                .Where(predecessor => !loopBlocks.Contains(predecessor))
                .ToList();
            if (outsidePredecessors.Count != 1 ||
                !blocksById.TryGetValue(outsidePredecessors[0], out var preheader) ||
                preheader.Terminator is not MirGoto { Target: var target } ||
                target != headerId ||
                cfg.GetSuccessors(preheader.Id).Count != 1 ||
                !blocksById.TryGetValue(headerId, out var header))
            {
                continue;
            }

            var loopDefinedLocals = CollectDefinedLocals(loopBlocks, blocksById);
            var definitionCounts = CountDefinitions(function);
            var candidates = new List<(int Index, MirCall Call)>();
            for (var i = 0; i < header.Instructions.Count; i++)
            {
                if (header.Instructions[i] is not MirCall call ||
                    !MirCallOptimization.TryGetReusableCall(
                        call,
                        _functionSummaries,
                        _parameterCounts,
                        out _,
                        out var targetPlace) ||
                    definitionCounts.GetValueOrDefault(targetPlace.Local) != 1 ||
                    !MirCallOptimization.TryCollectLocalDependencies(call.Arguments, out var dependencies) ||
                    dependencies.Contains(targetPlace.Local) ||
                    dependencies.Overlaps(loopDefinedLocals) ||
                    HeaderPrefixUsesLocal(header.Instructions, i, targetPlace.Local))
                {
                    continue;
                }

                candidates.Add((i, call));
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            var candidateIndices = candidates.Select(static candidate => candidate.Index).ToHashSet();
            var newPreheaderInstructions = preheader.Instructions.ToList();
            newPreheaderInstructions.AddRange(candidates.Select(static candidate => (MirInstruction)candidate.Call));
            var newHeaderInstructions = header.Instructions
                .Where((_, index) => !candidateIndices.Contains(index))
                .ToList();
            var newBlocks = function.BasicBlocks
                .Select(block => block.Id == preheader.Id
                    ? MirOptimizationCloner.WithInstructions(block, newPreheaderInstructions)
                    : block.Id == header.Id
                        ? MirOptimizationCloner.WithInstructions(block, newHeaderInstructions)
                        : block)
                .ToList();
            optimized = MirOptimizationCloner.WithBlocks(function, newBlocks);
            return true;
        }

        return false;
    }

    private static HashSet<BlockId> FindLoopHeaders(MirFunc function, ControlFlowGraph cfg)
    {
        var headers = new HashSet<BlockId>();
        foreach (var block in function.BasicBlocks)
        {
            foreach (var successor in cfg.GetSuccessors(block.Id))
            {
                if (cfg.GetDominators(block.Id).Contains(successor))
                {
                    headers.Add(successor);
                }
            }
        }

        return headers;
    }

    private static HashSet<BlockId> CollectNaturalLoop(
        MirFunc function,
        ControlFlowGraph cfg,
        BlockId header)
    {
        var loop = new HashSet<BlockId> { header };
        var latches = function.BasicBlocks
            .Where(block => cfg.GetSuccessors(block.Id).Contains(header) &&
                            cfg.GetDominators(block.Id).Contains(header))
            .Select(static block => block.Id)
            .ToList();
        var pending = new Stack<BlockId>(latches);
        while (pending.TryPop(out var block))
        {
            if (!loop.Add(block))
            {
                continue;
            }

            foreach (var predecessor in cfg.GetPredecessors(block))
            {
                if (!loop.Contains(predecessor))
                {
                    pending.Push(predecessor);
                }
            }
        }

        return loop;
    }

    private static HashSet<LocalId> CollectDefinedLocals(
        HashSet<BlockId> loopBlocks,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blocksById)
    {
        var result = new HashSet<LocalId>();
        foreach (var blockId in loopBlocks)
        {
            foreach (var instruction in blocksById[blockId].Instructions)
            {
                if (MirCallOptimization.GetDefinedLocal(instruction) is { } local)
                {
                    result.Add(local);
                }
            }
        }

        return result;
    }

    private static Dictionary<LocalId, int> CountDefinitions(MirFunc function)
    {
        var counts = new Dictionary<LocalId, int>();
        foreach (var instruction in function.BasicBlocks.SelectMany(static block => block.Instructions))
        {
            if (MirCallOptimization.GetDefinedLocal(instruction) is not { } local)
            {
                continue;
            }

            counts[local] = counts.GetValueOrDefault(local) + 1;
        }

        return counts;
    }

    private static bool HeaderPrefixUsesLocal(
        IReadOnlyList<MirInstruction> instructions,
        int exclusiveEnd,
        LocalId local)
    {
        for (var i = 0; i < exclusiveEnd; i++)
        {
            if (InstructionUsesLocal(instructions[i], local))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InstructionUsesLocal(MirInstruction instruction, LocalId local) => instruction switch
    {
        MirAssign assign => OperandUsesLocal(assign.Source, local),
        MirCall call => OperandUsesLocal(call.Function, local) ||
                        call.Arguments.Any(argument => OperandUsesLocal(argument, local)),
        MirBinOp binOp => OperandUsesLocal(binOp.Left, local) || OperandUsesLocal(binOp.Right, local),
        MirUnaryOp unaryOp => OperandUsesLocal(unaryOp.Operand, local),
        MirLoad load => OperandUsesLocal(load.Source, local),
        MirStore store => PlaceUsesLocal(store.Target, local) || OperandUsesLocal(store.Value, local),
        MirDrop drop => OperandUsesLocal(drop.Value, local),
        MirCopy copy => PlaceUsesLocal(copy.Source, local),
        MirMove move => PlaceUsesLocal(move.Source, local),
        _ => false
    };

    private static bool OperandUsesLocal(MirOperand operand, LocalId local) =>
        operand is MirPlace place && PlaceUsesLocal(place, local);

    private static bool PlaceUsesLocal(MirPlace place, LocalId local) =>
        place.Kind == PlaceKind.Local && place.Local == local ||
        place.Base != null && PlaceUsesLocal(place.Base, local) ||
        place.Index != null && OperandUsesLocal(place.Index, local);
}
