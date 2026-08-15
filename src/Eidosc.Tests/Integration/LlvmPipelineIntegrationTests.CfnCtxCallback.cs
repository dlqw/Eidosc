using Eidosc;
using Eidosc.CodeGen;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void CfnCtxCallback_CapturingClosure_LowersInvokeFnExtraction()
    {
        const string source = """
            import std.Ffi
            import std.IntNarrow

            @[extern(c, name: "eidos_test_register_visit")]
            register_visit :: Cfn[RawPtr, Int32, Int32] -> RawPtr -> Unit need ffi;

            main :: Unit -> Int need ffi
            {
                _ => {
                    base: Int32 := IntNarrow.from_int32(39);
                    visit := x => x + base;
                    register_visit(Ffi.cfn_ctx_from(visit), Ffi.cfn_ctx_data(visit));
                    0
                }
            }
            """;

        var result = RunSourceAtLlvm(source, "cfn_ctx_callback_ir.eidos");
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        // invoke_fn 位于闭包对象 offset 8（跳过 header）
        Assert.Matches(@"getelementptr i8, ptr \S+, i64 8", llvmIr);
        Assert.Contains("load ptr, ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("declare void @eidos_test_register_visit(ptr, ptr)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @eidos_test_register_visit(ptr", llvmIr, StringComparison.Ordinal);
    }

    /// <summary>
    /// M5 原生冒烟：ctx-pointer 约定的 C 回调 API 消费 Eidos 捕获闭包。
    /// 闭包 invoke thunk 的 ABI 恰为 (closure_ptr, args...)，与 C 侧
    /// callback(void* ctx, int) 同构；捕获值参与计算验证闭包语义完整。
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void CfnCtxCallback_CapturingClosure_NativeSmoke_CaptureParticipates()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            import std.Ffi
            import std.IntNarrow

            @[extern(c, name: "eidos_test_run_visit")]
            run_visit :: Cfn[RawPtr, Int32, Int32] -> RawPtr -> Int32 need ffi;

            main :: Unit -> Int need ffi
            {
                _ => {
                    base: Int32 := IntNarrow.from_int32(39);
                    visit := x => x + base;
                    IntNarrow.to_int32(run_visit(Ffi.cfn_ctx_from(visit), Ffi.cfn_ctx_data(visit)))
                }
            }
            """;

        const string cSource = """
            typedef int (*eidos_visit_fn)(void* ctx, int x);

            int eidos_test_run_visit(eidos_visit_fn fn, void* ctx)
            {
                static const int items[3] = {1, 2, 3};
                int total = 0;
                for (int i = 0; i < 3; i++)
                {
                    total += fn(ctx, items[i]);
                }
                return total;
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "cfn_ctx_callback_native.eidos",
            "cfn_ctx_callback_native",
            nativeCSource: cSource);

        // (1+39) + (2+39) + (3+39) = 123：捕获值 39 参与每次回调。
        Assert.Equal(123, execution.ExitCode);
    }
}
