using Eidosc.Diagnostic;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class FunctionBodyStyleDiagnosticTests
{
    [Fact]
    public void CompilationPipeline_FunctionBranchMatchingSameParameter_ReportsRedundantMatchWarning()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }

unwrap :: Option[Int] -> Int
{
    value => match value
    {
        Some(inner) => inner,
        None() => 0
    }
}
""";

        var result = RunNamer(source, "redundant_function_body_match.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4300");
        Assert.Equal(DiagnosticLevel.Warning, warning.Level);
        Assert.Equal(
            "Redundant match over function parameter; use function body pattern branches directly.",
            warning.Message);
    }

    [Fact]
    public void CompilationPipeline_FunctionBranchMatchingTupleParameters_ReportsRedundantMatchWarning()
    {
        const string source = """
Pair :: type { Pair:: type(Int, Int) }

same :: Pair -> Pair -> Bool
{
    left => right => match (left, right)
    {
        (Pair(a, b), Pair(c, d)) => a == c && b == d
    }
}
""";

        var result = RunNamer(source, "redundant_tuple_function_body_match.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4300");
    }

    [Fact]
    public void CompilationPipeline_FunctionBranchMatchingDerivedExpression_DoesNotReportRedundantMatchWarning()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }

unwrap :: Option[Int] -> Int
{
    value => match Some(value)
    {
        Some(inner) => 1,
        None() => 0
    }
}
""";

        var result = RunNamer(source, "non_redundant_function_body_match.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4300");
    }

    [Fact]
    public void CompilationPipeline_CurriedBranchReturningCall_DoesNotReportRedundantMatchWarning()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }

fold_left[A, B] :: Option[A] -> B -> (B -> A -> B) -> B
{
    Some(value) => acc => f => f(acc)(value),
    None() => acc => _ => acc
}

eq :: Option[Int] -> Option[Int] -> Bool
{
    left => right => match (left, right)
    {
        (Some(a), Some(b)) => a == b,
        (None(), None()) => true,
        _ => false
    }
}
""";

        var result = RunNamer(source, "curried_branch_call_no_redundant_match.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4300");
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
