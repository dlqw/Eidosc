using Eidosc.Cli.Commands;
using Eidosc.Pipeline;
using System.Reflection;
using System.Text.Json;

namespace Eidosc.Tests.Unit.Cli;

public sealed class BuildCommandModuleArtifactGateTests
{
    [Fact]
    public void FullBuildCacheDiagnosticResult_DoesNotCountModuleStageRestoreAsRealExecution()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_stage_cache_hit_{Guid.NewGuid():N}");
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
            var result = new CompilationResult
            {
                ModuleSemanticSignatureSnapshot = new ProjectModuleSemanticSignatureSnapshot(
                    ProjectModuleSemanticSignatureSnapshot.CurrentSchemaVersion,
                    [semanticNode]),
                ModuleArtifactRestorePlan = CreateSingleModuleRestorePlan(
                    SemanticReady: true,
                    TypedSemanticReady: false,
                    MirReady: false)
            };

            BuildCommand.StoreLatestModuleSemanticSignatureSnapshotArtifact(artifact, result);
            BuildCommand.StoreLatestModuleArtifactRestorePlanSnapshotArtifact(artifact, result);
            StoreModuleArtifactNode(
                cache,
                artifact,
                semanticNode.ModuleKey,
                ProjectModuleArtifactKinds.SemanticSignature,
                semanticNode.ExportSurfaceHash,
                semanticNode.DependencySemanticSignatureHash,
                semanticNode);

            var method = typeof(BuildCommand).GetMethod(
                "CreateFullBuildArtifactCacheDiagnosticResult",
                BindingFlags.Static | BindingFlags.NonPublic);
            var restored = Assert.IsType<CompilationResult>(method!.Invoke(
                null,
                [artifact, TimeSpan.FromMilliseconds(1), false]));

            Assert.Equal(1, restored.ModuleArtifactRestoreExecution?.RestoredModules);
            Assert.False(restored.ModuleArtifactRestoreExecution?.HasRealTaskExecution);
            Assert.Equal(0, restored.ProfilingCounters.GetValueOrDefault(
                "Build.moduleStage.Namer.realTaskExecution"));
            Assert.Equal(0, restored.ProfilingCounters.GetValueOrDefault(
                "Build.moduleStage.Namer.restoredModules"));
            Assert.Equal(0, restored.ProfilingCounters.GetValueOrDefault(
                "Build.moduleStage.Namer.compiledModules"));
            Assert.Equal(0, restored.ProfilingCounters.GetValueOrDefault(
                "Build.moduleStage.Namer.blockedModules"));
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
    public void FullBuildCacheHitResult_DoesNotMaterializeIncrementalState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_exact_cache_hit_{Guid.NewGuid():N}");
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
            var method = typeof(BuildCommand).GetMethod(
                "CreateFullBuildArtifactCacheHitResult",
                BindingFlags.Static | BindingFlags.NonPublic);

            var restored = Assert.IsType<CompilationResult>(method!.Invoke(
                null,
                [artifact, TimeSpan.FromMilliseconds(1), false]));

            Assert.Null(restored.ModuleSemanticSignatureSnapshot);
            Assert.Null(restored.ModuleArtifactRestorePayload);
            Assert.Equal(2, restored.ProfilingCounters.Count);
            Assert.Equal(1, restored.ProfilingCounters["Build.artifactCache.llvmIrFullBuild.hits"]);
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
    public void BuildOptions_ExposesModuleArtifactOnlyProfileGate()
    {
        var options = new BuildCommand.BuildOptions
        {
            ProfileModuleArtifactsOnly = true
        };

        Assert.True(options.ProfileModuleArtifactsOnly);
    }

    [Fact]
    public async Task BuildCommand_ProfileModuleArtifactsOnly_LoadsPersistedNamerPayloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_namer_payload_gate_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            var projectPath = Path.Combine(tempDir, "eidos.toml");
            await File.WriteAllTextAsync(projectPath, """
[package]
name = "namer-payload-gate"
version = "0.1.0"

[build]
sourceRoots = ["src"]
""");
            var mainPath = Path.Combine(tempDir, "src", "Main.eidos");
            await File.WriteAllTextAsync(mainPath, """
Main :: module {
    import Lib

    main :: Int -> Int
    {
        value => Lib::id(value)
    }
}
""");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "src", "Lib.eidos"), """
Lib :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""");
            var firstProfile = Path.Combine(tempDir, "first.json");
            var secondProfile = Path.Combine(tempDir, "second.json");

            var firstExit = await ExecuteBuildAsync(new BuildCommand.BuildOptions
            {
                Source = mainPath,
                Project = projectPath,
                Target = CompileTarget.Resolved,
                BuildMode = BuildMode.Dev,
                ProfileJson = firstProfile,
                ProfileModuleArtifactsOnly = true,
                NoImplicitPrelude = true,
                NoColor = true
            });
            foreach (var latestListArtifact in Directory.GetFiles(
                         Path.Combine(tempDir, "build", ".eidos-cache"),
                         "*.module-namer-state-payloads-latest.*"))
            {
                File.Delete(latestListArtifact);
            }

            var secondExit = await ExecuteBuildAsync(new BuildCommand.BuildOptions
            {
                Source = mainPath,
                Project = projectPath,
                Target = CompileTarget.Resolved,
                BuildMode = BuildMode.Dev,
                ProfileJson = secondProfile,
                ProfileModuleArtifactsOnly = true,
                NoImplicitPrelude = true,
                NoColor = true
            });

            Assert.Equal(0, firstExit);
            Assert.Equal(0, secondExit);
            var counters = ReadProfileCounters(secondProfile);
            Assert.True(
                counters.GetValueOrDefault("Namer.moduleRestore.applied") == 1,
                FormatCounters(counters));
            Assert.Equal(2, counters.GetValueOrDefault("Namer.moduleRestore.payloadModules"));
            Assert.Equal(1, counters.GetValueOrDefault("Build.moduleStage.Namer.realTaskExecution"));
            Assert.Equal(2, counters.GetValueOrDefault("Build.moduleStage.Namer.restoredModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.moduleStage.Namer.compiledModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.moduleStage.Namer.blockedModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.artifactCache.resolvedAnalysisFullBuild.hits"));
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
    public async Task BuildCommand_ProfileModuleArtifactsOnly_RestoresPersistedTypesPayloadsForTypedTarget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_types_payload_gate_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src", "basic"));
            var projectPath = Path.Combine(tempDir, "eidos.toml");
            await File.WriteAllTextAsync(projectPath, """
sourceRoots = ["src"]

[language]
version = "0.5.0-alpha.1"

[package]
name = "types-payload-gate"
version = "0.1.0"

[[targets]]
name = "literals"
entry = "src/basic/literals.eidos"
""");
            var mainPath = Path.Combine(tempDir, "src", "basic", "literals.eidos");
            await File.WriteAllTextAsync(mainPath, """
int_val :: 42;
float_val :: 3.14;
string_val :: "hello";
bool_val :: true;
char_val :: 'a';
""");
            var firstProfile = Path.Combine(tempDir, "first.json");
            var secondProfile = Path.Combine(tempDir, "second.json");

            var firstExit = await ExecuteBuildAsync(new BuildCommand.BuildOptions
            {
                Source = mainPath,
                Project = projectPath,
                Target = CompileTarget.Typed,
                BuildMode = BuildMode.Dev,
                ProfileJson = firstProfile,
                NoImplicitPrelude = true,
                NoColor = true
            });
            var secondExit = await ExecuteBuildAsync(new BuildCommand.BuildOptions
            {
                Source = mainPath,
                Project = projectPath,
                Target = CompileTarget.Typed,
                BuildMode = BuildMode.Dev,
                ProfileJson = secondProfile,
                ProfileModuleArtifactsOnly = true,
                NoImplicitPrelude = true,
                NoColor = true
            });

            Assert.Equal(0, firstExit);
            Assert.Equal(0, secondExit);
            var counters = ReadProfileCounters(secondProfile);
            Assert.Equal(0, counters.GetValueOrDefault("Build.moduleInvalidation.changes"));
            Assert.True(
                counters.GetValueOrDefault("Types.moduleRestore.applied") == 1,
                FormatCounters(counters));
            Assert.Equal(0, counters.GetValueOrDefault("Types.moduleRestore.fallbackFullInfer"));
            Assert.Equal(1, counters.GetValueOrDefault("Build.moduleStage.Types.realTaskExecution"));
            Assert.Equal(1, counters.GetValueOrDefault("Build.moduleStage.Types.restoredModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.moduleStage.Types.compiledModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.moduleStage.Types.blockedModules"));
            Assert.Equal(1, counters.GetValueOrDefault("Build.moduleTypedArtifactRestorePayload.validatedModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.artifactCache.typedAnalysisFullBuild.hits"));
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
    public async Task BuildCommand_ProfileModuleArtifactsOnly_RestoresPersistedMirPayloadsForMirTarget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_mir_payload_gate_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            var projectPath = Path.Combine(tempDir, "eidos.toml");
            await File.WriteAllTextAsync(projectPath, """
sourceRoots = ["src"]

[language]
version = "0.5.0-alpha.1"

[package]
name = "mir-payload-gate"
version = "0.1.0"

[[targets]]
name = "main"
entry = "src/Main.eidos"
""");
            var mainPath = Path.Combine(tempDir, "src", "Main.eidos");
            await File.WriteAllTextAsync(mainPath, """
Main :: module {
    Box :: type { Box(Int) }

    main :: Int -> Int
    {
        value => value
    }
}
""");
            var firstProfile = Path.Combine(tempDir, "first.json");
            var secondProfile = Path.Combine(tempDir, "second.json");

            var firstExit = await ExecuteBuildAsync(new BuildCommand.BuildOptions
            {
                Source = mainPath,
                Project = projectPath,
                Target = CompileTarget.Mir,
                BuildMode = BuildMode.Dev,
                ProfileJson = firstProfile,
                NoImplicitPrelude = true,
                NoColor = true
            });
            DeleteStatePayloadArtifacts(
                Path.Combine(tempDir, "build", ".eidos-cache"),
                "module-namer-state",
                "module-types-state",
                "module-hir-state");
            var secondExit = await ExecuteBuildAsync(new BuildCommand.BuildOptions
            {
                Source = mainPath,
                Project = projectPath,
                Target = CompileTarget.Mir,
                BuildMode = BuildMode.Dev,
                ProfileJson = secondProfile,
                ProfileModuleArtifactsOnly = true,
                NoImplicitPrelude = true,
                NoColor = true
            });

            Assert.Equal(0, firstExit);
            Assert.Equal(0, secondExit);
            var counters = ReadProfileCounters(secondProfile);
            Assert.Equal(0, counters.GetValueOrDefault("Build.moduleInvalidation.changes"));
            Assert.Equal(1, counters.GetValueOrDefault("Mir.moduleRestore.applied"));
            Assert.Equal(0, counters.GetValueOrDefault("Mir.moduleRestore.fallbackBuildMir"));
            Assert.Equal(1, counters.GetValueOrDefault("Build.moduleStage.Mir.realTaskExecution"));
            Assert.Equal(1, counters.GetValueOrDefault("Build.moduleStage.Mir.restoredModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.moduleStage.Mir.compiledModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.moduleStage.Mir.blockedModules"));
            Assert.Equal(1, counters.GetValueOrDefault("Build.moduleMirStateRestorePayload.validatedModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Mir.build_mir.calls"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.artifactCache.mirAnalysisFullBuild.hits"));
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
    public async Task BuildCommand_ProfileModuleArtifactsOnly_RestoresPersistedMirPayloadsForLlvmIrTarget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_mir_payload_llvm_gate_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            var projectPath = Path.Combine(tempDir, "eidos.toml");
            await File.WriteAllTextAsync(projectPath, """
sourceRoots = ["src"]

[language]
version = "0.5.0-alpha.1"

[package]
name = "mir-payload-llvm-gate"
version = "0.1.0"

[[targets]]
name = "main"
entry = "src/Main.eidos"
""");
            var mainPath = Path.Combine(tempDir, "src", "Main.eidos");
            await File.WriteAllTextAsync(mainPath, """
Main :: module {
    Box :: type { Box(Int) }

    main :: Int -> Int
    {
        value => value
    }
}
""");
            var firstProfile = Path.Combine(tempDir, "first.json");
            var secondProfile = Path.Combine(tempDir, "second.json");
            var firstOutput = Path.Combine(tempDir, "first.ll");
            var secondOutput = Path.Combine(tempDir, "second.ll");

            var firstExit = await ExecuteBuildAsync(new BuildCommand.BuildOptions
            {
                Source = mainPath,
                Project = projectPath,
                Target = CompileTarget.LlvmIr,
                BuildMode = BuildMode.Dev,
                Output = firstOutput,
                ProfileJson = firstProfile,
                NoImplicitPrelude = true,
                NoColor = true
            });
            DeleteStatePayloadArtifacts(
                Path.Combine(tempDir, "build", ".eidos-cache"),
                "module-namer-state",
                "module-types-state",
                "module-hir-state");
            var secondExit = await ExecuteBuildAsync(new BuildCommand.BuildOptions
            {
                Source = mainPath,
                Project = projectPath,
                Target = CompileTarget.LlvmIr,
                BuildMode = BuildMode.Dev,
                Output = secondOutput,
                ProfileJson = secondProfile,
                ProfileModuleArtifactsOnly = true,
                NoImplicitPrelude = true,
                NoColor = true
            });

            Assert.Equal(0, firstExit);
            Assert.Equal(0, secondExit);
            var restoredIr = await File.ReadAllTextAsync(secondOutput);
            Assert.Contains("define", restoredIr, StringComparison.Ordinal);
            Assert.DoesNotContain("declare external", restoredIr, StringComparison.Ordinal);
            var counters = ReadProfileCounters(secondProfile);
            Assert.Equal(1, counters.GetValueOrDefault("Mir.moduleRestore.applied"));
            Assert.Equal(1, counters.GetValueOrDefault("Build.moduleStage.Mir.realTaskExecution"));
            Assert.Equal(1, counters.GetValueOrDefault("Build.moduleStage.Mir.restoredModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.moduleStage.Mir.compiledModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.moduleStage.Mir.blockedModules"));
            Assert.Equal(0, counters.GetValueOrDefault("Mir.build.output.functions"));
            Assert.Equal(0, counters.GetValueOrDefault("Build.artifactCache.llvmIrFullBuild.hits"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static ProjectModuleArtifactRestorePlan CreateSingleModuleRestorePlan(
        bool SemanticReady,
        bool TypedSemanticReady,
        bool MirReady)
    {
        return new ProjectModuleArtifactRestorePlan(
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
    }

    private static ModuleArtifactKey CreateArtifactKey() =>
        new()
        {
            CacheSchema = "test",
            ModuleKey = "main",
            SourceHash = "source",
            LanguageVersion = "syntax",
            DependencySignatureHash = "deps",
            TargetTriple = "target",
            FlagsHash = "flags"
        };

    private static async Task<int> ExecuteBuildAsync(BuildCommand.BuildOptions options)
    {
        var method = typeof(BuildCommand).GetMethod(
            "Execute",
            BindingFlags.Static | BindingFlags.NonPublic);
        var task = Assert.IsAssignableFrom<Task<int>>(method!.Invoke(null, [options]));
        return await task;
    }

    private static IReadOnlyDictionary<string, long> ReadProfileCounters(string profilePath)
    {
        using var stream = File.OpenRead(profilePath);
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var counter in document.RootElement.GetProperty("Counters").EnumerateArray())
        {
            result[counter.GetProperty("Name").GetString() ?? ""] = counter.GetProperty("Value").GetInt64();
        }

        return result;
    }

    private static string FormatCounters(IReadOnlyDictionary<string, long> counters) =>
        string.Join(
            Environment.NewLine,
            counters
                .Where(static counter => counter.Key.Contains("module", StringComparison.OrdinalIgnoreCase) ||
                                         counter.Key.Contains("Namer", StringComparison.OrdinalIgnoreCase) ||
                                         counter.Key.Contains("Types", StringComparison.OrdinalIgnoreCase) ||
                                         counter.Key.Contains("Hir", StringComparison.OrdinalIgnoreCase) ||
                                         counter.Key.Contains("Mir", StringComparison.OrdinalIgnoreCase) ||
                                         counter.Key.Contains("artifact", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static counter => counter.Key, StringComparer.Ordinal)
                .Select(static counter => $"{counter.Key}={counter.Value}"));

    private static void DeleteStatePayloadArtifacts(string cacheDir, params string[] nameFragments)
    {
        if (!Directory.Exists(cacheDir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories))
        {
            if (nameFragments.Any(fragment => Path.GetFileName(file).Contains(fragment, StringComparison.Ordinal)))
            {
                File.Delete(file);
            }
        }
    }

    private static void StoreModuleArtifactNode<T>(
        ModuleArtifactCache cache,
        BuildCommand.FullBuildArtifact artifact,
        string moduleKey,
        string kind,
        string sourceHash,
        string dependencySignatureHash,
        T node)
    {
        var key = artifact.Key with
        {
            CacheSchema = "module-artifact-readiness-v1",
            ModuleKey = moduleKey,
            SourceHash = sourceHash,
            DependencySignatureHash = dependencySignatureHash,
            FlagsHash = artifact.OutputIndependentPayloadKey.FlagsHash
        };
        cache.StoreArtifact(key, kind, ".json", System.Text.Json.JsonSerializer.Serialize(node));
    }
}
