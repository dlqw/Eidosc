using System.Collections.Concurrent;
using Eidosc.Ast.Declarations;
using Eidosc.Symbols;
using Eidosc.Debug;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private bool RunTypeInferer()
    {
        using (MeasureSubphase(CompilationPhase.Types, "create_inferer"))
        {
            _typeInferer = new TypeInferer(_symbolTable!)
            {
                ComptimeExecution = _comptimeExecution,
                BuildComptimeContext = _options.BuildComptimeContext,
                UsePrecompiledImportSignatureOnly = ShouldUsePrecompiledImportSignaturesOnly(),
                PreviousTypeDirectedCallableResolutionSnapshot = _options.PreviousTypeDirectedCallableResolutionSnapshot,
                PreviousAssociatedTypeProjectionSnapshot = _options.PreviousAssociatedTypeProjectionSnapshot,
                PreviousAssociatedConstProjectionSnapshot = _options.PreviousAssociatedConstProjectionSnapshot,
                PreviousTraitCheckSnapshot = _options.PreviousTraitCheckSnapshot
            };
            _options.BuildComptimeContext?.AttachSymbolTable(_symbolTable!);
            BuiltinTraits.RegisterBuiltinTraits(_symbolTable!);
            CaptureTypesEntrySymbolState();
        }

        bool inferSuccess;
        using (MeasureSubphase(CompilationPhase.Types, "infer"))
        {
            inferSuccess = _typeInferer.Infer(_ast!);
        }
        if (inferSuccess && _nameResolver != null)
        {
            var previousNamerDiagnosticCount = _nameResolver.Diagnostics.Count;
            bool bodyStageSuccess;
            using (MeasureSubphase(CompilationPhase.Types, "meta_body_stage"))
            {
                bodyStageSuccess = _nameResolver.ProcessDeferredMetaExpansionStage(_ast!, ClauseStage.Body);
            }

            _diagnostics.AddRange(FilterTrustedPrecompiledDiagnostics(
                _nameResolver.Diagnostics.Skip(previousNamerDiagnosticCount)));
            inferSuccess &= bodyStageSuccess;
            if (bodyStageSuccess && _nameResolver.LastMetaExpansionChanged)
            {
                CaptureTypesEntrySymbolState();
                using (MeasureSubphase(CompilationPhase.Types, "infer_after_meta_body"))
                {
                    inferSuccess = _typeInferer.Infer(_ast!);
                }
            }
        }
        if (inferSuccess && _nameResolver != null)
        {
            var previousNamerDiagnosticCount = _nameResolver.Diagnostics.Count;
            using (MeasureSubphase(CompilationPhase.Types, "package_meta_extensions_body"))
            {
                inferSuccess &= _nameResolver.ProcessPackageMetaExtensions(_ast!, ClauseStage.Body);
            }
            _diagnostics.AddRange(FilterTrustedPrecompiledDiagnostics(
                _nameResolver.Diagnostics.Skip(previousNamerDiagnosticCount)));
        }
        if (inferSuccess && _nameResolver != null)
        {
            var previousNamerDiagnosticCount = _nameResolver.Diagnostics.Count;
            using (MeasureSubphase(CompilationPhase.Types, "package_meta_analyzers"))
            {
                inferSuccess &= _nameResolver.ProcessPackageMetaAnalyzers(_ast!);
            }
            _diagnostics.AddRange(FilterTrustedPrecompiledDiagnostics(
                _nameResolver.Diagnostics.Skip(previousNamerDiagnosticCount)));
        }
        AddProfilingCounters(_typeInferer.GetProfilingCounters());
        _typeDirectedCallableResolutionSnapshot = _typeInferer.CreateTypeDirectedCallableResolutionSnapshot();
        SetProfilingCounter(
            "Types.callableResolutionSnapshot.entries",
            _typeDirectedCallableResolutionSnapshot.Entries.Count);
        _associatedTypeProjectionSnapshot = _typeInferer.CreateAssociatedTypeProjectionSnapshot();
        SetProfilingCounter(
            "Types.associatedTypeProjectionSnapshot.entries",
            _associatedTypeProjectionSnapshot.Entries.Count);
        _associatedConstProjectionSnapshot = _typeInferer.CreateAssociatedConstProjectionSnapshot();
        SetProfilingCounter(
            "Types.associatedConstProjectionSnapshot.entries",
            _associatedConstProjectionSnapshot.Entries.Count);
        _traitCheckSnapshot = _typeInferer.CreateTraitCheckSnapshot();
        SetProfilingCounter(
            "Types.traitCheckSnapshot.entries",
            _traitCheckSnapshot.Entries.Count);

        var diagnostics = FilterTrustedPrecompiledDiagnostics(_typeInferer.Diagnostics).ToList();
        using (MeasureSubphase(CompilationPhase.Types, "collect_diagnostics"))
        {
            _diagnostics.AddRange(diagnostics);
        }

        if (_debugContext.IsEnabled)
        {
            using (MeasureSubphase(CompilationPhase.Types, "debug_emit"))
            {
                _debugContext.Emit("inferred", TypeFormatter.FormatInferredTypes(_ast!, _typeInferer));

                if (_debugContext.Level >= DebugLevel.Diagnostic)
                {
                    _debugContext.Emit("substitution", TypeFormatter.FormatSubstitution(_typeInferer.Substitution, _ast));
            }
            }
        }

        return inferSuccess || !diagnostics.Any(diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error);
    }

    private IEnumerable<Diagnostic.Diagnostic> FilterTrustedPrecompiledDiagnostics(IEnumerable<Diagnostic.Diagnostic> diagnostics)
    {
        if (IsPrecompiledInput(_options.InputFile))
        {
            return diagnostics;
        }

        return diagnostics.Where(diagnostic => !diagnostic.Labels.Any(label => IsPrecompiledInput(label.Span.FilePath)));
    }

    private static bool IsPrecompiledInput(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return Eidosc.Semantic.PrecompiledModuleRegistry.IsStdlibSourcePath(filePath);
    }

    private bool RunTypeInfererAndFfi()
    {
        if (TryRestoreLiveState(CompilationPhase.Types))
        {
            return true;
        }

        if (TryRestoreTypesFromModulePayloads())
        {
            StoreLiveState(CompilationPhase.Types);
            return true;
        }

        if (!RunTypeInferer())
        {
            return false;
        }

        var success = RunFfiTypeValidator();
        if (success)
        {
            BuildFunctionEffectSummaries();
        }
        BuildTypedSemanticSnapshot();
        if (success)
        {
            _moduleTypesStatePayloads = ShouldCreateModuleStatePayloads()
                ? CreateModuleTypesStatePayloads()
                : null;
            StoreLiveState(CompilationPhase.Types);
        }

        return success;
    }

    private bool TryRestoreTypesFromModulePayloads()
    {
        var previousPayloads = _options.PreviousModuleTypesStatePayloads ?? [];
        if (_ast == null ||
            _symbolTable == null ||
            !CanRestoreTypesFromModulePayloads(_options.StopAtPhase) ||
            (previousPayloads.Count == 0 && _options.ModuleTypesStatePayloadLoader == null))
        {
            return false;
        }

        using (MeasureSubphase(CompilationPhase.Types, "module_types_restore_prepare"))
        {
            BuildTypesRestoreModulePlan();
        }

        if (_moduleTypedArtifactRestorePlan == null)
        {
            return false;
        }

        var payloadByModule = BuildTypesPayloadLookup(previousPayloads);
        var semanticByModule = BuildTypesSemanticLookup();
        var syntheticPayload = ProjectModuleArtifactRestorePayloadSnapshot.LoadTypesStatePayload(
            _moduleTypedArtifactRestorePlan,
            (moduleKey, _, _, _) => TryGetTypesPayload(moduleKey, payloadByModule, previousPayloads, semanticByModule, out var payload)
                ? payload.TypedSemantic
                : null);
        _moduleTypedArtifactRestorePayload = syntheticPayload;
        _moduleTypedArtifactRestorePlan = _moduleTypedArtifactRestorePlan.GateWithPayload(syntheticPayload);
        _moduleTypedArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
            _moduleTypedArtifactRestorePlan,
            ProjectModuleDependencySignatureRequirement.SemanticOnly);

        var restoredPayloads = new ConcurrentDictionary<string, ModuleTypesStatePayload>(StringComparer.Ordinal);
        var compiledPayloads = new ConcurrentDictionary<string, ModuleTypesStatePayload>(StringComparer.Ordinal);
        var moduleCompilations = new ConcurrentDictionary<string, Lazy<CompilationResult>>(StringComparer.Ordinal);
        _moduleTypedArtifactRestoreExecution = ProjectModuleArtifactRestoreExecutor.ExecuteAsync(
                _moduleTypedArtifactRestorePlan,
                (item, cancellationToken) => RestoreTypesPayloadModuleAsync(
                    item,
                    payloadByModule,
                    previousPayloads,
                    semanticByModule,
                    restoredPayloads,
                    cancellationToken),
                (item, cancellationToken) => CompileTypesPayloadModuleAsync(
                    item,
                    compiledPayloads,
                    moduleCompilations,
                    cancellationToken),
                maxDegreeOfParallelism: GetModuleArtifactRestoreMaxDegreeOfParallelism(_moduleTypedArtifactRestorePlan))
            .GetAwaiter()
            .GetResult();

        SetModuleArtifactRestoreCounters("Build.moduleTypedArtifactRestore", _moduleTypedArtifactRestorePlan);
        SetModuleArtifactRestoreExecutionCounters(
            "Build.moduleTypedArtifactRestoreExecution",
            _moduleTypedArtifactRestoreExecution);
        SetModuleArtifactRestorePayloadCounters(
            "Build.moduleTypedArtifactRestorePayload",
            _moduleTypedArtifactRestorePayload);

        if (_moduleTypedArtifactRestoreExecution.FailedModules > 0 ||
            _moduleTypedArtifactRestoreExecution.BlockedModules > 0 ||
            compiledPayloads.Count != _moduleTypedArtifactRestoreExecution.CompiledModules ||
            restoredPayloads.Count != _moduleTypedArtifactRestoreExecution.RestoredModules ||
            _moduleTypedArtifactRestoreExecution.CompiledModules +
            _moduleTypedArtifactRestoreExecution.RestoredModules == 0)
        {
            SetTypesModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
            return false;
        }

        var orderedPayloads = restoredPayloads
            .Select(static entry => (entry.Key, Payload: entry.Value, Priority: 0))
            .Concat(compiledPayloads.Select(static entry => (entry.Key, Payload: entry.Value, Priority: 1)))
            .OrderBy(static entry => entry.Priority)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => entry.Payload)
            .ToArray();
        _typeInferer = new TypeInferer(_symbolTable)
        {
            ComptimeExecution = _comptimeExecution,
            BuildComptimeContext = _options.BuildComptimeContext,
            UsePrecompiledImportSignatureOnly = ShouldUsePrecompiledImportSignaturesOnly()
        };
        _options.BuildComptimeContext?.AttachSymbolTable(_symbolTable);
        BuiltinTraits.RegisterBuiltinTraits(_symbolTable);
        CaptureTypesEntrySymbolState();
        var currentIdentities = _typesEntrySymbolIdentities!;
        var restoreContexts = new List<(ModuleTypesStatePayload Payload, LiveStateIdRemapper Remapper, bool IsIdentity)>(
            orderedPayloads.Length);
        var remapFailures = new List<string>();
        var nextTypeVariableOffset = 0;
        var nextValueVariableOffset = 0;
        foreach (var payload in orderedPayloads)
        {
            var resolution = LiveStateStableIdentityBuilder.PlanRemap(
                payload.SymbolIdentities,
                currentIdentities);
            if (!resolution.IsValid)
            {
                remapFailures.AddRange(resolution.Failures);
                continue;
            }

            var plan = LiveStateRemapPlan.FromResolution(resolution);
            restoreContexts.Add((
                payload,
                new LiveStateIdRemapper(plan, nextTypeVariableOffset, nextValueVariableOffset),
                plan.IsIdentity && nextTypeVariableOffset == 0 && nextValueVariableOffset == 0));
            nextTypeVariableOffset = checked(
                nextTypeVariableOffset + Math.Max(1, payload.TypeSubstitution.NextFreshVarIndex));
            nextValueVariableOffset = checked(
                nextValueVariableOffset + Math.Max(1, payload.TypeSubstitution.NextFreshValueVarIndex));
        }

        if (remapFailures.Count > 0 || restoreContexts.Count != orderedPayloads.Length)
        {
            SetTypesModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
            SetProfilingCounter("Types.moduleRestore.fallbackRemapFailures", remapFailures.Count);
            SetProfilingCounter(
                "Types.moduleRestore.fallbackRemapMissingCurrentSymbols",
                remapFailures.Count(static failure => failure.StartsWith("missing-current-symbol:", StringComparison.Ordinal)));
            SetProfilingCounter(
                "Types.moduleRestore.fallbackRemapDuplicatePreviousKeys",
                remapFailures.Count(static failure => failure.StartsWith("duplicate-previous-symbol-key:", StringComparison.Ordinal)));
            SetProfilingCounter(
                "Types.moduleRestore.fallbackRemapDuplicateCurrentKeys",
                remapFailures.Count(static failure => failure.StartsWith("duplicate-current-symbol-key:", StringComparison.Ordinal)));
            SetProfilingCounter(
                "Types.moduleRestore.fallbackRemapAmbiguousTypes",
                remapFailures.Count(static failure => failure.StartsWith("ambiguous-type-remap:", StringComparison.Ordinal)));
            var currentIdentityKeys = currentIdentities
                .Select(static identity => identity.StableKey.ToString())
                .ToHashSet(StringComparer.Ordinal);
            var missingCurrentIdentities = orderedPayloads
                .SelectMany(static payload => payload.SymbolIdentities)
                .Where(identity => !currentIdentityKeys.Contains(identity.StableKey.ToString()))
                .ToArray();
            foreach (var missingKind in missingCurrentIdentities
                         .GroupBy(static identity => identity.SymbolKind, StringComparer.Ordinal))
            {
                SetProfilingCounter(
                    $"Types.moduleRestore.fallbackRemapMissingCurrentKind.{missingKind.Key}",
                    missingKind.Count());
            }

            foreach (var missingRole in missingCurrentIdentities
                         .GroupBy(static identity => identity.StableKey.SymbolRole, StringComparer.Ordinal))
            {
                SetProfilingCounter(
                    $"Types.moduleRestore.fallbackRemapMissingCurrentRole.{missingRole.Key}",
                    missingRole.Count());
            }

            foreach (var mismatchReason in missingCurrentIdentities
                         .GroupBy(
                             identity => ClassifyStableIdentityMismatch(identity, currentIdentities),
                             StringComparer.Ordinal))
            {
                SetProfilingCounter(
                    $"Types.moduleRestore.fallbackRemapMissingCurrentReason.{mismatchReason.Key}",
                    mismatchReason.Count());
            }

            var duplicateCurrentIdentities = currentIdentities
                .GroupBy(static identity => identity.StableKey.ToString(), StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .SelectMany(static group => group.Skip(1))
                .ToArray();
            foreach (var duplicateKind in duplicateCurrentIdentities
                         .GroupBy(static identity => identity.SymbolKind, StringComparer.Ordinal))
            {
                SetProfilingCounter(
                    $"Types.moduleRestore.fallbackRemapDuplicateCurrentKind.{duplicateKind.Key}",
                    duplicateKind.Count());
            }

            foreach (var duplicateRole in duplicateCurrentIdentities
                         .GroupBy(static identity => identity.StableKey.SymbolRole, StringComparer.Ordinal))
            {
                SetProfilingCounter(
                    $"Types.moduleRestore.fallbackRemapDuplicateCurrentRole.{duplicateRole.Key}",
                    duplicateRole.Count());
            }
            return false;
        }

        var restoredSymbolStates = 0;
        foreach (var context in restoreContexts)
        {
            var symbolStateRestore = ModuleTypesStateRestorer.RestoreSymbolState(
                _symbolTable,
                context.Payload.SymbolState,
                context.Remapper);
            if (!symbolStateRestore.Applied)
            {
                SetTypesModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
                SetProfilingCounter("Types.moduleRestore.fallbackSymbolStateFailures", symbolStateRestore.Failures.Count);
                return false;
            }

            restoredSymbolStates += symbolStateRestore.RestoredSymbols;
        }

        var mergedTypeEnv = new Dictionary<SymbolId, TypeScheme>();
        var mergedSubstitution = new Substitution();
        var mergedSubstitutionBindings = new List<SubstitutionBinding>();
        var mergedValueSubstitutionBindings = new List<ValueSubstitutionBinding>();
        var mergedNextFreshTypeVariable = 0;
        var mergedNextFreshValueVariable = 0;
        var mergedFunctionTypeParameters = new Dictionary<SymbolId, IReadOnlyList<Eidosc.Types.Type>>();
        var mergedComptimeValues = new Dictionary<SymbolId, ComptimeValue>();
        var mergedConstraints = new List<TypeConstraint>();
        var mergedClosedCaseInjections = new Dictionary<SourceSpan, TypeInferer.ClosedCaseInjectionFact>();
        var mergedFunctionEffectSummaries = new Dictionary<SymbolId, FunctionEffectSummary>();
        var mergedMetaQueryCacheEntries = new List<MetaQueryCacheEntry>();
        var mergedMetaQueryDependencies = new List<MetaQueryDependency>();
        var restoredInferredTypes = 0;
        var restoredSymbolIds = 0;
        var restoredTypeEnvBindings = 0;
        var restoredSubstitutionBindings = 0;
        var restoredFunctionTypeParameterBindings = 0;
        var restoredComptimeValues = 0;
        var restoredConstraints = 0;
        foreach (var context in restoreContexts)
        {
            var payload = context.Payload;
            var result = ModuleTypesStateRestorer.RestoreState(
                _ast!,
                payload,
                context.Remapper,
                out var typeEnv,
                out var substitution,
                out var functionTypeParameters,
                out var comptimeValues,
                out var constraints);
            if (!result.Applied)
            {
                SetTypesModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
                return false;
            }

            foreach (var binding in typeEnv)
            {
                mergedTypeEnv[binding.Key] = binding.Value;
            }

            mergedSubstitutionBindings.AddRange(substitution.GetBindingsSnapshot());
            mergedValueSubstitutionBindings.AddRange(substitution.GetValueBindingsSnapshot());
            mergedNextFreshTypeVariable = Math.Max(
                mergedNextFreshTypeVariable,
                substitution.NextFreshVarIndex);
            mergedNextFreshValueVariable = Math.Max(
                mergedNextFreshValueVariable,
                substitution.NextFreshValueVarIndex);
            foreach (var binding in functionTypeParameters)
            {
                mergedFunctionTypeParameters[binding.Key] = binding.Value;
            }

            foreach (var binding in comptimeValues)
            {
                mergedComptimeValues[binding.Key] = binding.Value;
            }

            mergedConstraints.AddRange(constraints);
            if (!payload.MetaQueries.TryRestoreState(
                    context.Remapper,
                    out var metaQueryCacheEntries,
                    out var metaQueryDependencies,
                    out _))
            {
                SetTypesModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
                SetProfilingCounter("Types.moduleRestore.fallbackMetaQueryState", 1);
                return false;
            }
            mergedMetaQueryCacheEntries.AddRange(metaQueryCacheEntries);
            mergedMetaQueryDependencies.AddRange(metaQueryDependencies);
            if (!payload.ClosedCaseInjections.TryRestore(context.Remapper, out var closedCaseInjections))
            {
                SetTypesModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
                return false;
            }
            foreach (var injection in closedCaseInjections)
            {
                mergedClosedCaseInjections[injection.Key] = injection.Value;
            }
            if (!payload.FunctionEffects.TryRestore(context.Remapper, out var functionEffects))
            {
                SetTypesModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
                return false;
            }
            foreach (var binding in functionEffects)
            {
                mergedFunctionEffectSummaries[binding.Key] = binding.Value;
            }
            restoredInferredTypes += result.RestoredInferredTypes;
            restoredSymbolIds += result.RestoredSymbolIds;
            restoredTypeEnvBindings += result.RestoredTypeEnvBindings;
            restoredSubstitutionBindings += result.RestoredSubstitutionBindings;
            restoredFunctionTypeParameterBindings += result.RestoredFunctionTypeParameterBindings;
            restoredComptimeValues += result.RestoredComptimeValues;
            restoredConstraints += result.RestoredConstraints;
        }

        mergedSubstitution.RestoreFromSnapshot(
            mergedSubstitutionBindings,
            mergedNextFreshTypeVariable,
            mergedValueSubstitutionBindings,
            mergedNextFreshValueVariable);

        _typeInferer.RestoreTypesState(
            mergedTypeEnv,
            mergedSubstitution,
            mergedFunctionTypeParameters,
            mergedComptimeValues,
            mergedConstraints);
        if (!MetaQueryState.For(_symbolTable).TryRestoreState(
                mergedMetaQueryCacheEntries,
                mergedMetaQueryDependencies,
                out _))
        {
            SetTypesModuleRestoreFallbackCounters(_moduleTypedArtifactRestoreExecution);
            SetProfilingCounter("Types.moduleRestore.fallbackMetaQueryConflict", 1);
            return false;
        }
        _typeInferer.RestoreClosedCaseInjections(mergedClosedCaseInjections);
        _abilityInferer = new EffectInferer(_symbolTable);
        _abilityInferer.Restore(_ast, mergedFunctionEffectSummaries);
        _symbolTable.EnsureIdCountersAtLeast(
            orderedPayloads.Max(static payload => payload.NextSymbolId),
            orderedPayloads.Max(static payload => payload.NextTypeId),
            orderedPayloads.Max(static payload => payload.NextEffectId));
        _moduleTypedSemanticSnapshot = new ProjectModuleTypedSemanticSnapshot(
            ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
            restoreContexts
                .Select(context => RemapTypedSemanticNode(
                    context.Payload.TypedSemantic,
                    context.Remapper))
                .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
                .ToArray());
        _moduleTypedInvalidationPlan = ProjectModuleInvalidationPlan.FromTypedSemanticSignatures(
            _options.PreviousModuleTypedSemanticSnapshot,
            _moduleTypedSemanticSnapshot);
        if (_moduleBuildSchedule != null)
        {
            _moduleTypedExecutionPlan = ProjectModuleExecutionPlan.FromSchedule(
                _moduleBuildSchedule,
                _moduleTypedInvalidationPlan,
                ProjectModuleExecutionPlan.IsPrecompiledReadyArtifact);
            _moduleTypedArtifactReadinessPlan = CreateArtifactReadinessPlan(
                _moduleTypedExecutionPlan,
                ProjectModuleArtifactRequirement.SemanticTyped);
        }

        _typeDirectedCallableResolutionSnapshot = _typeInferer.CreateTypeDirectedCallableResolutionSnapshot();
        _associatedTypeProjectionSnapshot = _typeInferer.CreateAssociatedTypeProjectionSnapshot();
        _associatedConstProjectionSnapshot = _typeInferer.CreateAssociatedConstProjectionSnapshot();
        _traitCheckSnapshot = _typeInferer.CreateTraitCheckSnapshot();
        SetProfilingCounter("Types.moduleRestore.applied", 1);
        SetProfilingCounter("Types.moduleRestore.payloadModules", orderedPayloads.Length);
        SetProfilingCounter("Types.moduleRestore.restoredInferredTypes", restoredInferredTypes);
        SetProfilingCounter("Types.moduleRestore.restoredSymbolIds", restoredSymbolIds);
        SetProfilingCounter("Types.moduleRestore.restoredTypeEnvBindings", restoredTypeEnvBindings);
        SetProfilingCounter("Types.moduleRestore.restoredSubstitutionBindings", restoredSubstitutionBindings);
        SetProfilingCounter("Types.moduleRestore.restoredFunctionTypeParameterBindings", restoredFunctionTypeParameterBindings);
        SetProfilingCounter("Types.moduleRestore.restoredComptimeValues", restoredComptimeValues);
        SetProfilingCounter("Types.moduleRestore.restoredConstraints", restoredConstraints);
        SetProfilingCounter(
            "Types.moduleRestore.restoredMetaQueryCacheEntries",
            mergedMetaQueryCacheEntries.Select(static entry => entry.Key).Distinct(StringComparer.Ordinal).Count());
        SetProfilingCounter(
            "Types.moduleRestore.restoredMetaQueryDependencies",
            mergedMetaQueryDependencies
                .DistinctBy(static dependency => (
                    dependency.Key,
                    dependency.ResultHash,
                    dependency.CacheHit,
                    dependency.ResultBytes))
                .Count());
        SetProfilingCounter("Types.moduleRestore.restoredSymbolStates", restoredSymbolStates);
        SetProfilingCounter("Types.moduleRestore.remapIdentity", restoreContexts.All(static context => context.IsIdentity) ? 1 : 0);
        SetProfilingCounter("Types.moduleRestore.fallbackFullInfer", 0);
        SetProfilingCounter("Types.callableResolutionSnapshot.entries", _typeDirectedCallableResolutionSnapshot.Entries.Count);
        SetProfilingCounter("Types.associatedTypeProjectionSnapshot.entries", _associatedTypeProjectionSnapshot.Entries.Count);
        SetProfilingCounter("Types.associatedConstProjectionSnapshot.entries", _associatedConstProjectionSnapshot.Entries.Count);
        SetProfilingCounter("Types.traitCheckSnapshot.entries", _traitCheckSnapshot.Entries.Count);
        SetProfilingCounter("Build.moduleTypedSemanticSignatures.modules", _moduleTypedSemanticSnapshot.Nodes.Count);
        SetProfilingCounter(
            "Build.moduleTypedSemanticSignatures.declarations",
            _moduleTypedSemanticSnapshot.Nodes.Sum(static node => node.Declarations.Count));
        SetProfilingCounter("Build.moduleTypedInvalidation.changes", _moduleTypedInvalidationPlan.Changes.Count);
        SetProfilingCounter("Build.moduleTypedInvalidation.affected", _moduleTypedInvalidationPlan.AffectedModules.Count);
        SetProfilingCounter("Build.moduleTypedInvalidation.unchanged", _moduleTypedInvalidationPlan.UnchangedModules.Count);
        if (_moduleTypedExecutionPlan != null)
        {
            SetModuleExecutionPlanCounters("Build.moduleTypedExecution", _moduleTypedExecutionPlan);
            _moduleTypedParallelExecution = CreateModuleParallelExecutionSnapshot(
                CompilationPhase.Types,
                "moduleTypedParallelExecution",
                _moduleTypedExecutionPlan);
        }

        if (_moduleTypedArtifactReadinessPlan != null && _moduleTypedExecutionPlan != null)
        {
            SetModuleArtifactReadinessCounters("Build.moduleTypedArtifactReadiness", _moduleTypedArtifactReadinessPlan);
            _moduleTypedArtifactRestorePlan = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(
                _moduleTypedExecutionPlan,
                _moduleTypedArtifactReadinessPlan);
            SetModuleArtifactRestoreCounters("Build.moduleTypedArtifactRestore", _moduleTypedArtifactRestorePlan);
        }

        SetModuleStageExecutionCounters(
            "Types",
            _moduleTypedArtifactRestoreExecution,
            hasRestorePayload: true);
        var success = RunFfiTypeValidator();
        if (success)
        {
            var reusablePayloads = new Dictionary<string, ModuleTypesStatePayload>(StringComparer.Ordinal);
            foreach (var payload in previousPayloads
                         .Concat(orderedPayloads)
                         .Where(payload => IsCurrentTypesPayload(payload.ModuleKey, payload, semanticByModule)))
            {
                reusablePayloads[payload.ModuleKey] = payload;
            }

            _moduleTypesStatePayloads = reusablePayloads.Values
                .OrderBy(static payload => payload.ModuleKey, StringComparer.Ordinal)
                .ToArray();
        }

        return success;
    }

    private static string ClassifyStableIdentityMismatch(
        LiveStateSymbolIdentity previous,
        IReadOnlyList<LiveStateSymbolIdentity> current)
    {
        var candidates = current
            .Where(candidate =>
                string.Equals(candidate.SymbolKind, previous.SymbolKind, StringComparison.Ordinal) &&
                string.Equals(candidate.Name, previous.Name, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
        {
            return "kindOrName";
        }

        var declaration = previous.StableKey.Declaration;
        var samePackage = candidates.Where(candidate => string.Equals(
            candidate.StableKey.Declaration.Module.PackageInstanceKey,
            declaration.Module.PackageInstanceKey,
            StringComparison.Ordinal)).ToArray();
        if (samePackage.Length == 0)
        {
            return "package";
        }

        var sameModule = samePackage.Where(candidate => string.Equals(
            candidate.StableKey.Declaration.Module.ModulePath,
            declaration.Module.ModulePath,
            StringComparison.Ordinal)).ToArray();
        if (sameModule.Length == 0)
        {
            return "modulePath";
        }

        var sameSource = sameModule.Where(candidate => string.Equals(
            candidate.StableKey.Declaration.Module.NormalizedSourcePath,
            declaration.Module.NormalizedSourcePath,
            StringComparison.Ordinal)).ToArray();
        if (sameSource.Length == 0)
        {
            return "sourcePath";
        }

        var sameOverload = sameSource.Where(candidate => string.Equals(
            candidate.StableKey.Declaration.OverloadDiscriminator,
            declaration.OverloadDiscriminator,
            StringComparison.Ordinal)).ToArray();
        if (sameOverload.Length == 0)
        {
            return "overload";
        }

        var sameSpan = sameOverload.Where(candidate => string.Equals(
            candidate.StableKey.Declaration.SourceStableSpan,
            declaration.SourceStableSpan,
            StringComparison.Ordinal)).ToArray();
        if (sameSpan.Length == 0)
        {
            return "sourceSpan";
        }

        return sameSpan.Any(candidate => string.Equals(
            candidate.StableKey.SymbolRole,
            previous.StableKey.SymbolRole,
            StringComparison.Ordinal))
            ? "unknown"
            : "role";
    }

    private static ProjectModuleTypedSemanticNode RemapTypedSemanticNode(
        ProjectModuleTypedSemanticNode node,
        LiveStateIdRemapper remapper) =>
        node with
        {
            Declarations = node.Declarations
                .Select(declaration => declaration with
                {
                    SymbolId = remapper.RemapSymbol(declaration.SymbolId),
                    TypeId = remapper.RemapType(declaration.TypeId)
                })
                .ToArray()
        };

    private static bool CanRestoreTypesFromModulePayloads(CompilationPhase? stopAtPhase) =>
        stopAtPhase is CompilationPhase.Types or
            CompilationPhase.Effects or
            CompilationPhase.Hir or
            CompilationPhase.Mir or
            CompilationPhase.Borrow or
            CompilationPhase.Send or
            CompilationPhase.Llvm;

    private void BuildTypesRestoreModulePlan()
    {
        if (_moduleBuildSchedule == null ||
            _moduleInvalidationPlan == null)
        {
            EnsureNamerModulePlansForRestore();
        }

        if (_moduleBuildSchedule == null ||
            _moduleInvalidationPlan == null)
        {
            return;
        }

        _moduleTypedExecutionPlan = ProjectModuleExecutionPlan.FromSchedule(
            _moduleBuildSchedule,
            _moduleInvalidationPlan,
            ProjectModuleExecutionPlan.IsPrecompiledReadyArtifact);
        _moduleTypedArtifactReadinessPlan = CreateArtifactReadinessPlan(
            _moduleTypedExecutionPlan,
            ProjectModuleArtifactRequirement.SemanticOnly);
        if (_moduleTypedArtifactReadinessPlan == null)
        {
            return;
        }

        _moduleTypedArtifactRestorePlan = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(
            _moduleTypedExecutionPlan,
            _moduleTypedArtifactReadinessPlan,
            ProjectModuleArtifactRequirement.SemanticOnly);
        _moduleTypedArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
            _moduleTypedArtifactRestorePlan,
            ProjectModuleDependencySignatureRequirement.SemanticOnly);
    }

    private static Dictionary<string, ModuleTypesStatePayload> BuildTypesPayloadLookup(
        IReadOnlyList<ModuleTypesStatePayload> previousPayloads)
    {
        var result = new Dictionary<string, ModuleTypesStatePayload>(StringComparer.Ordinal);
        foreach (var payload in previousPayloads)
        {
            if (!payload.HasValidPayloadHash())
            {
                continue;
            }

            AddTypesPayloadLookupKey(result, payload.ModuleKey, payload);
            AddTypesPayloadLookupKey(result, payload.TypedSemantic.ModuleKey, payload);
        }

        return result;
    }

    private static void AddTypesPayloadLookupKey(
        Dictionary<string, ModuleTypesStatePayload> result,
        string key,
        ModuleTypesStatePayload payload)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            result.TryAdd(key, payload);
        }
    }

    private Dictionary<string, ProjectModuleSemanticSignatureNode> BuildTypesSemanticLookup()
    {
        var result = _moduleSemanticSignatureSnapshot?.Nodes.ToDictionary(
            static node => node.ModuleKey,
            StringComparer.Ordinal) ?? new Dictionary<string, ProjectModuleSemanticSignatureNode>(StringComparer.Ordinal);
        SetProfilingCounter("Types.moduleRestore.currentSemanticModules", result.Count);
        return result;
    }

    private bool TryGetTypesPayload(
        string moduleKey,
        IReadOnlyDictionary<string, ModuleTypesStatePayload> payloadByModule,
        IReadOnlyList<ModuleTypesStatePayload> previousPayloads,
        IReadOnlyDictionary<string, ProjectModuleSemanticSignatureNode> semanticByModule,
        out ModuleTypesStatePayload payload)
    {
        if (payloadByModule.TryGetValue(moduleKey, out payload!))
        {
            return IsCurrentTypesPayload(moduleKey, payload, semanticByModule);
        }

        if (_options.ModuleTypesStatePayloadLoader != null)
        {
            foreach (var candidate in previousPayloads.Where(payload =>
                         string.Equals(payload.ModuleKey, moduleKey, StringComparison.Ordinal) ||
                         string.Equals(payload.TypedSemantic.ModuleKey, moduleKey, StringComparison.Ordinal)))
            {
                var loaded = _options.ModuleTypesStatePayloadLoader(
                    candidate.TypedSemantic.ModuleKey,
                    ProjectModuleArtifactKinds.TypesStatePayload,
                    candidate.TypedSemantic.LocalSurfaceHash,
                    candidate.TypedSemantic.DependencyTypedSemanticHash);
                if (loaded is { SchemaVersion: ModuleTypesStatePayload.CurrentSchemaVersion } &&
                    loaded.HasValidPayloadHash() &&
                    IsCurrentTypesPayload(moduleKey, loaded, semanticByModule))
                {
                    payload = loaded;
                    return true;
                }
            }

            var typedNode = _options.PreviousModuleTypedSemanticSnapshot?.Nodes.FirstOrDefault(node =>
                string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal));
            if (typedNode != null)
            {
                var loaded = _options.ModuleTypesStatePayloadLoader(
                    typedNode.ModuleKey,
                    ProjectModuleArtifactKinds.TypesStatePayload,
                    typedNode.LocalSurfaceHash,
                    typedNode.DependencyTypedSemanticHash);
                if (loaded is { SchemaVersion: ModuleTypesStatePayload.CurrentSchemaVersion } &&
                    loaded.HasValidPayloadHash() &&
                    IsCurrentTypesPayload(moduleKey, loaded, semanticByModule))
                {
                    payload = loaded;
                    return true;
                }
            }
        }

        if (previousPayloads.Count == 1)
        {
            payload = previousPayloads[0];
            return IsCurrentTypesPayload(moduleKey, payload, semanticByModule);
        }

        payload = null!;
        return false;
    }

    private static bool IsCurrentTypesPayload(
        string moduleKey,
        ModuleTypesStatePayload payload,
        IReadOnlyDictionary<string, ProjectModuleSemanticSignatureNode> semanticByModule)
    {
        if (!string.Equals(moduleKey, payload.ModuleKey, StringComparison.Ordinal) &&
            !string.Equals(moduleKey, payload.TypedSemantic.ModuleKey, StringComparison.Ordinal))
        {
            return false;
        }

        return semanticByModule.ContainsKey(moduleKey);
    }

    private ValueTask<ProjectModuleExecutionItemResult> RestoreTypesPayloadModuleAsync(
        ProjectModuleArtifactRestoreItem item,
        IReadOnlyDictionary<string, ModuleTypesStatePayload> payloadByModule,
        IReadOnlyList<ModuleTypesStatePayload> previousPayloads,
        IReadOnlyDictionary<string, ProjectModuleSemanticSignatureNode> semanticByModule,
        ConcurrentDictionary<string, ModuleTypesStatePayload> restoredPayloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetTypesPayload(item.ModuleKey, payloadByModule, previousPayloads, semanticByModule, out var payload))
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed("missing Types state payload"));
        }

        if (!payload.HasValidPayloadHash() ||
            !ModuleTypesStateRestorer.TryRestoreTypeEnv(payload, out _) ||
            !payload.TypeSubstitution.TryRestoreSubstitution(out _) ||
            !payload.FunctionTypeParameters.TryRestoreFunctionTypeParameters(out _) ||
            !payload.ComptimeValues.TryRestoreComptimeValues(out _) ||
            !payload.Constraints.TryRestoreConstraints(out _) ||
            !payload.FunctionEffects.TryRestore(remapper: null, out _))
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed("invalid Types state payload"));
        }

        restoredPayloads[item.ModuleKey] = payload;
        return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
    }

    private ValueTask<ProjectModuleExecutionItemResult> CompileTypesPayloadModuleAsync(
        ProjectModuleArtifactRestoreItem item,
        ConcurrentDictionary<string, ModuleTypesStatePayload> compiledPayloads,
        ConcurrentDictionary<string, Lazy<CompilationResult>> moduleCompilations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var compilationKey = GetModuleCompilationCacheKey(item.ModuleKey);
        var compilation = moduleCompilations.GetOrAdd(
            compilationKey,
            _ => new Lazy<CompilationResult>(
                () => CompileModuleToPhase(item.ModuleKey, CompilationPhase.Types),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (!compilation.Success)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                FormatSubcompilationFailure(compilation)));
        }

        var payload = compilation.ModuleTypesStatePayloads?.FirstOrDefault(candidate =>
            string.Equals(candidate.ModuleKey, item.ModuleKey, StringComparison.Ordinal) ||
            string.Equals(candidate.TypedSemantic.ModuleKey, item.ModuleKey, StringComparison.Ordinal));
        if (payload == null)
        {
            return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                $"missing compiled Types payload for module '{item.ModuleKey}'"));
        }

        if (_ast != null &&
            compilation.Ast != null &&
            compilation.TypeInferer != null &&
            compilation.SymbolTable != null &&
            compilation.ModuleTypedSemanticSnapshot != null &&
            !AstStableNodeTraversal.MatchesCompilationRoot(
                _ast,
                item.ModuleKey,
                payload.AstState.ModuleIdentityKey))
        {
            var namerPayload = compilation.ModuleNamerStatePayloads?.FirstOrDefault(candidate =>
                string.Equals(candidate.ModuleKey, item.ModuleKey, StringComparison.Ordinal) ||
                string.Equals(candidate.ModuleIdentityKey, item.ModuleKey, StringComparison.Ordinal) ||
                string.Equals(ToDisplayModuleKey(candidate.ModuleIdentityKey), item.ModuleKey, StringComparison.Ordinal));
            if (namerPayload == null)
            {
                return ValueTask.FromResult(ProjectModuleExecutionItemResult.Failed(
                    $"missing compiled Namer seed payload for module '{item.ModuleKey}'"));
            }

            var moduleStableNodes = AstStableNodeTraversal
                .Enumerate(compilation.Ast)
                .Where(static entry => entry.Ordinal != 0)
                .ToArray();
            var sourcePaths = compilation.ModuleGraphSnapshot?.Nodes
                .FirstOrDefault(node => string.Equals(node.ModuleKey, item.ModuleKey, StringComparison.Ordinal))?
                .SourcePaths ?? payload.AstState.SourcePaths;
            payload = ModuleTypesStatePayload.Create(
                item.ModuleKey,
                compilation.ModuleTypedSemanticSnapshot,
                namerPayload.SymbolIdentities,
                namerPayload.SymbolTable,
                compilation.Ast,
                compilation.TypeInferer,
                compilation.EffectInferer,
                compilation.SymbolTable,
                sourcePaths,
                moduleStableNodes);
        }

        compiledPayloads[item.ModuleKey] = payload;
        return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
    }

    private void SetTypesModuleRestoreFallbackCounters(ProjectModuleArtifactRestoreExecutionSnapshot execution)
    {
        SetProfilingCounter("Types.moduleRestore.applied", 0);
        SetProfilingCounter("Types.moduleRestore.fallbackFullInfer", 1);
        SetProfilingCounter("Types.moduleRestore.fallbackRestoredModules", execution.RestoredModules);
        SetProfilingCounter("Types.moduleRestore.fallbackCompiledModules", execution.CompiledModules);
        SetProfilingCounter("Types.moduleRestore.fallbackBlockedModules", execution.BlockedModules);
        SetProfilingCounter("Types.moduleRestore.fallbackFailedModules", execution.FailedModules);
        EnsureModuleStageCounters("Types");
    }

    private void BuildTypedSemanticSnapshot()
    {
        if (!_options.EnableDetailedProfiling ||
            _moduleGraphSnapshot == null ||
            _symbolTable == null)
        {
            return;
        }

        using (MeasureSubphase(CompilationPhase.Types, "typed_semantic_signature_snapshot"))
        {
            _moduleTypedSemanticSnapshot = ProjectModuleTypedSemanticSnapshot.FromGraphSnapshot(
                _moduleGraphSnapshot,
                _symbolTable,
                _options.LanguageVersion,
                CreateModuleSignatureFlagsHash());
            _moduleTypedInvalidationPlan = ProjectModuleInvalidationPlan.FromTypedSemanticSignatures(
                _options.PreviousModuleTypedSemanticSnapshot,
                _moduleTypedSemanticSnapshot);
            if (_moduleBuildSchedule != null)
            {
                _moduleTypedExecutionPlan = ProjectModuleExecutionPlan.FromSchedule(
                    _moduleBuildSchedule,
                    _moduleTypedInvalidationPlan,
                    ProjectModuleExecutionPlan.IsPrecompiledReadyArtifact);
            _moduleTypedArtifactReadinessPlan = CreateArtifactReadinessPlan(
                _moduleTypedExecutionPlan,
                ProjectModuleArtifactRequirement.SemanticTyped);
            }
        }

        SetProfilingCounter("Build.moduleTypedSemanticSignatures.modules", _moduleTypedSemanticSnapshot.Nodes.Count);
        SetProfilingCounter(
            "Build.moduleTypedSemanticSignatures.declarations",
            _moduleTypedSemanticSnapshot.Nodes.Sum(static node => node.Declarations.Count));
        BuildModuleDependencySignatureSnapshot(CompilationPhase.Types, "Build.moduleDependencySignatures");
        SetProfilingCounter("Build.moduleTypedInvalidation.changes", _moduleTypedInvalidationPlan.Changes.Count);
        SetProfilingCounter("Build.moduleTypedInvalidation.affected", _moduleTypedInvalidationPlan.AffectedModules.Count);
        SetProfilingCounter("Build.moduleTypedInvalidation.unchanged", _moduleTypedInvalidationPlan.UnchangedModules.Count);
        if (_moduleTypedExecutionPlan != null)
        {
            SetModuleExecutionPlanCounters("Build.moduleTypedExecution", _moduleTypedExecutionPlan);
            _moduleTypedParallelExecution = CreateModuleParallelExecutionSnapshot(
                CompilationPhase.Types,
                "moduleTypedParallelExecution",
                _moduleTypedExecutionPlan);
        }

        if (_moduleTypedArtifactReadinessPlan != null && _moduleTypedExecutionPlan != null)
        {
            SetModuleArtifactReadinessCounters("Build.moduleTypedArtifactReadiness", _moduleTypedArtifactReadinessPlan);
            _moduleTypedArtifactRestorePlan = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(
                _moduleTypedExecutionPlan,
                _moduleTypedArtifactReadinessPlan,
                ProjectModuleArtifactRequirement.SemanticTyped);
            _moduleTypedArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
                _moduleTypedArtifactRestorePlan,
                ProjectModuleDependencySignatureRequirement.SemanticOnly);
            SetModuleArtifactRestoreCounters("Build.moduleTypedArtifactRestore", _moduleTypedArtifactRestorePlan);
            _moduleTypedArtifactRestoreExecution = ExecuteModuleArtifactRestorePlan(
                _moduleTypedArtifactRestorePlan,
                payload: null,
                ProjectModuleDependencySignatureRequirement.SemanticTypedMemberAndMir);
            SetModuleArtifactRestoreExecutionCounters(
                "Build.moduleTypedArtifactRestoreExecution",
                _moduleTypedArtifactRestoreExecution);
            SetModuleStageExecutionCounters(
                "Types",
                _moduleTypedArtifactRestoreExecution,
                hasRestorePayload: false);
        }
    }

    private bool RunFfiTypeValidator()
    {
        var validator = new FfiTypeValidator();
        if (!validator.Validate(_ast!, _nameResolver?.LinkLibraries))
        {
            _diagnostics.AddRange(validator.Diagnostics);
            return false;
        }

        if (validator.Diagnostics.Count > 0)
        {
            _diagnostics.AddRange(validator.Diagnostics);
        }

        return true;
    }

    private bool RunEffectInferer()
    {
        if (TryRestoreLiveState(CompilationPhase.Effects))
        {
            return true;
        }

        if (_abilityInferer == null)
        {
            using (MeasureSubphase(CompilationPhase.Effects, "create_inferer"))
            {
                _abilityInferer = new EffectInferer(_symbolTable!);
            }

            using (MeasureSubphase(CompilationPhase.Effects, "infer"))
            {
                _abilityInferer.Infer(_ast!);
            }
        }

        EffectAuthorizationChecker authorizationChecker;
        using (MeasureSubphase(CompilationPhase.Effects, "create_authorization_checker"))
        {
            authorizationChecker = new EffectAuthorizationChecker(
                _symbolTable!,
                _abilityInferer.FunctionSummaries,
                allowImplicitEntryRootCapabilities: !string.Equals(
                    _options.LanguageVersion,
                    EidosLanguageVersions.Current,
                    StringComparison.Ordinal));
        }

        bool authorizationSuccess;
        using (MeasureSubphase(CompilationPhase.Effects, "authorization_check"))
        {
            authorizationSuccess = authorizationChecker.Check(_ast!);
        }

        using (MeasureSubphase(CompilationPhase.Effects, "collect_diagnostics"))
        {
            _diagnostics.AddRange(authorizationChecker.Diagnostics);
        }

        if (_debugContext.IsEnabled)
        {
            using (MeasureSubphase(CompilationPhase.Effects, "debug_emit"))
            {
                _debugContext.Emit("abilities", TypeFormatter.FormatAbilities(_ast!, _abilityInferer));
            }
        }

        if (authorizationSuccess)
        {
            StoreLiveState(CompilationPhase.Effects);
        }

        return authorizationSuccess;
    }

    private void BuildFunctionEffectSummaries()
    {
        _abilityInferer = new EffectInferer(_symbolTable!);
        _abilityInferer.Infer(_ast!);
    }
}
