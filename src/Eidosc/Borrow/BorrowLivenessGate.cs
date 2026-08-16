using Eidosc.Mir;

namespace Eidosc.Borrow;

/// <summary>
/// 借用者死性判定：写入/再借用点之后局部不再被读取（块内先重定义后使用，
/// 或既无块内后续使用也不在 LiveOut）时，其旧值已死，指向它的借用应从
/// 分析状态收缩而非触发冲突。LoanConstraintVerifier 与经典 BorrowChecker
/// 共用，保证两条借用分析的收敛语义一致。
/// </summary>
internal static class BorrowLivenessGate
{
    public static bool IsLocalDeadAfter(
        LocalId local,
        BlockId blockId,
        int instructionIndex,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blocksById,
        LivenessAnalyzer? liveness)
    {
        if (!blocksById.TryGetValue(blockId, out var block))
        {
            return false;
        }

        // 当前指令自身仍读取该局部时不为死（如 store %x, %borrower：
        // 借用者恰在本指令被消费，写入目标与借用读取同指令发生）。
        if (instructionIndex >= 0 &&
            instructionIndex < block.Instructions.Count &&
            InstructionUsesLocal(block.Instructions[instructionIndex], local))
        {
            return false;
        }

        for (var i = instructionIndex + 1; i < block.Instructions.Count; i++)
        {
            if (InstructionDefinesLocal(block.Instructions[i], local))
            {
                return true;
            }

            if (InstructionUsesLocal(block.Instructions[i], local))
            {
                return false;
            }
        }

        if (block.Terminator != null && TerminatorUsesLocal(block.Terminator, local))
        {
            return false;
        }

        // 块内无后续使用：跨块活性交给 LivenessAnalyzer（缺失时保守视为活）。
        return liveness?.LiveOut.TryGetValue(blockId, out var liveOut) == true && !liveOut.Contains(local);
    }

    public static bool InstructionDefinesLocal(MirInstruction instruction, LocalId local)
    {
        var target = instruction switch
        {
            MirAssign assign => assign.Target,
            MirCaseInject injection => injection.Target,
            MirCall { Target: { } callTarget } => callTarget,
            MirBinOp binary => binary.Target,
            MirUnaryOp unary => unary.Target,
            MirSelect select => select.Target,
            MirLoad load => load.Target,
            MirStore store => store.Target,
            MirCopy copy => copy.Target,
            MirMove move => move.Target,
            MirAlloc alloc => alloc.Target,
            _ => null
        };

        return target is MirPlace { Kind: PlaceKind.Local } place && place.Local.Equals(local);
    }

    public static bool InstructionUsesLocal(MirInstruction instruction, LocalId local)
    {
        return instruction switch
        {
            MirAssign assign => ContainsLocalOperand(assign.Source, local),
            MirCaseInject injection => ContainsLocalOperand(injection.Operand, local) || ContainsLocalOperand(injection.Target, local),
            MirCall call => ContainsLocalOperand(call.Function, local) ||
                            call.Arguments.Any(argument => ContainsLocalOperand(argument, local)),
            MirBinOp binary => ContainsLocalOperand(binary.Left, local) || ContainsLocalOperand(binary.Right, local),
            MirUnaryOp unary => ContainsLocalOperand(unary.Operand, local),
            MirSelect select => ContainsLocalOperand(select.Condition, local) ||
                                ContainsLocalOperand(select.TrueValue, local) ||
                                ContainsLocalOperand(select.FalseValue, local),
            MirLoad load => ContainsLocalOperand(load.Source, local),
            MirStore store => ContainsLocalOperand(store.Target, local) || ContainsLocalOperand(store.Value, local),
            MirDrop drop => ContainsLocalOperand(drop.Value, local),
            MirCopy copy => ContainsLocalOperand(copy.Source, local),
            MirMove move => ContainsLocalOperand(move.Source, local),
            _ => false
        };
    }

    public static bool TerminatorUsesLocal(MirTerminator terminator, LocalId local) => terminator switch
    {
        MirReturn { Value: { } value } => ContainsLocalOperand(value, local),
        MirSwitch @switch => ContainsLocalOperand(@switch.Discriminant, local),
        _ => false
    };

    public static bool ContainsLocalOperand(MirOperand? operand, LocalId local)
    {
        if (operand is not MirPlace place)
        {
            return false;
        }

        return place.Kind == PlaceKind.Local && place.Local.Equals(local) ||
               ContainsLocalOperand(place.Base, local) ||
               ContainsLocalOperand(place.Index, local);
    }
}
