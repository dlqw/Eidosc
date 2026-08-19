using System.Diagnostics;
using Eidosc;
using Eidosc.Bindgen.Clang;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

/// <summary>
/// M7 C2E tier-3 对拍：不透明/匿名嵌套 record 存储（calloc 局部、C 共享全局、
/// 路径摊平 accessor）、变参调用转发、盒化值记录局部、osret 槽、记录元素下标——
/// 与 clang 运行行为（退出码）逐值一致。辅助函数与 TranslateC2EKeepingInputFile
/// 位于 C2EParity.cs（同 partial 类）。
/// </summary>
public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_OpaqueRecordsAndVariadics_ParityWithClang()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string cSource = """
            #include <stdarg.h>
            #include <string.h>

            struct Big { int counts[4]; char tag[16]; double ratio; };

            static struct Big global_state;
            struct Big shared_state;

            // 匿名嵌套 struct（raylib CoreData 形态）：路径摊平 accessor。
            struct Node {
                struct { int lo; int hi; struct { int mid; } band; } range;
                int weight;
            };

            static struct Node tree;

            int anon_member(int v)
            {
                tree.range.lo = v;
                tree.range.band.mid = v * 3;
                struct Node *p = &tree;
                p->range.hi = v + 5;
                int *mp = &tree.range.band.mid;
                return tree.range.lo + tree.range.hi + tree.range.band.mid + *mp + p->weight;
            }

            static int sum_variadic(int count, ...)
            {
                va_list args;
                va_start(args, count);
                int total = 0;
                for (int i = 0; i < count; i++) total += va_arg(args, int);
                va_end(args);
                return total;
            }

            // 盒化值记录局部（&msg 供输出参数写回）+ 记录元素下标 + osret（不可映射
            // record 返回值的成员访问，本体留在 C 侧）。
            struct Pair { int a; int b; };

            static void fill_pair(struct Pair *p, int v) { p->a = v; p->b = v * 2; }

            int boxed_local(int v)
            {
                struct Pair msg = { 0 };
                fill_pair(&msg, v);
                return msg.a * 100 + msg.b;
            }

            static struct Pair table[3] = { { 1, 2 }, { 3, 4 }, { 5, 6 } };

            int elem_load(int i)
            {
                struct Pair p = table[i];
                p.a += 1;
                return p.a * 100 + p.b;
            }

            typedef struct { int v[4]; } Vec4Box;

            static Vec4Box make_vec4(int base)
            {
                Vec4Box r = { 0 };
                r.v[0] = base;
                r.v[1] = base + 1;
                return r;
            }

            int osret_use(int v)
            {
                return make_vec4(v).v[0] * 10 + make_vec4(v).v[1];
            }

            int use_global(int v)
            {
                global_state.ratio = 1.5;
                global_state.counts[1] = v;
                shared_state.counts[2] = v + 1;
                return (int)(global_state.ratio * 10.0) + global_state.counts[1] + shared_state.counts[2];
            }

            int use_local(int v)
            {
                struct Big local;
                memset(&local, 0, sizeof(local));
                local.tag[0] = 'x';
                local.counts[0] = v * 2;
                global_state.counts[3] = local.counts[0];
                return local.tag[0] + local.counts[0] + (int)strlen(local.tag);
            }

            int use_static(int v)
            {
                static struct Big cache;
                cache.counts[0] += v;
                cache.ratio = 2.5;
                return cache.counts[0] + (int)cache.ratio;
            }

            int call_variadic(int v)
            {
                int s = sum_variadic(3, v, v + 1, v + 2);
                return s;
            }

            int compute(void)
            {
                return use_global(3) + use_local(5) + use_static(4) + use_static(6) + call_variadic(7) +
                    anon_member(2) + boxed_local(9) + elem_load(1) + osret_use(4);
            }
            """;

        var referenceExit = RunCReference(cSource, "int compute(void);\nint main(void) { return compute(); }\n");

        var inputDirectory = string.Empty;
        try
        {
        var translated = TranslateC2EKeepingInputFile(cSource, out var nativeShimSource, out inputDirectory, ["sum_variadic", "make_vec4"]);
        // 不透明 record 局部：calloc 存储（绑定即地址），数组成员经 _addr 衰减访问。
        Assert.Contains("Ffi.calloc(1)(", translated, StringComparison.Ordinal);
        // 不透明 record 全局/static：C 侧存储 getter（内部链接全局带 TU 标签）。
        Assert.Contains("c2e_glob_", translated, StringComparison.Ordinal);
        Assert.Contains("global_state", translated, StringComparison.Ordinal);
        // 变参调用：调用点实参固化的 C 转发 shim（转调 va_list 实体函数）。
        Assert.Contains("c2e_var_sum_variadic_", translated, StringComparison.Ordinal);
        // 匿名嵌套 struct：路径摊平 accessor（游标链推进，无以命名的中间记录）。
        Assert.Contains("c2e_Node_range_band_mid_get", translated, StringComparison.Ordinal);
        // osret：不可映射 record 返回值的 malloc 槽包装 + 成员访问。
        Assert.Contains("c2e_ext_make_vec4_osret", translated, StringComparison.Ordinal);

        var eidosSource = translated + """

            main :: Unit -> Int
            {
                _ => compute()
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            eidosSource,
            "c2e_opaque_native.eidos",
            "c2e_opaque_native",
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
