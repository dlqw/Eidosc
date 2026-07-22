using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class DecisionTableExpressionTests
{
    [Fact]
    public void CompilationPipeline_DecisionTable_CompilesThroughMir()
    {
        const string source = """
is_even :: Int -> Bool
{
    value => value % 2 == 0
}

choose :: Int -> Int
{
    fallback => decide fallback {
        is_even(_):
            2 | 4 => 20,
            6 when fallback > 0 => 60
    }
}
""";

        var result = Run(source, CompilationPhase.Mir);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
        Assert.NotNull(result.MirModule);
    }

    [Fact]
    public void CompilationPipeline_DecisionTable_PropagatesTemplateEffectRequirement()
    {
        const string source = """
Poll :: effect;

poll :: Int -> Bool need Poll
{
    value => value > 0
}

choose :: Int -> Int
{
    fallback => decide fallback {
        poll(_):
            1 => 10
    }
}
""";

        var result = Run(source, CompilationPhase.Effects);

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3003");
    }

    [Fact]
    public void CompilationPipeline_DecisionTable_AcceptsAuthorizedTemplateEffect()
    {
        const string source = """
Poll :: effect;

poll :: Int -> Bool need Poll
{
    value => value > 0
}

choose :: Int -> Int need Poll
{
    fallback => decide fallback {
        poll(_):
            1 => 10
    }
}
""";

        var result = Run(source, CompilationPhase.Effects);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.Equal(CompilationPhase.Effects, result.CompletedPhase);
    }

    [Fact]
    public void CompilationPipeline_DecisionTable_RejectsNonBooleanTemplate()
    {
        const string source = """
identity :: Int -> Int
{
    value => value
}

choose :: Int -> Int
{
    fallback => decide fallback {
        identity(_):
            1 => 10
    }
}
""";

        var result = Run(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Bool", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_DecisionTable_RejectsResultTypeMismatch()
    {
        const string source = """
is_one :: Int -> Bool
{
    value => value == 1
}

choose :: Int -> Int
{
    fallback => decide fallback {
        is_one(_):
            1 => true
    }
}
""";

        var result = Run(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Bool", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("Int", StringComparison.Ordinal));
    }

    private static CompilationResult Run(string source, CompilationPhase phase)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "decision_table_expression.eidos",
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = phase,
            UseColors = false
        }).Run();
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
