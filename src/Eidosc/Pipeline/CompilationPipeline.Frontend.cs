using System.Collections.Concurrent;
using Eidosc.Symbols;
using Eidosc.ProjectSystem;
using Eidosc.Parsing.Handwritten;
using Eidosc.Semantic;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private bool RunParser()
    {
        return RunHandwrittenParser();
    }

    private bool RunNameResolver()
    {
        if (TryRestoreLiveState(CompilationPhase.Namer))
        {
            return true;
        }

        if (TryRestoreNamerFromModulePayloads())
        {
            StoreLiveState(CompilationPhase.Namer);
            return true;
        }

        using (MeasureSubphase(CompilationPhase.Namer, "create_resolver"))
        {
            _symbolTable = new SymbolTable();
            _nameResolver = new NameResolver(_symbolTable, _sourceCode, _options.ImportSearchRoots)
            {
                UsePrecompiledImportSignatureOnly = ShouldUsePrecompiledImportSignaturesOnly(),
                PreviousImplOverlapCheckSnapshot = _options.PreviousImplOverlapCheckSnapshot
            };
        }

        bool resolveSuccess;
        using (MeasureSubphase(CompilationPhase.Namer, "resolve"))
        {
            resolveSuccess = _nameResolver.Resolve(_ast!);
        }
        BuildModuleMemberIndexSnapshot();
        _implOverlapCheckSnapshot = _nameResolver.CreateImplOverlapCheckSnapshot();
        SetProfilingCounter("Namer.implOverlapSnapshot.entries", _implOverlapCheckSnapshot.Entries.Count);
        AddProfilingCounters(_nameResolver.GetProfilingCounters());

        var diagnostics = FilterTrustedPrecompiledDiagnostics(_nameResolver.Diagnostics).ToList();
        using (MeasureSubphase(CompilationPhase.Namer, "collect_diagnostics"))
        {
            _diagnostics.AddRange(diagnostics);
        }

        // 合并 eidos.toml 中的 FFI 链接库
        if (_options.ConfigFfiLibraries.Length > 0 && _nameResolver != null)
        {
            _nameResolver.AddConfigLinkLibraries(_options.ConfigFfiLibraries);
        }

        if (_debugContext.IsEnabled)
        {
            using (MeasureSubphase(CompilationPhase.Namer, "debug_emit"))
            {
                _debugContext.Emit("symbols", SemanticFormatter.FormatSymbols(_symbolTable));
                _debugContext.Emit("scopes", SemanticFormatter.FormatScopes(_symbolTable));
            }
        }

        if (resolveSuccess || !diagnostics.Any(diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error))
        {
            _moduleNamerStatePayloads = ShouldCreateModuleStatePayloads()
                ? CreateModuleNamerStatePayloads()
                : null;
            StoreLiveState(CompilationPhase.Namer);
            return true;
        }

        // IDE/LSP type snapshots should keep going with the partial symbol table so later clean
        // declarations can still expose reliable hover/inlay types.
        return _options.StopAtPhase == CompilationPhase.Types;
    }

    private bool RunHandwrittenParser()
    {
        var (ast, diagnostics) = SyntaxParser.Parse(_tokens!, GetPrimarySourceName(), _options.LanguageVersion);
        _diagnostics.AddRange(diagnostics);

        if (ast == null) return false;

        _ast = ast;
        ApplyPackageInstanceKeyToModuleTree(_ast, BuildCurrentPackageInstanceKey());

        if (_debugContext.IsEnabled)
        {
            using (MeasureSubphase(CompilationPhase.Parser, "debug_emit"))
            {
                _debugContext.Emit("ast", PhaseOutput.FormatAst(_ast));
            }
        }

        return true;
    }

    private bool ShouldUsePrecompiledImportSignaturesOnly()
    {
        return _options.StopAtPhase == CompilationPhase.Types;
    }

    private bool TryRestoreNamerFromModulePayloads()
    {
        var previousPayloads = _options.PreviousModuleNamerStatePayloads ?? [];
        if (_ast == null ||
            (previousPayloads.Count == 0 && _options.ModuleNamerStatePayloadLoader == null))
        {
            return false;
        }

        using (MeasureSubphase(CompilationPhase.Namer, "module_namer_restore_prepare"))
        {
            EnsureNamerModulePlansForRestore();
        }

        if (_moduleArtifactRestorePlan == null ||
            _moduleSemanticSignatureSnapshot == null)
        {
            return false;
        }

        var payloadByModule = BuildNamerPayloadLookup(previousPayloads);
        var semanticByModule = BuildNamerSemanticLookup(_moduleSemanticSignatureSnapshot);
        var syntheticPayload = ProjectModuleArtifactRestorePayloadSnapshot.LoadSemantic(
            _moduleArtifactRestorePlan,
            _moduleSemanticSignatureSnapshot,
            (moduleKey, _, _, _) => TryGetNamerPayload(moduleKey, payloadByModule, previousPayloads, semanticByModule, out _) &&
                                    semanticByModule.TryGetValue(moduleKey, out var semantic)
                ? semantic
                : null);
        _moduleArtifactRestorePayload = syntheticPayload;
        _moduleArtifactRestorePlan = _moduleArtifactRestorePlan.GateWithPayload(syntheticPayload);
        _moduleArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
            _moduleArtifactRestorePlan,
            ProjectModuleDependencySignatureRequirement.SemanticOnly);

        var restoredPayloads = new ConcurrentDictionary<string, ModuleNamerStatePayload>(StringComparer.Ordinal);
        var compiledPayloads = new ConcurrentDictionary<string, ModuleNamerStatePayload>(StringComparer.Ordinal);
        var compiledSupplementalPayloads = new ConcurrentDictionary<string, ModuleNamerStatePayload>(StringComparer.Ordinal);
        var moduleCompilations = new ConcurrentDictionary<string, Lazy<CompilationResult>>(StringComparer.Ordinal);
        _moduleArtifactRestoreExecution = ProjectModuleArtifactRestoreExecutor.ExecuteAsync(
                _moduleArtifactRestorePlan,
                (item, cancellationToken) =>
                    RestoreNamerPayloadModuleAsync(
                        item,
                        payloadByModule,
                        previousPayloads,
                        semanticByModule,
                        restoredPayloads,
                        cancellationToken),
                (item, cancellationToken) => CompileNamerPayloadModuleAsync(
                    item,
                    compiledPayloads,
                    compiledSupplementalPayloads,
                    moduleCompilations,
                    cancellationToken),
                _moduleArtifactRestorePayload,
                _moduleDependencySignatureSnapshot,
                _options.PreviousModuleDependencySignatureSnapshot,
                ProjectModuleDependencySignatureRequirement.SemanticOnly,
                maxDegreeOfParallelism: GetModuleArtifactRestoreMaxDegreeOfParallelism(_moduleArtifactRestorePlan))
            .GetAwaiter()
            .GetResult();

        SetModuleArtifactRestoreCounters("Build.moduleArtifactRestore", _moduleArtifactRestorePlan);
        SetModuleArtifactRestoreExecutionCounters(
            "Build.moduleArtifactRestoreExecution",
            _moduleArtifactRestoreExecution);
        SetModuleArtifactRestorePayloadCounters(
            "Build.moduleArtifactRestorePayload",
            _moduleArtifactRestorePayload);

        if (_moduleArtifactRestoreExecution.FailedModules > 0 ||
            _moduleArtifactRestoreExecution.BlockedModules > 0 ||
            (_moduleArtifactRestoreExecution.CompiledModules > 0 &&
             compiledPayloads.Count != _moduleArtifactRestoreExecution.CompiledModules) ||
            (_moduleArtifactRestoreExecution.RestoredModules > 0 &&
             restoredPayloads.Count != _moduleArtifactRestoreExecution.RestoredModules) ||
            _moduleArtifactRestoreExecution.CompiledModules +
            _moduleArtifactRestoreExecution.RestoredModules == 0)
        {
            SetNamerModuleRestoreFallbackCounters(_moduleArtifactRestoreExecution);
            return false;
        }

        var mergePayloads = ExpandNamerPayloadsForMerge(
            restoredPayloads
                .Concat(compiledPayloads)
                .Concat(compiledSupplementalPayloads)
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => entry.Value)
                .ToArray(),
            previousPayloads,
            semanticByModule);
        SetProfilingCounter("Namer.moduleRestore.mergePayloadModules", mergePayloads.Count);
        NamerStateMergeResult merge;
        using (MeasureSubphase(CompilationPhase.Namer, "module_namer_restore_merge"))
        {
            merge = NamerStateMerger.Merge(mergePayloads);
        }
        var restored = merge.BuildResult;
        if (!merge.IsApplied || restored?.SymbolTable == null)
        {
            SetNamerModuleRestoreFallbackCounters(_moduleArtifactRestoreExecution);
            SetProfilingCounter("Namer.moduleRestore.fallbackBuildFailures", merge.Failures.Count);
            foreach (var group in merge.Failures
                         .Select(static failure =>
                         {
                             var separator = failure.IndexOf(':', StringComparison.Ordinal);
                             return separator < 0 ? failure : failure[..separator];
                         })
                         .GroupBy(static failure => failure, StringComparer.Ordinal)
                         .OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                SetProfilingCounter(
                    $"Namer.moduleRestore.fallbackBuildFailure.{group.Key}",
                    group.LongCount());
            }

            return false;
        }

        AstNamerStateRestoreResult astRestore;
        using (MeasureSubphase(CompilationPhase.Namer, "module_namer_restore_ast"))
        {
            astRestore = AstNamerStateRestorer.Restore(
                _ast,
                restored.NormalizedAstStates,
                restored.RemapPlan!,
                restored.SymbolTable);
        }
        if (!astRestore.Applied)
        {
            SetNamerModuleRestoreFallbackCounters(_moduleArtifactRestoreExecution);
            SetProfilingCounter("Namer.moduleRestore.fallbackAstFailures", astRestore.Failures.Count);
            foreach (var group in astRestore.Failures
                         .Select(static failure => failure switch
                         {
                             _ when failure.StartsWith("AST Namer structure mismatch", StringComparison.Ordinal) => "structureMismatch",
                             _ when failure.StartsWith("AST Namer state node count mismatch", StringComparison.Ordinal) => "nodeCountMismatch",
                             _ when failure.StartsWith("duplicate AST Namer", StringComparison.Ordinal) => "duplicateKey",
                             _ when failure.StartsWith("conflicting AST Namer", StringComparison.Ordinal) => "conflictingState",
                             _ when failure.StartsWith("missing AST Namer", StringComparison.Ordinal) => "missingNode",
                             _ when failure.StartsWith("failed to remap AST Namer", StringComparison.Ordinal) => "remapFailure",
                             _ => "other"
                         })
                         .GroupBy(static category => category, StringComparer.Ordinal))
            {
                SetProfilingCounter(
                    $"Namer.moduleRestore.fallbackAstFailure.{group.Key}",
                    group.LongCount());
            }
            return false;
        }

        _symbolTable = restored.SymbolTable;
        _namerRestoreRemapPlan = restored.SourceRemapPlan;
        _nameResolver = new NameResolver(_symbolTable, _sourceCode, _options.ImportSearchRoots)
        {
            UsePrecompiledImportSignatureOnly = ShouldUsePrecompiledImportSignaturesOnly(),
            PreviousImplOverlapCheckSnapshot = _options.PreviousImplOverlapCheckSnapshot
        };
        if (_options.ConfigFfiLibraries.Length > 0)
        {
            _nameResolver.AddConfigLinkLibraries(_options.ConfigFfiLibraries);
        }
        BuildModuleMemberIndexSnapshot();
        _moduleNamerStatePayloads = mergePayloads;
        _implOverlapCheckSnapshot = _options.PreviousImplOverlapCheckSnapshot ?? ImplOverlapCheckSnapshot.Empty;
        SetProfilingCounter("Namer.implOverlapSnapshot.entries", _implOverlapCheckSnapshot.Entries.Count);
        SetProfilingCounter("Namer.moduleRestore.applied", 1);
        SetProfilingCounter("Namer.moduleRestore.payloadModules", restoredPayloads.Count);
        SetProfilingCounter("Namer.moduleRestore.restoredAstNodes", astRestore.RestoredNodes);
        SetProfilingCounter("Namer.moduleRestore.fallbackFullResolve", 0);
        SetModuleStageExecutionCounters(
            "Namer",
            _moduleArtifactRestoreExecution,
            hasRestorePayload: true);
        return true;
    }

    private void SetNamerModuleRestoreFallbackCounters(ProjectModuleArtifactRestoreExecutionSnapshot execution)
    {
        SetProfilingCounter("Namer.moduleRestore.applied", 0);
        SetProfilingCounter("Namer.moduleRestore.fallbackFullResolve", 1);
        SetProfilingCounter("Namer.moduleRestore.fallbackRestoredModules", execution.RestoredModules);
        SetProfilingCounter("Namer.moduleRestore.fallbackCompiledModules", execution.CompiledModules);
        SetProfilingCounter("Namer.moduleRestore.fallbackBlockedModules", execution.BlockedModules);
        SetProfilingCounter("Namer.moduleRestore.fallbackFailedModules", execution.FailedModules);
        EnsureModuleStageCounters("Namer");
    }

    private void EnsureNamerModulePlansForRestore()
    {
        _moduleGraphSnapshot = ProjectModuleGraphSnapshot.FromDependencyGraph(_moduleDependencyGraph);
        _moduleBuildSchedule = ProjectModuleBuildSchedule.FromGraphSnapshot(_moduleGraphSnapshot);
        _moduleSignatureSnapshot = ProjectModuleSignatureSnapshot.FromGraphSnapshot(
            _moduleGraphSnapshot,
            GetModuleSignatureSourceText,
            _options.LanguageVersion,
            CreateModuleSignatureFlagsHash());
        _moduleSemanticSignatureSnapshot = ProjectModuleSemanticSignatureSnapshot.FromGraphSnapshot(
            _moduleGraphSnapshot,
            CollectModuleDeclarationsForSemanticSignature(),
            _options.LanguageVersion,
            CreateModuleSignatureFlagsHash());
        _moduleInvalidationPlan = ProjectModuleInvalidationPlan.FromSemanticSignatures(
            _options.PreviousModuleSemanticSignatureSnapshot,
            _moduleSemanticSignatureSnapshot);
        _moduleInvalidationPlan = ExpandInvalidationToCompilationUnits(
            _moduleInvalidationPlan,
            _moduleGraphSnapshot);
        _moduleExecutionPlan = ProjectModuleExecutionPlan.FromSchedule(
            _moduleBuildSchedule,
            _moduleInvalidationPlan,
            ProjectModuleExecutionPlan.IsPrecompiledReadyArtifact);
        _moduleArtifactReadinessPlan = CreateArtifactReadinessPlan(
            _moduleExecutionPlan,
            ProjectModuleArtifactRequirement.SemanticOnly);
        if (_moduleArtifactReadinessPlan == null)
        {
            return;
        }

        BuildModuleDependencySignatureSnapshot(CompilationPhase.Namer, "Build.moduleDependencySignatures");
        _moduleArtifactRestorePlan = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(
            _moduleExecutionPlan,
            _moduleArtifactReadinessPlan,
            ProjectModuleArtifactRequirement.SemanticOnly);
        _moduleArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
            _moduleArtifactRestorePlan,
            ProjectModuleDependencySignatureRequirement.SemanticOnly);
    }

    private ValueTask<ProjectModuleExecutionItemResult> RestoreNamerPayloadModuleAsync(
        ProjectModuleArtifactRestoreItem item,
        IReadOnlyDictionary<string, ModuleNamerStatePayload> payloadByModule,
        IReadOnlyList<ModuleNamerStatePayload> previousPayloads,
        IReadOnlyDictionary<string, ProjectModuleSemanticSignatureNode> semanticByModule,
        ConcurrentDictionary<string, ModuleNamerStatePayload> restoredPayloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetNamerPayload(item.ModuleKey, payloadByModule, previousPayloads, semanticByModule, out var payload))
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed("missing Namer state payload"));
        }

        restoredPayloads[item.ModuleKey] = payload;
        return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
    }

    private ValueTask<ProjectModuleExecutionItemResult> CompileNamerPayloadModuleAsync(
        ProjectModuleArtifactRestoreItem item,
        ConcurrentDictionary<string, ModuleNamerStatePayload> compiledPayloads,
        ConcurrentDictionary<string, ModuleNamerStatePayload> compiledSupplementalPayloads,
        ConcurrentDictionary<string, Lazy<CompilationResult>> moduleCompilations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var compilationKey = GetModuleCompilationCacheKey(item.ModuleKey);
        var compilation = moduleCompilations.GetOrAdd(
            compilationKey,
            _ => new Lazy<CompilationResult>(
                () => CompileModuleToPhase(item.ModuleKey, CompilationPhase.Namer),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (!compilation.Success)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                FormatSubcompilationFailure(compilation)));
        }

        var payload = compilation.ModuleNamerStatePayloads?.FirstOrDefault(candidate =>
            string.Equals(candidate.ModuleKey, item.ModuleKey, StringComparison.Ordinal) ||
            string.Equals(candidate.ModuleIdentityKey, item.ModuleKey, StringComparison.Ordinal) ||
            string.Equals(ToDisplayModuleKey(candidate.ModuleIdentityKey), item.ModuleKey, StringComparison.Ordinal));
        if (payload == null)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                $"missing compiled Namer payload for module '{item.ModuleKey}'"));
        }

        if (_ast != null &&
            compilation.Ast != null &&
            compilation.SymbolTable != null &&
            compilation.ModuleMemberIndexSnapshot != null &&
            !AstStableNodeTraversal.MatchesCompilationRoot(
                _ast,
                item.ModuleKey,
                payload.ModuleIdentityKey))
        {
            var moduleStableNodes = AstStableNodeTraversal
                .Enumerate(compilation.Ast)
                .Where(static entry => entry.Ordinal != 0)
                .ToArray();
            payload = ModuleNamerStatePayload.Create(
                item.ModuleKey,
                compilation.SymbolTable,
                compilation.ModuleMemberIndexSnapshot,
                compilation.ModuleGraphSnapshot,
                compilation.Ast,
                moduleStableNodes,
                allSymbolIdentities: null,
                fullSymbolTablePayload: null,
                fullModuleRegistryPayload: null);
        }

        compiledPayloads[item.ModuleKey] = payload;
        AddSupplementalCompiledNamerPayloads(compilation, compiledSupplementalPayloads);
        return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
    }

    private static void AddSupplementalCompiledNamerPayloads(
        CompilationResult compilation,
        ConcurrentDictionary<string, ModuleNamerStatePayload> supplementalPayloads)
    {
        if (compilation.SymbolTable == null ||
            compilation.ModuleMemberIndexSnapshot == null ||
            compilation.ModuleGraphSnapshot == null)
        {
            return;
        }

        var existingModuleKeys = compilation.ModuleNamerStatePayloads?
            .Select(static payload => payload.ModuleKey)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var memberIndex in compilation.ModuleMemberIndexSnapshot.Nodes
                     .Where(node => !existingModuleKeys.Contains(node.ModuleKey)))
        {
            supplementalPayloads.TryAdd(
                memberIndex.ModuleKey,
                ModuleNamerStatePayload.Create(
                    memberIndex.ModuleKey,
                    compilation.SymbolTable,
                    compilation.ModuleMemberIndexSnapshot,
                    compilation.ModuleGraphSnapshot,
                    compilation.Ast));
        }
    }

    private static IReadOnlyDictionary<string, ModuleNamerStatePayload> BuildNamerPayloadLookup(
        IReadOnlyList<ModuleNamerStatePayload> previousPayloads)
    {
        var result = new Dictionary<string, ModuleNamerStatePayload>(StringComparer.Ordinal);
        foreach (var payload in previousPayloads.OrderBy(static payload => payload.ModuleKey, StringComparer.Ordinal))
        {
            AddNamerPayloadLookupKey(result, payload.ModuleKey, payload);
            AddNamerPayloadLookupKey(result, payload.ModuleIdentityKey, payload);
            AddNamerPayloadLookupKey(result, ToDisplayModuleKey(payload.ModuleIdentityKey), payload);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, ProjectModuleSemanticSignatureNode> BuildNamerSemanticLookup(
        ProjectModuleSemanticSignatureSnapshot snapshot)
    {
        var result = new Dictionary<string, ProjectModuleSemanticSignatureNode>(StringComparer.Ordinal);
        foreach (var node in snapshot.Nodes.OrderBy(static node => node.ModuleKey, StringComparer.Ordinal))
        {
            AddNamerSemanticLookupKey(result, node.ModuleKey, node);
            AddNamerSemanticLookupKey(result, ToDisplayModuleKey(node.ModuleKey), node);
        }

        return result;
    }

    private static void AddNamerSemanticLookupKey(
        Dictionary<string, ProjectModuleSemanticSignatureNode> result,
        string key,
        ProjectModuleSemanticSignatureNode node)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            result.TryAdd(key, node);
        }
    }

    private IReadOnlyList<ModuleNamerStatePayload> ExpandNamerPayloadsForMerge(
        IReadOnlyList<ModuleNamerStatePayload> restoredPayloads,
        IReadOnlyList<ModuleNamerStatePayload> previousPayloads,
        IReadOnlyDictionary<string, ProjectModuleSemanticSignatureNode> semanticByModule)
    {
        var lookup = BuildNamerPayloadLookup(previousPayloads);
        var selected = new Dictionary<string, ModuleNamerStatePayload>(StringComparer.Ordinal);
        var queue = new Queue<ModuleNamerStatePayload>(
            restoredPayloads.OrderBy(static payload => payload.ModuleKey, StringComparer.Ordinal));

        while (queue.Count > 0)
        {
            var payload = queue.Dequeue();
            if (!selected.TryAdd(payload.ModuleIdentityKey, payload))
            {
                continue;
            }

            var module = FindPayloadModule(payload);
            if (module == null)
            {
                continue;
            }

            foreach (var referencedModule in FindReferencedPayloadModules(payload, module)
                         .OrderBy(static module => module.IdentityKey, StringComparer.Ordinal))
            {
                if (selected.ContainsKey(referencedModule.IdentityKey))
                {
                    continue;
                }

                if (TryGetNamerPayloadByModulePayload(
                        referencedModule,
                        lookup,
                        previousPayloads,
                        semanticByModule,
                        out var referencedPayload))
                {
                    queue.Enqueue(referencedPayload);
                }
            }
        }

        return selected.Values
            .OrderBy(static payload => payload.ModuleKey, StringComparer.Ordinal)
            .ThenBy(static payload => payload.ModuleIdentityKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static ModuleRegistryModulePayload? FindPayloadModule(ModuleNamerStatePayload payload) =>
        payload.ModuleRegistry.Modules
            .Where(module =>
                string.Equals(module.IdentityKey, payload.ModuleIdentityKey, StringComparison.Ordinal) ||
                string.Equals(module.DisplayKey, payload.ModuleKey, StringComparison.Ordinal) ||
                string.Equals(module.IdentityKey, payload.ModuleKey, StringComparison.Ordinal))
            .OrderBy(static module => module.IdentityKey, StringComparer.Ordinal)
            .ThenBy(static module => module.Id)
            .FirstOrDefault();

    private static IEnumerable<ModuleRegistryModulePayload> FindReferencedPayloadModules(
        ModuleNamerStatePayload payload,
        ModuleRegistryModulePayload module)
    {
        var moduleIds = payload.ModuleRegistry.Modules
            .Select(static module => module.Id)
            .ToHashSet();
        var referencedIds = module.Imports
            .Concat(module.Members.Where(moduleIds.Contains))
            .Concat(module.ParentModule > 0 ? [module.ParentModule] : [])
            .ToHashSet();
        if (referencedIds.Count == 0)
        {
            yield break;
        }

        foreach (var referencedModule in payload.ModuleRegistry.Modules
                     .Where(candidate => referencedIds.Contains(candidate.Id)))
        {
            yield return referencedModule;
        }
    }

    private bool TryGetNamerPayloadByModulePayload(
        ModuleRegistryModulePayload module,
        IReadOnlyDictionary<string, ModuleNamerStatePayload> lookup,
        IReadOnlyList<ModuleNamerStatePayload> previousPayloads,
        IReadOnlyDictionary<string, ProjectModuleSemanticSignatureNode> semanticByModule,
        out ModuleNamerStatePayload payload)
    {
        if (lookup.TryGetValue(module.IdentityKey, out payload!) ||
            lookup.TryGetValue(module.DisplayKey, out payload!))
        {
            return true;
        }

        return TryGetNamerPayload(module.DisplayKey, lookup, previousPayloads, semanticByModule, out payload);
    }

    private static void AddNamerPayloadLookupKey(
        Dictionary<string, ModuleNamerStatePayload> result,
        string key,
        ModuleNamerStatePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            result.TryAdd(key, payload);
        }
    }

    private bool TryGetNamerPayload(
        string moduleKey,
        IReadOnlyDictionary<string, ModuleNamerStatePayload> payloadByModule,
        IReadOnlyList<ModuleNamerStatePayload> previousPayloads,
        IReadOnlyDictionary<string, ProjectModuleSemanticSignatureNode> semanticByModule,
        out ModuleNamerStatePayload payload)
    {
        if (payloadByModule.TryGetValue(moduleKey, out payload!))
        {
            return true;
        }

        if (_options.ModuleNamerStatePayloadLoader != null &&
            semanticByModule.TryGetValue(moduleKey, out var semantic))
        {
            AddProfilingCounter("Namer.moduleRestore.loaderAttempts", 1);
            var loaded = _options.ModuleNamerStatePayloadLoader(
                semantic.ModuleKey,
                ProjectModuleArtifactKinds.NamerStatePayload,
                semantic.ExportSurfaceHash,
                semantic.DependencySemanticSignatureHash);
            if (loaded is { SchemaVersion: ModuleNamerStatePayload.CurrentSchemaVersion } &&
                loaded.HasValidPayloadHash())
            {
                AddProfilingCounter("Namer.moduleRestore.loaderHits", 1);
                payload = loaded;
                return true;
            }

            AddProfilingCounter("Namer.moduleRestore.loaderMisses", 1);
        }

        if (previousPayloads.Count == 1)
        {
            payload = previousPayloads[0];
            return true;
        }

        payload = null!;
        return false;
    }

    private static string ToDisplayModuleKey(string moduleIdentityKey)
    {
        var separator = moduleIdentityKey.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
        {
            return moduleIdentityKey;
        }

        var packagePart = moduleIdentityKey[..separator];
        var modulePath = moduleIdentityKey[(separator + 2)..];
        var aliasSeparator = packagePart.IndexOf('@', StringComparison.Ordinal);
        var packageAlias = aliasSeparator >= 0 ? packagePart[..aliasSeparator] : packagePart;
        return string.IsNullOrWhiteSpace(packageAlias) || packageAlias == ModuleIdentity.CurrentPackageInstanceKey
            ? modulePath
            : $"{packageAlias}::{modulePath}";
    }
}
