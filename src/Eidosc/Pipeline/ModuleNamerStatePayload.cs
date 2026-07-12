namespace Eidosc.Pipeline;

using Eidosc.Ast.Declarations;
using Eidosc.Symbols;

public sealed record ModuleNamerStatePayload(
    string SchemaVersion,
    string ModuleKey,
    string ModuleIdentityKey,
    IReadOnlyList<LiveStateSymbolIdentity> SymbolIdentities,
    SymbolTablePayload SymbolTable,
    ModuleRegistryPayload ModuleRegistry,
    AstNamerStatePayload AstState,
    ProjectModuleMemberIndexNode MemberIndex,
    string ExportSurfaceHash,
    string DependencyIndexHash,
    string PayloadHash)
{
    public const string CurrentSchemaVersion = "module-namer-state-payload-v6";

    public static ModuleNamerStatePayload Create(
        string moduleKey,
        SymbolTable symbolTable,
        ProjectModuleMemberIndexSnapshot memberIndexSnapshot,
        ProjectModuleGraphSnapshot? graph = null,
        ModuleDecl? ast = null) =>
        Create(
            moduleKey,
            symbolTable,
            memberIndexSnapshot,
            graph,
            ast,
            allStableNodes: null,
            allSymbolIdentities: null,
            fullSymbolTablePayload: null,
            fullModuleRegistryPayload: null);

    internal static ModuleNamerStatePayload Create(
        string moduleKey,
        SymbolTable symbolTable,
        ProjectModuleMemberIndexSnapshot memberIndexSnapshot,
        ProjectModuleGraphSnapshot? graph,
        ModuleDecl? ast,
        IReadOnlyList<AstStableNodeEntry>? allStableNodes,
        IReadOnlyList<LiveStateSymbolIdentity>? allSymbolIdentities,
        SymbolTablePayload? fullSymbolTablePayload,
        ModuleRegistryPayload? fullModuleRegistryPayload)
    {
        var graphNode = graph?.Nodes.FirstOrDefault(node =>
            string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal));
        var usesGraphFallback = false;
        if (graphNode == null && graph?.Nodes.Count == 1)
        {
            graphNode = graph.Nodes[0];
            usesGraphFallback = true;
        }

        var memberIndex = memberIndexSnapshot.Nodes.FirstOrDefault(node =>
            string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal));
        memberIndex ??= memberIndexSnapshot.Nodes.FirstOrDefault(node =>
            string.Equals(node.ModuleKey, WellKnownStrings.SpecialNames.Main, StringComparison.OrdinalIgnoreCase));
        memberIndex ??= memberIndexSnapshot.Nodes
            .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
            .FirstOrDefault();
        if (memberIndex == null)
        {
            throw new ArgumentException($"Module '{moduleKey}' is missing from the member index snapshot.", nameof(moduleKey));
        }

        var sourcePaths = graphNode?.SourcePaths ?? [];
        var allIdentities = allSymbolIdentities ??
                            LiveStateStableIdentityBuilder.BuildSymbolIdentities(symbolTable, graph);
        var astModuleKey = usesGraphFallback ? graphNode!.ModuleKey : moduleKey;
        var astModuleIdentityKey = usesGraphFallback
            ? ModulePayloadSymbolSlicer.ResolveModuleIdentityKey(symbolTable, graphNode!.ModuleKey)
            : memberIndex.ModuleIdentityKey;
        var astState = AstNamerStatePayload.Create(
            ast,
            astModuleKey,
            astModuleIdentityKey,
            sourcePaths,
            allStableNodes);
        var allowedSymbolIds = ModulePayloadSymbolSlicer.CreateNamerSymbolClosure(
            symbolTable,
            allIdentities,
            astState,
            memberIndex.ModuleIdentityKey,
            sourcePaths);
        var symbolTablePayload = ModulePayloadSymbolSlicer.SliceSymbolTable(
            fullSymbolTablePayload ?? SymbolTablePayload.Create(symbolTable),
            allowedSymbolIds);
        var moduleRegistryPayload = ModulePayloadSymbolSlicer.SliceModuleRegistry(
            fullModuleRegistryPayload ?? ModuleRegistryPayload.Create(symbolTable.Modules),
            allowedSymbolIds);
        var identities = allIdentities
            .Where(identity => allowedSymbolIds.Contains(identity.SymbolId))
            .OrderBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
            .ToArray();
        var payload = new ModuleNamerStatePayload(
            CurrentSchemaVersion,
            moduleKey,
            memberIndex.ModuleIdentityKey,
            identities,
            symbolTablePayload,
            moduleRegistryPayload,
            astState,
            memberIndex,
            memberIndex.LocalIndexHash,
            memberIndex.DependencyIndexHash,
            "");

        return payload with { PayloadHash = ComputeHash(payload) };
    }

    public ModuleNamerStateValidationResult ValidateAgainst(
        ModuleNamerStatePayload current)
    {
        var failures = new List<string>();
        AddFailureIfDifferent(failures, nameof(SchemaVersion), SchemaVersion, current.SchemaVersion);
        AddFailureIfDifferent(failures, nameof(ModuleKey), ModuleKey, current.ModuleKey);
        AddFailureIfDifferent(failures, nameof(ModuleIdentityKey), ModuleIdentityKey, current.ModuleIdentityKey);
        AddFailureIfDifferent(failures, nameof(ExportSurfaceHash), ExportSurfaceHash, current.ExportSurfaceHash);
        AddFailureIfDifferent(failures, nameof(DependencyIndexHash), DependencyIndexHash, current.DependencyIndexHash);

        var remap = LiveStateStableIdentityBuilder.PlanRemap(SymbolIdentities, current.SymbolIdentities);
        failures.AddRange(remap.Failures);

        return new ModuleNamerStateValidationResult(
            failures.Count == 0,
            failures.Count == 0 ? LiveStateRemapPlan.FromResolution(remap) : null,
            failures);
    }

    public bool HasValidPayloadHash() =>
        !string.IsNullOrWhiteSpace(PayloadHash) &&
        string.Equals(PayloadHash, ComputeHash(this), StringComparison.Ordinal);

    private static void AddFailureIfDifferent(List<string> failures, string field, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            failures.Add($"{field}: expected {expected}, actual {actual}");
        }
    }

    private static string ComputeHash(ModuleNamerStatePayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { PayloadHash = "" });
}

public sealed record ModuleNamerStateValidationResult(
    bool IsValid,
    LiveStateRemapPlan? RemapPlan,
    IReadOnlyList<string> Failures);
