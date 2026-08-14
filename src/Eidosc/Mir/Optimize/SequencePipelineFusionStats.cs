namespace Eidosc.Mir.Optimize;

public sealed class SequencePipelineFusionStats
{
    public long FunctionsScanned { get; internal set; }
    public long RoleCalls { get; internal set; }
    public long PipelinesFormed { get; internal set; }
    public long MapFoldPipelines { get; internal set; }
    public long MapFilterFoldPipelines { get; internal set; }
    public long MapFilterCollectPipelines { get; internal set; }
    public long ZipWithFoldPipelines { get; internal set; }
    public long FlatMapCountPipelines { get; internal set; }
    public long FlatMapDirectSinkPipelines { get; internal set; }
    public long FlatMapFoldPipelines { get; internal set; }
    public long FlatMapCollectPipelines { get; internal set; }
    public long ZipSinkPipelines { get; internal set; }
    public long PartitionSinksLowered { get; internal set; }
    public long DropDropPipelines { get; internal set; }
    public long TakeTakePipelines { get; internal set; }
    public long TakeHeadPipelines { get; internal set; }
    public long FilterHeadPipelines { get; internal set; }
    public long DirectFoldsLowered { get; internal set; }
    public long SinkPlansDiscovered { get; internal set; }
    public long SinkPlansLowered { get; internal set; }
    public long SourceLoopsEmitted { get; internal set; }
    public long IntermediatesElided { get; internal set; }
    public long FallbackEffect { get; internal set; }
    public long FallbackPanicOrDivergence { get; internal set; }
    public long FallbackUnknownCallback { get; internal set; }
    public long FallbackMultiUse { get; internal set; }
    public long FallbackEscape { get; internal set; }
    public long FallbackOwnership { get; internal set; }
    public long FallbackShapeAfterMap { get; internal set; }
    public long FallbackShapeAfterFilter { get; internal set; }
    public long CollectorsStackPromoted { get; internal set; }
    public long ClosuresElided { get; internal set; }
    public long EvidenceElided { get; internal set; }

    public IReadOnlyDictionary<string, long> ToMetricsSnapshot() =>
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["sequence.functions_scanned"] = FunctionsScanned,
            ["sequence.role_calls"] = RoleCalls,
            ["sequence.pipelines_formed"] = PipelinesFormed,
            ["sequence.pipeline.map_fold"] = MapFoldPipelines,
            ["sequence.pipeline.map_filter_fold"] = MapFilterFoldPipelines,
            ["sequence.pipeline.map_filter_collect"] = MapFilterCollectPipelines,
            ["sequence.pipeline.zip_with_fold"] = ZipWithFoldPipelines,
            ["sequence.pipeline.flat_map_count"] = FlatMapCountPipelines,
            ["sequence.pipeline.flat_map_direct_sink"] = FlatMapDirectSinkPipelines,
            ["sequence.pipeline.flat_map_fold"] = FlatMapFoldPipelines,
            ["sequence.pipeline.flat_map_collect"] = FlatMapCollectPipelines,
            ["sequence.pipeline.zip_sink"] = ZipSinkPipelines,
            ["sequence.partition_sinks_lowered"] = PartitionSinksLowered,
            ["sequence.pipeline.drop_drop"] = DropDropPipelines,
            ["sequence.pipeline.take_take"] = TakeTakePipelines,
            ["sequence.pipeline.take_head"] = TakeHeadPipelines,
            ["sequence.pipeline.filter_head"] = FilterHeadPipelines,
            ["sequence.direct_folds_lowered"] = DirectFoldsLowered,
            ["sequence.sink_plans_discovered"] = SinkPlansDiscovered,
            ["sequence.sink_plans_lowered"] = SinkPlansLowered,
            ["sequence.source_loops_emitted"] = SourceLoopsEmitted,
            ["sequence.intermediates_elided"] = IntermediatesElided,
            ["sequence.fallback.effect"] = FallbackEffect,
            ["sequence.fallback.panic_or_divergence"] = FallbackPanicOrDivergence,
            ["sequence.fallback.unknown_callback"] = FallbackUnknownCallback,
            ["sequence.fallback.multi_use"] = FallbackMultiUse,
            ["sequence.fallback.escape"] = FallbackEscape,
            ["sequence.fallback.ownership"] = FallbackOwnership,
            ["sequence.fallback.shape_after_map"] = FallbackShapeAfterMap,
            ["sequence.fallback.shape_after_filter"] = FallbackShapeAfterFilter,
            ["sequence.collectors_stack_promoted"] = CollectorsStackPromoted,
            ["sequence.closures_elided"] = ClosuresElided,
            ["sequence.evidence_elided"] = EvidenceElided
        };

    internal void Reset()
    {
        FunctionsScanned = 0;
        RoleCalls = 0;
        PipelinesFormed = 0;
        MapFoldPipelines = 0;
        MapFilterFoldPipelines = 0;
        MapFilterCollectPipelines = 0;
        ZipWithFoldPipelines = 0;
        FlatMapCountPipelines = 0;
        FlatMapDirectSinkPipelines = 0;
        FlatMapFoldPipelines = 0;
        FlatMapCollectPipelines = 0;
        ZipSinkPipelines = 0;
        PartitionSinksLowered = 0;
        DropDropPipelines = 0;
        TakeTakePipelines = 0;
        TakeHeadPipelines = 0;
        FilterHeadPipelines = 0;
        DirectFoldsLowered = 0;
        SinkPlansDiscovered = 0;
        SinkPlansLowered = 0;
        SourceLoopsEmitted = 0;
        IntermediatesElided = 0;
        FallbackEffect = 0;
        FallbackPanicOrDivergence = 0;
        FallbackUnknownCallback = 0;
        FallbackMultiUse = 0;
        FallbackEscape = 0;
        FallbackOwnership = 0;
        FallbackShapeAfterMap = 0;
        FallbackShapeAfterFilter = 0;
        CollectorsStackPromoted = 0;
        ClosuresElided = 0;
        EvidenceElided = 0;
    }
}
