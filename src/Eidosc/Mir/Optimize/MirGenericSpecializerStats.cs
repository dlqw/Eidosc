namespace Eidosc.Mir.Optimize;

public sealed class MirGenericSpecializerStats
{
    public long InitialRewriteQueueEntries { get; set; }
    public long RewriteQueueDequeues { get; set; }
    public long RewriteQueueMaxDepth { get; set; }
    public long ClonedWorkingFunctions { get; set; }
    public long RewriteVisitedFunctions { get; set; }
    public long RewriteSingleBlockFunctions { get; set; }
    public long RewriteMultiBlockFunctions { get; set; }
    public long RewriteIterations { get; set; }
    public long RewriteBlocksScanned { get; set; }
    public long RewriteInstructionsScanned { get; set; }
    public long FunctionRewriteSummariesBuilt { get; set; }
    public long FunctionRewriteSummaryCandidateBlocks { get; set; }
    public long FunctionRewriteSummaryCandidateInstructions { get; set; }
    public long DirtyRewriteQueueEntries { get; set; }
    public long DirtyRewriteQueueSkippedSpecializations { get; set; }
    public long DirtyRewriteQueueNoOpDequeues { get; set; }
    public long DirtyRewriteFullScanFallbacks { get; set; }
    public long DirtyRewriteCandidateBlockFunctions { get; set; }
    public long DirtyRewriteCandidateInstructionFunctions { get; set; }
    public long DirtyRewriteCandidateInstructionsVisited { get; set; }
    public long LocalTypeMapBuilds { get; set; }
    public long LocalTypeConcretizeCalls { get; set; }
    public long LocalRefreshes { get; set; }
    public long OperandRefreshes { get; set; }
    public long ReturnTypePropagations { get; set; }
    public long SpecializationCacheHits { get; set; }
    public long SpecializationsCreated { get; set; }
    public long SpecializationRejections { get; set; }
    public long EnqueuedSpecializations { get; set; }
    public long TemplateCallRewrites { get; set; }
    public long StateTransferClones { get; set; }
    public long StateMergeClones { get; set; }
    public long StateStorageClones { get; set; }
    public long StateCloneEntries { get; set; }
    public long StateTransferPoolHits { get; set; }
    public long CombineBoundArgumentLists { get; set; }
    public long CloneOperandLists { get; set; }
    public long CloneOperandListItems { get; set; }
    public long FunctionRefRewrites { get; set; }
    public long TypeBindingCacheHits { get; set; }
    public long TypeBindingCacheMisses { get; set; }
    public long MeaningfulSignatureCacheHits { get; set; }
    public long MeaningfulSignatureCacheMisses { get; set; }

    public void Clear()
    {
        InitialRewriteQueueEntries = 0;
        RewriteQueueDequeues = 0;
        RewriteQueueMaxDepth = 0;
        ClonedWorkingFunctions = 0;
        RewriteVisitedFunctions = 0;
        RewriteSingleBlockFunctions = 0;
        RewriteMultiBlockFunctions = 0;
        RewriteIterations = 0;
        RewriteBlocksScanned = 0;
        RewriteInstructionsScanned = 0;
        FunctionRewriteSummariesBuilt = 0;
        FunctionRewriteSummaryCandidateBlocks = 0;
        FunctionRewriteSummaryCandidateInstructions = 0;
        DirtyRewriteQueueEntries = 0;
        DirtyRewriteQueueSkippedSpecializations = 0;
        DirtyRewriteQueueNoOpDequeues = 0;
        DirtyRewriteFullScanFallbacks = 0;
        DirtyRewriteCandidateBlockFunctions = 0;
        DirtyRewriteCandidateInstructionFunctions = 0;
        DirtyRewriteCandidateInstructionsVisited = 0;
        LocalTypeMapBuilds = 0;
        LocalTypeConcretizeCalls = 0;
        LocalRefreshes = 0;
        OperandRefreshes = 0;
        ReturnTypePropagations = 0;
        SpecializationCacheHits = 0;
        SpecializationsCreated = 0;
        SpecializationRejections = 0;
        EnqueuedSpecializations = 0;
        TemplateCallRewrites = 0;
        StateTransferClones = 0;
        StateMergeClones = 0;
        StateStorageClones = 0;
        StateCloneEntries = 0;
        StateTransferPoolHits = 0;
        CombineBoundArgumentLists = 0;
        CloneOperandLists = 0;
        CloneOperandListItems = 0;
        FunctionRefRewrites = 0;
        TypeBindingCacheHits = 0;
        TypeBindingCacheMisses = 0;
        MeaningfulSignatureCacheHits = 0;
        MeaningfulSignatureCacheMisses = 0;
    }

    public MirGenericSpecializerStats Snapshot()
    {
        return new MirGenericSpecializerStats
        {
            InitialRewriteQueueEntries = InitialRewriteQueueEntries,
            RewriteQueueDequeues = RewriteQueueDequeues,
            RewriteQueueMaxDepth = RewriteQueueMaxDepth,
            ClonedWorkingFunctions = ClonedWorkingFunctions,
            RewriteVisitedFunctions = RewriteVisitedFunctions,
            RewriteSingleBlockFunctions = RewriteSingleBlockFunctions,
            RewriteMultiBlockFunctions = RewriteMultiBlockFunctions,
            RewriteIterations = RewriteIterations,
            RewriteBlocksScanned = RewriteBlocksScanned,
            RewriteInstructionsScanned = RewriteInstructionsScanned,
            FunctionRewriteSummariesBuilt = FunctionRewriteSummariesBuilt,
            FunctionRewriteSummaryCandidateBlocks = FunctionRewriteSummaryCandidateBlocks,
            FunctionRewriteSummaryCandidateInstructions = FunctionRewriteSummaryCandidateInstructions,
            DirtyRewriteQueueEntries = DirtyRewriteQueueEntries,
            DirtyRewriteQueueSkippedSpecializations = DirtyRewriteQueueSkippedSpecializations,
            DirtyRewriteQueueNoOpDequeues = DirtyRewriteQueueNoOpDequeues,
            DirtyRewriteFullScanFallbacks = DirtyRewriteFullScanFallbacks,
            DirtyRewriteCandidateBlockFunctions = DirtyRewriteCandidateBlockFunctions,
            DirtyRewriteCandidateInstructionFunctions = DirtyRewriteCandidateInstructionFunctions,
            DirtyRewriteCandidateInstructionsVisited = DirtyRewriteCandidateInstructionsVisited,
            LocalTypeMapBuilds = LocalTypeMapBuilds,
            LocalTypeConcretizeCalls = LocalTypeConcretizeCalls,
            LocalRefreshes = LocalRefreshes,
            OperandRefreshes = OperandRefreshes,
            ReturnTypePropagations = ReturnTypePropagations,
            SpecializationCacheHits = SpecializationCacheHits,
            SpecializationsCreated = SpecializationsCreated,
            SpecializationRejections = SpecializationRejections,
            EnqueuedSpecializations = EnqueuedSpecializations,
            TemplateCallRewrites = TemplateCallRewrites,
            StateTransferClones = StateTransferClones,
            StateMergeClones = StateMergeClones,
            StateStorageClones = StateStorageClones,
            StateCloneEntries = StateCloneEntries,
            StateTransferPoolHits = StateTransferPoolHits,
            CombineBoundArgumentLists = CombineBoundArgumentLists,
            CloneOperandLists = CloneOperandLists,
            CloneOperandListItems = CloneOperandListItems,
            FunctionRefRewrites = FunctionRefRewrites,
            TypeBindingCacheHits = TypeBindingCacheHits,
            TypeBindingCacheMisses = TypeBindingCacheMisses,
            MeaningfulSignatureCacheHits = MeaningfulSignatureCacheHits,
            MeaningfulSignatureCacheMisses = MeaningfulSignatureCacheMisses
        };
    }

    public void Add(MirGenericSpecializerStats other)
    {
        InitialRewriteQueueEntries += other.InitialRewriteQueueEntries;
        RewriteQueueDequeues += other.RewriteQueueDequeues;
        RewriteQueueMaxDepth = Math.Max(RewriteQueueMaxDepth, other.RewriteQueueMaxDepth);
        ClonedWorkingFunctions += other.ClonedWorkingFunctions;
        RewriteVisitedFunctions += other.RewriteVisitedFunctions;
        RewriteSingleBlockFunctions += other.RewriteSingleBlockFunctions;
        RewriteMultiBlockFunctions += other.RewriteMultiBlockFunctions;
        RewriteIterations += other.RewriteIterations;
        RewriteBlocksScanned += other.RewriteBlocksScanned;
        RewriteInstructionsScanned += other.RewriteInstructionsScanned;
        FunctionRewriteSummariesBuilt += other.FunctionRewriteSummariesBuilt;
        FunctionRewriteSummaryCandidateBlocks += other.FunctionRewriteSummaryCandidateBlocks;
        FunctionRewriteSummaryCandidateInstructions += other.FunctionRewriteSummaryCandidateInstructions;
        DirtyRewriteQueueEntries += other.DirtyRewriteQueueEntries;
        DirtyRewriteQueueSkippedSpecializations += other.DirtyRewriteQueueSkippedSpecializations;
        DirtyRewriteQueueNoOpDequeues += other.DirtyRewriteQueueNoOpDequeues;
        DirtyRewriteFullScanFallbacks += other.DirtyRewriteFullScanFallbacks;
        DirtyRewriteCandidateBlockFunctions += other.DirtyRewriteCandidateBlockFunctions;
        DirtyRewriteCandidateInstructionFunctions += other.DirtyRewriteCandidateInstructionFunctions;
        DirtyRewriteCandidateInstructionsVisited += other.DirtyRewriteCandidateInstructionsVisited;
        LocalTypeMapBuilds += other.LocalTypeMapBuilds;
        LocalTypeConcretizeCalls += other.LocalTypeConcretizeCalls;
        LocalRefreshes += other.LocalRefreshes;
        OperandRefreshes += other.OperandRefreshes;
        ReturnTypePropagations += other.ReturnTypePropagations;
        SpecializationCacheHits += other.SpecializationCacheHits;
        SpecializationsCreated += other.SpecializationsCreated;
        SpecializationRejections += other.SpecializationRejections;
        EnqueuedSpecializations += other.EnqueuedSpecializations;
        TemplateCallRewrites += other.TemplateCallRewrites;
        StateTransferClones += other.StateTransferClones;
        StateMergeClones += other.StateMergeClones;
        StateStorageClones += other.StateStorageClones;
        StateCloneEntries += other.StateCloneEntries;
        StateTransferPoolHits += other.StateTransferPoolHits;
        CombineBoundArgumentLists += other.CombineBoundArgumentLists;
        CloneOperandLists += other.CloneOperandLists;
        CloneOperandListItems += other.CloneOperandListItems;
        FunctionRefRewrites += other.FunctionRefRewrites;
        TypeBindingCacheHits += other.TypeBindingCacheHits;
        TypeBindingCacheMisses += other.TypeBindingCacheMisses;
        MeaningfulSignatureCacheHits += other.MeaningfulSignatureCacheHits;
        MeaningfulSignatureCacheMisses += other.MeaningfulSignatureCacheMisses;
    }
}
