using Eidosc;
using Eidosc.CodeGen;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void NarrowScalarFfi_Int32ParameterAndReturn_LowersAsI32DeclareAndCall()
    {
        const string source = """
            import std.IntNarrow

            @[extern(c, name: "eidos_test_i32_probe")]
            i32_probe :: Int32 -> Int32 need ffi

            main :: Unit -> Int need ffi
            {
                _ => IntNarrow.to_int32(i32_probe(IntNarrow.from_int32(-7)))
            }
            """;

        var result = RunSourceAtLlvm(source, "narrow_scalar_ffi_i32.eidos");
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("declare i32 @eidos_test_i32_probe(i32)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @eidos_test_i32_probe(i32", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrowScalarFfi_UInt32LiteralArgument_LowersAsI32Call()
    {
        const string source = """
            import std.UInt

            @[extern(c, name: "eidos_test_u32_probe")]
            u32_probe :: UInt32 -> UInt32 need ffi

            main :: Unit -> Int need ffi
            {
                _ => UInt.to_int32(u32_probe(42u32))
            }
            """;

        var result = RunSourceAtLlvm(source, "narrow_scalar_ffi_u32.eidos");
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("declare i32 @eidos_test_u32_probe(i32)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @eidos_test_u32_probe(i32", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrowScalarFfi_Float32ParameterAndReturn_LowersAsFloatDeclareAndCall()
    {
        const string source = """
            import std.FloatNarrow

            @[extern(c, name: "eidos_test_f32_probe")]
            f32_probe :: Float32 -> Float32 need ffi

            main :: Unit -> Int need ffi
            {
                _ => FloatNarrow.to_float32(f32_probe(FloatNarrow.from_float32(2.5))) == 2.5
                    then 1
                    else 0
            }
            """;

        var result = RunSourceAtLlvm(source, "narrow_scalar_ffi_f32.eidos");
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("declare float @eidos_test_f32_probe(float)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call float @eidos_test_f32_probe(float", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrowScalarFfi_Int8Int16Float16_DeclareExactWidths()
    {
        const string source = """
            import std.IntNarrow
            import std.FloatNarrow

            @[extern(c, name: "eidos_test_i8_probe")]
            i8_probe :: Int8 -> Int8 need ffi

            @[extern(c, name: "eidos_test_i16_probe")]
            i16_probe :: Int16 -> Int16 need ffi

            @[extern(c, name: "eidos_test_f16_probe")]
            f16_probe :: Float16 -> Float16 need ffi

            main :: Unit -> Int need ffi
            {
                _ => {
                    a: Int := IntNarrow.to_int8(i8_probe(IntNarrow.from_int8(1)));
                    b: Int := IntNarrow.to_int16(i16_probe(IntNarrow.from_int16(2)));
                    c: Int := FloatNarrow.to_float16(f16_probe(FloatNarrow.from_float16(0.5))) == 0.5
                        then 10
                        else 0;
                    a + b + c
                }
            }
            """;

        var result = RunSourceAtLlvm(source, "narrow_scalar_ffi_widths.eidos");
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("declare i8 @eidos_test_i8_probe(i8)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("declare i16 @eidos_test_i16_probe(i16)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("declare half @eidos_test_f16_probe(half)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i8 @eidos_test_i8_probe(i8", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i16 @eidos_test_i16_probe(i16", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call half @eidos_test_f16_probe(half", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrowScalarFfi_Int32Value_SupportsArithmeticAndComparison()
    {
        const string source = """
            import std.IntNarrow

            @[extern(c, name: "eidos_test_i32_probe")]
            i32_probe :: Int32 -> Int32 need ffi

            main :: Unit -> Int need ffi
            {
                _ => {
                    value := i32_probe(IntNarrow.from_int32(21));
                    doubled: Int32 := value + value;
                    IntNarrow.to_int32(doubled) + (value < doubled then 1 else 0)
                }
            }
            """;

        var result = RunSourceAtLlvm(source, "narrow_scalar_ffi_int32_operators.eidos");
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("call i32 @eidos_test_i32_probe(i32", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NarrowScalar_Int32SignedDivision_NegativeOperands_TruncateTowardZero()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            import std.IntNarrow

            main :: Unit -> Int
            {
                _ => {
                    value := IntNarrow.from_int32(-7);
                    quotient := value / IntNarrow.from_int32(2);
                    IntNarrow.to_int32(quotient) == -3 then 0 else 1
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "narrow_int32_signed_div.eidos",
            "narrow_int32_signed_div");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NarrowScalar_UInt8FromInt_WrapsModulo256()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            import std.UInt

            main :: Unit -> Int
            {
                _ => UInt.to_int8(UInt.from_int8(300))
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "narrow_uint8_wrap.eidos",
            "narrow_uint8_wrap");

        Assert.Equal(44, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NarrowScalar_Int16NegativeRoundTrip_PreservesSign()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            import std.IntNarrow

            main :: Unit -> Int
            {
                _ => IntNarrow.to_int16(IntNarrow.from_int16(-1000)) == -1000
                    then 0
                    else 7
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "narrow_int16_negative.eidos",
            "narrow_int16_negative");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NarrowScalar_Float16RoundTrip_PreservesExactValue()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            import std.FloatNarrow

            main :: Unit -> Int
            {
                _ => FloatNarrow.to_float16(FloatNarrow.from_float16(0.5)) == 0.5
                    then 0
                    else 9
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "narrow_float16_roundtrip.eidos",
            "narrow_float16_roundtrip");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NarrowScalarFfi_Int32Abs_NativeSmoke_RoundTripsSignedValue()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            import std.IntNarrow

            @[extern(c, name: "abs")]
            c_abs :: Int32 -> Int32 need ffi

            main :: Unit -> Int need ffi
            {
                _ => IntNarrow.to_int32(c_abs(IntNarrow.from_int32(-42)))
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "narrow_scalar_ffi_abs_native.eidos",
            "narrow_scalar_ffi_abs_native");

        Assert.Equal(42, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NarrowScalarFfi_Float32Sqrtf_NativeSmoke_RoundTripsValue()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            import std.FloatNarrow

            @[extern(c, name: "sqrtf")]
            c_sqrtf :: Float32 -> Float32 need ffi

            main :: Unit -> Int need ffi
            {
                _ => FloatNarrow.to_float32(c_sqrtf(FloatNarrow.from_float32(2.25))) == 1.5
                    then 0
                    else 3
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "narrow_scalar_ffi_sqrtf_native.eidos",
            "narrow_scalar_ffi_sqrtf_native");

        Assert.Equal(0, execution.ExitCode);
    }
}
