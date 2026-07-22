using Eidosc.Diagnostic;
using Eidosc.Ast.Declarations;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private bool RunMirBuilder()
    {
        if (TryRestoreLiveState(CompilationPhase.Mir))
        {
            return true;
        }

        if (TryRestoreMirFromModulePayloads())
        {
            RefreshCompilationLiveStatePayload(CompilationPhase.Mir);
            StoreLiveState(CompilationPhase.Mir);
            return true;
        }

        MirBuilder mirBuilder;
        using (MeasureSubphase(CompilationPhase.Mir, "create_builder"))
        {
            mirBuilder = _symbolTable == null
                ? new MirBuilder(null, null, null, null, null, null, _hirParameterEffects, _typeInferer?.Substitution)
                : new MirBuilder(
                    CopyTypeSemantics.CreateSymbolTableCopyResolver(_symbolTable, _hirTypeDescriptors),
                    _hirCopyLikeTypeIds,
                    _hirDynamicTypeKeys,
                    _symbolTable,
                    _hirConstructorLayouts,
                    _hirTypeDescriptors,
                    _hirParameterEffects,
                    _typeInferer?.Substitution);
        }
        using (MeasureSubphase(CompilationPhase.Mir, "build_mir"))
        {
            _mirModule = mirBuilder.Build(_hirModule!);
        }
        AddMirModuleShapeCounters("Mir.build.output", _mirModule!);
        var buildFingerprintSnapshot = _options.EnableDetailedProfiling
            ? AddMirFunctionFingerprintCounters("Mir.build.output", _mirModule!)
            : null;
        var buildFingerprints = buildFingerprintSnapshot?.Functions ?? [];
        List<Diagnostic.Diagnostic> filteredDiagnostics;
        using (MeasureSubphase(CompilationPhase.Mir, "collect_diagnostics"))
        {
            filteredDiagnostics = FilterTrustedPrecompiledDiagnostics(mirBuilder.Diagnostics).ToList();
            _diagnostics.AddRange(filteredDiagnostics);
        }
        var hasMirErrors = filteredDiagnostics.Any(diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error);

        MirModule? mirBeforeOptimization = null;
        IReadOnlyList<string> optimizationPasses = [];
        var optimizationApplied = false;
        var specializerRunCount = 0;
        var specializerChangedIterationCount = 0;
        var optimizerChangedIterationCount = 0;
        string? specializationLoopConvergenceReason = null;
        string? optimizationSkipReason = null;

        if (hasMirErrors)
        {
            if (_options.EnableMirOptimizations)
            {
                optimizationSkipReason = DiagnosticMessages.MirOptimizationSkippedDueToLoweringErrors;
            }
            else
            {
                optimizationSkipReason = DiagnosticMessages.MirOptimizationDisabledByOption;
            }
        }
        else
        {
            _borrowMirModule = _mirModule;

            MirGenericSpecializer genericSpecializer;
            using (MeasureSubphase(CompilationPhase.Mir, "create_generic_specializer"))
            {
                genericSpecializer = _symbolTable == null
                    ? new MirGenericSpecializer(measureSubphase: MeasureMirSpecializerSubphase)
                    : new MirGenericSpecializer(
                        CopyTypeSemantics.CreateSymbolTableCopyResolver(_symbolTable, _hirTypeDescriptors),
                        _hirCopyLikeTypeIds,
                        _symbolTable,
                        MeasureMirSpecializerSubphase);
            }

            MirOptimizer? optimizer = null;
            if (_options.EnableMirOptimizations)
            {
                mirBeforeOptimization = _mirModule;
                optimizer = MirOptimizer.CreateDefault(
                    MeasureMirOptimizerSubphase,
                    _abilityInferer?.FunctionSummariesBySymbol);
                optimizationPasses = optimizer.PassNames;
                optimizationApplied = true;
            }
            else
            {
                optimizationSkipReason = DiagnosticMessages.MirOptimizationDisabledByOption;
            }

            using (MeasureSubphase(CompilationPhase.Mir, "specialization_loop"))
            {
                var inputFunctionCount = _mirModule!.Functions.Count;
                var specializationResult = RunSpecializationLoop(
                    _mirModule,
                    genericSpecializer,
                    optimizer,
                    MeasureMirSpecializerSubphase);
                _mirModule = specializationResult.Module;
                specializerRunCount = specializationResult.SpecializerRunCount;
                specializerChangedIterationCount = specializationResult.SpecializerChangedIterationCount;
                optimizerChangedIterationCount = specializationResult.OptimizerChangedIterationCount;
                specializationLoopConvergenceReason = specializationResult.ConvergenceReason;
                var filteredSpecializationDiagnostics =
                    FilterTrustedPrecompiledDiagnostics(specializationResult.Diagnostics).ToList();
                _diagnostics.AddRange(filteredSpecializationDiagnostics);
                hasMirErrors |= filteredSpecializationDiagnostics.Any(diagnostic =>
                    diagnostic.Level == Diagnostic.DiagnosticLevel.Error);
                SetProfilingCounter("Mir.specialization_loop.input_functions", inputFunctionCount);
                SetProfilingCounter("Mir.specialization_loop.output_functions", _mirModule.Functions.Count);
                SetProfilingCounter(
                    "Mir.specialization_loop.generated_specializations",
                    CountGeneratedSpecializationFunctions(_mirModule));
                SetProfilingCounter("Mir.specialization_loop.specializer_runs", specializerRunCount);
                SetProfilingCounter(
                    "Mir.specialization_loop.specializer_changed_iterations",
                    specializerChangedIterationCount);
                SetProfilingCounter(
                    "Mir.specialization_loop.optimizer_changed_iterations",
                    optimizerChangedIterationCount);
                AddMirSpecializerStats(specializationResult.SpecializerStats);
                AddMirOptimizerPassStats(specializationResult.OptimizerPassStats);
                AddMirModuleShapeCounters("Mir.specialization_loop.output", _mirModule);
                if (_options.EnableDetailedProfiling)
                {
                    var specializationFingerprintSnapshot = AddMirFunctionFingerprintCounters(
                        "Mir.specialization_loop.output",
                        _mirModule);
                    _mirFunctionFingerprints = specializationFingerprintSnapshot;
                    AddMirFunctionFingerprintComparisonCounters(
                        "Mir.specialization_loop",
                        "mir-specialization-loop",
                        buildFingerprintSnapshot?.ModuleFingerprint ?? "",
                        specializationFingerprintSnapshot.ModuleFingerprint,
                        buildFingerprints,
                        specializationFingerprintSnapshot.Functions);
                }
            }

            if (_options.EnableDetailedProfiling && _mirFunctionFingerprints == null)
            {
                _mirFunctionFingerprints = MirFunctionFingerprintSnapshot.FromModule(_mirModule);
            }

            if (_options.EnableDetailedProfiling &&
                _moduleTypedSemanticSnapshot != null &&
                _mirFunctionFingerprints != null)
            {
                using (MeasureSubphase(CompilationPhase.Mir, "module_mir_artifact_snapshot"))
                {
                    _moduleMirArtifactSnapshot = ProjectModuleMirArtifactSnapshot.Create(
                        _moduleTypedSemanticSnapshot,
                        _mirFunctionFingerprints);
                }

                SetProfilingCounter("Build.moduleMirArtifacts.modules", _moduleMirArtifactSnapshot.Nodes.Count);
                SetProfilingCounter(
                    "Build.moduleMirArtifacts.uniqueArtifactHashes",
                    _moduleMirArtifactSnapshot.Nodes
                        .Select(static node => node.MirArtifactHash)
                        .Distinct(StringComparer.Ordinal)
                        .Count());
                BuildModuleDependencySignatureSnapshot(CompilationPhase.Mir, "Build.moduleDependencySignatures");
                if (_moduleTypedExecutionPlan != null)
                {
                    _moduleTypedArtifactReadinessPlan = CreateArtifactReadinessPlan(_moduleTypedExecutionPlan);
                    if (_moduleTypedArtifactReadinessPlan != null)
                    {
                        SetModuleArtifactReadinessCounters(
                            "Build.moduleTypedArtifactReadiness",
                            _moduleTypedArtifactReadinessPlan);
                        _moduleTypedArtifactRestorePlan = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(
                            _moduleTypedExecutionPlan,
                            _moduleTypedArtifactReadinessPlan);
                        _moduleTypedArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
                            _moduleTypedArtifactRestorePlan,
                            ProjectModuleDependencySignatureRequirement.SemanticTypedMemberAndMir);
                        SetModuleArtifactRestoreCounters(
                            "Build.moduleTypedArtifactRestore",
                            _moduleTypedArtifactRestorePlan);
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
                        SetModuleStageExecutionCounters(
                            "Hir",
                            _moduleTypedArtifactRestoreExecution,
                            hasRestorePayload: false);
                        SetModuleStageExecutionCounters(
                            "Mir",
                            _moduleTypedArtifactRestoreExecution,
                            hasRestorePayload: false);
                        TryLoadModuleTypedArtifactRestorePayload();
                    }
                }
            }

            if (_options.EnableDetailedProfiling &&
                _options.PreviousMirFunctionFingerprintSnapshot != null &&
                _mirFunctionFingerprints != null)
            {
                _mirFunctionFingerprintDiff = AddMirFunctionFingerprintComparisonCounters(
                    "Mir.previous_build",
                    "mir",
                    _options.PreviousMirFunctionFingerprintSnapshot.ModuleFingerprint,
                    _mirFunctionFingerprints.ModuleFingerprint,
                    _options.PreviousMirFunctionFingerprintSnapshot.Functions,
                    _mirFunctionFingerprints.Functions);
                _mirFunctionWorklist = AddFunctionWorklistCounters(
                    "Mir.previous_build",
                    _mirFunctionFingerprintDiff);
                SetProfilingCounter(
                    "Mir.previous_build.previous_module_fingerprint",
                    StableCounterFromHash(_options.PreviousMirFunctionFingerprintSnapshot.ModuleFingerprint));
                SetProfilingCounter(
                    "Mir.previous_build.current_module_fingerprint",
                    StableCounterFromHash(_mirFunctionFingerprints.ModuleFingerprint));
            }

            if (!hasMirErrors)
            {
                using (MeasureSubphase(CompilationPhase.Mir, "validate_mir"))
                {
                    var validator = new MirValidator();
                    if (!validator.Validate(_mirModule!))
                    {
                        var filteredValidatorDiagnostics =
                            FilterTrustedPrecompiledDiagnostics(validator.Diagnostics).ToList();
                        _diagnostics.AddRange(filteredValidatorDiagnostics);
                        hasMirErrors = filteredValidatorDiagnostics.Any(diagnostic =>
                            diagnostic.Level == Diagnostic.DiagnosticLevel.Error);
                    }
                }
            }
        }

        if (!hasMirErrors && _nameResolver != null)
        {
            var previousNamerDiagnosticCount = _nameResolver.Diagnostics.Count;
            bool layoutStageSuccess;
            using (MeasureSubphase(CompilationPhase.Mir, "meta_layout_stage"))
            {
                layoutStageSuccess = _nameResolver.ProcessDeferredMetaExpansionStage(_ast!, ClauseStage.Layout);
            }
            _diagnostics.AddRange(FilterTrustedPrecompiledDiagnostics(
                _nameResolver.Diagnostics.Skip(previousNamerDiagnosticCount)));
            hasMirErrors |= !layoutStageSuccess;
        }

        if (!hasMirErrors && _nameResolver != null)
        {
            var previousNamerDiagnosticCount = _nameResolver.Diagnostics.Count;
            bool layoutExtensionSuccess;
            using (MeasureSubphase(CompilationPhase.Mir, "package_meta_extensions_layout"))
            {
                layoutExtensionSuccess = _nameResolver.ProcessPackageMetaExtensions(_ast!, ClauseStage.Layout);
            }
            _diagnostics.AddRange(FilterTrustedPrecompiledDiagnostics(
                _nameResolver.Diagnostics.Skip(previousNamerDiagnosticCount)));
            hasMirErrors |= !layoutExtensionSuccess;
        }

        if (_debugContext.IsEnabled)
        {
            using (MeasureSubphase(CompilationPhase.Mir, "debug_emit"))
            {
                if (mirBeforeOptimization != null)
                {
                    _debugContext.Emit("mir_before_opt", MirFormatter.FormatMir(mirBeforeOptimization));
                }

                _debugContext.Emit("mir", MirFormatter.FormatMir(_mirModule));
                _debugContext.Emit(
                    "mir_optimization",
                    MirFormatter.FormatMirOptimization(
                        _options.EnableMirOptimizations,
                        optimizationApplied,
                        optimizationPasses,
                        mirBeforeOptimization,
                        _mirModule,
                        optimizationSkipReason,
                        specializerRunCount,
                        specializerChangedIterationCount,
                        optimizerChangedIterationCount,
                        specializationLoopConvergenceReason));

                if (_options.EmitCfg)
                {
                    foreach (var func in _mirModule.Functions)
                    {
                        var cfg = new ControlFlowGraph(func);
                        var dotContent = cfg.ToDot();
                        _debugContext.Emit($"cfg_{func.Name}.dot", dotContent);
                    }
                }
            }
        }

        if (!hasMirErrors)
        {
            RefreshCompilationLiveStatePayload(CompilationPhase.Mir);
            StoreLiveState(CompilationPhase.Mir);
        }

        return !hasMirErrors;
    }

    internal readonly record struct SpecializationLoopResult(
        MirModule Module,
        IReadOnlyList<Diagnostic.Diagnostic> Diagnostics,
        int SpecializerRunCount,
        int SpecializerChangedIterationCount,
        int OptimizerChangedIterationCount,
        string ConvergenceReason,
        MirGenericSpecializerStats SpecializerStats,
        IReadOnlyList<MirOptimizationPassStats> OptimizerPassStats);

    private IDisposable MeasureMirSpecializerSubphase(string name)
    {
        return MeasureSubphase(CompilationPhase.Mir, name);
    }

    private IDisposable MeasureMirOptimizerSubphase(string name)
    {
        return MeasureSubphase(CompilationPhase.Mir, $"loop.optimizer.{name}");
    }

    internal static SpecializationLoopResult RunSpecializationLoop(
        MirModule module,
        MirGenericSpecializer specializer,
        MirOptimizer? optimizer,
        Func<string, IDisposable>? measureSubphase = null)
    {
        const int maxIterations = 10;
        var diagnostics = new List<Diagnostic.Diagnostic>();
        var diagnosticKeys = new HashSet<string>(StringComparer.Ordinal);
        var specializerRunCount = 0;
        var specializerChangedIterationCount = 0;
        var optimizerChangedIterationCount = 0;
        var specializationDirty = true;
        var specializerStats = new MirGenericSpecializerStats();
        var optimizerPassStats = new List<MirOptimizationPassStats>();

        for (var i = 0; i < maxIterations; i++)
        {
            MirModule specialized = module;
            var specializerChanged = false;
            if (specializationDirty)
            {
                ulong beforeSpecializerFingerprint;
                using (MeasureSpecializationLoopSubphase(measureSubphase, "loop.specializer_input_fingerprint"))
                {
                    beforeSpecializerFingerprint = ComputeConvergenceHash(module);
                }

                using (MeasureSpecializationLoopSubphase(measureSubphase, "loop.specializer_run"))
                {
                    specialized = specializer.Run(module);
                }
                specializerStats.Add(specializer.Stats.Snapshot());
                specializerRunCount++;
                CollectSpecializerDiagnostics(specializer, diagnostics, diagnosticKeys);

                ulong afterSpecializerFingerprint;
                using (MeasureSpecializationLoopSubphase(measureSubphase, "loop.specializer_fingerprint"))
                {
                    afterSpecializerFingerprint = ComputeConvergenceHash(specialized);
                }

                specializerChanged = afterSpecializerFingerprint != beforeSpecializerFingerprint;
                if (specializerChanged)
                {
                    specializerChangedIterationCount++;
                }
            }

            var optimizationChanged = false;
            var optimizationAffectsSpecialization = false;
            if (optimizer != null)
            {
                using (MeasureSpecializationLoopSubphase(measureSubphase, "loop.optimizer"))
                {
                    var optimizationResult = optimizer.OptimizeWithResult(specialized);
                    specialized = optimizationResult.Module;
                    optimizationChanged = optimizationResult.Changed;
                    optimizationAffectsSpecialization = optimizationResult.ChangeKind.AffectsSpecialization();
                    optimizerPassStats.AddRange(optimizationResult.PassStats);
                }

                if (optimizationChanged)
                {
                    optimizerChangedIterationCount++;
                }
            }

            if (!specializerChanged && !optimizationChanged)
            {
                return new SpecializationLoopResult(
                    specialized,
                    diagnostics,
                    specializerRunCount,
                    specializerChangedIterationCount,
                    optimizerChangedIterationCount,
                    "fixed-point",
                    specializerStats,
                    optimizerPassStats);
            }

            if (!optimizationAffectsSpecialization)
            {
                return new SpecializationLoopResult(
                    specialized,
                    diagnostics,
                    specializerRunCount,
                    specializerChangedIterationCount,
                    optimizerChangedIterationCount,
                    optimizer != null && optimizationChanged
                        ? "dirty-worklist-local-optimizer"
                        : "dirty-worklist-specializer",
                    specializerStats,
                    optimizerPassStats);
            }

            specializationDirty = true;
            module = specialized;
        }

        diagnostics.Add(CreateSpecializationLoopNonConvergenceDiagnostic(
            maxIterations,
            specializerRunCount,
            specializerChangedIterationCount,
            optimizerChangedIterationCount));
        return new SpecializationLoopResult(
            module,
            diagnostics,
            specializerRunCount,
            specializerChangedIterationCount,
            optimizerChangedIterationCount,
            "max-iterations",
            specializerStats,
            optimizerPassStats);
    }

    private void AddMirSpecializerStats(MirGenericSpecializerStats stats)
    {
        AddProfilingCounter("Mir.specializer.initial_rewrite_queue_entries", stats.InitialRewriteQueueEntries);
        AddProfilingCounter("Mir.specializer.rewrite_queue_dequeues", stats.RewriteQueueDequeues);
        AddProfilingCounter("Mir.specializer.rewrite_queue_max_depth", stats.RewriteQueueMaxDepth);
        AddProfilingCounter("Mir.specializer.cloned_working_functions", stats.ClonedWorkingFunctions);
        AddProfilingCounter("Mir.specializer.rewrite_visited_functions", stats.RewriteVisitedFunctions);
        AddProfilingCounter("Mir.specializer.rewrite_single_block_functions", stats.RewriteSingleBlockFunctions);
        AddProfilingCounter("Mir.specializer.rewrite_multi_block_functions", stats.RewriteMultiBlockFunctions);
        AddProfilingCounter("Mir.specializer.rewrite_iterations", stats.RewriteIterations);
        AddProfilingCounter("Mir.specializer.rewrite_blocks_scanned", stats.RewriteBlocksScanned);
        AddProfilingCounter("Mir.specializer.rewrite_instructions_scanned", stats.RewriteInstructionsScanned);
        AddProfilingCounter("Mir.specializer.function_rewrite_summaries_built", stats.FunctionRewriteSummariesBuilt);
        AddProfilingCounter("Mir.specializer.function_rewrite_summary_candidate_blocks", stats.FunctionRewriteSummaryCandidateBlocks);
        AddProfilingCounter("Mir.specializer.function_rewrite_summary_candidate_instructions", stats.FunctionRewriteSummaryCandidateInstructions);
        AddProfilingCounter("Mir.specializer.dirty_rewrite_queue_entries", stats.DirtyRewriteQueueEntries);
        AddProfilingCounter("Mir.specializer.dirty_rewrite_queue_skipped_specializations", stats.DirtyRewriteQueueSkippedSpecializations);
        AddProfilingCounter("Mir.specializer.dirty_rewrite_queue_noop_dequeues", stats.DirtyRewriteQueueNoOpDequeues);
        AddProfilingCounter("Mir.specializer.dirty_rewrite_full_scan_fallbacks", stats.DirtyRewriteFullScanFallbacks);
        AddProfilingCounter("Mir.specializer.dirty_rewrite_candidate_block_functions", stats.DirtyRewriteCandidateBlockFunctions);
        AddProfilingCounter("Mir.specializer.dirty_rewrite_candidate_instruction_functions", stats.DirtyRewriteCandidateInstructionFunctions);
        AddProfilingCounter("Mir.specializer.dirty_rewrite_candidate_instructions_visited", stats.DirtyRewriteCandidateInstructionsVisited);
        AddProfilingCounter("Mir.specializer.local_type_map_builds", stats.LocalTypeMapBuilds);
        AddProfilingCounter("Mir.specializer.local_type_concretize_calls", stats.LocalTypeConcretizeCalls);
        AddProfilingCounter("Mir.specializer.local_refreshes", stats.LocalRefreshes);
        AddProfilingCounter("Mir.specializer.operand_refreshes", stats.OperandRefreshes);
        AddProfilingCounter("Mir.specializer.return_type_propagations", stats.ReturnTypePropagations);
        AddProfilingCounter("Mir.specializer.specialization_cache_hits", stats.SpecializationCacheHits);
        AddProfilingCounter("Mir.specializer.specializations_created", stats.SpecializationsCreated);
        AddProfilingCounter("Mir.specializer.specialization_rejections", stats.SpecializationRejections);
        AddProfilingCounter("Mir.specializer.enqueued_specializations", stats.EnqueuedSpecializations);
        AddProfilingCounter("Mir.specializer.template_call_rewrites", stats.TemplateCallRewrites);
        AddProfilingCounter("Mir.specializer.state_transfer_clones", stats.StateTransferClones);
        AddProfilingCounter("Mir.specializer.state_merge_clones", stats.StateMergeClones);
        AddProfilingCounter("Mir.specializer.state_storage_clones", stats.StateStorageClones);
        AddProfilingCounter("Mir.specializer.state_clone_entries", stats.StateCloneEntries);
        AddProfilingCounter("Mir.specializer.state_transfer_pool_hits", stats.StateTransferPoolHits);
        AddProfilingCounter("Mir.specializer.combine_bound_argument_lists", stats.CombineBoundArgumentLists);
        AddProfilingCounter("Mir.specializer.clone_operand_lists", stats.CloneOperandLists);
        AddProfilingCounter("Mir.specializer.clone_operand_list_items", stats.CloneOperandListItems);
        AddProfilingCounter("Mir.specializer.function_ref_rewrites", stats.FunctionRefRewrites);
        AddProfilingCounter("Mir.specializer.type_binding_cache_hits", stats.TypeBindingCacheHits);
        AddProfilingCounter("Mir.specializer.type_binding_cache_misses", stats.TypeBindingCacheMisses);
        AddProfilingCounter("Mir.specializer.meaningful_signature_cache_hits", stats.MeaningfulSignatureCacheHits);
        AddProfilingCounter("Mir.specializer.meaningful_signature_cache_misses", stats.MeaningfulSignatureCacheMisses);
    }

    private void AddMirOptimizerPassStats(IReadOnlyList<MirOptimizationPassStats> passStats)
    {
        foreach (var group in passStats.GroupBy(static stat => (stat.PassIndex, stat.PassName)))
        {
            var counterPrefix = $"Mir.optimizer.pass.{group.Key.PassIndex}.{group.Key.PassName}";
            AddProfilingCounter($"{counterPrefix}.runs", group.Count());
            AddProfilingCounter($"{counterPrefix}.changed_runs", group.Count(static stat => stat.Changed));
            AddProfilingCounter($"{counterPrefix}.max_change_kind", group.Max(static stat => (int)stat.ChangeKind));
            AddProfilingCounter($"{counterPrefix}.last_input_functions", group.Last().InputFunctionCount);
            AddProfilingCounter($"{counterPrefix}.last_output_functions", group.Last().OutputFunctionCount);
        }
    }

    private void AddMirModuleShapeCounters(string prefix, MirModule module)
    {
        var functionCount = module.Functions.Count;
        var externalFunctionCount = 0;
        var runtimeWordAbiFunctionCount = 0;
        var generatedSpecializationCount = 0;
        var basicBlockCount = 0L;
        var instructionCount = 0L;
        var terminatorCount = 0L;
        var localCount = 0L;
        var parameterCount = 0L;
        var maxBlocksPerFunction = 0;
        var maxInstructionsPerFunction = 0;
        var maxLocalsPerFunction = 0;

        foreach (var function in module.Functions)
        {
            if (function.IsExternal)
            {
                externalFunctionCount++;
            }

            if (function.IsRuntimeWordAbi)
            {
                runtimeWordAbiFunctionCount++;
            }

            if (!string.IsNullOrWhiteSpace(function.Name) &&
                function.Name.Contains(WellKnownStrings.InternalNames.SpecializationMarker, StringComparison.Ordinal))
            {
                generatedSpecializationCount++;
            }

            var functionInstructionCount = 0;
            foreach (var block in function.BasicBlocks)
            {
                basicBlockCount++;
                functionInstructionCount += block.Instructions.Count;
                instructionCount += block.Instructions.Count;
                if (block.Terminator != null)
                {
                    terminatorCount++;
                }
            }

            localCount += function.Locals.Count;
            parameterCount += function.Locals.Count(static local => local.IsParameter);
            maxBlocksPerFunction = Math.Max(maxBlocksPerFunction, function.BasicBlocks.Count);
            maxInstructionsPerFunction = Math.Max(maxInstructionsPerFunction, functionInstructionCount);
            maxLocalsPerFunction = Math.Max(maxLocalsPerFunction, function.Locals.Count);
        }

        SetProfilingCounter($"{prefix}.functions", functionCount);
        SetProfilingCounter($"{prefix}.external_functions", externalFunctionCount);
        SetProfilingCounter($"{prefix}.runtime_word_abi_functions", runtimeWordAbiFunctionCount);
        SetProfilingCounter($"{prefix}.generated_specializations", generatedSpecializationCount);
        SetProfilingCounter($"{prefix}.basic_blocks", basicBlockCount);
        SetProfilingCounter($"{prefix}.instructions", instructionCount);
        SetProfilingCounter($"{prefix}.terminators", terminatorCount);
        SetProfilingCounter($"{prefix}.locals", localCount);
        SetProfilingCounter($"{prefix}.parameters", parameterCount);
        SetProfilingCounter($"{prefix}.max_blocks_per_function", maxBlocksPerFunction);
        SetProfilingCounter($"{prefix}.max_instructions_per_function", maxInstructionsPerFunction);
        SetProfilingCounter($"{prefix}.max_locals_per_function", maxLocalsPerFunction);
    }

    private MirFunctionFingerprintSnapshot AddMirFunctionFingerprintCounters(string prefix, MirModule module)
    {
        MirFunctionFingerprintSnapshot snapshot;
        using (MeasureSubphase(CompilationPhase.Mir, $"{prefix}.function_fingerprints"))
        {
            snapshot = MirFunctionFingerprintSnapshot.FromModule(module);
        }

        var fingerprints = snapshot.Functions;
        var duplicateHashGroups = fingerprints
            .GroupBy(static fingerprint => fingerprint.BodyHash, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .ToList();
        SetProfilingCounter($"{prefix}.function_fingerprints", fingerprints.Count);
        SetProfilingCounter(
            $"{prefix}.unique_function_fingerprints",
            fingerprints.Select(static fingerprint => fingerprint.BodyHash).Distinct(StringComparer.Ordinal).Count());
        SetProfilingCounter(
            $"{prefix}.duplicate_function_fingerprint_groups",
            duplicateHashGroups.Count);
        SetProfilingCounter(
            $"{prefix}.max_functions_per_fingerprint",
            duplicateHashGroups.Count == 0 ? 1 : duplicateHashGroups.Max(static group => group.Count()));
        SetProfilingCounter($"{prefix}.module_fingerprint", StableCounterFromHash(snapshot.ModuleFingerprint));
        return snapshot;
    }

    private FunctionFingerprintDiffSnapshot AddMirFunctionFingerprintComparisonCounters(
        string prefix,
        string kind,
        string previousModuleFingerprint,
        string currentModuleFingerprint,
        IReadOnlyList<MirFunctionFingerprint> before,
        IReadOnlyList<MirFunctionFingerprint> after)
    {
        var snapshot = FunctionFingerprintDiffSnapshot.Create(
            kind,
            previousModuleFingerprint,
            currentModuleFingerprint,
            before.Select(static fingerprint => (fingerprint.FunctionKey, fingerprint.BodyHash)),
            after.Select(static fingerprint => (fingerprint.FunctionKey, fingerprint.BodyHash)));

        SetProfilingCounter($"{prefix}.fingerprint_unchanged_functions", snapshot.Count(FunctionFingerprintDiffStatus.Unchanged));
        SetProfilingCounter($"{prefix}.fingerprint_changed_functions", snapshot.Count(FunctionFingerprintDiffStatus.Changed));
        SetProfilingCounter($"{prefix}.fingerprint_added_functions", snapshot.Count(FunctionFingerprintDiffStatus.Added));
        SetProfilingCounter($"{prefix}.fingerprint_removed_functions", snapshot.Count(FunctionFingerprintDiffStatus.Removed));
        return snapshot;
    }

    private static IDisposable MeasureSpecializationLoopSubphase(
        Func<string, IDisposable>? measureSubphase,
        string name)
    {
        return measureSubphase?.Invoke(name) ?? NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        private NullDisposable()
        {
        }

        public void Dispose()
        {
        }
    }

    internal static Diagnostic.Diagnostic CreateSpecializationLoopNonConvergenceDiagnostic(
        int maxIterations,
        int specializerRunCount,
        int specializerChangedIterationCount = 0,
        int optimizerChangedIterationCount = 0)
    {
        return Diagnostic.Diagnostic.Error(
                DiagnosticMessages.MirSpecializationLoopDidNotConverge(maxIterations),
                "E5311")
            .WithHelp(DiagnosticMessages.MirSpecializationLoopDidNotConvergeHelp)
            .WithMetadata("phase", CompilationPhase.Mir.ToString())
            .WithMetadata("reason", "specialization-loop-not-converged")
            .WithMetadata(
                "maxIterations",
                maxIterations.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithMetadata(
                "specializerRunCount",
                specializerRunCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithMetadata(
                "specializerChangedIterationCount",
                specializerChangedIterationCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithMetadata(
                "optimizerChangedIterationCount",
                optimizerChangedIterationCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static void CollectSpecializerDiagnostics(
        MirGenericSpecializer specializer,
        List<Diagnostic.Diagnostic> diagnostics,
        HashSet<string> diagnosticKeys)
    {
        foreach (var diagnostic in specializer.Diagnostics)
        {
            if (diagnosticKeys.Add(CreateDiagnosticKey(diagnostic)))
            {
                diagnostics.Add(diagnostic);
            }
        }
    }

    private static string CreateDiagnosticKey(Diagnostic.Diagnostic diagnostic)
    {
        var primaryLabel = diagnostic.Labels.FirstOrDefault();
        var span = primaryLabel?.Span;
        return string.Join(
            "\u001f",
            diagnostic.Level,
            diagnostic.Code ?? "",
            diagnostic.Message,
            span?.Location.Position.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            span?.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            string.Join(
                "\u001e",
                diagnostic.Metadata
                    .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                    .Select(static entry => $"{entry.Key}\u001d{entry.Value}")));
    }

    private static int CountGeneratedSpecializationFunctions(MirModule module)
    {
        var count = 0;
        foreach (var function in module.Functions)
        {
            if (!string.IsNullOrWhiteSpace(function.Name) &&
                function.Name.Contains(WellKnownStrings.InternalNames.SpecializationMarker, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
