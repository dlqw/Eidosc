using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class DeclarationTypeAnnotationTests
{
    [Fact]
    public void LocalLetDeclaration_WithMismatchedTypeAnnotation_ReportsTypeError()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        value: String := 1;
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Types, result.CompletedPhase);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("String", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("Int", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalMutableLetDeclaration_WithMismatchedTypeAnnotation_ReportsTypeError()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        mut value: String := 1;
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Types, result.CompletedPhase);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("String", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("Int", StringComparison.Ordinal));
    }

    private static CompilationResult RunPipeline(string source)
    {
        return new CompilationPipeline(
            source,
            new CompilationOptions
            {
                InputFile = "declaration_type_annotation_tests.eidos",
                StopAtPhase = CompilationPhase.Types,
                UseColors = false
            }).Run();
    }
}
