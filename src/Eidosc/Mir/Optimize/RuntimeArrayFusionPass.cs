using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Fuses compiler-generated singleton-array append shapes into a consuming
/// prepend operation so natural sequence expressions do not allocate a
/// temporary one-element container.
/// </summary>
public sealed class RuntimeArrayFusionPass : IMirOptimizationPass
{
    public string Name => "RuntimeArrayFusion";

    public MirModule Run(MirModule module)
    {
        foreach (var function in module.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                FuseSingletonAppends(function, block);
            }
        }

        return module;
    }

    private static void FuseSingletonAppends(MirFunc function, MirBasicBlock block)
    {
        for (var callIndex = 0; callIndex < block.Instructions.Count; callIndex++)
        {
            if (block.Instructions[callIndex] is not MirCall
                {
                    Function: MirFunctionRef
                    {
                        CompilerSemanticRole: CompilerSemanticRole.AppendLastAppend
                    },
                    Arguments.Count: 2
                } append ||
                append.Arguments[0] is not MirPlace { Kind: PlaceKind.Local } left ||
                append.Arguments[1] is not MirPlace right)
            {
                continue;
            }

            var leftMoveIndex = FindDefinition(block, callIndex, left.Local);
            if (leftMoveIndex < 0 ||
                block.Instructions[leftMoveIndex] is not MirMove
                {
                    Source: MirPlace { Kind: PlaceKind.Local } singleton
                } ||
                CountUses(function, left.Local) != 1)
            {
                continue;
            }

            var singletonDefinitionIndex = FindDefinition(block, leftMoveIndex, singleton.Local);
            if (singletonDefinitionIndex < 0 ||
                block.Instructions[singletonDefinitionIndex] is not MirCall
                {
                    Function: MirFunctionRef arrayNewRef,
                    Arguments.Count: >= 2
                } arrayNew ||
                !MirRuntimeFunctions.HasIdentity(arrayNewRef, WellKnownStrings.InternalNames.ArrayNew) ||
                arrayNew.Arguments[0] is not MirConstant
                {
                    Value: MirConstantValue.IntValue(1)
                })
            {
                continue;
            }

            var storeIndex = -1;
            MirOperand? element = null;
            for (var index = singletonDefinitionIndex + 1; index < leftMoveIndex; index++)
            {
                if (block.Instructions[index] is MirStore
                    {
                        Target:
                        {
                            Kind: PlaceKind.Index,
                            Base: MirPlace { Kind: PlaceKind.Local } basePlace,
                            Index: MirConstant { Value: MirConstantValue.IntValue(0) }
                        },
                        Value: var stored
                    } &&
                    basePlace.Local == singleton.Local)
                {
                    if (storeIndex >= 0)
                    {
                        storeIndex = -1;
                        break;
                    }

                    storeIndex = index;
                    element = stored;
                }
            }

            if (storeIndex < 0 || element == null || CountUses(function, singleton.Local) != 2)
            {
                continue;
            }

            block.Instructions[callIndex] = append with
            {
                Function = MirRuntimeFunctions.CreateFunctionRef(
                    WellKnownStrings.InternalNames.ArrayPrepend,
                    append.Target?.TypeId ?? TypeId.None,
                    append.Span),
                Arguments = [right, element, arrayNew.Arguments[1]]
            };

            foreach (var index in new[] { leftMoveIndex, storeIndex, singletonDefinitionIndex }
                         .OrderByDescending(static index => index))
            {
                block.Instructions.RemoveAt(index);
            }

            callIndex -= 3;
        }
    }

    private static int FindDefinition(MirBasicBlock block, int beforeIndex, LocalId local)
    {
        for (var index = beforeIndex - 1; index >= 0; index--)
        {
            if (DefinesLocal(block.Instructions[index], local))
            {
                return index;
            }
        }

        return -1;
    }

    private static int CountUses(MirFunc function, LocalId local)
    {
        return function.BasicBlocks.Sum(block =>
            block.Instructions.Sum(instruction => CountUses(instruction, local)) +
            CountUses(block.Terminator, local));
    }

    private static int CountUses(MirInstruction instruction, LocalId local) => instruction switch
    {
        MirAssign assign => CountUses(assign.Source, local),
        MirCaseInject injection => CountUses(injection.Operand, local),
        MirCall call => CountUses(call.Function, local) + call.Arguments.Sum(argument => CountUses(argument, local)),
        MirBinOp binary => CountUses(binary.Left, local) + CountUses(binary.Right, local),
        MirUnaryOp unary => CountUses(unary.Operand, local),
        MirLoad load => CountUses(load.Source, local),
        MirStore store => CountUses(store.Target, local) + CountUses(store.Value, local),
        MirDrop drop => CountUses(drop.Value, local),
        MirCopy copy => CountUses(copy.Source, local),
        MirMove move => CountUses(move.Source, local),
        _ => 0
    };

    private static int CountUses(MirTerminator? terminator, LocalId local) => terminator switch
    {
        MirReturn ret => CountUses(ret.Value, local),
        MirSwitch branch => CountUses(branch.Discriminant, local),
        _ => 0
    };

    private static int CountUses(MirOperand? operand, LocalId local)
    {
        if (operand is not MirPlace place)
        {
            return 0;
        }

        return (place.Kind == PlaceKind.Local && place.Local == local ? 1 : 0) +
               CountUses(place.Base, local) +
               CountUses(place.Index, local);
    }

    private static bool DefinesLocal(MirInstruction instruction, LocalId local) => instruction switch
    {
        MirAssign { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirCall { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirLoad { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirStore { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirCopy { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirMove { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirAlloc { Target.Kind: PlaceKind.Local, Target.Local: var target } => target == local,
        MirBinOp { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        _ => false
    };
}
