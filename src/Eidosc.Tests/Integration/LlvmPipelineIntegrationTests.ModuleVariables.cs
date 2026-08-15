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
        Assert.Matches(@"@eidos_g_\w+_counter = internal global i64 0", llvmIr);
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
    public void ModuleLevelMutableGlobal_StringInitializer_RuntimeInitializesViaModuleInit()
    {
        const string source = """
        mut greeting := "hello";

        main :: Unit -> Int
        {
            _ => 0
        }
        """;

        var result = RunSourceAtLlvm(source, "module_level_global_string_runtime_init.eidos");

        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Matches(@"@eidos_g_\w+_greeting = internal global \w+ zeroinitializer", llvmIr);
        Assert.Contains("define external void @eidos_module_init()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("module_var_init_greeting", llvmIr, StringComparison.Ordinal);
        Assert.Matches(@"store ptr \S+, ptr @eidos_g_\w+_greeting", llvmIr);
    }

    [Fact]
    public void ModuleLevelMutableGlobal_CallInitializer_RuntimeInitializesInDependencyOrder()
    {
        const string source = """
        mut base := compute_base();
        mut derived := base + 1;

        compute_base :: Unit -> Int
        {
            _ => 40
        }

        main :: Unit -> Int
        {
            _ => derived
        }
        """;

        var result = RunSourceAtLlvm(source, "module_level_global_call_init_order.eidos");

        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        var initStart = llvmIr.IndexOf("define external void @eidos_module_init()", StringComparison.Ordinal);
        Assert.True(initStart >= 0, "Expected eidos_module_init definition.");
        var initEnd = llvmIr.IndexOf("}", initStart, StringComparison.Ordinal);
        var initBody = llvmIr[initStart..initEnd];
        var baseInitIndex = initBody.IndexOf("module_var_init_base", StringComparison.Ordinal);
        var derivedInitIndex = initBody.IndexOf("module_var_init_derived", StringComparison.Ordinal);
        Assert.True(baseInitIndex >= 0, "Expected base runtime init call.");
        Assert.True(derivedInitIndex >= 0, "Expected derived runtime init call.");
        Assert.True(baseInitIndex < derivedInitIndex, "base init must run before derived init.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void ModuleLevelMutableGlobal_CallInitializer_NativeSmoke_PersistsRuntimeValue()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
        mut base := compute_base();

        compute_base :: Unit -> Int
        {
            _ => 40
        }

        main :: Unit -> Int
        {
            _ => base + 2
        }
        """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "module_level_global_runtime_init_native.eidos",
            "module_level_global_runtime_init_native");

        Assert.Equal(42, execution.ExitCode);
    }
}
