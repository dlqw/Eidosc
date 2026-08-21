using System;
using System.Linq;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_ArbitraryIntegerSuffix_InfersExactWidth()
    {
        const string source = """
main :: Unit -> I24
{
    _ => 7i24
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Llvm_ArbitraryIntegerArithmetic_PreservesNativeWidth()
    {
        const string source = """
add :: I24 -> I24 -> I24
{
    a b => a + b
}

main :: Unit -> I24
{
    _ => add(1i24, 2i24)
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.Contains("add i24", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void Llvm_ArbitraryIntegerBitwise_PreservesNativeWidth()
    {
        const string source = """
main :: Unit -> U16
{
    _ => 0xFFu16 & 0x0Fu16
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.Contains("and i16", result.LlvmIrText, StringComparison.Ordinal);
        Assert.DoesNotContain("and i64", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void Types_ArbitraryIntegerSuffix_OutOfRangeWidth_ReportsParseError()
    {
        const string source = """
main :: Unit -> I24
{
    _ => 1i5000
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "E4016" &&
            diagnostic.Message.Contains("exceeds the maximum supported width", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ArbitraryIntegerLiteral_AdaptsToExpectedWidth()
    {
        const string source = """
main :: Unit -> U24
{
    _ => 42
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Types_ArbitraryIntegerPattern_MatchesLiteral()
    {
        const string source = """
main :: U8 -> Bool
{
    x => match x
    {
        0u8 => true,
        _ => false
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Llvm_ArbitraryIntegerComparison_UsesSameWidth()
    {
        const string source = """
eq :: I24 -> Bool
{
    x => x == 7i24
}

main :: Unit -> Bool
{
    _ => eq(7i24)
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.Contains("icmp eq i24", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void Llvm_StandardNarrowIntegerBitwise_AcceptsAllIntegerWidths()
    {
        const string source = """
main :: Unit -> Int8
{
    _ => 7i8 & 3i8
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains("and i8", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void Llvm_NegativeLiteral_AdaptsToNarrowSignedType()
    {
        const string source = """
main :: Unit -> Int8
{
    _ => 10i8 + -1
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains("add i8", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void Llvm_I1AndU1_UnifyWithBool()
    {
        const string source = """
i1_true :: Unit -> I1
{
    _ => true
}

u1_true :: Unit -> U1
{
    _ => true
}

main :: Unit -> Bool
{
    _ => i1_true(()) && u1_true(())
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains("and i1", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void Llvm_ArbitraryIntegerTypeAnnotation_ResolvesIntegerIdAndLowersWidth()
    {
        const string source = """
and_i24 :: I24 -> I24 -> I24
{
    a => b => a & b
}

main :: Unit -> I24
{
    _ => and_i24(5i24, 3i24)
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains("and i24", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void Llvm_ArbitraryIntegerHugeLiteralArithmetic_LowersToI512()
    {
        const string source = """
main :: Unit -> U512
{
    _ => 340282366920938463463374607431768211455u512 + 1u512
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains("add i512", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void Llvm_ArbitraryIntegerHugeLiteral_EmitsBigIntegerConstant()
    {
        const string source = """
main :: Unit -> U512
{
    _ => 340282366920938463463374607431768211455u512
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.Contains("i512 340282366920938463463374607431768211455", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void Llvm_ArbitraryIntegerHugeNegativeLiteral_EmitsBigIntegerConstant()
    {
        const string source = """
main :: Unit -> I512
{
    _ => -170141183460469231731687303715884105728i512
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.Contains("i512 -170141183460469231731687303715884105728", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void Llvm_CStringModule_RoundTripsThroughStdCString()
    {
        const string source = """
import std.CString

main :: Unit -> String
{
    _ => CString.to_string(CString.from_string("hello"))
}
""";

        var available = string.Join(Environment.NewLine, PrecompiledModuleRegistry.GetAvailableModulePaths());
        Assert.Contains("Std/CString", available, StringComparison.OrdinalIgnoreCase);

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.Contains("eidos_string_to_cstr", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_string_from_cstr_raw", result.LlvmIrText, StringComparison.Ordinal);
    }
}
