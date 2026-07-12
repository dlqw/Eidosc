using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Hir;
using Eidosc.Pipeline;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.ProjectSystem;
using Eidosc.Symbols;
using Eidosc.Types;
using System.Text.Json;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class DetailedProfilingGateTests
{
    private const string Source = """
Main :: module {
    main :: Unit -> Unit { _ => () }
}
""";

    [Fact]
    public void Run_WithoutDetailedProfiling_DoesNotComputeFunctionFingerprints()
    {
        var result = Run(enableDetailedProfiling: false);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Empty(result.SubphaseMetrics);
        Assert.DoesNotContain(result.ProfilingCounters.Keys, IsFunctionFingerprintCounter);
    }

    [Fact]
    public void Run_WithDetailedProfiling_ComputesFunctionFingerprints()
    {
        var result = Run(enableDetailedProfiling: true);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Contains(result.SubphaseMetrics, static metric =>
            metric.Phase == CompilationPhase.Mir &&
            string.Equals(metric.Name, "Mir.build.output.function_fingerprints", StringComparison.Ordinal));
        Assert.Contains(result.SubphaseMetrics, static metric =>
            metric.Phase == CompilationPhase.Llvm &&
            string.Equals(metric.Name, "Llvm.convert.output.function_fragments", StringComparison.Ordinal));
        Assert.Contains("Mir.build.output.function_fingerprints", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.function_fingerprints", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.function_fragments", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.function_fragment_bytes", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.module_envelope_lines", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.module_envelope_fingerprint", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_functions", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_object_groups", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_max_group_functions", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_max_group_ir_bytes", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_recomposed_group_ir_bytes", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_max_recomposed_group_ir_bytes", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_object_eligible_functions", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_object_ineligible_functions", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_non_object_dependency_functions", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_direct_call_edges", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_max_direct_callees", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.codegen_unit_plan_fingerprint", result.ProfilingCounters.Keys);
        Assert.Contains("Build.moduleMirArtifacts.modules", result.ProfilingCounters.Keys);
        Assert.Contains("Build.moduleMirArtifacts.uniqueArtifactHashes", result.ProfilingCounters.Keys);
        Assert.Contains("Build.moduleDependencySignatures.mirAvailableModules", result.ProfilingCounters.Keys);
        Assert.Contains("Mir.specialization_loop.output.module_fingerprint", result.ProfilingCounters.Keys);
        Assert.Contains("Llvm.convert.output.module_fingerprint", result.ProfilingCounters.Keys);

        Assert.NotNull(result.MirFunctionFingerprints);
        Assert.NotNull(result.ModuleMirArtifactSnapshot);
        Assert.NotNull(result.ModuleDependencySignatureSnapshot);
        Assert.NotNull(result.LlvmFunctionFingerprints);
        Assert.NotNull(result.LlvmFunctionFragments);
        Assert.NotNull(result.LlvmModuleEnvelope);
        Assert.NotNull(result.LlvmCodegenUnitPlan);
        Assert.True(result.MirFunctionFingerprints!.Functions.Count > 0, FormatCounters(result));
        Assert.True(result.ModuleMirArtifactSnapshot!.Nodes.Count > 0, FormatCounters(result));
        Assert.Equal(result.ModuleMirArtifactSnapshot.Nodes.Count, result.ModuleDependencySignatureSnapshot!.Nodes.Count);
        Assert.Equal(
            result.ModuleMirArtifactSnapshot.Nodes.Count,
            result.ProfilingCounters["Build.moduleDependencySignatures.mirAvailableModules"]);
        Assert.True(result.LlvmFunctionFingerprints!.Functions.Count > 0, FormatCounters(result));
        Assert.Equal(result.LlvmFunctionFingerprints.Functions.Count, result.LlvmFunctionFragments!.Functions.Count);
        Assert.Equal(result.LlvmFunctionFragments.Functions.Count, result.LlvmCodegenUnitPlan!.FunctionUnits.Count);
        Assert.True(result.LlvmCodegenUnitPlan.ObjectGroups.Count > 0, FormatCounters(result));
        Assert.Equal(
            result.LlvmCodegenUnitPlan.FunctionUnits.Count(static unit => unit.IsObjectUnitEligible),
            result.ProfilingCounters["Llvm.convert.output.codegen_unit_plan_object_eligible_functions"]);
        Assert.Equal(
            result.LlvmCodegenUnitPlan.FunctionUnits.Count(static unit => !unit.IsObjectUnitEligible),
            result.ProfilingCounters["Llvm.convert.output.codegen_unit_plan_object_ineligible_functions"]);
        Assert.True(result.ProfilingCounters.TryGetValue(
            "Mir.specialization_loop.output.function_fingerprints",
            out var mirFunctionCount), FormatCounters(result));
        Assert.True(result.ProfilingCounters.TryGetValue(
            "Llvm.convert.output.function_fingerprints",
            out var llvmFunctionCount), FormatCounters(result));
        Assert.Equal(result.MirFunctionFingerprints.Functions.Count, mirFunctionCount);
        Assert.Equal(result.LlvmFunctionFingerprints.Functions.Count, llvmFunctionCount);

        var snapshot = CompilationProfilingFormatter.CreateSnapshot(result);
        Assert.NotNull(snapshot.MirFunctionFingerprints);
        Assert.NotNull(snapshot.ModuleMirArtifacts);
        Assert.NotNull(snapshot.ModuleDependencySignatures);
        Assert.NotNull(snapshot.LlvmFunctionFingerprints);
        Assert.NotNull(snapshot.LlvmFunctionFragments);
        Assert.NotNull(snapshot.LlvmModuleEnvelope);
        Assert.NotNull(snapshot.LlvmCodegenUnitPlan);
        Assert.Equal(result.MirFunctionFingerprints.ModuleFingerprint, snapshot.MirFunctionFingerprints!.ModuleFingerprint);
        Assert.Equal(result.ModuleMirArtifactSnapshot.Nodes.Count, snapshot.ModuleMirArtifacts!.Nodes.Count);
        Assert.Equal(result.ModuleDependencySignatureSnapshot.Nodes.Count, snapshot.ModuleDependencySignatures!.Nodes.Count);
        Assert.Equal(result.LlvmFunctionFingerprints.ModuleFingerprint, snapshot.LlvmFunctionFingerprints!.ModuleFingerprint);
        Assert.Equal(result.LlvmFunctionFragments.ModuleFingerprint, snapshot.LlvmFunctionFragments!.ModuleFingerprint);
        Assert.Equal(result.LlvmModuleEnvelope.EnvelopeFingerprint, snapshot.LlvmModuleEnvelope!.EnvelopeFingerprint);
        Assert.Equal(result.LlvmCodegenUnitPlan.PlanFingerprint, snapshot.LlvmCodegenUnitPlan!.PlanFingerprint);
    }

    [Fact]
    public void Run_WithDetailedProfilingWithoutIncrementalCompilation_DoesNotCreateModuleStatePayloads()
    {
        var result = Run(enableDetailedProfiling: true);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Null(result.ModuleNamerStatePayloads);
        Assert.Null(result.ModuleTypesStatePayloads);
        Assert.Null(result.ModuleHirStatePayloads);
        Assert.Null(result.ModuleMirStatePayloads);
    }

    [Fact]
    public void Run_ReusesSameProcessLiveStateForExactMirInput()
    {
        var first = RunLiveStateSource();
        var second = RunLiveStateSource();

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.NotNull(second.SymbolTable);
        Assert.NotNull(second.TypeInferer);
        Assert.NotNull(second.HirModule);
        Assert.NotNull(second.MirModule);
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Build.liveState.Mir.hits"));
    }

    [Fact]
    public void Run_ToMir_EmitsSerializableLiveStatePayload()
    {
        var result = RunMirPayloadSource(Source);

        Assert.True(result.Success, FormatDiagnostics(result));
        var payload = Assert.IsType<CompilationLiveStatePayload>(result.CompilationLiveStatePayload);
        Assert.Equal(CompilationLiveStatePayload.CurrentSchemaVersion, payload.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(payload.PayloadHash));
        Assert.NotEmpty(payload.SymbolTable.Symbols);
        Assert.NotEmpty(payload.SymbolTable.Scopes);
        Assert.NotEmpty(payload.ModuleRegistry.Modules);
        Assert.NotNull(payload.HirGraph.Module);
        Assert.NotNull(payload.HirState.Module);
        Assert.True(payload.HirState.IsRestorable, string.Join(Environment.NewLine, payload.HirState.UnsupportedNodeKinds));
        Assert.NotNull(payload.MirGraph.Module);
        Assert.NotEmpty(payload.HirGraph.Nodes);
        Assert.NotEmpty(payload.HirGraph.Edges);
        Assert.NotEmpty(payload.HirState.Module!.Declarations);
        Assert.NotEmpty(payload.MirGraph.Functions);
        Assert.NotEmpty(payload.AstInferredTypes.Entries);
        Assert.True(payload.RemapPlan.IsIdentity);
        Assert.Equal(1, result.ProfilingCounters.GetValueOrDefault("Build.liveStatePayload.Mir.present"));
        Assert.Equal(1, result.ProfilingCounters.GetValueOrDefault("Build.liveStatePayload.Mir.remapIdentity"));
    }

    [Fact]
    public void LiveStatePayload_RoundTripsThroughJson()
    {
        var result = RunMirPayloadSource(Source);
        var payload = Assert.IsType<CompilationLiveStatePayload>(result.CompilationLiveStatePayload);

        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<CompilationLiveStatePayload>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(payload.PayloadHash, roundTripped!.PayloadHash);
        Assert.Equal(payload.SymbolTable.Hash, roundTripped.SymbolTable.Hash);
        Assert.Equal(payload.ModuleRegistry.Hash, roundTripped.ModuleRegistry.Hash);
        Assert.Equal(payload.TypeSubstitution.Hash, roundTripped.TypeSubstitution.Hash);
        Assert.Equal(payload.AstInferredTypes.Hash, roundTripped.AstInferredTypes.Hash);
        Assert.Equal(payload.HirGraph.Hash, roundTripped.HirGraph.Hash);
        Assert.Equal(payload.HirState.Hash, roundTripped.HirState.Hash);
        Assert.Equal(payload.MirGraph.Hash, roundTripped.MirGraph.Hash);
    }

    [Fact]
    public void ModuleHirStatePayload_RestoresFormatterEquivalentHir()
    {
        var result = RunMirPayloadSource("""
Main :: module {
    answer :: Int = 40 + 2

    main :: Unit -> Unit
    {
        _ => ()
    }
}
""");

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<HirModule>(result.HirModule);
        var payload = ModuleHirStatePayload.Create(module);

        Assert.True(payload.IsRestorable, string.Join(Environment.NewLine, payload.UnsupportedNodeKinds));
        Assert.True(payload.TryRestore(out var restored));
        Assert.Equal(HirFormatter.FormatHir(module), HirFormatter.FormatHir(restored));
    }

    [Fact]
    public void LiveStatePayload_ValidationDetectsSourceDrift()
    {
        var first = RunMirPayloadSource(Source);
        var same = RunMirPayloadSource(Source);
        var changed = RunMirPayloadSource("""
Main :: module {
    helper :: Unit -> Unit { _ => () }

    main :: Unit -> Unit { _ =>
        ()
    }
}
""");

        var firstPayload = Assert.IsType<CompilationLiveStatePayload>(first.CompilationLiveStatePayload);
        var samePayload = Assert.IsType<CompilationLiveStatePayload>(same.CompilationLiveStatePayload);
        var changedPayload = Assert.IsType<CompilationLiveStatePayload>(changed.CompilationLiveStatePayload);

        var sameValidation = firstPayload.ValidateAgainst(samePayload);
        var changedValidation = firstPayload.ValidateAgainst(changedPayload);

        Assert.True(sameValidation.IsValid, string.Join(Environment.NewLine, sameValidation.Failures));
        Assert.True(sameValidation.IsRestorable, string.Join(Environment.NewLine, sameValidation.Failures));
        Assert.False(changedValidation.IsValid);
        Assert.False(changedValidation.IsRestorable);
        Assert.Contains(changedValidation.Failures, static failure =>
            failure.StartsWith("InputHash:", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WithPreviousFunctionFingerprints_EmitsCrossBuildUnchangedCounters()
    {
        var first = Run(enableDetailedProfiling: true);
        var second = new CompilationPipeline(Source, new CompilationOptions
        {
            InputFile = "profiling_gate.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousMirFunctionFingerprintSnapshot = Assert.IsType<MirFunctionFingerprintSnapshot>(first.MirFunctionFingerprints),
            PreviousLlvmFunctionFingerprintSnapshot = Assert.IsType<LlvmFunctionFingerprintSnapshot>(first.LlvmFunctionFingerprints),
            PreviousLlvmFunctionFragmentSnapshot = Assert.IsType<LlvmFunctionFragmentSnapshot>(first.LlvmFunctionFragments)
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.True(second.ProfilingCounters.TryGetValue(
            "Mir.previous_build.fingerprint_unchanged_functions",
            out var unchangedMir), FormatCounters(second));
        Assert.True(second.ProfilingCounters.TryGetValue(
            "Llvm.previous_build.fingerprint_unchanged_functions",
            out var unchangedLlvm), FormatCounters(second));
        Assert.Equal(second.MirFunctionFingerprints!.Functions.Count, unchangedMir);
        Assert.Equal(second.LlvmFunctionFingerprints!.Functions.Count, unchangedLlvm);
        Assert.Equal(0, second.ProfilingCounters["Mir.previous_build.fingerprint_changed_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.fingerprint_changed_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Mir.previous_build.fingerprint_added_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.fingerprint_added_functions"]);
        Assert.Contains("Mir.previous_build.current_module_fingerprint", second.ProfilingCounters.Keys);
        Assert.Contains("Llvm.previous_build.current_module_fingerprint", second.ProfilingCounters.Keys);
        Assert.Equal(unchangedMir, second.ProfilingCounters["Mir.previous_build.worklist_restore_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Mir.previous_build.worklist_rebuild_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Mir.previous_build.worklist_remove_functions"]);
        Assert.Equal(unchangedLlvm, second.ProfilingCounters["Llvm.previous_build.worklist_restore_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.worklist_rebuild_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.worklist_remove_functions"]);
        Assert.Equal(
            second.LlvmFunctionFragments!.Functions.Count,
            second.ProfilingCounters["Llvm.previous_build.fragment_restorable_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.fragment_changed_or_missing_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.fragment_rebuild_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.fragment_remove_functions"]);
        Assert.True(second.ProfilingCounters["Llvm.previous_build.fragment_restorable_ir_bytes"] > 0);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.fragment_rebuild_ir_bytes"]);
        Assert.Contains("Llvm.previous_build.current_fragment_module_fingerprint", second.ProfilingCounters.Keys);
        Assert.Equal(
            second.LlvmFunctionFragments.Functions.Count,
            second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_restored_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_rebuilt_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_removed_functions"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_fallback_rebuild_functions"]);
        Assert.True(second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_restored_ir_bytes"] > 0);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_rebuilt_ir_bytes"]);
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_matches_current_ir"]);
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_applied"]);
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.fragment_restore_emit_hits"]);
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.fragment_restore_emit_skipped_emitter"]);
        Assert.True(second.ProfilingCounters["Llvm.previous_build.object_group_restore_groups"] > 0);
        Assert.Equal(
            second.ProfilingCounters["Llvm.previous_build.object_group_restore_groups"],
            second.ProfilingCounters["Llvm.previous_build.object_group_restore_restorable_groups"]);
        Assert.Equal(0, second.ProfilingCounters["Llvm.previous_build.object_group_restore_rebuild_groups"]);

        var mirDiff = Assert.IsType<FunctionFingerprintDiffSnapshot>(second.MirFunctionFingerprintDiff);
        var llvmDiff = Assert.IsType<FunctionFingerprintDiffSnapshot>(second.LlvmFunctionFingerprintDiff);
        var mirWorklist = Assert.IsType<FunctionWorklistSnapshot>(second.MirFunctionWorklist);
        var llvmWorklist = Assert.IsType<FunctionWorklistSnapshot>(second.LlvmFunctionWorklist);
        var llvmFragmentRestorePlan = Assert.IsType<LlvmFunctionFragmentRestorePlanSnapshot>(second.LlvmFunctionFragmentRestorePlan);
        var llvmFragmentRestoreResult = Assert.IsType<LlvmFunctionFragmentRestoreResultSnapshot>(second.LlvmFunctionFragmentRestoreResult);
        var llvmObjectGroupRestorePlan = Assert.IsType<LlvmObjectGroupRestorePlanSnapshot>(second.LlvmObjectGroupRestorePlan);
        Assert.Equal(second.MirFunctionFingerprints.Functions.Count, mirDiff.Count(FunctionFingerprintDiffStatus.Unchanged));
        Assert.Equal(second.LlvmFunctionFingerprints.Functions.Count, llvmDiff.Count(FunctionFingerprintDiffStatus.Unchanged));
        Assert.Equal(mirDiff.Count(FunctionFingerprintDiffStatus.Unchanged), mirWorklist.Count(FunctionWorklistAction.Restore));
        Assert.Equal(llvmDiff.Count(FunctionFingerprintDiffStatus.Unchanged), llvmWorklist.Count(FunctionWorklistAction.Restore));
        Assert.Equal(second.LlvmFunctionFragments.Functions.Count, llvmFragmentRestorePlan.Count(LlvmFunctionFragmentRestoreAction.Restore));
        Assert.Equal(llvmObjectGroupRestorePlan.Groups.Count, llvmObjectGroupRestorePlan.Count(LlvmObjectGroupRestoreAction.Restore));
        Assert.True(llvmFragmentRestoreResult.Applied);
        Assert.True(llvmFragmentRestoreResult.MatchesCurrentIr);
        Assert.Equal(second.LlvmFunctionFragments.ModuleFingerprint, llvmFragmentRestoreResult.OutputModuleFingerprint);

        var snapshot = CompilationProfilingFormatter.CreateSnapshot(second);
        Assert.NotNull(snapshot.MirFunctionFingerprintDiff);
        Assert.NotNull(snapshot.LlvmFunctionFingerprintDiff);
        Assert.NotNull(snapshot.MirFunctionWorklist);
        Assert.NotNull(snapshot.LlvmFunctionWorklist);
        Assert.NotNull(snapshot.LlvmFunctionFragmentRestorePlan);
        Assert.NotNull(snapshot.LlvmFunctionFragmentRestoreResult);
        Assert.NotNull(snapshot.LlvmObjectGroupRestorePlan);
        Assert.Equal(mirDiff.Functions.Count, snapshot.MirFunctionFingerprintDiff!.Functions.Count);
        Assert.Equal(llvmDiff.Functions.Count, snapshot.LlvmFunctionFingerprintDiff!.Functions.Count);
        Assert.Equal(mirWorklist.Functions.Count, snapshot.MirFunctionWorklist!.Functions.Count);
        Assert.Equal(llvmWorklist.Functions.Count, snapshot.LlvmFunctionWorklist!.Functions.Count);
        Assert.Equal(llvmFragmentRestorePlan.Functions.Count, snapshot.LlvmFunctionFragmentRestorePlan!.Functions.Count);
        Assert.Equal(llvmObjectGroupRestorePlan.Groups.Count, snapshot.LlvmObjectGroupRestorePlan!.Groups.Count);
        Assert.True(snapshot.LlvmFunctionFragmentRestoreResult!.Applied);
    }

    [Fact]
    public void Run_WithMatchingPreviousMirFingerprint_CanRestoreLlvmIrText()
    {
        var first = Run(enableDetailedProfiling: true);
        var second = new CompilationPipeline(Source, new CompilationOptions
        {
            InputFile = "profiling_gate.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            AllowLlvmIrTextRestore = true,
            UseColors = false,
            PreviousMirFunctionFingerprintSnapshot = Assert.IsType<MirFunctionFingerprintSnapshot>(first.MirFunctionFingerprints),
            PreviousLlvmFunctionFragmentSnapshot = Assert.IsType<LlvmFunctionFragmentSnapshot>(first.LlvmFunctionFragments),
            PreviousLlvmModuleEnvelopeSnapshot = Assert.IsType<LlvmModuleEnvelopeSnapshot>(first.LlvmModuleEnvelope)
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.NotNull(second.LlvmIrText);
        Assert.True(second.LlvmModule == null, FormatCounters(second));
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.ir_text_restore_hits"]);
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.ir_text_restore_module_fingerprint_match"]);
        Assert.Equal(first.LlvmFunctionFragments!.Functions.Count, second.ProfilingCounters["Llvm.previous_build.ir_text_restore_functions"]);
        Assert.DoesNotContain("Llvm.convert.output.functions", second.ProfilingCounters.Keys);
        Assert.Contains(second.SubphaseMetrics, static metric =>
            metric.Phase == CompilationPhase.Llvm &&
            string.Equals(metric.Name, "restore_ir_text_from_previous_fragments", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WithChangedFunctionPreviousFragments_EmitsIrFromMixedFragmentRestore()
    {
        const string firstSource = """
Main :: module {
    keep :: Int -> Int { x => x }
    change :: Int -> Int { x => x + 1 }
    main :: Unit -> Unit { _ => () }
}
""";
        const string secondSource = """
Main :: module {
    keep :: Int -> Int { x => x }
    change :: Int -> Int {
        x => {
            y := x + 2;
            y
        }
    }
    main :: Unit -> Unit { _ => () }
}
""";
        var first = RunLlvmSource(firstSource, previousFragments: null);
        var second = RunLlvmSource(
            secondSource,
            Assert.IsType<LlvmFunctionFragmentSnapshot>(first.LlvmFunctionFragments));

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.NotNull(second.LlvmFunctionFragmentRestoreResult);
        Assert.True(second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_restored_functions"] > 0, FormatCounters(second));
        Assert.True(second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_rebuilt_functions"] > 0, FormatCounters(second));
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_matches_current_ir"]);
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_applied"]);
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.fragment_restore_emit_hits"]);
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.fragment_restore_emit_skipped_emitter"]);
    }

    [Fact]
    public void Run_WithChangedFunctionPreviousMirAndEnvelope_RestoresLlvmIrFromSelectedFragments()
    {
        const string firstSource = """
Main :: module {
    keep :: Int -> Int { x => x }
    change :: Int -> Int { x => x + 1 }
    main :: Unit -> Unit { _ => () }
}
""";
        const string secondSource = """
Main :: module {
    keep :: Int -> Int { x => x }
    change :: Int -> Int { x => x + 2 }
    main :: Unit -> Unit { _ => () }
}
""";
        var first = RunLlvmSource(firstSource, previousFragments: null);
        var second = new CompilationPipeline(secondSource, new CompilationOptions
        {
            InputFile = "llvm_fragment_restore_emit.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            AllowLlvmIrTextRestore = true,
            UseColors = false,
            PreviousMirFunctionFingerprintSnapshot = Assert.IsType<MirFunctionFingerprintSnapshot>(first.MirFunctionFingerprints),
            PreviousLlvmFunctionFragmentSnapshot = Assert.IsType<LlvmFunctionFragmentSnapshot>(first.LlvmFunctionFragments),
            PreviousLlvmModuleEnvelopeSnapshot = Assert.IsType<LlvmModuleEnvelopeSnapshot>(first.LlvmModuleEnvelope)
        }).Run();

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.NotNull(second.LlvmIrText);
        Assert.True(second.LlvmModule == null, FormatCounters(second));
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.selected_fragment_restore_hits"]);
        Assert.True(second.ProfilingCounters["Llvm.previous_build.selected_fragment_restore_candidate_functions"] > 0, FormatCounters(second));
        Assert.True(second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_restored_functions"] > 0, FormatCounters(second));
        Assert.True(second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_rebuilt_functions"] > 0, FormatCounters(second));
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_applied"]);
        Assert.DoesNotContain("Llvm.convert.output.functions", second.ProfilingCounters.Keys);
        Assert.Contains(second.SubphaseMetrics, static metric =>
            metric.Phase == CompilationPhase.Llvm &&
            string.Equals(metric.Name, "convert_selected_functions", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WithChangedFunctionPreviousObjectGroupArtifacts_RestoresNativeObjectGroupsFromSelectedFragments()
    {
        const string firstSource = """
Main :: module {
    keep :: Int -> Int { x => x }
    change :: Int -> Int { x => x + 1 }
    main :: Unit -> Unit { _ => () }
}
""";
        const string secondSource = """
Main :: module {
    keep :: Int -> Int { x => x }
    change :: Int -> Int { x => x + 2 }
    main :: Unit -> Unit { _ => () }
}
""";
        var first = RunLlvmSource(firstSource, previousFragments: null);
        var second = new CompilationPipeline(secondSource, new CompilationOptions
        {
            InputFile = "llvm_fragment_restore_emit.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            AllowNativeObjectGroupRestore = true,
            UseColors = false,
            PreviousMirFunctionFingerprintSnapshot = Assert.IsType<MirFunctionFingerprintSnapshot>(first.MirFunctionFingerprints),
            PreviousLlvmFunctionFragmentSnapshot = Assert.IsType<LlvmFunctionFragmentSnapshot>(first.LlvmFunctionFragments),
            PreviousLlvmModuleEnvelopeSnapshot = Assert.IsType<LlvmModuleEnvelopeSnapshot>(first.LlvmModuleEnvelope),
            PreviousLlvmCodegenUnitPlanSnapshot = Assert.IsType<LlvmCodegenUnitPlanSnapshot>(first.LlvmCodegenUnitPlan)
        }).Run();

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.Null(second.LlvmModule);
        Assert.NotNull(second.LlvmFunctionFragments);
        Assert.NotNull(second.LlvmCodegenUnitPlan);
        Assert.NotNull(second.LlvmObjectGroupRestorePlan);
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.native_selected_object_group_restore_hits"]);
        Assert.True(second.ProfilingCounters["Llvm.previous_build.selected_fragment_restore_candidate_functions"] > 0, FormatCounters(second));
        Assert.True(second.ProfilingCounters["Llvm.previous_build.object_group_restore_restorable_groups"] > 0, FormatCounters(second));
        Assert.True(second.ProfilingCounters["Llvm.previous_build.object_group_restore_rebuild_groups"] > 0, FormatCounters(second));
        Assert.DoesNotContain("Llvm.convert.output.functions", second.ProfilingCounters.Keys);
    }

    [Fact]
    public void Run_WithChangedFunctionPreviousFragments_RestoresNativeFullModuleIrFromSelectedFragments()
    {
        const string firstSource = """
Main :: module {
    keep :: Int -> Int { x => x }
    change :: Int -> Int { x => x + 1 }
    main :: Unit -> Unit { _ => () }
}
""";
        const string secondSource = """
Main :: module {
    keep :: Int -> Int { x => x }
    change :: Int -> Int { x => x + 2 }
    main :: Unit -> Unit { _ => () }
}
""";
        var first = RunLlvmSource(firstSource, previousFragments: null);
        var second = new CompilationPipeline(secondSource, new CompilationOptions
        {
            InputFile = "llvm_fragment_restore_emit.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            AllowLlvmIrTextRestore = true,
            UseColors = false,
            PreviousMirFunctionFingerprintSnapshot = Assert.IsType<MirFunctionFingerprintSnapshot>(first.MirFunctionFingerprints),
            PreviousLlvmFunctionFragmentSnapshot = Assert.IsType<LlvmFunctionFragmentSnapshot>(first.LlvmFunctionFragments),
            PreviousLlvmModuleEnvelopeSnapshot = Assert.IsType<LlvmModuleEnvelopeSnapshot>(first.LlvmModuleEnvelope)
        }).Run();

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.Null(second.LlvmModule);
        Assert.NotNull(second.LlvmIrText);
        Assert.NotNull(second.LlvmFunctionFragments);
        Assert.NotNull(second.LlvmModuleEnvelope);
        Assert.Equal(1, second.ProfilingCounters["Llvm.previous_build.selected_fragment_restore_hits"]);
        Assert.True(second.ProfilingCounters["Llvm.previous_build.fragment_restore_execute_rebuilt_functions"] > 0, FormatCounters(second));
        Assert.DoesNotContain("Llvm.convert.output.functions", second.ProfilingCounters.Keys);
    }

    [Fact]
    public void Run_WithDetailedProfiling_RefreshesFinalSymbolTableCounters()
    {
        var result = new CompilationPipeline("""
Main :: module {
    Box :: type { Box(Int) }

    main :: Unit -> Box
    {
        _ => Box(1)
    }
}
""", new CompilationOptions
        {
            InputFile = "final_symbol_counter_refresh.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(
            result.ProfilingCounters.TryGetValue(
                "Namer.moduleRegistry.memberOwnerIndex.hits",
                out var hits),
            FormatCounters(result));
        Assert.True(hits > 0, FormatCounters(result));
    }

    [Fact]
    public void Run_WithDetailedProfiling_EmitsModuleGraphAndSignatureSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_graph_profile_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var entryFile = Path.Combine(tempDir, "Main.eidos");
            var libFile = Path.Combine(tempDir, "Lib.eidos");
            File.WriteAllText(entryFile, """
Main :: module {
    import Lib

    main :: Unit -> Unit { _ => () }
}
""");
            File.WriteAllText(libFile, """
Lib :: module {
    export id :: Int -> Int { value => value }
}
""");

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                EnableDetailedProfiling = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            var graph = Assert.IsType<ProjectModuleGraphSnapshot>(result.ModuleGraphSnapshot);
            var schedule = Assert.IsType<ProjectModuleBuildSchedule>(result.ModuleBuildSchedule);
            var signatures = Assert.IsType<ProjectModuleSignatureSnapshot>(result.ModuleSignatureSnapshot);
            var semanticSignatures = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(result.ModuleSemanticSignatureSnapshot);
            var dependencySignatures = Assert.IsType<ProjectModuleDependencySignatureSnapshot>(result.ModuleDependencySignatureSnapshot);
            var invalidation = Assert.IsType<ProjectModuleInvalidationPlan>(result.ModuleInvalidationPlan);
            var execution = Assert.IsType<ProjectModuleExecutionPlan>(result.ModuleExecutionPlan);
            var parallelExecution = Assert.IsType<ProjectModuleParallelExecutionSnapshot>(result.ModuleParallelExecution);
            Assert.Contains(graph.Nodes, node => node.ModuleKey == "Main" && node.Dependencies.SequenceEqual(["Lib"]));
            Assert.Contains(graph.Nodes, node => node.ModuleKey == "Lib" && node.Dependents.SequenceEqual(["Main"]));
            Assert.Equal(graph.Nodes.Count, signatures.Nodes.Count);
            Assert.Equal(graph.Nodes.Count, semanticSignatures.Nodes.Count);
            Assert.Equal(graph.Nodes.Count, dependencySignatures.Nodes.Count);
            Assert.Equal(graph.TopologicalLayers.Count, schedule.Layers.Count);
            Assert.Equal(semanticSignatures.Nodes.Count, invalidation.AffectedModules.Count);
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleGraph.modules", out var modules), FormatCounters(result));
            Assert.True(modules >= 2, FormatCounters(result));
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleGraph.edges", out var edges), FormatCounters(result));
            Assert.True(edges >= 1, FormatCounters(result));
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleSchedule.layers", out var scheduleLayers), FormatCounters(result));
            Assert.Equal(schedule.Layers.Count, scheduleLayers);
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleSchedule.maxParallelWidth", out var scheduleWidth), FormatCounters(result));
            Assert.Equal(schedule.MaxParallelWidth, scheduleWidth);
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleSignatures.modules", out var signatureModules), FormatCounters(result));
            Assert.Equal(signatures.Nodes.Count, signatureModules);
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleSemanticSignatures.modules", out var semanticSignatureModules), FormatCounters(result));
            Assert.Equal(semanticSignatures.Nodes.Count, semanticSignatureModules);
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleSemanticSignatures.declarations", out var semanticDeclarations), FormatCounters(result));
            Assert.True(semanticDeclarations >= 1, FormatCounters(result));
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleDependencySignatures.semanticAvailableModules", out var dependencySemanticModules), FormatCounters(result));
            Assert.Equal(semanticSignatures.Nodes.Count, dependencySemanticModules);
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleInvalidation.affected", out var affectedModules), FormatCounters(result));
            Assert.Equal(invalidation.AffectedModules.Count, affectedModules);
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleInvalidation.unchanged", out var unchangedModules), FormatCounters(result));
            Assert.Equal(invalidation.UnchangedModules.Count, unchangedModules);
            Assert.Equal(invalidation.AffectedModules.Count, execution.CompileModules);
            Assert.Equal(invalidation.UnchangedModules.Count, execution.RestoreModules);
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleExecution.compileModules", out var compileModules), FormatCounters(result));
            Assert.Equal(execution.CompileModules, compileModules);
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleExecution.restoreModules", out var restoreModules), FormatCounters(result));
            Assert.Equal(execution.RestoreModules, restoreModules);
            Assert.Equal(execution.TotalModules, parallelExecution.TotalModules);
            Assert.Equal(execution.TotalModules, parallelExecution.CompletedModules + parallelExecution.SkippedModules);
            Assert.True(parallelExecution.MaxObservedParallelism >= 1);
            Assert.True(result.ProfilingCounters.TryGetValue("Build.moduleParallelExecution.maxObservedParallelism", out var observedParallelism), FormatCounters(result));
            Assert.Equal(parallelExecution.MaxObservedParallelism, observedParallelism);
            Assert.Equal(1, result.ProfilingCounters.GetValueOrDefault("Build.moduleParallelExecution.hasRealTaskExecution"));

            var snapshot = CompilationProfilingFormatter.CreateSnapshot(result);
            Assert.NotNull(snapshot.ModuleGraph);
            Assert.NotNull(snapshot.ModuleBuildSchedule);
            Assert.NotNull(snapshot.ModuleSignatures);
            Assert.NotNull(snapshot.ModuleSemanticSignatures);
            Assert.NotNull(snapshot.ModuleDependencySignatures);
            Assert.NotNull(snapshot.ModuleInvalidation);
            Assert.NotNull(snapshot.ModuleExecution);
            Assert.NotNull(snapshot.ModuleParallelExecution);
            Assert.Equal(graph.Nodes.Count, snapshot.ModuleGraph.Nodes.Count);
            Assert.Equal(schedule.Layers.Count, snapshot.ModuleBuildSchedule.Layers.Count);
            Assert.Equal(signatures.Nodes.Count, snapshot.ModuleSignatures.Nodes.Count);
            Assert.Equal(semanticSignatures.Nodes.Count, snapshot.ModuleSemanticSignatures.Nodes.Count);
            Assert.Equal(dependencySignatures.Nodes.Count, snapshot.ModuleDependencySignatures.Nodes.Count);
            Assert.Equal(invalidation.AffectedModules.Count, snapshot.ModuleInvalidation.AffectedModules.Count);
            Assert.Equal(execution.CompileModules, snapshot.ModuleExecution.CompileModules);
            Assert.Equal(parallelExecution.MaxObservedParallelism, snapshot.ModuleParallelExecution.MaxObservedParallelism);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Run_WithDetailedProfiling_EmitsTypedSemanticSnapshotAfterTypes()
    {
        var result = new CompilationPipeline("""
Main :: module {
    Box :: type { Box(Int) }

    id :: Int -> Int
    {
        value => value
    }
}
""", new CompilationOptions
        {
            InputFile = "typed_semantic_profile.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
        var typed = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(result.ModuleTypedSemanticSnapshot);
        Assert.NotEmpty(typed.Nodes);
        Assert.Contains(typed.Nodes.SelectMany(static node => node.Declarations), declaration => declaration.Name == "Box");
        Assert.True(result.ProfilingCounters.TryGetValue(
            "Build.moduleTypedSemanticSignatures.modules",
            out var modules), FormatCounters(result));
        Assert.Equal(typed.Nodes.Count, modules);
        Assert.True(result.ProfilingCounters.TryGetValue(
            "Build.moduleTypedSemanticSignatures.declarations",
            out var declarations), FormatCounters(result));
        Assert.Equal(typed.Nodes.Sum(static node => node.Declarations.Count), declarations);
        var dependencySignatures = Assert.IsType<ProjectModuleDependencySignatureSnapshot>(result.ModuleDependencySignatureSnapshot);
        Assert.Equal(typed.Nodes.Count, dependencySignatures.Nodes.Count);
        Assert.True(result.ProfilingCounters.TryGetValue(
            "Build.moduleDependencySignatures.typedAvailableModules",
            out var typedDependencyModules), FormatCounters(result));
        Assert.Equal(typed.Nodes.Count, typedDependencyModules);
        Assert.True(result.ProfilingCounters.TryGetValue(
            "Build.moduleDependencySignatures.memberIndexAvailableModules",
            out var memberDependencyModules), FormatCounters(result));
        Assert.True(memberDependencyModules >= 1, FormatCounters(result));
        var typedInvalidation = Assert.IsType<ProjectModuleInvalidationPlan>(result.ModuleTypedInvalidationPlan);
        var typedExecution = Assert.IsType<ProjectModuleExecutionPlan>(result.ModuleTypedExecutionPlan);
        var typedParallelExecution = Assert.IsType<ProjectModuleParallelExecutionSnapshot>(result.ModuleTypedParallelExecution);
        Assert.Equal(typed.Nodes.Count, typedInvalidation.AffectedModules.Count);
        Assert.True(result.ProfilingCounters.TryGetValue(
            "Build.moduleTypedInvalidation.affected",
            out var typedAffected), FormatCounters(result));
        Assert.Equal(typedInvalidation.AffectedModules.Count, typedAffected);
        Assert.Equal(typedInvalidation.AffectedModules.Count, typedExecution.CompileModules);
        Assert.True(result.ProfilingCounters.TryGetValue(
            "Build.moduleTypedExecution.compileModules",
            out var typedCompileModules), FormatCounters(result));
        Assert.Equal(typedExecution.CompileModules, typedCompileModules);
        Assert.Equal(typedExecution.TotalModules, typedParallelExecution.TotalModules);
        Assert.True(result.ProfilingCounters.TryGetValue(
            "Build.moduleTypedParallelExecution.maxObservedParallelism",
            out var typedParallelism), FormatCounters(result));
        Assert.Equal(typedParallelExecution.MaxObservedParallelism, typedParallelism);

        var snapshot = CompilationProfilingFormatter.CreateSnapshot(result);
        Assert.NotNull(snapshot.ModuleTypedSemanticSignatures);
        Assert.NotNull(snapshot.ModuleDependencySignatures);
        Assert.NotNull(snapshot.ModuleTypedInvalidation);
        Assert.NotNull(snapshot.ModuleTypedExecution);
        Assert.NotNull(snapshot.ModuleTypedParallelExecution);
        Assert.Equal(typed.Nodes.Count, snapshot.ModuleTypedSemanticSignatures!.Nodes.Count);
        Assert.Equal(dependencySignatures.Nodes.Count, snapshot.ModuleDependencySignatures!.Nodes.Count);
        Assert.Equal(typedInvalidation.AffectedModules.Count, snapshot.ModuleTypedInvalidation!.AffectedModules.Count);
        Assert.Equal(typedExecution.CompileModules, snapshot.ModuleTypedExecution!.CompileModules);
        Assert.Equal(typedParallelExecution.MaxObservedParallelism, snapshot.ModuleTypedParallelExecution!.MaxObservedParallelism);
    }

    [Fact]
    public void Run_WithIncrementalCompilation_EmitsModuleTypesStatePayloadsAfterTypes()
    {
        const string source = """
Main :: module {
    Box :: type { Box(Int) }

    id :: Int -> Int
    {
        value => value
    }

    keep[T] :: T -> T
    {
        value => value
    }

    Copy :: trait {
        copy :: Self -> Self
    }

    needsCopy[T: Copy] :: T -> T
    {
        value => value
    }

    DefaultCapacity :: comptime match (40, 2) { (base, extra) => base + extra, _ => 0 };
    StaticList :: comptime [1, 2, 3];
}
""";
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_types_payload.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
        var typed = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(result.ModuleTypedSemanticSnapshot);
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleTypesStatePayload>>(result.ModuleTypesStatePayloads);
        Assert.Equal(typed.Nodes.Count, payloads.Count);
        var typedNode = Assert.Single(typed.Nodes);
        var payload = Assert.Single(payloads, candidate => candidate.ModuleKey == typedNode.ModuleKey);
        Assert.Equal(ModuleTypesStatePayload.CurrentSchemaVersion, payload.SchemaVersion);
        Assert.True(payload.HasValidPayloadHash());
        Assert.Equal(typedNode.ModuleKey, payload.TypedSemantic.ModuleKey);
        Assert.Contains(payload.TypedSemantic.Declarations, declaration => declaration.Name == "Box");
        Assert.Equal(typedNode.TypedSemanticHash, payload.TypedSemantic.TypedSemanticHash);
        Assert.Equal(TypeEnvPayload.CurrentSchemaVersion, payload.TypeEnv.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(payload.TypeEnv.Hash));
        Assert.Contains(
            payload.TypeEnv.Bindings,
            binding => binding.Scheme.Type.Kind == nameof(TyFun) &&
                       binding.Scheme.Type.Parameters is { Count: 1 } &&
                       binding.Scheme.Type.Result?.Kind == nameof(TyCon));
        var functionScheme = Assert.Single(
            payload.TypeEnv.Bindings,
            binding => binding.Scheme.Type.Kind == nameof(TyFun) &&
                       binding.Scheme.Type.Parameters is { Count: 1 } &&
                       binding.Scheme.Type.Result?.Kind == nameof(TyCon));
        Assert.True(functionScheme.Scheme.TryRestoreTypeScheme(out var restoredScheme));
        Assert.Equal(functionScheme.Scheme.Display, restoredScheme.ToString());
        Assert.False(string.IsNullOrWhiteSpace(payload.TypeSubstitution.Hash));
        Assert.False(string.IsNullOrWhiteSpace(payload.AstInferredTypes.Hash));
        Assert.Equal(AstInferredTypeMapPayload.CurrentSchemaVersion, payload.AstInferredTypes.SchemaVersion);
        Assert.Contains(
            payload.AstInferredTypes.Entries,
            entry => entry.ResolvedTypeShape.Kind == nameof(TyCon) &&
                     entry.ResolvedTypeShape.Name == "Int");
        Assert.Contains(
            payload.AstInferredTypes.Entries,
            entry => entry.ResolvedTypeShape.Kind == nameof(TyFun) &&
                     entry.ResolvedTypeShape.Parameters is { Count: 1 } &&
                     entry.ResolvedTypeShape.Result?.Kind == nameof(TyCon));
        Assert.All(payload.AstInferredTypes.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.RawTypeShape.Hash));
            Assert.False(string.IsNullOrWhiteSpace(entry.ResolvedTypeShape.CanonicalKey));
            Assert.False(string.IsNullOrWhiteSpace(entry.ResolvedTypeShape.Hash));
            Assert.True(entry.ResolvedTypeShape.TryRestoreType(out var restoredType));
            Assert.Equal(entry.ResolvedType, restoredType.ToString());
            Assert.Equal(AstInferredTypeStableKeyPayload.CurrentSchemaVersion, entry.StableIdentity.SchemaVersion);
            Assert.Equal(entry.StableKey, entry.StableIdentity.StableKey);
            Assert.False(string.IsNullOrWhiteSpace(entry.StableKey));
            // The stable key must not depend on SymbolId (which can churn across
            // builds), so the structural identity inputs rather than the symbol id
            // drive it. We assert against the compositional fields, not the hex
            // digest itself: a hex SHA-256 can coincidentally contain a decimal
            // symbol id as a substring.
            var symbolIdText = entry.SymbolId.ToString();
            Assert.DoesNotContain(symbolIdText, entry.StableIdentity.ModuleKey, StringComparison.Ordinal);
            Assert.DoesNotContain(symbolIdText, entry.StableIdentity.ModuleIdentityKey, StringComparison.Ordinal);
            Assert.DoesNotContain(symbolIdText, entry.StableIdentity.Details, StringComparison.Ordinal);
            Assert.DoesNotContain(entry.SymbolId, entry.StableIdentity.SiblingPath);
            Assert.False(string.IsNullOrWhiteSpace(entry.StableIdentity.ModuleKey));
            Assert.False(string.IsNullOrWhiteSpace(entry.StableIdentity.ModuleIdentityKey));
            Assert.NotEmpty(entry.StableIdentity.SiblingPath);
        });

        var firstEntry = Assert.Single(
            payload.AstInferredTypes.Entries,
            entry => entry.ResolvedTypeShape.Kind == nameof(TyFun) &&
                     entry.StableIdentity.Details.Contains("name=\"id\"", StringComparison.Ordinal));
        Assert.False((firstEntry.ResolvedTypeShape with { Hash = "stale" }).TryRestoreType(out _));

        var namerOnly = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_types_payload.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        }).Run();

        Assert.True(namerOnly.Success, FormatDiagnostics(namerOnly));
        Assert.NotNull(namerOnly.Ast);
        Assert.DoesNotContain(
            EnumerateAstNodes(namerOnly.Ast!),
            static node => node.InferredType != null);

        var restore = ModuleTypesStateRestorer.ApplyInferredTypes(namerOnly.Ast!, payload);

        Assert.True(restore.Applied, string.Join(Environment.NewLine, restore.Failures));
        Assert.Equal(payload.AstInferredTypes.Entries.Count, restore.RestoredInferredTypes);
        Assert.Equal(0, restore.RestoredTypeEnvBindings);
        Assert.Equal(0, restore.RestoredSubstitutionBindings);
        Assert.Equal(0, restore.MissingNodes);
        Assert.Equal(0, restore.StaleEntries);
        var restoredTypes = EnumerateAstNodes(namerOnly.Ast!)
            .Where(static node => node.InferredType != null)
            .Select(static node => node.InferredType!.ToString())
            .Order(StringComparer.Ordinal)
            .ToArray();
        var payloadTypes = payload.AstInferredTypes.Entries
            .Select(static entry => entry.ResolvedType)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(payloadTypes, restoredTypes);
        Assert.True(ModuleTypesStateRestorer.TryRestoreTypeEnv(payload, out var restoredEnv));
        Assert.Contains(restoredEnv, binding => binding.Value.ToString() == functionScheme.Scheme.Display);
        Assert.True(payload.TypeSubstitution.TryRestoreSubstitution(out var restoredSubstitution));
        Assert.Equal(payload.TypeSubstitution.Bindings.Count, restoredSubstitution.Count);
        Assert.Equal(FunctionTypeParametersPayload.CurrentSchemaVersion, payload.FunctionTypeParameters.SchemaVersion);
        Assert.True(payload.FunctionTypeParameters.HasValidHash());
        Assert.Contains(payload.FunctionTypeParameters.Bindings, binding => binding.TypeParameters.Count == 1);
        Assert.True(payload.FunctionTypeParameters.TryRestoreFunctionTypeParameters(out var restoredFunctionTypeParameters));
        Assert.Equal(payload.FunctionTypeParameters.Bindings.Count, restoredFunctionTypeParameters.Count);
        Assert.Equal(ComptimeValuesPayload.CurrentSchemaVersion, payload.ComptimeValues.SchemaVersion);
        Assert.True(payload.ComptimeValues.HasValidHash());
        Assert.Equal(0, payload.ComptimeValues.UnsupportedValues);
        Assert.Equal(2, payload.ComptimeValues.Bindings.Count);
        Assert.Contains(
            payload.ComptimeValues.Bindings,
            binding => binding.Value.Kind == ComptimeValuePayload.ScalarKindName);
        Assert.Contains(
            payload.ComptimeValues.Bindings,
            binding => binding.Value.Kind == ComptimeValuePayload.SequenceKindName &&
                       binding.Value.SequenceKind == "List" &&
                       binding.Value.Elements is { Count: 3 });
        Assert.True(payload.ComptimeValues.TryRestoreComptimeValues(out var restoredComptimeValues));
        Assert.Contains(restoredComptimeValues.Values, value => value.Value is long and 42);
        Assert.Contains(restoredComptimeValues.Values, value => value.Value is ComptimeSequence { Kind: ComptimeSequenceKind.List, Elements.Length: 3 });
        Assert.Equal(TypeConstraintsPayload.CurrentSchemaVersion, payload.Constraints.SchemaVersion);
        Assert.True(payload.Constraints.HasValidHash());
        Assert.Contains(
            payload.Constraints.Constraints,
            constraint => constraint.Kind == nameof(TraitConstraint) &&
                          constraint.TraitName == "Copy" &&
                          constraint.Type?.Kind == nameof(TyVar));
        Assert.True(payload.Constraints.TryRestoreConstraints(out var restoredConstraints));
        Assert.Contains(restoredConstraints.OfType<TraitConstraint>(), constraint => constraint.TraitName == "Copy");

        var fullRestore = ModuleTypesStateRestorer.RestoreState(
            namerOnly.Ast!,
            payload,
            out var fullTypeEnv,
            out var fullSubstitution,
            out var fullFunctionTypeParameters,
            out var fullComptimeValues,
            out var fullConstraints);
        Assert.True(fullRestore.Applied, string.Join(Environment.NewLine, fullRestore.Failures));
        Assert.Equal(payload.AstInferredTypes.Entries.Count, fullRestore.RestoredInferredTypes);
        Assert.Equal(fullTypeEnv.Count, fullRestore.RestoredTypeEnvBindings);
        Assert.Equal(fullSubstitution.Count, fullRestore.RestoredSubstitutionBindings);
        Assert.Equal(fullFunctionTypeParameters.Count, fullRestore.RestoredFunctionTypeParameterBindings);
        Assert.Equal(fullComptimeValues.Count, fullRestore.RestoredComptimeValues);
        Assert.Equal(fullConstraints.Count, fullRestore.RestoredConstraints);

        var restoredInferer = new TypeInferer(Assert.IsType<SymbolTable>(namerOnly.SymbolTable));
        restoredInferer.RestoreTypesState(
            fullTypeEnv,
            fullSubstitution,
            fullFunctionTypeParameters,
            fullComptimeValues,
            fullConstraints);
        Assert.Equal(fullComptimeValues.Count, restoredInferer.ComptimeValues.Count);
        Assert.Contains(restoredInferer.ComptimeValues.Values, value => value.Value is long and 42);
        Assert.Equal(fullConstraints.Count, restoredInferer.ConstraintGenerator.Constraints.Count);
        var copyConstraint = Assert.Single(
            restoredInferer.ConstraintGenerator.Constraints.Constraints.OfType<TraitConstraint>(),
            constraint => constraint.TraitName == "Copy");
        Assert.NotEmpty(restoredInferer.ConstraintGenerator.Constraints.GetTraitConstraintsForVar(
            ((TyVar)copyConstraint.Type).Index));
    }

    [Fact]
    public void Run_WithPreviousTypesPayloads_RestoresTypesAtStageEntryForTypedTarget()
    {
        const string source = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }

    keep[T] :: T -> T
    {
        value => value
    }

    Copy :: trait {
        copy :: Self -> Self
    }

    needsCopy[T: Copy] :: T -> T
    {
        value => value
    }
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_types_restore.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        }).Run();

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.NotNull(first.ModuleSemanticSignatureSnapshot);
        Assert.NotNull(first.ModuleTypedSemanticSnapshot);
        Assert.NotNull(first.ModuleMemberIndexSnapshot);
        Assert.NotNull(first.ModuleDependencySignatureSnapshot);
        Assert.NotNull(first.ModuleTypesStatePayloads);
        var payloads = first.ModuleTypesStatePayloads!;
        var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_types_restore.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false,
            PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot,
            PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot,
            PreviousModuleMemberIndexSnapshot = first.ModuleMemberIndexSnapshot,
            PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot,
            PreviousModuleTypesStatePayloads = payloads,
            ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
            {
                ProjectModuleArtifactKinds.SemanticSignature => true,
                ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                ProjectModuleArtifactKinds.TypesStatePayload => payloadByModule.ContainsKey(moduleKey),
                _ => false
            },
            ModuleTypesStatePayloadLoader = (moduleKey, kind, _, _) =>
                kind == ProjectModuleArtifactKinds.TypesStatePayload &&
                payloadByModule.TryGetValue(moduleKey, out var payload)
                    ? payload
                    : null
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.True(second.ProfilingCounters.TryGetValue("Types.moduleRestore.applied", out var applied), FormatCounters(second));
        Assert.Equal(1, applied);
        Assert.Equal(1, second.ProfilingCounters["Build.moduleStage.Types.realTaskExecution"]);
        Assert.Equal(1, second.ProfilingCounters["Build.moduleStage.Types.restoredModules"]);
        Assert.Equal(0, second.ProfilingCounters["Build.moduleStage.Types.compiledModules"]);
        Assert.Equal(0, second.ProfilingCounters["Build.moduleStage.Types.blockedModules"]);
        var restoredTypeParameterBindings = payloads.Sum(static payload => payload.FunctionTypeParameters.Bindings.Count);
        Assert.Equal(restoredTypeParameterBindings, second.ProfilingCounters["Types.moduleRestore.restoredFunctionTypeParameterBindings"]);
        Assert.True(second.ProfilingCounters["Types.moduleRestore.restoredConstraints"] > 0, FormatCounters(second));
        Assert.Equal(restoredTypeParameterBindings, second.ProfilingCounters["Types.functionTypeParameters.count"]);
        Assert.True(second.ProfilingCounters["Types.constraints.count"] > 0, FormatCounters(second));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Types.step.infer_module_declarations.calls"));
    }

    [Fact]
    public void Run_WithIncrementalCompilation_EmitsModuleHirStatePayloadsAfterHir()
    {
        var result = new CompilationPipeline("""
Main :: module {
    Box :: type { Box(Int) }

    id :: Int -> Int
    {
        value => value
    }
}
""", new CompilationOptions
        {
            InputFile = "module_hir_payload.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Hir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
        var typed = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(result.ModuleTypedSemanticSnapshot);
        var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleHirStateArtifactPayload>>(result.ModuleHirStatePayloads);
        Assert.Equal(typed.Nodes.Count, payloads.Count);

        var typedNode = Assert.Single(typed.Nodes);
        var payload = Assert.Single(payloads, candidate => candidate.ModuleKey == typedNode.ModuleKey);
        Assert.Equal(ModuleHirStateArtifactPayload.CurrentSchemaVersion, payload.SchemaVersion);
        Assert.Equal(typedNode.TypedSemanticHash, payload.TypedSemantic.TypedSemanticHash);
        Assert.True(payload.IsModuleLocal);
        Assert.True(payload.ModuleLocalDeclarationCount > 0);
        Assert.True(payload.HasValidPayloadHash());
        Assert.True(payload.HirState.IsRestorable, string.Join(Environment.NewLine, payload.HirState.UnsupportedNodeKinds));
        Assert.True(payload.HirState.TryRestore(out var restored, out var attachedState));
        Assert.NotEmpty(restored.Declarations);
        Assert.True(payload.HirState.AttachedState.HasValidHash());
        Assert.NotNull(attachedState.ParameterEffects);
    }

    [Fact]
    public void Run_WithIncrementalCompilation_EmitsDistinctModuleLocalHirPayloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_hir_payloads_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var entryFile = Path.Combine(tempDir, "Main.eidos");
            var libFile = Path.Combine(tempDir, "Utils.eidos");
            File.WriteAllText(entryFile, """
Main :: module {
    import Utils

    main :: Int -> Int
    {
        value => helper(value)
    }
}
""");
            File.WriteAllText(libFile, """
Utils :: module {
    export helper :: Int -> Int
    {
        value => value
    }
}
""");

            var result = new CompilationPipeline(File.ReadAllText(entryFile), new CompilationOptions
            {
                InputFile = entryFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Hir,
                NoImplicitPrelude = true,
                EnableDetailedProfiling = true,
                EnableIncrementalCompilation = true,
                UseColors = false
            }).Run();

            Assert.True(result.Success, FormatDiagnostics(result));
            var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleHirStateArtifactPayload>>(result.ModuleHirStatePayloads);
            Assert.Contains(payloads, static payload => payload.ModuleKey == "Main");
            Assert.Contains(payloads, static payload => payload.ModuleKey == "Utils");
            Assert.All(payloads, payload =>
            {
                Assert.True(payload.IsModuleLocal, payload.ModuleKey);
                Assert.True(payload.ModuleLocalDeclarationCount > 0, payload.ModuleKey);
                Assert.True(payload.HasValidPayloadHash(), payload.ModuleKey);
            });

            var declarationNamesByModule = payloads.ToDictionary(
                static payload => payload.ModuleKey,
                payload =>
                {
                    Assert.True(payload.HirState.TryRestore(out var module, out _));
                    return module.Declarations.Select(static declaration => declaration.Name).Order(StringComparer.Ordinal).ToArray();
                },
                StringComparer.Ordinal);

            Assert.Contains("Utils__helper", declarationNamesByModule["Utils"]);
            Assert.Contains("Main__main", declarationNamesByModule["Main"]);
            Assert.DoesNotContain("Main__main", declarationNamesByModule["Utils"]);
            Assert.DoesNotContain("Utils__helper", declarationNamesByModule["Main"]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Run_WithPreviousHirPayloads_RestoresHirAtStageEntryForHirTarget()
    {
        const string source = """
Main :: module {
    Box :: type { Box(Int) }

    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_hir_restore.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Hir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        }).Run();

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.NotNull(first.ModuleSemanticSignatureSnapshot);
        Assert.NotNull(first.ModuleTypedSemanticSnapshot);
        Assert.NotNull(first.ModuleDependencySignatureSnapshot);
        Assert.NotNull(first.ModuleHirStatePayloads);
        var payloads = first.ModuleHirStatePayloads!;
        var payloadByModule = payloads.ToDictionary(static payload => payload.ModuleKey, StringComparer.Ordinal);
        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_hir_restore.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Hir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false,
            PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot,
            PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot,
            PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot,
            PreviousModuleHirStatePayloads = payloads,
            ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
            {
                ProjectModuleArtifactKinds.SemanticSignature => true,
                ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                ProjectModuleArtifactKinds.HirStatePayload => payloadByModule.ContainsKey(moduleKey),
                _ => false
            },
            ModuleHirStatePayloadLoader = (moduleKey, kind, _, _) =>
                kind == ProjectModuleArtifactKinds.HirStatePayload &&
                payloadByModule.TryGetValue(moduleKey, out var payload)
                    ? payload
                    : null
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.True(second.ProfilingCounters.TryGetValue("Hir.moduleRestore.applied", out var applied), FormatCounters(second));
        Assert.Equal(1, applied);
        Assert.Equal(1, second.ProfilingCounters["Build.moduleStage.Hir.realTaskExecution"]);
        Assert.Equal(1, second.ProfilingCounters["Build.moduleStage.Hir.restoredModules"]);
        Assert.Equal(0, second.ProfilingCounters["Build.moduleStage.Hir.compiledModules"]);
        Assert.Equal(0, second.ProfilingCounters["Build.moduleStage.Hir.blockedModules"]);
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Hir.build_hir.calls"));
        Assert.NotNull(second.HirModule);
        Assert.Equal(payloads.Sum(static payload => payload.ModuleLocalDeclarationCount), second.HirModule!.Declarations.Count);
    }

    [Fact]
    public void Run_WithHirPayloadLoader_RestoresHirWithoutLatestPayloadList()
    {
        const string source = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_hir_loader_restore.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Hir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        }).Run();

        Assert.True(first.Success, FormatDiagnostics(first));
        var payload = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ModuleHirStateArtifactPayload>>(first.ModuleHirStatePayloads));
        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_hir_loader_restore.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Hir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false,
            PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot,
            PreviousModuleTypedSemanticSnapshot = first.ModuleTypedSemanticSnapshot,
            PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot,
            ModuleArtifactAvailability = (moduleKey, kind, _, _) => kind switch
            {
                ProjectModuleArtifactKinds.SemanticSignature => true,
                ProjectModuleArtifactKinds.TypedSemanticSignature => true,
                ProjectModuleArtifactKinds.HirStatePayload => moduleKey == payload.ModuleKey,
                _ => false
            },
            ModuleHirStatePayloadLoader = (moduleKey, kind, sourceHash, dependencyHash) =>
                kind == ProjectModuleArtifactKinds.HirStatePayload &&
                moduleKey == payload.ModuleKey &&
                sourceHash == payload.TypedSemantic.LocalSurfaceHash &&
                dependencyHash == payload.TypedSemantic.DependencyTypedSemanticHash
                    ? payload
                    : null
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.Equal(1, second.ProfilingCounters["Hir.moduleRestore.applied"]);
        Assert.Equal(1, second.ProfilingCounters["Build.moduleStage.Hir.realTaskExecution"]);
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Hir.build_hir.calls"));
    }

    private static IEnumerable<EidosAstNode> EnumerateAstNodes(EidosAstNode node)
    {
        yield return node;
        foreach (var child in AstStableNodeTraversal.GetStructuralChildren(node))
        {
            foreach (var descendant in EnumerateAstNodes(child))
            {
                yield return descendant;
            }
        }
    }

    [Fact]
    public void Run_StoppedBeforeTypes_DoesNotEmitTypedSemanticSnapshot()
    {
        var result = new CompilationPipeline(Source, new CompilationOptions
        {
            InputFile = "typed_semantic_before_types.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Null(result.ModuleTypedSemanticSnapshot);
        Assert.Null(result.ModuleTypesStatePayloads);
        Assert.DoesNotContain("Build.moduleTypedSemanticSignatures.modules", result.ProfilingCounters.Keys);
    }


    private static CompilationResult Run(bool enableDetailedProfiling)
    {
        return new CompilationPipeline(Source, new CompilationOptions
        {
            InputFile = "profiling_gate.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = enableDetailedProfiling,
            UseColors = false
        }).Run();
    }

    private static CompilationResult RunLlvmSource(
        string source,
        LlvmFunctionFragmentSnapshot? previousFragments)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "llvm_fragment_restore_emit.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousLlvmFunctionFragmentSnapshot = previousFragments
        }).Run();
    }

    private static CompilationResult RunLiveStateSource()
    {
        return new CompilationPipeline(Source, new CompilationOptions
        {
            InputFile = "live_state_cache_profile.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Mir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableLiveStateCache = true,
            UseColors = false
        }).Run();
    }

    private static CompilationResult RunMirPayloadSource(string source)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "live_state_payload_profile.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Mir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();
    }

    private static bool IsFunctionFingerprintCounter(string key)
    {
        return key.Contains(".function_fingerprints", StringComparison.Ordinal) ||
               key.Contains(".unique_function_fingerprints", StringComparison.Ordinal) ||
               key.Contains(".duplicate_function_fingerprint_groups", StringComparison.Ordinal) ||
               key.Contains(".max_functions_per_fingerprint", StringComparison.Ordinal) ||
               key.Contains(".module_fingerprint", StringComparison.Ordinal) ||
               key.Contains(".fingerprint_", StringComparison.Ordinal);
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }

    private static string FormatCounters(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.ProfilingCounters
                .OrderBy(static counter => counter.Key, StringComparer.Ordinal)
                .Select(static counter => $"{counter.Key}={counter.Value}"));
    }
}
