using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

// Block data flow analysis, predecessor maps, state merging, equality checks
public sealed partial class MirGenericSpecializer
{
    private static readonly Dictionary<LocalId, LocalCallBinding> EmptyLocalFunctionState = new(0);

    private static BlockId ResolveEntryBlockId(
        MirFunc function,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blocksById)
    {
        if (function.EntryBlockId.IsValid && blocksById.ContainsKey(function.EntryBlockId))
        {
            return function.EntryBlockId;
        }

        return function.BasicBlocks[0].Id;
    }

    private static Dictionary<BlockId, List<BlockId>> BuildPredecessorMap(MirFunc function)
    {
        var predecessorsByBlock = new Dictionary<BlockId, List<BlockId>>(function.BasicBlocks.Count);
        foreach (var block in function.BasicBlocks)
        {
            if (!predecessorsByBlock.ContainsKey(block.Id))
            {
                predecessorsByBlock[block.Id] = [];
            }

            AddPredecessorEdges(block, predecessorsByBlock);
        }

        return predecessorsByBlock;
    }

    private static void AddPredecessorEdges(
        MirBasicBlock block,
        Dictionary<BlockId, List<BlockId>> predecessorsByBlock)
    {
        switch (block.Terminator)
        {
            case MirGoto jump:
                AddPredecessorEdge(jump.Target, block.Id, predecessorsByBlock);
                break;
            case MirSwitch sw:
                foreach (var branch in sw.Branches)
                {
                    AddPredecessorEdge(branch.Target, block.Id, predecessorsByBlock);
                }

                if (sw.DefaultTarget.HasValue)
                {
                    AddPredecessorEdge(sw.DefaultTarget.Value, block.Id, predecessorsByBlock);
                }
                break;
        }
    }

    private static void AddPredecessorEdge(
        BlockId successorId,
        BlockId predecessorId,
        Dictionary<BlockId, List<BlockId>> predecessorsByBlock)
    {
        if (!predecessorsByBlock.TryGetValue(successorId, out var predecessors))
        {
            predecessors = [];
            predecessorsByBlock[successorId] = predecessors;
        }

        predecessors.Add(predecessorId);
    }

    private static void EnqueueSuccessorBlockIds(
        MirBasicBlock block,
        HashSet<BlockId> queuedBlockIds,
        Queue<BlockId> pendingBlockIds)
    {
        switch (block.Terminator)
        {
            case MirGoto jump:
                EnqueueSuccessorBlockId(jump.Target, queuedBlockIds, pendingBlockIds);
                break;
            case MirSwitch sw:
                foreach (var branch in sw.Branches)
                {
                    EnqueueSuccessorBlockId(branch.Target, queuedBlockIds, pendingBlockIds);
                }

                if (sw.DefaultTarget.HasValue)
                {
                    EnqueueSuccessorBlockId(sw.DefaultTarget.Value, queuedBlockIds, pendingBlockIds);
                }
                break;
        }
    }

    private static void EnqueueSuccessorBlockId(
        BlockId successorId,
        HashSet<BlockId> queuedBlockIds,
        Queue<BlockId> pendingBlockIds)
    {
        if (queuedBlockIds.Add(successorId))
        {
            pendingBlockIds.Enqueue(successorId);
        }
    }

    private IReadOnlyDictionary<LocalId, LocalCallBinding> MergeIncomingStateForBlock(
        BlockId blockId,
        BlockId entryBlockId,
        IReadOnlyDictionary<BlockId, List<BlockId>> predecessorsByBlock,
        IReadOnlyDictionary<BlockId, Dictionary<LocalId, LocalCallBinding>> outgoingStates)
    {
        if (blockId.Equals(entryBlockId))
        {
            return EmptyLocalFunctionState;
        }

        if (!predecessorsByBlock.TryGetValue(blockId, out var predecessors) || predecessors.Count == 0)
        {
            return EmptyLocalFunctionState;
        }

        if (predecessors.Count == 1)
        {
            return outgoingStates.TryGetValue(predecessors[0], out var predecessorState)
                ? predecessorState
                : EmptyLocalFunctionState;
        }

        Dictionary<LocalId, LocalCallBinding>? mergedState = null;
        foreach (var predecessorId in predecessors)
        {
            if (!outgoingStates.TryGetValue(predecessorId, out var predecessorState))
            {
                return EmptyLocalFunctionState;
            }

            if (mergedState == null)
            {
                mergedState = CloneLocalFunctionStateForMerge(predecessorState);
                continue;
            }

            IntersectLocalFunctionState(mergedState, predecessorState);
            if (mergedState.Count == 0)
            {
                break;
            }
        }

        return mergedState ?? EmptyLocalFunctionState;
    }

    private static void IntersectLocalFunctionState(
        Dictionary<LocalId, LocalCallBinding> accumulator,
        IReadOnlyDictionary<LocalId, LocalCallBinding> current)
    {
        List<LocalId>? localIdsToRemove = null;
        foreach (var (localId, accumulatorBinding) in accumulator)
        {
            if (!current.TryGetValue(localId, out var currentBinding) ||
                !AreSameLocalCallBinding(accumulatorBinding, currentBinding))
            {
                localIdsToRemove ??= [];
                localIdsToRemove.Add(localId);
            }
        }

        if (localIdsToRemove == null)
        {
            return;
        }

        foreach (var localId in localIdsToRemove)
        {
            accumulator.Remove(localId);
        }
    }

    private Dictionary<LocalId, LocalCallBinding> RentLocalFunctionState(
        IReadOnlyDictionary<LocalId, LocalCallBinding> source,
        Stack<Dictionary<LocalId, LocalCallBinding>> pool)
    {
        _stats.StateTransferClones++;
        _stats.StateCloneEntries += source.Count;
        Dictionary<LocalId, LocalCallBinding> state;
        if (pool.Count > 0)
        {
            _stats.StateTransferPoolHits++;
            state = pool.Pop();
        }
        else
        {
            state = new Dictionary<LocalId, LocalCallBinding>(Math.Max(source.Count, 4));
        }

        foreach (var (localId, binding) in source)
        {
            state[localId] = binding;
        }

        return state;
    }

    private static void ReturnLocalFunctionState(
        Dictionary<LocalId, LocalCallBinding> state,
        Stack<Dictionary<LocalId, LocalCallBinding>> pool)
    {
        state.Clear();
        pool.Push(state);
    }

    private Dictionary<LocalId, LocalCallBinding> CloneLocalFunctionStateForMerge(
        IReadOnlyDictionary<LocalId, LocalCallBinding> source)
    {
        _stats.StateMergeClones++;
        _stats.StateCloneEntries += source.Count;
        return source.Count == 0
            ? EmptyLocalFunctionState
            : new Dictionary<LocalId, LocalCallBinding>(source);
    }

    private Dictionary<LocalId, LocalCallBinding> CloneLocalFunctionStateForStorage(
        IReadOnlyDictionary<LocalId, LocalCallBinding> source)
    {
        _stats.StateStorageClones++;
        _stats.StateCloneEntries += source.Count;
        return source.Count == 0
            ? EmptyLocalFunctionState
            : new Dictionary<LocalId, LocalCallBinding>(source);
    }

    private static bool TryGetLocalFunctionState(
        IReadOnlyDictionary<BlockId, Dictionary<LocalId, LocalCallBinding>> states,
        BlockId blockId,
        out Dictionary<LocalId, LocalCallBinding> state)
    {
        if (states.TryGetValue(blockId, out var existing))
        {
            state = existing;
            return true;
        }

        state = null!;
        return false;
    }

    private static bool AreSameLocalFunctionState(
        IReadOnlyDictionary<LocalId, LocalCallBinding> left,
        IReadOnlyDictionary<LocalId, LocalCallBinding> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (localId, leftBinding) in left)
        {
            if (!right.TryGetValue(localId, out var rightBinding) ||
                !AreSameLocalCallBinding(leftBinding, rightBinding))
            {
                return false;
            }
        }

        return true;
    }

    private static LocalCallBinding CloneLocalCallBinding(LocalCallBinding binding)
    {
        return binding;
    }

    private static bool AreSameLocalCallBinding(LocalCallBinding left, LocalCallBinding right)
    {
        return AreSameFunctionRef(left.FunctionRef, right.FunctionRef) &&
               string.Equals(left.BoundArgumentKey, right.BoundArgumentKey, StringComparison.Ordinal) &&
               left.SupportsDirectApplication == right.SupportsDirectApplication;
    }

    private static bool AreSameFunctionRef(MirFunctionRef left, MirFunctionRef right)
    {
        return left.SymbolId.Equals(right.SymbolId) &&
               left.TypeId.Equals(right.TypeId) &&
               left.SymbolKind == right.SymbolKind &&
               left.SignatureTypeId.Equals(right.SignatureTypeId) &&
               Equals(left.FunctionId, right.FunctionId) &&
               TypeArgumentsEqual(left.TypeArgumentIds, right.TypeArgumentIds) &&
               left.TraitOwnerId.Equals(right.TraitOwnerId) &&
               left.TraitSelfPosition == right.TraitSelfPosition &&
               IntArgumentsEqual(left.TraitSelfParameterIndices, right.TraitSelfParameterIndices) &&
               left.TraitSelfInResult == right.TraitSelfInResult &&
               left.TraitMethodRole == right.TraitMethodRole &&
               string.Equals(left.Name, right.Name, StringComparison.Ordinal);
    }

    private static bool TypeArgumentsEqual(IReadOnlyList<TypeId> left, IReadOnlyList<TypeId> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IntArgumentsEqual(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
