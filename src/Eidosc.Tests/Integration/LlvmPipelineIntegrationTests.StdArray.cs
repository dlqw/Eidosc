using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void SmallCopyRecord_UsesInlineValueAbiAcrossCallsAndNativeExecution()
    {
        const string source = """
@[derive(Copy)]
Point :: type { Point :: type(Int, Int) }

sum :: Point -> Int
{
    Point(x, y) => x + y
}

main :: Unit -> Int
{
    _ => sum(Point(20, 22))
}
""";

        var llvm = RunSourceAtLlvm(source, StdlibListImportInputFile());
        Assert.True(
            llvm.Success,
            string.Join(Environment.NewLine, llvm.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var llvmIr = Assert.IsType<string>(llvm.LlvmIrText);
        Assert.Contains("%struct.eidos_Point", llvmIr, StringComparison.Ordinal);
        var sumDefinition = Assert.Single(
            llvmIr.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries),
            static line =>
                line.StartsWith("define external i64 @", StringComparison.Ordinal) &&
                line.Contains("_Function_u0000_sum_", StringComparison.Ordinal) &&
                !line.Contains("__eidos_prelude_core__", StringComparison.Ordinal));
        Assert.Contains("(%struct.eidos_Point ", sumDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("(ptr ", sumDefinition, StringComparison.Ordinal);

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_inline_copy_record.eidos",
            "native_inline_copy_record");
        Assert.Equal(42, execution.ExitCode);
    }

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
        shrunk := SeqBuilder.pop_last(xs);
        if SeqBuilder.len(ref shrunk) == 1 && SeqBuilder.get(shrunk)(0) == 10 then { 0 } else { 99 }
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
    public void SeqTake_LowersToConsumingRuntimePrimitiveAndPreservesPrefix()
    {
        const string source = """
import std.Seq

main :: Unit -> Int
{
    _ => {
        prefix := Seq.take([10, 20, 30])(2)
        if Seq.len(ref prefix) == 2 && prefix[0] == 10 && prefix[1] == 20 then { 0 } else { 99 }
    }
}
""";

        var llvm = RunSourceAtLlvm(source, StdlibListImportInputFile());
        Assert.True(
            llvm.Success,
            string.Join(Environment.NewLine, llvm.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains("@eidos_array_take", Assert.IsType<string>(llvm.LlvmIrText), StringComparison.Ordinal);

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_std_seq_take.eidos",
            "native_std_seq_take");
        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void ListRestPattern_LowersToCowSliceAndPreservesSuffixRules()
    {
        const string source = """
import std.Seq

main :: Unit -> Int
{
    _ => match [10, 20, 30, 40] {
        [head, ..middle, last] =>
            if head == 10 && last == 40 && Seq.len(ref middle) == 2 &&
               middle[0] == 20 && middle[1] == 30
            then { 0 }
            else { 99 },
        _ => 98
    }
}
""";

        var llvm = RunSourceAtLlvm(source, StdlibListImportInputFile());
        Assert.True(
            llvm.Success,
            string.Join(Environment.NewLine, llvm.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains("@eidos_array_slice", Assert.IsType<string>(llvm.LlvmIrText), StringComparison.Ordinal);

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_std_list_rest_slice.eidos",
            "native_std_list_rest_slice");
        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void SingletonAppend_LowersToConsumingPrependWithoutTemporaryArray()
    {
        const string source = """
import std.Seq

main :: Unit -> Int
{
    _ => {
        values := [10].append([20, 30])
        if Seq.len(ref values) == 3 && values[0] == 10 && values[2] == 30 then { 0 } else { 99 }
    }
}
""";

        var llvm = RunSourceAtLlvm(source, StdlibListImportInputFile());
        Assert.True(
            llvm.Success,
            string.Join(Environment.NewLine, llvm.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var llvmIr = Assert.IsType<string>(llvm.LlvmIrText);
        Assert.Contains("@eidos_array_prepend", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain(llvm.Diagnostics, diagnostic => diagnostic.Code == "E5310");

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_std_singleton_append.eidos",
            "native_std_singleton_append");
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
        swapped := SeqBuilder.swap(xs)(0)(1);
        match SeqBuilder.freeze(swapped)
        {
            [(a, b), (c, d)] => if a == 2 && b == 20 && c == 1 && d == 10 then { 0 } else { 99 },
            _ => 99
        }
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
        Seq.len(ref ys)
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
