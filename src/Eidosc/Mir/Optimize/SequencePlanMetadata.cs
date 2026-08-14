using Eidosc.Borrow;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

internal static class SequencePlanMetadata
{
    public static SequencePipelinePlan Attach(
        MirFunc function,
        SequencePipelinePlan plan,
        IReadOnlyDictionary<string, OwnershipAnalysisSnapshot> snapshots)
    {
        if (plan.UnifiedPlan != null)
            return plan;

        var snapshot = snapshots.GetValueOrDefault(MirFunctionIdentity.GetStableKey(function));
        return plan switch
        {
            DropDropPlan drop => drop with
            {
                UnifiedPlan = Create(
                    drop.Source,
                    TypeId.None,
                    drop.ResultTarget.TypeId,
                    [new SequenceDropStagePlan(drop.Bound)],
                    new SequenceIdentitySinkPlan(),
                    drop.Block,
                    drop.FirstInstructionIndex,
                    snapshot,
                    [drop.FirstSpan, drop.SecondSpan],
                    ["source", "first-drop-bound", "second-drop-bound"])
            },
            TakeTakePlan take => take with
            {
                UnifiedPlan = Create(
                    take.Source,
                    TypeId.None,
                    take.ResultTarget.TypeId,
                    [new SequenceTakeStagePlan(take.Bound)],
                    new SequenceIdentitySinkPlan(),
                    take.Block,
                    take.FirstInstructionIndex,
                    snapshot,
                    [take.FirstSpan, take.SecondSpan],
                    ["source", "first-take-bound", "second-take-bound"])
            },
            TakeHeadPlan takeHead => takeHead with
            {
                UnifiedPlan = Create(
                    takeHead.Source,
                    TypeId.None,
                    takeHead.HeadTarget.TypeId,
                    [new SequenceTakeViewStagePlan(1)],
                    new SequenceHeadSinkPlan(takeHead.HeadFunction),
                    takeHead.Block,
                    takeHead.TakeInstructionIndex,
                    snapshot,
                    [takeHead.TakeSpan, takeHead.HeadSpan],
                    ["source", "take-bound", "head"])
            },
            FilterTakeHeadPlan filterTakeHead => filterTakeHead with
            {
                UnifiedPlan = Create(
                    filterTakeHead.Source,
                    TypeId.None,
                    filterTakeHead.HeadTarget.TypeId,
                    [
                        new SequenceFilterStagePlan(filterTakeHead.Predicate),
                        new SequenceTakeViewStagePlan(filterTakeHead.Bound)
                    ],
                    new SequenceFindSinkPlan(filterTakeHead.FindFunction),
                    filterTakeHead.Block,
                    filterTakeHead.FilterInstructionIndex,
                    snapshot,
                    [filterTakeHead.FilterSpan, filterTakeHead.TakeSpan, filterTakeHead.HeadSpan],
                    ["source", "predicate", "take-bound", "find"])
            },
            MapFoldPlan mapFold => mapFold with
            {
                UnifiedPlan = Create(
                    mapFold.Source,
                    mapFold.SourceElementType,
                    mapFold.FoldTarget.TypeId,
                    [new SequenceMapStagePlan(mapFold.Mapper)],
                    new SequenceFoldSinkPlan(mapFold.Reducer),
                    mapFold.Block,
                    mapFold.StartInstructionIndex,
                    snapshot,
                    [mapFold.MapSpan, mapFold.FoldSpan],
                    ["source", "mapper", "initial", "reducer"])
            },
            ZipWithFoldPlan zipFold => zipFold with
            {
                UnifiedPlan = Create(
                    zipFold.LeftSource,
                    zipFold.LeftElementType,
                    zipFold.FoldTarget.TypeId,
                    [new SequenceZipStagePlan(zipFold.RightSource, zipFold.Combiner)],
                    new SequenceFoldSinkPlan(zipFold.Reducer),
                    zipFold.Block,
                    zipFold.StartInstructionIndex,
                    snapshot,
                    [zipFold.ZipSpan, zipFold.FoldSpan],
                    ["left", "right", "combiner", "initial", "reducer"])
            },
            FlatMapCountPlan flatMapCount => flatMapCount with
            {
                UnifiedPlan = Create(
                    flatMapCount.Source,
                    flatMapCount.OuterElementType,
                    flatMapCount.CountTarget.TypeId,
                    [new SequenceFlatMapStagePlan(flatMapCount.Mapper)],
                    new SequenceCountSinkPlan(flatMapCount.Predicate),
                    flatMapCount.Block,
                    flatMapCount.FlatMapInstructionIndex,
                    snapshot,
                    [flatMapCount.FlatMapSpan, flatMapCount.CountSpan],
                    ["source", "mapper", "inner-source", "predicate", "count"])
            },
            FlatMapDirectSinkPlan flatMapSink => flatMapSink with
            {
                UnifiedPlan = Create(
                    flatMapSink.Source,
                    flatMapSink.OuterElementType,
                    flatMapSink.ResultTarget.TypeId,
                    [new SequenceFlatMapStagePlan(flatMapSink.Mapper)],
                    CreateFlatMapSink(flatMapSink),
                    flatMapSink.Block,
                    flatMapSink.FlatMapInstructionIndex,
                    snapshot,
                    [flatMapSink.FlatMapSpan, flatMapSink.SinkSpan],
                    ["source", "mapper", "inner-source", "predicate", flatMapSink.Kind.ToString().ToLowerInvariant()])
            },
            FlatMapFoldPlan flatMapFold => flatMapFold with
            {
                UnifiedPlan = Create(
                    flatMapFold.Source,
                    flatMapFold.OuterElementType,
                    flatMapFold.ResultTarget.TypeId,
                    [new SequenceFlatMapStagePlan(flatMapFold.Mapper)],
                    new SequenceFoldSinkPlan(flatMapFold.Reducer),
                    flatMapFold.Block,
                    flatMapFold.FlatMapInstructionIndex,
                    snapshot,
                    [flatMapFold.FlatMapSpan, flatMapFold.FoldSpan],
                    ["source", "mapper", "inner-source", "initial", "reducer"])
            },
            FlatMapCollectPlan flatMapCollect => flatMapCollect with
            {
                UnifiedPlan = Create(
                    flatMapCollect.Source,
                    flatMapCollect.OuterElementType,
                    flatMapCollect.ResultTarget.TypeId,
                    [new SequenceFlatMapStagePlan(flatMapCollect.Mapper)],
                    new SequenceCollectSinkPlan(flatMapCollect.ResultTarget),
                    flatMapCollect.Block,
                    flatMapCollect.FlatMapInstructionIndex,
                    snapshot,
                    [flatMapCollect.FlatMapSpan],
                    ["source", "mapper", "inner-source", "collect"])
            },
            DirectZipSequenceSinkPlan zipSink => zipSink with
            {
                UnifiedPlan = Create(
                    zipSink.LeftSource,
                    zipSink.LeftElementType,
                    zipSink.ResultTarget.TypeId,
                    [new SequenceZipStagePlan(zipSink.RightSource, null)],
                    CreateZipSink(zipSink),
                    zipSink.Block,
                    zipSink.FirstInstructionIndex,
                    snapshot,
                    [zipSink.SinkSpan],
                    ["left", "right", "zip", "callback", zipSink.Kind.ToString().ToLowerInvariant()])
            },
            MapFilterFoldPlan mapFilterFold => mapFilterFold with
            {
                UnifiedPlan = Create(
                    mapFilterFold.Source,
                    mapFilterFold.SourceElementType,
                    mapFilterFold.FoldTarget.TypeId,
                    [
                        new SequenceMapStagePlan(mapFilterFold.Mapper),
                        new SequenceFilterStagePlan(mapFilterFold.Predicate)
                    ],
                    new SequenceFoldSinkPlan(mapFilterFold.Reducer),
                    mapFilterFold.Block,
                    mapFilterFold.StartInstructionIndex,
                    snapshot,
                    [mapFilterFold.MapSpan, mapFilterFold.FilterSpan, mapFilterFold.FoldSpan],
                    ["source", "mapper", "predicate", "initial", "reducer"])
            },
            MapFilterCollectPlan collect => collect with
            {
                UnifiedPlan = Create(
                    collect.Source,
                    collect.SourceElementType,
                    collect.ResultTarget.TypeId,
                    [
                        new SequenceMapStagePlan(collect.Mapper),
                        new SequenceFilterStagePlan(collect.Predicate)
                    ],
                    new SequenceCollectSinkPlan(collect.ResultTarget),
                    collect.Block,
                    collect.StartInstructionIndex,
                    snapshot,
                    [collect.MapSpan, collect.FilterSpan],
                    ["source", "mapper", "predicate", "collect"])
            },
            DirectFoldPlan fold => fold with
            {
                UnifiedPlan = Create(
                    fold.Source,
                    fold.SourceElementType,
                    fold.FoldTarget.TypeId,
                    [],
                    new SequenceFoldSinkPlan(fold.Reducer),
                    fold.Block,
                    fold.InstructionIndex,
                    snapshot,
                    [fold.FoldSpan],
                    ["source", "initial", "reducer"])
            },
            DirectSequenceSinkPlan sink => sink with
            {
                UnifiedPlan = Create(
                    sink.Source,
                    sink.ElementType,
                    sink.ResultTarget.TypeId,
                    sink.Stages,
                    CreateSink(sink),
                    sink.Block,
                    sink.FirstInstructionIndex,
                    snapshot,
                    [sink.SinkSpan],
                    sink.Stages
                        .Select(static stage => stage switch
                        {
                            SequenceTakeViewStagePlan => "take-view",
                            SequenceDropViewStagePlan => "drop-view",
                            SequenceReverseStagePlan => "reverse-view",
                            _ => "stage"
                        })
                        .Append("source")
                        .Append("callback")
                        .Append(sink.Kind.ToString().ToLowerInvariant())
                        .ToArray())
            },
            DirectPartitionPlan partition => partition with
            {
                UnifiedPlan = Create(
                    partition.Source,
                    partition.ElementType,
                    partition.ResultTarget.TypeId,
                    [],
                    new SequencePartitionSinkPlan(partition.Predicate),
                    partition.Block,
                    partition.InstructionIndex,
                    snapshot,
                    [partition.SinkSpan],
                    ["source", "predicate", "partition"])
            },
            _ => plan
        };
    }

    private static SequenceSinkPlan CreateSink(DirectSequenceSinkPlan plan) => plan.Kind switch
    {
        DirectSequenceSinkKind.Find => new SequenceFindSinkPlan(plan.SinkFunction),
        DirectSequenceSinkKind.Any => new SequenceAnySinkPlan(plan.SinkFunction),
        DirectSequenceSinkKind.All => new SequenceAllSinkPlan(plan.SinkFunction),
        DirectSequenceSinkKind.Count => new SequenceCountSinkPlan(plan.SinkFunction),
        DirectSequenceSinkKind.ForEach => new SequenceForEachSinkPlan(plan.SinkFunction),
        _ => throw new ArgumentOutOfRangeException(nameof(plan.Kind), plan.Kind, null)
    };

    private static SequenceSinkPlan CreateZipSink(DirectZipSequenceSinkPlan plan) => plan.Kind switch
    {
        DirectSequenceSinkKind.Find => new SequenceFindSinkPlan(plan.SinkFunction),
        DirectSequenceSinkKind.Any => new SequenceAnySinkPlan(plan.SinkFunction),
        DirectSequenceSinkKind.All => new SequenceAllSinkPlan(plan.SinkFunction),
        DirectSequenceSinkKind.Count => new SequenceCountSinkPlan(plan.SinkFunction),
        DirectSequenceSinkKind.ForEach => new SequenceForEachSinkPlan(plan.SinkFunction),
        _ => throw new ArgumentOutOfRangeException(nameof(plan.Kind), plan.Kind, null)
    };

    private static SequenceSinkPlan CreateFlatMapSink(FlatMapDirectSinkPlan plan) => plan.Kind switch
    {
        DirectSequenceSinkKind.Find => new SequenceFindSinkPlan(plan.Predicate),
        DirectSequenceSinkKind.Any => new SequenceAnySinkPlan(plan.Predicate),
        DirectSequenceSinkKind.All => new SequenceAllSinkPlan(plan.Predicate),
        DirectSequenceSinkKind.ForEach => new SequenceForEachSinkPlan(plan.Predicate),
        _ => throw new ArgumentOutOfRangeException(nameof(plan.Kind), plan.Kind, null)
    };

    private static SequencePlan Create(
        MirPlace source,
        TypeId elementType,
        TypeId resultType,
        IReadOnlyList<SequenceStagePlan> stages,
        SequenceSinkPlan sink,
        MirBasicBlock block,
        int instructionIndex,
        OwnershipAnalysisSnapshot? snapshot,
        IReadOnlyList<SourceSpan> spans,
        IReadOnlyList<string> evaluationSteps)
    {
        var mustOwned = snapshot?.IsMustOwned(source.Local, block.Id, instructionIndex) ?? false;
        var mustUnique = snapshot?.IsMustUnique(source.Local, block.Id, instructionIndex) ?? false;
        var activeBorrow = snapshot?.HasActiveBorrow(source.Local, block.Id, instructionIndex) ?? true;
        var escape = snapshot?.EscapeFacts.GetValueOrDefault(source.Local) ?? OwnershipEscapeKind.UnknownCall;
        var cleanupComplete = snapshot?.ExitCleanupFacts.Values.All(static fact =>
            fact.LocalsRequiringCleanup.IsSubsetOf(fact.LocalsWithInsertedDrop)) ?? false;
        var ownershipSafe = mustOwned && mustUnique && !activeBorrow && escape == OwnershipEscapeKind.None && cleanupComplete;
        return new SequencePlan(
            new SequenceSourcePlan(source, elementType),
            stages,
            sink,
            elementType,
            resultType,
            new SequenceEvaluationOrderPlan(evaluationSteps),
            new SequenceOwnershipRoutePlan(
                mustOwned,
                mustUnique,
                NoAlias: snapshot?.AliasSets.GetValueOrDefault(source.Local)?.Count == 1,
                NoActiveBorrow: !activeBorrow,
                NoEscape: escape == OwnershipEscapeKind.None,
                CleanupComplete: cleanupComplete,
                SnapshotFingerprint: snapshot?.BodyFingerprint),
            new SequenceStoragePlan(
                MaterializeIntermediate: false,
                PreferSourceReuse: ownershipSafe,
                UseInternalView: stages.Any(static stage => stage is SequenceTakeViewStagePlan or SequenceDropViewStagePlan),
                AllowStackPromotion: false),
            new SequenceProofSummary(
                CallbackReorder: true,
                SingleUse: true,
                NonEscaping: escape == OwnershipEscapeKind.None,
                EffectsSafe: true,
                OwnershipSafe: ownershipSafe,
                RepresentationSafe: true,
                FallbackReason: null),
            new SequenceOriginSpansPlan(spans));
    }
}
