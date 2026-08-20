using System.Linq;
using Eidosc.Diagnostic;
using Eidosc;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

/// <summary>
/// Cfn[A..., R] 作为完整类型构造器出现在 alias、ADT、@[repr(c)] 字段与
/// 泛型容器中的回归测试。编译器后端本身支持任意 arity，这些测试同时
/// 覆盖此前 kind 系统只接受一元 Cfn 的缺口。
/// </summary>
public class CfnTypePositionTests
{
    [Fact]
    public void CfnType_Alias_ResolvesAndCalls()
    {
        const string source = """
IntFn :: type = Cfn[Int, Int]

inc :: Int -> Int { x => x + 1 }

main :: Unit -> Int
{
    _ => {
        fp: IntFn := Ffi.null_pointer();
        real := Ffi.cfn_from(inc);
        Ffi.cfn_call(real, 41)
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnType_ReprCField_GetSetAndCall()
    {
        const string source = """
@[repr(c)]
Holder :: type
{
    fp:: Cfn[Int, Int]
}

inc :: Int -> Int { x => x + 1 }

main :: Unit -> Int need ffi
{
    _ => {
        p := Ffi.malloc(8);
        holder_fp_set(p, Ffi.cfn_from(inc));
        got := Ffi.cfn_call(p.fp, 41);
        got
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnType_NestedInGenericContainer_Resolves()
    {
        const string source = """
count_fn[A, R] :: Seq[Cfn[A, R]] -> Int
{
    fns => fns.len()
}

main :: Unit -> Int
{
    _ => count_fn[Int, Int]([Ffi.null_pointer()])
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnType_GenericFunctionSignature_Resolves()
    {
        const string source = """
call_through[A, R] :: Cfn[A, R] -> A -> R
{
    fn_ptr => value => Ffi.cfn_call(fn_ptr, value)
}

main :: Unit -> Int
{
    _ => call_through[Int, Int](Ffi.null_pointer(), 1)
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnType_ZeroArgumentAlias_Resolves()
    {
        const string source = """
ThunkFn :: type = Cfn[Int]

answer :: Unit -> Int { 42 }

main :: Unit -> Int
{
    _ => {
        fp: ThunkFn := Ffi.cfn_from(answer);
        Ffi.cfn_call(fp)
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnType_HighArityAlias_Resolves()
    {
        const string source = """
SevenFn :: type = Cfn[Int, Int, Int, Int, Int, Int, Int, Int]

sum_seven :: Int -> Int -> Int -> Int -> Int -> Int -> Int -> Int {
    a => b => c => d => e => f => g => a + b + c + d + e + f + g
}

main :: Unit -> Int
{
    _ => {
        fp: SevenFn := Ffi.cfn_from(sum_seven);
        Ffi.cfn_call(fp, 1, 2, 3, 4, 5, 6, 7)
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnType_ModuleLevelMutableInitializer_CompilesThroughLlvm()
    {
        const string source = """
inc :: Int -> Int { x => x + 1 }

mut fp_null: Cfn[Int, Int] := Ffi.null_pointer();
mut fp_real: Cfn[Int, Int] := Ffi.cfn_from(inc);

main :: Unit -> Int need ffi
{
    _ => Ffi.cfn_call(fp_real, 41)
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CfnType_NarrowScalarSignature_CompilesThroughLlvm()
    {
        const string source = """
import std.FloatNarrow
import std.IntNarrow

f32_id :: Float32 -> Float32 { x => x }
i8_id :: Int8 -> Int8 { x => x }

main :: Unit -> Float32 need ffi
{
    _ => {
        fp32 := Ffi.cfn_from(f32_id);
        fp8 := Ffi.cfn_from(i8_id);
        r32 := Ffi.cfn_call(fp32, FloatNarrow.from_float32(1.5));
        r8 := Ffi.cfn_call(fp8, IntNarrow.from_int8(7));
        r32
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Llvm);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    private static CompilationResult RunPipeline(
        string source,
        CompilationPhase stopAtPhase = CompilationPhase.Types)
    {
        var options = new CompilationOptions
        {
            InputFile = "cfn_type_position_test.eidos",
            StopAtPhase = stopAtPhase,
            AllowVirtualInputFile = true,
            UseColors = false
        };

        const string ffiImports = "import std.Ffi\n\n";
        options.PackageImportRoots = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [WellKnownStrings.Std.Module] = []
        };
        return new CompilationPipeline(ffiImports + source, options).Run();
    }
}
