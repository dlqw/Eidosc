using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void DoExpression_ApplicativeLlvm_UsesStaticEvidenceWithoutDictionariesOrIndirectDispatch()
    {
        const string source = """
            import std.Option
            import std.Monad

            main :: Unit -> Int
            {
                _ => {
                    result := do {
                        x <- Some(2)
                        y <- Some(3)
                        Some(x + y)
                    };
                    Option.unwrap_or(result)(0)
                }
            }
            """;

        var result = RunSourceAtLlvm(
            source,
            StdlibListImportInputFile(),
            enableDetailedProfiling: true);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        var mainBody = ExtractLlvmFunctionBody(llvmIr, "main");
        Assert.Contains("FunctorOption__fmap", mainBody, StringComparison.Ordinal);
        Assert.Contains("ApplicativeOption__apply", mainBody, StringComparison.Ordinal);
        Assert.Contains("MonadOption__bind", mainBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dictionary", mainBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"call[^@\r\n]*%[^\(\r\n]*\(", mainBody);
        Assert.DoesNotContain("@eidos_closure_new", mainBody, StringComparison.Ordinal);
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                mainBody,
                @"call ptr @eidos_closure_init_stack\(",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count);
        Assert.Equal(
            2,
            result.ProfilingCounters.GetValueOrDefault(
                "Borrow.unified_stack_promotion.function_argument_closure_candidates"));
        Assert.Equal(
            2,
            result.ProfilingCounters.GetValueOrDefault(
                "Borrow.unified_stack_promotion.promoted_function_argument_closures"));
    }

    [Fact]
    public void Alternative_EmptyOption_NativeSmoke_ReturnsNone()
    {
        const string source = """
            import std.Option
            import std.Alternative

            main :: Unit -> Int
            {
                _ => {
                    Option.unwrap_or(Alternative.empty(()))(0)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "alternative_empty_option.eidos",
            "alternative_empty_option");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void DoExpression_Option_NativeSmoke_BindsSequentialValues()
    {
        const string source = """
            import std.Option
            import std.Monad

            main :: Unit -> Int
            {
                _ => {
                    result := do {
                        x <- Some(2)
                        y <- Some(3)
                        Some(x + y)
                    };
                    Option.unwrap_or(result)(0)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "do_option_bind.eidos",
            "do_option_bind");

        Assert.Equal(5, execution.ExitCode);
    }

    [Fact]
    public void DoExpression_ListWithSemicolonSeparators_NativeSmoke_BindsSequentialValues()
    {
        const string source = """
            import std.Seq
            import std.Monad

            main :: Unit -> Int
            {
                _ => {
                    values := do {
                        x <- [1, 2];
                        y <- [10, 20];
                        [x + y]
                    };
                    Seq.sum(values)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "do_list_bind.eidos",
            "do_list_bind");

        Assert.Equal(66, execution.ExitCode);
    }

    [Fact]
    public void DoExpression_ListTuplePattern_NativeSmoke_MaterializesAggregateScrutinee()
    {
        const string source = """
            import std.Seq
            import std.Monad

            main :: Unit -> Int
            {
                _ => {
                    values := do {
                        (x, y) <- [(1, 10), (2, 20)];
                        [x + y]
                    };
                    Seq.sum(values)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "do_list_tuple_pattern.eidos",
            "do_list_tuple_pattern");

        Assert.Equal(33, execution.ExitCode);
    }

    [Fact]
    public void DoExpression_LocalLetBinding_NativeSmoke_BindsValueForFollowingItems()
    {
        const string source = """
            import std.Option
            import std.Monad

            main :: Unit -> Int
            {
                _ => {
                    result := do {
                        x := 2;
                        y <- Some(3);
                        Some(x + y)
                    };
                    Option.unwrap_or(result)(0)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "do_local_let_bind.eidos",
            "do_local_let_bind");

        Assert.Equal(5, execution.ExitCode);
    }

    [Fact]
    public void DoExpression_LocalLetCapturedByNestedLambda_NativeSmoke_CapturesAcrossContinuation()
    {
        const string source = """
            import std.Option
            import std.Monad
            import std.Seq

            main :: Unit -> Int
            {
                _ => {
                    result := do {
                        offset := 2;
                        x <- Some(3);
                        add_offset := y => y + offset + x;
                        Some(Seq.sum(Seq.map([1, 2])(add_offset)))
                    };
                    Option.unwrap_or(result)(0)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "do_nested_lambda_capture.eidos",
            "do_nested_lambda_capture");

        Assert.Equal(13, execution.ExitCode);
    }

    [Theory]
    [InlineData(true, 7)]
    [InlineData(false, 0)]
    public void DoExpression_RefutableOptionPattern_NativeSmoke_UsesAlternativeEmpty(
        bool matches,
        int expectedExitCode)
    {
        var inner = matches ? "Some(7)" : "None()";
        var source = $$"""
            import std.Option
            import std.Monad

            main :: Unit -> Int
            {
                _ => {
                    result := do {
                        Some(value) <- Some({{inner}})
                        Some(value)
                    };
                    Option.unwrap_or(result)(0)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            $"do_refutable_option_{matches}.eidos",
            $"do_refutable_option_{matches}");

        Assert.Equal(expectedExitCode, execution.ExitCode);
    }

    [Theory]
    [InlineData(false, "ok_int(3)")]
    [InlineData(true, "ok_int(x + 1)")]
    public void DoExpression_Result_NativeSmoke_PreservesApplicativeAndDependentSemantics(
        bool dependent,
        string secondExpression)
    {
        var source = $$"""
            import std.Result
            import std.Monad

            ok_int :: Int -> Result.With[String, Int]
            {
                value => Ok(value)
            }

            main :: Unit -> Int
            {
                _ => {
                    result := do {
                        x <- ok_int(2)
                        y <- {{secondExpression}}
                        ok_int(x + y)
                    };
                    match result {
                        Ok(value) => value,
                        Err(_) => 0
                    }
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            $"do_result_{dependent}.eidos",
            $"do_result_{dependent}");

        Assert.Equal(5, execution.ExitCode);
    }

    [Fact]
    public void DoExpression_RefutableSeqPattern_NativeSmoke_FiltersFailedBranches()
    {
        const string source = """
            import std.Option
            import std.Seq
            import std.Monad

            maybe :: Int -> Option[Int]
            {
                value => if value > 0 then { Some(value) } else { None() }
            }

            options :: Unit -> Seq[Option[Int]]
            {
                _ => [maybe(1), maybe(0), maybe(2)]
            }

            main :: Unit -> Int
            {
                _ => {
                    values := do {
                        Some(_) <- options();
                        [1]
                    };
                    Seq.sum(values)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "do_refutable_seq.eidos",
            "do_refutable_seq");

        Assert.Equal(2, execution.ExitCode);
    }

    private static string ExtractLlvmFunctionBody(string llvmIr, string functionName)
    {
        var definitionLine = llvmIr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .First(line =>
                line.StartsWith("define ", StringComparison.Ordinal) &&
                line.Contains(functionName, StringComparison.Ordinal));
        var functionStart = llvmIr.IndexOf(definitionLine, StringComparison.Ordinal);
        var nextFunction = llvmIr.IndexOf("\ndefine ", functionStart + definitionLine.Length, StringComparison.Ordinal);
        return nextFunction > functionStart
            ? llvmIr[functionStart..nextFunction]
            : llvmIr[functionStart..];
    }
}

