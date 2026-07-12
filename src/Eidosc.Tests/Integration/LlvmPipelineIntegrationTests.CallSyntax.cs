using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void CallSyntax_DotChainAndBacktickInfix_NativeSmoke_ReturnsExpectedValue()
    {
        const string source = """
            inc :: Int -> Int
            {
                x => x + 1
            }

            double :: Int -> Int
            {
                x => x + x
            }

            add :: Int -> Int -> Int
            {
                left => right => left + right
            }

            main :: Unit -> Int
            {
                _ => {
                    chained := 3.inc.double;
                    infixed := 4 `add` 5;
                    chained + infixed
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "call_syntax_dot_infix.eidos",
            "call_syntax_dot_infix");

        Assert.Equal(17, execution.ExitCode);
    }

    [Fact]
    public void CallSyntax_CommaCallSugarAndHigherOrderPartial_NativeSmoke_ReturnsExpectedValue()
    {
        const string source = """
            sum3 :: Int -> Int -> Int -> Int
            {
                a => b => c => a + b + c
            }

            inc :: Int -> Int
            {
                x => x + 1
            }

            double :: Int -> Int
            {
                x => x + x
            }

            select_first[A] :: A -> A -> A -> A
            {
                first => second => third => first
            }

            main :: Unit -> Int
            {
                _ => {
                    add_three := sum3(1, 2);
                    choose_function := select_first(inc)(double);
                    chosen := choose_function(inc);
                    add_three(4) + chosen(10)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "call_syntax_partial_application.eidos",
            "call_syntax_partial_application");

        Assert.Equal(18, execution.ExitCode);
    }

    [Fact]
    public void CallSyntax_CurriedModuleFunctionGroupedCall_NativeSmoke_ReturnsExpectedValue()
    {
        const string source = """
            Pair :: type {
                Pair(Int, Int)
            }

            pair_score :: Int -> Int -> Int
            {
                left => right => match Pair(left, right)
                {
                    Pair(a, b) => a * 10 + b
                }
            }

            main :: Unit -> Int
            {
                _ => pair_score(3, 4)
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "call_syntax_curried_module_grouped.eidos",
            "call_syntax_curried_module_grouped");

        Assert.Equal(34, execution.ExitCode);
    }

    [Fact]
    public void CallSyntax_CurriedFunctionBody_LlvmDoesNotReturnClosure()
    {
        const string source = """
            direct_result :: Int -> Int -> Int
            {
                left => right => right - left
            }

            main :: Unit -> Int
            {
                _ => direct_result(3, 10)
            }
            """;

        var result = RunSourceAtLlvm(source, "call_syntax_curried_direct_result.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        var definitionLine = llvmIr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(static line =>
                line.StartsWith("define external", StringComparison.Ordinal) &&
                line.Contains("direct_result", StringComparison.Ordinal));

        Assert.NotNull(definitionLine);

        var functionStart = llvmIr.IndexOf(definitionLine, StringComparison.Ordinal);
        var nextFunction = llvmIr.IndexOf("\ndefine ", functionStart + definitionLine.Length, StringComparison.Ordinal);
        var functionBody = nextFunction > functionStart
            ? llvmIr[functionStart..nextFunction]
            : llvmIr[functionStart..];

        Assert.DoesNotContain("eidos_closure_new", functionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void CallSyntax_OrdinaryUnitParameterEmptyCall_LowersWithUnitArgument()
    {
        const string source = """
            ping :: Unit -> Int
            {
                _ => 1
            }

            main :: Unit -> Int
            {
                _ => ping()
            }
            """;

        var result = RunSourceAtLlvm(source, "call_syntax_unit_sugar_internal.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Matches(@"define\s+external\s+i64\s+@.*ping.*\(i1", llvmIr);
        Assert.Matches(@"call\s+i64\s+@.*ping.*\(i1\s+0\)", llvmIr);
    }

    [Fact]
    public void CallSyntax_FfiUnitParameterEmptyCall_LowersAsCZeroArgumentCall()
    {
        const string source = """
            @ffi("eidos_test_ping")
            ping :: Unit -> Int need FFI;

            main :: Unit -> Int need FFI
            {
                _ => ping()
            }
            """;

        var result = RunSourceAtLlvm(source, "call_syntax_unit_sugar_ffi.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("declare i64 @eidos_test_ping()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i64 @eidos_test_ping()", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("call i64 @eidos_test_ping(i1", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void CallSyntax_MultipleLeadingUnitEmptyCall_ConsumesOneUnitLayer()
    {
        const string source = """
            ping2 :: Unit -> Unit -> Int
            {
                _ => _ => 2
            }

            main :: Unit -> Int
            {
                _ => ping2()(())
            }
            """;

        var result = RunSourceAtLlvm(source, "call_syntax_unit_sugar_one_layer.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Matches(@"define\s+external\s+i64\s+@.*ping2.*\(i1\s+%[^,]+,\s+i1", llvmIr);
        Assert.Matches(@"call\s+i64\s+@.*ping2.*\(i1\s+0,\s+i1\s+0\)", llvmIr);
    }

    [Fact]
    public void CallSyntax_UnitIfCanOmitElse_LowersWithImplicitUnitElse()
    {
        const string source = """
            draw_if :: Bool -> Unit
            {
                alive => if alive
                then
                {
                    ()
                }
            }

            main :: Unit -> Int
            {
                _ => {
                    draw_if(true);
                    0
                }
            }
            """;

        var result = RunSourceAtLlvm(source, "call_syntax_omit_unit_else.eidos");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.DoesNotContain("missing non-Unit else branch", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void CallSyntax_SemicolonTerminatedUnitIfDoesNotBecomeBlockResult()
    {
        const string source = """
            draw_if :: Bool -> Int
            {
                alive => {
                    if !alive
                    then
                    {
                        ()
                    };
                    7
                }
            }

            main :: Unit -> Int
            {
                _ => draw_if(false)
            }
            """;

        var result = RunSourceAtLlvm(source, "call_syntax_omit_unit_else_statement.eidos");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E5330");
    }

    [Fact]
    public void CallSyntax_EffectfulUnitIfCanOmitElse()
    {
        const string source = """
            @ffi("eidos_test_ping")
            ping :: Unit -> Unit need FFI;

            draw_if :: Bool -> Unit need FFI
            {
                alive => if !alive
                then
                {
                    ping()
                }
            }

            main :: Unit -> Int need FFI
            {
                _ => {
                    draw_if(false);
                    0
                }
            }
            """;

        var result = RunSourceAtLlvm(source, "call_syntax_omit_effectful_unit_else.eidos");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E5330");
    }
}
