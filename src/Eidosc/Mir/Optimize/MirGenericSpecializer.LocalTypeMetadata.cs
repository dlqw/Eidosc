namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private void RefreshFunctionOperandTypes(
        MirFunc function,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        _stats.OperandRefreshes++;
        for (var blockIndex = 0; blockIndex < function.BasicBlocks.Count; blockIndex++)
        {
            var block = function.BasicBlocks[blockIndex];
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                block.Instructions[instructionIndex] = RefreshInstructionOperandTypes(
                    block.Instructions[instructionIndex],
                    localTypes);
            }

            if (block.Terminator != null)
            {
                block.Terminator = RefreshTerminatorOperandTypes(block.Terminator, localTypes);
            }
        }
    }

    private MirInstruction RefreshInstructionOperandTypes(
        MirInstruction instruction,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        switch (instruction)
        {
            case MirAssign assign:
            {
                var target = RefreshPlaceType(assign.Target, localTypes);
                var source = RefreshOperandType(assign.Source, localTypes);
                return ReferenceEquals(target, assign.Target) && ReferenceEquals(source, assign.Source)
                    ? instruction
                    : assign with { Target = target, Source = source };
            }
            case MirCall call:
            {
                var target = call.Target is null ? null : RefreshPlaceType(call.Target, localTypes);
                var function = RefreshOperandType(call.Function, localTypes);
                var arguments = RefreshOperandList(call.Arguments, localTypes);
                return ReferenceEquals(target, call.Target) &&
                       ReferenceEquals(function, call.Function) &&
                       ReferenceEquals(arguments, call.Arguments)
                    ? instruction
                    : call with { Target = target, Function = function, Arguments = arguments };
            }
            case MirBinOp binOp:
            {
                var target = RefreshOperandType(binOp.Target, localTypes);
                var left = RefreshOperandType(binOp.Left, localTypes);
                var right = RefreshOperandType(binOp.Right, localTypes);
                return ReferenceEquals(target, binOp.Target) &&
                       ReferenceEquals(left, binOp.Left) &&
                       ReferenceEquals(right, binOp.Right)
                    ? instruction
                    : binOp with { Target = target, Left = left, Right = right };
            }
            case MirUnaryOp unaryOp:
            {
                var target = RefreshOperandType(unaryOp.Target, localTypes);
                var operand = RefreshOperandType(unaryOp.Operand, localTypes);
                return ReferenceEquals(target, unaryOp.Target) && ReferenceEquals(operand, unaryOp.Operand)
                    ? instruction
                    : unaryOp with { Target = target, Operand = operand };
            }
            case MirLoad load:
            {
                var target = RefreshPlaceType(load.Target, localTypes);
                var source = RefreshOperandType(load.Source, localTypes);
                return ReferenceEquals(target, load.Target) && ReferenceEquals(source, load.Source)
                    ? instruction
                    : load with { Target = target, Source = source };
            }
            case MirStore store:
            {
                var target = RefreshPlaceType(store.Target, localTypes);
                var value = RefreshOperandType(store.Value, localTypes);
                return ReferenceEquals(target, store.Target) && ReferenceEquals(value, store.Value)
                    ? instruction
                    : store with { Target = target, Value = value };
            }
            case MirDrop drop:
            {
                var value = RefreshOperandType(drop.Value, localTypes);
                return ReferenceEquals(value, drop.Value) ? instruction : drop with { Value = value };
            }
            case MirCopy copy:
            {
                var target = RefreshPlaceType(copy.Target, localTypes);
                var source = RefreshPlaceType(copy.Source, localTypes);
                return ReferenceEquals(target, copy.Target) && ReferenceEquals(source, copy.Source)
                    ? instruction
                    : copy with { Target = target, Source = source };
            }
            case MirMove move:
            {
                var target = RefreshPlaceType(move.Target, localTypes);
                var source = RefreshPlaceType(move.Source, localTypes);
                return ReferenceEquals(target, move.Target) && ReferenceEquals(source, move.Source)
                    ? instruction
                    : move with { Target = target, Source = source };
            }
            case MirAlloc alloc:
            {
                var target = RefreshPlaceType(alloc.Target, localTypes);
                return ReferenceEquals(target, alloc.Target) ? instruction : alloc with { Target = target };
            }
            default:
                return instruction;
        }
    }

    private MirTerminator RefreshTerminatorOperandTypes(
        MirTerminator terminator,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        switch (terminator)
        {
            case MirReturn ret:
            {
                var value = ret.Value is null ? null : RefreshOperandType(ret.Value, localTypes);
                return ReferenceEquals(value, ret.Value) ? terminator : ret with { Value = value };
            }
            case MirSwitch sw:
            {
                var discriminant = RefreshOperandType(sw.Discriminant, localTypes);
                var branches = RefreshSwitchBranches(sw.Branches, localTypes);
                return ReferenceEquals(discriminant, sw.Discriminant) && ReferenceEquals(branches, sw.Branches)
                    ? terminator
                    : sw with { Discriminant = discriminant, Branches = branches };
            }
            default:
                return terminator;
        }
    }

    private List<MirOperand> RefreshOperandList(
        List<MirOperand> operands,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        List<MirOperand>? refreshed = null;
        for (var i = 0; i < operands.Count; i++)
        {
            var operand = operands[i];
            var refreshedOperand = RefreshOperandType(operand, localTypes);
            if (refreshed == null && !ReferenceEquals(refreshedOperand, operand))
            {
                refreshed = new List<MirOperand>(operands.Count);
                for (var j = 0; j < i; j++)
                {
                    refreshed.Add(operands[j]);
                }
            }

            refreshed?.Add(refreshedOperand);
        }

        return refreshed ?? operands;
    }

    private List<MirSwitchBranch> RefreshSwitchBranches(
        List<MirSwitchBranch> branches,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        List<MirSwitchBranch>? refreshed = null;
        for (var i = 0; i < branches.Count; i++)
        {
            var branch = branches[i];
            var value = (MirConstant)RefreshOperandType(branch.Value, localTypes);
            var refreshedBranch = ReferenceEquals(value, branch.Value)
                ? branch
                : branch with { Value = value };
            if (refreshed == null && !ReferenceEquals(refreshedBranch, branch))
            {
                refreshed = new List<MirSwitchBranch>(branches.Count);
                for (var j = 0; j < i; j++)
                {
                    refreshed.Add(branches[j]);
                }
            }

            refreshed?.Add(refreshedBranch);
        }

        return refreshed ?? branches;
    }

    private MirOperand RefreshOperandType(
        MirOperand operand,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        return operand switch
        {
            MirPlace place => RefreshPlaceType(place, localTypes),
            _ => operand
        };
    }

    private MirPlace RefreshPlaceType(
        MirPlace place,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        var refreshedBase = place.Base is null ? null : RefreshPlaceType(place.Base, localTypes);
        var refreshedIndex = place.Index is null ? null : RefreshOperandType(place.Index, localTypes);
        var refreshed = ReferenceEquals(refreshedBase, place.Base) && ReferenceEquals(refreshedIndex, place.Index)
            ? place
            : place with
            {
                Base = refreshedBase,
                Index = refreshedIndex
            };
        var resolvedType = ResolvePlaceType(refreshed, localTypes);
        if (!resolvedType.IsValid ||
            ContainsOpenTypeVariable(resolvedType) ||
            resolvedType == refreshed.TypeId)
        {
            return refreshed;
        }

        return refreshed with { TypeId = resolvedType };
    }
}
