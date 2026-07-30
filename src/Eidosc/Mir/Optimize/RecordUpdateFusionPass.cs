using Eidosc.Borrow;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Recovers consuming record updates from constructor reconstruction after
/// ownership finalization. Unchanged fields remain in the original object so
/// the backend can update unique records in place and clone shared records.
/// </summary>
public sealed class RecordUpdateFusionPass : IMirOptimizationPass
{
    public string Name => "RecordUpdateFusion";

    public MirModule Run(MirModule module)
    {
        foreach (var function in module.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                FuseUpdates(module, function, block);
            }
        }

        return module;
    }

    private static void FuseUpdates(MirModule module, MirFunc function, MirBasicBlock block)
    {
        for (var callIndex = 1; callIndex < block.Instructions.Count; callIndex++)
        {
            if (block.Instructions[callIndex] is not MirCall
                {
                    Target: MirPlace { Kind: PlaceKind.Local } target,
                    Function: MirFunctionRef constructor,
                    RecordUpdate: null
                } call)
            {
                continue;
            }

            if (!TypeSemantics.IsAdtConstructorCall(constructor) ||
                block.Instructions[callIndex - 1] is not MirDrop
                {
                    Value: MirPlace { Kind: PlaceKind.Local } source
                })
            {
                continue;
            }

            if (source.TypeId != target.TypeId ||
                !TryResolveLayout(module, target.TypeId, constructor.Name, call.Arguments.Count, out var layout) ||
                HasUseAfter(block, callIndex, source.Local))
            {
                continue;
            }

            var preservedDefinitions = new List<int>();
            var updatedFieldIndices = new List<int>();
            var updatedArguments = new List<MirOperand>();
            var updatedBorrowedIndices = new HashSet<int>();

            for (var fieldIndex = 0; fieldIndex < layout.FieldTypeIds.Count; fieldIndex++)
            {
                var argument = call.Arguments[fieldIndex];
                if (TryFindPreservedFieldLoad(
                        function,
                        block,
                        callIndex - 1,
                        argument,
                        source.Local,
                        fieldIndex,
                        out var definitionIndex))
                {
                    preservedDefinitions.Add(definitionIndex);
                    continue;
                }

                if (OperandUsesLocal(argument, source.Local))
                {
                    updatedFieldIndices.Clear();
                    break;
                }

                if (call.BorrowedArgumentIndices.Contains(fieldIndex))
                {
                    updatedBorrowedIndices.Add(updatedArguments.Count);
                }

                updatedFieldIndices.Add(fieldIndex);
                updatedArguments.Add(argument);
            }

            if (preservedDefinitions.Count == 0 ||
                updatedFieldIndices.Count == 0 ||
                preservedDefinitions.Count + updatedFieldIndices.Count != layout.FieldTypeIds.Count)
            {
                continue;
            }

            block.Instructions[callIndex] = call with
            {
                Arguments = [source, .. updatedArguments],
                BorrowedArgumentIndices = updatedBorrowedIndices
                    .Select(static index => index + 1)
                    .ToHashSet(),
                RecordUpdate = new MirRecordUpdateInfo
                {
                    Source = source,
                    UpdatedFieldIndices = updatedFieldIndices
                }
            };

            var removedBeforeCall = 0;
            foreach (var index in preservedDefinitions
                         .Append(callIndex - 1)
                         .Distinct()
                         .OrderByDescending(static index => index))
            {
                block.Instructions.RemoveAt(index);
                removedBeforeCall++;
            }

            callIndex -= removedBeforeCall;
        }
    }

    private static bool TryResolveLayout(
        MirModule module,
        TypeId typeId,
        string constructorName,
        int argumentCount,
        out ConstructorTypeLayout layout)
    {
        layout = null!;
        if (!module.ConstructorLayouts.TryGetValue(typeId.Value, out var layouts))
        {
            return false;
        }

        layout = layouts.FirstOrDefault(candidate =>
            candidate.FieldTypeIds.Count == argumentCount &&
            (string.Equals(candidate.ConstructorName, constructorName, StringComparison.Ordinal) ||
             constructorName.EndsWith($"__{candidate.ConstructorName}", StringComparison.Ordinal) ||
             constructorName.EndsWith($".{candidate.ConstructorName}", StringComparison.Ordinal)))!;
        return layout != null;
    }

    private static bool TryFindPreservedFieldLoad(
        MirFunc function,
        MirBasicBlock block,
        int beforeIndex,
        MirOperand argument,
        LocalId source,
        int fieldIndex,
        out int definitionIndex)
    {
        definitionIndex = -1;
        if (argument is not MirPlace { Kind: PlaceKind.Local } argumentPlace ||
            CountUses(function, argumentPlace.Local) != 1)
        {
            return false;
        }

        for (var index = beforeIndex - 1; index >= 0; index--)
        {
            if (!DefinesLocal(block.Instructions[index], argumentPlace.Local))
            {
                continue;
            }

            if (block.Instructions[index] is MirLoad
                {
                    Source: MirPlace { Kind: PlaceKind.Field } field
                } &&
                TryGetRootLocal(field, out var root) &&
                root == source &&
                TryParseFieldIndex(field.FieldName, out var loadedFieldIndex) &&
                loadedFieldIndex == fieldIndex)
            {
                definitionIndex = index;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryParseFieldIndex(string? fieldName, out int index)
    {
        index = -1;
        return fieldName is { Length: > 1 } &&
               fieldName[0] == '_' &&
               int.TryParse(fieldName.AsSpan(1), out index) &&
               index >= 0;
    }

    private static bool TryGetRootLocal(MirPlace place, out LocalId local)
    {
        var current = place;
        while (current.Kind != PlaceKind.Local)
        {
            if (current.Base == null)
            {
                local = default;
                return false;
            }

            current = current.Base;
        }

        local = current.Local;
        return true;
    }

    private static bool HasUseAfter(MirBasicBlock block, int callIndex, LocalId local)
    {
        for (var index = callIndex + 1; index < block.Instructions.Count; index++)
        {
            if (CountUses(block.Instructions[index], local) > 0)
            {
                return true;
            }
        }

        return CountUses(block.Terminator, local) > 0;
    }

    private static int CountUses(MirFunc function, LocalId local) =>
        function.BasicBlocks.Sum(block =>
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

    private static bool OperandUsesLocal(MirOperand operand, LocalId local) => CountUses(operand, local) > 0;

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
