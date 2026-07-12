using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Eidosc.Cli.Resources;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using System.Diagnostics;
using System.Text.Json;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    private const string LatestBackendArtifactRestoreInputSnapshotArtifactKind = "backend-artifact-restore-input-latest";
    private const string BackendArtifactRestoreInputSchemaVersion = "backend-artifact-restore-input-v1";

    private sealed record BackendArtifactRestoreInputSnapshot(
        string SchemaVersion,
        string ModuleKey,
        string SourceHash,
        string FlagsHash,
        string TargetTriple,
        string ArtifactKind);

    private static bool TryRestoreNativeFromBackendArtifacts(
        FullBuildArtifact artifact,
        BuildOptions options,
        CompilationOptions compileOptions,
        TargetInfo targetInfo,
        int optimizationLevel,
        CodeGenProfile? profile,
        out bool restored)
    {
        restored = false;
        if (IsObjectGroupsCodegenMode(options.CodegenMode))
        {
            restored = TryRestoreNativeObjectGroupsFromBackendArtifactsWithProfile(
                artifact,
                compileOptions,
                targetInfo,
                optimizationLevel,
                options.Lto,
                Math.Max(0, options.MaxObjectGroups),
                options.NoColor,
                options.ProfileJson,
                profile);
            return restored;
        }

        restored = TryRestoreNativeFullModuleFromBackendArtifactsWithProfile(
            artifact,
            compileOptions,
            targetInfo,
            optimizationLevel,
            options.Lto,
            options.NoColor,
            options.ProfileJson,
            profile);
        return restored;
    }

    internal static void StoreLatestBackendArtifactRestoreInputSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null ||
            result.LlvmFunctionFragments == null ||
            result.LlvmModuleEnvelope == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestBackendArtifactRestoreInputKey(artifact),
            LatestBackendArtifactRestoreInputSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(
                new BackendArtifactRestoreInputSnapshot(
                    BackendArtifactRestoreInputSchemaVersion,
                    artifact.Key.ModuleKey,
                    artifact.Key.SourceHash,
                    artifact.OutputIndependentPayloadKey.FlagsHash,
                    artifact.Key.TargetTriple,
                    artifact.Kind),
                new JsonSerializerOptions { WriteIndented = false }));
    }

    private static bool TryRestoreLlvmIrFromBackendArtifacts(
        FullBuildArtifact artifact,
        bool noColor,
        string? profileJson) =>
        TryRestoreLlvmIrFromBackendArtifactsWithProfile(artifact, noColor, profileJson, new CodeGenProfile());

    private static bool TryRestoreLlvmIrFromBackendArtifactsWithProfile(
        FullBuildArtifact artifact,
        bool noColor,
        string? profileJson,
        CodeGenProfile? profile)
    {
        var stopwatch = Stopwatch.StartNew();
        var codeGenProfile = profile ?? new CodeGenProfile();
        var restored = false;
        try
        {
            if (!IsLatestBackendArtifactRestoreInputCurrent(artifact))
            {
                return false;
            }

            var envelope = TryLoadLatestLlvmModuleEnvelopeSnapshot(artifact);
            var fragments = TryLoadLatestLlvmFunctionFragmentSnapshot(artifact);
            if (envelope == null || fragments == null)
            {
                return false;
            }

            var recomposed = LlvmFunctionFingerprintBuilder.RecomposeModule(envelope, fragments);
            Directory.CreateDirectory(Path.GetDirectoryName(artifact.OutputPath) ?? Directory.GetCurrentDirectory());
            File.WriteAllText(artifact.OutputPath, recomposed.IrText);
            artifact.Cache.StoreArtifact(
                artifact.Key,
                artifact.Kind,
                ".ll",
                recomposed.IrText);
            artifact.Cache.StoreArtifact(
                artifact.OutputIndependentPayloadKey,
                artifact.Kind,
                ".ll",
                recomposed.IrText);

            restored = true;
            CliOutput.WriteArtifact(CliMessages.ArtifactKindLlvmIr, artifact.OutputPath, !noColor);
            CliOutput.WriteStatus(DiagnosticLevel.Help, CliMessages.LlvmIrWritten(artifact.OutputPath), !noColor);
            return true;
        }
        finally
        {
            stopwatch.Stop();
            codeGenProfile.Record(
                "artifact_cache",
                "restore_llvm_ir_from_backend_fragments",
                tool: null,
                stopwatch.Elapsed,
                success: restored,
                cacheHit: restored);
            if (restored)
            {
                var result = CreateBackendArtifactCacheHitResult(
                    artifact,
                    CompilationPhase.Llvm,
                    stopwatch.Elapsed,
                    "Build.artifactCache.llvmIrBackendFragments.hits");
                WriteBuildProfileJsonAsync(profileJson, result, codeGenProfile).GetAwaiter().GetResult();
            }
        }
    }

    private static bool TryRestoreNativeObjectGroupsFromBackendArtifactsWithProfile(
        FullBuildArtifact artifact,
        CompilationOptions compileOptions,
        TargetInfo targetInfo,
        int optimizationLevel,
        bool enableLto,
        int maxObjectGroups,
        bool noColor,
        string? profileJson,
        CodeGenProfile? profile = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var codeGenProfile = profile ?? new CodeGenProfile();
        var restored = false;
        try
        {
            if (!IsLatestBackendArtifactRestoreInputCurrent(artifact))
            {
                return false;
            }

            var envelope = TryLoadLatestLlvmModuleEnvelopeSnapshot(artifact);
            var fragments = TryLoadLatestLlvmFunctionFragmentSnapshot(artifact);
            var plan = TryLoadLatestLlvmCodegenUnitPlanSnapshot(artifact);
            if (envelope == null || fragments == null || plan == null)
            {
                return false;
            }

            var restorePlan = LlvmObjectGroupRestorePlanSnapshot.Create(
                plan.ObjectGroups,
                LlvmFunctionFragmentRestorePlanSnapshot.Create(fragments, fragments));
            var llvmCompiler = new LlvmCompiler(
                targetInfo,
                optimizationLevel: optimizationLevel,
                enableLto: enableLto,
                linkMode: compileOptions.NativeLinkMode,
                profile: codeGenProfile,
                maxDegreeOfParallelism: compileOptions.MaxDegreeOfParallelism);
            Directory.CreateDirectory(Path.GetDirectoryName(artifact.OutputPath) ?? Directory.GetCurrentDirectory());
            var codeGenResult = llvmCompiler.CompileRestoredFragmentsToExecutableWithObjectGroups(
                envelope,
                fragments,
                plan,
                artifact.OutputPath,
                maxObjectGroups,
                restorePlan);
            if (!codeGenResult.Success)
            {
                return false;
            }

            artifact.Cache.StoreArtifactFile(
                artifact.Key,
                artifact.Kind,
                targetInfo.ExecutableExtension,
                artifact.OutputPath);
            artifact.Cache.StoreArtifactFile(
                artifact.OutputIndependentPayloadKey,
                artifact.Kind,
                targetInfo.ExecutableExtension,
                artifact.OutputPath);
            restored = true;
            CliOutput.WriteArtifact(CliMessages.ArtifactKindExecutable, artifact.OutputPath, !noColor);
            CliOutput.WriteStatus(DiagnosticLevel.Help, CliMessages.ExecutableGenerated(artifact.OutputPath), !noColor);
            return true;
        }
        finally
        {
            stopwatch.Stop();
            codeGenProfile.Record(
                "artifact_cache",
                "restore_native_from_backend_object_groups",
                tool: null,
                stopwatch.Elapsed,
                success: restored,
                cacheHit: restored);
            if (restored)
            {
                var result = CreateBackendArtifactCacheHitResult(
                    artifact,
                    CompilationPhase.Llvm,
                    stopwatch.Elapsed,
                    "Build.artifactCache.nativeBackendObjectGroups.hits");
                WriteBuildProfileJsonAsync(profileJson, result, codeGenProfile).GetAwaiter().GetResult();
            }
        }
    }

    private static bool TryRestoreNativeFullModuleFromBackendArtifacts(
        FullBuildArtifact artifact,
        CompilationOptions compileOptions,
        TargetInfo targetInfo,
        int optimizationLevel,
        bool enableLto,
        bool noColor,
        string? profileJson) =>
        TryRestoreNativeFullModuleFromBackendArtifactsWithProfile(
            artifact,
            compileOptions,
            targetInfo,
            optimizationLevel,
            enableLto,
            noColor,
            profileJson,
            profile: null);

    private static bool TryRestoreNativeFullModuleFromBackendArtifactsWithProfile(
        FullBuildArtifact artifact,
        CompilationOptions compileOptions,
        TargetInfo targetInfo,
        int optimizationLevel,
        bool enableLto,
        bool noColor,
        string? profileJson,
        CodeGenProfile? profile)
    {
        var stopwatch = Stopwatch.StartNew();
        var codeGenProfile = profile ?? new CodeGenProfile();
        var restored = false;
        try
        {
            if (!IsLatestBackendArtifactRestoreInputCurrent(artifact))
            {
                return false;
            }

            var envelope = TryLoadLatestLlvmModuleEnvelopeSnapshot(artifact);
            var fragments = TryLoadLatestLlvmFunctionFragmentSnapshot(artifact);
            if (envelope == null || fragments == null)
            {
                return false;
            }

            var llvmCompiler = new LlvmCompiler(
                targetInfo,
                optimizationLevel: optimizationLevel,
                enableLto: enableLto,
                linkMode: compileOptions.NativeLinkMode,
                profile: codeGenProfile,
                maxDegreeOfParallelism: compileOptions.MaxDegreeOfParallelism);
            Directory.CreateDirectory(Path.GetDirectoryName(artifact.OutputPath) ?? Directory.GetCurrentDirectory());
            var codeGenResult = llvmCompiler.CompileRestoredFragmentsToExecutable(
                envelope,
                fragments,
                artifact.OutputPath);
            if (!codeGenResult.Success)
            {
                return false;
            }

            artifact.Cache.StoreArtifactFile(
                artifact.Key,
                artifact.Kind,
                targetInfo.ExecutableExtension,
                artifact.OutputPath);
            artifact.Cache.StoreArtifactFile(
                artifact.OutputIndependentPayloadKey,
                artifact.Kind,
                targetInfo.ExecutableExtension,
                artifact.OutputPath);
            restored = true;
            CliOutput.WriteArtifact(CliMessages.ArtifactKindExecutable, artifact.OutputPath, !noColor);
            CliOutput.WriteStatus(DiagnosticLevel.Help, CliMessages.ExecutableGenerated(artifact.OutputPath), !noColor);
            return true;
        }
        finally
        {
            stopwatch.Stop();
            codeGenProfile.Record(
                "artifact_cache",
                "restore_native_from_backend_full_module",
                tool: null,
                stopwatch.Elapsed,
                success: restored,
                cacheHit: restored);
            if (restored)
            {
                var result = CreateBackendArtifactCacheHitResult(
                    artifact,
                    CompilationPhase.Llvm,
                    stopwatch.Elapsed,
                    "Build.artifactCache.nativeBackendFullModule.hits");
                WriteBuildProfileJsonAsync(profileJson, result, codeGenProfile).GetAwaiter().GetResult();
            }
        }
    }

    private static CompilationResult CreateBackendArtifactCacheHitResult(
        FullBuildArtifact artifact,
        CompilationPhase completedPhase,
        TimeSpan elapsed,
        string counterName)
    {
        var baseResult = CreateFullBuildArtifactCacheHitResult(
            artifact,
            elapsed,
            outputIndependentHit: true);
        var counters = new Dictionary<string, long>(baseResult.ProfilingCounters, StringComparer.Ordinal)
        {
            [counterName] = 1,
            ["Build.artifactCache.backendArtifactRestore.hits"] = 1
        };
        counters.Remove("Build.artifactCache.nativeFullBuild.hits");
        counters.Remove("Build.artifactCache.nativeFullBuild.outputIndependentHits");
        counters.Remove("Build.artifactCache.llvmIrFullBuild.hits");
        counters.Remove("Build.artifactCache.llvmIrFullBuild.outputIndependentHits");

        return new CompilationResult
        {
            Success = true,
            CompletedPhase = completedPhase,
            InputFile = artifact.OutputPath,
            ModuleSemanticSignatureSnapshot = baseResult.ModuleSemanticSignatureSnapshot,
            ModuleTypedSemanticSnapshot = baseResult.ModuleTypedSemanticSnapshot,
            ModuleDependencySignatureSnapshot = baseResult.ModuleDependencySignatureSnapshot,
            ModuleMemberIndexSnapshot = baseResult.ModuleMemberIndexSnapshot,
            ModuleMemberIndexRestorePlan = baseResult.ModuleMemberIndexRestorePlan,
            ModuleMemberIndexRestorePayload = baseResult.ModuleMemberIndexRestorePayload,
            ImplOverlapCheckSnapshot = baseResult.ImplOverlapCheckSnapshot,
            ModuleMirArtifactSnapshot = baseResult.ModuleMirArtifactSnapshot,
            ModuleArtifactRestorePlan = baseResult.ModuleArtifactRestorePlan,
            ModuleArtifactRestoreExecution = baseResult.ModuleArtifactRestoreExecution,
            ModuleTypedArtifactRestorePlan = baseResult.ModuleTypedArtifactRestorePlan,
            ModuleTypedArtifactRestoreExecution = baseResult.ModuleTypedArtifactRestoreExecution,
            AssociatedTypeProjectionSnapshot = baseResult.AssociatedTypeProjectionSnapshot,
            AssociatedConstProjectionSnapshot = baseResult.AssociatedConstProjectionSnapshot,
            TraitCheckSnapshot = baseResult.TraitCheckSnapshot,
            SendAnalysisSnapshot = baseResult.SendAnalysisSnapshot,
            BorrowDiagnosticSnapshot = baseResult.BorrowDiagnosticSnapshot,
            BorrowCodegenHintsSnapshot = baseResult.BorrowCodegenHintsSnapshot,
            TotalTime = elapsed,
            PhaseTimes = new Dictionary<CompilationPhase, TimeSpan>
            {
                [completedPhase] = elapsed
            },
            ProfilingCounters = counters
        };
    }

    private static bool IsLatestBackendArtifactRestoreInputCurrent(FullBuildArtifact artifact)
    {
        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestBackendArtifactRestoreInputKey(artifact),
                    LatestBackendArtifactRestoreInputSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return false;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<BackendArtifactRestoreInputSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot != null &&
                   string.Equals(snapshot.SchemaVersion, BackendArtifactRestoreInputSchemaVersion, StringComparison.Ordinal) &&
                   string.Equals(snapshot.ModuleKey, artifact.Key.ModuleKey, StringComparison.Ordinal) &&
                   string.Equals(snapshot.SourceHash, artifact.Key.SourceHash, StringComparison.Ordinal) &&
                   string.Equals(snapshot.FlagsHash, artifact.OutputIndependentPayloadKey.FlagsHash, StringComparison.Ordinal) &&
                   string.Equals(snapshot.TargetTriple, artifact.Key.TargetTriple, StringComparison.Ordinal) &&
                   string.Equals(snapshot.ArtifactKind, artifact.Kind, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static ModuleArtifactKey CreateLatestBackendArtifactRestoreInputKey(FullBuildArtifact artifact)
    {
        return artifact.LatestLlvmFunctionFragmentKey with
        {
            CacheSchema = "backend-artifact-restore-input-latest-v1"
        };
    }
}
