using Eidosc.Cli.Commands;
using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Types;
using System.Reflection;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

public sealed class BuildCommandTests
{
    [Fact]
    public void CreateFullBuildArtifactFlags_CanIgnoreOutputPathForLatestSemanticSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_build_flags_{Guid.NewGuid():N}");
        try
        {
            var (_, resolution, compileOptions) = CreateResolvedProject(tempDir);
            var first = BuildCommand.CreateFullBuildArtifactFlags(
                resolution,
                compileOptions,
                CreateBuildOptions(CompileTarget.LlvmIr),
                optimizationLevel: 2,
                targetInfo: null,
                outputPath: Path.Combine(tempDir, "a.ll"),
                includeOutputPath: false);
            var second = BuildCommand.CreateFullBuildArtifactFlags(
                resolution,
                compileOptions,
                CreateBuildOptions(CompileTarget.LlvmIr),
                optimizationLevel: 2,
                targetInfo: null,
                outputPath: Path.Combine(tempDir, "b.ll"),
                includeOutputPath: false);

            Assert.Equal(first, second);
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
    public void CreateFullBuildArtifactFlags_ChangesForSemanticRelevantFlags()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_build_flags_{Guid.NewGuid():N}");
        try
        {
            var (_, resolution, compileOptions) = CreateResolvedProject(tempDir);
            var baseline = BuildCommand.CreateFullBuildArtifactFlags(
                resolution,
                compileOptions,
                CreateBuildOptions(CompileTarget.LlvmIr),
                optimizationLevel: 2,
                targetInfo: null,
                outputPath: Path.Combine(tempDir, "main.ll"),
                includeOutputPath: false);
            var changedOptions = new CompilationOptions
            {
                InputFile = compileOptions.InputFile,
                LanguageVersion = compileOptions.LanguageVersion,
                Target = compileOptions.Target,
                StopAtPhase = compileOptions.StopAtPhase,
                EnableMirOptimizations = false,
                NativeLinkMode = compileOptions.NativeLinkMode,
                ConfigFfiLibraries = ["raylib"]
            };
            var changed = BuildCommand.CreateFullBuildArtifactFlags(
                resolution,
                changedOptions,
                CreateBuildOptions(CompileTarget.LlvmIr),
                optimizationLevel: 2,
                targetInfo: null,
                outputPath: Path.Combine(tempDir, "main.ll"),
                includeOutputPath: false);

            Assert.NotEqual(baseline, changed);
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
    public void CreateFullBuildArtifactFlags_ChangesForNativeCodegenMode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_build_codegen_mode_{Guid.NewGuid():N}");
        try
        {
            var (_, resolution, compileOptions) = CreateResolvedProject(tempDir);
            var fullModuleOptions = CreateBuildOptions(CompileTarget.Native);
            fullModuleOptions.CodegenMode = BuildCommand.NativeCodegenModes.FullModule;
            fullModuleOptions.MaxObjectGroups = 0;
            var objectGroupsOptions = CreateBuildOptions(CompileTarget.Native);
            objectGroupsOptions.CodegenMode = BuildCommand.NativeCodegenModes.ObjectGroups;
            objectGroupsOptions.MaxObjectGroups = 64;
            var fullModule = BuildCommand.CreateFullBuildArtifactFlags(
                resolution,
                compileOptions,
                fullModuleOptions,
                optimizationLevel: 2,
                targetInfo: null,
                outputPath: Path.Combine(tempDir, "main.exe"),
                includeOutputPath: false).ToArray();
            var objectGroups = BuildCommand.CreateFullBuildArtifactFlags(
                resolution,
                compileOptions,
                objectGroupsOptions,
                optimizationLevel: 2,
                targetInfo: null,
                outputPath: Path.Combine(tempDir, "main.exe"),
                includeOutputPath: false).ToArray();

            Assert.Contains("codegenMode=full-module", fullModule);
            Assert.Contains("maxObjectGroups=0", fullModule);
            Assert.Contains("codegenMode=object-groups", objectGroups);
            Assert.Contains("maxObjectGroups=64", objectGroups);
            Assert.NotEqual(fullModule, objectGroups);
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
    public void CreateFullBuildArtifactFlags_IncludesOutputPathWhenRequested()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_build_flags_{Guid.NewGuid():N}");
        try
        {
            var (_, resolution, compileOptions) = CreateResolvedProject(tempDir);
            var first = BuildCommand.CreateFullBuildArtifactFlags(
                resolution,
                compileOptions,
                CreateBuildOptions(CompileTarget.LlvmIr),
                optimizationLevel: 2,
                targetInfo: null,
                outputPath: Path.Combine(tempDir, "a.ll"));
            var second = BuildCommand.CreateFullBuildArtifactFlags(
                resolution,
                compileOptions,
                CreateBuildOptions(CompileTarget.LlvmIr),
                optimizationLevel: 2,
                targetInfo: null,
                outputPath: Path.Combine(tempDir, "b.ll"));

            Assert.NotEqual(first, second);
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
    public void FullBuildCacheDiagnosticResult_LoadsLatestAnalysisMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_full_build_cache_hit_metadata_{Guid.NewGuid():N}");
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
                ModuleMemberIndexSnapshot = new ProjectModuleMemberIndexSnapshot(
                    "module-member-index-snapshot-v1",
                    [
                        new ProjectModuleMemberIndexNode(
                            "Main",
                            "Main",
                            UsesExplicitExports: false,
                            LocalIndexHash: "local-main",
                            DependencyIndexHash: "deps-main",
                            MemberIndexHash: "index-main",
                            [new ProjectModuleMemberBinding("main", "Value", "Main::Function:main", IsPublic: true)],
                            [],
                            [new ProjectModuleMemberBinding("main", "Value", "Main::Function:main", IsPublic: true)])
                    ]),
                ModuleMemberIndexRestorePlan = new ProjectModuleMemberIndexRestorePlan(
                    "module-member-index-restore-plan-v1",
                    [
                        new ProjectModuleMemberIndexRestoreItem(
                            "Main",
                            ProjectModuleMemberIndexRestoreAction.Restore,
                            "local-main",
                            "local-main",
                            "deps-main",
                            "deps-main",
                            "index-main",
                            "index-main")
                    ],
                    TotalModules: 1,
                    RestoreModules: 1,
                    RebuildModules: 0,
                    AddedModules: 0,
                    RemovedModules: 0),
                ImplOverlapCheckSnapshot = new ImplOverlapCheckSnapshot(
                    ImplOverlapCheckSnapshot.CurrentSchemaVersion,
                    [
                        new ImplOverlapCheckSnapshotEntry(
                            "1|Box||",
                            "1",
                            "Box",
                            "",
                            "",
                            CandidateCount: 0,
                            CandidateSetFingerprint: "",
                            NonOverlappingCandidateCount: 0,
                            SpecializationAllowedCandidateCount: 0,
                            HasConflict: false,
                            ConflictingImplKey: null,
                            SpecializationRelation: null)
                    ]),
                TraitCheckSnapshot = new TraitCheckSnapshot(
                    TraitCheckSnapshot.CurrentSchemaVersion,
                    [new TraitCheckSnapshotEntry("Box", "Eq", "Eq", "", "", Success: true, ErrorMessage: null)]),
                AssociatedTypeProjectionSnapshot = new AssociatedTypeProjectionSnapshot(
                    AssociatedTypeProjectionSnapshot.CurrentSchemaVersion,
                    [
                        new AssociatedTypeProjectionSnapshotEntry(
                            "1",
                            "Iterator",
                            "Item",
                            "Int",
                            "Int",
                            AllowTypeConstructorReference: false,
                            "path(String)",
                            nameof(TyCon),
                            "String",
                            "",
                            "String")
                    ]),
                AssociatedConstProjectionSnapshot = new AssociatedConstProjectionSnapshot(
                    AssociatedConstProjectionSnapshot.CurrentSchemaVersion,
                    [
                        new AssociatedConstProjectionSnapshotEntry(
                            "1",
                            "Bounded",
                            "Min",
                            "Int",
                            "Int",
                            "path(Int)",
                            "literal(Integer:0)")
                    ]),
                SendAnalysisSnapshot = new SendAnalysisSnapshot(
                    SendAnalysisSnapshot.CurrentSchemaVersion,
                    "mir-module",
                    "send-deps",
                    [new SendAnalysisFunctionSnapshot("name:main", "body", [])]),
                BorrowDiagnosticSnapshot = new BorrowDiagnosticSnapshot(
                    BorrowDiagnosticSnapshot.CurrentSchemaVersion,
                    "mir-module",
                    "borrow-deps",
                    [new BorrowDiagnosticFunctionSnapshot("name:main", "body", 0, 0, 0, 0, 0, [])]),
                BorrowCodegenHintsSnapshot = new BorrowCodegenHintsSnapshot(
                    BorrowCodegenHintsSnapshot.CurrentSchemaVersion,
                    "mir-module",
                    "borrow-codegen-deps",
                    [
                        new BorrowCodegenHintsFunctionSnapshot(
                            "name:main",
                            "body",
                            "main",
                            1,
                            new PerceusHintsSnapshot(
                                [new BorrowInstructionSiteSnapshot(1, 0)],
                                []),
                            null,
                            null,
                            null)
                    ]),
                ModuleSemanticSignatureSnapshot = new ProjectModuleSemanticSignatureSnapshot(
                    ProjectModuleSemanticSignatureSnapshot.CurrentSchemaVersion,
                    [
                        new ProjectModuleSemanticSignatureNode(
                            "Main",
                            [],
                            [],
                            "surface-main",
                            "semantic-deps-main",
                            "semantic-main")
                    ]),
                ModuleTypedSemanticSnapshot = new ProjectModuleTypedSemanticSnapshot(
                    ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
                    [
                        new ProjectModuleTypedSemanticNode(
                            "Main",
                            [],
                            [],
                            "typed-surface-main",
                            "typed-deps-main",
                            "typed-main")
                    ]),
                ModuleMirArtifactSnapshot = new ProjectModuleMirArtifactSnapshot(
                    "module-mir-artifact-snapshot-v1",
                    [
                        new ProjectModuleMirArtifactNode(
                            "Main",
                            [],
                            "typed-main",
                            "mir-functions-main",
                            "mir-main")
                    ]),
                ModuleDependencySignatureSnapshot = new ProjectModuleDependencySignatureSnapshot(
                    ProjectModuleDependencySignatureSnapshot.CurrentSchemaVersion,
                    [
                        new ProjectModuleDependencySignatureNode(
                            "Main",
                            [],
                            "source-main",
                            "input-main",
                            "graph-deps-main",
                            "semantic-deps-main",
                            "typed-deps-main",
                            "member-deps-main",
                            "typed-main",
                            "",
                            "combined-main",
                            SourceAvailable: true,
                            SemanticAvailable: true,
                            TypedAvailable: true,
                            MemberIndexAvailable: true,
                            MirAvailable: true)
                    ]),
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
                                    ProjectModuleArtifactRestoreAction.Restore,
                                    SemanticReady: true,
                                    TypedSemanticReady: true,
                                    MirReady: true)
                            ],
                            RestoreCount: 1,
                            BlockedCount: 0,
                            ReadyArtifactCount: 0)
                    ],
                    TotalModules: 1,
                    RestoreModules: 1,
                    BlockedModules: 0,
                    ReadyArtifactModules: 0,
                    MaxRestoreParallelWidth: 1)
            };

            BuildCommand.StoreLatestModuleMemberIndexSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleMemberIndexRestorePlanSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestImplOverlapCheckSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestAssociatedTypeProjectionSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestAssociatedConstProjectionSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestTraitCheckSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestSendAnalysisSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestBorrowDiagnosticSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestBorrowCodegenHintsSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleSemanticSignatureSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleTypedSemanticSignatureSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleMirArtifactSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleDependencySignatureSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleArtifactRestorePlanSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleTypedArtifactRestorePlanSnapshotArtifact(artifact, result);

            var method = typeof(BuildCommand).GetMethod(
                "CreateFullBuildArtifactCacheDiagnosticResult",
                BindingFlags.Static | BindingFlags.NonPublic);
            var restored = Assert.IsType<CompilationResult>(method!.Invoke(
                null,
                [artifact, TimeSpan.FromMilliseconds(1), false]));

            Assert.NotNull(restored.ModuleMemberIndexSnapshot);
            Assert.NotNull(restored.ModuleMemberIndexRestorePlan);
            Assert.NotNull(restored.ModuleMemberIndexRestorePayload);
            Assert.NotNull(restored.ImplOverlapCheckSnapshot);
            Assert.NotNull(restored.AssociatedTypeProjectionSnapshot);
            Assert.NotNull(restored.AssociatedConstProjectionSnapshot);
            Assert.NotNull(restored.TraitCheckSnapshot);
            Assert.NotNull(restored.SendAnalysisSnapshot);
            Assert.NotNull(restored.BorrowDiagnosticSnapshot);
            Assert.NotNull(restored.BorrowCodegenHintsSnapshot);
            Assert.NotNull(restored.ModuleDependencySignatureSnapshot);
            Assert.NotNull(restored.ModuleArtifactRestorePayload);
            Assert.NotNull(restored.ModuleTypedArtifactRestorePayload);
            Assert.NotNull(restored.ModuleArtifactRestoreExecution);
            Assert.NotNull(restored.ModuleTypedArtifactRestoreExecution);
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleMemberIndex.modules"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleMemberIndexRestorePlan.restoreModules"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleMemberIndexRestorePayload.validatedModules"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.implOverlapChecks.entries"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.associatedTypeProjection.entries"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.associatedConstProjection.entries"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.traitCheck.entries"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.sendAnalysis.functions"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.borrowDiagnostics.functions"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.borrowCodegenHints.functions"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.borrowCodegenHints.perceusFunctions"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleDependencySignatures.modules"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleDependencySignatures.mirAvailableModules"));
            Assert.Equal(0, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleArtifactRestoreExecution.restoredModules"));
            Assert.Equal(0, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleArtifactRestoreExecution.blockedModules"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleArtifactRestoreExecution.compiledModules"));
            Assert.Equal(0, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleTypedArtifactRestoreExecution.restoredModules"));
            Assert.Equal(0, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleTypedArtifactRestoreExecution.blockedModules"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleTypedArtifactRestoreExecution.compiledModules"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleArtifactRestorePayload.missingModules"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleTypedArtifactRestorePayload.missingModules"));
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
    public void StoreModuleArtifactRestorePlanSnapshotArtifact_WritesRestorePlanPayload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_restore_plan_artifact_{Guid.NewGuid():N}");
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
                Path.Combine(tempDir, "main.ll"),
                "llvm-ir-full-build",
                CompileTarget.LlvmIr);
            var result = new CompilationResult
            {
                ModuleTypedArtifactRestorePlan = new ProjectModuleArtifactRestorePlan(
                    ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
                    [
                        new ProjectModuleArtifactRestoreLayer(
                            0,
                            [
                                new ProjectModuleArtifactRestoreItem(
                                    "Main",
                                    ProjectModuleArtifactRestoreAction.Restore,
                                    SemanticReady: true,
                                    TypedSemanticReady: true,
                                    MirReady: true)
                            ],
                            RestoreCount: 1,
                            BlockedCount: 0,
                            ReadyArtifactCount: 0)
                    ],
                    TotalModules: 1,
                    RestoreModules: 1,
                    BlockedModules: 0,
                    ReadyArtifactModules: 0,
                    MaxRestoreParallelWidth: 1)
            };

            BuildCommand.StoreModuleArtifactRestorePlanSnapshotArtifact(artifact, result);

            Assert.True(cache.TryGetArtifact(
                key,
                BuildCommand.ModuleArtifactRestorePlanSnapshotArtifactKind,
                out var manifest));
            Assert.NotNull(manifest);
            var json = File.ReadAllText(manifest!.PayloadPath);
            Assert.Contains("Main", json, StringComparison.Ordinal);
            Assert.Contains("Restore", json, StringComparison.Ordinal);
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
    public void FullBuildCacheDiagnosticResult_GatesModuleRestoreWithValidatedArtifacts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_full_build_cache_hit_module_restore_{Guid.NewGuid():N}");
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
                Path.Combine(tempDir, "main.ll"),
                "llvm-ir-full-build",
                CompileTarget.LlvmIr);
            var semanticNode = new ProjectModuleSemanticSignatureNode(
                "Main",
                [],
                [],
                "surface-main",
                "semantic-deps-main",
                "semantic-main");
            var typedNode = new ProjectModuleTypedSemanticNode(
                "Main",
                [],
                [],
                "typed-surface-main",
                "typed-deps-main",
                "typed-main");
            var mirNode = new ProjectModuleMirArtifactNode(
                "Main",
                [],
                "typed-main",
                "mir-functions-main",
                "mir-main");
            var result = new CompilationResult
            {
                ModuleSemanticSignatureSnapshot = new ProjectModuleSemanticSignatureSnapshot(
                    ProjectModuleSemanticSignatureSnapshot.CurrentSchemaVersion,
                    [semanticNode]),
                ModuleTypedSemanticSnapshot = new ProjectModuleTypedSemanticSnapshot(
                    ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
                    [typedNode]),
                ModuleMirArtifactSnapshot = new ProjectModuleMirArtifactSnapshot(
                    ProjectModuleMirArtifactSnapshot.CurrentSchemaVersion,
                    [mirNode]),
                ModuleArtifactRestorePlan = CreateSingleModuleRestorePlan(
                    SemanticReady: true,
                    TypedSemanticReady: false,
                    MirReady: false),
                ModuleTypedArtifactRestorePlan = CreateSingleModuleRestorePlan(
                    SemanticReady: true,
                    TypedSemanticReady: true,
                    MirReady: true)
            };

            BuildCommand.StoreLatestModuleSemanticSignatureSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleTypedSemanticSignatureSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleMirArtifactSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleArtifactRestorePlanSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleTypedArtifactRestorePlanSnapshotArtifact(artifact, result);
            StoreModuleArtifactNode(
                cache,
                artifact,
                semanticNode.ModuleKey,
                ProjectModuleArtifactKinds.SemanticSignature,
                semanticNode.ExportSurfaceHash,
                semanticNode.DependencySemanticSignatureHash,
                semanticNode);
            StoreModuleArtifactNode(
                cache,
                artifact,
                typedNode.ModuleKey,
                ProjectModuleArtifactKinds.TypedSemanticSignature,
                typedNode.LocalSurfaceHash,
                typedNode.DependencyTypedSemanticHash,
                typedNode);
            StoreModuleArtifactNode(
                cache,
                artifact,
                mirNode.ModuleKey,
                ProjectModuleArtifactKinds.MirArtifact,
                mirNode.TypedSemanticHash,
                mirNode.MirArtifactHash,
                mirNode);

            var method = typeof(BuildCommand).GetMethod(
                "CreateFullBuildArtifactCacheDiagnosticResult",
                BindingFlags.Static | BindingFlags.NonPublic);
            var restored = Assert.IsType<CompilationResult>(method!.Invoke(
                null,
                [artifact, TimeSpan.FromMilliseconds(1), false]));

            Assert.Equal(1, restored.ModuleArtifactRestorePayload?.ValidatedModules);
            Assert.Equal(1, restored.ModuleTypedArtifactRestorePayload?.ValidatedModules);
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleArtifactRestoreExecution.restoredModules"));
            Assert.Equal(0, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleArtifactRestoreExecution.blockedModules"));
            Assert.Equal(1, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleTypedArtifactRestoreExecution.restoredModules"));
            Assert.Equal(0, restored.ProfilingCounters.GetValueOrDefault("Build.artifactCache.moduleTypedArtifactRestoreExecution.blockedModules"));
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
    public void StoreLatestModuleMemberIndexRestorePlanSnapshotArtifact_CanLoadPreviousSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_member_index_restore_plan_{Guid.NewGuid():N}");
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
                Path.Combine(tempDir, "main.ll"),
                "llvm-ir-full-build",
                CompileTarget.LlvmIr);
            var result = new CompilationResult
            {
                ModuleMemberIndexRestorePlan = new ProjectModuleMemberIndexRestorePlan(
                    "module-member-index-restore-plan-v1",
                    [
                        new ProjectModuleMemberIndexRestoreItem(
                            "Main",
                            ProjectModuleMemberIndexRestoreAction.Rebuild,
                            "old-local",
                            "new-local",
                            "old-deps",
                            "new-deps",
                            "old-member",
                            "new-member")
                    ],
                    TotalModules: 1,
                    RestoreModules: 0,
                    RebuildModules: 1,
                    AddedModules: 0,
                    RemovedModules: 0)
            };

            BuildCommand.StoreLatestModuleMemberIndexRestorePlanSnapshotArtifact(artifact, result);
            var restored = BuildCommand.TryLoadLatestModuleMemberIndexRestorePlanSnapshot(artifact);

            Assert.NotNull(restored);
            Assert.Equal(1, restored!.RebuildModules);
            Assert.Equal(ProjectModuleMemberIndexRestoreAction.Rebuild, Assert.Single(restored.Modules).Action);
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
    public void LatestSnapshotArtifacts_UseSchemaVersionedCacheKeys()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_latest_snapshot_schema_keys_{Guid.NewGuid():N}");
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
                Path.Combine(tempDir, "main.ll"),
                "llvm-ir-full-build",
                CompileTarget.LlvmIr);
            var oldAssociatedTypeKey = key with { CacheSchema = "associated-type-projection-latest-v1" };
            cache.StoreArtifact(
                oldAssociatedTypeKey,
                BuildCommand.LatestAssociatedTypeProjectionSnapshotArtifactKind,
                ".json",
                """{"SchemaVersion":"associated-type-projection-snapshot-v3","Entries":[]}""");
            var oldBorrowDiagnosticKey = key with { CacheSchema = "borrow-diagnostic-latest-v1" };
            cache.StoreArtifact(
                oldBorrowDiagnosticKey,
                BuildCommand.LatestBorrowDiagnosticSnapshotArtifactKind,
                ".json",
                """{"SchemaVersion":"borrow-diagnostic-snapshot-v1","MirModuleFingerprint":"old","BorrowDependencyHash":"old","Functions":[]}""");
            var oldBorrowCodegenHintsKey = key with { CacheSchema = "borrow-codegen-hints-latest-v1" };
            cache.StoreArtifact(
                oldBorrowCodegenHintsKey,
                BuildCommand.LatestBorrowCodegenHintsSnapshotArtifactKind,
                ".json",
                """{"SchemaVersion":"borrow-codegen-hints-snapshot-v1","MirModuleFingerprint":"old","BorrowCodegenDependencyHash":"old","Functions":[]}""");
            var oldLlvmCodegenUnitPlanKey = key with { CacheSchema = "llvm-codegen-unit-plan-latest-v1" };
            cache.StoreArtifact(
                oldLlvmCodegenUnitPlanKey,
                BuildCommand.LlvmCodegenUnitPlanSnapshotArtifactKind,
                ".json",
                """{"SchemaVersion":"llvm-codegen-unit-plan-snapshot-v1","EnvelopeUnit":{"EnvelopeFingerprint":"env","UnitCacheKey":"env","LineCount":1,"GlobalCount":0,"DeclarationCount":0,"TypeDefinitionCount":0},"FunctionUnits":[],"ObjectGroups":[]}""");

            Assert.Null(BuildCommand.TryLoadLatestAssociatedTypeProjectionSnapshot(artifact));
            Assert.Null(BuildCommand.TryLoadLatestBorrowDiagnosticSnapshot(artifact));
            Assert.Null(BuildCommand.TryLoadLatestBorrowCodegenHintsSnapshot(artifact));
            Assert.Null(BuildCommand.TryLoadLatestLlvmCodegenUnitPlanSnapshot(artifact));

            var currentAssociatedTypeKey = key with { CacheSchema = "associated-type-projection-latest-v4" };
            cache.StoreArtifact(
                currentAssociatedTypeKey,
                BuildCommand.LatestAssociatedTypeProjectionSnapshotArtifactKind,
                ".json",
                """{"SchemaVersion":"associated-type-projection-snapshot-v3","Entries":[]}""");
            var currentBorrowDiagnosticKey = key with { CacheSchema = "borrow-diagnostic-latest-v2" };
            cache.StoreArtifact(
                currentBorrowDiagnosticKey,
                BuildCommand.LatestBorrowDiagnosticSnapshotArtifactKind,
                ".json",
                """{"SchemaVersion":"borrow-diagnostic-snapshot-v1","MirModuleFingerprint":"old","BorrowDependencyHash":"old","Functions":[]}""");
            var currentLlvmCodegenUnitPlanKey = key with { CacheSchema = "llvm-codegen-unit-plan-latest-v2" };
            cache.StoreArtifact(
                currentLlvmCodegenUnitPlanKey,
                BuildCommand.LlvmCodegenUnitPlanSnapshotArtifactKind,
                ".json",
                """{"SchemaVersion":"llvm-codegen-unit-plan-snapshot-v1","EnvelopeUnit":{"EnvelopeFingerprint":"env","UnitCacheKey":"env","LineCount":1,"GlobalCount":0,"DeclarationCount":0,"TypeDefinitionCount":0},"FunctionUnits":[],"ObjectGroups":[]}""");

            Assert.Null(BuildCommand.TryLoadLatestAssociatedTypeProjectionSnapshot(artifact));
            Assert.Null(BuildCommand.TryLoadLatestBorrowDiagnosticSnapshot(artifact));
            Assert.Null(BuildCommand.TryLoadLatestLlvmCodegenUnitPlanSnapshot(artifact));

            var result = new CompilationResult
            {
                AssociatedTypeProjectionSnapshot = new AssociatedTypeProjectionSnapshot(
                    AssociatedTypeProjectionSnapshot.CurrentSchemaVersion,
                    [
                        new AssociatedTypeProjectionSnapshotEntry(
                            "Iterator",
                            "Iterator",
                            "Item",
                            "Int",
                            "Int",
                            AllowTypeConstructorReference: false,
                            "path(String)",
                            nameof(TyCon),
                            "String",
                            "",
                            "String",
                            new AssociatedTypeProjectionReducedTypeShape(
                                nameof(TyCon),
                                "String",
                                [],
                                "type:4",
                                SymbolId: 0,
                                TypeId: 4))
                    ]),
                TraitCheckSnapshot = new TraitCheckSnapshot(
                    TraitCheckSnapshot.CurrentSchemaVersion,
                    [new TraitCheckSnapshotEntry("Box", "Eq", "Eq", "", "", Success: true, ErrorMessage: null)]),
                ImplOverlapCheckSnapshot = new ImplOverlapCheckSnapshot(
                    ImplOverlapCheckSnapshot.CurrentSchemaVersion,
                    [
                        new ImplOverlapCheckSnapshotEntry(
                            "Eq|Box||",
                            "Eq",
                            "Box",
                            "",
                            "",
                            CandidateCount: 0,
                            CandidateSetFingerprint: "",
                            NonOverlappingCandidateCount: 0,
                            SpecializationAllowedCandidateCount: 0,
                            HasConflict: false,
                            ConflictingImplKey: null,
                            SpecializationRelation: null)
                    ]),
                BorrowDiagnosticSnapshot = new BorrowDiagnosticSnapshot(
                    BorrowDiagnosticSnapshot.CurrentSchemaVersion,
                    "mir",
                    "borrow",
                    [new BorrowDiagnosticFunctionSnapshot("name:main", "body", 0, 0, 0, 0, 0, [])]),
                BorrowCodegenHintsSnapshot = new BorrowCodegenHintsSnapshot(
                    BorrowCodegenHintsSnapshot.CurrentSchemaVersion,
                    "mir",
                    "borrow-codegen",
                    [
                        new BorrowCodegenHintsFunctionSnapshot(
                            "name:main",
                            "body",
                            "main",
                            1,
                            Perceus: null,
                            Reuse: null,
                            StackPromotion: null,
                            UnifiedStackPromotion: null)
                    ]),
                LlvmCodegenUnitPlan = new LlvmCodegenUnitPlanSnapshot(
                    LlvmCodegenUnitPlanSnapshot.CurrentSchemaVersion,
                    new LlvmCodegenUnitPlanEnvelopeUnit("env", "env-key", 1, 0, 0, 0),
                    [],
                    [])
            };

            BuildCommand.StoreLatestAssociatedTypeProjectionSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestTraitCheckSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestImplOverlapCheckSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestBorrowDiagnosticSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestBorrowCodegenHintsSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestFunctionFingerprintSnapshotArtifacts(artifact, result);

            Assert.NotNull(BuildCommand.TryLoadLatestAssociatedTypeProjectionSnapshot(artifact));
            Assert.NotNull(BuildCommand.TryLoadLatestTraitCheckSnapshot(artifact));
            Assert.NotNull(BuildCommand.TryLoadLatestImplOverlapCheckSnapshot(artifact));
            Assert.NotNull(BuildCommand.TryLoadLatestBorrowDiagnosticSnapshot(artifact));
            Assert.NotNull(BuildCommand.TryLoadLatestBorrowCodegenHintsSnapshot(artifact));
            Assert.NotNull(BuildCommand.TryLoadLatestLlvmCodegenUnitPlanSnapshot(artifact));
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
    public void TryRestoreLlvmIrFromBackendArtifacts_RecomposesLatestFragmentsWithoutFullBuildHit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_backend_fragment_restore_{Guid.NewGuid():N}");
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
                Path.Combine(tempDir, "main.ll"),
                "llvm-ir-full-build",
                CompileTarget.LlvmIr);
            var module = new LlvmModule
            {
                Name = "backend_restore",
                Functions =
                [
                    new LlvmFunction
                    {
                        Name = "main",
                        ReturnType = LlvmIntType.I32,
                        BasicBlocks =
                        [
                            new LlvmBasicBlock
                            {
                                Label = "entry",
                                Terminator = new LlvmRet
                                {
                                    Value = new LlvmConstant
                                    {
                                        Value = 0L,
                                        Type = LlvmIntType.I32
                                    }
                                }
                            }
                        ]
                    }
                ]
            };
            var result = new CompilationResult
            {
                LlvmFunctionFragments = LlvmFunctionFragmentSnapshot.FromModule(module),
                LlvmModuleEnvelope = LlvmModuleEnvelopeSnapshot.FromModule(module, "layout", "triple")
            };
            var profilePath = Path.Combine(tempDir, "profile.json");

            BuildCommand.StoreLatestFunctionFingerprintSnapshotArtifacts(artifact, result);
            BuildCommand.StoreLatestBackendArtifactRestoreInputSnapshotArtifact(artifact, result);
            var method = typeof(BuildCommand).GetMethod(
                "TryRestoreLlvmIrFromBackendArtifacts",
                BindingFlags.Static | BindingFlags.NonPublic);
            var restored = Assert.IsType<bool>(method!.Invoke(null, [artifact, true, profilePath]));

            Assert.True(restored);
            Assert.Contains("define external i32 @main()", File.ReadAllText(artifact.OutputPath), StringComparison.Ordinal);
            Assert.True(File.Exists(profilePath));
            var profile = File.ReadAllText(profilePath);
            Assert.Contains("restore_llvm_ir_from_backend_fragments", profile, StringComparison.Ordinal);
            Assert.Contains("Build.artifactCache.backendArtifactRestore.hits", profile, StringComparison.Ordinal);
            Assert.Contains("Build.artifactCache.llvmIrBackendFragments.hits", profile, StringComparison.Ordinal);
            Assert.DoesNotContain("Build.artifactCache.llvmIrFullBuild.hits", profile, StringComparison.Ordinal);
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
    public void TryRestoreLlvmIrFromBackendArtifacts_RejectsStaleInputSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_backend_fragment_restore_stale_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            var key = CreateArtifactKey();
            var staleArtifact = new BuildCommand.FullBuildArtifact(
                cache,
                key,
                key,
                key,
                key,
                key,
                key,
                key,
                key,
                Path.Combine(tempDir, "stale.ll"),
                "llvm-ir-full-build",
                CompileTarget.LlvmIr);
            var currentArtifact = staleArtifact with
            {
                Key = key with { SourceHash = "changed-input" },
                OutputPath = Path.Combine(tempDir, "current.ll")
            };
            var module = new LlvmModule
            {
                Name = "backend_restore_stale",
                Functions =
                [
                    new LlvmFunction
                    {
                        Name = "main",
                        ReturnType = LlvmVoidType.Instance,
                        BasicBlocks =
                        [
                            new LlvmBasicBlock
                            {
                                Label = "entry",
                                Terminator = new LlvmRet()
                            }
                        ]
                    }
                ]
            };
            var result = new CompilationResult
            {
                LlvmFunctionFragments = LlvmFunctionFragmentSnapshot.FromModule(module),
                LlvmModuleEnvelope = LlvmModuleEnvelopeSnapshot.FromModule(module, "layout", "triple")
            };

            BuildCommand.StoreLatestFunctionFingerprintSnapshotArtifacts(staleArtifact, result);
            BuildCommand.StoreLatestBackendArtifactRestoreInputSnapshotArtifact(staleArtifact, result);
            var method = typeof(BuildCommand).GetMethod(
                "TryRestoreLlvmIrFromBackendArtifacts",
                BindingFlags.Static | BindingFlags.NonPublic);
            var restored = Assert.IsType<bool>(method!.Invoke(null, [currentArtifact, true, null]));

            Assert.False(restored);
            Assert.False(File.Exists(currentArtifact.OutputPath));
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
    public void TryRestoreNativeFullModuleFromBackendArtifacts_WithAvailableToolchain_ProducesExecutable()
    {
        if (!ToolExists("clang") || (!ToolExists("llc") && !ToolExists("clang")))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_backend_native_full_restore_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            var key = CreateArtifactKey();
            var targetInfo = TargetInfo.Default;
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
                Path.Combine(tempDir, OperatingSystem.IsWindows() ? "main.exe" : "main"),
                "native-full-build",
                CompileTarget.Native);
            var module = new LlvmModule
            {
                Name = "backend_native_full_restore",
                Functions =
                [
                    new LlvmFunction
                    {
                        Name = "eidos_main",
                        ReturnType = LlvmIntType.I64,
                        BasicBlocks =
                        [
                            new LlvmBasicBlock
                            {
                                Label = "entry",
                                Terminator = new LlvmRet
                                {
                                    Value = new LlvmConstant
                                    {
                                        Value = 0L,
                                        Type = LlvmIntType.I64
                                    }
                                }
                            }
                        ]
                    }
                ]
            };
            var result = new CompilationResult
            {
                LlvmFunctionFragments = LlvmFunctionFragmentSnapshot.FromModule(module),
                LlvmModuleEnvelope = LlvmModuleEnvelopeSnapshot.FromModule(
                    module,
                    targetInfo.DataLayout,
                    targetInfo.Triple)
            };
            var profilePath = Path.Combine(tempDir, "profile.json");

            BuildCommand.StoreLatestFunctionFingerprintSnapshotArtifacts(artifact, result);
            BuildCommand.StoreLatestBackendArtifactRestoreInputSnapshotArtifact(artifact, result);
            var method = typeof(BuildCommand).GetMethod(
                "TryRestoreNativeFullModuleFromBackendArtifacts",
                BindingFlags.Static | BindingFlags.NonPublic);
            var restored = Assert.IsType<bool>(method!.Invoke(
                null,
                [
                    artifact,
                    new CompilationOptions(),
                    targetInfo,
                    0,
                    false,
                    true,
                    profilePath
                ]));

            Assert.True(restored);
            Assert.True(File.Exists(artifact.OutputPath));
            Assert.True(File.Exists(profilePath));
            var profile = File.ReadAllText(profilePath);
            Assert.Contains("restore_native_from_backend_full_module", profile, StringComparison.Ordinal);
            Assert.Contains("native_full_module_restore_from_previous_fragments", profile, StringComparison.Ordinal);
            Assert.Contains("Build.artifactCache.backendArtifactRestore.hits", profile, StringComparison.Ordinal);
            Assert.Contains("Build.artifactCache.nativeBackendFullModule.hits", profile, StringComparison.Ordinal);
            Assert.DoesNotContain("Build.artifactCache.nativeFullBuild.hits", profile, StringComparison.Ordinal);
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
    public void CompileNativeFullModule_WithRestoredFragmentsAndAvailableToolchain_ProducesExecutable()
    {
        if (!ToolExists("clang") || (!ToolExists("llc") && !ToolExists("clang")))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_native_full_fragment_compile_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var targetInfo = TargetInfo.Default;
            var module = new LlvmModule
            {
                Name = "native_full_fragment_compile",
                Functions =
                [
                    new LlvmFunction
                    {
                        Name = "eidos_main",
                        ReturnType = LlvmIntType.I64,
                        BasicBlocks =
                        [
                            new LlvmBasicBlock
                            {
                                Label = "entry",
                                Terminator = new LlvmRet
                                {
                                    Value = new LlvmConstant
                                    {
                                        Value = 0L,
                                        Type = LlvmIntType.I64
                                    }
                                }
                            }
                        ]
                    }
                ]
            };
            var profile = new CodeGenProfile();
            var compiler = new LlvmCompiler(targetInfo, optimizationLevel: 0, profile: profile);
            var result = new CompilationResult
            {
                LlvmFunctionFragments = LlvmFunctionFragmentSnapshot.FromModule(module),
                LlvmModuleEnvelope = LlvmModuleEnvelopeSnapshot.FromModule(
                    module,
                    targetInfo.DataLayout,
                    targetInfo.Triple)
            };
            var outputPath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "main.exe" : "main");
            var method = typeof(BuildCommand).GetMethod(
                "CompileNativeFullModule",
                BindingFlags.Static | BindingFlags.NonPublic);
            var codeGenResult = Assert.IsType<CodeGenResult>(method!.Invoke(
                null,
                [compiler, result, outputPath]));

            Assert.True(codeGenResult.Success, codeGenResult.ErrorMessage ?? "CompileNativeFullModule failed.");
            Assert.True(File.Exists(outputPath));
            Assert.Contains(
                profile.Events,
                static entry => entry.Name == "native_full_module_restore_from_previous_fragments");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static (string EntryPath, ProjectCommandInputResolution Resolution, CompilationOptions CompileOptions)
        CreateResolvedProject(string tempDir)
    {
        var srcDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(srcDir);
        var entryPath = Path.Combine(srcDir, "Main.eidos");
        var projectPath = Path.Combine(tempDir, "eidos.toml");
        File.WriteAllText(projectPath, """
name = "flags-test"
sourceRoots = ["src"]

[[targets]]
name = "main"
entry = "src/Main.eidos"
kind = "executable"
""");
        File.WriteAllText(entryPath, """
Main :: module {
    main :: Unit -> Unit { _ => () }
}
""");

        var resolution = ProjectCommandInputResolver.Resolve(
            projectPath,
            project: null,
            targetName: "main",
            explicitImportRoots: null,
            workingDirectory: tempDir);
        var compileOptions = new CompilationOptions
        {
            InputFile = resolution.SourceFilePath,
            LanguageVersion = resolution.GetLanguageVersion(),
            Target = CompilationTarget.LlvmIr,
            StopAtPhase = CompilationPhase.Llvm,
            EnableMirOptimizations = true
        };
        return (entryPath, resolution, compileOptions);
    }

    private static BuildCommand.BuildOptions CreateBuildOptions(CompileTarget target) =>
        new()
        {
            Source = "src/Main.eidos",
            Target = target,
            BuildMode = BuildMode.Release
        };

    private static int ExecuteBuild(BuildCommand.BuildOptions options)
    {
        var method = typeof(BuildCommand).GetMethod(
            "Execute",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<int>>(method!.Invoke(null, [options]));
        return task.GetAwaiter().GetResult();
    }

    private static Dictionary<string, long> ReadProfileCounters(string profilePath)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(profilePath));
        return document.RootElement
            .GetProperty("Counters")
            .EnumerateArray()
            .ToDictionary(
                counter => counter.GetProperty("Name").GetString() ?? "",
                counter => counter.GetProperty("Value").GetInt64(),
                StringComparer.Ordinal);
    }

    private static ProjectModuleArtifactRestorePlan CreateSingleModuleRestorePlan(
        bool SemanticReady,
        bool TypedSemanticReady,
        bool MirReady) =>
        new(
            ProjectModuleArtifactRestorePlan.CurrentSchemaVersion,
            [
                new ProjectModuleArtifactRestoreLayer(
                    0,
                    [
                        new ProjectModuleArtifactRestoreItem(
                            "Main",
                            ProjectModuleArtifactRestoreAction.Restore,
                            SemanticReady,
                            TypedSemanticReady,
                            MirReady)
                    ],
                    RestoreCount: 1,
                    BlockedCount: 0,
                    ReadyArtifactCount: 0)
            ],
            TotalModules: 1,
            RestoreModules: 1,
            BlockedModules: 0,
            ReadyArtifactModules: 0,
            MaxRestoreParallelWidth: 1);

    private static void StoreModuleArtifactNode<T>(
        ModuleArtifactCache cache,
        BuildCommand.FullBuildArtifact artifact,
        string moduleKey,
        string kind,
        string sourceHash,
        string dependencySignatureHash,
        T node)
    {
        cache.StoreArtifact(
            artifact.Key with
            {
                CacheSchema = "module-artifact-readiness-v1",
                ModuleKey = moduleKey,
                SourceHash = sourceHash,
                DependencySignatureHash = dependencySignatureHash,
                FlagsHash = artifact.OutputIndependentPayloadKey.FlagsHash
            },
            kind,
            ".json",
            System.Text.Json.JsonSerializer.Serialize(node));
    }

    private static bool ToolExists(string toolName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return false;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, toolName);
            if (File.Exists(candidate))
            {
                return true;
            }

            if (OperatingSystem.IsWindows() &&
                File.Exists(Path.Combine(dir, $"{toolName}.exe")))
            {
                return true;
            }
        }

        return false;
    }
    private static ModuleArtifactKey CreateArtifactKey() => new()
    {
        CacheSchema = "full-build",
        ModuleKey = "module",
        SourceHash = "input",
        LanguageVersion = "syntax",
        DependencySignatureHash = "deps",
        TargetTriple = "triple",
        FlagsHash = "flags"
    };
}
