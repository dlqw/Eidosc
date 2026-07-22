namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private static bool HasNonDeferredUses(
        MirFunc function,
        BlockId partialBlockId,
        int partialInstructionIndex,
        int applicationInstructionIndex,
        LocalId localId)
    {
        var currentLocalId = localId;

        for (var blockIndex = 0; blockIndex < function.BasicBlocks.Count; blockIndex++)
        {
            var block = function.BasicBlocks[blockIndex];
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                if (block.Id.Equals(partialBlockId))
                {
                    if (instructionIndex == partialInstructionIndex)
                    {
                        if (InstructionUsesLocalOutsidePartialTarget(instruction, currentLocalId))
                        {
                            return true;
                        }

                        continue;
                    }

                    if (instructionIndex == applicationInstructionIndex)
                    {
                        if (InstructionUsesLocalOutsideImmediateFunctionOperand(instruction, currentLocalId))
                        {
                            return true;
                        }

                        continue;
                    }

                    // A move of the tracked local is a deferred chain link, not a non-deferred use.
                    if (instruction is MirMove { Target: var moveTarget } move &&
                        moveTarget.Kind == PlaceKind.Local &&
                        move.Source is MirPlace { Kind: PlaceKind.Local, Local: var moveSrc } &&
                        moveSrc.Equals(currentLocalId))
                    {
                        currentLocalId = moveTarget.Local;
                        continue;
                    }
                }

                if (InstructionUsesLocal(instruction, currentLocalId))
                {
                    return true;
                }
            }

            if (block.Terminator != null && TerminatorUsesLocal(block.Terminator, currentLocalId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InstructionUsesLocalOutsidePartialTarget(MirInstruction instruction, LocalId localId)
    {
        if (instruction is not MirCall partialCall)
        {
            return InstructionUsesLocal(instruction, localId);
        }

        if (partialCall.Target is not { Kind: PlaceKind.Local } target || !target.Local.Equals(localId))
        {
            return true;
        }

        if (OperandUsesLocal(partialCall.Function, localId))
        {
            return true;
        }

        foreach (var argument in partialCall.Arguments)
        {
            if (OperandUsesLocal(argument, localId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InstructionUsesLocalOutsideImmediateFunctionOperand(MirInstruction instruction, LocalId localId)
    {
        if (instruction is not MirCall immediateApply)
        {
            return InstructionUsesLocal(instruction, localId);
        }

        if (immediateApply.Function is not MirPlace { Kind: PlaceKind.Local } functionLocal ||
            !functionLocal.Local.Equals(localId))
        {
            return true;
        }

        if (immediateApply.Target is { Kind: PlaceKind.Local } target && target.Local.Equals(localId))
        {
            return true;
        }

        foreach (var argument in immediateApply.Arguments)
        {
            if (OperandUsesLocal(argument, localId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InstructionUsesLocal(MirInstruction instruction, LocalId localId)
    {
        return instruction switch
        {
            MirAssign assign => PlaceUsesLocal(assign.Target, localId) || OperandUsesLocal(assign.Source, localId),
            MirCaseInject injection =>
                OperandUsesLocal(injection.Target, localId) || OperandUsesLocal(injection.Operand, localId),
            MirCall call =>
                (call.Target != null && PlaceUsesLocal(call.Target, localId)) ||
                OperandUsesLocal(call.Function, localId) ||
                call.Arguments.Any(argument => OperandUsesLocal(argument, localId)),
            MirBinOp binOp =>
                OperandUsesLocal(binOp.Target, localId) ||
                OperandUsesLocal(binOp.Left, localId) ||
                OperandUsesLocal(binOp.Right, localId),
            MirUnaryOp unaryOp =>
                OperandUsesLocal(unaryOp.Target, localId) ||
                OperandUsesLocal(unaryOp.Operand, localId),
            MirLoad load => PlaceUsesLocal(load.Target, localId) || OperandUsesLocal(load.Source, localId),
            MirStore store => PlaceUsesLocal(store.Target, localId) || OperandUsesLocal(store.Value, localId),
            MirDrop drop => OperandUsesLocal(drop.Value, localId),
            MirCopy copy => PlaceUsesLocal(copy.Target, localId) || PlaceUsesLocal(copy.Source, localId),
            MirMove move => PlaceUsesLocal(move.Target, localId) || PlaceUsesLocal(move.Source, localId),
            MirAlloc alloc => PlaceUsesLocal(alloc.Target, localId),
            _ => false
        };
    }

    private static bool TerminatorUsesLocal(MirTerminator terminator, LocalId localId)
    {
        return terminator switch
        {
            MirReturn ret => ret.Value != null && OperandUsesLocal(ret.Value, localId),
            MirSwitch sw =>
                OperandUsesLocal(sw.Discriminant, localId) ||
                sw.Branches.Any(branch => OperandUsesLocal(branch.Value, localId)),
            _ => false
        };
    }

    private static bool OperandUsesLocal(MirOperand operand, LocalId localId)
    {
        return operand switch
        {
            MirPlace place => PlaceUsesLocal(place, localId),
            _ => false
        };
    }

    private static bool PlaceUsesLocal(MirPlace place, LocalId localId)
    {
        if (place.Kind == PlaceKind.Local && place.Local.Equals(localId))
        {
            return true;
        }

        return (place.Base != null && PlaceUsesLocal(place.Base, localId)) ||
               (place.Index != null && OperandUsesLocal(place.Index, localId));
    }
}
