using Eidosc.Cli.Commands;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

public sealed class BuildCommandStatePayloadArtifactTests
{
    [Fact]
    public void StoreLatestModuleArtifactRestorePlanSnapshotArtifact_CanLoadSemanticAndTypedSnapshots()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_latest_module_restore_plan_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            var key = CreateArtifactKey();
            var latestMirArtifactKey = key with
            {
                CacheSchema = "module-mir-artifact-latest-v1",
                SourceHash = "latest",
                DependencySignatureHash = "latest"
            };
            var artifact = new BuildCommand.FullBuildArtifact(
                cache,
                key,
                key,
                key,
                key,
                latestMirArtifactKey,
                key,
                key,
                key,
                Path.Combine(tempDir, "main.ll"),
                "llvm-ir-full-build",
                CompileTarget.LlvmIr);
            var result = new CompilationResult
            {
                ModuleArtifactRestorePlan = new ProjectModuleArtifactRestorePlan(
                    ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
                    [
                        new ProjectModuleArtifactRestoreLayer(
                            0,
                            [
                                new ProjectModuleArtifactRestoreItem(
                                    "Main",
                                    ProjectModuleArtifactRestoreAction.Restore,
                                    SemanticReady: true,
                                    TypedSemanticReady: false,
                                    MirReady: false)
                            ],
                            RestoreCount: 1,
                            BlockedCount: 0,
                            ReadyArtifactCount: 0)
                    ],
                    TotalModules: 1,
                    RestoreModules: 1,
                    BlockedModules: 0,
                    ReadyArtifactModules: 0,
                    MaxRestoreParallelWidth: 1),
                ModuleTypedArtifactRestorePlan = new ProjectModuleArtifactRestorePlan(
                    ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
                    [
                        new ProjectModuleArtifactRestoreLayer(
                            0,
                            [
                                new ProjectModuleArtifactRestoreItem(
                                    "Main",
                                    ProjectModuleArtifactRestoreAction.Blocked,
                                    SemanticReady: true,
                                    TypedSemanticReady: true,
                                    MirReady: false)
                            ],
                            RestoreCount: 0,
                            BlockedCount: 1,
                            ReadyArtifactCount: 0)
                    ],
                    TotalModules: 1,
                    RestoreModules: 0,
                    BlockedModules: 1,
                    ReadyArtifactModules: 0,
                    MaxRestoreParallelWidth: 0)
            };

            BuildCommand.StoreLatestModuleArtifactRestorePlanSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleTypedArtifactRestorePlanSnapshotArtifact(artifact, result);
            var restoredSemantic = BuildCommand.TryLoadLatestModuleArtifactRestorePlanSnapshot(artifact);
            var restoredTyped = BuildCommand.TryLoadLatestModuleTypedArtifactRestorePlanSnapshot(artifact);

            Assert.NotNull(restoredSemantic);
            Assert.NotNull(restoredTyped);
            Assert.Equal(1, restoredSemantic!.RestoreModules);
            Assert.Equal(0, restoredSemantic.BlockedModules);
            Assert.Equal(ProjectModuleArtifactRestoreAction.Restore, Assert.Single(restoredSemantic.Layers[0].Modules).Action);
            Assert.Equal(0, restoredTyped!.RestoreModules);
            Assert.Equal(1, restoredTyped.BlockedModules);
            Assert.Equal(ProjectModuleArtifactRestoreAction.Blocked, Assert.Single(restoredTyped.Layers[0].Modules).Action);
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
    public void StoreLatestModuleNamerStatePayloadsArtifact_CanLoadPreviousPayloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_latest_module_namer_payload_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            var key = CreateArtifactKey();
            var artifact = new BuildCommand.FullBuildArtifact(
                cache,
                key,
                key,
                key,
                key,
                key,
                key,
                key,
                key,
                Path.Combine(tempDir, "main.resolved.json"),
                "resolved-analysis-full-build",
                CompileTarget.Resolved);
            var pipeline = new CompilationPipeline("""
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""", new CompilationOptions
            {
                InputFile = "main.eidos",
                AllowVirtualInputFile = true,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                EnableDetailedProfiling = true,
                EnableIncrementalCompilation = true,
                UseColors = false
            }).Run();

            Assert.True(pipeline.Success, string.Join(Environment.NewLine, pipeline.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.NotNull(pipeline.ModuleNamerStatePayloads);
            var result = new CompilationResult
            {
                ModuleSemanticSignatureSnapshot = pipeline.ModuleSemanticSignatureSnapshot,
                ModuleNamerStatePayloads = pipeline.ModuleNamerStatePayloads
            };

            BuildCommand.StoreLatestModuleNamerStatePayloadsArtifact(artifact, result);
            BuildCommand.StorePerModuleNamerStatePayloadArtifacts(artifact, result);
            var restored = BuildCommand.TryLoadLatestModuleNamerStatePayloads(artifact);

            Assert.NotNull(restored);
            Assert.Equal(pipeline.ModuleNamerStatePayloads!.Count, restored!.Count);
            Assert.All(restored, payload =>
            {
                Assert.Equal(ModuleNamerStatePayload.CurrentSchemaVersion, payload.SchemaVersion);
                Assert.True(payload.HasValidPayloadHash());
            });
            Assert.Contains(restored, static payload => payload.SymbolIdentities.Count > 0);
            var semanticKeys = pipeline.ModuleSemanticSignatureSnapshot!.Nodes
                .Select(static node => node.ModuleKey)
                .ToHashSet(StringComparer.Ordinal);
            var payload = Assert.Single(restored, payload => semanticKeys.Contains(payload.ModuleKey));
            var semantic = Assert.Single(
                pipeline.ModuleSemanticSignatureSnapshot.Nodes,
                node => node.ModuleKey == payload.ModuleKey);
            Assert.True(cache.TryGetArtifact(
                artifact.Key with
                {
                    CacheSchema = "module-artifact-readiness-v1",
                    ModuleKey = payload.ModuleKey,
                    SourceHash = semantic.ExportSurfaceHash,
                    DependencySignatureHash = semantic.DependencySemanticSignatureHash,
                    FlagsHash = artifact.OutputIndependentPayloadKey.FlagsHash
                },
                BuildCommand.ModuleNamerStatePayloadArtifactKind,
                out var perModuleManifest));
            Assert.Contains(ModuleNamerStatePayload.CurrentSchemaVersion, File.ReadAllText(perModuleManifest!.PayloadPath), StringComparison.Ordinal);
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
    public void StoreModuleTypesStatePayloadArtifacts_CanLoadLatestAndPerModulePayloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_types_payload_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            var key = CreateArtifactKey();
            var artifact = new BuildCommand.FullBuildArtifact(
                cache,
                key,
                key,
                key,
                key,
                key,
                key,
                key,
                key,
                Path.Combine(tempDir, "main.typed.json"),
                "typed-analysis-full-build",
                CompileTarget.Typed);
            var pipeline = RunPipeline(CompilationPhase.Types);

            Assert.True(pipeline.Success, string.Join(Environment.NewLine, pipeline.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.NotNull(pipeline.ModuleTypesStatePayloads);
            var result = new CompilationResult
            {
                ModuleTypesStatePayloads = pipeline.ModuleTypesStatePayloads
            };

            BuildCommand.StoreLatestModuleTypesStatePayloadsArtifact(artifact, result);
            BuildCommand.StorePerModuleTypesStatePayloadArtifacts(artifact, result);

            var payload = Assert.Single(pipeline.ModuleTypesStatePayloads!);
            var latest = BuildCommand.TryLoadLatestModuleTypesStatePayloads(artifact);
            var restored = BuildCommand.TryLoadModuleTypesStatePayloadArtifact(
                artifact,
                payload.ModuleKey,
                BuildCommand.ModuleTypesStatePayloadArtifactKind,
                payload.TypedSemantic.LocalSurfaceHash,
                payload.TypedSemantic.DependencyTypedSemanticHash);

            Assert.NotNull(latest);
            var latestPayload = Assert.Single(latest!);
            Assert.Equal(ModuleTypesStatePayload.CurrentSchemaVersion, latestPayload.SchemaVersion);
            Assert.Equal(payload.PayloadHash, latestPayload.PayloadHash);
            Assert.True(latestPayload.HasValidPayloadHash());
            Assert.NotNull(restored);
            Assert.Equal(ModuleTypesStatePayload.CurrentSchemaVersion, restored!.SchemaVersion);
            Assert.Equal(payload.PayloadHash, restored.PayloadHash);
            Assert.True(restored.HasValidPayloadHash());
            Assert.True(cache.TryGetArtifact(
                artifact.LatestTypedSemanticSignatureKey with
                {
                    CacheSchema = "module-types-state-payloads-latest-v1",
                },
                BuildCommand.LatestModuleTypesStatePayloadsArtifactKind,
                out var latestManifest));
            Assert.Contains(ModuleTypesStatePayload.CurrentSchemaVersion, File.ReadAllText(latestManifest!.PayloadPath), StringComparison.Ordinal);
            Assert.True(cache.TryGetArtifact(
                artifact.Key with
                {
                    CacheSchema = "module-artifact-readiness-v1",
                    ModuleKey = payload.ModuleKey,
                    SourceHash = payload.TypedSemantic.LocalSurfaceHash,
                    DependencySignatureHash = payload.TypedSemantic.DependencyTypedSemanticHash,
                    FlagsHash = artifact.OutputIndependentPayloadKey.FlagsHash
                },
                BuildCommand.ModuleTypesStatePayloadArtifactKind,
                out var manifest));
            Assert.Contains(ModuleTypesStatePayload.CurrentSchemaVersion, File.ReadAllText(manifest!.PayloadPath), StringComparison.Ordinal);
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
    public void StoreModuleHirStatePayloadArtifacts_CanLoadLatestAndPerModulePayloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_hir_payload_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            var key = CreateArtifactKey();
            var artifact = new BuildCommand.FullBuildArtifact(
                cache,
                key,
                key,
                key,
                key,
                key,
                key,
                key,
                key,
                Path.Combine(tempDir, "main.hir.json"),
                "hir-analysis-full-build",
                CompileTarget.Hir);
            var pipeline = RunPipeline(CompilationPhase.Hir);

            Assert.True(pipeline.Success, string.Join(Environment.NewLine, pipeline.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.NotNull(pipeline.ModuleHirStatePayloads);
            var result = new CompilationResult
            {
                ModuleHirStatePayloads = pipeline.ModuleHirStatePayloads
            };

            BuildCommand.StoreLatestModuleHirStatePayloadsArtifact(artifact, result);
            BuildCommand.StorePerModuleHirStatePayloadArtifacts(artifact, result);

            var payload = Assert.Single(pipeline.ModuleHirStatePayloads!);
            var latest = BuildCommand.TryLoadLatestModuleHirStatePayloads(artifact);
            var restored = BuildCommand.TryLoadModuleHirStatePayloadArtifact(
                artifact,
                payload.ModuleKey,
                BuildCommand.ModuleHirStatePayloadArtifactKind,
                payload.TypedSemantic.LocalSurfaceHash,
                payload.TypedSemantic.DependencyTypedSemanticHash);

            Assert.NotNull(latest);
            var latestPayload = Assert.Single(latest!);
            Assert.Equal(ModuleHirStateArtifactPayload.CurrentSchemaVersion, latestPayload.SchemaVersion);
            Assert.Equal(payload.PayloadHash, latestPayload.PayloadHash);
            Assert.True(latestPayload.HasValidPayloadHash());
            Assert.NotNull(restored);
            Assert.Equal(ModuleHirStateArtifactPayload.CurrentSchemaVersion, restored!.SchemaVersion);
            Assert.Equal(payload.PayloadHash, restored.PayloadHash);
            Assert.True(restored.HasValidPayloadHash());
            Assert.True(cache.TryGetArtifact(
                artifact.LatestTypedSemanticSignatureKey with
                {
                    CacheSchema = "module-hir-state-payloads-latest-v1",
                },
                BuildCommand.LatestModuleHirStatePayloadsArtifactKind,
                out var latestManifest));
            Assert.Contains(ModuleHirStateArtifactPayload.CurrentSchemaVersion, File.ReadAllText(latestManifest!.PayloadPath), StringComparison.Ordinal);
            Assert.True(cache.TryGetArtifact(
                artifact.Key with
                {
                    CacheSchema = "module-artifact-readiness-v1",
                    ModuleKey = payload.ModuleKey,
                    SourceHash = payload.TypedSemantic.LocalSurfaceHash,
                    DependencySignatureHash = payload.TypedSemantic.DependencyTypedSemanticHash,
                    FlagsHash = artifact.OutputIndependentPayloadKey.FlagsHash
                },
                BuildCommand.ModuleHirStatePayloadArtifactKind,
                out var manifest));
            Assert.Contains(ModuleHirStateArtifactPayload.CurrentSchemaVersion, File.ReadAllText(manifest!.PayloadPath), StringComparison.Ordinal);
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
    public void StoreModuleMirStatePayloadArtifacts_CanLoadLatestAndPerModulePayloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_mir_payload_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            var key = CreateArtifactKey();
            var artifact = new BuildCommand.FullBuildArtifact(
                cache,
                key,
                key,
                key,
                key,
                key,
                key,
                key,
                key,
                Path.Combine(tempDir, "main.mir.json"),
                "mir-analysis-full-build",
                CompileTarget.Mir);
            var pipeline = RunPipeline(CompilationPhase.Mir);

            Assert.True(pipeline.Success, string.Join(Environment.NewLine, pipeline.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.NotNull(pipeline.ModuleMirStatePayloads);
            var result = new CompilationResult
            {
                ModuleMirStatePayloads = pipeline.ModuleMirStatePayloads
            };

            BuildCommand.StoreLatestModuleMirStatePayloadsArtifact(artifact, result);
            BuildCommand.StorePerModuleMirStatePayloadArtifacts(artifact, result);

            var payload = Assert.Single(pipeline.ModuleMirStatePayloads!);
            var latest = BuildCommand.TryLoadLatestModuleMirStatePayloads(artifact);
            var restored = BuildCommand.TryLoadModuleMirStatePayloadArtifact(
                artifact,
                payload.ModuleKey,
                BuildCommand.ModuleMirStatePayloadArtifactKind,
                payload.TypedSemantic.LocalSurfaceHash,
                payload.TypedSemantic.DependencyTypedSemanticHash);

            Assert.NotNull(latest);
            var latestPayload = Assert.Single(latest!);
            Assert.Equal(ModuleMirStateArtifactPayload.CurrentSchemaVersion, latestPayload.SchemaVersion);
            Assert.Equal(payload.PayloadHash, latestPayload.PayloadHash);
            Assert.True(latestPayload.HasValidPayloadHash());
            Assert.NotNull(restored);
            Assert.Equal(ModuleMirStateArtifactPayload.CurrentSchemaVersion, restored!.SchemaVersion);
            Assert.Equal(payload.PayloadHash, restored.PayloadHash);
            Assert.True(restored.HasValidPayloadHash());
            Assert.True(restored.MirState.TryRestore(out var restoredModule), FormatMirPayload(restored));
            Assert.NotEmpty(restoredModule.Functions);
            Assert.True(cache.TryGetArtifact(
                artifact.LatestMirArtifactKey with
                {
                    CacheSchema = "module-mir-state-payloads-latest-v1",
                },
                BuildCommand.LatestModuleMirStatePayloadsArtifactKind,
                out var latestManifest));
            Assert.Contains(ModuleMirStateArtifactPayload.CurrentSchemaVersion, File.ReadAllText(latestManifest!.PayloadPath), StringComparison.Ordinal);
            Assert.True(cache.TryGetArtifact(
                artifact.Key with
                {
                    CacheSchema = "module-artifact-readiness-v1",
                    ModuleKey = payload.ModuleKey,
                    SourceHash = payload.TypedSemantic.LocalSurfaceHash,
                    DependencySignatureHash = payload.TypedSemantic.DependencyTypedSemanticHash,
                    FlagsHash = artifact.OutputIndependentPayloadKey.FlagsHash
                },
                BuildCommand.ModuleMirStatePayloadArtifactKind,
                out var manifest));
            Assert.Contains(ModuleMirStateArtifactPayload.CurrentSchemaVersion, File.ReadAllText(manifest!.PayloadPath), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static CompilationResult RunPipeline(CompilationPhase phase) =>
        new CompilationPipeline("""
Main :: module {
    Box :: type { Box(Int) }

    id :: Int -> Int
    {
        value => value
    }
}
""", new CompilationOptions
        {
            InputFile = "main.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = phase,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        }).Run();

    private static ModuleArtifactKey CreateArtifactKey() =>
        new()
        {
            CacheSchema = "full-build",
            ModuleKey = "module",
            SourceHash = "input",
            LanguageVersion = "syntax",
            DependencySignatureHash = "deps",
            TargetTriple = "triple",
            FlagsHash = "flags"
        };

    private static string FormatMirPayload(ModuleMirStateArtifactPayload payload) =>
        string.Join(
            Environment.NewLine,
            [
                $"module={payload.ModuleKey}",
                $"isModuleLocal={payload.IsModuleLocal}",
                $"moduleLocalFunctionCount={payload.ModuleLocalFunctionCount}",
                $"payloadHashValid={payload.HasValidPayloadHash()}",
                $"mirStateHashValid={payload.MirState.HasValidHash()}",
                $"mirStateRestorable={payload.MirState.IsRestorable}",
                $"unsupportedNodeCount={payload.MirState.UnsupportedNodeCount}",
                $"unsupportedNodeKinds={string.Join(",", payload.MirState.UnsupportedNodeKinds)}",
                $"moduleNull={payload.MirState.Module == null}",
                $"moduleFingerprint={payload.MirState.ModuleFingerprint}"
            ]);
}
