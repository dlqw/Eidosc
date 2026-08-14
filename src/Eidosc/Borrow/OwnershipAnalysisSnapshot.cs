using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;

namespace Eidosc.Borrow;

/// <summary>
/// Conservative ownership state attached to a MIR place.
/// </summary>
internal enum OwnershipPlaceState
{
    Uninitialized,
    Owned,
    SharedAlias,
    MutableBorrowed,
    Moved,
    Dropped,
    MaybeOwned
}

[Flags]
internal enum OwnershipEscapeKind
{
    None = 0,
    Return = 1,
    Store = 2,
    Capture = 4,
    Ffi = 8,
    UnknownCall = 16
}

internal readonly record struct OwnershipPlaceKey(LocalId Root, string ProjectionPath)
{
    public override string ToString() => string.IsNullOrEmpty(ProjectionPath)
        ? Root.ToString()
        : $"{Root}{ProjectionPath}";
}

internal sealed record OwnershipProvenanceFact(
    OwnershipPlaceKey Place,
    OwnershipPlaceState State,
    bool MustOwned,
    bool MustUnique,
    bool MaybeShared,
    bool HasActiveBorrow);

internal sealed record OwnershipBorrowRegionFact(
    int BorrowId,
    LocalId Borrower,
    LocalId Borrowee,
    bool IsMutable,
    (BlockId Block, int Index) Origin,
    (BlockId Block, int Index)? End);

internal sealed record OwnershipInstructionFact(
    BlockId Block,
    int Index,
    IReadOnlyDictionary<LocalId, OwnershipPlaceState> States,
    IReadOnlySet<LocalId> MustOwned,
    IReadOnlySet<LocalId> MustUnique,
    IReadOnlySet<LocalId> ActiveBorrowRoots,
    OwnershipEscapeKind Escapes,
    bool IsDropObligation,
    bool IsEarlyReturn,
    bool IsPanicPath);

internal sealed record OwnershipExitCleanupFact(
    BlockId Block,
    bool IsEarlyReturn,
    bool IsPanicPath,
    IReadOnlySet<LocalId> LocalsRequiringCleanup,
    IReadOnlySet<LocalId> LocalsWithInsertedDrop);

internal sealed record PartialMoveFact(
    OwnershipPlaceKey Place,
    (BlockId Block, int Index) MoveSite,
    (BlockId Block, int Index)? ReinitializeSite);

internal sealed class OwnershipBlockState
{
    public OwnershipBlockState(IReadOnlyDictionary<LocalId, OwnershipPlaceState> states)
    {
        States = new Dictionary<LocalId, OwnershipPlaceState>(states);
    }

    public Dictionary<LocalId, OwnershipPlaceState> States { get; }

    public OwnershipBlockState Clone() => new(States);

    public static OwnershipBlockState Empty(IEnumerable<LocalId> locals) =>
        new(new Dictionary<LocalId, OwnershipPlaceState>());
}

/// <summary>
/// Unified compiler-owned ownership facts shared by borrow, drop, Perceus,
/// reuse and sequence planning. The snapshot is deliberately conservative:
/// an unknown fact removes destructive-reuse proofs instead of inventing one.
/// </summary>
internal sealed class OwnershipAnalysisSnapshot
{
    private OwnershipAnalysisSnapshot(
        string functionStableKey,
        string bodyFingerprint,
        IReadOnlyDictionary<OwnershipPlaceKey, OwnershipProvenanceFact> provenance,
        IReadOnlyDictionary<LocalId, IReadOnlySet<LocalId>> aliasSets,
        IReadOnlyDictionary<(BlockId Block, int Index), OwnershipBorrowRegionFact[]> borrowRegions,
        IReadOnlyDictionary<(BlockId Block, int Index), OwnershipInstructionFact> instructionFacts,
        IReadOnlyDictionary<BlockId, OwnershipBlockState> blockIn,
        IReadOnlyDictionary<BlockId, OwnershipBlockState> blockOut,
        IReadOnlyDictionary<LocalId, (BlockId Block, int Index)> lastOwnershipUse,
        IReadOnlySet<LocalId> dropObligations,
        IReadOnlySet<LocalId> loopCarriedLocals,
        IReadOnlyDictionary<LocalId, OwnershipEscapeKind> escapes,
        IReadOnlySet<BlockId> earlyReturnBlocks,
        IReadOnlySet<BlockId> panicBlocks,
        SequenceOptimizationFacts sequenceFacts,
        IReadOnlyDictionary<BlockId, OwnershipExitCleanupFact> exitCleanupFacts,
        IReadOnlyDictionary<OwnershipPlaceKey, PartialMoveFact> partialMoves,
        IReadOnlyDictionary<LocalId, int> dropCounts,
        LivenessAnalyzer livenessAnalyzer,
        BorrowChecker borrowChecker,
        LoanConstraintVerifier loanVerifier,
        PerceusHints perceusHints,
        ReuseHints? reuseHints,
        IReadOnlyList<LoanConstraintResult> loanResults)
    {
        FunctionStableKey = functionStableKey;
        BodyFingerprint = bodyFingerprint;
        PlaceProvenance = provenance;
        AliasSets = aliasSets;
        BorrowRegions = borrowRegions;
        PerInstructionFacts = instructionFacts;
        PerBlockInFacts = blockIn;
        PerBlockOutFacts = blockOut;
        LastOwnershipUse = lastOwnershipUse;
        DropObligations = dropObligations;
        LoopCarriedLocals = loopCarriedLocals;
        EscapeFacts = escapes;
        EarlyReturnBlocks = earlyReturnBlocks;
        PanicBlocks = panicBlocks;
        SequenceFacts = sequenceFacts;
        ExitCleanupFacts = exitCleanupFacts;
        PartialMoves = partialMoves;
        DropCounts = dropCounts;
        DropExactlyOnceLocals = dropCounts
            .Where(static pair => pair.Value == 1)
            .Select(static pair => pair.Key)
            .ToHashSet();
        LivenessAnalyzer = livenessAnalyzer;
        BorrowChecker = borrowChecker;
        LoanConstraintVerifier = loanVerifier;
        PerceusHints = perceusHints;
        ReuseHints = reuseHints;
        LoanConstraintResults = loanResults;
    }

    public string FunctionStableKey { get; }
    public string BodyFingerprint { get; }
    public IReadOnlyDictionary<OwnershipPlaceKey, OwnershipProvenanceFact> PlaceProvenance { get; }
    public IReadOnlyDictionary<LocalId, IReadOnlySet<LocalId>> AliasSets { get; }
    public IReadOnlyDictionary<(BlockId Block, int Index), OwnershipBorrowRegionFact[]> BorrowRegions { get; }
    public IReadOnlyDictionary<(BlockId Block, int Index), OwnershipInstructionFact> PerInstructionFacts { get; }
    public IReadOnlyDictionary<BlockId, OwnershipBlockState> PerBlockInFacts { get; }
    public IReadOnlyDictionary<BlockId, OwnershipBlockState> PerBlockOutFacts { get; }
    public IReadOnlyDictionary<LocalId, (BlockId Block, int Index)> LastOwnershipUse { get; }
    public IReadOnlySet<LocalId> DropObligations { get; }
    public IReadOnlySet<LocalId> LoopCarriedLocals { get; }
    public IReadOnlyDictionary<LocalId, OwnershipEscapeKind> EscapeFacts { get; }
    public IReadOnlySet<BlockId> EarlyReturnBlocks { get; }
    public IReadOnlySet<BlockId> PanicBlocks { get; }
    public SequenceOptimizationFacts SequenceFacts { get; }
    public IReadOnlyDictionary<BlockId, OwnershipExitCleanupFact> ExitCleanupFacts { get; }
    public IReadOnlyDictionary<OwnershipPlaceKey, PartialMoveFact> PartialMoves { get; }
    public IReadOnlyDictionary<LocalId, int> DropCounts { get; }
    public IReadOnlySet<LocalId> DropExactlyOnceLocals { get; }
    public LivenessAnalyzer LivenessAnalyzer { get; }
    public BorrowChecker BorrowChecker { get; }
    public LoanConstraintVerifier LoanConstraintVerifier { get; }
    public PerceusHints PerceusHints { get; }
    public ReuseHints? ReuseHints { get; }
    public IReadOnlyList<LoanConstraintResult> LoanConstraintResults { get; }

    public bool IsMustOwned(LocalId local, BlockId block, int index) =>
        PerInstructionFacts.TryGetValue((block, index), out var fact) && fact.MustOwned.Contains(local);

    public bool IsMustUnique(LocalId local, BlockId block, int index) =>
        PerInstructionFacts.TryGetValue((block, index), out var fact) && fact.MustUnique.Contains(local);

    public bool HasActiveBorrow(LocalId local, BlockId block, int index) =>
        PerInstructionFacts.TryGetValue((block, index), out var fact) && fact.ActiveBorrowRoots.Contains(local);

    public bool CanDestructivelyUpdate(LocalId local, BlockId block, int index) =>
        IsMustOwned(local, block, index) &&
        IsMustUnique(local, block, index) &&
        !HasActiveBorrow(local, block, index) &&
        EscapeFacts.GetValueOrDefault(local) == OwnershipEscapeKind.None;

    public bool HasExactlyOneDrop(LocalId local) => DropExactlyOnceLocals.Contains(local);

    public bool IsPartialMoveReinitialized(OwnershipPlaceKey place) =>
        PartialMoves.TryGetValue(place, out var fact) && fact.ReinitializeSite.HasValue;

    /// <summary>
    /// Builds one snapshot after the individual analyses have run. The MIR
    /// scan is the common fallback and the supplied analyzers enrich it with
    /// their authoritative borrow/liveness/reuse facts.
    /// </summary>
    public static OwnershipAnalysisSnapshot Build(
        MirFunc function,
        ControlFlowGraph cfg,
        VariableUsageAnalyzer usage,
        LivenessAnalyzer liveness,
        BorrowChecker borrowChecker,
        LoanConstraintVerifier loanVerifier,
        PerceusAnalyzer perceus,
        ReuseAnalyzer? reuse,
        IReadOnlyList<LoanConstraintResult> loanResults,
        bool managedOnly = false)
    {
        var locals = function.Locals.Select(static local => local.Id).ToArray();
        var blockIn = new Dictionary<BlockId, OwnershipBlockState>();
        var blockOut = new Dictionary<BlockId, OwnershipBlockState>();
        foreach (var block in function.BasicBlocks)
        {
            blockIn[block.Id] = OwnershipBlockState.Empty(locals);
            blockOut[block.Id] = OwnershipBlockState.Empty(locals);
        }

        var entryBlockId = blockIn.ContainsKey(function.EntryBlockId)
            ? function.EntryBlockId
            : function.BasicBlocks.FirstOrDefault()?.Id ?? BlockId.None;
        if (blockIn.TryGetValue(entryBlockId, out var entry))
        {
            foreach (var local in function.Locals.Where(local =>
                         local.IsParameter && (!managedOnly || TypeSemantics.IsManagedType(local.TypeId))))
                entry.States[local.Id] = OwnershipPlaceState.Owned;
        }

        var predecessors = function.BasicBlocks.ToDictionary(
            static block => block.Id,
            block => cfg.GetPredecessors(block.Id));
        var changed = true;
        var iterations = 0;
        while (changed && iterations++ < Math.Max(8, function.BasicBlocks.Count * 4))
        {
            changed = false;
            foreach (var block in function.BasicBlocks)
            {
                if (!block.Id.Equals(entryBlockId))
                {
                    var incoming = MergeIncoming(block.Id, predecessors, blockOut);
                    if (!StatesEqual(blockIn[block.Id], incoming))
                    {
                        blockIn[block.Id] = incoming;
                        changed = true;
                    }
                }

                var outgoing = TransferBlock(block, blockIn[block.Id], managedOnly);
                if (!StatesEqual(blockOut[block.Id], outgoing))
                {
                    blockOut[block.Id] = outgoing;
                    changed = true;
                }
            }
        }

        var managedLocals = function.Locals
            .Where(local => !managedOnly || TypeSemantics.IsManagedType(local.TypeId))
            .Select(static local => local.Id)
            .ToHashSet();
        var aliases = BuildAliasSets(function, managedLocals);
        var escapes = BuildEscapeFacts(function, managedLocals);
        var borrowRegions = BuildBorrowRegions(borrowChecker);
        var activeBorrowByPoint = borrowRegions.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.SelectMany(static region => new[] { region.Borrowee, region.Borrower }).ToHashSet());
        var dropObligations = BuildDropObligations(function);
        var earlyReturns = function.BasicBlocks
            .Where(block => block.Terminator is MirReturn && !block.Id.Equals(function.EntryBlockId))
            .Select(static block => block.Id)
            .ToHashSet();
        var panicBlocks = function.BasicBlocks
            .Where(static block => block.Terminator is MirUnreachable)
            .Select(static block => block.Id)
            .ToHashSet();
        var loopCarried = BuildLoopCarriedLocals(function, cfg);
        var lastOwnershipUse = function.Locals
            .Select(local => (local.Id, Use: usage.GetLastUse(local.Id)))
            .Where(static item => item.Use.HasValue)
            .ToDictionary(static item => item.Id, static item => item.Use!.Value);

        var provenance = BuildProvenance(function, aliases, escapes, activeBorrowByPoint, blockIn, blockOut);
        var dropCounts = BuildDropCounts(function);
        var partialMoves = BuildPartialMoves(function);
        var exitCleanupFacts = BuildExitCleanupFacts(function, blockOut, earlyReturns, panicBlocks, dropCounts);
        var instructionFacts = BuildInstructionFacts(
            function,
            blockIn,
            aliases,
            escapes,
            activeBorrowByPoint,
            dropObligations,
            earlyReturns,
            panicBlocks,
            managedOnly);

        var sequenceFacts = SequenceOptimizationFacts.Analyze(function);
        var stableKey = MirFunctionIdentity.GetStableKey(function);
        var fingerprint = MirFunctionFingerprintBuilder.Compute(function).BodyHash;
        return new OwnershipAnalysisSnapshot(
            stableKey,
            fingerprint,
            provenance,
            aliases,
            borrowRegions,
            instructionFacts,
            blockIn,
            blockOut,
            lastOwnershipUse,
            dropObligations,
            loopCarried,
            escapes,
            earlyReturns,
            panicBlocks,
            sequenceFacts,
            exitCleanupFacts,
            partialMoves,
            dropCounts,
            liveness,
            borrowChecker,
            loanVerifier,
            perceus.Hints,
            reuse?.Hints,
            loanResults);
    }

    /// <summary>
    /// Creates conservative snapshots for MIR optimization. This runs before
    /// ownership-finalizing rewrites, so the optimizer must discard and rebuild
    /// the map whenever the MIR module identity changes.
    /// </summary>
    internal static IReadOnlyDictionary<string, OwnershipAnalysisSnapshot> BuildForOptimization(MirModule module)
    {
        var snapshots = new Dictionary<string, OwnershipAnalysisSnapshot>(StringComparer.Ordinal);
        foreach (var function in module.Functions.Where(static function => function.BasicBlocks.Count > 0))
        {
            try
            {
                var usage = new VariableUsageAnalyzer(function);
                usage.Analyze();
                var cfg = new ControlFlowGraph(function);
                var liveness = new LivenessAnalyzer(function, usage, cfg);
                liveness.Analyze();
                var borrowChecker = new BorrowChecker(function, liveness, capturePointStates: true, cfg: cfg);
                borrowChecker.Check();
                var perceus = new PerceusAnalyzer(function, liveness, usage);
                perceus.Analyze();
                var reuse = new ReuseAnalyzer(function, perceus.Hints);
                reuse.Analyze();
                var loanVerifier = new LoanConstraintVerifier(
                    new LoanSignatureCache(),
                    new SymbolTable(),
                    capturePointStates: true,
                    dynamicTypeKeys: module.DynamicTypeKeys);
                var loanResults = loanVerifier.VerifyFunction(function, cfg);
                var snapshot = Build(
                    function,
                    cfg,
                    usage,
                    liveness,
                    borrowChecker,
                    loanVerifier,
                    perceus,
                    reuse,
                    loanResults,
                    managedOnly: true);
                snapshots[MirFunctionIdentity.GetStableKey(function)] = snapshot;
            }
            catch (InvalidOperationException)
            {
                // Optimization proof construction is optional. An unknown
                // snapshot must force the ordinary lowering path.
            }
        }

        return snapshots;
    }

    private static OwnershipBlockState MergeIncoming(
        BlockId block,
        IReadOnlyDictionary<BlockId, IReadOnlySet<BlockId>> predecessors,
        IReadOnlyDictionary<BlockId, OwnershipBlockState> blockOut)
    {
        var incoming = OwnershipBlockState.Empty([]);
        if (!predecessors.TryGetValue(block, out var preds) || preds.Count == 0)
            return incoming;

        var predecessorStates = preds
            .Where(blockOut.ContainsKey)
            .Select(pred => blockOut[pred])
            .ToArray();
        foreach (var local in predecessorStates.SelectMany(static state => state.States.Keys).Distinct())
        {
            var states = predecessorStates
                .Select(state => state.States.GetValueOrDefault(local, OwnershipPlaceState.Uninitialized))
                .ToArray();
            incoming.States[local] = states.Length == 0
                ? OwnershipPlaceState.Uninitialized
                : states.All(state => state == states[0])
                    ? states[0]
                    : OwnershipPlaceState.MaybeOwned;
        }

        return incoming;
    }

    private static OwnershipBlockState TransferBlock(
        MirBasicBlock block,
        OwnershipBlockState input,
        bool managedOnly)
    {
        var state = input.Clone();
        foreach (var instruction in block.Instructions)
            TransferInstruction(instruction, state, managedOnly);
        if (block.Terminator is MirReturn { Value: MirPlace place } &&
            (!managedOnly || TypeSemantics.IsManagedType(place.TypeId)))
            state.States[place.RootLocal()] = OwnershipPlaceState.Moved;
        return state;
    }

    private static void TransferInstruction(
        MirInstruction instruction,
        OwnershipBlockState state,
        bool managedOnly = false)
    {
        switch (instruction)
        {
            case MirMove move:
                SetTrackedState(move.Source, OwnershipPlaceState.Moved, state, managedOnly);
                SetTrackedState(move.Target, OwnershipPlaceState.Owned, state, managedOnly);
                break;
            case MirCopy copy:
                SetTrackedState(copy.Source, OwnershipPlaceState.SharedAlias, state, managedOnly);
                SetTrackedState(copy.Target, OwnershipPlaceState.SharedAlias, state, managedOnly);
                break;
            case MirLoad load:
                SetTrackedState(load.Target, load.CreatesBorrowAlias
                    ? OwnershipPlaceState.SharedAlias
                    : OwnershipPlaceState.Owned, state, managedOnly);
                if (load.MovesOutOfSource)
                    SetTrackedState(load.Source, OwnershipPlaceState.Moved, state, managedOnly);
                break;
            case MirDrop drop:
                SetTrackedState(drop.Value, OwnershipPlaceState.Dropped, state, managedOnly);
                break;
            case MirCall call:
                if (call.Target != null)
                    SetTrackedState(call.Target, OwnershipPlaceState.Owned, state, managedOnly);
                for (var argumentIndex = 0; argumentIndex < call.Arguments.Count; argumentIndex++)
                {
                    if (call.Arguments[argumentIndex] is not MirPlace argument ||
                        call.BorrowedArgumentIndices.Contains(argumentIndex))
                        continue;
                    SetTrackedState(argument, OwnershipPlaceState.Moved, state, managedOnly);
                }
                break;
            case MirAssign assign:
                SetTrackedState(assign.Target, OwnershipPlaceState.Owned, state, managedOnly);
                break;
            case MirCaseInject inject when inject.Target is MirPlace target:
                SetTrackedState(target, OwnershipPlaceState.Owned, state, managedOnly);
                break;
            case MirStore store:
                SetTrackedState(store.Value, OwnershipPlaceState.Moved, state, managedOnly);
                break;
            case MirBinOp binary when binary.Target is MirPlace target:
                SetTrackedState(target, OwnershipPlaceState.Owned, state, managedOnly);
                break;
            case MirUnaryOp unary when unary.Target is MirPlace target:
                SetTrackedState(target, OwnershipPlaceState.Owned, state, managedOnly);
                break;
            case MirSelect select:
                SetTrackedState(select.Target, OwnershipPlaceState.Owned, state, managedOnly);
                break;
            case MirAlloc alloc:
                SetTrackedState(alloc.Target, OwnershipPlaceState.Owned, state, managedOnly);
                break;
        }
    }

    private static void SetTrackedState(
        MirOperand operand,
        OwnershipPlaceState value,
        OwnershipBlockState state,
        bool managedOnly)
    {
        if (operand is MirPlace place && (!managedOnly || TypeSemantics.IsManagedType(place.TypeId)))
        {
            state.States[place.RootLocal()] = value;
        }
    }

    private static Dictionary<LocalId, IReadOnlySet<LocalId>> BuildAliasSets(
        MirFunc function,
        IReadOnlySet<LocalId> managedLocals)
    {
        var parent = managedLocals.ToDictionary(static local => local, static local => local);
        LocalId Find(LocalId local)
        {
            if (!parent.TryGetValue(local, out var value) || value.Equals(local)) return local;
            var root = Find(value);
            parent[local] = root;
            return root;
        }
        void Union(LocalId left, LocalId right)
        {
            if (!left.IsValid || !right.IsValid) return;
            var l = Find(left); var r = Find(right);
            if (!l.Equals(r)) parent[r] = l;
        }
        foreach (var block in function.BasicBlocks)
            foreach (var instruction in block.Instructions)
                if (instruction is MirCopy copy && managedLocals.Contains(copy.Source.RootLocal()))
                    Union(copy.Source.RootLocal(), copy.Target.RootLocal());

        var groups = parent.Keys.GroupBy(Find).ToDictionary(group => group.Key, group => (IReadOnlySet<LocalId>)group.ToHashSet());
        return parent.Keys.ToDictionary(local => local, local => groups[Find(local)]);
    }

    private static Dictionary<LocalId, OwnershipEscapeKind> BuildEscapeFacts(
        MirFunc function,
        IReadOnlySet<LocalId> managedLocals)
    {
        var escapes = managedLocals.ToDictionary(static local => local, static _ => OwnershipEscapeKind.None);
        void Add(MirOperand? operand, OwnershipEscapeKind kind)
        {
            if (operand is not MirPlace place) return;
            foreach (var local in place.EnumerateRootLocals())
                if (managedLocals.Contains(local))
                    escapes[local] = escapes.GetValueOrDefault(local) | kind;
        }
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case MirStore store when store.Target.Kind != PlaceKind.Local:
                        Add(store.Value, OwnershipEscapeKind.Store);
                        break;
                    case MirCall call:
                        var kind = call.Function is MirFunctionRef functionRef && LooksLikeFfi(functionRef.Name)
                            ? OwnershipEscapeKind.Ffi
                            : call.Function is MirFunctionRef knownFunction &&
                              (IsKnownNonEscapingSequenceRole(knownFunction.CompilerSemanticRole) ||
                               LooksLikeKnownNonEscapingSequence(knownFunction))
                                ? OwnershipEscapeKind.None
                                : OwnershipEscapeKind.UnknownCall;
                        for (var i = 0; i < call.Arguments.Count; i++)
                            if (!call.BorrowedArgumentIndices.Contains(i)) Add(call.Arguments[i], kind);
                        break;
                }
            }
            if (block.Terminator is MirReturn { Value: { } value }) Add(value, OwnershipEscapeKind.Return);
        }
        return escapes;
    }

    private static bool LooksLikeFfi(string name) =>
        name.Contains("ffi", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("extern", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownNonEscapingSequenceRole(CompilerSemanticRole role) => role is
        CompilerSemanticRole.SequenceHead or
        CompilerSemanticRole.SequenceTake or
        CompilerSemanticRole.SequenceDrop or
        CompilerSemanticRole.SequenceMap or
        CompilerSemanticRole.SequenceFilter or
        CompilerSemanticRole.SequenceFlatMap or
        CompilerSemanticRole.SequenceFoldLeft or
        CompilerSemanticRole.SequenceFoldRight or
        CompilerSemanticRole.SequenceFind or
        CompilerSemanticRole.SequenceAny or
        CompilerSemanticRole.SequenceAll or
        CompilerSemanticRole.SequenceCount or
        CompilerSemanticRole.SequenceForEach or
        CompilerSemanticRole.SequenceZip or
        CompilerSemanticRole.SequenceZipWith or
        CompilerSemanticRole.SequencePartition or
        CompilerSemanticRole.SequenceReverse or
        CompilerSemanticRole.SequenceBuilderFreeze;

    private static bool LooksLikeKnownNonEscapingSequence(MirFunctionRef function)
    {
        var names = new[] { function.Name, function.FunctionId.Name };
        return names.Any(name =>
        {
            var canonical = name.Split("__spec_", 2, StringSplitOptions.None)[0];
            return canonical.Contains("__Seq__", StringComparison.Ordinal) &&
                   (canonical.Contains("__take", StringComparison.Ordinal) ||
                    canonical.Contains("__drop", StringComparison.Ordinal) ||
                    canonical.Contains("__reverse", StringComparison.Ordinal) ||
                    canonical.Contains("__find", StringComparison.Ordinal) ||
                    canonical.Contains("__any", StringComparison.Ordinal) ||
                    canonical.Contains("__all", StringComparison.Ordinal) ||
                    canonical.Contains("__count", StringComparison.Ordinal) ||
                    canonical.Contains("__for_each", StringComparison.Ordinal));
        }) ||
        names.Any(name => name.Split("__spec_", 2, StringSplitOptions.None)[0] is
            "take" or "drop" or "reverse" or "find" or "any" or "all" or "count" or "for_each");
    }

    private static Dictionary<(BlockId Block, int Index), OwnershipBorrowRegionFact[]> BuildBorrowRegions(BorrowChecker checker) =>
        checker.EnumerateBorrowStates().ToDictionary(
            static entry => entry.Point,
            static entry => entry.Borrows.Select(static borrow => new OwnershipBorrowRegionFact(
                borrow.Id,
                borrow.Borrower,
                borrow.Borrowee,
                borrow.IsMutable,
                borrow.OriginLocation,
                borrow.EndLocation)).ToArray());

    private static HashSet<LocalId> BuildDropObligations(MirFunc function)
    {
        var drops = new HashSet<LocalId>();
        foreach (var block in function.BasicBlocks)
            foreach (var instruction in block.Instructions)
                if (instruction is MirDrop drop)
                    if (drop.Value.RootLocal().IsValid)
                        drops.Add(drop.Value.RootLocal());
        return drops;
    }

    private static Dictionary<LocalId, int> BuildDropCounts(MirFunc function)
    {
        var counts = function.Locals.ToDictionary(static local => local.Id, static _ => 0);
        foreach (var block in function.BasicBlocks)
            foreach (var instruction in block.Instructions)
                if (instruction is MirDrop drop && drop.Value.RootLocal().IsValid)
                    counts[drop.Value.RootLocal()] = counts.GetValueOrDefault(drop.Value.RootLocal()) + 1;
        return counts;
    }

    private static Dictionary<OwnershipPlaceKey, PartialMoveFact> BuildPartialMoves(MirFunc function)
    {
        var result = new Dictionary<OwnershipPlaceKey, PartialMoveFact>();
        foreach (var block in function.BasicBlocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                switch (block.Instructions[index])
                {
                    case MirMove move when move.Source.Kind != PlaceKind.Local:
                    {
                        var key = new OwnershipPlaceKey(move.Source.RootLocal(), move.Source.ProjectionPath());
                        result[key] = new PartialMoveFact(key, (block.Id, index), null);
                        break;
                    }
                    case MirAssign assign when assign.Target.Kind != PlaceKind.Local:
                    {
                        var key = new OwnershipPlaceKey(assign.Target.RootLocal(), assign.Target.ProjectionPath());
                        if (result.TryGetValue(key, out var moved))
                            result[key] = moved with { ReinitializeSite = (block.Id, index) };
                        break;
                    }
                    case MirStore store when store.Target.Kind != PlaceKind.Local:
                    {
                        var key = new OwnershipPlaceKey(store.Target.RootLocal(), store.Target.ProjectionPath());
                        if (result.TryGetValue(key, out var moved))
                            result[key] = moved with { ReinitializeSite = (block.Id, index) };
                        break;
                    }
                }
            }
        }
        return result;
    }

    private static Dictionary<BlockId, OwnershipExitCleanupFact> BuildExitCleanupFacts(
        MirFunc function,
        IReadOnlyDictionary<BlockId, OwnershipBlockState> blockOut,
        IReadOnlySet<BlockId> earlyReturns,
        IReadOnlySet<BlockId> panicBlocks,
        IReadOnlyDictionary<LocalId, int> dropCounts)
    {
        var result = new Dictionary<BlockId, OwnershipExitCleanupFact>();
        foreach (var block in function.BasicBlocks.Where(block => block.Terminator is MirReturn or MirUnreachable))
        {
            var requiring = blockOut[block.Id].States
                .Where(static pair => pair.Value is OwnershipPlaceState.Owned or OwnershipPlaceState.MaybeOwned)
                .Select(static pair => pair.Key)
                .ToHashSet();
            var inserted = dropCounts.Keys.Where(local =>
                block.Instructions.Any(instruction => instruction is MirDrop drop && drop.Value.RootLocal().Equals(local))).ToHashSet();
            result[block.Id] = new OwnershipExitCleanupFact(
                block.Id,
                earlyReturns.Contains(block.Id),
                panicBlocks.Contains(block.Id),
                requiring,
                inserted);
        }
        return result;
    }

    private static HashSet<LocalId> BuildLoopCarriedLocals(MirFunc function, ControlFlowGraph cfg)
    {
        var result = new HashSet<LocalId>();
        foreach (var block in function.BasicBlocks)
        {
            foreach (var successor in cfg.GetSuccessors(block.Id))
            {
                if (!cfg.GetDominators(block.Id).Contains(successor)) continue;
                foreach (var instruction in block.Instructions)
                    foreach (var local in instruction.Operands().OfType<MirPlace>().SelectMany(static place => place.EnumerateRootLocals()))
                        result.Add(local);
            }
        }
        return result;
    }

    private static Dictionary<OwnershipPlaceKey, OwnershipProvenanceFact> BuildProvenance(
        MirFunc function,
        IReadOnlyDictionary<LocalId, IReadOnlySet<LocalId>> aliases,
        IReadOnlyDictionary<LocalId, OwnershipEscapeKind> escapes,
        IReadOnlyDictionary<(BlockId Block, int Index), HashSet<LocalId>> activeBorrowByPoint,
        IReadOnlyDictionary<BlockId, OwnershipBlockState> blockIn,
        IReadOnlyDictionary<BlockId, OwnershipBlockState> blockOut)
    {
        var result = new Dictionary<OwnershipPlaceKey, OwnershipProvenanceFact>();
        foreach (var local in function.Locals)
        {
            var states = blockOut.Values;
            var allOwned = states.Any() && states.All(value =>
                value.States.GetValueOrDefault(local.Id) == OwnershipPlaceState.Owned);
            var hasSharedAlias = states.Any(value =>
                value.States.GetValueOrDefault(local.Id) == OwnershipPlaceState.SharedAlias);
            var current = allOwned
                ? OwnershipPlaceState.Owned
                : hasSharedAlias
                    ? OwnershipPlaceState.SharedAlias
                    : OwnershipPlaceState.MaybeOwned;
            var escape = escapes.GetValueOrDefault(local.Id);
            result[new OwnershipPlaceKey(local.Id, string.Empty)] = new OwnershipProvenanceFact(
                new OwnershipPlaceKey(local.Id, string.Empty),
                current,
                allOwned,
                aliases.TryGetValue(local.Id, out var group) && group.Count == 1 && escape == OwnershipEscapeKind.None,
                aliases.TryGetValue(local.Id, out group) && group.Count > 1,
                activeBorrowByPoint.Values.Any(roots => roots.Contains(local.Id)));
        }
        foreach (var block in function.BasicBlocks)
            foreach (var instruction in block.Instructions)
                foreach (var place in instruction.Operands().OfType<MirPlace>())
                    AddPlaceAndProjections(place, result, aliases, escapes);
        return result;
    }

    private static void AddPlaceAndProjections(
        MirPlace place,
        IDictionary<OwnershipPlaceKey, OwnershipProvenanceFact> result,
        IReadOnlyDictionary<LocalId, IReadOnlySet<LocalId>> aliases,
        IReadOnlyDictionary<LocalId, OwnershipEscapeKind> escapes)
    {
        var key = new OwnershipPlaceKey(place.RootLocal(), place.ProjectionPath());
        if (result.ContainsKey(key)) return;
        var aliasCount = aliases.TryGetValue(key.Root, out var group) ? group.Count : 1;
        var escape = escapes.GetValueOrDefault(key.Root);
        result[key] = new OwnershipProvenanceFact(
            key,
            aliasCount > 1 ? OwnershipPlaceState.SharedAlias : OwnershipPlaceState.MaybeOwned,
            false,
            aliasCount == 1 && escape == OwnershipEscapeKind.None,
            aliasCount > 1,
            false);
        if (place.Base is not null) AddPlaceAndProjections(place.Base, result, aliases, escapes);
    }

    private static Dictionary<(BlockId Block, int Index), OwnershipInstructionFact> BuildInstructionFacts(
        MirFunc function,
        IReadOnlyDictionary<BlockId, OwnershipBlockState> blockIn,
        IReadOnlyDictionary<LocalId, IReadOnlySet<LocalId>> aliases,
        IReadOnlyDictionary<LocalId, OwnershipEscapeKind> escapes,
        IReadOnlyDictionary<(BlockId Block, int Index), HashSet<LocalId>> activeBorrowByPoint,
        IReadOnlySet<LocalId> dropObligations,
        IReadOnlySet<BlockId> earlyReturns,
        IReadOnlySet<BlockId> panicBlocks,
        bool managedOnly)
    {
        var result = new Dictionary<(BlockId Block, int Index), OwnershipInstructionFact>();
        foreach (var block in function.BasicBlocks)
        {
            var state = blockIn[block.Id].Clone();
            for (var index = 0; index <= block.Instructions.Count; index++)
            {
                var active = activeBorrowByPoint.GetValueOrDefault((block.Id, index)) ?? [];
                var relevantRoots = index < block.Instructions.Count
                    ? block.Instructions[index].Operands()
                        .OfType<MirPlace>()
                        .SelectMany(static place => place.EnumerateRootLocals())
                        .Concat(active)
                        .Where(static local => local.IsValid)
                        .ToHashSet()
                    : active.ToHashSet();
                var relevantStates = relevantRoots.ToDictionary(
                    static local => local,
                    local => state.States.GetValueOrDefault(local, OwnershipPlaceState.Uninitialized));
                var mustOwned = relevantStates
                    .Where(static pair => pair.Value == OwnershipPlaceState.Owned)
                    .Select(static pair => pair.Key)
                    .ToHashSet();
                var mustUnique = mustOwned.Where(local => aliases.TryGetValue(local, out var group) && group.Count == 1 && escapes.GetValueOrDefault(local) == OwnershipEscapeKind.None && active.Contains(local) == false).ToHashSet();
                var escape = index < block.Instructions.Count ? InstructionEscape(block.Instructions[index], escapes) : OwnershipEscapeKind.None;
                var isDrop = index < block.Instructions.Count && block.Instructions[index] is MirDrop;
                result[(block.Id, index)] = new OwnershipInstructionFact(block.Id, index, relevantStates, mustOwned, mustUnique, active, escape, isDrop, earlyReturns.Contains(block.Id), panicBlocks.Contains(block.Id));
                if (index < block.Instructions.Count)
                    TransferInstruction(block.Instructions[index], state, managedOnly);
            }
        }
        return result;
    }

    private static OwnershipEscapeKind InstructionEscape(MirInstruction instruction, IReadOnlyDictionary<LocalId, OwnershipEscapeKind> escapes)
    {
        return instruction switch
        {
            MirStore store when store.Target.Kind != PlaceKind.Local => OwnershipEscapeKind.Store,
            MirCall call when call.Function is MirFunctionRef functionRef && LooksLikeFfi(functionRef.Name) => OwnershipEscapeKind.Ffi,
            MirCall => OwnershipEscapeKind.UnknownCall,
            _ => OwnershipEscapeKind.None
        };
    }

    private static bool StatesEqual(OwnershipBlockState left, OwnershipBlockState right) =>
        left.States.Count == right.States.Count && left.States.All(pair => right.States.GetValueOrDefault(pair.Key) == pair.Value);
}

internal static class OwnershipSnapshotMirExtensions
{
    public static LocalId RootLocal(this MirOperand? operand) =>
        operand is MirPlace place ? place.RootLocal() : LocalId.None;

    public static LocalId RootLocal(this MirPlace place) =>
        place.Kind == PlaceKind.Local ? place.Local : place.Base?.RootLocal() ?? LocalId.None;

    public static string ProjectionPath(this MirPlace place)
    {
        var suffix = place.Kind switch
        {
            PlaceKind.Local => string.Empty,
            PlaceKind.Field => $".{place.FieldName}",
            PlaceKind.Index => $"[{place.Index}]",
            PlaceKind.Deref => ".*",
            _ => string.Empty
        };
        return (place.Base is null ? string.Empty : place.Base.ProjectionPath()) + suffix;
    }

    public static IEnumerable<LocalId> EnumerateRootLocals(this MirPlace place)
    {
        yield return place.RootLocal();
        if (place.Index is MirPlace index)
            foreach (var local in index.EnumerateRootLocals()) yield return local;
    }

    public static IEnumerable<MirOperand> Operands(this MirInstruction instruction)
    {
        return instruction switch
        {
            MirAssign value => new[] { value.Target, value.Source },
            MirCaseInject value => new[] { value.Target, value.Operand },
            MirCall value => value.Arguments.Append(value.Function).Append(value.Target).Where(static operand => operand is not null)!.Cast<MirOperand>(),
            MirBinOp value => new[] { value.Target, value.Left, value.Right },
            MirUnaryOp value => new[] { value.Target, value.Operand },
            MirSelect value => new[] { value.Target, value.Condition, value.TrueValue, value.FalseValue },
            MirLoad value => new[] { value.Target, value.Source },
            MirStore value => new[] { value.Target, value.Value },
            MirDrop value => new[] { value.Value },
            MirCopy value => new[] { value.Target, value.Source },
            MirMove value => new[] { value.Target, value.Source },
            MirAlloc value => new[] { value.Target },
            _ => []
        };
    }
}
