using System.Collections.Concurrent;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private bool TryRestoreMirFromModulePayloads()
    {
        var previousPayloads = _options.PreviousModuleMirStatePayloads ?? [];
        if (_options.StopAtPhase is not (CompilationPhase.Mir or CompilationPhase.Llvm) ||
            _moduleTypedSemanticSnapshot == null ||
            (previousPayloads.Count == 0 && _options.ModuleMirStatePayloadLoader == null))
        {
            return false;
        }

        using (MeasureSubphase(CompilationPhase.Mir, "module_mir_restore_prepare"))
        {
            BuildMirRestoreModulePlan();
        }

        if (_moduleTypedArtifactRestorePlan == null)
        {
            return false;
        }

        var payloadByModule = BuildMirPayloadLookup(previousPayloads);
        _moduleTypedArtifactRestorePayload = ProjectModuleArtifactRestorePayloadSnapshot.LoadTypesStatePayload(
            _moduleTypedArtifactRestorePlan,
            (moduleKey, _, _, _) => TryGetMirPayload(moduleKey, payloadByModule, previousPayloads, out var payload)
                ? payload.TypedSemantic
                : null);
        var readyArtifactPayloads = LoadReadyArtifactMirPayloads(
            _moduleTypedArtifactRestorePlan,
            payloadByModule,
            previousPayloads);
        _moduleTypedArtifactRestorePlan = _moduleTypedArtifactRestorePlan.GateWithPayload(_moduleTypedArtifactRestorePayload);
        _moduleTypedArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
            _moduleTypedArtifactRestorePlan,
            ProjectModuleDependencySignatureRequirement.SemanticTyped);

        var restoredPayloads = new ConcurrentDictionary<string, ModuleMirStateArtifactPayload>(StringComparer.Ordinal);
        var compiledPayloads = new ConcurrentDictionary<string, ModuleMirStateArtifactPayload>(StringComparer.Ordinal);
        var moduleCompilations = new ConcurrentDictionary<
            string,
            Lazy<IReadOnlyDictionary<string, ModuleMirCompilationResult>>>(StringComparer.Ordinal);
        _moduleTypedArtifactRestoreExecution = ProjectModuleArtifactRestoreExecutor.ExecuteAsync(
                _moduleTypedArtifactRestorePlan,
                (item, cancellationToken) => RestoreMirPayloadModuleAsync(
                    item,
                    payloadByModule,
                    previousPayloads,
                    restoredPayloads,
                    cancellationToken),
                (item, cancellationToken) => CompileMirPayloadModuleAsync(
                    item,
                    compiledPayloads,
                    moduleCompilations,
                    cancellationToken),
                maxDegreeOfParallelism: GetModuleArtifactRestoreMaxDegreeOfParallelism(_moduleTypedArtifactRestorePlan))
            .GetAwaiter()
            .GetResult();

        foreach (var compilation in moduleCompilations.Values
                     .Where(static value => value.IsValueCreated)
                     .Select(static value => value.Value)
                     .SelectMany(static batch => batch.Values)
                     .OrderBy(static result => result.ModuleKey, StringComparer.Ordinal))
        {
            _diagnostics.AddRange(compilation.Diagnostics);
        }

        SetModuleArtifactRestoreCounters("Build.moduleMirStateRestore", _moduleTypedArtifactRestorePlan);
        SetModuleArtifactRestoreExecutionCounters(
            "Build.moduleMirStateRestoreExecution",
            _moduleTypedArtifactRestoreExecution);
        SetModuleArtifactRestorePayloadCounters(
            "Build.moduleMirStateRestorePayload",
            _moduleTypedArtifactRestorePayload);

        if (_moduleTypedArtifactRestoreExecution.FailedModules > 0 ||
            _moduleTypedArtifactRestoreExecution.BlockedModules > 0 ||
            compiledPayloads.Count != _moduleTypedArtifactRestoreExecution.CompiledModules ||
            restoredPayloads.Count != _moduleTypedArtifactRestoreExecution.RestoredModules ||
            _moduleTypedArtifactRestoreExecution.CompiledModules +
            _moduleTypedArtifactRestoreExecution.RestoredModules +
            readyArtifactPayloads.Count == 0)
        {
            SetMirModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
            return false;
        }

        var orderedPayloads = restoredPayloads
            .Select(static entry => (entry.Key, Payload: entry.Value, IsCompiled: false))
            .Concat(readyArtifactPayloads.Select(static entry => (entry.Key, Payload: entry.Value, IsCompiled: false)))
            .Concat(compiledPayloads.Select(static entry => (entry.Key, Payload: entry.Value, IsCompiled: true)))
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        var restoredModules = new List<(string ModuleKey, bool IsCompiled, MirModule Module)>(orderedPayloads.Length);
        foreach (var entry in orderedPayloads)
        {
            var payload = entry.Payload;
            if (!payload.IsModuleLocal ||
                payload.ModuleLocalFunctionCount <= 0 ||
                !payload.MirState.TryRestore(out var module))
            {
                SetMirModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
                return false;
            }

            restoredModules.Add((entry.Key, entry.IsCompiled, module));
        }

        _mirModule = MergeRestoredMirModules(restoredModules);
        _borrowMirModule = _mirModule;
        _mirFunctionFingerprints = MirFunctionFingerprintSnapshot.FromModule(_mirModule);
        _moduleMirArtifactSnapshot = ProjectModuleMirArtifactSnapshot.Create(
            _moduleTypedSemanticSnapshot,
            _mirFunctionFingerprints);
        BuildModuleDependencySignatureSnapshot(CompilationPhase.Mir, "Build.moduleDependencySignatures");

        SetProfilingCounter("Mir.moduleRestore.applied", 1);
        SetProfilingCounter("Mir.moduleRestore.payloadModules", orderedPayloads.Length);
        SetProfilingCounter("Mir.moduleRestore.restoredPayloadModules", restoredPayloads.Count);
        SetProfilingCounter("Mir.moduleRestore.compiledPayloadModules", compiledPayloads.Count);
        SetProfilingCounter("Mir.moduleRestore.readyArtifactPayloadModules", readyArtifactPayloads.Count);
        SetProfilingCounter("Mir.moduleRestore.restoredFunctions", _mirModule.Functions.Count);
        SetProfilingCounter("Mir.moduleRestore.fallbackBuildMir", 0);
        AddMirModuleShapeCounters("Mir.moduleRestore.output", _mirModule);
        if (_options.EnableDetailedProfiling)
        {
            AddMirFunctionFingerprintCounters("Mir.moduleRestore.output", _mirModule);
        }

        SetModuleStageExecutionCounters(
            "Mir",
            _moduleTypedArtifactRestoreExecution,
            hasRestorePayload: true);
        return true;
    }

    private void BuildMirRestoreModulePlan()
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

        BuildModuleDependencySignatureSnapshot(CompilationPhase.Mir, "Build.moduleDependencySignatures");
        _moduleTypedArtifactRestorePlan = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(
            _moduleTypedExecutionPlan,
            _moduleTypedArtifactReadinessPlan,
            ProjectModuleArtifactRequirement.SemanticTyped);
        _moduleTypedArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
            _moduleTypedArtifactRestorePlan,
            ProjectModuleDependencySignatureRequirement.SemanticTyped);
    }

    private static Dictionary<string, ModuleMirStateArtifactPayload> BuildMirPayloadLookup(
        IReadOnlyList<ModuleMirStateArtifactPayload> previousPayloads)
    {
        var result = new Dictionary<string, ModuleMirStateArtifactPayload>(StringComparer.Ordinal);
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

    private Dictionary<string, ModuleMirStateArtifactPayload> LoadReadyArtifactMirPayloads(
        ProjectModuleArtifactRestorePlan plan,
        IReadOnlyDictionary<string, ModuleMirStateArtifactPayload> payloadByModule,
        IReadOnlyList<ModuleMirStateArtifactPayload> previousPayloads)
    {
        var result = new Dictionary<string, ModuleMirStateArtifactPayload>(StringComparer.Ordinal);
        foreach (var item in plan.Layers
                     .SelectMany(static layer => layer.Modules)
                     .Where(static item => item.Action == ProjectModuleArtifactRestoreAction.ReadyArtifact)
                     .OrderBy(static item => item.ModuleKey, StringComparer.Ordinal))
        {
            if (!TryGetMirPayload(item.ModuleKey, payloadByModule, previousPayloads, out var payload) ||
                !payload.IsModuleLocal ||
                !payload.HasValidPayloadHash() ||
                !payload.MirState.IsRestorable ||
                payload.ModuleLocalFunctionCount <= 0)
            {
                continue;
            }

            result[item.ModuleKey] = payload;
        }

        return result;
    }

    private bool TryGetMirPayload(
        string moduleKey,
        IReadOnlyDictionary<string, ModuleMirStateArtifactPayload> payloadByModule,
        IReadOnlyList<ModuleMirStateArtifactPayload> previousPayloads,
        out ModuleMirStateArtifactPayload payload)
    {
        if (payloadByModule.TryGetValue(moduleKey, out payload!))
        {
            return true;
        }

        if (_options.ModuleMirStatePayloadLoader != null)
        {
            foreach (var candidate in previousPayloads.Where(payload =>
                         string.Equals(payload.ModuleKey, moduleKey, StringComparison.Ordinal) ||
                         string.Equals(payload.TypedSemantic.ModuleKey, moduleKey, StringComparison.Ordinal)))
            {
                var loaded = _options.ModuleMirStatePayloadLoader(
                    candidate.TypedSemantic.ModuleKey,
                    ProjectModuleArtifactKinds.MirStatePayload,
                    candidate.TypedSemantic.LocalSurfaceHash,
                    candidate.TypedSemantic.DependencyTypedSemanticHash);
                if (loaded is { SchemaVersion: ModuleMirStateArtifactPayload.CurrentSchemaVersion } &&
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
                var loaded = _options.ModuleMirStatePayloadLoader(
                    typedNode.ModuleKey,
                    ProjectModuleArtifactKinds.MirStatePayload,
                    typedNode.LocalSurfaceHash,
                    typedNode.DependencyTypedSemanticHash);
                if (loaded is { SchemaVersion: ModuleMirStateArtifactPayload.CurrentSchemaVersion } &&
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

    private ValueTask<ProjectModuleExecutionItemResult> RestoreMirPayloadModuleAsync(
        ProjectModuleArtifactRestoreItem item,
        IReadOnlyDictionary<string, ModuleMirStateArtifactPayload> payloadByModule,
        IReadOnlyList<ModuleMirStateArtifactPayload> previousPayloads,
        ConcurrentDictionary<string, ModuleMirStateArtifactPayload> restoredPayloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetMirPayload(item.ModuleKey, payloadByModule, previousPayloads, out var payload))
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed("missing MIR state payload"));
        }

        if (!payload.IsModuleLocal ||
            !payload.HasValidPayloadHash() ||
            !payload.MirState.IsRestorable ||
            payload.ModuleLocalFunctionCount <= 0)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed("invalid MIR state payload"));
        }

        restoredPayloads[item.ModuleKey] = payload;
        return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
    }

    private ValueTask<ProjectModuleExecutionItemResult> CompileMirPayloadModuleAsync(
        ProjectModuleArtifactRestoreItem item,
        ConcurrentDictionary<string, ModuleMirStateArtifactPayload> compiledPayloads,
        ConcurrentDictionary<
            string,
            Lazy<IReadOnlyDictionary<string, ModuleMirCompilationResult>>> moduleCompilations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var compilationKey = GetModuleCompilationCacheKey(item.ModuleKey);
        var compilationBatch = moduleCompilations.GetOrAdd(
            compilationKey,
            _ => new Lazy<IReadOnlyDictionary<string, ModuleMirCompilationResult>>(
                () => CompileCurrentHirCompilationUnitToMir(compilationKey, item.ModuleKey),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (!compilationBatch.TryGetValue(item.ModuleKey, out var compilation))
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                $"missing MIR compilation result for module '{item.ModuleKey}'"));
        }

        if (!compilation.Success)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                FormatModuleMirCompilationFailure(compilation)));
        }

        var payload = compilation.Payload;
        if (payload == null)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                $"missing compiled MIR payload for module '{item.ModuleKey}'"));
        }

        if (!payload.IsModuleLocal ||
            payload.ModuleLocalFunctionCount <= 0 ||
            !payload.HasValidPayloadHash() ||
            !payload.MirState.IsRestorable)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                $"invalid compiled MIR payload for module '{item.ModuleKey}'"));
        }

        compiledPayloads[item.ModuleKey] = payload;
        return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
    }

    private IReadOnlyDictionary<string, ModuleMirCompilationResult> CompileCurrentHirCompilationUnitToMir(
        string compilationKey,
        string fallbackModuleKey)
    {
        var moduleKeys = _moduleTypedArtifactRestorePlan?.Layers
            .SelectMany(static layer => layer.Modules)
            .Where(item => item.Action == ProjectModuleArtifactRestoreAction.Compile &&
                           string.Equals(
                               GetModuleCompilationCacheKey(item.ModuleKey),
                               compilationKey,
                               StringComparison.Ordinal))
            .Select(static item => item.ModuleKey)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (moduleKeys.Length == 0)
        {
            moduleKeys = [fallbackModuleKey];
        }

        return moduleKeys.ToDictionary(
            static moduleKey => moduleKey,
            CompileCurrentHirModuleToMir,
            StringComparer.Ordinal);
    }

    private ModuleMirCompilationResult CompileCurrentHirModuleToMir(string moduleKey)
    {
        if (_hirModule == null ||
            _moduleTypedSemanticSnapshot == null ||
            _symbolTable == null)
        {
            return ModuleMirCompilationResult.Failed(moduleKey, "missing current HIR or Types state");
        }

        var hirPayload = ModuleHirStateArtifactPayload.Create(
            moduleKey,
            _moduleTypedSemanticSnapshot,
            _hirModule,
            _hirParameterEffects,
            _hirCopyLikeTypeIds,
            _hirDynamicTypeKeys,
            _hirTypeDescriptors,
            _hirConstructorLayouts);
        if (!hirPayload.IsModuleLocal ||
            !hirPayload.HirState.TryRestore(out var hirModule, out _))
        {
            return ModuleMirCompilationResult.Failed(moduleKey, "missing module-local HIR slice");
        }

        var mirBuilder = new MirBuilder(
            CopyTypeSemantics.CreateSymbolTableCopyResolver(_symbolTable, _hirTypeDescriptors),
            _hirCopyLikeTypeIds,
            _hirDynamicTypeKeys,
            _symbolTable,
            _hirConstructorLayouts,
            _hirTypeDescriptors,
            _hirParameterEffects,
            _typeInferer?.Substitution);
        var mirModule = mirBuilder.Build(hirModule, _hirModule);
        var diagnostics = FilterTrustedPrecompiledDiagnostics(mirBuilder.Diagnostics).ToList();
        if (diagnostics.Any(static diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error))
        {
            return new ModuleMirCompilationResult(moduleKey, null, diagnostics);
        }

        var specializer = new MirGenericSpecializer(
            CopyTypeSemantics.CreateSymbolTableCopyResolver(_symbolTable, _hirTypeDescriptors),
            _hirCopyLikeTypeIds,
            _symbolTable);
        var optimizer = _options.EnableMirOptimizations
            ? MirOptimizer.CreateDefault(
                effectSummaries: _abilityInferer?.FunctionSummariesBySymbol)
            : null;
        var specialization = RunSpecializationLoop(mirModule, specializer, optimizer);
        mirModule = specialization.Module;
        diagnostics.AddRange(FilterTrustedPrecompiledDiagnostics(specialization.Diagnostics));
        if (diagnostics.Any(static diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error))
        {
            return new ModuleMirCompilationResult(moduleKey, null, diagnostics);
        }

        var validator = new MirValidator();
        if (!validator.Validate(mirModule))
        {
            diagnostics.AddRange(FilterTrustedPrecompiledDiagnostics(validator.Diagnostics));
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error))
        {
            return new ModuleMirCompilationResult(moduleKey, null, diagnostics);
        }

        return new ModuleMirCompilationResult(
            moduleKey,
            ModuleMirStateArtifactPayload.Create(moduleKey, _moduleTypedSemanticSnapshot, mirModule),
            diagnostics);
    }

    private static string FormatModuleMirCompilationFailure(ModuleMirCompilationResult compilation)
    {
        if (compilation.Diagnostics.Count > 0)
        {
            return string.Join(
                "; ",
                compilation.Diagnostics
                    .Where(static diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error)
                    .Take(4)
                    .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
        }

        return compilation.FailureReason ?? $"failed to compile MIR module '{compilation.ModuleKey}'";
    }

    private sealed record ModuleMirCompilationResult(
        string ModuleKey,
        ModuleMirStateArtifactPayload? Payload,
        IReadOnlyList<Diagnostic.Diagnostic> Diagnostics,
        string? FailureReason = null)
    {
        public bool Success => Payload != null &&
                               Diagnostics.All(static diagnostic =>
                                   diagnostic.Level != Diagnostic.DiagnosticLevel.Error);

        public static ModuleMirCompilationResult Failed(string moduleKey, string reason) =>
            new(moduleKey, null, [], reason);
    }

    private MirModule MergeRestoredMirModules(
        IReadOnlyList<(string ModuleKey, bool IsCompiled, MirModule Module)> modules)
    {
        if (modules.Count == 0)
        {
            return new MirModule();
        }

        var rootModuleKey = _ast == null ? "" : ToModuleDeclKey(_ast);
        var stateSource = modules
            .Where(entry => string.Equals(entry.ModuleKey, rootModuleKey, StringComparison.Ordinal))
            .OrderByDescending(static entry => entry.IsCompiled)
            .Select(static entry => entry.Module)
            .FirstOrDefault() ?? modules[0].Module;
        var moduleOrder = BuildHirModuleOrder();
        var orderedModules = modules
            .OrderBy(entry => moduleOrder.GetValueOrDefault(entry.ModuleKey, int.MaxValue))
            .ThenBy(static entry => entry.ModuleKey, StringComparer.Ordinal)
            .ToArray();
        return new MirModule
        {
            Name = stateSource.Name,
            PackageAlias = stateSource.PackageAlias,
            PackageInstanceKey = stateSource.PackageInstanceKey,
            Path = stateSource.Path.ToList(),
            Span = stateSource.Span,
            Functions = orderedModules
                .SelectMany(static entry => entry.Module.Functions.Where(static function => function.SymbolId.IsValid))
                .Concat(orderedModules.SelectMany(static entry =>
                    entry.Module.Functions.Where(static function => !function.SymbolId.IsValid)))
                .ToList(),
            DynamicTypeKeys = new Dictionary<int, string>(stateSource.DynamicTypeKeys),
            TypeDescriptors = new Dictionary<int, TypeDescriptor>(stateSource.TypeDescriptors),
            LinkLibraries = modules
                .SelectMany(static entry => entry.Module.LinkLibraries)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            CStructAccessors = new Dictionary<string, CStructAccessorInfo>(stateSource.CStructAccessors),
            ConstructorLayouts = stateSource.ConstructorLayouts.ToDictionary(
                static entry => entry.Key,
                static entry => entry.Value.ToList()),
            TraitImpls = stateSource.TraitImpls.ToList(),
            TraitInfos = stateSource.TraitInfos.ToList(),
            TypeAliases = stateSource.TypeAliases.ToList(),
            TypeConstructors = stateSource.TypeConstructors.ToList(),
            SpecializationFailures = stateSource.SpecializationFailures.ToList()
        };
    }

    private void SetMirModuleRestoreFallbackCounters(ProjectModuleArtifactRestoreExecutionSnapshot execution)
    {
        SetProfilingCounter("Mir.moduleRestore.applied", 0);
        SetProfilingCounter("Mir.moduleRestore.fallbackBuildMir", 1);
        SetProfilingCounter("Mir.moduleRestore.fallbackRestoredModules", execution.RestoredModules);
        SetProfilingCounter("Mir.moduleRestore.fallbackCompiledModules", execution.CompiledModules);
        SetProfilingCounter("Mir.moduleRestore.fallbackBlockedModules", execution.BlockedModules);
        SetProfilingCounter("Mir.moduleRestore.fallbackFailedModules", execution.FailedModules);
        EnsureModuleStageCounters("Mir");
    }
}
