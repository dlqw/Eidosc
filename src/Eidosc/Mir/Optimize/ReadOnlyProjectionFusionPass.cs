using Eidosc.Borrow;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Fuses a short-lived managed field projection into the immediately nested
/// read that consumes it. This preserves borrow provenance while avoiding a
/// retain/release pair for the intermediate container value.
/// </summary>
public sealed class ReadOnlyProjectionFusionPass : IMirOptimizationPass
{
    public string Name => "ReadOnlyProjectionFusion";

    public MirModule Run(MirModule module)
    {
        foreach (var function in module.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                FuseBlock(function, block);
            }
        }

        return module;
    }

    private static void FuseBlock(MirFunc function, MirBasicBlock block)
    {
        for (var index = 0; index + 2 < block.Instructions.Count; index++)
        {
            if (block.Instructions[index] is not MirLoad
                {
                    Target: { Kind: PlaceKind.Local } intermediate,
                    Source: MirPlace source,
                    IsMutableBorrow: false,
                    MovesOutOfSource: false
                } ||
                !TypeSemantics.IsManagedType(intermediate.TypeId) ||
                block.Instructions[index + 1] is not MirLoad
                {
                    Source: MirPlace nestedSource,
                    IsMutableBorrow: false,
                    MovesOutOfSource: false
                } nestedLoad ||
                block.Instructions[index + 2] is not MirDrop
                {
                    Value: MirPlace { Kind: PlaceKind.Local, Local: var dropped }
                } ||
                dropped != intermediate.Local ||
                CountUses(function, intermediate.Local) != 2 ||
                !PlaceUsesRoot(nestedSource, intermediate.Local))
            {
                continue;
            }

            block.Instructions[index + 1] = nestedLoad with
            {
                Source = ReplaceRoot(nestedSource, intermediate.Local, source)
            };
            block.Instructions.RemoveAt(index + 2);
            block.Instructions.RemoveAt(index);
            index--;
        }
    }

    private static MirPlace ReplaceRoot(MirPlace place, LocalId root, MirPlace replacement)
    {
        if (place.Kind == PlaceKind.Local && place.Local == root)
        {
            return replacement;
        }

        return place with
        {
            Base = place.Base == null ? null : ReplaceRoot(place.Base, root, replacement),
            Index = place.Index is MirPlace indexPlace
                ? ReplaceRoot(indexPlace, root, replacement)
                : place.Index
        };
    }

    private static bool PlaceUsesRoot(MirPlace place, LocalId root)
    {
        var current = place;
        while (current.Kind != PlaceKind.Local)
        {
            if (current.Base == null)
            {
                return false;
            }

            current = current.Base;
        }

        return current.Local == root;
    }

    private static int CountUses(MirFunc function, LocalId local) => function.BasicBlocks.Sum(block =>
        block.Instructions.Sum(instruction => CountUses(instruction, local)) +
        CountUses(block.Terminator, local));

    private static int CountUses(MirInstruction instruction, LocalId local) => instruction switch
    {
        MirAssign assign => CountUses(assign.Source, local),
        MirCaseInject injection => CountUses(injection.Operand, local),
        MirCall call => CountUses(call.Function, local) +
                        call.Arguments.Sum(argument => CountUses(argument, local)) +
                        CountUses(call.RecordUpdate?.Source, local),
        MirBinOp binary => CountUses(binary.Left, local) + CountUses(binary.Right, local),
        MirUnaryOp unary => CountUses(unary.Operand, local),
        MirSelect select => CountUses(select.Condition, local) +
                            CountUses(select.TrueValue, local) +
                            CountUses(select.FalseValue, local),
        MirLoad load => CountUses(load.Source, local),
        MirStore store => CountUses(store.Target, local) + CountUses(store.Value, local),
        MirDrop drop => CountUses(drop.Value, local),
        MirCopy copy => CountUses(copy.Source, local),
        MirMove move => CountUses(move.Source, local),
        _ => 0
    };

    private static int CountUses(MirTerminator? terminator, LocalId local) => terminator switch
    {
        MirReturn result => CountUses(result.Value, local),
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
}
