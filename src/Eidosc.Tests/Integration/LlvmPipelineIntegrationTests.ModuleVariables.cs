using Eidosc;
using Eidosc.CodeGen;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void ModuleLevelMutableGlobal_LowersToLlvmGlobalWithLoadAndStore()
    {
        const string source = """
        mut counter := 0;

        inc :: Unit -> Int
        {
            _ => {
                counter := counter + 1;
                counter
            }
        }

        main :: Unit -> Int
        {
            _ => {
                inc();
                inc()
            }
        }
        """;

        var result = RunSourceAtLlvm(source, "module_level_global_codegen.eidos");

        Assert.True(
            result.Success,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Matches(@"@eidos_g_\w+_counter = global i64 0", llvmIr);
        Assert.Contains("load i64, ptr @eidos_g_", llvmIr, StringComparison.Ordinal);
        Assert.Contains("store i64", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void ModuleLevelMutableGlobal_NativeSmoke_PersistsAcrossCalls()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
        mut counter := 0;

        inc :: Unit -> Int
        {
            _ => {
                counter := counter + 1;
                counter
            }
        }

        main :: Unit -> Int
        {
            _ => {
                inc();
                inc()
            }
        }
        """;

        using var executable = CompileSourceToNativeExecutable(
            source,
            "module_level_global_native_smoke.eidos",
            "module_level_global_native_smoke",
            NativeLinkMode.NonPieExecutable);

        var execution = ExecuteProcess(
            executable.ExecutablePath,
            workingDirectory: executable.WorkingDirectory);

        Assert.Equal(2, execution.ExitCode);
    }

    [Fact]
    public void ModuleLevelMutableGlobal_NonStaticScalarInitializer_ReportsE5313AndFallsBackToZero()
    {
        const string source = """
        mut greeting := "hello";

        main :: Unit -> Int
        {
            _ => 0
        }
        """;

        var result = RunSourceAtLlvm(source, "module_level_global_static_scalar_fallback.eidos");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "E5313");
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Matches(@"@eidos_g_\w+_greeting = global \w+ zeroinitializer", llvmIr);
    }
}
