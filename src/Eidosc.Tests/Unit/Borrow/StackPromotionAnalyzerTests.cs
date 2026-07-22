using Eidosc.Diagnostic;
using Eidosc;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public class StackPromotionAnalyzerTests
{
    [Fact]
    public void NonEscapingConstructor_GetsPromoted()
    {
        const string source = """
Pair :: type { MkPair:: type(Int, Int) }

main :: Int -> Int {
    _ => {
        p := MkPair(1, 2);
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void EscapingViaReturn_NotPromoted()
    {
        const string source = """
Pair :: type { MkPair:: type(Int, Int) }

make :: Int -> Pair {
    x => MkPair(x, x)
}

main :: Int -> Int {
    _ => {
        p := make(1);
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void EscapingViaCallArg_NotPromoted()
    {
        const string source = """
Pair :: type { MkPair:: type(Int, Int) }

consume :: Pair -> Int {
    _ => 0
}

main :: Int -> Int {
    _ => {
        p := MkPair(3, 4);
        r := consume(p);
        r
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void ManagedFieldConstructor_NotPromoted()
    {
        const string source = """
Holder :: type { MkHolder:: type(String) }

main :: Int -> Int {
    _ => {
        h := MkHolder("test");
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void MultipleNonEscapingConstructors_BothPromoted()
    {
        const string source = """
Pair :: type { MkPair:: type(Int, Int) }

main :: Int -> Int {
    _ => {
        a := MkPair(1, 2);
        b := MkPair(3, 4);
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    private static CompilationResult RunPipeline(string source)
    {
        var options = new CompilationOptions
        {
            InputFile = "stack_promo_test.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }
}
