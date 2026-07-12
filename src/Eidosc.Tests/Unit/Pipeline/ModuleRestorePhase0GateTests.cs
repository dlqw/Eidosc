using Eidosc.Pipeline;
using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ModuleRestorePhase0GateTests
{
    [Fact]
    public void Run_WithRestorePlanAudit_DoesNotCountStageRestoreAsRealExecution()
    {
        var source = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunToNamer(source, previousSemantic: null, artifactAvailability: null);
        var previousSemantic = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(
            first.ModuleSemanticSignatureSnapshot);
        var second = RunToNamer(
            source,
            previousSemantic,
            static (_, _, _, _) => true);

        Assert.True(second.Success, FormatDiagnostics(second));
        var auditExecution = Assert.IsType<ProjectModuleArtifactRestoreExecutionSnapshot>(
            second.ModuleArtifactRestoreExecution);
        Assert.False(auditExecution.HasRealTaskExecution);
        Assert.Equal(1, auditExecution.RestoredModules);
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleArtifactRestoreExecution.realTaskExecution"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.realTaskExecution"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.restoredModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.compiledModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.blockedModules"));
    }

    [Fact]
    public void Run_WithValidatedSemanticArtifactPayload_CountsRealNamerStageRestore()
    {
        var source = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunToNamer(source, previousSemantic: null, artifactAvailability: null);
        var previousSemantic = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(
            first.ModuleSemanticSignatureSnapshot);
        var semanticByModule = previousSemantic.Nodes.ToDictionary(
            static node => node.ModuleKey,
            StringComparer.Ordinal);

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "phase0_namer_gate.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousModuleSemanticSignatureSnapshot = previousSemantic,
            ModuleArtifactAvailability = static (_, _, _, _) => true,
            ModuleSemanticArtifactLoader = (moduleKey, _, _, _) =>
                semanticByModule.GetValueOrDefault(moduleKey)
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        var execution = Assert.IsType<ProjectModuleArtifactRestoreExecutionSnapshot>(
            second.ModuleArtifactRestoreExecution);
        Assert.True(execution.HasRealTaskExecution);
        Assert.Equal(1, execution.RestoredModules);
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleArtifactRestoreExecution.realTaskExecution"));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.realTaskExecution"));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.restoredModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.compiledModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.blockedModules"));
    }

    private static CompilationResult RunToNamer(
        string source,
        ProjectModuleSemanticSignatureSnapshot? previousSemantic,
        Func<string, string, string, string, bool>? artifactAvailability)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "phase0_namer_gate.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false,
            PreviousModuleSemanticSignatureSnapshot = previousSemantic,
            ModuleArtifactAvailability = artifactAvailability
        }).Run();
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }
}
