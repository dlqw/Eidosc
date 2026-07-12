using Eidosc.Diagnostic;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class PatternCoverageDiagnosticStabilityTests
{
    [Fact]
    public void CompilationPipeline_BoolNonExhaustiveWarning_UsesStableCodeAndWitnessNotes()
    {
        const string source = """
classify :: Bool -> Int
{
    true => 1
}
""";

        var result = RunNamer(source, "pattern_bool_non_exhaustive_stability.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Equal(DiagnosticLevel.Warning, warning.Level);
        Assert.Equal(
            "Non-exhaustive pattern matching in function 'classify'; missing bool cases: false",
            warning.Message);
        Assert.Contains("Missing-case witnesses: false", warning.Notes);
        Assert.Contains("Missing-case traces: false [bool:false]", warning.Notes);
        Assert.Contains("Missing-case trace groups: bool=bool:false", warning.Notes);
        Assert.Contains("Missing-case trace kv: kind=bool;key=bool:false;display=false", warning.Notes);
    }

    [Fact]
    public void CompilationPipeline_BoolDuplicateBranchWarning_UsesStableCodeAndCoveredWitnessNotes()
    {
        const string source = """
classify :: Bool -> Int
{
    true => 1,
    true => 2,
    false => 0
}
""";

        var result = RunNamer(source, "pattern_bool_duplicate_branch_stability.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
        Assert.Equal(DiagnosticLevel.Warning, warning.Level);
        Assert.Equal(
            "Unreachable pattern branch #2: all finite cases are already covered by previous branches (#1)",
            warning.Message);
        Assert.Contains("Covered-case witnesses: true", warning.Notes);
        Assert.Contains("Covered-case traces: true <- #1", warning.Notes);
        Assert.Contains(
            "Move this branch earlier or refine its pattern/guard to introduce new coverage.",
            warning.Notes);
    }

    private static CompilationResult RunNamer(string source, string inputFile)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message} :: {string.Join(" | ", diagnostic.Notes)}"));
    }
}
