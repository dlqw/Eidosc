using System.Diagnostics;
using Eidosc;
using Eidosc.Bindgen.Clang;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

/// <summary>
/// C2E 函数指针对拍：typedef 签名、局部/全局/record 字段函数指针、函数名退化取址、
/// 间接调用、NULL 哨兵、指针相等、重赋值与函数指针数组，全部直译为
/// Cfn + cfn_from/cfn_call，不再生成 c2e_icall/c2e_addr 摘要桥。
/// </summary>
public partial class LlvmPipelineIntegrationTests
{
    /// <summary>
    /// 函数指针对拍：typedef 签名、局部/全局/record 字段函数指针、函数名退化取址、
    /// 间接调用、NULL 哨兵、指针相等与重赋值，全部直译为 Cfn + cfn_from/cfn_call，
    /// 不再生成 c2e_icall/c2e_addr 摘要桥。
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_FunctionPointers_ParityWithClang()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string cSource = """
            typedef int (*BinOp)(int, int);
            typedef void (*VoidFn)(void);

            static int add(int a, int b) { return a + b; }
            static int mul(int a, int b) { return a * b; }

            BinOp promote(VoidFn fn) { return (BinOp)fn; }

            static BinOp g_op = mul;

            struct Holder { BinOp op; };

            BinOp get_add(void) { return add; }

            int apply(BinOp op, int a, int b) { return op(a, b); }

            int indirect_add(int a, int b)
            {
                BinOp f = add;
                return f(a, b);
            }

            int global_call(int a, int b) { return g_op(a, b); }

            int member_call(struct Holder* h, int a, int b) { return h->op(a, b); }

            int null_sentinel(void)
            {
                BinOp op = 0;
                if (op) { return 1; }
                return 0;
            }

            int eq_sentinel(BinOp left, BinOp right) { return left == right; }

            int assign_through(void)
            {
                BinOp f = add;
                f = mul;
                return f(6, 7);
            }

            int array_call(int idx, int a, int b)
            {
                BinOp table[2] = { add, mul };
                return table[idx](a, b);
            }

            int compute(void)
            {
                BinOp picked = get_add();
                return indirect_add(20, 22)
                    + global_call(6, 7)
                    + apply(add, 3, 4)
                    + assign_through()
                    + picked(5, 6)
                    + null_sentinel()
                    + eq_sentinel(add, add)
                    + eq_sentinel(add, mul)
                    + array_call(0, 5, 6)
                    + array_call(1, 5, 6);
            }
            """;

        var referenceExit = RunCReference(
            cSource,
            """
                int compute(void);
                int member_call(void*, int, int);
                typedef int (*BinOp)(int, int);
                static int mul(int a, int b) { return a * b; }
                int main(void)
                {
                    struct Holder { BinOp op; } h;
                    h.op = mul;
                    return (compute() + member_call(&h, 6, 7)) % 251;
                }
                """);

        var inputDirectory = string.Empty;
        try
        {
            var translated = TranslateC2EKeepingInputFile(cSource, out var nativeShimSource, out inputDirectory);
            Assert.DoesNotContain("c2e_icall_", translated, StringComparison.Ordinal);
            Assert.DoesNotContain("c2e_addr_", translated, StringComparison.Ordinal);
            Assert.Contains("Cfn[Int, Int, Int]", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.cfn_from(add)", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.cfn_call(op, a, b)", translated, StringComparison.Ordinal);
            Assert.Contains("c2e_Holder_op_get :: RawPtr -> Cfn[Int, Int, Int] need ffi;", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.pointer_eq(left)(right)", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.load[Cfn[Int, Int, Int]]", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.store[Cfn[Int, Int, Int]]", translated, StringComparison.Ordinal);
            Assert.Contains("c2e_cfn_typed: Cfn[Int, Int, Int] := c2e_cfn_ptr", translated, StringComparison.Ordinal);

            var eidosSource = translated + """

                main :: Unit -> Int need ffi
                {
                    _ => {
                        h := Ffi.malloc(8);
                        c2e_Holder_op_set(h, Ffi.cfn_from(mul));
                        (compute() + member_call(h, 6, 7)) % 251
                    }
                }
                """;

            var execution = CompileAndRunSourceAtNative(
                eidosSource,
                "c2e_fnptr_native.eidos",
                "c2e_fnptr_native",
                nativeCSource: nativeShimSource);

            Assert.Equal(referenceExit, execution.ExitCode);
            Assert.NotEqual(0, execution.ExitCode);
        }
        finally
        {
            if (!string.IsNullOrEmpty(inputDirectory))
            {
                Directory.Delete(inputDirectory, true);
            }
        }
    }

}
