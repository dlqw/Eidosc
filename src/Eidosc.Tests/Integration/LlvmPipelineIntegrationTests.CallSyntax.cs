using Eidosc.CodeGen.Llvm;
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
                Pair:: type(Int, Int)
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
            @[extern(c, name: "eidos_test_unit_probe")]
            unit_probe :: Unit -> Int need ffi

            ping :: Unit -> Int need ffi
            {
                _ => unit_probe()
            }

            main :: Unit -> Int need ffi
            {
                _ => ping()
            }
            """;

        var result = RunSourceAtLlvm(source, "call_syntax_unit_sugar_internal.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Matches(@"define\s+external\s+i64\s+@.*ping.*\(i1\s+noundef", llvmIr);
        Assert.Matches(@"call\s+i64\s+@.*ping.*\(i1\s+0\)", llvmIr);

        var llvmModule = Assert.IsType<LlvmModule>(result.LlvmModule);
        var pingName = new NameMangler().MangleFunctionName("", "ping");
        var pingFunction = Assert.Single(
            llvmModule.Functions,
            function => function.Name.StartsWith(pingName, StringComparison.Ordinal));
        Assert.False(HasNounwindAttribute(llvmModule, pingFunction));

        var mainName = new NameMangler().MangleFunctionName("", "main");
        Assert.All(
            llvmModule.Functions.Where(function =>
                function.Name.StartsWith(mainName, StringComparison.Ordinal)),
            function => Assert.False(HasNounwindAttribute(llvmModule, function)));
    }

    private static bool HasNounwindAttribute(LlvmModule module, LlvmFunction function) =>
        function.AttributeIds.Any(attributeId =>
            module.AttributeGroups.Any(group =>
                group.Id == attributeId && group.Attributes.Contains("nounwind", StringComparer.Ordinal)));

    [Fact]
    public void CallAttributes_PureDirectCallChainAndEntryWrapper_AreNounwind()
    {
        const string source = """
            leaf :: Int -> Int
            {
                value => value + 1
            }

            wrapper :: Int -> Int
            {
                value => leaf(value)
            }

            main :: Unit -> Int
            {
                _ => wrapper(41)
            }
            """;

        var result = RunSourceAtLlvm(source, "call_attributes_pure_chain.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var llvmModule = Assert.IsType<LlvmModule>(result.LlvmModule);
        var nounwindAttributeId = Assert.Single(
            llvmModule.AttributeGroups,
            static group => group.Attributes.SequenceEqual(["nounwind"])).Id;
        foreach (var sourceName in new[] { "leaf", "wrapper", "main" })
        {
            var llvmName = new NameMangler().MangleFunctionName("", sourceName);
            var functions = llvmModule.Functions
                .Where(function => function.Name.StartsWith(llvmName, StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(functions);
            Assert.All(
                functions,
                function => Assert.Contains(nounwindAttributeId, function.AttributeIds));
        }
    }

    [Fact]
    public void CallSyntax_FfiUnitParameterEmptyCall_LowersAsCZeroArgumentCall()
    {
        const string source = """

            @[extern(c, name: "eidos_test_ping")]
            ping :: Unit -> Int need ffi

            main :: Unit -> Int need ffi
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
            @[extern(c, name: "eidos_test_unit_probe2")]
            unit_probe2 :: Unit -> Int need ffi

            ping2 :: Unit -> Unit -> Int need ffi
            {
                _ => _ => unit_probe2()
            }

            main :: Unit -> Int need ffi
            {
                _ => ping2()(())
            }
            """;

        var result = RunSourceAtLlvm(source, "call_syntax_unit_sugar_one_layer.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Matches(@"define\s+external\s+i64\s+@.*ping2.*\(i1\s+noundef\s+%[^,]+,\s+i1\s+noundef", llvmIr);
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

            @[extern(c, name: "eidos_test_ping")]
            ping :: Unit -> Unit need ffi

            draw_if :: Bool -> Unit need ffi
            {
                alive => if !alive
                then
                {
                    ping()
                }
            }

            main :: Unit -> Int need ffi
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
