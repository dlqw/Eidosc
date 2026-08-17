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
        var source = TranslateC2EKeepingInputFile(cSource, out nativeShimSource, out var inputDirectory);
        Directory.Delete(inputDirectory, true);
        return source;
    }

    /// <summary>
    /// 翻译并保留 input.c 所在目录：union 用例的 C shim 以绝对路径 include 该文件，
    /// 原生编译发生在本方法返回之后，由调用方在用完后删除目录。
    /// </summary>
    private static string TranslateC2EKeepingInputFile(
        string cSource,
        out string nativeShimSource,
        out string inputDirectory)
    {
        Assert.True(ClangNative.TryLoad(out var loadError, out var api), loadError);
        inputDirectory = Path.Combine(Path.GetTempPath(), $"c2e_parity_{Guid.NewGuid():N}");
        Directory.CreateDirectory(inputDirectory);
        var cPath = Path.Combine(inputDirectory, "input.c");
        File.WriteAllText(cPath, cSource);
        var result = new CBodyTranslator(api!).Translate(cPath);
        Assert.Empty(result.SkippedFunctions);
        Assert.False(result.IsEmpty);
        nativeShimSource = result.NativeShimSource;
        return result.Source;
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
        // C int*（4 字节元素）走 i32 变体；Ffi.load[Int] 是 i64 存取。
        Assert.Contains("Ffi.load_i32(p)", translated, StringComparison.Ordinal);
        Assert.Contains("Ffi.store_i32(p)", translated, StringComparison.Ordinal);
        Assert.Contains("Ffi.pointer_eq(p)(Ffi.null_pointer())", translated, StringComparison.Ordinal);
        var eidosSource = translated + """

            @[extern(c, name: "test_box_i64")]
            test_box_i64 :: Int -> RawPtr need ffi;

            main :: Unit -> Int need ffi
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

        var inputDirectory = string.Empty;
        try
        {
            var translated = TranslateC2EKeepingInputFile(cSource, out var nativeShimSource, out inputDirectory);
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
            // 联合体别名语义（x86 小端 IEEE-754）：5.5 与 12.5 的 double 低 32 位均为 0，
            // radius 读作 0 → first = 0；第二次 12.5 > 10.0 触发 +100 → second = 100。
            Assert.Equal(100, execution.ExitCode);
        }
        finally
        {
            if (inputDirectory.Length > 0)
            {
                Directory.Delete(inputDirectory, true);
            }
        }
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
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_StructValueBridge_ParityWithClang()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string cSource = """
            typedef struct V2 { float x; float y; } V2;

            int corner_code(float rotation)
            {
                V2 a = { 1, 2 };
                V2 b = (V2){ 3.5, 4.5 };
                if (rotation == 0.0f)
                {
                    a = (V2){ a.x + b.x, a.y };
                }
                else
                {
                    a.y = (a.y + b.y) * (rotation + 1.0f);
                }
                float total = (a.x + a.y) * 10.0f;
                if (total > 60.0f) { return 1; }
                return 0;
            }

            int compute(void)
            {
                return corner_code(0.0f) + 2 * corner_code(1.0f);
            }
            """;

        var referenceExit = RunCReference(cSource, "int compute(void);\nint main(void) { return compute(); }\n");

        var translated = TranslateC2E(cSource);
        Assert.Contains("V2 {", translated, StringComparison.Ordinal);
        Assert.Contains(".{y:", translated, StringComparison.Ordinal);
        Assert.Contains("(a.y + b.y) * (rotation + 1.0)", translated, StringComparison.Ordinal);

        var eidosSource = translated + """

            main :: Unit -> Int
            {
                _ => compute()
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            eidosSource,
            "c2e_struct_bridge_native.eidos",
            "c2e_struct_bridge_native");

        Assert.Equal(referenceExit, execution.ExitCode);
        Assert.Equal(3, execution.ExitCode);
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
                struct Big { int a; int arr[4]; };
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

    /// <summary>
    /// L1 边界识别：-isystem 头里的无 body 声明是二进制边界（floor 符号），
    /// -I 项目头里的无 body 声明是跨 TU 符号（cross-TU）。
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_ExternClassification_SystemVsProjectHeader()
    {
        Assert.True(ClangNative.TryLoad(out var loadError, out var api), loadError);
        var dir = Path.Combine(Path.GetTempPath(), $"c2e_floor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "project"));
            Directory.CreateDirectory(Path.Combine(dir, "system"));
            File.WriteAllText(Path.Combine(dir, "project", "proj.h"), "int project_helper(int v);\n");
            File.WriteAllText(Path.Combine(dir, "system", "sys.h"), "int system_helper(int v);\n");
            var cPath = Path.Combine(dir, "input.c");
            File.WriteAllText(cPath, """
                #include "proj.h"
                #include <sys.h>

                int caller(int x)
                {
                    return project_helper(x) + system_helper(x);
                }
                """);
            var result = new CBodyTranslator(api!).Translate(
                cPath,
                includePaths: [Path.Combine(dir, "project")],
                defines: null,
                systemIncludePaths: [Path.Combine(dir, "system")]);
            Assert.Empty(result.SkippedFunctions);
            Assert.Contains("system_helper", result.FloorSymbols);
            Assert.DoesNotContain("system_helper", result.CrossTuSymbols);
            Assert.Contains("project_helper", result.CrossTuSymbols);
            Assert.DoesNotContain("project_helper", result.FloorSymbols);
            Assert.Contains("caller :: Int -> Int", result.Source, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 位运算对拍：C 的 & | ^ << >> ~ 翻译为 Eidos 同形运算符，
    /// 含 C 隐式优先级用例（翻译器平文本再解析，Eidos 优先级必须与 C 等价重分组）。
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_BitwiseOperators_ParityWithClang()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string cSource = """
            int bit_mix(int x, int y)
            {
                int and_ = x & y;
                int or_ = x | y;
                int xor_ = x ^ y;
                int shl_ = x << 3;
                int shr_ = y >> 2;
                int not_ = ~x;
                return (and_ + or_ + xor_ + shl_ + shr_ + not_) & 0x7F;
            }

            int prec_mix(int a, int b)
            {
                return a & 1 ^ b & 2 | a << 1 & 3;
            }

            int compute(void)
            {
                return bit_mix(0x25, 0x1A) + bit_mix(0x7, 0x40) + prec_mix(9, 5);
            }
            """;

        var referenceExit = RunCReference(cSource, "int compute(void);\nint main(void) { return compute(); }\n");

        var translated = TranslateC2E(cSource);
        Assert.Contains(" ^ ", translated, StringComparison.Ordinal);
        Assert.Contains(" ^ -1", translated, StringComparison.Ordinal);
        Assert.Contains(" << ", translated, StringComparison.Ordinal);
        Assert.Contains(" >> ", translated, StringComparison.Ordinal);

        var eidosSource = translated + """

            main :: Unit -> Int
            {
                _ => compute()
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            eidosSource,
            "c2e_bitwise_native.eidos",
            "c2e_bitwise_native");

        Assert.Equal(referenceExit, execution.ExitCode);
        Assert.Equal(87, execution.ExitCode);
    }

    /// <summary>
    /// 字符串字面量对拍：C 字符串字面量翻为 Eidos String，进入 RawPtr 语境
    /// （const char* 局部/参数/返回）时边界处 Ffi.to_c_string；转义集对齐。
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_StringLiterals_ParityWithClang()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string cSource = """
            extern int c_strlen(const char *s);

            int tag_weight(const char *tag)
            {
                return c_strlen(tag);
            }

            const char *fixed_label(void)
            {
                return "a\"b\\c";
            }

            int compute(void)
            {
                const char *msg = "hello\tworld";
                int a = tag_weight(msg);
                int b = tag_weight("raylib");
                return a + b + c_strlen(fixed_label());
            }
            """;

        var helperC = "#include <string.h>\nint c_strlen(const char *s) { return (int)strlen(s); }\n";
        var referenceExit = RunCReference(
            cSource,
            helperC + "int compute(void);\nint main(void) { return compute(); }\n");

        var translated = TranslateC2E(cSource);
        Assert.Contains("""Ffi.to_c_string("hello\tworld")""", translated, StringComparison.Ordinal);
        Assert.Contains("""Ffi.to_c_string("raylib")""", translated, StringComparison.Ordinal);
        Assert.Contains("""Ffi.to_c_string("a\"b\\c")""", translated, StringComparison.Ordinal);

        var eidosSource = translated + """

            main :: Unit -> Int
            {
                _ => compute()
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            eidosSource,
            "c2e_string_native.eidos",
            "c2e_string_native",
            nativeCSource: helperC);

        Assert.Equal(referenceExit, execution.ExitCode);
        Assert.Equal(22, execution.ExitCode);
    }

    /// <summary>
    /// 数组下标与指针算术对拍：a[i]/rows[i][j] 经 Ffi.offset_bytes 寻址 + load/store[T]；
    /// C float（32 位）元素经 c2e_f32 shim（Ffi.load[Float] 是 f64 存储）；
    /// T a[N] 局部映射 RawPtr 堆缓冲（malloc / calloc+store）；&amp;a[i] 取址、
    /// sizeof 常量求值、记录元素下标成员（pts[i].f）经 accessor 桥。
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void C2E_ArraySubscript_ParityWithClang()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string cSource = """
            struct Point { int x; int y; };

            int sum_ints(int* a, int n)
            {
                int s = 0;
                for (int i = 0; i < n; i++)
                {
                    s += a[i];
                }
                return s;
            }

            void fill_squares(int* a, int n)
            {
                for (int i = 0; i < n; i++)
                {
                    a[i] = i * i;
                }
            }

            int bump_elems(int* a, int n)
            {
                int s = 0;
                for (int i = 0; i < n; i++)
                {
                    a[i] += 3;
                    a[i]++;
                    s += a[i];
                }
                return s;
            }

            int matrix_trace(int* m, int n)
            {
                int t = 0;
                for (int i = 0; i < n; i++)
                {
                    t += m[i * n + i];
                }
                return t;
            }

            double sum_floats(float* a, int n)
            {
                double s = 0.0;
                for (int i = 0; i < n; i++)
                {
                    s += a[i];
                }
                return s;
            }

            void scale_floats(float* a, int n, float f)
            {
                for (int i = 0; i < n; i++)
                {
                    a[i] = a[i] * f;
                }
            }

            double sum_doubles(double* a, int n)
            {
                double s = 0.0;
                for (int i = 0; i < n; i++)
                {
                    s += a[i];
                }
                return s;
            }

            int point_array_sum(struct Point* pts, int n)
            {
                int s = 0;
                for (int i = 0; i < n; i++)
                {
                    pts[i].x += i;
                    pts[i].y = pts[i].x * 2;
                    s += pts[i].x + pts[i].y;
                }
                return s;
            }

            int sum_indirect(int** rows, int n)
            {
                int s = 0;
                for (int i = 0; i < n; i++)
                {
                    s += rows[i][0] + rows[i][1];
                }
                return s;
            }

            int local_array_sum(int seed)
            {
                int buf[6];
                for (int i = 0; i < 6; i++)
                {
                    buf[i] = seed + i;
                }
                int partial[4] = { 10, 20, 30 };
                int s = 0;
                for (int i = 0; i < 6; i++)
                {
                    s += buf[i];
                }
                for (int i = 0; i < 4; i++)
                {
                    s += partial[i];
                }
                return s;
            }

            int sizeof_mix(int n)
            {
                return n * (int)sizeof(int) + (int)sizeof(double);
            }

            int addr_and_deref(int* a)
            {
                int* p = &a[2];
                *p = *p + 5;
                return p[1];
            }

            int compute(int* ints, float* floats, double* doubles, struct Point* pts, int** rows)
            {
                fill_squares(ints, 8);
                int a = sum_ints(ints, 8);
                int b = bump_elems(ints, 8);
                int m[9];
                for (int i = 0; i < 9; i++)
                {
                    m[i] = i;
                }
                int t = matrix_trace(m, 3);
                double fs = sum_floats(floats, 6);
                scale_floats(floats, 6, 4.0f);
                double fs2 = sum_floats(floats, 6);
                double ds = sum_doubles(doubles, 5);
                int ps = point_array_sum(pts, 4);
                int rs = sum_indirect(rows, 3);
                int local = local_array_sum(7);
                int sf = sizeof_mix(3);
                int ad = addr_and_deref(ints);
                if (fs > 5.0) { a += 1; }
                if (fs2 > 20.0) { a += 2; }
                if (ds > 15.0) { a += 4; }
                return (a + b + t + ps + rs + local + sf + ad) % 251;
            }
            """;

        var helperC = """
            #include <stdlib.h>

            // 外部链接：Eidos 侧 extern(c) 引用来自其他编译单元，static 会造成链接期 undefined symbol。
            void* test_int_array(void)
            {
                int* a = (int*)malloc(8 * sizeof(int));
                return a;
            }

            void* test_float_array(void)
            {
                float* a = (float*)malloc(6 * sizeof(float));
                for (int i = 0; i < 6; i++) { a[i] = 0.25f * (float)(i + 1); }
                return a;
            }

            void* test_double_array(void)
            {
                double* a = (double*)malloc(5 * sizeof(double));
                for (int i = 0; i < 5; i++) { a[i] = 1.5 * (double)i; }
                return a;
            }

            void* test_point_array(void)
            {
                struct Point { int x; int y; };
                struct Point* p = (struct Point*)malloc(4 * sizeof(struct Point));
                for (int i = 0; i < 4; i++) { p[i].x = i + 1; p[i].y = 0; }
                return p;
            }

            void* test_row_array(void)
            {
                int** r = (int**)malloc(3 * sizeof(int*));
                for (int i = 0; i < 3; i++)
                {
                    r[i] = (int*)malloc(2 * sizeof(int));
                    r[i][0] = i + 1;
                    r[i][1] = 2 * (i + 1);
                }
                return r;
            }
            """;

        var referenceExit = RunCReference(
            cSource,
            helperC + """
                int compute(int*, float*, double*, void*, int**);
                int main(void)
                {
                    return compute(test_int_array(), test_float_array(), test_double_array(), test_point_array(), test_row_array());
                }
                """);

        var inputDirectory = string.Empty;
        try
        {
            var translated = TranslateC2EKeepingInputFile(cSource, out var nativeShimSource, out inputDirectory);
            Assert.Contains("Ffi.offset_bytes(", translated, StringComparison.Ordinal);
            // C int（4 字节）元素走 i32 变体；Ffi.load[Int] 是 i64 存取。
            Assert.Contains("Ffi.load_i32(", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.store_i32(", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.load[Float]", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.load[RawPtr]", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.load_f32(", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.store_f32(", translated, StringComparison.Ordinal);
            Assert.Contains("c2e_Point_x_get(", translated, StringComparison.Ordinal);
            Assert.Contains("c2e_Point_y_set(", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.malloc(", translated, StringComparison.Ordinal);
            Assert.Contains("Ffi.calloc(", translated, StringComparison.Ordinal);

            var eidosSource = translated + """

                @[extern(c, name: "test_int_array")]
                test_int_array :: Unit -> RawPtr need ffi;
                @[extern(c, name: "test_float_array")]
                test_float_array :: Unit -> RawPtr need ffi;
                @[extern(c, name: "test_double_array")]
                test_double_array :: Unit -> RawPtr need ffi;
                @[extern(c, name: "test_point_array")]
                test_point_array :: Unit -> RawPtr need ffi;
                @[extern(c, name: "test_row_array")]
                test_row_array :: Unit -> RawPtr need ffi;

                main :: Unit -> Int need ffi
                {
                    _ => compute(test_int_array(), test_float_array(), test_double_array(), test_point_array(), test_row_array())
                }
                """;

            var execution = CompileAndRunSourceAtNative(
                eidosSource,
                "c2e_subscript_native.eidos",
                "c2e_subscript_native",
                nativeCSource: nativeShimSource + helperC);

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
