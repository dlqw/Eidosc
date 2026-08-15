using System.Diagnostics;
using Eidosc;
using Eidosc.Bindgen.Clang;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

/// <summary>
/// M7 C2E 垂直切片对拍门：同一 C 源分别经 clang 编译与 Eidos 翻译后编译，
/// 运行行为（退出码）必须一致。切片支持标量算术/比较、局部变量、
/// if/else、while/for、return、同文件函数调用。
/// </summary>
public partial class LlvmPipelineIntegrationTests
{
    private static string TranslateC2E(string cSource) =>
        TranslateC2E(cSource, out _);

    private static string TranslateC2E(string cSource, out string nativeShimSource)
    {
        Assert.True(ClangNative.TryLoad(out var loadError, out var api), loadError);
        var dir = Path.Combine(Path.GetTempPath(), $"c2e_parity_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var cPath = Path.Combine(dir, "input.c");
            File.WriteAllText(cPath, cSource);
            var result = new CBodyTranslator(api!).Translate(cPath);
            Assert.Empty(result.SkippedFunctions);
            Assert.False(result.IsEmpty);
            nativeShimSource = result.NativeShimSource;
            return result.Source;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>编译并运行 C 参照程序，返回退出码。</summary>
    private static int RunCReference(string cSource, string mainCSource)
    {
        var cDir = Path.Combine(Path.GetTempPath(), $"c2e_ref_{Guid.NewGuid():N}");
        Directory.CreateDirectory(cDir);
        try
        {
            var cPath = Path.Combine(cDir, "ref.c");
            var mainPath = Path.Combine(cDir, "main.c");
            File.WriteAllText(cPath, cSource);
            File.WriteAllText(mainPath, mainCSource);
            var exePath = Path.Combine(cDir, OperatingSystem.IsWindows() ? "ref.exe" : "ref");
            var compile = ExecuteProcess(
                ResolveToolPath("clang")!,
                $"-std=c11 \"{cPath}\" \"{mainPath}\" -o \"{exePath}\"");
            Assert.True(compile.ExitCode == 0, compile.StandardOutput + compile.StandardError);
            return ExecuteProcess(exePath, workingDirectory: cDir).ExitCode;
        }
        finally
        {
            Directory.Delete(cDir, true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_ScalarControlFlow_ParityWithClang()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string cSource = """
            int add(int a, int b) { return a + b; }

            int sum_evens(int n)
            {
                int acc = 0;
                int i = 1;
                while (i <= n)
                {
                    if (i % 2 == 0)
                    {
                        acc = acc + i;
                    }
                    else
                    {
                        acc = acc - 1;
                    }
                    i = i + 1;
                }
                return acc;
            }

            int tri(int n)
            {
                int t = 0;
                for (int j = 1; j <= n; j = j + 1)
                {
                    t = t + j;
                }
                return t;
            }

            int compute(void)
            {
                int a = sum_evens(9);
                int b = tri(6);
                return add(a, b) % 251;
            }
            """;

        // C 参照：clang 编译后运行。
        var referenceExit = RunCReference(cSource, "int compute(void);\nint main(void) { return compute(); }\n");

        // Eidos 翻译：生成函数 + 手写 main 调 compute()。
        var translated = TranslateC2E(cSource);
        var eidosSource = translated + """

            main :: Unit -> Int
            {
                _ => compute()
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            eidosSource,
            "c2e_parity_native.eidos",
            "c2e_parity_native");

        Assert.Equal(referenceExit, execution.ExitCode);
        Assert.NotEqual(0, referenceExit);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_PointerDerefAndNull_ParityWithClang()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string cSource = """
            int bump(int* p)
            {
                *p = *p + 1;
                return *p;
            }

            int classify(int* p)
            {
                if (p == 0) { return 100; }
                if (p != 0) { return 200; }
                return 300;
            }

            int compute(int* p)
            {
                int bumped = bump(p);
                return bumped + classify(p) + classify(0);
            }
            """;

        var referenceExit = RunCReference(
            cSource,
            "int compute(int*);\nint main(void) { int cell = 41; return compute(&cell); }\n");

        var translated = TranslateC2E(cSource);
        Assert.Contains("Ffi.load[Int](p)", translated, StringComparison.Ordinal);
        Assert.Contains("Ffi.store[Int](p)", translated, StringComparison.Ordinal);
        Assert.Contains("Ffi.pointer_eq(p)(Ffi.null_pointer())", translated, StringComparison.Ordinal);
        var eidosSource = translated + """

            @[extern(c, name: "test_box_i64")]
            test_box_i64 :: Int -> RawPtr need ffi;

            main :: Unit -> Int
            {
                _ => compute(test_box_i64(41))
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            eidosSource,
            "c2e_ptr_parity_native.eidos",
            "c2e_ptr_parity_native",
            nativeCSource: """
                #include <stdlib.h>

                void* test_box_i64(long long v)
                {
                    long long* p = (long long*)malloc(sizeof(long long));
                    *p = v;
                    return p;
                }
                """);

        Assert.Equal(referenceExit, execution.ExitCode);
        Assert.Equal(342, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_UnionMemberBridge_ParityWithClang()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string cSource = """
            union Shape { int radius; double width; };

            int area_code(union Shape* s, int scale)
            {
                s->radius = s->radius * scale;
                if (s->width > 10.0) { s->radius = s->radius + 100; }
                return s->radius;
            }

            int compute(union Shape* s)
            {
                s->width = 5.5;
                int first = area_code(s, 4);
                s->width = 12.5;
                int second = area_code(s, 3);
                return first + second;
            }
            """;

        var referenceExit = RunCReference(
            cSource,
            """
                int compute(void*);
                int main(void)
                {
                    union Shape { int radius; double width; };
                    union Shape v;
                    v.radius = 3;
                    v.width = 0.0;
                    return compute(&v);
                }
                """);

        var translated = TranslateC2E(cSource, out var nativeShimSource);
        Assert.Contains("c2e_Shape_radius_get", translated, StringComparison.Ordinal);
        Assert.Contains("c2e_Shape_radius_set", translated, StringComparison.Ordinal);
        Assert.Contains("c2e_Shape_width_get", translated, StringComparison.Ordinal);
        Assert.Contains("#include \"", nativeShimSource, StringComparison.Ordinal);
        var eidosSource = translated + """

            @[extern(c, name: "test_alloc_shape")]
            test_alloc_shape :: Unit -> RawPtr need ffi;

            main :: Unit -> Int
            {
                _ => {
                    s := test_alloc_shape();
                    c2e_Shape_radius_set(s)(3);
                    compute(s)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            eidosSource,
            "c2e_union_parity_native.eidos",
            "c2e_union_parity_native",
            nativeCSource: nativeShimSource + """

                #include <stdlib.h>

                void* test_alloc_shape(void) { return malloc(16); }
                """);

        Assert.Equal(referenceExit, execution.ExitCode);
        Assert.Equal(148, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_ExternalCall_ParityWithClang()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string cSource = """
            extern int helper_scale(int v);

            int twice_scaled(int x)
            {
                return helper_scale(x) * 2 + 1;
            }

            int compute(void)
            {
                return twice_scaled(5);
            }
            """;

        var helperC = "int helper_scale(int v) { return v * 3 + 7; }\n";
        var referenceExit = RunCReference(
            cSource,
            helperC + "int compute(void);\nint main(void) { return compute(); }\n");

        var translated = TranslateC2E(cSource);
        Assert.Contains("""@[extern(c, name: "helper_scale")]""", translated, StringComparison.Ordinal);
        Assert.Contains("c2e_ext_helper_scale :: Int -> Int need ffi;", translated, StringComparison.Ordinal);
        var eidosSource = translated + """

            main :: Unit -> Int
            {
                _ => compute()
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            eidosSource,
            "c2e_extern_parity_native.eidos",
            "c2e_extern_parity_native",
            nativeCSource: helperC);

        Assert.Equal(referenceExit, execution.ExitCode);
        Assert.Equal(45, execution.ExitCode);
    }

    [Fact]
    public void C2E_UnsupportedConstruct_SkipsWithReason()
    {
        Assert.True(ClangNative.TryLoad(out var loadError, out var api), loadError);
        var dir = Path.Combine(Path.GetTempPath(), $"c2e_skip_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var cPath = Path.Combine(dir, "input.c");
            File.WriteAllText(cPath, """
                struct Big { int a; int b; };
                int bad(struct Big s) { return s.a; }
                int good(int x) { return x + 1; }
                """);
            var result = new CBodyTranslator(api!).Translate(cPath);
            Assert.Equal(["bad"], result.SkippedFunctions);
            Assert.Contains("SKIP bad: parameter 's' has unsupported type", result.Source, StringComparison.Ordinal);
            Assert.Contains("good :: Int -> Int", result.Source, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
