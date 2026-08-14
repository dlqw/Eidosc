using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Eidosc.Borrow;

namespace Eidosc.Mir.Optimize;

public sealed partial class SequencePipelineFusionPass
{
    private bool TryFindDirectSequenceSinkPlans(
        MirModule module,
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out IReadOnlyList<SequencePipelinePlan> plans)
    {
        var discovered = new List<SequencePipelinePlan>();
        foreach (var block in function.BasicBlocks.ToArray())
        {
            var consumedByFlatMap = new HashSet<int>();
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                if (consumedByFlatMap.Contains(index))
                {
                    continue;
                }

                if (TryFindZipSinkPlan(module, function, block, index, functionsByKey, out var zipSinkPlan))
                {
                    discovered.Add(zipSinkPlan);
                    consumedByFlatMap.Add(zipSinkPlan.SinkInstructionIndex);
                    continue;
                }

                if (TryFindFlatMapCountPlan(module, function, block, index, functionsByKey, out var flatMapCountPlan))
                {
                    discovered.Add(flatMapCountPlan);
                    consumedByFlatMap.Add(flatMapCountPlan.CountInstructionIndex);
                    continue;
                }

                if (TryFindFlatMapDirectSinkPlan(module, function, block, index, functionsByKey, out var flatMapSinkPlan))
                {
                    discovered.Add(flatMapSinkPlan);
                    consumedByFlatMap.Add(flatMapSinkPlan.SinkInstructionIndex);
                    continue;
                }

                if (TryFindFlatMapFoldPlan(module, function, block, index, functionsByKey, out var flatMapFoldPlan))
                {
                    discovered.Add(flatMapFoldPlan);
                    consumedByFlatMap.Add(flatMapFoldPlan.FoldInstructionIndex);
                    continue;
                }

                if (block.Instructions[index] is MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } partitionTarget,
                        Function: MirFunctionRef partitionFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } partitionSource, MirFunctionRef partitionPredicate]
                    } partitionCall &&
                    GetEffectiveSequenceRole(partitionFunction, functionsByKey) == CompilerSemanticRole.SequencePartition &&
                    TryResolveCallback(functionsByKey, partitionPredicate, out var partitionCallback) &&
                    TryGetPartitionElementType(module, partitionTarget.TypeId, partitionCallback, out var partitionElementType, out var partitionParameterType) &&
                    (IsCopyType(module, partitionElementType) ||
                     CanMovePartitionElement(function, partitionSource, block, index)) &&
                    HasSingleUseNonEscaping(function, partitionSource.Local) &&
                    (!_ownershipSnapshots.TryGetValue(MirFunctionIdentity.GetStableKey(function), out var partitionSnapshot) ||
                     partitionSnapshot.CanDestructivelyUpdate(partitionSource.Local, block.Id, index)))
                {
                    discovered.Add(new DirectPartitionPlan(
                        block,
                        index,
                        partitionSource,
                        partitionPredicate,
                        partitionTarget,
                        partitionElementType,
                        partitionParameterType,
                        partitionTarget.TypeId,
                        GetRuntimeElementSize(module, partitionElementType),
                        partitionCall.Span));
                    continue;
                }

                if (block.Instructions[index] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } resultTarget,
                        Function: MirFunctionRef sinkFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } sinkSource, MirFunctionRef callback]
                    } call ||
                    !TryGetDirectSinkKind(GetEffectiveSequenceRole(sinkFunction, functionsByKey), out var kind) ||
                    !TryResolveCallback(functionsByKey, callback, out var callbackFunction))
                {
                    continue;
                }

                var parameters = callbackFunction.Locals
                    .Where(static local => local.IsParameter)
                    .ToArray();
                var expectedReturn = kind == DirectSequenceSinkKind.ForEach
                    ? new TypeId(BaseTypes.UnitId)
                    : new TypeId(BaseTypes.BoolId);
                if (parameters.Length != 1 || callbackFunction.ReturnType != expectedReturn)
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                TypeId elementType;
                if (kind == DirectSequenceSinkKind.Find)
                {
                    if (!TryGetSharedBorrowInnerType(module, parameters[0].TypeId, out elementType))
                    {
                        Stats.FallbackUnknownCallback++;
                        continue;
                    }
                }
                else
                {
                    elementType = parameters[0].TypeId;
                    if (!elementType.IsValid || !IsCopyType(module, elementType))
                    {
                        Stats.FallbackOwnership++;
                        continue;
                    }
                }

                if (!TryFindViewSpine(
                        block,
                        index,
                        sinkSource,
                        functionsByKey,
                        out var source,
                        out var firstInstructionIndex,
                        out var stages))
                {
                    Stats.FallbackShapeAfterMap++;
                    continue;
                }

                // View fusion currently targets the Copy route.  The direct
                // find lowering for non-Copy values has a separate cleanup
                // proof and must not silently bypass it through a view.
                if (stages.Count > 0 &&
                    (kind == DirectSequenceSinkKind.Find ||
                     !IsCopyType(module, elementType)))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                if (!HasSingleUseNonEscaping(function, source.Local) ||
                    stages.Any(stage => !HasSingleUseNonEscaping(function, stage.Output)))
                {
                    Stats.FallbackMultiUse++;
                    continue;
                }

                if (_ownershipSnapshots.TryGetValue(
                        MirFunctionIdentity.GetStableKey(function),
                        out var snapshot) &&
                    !snapshot.CanDestructivelyUpdate(source.Local, block.Id, firstInstructionIndex))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                discovered.Add(new DirectSequenceSinkPlan(
                    block,
                    firstInstructionIndex,
                    index,
                    source,
                    sinkFunction,
                    callback,
                    resultTarget,
                    elementType,
                    parameters[0].TypeId,
                    kind,
                    stages.Select(static stage => stage.Plan).ToArray(),
                    call.Span));
            }
        }

        plans = discovered
            .OrderByDescending(static candidate => candidate switch
            {
                DirectSequenceSinkPlan direct => direct.Block.Id.Value,
                _ => 0
            })
            .ThenByDescending(static candidate => candidate switch
            {
                DirectSequenceSinkPlan direct => direct.InstructionIndex,
                _ => 0
            })
            .ToArray();
        return plans.Count > 0;
    }

    private bool CanMovePartitionElement(
        MirFunc function,
        MirPlace source,
        MirBasicBlock block,
        int instructionIndex)
    {
        if (!_ownershipSnapshots.TryGetValue(MirFunctionIdentity.GetStableKey(function), out var snapshot))
        {
            return false;
        }

        return snapshot.IsMustOwned(source.Local, block.Id, instructionIndex) &&
               snapshot.IsMustUnique(source.Local, block.Id, instructionIndex) &&
               !snapshot.HasActiveBorrow(source.Local, block.Id, instructionIndex) &&
               snapshot.EscapeFacts.GetValueOrDefault(source.Local) == OwnershipEscapeKind.None;
    }

    private bool TryFindZipSinkPlan(
        MirModule module,
        MirFunc function,
        MirBasicBlock block,
        int zipIndex,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out DirectZipSequenceSinkPlan plan)
    {
        plan = null!;
        var instructions = block.Instructions;
        if (instructions[zipIndex] is not MirCall
            {
                Target: MirPlace { Kind: PlaceKind.Local } zipTarget,
                Function: MirFunctionRef zipFunction,
                Arguments: [MirPlace { Kind: PlaceKind.Local } left, MirPlace { Kind: PlaceKind.Local } right]
            } ||
            GetEffectiveSequenceRole(zipFunction, functionsByKey) != CompilerSemanticRole.SequenceZip ||
            !TryGetSequenceElementType(module, zipTarget.TypeId, out var pairType) ||
            !module.TypeDescriptors.TryGetValue(pairType.Value, out var pairDescriptor) ||
            pairDescriptor is not TypeDescriptor.Tuple { FieldTypes.Length: 2 } tuple)
        {
            return false;
        }

        var cursor = zipIndex + 1;
        var zipOutput = FollowSingleMove(instructions, ref cursor, zipTarget);
        if (cursor >= instructions.Count ||
            instructions[cursor] is not MirCall
            {
                Target: MirPlace { Kind: PlaceKind.Local } resultTarget,
                Function: MirFunctionRef sinkFunction,
                Arguments: [MirPlace { Kind: PlaceKind.Local } sinkSource, MirFunctionRef callback]
            } sinkCall ||
            sinkSource.Local != zipOutput.Local ||
            !TryGetDirectSinkKind(GetEffectiveSequenceRole(sinkFunction, functionsByKey), out var kind) ||
            !TryResolveCallback(functionsByKey, callback, out var callbackFunction))
        {
            return false;
        }

        var parameters = callbackFunction.Locals.Where(static local => local.IsParameter).ToArray();
        var expectedReturn = kind == DirectSequenceSinkKind.ForEach
            ? new TypeId(BaseTypes.UnitId)
            : new TypeId(BaseTypes.BoolId);
        if (parameters.Length != 1 || callbackFunction.ReturnType != expectedReturn)
        {
            Stats.FallbackUnknownCallback++;
            return false;
        }

        var callbackPairType = parameters[0].TypeId;
        if (kind == DirectSequenceSinkKind.Find &&
            !TryGetSharedBorrowInnerType(module, callbackPairType, out callbackPairType))
        {
            Stats.FallbackUnknownCallback++;
            return false;
        }

        if (callbackPairType != pairType ||
            ((!IsCopyType(module, tuple.FieldTypes[0]) ||
              !IsCopyType(module, tuple.FieldTypes[1]) ||
              !IsCopyType(module, pairType)) && kind != DirectSequenceSinkKind.Find) ||
            !HasSingleUseNonEscaping(function, left.Local) ||
            !HasSingleUseNonEscaping(function, right.Local) ||
            !HasSingleUseNonEscaping(function, zipTarget.Local) ||
            !HasSingleUseNonEscaping(function, zipOutput.Local))
        {
            Stats.FallbackOwnership++;
            return false;
        }

        if (_ownershipSnapshots.TryGetValue(MirFunctionIdentity.GetStableKey(function), out var snapshot) &&
            (!snapshot.IsMustOwned(left.Local, block.Id, zipIndex) ||
             !snapshot.IsMustUnique(left.Local, block.Id, zipIndex) ||
             snapshot.HasActiveBorrow(left.Local, block.Id, zipIndex) ||
             !snapshot.IsMustOwned(right.Local, block.Id, zipIndex) ||
             !snapshot.IsMustUnique(right.Local, block.Id, zipIndex) ||
             snapshot.HasActiveBorrow(right.Local, block.Id, zipIndex)))
        {
            Stats.FallbackOwnership++;
            return false;
        }

        plan = new DirectZipSequenceSinkPlan(
            block,
            zipIndex,
            zipIndex,
            cursor,
            left,
            right,
            zipTarget,
            sinkFunction,
            callback,
            resultTarget,
            tuple.FieldTypes[0],
            tuple.FieldTypes[1],
            pairType,
            parameters[0].TypeId,
            kind,
            sinkCall.Span);
        return true;
    }

    private bool TryFindFlatMapCountPlan(
        MirModule module,
        MirFunc function,
        MirBasicBlock block,
        int flatMapIndex,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out FlatMapCountPlan plan)
    {
        plan = null!;
        var instructions = block.Instructions;
        if (instructions[flatMapIndex] is not MirCall
            {
                Target: MirPlace { Kind: PlaceKind.Local } flatMapTarget,
                Function: MirFunctionRef flatMapFunction,
                Arguments: [MirPlace { Kind: PlaceKind.Local } source, MirFunctionRef mapper]
            } flatMapCall ||
            GetEffectiveSequenceRole(flatMapFunction, functionsByKey) != CompilerSemanticRole.SequenceFlatMap ||
            !TryResolveCallback(functionsByKey, mapper, out var mapperFunction))
        {
            return false;
        }

        var cursor = flatMapIndex + 1;
        var flatMapOutput = FollowSingleMove(instructions, ref cursor, flatMapTarget);
        if (cursor >= instructions.Count ||
            instructions[cursor] is not MirCall
            {
                Target: MirPlace { Kind: PlaceKind.Local } countTarget,
                Function: MirFunctionRef countFunction,
                Arguments: [MirPlace { Kind: PlaceKind.Local } countInput, MirFunctionRef predicate]
            } countCall ||
            countInput.Local != flatMapOutput.Local ||
            GetEffectiveSequenceRole(countFunction, functionsByKey) != CompilerSemanticRole.SequenceCount ||
            !TryResolveCallback(functionsByKey, predicate, out var predicateFunction))
        {
            return false;
        }

        var mapperParameters = mapperFunction.Locals.Where(static local => local.IsParameter).ToArray();
        var predicateParameters = predicateFunction.Locals.Where(static local => local.IsParameter).ToArray();
        if (mapperParameters.Length != 1 || predicateParameters.Length != 1 ||
            mapperFunction.ReturnType == TypeId.None ||
            predicateFunction.ReturnType != new TypeId(BaseTypes.BoolId) ||
            countTarget.TypeId != new TypeId(BaseTypes.IntId) ||
            !TryGetSequenceElementType(module, mapperFunction.ReturnType, out var innerElementType))
        {
            Stats.FallbackUnknownCallback++;
            return false;
        }

        var outerElementType = mapperParameters[0].TypeId;
        var predicateParameterType = predicateParameters[0].TypeId;
        if (!outerElementType.IsValid || !innerElementType.IsValid ||
            !IsCopyType(module, outerElementType) || !IsCopyType(module, innerElementType) ||
            !IsCopyType(module, predicateParameterType) ||
            !HasSingleUseNonEscaping(function, source.Local) ||
            !HasSingleUseNonEscaping(function, flatMapTarget.Local) ||
            !HasSingleUseNonEscaping(function, flatMapOutput.Local) ||
            !AllowsFlatMapInterleaving(mapper, mapperFunction, allowAllocation: true, allowReturnedAggregate: true) ||
            !AllowsFlatMapInterleaving(predicate, predicateFunction, allowAllocation: false, allowReturnedAggregate: false))
        {
            Stats.FallbackOwnership++;
            return false;
        }

        if (_ownershipSnapshots.TryGetValue(
                MirFunctionIdentity.GetStableKey(function),
                out var snapshot) &&
            (!snapshot.IsMustOwned(source.Local, block.Id, flatMapIndex) ||
             !snapshot.IsMustUnique(source.Local, block.Id, flatMapIndex) ||
             snapshot.HasActiveBorrow(source.Local, block.Id, flatMapIndex)))
        {
            Stats.FallbackOwnership++;
            return false;
        }

        plan = new FlatMapCountPlan(
            block,
            flatMapIndex,
            cursor,
            source,
            mapper,
            predicate,
            countTarget,
            outerElementType,
            mapperFunction.ReturnType,
            innerElementType,
            predicateParameterType,
            flatMapCall.Span,
            countCall.Span);
        return true;
    }

    private bool TryFindFlatMapDirectSinkPlan(
        MirModule module,
        MirFunc function,
        MirBasicBlock block,
        int flatMapIndex,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out FlatMapDirectSinkPlan plan)
    {
        plan = null!;
        var instructions = block.Instructions;
        if (instructions[flatMapIndex] is not MirCall
            {
                Target: MirPlace { Kind: PlaceKind.Local } flatMapTarget,
                Function: MirFunctionRef flatMapFunction,
                Arguments: [MirPlace { Kind: PlaceKind.Local } source, MirFunctionRef mapper]
            } flatMapCall ||
            GetEffectiveSequenceRole(flatMapFunction, functionsByKey) != CompilerSemanticRole.SequenceFlatMap ||
            !TryResolveCallback(functionsByKey, mapper, out var mapperFunction))
        {
            return false;
        }

        var cursor = flatMapIndex + 1;
        var flatMapOutput = FollowSingleMove(instructions, ref cursor, flatMapTarget);
        if (cursor >= instructions.Count ||
            instructions[cursor] is not MirCall
            {
                Target: MirPlace { Kind: PlaceKind.Local } resultTarget,
                Function: MirFunctionRef sinkFunction,
                Arguments: [MirPlace { Kind: PlaceKind.Local } sinkInput, MirFunctionRef predicate]
            } sinkCall ||
            sinkInput.Local != flatMapOutput.Local ||
            !TryGetDirectSinkKind(GetEffectiveSequenceRole(sinkFunction, functionsByKey), out var kind) ||
            kind == DirectSequenceSinkKind.Count ||
            !TryResolveCallback(functionsByKey, predicate, out var predicateFunction))
        {
            return false;
        }

        var mapperParameters = mapperFunction.Locals.Where(static local => local.IsParameter).ToArray();
        var predicateParameters = predicateFunction.Locals.Where(static local => local.IsParameter).ToArray();
        if (mapperParameters.Length != 1 || predicateParameters.Length != 1 ||
            mapperFunction.ReturnType == TypeId.None ||
            !TryGetSequenceElementType(module, mapperFunction.ReturnType, out var innerElementType))
        {
            Stats.FallbackUnknownCallback++;
            return false;
        }

        var outerElementType = mapperParameters[0].TypeId;
        var predicateParameterType = predicateParameters[0].TypeId;
        var expectedReturn = kind == DirectSequenceSinkKind.ForEach
            ? new TypeId(BaseTypes.UnitId)
            : new TypeId(BaseTypes.BoolId);
        var callbackElementType = kind == DirectSequenceSinkKind.Find
            ? TryGetSharedBorrowInnerType(module, predicateParameterType, out var borrowedInner)
                ? borrowedInner
                : TypeId.None
            : predicateParameterType;
        if (predicateFunction.ReturnType != expectedReturn ||
            !outerElementType.IsValid || !innerElementType.IsValid || !callbackElementType.IsValid ||
            !IsCopyType(module, outerElementType) ||
            (kind != DirectSequenceSinkKind.Find && !IsCopyType(module, innerElementType)) ||
            (kind != DirectSequenceSinkKind.Find && !IsCopyType(module, callbackElementType)) ||
            !HasSingleUseNonEscaping(function, source.Local) ||
            !HasSingleUseNonEscaping(function, flatMapTarget.Local) ||
            !HasSingleUseNonEscaping(function, flatMapOutput.Local) ||
            !AllowsFlatMapInterleaving(mapper, mapperFunction, allowAllocation: true, allowReturnedAggregate: true) ||
            !AllowsFlatMapInterleaving(predicate, predicateFunction, allowAllocation: false, allowReturnedAggregate: false))
        {
            Stats.FallbackOwnership++;
            return false;
        }

        if (_ownershipSnapshots.TryGetValue(MirFunctionIdentity.GetStableKey(function), out var snapshot) &&
            (!snapshot.IsMustOwned(source.Local, block.Id, flatMapIndex) ||
             !snapshot.IsMustUnique(source.Local, block.Id, flatMapIndex) ||
             snapshot.HasActiveBorrow(source.Local, block.Id, flatMapIndex) ||
             snapshot.EscapeFacts.GetValueOrDefault(source.Local) != OwnershipEscapeKind.None))
        {
            Stats.FallbackOwnership++;
            return false;
        }

        plan = new FlatMapDirectSinkPlan(
            block, flatMapIndex, cursor, source, mapper, predicate, resultTarget,
            outerElementType, mapperFunction.ReturnType, innerElementType,
            predicateParameterType, kind, flatMapCall.Span, sinkCall.Span);
        return true;
    }

    private bool TryFindFlatMapFoldPlan(
        MirModule module,
        MirFunc function,
        MirBasicBlock block,
        int flatMapIndex,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out FlatMapFoldPlan plan)
    {
        plan = null!;
        var instructions = block.Instructions;
        if (instructions[flatMapIndex] is not MirCall
            {
                Target: MirPlace { Kind: PlaceKind.Local } flatMapTarget,
                Function: MirFunctionRef flatMapFunction,
                Arguments: [MirPlace { Kind: PlaceKind.Local } source, MirFunctionRef mapper]
            } flatMapCall ||
            GetEffectiveSequenceRole(flatMapFunction, functionsByKey) != CompilerSemanticRole.SequenceFlatMap ||
            !TryResolveCallback(functionsByKey, mapper, out var mapperFunction))
        {
            return false;
        }

        var cursor = flatMapIndex + 1;
        var flatMapOutput = FollowSingleMove(instructions, ref cursor, flatMapTarget);
        if (cursor >= instructions.Count ||
            instructions[cursor] is not MirCall
            {
                Target: MirPlace { Kind: PlaceKind.Local } resultTarget,
                Function: MirFunctionRef foldFunction,
                Arguments: [MirPlace { Kind: PlaceKind.Local } foldInput, MirOperand initial, MirFunctionRef reducer]
            } foldCall ||
            foldInput.Local != flatMapOutput.Local ||
            GetEffectiveSequenceRole(foldFunction, functionsByKey) != CompilerSemanticRole.SequenceFoldLeft ||
            !TryResolveCallback(functionsByKey, reducer, out var reducerFunction))
        {
            return false;
        }

        var mapperParameters = mapperFunction.Locals.Where(static local => local.IsParameter).ToArray();
        var reducerParameters = reducerFunction.Locals.Where(static local => local.IsParameter).ToArray();
        if (mapperParameters.Length != 1 || reducerParameters.Length != 2 ||
            mapperFunction.ReturnType == TypeId.None ||
            !TryGetSequenceElementType(module, mapperFunction.ReturnType, out var innerElementType) ||
            reducerFunction.ReturnType != reducerParameters[0].TypeId ||
            reducerParameters[1].TypeId != innerElementType ||
            resultTarget.TypeId != reducerFunction.ReturnType)
        {
            Stats.FallbackUnknownCallback++;
            return false;
        }

        var outerElementType = mapperParameters[0].TypeId;
        var accumulatorType = reducerFunction.ReturnType;
        if (!outerElementType.IsValid || !accumulatorType.IsValid ||
            !IsCopyType(module, outerElementType) || !IsCopyType(module, innerElementType) ||
            !IsCopyType(module, accumulatorType) ||
            !HasSingleUseNonEscaping(function, source.Local) ||
            !HasSingleUseNonEscaping(function, flatMapTarget.Local) ||
            !HasSingleUseNonEscaping(function, flatMapOutput.Local) ||
            !AllowsFlatMapInterleaving(mapper, mapperFunction, allowAllocation: true, allowReturnedAggregate: true) ||
            !AllowsFlatMapInterleaving(reducer, reducerFunction, allowAllocation: false, allowReturnedAggregate: false))
        {
            Stats.FallbackOwnership++;
            return false;
        }

        if (_ownershipSnapshots.TryGetValue(MirFunctionIdentity.GetStableKey(function), out var snapshot) &&
            (!snapshot.CanDestructivelyUpdate(source.Local, block.Id, flatMapIndex) ||
             snapshot.EscapeFacts.GetValueOrDefault(source.Local) != OwnershipEscapeKind.None))
        {
            Stats.FallbackOwnership++;
            return false;
        }

        plan = new FlatMapFoldPlan(
            block, flatMapIndex, cursor, source, mapper, initial, reducer, resultTarget,
            outerElementType, mapperFunction.ReturnType, innerElementType, accumulatorType,
            flatMapCall.Span, foldCall.Span);
        return true;
    }

    private bool TryFindFlatMapCollectPlan(
        MirModule module,
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out FlatMapCollectPlan plan)
    {
        plan = null!;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            var instructions = block.Instructions;
            for (var flatMapIndex = 0; flatMapIndex < instructions.Count; flatMapIndex++)
            {
                if (instructions[flatMapIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } flatMapTarget,
                        Function: MirFunctionRef flatMapFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } source, MirFunctionRef mapper]
                    } flatMapCall ||
                    GetEffectiveSequenceRole(flatMapFunction, functionsByKey) != CompilerSemanticRole.SequenceFlatMap ||
                    !TryResolveCallback(functionsByKey, mapper, out var mapperFunction))
                {
                    continue;
                }

                var cursor = flatMapIndex + 1;
                var flatMapOutput = flatMapTarget;
                // A direct collector is represented by a move of the flat_map
                // result into the final Seq local. There is no public collect
                // role: this is the natural Seq materialization boundary.
                if (cursor >= instructions.Count ||
                    instructions[cursor] is not MirMove
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } resultTarget,
                        Source: MirPlace { Kind: PlaceKind.Local } collectInput
                    } ||
                    collectInput.Local != flatMapOutput.Local ||
                    resultTarget.TypeId != flatMapOutput.TypeId)
                {
                    continue;
                }

                var mapperParameters = mapperFunction.Locals.Where(static local => local.IsParameter).ToArray();
                if (mapperParameters.Length != 1 ||
                    mapperFunction.ReturnType == TypeId.None ||
                    !TryGetSequenceElementType(module, mapperFunction.ReturnType, out var innerElementType) ||
                    !innerElementType.IsValid ||
                    !mapperParameters[0].TypeId.IsValid ||
                    !HasSingleUseNonEscaping(function, source.Local) ||
                    !HasSingleUseNonEscaping(function, flatMapTarget.Local) ||
                    !HasSingleUseNonEscaping(function, flatMapOutput.Local) ||
                    !AllowsFlatMapInterleaving(mapper, mapperFunction, allowAllocation: true, allowReturnedAggregate: true))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                if (_ownershipSnapshots.TryGetValue(MirFunctionIdentity.GetStableKey(function), out var snapshot) &&
                    (!snapshot.IsMustOwned(source.Local, block.Id, flatMapIndex) ||
                     !snapshot.IsMustUnique(source.Local, block.Id, flatMapIndex) ||
                     snapshot.HasActiveBorrow(source.Local, block.Id, flatMapIndex) ||
                     snapshot.EscapeFacts.GetValueOrDefault(source.Local) != OwnershipEscapeKind.None))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                plan = new FlatMapCollectPlan(
                    block,
                    flatMapIndex,
                    cursor,
                    source,
                    mapper,
                    resultTarget,
                    mapperParameters[0].TypeId,
                    mapperFunction.ReturnType,
                    innerElementType,
                    GetRuntimeElementSize(module, innerElementType),
                    !IsCopyType(module, mapperParameters[0].TypeId),
                    flatMapCall.Span);
                return true;
            }
        }

        return false;
    }

    private static bool TryGetSequenceElementType(
        MirModule module,
        TypeId sequenceType,
        out TypeId elementType)
    {
        elementType = TypeId.None;
        if (!module.TypeDescriptors.TryGetValue(sequenceType.Value, out var descriptor) ||
            descriptor is not TypeDescriptor.TyCon { TypeArgs.Length: 1 } tyCon)
        {
            return false;
        }

        var isSeq = tyCon.Constructor switch
        {
            { Kind: TypeConstructorKeyKind.Symbol } constructor => module.TypeConstructors.Any(info =>
                info.SymbolId == constructor.SymbolId &&
                string.Equals(info.Name, WellKnownStrings.BuiltinTypes.Seq, StringComparison.Ordinal)),
            { Kind: TypeConstructorKeyKind.TypeId or TypeConstructorKeyKind.Builtin } constructor => module.TypeConstructors.Any(info =>
                info.TypeId == constructor.TypeId &&
                string.Equals(info.Name, WellKnownStrings.BuiltinTypes.Seq, StringComparison.Ordinal)),
            _ => false
        };
        if (!isSeq || !tyCon.TypeArgs[0].IsValid)
        {
            return false;
        }

        elementType = tyCon.TypeArgs[0];
        return true;
    }

    private static bool TryGetPartitionElementType(
        MirModule module,
        TypeId resultType,
        MirFunc callback,
        out TypeId elementType,
        out TypeId parameterType)
    {
        elementType = TypeId.None;
        parameterType = TypeId.None;
        var parameters = callback.Locals.Where(static local => local.IsParameter).ToArray();
        if (parameters.Length != 1 || callback.ReturnType != new TypeId(BaseTypes.BoolId) ||
            !TryGetSharedBorrowInnerType(module, parameters[0].TypeId, out elementType))
        {
            return false;
        }

        parameterType = parameters[0].TypeId;
        return module.TypeDescriptors.TryGetValue(resultType.Value, out var descriptor) &&
               descriptor is TypeDescriptor.Tuple { FieldTypes.Length: 2 } tuple &&
               tuple.FieldTypes[0] == tuple.FieldTypes[1] &&
               tuple.FieldTypes[0].IsValid;
    }

    private bool TryFindViewSpine(
        MirBasicBlock block,
        int sinkIndex,
        MirPlace sinkSource,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out MirPlace source,
        out int firstInstructionIndex,
        out IReadOnlyList<ViewStageMatch> stages)
    {
        source = sinkSource;
        firstInstructionIndex = sinkIndex;
        var reversed = new List<ViewStageMatch>();
        var cursor = sinkIndex - 1;
        var current = sinkSource.Local;
        while (cursor >= 0)
        {
            if (block.Instructions[cursor] is MirMove
                {
                    Target: MirPlace { Kind: PlaceKind.Local, Local: var moveTarget },
                    Source: MirPlace { Kind: PlaceKind.Local, Local: var moveSource }
                } && moveTarget == current)
            {
                current = moveSource;
                source = new MirPlace
                {
                    Kind = PlaceKind.Local,
                    Local = moveSource,
                    TypeId = source.TypeId,
                    Span = source.Span
                };
                firstInstructionIndex = cursor;
                cursor--;
                continue;
            }

            if (block.Instructions[cursor] is not MirCall
                {
                    Target: MirPlace { Kind: PlaceKind.Local, Local: var targetLocal },
                    Function: MirFunctionRef viewFunction
                } call || targetLocal != current)
            {
                break;
            }

            var role = GetEffectiveSequenceRole(viewFunction, functionsByKey);
            if (role == CompilerSemanticRole.SequenceTake ||
                role == CompilerSemanticRole.SequenceDrop)
            {
                if (call.Arguments is not [MirPlace { Kind: PlaceKind.Local } input, MirConstant bound] ||
                    !TryGetNonNegativeIntConstant(bound, out var value))
                {
                    source = sinkSource;
                    firstInstructionIndex = sinkIndex;
                    stages = [];
                    return true;
                }

                reversed.Add(new ViewStageMatch(
                    role == CompilerSemanticRole.SequenceTake
                        ? new SequenceTakeViewStagePlan(value)
                        : new SequenceDropViewStagePlan(value),
                    targetLocal,
                    input,
                    cursor,
                    call.Span));
                current = input.Local;
                source = input;
                firstInstructionIndex = cursor;
                cursor--;
                continue;
            }

            if (role == CompilerSemanticRole.SequenceReverse &&
                call.Arguments is [MirPlace { Kind: PlaceKind.Local } reverseInput])
            {
                reversed.Add(new ViewStageMatch(
                    new SequenceReverseStagePlan(),
                    targetLocal,
                    reverseInput,
                    cursor,
                    call.Span));
                current = reverseInput.Local;
                source = reverseInput;
                firstInstructionIndex = cursor;
                cursor--;
                continue;
            }

            if (reversed.Count > 0)
            {
                source = sinkSource;
                firstInstructionIndex = sinkIndex;
                stages = [];
                return true;
            }

            break;
        }

        reversed.Reverse();
        if (reversed.Count == 0)
        {
            // A direct sink without a view must retain the sink's immediate
            // owned temporary as its proof root.  Walking an earlier move
            // would include the array-literal stores in the source read count
            // and incorrectly reject an otherwise valid sink lowering.
            source = sinkSource;
            firstInstructionIndex = sinkIndex;
        }
        stages = reversed;
        return source.Local != sinkSource.Local || stages.Count == 0;
    }

    private static bool TryGetNonNegativeIntConstant(MirConstant constant, out long value)
    {
        if (constant.Value is MirConstantValue.IntValue { Value: >= 0 } intValue)
        {
            value = intValue.Value;
            return true;
        }

        value = 0;
        return false;
    }

    private sealed record ViewStageMatch(
        SequenceStagePlan Plan,
        LocalId Output,
        MirPlace Input,
        int InstructionIndex,
        SourceSpan Span);

    private static bool TryGetDirectSinkKind(
        CompilerSemanticRole role,
        out DirectSequenceSinkKind kind)
    {
        kind = role switch
        {
            CompilerSemanticRole.SequenceFind => DirectSequenceSinkKind.Find,
            CompilerSemanticRole.SequenceAny => DirectSequenceSinkKind.Any,
            CompilerSemanticRole.SequenceAll => DirectSequenceSinkKind.All,
            CompilerSemanticRole.SequenceCount => DirectSequenceSinkKind.Count,
            CompilerSemanticRole.SequenceForEach => DirectSequenceSinkKind.ForEach,
            _ => default
        };
        return role is CompilerSemanticRole.SequenceFind or
            CompilerSemanticRole.SequenceAny or
            CompilerSemanticRole.SequenceAll or
            CompilerSemanticRole.SequenceCount or
            CompilerSemanticRole.SequenceForEach;
    }

    private bool TryFindDropDropPlan(
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out SequencePipelinePlan plan)
    {
        plan = null!;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            var instructions = block.Instructions;
            for (var firstIndex = 0; firstIndex < instructions.Count; firstIndex++)
            {
                if (instructions[firstIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } firstTarget,
                        Function: MirFunctionRef firstFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } source, MirConstant firstBound]
                    } firstCall ||
                    GetEffectiveSequenceRole(firstFunction, functionsByKey) != CompilerSemanticRole.SequenceDrop ||
                    !TryGetPositiveIntConstant(firstBound, out var firstValue))
                {
                    continue;
                }

                var cursor = firstIndex + 1;
                var firstOutput = FollowSingleMove(instructions, ref cursor, firstTarget);
                if (cursor >= instructions.Count ||
                    instructions[cursor] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } resultTarget,
                        Function: MirFunctionRef secondFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } secondInput, MirConstant secondBound]
                    } secondCall ||
                    secondInput.Local != firstOutput.Local ||
                    GetEffectiveSequenceRole(secondFunction, functionsByKey) != CompilerSemanticRole.SequenceDrop ||
                    !TryGetPositiveIntConstant(secondBound, out var secondValue) ||
                    firstValue > long.MaxValue - secondValue)
                {
                    continue;
                }

                if (!HasSingleUseNonEscaping(function, source.Local) ||
                    !HasSingleUseNonEscaping(function, firstTarget.Local) ||
                    !HasSingleUseNonEscaping(function, firstOutput.Local))
                {
                    Stats.FallbackMultiUse++;
                    continue;
                }

                plan = new DropDropPlan(
                    block,
                    firstIndex,
                    cursor,
                    source,
                    secondFunction,
                    resultTarget,
                    firstValue + secondValue,
                    firstCall.Span,
                    secondCall.Span);
                return true;
            }
        }

        return false;
    }

    private bool TryFindTakeTakePlan(
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out SequencePipelinePlan plan)
    {
        plan = null!;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            var instructions = block.Instructions;
            for (var firstIndex = 0; firstIndex < instructions.Count; firstIndex++)
            {
                if (instructions[firstIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } firstTarget,
                        Function: MirFunctionRef firstFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } source, MirConstant firstBound]
                    } firstCall ||
                    GetEffectiveSequenceRole(firstFunction, functionsByKey) != CompilerSemanticRole.SequenceTake ||
                    !TryGetPositiveIntConstant(firstBound, out var firstValue))
                {
                    continue;
                }

                var cursor = firstIndex + 1;
                var firstOutput = FollowSingleMove(instructions, ref cursor, firstTarget);
                if (cursor >= instructions.Count ||
                    instructions[cursor] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } resultTarget,
                        Function: MirFunctionRef secondFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } secondInput, MirConstant secondBound]
                    } secondCall ||
                    secondInput.Local != firstOutput.Local ||
                    GetEffectiveSequenceRole(secondFunction, functionsByKey) != CompilerSemanticRole.SequenceTake ||
                    !TryGetPositiveIntConstant(secondBound, out var secondValue))
                {
                    continue;
                }

                if (!HasSingleUseNonEscaping(function, source.Local) ||
                    !HasSingleUseNonEscaping(function, firstTarget.Local) ||
                    !HasSingleUseNonEscaping(function, firstOutput.Local))
                {
                    Stats.FallbackMultiUse++;
                    continue;
                }

                plan = new TakeTakePlan(
                    block,
                    firstIndex,
                    cursor,
                    source,
                    secondFunction,
                    resultTarget,
                    Math.Min(firstValue, secondValue),
                    firstCall.Span,
                    secondCall.Span);
                return true;
            }
        }

        return false;
    }

    private bool TryFindTakeHeadPlan(
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out SequencePipelinePlan plan)
    {
        plan = null!;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            var instructions = block.Instructions;
            for (var takeIndex = 0; takeIndex < instructions.Count; takeIndex++)
            {
                if (instructions[takeIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } takeTarget,
                        Function: MirFunctionRef takeFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } source, MirConstant bound]
                    } takeCall ||
                    GetEffectiveSequenceRole(takeFunction, functionsByKey) != CompilerSemanticRole.SequenceTake ||
                    !TryGetPositiveIntConstant(bound))
                {
                    continue;
                }

                var cursor = takeIndex + 1;
                var takeOutput = FollowSingleMove(instructions, ref cursor, takeTarget);
                if (cursor >= instructions.Count ||
                    instructions[cursor] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } headTarget,
                        Function: MirFunctionRef headFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } headInput]
                    } headCall ||
                    headInput.Local != takeOutput.Local ||
                    GetEffectiveSequenceRole(headFunction, functionsByKey) != CompilerSemanticRole.SequenceHead)
                {
                    continue;
                }

                if (!HasSingleUseNonEscaping(function, source.Local) ||
                    !HasSingleUseNonEscaping(function, takeTarget.Local) ||
                    !HasSingleUseNonEscaping(function, takeOutput.Local))
                {
                    Stats.FallbackMultiUse++;
                    continue;
                }

                plan = new TakeHeadPlan(
                    block,
                    takeIndex,
                    cursor,
                    source,
                    headFunction,
                    headTarget,
                    takeCall.Span,
                    headCall.Span);
                return true;
            }
        }

        return false;
    }
}
