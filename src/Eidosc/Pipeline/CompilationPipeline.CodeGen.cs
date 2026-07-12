using Eidosc.Symbols;
using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Semantic;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private bool RunLlvmGenerator()
    {
        if (TryRestoreNativeObjectGroupsFromPreviousArtifacts())
        {
            return true;
        }

        if (TryRestoreLlvmIrTextFromPreviousArtifacts())
        {
            return true;
        }

        if (TryRestoreChangedLlvmIrTextFromSelectedFunctions())
        {
            return true;
        }

        if (TryRestoreChangedNativeObjectGroupsFromSelectedFunctions())
        {
            return true;
        }

        MirToLlvmConverter converter;
        using (MeasureSubphase(CompilationPhase.Llvm, "create_converter"))
        {
            converter = CreateMirToLlvmConverter();
        }

        using (MeasureSubphase(CompilationPhase.Llvm, "convert_module"))
        {
            _llvmModule = converter.Convert(_mirModule!);
            _llvmModule.LinkLibraryPaths.AddRange(_options.ConfigFfiLibraryPaths);
            _llvmModule.NativeIncludePaths.AddRange(_options.ConfigFfiIncludePaths);
            _llvmModule.NativeSources.AddRange(_options.ConfigFfiNativeSources);
            _llvmModule.LinkerFlags.AddRange(_options.ConfigFfiLinkerFlags);
        }
        AddLlvmModuleShapeCounters("Llvm.convert.output", _llvmModule);
        if (_options.EnableDetailedProfiling)
        {
            _llvmFunctionFragments = AddLlvmFunctionFragmentSnapshotCounters("Llvm.convert.output", _llvmModule);
            _llvmFunctionFingerprints = AddLlvmFunctionFingerprintCounters("Llvm.convert.output", _llvmFunctionFragments);
            if (_options.PreviousLlvmFunctionFragmentSnapshot != null)
            {
                _llvmFunctionFragmentRestorePlan = AddLlvmFunctionFragmentRestoreCounters(
                    "Llvm.previous_build",
                    _options.PreviousLlvmFunctionFragmentSnapshot,
                    _llvmFunctionFragments);
            }

            if (_options.PreviousLlvmFunctionFingerprintSnapshot != null)
            {
                _llvmFunctionFingerprintDiff = AddLlvmFunctionFingerprintComparisonCounters(
                    "Llvm.previous_build",
                    "llvm",
                    _options.PreviousLlvmFunctionFingerprintSnapshot.ModuleFingerprint,
                    _llvmFunctionFingerprints.ModuleFingerprint,
                    _options.PreviousLlvmFunctionFingerprintSnapshot.Functions,
                    _llvmFunctionFingerprints.Functions);
                _llvmFunctionWorklist = AddFunctionWorklistCounters(
                    "Llvm.previous_build",
                    _llvmFunctionFingerprintDiff);
                SetProfilingCounter(
                    "Llvm.previous_build.previous_module_fingerprint",
                    StableCounterFromHash(_options.PreviousLlvmFunctionFingerprintSnapshot.ModuleFingerprint));
                SetProfilingCounter(
                    "Llvm.previous_build.current_module_fingerprint",
                    StableCounterFromHash(_llvmFunctionFingerprints.ModuleFingerprint));
            }
        }
        using (MeasureSubphase(CompilationPhase.Llvm, "collect_diagnostics"))
        {
            _diagnostics.AddRange(converter.Diagnostics);
        }

        TargetInfo? targetInfo;
        LlvmEmitter emitter;
        using (MeasureSubphase(CompilationPhase.Llvm, "resolve_target"))
        {
            targetInfo = ResolveLlvmTargetInfo();
        }
        if (_options.EnableDetailedProfiling)
        {
            var effectiveDataLayout = targetInfo?.DataLayout ??
                "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128";
            var effectiveTargetTriple = targetInfo?.Triple ?? "x86_64-pc-linux-gnu";
            var effectiveTargetInfo = targetInfo ?? TargetInfo.Default;
            _llvmModuleEnvelope = AddLlvmModuleEnvelopeSnapshotCounters(
                "Llvm.convert.output",
                _llvmModule,
                effectiveDataLayout,
                effectiveTargetTriple);
            if (_llvmFunctionFragments != null)
            {
                _llvmCodegenUnitPlan = AddLlvmCodegenUnitPlanCounters(
                    "Llvm.convert.output",
                    _llvmModuleEnvelope,
                    _llvmFunctionFragments,
                    effectiveTargetInfo);
                if (_llvmFunctionFragmentRestorePlan != null)
                {
                    _llvmObjectGroupRestorePlan = AddLlvmObjectGroupRestorePlanCounters(
                        "Llvm.previous_build",
                        _llvmCodegenUnitPlan,
                        _llvmFunctionFragmentRestorePlan);
                }
            }
        }
        using (MeasureSubphase(CompilationPhase.Llvm, "create_emitter"))
        {
            emitter = new LlvmEmitter();
        }
        if (_options.EnableDetailedProfiling &&
            _options.PreviousLlvmFunctionFragmentSnapshot != null &&
            _llvmFunctionFragmentRestorePlan != null &&
            _llvmModuleEnvelope != null &&
            _llvmFunctionFragments != null &&
            TryEmitLlvmIrFromFunctionFragmentRestore(
                _options.PreviousLlvmFunctionFragmentSnapshot,
                _llvmFunctionFragments,
                _llvmFunctionFragmentRestorePlan,
                _llvmModuleEnvelope,
                _llvmModule.Functions
                    .Select(static function => string.IsNullOrWhiteSpace(function.Name)
                        ? "anon:<unknown>"
                        : $"name:{function.Name}")
                    .ToArray()))
        {
            SetProfilingCounter("Llvm.previous_build.fragment_restore_emit_skipped_emitter", 1);
        }
        else
        {
            using (MeasureSubphase(CompilationPhase.Llvm, "emit_ir"))
            {
                _llvmIrText = emitter.Emit(_llvmModule, targetInfo?.DataLayout, targetInfo?.Triple);
            }

            if (_options.EnableDetailedProfiling &&
                _options.PreviousLlvmFunctionFragmentSnapshot != null &&
                _llvmFunctionFragmentRestorePlan != null &&
                _llvmModuleEnvelope != null &&
                _llvmFunctionFragments != null &&
                _llvmIrText != null)
            {
                ApplyLlvmFunctionFragmentRestoreExecution(
                    _options.PreviousLlvmFunctionFragmentSnapshot,
                    _llvmFunctionFragments,
                    _llvmFunctionFragmentRestorePlan,
                    _llvmModuleEnvelope,
                    _llvmModule.Functions
                        .Select(static function => string.IsNullOrWhiteSpace(function.Name)
                            ? "anon:<unknown>"
                            : $"name:{function.Name}")
                        .ToArray(),
                    _llvmIrText);
            }
        }

        if (_debugContext.IsEnabled)
        {
            using (MeasureSubphase(CompilationPhase.Llvm, "debug_emit"))
            {
                _debugContext.Emit("llvm_ir", _llvmIrText ?? "");
            }
        }

        return !converter.Diagnostics.Any(diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error);
    }

    private bool TryRestoreNativeObjectGroupsFromPreviousArtifacts()
    {
        if (!_options.AllowNativeObjectGroupRestore ||
            !_options.EnableDetailedProfiling ||
            _mirFunctionFingerprints == null ||
            _options.PreviousMirFunctionFingerprintSnapshot == null ||
            _options.PreviousLlvmFunctionFragmentSnapshot == null ||
            _options.PreviousLlvmModuleEnvelopeSnapshot == null ||
            _options.PreviousLlvmCodegenUnitPlanSnapshot == null)
        {
            return false;
        }

        if (!string.Equals(
                _options.PreviousMirFunctionFingerprintSnapshot.ModuleFingerprint,
                _mirFunctionFingerprints.ModuleFingerprint,
                StringComparison.Ordinal))
        {
            SetProfilingCounter("Llvm.previous_build.native_object_group_restore_module_fingerprint_match", 0);
            return false;
        }

        using (MeasureSubphase(CompilationPhase.Llvm, "restore_native_object_groups_from_previous_fragments"))
        {
            _llvmFunctionFragments = _options.PreviousLlvmFunctionFragmentSnapshot;
            _llvmModuleEnvelope = _options.PreviousLlvmModuleEnvelopeSnapshot;
            _llvmCodegenUnitPlan = _options.PreviousLlvmCodegenUnitPlanSnapshot;
            _llvmFunctionFingerprints = new LlvmFunctionFingerprintSnapshot(
                LlvmFunctionFingerprintSnapshot.CurrentSchemaVersion,
                _llvmFunctionFragments.Functions
                    .Select(static fragment => new LlvmFunctionFingerprint(
                        fragment.FunctionKey,
                        fragment.BodyHash,
                        fragment.BasicBlockCount,
                        fragment.InstructionCount,
                        fragment.ParameterCount))
                    .ToArray());
            _llvmFunctionFingerprintDiff = AddLlvmFunctionFingerprintComparisonCounters(
                "Llvm.previous_build",
                "llvm",
                _llvmFunctionFingerprints.ModuleFingerprint,
                _llvmFunctionFingerprints.ModuleFingerprint,
                _llvmFunctionFingerprints.Functions,
                _llvmFunctionFingerprints.Functions);
            _llvmFunctionWorklist = AddFunctionWorklistCounters(
                "Llvm.previous_build",
                _llvmFunctionFingerprintDiff);
            _llvmFunctionFragmentRestorePlan = LlvmFunctionFragmentRestorePlanSnapshot.Create(
                _llvmFunctionFragments,
                _llvmFunctionFragments);
            _llvmObjectGroupRestorePlan = AddLlvmObjectGroupRestorePlanCounters(
                "Llvm.previous_build",
                _llvmCodegenUnitPlan,
                _llvmFunctionFragmentRestorePlan);
            _llvmFunctionFragmentRestoreResult = new LlvmFunctionFragmentRestoreResultSnapshot(
                "llvm-function-fragment-restore-result-snapshot-v1",
                RestoredFragments: _llvmFunctionFragments.Functions.Count,
                RebuiltFragments: 0,
                RemovedFragments: 0,
                FallbackRebuildFragments: 0,
                RestoredIrBytes: _llvmFunctionFragments.Functions.Sum(static fragment => fragment.IrFragment.Length),
                RebuiltIrBytes: 0,
                OutputModuleFingerprint: _llvmFunctionFragments.ModuleFingerprint,
                MatchesCurrentIr: true,
                Applied: true);
        }

        SetProfilingCounter("Llvm.previous_build.native_object_group_restore_hits", 1);
        SetProfilingCounter("Llvm.previous_build.native_object_group_restore_module_fingerprint_match", 1);
        SetProfilingCounter("Llvm.previous_build.native_object_group_restore_functions", _llvmFunctionFragments.Functions.Count);
        SetProfilingCounter(
            "Llvm.previous_build.native_object_group_restore_ir_bytes",
            _llvmFunctionFragments.Functions.Sum(static fragment => fragment.IrFragment.Length));
        SetProfilingCounter(
            "Llvm.previous_build.native_object_group_restore_envelope_fingerprint",
            StableCounterFromHash(_llvmModuleEnvelope.EnvelopeFingerprint));
        SetProfilingCounter(
            "Llvm.previous_build.native_object_group_restore_fragment_fingerprint",
            StableCounterFromHash(_llvmFunctionFragments.ModuleFingerprint));
        return true;
    }

    private bool TryRestoreLlvmIrTextFromPreviousArtifacts()
    {
        if (!_options.AllowLlvmIrTextRestore ||
            !_options.EnableDetailedProfiling ||
            _mirFunctionFingerprints == null ||
            _options.PreviousMirFunctionFingerprintSnapshot == null ||
            _options.PreviousLlvmFunctionFragmentSnapshot == null ||
            _options.PreviousLlvmModuleEnvelopeSnapshot == null)
        {
            return false;
        }

        if (!string.Equals(
                _options.PreviousMirFunctionFingerprintSnapshot.ModuleFingerprint,
                _mirFunctionFingerprints.ModuleFingerprint,
                StringComparison.Ordinal))
        {
            SetProfilingCounter("Llvm.previous_build.ir_text_restore_module_fingerprint_match", 0);
            return false;
        }

        LlvmRecomposedModuleSnapshot restored;
        using (MeasureSubphase(CompilationPhase.Llvm, "restore_ir_text_from_previous_fragments"))
        {
            restored = LlvmFunctionFingerprintBuilder.RecomposeModule(
                _options.PreviousLlvmModuleEnvelopeSnapshot,
                _options.PreviousLlvmFunctionFragmentSnapshot);
        }

        _llvmIrText = restored.IrText;
        _llvmFunctionFragments = _options.PreviousLlvmFunctionFragmentSnapshot;
        _llvmModuleEnvelope = _options.PreviousLlvmModuleEnvelopeSnapshot;
        _llvmFunctionFingerprints = new LlvmFunctionFingerprintSnapshot(
            LlvmFunctionFingerprintSnapshot.CurrentSchemaVersion,
            _llvmFunctionFragments.Functions
                .Select(static fragment => new LlvmFunctionFingerprint(
                    fragment.FunctionKey,
                    fragment.BodyHash,
                    fragment.BasicBlockCount,
                    fragment.InstructionCount,
                    fragment.ParameterCount))
                .ToArray());
        _llvmFunctionFingerprintDiff = AddLlvmFunctionFingerprintComparisonCounters(
            "Llvm.previous_build",
            "llvm",
            _llvmFunctionFingerprints.ModuleFingerprint,
            _llvmFunctionFingerprints.ModuleFingerprint,
            _llvmFunctionFingerprints.Functions,
            _llvmFunctionFingerprints.Functions);
        _llvmFunctionWorklist = AddFunctionWorklistCounters(
            "Llvm.previous_build",
            _llvmFunctionFingerprintDiff);
        SetProfilingCounter("Llvm.previous_build.ir_text_restore_hits", 1);
        SetProfilingCounter("Llvm.previous_build.ir_text_restore_module_fingerprint_match", 1);
        SetProfilingCounter("Llvm.previous_build.ir_text_restore_functions", _llvmFunctionFragments.Functions.Count);
        SetProfilingCounter("Llvm.previous_build.ir_text_restore_ir_bytes", _llvmIrText.Length);
        SetProfilingCounter(
            "Llvm.previous_build.ir_text_restore_output_module_fingerprint",
            StableCounterFromHash(restored.FunctionFragmentFingerprint));
        return true;
    }

    private bool TryRestoreChangedLlvmIrTextFromSelectedFunctions()
    {
        if (!_options.AllowLlvmIrTextRestore ||
            !TryBuildChangedLlvmFragmentsFromSelectedFunctions(out var restore))
        {
            return false;
        }

        var functionOrder = _mirFunctionFingerprints!.Functions
            .Select(static fingerprint => fingerprint.FunctionKey)
            .ToArray();
        var restored = LlvmFunctionFingerprintBuilder.RecomposeModule(
            _options.PreviousLlvmModuleEnvelopeSnapshot!,
            restore.Execution.Fragments,
            functionOrder);

        _llvmIrText = restored.IrText;
        _llvmFunctionFragments = restore.Execution.Fragments;
        _llvmFunctionFingerprints = new LlvmFunctionFingerprintSnapshot(
            LlvmFunctionFingerprintSnapshot.CurrentSchemaVersion,
            _llvmFunctionFragments.Functions
                .Select(static fragment => new LlvmFunctionFingerprint(
                    fragment.FunctionKey,
                    fragment.BodyHash,
                    fragment.BasicBlockCount,
                    fragment.InstructionCount,
                    fragment.ParameterCount))
                .ToArray());
        _llvmFunctionFingerprintDiff = AddLlvmFunctionFingerprintComparisonCounters(
            "Llvm.previous_build",
            "llvm",
            _options.PreviousLlvmFunctionFragmentSnapshot!.ModuleFingerprint,
            _llvmFunctionFingerprints.ModuleFingerprint,
            _options.PreviousLlvmFunctionFragmentSnapshot.Functions
                .Select(static fragment => new LlvmFunctionFingerprint(
                    fragment.FunctionKey,
                    fragment.BodyHash,
                    fragment.BasicBlockCount,
                    fragment.InstructionCount,
                    fragment.ParameterCount))
                .ToArray(),
            _llvmFunctionFingerprints.Functions);
        _llvmFunctionWorklist = AddFunctionWorklistCounters(
            "Llvm.previous_build",
            _llvmFunctionFingerprintDiff);
        _llvmModuleEnvelope = _options.PreviousLlvmModuleEnvelopeSnapshot;
        _llvmFunctionFragmentRestorePlan = restore.FunctionRestorePlan;
        _llvmFunctionFragmentRestoreResult = restore.Execution.Result with
        {
            MatchesCurrentIr = true,
            Applied = true
        };

        SetProfilingCounter("Llvm.previous_build.selected_fragment_restore_hits", 1);
        SetProfilingCounter("Llvm.previous_build.selected_fragment_restore_rebuilt_functions", restore.RebuiltFragments.Functions.Count);
        SetProfilingCounter("Llvm.previous_build.selected_fragment_restore_ir_bytes", _llvmIrText.Length);
        SetProfilingCounter(
            "Llvm.previous_build.selected_fragment_restore_output_module_fingerprint",
            StableCounterFromHash(_llvmFunctionFragments.ModuleFingerprint));
        SetFragmentRestoreExecutionCounters(_llvmFunctionFragmentRestoreResult, matchesEmitterIr: false);
        return true;
    }

    private bool TryRestoreChangedNativeObjectGroupsFromSelectedFunctions()
    {
        if (!_options.AllowNativeObjectGroupRestore ||
            _options.PreviousLlvmCodegenUnitPlanSnapshot == null ||
            !TryBuildChangedLlvmFragmentsFromSelectedFunctions(out var restore))
        {
            return false;
        }

        var selectedCodegenPlan = LlvmCodegenUnitPlanSnapshot.Create(
            restore.SelectedEnvelope,
            restore.SelectedModule,
            restore.RebuiltFragments,
            LlvmBackendConfiguration.Create(
                ResolveLlvmTargetInfo() ?? TargetInfo.Default,
                _options.LlvmOptimizationLevel,
                _options.LlvmEnableLto,
                _options.NativeLinkMode,
                extraCFlags: null,
                extraLinkFlags: null));
        if (!CanBuildMixedObjectGroupPlanFromSelectedPlan(
                _options.PreviousLlvmCodegenUnitPlanSnapshot,
                selectedCodegenPlan,
                restore.RebuildFunctionKeys))
        {
            SetProfilingCounter("Llvm.previous_build.native_selected_object_group_restore_plan_fallback", 1);
            return false;
        }

        _llvmFunctionFragments = restore.Execution.Fragments;
        _llvmFunctionFingerprints = new LlvmFunctionFingerprintSnapshot(
            LlvmFunctionFingerprintSnapshot.CurrentSchemaVersion,
            _llvmFunctionFragments.Functions
                .Select(static fragment => new LlvmFunctionFingerprint(
                    fragment.FunctionKey,
                    fragment.BodyHash,
                    fragment.BasicBlockCount,
                    fragment.InstructionCount,
                    fragment.ParameterCount))
                .ToArray());
        _llvmModuleEnvelope = _options.PreviousLlvmModuleEnvelopeSnapshot;
        _llvmFunctionFragmentRestorePlan = restore.FunctionRestorePlan;
        _llvmCodegenUnitPlan = LlvmCodegenUnitPlanSnapshot.CreateFromSelectedPlan(
            _options.PreviousLlvmCodegenUnitPlanSnapshot,
            selectedCodegenPlan,
            _llvmFunctionFragments,
            _llvmFunctionFragmentRestorePlan);
        _llvmObjectGroupRestorePlan = AddLlvmObjectGroupRestorePlanCounters(
            "Llvm.previous_build",
            _llvmCodegenUnitPlan,
            _llvmFunctionFragmentRestorePlan);
        _llvmFunctionFingerprintDiff = AddLlvmFunctionFingerprintComparisonCounters(
            "Llvm.previous_build",
            "llvm",
            _options.PreviousLlvmFunctionFragmentSnapshot!.ModuleFingerprint,
            _llvmFunctionFingerprints.ModuleFingerprint,
            _options.PreviousLlvmFunctionFragmentSnapshot.Functions
                .Select(static fragment => new LlvmFunctionFingerprint(
                    fragment.FunctionKey,
                    fragment.BodyHash,
                    fragment.BasicBlockCount,
                    fragment.InstructionCount,
                    fragment.ParameterCount))
                .ToArray(),
            _llvmFunctionFingerprints.Functions);
        _llvmFunctionWorklist = AddFunctionWorklistCounters(
            "Llvm.previous_build",
            _llvmFunctionFingerprintDiff);
        _llvmFunctionFragmentRestoreResult = restore.Execution.Result with
        {
            MatchesCurrentIr = true,
            Applied = true
        };

        SetProfilingCounter("Llvm.previous_build.native_selected_object_group_restore_hits", 1);
        SetProfilingCounter("Llvm.previous_build.native_selected_object_group_restore_rebuilt_functions", restore.RebuiltFragments.Functions.Count);
        SetProfilingCounter(
            "Llvm.previous_build.native_selected_object_group_restore_plan_fingerprint",
            StableCounterFromHash(_llvmCodegenUnitPlan.PlanFingerprint));
        SetFragmentRestoreExecutionCounters(_llvmFunctionFragmentRestoreResult, matchesEmitterIr: false);
        return true;
    }

    private bool TryBuildChangedLlvmFragmentsFromSelectedFunctions(out SelectedLlvmFragmentRestore restore)
    {
        restore = default!;
        if (!_options.EnableDetailedProfiling ||
            _mirModule == null ||
            _mirFunctionFingerprints == null ||
            _options.PreviousMirFunctionFingerprintSnapshot == null ||
            _options.PreviousLlvmFunctionFragmentSnapshot == null ||
            _options.PreviousLlvmModuleEnvelopeSnapshot == null)
        {
            return false;
        }

        if (string.Equals(
                _options.PreviousMirFunctionFingerprintSnapshot.ModuleFingerprint,
                _mirFunctionFingerprints.ModuleFingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }

        var mirDiff = FunctionFingerprintDiffSnapshot.Create(
            "mir",
            _options.PreviousMirFunctionFingerprintSnapshot.ModuleFingerprint,
            _mirFunctionFingerprints.ModuleFingerprint,
            _options.PreviousMirFunctionFingerprintSnapshot.Functions.Select(static fingerprint => (fingerprint.FunctionKey, fingerprint.BodyHash)),
            _mirFunctionFingerprints.Functions.Select(static fingerprint => (fingerprint.FunctionKey, fingerprint.BodyHash)));
        var rebuildFunctionKeys = mirDiff.Functions
            .Where(static entry => entry.Status is FunctionFingerprintDiffStatus.Changed or FunctionFingerprintDiffStatus.Added)
            .Select(static entry => entry.FunctionKey)
            .ToHashSet(StringComparer.Ordinal);
        if (rebuildFunctionKeys.Count == 0)
        {
            return false;
        }

        SetProfilingCounter("Llvm.previous_build.selected_fragment_restore_candidate_functions", rebuildFunctionKeys.Count);
        var converter = CreateMirToLlvmConverter();
        LlvmModule selectedModule;
        using (MeasureSubphase(CompilationPhase.Llvm, "convert_selected_functions"))
        {
            selectedModule = converter.ConvertSelectedFunctions(_mirModule, rebuildFunctionKeys);
            selectedModule.LinkLibraryPaths.AddRange(_options.ConfigFfiLibraryPaths);
            selectedModule.NativeIncludePaths.AddRange(_options.ConfigFfiIncludePaths);
            selectedModule.NativeSources.AddRange(_options.ConfigFfiNativeSources);
            selectedModule.LinkerFlags.AddRange(_options.ConfigFfiLinkerFlags);
        }

        using (MeasureSubphase(CompilationPhase.Llvm, "collect_selected_diagnostics"))
        {
            _diagnostics.AddRange(converter.Diagnostics);
        }

        if (converter.Diagnostics.Any(diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error))
        {
            SetProfilingCounter("Llvm.previous_build.selected_fragment_restore_diagnostic_fallback", 1);
            return false;
        }

        TargetInfo? targetInfo;
        using (MeasureSubphase(CompilationPhase.Llvm, "resolve_target"))
        {
            targetInfo = ResolveLlvmTargetInfo();
        }

        var effectiveDataLayout = targetInfo?.DataLayout ??
            "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128";
        var effectiveTargetTriple = targetInfo?.Triple ?? "x86_64-pc-linux-gnu";
        LlvmModuleEnvelopeSnapshot selectedEnvelope;
        using (MeasureSubphase(CompilationPhase.Llvm, "selected_module_envelope"))
        {
            selectedEnvelope = LlvmModuleEnvelopeSnapshot.FromModule(
                selectedModule,
                effectiveDataLayout,
                effectiveTargetTriple);
        }

        if (!CanReusePreviousEnvelopeForSelectedFragments(
                _options.PreviousLlvmModuleEnvelopeSnapshot,
                selectedEnvelope))
        {
            SetProfilingCounter("Llvm.previous_build.selected_fragment_restore_envelope_fallback", 1);
            return false;
        }

        LlvmFunctionFragmentSnapshot rebuiltFragments;
        using (MeasureSubphase(CompilationPhase.Llvm, "selected_function_fragments"))
        {
            rebuiltFragments = LlvmFunctionFragmentSnapshot.FromModule(selectedModule);
        }

        var currentFragments = MergeSelectedLlvmFragments(
            _options.PreviousLlvmFunctionFragmentSnapshot,
            rebuiltFragments);
        _llvmFunctionFragmentRestorePlan = LlvmFunctionFragmentRestorePlanSnapshot.Create(
            _options.PreviousLlvmFunctionFragmentSnapshot,
            currentFragments);
        var execution = LlvmFunctionFragmentRestoreExecutor.Execute(
            _options.PreviousLlvmFunctionFragmentSnapshot,
            currentFragments,
            _llvmFunctionFragmentRestorePlan);
        restore = new SelectedLlvmFragmentRestore(
            selectedModule,
            selectedEnvelope,
            rebuildFunctionKeys,
            rebuiltFragments,
            _llvmFunctionFragmentRestorePlan,
            execution);
        return true;
    }

    private static bool CanBuildMixedObjectGroupPlanFromSelectedPlan(
        LlvmCodegenUnitPlanSnapshot previous,
        LlvmCodegenUnitPlanSnapshot selected,
        IReadOnlySet<string> rebuildFunctionKeys)
    {
        if (rebuildFunctionKeys.Count == 0)
        {
            return false;
        }

        var selectedRoots = selected.ObjectGroups
            .Select(static group => group.RootFunctionKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var group in previous.ObjectGroups)
        {
            if (!group.MemberFunctionKeys.Any(rebuildFunctionKeys.Contains))
            {
                continue;
            }

            if (!selectedRoots.Contains(group.RootFunctionKey))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record SelectedLlvmFragmentRestore(
        LlvmModule SelectedModule,
        LlvmModuleEnvelopeSnapshot SelectedEnvelope,
        IReadOnlySet<string> RebuildFunctionKeys,
        LlvmFunctionFragmentSnapshot RebuiltFragments,
        LlvmFunctionFragmentRestorePlanSnapshot FunctionRestorePlan,
        LlvmFunctionFragmentRestoreExecution Execution);

    private MirToLlvmConverter CreateMirToLlvmConverter()
    {
        var converter = new MirToLlvmConverter(
            _symbolTable,
            name => MeasureSubphase(CompilationPhase.Llvm, $"convert_module.{name}"));
        converter.SetPerceusHints(_borrowCheckResult!);
        converter.SetReuseHints(_borrowCheckResult!);
        converter.SetStackPromotionHints(_borrowCheckResult!);
        converter.SetUnifiedStackPromotionHints(_borrowCheckResult!);

        if (_symbolTable != null)
        {
            var cstructAccessors = new Dictionary<string, CStructAccessorInfo>();
            foreach (var symbol in _symbolTable.Symbols.Values)
            {
                if (symbol is FuncSymbol { IsCStructAccessor: true } funcSymbol)
                {
                    cstructAccessors[funcSymbol.Name] = new CStructAccessorInfo
                    {
                        FieldOffset = funcSymbol.CStructFieldOffset,
                        FieldTypeId = funcSymbol.CStructFieldTypeId.Value,
                        IsGetter = funcSymbol.IsCStructGetter
                    };
                }
            }

            converter.SetCStructAccessors(cstructAccessors);
        }

        return converter;
    }

    private static bool CanReusePreviousEnvelopeForSelectedFragments(
        LlvmModuleEnvelopeSnapshot previous,
        LlvmModuleEnvelopeSnapshot selected)
    {
        return string.Equals(previous.DataLayout, selected.DataLayout, StringComparison.Ordinal) &&
               string.Equals(previous.TargetTriple, selected.TargetTriple, StringComparison.Ordinal) &&
               IsSubset(selected.TypeDefinitionIr, previous.TypeDefinitionIr) &&
               IsSubset(selected.GlobalIr, previous.GlobalIr) &&
               IsSubset(selected.DeclarationIr, previous.DeclarationIr) &&
               IsSubset(selected.AttributeGroupIr, previous.AttributeGroupIr) &&
               IsSubset(selected.LinkLibraries, previous.LinkLibraries) &&
               IsSubset(selected.LinkLibraryPaths, previous.LinkLibraryPaths) &&
               IsSubset(selected.NativeSources, previous.NativeSources) &&
               IsSubset(selected.NativeIncludePaths, previous.NativeIncludePaths) &&
               IsSubset(selected.LinkerFlags, previous.LinkerFlags);
    }

    private static bool IsSubset(IReadOnlyList<string> required, IReadOnlyList<string> available)
    {
        if (required.Count == 0)
        {
            return true;
        }

        var availableSet = available.ToHashSet(StringComparer.Ordinal);
        return required.All(availableSet.Contains);
    }

    private static LlvmFunctionFragmentSnapshot MergeSelectedLlvmFragments(
        LlvmFunctionFragmentSnapshot previous,
        LlvmFunctionFragmentSnapshot rebuilt)
    {
        var previousByKey = previous.Functions.ToDictionary(static fragment => fragment.FunctionKey, StringComparer.Ordinal);
        foreach (var rebuiltFragment in rebuilt.Functions)
        {
            previousByKey[rebuiltFragment.FunctionKey] = rebuiltFragment;
        }

        return new LlvmFunctionFragmentSnapshot(
            LlvmFunctionFragmentSnapshot.CurrentSchemaVersion,
            previousByKey.Values
                .OrderBy(static fragment => fragment.FunctionKey, StringComparer.Ordinal)
                .ToArray());
    }

    private void SetFragmentRestoreExecutionCounters(
        LlvmFunctionFragmentRestoreResultSnapshot result,
        bool matchesEmitterIr)
    {
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_restored_functions",
            result.RestoredFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_rebuilt_functions",
            result.RebuiltFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_removed_functions",
            result.RemovedFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_fallback_rebuild_functions",
            result.FallbackRebuildFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_restored_ir_bytes",
            result.RestoredIrBytes);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_rebuilt_ir_bytes",
            result.RebuiltIrBytes);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_matches_current_ir",
            result.MatchesCurrentIr ? 1 : 0);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_matches_emitter_ir",
            matchesEmitterIr ? 1 : 0);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_applied",
            result.Applied ? 1 : 0);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_output_module_fingerprint",
            StableCounterFromHash(result.OutputModuleFingerprint));
    }

    private void AddLlvmModuleShapeCounters(string prefix, LlvmModule module)
    {
        var functionCount = module.Functions.Count;
        var declarationFunctionCount = 0;
        var definedFunctionCount = 0;
        var basicBlockCount = 0L;
        var instructionCount = 0L;
        var terminatorCount = 0L;
        var parameterCount = 0L;
        var maxBlocksPerFunction = 0;
        var maxInstructionsPerFunction = 0;
        var maxParametersPerFunction = 0;

        foreach (var function in module.Functions)
        {
            if (function.IsDeclaration)
            {
                declarationFunctionCount++;
            }
            else
            {
                definedFunctionCount++;
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

            parameterCount += function.Parameters.Count;
            maxBlocksPerFunction = Math.Max(maxBlocksPerFunction, function.BasicBlocks.Count);
            maxInstructionsPerFunction = Math.Max(maxInstructionsPerFunction, functionInstructionCount);
            maxParametersPerFunction = Math.Max(maxParametersPerFunction, function.Parameters.Count);
        }

        SetProfilingCounter($"{prefix}.functions", functionCount);
        SetProfilingCounter($"{prefix}.defined_functions", definedFunctionCount);
        SetProfilingCounter($"{prefix}.declaration_functions", declarationFunctionCount);
        SetProfilingCounter($"{prefix}.basic_blocks", basicBlockCount);
        SetProfilingCounter($"{prefix}.instructions", instructionCount);
        SetProfilingCounter($"{prefix}.terminators", terminatorCount);
        SetProfilingCounter($"{prefix}.parameters", parameterCount);
        SetProfilingCounter($"{prefix}.globals", module.Globals.Count);
        SetProfilingCounter($"{prefix}.declarations", module.Declarations.Count);
        SetProfilingCounter($"{prefix}.named_struct_types", module.NamedStructTypes.Count);
        SetProfilingCounter($"{prefix}.native_sources", module.NativeSources.Count);
        SetProfilingCounter($"{prefix}.native_include_paths", module.NativeIncludePaths.Count);
        SetProfilingCounter($"{prefix}.link_libraries", module.LinkLibraries.Count);
        SetProfilingCounter($"{prefix}.link_library_paths", module.LinkLibraryPaths.Count);
        SetProfilingCounter($"{prefix}.linker_flags", module.LinkerFlags.Count);
        SetProfilingCounter($"{prefix}.max_blocks_per_function", maxBlocksPerFunction);
        SetProfilingCounter($"{prefix}.max_instructions_per_function", maxInstructionsPerFunction);
        SetProfilingCounter($"{prefix}.max_parameters_per_function", maxParametersPerFunction);
    }

    private LlvmFunctionFragmentSnapshot AddLlvmFunctionFragmentSnapshotCounters(string prefix, LlvmModule module)
    {
        LlvmFunctionFragmentSnapshot snapshot;
        using (MeasureSubphase(CompilationPhase.Llvm, $"{prefix}.function_fragments"))
        {
            snapshot = LlvmFunctionFragmentSnapshot.FromModule(module);
        }

        SetProfilingCounter($"{prefix}.function_fragments", snapshot.Functions.Count);
        SetProfilingCounter(
            $"{prefix}.function_fragment_bytes",
            snapshot.Functions.Sum(static fragment => fragment.IrFragment.Length));
        SetProfilingCounter($"{prefix}.fragment_module_fingerprint", StableCounterFromHash(snapshot.ModuleFingerprint));
        return snapshot;
    }

    private LlvmModuleEnvelopeSnapshot AddLlvmModuleEnvelopeSnapshotCounters(
        string prefix,
        LlvmModule module,
        string dataLayout,
        string targetTriple)
    {
        LlvmModuleEnvelopeSnapshot snapshot;
        using (MeasureSubphase(CompilationPhase.Llvm, $"{prefix}.module_envelope"))
        {
            snapshot = LlvmModuleEnvelopeSnapshot.FromModule(module, dataLayout, targetTriple);
        }

        SetProfilingCounter($"{prefix}.module_envelope_lines", snapshot.FragmentLineCount);
        SetProfilingCounter($"{prefix}.module_envelope_fingerprint", StableCounterFromHash(snapshot.EnvelopeFingerprint));
        SetProfilingCounter($"{prefix}.module_envelope_link_libraries", snapshot.LinkLibraries.Count);
        SetProfilingCounter($"{prefix}.module_envelope_native_sources", snapshot.NativeSources.Count);
        return snapshot;
    }

    private LlvmCodegenUnitPlanSnapshot AddLlvmCodegenUnitPlanCounters(
        string prefix,
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmFunctionFragmentSnapshot functions,
        TargetInfo targetInfo)
    {
        LlvmCodegenUnitPlanSnapshot snapshot;
        using (MeasureSubphase(CompilationPhase.Llvm, $"{prefix}.codegen_unit_plan"))
        {
            snapshot = LlvmCodegenUnitPlanSnapshot.Create(
                envelope,
                _llvmModule!,
                functions,
                LlvmBackendConfiguration.Create(
                    targetInfo,
                    _options.LlvmOptimizationLevel,
                    _options.LlvmEnableLto,
                    _options.NativeLinkMode,
                    extraCFlags: null,
                    extraLinkFlags: null));
        }

        SetProfilingCounter($"{prefix}.codegen_unit_plan_functions", snapshot.FunctionUnits.Count);
        SetProfilingCounter($"{prefix}.codegen_unit_plan_object_groups", snapshot.ObjectGroups.Count);
        SetProfilingCounter(
            $"{prefix}.codegen_unit_plan_max_group_functions",
            snapshot.ObjectGroups.Count == 0 ? 0 : snapshot.ObjectGroups.Max(static group => group.FunctionCount));
        SetProfilingCounter(
            $"{prefix}.codegen_unit_plan_max_group_ir_bytes",
            snapshot.ObjectGroups.Count == 0 ? 0 : snapshot.ObjectGroups.Max(static group => group.TotalIrBytes));
        if (_llvmModuleEnvelope != null && _llvmFunctionFragments != null)
        {
            var recomposedGroupIrBytes = 0;
            var maxRecomposedGroupIrBytes = 0;
            foreach (var group in snapshot.ObjectGroups)
            {
                var recomposedGroup = LlvmFunctionFingerprintBuilder.RecomposeObjectGroup(
                    _llvmModuleEnvelope,
                    _llvmFunctionFragments,
                    group);
                recomposedGroupIrBytes += recomposedGroup.IrBytes;
                maxRecomposedGroupIrBytes = Math.Max(maxRecomposedGroupIrBytes, recomposedGroup.IrBytes);
            }

            SetProfilingCounter(
                $"{prefix}.codegen_unit_plan_recomposed_group_ir_bytes",
                recomposedGroupIrBytes);
            SetProfilingCounter(
                $"{prefix}.codegen_unit_plan_max_recomposed_group_ir_bytes",
                maxRecomposedGroupIrBytes);
        }

        SetProfilingCounter(
            $"{prefix}.codegen_unit_plan_object_eligible_functions",
            snapshot.FunctionUnits.Count(static unit => unit.IsObjectUnitEligible));
        SetProfilingCounter(
            $"{prefix}.codegen_unit_plan_object_ineligible_functions",
            snapshot.FunctionUnits.Count(static unit => !unit.IsObjectUnitEligible));
        SetProfilingCounter(
            $"{prefix}.codegen_unit_plan_non_object_dependency_functions",
            snapshot.FunctionUnits.Count(static unit =>
                unit.ObjectUnitIneligibilityReason.StartsWith("depends-on-non-object-unit:", StringComparison.Ordinal)));
        SetProfilingCounter(
            $"{prefix}.codegen_unit_plan_direct_call_edges",
            snapshot.FunctionUnits.Sum(static unit => unit.DirectCallees.Count));
        SetProfilingCounter(
            $"{prefix}.codegen_unit_plan_max_direct_callees",
            snapshot.FunctionUnits.Count == 0 ? 0 : snapshot.FunctionUnits.Max(static unit => unit.DirectCallees.Count));
        SetProfilingCounter($"{prefix}.codegen_unit_plan_total_function_ir_bytes", snapshot.FunctionUnits.Sum(static unit => unit.IrBytes));
        SetProfilingCounter(
            $"{prefix}.codegen_unit_plan_max_function_ir_bytes",
            snapshot.FunctionUnits.Count == 0 ? 0 : snapshot.FunctionUnits.Max(static unit => unit.IrBytes));
        SetProfilingCounter($"{prefix}.codegen_unit_plan_envelope_lines", snapshot.EnvelopeUnit.LineCount);
        SetProfilingCounter($"{prefix}.codegen_unit_plan_fingerprint", StableCounterFromHash(snapshot.PlanFingerprint));
        return snapshot;
    }

    private LlvmFunctionFragmentRestorePlanSnapshot AddLlvmFunctionFragmentRestoreCounters(
        string prefix,
        LlvmFunctionFragmentSnapshot previous,
        LlvmFunctionFragmentSnapshot current)
    {
        var plan = LlvmFunctionFragmentRestorePlanSnapshot.Create(previous, current);
        var restorable = plan.Count(LlvmFunctionFragmentRestoreAction.Restore);
        var rebuild = plan.Count(LlvmFunctionFragmentRestoreAction.Rebuild);
        var remove = plan.Count(LlvmFunctionFragmentRestoreAction.Remove);

        SetProfilingCounter($"{prefix}.fragment_restorable_functions", restorable);
        SetProfilingCounter($"{prefix}.fragment_changed_or_missing_functions", rebuild);
        SetProfilingCounter($"{prefix}.fragment_rebuild_functions", rebuild);
        SetProfilingCounter($"{prefix}.fragment_remove_functions", remove);
        SetProfilingCounter(
            $"{prefix}.fragment_restorable_ir_bytes",
            plan.Functions
                .Where(static entry => entry.Action == LlvmFunctionFragmentRestoreAction.Restore)
                .Sum(static entry => entry.IrBytes));
        SetProfilingCounter(
            $"{prefix}.fragment_rebuild_ir_bytes",
            plan.Functions
                .Where(static entry => entry.Action == LlvmFunctionFragmentRestoreAction.Rebuild)
                .Sum(static entry => entry.IrBytes));
        SetProfilingCounter($"{prefix}.previous_fragment_module_fingerprint", StableCounterFromHash(previous.ModuleFingerprint));
        SetProfilingCounter($"{prefix}.current_fragment_module_fingerprint", StableCounterFromHash(current.ModuleFingerprint));
        return plan;
    }

    private void ApplyLlvmFunctionFragmentRestoreExecution(
        LlvmFunctionFragmentSnapshot previous,
        LlvmFunctionFragmentSnapshot current,
        LlvmFunctionFragmentRestorePlanSnapshot plan,
        LlvmModuleEnvelopeSnapshot envelope,
        IReadOnlyList<string> functionOrder,
        string emittedIr)
    {
        LlvmFunctionFragmentRestoreExecution execution;
        using (MeasureSubphase(CompilationPhase.Llvm, "Llvm.previous_build.fragment_restore_execute"))
        {
            execution = LlvmFunctionFragmentRestoreExecutor.Execute(previous, current, plan);
        }

        var restoredModule = LlvmFunctionFingerprintBuilder.RecomposeModule(envelope, execution.Fragments, functionOrder);
        var currentModule = LlvmFunctionFingerprintBuilder.RecomposeModule(envelope, current, functionOrder);
        var matchesCurrentIr =
            string.Equals(restoredModule.IrText, currentModule.IrText, StringComparison.Ordinal) &&
            string.Equals(currentModule.IrText, emittedIr, StringComparison.Ordinal);
        var matchesEmitterIr = string.Equals(currentModule.IrText, emittedIr, StringComparison.Ordinal);
        _llvmFunctionFragmentRestoreResult = execution.Result with
        {
            MatchesCurrentIr = matchesCurrentIr,
            Applied = matchesCurrentIr
        };
        if (matchesCurrentIr)
        {
            _llvmIrText = restoredModule.IrText;
            _llvmFunctionFragments = execution.Fragments;
        }

        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_restored_functions",
            _llvmFunctionFragmentRestoreResult.RestoredFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_rebuilt_functions",
            _llvmFunctionFragmentRestoreResult.RebuiltFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_removed_functions",
            _llvmFunctionFragmentRestoreResult.RemovedFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_fallback_rebuild_functions",
            _llvmFunctionFragmentRestoreResult.FallbackRebuildFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_restored_ir_bytes",
            _llvmFunctionFragmentRestoreResult.RestoredIrBytes);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_rebuilt_ir_bytes",
            _llvmFunctionFragmentRestoreResult.RebuiltIrBytes);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_matches_current_ir",
            matchesCurrentIr ? 1 : 0);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_matches_emitter_ir",
            matchesEmitterIr ? 1 : 0);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_applied",
            _llvmFunctionFragmentRestoreResult.Applied ? 1 : 0);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_output_module_fingerprint",
            StableCounterFromHash(_llvmFunctionFragmentRestoreResult.OutputModuleFingerprint));
    }

    private bool TryEmitLlvmIrFromFunctionFragmentRestore(
        LlvmFunctionFragmentSnapshot previous,
        LlvmFunctionFragmentSnapshot current,
        LlvmFunctionFragmentRestorePlanSnapshot plan,
        LlvmModuleEnvelopeSnapshot envelope,
        IReadOnlyList<string> functionOrder)
    {
        LlvmFunctionFragmentRestoreExecution execution;
        using (MeasureSubphase(CompilationPhase.Llvm, "Llvm.previous_build.fragment_restore_emit"))
        {
            execution = LlvmFunctionFragmentRestoreExecutor.Execute(previous, current, plan);
        }

        var restoredModule = LlvmFunctionFingerprintBuilder.RecomposeModule(envelope, execution.Fragments, functionOrder);
        var currentModule = LlvmFunctionFingerprintBuilder.RecomposeModule(envelope, current, functionOrder);
        var matchesCurrentIr = string.Equals(restoredModule.IrText, currentModule.IrText, StringComparison.Ordinal);
        _llvmFunctionFragmentRestoreResult = execution.Result with
        {
            MatchesCurrentIr = matchesCurrentIr,
            Applied = matchesCurrentIr
        };

        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_restored_functions",
            _llvmFunctionFragmentRestoreResult.RestoredFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_rebuilt_functions",
            _llvmFunctionFragmentRestoreResult.RebuiltFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_removed_functions",
            _llvmFunctionFragmentRestoreResult.RemovedFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_fallback_rebuild_functions",
            _llvmFunctionFragmentRestoreResult.FallbackRebuildFragments);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_restored_ir_bytes",
            _llvmFunctionFragmentRestoreResult.RestoredIrBytes);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_rebuilt_ir_bytes",
            _llvmFunctionFragmentRestoreResult.RebuiltIrBytes);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_matches_current_ir",
            matchesCurrentIr ? 1 : 0);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_matches_emitter_ir",
            matchesCurrentIr ? 1 : 0);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_applied",
            _llvmFunctionFragmentRestoreResult.Applied ? 1 : 0);
        SetProfilingCounter(
            "Llvm.previous_build.fragment_restore_execute_output_module_fingerprint",
            StableCounterFromHash(_llvmFunctionFragmentRestoreResult.OutputModuleFingerprint));

        if (!matchesCurrentIr)
        {
            SetProfilingCounter("Llvm.previous_build.fragment_restore_emit_mismatch_fallback", 1);
            return false;
        }

        _llvmIrText = restoredModule.IrText;
        _llvmFunctionFragments = execution.Fragments;
        SetProfilingCounter("Llvm.previous_build.fragment_restore_emit_hits", 1);
        SetProfilingCounter("Llvm.previous_build.fragment_restore_emit_ir_bytes", _llvmIrText.Length);
        return true;
    }

    private LlvmObjectGroupRestorePlanSnapshot AddLlvmObjectGroupRestorePlanCounters(
        string prefix,
        LlvmCodegenUnitPlanSnapshot codegenUnitPlan,
        LlvmFunctionFragmentRestorePlanSnapshot functionRestorePlan)
    {
        var plan = LlvmObjectGroupRestorePlanSnapshot.Create(
            codegenUnitPlan.ObjectGroups,
            functionRestorePlan);
        SetProfilingCounter($"{prefix}.object_group_restore_groups", plan.Groups.Count);
        SetProfilingCounter($"{prefix}.object_group_restore_restorable_groups", plan.Count(LlvmObjectGroupRestoreAction.Restore));
        SetProfilingCounter($"{prefix}.object_group_restore_rebuild_groups", plan.Count(LlvmObjectGroupRestoreAction.Rebuild));
        SetProfilingCounter(
            $"{prefix}.object_group_restore_functions",
            plan.Groups.Sum(static group => group.RestoreFunctions));
        SetProfilingCounter(
            $"{prefix}.object_group_rebuild_functions",
            plan.Groups.Sum(static group => group.RebuildFunctions));
        SetProfilingCounter(
            $"{prefix}.object_group_restore_ir_bytes",
            plan.Groups
                .Where(static group => group.Action == LlvmObjectGroupRestoreAction.Restore)
                .Sum(static group => group.TotalIrBytes));
        SetProfilingCounter(
            $"{prefix}.object_group_rebuild_ir_bytes",
            plan.Groups
                .Where(static group => group.Action == LlvmObjectGroupRestoreAction.Rebuild)
                .Sum(static group => group.TotalIrBytes));
        return plan;
    }

    private LlvmFunctionFingerprintSnapshot AddLlvmFunctionFingerprintCounters(
        string prefix,
        LlvmFunctionFragmentSnapshot fragments)
    {
        var snapshot = new LlvmFunctionFingerprintSnapshot(
            LlvmFunctionFingerprintSnapshot.CurrentSchemaVersion,
            fragments.Functions
                .Select(static fragment => new LlvmFunctionFingerprint(
                    fragment.FunctionKey,
                    fragment.BodyHash,
                    fragment.BasicBlockCount,
                    fragment.InstructionCount,
                    fragment.ParameterCount))
                .ToArray());
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

    private FunctionFingerprintDiffSnapshot AddLlvmFunctionFingerprintComparisonCounters(
        string prefix,
        string kind,
        string previousModuleFingerprint,
        string currentModuleFingerprint,
        IReadOnlyList<LlvmFunctionFingerprint> before,
        IReadOnlyList<LlvmFunctionFingerprint> after)
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
}
