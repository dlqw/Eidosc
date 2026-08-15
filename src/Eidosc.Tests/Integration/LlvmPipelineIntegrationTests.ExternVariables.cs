using Eidosc;
using Eidosc.CodeGen;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void ExternVariable_DeclarationOnlyGlobal_LowersWithExactCName()
    {
        const string source = """
            @[extern(c, name: "eidos_test_extern_state")]
            mut state : Int;

            advance :: Unit -> Int
            {
                _ => {
                    state := state + 1;
                    state
                }
            }

            main :: Unit -> Int
            {
                _ => advance()
            }
            """;

        var result = RunSourceAtLlvm(source, "extern_variable_declaration.eidos");
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("@eidos_test_extern_state = external global i64", llvmIr, StringComparison.Ordinal);
        Assert.Contains("load i64, ptr @eidos_test_extern_state", llvmIr, StringComparison.Ordinal);
        Assert.Contains("store i64", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("@eidos_g_", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternVariable_PointerGlobal_LowersAsExternalPointer()
    {
        const string source = """
            @[extern(c, name: "eidos_test_extern_cursor")]
            mut cursor : RawPtr;

            main :: Unit -> Int
            {
                _ => 0
            }
            """;

        var result = RunSourceAtLlvm(source, "extern_variable_pointer.eidos");
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("@eidos_test_extern_cursor = external global ptr", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternVariable_MissingInitializerWithoutExtern_IsRejected()
    {
        const string source = """
            mut counter : Int;

            main :: Unit -> Int
            {
                _ => 0
            }
            """;

        var result = RunSourceAtLlvm(source, "extern_variable_missing_initializer.eidos");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "E3050");
    }

    [Fact]
    public void ExternVariable_WithInitializer_IsRejected()
    {
        const string source = """
            @[extern(c, name: "eidos_test_extern_rejected")]
            mut counter : Int := 0;

            main :: Unit -> Int
            {
                _ => 0
            }
            """;

        var result = RunSourceAtLlvm(source, "extern_variable_with_initializer.eidos");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "E3050");
    }

    [Fact]
    public void ExternVariable_MissingTypeAnnotation_IsRejected()
    {
        const string source = """
            @[extern(c, name: "eidos_test_extern_untyped")]
            mut counter;

            main :: Unit -> Int
            {
                _ => 0
            }
            """;

        var result = RunSourceAtLlvm(source, "extern_variable_untyped.eidos");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "E3050");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void ExternVariable_CounterGlobal_NativeSmoke_ReadsAndWritesCSymbol()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            @[extern(c, name: "eidos_test_extern_counter")]
            mut counter : Int;

            bump :: Unit -> Int
            {
                _ => {
                    counter := counter + 2;
                    counter
                }
            }

            main :: Unit -> Int
            {
                _ => bump()
            }
            """;

        const string cSource = """
            long long eidos_test_extern_counter = 40;
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "extern_variable_counter_native.eidos",
            "extern_variable_counter_native",
            nativeCSource: cSource);

        Assert.Equal(42, execution.ExitCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void ExternVariable_NativeSmoke_PersistsAcrossCalls()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            @[extern(c, name: "eidos_test_extern_total")]
            mut total : Int;

            add :: Int -> Int
            {
                value => {
                    total := total + value;
                    total
                }
            }

            main :: Unit -> Int
            {
                _ => {
                    add(3);
                    add(4)
                }
            }
            """;

        const string cSource = """
            long long eidos_test_extern_total = 0;
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "extern_variable_persist_native.eidos",
            "extern_variable_persist_native",
            nativeCSource: cSource);

        Assert.Equal(7, execution.ExitCode);
    }
}
