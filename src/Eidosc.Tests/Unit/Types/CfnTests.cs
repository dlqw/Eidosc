using System.Linq;
using Eidosc.Diagnostic;
using Eidosc;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class CfnTests
{
    [Fact]
    public void CfnType_DeclaredAsGeneric_ResolvesCorrectly()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        fn_ptr: Cfn[Int, Int] := ptr_null();
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnFrom_ZeroCaptureFunction_Compiles()
    {
        const string source = """
my_func :: Int -> Int {
    x => x * 2
}

main :: Int -> Int {
    _ => {
        fn_ptr := cfn_from(my_func);
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnCall_WithTypedCfnArg_Compiles()
    {
        const string source = """
main :: Int -> Int {
    _ => {
        fn_ptr: Cfn[Int, Int] := ptr_null();
        result := cfn_call(fn_ptr, 42);
        result
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnCall_WithNonCfnArg_Fails()
    {
        const string source = """
main :: Int -> Int {
    _ => {
        not_fn := 1;
        result := cfn_call(not_fn, 42);
        result
    }
}
""";

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Message.Contains("first argument is not a Cfn", StringComparison.Ordinal));
    }

    [Fact]
    public void CfnCall_WithNonCfnArg_DoesNotExposeIntFallbackType()
    {
        const string source = """
main :: Int -> Int {
    _ => {
        not_fn := 1;
        result := cfn_call(not_fn, 42);
        result
    }
}
""";

        var result = RunPipeline(source);
        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var resultSymbol = Assert.Single(snapshot.Symbols, symbol => symbol.Name == "result");

        Assert.False(result.Success);
        Assert.Null(resultSymbol.TypeText);
        Assert.Null(resultSymbol.TypeConfidence);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Message.Contains("first argument is not a Cfn", StringComparison.Ordinal));
    }

    [Fact]
    public void CfnFromAndCall_EndToEnd_Compiles()
    {
        const string source = """
add :: Int -> Int -> Int {
    a => b => a + b
}

main :: Int -> Int {
    _ => {
        fn_ptr := cfn_from(add);
        result := cfn_call(fn_ptr, 3, 4);
        result
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnFrom_CapturingClosure_ReportsE3053BeforeNative()
    {
        const string source = """
main :: Int -> Int need ffi {
    captured => {
        closure := x => x + captured;
        fn_ptr := cfn_from(closure);
        0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E3053");
    }

    private static CompilationResult RunPipeline(
        string source,
        CompilationPhase stopAtPhase = CompilationPhase.Types)
    {
        var options = new CompilationOptions
        {
            InputFile = "cfn_test.eidos",
            StopAtPhase = stopAtPhase,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }
}
