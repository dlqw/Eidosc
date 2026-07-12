using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ModuleArtifactRestoreProfileGateTests
{
    [Fact]
    public void Run_WithPreviousSemanticSignature_EmitsUnchangedInvalidationPlan()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_invalidation_profile_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var entryFile = Path.Combine(tempDir, "Main.eidos");
            File.WriteAllText(entryFile, """
Main :: module {
    export main :: Unit -> Unit { _ => () }
}
""");

            var first = RunFileWithPreviousSnapshot(entryFile, previous: null);
            var previous = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(first.ModuleSemanticSignatureSnapshot);
            var second = RunFileWithPreviousSnapshot(entryFile, previous);

            Assert.True(second.Success, FormatDiagnostics(second));
            var invalidation = Assert.IsType<ProjectModuleInvalidationPlan>(second.ModuleInvalidationPlan);
            Assert.Empty(invalidation.Changes);
            Assert.Empty(invalidation.AffectedModules);
            Assert.Contains("Main", invalidation.UnchangedModules);
            Assert.True(second.ProfilingCounters.TryGetValue("Build.moduleInvalidation.affected", out var affected), FormatCounters(second));
            Assert.Equal(0, affected);
            Assert.True(second.ProfilingCounters.TryGetValue("Build.moduleInvalidation.unchanged", out var unchanged), FormatCounters(second));
            Assert.True(unchanged >= 1, FormatCounters(second));
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
    public void Run_WithPreviousTypedSemanticSignature_EmitsUnchangedTypedInvalidationPlan()
    {
        var source = """
Main :: module {
    Box :: type { Box(Int) }

    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunSourceToTypes(source, previousTyped: null);
        var previousTyped = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(first.ModuleTypedSemanticSnapshot);
        var second = RunSourceToTypes(source, previousTyped);

        Assert.True(second.Success, FormatDiagnostics(second));
        var invalidation = Assert.IsType<ProjectModuleInvalidationPlan>(second.ModuleTypedInvalidationPlan);
        Assert.Empty(invalidation.Changes);
        Assert.Empty(invalidation.AffectedModules);
        Assert.Single(invalidation.UnchangedModules);
        Assert.True(second.ProfilingCounters.TryGetValue("Build.moduleTypedInvalidation.affected", out var affected), FormatCounters(second));
        Assert.Equal(0, affected);
        Assert.True(second.ProfilingCounters.TryGetValue("Build.moduleTypedInvalidation.unchanged", out var unchanged), FormatCounters(second));
        Assert.True(unchanged >= 1, FormatCounters(second));
    }

    [Fact]
    public void Run_WithArtifactAvailability_EmitsTypedArtifactRestorePlan()
    {
        var source = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunSourceToLlvm(source, previousTyped: null, artifactAvailability: null);
        var previousTyped = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(first.ModuleTypedSemanticSnapshot);
        var second = RunSourceToLlvm(
            source,
            previousTyped,
            static (_, _, _, _) => true);

        Assert.True(second.Success, FormatDiagnostics(second));
        var restore = Assert.IsType<ProjectModuleArtifactRestorePlan>(second.ModuleTypedArtifactRestorePlan);
        var restoreExecution = Assert.IsType<ProjectModuleArtifactRestoreExecutionSnapshot>(
            second.ModuleTypedArtifactRestoreExecution);
        Assert.Equal(1, restore.RestoreModules);
        Assert.Equal(0, restore.BlockedModules);
        Assert.Equal(1, restoreExecution.RestoredModules);
        Assert.Equal(0, restoreExecution.BlockedModules);
        Assert.True(restoreExecution.HasRealTaskExecution);
        Assert.True(second.ProfilingCounters.TryGetValue(
            "Build.moduleTypedArtifactRestore.restoreModules",
            out var restoreModules), FormatCounters(second));
        Assert.Equal(1, restoreModules);
        Assert.True(second.ProfilingCounters.TryGetValue(
            "Build.moduleTypedArtifactRestoreExecution.restoredModules",
            out var restoredModules), FormatCounters(second));
        Assert.Equal(1, restoredModules);

        var snapshot = CompilationProfilingFormatter.CreateSnapshot(second);
        Assert.NotNull(snapshot.ModuleTypedArtifactRestore);
        Assert.NotNull(snapshot.ModuleTypedArtifactRestoreExecution);
        Assert.Equal(restore.RestoreModules, snapshot.ModuleTypedArtifactRestore!.RestoreModules);
        Assert.Equal(restoreExecution.RestoredModules, snapshot.ModuleTypedArtifactRestoreExecution!.RestoredModules);
    }

    [Fact]
    public void Run_WithSemanticArtifactLoader_DegradesStaleRestorePayloadToCompile()
    {
        var source = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "semantic_restore_plan_profile.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();
        var semantic = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(
            first.ModuleSemanticSignatureSnapshot);
        var semanticByModule = semantic.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "semantic_restore_plan_profile.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousModuleSemanticSignatureSnapshot = semantic,
            ModuleArtifactAvailability = static (_, _, _, _) => true,
            ModuleSemanticArtifactLoader = (moduleKey, _, _, _) =>
                semanticByModule.TryGetValue(moduleKey, out var node)
                    ? node with { ExportSurfaceHash = $"{node.ExportSurfaceHash}-stale" }
                    : null
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        var payload = Assert.IsType<ProjectModuleArtifactRestorePayloadSnapshot>(
            second.ModuleArtifactRestorePayload);
        var restore = Assert.IsType<ProjectModuleArtifactRestorePlan>(second.ModuleArtifactRestorePlan);
        var restoreExecution = Assert.IsType<ProjectModuleArtifactRestoreExecutionSnapshot>(
            second.ModuleArtifactRestoreExecution);
        Assert.Equal(1, payload.LoadedModules);
        Assert.Equal(0, payload.ValidatedModules);
        Assert.Equal(1, payload.StaleModules);
        Assert.Equal(0, restore.RestoreModules);
        Assert.Equal(1, restore.CompileModules);
        Assert.Equal(0, restore.BlockedModules);
        Assert.Equal(0, restoreExecution.RestoredModules);
        Assert.Equal(1, restoreExecution.CompiledModules);
        Assert.Equal(0, restoreExecution.BlockedModules);
        Assert.True(restoreExecution.HasRealTaskExecution);
        Assert.Equal(0, restoreExecution.SkippedModules);
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleArtifactRestorePayload.staleModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleArtifactRestoreExecution.restoredModules"));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleArtifactRestoreExecution.compiledModules"));
        Assert.NotNull(CompilationProfilingFormatter.CreateSnapshot(second).ModuleArtifactRestorePayload);
    }

    [Fact]
    public void Run_WithArtifactLoaders_LoadsTypedArtifactRestorePayload()
    {
        var source = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunSourceToLlvm(source, previousTyped: null, artifactAvailability: null);
        var semantic = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(first.ModuleSemanticSignatureSnapshot);
        var typed = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(first.ModuleTypedSemanticSnapshot);
        var mir = Assert.IsType<ProjectModuleMirArtifactSnapshot>(first.ModuleMirArtifactSnapshot);
        var semanticByModule = semantic.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var typedByModule = typed.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var mirByModule = mir.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "typed_restore_plan_profile.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousModuleTypedSemanticSnapshot = typed,
            ModuleArtifactAvailability = static (_, _, _, _) => true,
            ModuleSemanticArtifactLoader = (moduleKey, _, _, _) => semanticByModule.GetValueOrDefault(moduleKey),
            ModuleTypedSemanticArtifactLoader = (moduleKey, _, _, _) => typedByModule.GetValueOrDefault(moduleKey),
            ModuleMirArtifactLoader = (moduleKey, _, _, _) => mirByModule.GetValueOrDefault(moduleKey)
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        var payload = Assert.IsType<ProjectModuleArtifactRestorePayloadSnapshot>(
            second.ModuleTypedArtifactRestorePayload);
        var restoreExecution = Assert.IsType<ProjectModuleArtifactRestoreExecutionSnapshot>(
            second.ModuleTypedArtifactRestoreExecution);
        Assert.Equal(1, payload.RestoreModules);
        Assert.Equal(1, payload.LoadedModules);
        Assert.Equal(1, payload.ValidatedModules);
        Assert.Equal(0, payload.StaleModules);
        Assert.Equal(0, payload.MissingModules);
        Assert.True(restoreExecution.HasRealTaskExecution);
        Assert.Equal(1, restoreExecution.RestoredModules);
        Assert.True(second.ProfilingCounters.TryGetValue(
            "Build.moduleTypedArtifactRestorePayload.loadedModules",
            out var loadedModules), FormatCounters(second));
        Assert.Equal(1, loadedModules);
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleTypedArtifactRestorePayload.validatedModules"));
        Assert.NotNull(CompilationProfilingFormatter.CreateSnapshot(second).ModuleTypedArtifactRestorePayload);
    }

    [Fact]
    public void Run_WithArtifactLoaders_DegradesStaleTypedArtifactRestorePayloadToCompile()
    {
        var source = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunSourceToLlvm(source, previousTyped: null, artifactAvailability: null);
        var semantic = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(first.ModuleSemanticSignatureSnapshot);
        var typed = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(first.ModuleTypedSemanticSnapshot);
        var mir = Assert.IsType<ProjectModuleMirArtifactSnapshot>(first.ModuleMirArtifactSnapshot);
        var semanticByModule = semantic.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var typedByModule = typed.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var mirByModule = mir.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "typed_restore_plan_profile.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousModuleTypedSemanticSnapshot = typed,
            ModuleArtifactAvailability = static (_, _, _, _) => true,
            ModuleSemanticArtifactLoader = (moduleKey, _, _, _) => semanticByModule.GetValueOrDefault(moduleKey),
            ModuleTypedSemanticArtifactLoader = (moduleKey, _, _, _) =>
                typedByModule.TryGetValue(moduleKey, out var node)
                    ? node with { LocalSurfaceHash = $"{node.LocalSurfaceHash}-stale" }
                    : null,
            ModuleMirArtifactLoader = (moduleKey, _, _, _) => mirByModule.GetValueOrDefault(moduleKey)
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        var payload = Assert.IsType<ProjectModuleArtifactRestorePayloadSnapshot>(
            second.ModuleTypedArtifactRestorePayload);
        Assert.Equal(1, payload.LoadedModules);
        Assert.Equal(0, payload.ValidatedModules);
        Assert.Equal(1, payload.StaleModules);
        var restore = Assert.IsType<ProjectModuleArtifactRestorePlan>(second.ModuleTypedArtifactRestorePlan);
        var restoreExecution = Assert.IsType<ProjectModuleArtifactRestoreExecutionSnapshot>(
            second.ModuleTypedArtifactRestoreExecution);
        Assert.Equal(0, restore.RestoreModules);
        Assert.Equal(1, restore.CompileModules);
        Assert.Equal(0, restore.BlockedModules);
        Assert.Equal(0, restoreExecution.RestoredModules);
        Assert.Equal(1, restoreExecution.CompiledModules);
        Assert.Equal(0, restoreExecution.BlockedModules);
        Assert.True(restoreExecution.HasRealTaskExecution);
        Assert.Equal(0, restoreExecution.SkippedModules);
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleTypedArtifactRestorePayload.staleModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleTypedArtifactRestoreExecution.restoredModules"));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleTypedArtifactRestoreExecution.compiledModules"));
    }

    [Fact]
    public void Run_WithArtifactLoaders_CompilesWhenDependencySignatureDrifts()
    {
        var source = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunSourceToLlvm(source, previousTyped: null, artifactAvailability: null);
        var semantic = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(first.ModuleSemanticSignatureSnapshot);
        var typed = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(first.ModuleTypedSemanticSnapshot);
        var mir = Assert.IsType<ProjectModuleMirArtifactSnapshot>(first.ModuleMirArtifactSnapshot);
        var previousDependency = Assert.IsType<ProjectModuleDependencySignatureSnapshot>(
            first.ModuleDependencySignatureSnapshot);
        var semanticByModule = semantic.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var typedByModule = typed.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var mirByModule = mir.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var staleDependency = previousDependency with
        {
            Nodes = previousDependency.Nodes
                .Select(static node => node with { MirDependencySignatureHash = $"{node.MirDependencySignatureHash}-stale" })
                .ToArray()
        };

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "typed_restore_plan_profile.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousModuleTypedSemanticSnapshot = typed,
            PreviousModuleDependencySignatureSnapshot = staleDependency,
            ModuleArtifactAvailability = static (_, _, _, _) => true,
            ModuleSemanticArtifactLoader = (moduleKey, _, _, _) => semanticByModule.GetValueOrDefault(moduleKey),
            ModuleTypedSemanticArtifactLoader = (moduleKey, _, _, _) => typedByModule.GetValueOrDefault(moduleKey),
            ModuleMirArtifactLoader = (moduleKey, _, _, _) => mirByModule.GetValueOrDefault(moduleKey)
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        var payload = Assert.IsType<ProjectModuleArtifactRestorePayloadSnapshot>(
            second.ModuleTypedArtifactRestorePayload);
        var restore = Assert.IsType<ProjectModuleArtifactRestorePlan>(second.ModuleTypedArtifactRestorePlan);
        var restoreExecution = Assert.IsType<ProjectModuleArtifactRestoreExecutionSnapshot>(
            second.ModuleTypedArtifactRestoreExecution);
        Assert.Equal(0, payload.ValidatedModules);
        Assert.Equal(0, restore.RestoreModules);
        Assert.Equal(1, restore.CompileModules);
        Assert.Equal(0, restore.BlockedModules);
        Assert.Equal(0, restoreExecution.RestoredModules);
        Assert.Equal(1, restoreExecution.CompiledModules);
        Assert.Equal(0, restoreExecution.BlockedModules);
        Assert.True(restoreExecution.HasRealTaskExecution);
        Assert.Equal(0, restoreExecution.SkippedModules);
    }

    private static CompilationResult RunFileWithPreviousSnapshot(
        string inputFile,
        ProjectModuleSemanticSignatureSnapshot? previous)
    {
        return new CompilationPipeline(File.ReadAllText(inputFile), new CompilationOptions
        {
            InputFile = inputFile,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousModuleSemanticSignatureSnapshot = previous
        }).Run();
    }

    private static CompilationResult RunSourceToTypes(
        string source,
        ProjectModuleTypedSemanticSnapshot? previousTyped)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "typed_invalidation_profile.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousModuleTypedSemanticSnapshot = previousTyped
        }).Run();
    }

    private static CompilationResult RunSourceToLlvm(
        string source,
        ProjectModuleTypedSemanticSnapshot? previousTyped,
        Func<string, string, string, string, bool>? artifactAvailability)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "typed_restore_plan_profile.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Llvm,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousModuleTypedSemanticSnapshot = previousTyped,
            ModuleArtifactAvailability = artifactAvailability
        }).Run();
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
