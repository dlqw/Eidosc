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
    private static string TranslateC2E(string cSource)
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
            return result.Source;
        }
        finally
        {
            Directory.Delete(dir, true);
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
        var cDir = Path.Combine(Path.GetTempPath(), $"c2e_ref_{Guid.NewGuid():N}");
        Directory.CreateDirectory(cDir);
        int referenceExit;
        try
        {
            var cPath = Path.Combine(cDir, "ref.c");
            var mainPath = Path.Combine(cDir, "main.c");
            File.WriteAllText(cPath, cSource);
            File.WriteAllText(mainPath, "int compute(void);\nint main(void) { return compute(); }\n");
            var exePath = Path.Combine(cDir, OperatingSystem.IsWindows() ? "ref.exe" : "ref");
            var compile = ExecuteProcess(
                ResolveToolPath("clang")!,
                $"-std=c11 \"{cPath}\" \"{mainPath}\" -o \"{exePath}\"");
            Assert.True(compile.ExitCode == 0, compile.StandardOutput + compile.StandardError);
            referenceExit = ExecuteProcess(exePath, workingDirectory: cDir).ExitCode;
        }
        finally
        {
            Directory.Delete(cDir, true);
        }

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
    public void C2E_UnsupportedConstruct_SkipsWithReason()
    {
        Assert.True(ClangNative.TryLoad(out var loadError, out var api), loadError);
        var dir = Path.Combine(Path.GetTempPath(), $"c2e_skip_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var cPath = Path.Combine(dir, "input.c");
            File.WriteAllText(cPath, """
                int bad(int* p) { return *p; }
                int good(int x) { return x + 1; }
                """);
            var result = new CBodyTranslator(api!).Translate(cPath);
            Assert.Equal(["bad"], result.SkippedFunctions);
            Assert.Contains("SKIP bad: parameter 'p' has unsupported type", result.Source, StringComparison.Ordinal);
            Assert.Contains("good :: Int -> Int", result.Source, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
