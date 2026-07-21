using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void RuntimeArrayImportSource_LowersRuntimeArrayPrimitivesToLlvmCalls()
    {
        const string source = """
import std.RuntimeArray
import std.Seq

main :: Unit -> Int
{
    _ => {
        mut ys := RuntimeArray.push(RuntimeArray.with_capacity[Int](1))(41);
        ys := RuntimeArray.push(ys)(1);
        RuntimeArray.swap(mref ys, 0, 1);
        RuntimeArray.pop_last(mref ys);
        len := RuntimeArray.len(Seq.clone(ref ys));
        RuntimeArray.get(ys)(0) + len
    }
}
""";

        var result = RunSourceAtLlvm(source, StdlibListImportInputFile());

        Assert.True(
            result.Success,
            $"Completed={result.CompletedPhase}, Errors={result.ErrorCount}, Warnings={result.WarningCount}{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("@eidos_array_new", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@eidos_array_push", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@eidos_array_swap", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@eidos_array_pop", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@eidos_array_get", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@eidos_array_length", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void SeqBuilderPopLast_NativeSmoke_ShrinksWithoutCopyingPrefix()
    {
        const string source = """
import std.SeqBuilder

main :: Unit -> Int
{
    _ => {
        xs := SeqBuilder.push(SeqBuilder.push(SeqBuilder.with_capacity[Int](3))(10))(20);
        SeqBuilder.pop_last(xs);
        if SeqBuilder.len(xs) == 1 && SeqBuilder.get(xs)(0) == 10 then { 0 } else { 99 }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_std_array_pop_last.eidos",
            "native_std_array_pop_last");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void SeqBuilderSwap_NativeSmoke_SwapsCompositeSlotsWithoutClone()
    {
        const string source = """
import std.SeqBuilder

main :: Unit -> Int
{
    _ => {
        xs := SeqBuilder.push(SeqBuilder.push(SeqBuilder.with_capacity[(Int, Int)](2))((1, 10)))((2, 20));
        SeqBuilder.swap(xs)(0)(1);
        (a, b) := SeqBuilder.get(xs)(0);
        (c, d) := SeqBuilder.get(xs)(1);
        if a == 2 && b == 20 && c == 1 && d == 10 then { 0 } else { 99 }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_std_array_swap_tuple.eidos",
            "native_std_array_swap_tuple");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void SeqBuilderWithCapacity_SpecializesCompositeTypeArgumentInsideGenericFunction()
    {
        const string source = """
import std.SeqBuilder
import std.Seq

build_aa[A] :: Int -> SeqBuilder[(A, A)]
{
    n => SeqBuilder.with_capacity[(A, A)](n)
}

main :: Unit -> Int
{
    _ => {
        xs := build_aa[Int](4)
        builder := SeqBuilder.push(xs)((1, 2))
        ys := SeqBuilder.freeze(builder)
        Seq.len(ys)
    }
}
""";

        var result = RunSourceAtLlvm(source, StdlibListImportInputFile());

        Assert.True(
            result.Success,
            $"Completed={result.CompletedPhase}, Errors={result.ErrorCount}, Warnings={result.WarningCount}{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("@eidos_std__SeqBuilder__with_capacity__spec_", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call ptr @eidos_array_new_with_policy(i64 %capacity, i64 16", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("musttail call ptr @eidos_std__SeqBuilder__with_capacity(i64 %n)", llvmIr, StringComparison.Ordinal);
    }
}
