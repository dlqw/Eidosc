using Eidosc.Types;
using Eidosc.Utils;
using Eidosc.Borrow;

namespace Eidosc.Mir.Optimize;

internal sealed record SequenceSourcePlan(MirPlace Place, TypeId ElementType);

internal abstract record SequenceStagePlan;

internal sealed record SequenceFilterStagePlan(MirFunctionRef Predicate) : SequenceStagePlan;

internal sealed record SequenceMapStagePlan(MirFunctionRef Mapper) : SequenceStagePlan;

internal sealed record SequenceTakeStagePlan(long Bound) : SequenceStagePlan;

internal sealed record SequenceDropStagePlan(long Bound) : SequenceStagePlan;

internal sealed record SequenceTakeViewStagePlan(long Bound) : SequenceStagePlan;

internal sealed record SequenceDropViewStagePlan(long Bound) : SequenceStagePlan;

internal sealed record SequenceReverseStagePlan : SequenceStagePlan;

internal sealed record SequenceZipStagePlan(MirPlace OtherSource, MirFunctionRef? Combiner) : SequenceStagePlan;

internal sealed record SequenceFlatMapStagePlan(MirFunctionRef Mapper) : SequenceStagePlan;

internal abstract record SequenceSinkPlan;

internal sealed record SequenceCollectSinkPlan(MirPlace Result) : SequenceSinkPlan;

internal sealed record SequenceIdentitySinkPlan : SequenceSinkPlan;

internal sealed record SequenceFindSinkPlan(MirFunctionRef Function) : SequenceSinkPlan;

internal sealed record SequenceAnySinkPlan(MirFunctionRef Function) : SequenceSinkPlan;

internal sealed record SequenceAllSinkPlan(MirFunctionRef Function) : SequenceSinkPlan;

internal sealed record SequenceCountSinkPlan(MirFunctionRef Function) : SequenceSinkPlan;

internal sealed record SequenceForEachSinkPlan(MirFunctionRef Function) : SequenceSinkPlan;

internal sealed record SequencePartitionSinkPlan(MirFunctionRef Predicate) : SequenceSinkPlan;

internal sealed record SequenceHeadSinkPlan(MirFunctionRef Function) : SequenceSinkPlan;

internal sealed record SequenceFoldSinkPlan(MirFunctionRef Function) : SequenceSinkPlan;

internal sealed record SequenceEvaluationOrderPlan(IReadOnlyList<string> Steps);

internal sealed record SequenceOwnershipRoutePlan(
    bool SourceMustOwned,
    bool SourceMustUnique,
    bool NoAlias,
    bool NoActiveBorrow,
    bool NoEscape,
    bool CleanupComplete,
    string? SnapshotFingerprint);

internal sealed record SequenceStoragePlan(
    bool MaterializeIntermediate,
    bool PreferSourceReuse,
    bool UseInternalView,
    bool AllowStackPromotion);

internal sealed record SequenceProofSummary(
    bool CallbackReorder,
    bool SingleUse,
    bool NonEscaping,
    bool EffectsSafe,
    bool OwnershipSafe,
    bool RepresentationSafe,
    string? FallbackReason);

internal sealed record SequenceOriginSpansPlan(IReadOnlyList<SourceSpan> Spans);

/// <summary>
/// Unified compiler-internal sequence plan metadata. Concrete lowering nodes
/// retain only operation-specific MIR coordinates while all proof and storage
/// decisions flow through this structure.
/// </summary>
internal sealed record SequencePlan(
    SequenceSourcePlan Source,
    IReadOnlyList<SequenceStagePlan> Stages,
    SequenceSinkPlan Sink,
    TypeId ElementType,
    TypeId ResultType,
    SequenceEvaluationOrderPlan EvaluationOrder,
    SequenceOwnershipRoutePlan OwnershipRoute,
    SequenceStoragePlan StoragePlan,
    SequenceProofSummary ProofSummary,
    SequenceOriginSpansPlan OriginSpans);

internal abstract record SequencePipelinePlan(int IntermediatesElided)
{
    /// <summary>
    /// Unified plan metadata. This is populated for every discovered node;
    /// operation-specific records are temporary lowering coordinates only.
    /// </summary>
    public SequencePlan? UnifiedPlan { get; init; }
}

internal sealed record DropDropPlan(
    MirBasicBlock Block,
    int FirstInstructionIndex,
    int SecondInstructionIndex,
    MirPlace Source,
    MirFunctionRef DropFunction,
    MirPlace ResultTarget,
    long Bound,
    SourceSpan FirstSpan,
    SourceSpan SecondSpan) : SequencePipelinePlan(1);

internal sealed record TakeTakePlan(
    MirBasicBlock Block,
    int FirstInstructionIndex,
    int SecondInstructionIndex,
    MirPlace Source,
    MirFunctionRef TakeFunction,
    MirPlace ResultTarget,
    long Bound,
    SourceSpan FirstSpan,
    SourceSpan SecondSpan) : SequencePipelinePlan(1);

internal sealed record TakeHeadPlan(
    MirBasicBlock Block,
    int TakeInstructionIndex,
    int HeadInstructionIndex,
    MirPlace Source,
    MirFunctionRef HeadFunction,
    MirPlace HeadTarget,
    SourceSpan TakeSpan,
    SourceSpan HeadSpan) : SequencePipelinePlan(1);

internal sealed record FilterHeadPlan(
    MirBasicBlock Block,
    int FilterInstructionIndex,
    int HeadInstructionIndex,
    MirPlace Source,
    MirFunctionRef Predicate,
    MirFunctionRef FindFunction,
    MirPlace HeadTarget,
    SourceSpan FilterSpan,
    SourceSpan HeadSpan) : SequencePipelinePlan(1);

internal sealed record FilterTakeHeadPlan(
    MirBasicBlock Block,
    int FilterInstructionIndex,
    int TakeInstructionIndex,
    int HeadInstructionIndex,
    MirPlace Source,
    MirFunctionRef Predicate,
    MirFunctionRef FindFunction,
    MirPlace HeadTarget,
    long Bound,
    SourceSpan FilterSpan,
    SourceSpan TakeSpan,
    SourceSpan HeadSpan) : SequencePipelinePlan(2);

internal sealed record MapFilterFoldPlan(
    MirBasicBlock Block,
    int StartInstructionIndex,
    int EndInstructionIndex,
    MirPlace Source,
    MirFunctionRef Mapper,
    MirFunctionRef Predicate,
    MirFunctionRef Reducer,
    MirOperand Initial,
    MirPlace FoldTarget,
    TypeId SourceElementType,
    TypeId MappedElementType,
    TypeId PredicateParameterType,
    TypeId AccumulatorType,
    SourceSpan MapSpan,
    SourceSpan FilterSpan,
    SourceSpan FoldSpan) : SequencePipelinePlan(2);

internal sealed record MapFoldPlan(
    MirBasicBlock Block,
    int StartInstructionIndex,
    int EndInstructionIndex,
    MirPlace Source,
    MirFunctionRef Mapper,
    MirFunctionRef Reducer,
    MirOperand Initial,
    MirPlace FoldTarget,
    TypeId SourceElementType,
    TypeId MappedElementType,
    TypeId AccumulatorType,
    SourceSpan MapSpan,
    SourceSpan FoldSpan) : SequencePipelinePlan(1);

internal sealed record ZipWithFoldPlan(
    MirBasicBlock Block,
    int StartInstructionIndex,
    int EndInstructionIndex,
    MirPlace LeftSource,
    MirPlace RightSource,
    MirFunctionRef Combiner,
    MirFunctionRef Reducer,
    MirOperand Initial,
    MirPlace FoldTarget,
    TypeId LeftElementType,
    TypeId RightElementType,
    TypeId CombinedElementType,
    TypeId AccumulatorType,
    bool MovesLeftOutOfSource,
    bool MovesRightOutOfSource,
    bool MovesCombinedOutOfSource,
    bool MovesAccumulatorOutOfSource,
    SourceSpan ZipSpan,
    SourceSpan FoldSpan) : SequencePipelinePlan(1);

internal sealed record MapFilterCollectPlan(
    MirBasicBlock Block,
    int StartInstructionIndex,
    int EndInstructionIndex,
    MirPlace Source,
    MirPlace ResultTarget,
    MirFunctionRef Mapper,
    MirFunctionRef Predicate,
    TypeId SourceElementType,
    TypeId MappedElementType,
    TypeId PredicateParameterType,
    int MappedElementSize,
    long? StaticCapacityUpperBound,
    SourceSpan MapSpan,
    SourceSpan FilterSpan) : SequencePipelinePlan(1);

internal sealed record DirectFoldPlan(
    MirBasicBlock Block,
    int InstructionIndex,
    MirPlace Source,
    MirFunctionRef Reducer,
    MirOperand Initial,
    MirPlace FoldTarget,
    TypeId SourceElementType,
    TypeId AccumulatorType,
    SourceSpan FoldSpan) : SequencePipelinePlan(0);

internal enum DirectSequenceSinkKind
{
    Find,
    Any,
    All,
    Count,
    ForEach
}

internal sealed record DirectSequenceSinkPlan(
    MirBasicBlock Block,
    int FirstInstructionIndex,
    int InstructionIndex,
    MirPlace Source,
    MirFunctionRef SinkFunction,
    MirFunctionRef Callback,
    MirPlace ResultTarget,
    TypeId ElementType,
    TypeId CallbackParameterType,
    DirectSequenceSinkKind Kind,
    IReadOnlyList<SequenceStagePlan> Stages,
    SourceSpan SinkSpan) : SequencePipelinePlan(Stages.Count);

internal sealed record DirectPartitionPlan(
    MirBasicBlock Block,
    int InstructionIndex,
    MirPlace Source,
    MirFunctionRef Predicate,
    MirPlace ResultTarget,
    TypeId ElementType,
    TypeId PredicateParameterType,
    TypeId SequenceType,
    int ElementSize,
    SourceSpan SinkSpan) : SequencePipelinePlan(0);

/// <summary>
/// Proof-gated flat_map followed by count.  The lowering keeps the mapper's
/// returned sequence local to the outer iteration and scans it immediately,
/// avoiding the flattened intermediate allocation.
/// </summary>
internal sealed record FlatMapCountPlan(
    MirBasicBlock Block,
    int FlatMapInstructionIndex,
    int CountInstructionIndex,
    MirPlace Source,
    MirFunctionRef Mapper,
    MirFunctionRef Predicate,
    MirPlace CountTarget,
    TypeId OuterElementType,
    TypeId InnerSequenceType,
    TypeId InnerElementType,
    TypeId PredicateParameterType,
    SourceSpan FlatMapSpan,
    SourceSpan CountSpan) : SequencePipelinePlan(1);

internal sealed record FlatMapDirectSinkPlan(
    MirBasicBlock Block,
    int FlatMapInstructionIndex,
    int SinkInstructionIndex,
    MirPlace Source,
    MirFunctionRef Mapper,
    MirFunctionRef Predicate,
    MirPlace ResultTarget,
    TypeId OuterElementType,
    TypeId InnerSequenceType,
    TypeId InnerElementType,
    TypeId PredicateParameterType,
    DirectSequenceSinkKind Kind,
    SourceSpan FlatMapSpan,
    SourceSpan SinkSpan) : SequencePipelinePlan(1);

internal sealed record FlatMapFoldPlan(
    MirBasicBlock Block,
    int FlatMapInstructionIndex,
    int FoldInstructionIndex,
    MirPlace Source,
    MirFunctionRef Mapper,
    MirOperand Initial,
    MirFunctionRef Reducer,
    MirPlace ResultTarget,
    TypeId OuterElementType,
    TypeId InnerSequenceType,
    TypeId InnerElementType,
    TypeId AccumulatorType,
    SourceSpan FlatMapSpan,
    SourceSpan FoldSpan) : SequencePipelinePlan(1);

internal sealed record FlatMapCollectPlan(
    MirBasicBlock Block,
    int FlatMapInstructionIndex,
    int CollectInstructionIndex,
    MirPlace Source,
    MirFunctionRef Mapper,
    MirPlace ResultTarget,
    TypeId OuterElementType,
    TypeId InnerSequenceType,
    TypeId InnerElementType,
    int InnerElementSize,
    bool OuterMovesOutOfSource,
    SourceSpan FlatMapSpan) : SequencePipelinePlan(1);

internal sealed record DirectZipSequenceSinkPlan(
    MirBasicBlock Block,
    int FirstInstructionIndex,
    int ZipInstructionIndex,
    int SinkInstructionIndex,
    MirPlace LeftSource,
    MirPlace RightSource,
    MirPlace ZippedTarget,
    MirFunctionRef SinkFunction,
    MirFunctionRef Callback,
    MirPlace ResultTarget,
    TypeId LeftElementType,
    TypeId RightElementType,
    TypeId PairType,
    TypeId CallbackParameterType,
    DirectSequenceSinkKind Kind,
    SourceSpan SinkSpan) : SequencePipelinePlan(1);
