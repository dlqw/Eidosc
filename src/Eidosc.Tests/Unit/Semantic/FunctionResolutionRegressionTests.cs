using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    private static readonly TestPathConfig Paths = TestPathConfig.Current;

    [Fact]
    public void CompilationPipeline_CtorExprArgumentsInsidePatternBranch_AreResolved()
    {
        const string source = """
S3 :: type {
    S3:: type(Int, Int, Int)
}

roundTrip :: S3 -> S3
{
    S3(a, b, c) => S3(a, b, c)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ctor_expr_args_resolved.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Undefined identifier"));
    }

    [Fact]
    public void CompilationPipeline_FunctionDefinitionArity_IsRegisteredForForwardCalls()
    {
        const string source = """
callForward :: Int -> Int
{
    x => target(x)
}

target :: Int -> Int
{
    x => x
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "func_arity_forward_call.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Function arity mismatch"));
    }

    [Fact]
    public void CompilationPipeline_StdTextFunctions_AreResolvedThroughExplicitModule()
    {
        const string source = """
import std.Text

main :: String -> Int
{
    src => Text.len(src) + Text.char_code_at(src)(0) + Text.len(Text.slice(src)(0)(1))
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "builtin_string_functions.eidos",
            AllowVirtualInputFile = true,
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false,
            PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [WellKnownStrings.Std.Module] = []
            }
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_PreludeDisplayPrintFunctions_AreResolvedWithoutDeclarations()
    {
        const string source = """
main :: String -> Unit need io
{
    src => { print(src); print(42) }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "builtin_print_functions.eidos",
            AllowVirtualInputFile = true,
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CompilationPipeline_MultipleWhenGuards_AreLoweredInOrder()
    {
        const string source = """
OptionInt :: type { SomeInt:: type(Int) , NoneInt :: type {} }

addIfPositive :: Int -> OptionInt -> Int
{
    base => opt when SomeInt(n) <- opt when n > 0 => base + n,
    _ => _ => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "multi_when_guard_lowering.eidos",
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Unsupported HIR expression", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Undefined identifier 'n'", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);
    }
}
