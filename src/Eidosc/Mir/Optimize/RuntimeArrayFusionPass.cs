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
        var arrayTakeFunctions = FindConsumingArrayTakeFunctions(module);
        var dropLastFunctions = module.Functions
            .Where(candidate => IsConsumingDropLastFunction(candidate, arrayTakeFunctions))
            .Select(MirFunctionIdentity.GetStableKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var function in module.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                FuseSingletonAppends(function, block);
            }

            FuseConditionalShiftPrepend(function, dropLastFunctions);
        }

        return module;
    }

    private static void FuseConditionalShiftPrepend(
        MirFunc function,
        IReadOnlySet<string> dropLastFunctions)
    {
        if (dropLastFunctions.Count == 0)
        {
            return;
        }

        var cfg = new ControlFlowGraph(function);
        var blocks = function.BasicBlocks.ToDictionary(static block => block.Id);
        foreach (var merge in function.BasicBlocks)
        {
            var predecessors = cfg.GetPredecessors(merge.Id).Distinct().ToArray();
            if (predecessors.Length != 2 ||
                !blocks.TryGetValue(predecessors[0], out var firstBlock) ||
                !blocks.TryGetValue(predecessors[1], out var secondBlock) ||
                !TryMatchPrependBranch(firstBlock, dropLastFunctions, out var first) ||
                !TryMatchPrependBranch(secondBlock, dropLastFunctions, out var second) ||
                first.HasDropLast == second.HasDropLast)
            {
                continue;
            }

            var growBranch = first.HasDropLast ? second : first;
            var trimBranch = first.HasDropLast ? first : second;
            if (!SamePlace(growBranch.MergeTarget, trimBranch.MergeTarget) ||
                !SamePlace(growBranch.Rest, trimBranch.Rest) ||
                !SamePlace(growBranch.OldHead, trimBranch.OldHead) ||
                (growBranch.OldHeadCopy == null) != (trimBranch.OldHeadCopy == null) ||
                !SameOperand(growBranch.Size, trimBranch.Size) ||
                !TryFindGrowDiscriminant(function, growBranch.Block.Id, trimBranch.Block.Id, out var grow) ||
                !TryMatchFinalPrepend(function, merge, growBranch.MergeTarget, out var final) ||
                !SameOperand(growBranch.Size, final.Size))
            {
                continue;
            }

            MirOperand oldHead = growBranch.OldHead;
            MirCopy? oldHeadCopy = null;
            if (growBranch.OldHeadCopy is { } copy)
            {
                oldHeadCopy = copy with { Source = growBranch.OldHead };
                oldHead = oldHeadCopy.Target;
            }

            var replacement = final.Call with
            {
                Target = final.Result,
                Function = MirRuntimeFunctions.CreateFunctionRef(
                    WellKnownStrings.InternalNames.ArrayShiftPrepend,
                    final.Result.TypeId,
                    final.Call.Span),
                Arguments = [growBranch.Rest, final.NewHead, oldHead, grow, final.Size]
            };
            foreach (var index in final.RemovedIndices.OrderByDescending(static index => index))
            {
                merge.Instructions.RemoveAt(index);
            }
            merge.Instructions.Insert(final.InsertIndex, replacement);
            if (oldHeadCopy != null)
            {
                merge.Instructions.Insert(final.InsertIndex, oldHeadCopy);
            }
            growBranch.Block.Instructions.Clear();
            trimBranch.Block.Instructions.Clear();
        }
    }

    private static bool TryMatchPrependBranch(
        MirBasicBlock block,
        IReadOnlySet<string> dropLastFunctions,
        out PrependBranch pattern)
    {
        pattern = null!;
        if (block.Terminator is not MirGoto ||
            block.Instructions.Any(static instruction => instruction is not MirMove and not MirCopy and not MirCall))
        {
            return false;
        }

        var prependIndex = block.Instructions.FindIndex(instruction =>
            instruction is MirCall { Function: MirFunctionRef functionRef } &&
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayPrepend));
        if (prependIndex < 0 ||
            block.Instructions[prependIndex] is not MirCall
            {
                Target: MirPlace { Kind: PlaceKind.Local } prependTarget,
                Arguments.Count: >= 3
            } prepend ||
            !TryResolveMoveOrigin(
                block,
                prependIndex,
                prepend.Arguments[0],
                out var arrayOrigin,
                out var arrayMoves) ||
            !TryResolveCopyMoveOrigin(
                block,
                prependIndex,
                prepend.Arguments[1],
                out var oldHead,
                out var oldHeadCopy,
                out var oldHeadDefinitions) ||
            arrayOrigin is not MirPlace { Kind: PlaceKind.Local } arrayLocal ||
            oldHead is not MirPlace oldHeadPlace)
        {
            return false;
        }

        var consumed = new HashSet<int>(arrayMoves) { prependIndex };
        consumed.UnionWith(oldHeadDefinitions);
        var hasDropLast = false;
        MirPlace rest = arrayLocal;
        var arrayDefinition = FindDefinition(block, prependIndex, arrayLocal.Local);
        if (arrayDefinition >= 0)
        {
            if (block.Instructions[arrayDefinition] is not MirCall
                {
                    Function: MirFunctionRef dropLastRef,
                    Arguments.Count: 1
                } dropLast ||
                !dropLastFunctions.Contains(MirFunctionIdentity.GetStableKey(dropLastRef)) ||
                !TryResolveMoveOrigin(
                    block,
                    arrayDefinition,
                    dropLast.Arguments[0],
                    out var restOrigin,
                    out var restMoves) ||
                restOrigin is not MirPlace restPlace)
            {
                return false;
            }

            rest = restPlace;
            hasDropLast = true;
            consumed.Add(arrayDefinition);
            consumed.UnionWith(restMoves);
        }

        var mergeMove = block.Instructions
            .Skip(prependIndex + 1)
            .OfType<MirMove>()
            .SingleOrDefault(move => move.Source.Kind == PlaceKind.Local && move.Source.Local == prependTarget.Local);
        if (mergeMove == null ||
            block.Instructions.Count(instruction => instruction is MirCall) != (hasDropLast ? 2 : 1))
        {
            return false;
        }

        consumed.Add(block.Instructions.IndexOf(mergeMove));
        if (consumed.Count != block.Instructions.Count)
        {
            return false;
        }

        pattern = new PrependBranch(
            block,
            hasDropLast,
            rest,
            oldHeadPlace,
            oldHeadCopy,
            mergeMove.Target,
            prepend.Arguments[2]);
        return true;
    }

    private static bool TryFindGrowDiscriminant(
        MirFunc function,
        BlockId growTarget,
        BlockId trimTarget,
        out MirOperand discriminant)
    {
        foreach (var block in function.BasicBlocks)
        {
            if (block.Terminator is not MirSwitch branch || branch.DefaultTarget != trimTarget)
            {
                continue;
            }

            if (branch.Branches.Any(item => item.Target == growTarget &&
                item.Value.Value is MirConstantValue.BoolValue(true)))
            {
                discriminant = branch.Discriminant;
                return true;
            }
        }

        discriminant = null!;
        return false;
    }

    private static bool TryMatchFinalPrepend(
        MirFunc function,
        MirBasicBlock block,
        MirPlace mergedTail,
        out FinalPrepend pattern)
    {
        pattern = null!;
        for (var callIndex = 0; callIndex < block.Instructions.Count; callIndex++)
        {
            if (block.Instructions[callIndex] is not MirCall
                {
                    Target: MirPlace { Kind: PlaceKind.Local } callTarget,
                    Function: MirFunctionRef functionRef,
                    Arguments.Count: >= 3
                } call ||
                !MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayPrepend))
            {
                continue;
            }

            var removed = new HashSet<int> { callIndex };
            if (!TryResolveMoveOrigin(block, callIndex, call.Arguments[0], out var tail, removed) ||
                tail is not MirPlace tailPlace || !SamePlace(tailPlace, mergedTail) ||
                !TryResolveMoveOrigin(block, callIndex, call.Arguments[1], out var newHead, removed) ||
                newHead is not MirPlace newHeadPlace)
            {
                continue;
            }

            MirPlace result = callTarget;
            var resultMoves = block.Instructions
                .Skip(callIndex + 1)
                .Select((instruction, offset) => (instruction, index: callIndex + 1 + offset))
                .Where(item => item.instruction is MirMove move &&
                               move.Source.Kind == PlaceKind.Local &&
                               move.Source.Local == callTarget.Local)
                .ToArray();
            if (resultMoves.Length > 1)
            {
                continue;
            }

            if (resultMoves.Length == 1)
            {
                var resultMove = (MirMove)resultMoves[0].instruction;
                if (CountUses(function, callTarget.Local) != 1)
                {
                    continue;
                }

                result = resultMove.Target;
                removed.Add(resultMoves[0].index);
            }

            if (removed
                .Where(index => index < callIndex)
                .Select(index => block.Instructions[index])
                .OfType<MirMove>()
                .Any(move => CountUses(function, move.Target.Local) != 1))
            {
                continue;
            }

            pattern = new FinalPrepend(
                call,
                result,
                newHeadPlace,
                call.Arguments[2],
                removed,
                callIndex - removed.Count(index => index < callIndex));
            return true;
        }

        return false;
    }

    private static HashSet<string> FindConsumingArrayTakeFunctions(MirModule module)
    {
        var result = module.Functions
            .Where(function => string.Equals(
                function.IntrinsicName,
                WellKnownStrings.InternalNames.ArrayTake,
                StringComparison.Ordinal))
            .Select(MirFunctionIdentity.GetStableKey)
            .ToHashSet(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var function in module.Functions)
            {
                var key = MirFunctionIdentity.GetStableKey(function);
                if (!result.Contains(key) && IsTransparentTakeWrapper(function, result))
                {
                    result.Add(key);
                    changed = true;
                }
            }
        }

        return result;
    }

    private static bool IsTransparentTakeWrapper(MirFunc function, IReadOnlySet<string> knownTakeFunctions)
    {
        var parameters = function.Locals.Where(static local => local.IsParameter).ToArray();
        if (parameters.Length != 2 || function.BasicBlocks.Count != 1)
        {
            return false;
        }

        var block = function.BasicBlocks[0];
        var provenance = new Dictionary<PlaceSlot, WrapperValue>
        {
            [new PlaceSlot(parameters[0].Id, string.Empty)] = WrapperValue.FirstParameter,
            [new PlaceSlot(parameters[1].Id, string.Empty)] = WrapperValue.SecondParameter
        };
        var takeCalls = 0;
        foreach (var instruction in block.Instructions)
        {
            switch (instruction)
            {
                case MirAlloc alloc:
                    ClearPlace(alloc.Target, provenance);
                    break;
                case MirAssign assign:
                    CopyProvenance(assign.Source, assign.Target, provenance);
                    break;
                case MirCopy copy:
                    CopyProvenance(copy.Source, copy.Target, provenance);
                    break;
                case MirMove move:
                    CopyProvenance(move.Source, move.Target, provenance);
                    break;
                case MirLoad load:
                    CopyProvenance(load.Source, load.Target, provenance);
                    break;
                case MirStore store:
                    CopyProvenance(store.Value, store.Target, provenance);
                    break;
                case MirDrop:
                    break;
                case MirCall
                {
                    Target: MirPlace target,
                    Function: MirFunctionRef functionRef,
                    Arguments.Count: 2
                } call when knownTakeFunctions.Contains(MirFunctionIdentity.GetStableKey(functionRef)) &&
                            GetProvenance(call.Arguments[0], provenance) == WrapperValue.FirstParameter &&
                            GetProvenance(call.Arguments[1], provenance) == WrapperValue.SecondParameter:
                    ClearPlace(target, provenance);
                    provenance[GetSlot(target)] = WrapperValue.TakeResult;
                    takeCalls++;
                    break;
                case MirCall call:
                    if (call.Target is { } callTarget)
                    {
                        ClearPlace(callTarget, provenance);
                    }
                    break;
                default:
                    return false;
            }
        }

        return takeCalls == 1 &&
               block.Terminator is MirReturn { Value: not null } result &&
               GetProvenance(result.Value, provenance) == WrapperValue.TakeResult;
    }

    private static void CopyProvenance(
        MirOperand source,
        MirPlace target,
        Dictionary<PlaceSlot, WrapperValue> provenance)
    {
        ClearPlace(target, provenance);
        if (source is not MirPlace sourcePlace ||
            !TryGetSlot(sourcePlace, out var sourceSlot) ||
            !TryGetSlot(target, out var targetSlot))
        {
            return;
        }

        foreach (var (slot, value) in provenance.ToArray())
        {
            if (slot.Root != sourceSlot.Root ||
                slot.Path != sourceSlot.Path &&
                !slot.Path.StartsWith($"{sourceSlot.Path}/", StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = slot.Path[sourceSlot.Path.Length..];
            provenance[targetSlot with { Path = $"{targetSlot.Path}{suffix}" }] = value;
        }
    }

    private static WrapperValue GetProvenance(
        MirOperand operand,
        IReadOnlyDictionary<PlaceSlot, WrapperValue> provenance) =>
        operand is MirPlace place && TryGetSlot(place, out var slot)
            ? provenance.GetValueOrDefault(slot)
            : WrapperValue.Unknown;

    private static void ClearPlace(MirPlace place, Dictionary<PlaceSlot, WrapperValue> provenance)
    {
        if (!TryGetSlot(place, out var slot))
        {
            return;
        }

        foreach (var candidate in provenance.Keys.Where(candidate =>
                     candidate.Root == slot.Root &&
                     (candidate.Path == slot.Path ||
                      slot.Path.Length == 0 ||
                      candidate.Path.StartsWith($"{slot.Path}/", StringComparison.Ordinal))).ToArray())
        {
            provenance.Remove(candidate);
        }
    }

    private static PlaceSlot GetSlot(MirPlace place)
    {
        return TryGetSlot(place, out var slot) ? slot : default;
    }

    private static bool TryGetSlot(MirPlace place, out PlaceSlot slot)
    {
        switch (place.Kind)
        {
            case PlaceKind.Local:
                slot = new PlaceSlot(place.Local, string.Empty);
                return true;
            case PlaceKind.Field when place.Base != null && TryGetSlot(place.Base, out var fieldBase):
                slot = fieldBase with { Path = $"{fieldBase.Path}/f:{place.FieldName}" };
                return true;
            case PlaceKind.Index when place.IndexAccessKind == MirIndexAccessKind.Aggregate &&
                                      place.Base != null &&
                                      place.Index is MirConstant { Value: MirConstantValue.IntValue(var index) } &&
                                      TryGetSlot(place.Base, out var indexBase):
                slot = indexBase with { Path = $"{indexBase.Path}/i:{index}" };
                return true;
            default:
                slot = default;
                return false;
        }
    }

    private static bool SamePlace(MirPlace left, MirPlace right) =>
        TryGetSlot(left, out var leftSlot) &&
        TryGetSlot(right, out var rightSlot) &&
        leftSlot == rightSlot;

    private static bool SameOperand(MirOperand left, MirOperand right) => (left, right) switch
    {
        (MirPlace leftPlace, MirPlace rightPlace) => SamePlace(leftPlace, rightPlace),
        (MirConstant leftConstant, MirConstant rightConstant) =>
            leftConstant.TypeId == rightConstant.TypeId && leftConstant.Value == rightConstant.Value,
        _ => false
    };

    private static bool IsConsumingDropLastFunction(
        MirFunc function,
        IReadOnlySet<string> arrayTakeFunctions)
    {
        var parameters = function.Locals.Where(static local => local.IsParameter).ToArray();
        if (parameters.Length != 1 || function.BasicBlocks.Count != 1)
        {
            return false;
        }

        var block = function.BasicBlocks[0];
        var takeIndex = block.Instructions.FindIndex(instruction =>
            instruction is MirCall { Function: MirFunctionRef functionRef } &&
            (arrayTakeFunctions.Contains(MirFunctionIdentity.GetStableKey(functionRef)) ||
             functionRef.CompilerSemanticRole == CompilerSemanticRole.SequenceTake ||
             IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArrayTake)));
        if (takeIndex < 0 ||
            block.Instructions[takeIndex] is not MirCall { Arguments.Count: 2 } take ||
            !TryResolveMoveOrigin(block, takeIndex, take.Arguments[0], out var array) ||
            array is not MirPlace { Kind: PlaceKind.Local, Local: var arrayLocal } ||
            arrayLocal != parameters[0].Id ||
            !TryMatchLengthMinusOne(block, takeIndex, take.Arguments[1], parameters[0].Id))
        {
            return false;
        }

        return block.Terminator is MirReturn { Value: MirPlace result } &&
               take.Target is MirPlace { Kind: PlaceKind.Local } target &&
               TryResolveMoveOrigin(block, block.Instructions.Count, result, out var returned) &&
               returned is MirPlace { Kind: PlaceKind.Local, Local: var returnedLocal } &&
               returnedLocal == target.Local;
    }

    private static bool TryMatchLengthMinusOne(
        MirBasicBlock block,
        int beforeIndex,
        MirOperand operand,
        LocalId parameter)
    {
        if (!TryResolveDefinition(block, beforeIndex, operand, out var definition, out var definitionIndex) ||
            definition is not MirBinOp
            {
                Operator: BinaryOp.Sub,
                Right: MirConstant { Value: MirConstantValue.IntValue(1) }
            } subtraction ||
            !TryResolveDefinition(block, definitionIndex, subtraction.Left, out var lengthDefinition, out _) ||
            lengthDefinition is not MirCall
            {
                Function: MirFunctionRef lengthRef,
                Arguments.Count: 1
            } lengthCall ||
            !IsArrayIntrinsic(lengthRef, WellKnownStrings.InternalNames.ArrayLength) ||
            !TryResolveMoveOrigin(block, definitionIndex, lengthCall.Arguments[0], out var lengthArray) ||
            lengthArray is not MirPlace { Kind: PlaceKind.Local, Local: var lengthLocal })
        {
            return false;
        }

        return lengthLocal == parameter;
    }

    private static bool TryResolveDefinition(
        MirBasicBlock block,
        int beforeIndex,
        MirOperand operand,
        out MirInstruction definition,
        out int definitionIndex)
    {
        var current = operand;
        var currentBefore = beforeIndex;
        var visited = new HashSet<LocalId>();
        while (current is MirPlace { Kind: PlaceKind.Local, Local: var local } && visited.Add(local))
        {
            definitionIndex = FindDefinition(block, currentBefore, local);
            if (definitionIndex < 0)
            {
                break;
            }

            definition = block.Instructions[definitionIndex];
            currentBefore = definitionIndex;
            current = definition switch
            {
                MirCopy copy => copy.Source,
                MirMove move => move.Source,
                MirAssign assign => assign.Source,
                _ => current
            };
            if (current == operand || definition is not MirCopy and not MirMove and not MirAssign)
            {
                return true;
            }
        }

        definition = null!;
        definitionIndex = -1;
        return false;
    }

    private static bool IsArrayIntrinsic(MirFunctionRef functionRef, string name) =>
        MirRuntimeFunctions.HasIdentity(functionRef, name) ||
        MirBuiltinFunctions.TryGetIntrinsicName(functionRef, out var intrinsic) &&
        string.Equals(intrinsic, name, StringComparison.Ordinal);

    private static bool TryResolveMoveOrigin(
        MirBasicBlock block,
        int beforeIndex,
        MirOperand operand,
        out MirOperand origin,
        HashSet<int>? definitions = null)
    {
        origin = operand;
        var visited = new HashSet<LocalId>();
        while (origin is MirPlace { Kind: PlaceKind.Local, Local: var local } && visited.Add(local))
        {
            var definitionIndex = FindDefinition(block, beforeIndex, local);
            if (definitionIndex < 0 || block.Instructions[definitionIndex] is not MirMove move)
            {
                return true;
            }

            definitions?.Add(definitionIndex);
            origin = move.Source;
            beforeIndex = definitionIndex;
        }

        return true;
    }

    private static bool TryResolveMoveOrigin(
        MirBasicBlock block,
        int beforeIndex,
        MirOperand operand,
        out MirOperand origin,
        out HashSet<int> definitions)
    {
        definitions = [];
        return TryResolveMoveOrigin(block, beforeIndex, operand, out origin, definitions);
    }

    private static bool TryResolveCopyMoveOrigin(
        MirBasicBlock block,
        int beforeIndex,
        MirOperand operand,
        out MirOperand origin,
        out MirCopy? copy,
        out HashSet<int> definitions)
    {
        origin = operand;
        copy = null;
        definitions = [];
        var visited = new HashSet<LocalId>();
        while (origin is MirPlace { Kind: PlaceKind.Local, Local: var local } && visited.Add(local))
        {
            var definitionIndex = FindDefinition(block, beforeIndex, local);
            if (definitionIndex < 0)
            {
                return true;
            }

            switch (block.Instructions[definitionIndex])
            {
                case MirMove move:
                    definitions.Add(definitionIndex);
                    origin = move.Source;
                    beforeIndex = definitionIndex;
                    break;
                case MirCopy currentCopy when copy == null:
                    definitions.Add(definitionIndex);
                    copy = currentCopy;
                    origin = currentCopy.Source;
                    beforeIndex = definitionIndex;
                    break;
                default:
                    return true;
            }
        }

        return true;
    }

    private sealed record PrependBranch(
        MirBasicBlock Block,
        bool HasDropLast,
        MirPlace Rest,
        MirPlace OldHead,
        MirCopy? OldHeadCopy,
        MirPlace MergeTarget,
        MirOperand Size);

    private sealed record FinalPrepend(
        MirCall Call,
        MirPlace Result,
        MirPlace NewHead,
        MirOperand Size,
        IReadOnlySet<int> RemovedIndices,
        int InsertIndex);

    private enum WrapperValue
    {
        Unknown,
        FirstParameter,
        SecondParameter,
        TakeResult
    }

    private readonly record struct PlaceSlot(LocalId Root, string Path);

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
        MirSelect select => CountUses(select.Condition, local) + CountUses(select.TrueValue, local) + CountUses(select.FalseValue, local),
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
        MirSelect { Target: { Kind: PlaceKind.Local, Local: var target } } => target == local,
        _ => false
    };
}
