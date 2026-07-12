using Eidosc.Cli.Commands;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

public sealed class BuildCommandSnapshotArtifactTests
{
    [Fact]
    public void StoreModuleTypedSemanticSignatureSnapshotArtifact_WritesTypedSnapshotPayload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_typed_snapshot_artifact_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            var key = new ModuleArtifactKey
            {
                CacheSchema = "full-build",
                ModuleKey = "module",
                SourceHash = "input",
                LanguageVersion = "syntax",
                DependencySignatureHash = "deps",
                TargetTriple = "triple",
                FlagsHash = "flags"
            };
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
                ModuleTypedSemanticSnapshot = new ProjectModuleTypedSemanticSnapshot(
                    ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
                    [
                        new ProjectModuleTypedSemanticNode(
                            "Main",
                            [],
                            [
                                new ProjectModuleTypedSemanticDeclaration(
                                    "Function",
                                    "main",
                                    "Main::Function:main",
                                    "",
                                    1,
                                    0,
                                    IsPublic: true,
                                    ["returnType:Unit"],
                                    "hash")
                            ],
                            "local",
                            "deps",
                            "hash")
                    ])
            };

            BuildCommand.StoreModuleTypedSemanticSignatureSnapshotArtifact(artifact, result);

            Assert.True(cache.TryGetArtifact(
                key,
                BuildCommand.ModuleTypedSemanticSignatureSnapshotArtifactKind,
                out var manifest));
            Assert.NotNull(manifest);
            var json = File.ReadAllText(manifest!.PayloadPath);
        Assert.Contains("typed-semantic-snapshot-v3", json, StringComparison.Ordinal);
            Assert.Contains("main", json, StringComparison.Ordinal);
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
    public void StoreLatestModuleTypedSemanticSignatureSnapshotArtifact_CanLoadPreviousSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_latest_typed_snapshot_artifact_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            var key = CreateArtifactKey();
            var latestTypedKey = key with
            {
                CacheSchema = "module-typed-semantic-signature-latest-v1",
                SourceHash = "latest",
                DependencySignatureHash = "latest"
            };
            var artifact = new BuildCommand.FullBuildArtifact(
                cache,
                key,
                key,
                key,
                latestTypedKey,
                key,
                key,
                key,
                key,
                Path.Combine(tempDir, "main.ll"),
                "llvm-ir-full-build",
                CompileTarget.LlvmIr);
            cache.StoreArtifact(
                latestTypedKey,
                "module-typed-semantic-signature-latest",
                ".json",
                """{"SchemaVersion":"typed-semantic-snapshot-v0","Nodes":[]}""");

            Assert.Null(BuildCommand.TryLoadLatestModuleTypedSemanticSignatureSnapshot(artifact));

            var result = new CompilationResult
            {
                ModuleTypedSemanticSnapshot = new ProjectModuleTypedSemanticSnapshot(
                    ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
                    [
                        new ProjectModuleTypedSemanticNode(
                            "Main",
                            [],
                            [],
                            "local",
                            "deps",
                            "typed")
                    ])
            };

            BuildCommand.StoreLatestModuleTypedSemanticSignatureSnapshotArtifact(artifact, result);
            var restored = BuildCommand.TryLoadLatestModuleTypedSemanticSignatureSnapshot(artifact);

            Assert.NotNull(restored);
            Assert.Equal("Main", Assert.Single(restored!.Nodes).ModuleKey);
            Assert.Equal("typed", Assert.Single(restored.Nodes).TypedSemanticHash);
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
    public void StoreModuleSemanticSignatureSnapshotArtifact_CanLoadExactSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_exact_semantic_snapshot_artifact_{Guid.NewGuid():N}");
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
                Path.Combine(tempDir, "typed.profile"),
                "profile-module-artifacts",
                CompileTarget.Typed);
            cache.StoreArtifact(
                key,
                "module-semantic-signature-snapshot",
                ".json",
                """
                {"Nodes":[{"ModuleKey":"Main","Dependencies":[],"Declarations":[],"ExportSurfaceHash":"surface","DependencySemanticSignatureHash":"deps","SemanticSignatureHash":"semantic"}]}
                """);
            Assert.Null(BuildCommand.TryLoadModuleSemanticSignatureSnapshot(artifact));
            cache.StoreArtifact(
                key,
                "module-semantic-signature-snapshot",
                ".json",
                """
                {"SchemaVersion":"semantic-signature-snapshot-v1","Nodes":[{"ModuleKey":"Main","Dependencies":[],"Declarations":[],"ExportSurfaceHash":"surface","DependencySemanticSignatureHash":"deps","SemanticSignatureHash":"semantic"}]}
                """);
            var restored = BuildCommand.TryLoadModuleSemanticSignatureSnapshot(artifact);
            Assert.NotNull(restored);
            Assert.Equal("Main", Assert.Single(restored!.Nodes).ModuleKey);
            Assert.Equal("semantic", Assert.Single(restored.Nodes).SemanticSignatureHash);
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
    public void StoreModuleTypedSemanticSignatureSnapshotArtifact_CanLoadExactSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_exact_typed_snapshot_artifact_{Guid.NewGuid():N}");
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
                Path.Combine(tempDir, "typed.profile"),
                "profile-module-artifacts",
                CompileTarget.Typed);
            var result = new CompilationResult
            {
                ModuleTypedSemanticSnapshot = new ProjectModuleTypedSemanticSnapshot(
                    ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
                    [
                        new ProjectModuleTypedSemanticNode(
                            "Main",
                            [],
                            [],
                            "typed-surface",
                            "typed-deps",
                            "typed")
                    ])
            };

            BuildCommand.StoreModuleTypedSemanticSignatureSnapshotArtifact(artifact, result);
            var restored = BuildCommand.TryLoadModuleTypedSemanticSignatureSnapshot(artifact);

            Assert.NotNull(restored);
            Assert.Equal("Main", Assert.Single(restored!.Nodes).ModuleKey);
            Assert.Equal("typed", Assert.Single(restored.Nodes).TypedSemanticHash);
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
    public void StorePerModuleSemanticArtifacts_WritesModuleScopedPayloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_semantic_artifacts_{Guid.NewGuid():N}");
        try
        {
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "cache"));
            var key = CreateArtifactKey();
            var outputIndependentKey = key with
            {
                FlagsHash = "output-independent-flags"
            };
            var artifact = new BuildCommand.FullBuildArtifact(
                cache,
                key,
                outputIndependentKey,
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
                ModuleSemanticSignatureSnapshot = new ProjectModuleSemanticSignatureSnapshot(
                    ProjectModuleSemanticSignatureSnapshot.CurrentSchemaVersion,
                    [
                        new ProjectModuleSemanticSignatureNode(
                            "Main",
                            [],
                            [],
                            "surface",
                            "deps",
                            "semantic")
                    ]),
                ModuleTypedSemanticSnapshot = new ProjectModuleTypedSemanticSnapshot(
                    ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion,
                    [
                        new ProjectModuleTypedSemanticNode(
                            "Main",
                            [],
                            [],
                            "typed-surface",
                            "typed-deps",
                            "typed")
                    ])
            };

            BuildCommand.StorePerModuleSemanticArtifacts(artifact, result);

            var semanticKey = key with
            {
                CacheSchema = "module-artifact-readiness-v1",
                ModuleKey = "Main",
                SourceHash = "surface",
                DependencySignatureHash = "deps",
                FlagsHash = outputIndependentKey.FlagsHash
            };
            var typedKey = key with
            {
                CacheSchema = "module-artifact-readiness-v1",
                ModuleKey = "Main",
                SourceHash = "typed-surface",
                DependencySignatureHash = "typed-deps",
                FlagsHash = outputIndependentKey.FlagsHash
            };
            Assert.True(cache.TryGetArtifact(
                semanticKey,
                BuildCommand.ModuleSemanticSignatureArtifactKind,
                out var semanticManifest));
            Assert.True(cache.TryGetArtifact(
                typedKey,
                BuildCommand.ModuleTypedSemanticSignatureArtifactKind,
                out var typedManifest));
            Assert.Contains("\"ModuleKey\":\"Main\"", File.ReadAllText(semanticManifest!.PayloadPath), StringComparison.Ordinal);
            Assert.Contains("\"TypedSemanticHash\":\"typed\"", File.ReadAllText(typedManifest!.PayloadPath), StringComparison.Ordinal);
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
    public void StoreFunctionFingerprintSnapshotArtifacts_WritesMirAndLlvmPayloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_function_snapshot_artifact_{Guid.NewGuid():N}");
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
                MirFunctionFingerprints = new MirFunctionFingerprintSnapshot(
                    "mir-function-fingerprint-snapshot-v1",
                    [new MirFunctionFingerprint("name:main", "abc", 1, 2, 3, 0)]),
                LlvmFunctionFingerprints = new LlvmFunctionFingerprintSnapshot(
                    "llvm-function-fingerprint-snapshot-v1",
                    [new LlvmFunctionFingerprint("name:main", "def", 1, 2, 0)]),
                LlvmFunctionFragments = new LlvmFunctionFragmentSnapshot(
                    "llvm-function-fragment-snapshot-v1",
                    [new LlvmFunctionFragment("name:main", "def", "define i64 @main() { ret i64 0 }", "declare i64 @main()", "External", 1, 2, 0)]),
                LlvmModuleEnvelope = new LlvmModuleEnvelopeSnapshot(
                    "llvm-module-envelope-snapshot-v1",
                    "main",
                    "main.eidos",
                    "layout",
                    "triple",
                    ["target triple = \"triple\""],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    [],
                    []),
                LlvmCodegenUnitPlan = new LlvmCodegenUnitPlanSnapshot(
                    LlvmCodegenUnitPlanSnapshot.CurrentSchemaVersion,
                    new LlvmCodegenUnitPlanEnvelopeUnit("env", "env-key", 1, 0, 0, 0),
                    [new LlvmCodegenUnitPlanFunctionUnit("name:main", "def", "fn-key", "External", true, "", [], [], 42, 1, 2, 0)],
                    [new LlvmCodegenUnitPlanObjectGroup("group-key", "name:main", ["name:main"], [], [], 42, 1)])
            };

            BuildCommand.StoreFunctionFingerprintSnapshotArtifacts(artifact, result);
            BuildCommand.StoreModuleMemberIndexSnapshotArtifact(artifact, result);

            Assert.True(cache.TryGetArtifact(
                key,
                BuildCommand.ModuleMemberIndexSnapshotArtifactKind,
                out var memberIndexManifest));
            Assert.True(cache.TryGetArtifact(
                key,
                BuildCommand.MirFunctionFingerprintSnapshotArtifactKind,
                out var mirManifest));
            Assert.True(cache.TryGetArtifact(
                key,
                BuildCommand.LlvmFunctionFingerprintSnapshotArtifactKind,
                out var llvmManifest));
            Assert.True(cache.TryGetArtifact(
                key,
                BuildCommand.LlvmFunctionFragmentSnapshotArtifactKind,
                out var llvmFragmentManifest));
            Assert.True(cache.TryGetArtifact(
                key,
                BuildCommand.LlvmModuleEnvelopeSnapshotArtifactKind,
                out var llvmEnvelopeManifest));
            Assert.True(cache.TryGetArtifact(
                key,
                BuildCommand.LlvmCodegenUnitPlanSnapshotArtifactKind,
                out var llvmCodegenUnitPlanManifest));
            Assert.Contains("module-member-index-snapshot-v1", File.ReadAllText(memberIndexManifest!.PayloadPath), StringComparison.Ordinal);
            Assert.Contains("Main::Function:main", File.ReadAllText(memberIndexManifest.PayloadPath), StringComparison.Ordinal);
            Assert.Contains("mir-function-fingerprint-snapshot-v1", File.ReadAllText(mirManifest!.PayloadPath), StringComparison.Ordinal);
            Assert.Contains("llvm-function-fingerprint-snapshot-v1", File.ReadAllText(llvmManifest!.PayloadPath), StringComparison.Ordinal);
            Assert.Contains("llvm-function-fragment-snapshot-v1", File.ReadAllText(llvmFragmentManifest!.PayloadPath), StringComparison.Ordinal);
            Assert.Contains("define i64", File.ReadAllText(llvmFragmentManifest.PayloadPath), StringComparison.Ordinal);
            Assert.Contains("llvm-module-envelope-snapshot-v1", File.ReadAllText(llvmEnvelopeManifest!.PayloadPath), StringComparison.Ordinal);
            Assert.Contains("target triple", File.ReadAllText(llvmEnvelopeManifest.PayloadPath), StringComparison.Ordinal);
            Assert.Contains(LlvmCodegenUnitPlanSnapshot.CurrentSchemaVersion, File.ReadAllText(llvmCodegenUnitPlanManifest!.PayloadPath), StringComparison.Ordinal);
            Assert.Contains("fn-key", File.ReadAllText(llvmCodegenUnitPlanManifest.PayloadPath), StringComparison.Ordinal);
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
    public void StoreLatestFunctionFingerprintSnapshotArtifacts_CanLoadPreviousSnapshots()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_latest_function_snapshot_artifact_{Guid.NewGuid():N}");
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
            var latestMirFunctionKey = key with
            {
                CacheSchema = "mir-function-fingerprint-latest-v1",
                SourceHash = "latest",
                DependencySignatureHash = "latest"
            };
            var latestLlvmKey = key with
            {
                CacheSchema = "llvm-function-fingerprint-latest-v1",
                SourceHash = "latest",
                DependencySignatureHash = "latest"
            };
            var latestLlvmFragmentKey = key with
            {
                CacheSchema = "llvm-function-fragment-latest-v1",
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
                latestMirFunctionKey,
                latestLlvmKey,
                latestLlvmFragmentKey,
                Path.Combine(tempDir, "main.ll"),
                "llvm-ir-full-build",
                CompileTarget.LlvmIr);
            cache.StoreArtifact(
                latestMirFunctionKey,
                "mir-function-fingerprint-latest",
                ".json",
                """{"SchemaVersion":"mir-function-fingerprint-snapshot-v0","Functions":[]}""");
            cache.StoreArtifact(
                latestLlvmKey,
                "llvm-function-fingerprint-latest",
                ".json",
                """{"SchemaVersion":"llvm-function-fingerprint-snapshot-v0","Functions":[]}""");
            cache.StoreArtifact(
                latestLlvmFragmentKey,
                "llvm-function-fragment-latest",
                ".json",
                """{"SchemaVersion":"llvm-function-fragment-snapshot-v0","Functions":[]}""");

            Assert.Null(BuildCommand.TryLoadLatestMirFunctionFingerprintSnapshot(artifact));
            Assert.Null(BuildCommand.TryLoadLatestLlvmFunctionFingerprintSnapshot(artifact));
            Assert.Null(BuildCommand.TryLoadLatestLlvmFunctionFragmentSnapshot(artifact));

            var result = new CompilationResult
            {
                MirFunctionFingerprints = new MirFunctionFingerprintSnapshot(
                    MirFunctionFingerprintSnapshot.CurrentSchemaVersion,
                    [new MirFunctionFingerprint("name:main", "abc", 1, 2, 3, 0)]),
                LlvmFunctionFingerprints = new LlvmFunctionFingerprintSnapshot(
                    LlvmFunctionFingerprintSnapshot.CurrentSchemaVersion,
                    [new LlvmFunctionFingerprint("name:main", "def", 1, 2, 0)]),
                LlvmFunctionFragments = new LlvmFunctionFragmentSnapshot(
                    LlvmFunctionFragmentSnapshot.CurrentSchemaVersion,
                    [new LlvmFunctionFragment("name:main", "def", "define i64 @main() { ret i64 0 }", "declare i64 @main()", "External", 1, 2, 0)])
            };

            BuildCommand.StoreLatestFunctionFingerprintSnapshotArtifacts(artifact, result);
            var restoredMir = BuildCommand.TryLoadLatestMirFunctionFingerprintSnapshot(artifact);
            var restoredLlvm = BuildCommand.TryLoadLatestLlvmFunctionFingerprintSnapshot(artifact);
            var restoredLlvmFragments = BuildCommand.TryLoadLatestLlvmFunctionFragmentSnapshot(artifact);

            Assert.NotNull(restoredMir);
            Assert.NotNull(restoredLlvm);
            Assert.NotNull(restoredLlvmFragments);
            Assert.Equal("abc", Assert.Single(restoredMir!.Functions).BodyHash);
            Assert.Equal("def", Assert.Single(restoredLlvm!.Functions).BodyHash);
            Assert.Contains("define i64", Assert.Single(restoredLlvmFragments!.Functions).IrFragment, StringComparison.Ordinal);
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
    public void StoreLatestModuleMirArtifactSnapshotArtifact_CanLoadPreviousSnapshot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_latest_module_mir_artifact_{Guid.NewGuid():N}");
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
            cache.StoreArtifact(
                latestMirArtifactKey with { CacheSchema = "module-artifact-restore-plan-latest-v1" },
                "module-artifact-restore-plan-latest",
                ".json",
                """{"Layers":[],"TotalModules":0,"RestoreModules":0,"BlockedModules":0,"ReadyArtifactModules":0,"MaxRestoreParallelWidth":0}""");
            cache.StoreArtifact(
                latestMirArtifactKey with { CacheSchema = "module-typed-artifact-restore-plan-latest-v1" },
                "module-typed-artifact-restore-plan-latest",
                ".json",
                """{"Layers":[],"TotalModules":0,"RestoreModules":0,"BlockedModules":0,"ReadyArtifactModules":0,"MaxRestoreParallelWidth":0}""");

            Assert.Null(BuildCommand.TryLoadLatestModuleArtifactRestorePlanSnapshot(artifact));
            Assert.Null(BuildCommand.TryLoadLatestModuleTypedArtifactRestorePlanSnapshot(artifact));
            cache.StoreArtifact(
                latestMirArtifactKey,
                "module-mir-artifact-latest",
                ".json",
                """{"SchemaVersion":"module-mir-artifact-snapshot-v0","Nodes":[]}""");

            Assert.Null(BuildCommand.TryLoadLatestModuleMirArtifactSnapshot(artifact));

            var result = new CompilationResult
            {
                ModuleMirArtifactSnapshot = new ProjectModuleMirArtifactSnapshot(
                    ProjectModuleMirArtifactSnapshot.CurrentSchemaVersion,
                    [
                        new ProjectModuleMirArtifactNode(
                            "Main",
                            [],
                            "typed-main",
                            "mir-functions",
                            "mir-artifact")
                    ])
            };

            BuildCommand.StoreLatestModuleMirArtifactSnapshotArtifact(artifact, result);
            var restored = BuildCommand.TryLoadLatestModuleMirArtifactSnapshot(artifact);

            Assert.NotNull(restored);
            var node = Assert.Single(restored!.Nodes);
            Assert.Equal("Main", node.ModuleKey);
            Assert.Equal("mir-artifact", node.MirArtifactHash);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
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
