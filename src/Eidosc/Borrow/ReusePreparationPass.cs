using Eidosc.Mir;
using Eidosc.Mir.Optimize;

namespace Eidosc.Borrow;

/// <summary>
/// Moves a dead owned value's block-local drop immediately before a
/// same-representation constructor. The runtime reuse primitive performs the
/// final uniqueness check, so shared values safely fall back to allocation.
/// </summary>
public sealed class ReusePreparationPass : IMirOptimizationPass
{
    public string Name => "ReusePreparation";

    public MirModule Run(MirModule module)
    {
        foreach (var function in module.Functions)
        {
            var ownedAtBlockEntry = AnalyzeDefinitelyOwnedLocals(function);
            foreach (var block in function.BasicBlocks)
            {
                PrepareBlock(function, block, ownedAtBlockEntry.GetValueOrDefault(block.Id, []));
            }
        }

        return module;
    }

    private static void PrepareBlock(
        MirFunc function,
        MirBasicBlock block,
        IReadOnlySet<LocalId> ownedAtBlockEntry)
    {
        for (var callIndex = 0; callIndex < block.Instructions.Count; callIndex++)
        {
            if (!ReuseAnalyzer.IsHeapAllocatingConstructorCall(
                    block.Instructions[callIndex],
                    out var targetTypeId))
            {
                continue;
            }

            var call = (MirCall)block.Instructions[callIndex];
            for (var dropIndex = callIndex + 1; dropIndex < block.Instructions.Count; dropIndex++)
            {
                if (block.Instructions[dropIndex] is not MirDrop
                    {
                        Value: MirPlace
                        {
                            Kind: PlaceKind.Local,
                            Local: var droppedLocal,
                            TypeId: var droppedTypeId
                        }
                    } drop ||
                    droppedTypeId != targetTypeId ||
                    !HasOwnedValueBeforeCall(block, callIndex, droppedLocal, ownedAtBlockEntry) ||
                    UsesLocal(call, droppedLocal) ||
                    block.Instructions
                        .Skip(callIndex + 1)
                        .Take(dropIndex - callIndex - 1)
                        .Any(instruction => UsesLocal(instruction, droppedLocal)))
                {
                    continue;
                }

                if (!TryMaterializeConstructorArguments(block, callIndex, call, droppedLocal))
                {
                    continue;
                }

                block.Instructions.RemoveAt(dropIndex);
                block.Instructions.Insert(callIndex, drop);
                callIndex++;
                break;
            }
        }
    }

    private static bool HasOwnedValueBeforeCall(
        MirBasicBlock block,
        int callIndex,
        LocalId local,
        IReadOnlySet<LocalId> ownedAtBlockEntry)
    {
        var ownsValue = ownedAtBlockEntry.Contains(local);
        for (var index = 0; index < callIndex; index++)
        {
            var instruction = block.Instructions[index];
            if (ConsumesLocalOwnership(instruction, local))
            {
                ownsValue = false;
            }

            if (DefinesLocal(instruction, local))
            {
                ownsValue = true;
            }
        }

        return ownsValue;
    }

    private static Dictionary<BlockId, HashSet<LocalId>> AnalyzeDefinitelyOwnedLocals(MirFunc function)
    {
        var cfg = new ControlFlowGraph(function);
        var allLocals = function.Locals.Select(static local => local.Id).ToHashSet();
        var entryOwnership = function.Locals
            .Where(static local => local.IsParameter)
            .Select(static local => local.Id)
            .ToHashSet();
        var ownedIn = function.BasicBlocks.ToDictionary(
            static block => block.Id,
            _ => new HashSet<LocalId>(allLocals));
        var ownedOut = function.BasicBlocks.ToDictionary(
            static block => block.Id,
            _ => new HashSet<LocalId>(allLocals));

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in function.BasicBlocks)
            {
                var incoming = new List<IReadOnlySet<LocalId>>();
                if (block.Id == function.EntryBlockId)
                {
                    incoming.Add(entryOwnership);
                }

                foreach (var predecessor in cfg.GetPredecessors(block.Id))
                {
                    if (ownedOut.TryGetValue(predecessor, out var predecessorOut))
                    {
                        incoming.Add(predecessorOut);
                    }
                }

                var nextIn = incoming.Count == 0
                    ? []
                    : new HashSet<LocalId>(incoming[0]);
                for (var index = 1; index < incoming.Count; index++)
                {
                    nextIn.IntersectWith(incoming[index]);
                }

                var nextOut = new HashSet<LocalId>(nextIn);
                foreach (var instruction in block.Instructions)
                {
                    ApplyDefiniteOwnershipTransfer(instruction, nextOut);
                }

                if (!ownedIn[block.Id].SetEquals(nextIn))
                {
                    ownedIn[block.Id] = nextIn;
                    changed = true;
                }

                if (!ownedOut[block.Id].SetEquals(nextOut))
                {
                    ownedOut[block.Id] = nextOut;
                    changed = true;
                }
            }
        }

        return ownedIn;
    }

    private static void ApplyDefiniteOwnershipTransfer(
        MirInstruction instruction,
        HashSet<LocalId> owned)
    {
        static bool Consume(MirOperand? operand, HashSet<LocalId> state)
        {
            return operand is MirPlace { Kind: PlaceKind.Local, Local: var local } &&
                   state.Remove(local);
        }

        static void Define(MirOperand? target, HashSet<LocalId> state, bool producesOwnedValue = true)
        {
            if (target is not MirPlace { Kind: PlaceKind.Local } place)
            {
                return;
            }

            if (producesOwnedValue)
            {
                state.Add(place.Local);
            }
            else
            {
                state.Remove(place.Local);
            }
        }

        switch (instruction)
        {
            case MirAssign assign:
                var assignOwns = assign.Source is not MirPlace { Kind: PlaceKind.Local } ||
                                 Consume(assign.Source, owned);
                Define(assign.Target, owned, assignOwns);
                break;
            case MirCaseInject injection when injection.Target is MirPlace target:
                var injectionOwns = injection.Operand is not MirPlace { Kind: PlaceKind.Local } ||
                                    Consume(injection.Operand, owned);
                Define(target, owned, injectionOwns);
                break;
            case MirCall call:
                for (var index = 0; index < call.Arguments.Count; index++)
                {
                    if (!call.BorrowedArgumentIndices.Contains(index))
                    {
                        Consume(call.Arguments[index], owned);
                    }
                }

                Define(call.Target, owned);
                break;
            case MirLoad load:
                Define(load.Target, owned, !load.CreatesBorrowAlias);
                break;
            case MirStore store:
                var storeOwns = store.Value is not MirPlace { Kind: PlaceKind.Local } ||
                                Consume(store.Value, owned);
                Define(store.Target, owned, storeOwns);
                break;
            case MirCopy copy:
                Define(copy.Target, owned);
                break;
            case MirMove move:
                var moveOwns = Consume(move.Source, owned);
                Define(move.Target, owned, moveOwns);
                break;
            case MirDrop drop:
                Consume(drop.Value, owned);
                break;
            case MirAlloc alloc:
                Define(alloc.Target, owned);
                break;
            case MirBinOp binary:
                Define(binary.Target, owned);
                break;
            case MirUnaryOp unary:
                Define(unary.Target, owned);
                break;
            case MirSelect select:
                Define(select.Target, owned);
                break;
        }
    }

    private static bool ConsumesLocalOwnership(MirInstruction instruction, LocalId local) => instruction switch
    {
        MirAssign { Source: MirPlace { Kind: PlaceKind.Local, Local: var source } } => source == local,
        MirCaseInject { Operand: MirPlace { Kind: PlaceKind.Local, Local: var source } } => source == local,
        MirCall call => call.Arguments
            .Select((argument, index) => (argument, index))
            .Any(pair => !call.BorrowedArgumentIndices.Contains(pair.index) &&
                         pair.argument is MirPlace { Kind: PlaceKind.Local, Local: var source } &&
                         source == local),
        MirStore { Value: MirPlace { Kind: PlaceKind.Local, Local: var source } } => source == local,
        MirMove { Source: { Kind: PlaceKind.Local, Local: var source } } => source == local,
        MirDrop { Value: MirPlace { Kind: PlaceKind.Local, Local: var source } } => source == local,
        _ => false
    };

    private static bool TryMaterializeConstructorArguments(
        MirBasicBlock block,
        int callIndex,
        MirCall call,
        LocalId droppedLocal)
    {
        var loadIndices = new HashSet<int>();
        foreach (var argument in call.Arguments)
        {
            if (!TryCollectBorrowedLoads(
                    block,
                    callIndex,
                    argument,
                    droppedLocal,
                    loadIndices,
                    new HashSet<LocalId>()))
            {
                return false;
            }
        }

        foreach (var loadIndex in loadIndices)
        {
            var load = (MirLoad)block.Instructions[loadIndex];
            block.Instructions[loadIndex] = load with { CreatesBorrowAlias = false };
        }

        return true;
    }

    private static bool TryCollectBorrowedLoads(
        MirBasicBlock block,
        int beforeIndex,
        MirOperand operand,
        LocalId droppedLocal,
        HashSet<int> loadIndices,
        HashSet<LocalId> visiting)
    {
        if (operand is not MirPlace place)
        {
            return true;
        }

        if (ContainsLocal(place, droppedLocal))
        {
            return false;
        }

        foreach (var local in EnumerateLocals(place))
        {
            if (!TryCollectBorrowedLoads(
                    block,
                    beforeIndex,
                    local,
                    droppedLocal,
                    loadIndices,
                    visiting))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCollectBorrowedLoads(
        MirBasicBlock block,
        int beforeIndex,
        LocalId local,
        LocalId droppedLocal,
        HashSet<int> loadIndices,
        HashSet<LocalId> visiting)
    {
        if (local == droppedLocal)
        {
            return false;
        }

        if (!visiting.Add(local))
        {
            return true;
        }

        try
        {
            for (var index = beforeIndex - 1; index >= 0; index--)
            {
                var instruction = block.Instructions[index];
                MirOperand? source = instruction switch
                {
                    MirAssign { Target.Kind: PlaceKind.Local, Target.Local: var target } assign
                        when target == local => assign.Source,
                    MirStore { Target.Kind: PlaceKind.Local, Target.Local: var target } store
                        when target == local => store.Value,
                    MirMove { Target.Kind: PlaceKind.Local, Target.Local: var target } move
                        when target == local => move.Source,
                    MirCopy { Target.Kind: PlaceKind.Local, Target.Local: var target }
                        when target == local => null,
                    MirLoad { Target.Kind: PlaceKind.Local, Target.Local: var target } loadDefinition
                        when target == local => loadDefinition.Source,
                    _ => null
                };

                if (!DefinesLocal(instruction, local))
                {
                    continue;
                }

                if (instruction is MirCopy)
                {
                    return true;
                }

                if (instruction is MirLoad load)
                {
                    if (!load.CreatesBorrowAlias)
                    {
                        return true;
                    }

                    if (!OperandDependsOnLocal(block, index, load.Source, droppedLocal, new HashSet<LocalId>()))
                    {
                        return true;
                    }

                    if (load.IsMutableBorrow)
                    {
                        return false;
                    }

                    if (TypeSemantics.IsManagedType(load.Target.TypeId))
                    {
                        loadIndices.Add(index);
                    }

                    return true;
                }

                return source == null || TryCollectBorrowedLoads(
                    block,
                    index,
                    source,
                    droppedLocal,
                    loadIndices,
                    visiting);
            }

            return true;
        }
        finally
        {
            visiting.Remove(local);
        }
    }

    private static bool OperandDependsOnLocal(
        MirBasicBlock block,
        int beforeIndex,
        MirOperand operand,
        LocalId expected,
        HashSet<LocalId> visiting)
    {
        if (operand is not MirPlace place)
        {
            return false;
        }

        if (ContainsLocal(place, expected))
        {
            return true;
        }

        return EnumerateLocals(place).Any(local =>
            LocalDependsOnLocal(block, beforeIndex, local, expected, visiting));
    }

    private static bool LocalDependsOnLocal(
        MirBasicBlock block,
        int beforeIndex,
        LocalId local,
        LocalId expected,
        HashSet<LocalId> visiting)
    {
        if (local == expected)
        {
            return true;
        }

        if (!visiting.Add(local))
        {
            return false;
        }

        try
        {
            for (var index = beforeIndex - 1; index >= 0; index--)
            {
                var instruction = block.Instructions[index];
                MirOperand? source = instruction switch
                {
                    MirAssign { Target.Kind: PlaceKind.Local, Target.Local: var target } assign
                        when target == local => assign.Source,
                    MirStore { Target.Kind: PlaceKind.Local, Target.Local: var target } store
                        when target == local => store.Value,
                    MirMove { Target.Kind: PlaceKind.Local, Target.Local: var target } move
                        when target == local => move.Source,
                    MirCopy { Target.Kind: PlaceKind.Local, Target.Local: var target } copy
                        when target == local => copy.Source,
                    MirLoad { Target.Kind: PlaceKind.Local, Target.Local: var target } load
                        when target == local => load.Source,
                    _ => null
                };

                if (!DefinesLocal(instruction, local))
                {
                    continue;
                }

                return source != null && OperandDependsOnLocal(block, index, source, expected, visiting);
            }

            return false;
        }
        finally
        {
            visiting.Remove(local);
        }
    }

    private static bool DefinesLocal(MirInstruction instruction, LocalId local) => instruction switch
    {
        MirAssign { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirStore { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirMove { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirCopy { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirLoad { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirCall { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirCaseInject { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirAlloc { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        _ => false
    };

    private static IEnumerable<LocalId> EnumerateLocals(MirPlace place)
    {
        if (place.Kind == PlaceKind.Local)
        {
            yield return place.Local;
        }

        if (place.Base is MirPlace basePlace)
        {
            foreach (var local in EnumerateLocals(basePlace))
            {
                yield return local;
            }
        }

        if (place.Index is MirPlace indexPlace)
        {
            foreach (var local in EnumerateLocals(indexPlace))
            {
                yield return local;
            }
        }
    }

    private static bool UsesLocal(MirInstruction instruction, LocalId local)
    {
        return instruction switch
        {
            MirAssign assign => ContainsLocal(assign.Source, local),
            MirCaseInject injection => ContainsLocal(injection.Operand, local),
            MirCall call => ContainsLocal(call.Function, local) ||
                            call.Arguments.Any(argument => ContainsLocal(argument, local)),
            MirBinOp binary => ContainsLocal(binary.Left, local) || ContainsLocal(binary.Right, local),
            MirUnaryOp unary => ContainsLocal(unary.Operand, local),
            MirSelect select => ContainsLocal(select.Condition, local) ||
                                ContainsLocal(select.TrueValue, local) ||
                                ContainsLocal(select.FalseValue, local),
            MirLoad load => ContainsLocal(load.Source, local),
            MirStore store => ContainsLocal(store.Target, local) || ContainsLocal(store.Value, local),
            MirDrop drop => ContainsLocal(drop.Value, local),
            MirCopy copy => ContainsLocal(copy.Source, local),
            MirMove move => ContainsLocal(move.Source, local),
            _ => false
        };
    }

    private static bool ContainsLocal(MirOperand? operand, LocalId local)
    {
        if (operand is not MirPlace place)
        {
            return false;
        }

        return place.Kind == PlaceKind.Local && place.Local == local ||
               ContainsLocal(place.Base, local) ||
               ContainsLocal(place.Index, local);
    }
}
