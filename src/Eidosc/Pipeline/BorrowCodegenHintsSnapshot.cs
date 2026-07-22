using Eidosc.Borrow;
using Eidosc.Mir;

namespace Eidosc.Pipeline;

public sealed record BorrowCodegenHintsSnapshot(
    string SchemaVersion,
    string MirModuleFingerprint,
    string BorrowCodegenDependencyHash,
    IReadOnlyList<BorrowCodegenHintsFunctionSnapshot> Functions)
{
    public const string CurrentSchemaVersion = "borrow-codegen-hints-snapshot-v2";

    public static BorrowCodegenHintsSnapshot Create(
        MirFunctionFingerprintSnapshot mirFingerprints,
        string borrowCodegenDependencyHash,
        ModuleBorrowCheckResult result)
    {
        var identity = BorrowSnapshotFunctionIdentity.Create(mirFingerprints);
        var functions = result.ResultsByFunctionKey
            .Select(pair =>
            {
                var stableKey = identity.ResolveFunctionKey(pair.Value, pair.Key);
                var bodyHash = identity.ResolveBodyHash(pair.Value, stableKey);
                return BorrowCodegenHintsFunctionSnapshot.FromResult(
                    stableKey,
                    bodyHash ?? "",
                    pair.Value);
            })
            .OrderBy(static function => function.FunctionKey, StringComparer.Ordinal)
            .ToArray();

        return new BorrowCodegenHintsSnapshot(
            CurrentSchemaVersion,
            mirFingerprints.ModuleFingerprint,
            borrowCodegenDependencyHash,
            functions);
    }

    public ModuleBorrowCheckResult ToBorrowCheckResult(BorrowDiagnosticSnapshot? diagnostics = null)
    {
        var result = new ModuleBorrowCheckResult();
        var diagnosticsByFunction = diagnostics?.Functions.ToDictionary(
            static function => function.FunctionKey,
            StringComparer.Ordinal);
        foreach (var function in Functions)
        {
            BorrowDiagnosticFunctionSnapshot? diagnostic = null;
            _ = diagnosticsByFunction?.TryGetValue(function.FunctionKey, out diagnostic);
            result.AddResult(function.ToBorrowCheckResult(
                loanSignature: diagnostic?.LoanSummary?.Restore()));
        }

        return result;
    }
}

public sealed record BorrowCodegenHintsFunctionSnapshot(
    string FunctionKey,
    string BodyHash,
    string FunctionName,
    int FunctionSymbolId,
    PerceusHintsSnapshot? Perceus,
    ReuseHintsSnapshot? Reuse,
    StackPromotionHintsSnapshot? StackPromotion,
    UnifiedStackPromotionHintsSnapshot? UnifiedStackPromotion)
{
    public static BorrowCodegenHintsFunctionSnapshot FromResult(
        string functionKey,
        string bodyHash,
        BorrowCheckResult result)
    {
        var perceusHints = result.PerceusHints ?? result.PerceusAnalyzer?.Hints;
        var reuseHints = result.ReuseHints ?? result.ReuseAnalyzer?.Hints;
        var stackPromotionHints = result.StackPromotionHints ?? result.StackPromotionAnalyzer?.Hints;
        var unifiedStackPromotionHints = result.UnifiedStackPromotionHints ?? result.UnifiedStackPromotionAnalyzer?.Hints;

        return new BorrowCodegenHintsFunctionSnapshot(
            functionKey,
            bodyHash,
            result.FunctionName,
            result.FunctionSymbolId.Value,
            perceusHints == null ? null : PerceusHintsSnapshot.FromHints(perceusHints),
            reuseHints == null ? null : ReuseHintsSnapshot.FromHints(reuseHints),
            stackPromotionHints == null ? null : StackPromotionHintsSnapshot.FromHints(stackPromotionHints),
            unifiedStackPromotionHints == null ? null : UnifiedStackPromotionHintsSnapshot.FromHints(unifiedStackPromotionHints));
    }

    public BorrowCheckResult ToBorrowCheckResult(
        string? functionName = null,
        SymbolId? functionSymbolId = null,
        LoanSignature? loanSignature = null)
    {
        return new BorrowCheckResult
        {
            FunctionName = functionName ?? FunctionName,
            FunctionSymbolId = functionSymbolId ?? new SymbolId(FunctionSymbolId),
            LoanSignature = loanSignature,
            PerceusHints = Perceus?.ToHints(),
            ReuseHints = Reuse?.ToHints(),
            StackPromotionHints = StackPromotion?.ToHints(),
            UnifiedStackPromotionHints = UnifiedStackPromotion?.ToHints()
        };
    }
}

public sealed record PerceusHintsSnapshot(
    IReadOnlyList<BorrowInstructionSiteSnapshot> OmitDup,
    IReadOnlyList<BorrowInstructionSiteSnapshot> OmitDrop)
{
    public static PerceusHintsSnapshot FromHints(PerceusHints hints) =>
        new(
            SortSites(hints.OmitDup),
            SortSites(hints.OmitDrop));

    public PerceusHints ToHints()
    {
        var hints = new PerceusHints();
        AddSites(hints.OmitDup, OmitDup);
        AddSites(hints.OmitDrop, OmitDrop);
        return hints;
    }

    private static BorrowInstructionSiteSnapshot[] SortSites(IEnumerable<(BlockId Block, int Index)> sites) =>
        sites.Select(static site => BorrowInstructionSiteSnapshot.FromSite(site.Block, site.Index))
            .OrderBy(static site => site.Block)
            .ThenBy(static site => site.Index)
            .ToArray();

    private static void AddSites(HashSet<(BlockId Block, int Index)> target, IEnumerable<BorrowInstructionSiteSnapshot> sites)
    {
        foreach (var site in sites)
        {
            target.Add(site.ToSite());
        }
    }
}

public sealed record ReuseHintsSnapshot(
    IReadOnlyList<BorrowInstructionSlotSnapshot> DropReuseSites,
    IReadOnlyList<BorrowInstructionSlotSnapshot> AllocReuseSites,
    int SlotCount)
{
    public static ReuseHintsSnapshot FromHints(ReuseHints hints) =>
        new(
            SortSlots(hints.DropReuseSites),
            SortSlots(hints.AllocReuseSites),
            hints.SlotCount);

    public ReuseHints ToHints()
    {
        var hints = new ReuseHints { SlotCount = SlotCount };
        AddSlots(hints.DropReuseSites, DropReuseSites);
        AddSlots(hints.AllocReuseSites, AllocReuseSites);
        return hints;
    }

    private static BorrowInstructionSlotSnapshot[] SortSlots(Dictionary<(BlockId Block, int Index), int> sites) =>
        sites.Select(static pair => BorrowInstructionSlotSnapshot.FromSite(pair.Key.Block, pair.Key.Index, pair.Value))
            .OrderBy(static site => site.Block)
            .ThenBy(static site => site.Index)
            .ThenBy(static site => site.Slot)
            .ToArray();

    private static void AddSlots(Dictionary<(BlockId Block, int Index), int> target, IEnumerable<BorrowInstructionSlotSnapshot> sites)
    {
        foreach (var site in sites)
        {
            target[site.ToSite()] = site.Slot;
        }
    }
}

public sealed record StackPromotionHintsSnapshot(
    IReadOnlyList<BorrowInstructionSiteSnapshot> StackAllocSites,
    IReadOnlyList<StackAllocInfoSnapshot> StackAllocInfoByLocal,
    IReadOnlyList<int> PromotedLocals)
{
    public static StackPromotionHintsSnapshot FromHints(StackPromotionHints hints) =>
        new(
            hints.StackAllocSites
                .Select(static site => BorrowInstructionSiteSnapshot.FromSite(site.Block, site.Index))
                .OrderBy(static site => site.Block)
                .ThenBy(static site => site.Index)
                .ToArray(),
            hints.StackAllocInfoByLocal
                .Select(static pair => StackAllocInfoSnapshot.FromInfo(pair.Key, pair.Value))
                .OrderBy(static info => info.Local)
                .ToArray(),
            hints.PromotedLocals
                .Select(static local => local.Value)
                .Order()
                .ToArray());

    public StackPromotionHints ToHints()
    {
        var hints = new StackPromotionHints();
        foreach (var site in StackAllocSites)
        {
            hints.StackAllocSites.Add(site.ToSite());
        }

        foreach (var info in StackAllocInfoByLocal)
        {
            hints.StackAllocInfoByLocal[new LocalId { Value = info.Local }] = info.ToInfo();
        }

        foreach (var local in PromotedLocals)
        {
            hints.PromotedLocals.Add(new LocalId { Value = local });
        }

        return hints;
    }
}

public sealed record UnifiedStackPromotionHintsSnapshot(
    IReadOnlyList<UnifiedStackAllocInfoSnapshot> AllocInfoByLocal,
    IReadOnlyList<int> PromotedLocals)
{
    public static UnifiedStackPromotionHintsSnapshot FromHints(UnifiedStackPromotionHints hints) =>
        new(
            hints.AllocInfoByLocal
                .Select(static pair => UnifiedStackAllocInfoSnapshot.FromInfo(pair.Key, pair.Value))
                .OrderBy(static info => info.Local)
                .ToArray(),
            hints.PromotedLocals
                .Select(static local => local.Value)
                .Order()
                .ToArray());

    public UnifiedStackPromotionHints ToHints()
    {
        var hints = new UnifiedStackPromotionHints();
        foreach (var info in AllocInfoByLocal)
        {
            hints.AllocInfoByLocal[new LocalId { Value = info.Local }] = info.ToInfo();
        }

        foreach (var local in PromotedLocals)
        {
            hints.PromotedLocals.Add(new LocalId { Value = local });
        }

        return hints;
    }
}

public sealed record BorrowInstructionSiteSnapshot(int Block, int Index)
{
    public static BorrowInstructionSiteSnapshot FromSite(BlockId block, int index) => new(block.Value, index);

    public (BlockId Block, int Index) ToSite() => (new BlockId { Value = Block }, Index);
}

public sealed record BorrowInstructionSlotSnapshot(int Block, int Index, int Slot)
{
    public static BorrowInstructionSlotSnapshot FromSite(BlockId block, int index, int slot) => new(block.Value, index, slot);

    public (BlockId Block, int Index) ToSite() => (new BlockId { Value = Block }, Index);
}

public sealed record StackAllocInfoSnapshot(int Local, int FieldCount, int TypeId, long PayloadSize)
{
    public static StackAllocInfoSnapshot FromInfo(LocalId local, StackAllocInfo info) =>
        new(local.Value, info.FieldCount, info.TypeId, info.PayloadSize);

    public StackAllocInfo ToInfo() => new(FieldCount, TypeId, PayloadSize);
}

public sealed record UnifiedStackAllocInfoSnapshot(
    int Local,
    string Kind,
    BorrowInstructionSiteSnapshot Site,
    int TargetLocal,
    IReadOnlyList<int> RcFieldIndices,
    int TypeId,
    int FieldCount,
    long PayloadSize,
    string? InvokeFunctionName,
    string? ReleaseFunctionName,
    IReadOnlyList<int>? CapturedTypeIds)
{
    public static UnifiedStackAllocInfoSnapshot FromInfo(LocalId local, UnifiedStackAllocInfo info) =>
        new(
            local.Value,
            info.Kind.ToString(),
            BorrowInstructionSiteSnapshot.FromSite(info.Site.Block, info.Site.Index),
            info.TargetLocal.Value,
            info.RcFieldIndices.Order().ToArray(),
            info.TypeId,
            info.FieldCount,
            info.PayloadSize,
            info.InvokeFunctionName,
            info.ReleaseFunctionName,
            info.CapturedTypeIds?.Select(static typeId => typeId.Value).ToArray());

    public UnifiedStackAllocInfo ToInfo()
    {
        var kind = Enum.TryParse<PromotableAllocationKind>(Kind, out var parsedKind)
            ? parsedKind
            : PromotableAllocationKind.AdtConstructor;
        var site = Site.ToSite();
        return new UnifiedStackAllocInfo(
            kind,
            site,
            new LocalId { Value = TargetLocal },
            RcFieldIndices.ToList())
        {
            TypeId = TypeId,
            FieldCount = FieldCount,
            PayloadSize = PayloadSize,
            InvokeFunctionName = InvokeFunctionName,
            ReleaseFunctionName = ReleaseFunctionName,
            CapturedTypeIds = CapturedTypeIds?.Select(static id => new TypeId(id)).ToList()
        };
    }
}
