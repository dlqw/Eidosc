using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void DoExpression_Option_NativeSmoke_BindsSequentialValues()
    {
        const string source = """
            import Std.Option
            import Std.Monad

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
            import Std.Seq
            import Std.Monad

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
            import Std.Seq
            import Std.Monad

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
            import Std.Option
            import Std.Monad

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
            import Std.Option
            import Std.Monad
            import Std.Seq

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
}

