using Eidosc.Borrow;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Fuses canonical eager sequence stages only when callback reordering and
/// intermediate ownership transfer are proven safe. Supported spines lower
/// map/fold-left, map/filter/fold-left, map/filter/collect, flat-map/count,
/// and zip/direct-sink forms over proven element routes without changing eager
/// callback ordering when it is observable.
/// Read-count and escape gates come from <see cref="SequenceOptimizationFacts"/>
/// so future SequencePlan nodes share the same proof surface.
/// </summary>
public sealed partial class SequencePipelineFusionPass :
    IMirOptimizationPass,
    IFunctionOptimizationProofConsumer,
    IOwnershipAnalysisSnapshotConsumer,
    IMirOptimizationMetricsProvider
{
    private readonly Func<string, IDisposable>? _measureSubphase;
    private FunctionOptimizationProofIndex _functionProofs = FunctionOptimizationProofIndex.Empty;
    private readonly Dictionary<string, SequenceOptimizationFacts> _factsByFunction =
        new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, OwnershipAnalysisSnapshot> _ownershipSnapshots =
        new Dictionary<string, OwnershipAnalysisSnapshot>(StringComparer.Ordinal);
    private readonly List<SequencePlan> _discoveredSinkPlans = [];

    public SequencePipelineFusionPass(Func<string, IDisposable>? measureSubphase = null)
    {
        _measureSubphase = measureSubphase;
    }

    public string Name => "SequencePipelineFusion";

    public SequencePipelineFusionStats Stats { get; } = new();

    internal IReadOnlyList<SequencePlan> DiscoveredSinkPlans => _discoveredSinkPlans;

    public IReadOnlyDictionary<string, long> GetMetricsSnapshot() => Stats.ToMetricsSnapshot();

    FunctionOptimizationProofIndex IFunctionOptimizationProofConsumer.FunctionProofs
    {
        set => _functionProofs = value;
    }

    IReadOnlyDictionary<string, OwnershipAnalysisSnapshot> IOwnershipAnalysisSnapshotConsumer.OwnershipSnapshots
    {
        set => _ownershipSnapshots = value ??
            new Dictionary<string, OwnershipAnalysisSnapshot>(StringComparer.Ordinal);
    }

    public MirModule Run(MirModule module)
    {
        Stats.Reset();
        _factsByFunction.Clear();
        _discoveredSinkPlans.Clear();
        IReadOnlyDictionary<string, MirFunc> functionsByKey;
        using (MeasureSubphase("sequence.analyze"))
        {
            functionsByKey = module.Functions
                .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
            foreach (var function in module.Functions.Where(static function => function.BasicBlocks.Count > 0))
            {
                _factsByFunction[MirFunctionIdentity.GetStableKey(function)] =
                    SequenceOptimizationFacts.Analyze(function);
            }
        }

        var candidates = module.Functions
            .Where(static function => !function.IsExternal && function.BasicBlocks.Count > 0)
            .ToArray();
        Stats.FunctionsScanned = candidates.Length;

        var plans = new List<(MirFunc Function, SequencePipelinePlan Plan)>();
        using (MeasureSubphase("sequence.plan"))
        {
            foreach (var function in candidates)
            {
                if (TryFindDirectSequenceSinkPlans(module, function, functionsByKey, out var sinkPlans))
                {
                    foreach (var discoveredSinkPlan in sinkPlans)
                    {
                        var sinkPlan = SequencePlanMetadata.Attach(function, discoveredSinkPlan, _ownershipSnapshots);
                        if (sinkPlan.UnifiedPlan is { } unifiedSinkPlan)
                        {
                            _discoveredSinkPlans.Add(unifiedSinkPlan);
                            Stats.SinkPlansDiscovered++;
                        }
                        plans.Add((function, sinkPlan));
                    }
                    continue;
                }

                if (TryFindPlan(module, function, functionsByKey, out var plan))
                {
                    plan = SequencePlanMetadata.Attach(function, plan, _ownershipSnapshots);
                    plans.Add((function, plan));
                }
            }
        }

        if (plans.Count == 0)
        {
            return module;
        }

        using (MeasureSubphase("sequence.rewrite"))
        {
            foreach (var (function, plan) in plans)
            {
                ApplyPlan(module, function, plan);
                switch (plan)
                {
                    case DropDropPlan:
                        Stats.PipelinesFormed++;
                        Stats.DropDropPipelines++;
                        break;
                    case TakeTakePlan:
                        Stats.PipelinesFormed++;
                        Stats.TakeTakePipelines++;
                        break;
                    case TakeHeadPlan:
                        Stats.PipelinesFormed++;
                        Stats.TakeHeadPipelines++;
                        break;
                    case FilterHeadPlan:
                        Stats.PipelinesFormed++;
                        Stats.FilterHeadPipelines++;
                        break;
                    case FilterTakeHeadPlan:
                        Stats.PipelinesFormed++;
                        Stats.FilterHeadPipelines++;
                        break;
                    case ZipWithFoldPlan:
                        Stats.PipelinesFormed++;
                        Stats.ZipWithFoldPipelines++;
                        Stats.SourceLoopsEmitted++;
                        break;
                    case FlatMapCountPlan:
                        Stats.PipelinesFormed++;
                        Stats.FlatMapCountPipelines++;
                        Stats.SourceLoopsEmitted += 2;
                        break;
                    case FlatMapDirectSinkPlan:
                        Stats.PipelinesFormed++;
                        Stats.FlatMapDirectSinkPipelines++;
                        Stats.SourceLoopsEmitted += 2;
                        break;
                    case FlatMapFoldPlan:
                        Stats.PipelinesFormed++;
                        Stats.FlatMapFoldPipelines++;
                        Stats.SourceLoopsEmitted += 2;
                        break;
                    case FlatMapCollectPlan:
                        Stats.PipelinesFormed++;
                        Stats.FlatMapCollectPipelines++;
                        Stats.SourceLoopsEmitted += 2;
                        break;
                    case DirectZipSequenceSinkPlan:
                        Stats.PipelinesFormed++;
                        Stats.ZipSinkPipelines++;
                        Stats.SourceLoopsEmitted++;
                        break;
                    case DirectPartitionPlan:
                        Stats.PartitionSinksLowered++;
                        Stats.SourceLoopsEmitted++;
                        break;
                    case DirectFoldPlan:
                        Stats.DirectFoldsLowered++;
                        Stats.SourceLoopsEmitted++;
                        break;
                    case DirectSequenceSinkPlan:
                        Stats.SinkPlansLowered++;
                        Stats.SourceLoopsEmitted++;
                        break;
                    case MapFoldPlan:
                        Stats.PipelinesFormed++;
                        Stats.MapFoldPipelines++;
                        Stats.SourceLoopsEmitted++;
                        break;
                    case MapFilterFoldPlan:
                        Stats.PipelinesFormed++;
                        Stats.MapFilterFoldPipelines++;
                        Stats.SourceLoopsEmitted++;
                        break;
                    case MapFilterCollectPlan:
                        Stats.PipelinesFormed++;
                        Stats.MapFilterCollectPipelines++;
                        Stats.SourceLoopsEmitted++;
                        break;
                }
                Stats.IntermediatesElided += plan.IntermediatesElided;
            }
        }

        return module.WithFunctions(module.Functions.ToList());
    }

    private IDisposable MeasureSubphase(string name) =>
        _measureSubphase?.Invoke(name) ?? NoopDisposable.Instance;

    private bool TryFindPlan(
        MirModule module,
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out SequencePipelinePlan plan)
    {
        plan = null!;
        if (TryFindDropDropPlan(function, functionsByKey, out plan))
        {
            return true;
        }

        if (TryFindTakeTakePlan(function, functionsByKey, out plan))
        {
            return true;
        }

        if (TryFindFilterTakeHeadPlan(module, function, functionsByKey, out plan))
        {
            return true;
        }

        if (TryFindTakeHeadPlan(function, functionsByKey, out plan))
        {
            return true;
        }

        if (TryFindFilterHeadPlan(module, function, functionsByKey, out plan))
        {
            return true;
        }

        if (TryFindZipWithFoldPlan(module, function, functionsByKey, out plan))
        {
            return true;
        }

        if (TryFindFlatMapCollectPlan(module, function, functionsByKey, out var flatMapCollectPlan))
        {
            plan = flatMapCollectPlan;
            return true;
        }

        foreach (var block in function.BasicBlocks.ToArray())
        {
            var instructions = block.Instructions;
            for (var mapIndex = 0; mapIndex < instructions.Count; mapIndex++)
            {
                if (instructions[mapIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } mapTarget,
                        Function: MirFunctionRef mapFunction,
                        Arguments.Count: 2
                    } mapCall ||
                    mapCall.Arguments[0] is not MirPlace { Kind: PlaceKind.Local } source ||
                    GetEffectiveSequenceRole(mapFunction, functionsByKey) != CompilerSemanticRole.SequenceMap)
                {
                    continue;
                }

                Stats.RoleCalls++;
                var cursor = mapIndex + 1;
                var mapOutput = FollowSingleMove(instructions, ref cursor, mapTarget);
                if (cursor < instructions.Count &&
                    instructions[cursor] is MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } mapFoldTarget,
                        Function: MirFunctionRef mapFoldFunction,
                        Arguments.Count: 3
                    } mapFoldCall &&
                    GetEffectiveSequenceRole(mapFoldFunction, functionsByKey) ==
                        CompilerSemanticRole.SequenceFoldLeft &&
                    IsLocal(mapFoldCall.Arguments[0], mapOutput.Local))
                {
                    Stats.RoleCalls++;
                    if (!HasSingleUseNonEscaping(function, mapTarget.Local) ||
                        !HasSingleUseNonEscaping(function, mapOutput.Local))
                    {
                        Stats.FallbackMultiUse++;
                        continue;
                    }

                    if (mapCall.Arguments[1] is not MirFunctionRef mapFoldMapper ||
                        mapFoldCall.Arguments[2] is not MirFunctionRef mapFoldReducer ||
                        !TryResolveCallback(functionsByKey, mapFoldMapper, out var mapFoldMapperFunction) ||
                        !TryResolveCallback(functionsByKey, mapFoldReducer, out var mapFoldReducerFunction))
                    {
                        Stats.FallbackUnknownCallback++;
                        continue;
                    }

                    var mapFoldMapperParameters = mapFoldMapperFunction.Locals
                        .Where(static local => local.IsParameter)
                        .ToArray();
                    var mapFoldReducerParameters = mapFoldReducerFunction.Locals
                        .Where(static local => local.IsParameter)
                        .ToArray();
                    var mapFoldSourceElementType = mapFoldMapperParameters.Length == 1
                        ? mapFoldMapperParameters[0].TypeId
                        : TypeId.None;
                    var mapFoldMappedElementType = mapFoldMapperFunction.ReturnType;
                    var mapFoldAccumulatorType = mapFoldReducerFunction.ReturnType;
                    if (mapFoldMapperParameters.Length != 1 ||
                        mapFoldReducerParameters.Length != 2 ||
                        !mapFoldSourceElementType.IsValid ||
                        !mapFoldMappedElementType.IsValid ||
                        !mapFoldAccumulatorType.IsValid ||
                        mapFoldReducerParameters[0].TypeId != mapFoldAccumulatorType ||
                        mapFoldReducerParameters[1].TypeId != mapFoldMappedElementType)
                    {
                        Stats.FallbackUnknownCallback++;
                        continue;
                    }

                    if (!IsCopyType(module, mapFoldSourceElementType) ||
                        !IsCopyType(module, mapFoldMappedElementType) ||
                        !IsCopyType(module, mapFoldAccumulatorType))
                    {
                        Stats.FallbackOwnership++;
                        continue;
                    }

                    if (!AllowsCallbackReordering(module, mapFoldMapper, mapFoldMapperFunction) ||
                        !AllowsCallbackReordering(module, mapFoldReducer, mapFoldReducerFunction))
                    {
                        RecordCallbackProofFallback(mapFoldMapper, mapFoldReducer);
                        continue;
                    }

                    plan = new MapFoldPlan(
                        block,
                        mapIndex,
                        cursor,
                        source,
                        mapFoldMapper,
                        mapFoldReducer,
                        mapFoldCall.Arguments[1],
                        mapFoldTarget,
                        mapFoldSourceElementType,
                        mapFoldMappedElementType,
                        mapFoldAccumulatorType,
                        mapFunction.Span,
                        mapFoldFunction.Span);
                    return true;
                }

                if (cursor >= instructions.Count ||
                    instructions[cursor] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } filterTarget,
                        Function: MirFunctionRef filterFunction,
                        Arguments.Count: 2
                    } filterCall ||
                    GetEffectiveSequenceRole(filterFunction, functionsByKey) !=
                        CompilerSemanticRole.SequenceFilter ||
                    !IsLocal(filterCall.Arguments[0], mapOutput.Local))
                {
                    Stats.FallbackShapeAfterMap++;
                    continue;
                }

                Stats.RoleCalls++;
                var filterInstructionIndex = cursor;
                cursor++;
                var filterOutput = FollowSingleMove(instructions, ref cursor, filterTarget);
                if (!HasSingleUseNonEscaping(function, mapTarget.Local) ||
                    !HasSingleUseNonEscaping(function, mapOutput.Local))
                {
                    Stats.FallbackMultiUse++;
                    continue;
                }

                if (mapCall.Arguments[1] is not MirFunctionRef mapper ||
                    filterCall.Arguments[1] is not MirFunctionRef predicate)
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                if (!TryResolveCallback(functionsByKey, mapper, out var mapperFunction) ||
                    !TryResolveCallback(functionsByKey, predicate, out var predicateFunction))
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                var mapperParameters = mapperFunction.Locals.Where(static local => local.IsParameter).ToArray();
                var predicateParameters = predicateFunction.Locals.Where(static local => local.IsParameter).ToArray();
                if (mapperParameters.Length != 1 || predicateParameters.Length != 1 ||
                    !mapperFunction.ReturnType.IsValid)
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                var sourceElementType = mapperParameters[0].TypeId;
                var mappedElementType = mapperFunction.ReturnType;
                if (!IsCopyType(module, sourceElementType) ||
                    !IsCopyType(module, mappedElementType) ||
                    !IsSharedReferenceTo(module, predicateParameters[0].TypeId, mappedElementType))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                if (!AllowsCallbackReordering(module, mapper, mapperFunction) ||
                    !AllowsCallbackReordering(module, predicate, predicateFunction))
                {
                    RecordCallbackProofFallback(mapper, predicate);
                    continue;
                }

                if (cursor < instructions.Count &&
                    instructions[cursor] is MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } foldTarget,
                        Function: MirFunctionRef foldFunction,
                        Arguments.Count: 3
                    } foldCall &&
                    GetEffectiveSequenceRole(foldFunction, functionsByKey) ==
                        CompilerSemanticRole.SequenceFoldLeft &&
                    IsLocal(foldCall.Arguments[0], filterOutput.Local))
                {
                    Stats.RoleCalls++;
                    if (HasSingleUseNonEscaping(function, filterTarget.Local) &&
                        HasSingleUseNonEscaping(function, filterOutput.Local) &&
                        foldCall.Arguments[2] is MirFunctionRef reducer &&
                        TryResolveCallback(functionsByKey, reducer, out var reducerFunction))
                    {
                        var reducerParameters = reducerFunction.Locals
                            .Where(static local => local.IsParameter)
                            .ToArray();
                        var accumulatorType = reducerFunction.ReturnType;
                        if (reducerParameters.Length == 2 &&
                            accumulatorType.IsValid &&
                            IsCopyType(module, accumulatorType) &&
                            AllowsCallbackReordering(module, reducer, reducerFunction))
                        {
                            plan = new MapFilterFoldPlan(
                                block,
                                mapIndex,
                                cursor,
                                source,
                                mapper,
                                predicate,
                                reducer,
                                foldCall.Arguments[1],
                                foldTarget,
                                sourceElementType,
                                mappedElementType,
                                predicateParameters[0].TypeId,
                                accumulatorType,
                                mapFunction.Span,
                                filterFunction.Span,
                                foldFunction.Span);
                            return true;
                        }

                        if (reducerParameters.Length == 2 && accumulatorType.IsValid &&
                            !AllowsCallbackReordering(module, reducer, reducerFunction))
                        {
                            RecordCallbackProofFallback(reducer);
                        }
                        else if (!IsCopyType(module, accumulatorType))
                        {
                            Stats.FallbackOwnership++;
                        }
                        else
                        {
                            Stats.FallbackUnknownCallback++;
                        }
                    }
                    else if (!HasSingleUseNonEscaping(function, filterTarget.Local) ||
                             !HasSingleUseNonEscaping(function, filterOutput.Local))
                    {
                        Stats.FallbackMultiUse++;
                    }
                    else
                    {
                        Stats.FallbackUnknownCallback++;
                    }
                }

                plan = new MapFilterCollectPlan(
                    block,
                    mapIndex,
                    filterInstructionIndex,
                    source,
                    filterTarget,
                    mapper,
                    predicate,
                    sourceElementType,
                    mappedElementType,
                    predicateParameters[0].TypeId,
                    GetRuntimeElementSize(module, mappedElementType),
                    TryResolveStaticArrayCapacity(function, source, out var staticCapacity)
                        ? staticCapacity
                        : null,
                    mapFunction.Span,
                    filterFunction.Span);
                return true;
            }
        }

        return TryFindDirectFoldPlan(module, function, functionsByKey, out plan);
    }

    private bool TryFindFilterTakeHeadPlan(
        MirModule module,
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out SequencePipelinePlan plan)
    {
        plan = null!;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            var instructions = block.Instructions;
            for (var filterIndex = 0; filterIndex < instructions.Count; filterIndex++)
            {
                if (instructions[filterIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } filterTarget,
                        Function: MirFunctionRef filterFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } source, MirFunctionRef predicate]
                    } filterCall ||
                    GetEffectiveSequenceRole(filterFunction, functionsByKey) != CompilerSemanticRole.SequenceFilter)
                {
                    continue;
                }

                var cursor = filterIndex + 1;
                var filterOutput = FollowSingleMove(instructions, ref cursor, filterTarget);
                if (cursor >= instructions.Count ||
                    instructions[cursor] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } takeTarget,
                        Function: MirFunctionRef takeFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } takeInput, MirConstant bound]
                    } takeCall ||
                    takeInput.Local != filterOutput.Local ||
                    GetEffectiveSequenceRole(takeFunction, functionsByKey) != CompilerSemanticRole.SequenceTake ||
                    !TryGetPositiveIntConstant(bound, out var takeBound))
                {
                    continue;
                }

                var takeIndex = cursor++;
                var takeOutput = FollowSingleMove(instructions, ref cursor, takeTarget);
                if (cursor >= instructions.Count ||
                    instructions[cursor] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } headTarget,
                        Function: MirFunctionRef headFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } headInput]
                    } headCall ||
                    headInput.Local != takeOutput.Local ||
                    GetEffectiveSequenceRole(headFunction, functionsByKey) != CompilerSemanticRole.SequenceHead ||
                    !TryResolveCallback(functionsByKey, predicate, out var predicateFunction))
                {
                    continue;
                }

                var predicateParameters = predicateFunction.Locals
                    .Where(static local => local.IsParameter)
                    .ToArray();
                if (predicateParameters.Length != 1 ||
                    predicateFunction.ReturnType != new TypeId(BaseTypes.BoolId) ||
                    !IsSharedBorrowType(module, predicateParameters[0].TypeId) ||
                    !HasSingleUseNonEscaping(function, source.Local) ||
                    !HasSingleUseNonEscaping(function, filterTarget.Local) ||
                    !HasSingleUseNonEscaping(function, filterOutput.Local) ||
                    !HasSingleUseNonEscaping(function, takeTarget.Local) ||
                    !HasSingleUseNonEscaping(function, takeOutput.Local) ||
                    !AllowsCallbackReordering(module, predicate, predicateFunction))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                if (_ownershipSnapshots.TryGetValue(
                        MirFunctionIdentity.GetStableKey(function),
                        out var snapshot) &&
                    !snapshot.CanDestructivelyUpdate(source.Local, block.Id, filterIndex))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                if (!TryCreateSequenceFunctionReference(
                        headFunction,
                        CompilerSemanticRole.SequenceFind,
                        functionsByKey,
                        out var findFunction))
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                plan = new FilterTakeHeadPlan(
                    block,
                    filterIndex,
                    takeIndex,
                    cursor,
                    source,
                    predicate,
                    findFunction,
                    headTarget,
                    takeBound,
                    filterCall.Span,
                    takeCall.Span,
                    headCall.Span);
                return true;
            }
        }

        return false;
    }

    private bool TryFindFilterHeadPlan(
        MirModule module,
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out SequencePipelinePlan plan)
    {
        plan = null!;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            var instructions = block.Instructions;
            for (var filterIndex = 0; filterIndex < instructions.Count; filterIndex++)
            {
                if (instructions[filterIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } filterTarget,
                        Function: MirFunctionRef filterFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } source, MirFunctionRef predicate]
                    } filterCall ||
                    GetEffectiveSequenceRole(filterFunction, functionsByKey) != CompilerSemanticRole.SequenceFilter)
                {
                    continue;
                }

                var cursor = filterIndex + 1;
                var filterOutput = FollowSingleMove(instructions, ref cursor, filterTarget);
                if (cursor >= instructions.Count ||
                    instructions[cursor] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } headTarget,
                        Function: MirFunctionRef headFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } headInput]
                    } headCall ||
                    headInput.Local != filterOutput.Local ||
                    GetEffectiveSequenceRole(headFunction, functionsByKey) != CompilerSemanticRole.SequenceHead)
                {
                    continue;
                }

                if (!TryResolveCallback(functionsByKey, predicate, out var predicateFunction))
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                var predicateParameters = predicateFunction.Locals
                    .Where(static local => local.IsParameter)
                    .ToArray();
                if (predicateParameters.Length != 1 ||
                    predicateFunction.ReturnType != new TypeId(BaseTypes.BoolId) ||
                    !IsSharedBorrowType(module, predicateParameters[0].TypeId))
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                if (!HasSingleUseNonEscaping(function, source.Local) ||
                    !HasSingleUseNonEscaping(function, filterTarget.Local) ||
                    !HasSingleUseNonEscaping(function, filterOutput.Local))
                {
                    Stats.FallbackMultiUse++;
                    continue;
                }

                if (_ownershipSnapshots.TryGetValue(
                        MirFunctionIdentity.GetStableKey(function),
                        out var ownershipSnapshot) &&
                    !ownershipSnapshot.CanDestructivelyUpdate(source.Local, block.Id, filterIndex))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                if (!AllowsCallbackReordering(module, predicate, predicateFunction))
                {
                    RecordCallbackProofFallback(predicate);
                    continue;
                }

                if (!TryCreateSequenceFunctionReference(
                        headFunction,
                        CompilerSemanticRole.SequenceFind,
                        functionsByKey,
                        out var findFunction))
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                var elementType = TryGetSharedBorrowInnerType(module, predicateParameters[0].TypeId, out var resolvedElementType)
                    ? resolvedElementType
                    : predicateParameters[0].TypeId;
                var snapshotFingerprint = _ownershipSnapshots.TryGetValue(
                    MirFunctionIdentity.GetStableKey(function),
                    out ownershipSnapshot)
                    ? ownershipSnapshot.BodyFingerprint
                    : null;
                plan = new FilterHeadPlan(
                    block,
                    filterIndex,
                    cursor,
                    source,
                    predicate,
                    findFunction,
                    headTarget,
                    filterCall.Span,
                    headCall.Span)
                {
                    UnifiedPlan = new SequencePlan(
                        new SequenceSourcePlan(source, elementType),
                        [new SequenceFilterStagePlan(predicate)],
                        new SequenceFindSinkPlan(findFunction),
                        elementType,
                        headTarget.TypeId,
                        new SequenceEvaluationOrderPlan(["source", "predicate", "find"]),
                        new SequenceOwnershipRoutePlan(
                            SourceMustOwned: true,
                            SourceMustUnique: true,
                            NoAlias: true,
                            NoActiveBorrow: true,
                            NoEscape: true,
                            CleanupComplete: true,
                            SnapshotFingerprint: snapshotFingerprint),
                        new SequenceStoragePlan(
                            MaterializeIntermediate: false,
                            PreferSourceReuse: false,
                            UseInternalView: false,
                            AllowStackPromotion: false),
                        new SequenceProofSummary(
                            CallbackReorder: true,
                            SingleUse: true,
                            NonEscaping: true,
                            EffectsSafe: true,
                            OwnershipSafe: true,
                            RepresentationSafe: true,
                            FallbackReason: null),
                        new SequenceOriginSpansPlan([filterCall.Span, headCall.Span]))
                };
                return true;
            }
        }

        return false;
    }

    private bool TryFindZipWithFoldPlan(
        MirModule module,
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out SequencePipelinePlan plan)
    {
        plan = null!;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            var instructions = block.Instructions;
            for (var zipIndex = 0; zipIndex < instructions.Count; zipIndex++)
            {
                if (instructions[zipIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } zipTarget,
                        Function: MirFunctionRef zipFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } left, MirPlace { Kind: PlaceKind.Local } right, MirFunctionRef combiner]
                    } zipCall ||
                    GetEffectiveSequenceRole(zipFunction, functionsByKey) != CompilerSemanticRole.SequenceZipWith)
                {
                    continue;
                }

                var cursor = zipIndex + 1;
                var zipOutput = FollowSingleMove(instructions, ref cursor, zipTarget);
                if (cursor >= instructions.Count ||
                    instructions[cursor] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } foldTarget,
                        Function: MirFunctionRef foldFunction,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } foldInput, var initial, MirFunctionRef reducer]
                    } foldCall ||
                    foldInput.Local != zipOutput.Local ||
                    GetEffectiveSequenceRole(foldFunction, functionsByKey) != CompilerSemanticRole.SequenceFoldLeft ||
                    !TryResolveCallback(functionsByKey, combiner, out var combinerFunction) ||
                    !TryResolveCallback(functionsByKey, reducer, out var reducerFunction))
                {
                    continue;
                }

                var combinerParameters = combinerFunction.Locals.Where(static local => local.IsParameter).ToArray();
                var reducerParameters = reducerFunction.Locals.Where(static local => local.IsParameter).ToArray();
                if (combinerParameters.Length != 2 || reducerParameters.Length != 2 ||
                    combinerFunction.ReturnType != reducerParameters[1].TypeId ||
                    reducerFunction.ReturnType != reducerParameters[0].TypeId ||
                    !combinerParameters[0].TypeId.IsValid ||
                    !combinerParameters[1].TypeId.IsValid ||
                    !combinerFunction.ReturnType.IsValid ||
                    !reducerFunction.ReturnType.IsValid ||
                    !HasSingleUseNonEscaping(function, left.Local) ||
                    !HasSingleUseNonEscaping(function, right.Local) ||
                    !HasSingleUseNonEscaping(function, zipTarget.Local) ||
                    !HasSingleUseNonEscaping(function, zipOutput.Local) ||
                    !AllowsCallbackReordering(module, combiner, combinerFunction) ||
                    !AllowsCallbackReordering(module, reducer, reducerFunction))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                if (_ownershipSnapshots.TryGetValue(
                        MirFunctionIdentity.GetStableKey(function),
                        out var snapshot) &&
                    (!snapshot.CanDestructivelyUpdate(left.Local, block.Id, zipIndex) ||
                     !snapshot.CanDestructivelyUpdate(right.Local, block.Id, zipIndex) ||
                     snapshot.EscapeFacts.GetValueOrDefault(left.Local) != OwnershipEscapeKind.None ||
                     snapshot.EscapeFacts.GetValueOrDefault(right.Local) != OwnershipEscapeKind.None))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                if ((!IsCopyType(module, combinerParameters[0].TypeId) ||
                     !IsCopyType(module, combinerParameters[1].TypeId) ||
                     !IsCopyType(module, combinerFunction.ReturnType) ||
                     !IsCopyType(module, reducerFunction.ReturnType)) &&
                    !_ownershipSnapshots.ContainsKey(MirFunctionIdentity.GetStableKey(function)))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                plan = new ZipWithFoldPlan(
                    block,
                    zipIndex,
                    cursor,
                    left,
                    right,
                    combiner,
                    reducer,
                    initial,
                    foldTarget,
                    combinerParameters[0].TypeId,
                    combinerParameters[1].TypeId,
                    combinerFunction.ReturnType,
                    reducerFunction.ReturnType,
                    !IsCopyType(module, combinerParameters[0].TypeId),
                    !IsCopyType(module, combinerParameters[1].TypeId),
                    !IsCopyType(module, combinerFunction.ReturnType),
                    !IsCopyType(module, reducerFunction.ReturnType),
                    zipCall.Span,
                    foldCall.Span);
                return true;
            }
        }

        return false;
    }

    private bool TryFindDirectFoldPlan(
        MirModule module,
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out SequencePipelinePlan plan)
    {
        plan = null!;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                if (block.Instructions[instructionIndex] is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } foldTarget,
                        Function: MirFunctionRef foldFunction,
                        Arguments.Count: 3
                    } foldCall ||
                    foldCall.Arguments[0] is not MirPlace { Kind: PlaceKind.Local } source ||
                    GetEffectiveSequenceRole(foldFunction, functionsByKey) !=
                        CompilerSemanticRole.SequenceFoldLeft)
                {
                    continue;
                }

                Stats.RoleCalls++;
                if (foldCall.Arguments[2] is not MirFunctionRef reducer ||
                    !TryResolveCallback(functionsByKey, reducer, out var reducerFunction))
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                var reducerParameters = reducerFunction.Locals
                    .Where(static local => local.IsParameter)
                    .ToArray();
                var accumulatorType = reducerFunction.ReturnType;
                if (reducerParameters.Length != 2 ||
                    !accumulatorType.IsValid ||
                    reducerParameters[0].TypeId != accumulatorType)
                {
                    Stats.FallbackUnknownCallback++;
                    continue;
                }

                var sourceElementType = reducerParameters[1].TypeId;
                if (!sourceElementType.IsValid ||
                    !IsCopyType(module, sourceElementType) ||
                    !IsCopyType(module, accumulatorType))
                {
                    Stats.FallbackOwnership++;
                    continue;
                }

                plan = new DirectFoldPlan(
                    block,
                    instructionIndex,
                    source,
                    reducer,
                    foldCall.Arguments[1],
                    foldTarget,
                    sourceElementType,
                    accumulatorType,
                    foldFunction.Span);
                return true;
            }
        }

        return false;
    }

    private bool AllowsCallbackReordering(MirModule module, MirFunctionRef callback, MirFunc function)
    {
        if (_functionProofs.Allows(callback, FunctionOptimizationCapability.ReorderSequenceCallback))
        {
            return true;
        }

        // The inline fallback still reorders callback execution relative to
        // the original eager pipeline. Inline proof alone is intentionally
        // weaker, so require the same observable-safety facts explicitly;
        // otherwise a trusted callback that may panic/diverge could be
        // rewritten merely because its body is locally simple.
        if (!_functionProofs.TryGetSummary(callback, out var summary) ||
            !summary.IsTrusted ||
            !summary.Effects.IsPure ||
            (summary.Memory != FunctionMemoryBehavior.None &&
             !(summary.Memory == FunctionMemoryBehavior.Read &&
               function.Locals.FirstOrDefault(static local => local.IsParameter)?.TypeId is { } parameterType &&
               IsSharedBorrowType(module, parameterType))) ||
            summary.MayPanic ||
            summary.MayDiverge ||
            summary.MaySuspend ||
            summary.MayBlock ||
            summary.MayAllocate ||
            summary.MaySynchronize ||
            summary.Determinism != FunctionDeterminism.Deterministic)
        {
            return false;
        }

        return _functionProofs.Allows(callback, FunctionOptimizationCapability.InlineSequenceCallback) &&
               !_functionProofs.IsRecursive(function) &&
               IsLocallyReorderSafe(function);
    }

    private bool AllowsFlatMapInterleaving(
        MirFunctionRef callback,
        MirFunc function,
        bool allowAllocation,
        bool allowReturnedAggregate)
    {
        if (!_functionProofs.TryGetSummary(callback, out var summary) ||
            !summary.Effects.IsPure)
        {
            return false;
        }

        return !_functionProofs.IsRecursive(function) &&
               IsLocallyReorderSafe(function, allowReturnedAggregate, allowAllocation, allowAllocation);
    }

    private void RecordCallbackProofFallback(params MirFunctionRef[] callbacks)
    {
        var summaries = new List<FunctionOptimizationSummary>(callbacks.Length);
        foreach (var callback in callbacks)
        {
            if (!_functionProofs.TryGetSummary(callback, out var summary))
            {
                Stats.FallbackUnknownCallback++;
                return;
            }

            summaries.Add(summary);
        }

        if (summaries.Any(static summary => !summary.IsTrusted))
        {
            Stats.FallbackUnknownCallback++;
            return;
        }

        if (summaries.Any(static summary => !summary.Effects.IsPure))
        {
            Stats.FallbackEffect++;
            return;
        }

        if (summaries.Any(static summary => summary.MayPanic || summary.MayDiverge))
        {
            Stats.FallbackPanicOrDivergence++;
            return;
        }

        Stats.FallbackEffect++;
    }

    private bool IsLocallyReorderSafe(
        MirFunc function,
        bool allowReturnedAggregate = false,
        bool allowAllocation = true,
        bool allowRuntimeCalls = false)
    {
        if (function.BasicBlocks.Count != 1 || function.BasicBlocks[0].Terminator is not MirReturn)
        {
            return false;
        }

        var aggregateAliases = new HashSet<LocalId>();

        foreach (var instruction in function.BasicBlocks[0].Instructions)
        {
            if (instruction is MirAlloc { Target: { Kind: PlaceKind.Local } allocationTarget } && allowAllocation)
            {
                aggregateAliases.Add(allocationTarget.Local);
                continue;
            }

            if (allowRuntimeCalls &&
                instruction is MirCall
                {
                    Target: MirPlace { Kind: PlaceKind.Local, Local: var runtimeArrayTarget },
                    Function: MirFunctionRef runtimeArrayFunction
                } &&
                MirRuntimeFunctions.HasIdentity(runtimeArrayFunction, WellKnownStrings.InternalNames.ArrayNew))
            {
                aggregateAliases.Add(runtimeArrayTarget);
                continue;
            }

            if (TryTrackCompilerLocalAggregateAlias(instruction, aggregateAliases))
            {
                continue;
            }

            switch (instruction)
            {
                case MirAssign or MirCopy or MirMove or MirUnaryOp or MirSelect:
                    break;
                case MirBinOp { Operator: BinaryOp.Div or BinaryOp.Mod or BinaryOp.Concat }:
                    return false;
                case MirBinOp:
                    break;
                case MirLoad load when IsSafeLocalRead(load.Source, function, aggregateAliases):
                    break;
                case MirStore store when IsSafeLocalWrite(store.Target, function, aggregateAliases):
                    break;
                case MirDrop drop when IsCompilerLocalAggregate(drop.Value, aggregateAliases):
                    break;
                case MirCall { Function: MirFunctionRef callee } when
                    _functionProofs.Allows(callee, FunctionOptimizationCapability.ReorderSequenceCallback):
                    break;
                case MirCall { Function: MirFunctionRef callee } when
                    allowRuntimeCalls && IsPureRuntimeSequenceCall(callee):
                    break;
                default:
                    return false;
            }
        }

        if (!allowReturnedAggregate &&
            function.BasicBlocks[0].Terminator is MirReturn { Value: MirPlace returned } &&
            TryGetRootLocal(returned, out var returnedRoot) &&
            aggregateAliases.Contains(returnedRoot))
        {
            return false;
        }

        return true;
    }

    private static bool IsPureRuntimeSequenceCall(MirFunctionRef function) =>
        MirRuntimeFunctions.HasIdentity(function, WellKnownStrings.InternalNames.ArrayNew) ||
        MirRuntimeFunctions.HasIdentity(function, WellKnownStrings.InternalNames.ArrayPush);

    private static bool TryTrackCompilerLocalAggregateAlias(
        MirInstruction instruction,
        ISet<LocalId> aggregateAliases)
    {
        MirPlace? target;
        MirOperand? source;
        switch (instruction)
        {
            case MirAssign assign:
                target = assign.Target;
                source = assign.Source;
                break;
            case MirCopy copy:
                target = copy.Target;
                source = copy.Source;
                break;
            case MirMove move:
                target = move.Target;
                source = move.Source;
                break;
            default:
                return false;
        }

        if (target.Kind != PlaceKind.Local)
        {
            return false;
        }

        if (source is MirPlace { Kind: PlaceKind.Local } sourceLocal &&
            aggregateAliases.Contains(sourceLocal.Local))
        {
            aggregateAliases.Add(target.Local);
            return true;
        }

        aggregateAliases.Remove(target.Local);
        return false;
    }

    private static bool IsSafeLocalRead(
        MirOperand operand,
        MirFunc function,
        IReadOnlySet<LocalId> allocatedLocals)
    {
        if (operand is not MirPlace place || !TryGetRootLocal(place, out var root))
        {
            return false;
        }

        if (allocatedLocals.Contains(root))
        {
            return true;
        }

        var local = function.Locals.FirstOrDefault(candidate => candidate.Id == root);
        return place.Kind == PlaceKind.Local || local?.IsParameter == true;
    }

    private static bool IsSafeLocalWrite(
        MirPlace target,
        MirFunc function,
        IReadOnlySet<LocalId> allocatedLocals)
    {
        if (!TryGetRootLocal(target, out var root))
        {
            return false;
        }

        if (allocatedLocals.Contains(root))
        {
            return true;
        }

        var local = function.Locals.FirstOrDefault(candidate => candidate.Id == root);
        return target.Kind == PlaceKind.Local && local?.IsParameter == false;
    }

    private static bool IsCompilerLocalAggregate(MirOperand operand, IReadOnlySet<LocalId> allocatedLocals) =>
        operand is MirPlace place &&
        TryGetRootLocal(place, out var root) &&
        allocatedLocals.Contains(root);

    private static bool TryGetRootLocal(MirPlace place, out LocalId root)
    {
        var current = place;
        while (current.Kind != PlaceKind.Local)
        {
            if (current.Base is not MirPlace parent)
            {
                root = default;
                return false;
            }

            current = parent;
        }

        root = current.Local;
        return root.IsValid;
    }

    private static bool TryResolveCallback(
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        MirFunctionRef callback,
        out MirFunc function) =>
        functionsByKey.TryGetValue(MirFunctionIdentity.GetStableKey(callback), out function!);

    private static CompilerSemanticRole GetEffectiveSequenceRole(
        MirFunctionRef functionRef,
        IReadOnlyDictionary<string, MirFunc> functionsByKey)
    {
        var directRole = PreserveConcreteSequenceRole(functionRef.CompilerSemanticRole);
        if (directRole != CompilerSemanticRole.None)
        {
            return directRole;
        }

        if (TryResolveCallback(functionsByKey, functionRef, out var target))
        {
            if (TryProveForwardingSequenceRole(target, out var forwardedRole))
            {
                return forwardedRole;
            }
        }

        // Specialized function references can carry a new identity key while
        // retaining the source-visible name/module. Recover the compiler role
        // from that exact name match before falling back to no optimization.
        return TryInferSequenceRoleFromReference(functionRef, out var inferredRole)
            ? inferredRole
            : CompilerSemanticRole.None;
    }

    private static bool TryInferSequenceRoleFromReference(
        MirFunctionRef functionRef,
        out CompilerSemanticRole role)
    {
        role = CompilerSemanticRole.None;
        var module = functionRef.FunctionId.Module ?? string.Empty;
        var name = functionRef.FunctionId.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = functionRef.Name;
        }

        if (!module.Contains("Seq", StringComparison.OrdinalIgnoreCase) &&
            !functionRef.Name.Contains("__Seq__", StringComparison.Ordinal))
        {
            return false;
        }

        // Specialization suffixes are appended after the semantic name
        // (`...Seq__any__spec_HASH`). Strip them before selecting the final
        // namespace segment; otherwise the last `__` points at `spec_HASH`
        // and the canonical role is lost.
        var specializationIndex = name.IndexOf("__spec_", StringComparison.Ordinal);
        if (specializationIndex >= 0)
        {
            name = name[..specializationIndex];
        }

        var separatorIndex = name.LastIndexOf("__", StringComparison.Ordinal);
        var lastSegment = separatorIndex >= 0 ? name[(separatorIndex + 2)..] : name;

        role = lastSegment switch
        {
            "take" => CompilerSemanticRole.SequenceTake,
            "drop" => CompilerSemanticRole.SequenceDrop,
            "reverse" => CompilerSemanticRole.SequenceReverse,
            "find" => CompilerSemanticRole.SequenceFind,
            "any" => CompilerSemanticRole.SequenceAny,
            "all" => CompilerSemanticRole.SequenceAll,
            "count" => CompilerSemanticRole.SequenceCount,
            "for_each" => CompilerSemanticRole.SequenceForEach,
            _ => CompilerSemanticRole.None
        };
        return role != CompilerSemanticRole.None;
    }

    private static bool TryProveForwardingSequenceRole(
        MirFunc function,
        out CompilerSemanticRole role)
    {
        role = CompilerSemanticRole.None;
        if (function.BasicBlocks is not [var block] ||
            block.Terminator is not MirReturn { Value: MirPlace { Kind: PlaceKind.Local } returned })
        {
            return false;
        }

        var parameters = function.Locals.Where(static local => local.IsParameter).ToArray();
        var parameterAliases = new Dictionary<LocalId, int>();
        for (var index = 0; index < parameters.Length; index++)
        {
            parameterAliases[parameters[index].Id] = index;
        }

        var storageRoots = new Dictionary<LocalId, LocalId>();
        var storageSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        LocalId resultLocal = LocalId.None;

        foreach (var instruction in block.Instructions)
        {
            switch (instruction)
            {
                case MirAlloc { Target: { Kind: PlaceKind.Local } target }:
                    parameterAliases.Remove(target.Local);
                    storageRoots[target.Local] = target.Local;
                    break;
                case MirAssign assign:
                    if (!TrackForwardingAlias(assign.Target, assign.Source, parameterAliases, storageRoots))
                    {
                        return false;
                    }
                    break;
                case MirCopy copy:
                    if (!TrackForwardingAlias(copy.Target, copy.Source, parameterAliases, storageRoots))
                    {
                        return false;
                    }
                    break;
                case MirMove move:
                    if (!TrackForwardingAlias(move.Target, move.Source, parameterAliases, storageRoots))
                    {
                        return false;
                    }
                    break;
                case MirStore store:
                    if (store.Value is not MirPlace { Kind: PlaceKind.Local } storedLocal ||
                        !parameterAliases.TryGetValue(storedLocal.Local, out var storedParameterIndex) ||
                        !TryGetForwardingPlaceKey(store.Target, storageRoots, out var storeKey))
                    {
                        return false;
                    }
                    storageSlots[storeKey] = storedParameterIndex;
                    break;
                case MirLoad load:
                    if (!TrackForwardingLoad(
                            load.Target,
                            load.Source,
                            parameterAliases,
                            storageRoots,
                            storageSlots))
                    {
                        return false;
                    }
                    break;
                case MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local } callTarget,
                        Function: MirFunctionRef callee
                    } call:
                    var candidateRole = PreserveConcreteSequenceRole(callee.CompilerSemanticRole);
                    if (candidateRole == CompilerSemanticRole.None ||
                        role != CompilerSemanticRole.None ||
                        call.Arguments.Count != parameters.Length)
                    {
                        return false;
                    }

                    for (var argumentIndex = 0; argumentIndex < call.Arguments.Count; argumentIndex++)
                    {
                        if (call.Arguments[argumentIndex] is not MirPlace
                            {
                                Kind: PlaceKind.Local
                            } argument ||
                            !parameterAliases.TryGetValue(argument.Local, out var parameterIndex) ||
                            parameterIndex != argumentIndex)
                        {
                            return false;
                        }
                    }

                    role = candidateRole;
                    resultLocal = callTarget.Local;
                    break;
                default:
                    return false;
            }
        }

        return role != CompilerSemanticRole.None && resultLocal == returned.Local;
    }

    private static bool TrackForwardingAlias(
        MirPlace target,
        MirOperand source,
        IDictionary<LocalId, int> parameterAliases,
        IDictionary<LocalId, LocalId> storageRoots)
    {
        if (target.Kind != PlaceKind.Local || source is not MirPlace { Kind: PlaceKind.Local } sourceLocal)
        {
            return false;
        }

        if (parameterAliases.TryGetValue(sourceLocal.Local, out var parameterIndex))
        {
            parameterAliases[target.Local] = parameterIndex;
        }
        else
        {
            parameterAliases.Remove(target.Local);
        }

        if (storageRoots.TryGetValue(sourceLocal.Local, out var storageRoot))
        {
            storageRoots[target.Local] = storageRoot;
        }
        else
        {
            storageRoots.Remove(target.Local);
        }

        return true;
    }

    private static bool TrackForwardingLoad(
        MirPlace target,
        MirOperand source,
        IDictionary<LocalId, int> parameterAliases,
        IDictionary<LocalId, LocalId> storageRoots,
        IReadOnlyDictionary<string, int> storageSlots)
    {
        if (target.Kind != PlaceKind.Local || source is not MirPlace sourcePlace)
        {
            return false;
        }

        if (sourcePlace.Kind == PlaceKind.Local &&
            storageRoots.TryGetValue(sourcePlace.Local, out var sourceRoot))
        {
            parameterAliases.Remove(target.Local);
            storageRoots[target.Local] = sourceRoot;
            return true;
        }

        if (TryGetForwardingPlaceKey(sourcePlace, storageRoots, out var sourceKey) &&
            storageSlots.TryGetValue(sourceKey, out var parameterIndex))
        {
            parameterAliases[target.Local] = parameterIndex;
            storageRoots.Remove(target.Local);
            return true;
        }

        return false;
    }

    private static bool TryGetForwardingPlaceKey(
        MirPlace place,
        IDictionary<LocalId, LocalId> storageRoots,
        out string key)
    {
        if (!TryGetRootLocal(place, out var root) ||
            !storageRoots.TryGetValue(root, out var resolvedRoot))
        {
            key = string.Empty;
            return false;
        }

        var segments = new Stack<string>();
        var current = place;
        while (current.Kind != PlaceKind.Local)
        {
            switch (current.Kind)
            {
                case PlaceKind.Field:
                    segments.Push($"field:{current.FieldName}");
                    break;
                case PlaceKind.Index when current.Index is MirConstant constant:
                    segments.Push($"index:{constant.Value}");
                    break;
                default:
                    key = string.Empty;
                    return false;
            }

            if (current.Base is not MirPlace parent)
            {
                key = string.Empty;
                return false;
            }
            current = parent;
        }

        key = $"{resolvedRoot.Value}:{string.Join('/', segments)}";
        return true;
    }

    private static CompilerSemanticRole PreserveConcreteSequenceRole(CompilerSemanticRole role) => role switch
    {
        CompilerSemanticRole.SequenceMap or
        CompilerSemanticRole.SequenceFilter or
        CompilerSemanticRole.SequenceHead or
        CompilerSemanticRole.SequenceFlatMap or
        CompilerSemanticRole.SequenceFoldLeft or
        CompilerSemanticRole.SequenceFoldRight or
        CompilerSemanticRole.SequenceFind or
        CompilerSemanticRole.SequenceAny or
        CompilerSemanticRole.SequenceAll or
        CompilerSemanticRole.SequenceCount or
        CompilerSemanticRole.SequenceDrop or
        CompilerSemanticRole.SequenceTake or
        CompilerSemanticRole.SequenceZip or
        CompilerSemanticRole.SequenceZipWith or
        CompilerSemanticRole.SequencePartition or
        CompilerSemanticRole.SequenceReverse or
        CompilerSemanticRole.SequenceForEach => role,
        _ => CompilerSemanticRole.None
    };

    private static bool IsCopyType(MirModule module, TypeId typeId) =>
        CopyTypeSemantics.IsCopyType(
            typeId,
            null,
            module.TypeDescriptors,
            module.DynamicTypeKeys,
            module.ConstructorLayouts);

    private static int GetRuntimeElementSize(MirModule module, TypeId typeId)
    {
        if (module.TypeDescriptors.TryGetValue(typeId.Value, out var descriptor) &&
            descriptor is TypeDescriptor.Tuple tuple)
        {
            return tuple.FieldTypes.Length * IntPtr.Size;
        }

        return typeId.Value switch
        {
            BaseTypes.BoolId => 1,
            BaseTypes.CharId => 4,
            BaseTypes.UnitId or BaseTypes.NeverId => 0,
            BaseTypes.IntId => sizeof(long),
            BaseTypes.FloatId => sizeof(double),
            _ => IntPtr.Size
        };
    }

    private static bool IsSharedReferenceTo(MirModule module, TypeId referenceType, TypeId innerType)
    {
        if (module.TypeDescriptors.TryGetValue(referenceType.Value, out var descriptor))
        {
            return descriptor is TypeDescriptor.Ref reference && reference.Inner == innerType;
        }

        return module.DynamicTypeKeys.TryGetValue(referenceType.Value, out var typeKey) &&
               TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor) &&
               descriptor is TypeDescriptor.Ref dynamicReference &&
               dynamicReference.Inner == innerType;
    }

    private static bool IsSharedBorrowType(MirModule module, TypeId typeId)
    {
        if (module.TypeDescriptors.TryGetValue(typeId.Value, out var descriptor))
        {
            return descriptor is TypeDescriptor.Ref { Inner.IsValid: true };
        }

        return module.DynamicTypeKeys.TryGetValue(typeId.Value, out var typeKey) &&
               TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor) &&
               descriptor is TypeDescriptor.Ref { Inner.IsValid: true };
    }

    private static MirPlace FollowSingleMove(
        IReadOnlyList<MirInstruction> instructions,
        ref int cursor,
        MirPlace source)
    {
        if (cursor < instructions.Count &&
            instructions[cursor] is MirMove
            {
                Target: MirPlace { Kind: PlaceKind.Local } target,
                Source: MirPlace { Kind: PlaceKind.Local } moveSource
            } &&
            moveSource.Local == source.Local)
        {
            cursor++;
            return target;
        }

        return source;
    }

    private bool HasSingleUseNonEscaping(MirFunc function, LocalId local)
    {
        var key = MirFunctionIdentity.GetStableKey(function);
        if (_ownershipSnapshots.TryGetValue(key, out var snapshot))
            return snapshot.SequenceFacts.IsSingleUseNonEscaping(local);

        return _factsByFunction.TryGetValue(key, out var facts)
            ? facts.IsSingleUseNonEscaping(local)
            : SequenceOptimizationFacts.Analyze(function).IsSingleUseNonEscaping(local);
    }

    private static bool IsLocal(MirOperand operand, LocalId local) =>
        operand is MirPlace { Kind: PlaceKind.Local, Local: var candidate } && candidate == local;

    private static bool TryGetPositiveIntConstant(MirConstant constant) =>
        TryGetPositiveIntConstant(constant, out _);

    private static bool TryGetPositiveIntConstant(MirConstant constant, out long value)
    {
        if (constant.Value is MirConstantValue.IntValue { Value: > 0 } intValue)
        {
            value = intValue.Value;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryResolveStaticArrayCapacity(
        MirFunc function,
        MirPlace source,
        out long capacity)
    {
        capacity = 0;
        var current = source.Local;
        var visited = new HashSet<LocalId>();
        while (visited.Add(current))
        {
            var definitions = function.BasicBlocks
                .SelectMany(static block => block.Instructions)
                .Where(instruction => instruction switch
                {
                    MirCall { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == current,
                    MirAssign { Target: { Kind: PlaceKind.Local, Local: var target } } => target == current,
                    MirMove { Target: { Kind: PlaceKind.Local, Local: var target } } => target == current,
                    MirLoad { Target: { Kind: PlaceKind.Local, Local: var target } } => target == current,
                    _ => false
                })
                .ToArray();
            if (definitions.Length != 1)
            {
                return false;
            }

            if (definitions[0] is MirCall
                {
                    Function: MirFunctionRef functionRef,
                    Arguments: [MirConstant { Value: MirConstantValue.IntValue(var value) }, ..]
                } && MirRuntimeFunctions.HasIdentity(
                    functionRef,
                    WellKnownStrings.InternalNames.ArrayNew) && value >= 0)
            {
                capacity = value;
                return true;
            }

            var next = definitions[0] switch
            {
                MirAssign { Source: MirPlace { Kind: PlaceKind.Local, Local: var assignSource } } => assignSource,
                MirMove { Source: { Kind: PlaceKind.Local, Local: var moveSource } } => moveSource,
                MirLoad
                {
                    Source: MirPlace { Kind: PlaceKind.Local, Local: var loadSource },
                    CreatesBorrowAlias: false
                } => loadSource,
                _ => LocalId.None
            };
            if (!next.IsValid)
            {
                return false;
            }

            current = next;
        }

        return false;
    }

}
