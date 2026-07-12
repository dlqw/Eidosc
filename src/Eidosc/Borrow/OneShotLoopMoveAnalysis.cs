using Eidosc.Mir;

namespace Eidosc.Borrow;

internal static class OneShotLoopMoveAnalysis
{
    public static Dictionary<(BlockId Predecessor, BlockId Successor), HashSet<LocalId>> CollectBackedgeSuppressions(
        MirFunc function,
        ControlFlowGraph cfg)
    {
        var blocks = function.BasicBlocks.ToArray();
        var blockById = blocks.ToDictionary(static block => block.Id);
        var guardedMoves = CollectOneShotGuardedMoves(blocks, blockById);
        var suppressions = new Dictionary<(BlockId Predecessor, BlockId Successor), HashSet<LocalId>>();
        if (guardedMoves.Count == 0)
        {
            return suppressions;
        }

        foreach (var predecessorBlock in blocks)
        {
            foreach (var successor in cfg.GetSuccessors(predecessorBlock.Id))
            {
                if (!blockById.TryGetValue(successor, out var headerBlock) ||
                    !cfg.GetDominators(predecessorBlock.Id).Contains(headerBlock.Id))
                {
                    continue;
                }

                foreach (var move in guardedMoves)
                {
                    if (!cfg.GetDominators(move.GuardBlock).Contains(headerBlock.Id) ||
                        !CanReach(move.MoveBlock, predecessorBlock.Id, cfg) ||
                        !IsIncrementedOnAllPaths(move.MoveBlock, predecessorBlock.Id, move.InductionLocal, cfg, blockById) ||
                        IsLocalMutatedInLoop(move.InvariantLocal, headerBlock.Id, predecessorBlock.Id, cfg, blocks))
                    {
                        continue;
                    }

                    var key = (predecessorBlock.Id, successor);
                    if (!suppressions.TryGetValue(key, out var locals))
                    {
                        locals = [];
                        suppressions[key] = locals;
                    }

                    locals.Add(move.MovedLocal);
                }
            }
        }

        return suppressions;
    }

    private static List<OneShotGuardedMove> CollectOneShotGuardedMoves(
        MirBasicBlock[] blocks,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blockById)
    {
        var moves = new List<OneShotGuardedMove>();
        foreach (var block in blocks)
        {
            if (!TryMatchEqualitySwitch(block, blocks, out var inductionLocal, out var invariantLocal, out var trueTarget) ||
                !blockById.TryGetValue(trueTarget, out var trueBlock))
            {
                continue;
            }

            foreach (var instruction in trueBlock.Instructions)
            {
                if (instruction is MirMove
                    {
                        Source: { Kind: PlaceKind.Local, Local: var movedLocal },
                        Target.Kind: PlaceKind.Local
                    })
                {
                    moves.Add(new OneShotGuardedMove(block.Id, trueBlock.Id, inductionLocal, invariantLocal, movedLocal));
                }
            }
        }

        return moves;
    }

    private static bool TryMatchEqualitySwitch(
        MirBasicBlock block,
        MirBasicBlock[] blocks,
        out LocalId inductionLocal,
        out LocalId invariantLocal,
        out BlockId trueTarget)
    {
        inductionLocal = LocalId.None;
        invariantLocal = LocalId.None;
        trueTarget = BlockId.None;

        if (block.Terminator is not MirSwitch
            {
                Discriminant: MirPlace { Kind: PlaceKind.Local, Local: var discriminantLocal },
                Branches: var branches
            } ||
            branches.Count == 0)
        {
            return false;
        }

        var trueBranch = branches.FirstOrDefault(static branch =>
            branch.Value?.Value is MirConstantValue.BoolValue { Value: true });
        if (trueBranch == null)
        {
            return false;
        }

        trueTarget = trueBranch.Target;
        var definitions = BuildBlockLocalDefinitions(block);
        if (!TryResolveDefinitionLocal(discriminantLocal, definitions, out var conditionLocal) ||
            !definitions.TryGetValue(conditionLocal, out var conditionInstruction) ||
            conditionInstruction is not MirBinOp { Operator: BinaryOp.Eq } eq ||
            !TryResolveOperandToLocal(eq.Left, definitions, out var leftLocal) ||
            !TryResolveOperandToLocal(eq.Right, definitions, out var rightLocal))
        {
            return false;
        }

        var leftIncremented = IsLocalIncrementedOnBackedgeCandidate(blocks, leftLocal);
        var rightIncremented = IsLocalIncrementedOnBackedgeCandidate(blocks, rightLocal);
        if (leftIncremented && !rightIncremented)
        {
            inductionLocal = leftLocal;
            invariantLocal = rightLocal;
            return true;
        }

        if (rightIncremented && !leftIncremented)
        {
            inductionLocal = rightLocal;
            invariantLocal = leftLocal;
            return true;
        }

        return false;
    }

    private static bool IsLocalIncrementedOnBackedgeCandidate(MirBasicBlock[] blocks, LocalId localId)
    {
        foreach (var block in blocks)
        {
            if (BlockIncrementsLocal(block, localId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIncrementedOnAllPaths(
        BlockId start,
        BlockId target,
        LocalId inductionLocal,
        ControlFlowGraph cfg,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blockById)
    {
        return IsIncrementedOnAllPaths(
            start,
            target,
            inductionLocal,
            cfg,
            blockById,
            incremented: false,
            []);
    }

    private static bool IsIncrementedOnAllPaths(
        BlockId current,
        BlockId target,
        LocalId inductionLocal,
        ControlFlowGraph cfg,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blockById,
        bool incremented,
        HashSet<(BlockId Block, bool Incremented)> visited)
    {
        if (!visited.Add((current, incremented)))
        {
            return true;
        }

        if (!blockById.TryGetValue(current, out var block))
        {
            return false;
        }

        var nowIncremented = incremented || BlockIncrementsLocal(block, inductionLocal);
        if (current.Equals(target))
        {
            return nowIncremented;
        }

        var successors = cfg.GetSuccessors(current);
        if (successors.Count == 0)
        {
            return false;
        }

        foreach (var successor in successors)
        {
            if (!IsIncrementedOnAllPaths(successor, target, inductionLocal, cfg, blockById, nowIncremented, visited))
            {
                return false;
            }
        }

        return true;
    }

    private static bool BlockIncrementsLocal(MirBasicBlock block, LocalId localId)
    {
        var definitions = BuildBlockLocalDefinitions(block);
        foreach (var instruction in block.Instructions)
        {
            if (instruction is MirStore
                {
                    Target: { Kind: PlaceKind.Local, Local: var targetLocal },
                    Value: MirPlace { Kind: PlaceKind.Local, Local: var valueLocal }
                } &&
                targetLocal.Equals(localId) &&
                IsLocalPlusOne(valueLocal, localId, definitions))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLocalPlusOne(
        LocalId valueLocal,
        LocalId inductionLocal,
        Dictionary<LocalId, MirInstruction> definitions)
    {
        if (!TryResolveDefinitionLocal(valueLocal, definitions, out var resolvedLocal) ||
            !definitions.TryGetValue(resolvedLocal, out var instruction) ||
            instruction is not MirBinOp { Operator: BinaryOp.Add } add)
        {
            return false;
        }

        return (TryResolveOperandToLocal(add.Left, definitions, out var leftLocal) &&
                leftLocal.Equals(inductionLocal) &&
                IsOne(add.Right)) ||
               (TryResolveOperandToLocal(add.Right, definitions, out var rightLocal) &&
                rightLocal.Equals(inductionLocal) &&
                IsOne(add.Left));
    }

    private static bool IsLocalMutatedInLoop(
        LocalId localId,
        BlockId header,
        BlockId backedgePredecessor,
        ControlFlowGraph cfg,
        MirBasicBlock[] blocks)
    {
        foreach (var block in blocks)
        {
            if (!cfg.GetDominators(block.Id).Contains(header) ||
                !CanReach(block.Id, backedgePredecessor, cfg))
            {
                continue;
            }

            foreach (var instruction in block.Instructions)
            {
                if (instruction is MirStore { Target: { Kind: PlaceKind.Local, Local: var storeTarget } } &&
                    storeTarget.Equals(localId))
                {
                    return true;
                }

                if (instruction is MirMove { Source: { Kind: PlaceKind.Local, Local: var moveSource } } &&
                    moveSource.Equals(localId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool CanReach(BlockId start, BlockId target, ControlFlowGraph cfg)
    {
        if (start.Equals(target))
        {
            return true;
        }

        var visited = new HashSet<BlockId>();
        var pending = new Queue<BlockId>();
        pending.Enqueue(start);
        visited.Add(start);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var successor in cfg.GetSuccessors(current))
            {
                if (successor.Equals(target))
                {
                    return true;
                }

                if (visited.Add(successor))
                {
                    pending.Enqueue(successor);
                }
            }
        }

        return false;
    }

    private static Dictionary<LocalId, MirInstruction> BuildBlockLocalDefinitions(MirBasicBlock block)
    {
        var definitions = new Dictionary<LocalId, MirInstruction>();
        foreach (var instruction in block.Instructions)
        {
            if (GetInstructionTargetLocal(instruction, out var localId))
            {
                definitions[localId] = instruction;
            }
        }

        return definitions;
    }

    private static bool GetInstructionTargetLocal(MirInstruction instruction, out LocalId localId)
    {
        MirPlace? target = instruction switch
        {
            MirAssign assign => assign.Target,
            MirCall call => call.Target,
            MirLoad load => load.Target,
            MirCopy copy => copy.Target,
            MirMove move => move.Target,
            MirAlloc alloc => alloc.Target,
            MirBinOp { Target: MirPlace place } => place,
            MirUnaryOp { Target: MirPlace place } => place,
            _ => null
        };

        if (target is { Kind: PlaceKind.Local, Local: var targetLocal })
        {
            localId = targetLocal;
            return true;
        }

        localId = LocalId.None;
        return false;
    }

    private static bool TryResolveOperandToLocal(
        MirOperand operand,
        Dictionary<LocalId, MirInstruction> definitions,
        out LocalId localId)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var directLocal })
        {
            return TryResolveDefinitionLocal(directLocal, definitions, out localId);
        }

        localId = LocalId.None;
        return false;
    }

    private static bool TryResolveDefinitionLocal(
        LocalId localId,
        Dictionary<LocalId, MirInstruction> definitions,
        out LocalId resolvedLocal)
    {
        var current = localId;
        var seen = new HashSet<LocalId>();
        while (definitions.TryGetValue(current, out var instruction) && seen.Add(current))
        {
            if (instruction is MirCopy { Source: { Kind: PlaceKind.Local, Local: var source } })
            {
                current = source;
                continue;
            }

            break;
        }

        resolvedLocal = current;
        return true;
    }

    private static bool IsOne(MirOperand operand)
    {
        return operand is MirConstant { Value: MirConstantValue.IntValue { Value: 1 } };
    }
}

internal readonly record struct OneShotGuardedMove(
    BlockId GuardBlock,
    BlockId MoveBlock,
    LocalId InductionLocal,
    LocalId InvariantLocal,
    LocalId MovedLocal);
