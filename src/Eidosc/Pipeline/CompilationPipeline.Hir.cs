using System.Collections.Concurrent;
using Eidosc.Symbols;
using Eidosc.Mir;
using Eidosc.Mir.Closure;
using Eidosc.Hir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private bool RunHirBuilder()
    {
        if (TryRestoreLiveState(CompilationPhase.Hir))
        {
            return true;
        }

        if (TryRestoreHirFromModulePayloads())
        {
            RefreshCompilationLiveStatePayload(CompilationPhase.Hir);
            StoreLiveState(CompilationPhase.Hir);
            return true;
        }

        HirBuilder hirBuilder;
        using (MeasureSubphase(CompilationPhase.Hir, "create_builder"))
        {
            hirBuilder = new HirBuilder(_symbolTable!, _typeInferer, _abilityInferer)
            {
                EntryFunctionName = _options.EntryFunctionName
            };
        }

        using (MeasureSubphase(CompilationPhase.Hir, "build_hir"))
        {
            _hirModule = hirBuilder.Build(_ast!, _nameResolver?.LinkLibraries);
            _hirCopyLikeTypeIds = hirBuilder.CopyLikeTypeIds;
            _hirDynamicTypeKeys = hirBuilder.DynamicTypeKeys;
            _hirTypeDescriptors = hirBuilder.TypeDescriptors;
            _hirConstructorLayouts = hirBuilder.ConstructorLayouts;
        }

        using (MeasureSubphase(CompilationPhase.Hir, "collect_diagnostics"))
        {
            _diagnostics.AddRange(hirBuilder.Diagnostics);
        }

        if (_debugContext.IsEnabled)
        {
            using (MeasureSubphase(CompilationPhase.Hir, "debug_emit"))
            {
                _debugContext.Emit("hir", HirFormatter.FormatHir(_hirModule));

            }
        }

        if (_hirModule != null)
        {
            using (MeasureSubphase(CompilationPhase.Hir, "parameter_effect_analysis"))
            {
                var effectAnalysis = new HirParameterEffectAnalysis(_hirModule);
                effectAnalysis.Analyze();
                _hirParameterEffects = effectAnalysis.Results;
            }
        }

        var success = !hirBuilder.Diagnostics.Any(diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error);
        if (success)
        {
            RefreshCompilationLiveStatePayload(CompilationPhase.Hir);
            StoreLiveState(CompilationPhase.Hir);
        }

        return success;
    }

    private bool TryRestoreHirFromModulePayloads()
    {
        var previousPayloads = _options.PreviousModuleHirStatePayloads ?? [];
        if (_options.StopAtPhase != CompilationPhase.Hir ||
            _moduleTypedSemanticSnapshot == null ||
            (previousPayloads.Count == 0 && _options.ModuleHirStatePayloadLoader == null))
        {
            return false;
        }

        using (MeasureSubphase(CompilationPhase.Hir, "module_hir_restore_prepare"))
        {
            BuildHirRestoreModulePlan();
        }

        if (_moduleTypedArtifactRestorePlan == null)
        {
            return false;
        }

        var payloadByModule = BuildHirPayloadLookup(previousPayloads);
        _moduleTypedArtifactRestorePayload = ProjectModuleArtifactRestorePayloadSnapshot.LoadTypesStatePayload(
            _moduleTypedArtifactRestorePlan,
            (moduleKey, _, _, _) => TryGetHirPayload(moduleKey, payloadByModule, previousPayloads, out var payload)
                ? payload.TypedSemantic
                : null);
        _moduleTypedArtifactRestorePlan = _moduleTypedArtifactRestorePlan.GateWithPayload(_moduleTypedArtifactRestorePayload);
        _moduleTypedArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
            _moduleTypedArtifactRestorePlan,
            ProjectModuleDependencySignatureRequirement.SemanticTyped);

        var restoredPayloads = new ConcurrentDictionary<string, ModuleHirStateArtifactPayload>(StringComparer.Ordinal);
        var compiledPayloads = new ConcurrentDictionary<string, ModuleHirStateArtifactPayload>(StringComparer.Ordinal);
        var moduleCompilations = new ConcurrentDictionary<string, Lazy<CompilationResult>>(StringComparer.Ordinal);
        _moduleTypedArtifactRestoreExecution = ProjectModuleArtifactRestoreExecutor.ExecuteAsync(
                _moduleTypedArtifactRestorePlan,
                (item, cancellationToken) => RestoreHirPayloadModuleAsync(
                    item,
                    payloadByModule,
                    previousPayloads,
                    restoredPayloads,
                    cancellationToken),
                (item, cancellationToken) => CompileHirPayloadModuleAsync(
                    item,
                    compiledPayloads,
                    moduleCompilations,
                    cancellationToken),
                maxDegreeOfParallelism: GetModuleArtifactRestoreMaxDegreeOfParallelism(_moduleTypedArtifactRestorePlan))
            .GetAwaiter()
            .GetResult();

        SetModuleArtifactRestoreCounters("Build.moduleHirArtifactRestore", _moduleTypedArtifactRestorePlan);
        SetModuleArtifactRestoreExecutionCounters(
            "Build.moduleHirArtifactRestoreExecution",
            _moduleTypedArtifactRestoreExecution);
        SetModuleArtifactRestorePayloadCounters(
            "Build.moduleHirArtifactRestorePayload",
            _moduleTypedArtifactRestorePayload);

        if (_moduleTypedArtifactRestoreExecution.FailedModules > 0 ||
            _moduleTypedArtifactRestoreExecution.BlockedModules > 0 ||
            compiledPayloads.Count != _moduleTypedArtifactRestoreExecution.CompiledModules ||
            restoredPayloads.Count != _moduleTypedArtifactRestoreExecution.RestoredModules ||
            _moduleTypedArtifactRestoreExecution.CompiledModules +
            _moduleTypedArtifactRestoreExecution.RestoredModules == 0)
        {
            SetHirModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
            return false;
        }

        var moduleOrder = BuildHirModuleOrder();
        var orderedPayloads = restoredPayloads
            .Select(static entry => (entry.Key, Payload: entry.Value, IsCompiled: false))
            .Concat(compiledPayloads.Select(static entry => (entry.Key, Payload: entry.Value, IsCompiled: true)))
            .OrderBy(entry => moduleOrder.GetValueOrDefault(entry.Key, int.MaxValue))
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        var restoredModules = new List<HirModule>(orderedPayloads.Length);
        var attachedStates = new List<(bool IsCompiled, string ModuleKey, ModuleHirAttachedState State)>(
            orderedPayloads.Length);
        foreach (var entry in orderedPayloads)
        {
            var payload = entry.Payload;
            if (!payload.IsModuleLocal ||
                !payload.HirState.TryRestore(out var module, out var restoredAttachedState))
            {
                SetHirModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
                return false;
            }

            restoredModules.Add(module);
            attachedStates.Add((entry.IsCompiled, entry.Key, restoredAttachedState));
        }

        _hirModule = MergeRestoredHirModules(restoredModules);
        if (attachedStates.Count > 0)
        {
            var attachedState = MergeHirAttachedStates(attachedStates
                .OrderBy(static entry => entry.IsCompiled)
                .ThenBy(static entry => entry.ModuleKey, StringComparer.Ordinal)
                .Select(static entry => entry.State)
                .ToArray());
            _hirParameterEffects = attachedState.ParameterEffects;
            _hirCopyLikeTypeIds = attachedState.CopyLikeTypeIds;
            _hirDynamicTypeKeys = attachedState.DynamicTypeKeys;
            _hirTypeDescriptors = attachedState.TypeDescriptors;
            _hirConstructorLayouts = attachedState.ConstructorLayouts;
        }

        SetProfilingCounter("Hir.moduleRestore.applied", 1);
        SetProfilingCounter("Hir.moduleRestore.payloadModules", orderedPayloads.Length);
        SetProfilingCounter("Hir.moduleRestore.restoredPayloadModules", restoredPayloads.Count);
        SetProfilingCounter("Hir.moduleRestore.compiledPayloadModules", compiledPayloads.Count);
        SetProfilingCounter("Hir.moduleRestore.restoredDeclarations", _hirModule.Declarations.Count);
        SetProfilingCounter("Hir.moduleRestore.fallbackBuildHir", 0);
        SetModuleStageExecutionCounters(
            "Hir",
            _moduleTypedArtifactRestoreExecution,
            hasRestorePayload: true);
        return true;
    }

    private IReadOnlyDictionary<string, int> BuildHirModuleOrder()
    {
        if (_ast == null)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        return EnumerateModuleTree(_ast)
            .Select(static module => ToModuleDeclKey(module))
            .Where(static moduleKey => !string.IsNullOrWhiteSpace(moduleKey))
            .Distinct(StringComparer.Ordinal)
            .Select(static (moduleKey, index) => (moduleKey, index))
            .ToDictionary(static entry => entry.moduleKey, static entry => entry.index, StringComparer.Ordinal);
    }

    private void BuildHirRestoreModulePlan()
    {
        if (_moduleBuildSchedule == null ||
            _moduleTypedInvalidationPlan == null)
        {
            BuildTypedSemanticSnapshot();
        }

        if (_moduleBuildSchedule == null ||
            _moduleTypedInvalidationPlan == null)
        {
            return;
        }

        _moduleTypedExecutionPlan = ProjectModuleExecutionPlan.FromSchedule(
            _moduleBuildSchedule,
            _moduleTypedInvalidationPlan,
            ProjectModuleExecutionPlan.IsPrecompiledReadyArtifact);
        _moduleTypedArtifactReadinessPlan = CreateArtifactReadinessPlan(
            _moduleTypedExecutionPlan,
            ProjectModuleArtifactRequirement.SemanticTyped);
        if (_moduleTypedArtifactReadinessPlan == null)
        {
            return;
        }

        BuildModuleDependencySignatureSnapshot(CompilationPhase.Hir, "Build.moduleDependencySignatures");
        _moduleTypedArtifactRestorePlan = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(
            _moduleTypedExecutionPlan,
            _moduleTypedArtifactReadinessPlan,
            ProjectModuleArtifactRequirement.SemanticTyped);
        _moduleTypedArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
            _moduleTypedArtifactRestorePlan,
            ProjectModuleDependencySignatureRequirement.SemanticTyped);
    }

    private static Dictionary<string, ModuleHirStateArtifactPayload> BuildHirPayloadLookup(
        IReadOnlyList<ModuleHirStateArtifactPayload> previousPayloads)
    {
        var result = new Dictionary<string, ModuleHirStateArtifactPayload>(StringComparer.Ordinal);
        foreach (var payload in previousPayloads)
        {
            if (payload.HasValidPayloadHash())
            {
                result.TryAdd(payload.ModuleKey, payload);
                result.TryAdd(payload.TypedSemantic.ModuleKey, payload);
            }
        }

        return result;
    }

    private bool TryGetHirPayload(
        string moduleKey,
        IReadOnlyDictionary<string, ModuleHirStateArtifactPayload> payloadByModule,
        IReadOnlyList<ModuleHirStateArtifactPayload> previousPayloads,
        out ModuleHirStateArtifactPayload payload)
    {
        if (payloadByModule.TryGetValue(moduleKey, out payload!))
        {
            return true;
        }

        if (_options.ModuleHirStatePayloadLoader != null)
        {
            foreach (var candidate in previousPayloads.Where(payload =>
                         string.Equals(payload.ModuleKey, moduleKey, StringComparison.Ordinal) ||
                         string.Equals(payload.TypedSemantic.ModuleKey, moduleKey, StringComparison.Ordinal)))
            {
                var loaded = _options.ModuleHirStatePayloadLoader(
                    candidate.TypedSemantic.ModuleKey,
                    ProjectModuleArtifactKinds.HirStatePayload,
                    candidate.TypedSemantic.LocalSurfaceHash,
                    candidate.TypedSemantic.DependencyTypedSemanticHash);
                if (loaded is { SchemaVersion: ModuleHirStateArtifactPayload.CurrentSchemaVersion } &&
                    loaded.HasValidPayloadHash())
                {
                    payload = loaded;
                    return true;
                }
            }

            var typedNode = _moduleTypedSemanticSnapshot?.Nodes.FirstOrDefault(node =>
                string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal));
            if (typedNode != null)
            {
                var loaded = _options.ModuleHirStatePayloadLoader(
                    typedNode.ModuleKey,
                    ProjectModuleArtifactKinds.HirStatePayload,
                    typedNode.LocalSurfaceHash,
                    typedNode.DependencyTypedSemanticHash);
                if (loaded is { SchemaVersion: ModuleHirStateArtifactPayload.CurrentSchemaVersion } &&
                    loaded.HasValidPayloadHash())
                {
                    payload = loaded;
                    return true;
                }
            }
        }

        if (previousPayloads.Count == 1)
        {
            payload = previousPayloads[0];
            return true;
        }

        payload = null!;
        return false;
    }

    private ValueTask<ProjectModuleExecutionItemResult> RestoreHirPayloadModuleAsync(
        ProjectModuleArtifactRestoreItem item,
        IReadOnlyDictionary<string, ModuleHirStateArtifactPayload> payloadByModule,
        IReadOnlyList<ModuleHirStateArtifactPayload> previousPayloads,
        ConcurrentDictionary<string, ModuleHirStateArtifactPayload> restoredPayloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetHirPayload(item.ModuleKey, payloadByModule, previousPayloads, out var payload))
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed("missing HIR state payload"));
        }

        if (!payload.IsModuleLocal ||
            !payload.HasValidPayloadHash() ||
            !payload.HirState.IsRestorable)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed("invalid HIR state payload"));
        }

        restoredPayloads[item.ModuleKey] = payload;
        return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
    }

    private ValueTask<ProjectModuleExecutionItemResult> CompileHirPayloadModuleAsync(
        ProjectModuleArtifactRestoreItem item,
        ConcurrentDictionary<string, ModuleHirStateArtifactPayload> compiledPayloads,
        ConcurrentDictionary<string, Lazy<CompilationResult>> moduleCompilations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var compilationKey = GetModuleCompilationCacheKey(item.ModuleKey);
        var compilation = moduleCompilations.GetOrAdd(
            compilationKey,
            _ => new Lazy<CompilationResult>(
                () => CompileModuleToPhase(item.ModuleKey, CompilationPhase.Hir),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (!compilation.Success)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                FormatSubcompilationFailure(compilation)));
        }

        var payload = compilation.ModuleHirStatePayloads?.FirstOrDefault(candidate =>
            string.Equals(candidate.ModuleKey, item.ModuleKey, StringComparison.Ordinal) ||
            string.Equals(candidate.TypedSemantic.ModuleKey, item.ModuleKey, StringComparison.Ordinal));
        if (payload == null)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                $"missing compiled HIR payload for module '{item.ModuleKey}'"));
        }

        if (!payload.IsModuleLocal ||
            !payload.HasValidPayloadHash() ||
            !payload.HirState.IsRestorable)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                $"invalid compiled HIR payload for module '{item.ModuleKey}'"));
        }

        compiledPayloads[item.ModuleKey] = payload;
        return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
    }

    private HirModule MergeRestoredHirModules(IReadOnlyList<HirModule> modules)
    {
        if (modules.Count == 0)
        {
            return new HirModule();
        }

        var envelope = _ast == null
            ? modules[0]
            : modules.FirstOrDefault(module =>
                  string.Equals(module.PackageAlias, _ast.PackageAlias, StringComparison.Ordinal) &&
                  string.Equals(module.PackageInstanceKey, _ast.PackageInstanceKey, StringComparison.Ordinal) &&
                  module.Path.SequenceEqual(_ast.Path, StringComparer.Ordinal)) ?? modules[0];
        return envelope with
        {
            Declarations = modules
                .SelectMany(static module => module.Declarations)
                .ToList(),
            Exports = modules
                .SelectMany(static module => module.Exports)
                .Distinct()
                .ToList(),
            Imports = modules
                .SelectMany(static module => module.Imports)
                .Distinct()
                .ToList(),
            LinkLibraries = modules
                .SelectMany(static module => module.LinkLibraries)
                .Distinct(StringComparer.Ordinal)
                .ToList()
        };
    }

    private static ModuleHirAttachedState MergeHirAttachedStates(
        IReadOnlyList<ModuleHirAttachedState> states)
    {
        var parameterEffects = new ParameterEffectMap();
        var copyLikeTypeIds = new HashSet<TypeId>();
        var dynamicTypeKeys = new Dictionary<TypeId, string>();
        var typeDescriptors = new Dictionary<int, TypeDescriptor>();
        var constructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>();

        foreach (var state in states)
        {
            foreach (var (name, effects) in state.ParameterEffects.EffectsByName)
            {
                parameterEffects.Add(name, 0, effects.ToList());
            }

            foreach (var (symbolId, effects) in state.ParameterEffects.EffectsBySymbolId)
            {
                parameterEffects.Add("", symbolId, effects.ToList());
            }

            copyLikeTypeIds.UnionWith(state.CopyLikeTypeIds);
            foreach (var (typeId, typeKey) in state.DynamicTypeKeys)
            {
                dynamicTypeKeys[typeId] = typeKey;
            }

            foreach (var (typeId, descriptor) in state.TypeDescriptors)
            {
                typeDescriptors[typeId] = descriptor;
            }

            foreach (var (typeId, layouts) in state.ConstructorLayouts)
            {
                constructorLayouts[typeId] = layouts.ToList();
            }
        }

        return new ModuleHirAttachedState(
            parameterEffects,
            copyLikeTypeIds,
            dynamicTypeKeys,
            typeDescriptors,
            constructorLayouts);
    }

    private void SetHirModuleRestoreFallbackCounters(ProjectModuleArtifactRestoreExecutionSnapshot execution)
    {
        SetProfilingCounter("Hir.moduleRestore.applied", 0);
        SetProfilingCounter("Hir.moduleRestore.fallbackBuildHir", 1);
        SetProfilingCounter("Hir.moduleRestore.fallbackRestoredModules", execution.RestoredModules);
        SetProfilingCounter("Hir.moduleRestore.fallbackCompiledModules", execution.CompiledModules);
        SetProfilingCounter("Hir.moduleRestore.fallbackBlockedModules", execution.BlockedModules);
        SetProfilingCounter("Hir.moduleRestore.fallbackFailedModules", execution.FailedModules);
        EnsureModuleStageCounters("Hir");
    }

}
