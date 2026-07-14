namespace Eidosc.Pipeline;

using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Symbols;

public sealed record AstNamerStatePayload(
    string SchemaVersion,
    string ModuleKey,
    string ModuleIdentityKey,
    IReadOnlyList<string> SourcePaths,
    int AstNodeCount,
    string AstStructureHash,
    IReadOnlyList<AstNamerStateEntryPayload> Entries,
    string Hash)
{
    public const string CurrentSchemaVersion = "ast-namer-state-payload-v5";

    public static AstNamerStatePayload Create(
        ModuleDecl? ast,
        string moduleKey = "",
        string moduleIdentityKey = "",
        IReadOnlyList<string>? sourcePaths = null) =>
        Create(ast, moduleKey, moduleIdentityKey, sourcePaths, allStableNodes: null);

    internal static AstNamerStatePayload Create(
        ModuleDecl? ast,
        string moduleKey,
        string moduleIdentityKey,
        IReadOnlyList<string>? sourcePaths,
        IReadOnlyList<AstStableNodeEntry>? allStableNodes)
    {
        var stableNodes = ast == null
            ? []
            : string.IsNullOrWhiteSpace(moduleKey)
                ? allStableNodes ?? AstStableNodeTraversal.Enumerate(ast)
                : AstStableNodeTraversal.EnumerateModule(
                    allStableNodes ?? AstStableNodeTraversal.Enumerate(ast),
                    moduleKey,
                    moduleIdentityKey,
                    sourcePaths);
        var entries = stableNodes
            .Select(static entry => AstNamerStateEntryPayload.Create(entry.Node, entry.StableIdentity))
            .Where(static entry => entry.HasRestorableState())
            .ToArray();

        var payload = new AstNamerStatePayload(
            CurrentSchemaVersion,
            moduleKey,
            moduleIdentityKey,
            sourcePaths?.ToArray() ?? [],
            stableNodes.Count,
            AstInferredTypeMapPayload.ComputeStructureHash(stableNodes),
            entries,
            "");
        return payload with { Hash = ComputeHash(payload) };
    }

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ComputeHash(this), StringComparison.Ordinal);

    internal AstNamerStatePayload RemapSymbolIds(IReadOnlyDictionary<int, int> symbolIdMap)
    {
        var remapped = this with
        {
            Entries = Entries.Select(entry => entry.RemapSymbolIds(symbolIdMap)).ToArray(),
            Hash = ""
        };
        return remapped with { Hash = ComputeHash(remapped) };
    }

    private static string ComputeHash(AstNamerStatePayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" });
}

public sealed record AstNamerStateEntryPayload(
    AstInferredTypeStableKeyPayload StableIdentity,
    int SymbolId,
    bool? IsConstructor,
    IReadOnlyList<int>? IdentifierValueCandidateSymbolIds,
    IReadOnlyList<int>? PathValueCandidateSymbolIds,
    int? FunctionSymbolId,
    IReadOnlyList<int>? FunctionCandidateSymbolIds,
    int? EvidenceSymbolId,
    int? TargetSymbolId,
    int? ProofIntroSymbolId,
    IReadOnlyList<int>? MethodCandidateSymbolIds,
    int? ResolvedModule,
    IReadOnlyList<AstImportedSymbolPayload>? ResolvedSymbols,
    IReadOnlyList<int>? EffectSymbolIds)
{
    public static AstNamerStateEntryPayload Create(
        EidosAstNode node,
        AstInferredTypeStableKeyPayload stableIdentity) =>
        new(
            stableIdentity,
            node.SymbolId.Value,
            node is IdentifierExpr identifier ? identifier.IsConstructor : null,
            node is IdentifierExpr identifierCandidates
                ? identifierCandidates.ValueCandidateSymbolIds.Select(static id => id.Value).ToArray()
                : null,
            node is PathExpr pathCandidates
                ? pathCandidates.ValueCandidateSymbolIds.Select(static id => id.Value).ToArray()
                : null,
            node is InfixCallExpr infix ? infix.FunctionSymbolId.Value : null,
            node is InfixCallExpr infixCandidates
                ? infixCandidates.FunctionCandidateSymbolIds.Select(static id => id.Value).ToArray()
                : null,
            node is GivenExpr given ? given.EvidenceSymbolId.Value : null,
            node is Assignment assignment ? assignment.TargetSymbolId.Value : null,
            node is ProofTermClause proof ? proof.IntroSymbolId.Value : null,
            node is MethodCallExpr method
                ? method.MethodCandidateSymbolIds.Select(static id => id.Value).ToArray()
                : null,
            node is ImportDecl import ? import.ResolvedModule.Value : null,
            node is ImportDecl imported
                ? imported.ResolvedSymbols.Select(AstImportedSymbolPayload.Create).ToArray()
                : null,
            node is EffectfulType effectful
                ? effectful.EffectSymbolIds.Select(static id => id.Value).ToArray()
                : null);

    internal AstNamerStateEntryPayload RemapSymbolIds(IReadOnlyDictionary<int, int> symbolIdMap) =>
        this with
        {
            SymbolId = Remap(SymbolId, symbolIdMap),
            IdentifierValueCandidateSymbolIds = RemapList(IdentifierValueCandidateSymbolIds, symbolIdMap),
            PathValueCandidateSymbolIds = RemapList(PathValueCandidateSymbolIds, symbolIdMap),
            FunctionSymbolId = RemapOptional(FunctionSymbolId, symbolIdMap),
            FunctionCandidateSymbolIds = RemapList(FunctionCandidateSymbolIds, symbolIdMap),
            EvidenceSymbolId = RemapOptional(EvidenceSymbolId, symbolIdMap),
            TargetSymbolId = RemapOptional(TargetSymbolId, symbolIdMap),
            ProofIntroSymbolId = RemapOptional(ProofIntroSymbolId, symbolIdMap),
            MethodCandidateSymbolIds = RemapList(MethodCandidateSymbolIds, symbolIdMap),
            ResolvedModule = RemapOptional(ResolvedModule, symbolIdMap),
            ResolvedSymbols = ResolvedSymbols?
                .Select(symbol => symbol with { SymbolId = Remap(symbol.SymbolId, symbolIdMap) })
                .ToArray(),
            EffectSymbolIds = RemapList(EffectSymbolIds, symbolIdMap)
        };

    internal bool HasRestorableState() =>
        SymbolId > 0 ||
        IsConstructor == true ||
        IdentifierValueCandidateSymbolIds is { Count: > 0 } ||
        PathValueCandidateSymbolIds is { Count: > 0 } ||
        FunctionSymbolId is > 0 ||
        FunctionCandidateSymbolIds is { Count: > 0 } ||
        EvidenceSymbolId is > 0 ||
        TargetSymbolId is > 0 ||
        ProofIntroSymbolId is > 0 ||
        MethodCandidateSymbolIds is { Count: > 0 } ||
        ResolvedModule is > 0 ||
        ResolvedSymbols is { Count: > 0 } ||
        EffectSymbolIds is { Count: > 0 };

    private static int Remap(int value, IReadOnlyDictionary<int, int> symbolIdMap) =>
        value < 0 ? value : symbolIdMap.GetValueOrDefault(value, value);

    private static int? RemapOptional(int? value, IReadOnlyDictionary<int, int> symbolIdMap) =>
        value.HasValue ? Remap(value.Value, symbolIdMap) : null;

    private static IReadOnlyList<int>? RemapList(
        IReadOnlyList<int>? values,
        IReadOnlyDictionary<int, int> symbolIdMap) =>
        values?.Select(value => Remap(value, symbolIdMap)).ToArray();
}

public sealed record AstImportedSymbolPayload(
    string Name,
    int SymbolId,
    ResolutionKind Kind,
    bool IsAliased,
    bool IsImplicitModuleMember,
    bool IsTraitMethod)
{
    public static AstImportedSymbolPayload Create(ImportedSymbol symbol) =>
        new(
            symbol.Name,
            symbol.SymbolId.Value,
            symbol.Kind,
            symbol.IsAliased,
            symbol.IsImplicitModuleMember,
            symbol.IsTraitMethod);
}

public sealed record AstNamerStateRestoreResult(
    bool Applied,
    int RestoredNodes,
    IReadOnlyList<string> Failures)
{
    public static AstNamerStateRestoreResult Failed(params string[] failures) =>
        new(false, 0, failures);
}

public static class AstNamerStateRestorer
{
    public static AstNamerStateRestoreResult Restore(
        ModuleDecl ast,
        IReadOnlyList<AstNamerStatePayload> payloads,
        LiveStateRemapPlan remapPlan,
        SymbolTable symbolTable)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(remapPlan);
        ArgumentNullException.ThrowIfNull(symbolTable);

        if (payloads.Count == 0)
        {
            return AstNamerStateRestoreResult.Failed("missing AST Namer state payload");
        }

        if (payloads.Any(payload =>
                payload.SchemaVersion != AstNamerStatePayload.CurrentSchemaVersion ||
                !payload.HasValidHash() ||
                payload.AstNodeCount < payload.Entries.Count ||
                string.IsNullOrWhiteSpace(payload.AstStructureHash)))
        {
            return AstNamerStateRestoreResult.Failed("invalid AST Namer state payload");
        }

        var rawEntries = payloads
            .OrderBy(static payload => payload.ModuleIdentityKey, StringComparer.Ordinal)
            .ThenBy(static payload => payload.ModuleKey, StringComparer.Ordinal)
            .SelectMany(static payload => payload.Entries)
            .ToArray();
        var conflictingEntryKeys = rawEntries
            .GroupBy(static entry => entry.StableIdentity.StableKey, StringComparer.Ordinal)
            .Where(static group => group
                .Select(ModuleArtifactHash.ComputeJsonHash)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any())
            .Select(static group => group.Key)
            .ToArray();
        if (conflictingEntryKeys.Length > 0)
        {
            return AstNamerStateRestoreResult.Failed(
                conflictingEntryKeys.Select(static key => $"conflicting AST Namer state key: {key}").ToArray());
        }

        var entries = rawEntries
            .GroupBy(static entry => entry.StableIdentity.StableKey, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

        var failures = new List<string>();
        var currentStableNodes = AstStableNodeTraversal.Enumerate(ast);
        var currentNodes = BuildCurrentNodeLookup(currentStableNodes, failures);
        var payloadStructureKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            var moduleNodes = string.IsNullOrWhiteSpace(payload.ModuleKey)
                ? currentStableNodes
                : AstStableNodeTraversal.EnumerateModule(
                    currentStableNodes,
                    payload.ModuleKey,
                    payload.ModuleIdentityKey,
                    payload.SourcePaths);
            if (moduleNodes.Count != payload.AstNodeCount ||
                !string.Equals(
                    AstInferredTypeMapPayload.ComputeStructureHash(moduleNodes),
                    payload.AstStructureHash,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    $"AST Namer structure mismatch: {payload.ModuleKey} " +
                    $"expected-count={payload.AstNodeCount} actual-count={moduleNodes.Count} " +
                    $"expected-hash={payload.AstStructureHash} " +
                    $"actual-hash={AstInferredTypeMapPayload.ComputeStructureHash(moduleNodes)}");
                continue;
            }

            foreach (var moduleNode in moduleNodes)
            {
                payloadStructureKeys.Add(moduleNode.StableIdentity.StableKey);
            }
        }

        if (failures.Count > 0 || currentNodes.Count != payloadStructureKeys.Count)
        {
            failures.Add(
                $"AST Namer state node count mismatch: expected {payloadStructureKeys.Count}, actual {currentNodes.Count}");
            return new AstNamerStateRestoreResult(false, 0, failures);
        }

        var symbolRemap = remapPlan.Symbols.ToDictionary(
            static entry => entry.From,
            static entry => new SymbolId(entry.To));
        var pending = new List<PendingAstNamerState>(entries.Length);
        foreach (var entry in entries)
        {
            if (!currentNodes.TryGetValue(entry.StableIdentity.StableKey, out var node))
            {
                failures.Add($"missing AST Namer state node: {entry.StableIdentity.StableKey}");
                continue;
            }

            if (!TryMapEntry(entry, node, symbolRemap, symbolTable, out var mapped, out var failure))
            {
                failures.Add(failure);
                continue;
            }

            pending.Add(mapped);
        }

        if (failures.Count > 0)
        {
            return new AstNamerStateRestoreResult(false, 0, failures);
        }

        foreach (var state in pending)
        {
            Apply(state);
        }

        return new AstNamerStateRestoreResult(true, pending.Count, []);
    }

    private static Dictionary<string, EidosAstNode> BuildCurrentNodeLookup(
        IReadOnlyList<AstStableNodeEntry> stableNodes,
        List<string> failures)
    {
        var result = new Dictionary<string, EidosAstNode>(StringComparer.Ordinal);
        foreach (var entry in stableNodes)
        {
            if (!result.TryAdd(entry.StableIdentity.StableKey, entry.Node))
            {
                failures.Add($"duplicate AST Namer state key: {entry.StableIdentity.StableKey}");
            }
        }

        return result;
    }

    private static bool TryMapEntry(
        AstNamerStateEntryPayload entry,
        EidosAstNode node,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        SymbolTable symbolTable,
        out PendingAstNamerState mapped,
        out string failure)
    {
        mapped = null!;
        failure = "";
        if (!TryMapSymbol(entry.SymbolId, symbolRemap, symbolTable, out var symbolId) ||
            !TryMapOptionalSymbol(entry.FunctionSymbolId, symbolRemap, symbolTable, out var functionSymbolId) ||
            !TryMapSymbols(entry.IdentifierValueCandidateSymbolIds, symbolRemap, symbolTable, out var identifierCandidates) ||
            !TryMapSymbols(entry.PathValueCandidateSymbolIds, symbolRemap, symbolTable, out var pathCandidates) ||
            !TryMapSymbols(entry.FunctionCandidateSymbolIds, symbolRemap, symbolTable, out var functionCandidates) ||
            !TryMapOptionalSymbol(entry.EvidenceSymbolId, symbolRemap, symbolTable, out var evidenceSymbolId) ||
            !TryMapOptionalSymbol(entry.TargetSymbolId, symbolRemap, symbolTable, out var targetSymbolId) ||
            !TryMapOptionalSymbol(entry.ProofIntroSymbolId, symbolRemap, symbolTable, out var proofIntroSymbolId) ||
            !TryMapOptionalSymbol(entry.ResolvedModule, symbolRemap, symbolTable, out var resolvedModule) ||
            !TryMapSymbols(entry.MethodCandidateSymbolIds, symbolRemap, symbolTable, out var methodCandidates) ||
            !TryMapSymbols(entry.EffectSymbolIds, symbolRemap, symbolTable, out var effectSymbols) ||
            !TryMapImportedSymbols(entry.ResolvedSymbols, symbolRemap, symbolTable, out var importedSymbols))
        {
            failure = $"failed to remap AST Namer state node: {entry.StableIdentity.StableKey}";
            return false;
        }

        mapped = new PendingAstNamerState(
            node,
            symbolId,
            entry.IsConstructor,
            identifierCandidates,
            pathCandidates,
            functionSymbolId,
            functionCandidates,
            evidenceSymbolId,
            targetSymbolId,
            proofIntroSymbolId,
            methodCandidates,
            resolvedModule,
            importedSymbols,
            effectSymbols);
        return true;
    }

    private static bool TryMapOptionalSymbol(
        int? value,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        SymbolTable symbolTable,
        out SymbolId? mapped)
    {
        mapped = null;
        if (!value.HasValue)
        {
            return true;
        }

        if (!TryMapSymbol(value.Value, symbolRemap, symbolTable, out var symbolId))
        {
            return false;
        }

        mapped = symbolId;
        return true;
    }

    private static bool TryMapSymbols(
        IReadOnlyList<int>? values,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        SymbolTable symbolTable,
        out IReadOnlyList<SymbolId>? mapped)
    {
        mapped = null;
        if (values == null)
        {
            return true;
        }

        var result = new List<SymbolId>(values.Count);
        foreach (var value in values)
        {
            if (!TryMapSymbol(value, symbolRemap, symbolTable, out var symbolId))
            {
                return false;
            }

            result.Add(symbolId);
        }

        mapped = result;
        return true;
    }

    private static bool TryMapImportedSymbols(
        IReadOnlyList<AstImportedSymbolPayload>? values,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        SymbolTable symbolTable,
        out IReadOnlyList<ImportedSymbol>? mapped)
    {
        mapped = null;
        if (values == null)
        {
            return true;
        }

        var result = new List<ImportedSymbol>(values.Count);
        foreach (var value in values)
        {
            if (!TryMapSymbol(value.SymbolId, symbolRemap, symbolTable, out var symbolId))
            {
                return false;
            }

            result.Add(new ImportedSymbol
            {
                Name = value.Name,
                SymbolId = symbolId,
                Kind = value.Kind,
                IsAliased = value.IsAliased,
                IsImplicitModuleMember = value.IsImplicitModuleMember,
                IsTraitMethod = value.IsTraitMethod
            });
        }

        mapped = result;
        return true;
    }

    private static bool TryMapSymbol(
        int value,
        IReadOnlyDictionary<int, SymbolId> symbolRemap,
        SymbolTable symbolTable,
        out SymbolId mapped)
    {
        if (value < 0)
        {
            mapped = SymbolId.None;
            return true;
        }

        if (symbolRemap.TryGetValue(value, out mapped))
        {
            return true;
        }

        mapped = new SymbolId(value);
        return symbolTable.GetSymbol(mapped) != null;
    }

    private static void Apply(PendingAstNamerState state)
    {
        state.Node.SymbolId = state.SymbolId;
        if (state.Node is IdentifierExpr identifier && state.IsConstructor.HasValue)
        {
            identifier.IsConstructor = state.IsConstructor.Value;
            identifier.ClearValueCandidates();
            foreach (var candidate in state.IdentifierValueCandidateSymbolIds ?? [])
            {
                identifier.AddValueCandidate(candidate);
            }
        }

        if (state.Node is PathExpr path && state.PathValueCandidateSymbolIds != null)
        {
            path.ClearValueCandidates();
            foreach (var candidate in state.PathValueCandidateSymbolIds)
            {
                path.AddValueCandidate(candidate);
            }
        }

        if (state.Node is InfixCallExpr infix && state.FunctionSymbolId.HasValue)
        {
            infix.FunctionSymbolId = state.FunctionSymbolId.Value;
            infix.ClearFunctionCandidates();
            foreach (var candidate in state.FunctionCandidateSymbolIds ?? [])
            {
                infix.AddFunctionCandidate(candidate);
            }
        }

        if (state.Node is GivenExpr given && state.EvidenceSymbolId.HasValue)
        {
            given.EvidenceSymbolId = state.EvidenceSymbolId.Value;
        }

        if (state.Node is Assignment assignment && state.TargetSymbolId.HasValue)
        {
            assignment.TargetSymbolId = state.TargetSymbolId.Value;
        }

        if (state.Node is ProofTermClause proof && state.ProofIntroSymbolId.HasValue)
        {
            proof.SetIntroSymbolId(state.ProofIntroSymbolId.Value);
        }

        if (state.Node is MethodCallExpr method && state.MethodCandidateSymbolIds != null)
        {
            method.ClearMethodCandidates();
            foreach (var candidate in state.MethodCandidateSymbolIds)
            {
                method.AddMethodCandidate(candidate);
            }
        }

        if (state.Node is ImportDecl import &&
            state.ResolvedModule.HasValue &&
            state.ResolvedSymbols != null)
        {
            import.ResolvedModule = state.ResolvedModule.Value;
            import.ResolvedSymbols = state.ResolvedSymbols.ToList();
        }

        if (state.Node is EffectfulType effectful && state.EffectSymbolIds != null)
        {
            effectful.EffectSymbolIds = state.EffectSymbolIds.ToList();
        }
    }

    private sealed record PendingAstNamerState(
        EidosAstNode Node,
        SymbolId SymbolId,
        bool? IsConstructor,
        IReadOnlyList<SymbolId>? IdentifierValueCandidateSymbolIds,
        IReadOnlyList<SymbolId>? PathValueCandidateSymbolIds,
        SymbolId? FunctionSymbolId,
        IReadOnlyList<SymbolId>? FunctionCandidateSymbolIds,
        SymbolId? EvidenceSymbolId,
        SymbolId? TargetSymbolId,
        SymbolId? ProofIntroSymbolId,
        IReadOnlyList<SymbolId>? MethodCandidateSymbolIds,
        SymbolId? ResolvedModule,
        IReadOnlyList<ImportedSymbol>? ResolvedSymbols,
        IReadOnlyList<SymbolId>? EffectSymbolIds);
}
