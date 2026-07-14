using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void ResultMapHigherOrderClosure_NativeSmoke_ReturnsMappedValue()
    {
        const string source = """
            import Std.Result

            inc :: Int -> Int
            {
                x => x + 1
            }

            main :: Unit -> Int
            {
                _ => {
                    input: Result[Int, String] := Ok(1);
                    Result.unwrap_or(Result.map(input)(inc))(0)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "result_map_hof.eidos",
            "result_map_hof");

        Assert.Equal(2, execution.ExitCode);
    }

    [Fact]
    public void ResultApplyCurriedPartial_NativeSmoke_ReturnsAppliedValue()
    {
        const string source = """
            import Std.Result

            add :: Int -> Int -> Int
            {
                left => right => left + right
            }

            main :: Unit -> Int
            {
                _ => {
                    applyFunction: Result[Int -> Int, String] := Ok(add(20));
                    applyInput: Result[Int, String] := Ok(4);
                    Result.unwrap_or(Result.apply(applyFunction)(applyInput))(0)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "result_apply_partial.eidos",
            "result_apply_partial");

        Assert.Equal(24, execution.ExitCode);
    }

    [Fact]
    public void ResultShow_NativeSmoke_ShowsOkAndErrValues()
    {
        var execution = CompileAndRunSourceAtNative(
            ResultShowSource,
            "result_show.eidos",
            "result_show");

        Assert.Equal(2, execution.ExitCode);
    }

    [Fact]
    public void NestedCtorPattern_NativeSmoke_ShortCircuitsBeforeReadingFields()
    {
        const string source = """
            import Std.Option
            import Std.Result

            main :: Unit -> Int
            {
                _ => {
                    optNoneInput: Option[Result[Int, String]] := None();

                    if Option.is_none(Result.unwrap_or(Result.transpose_option(optNoneInput))(Some(99))) then { 9 } else { 0 }
                }
            }
            """;

        for (var iteration = 0; iteration < 5; iteration++)
        {
            var execution = CompileAndRunSourceAtNative(
                source,
                $"nested_ctor_pattern_short_circuit_{iteration}.eidos",
                $"nested_ctor_pattern_short_circuit_{iteration}");

            Assert.Equal(9, execution.ExitCode);
        }
    }

    [Fact]
    public void ResultTraverse_WithResultApplicative_NativeSmoke_ReturnsInnerValue()
    {
        const string source = """
            import Std.Result

            positive_result :: Int -> Result.ResultWith[String, Int]
            {
                x => if x > 0 then { Ok(x + 1) } else { Err("bad") }
            }

            main :: Unit -> Int
            {
                _ => {
                    input: Result.ResultWith[String, Int] := Ok(2);
                    match Result.traverse(input)(positive_result)
                    {
                        Ok(inner) => Result.unwrap_or(inner)(0),
                        Err(_) => 0
                    }
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "result_traverse_result_applicative.eidos",
            "result_traverse_result_applicative");

        Assert.Equal(3, execution.ExitCode);
    }

    [Fact]
    public void ResultPureThenApply_WithCurriedUnwrapOr_NativeSmoke_ReturnsCombinedValue()
    {
        const string source = """
            import Std.Result

            add :: Int -> Int -> Int
            {
                left => right => left + right
            }

            main :: Unit -> Int
            {
                _ => {
                    pureValue: Result.ResultWith[String, Int] := Result.pure(5);
                    pureCollapsed := Result.unwrap_or(pureValue)(0);
                    applyFunction: Result[Int -> Int, String] := Ok(add(20));
                    applyInput: Result[Int, String] := Ok(4);
                    applied := Result.apply(applyFunction)(applyInput);
                    appliedCollapsed := Result.unwrap_or(applied)(0);
                    pureCollapsed + appliedCollapsed
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "result_pure_apply_curried_unwrap.eidos",
            "result_pure_apply_curried_unwrap");

        Assert.Equal(29, execution.ExitCode);
    }

    [Fact]
    public void ResultSequence_AfterApply_NativeSmoke_ReturnsCombinedValue()
    {
        const string source = """
            import Std.Result

            add :: Int -> Int -> Int
            {
                left => right => left + right
            }

            main :: Unit -> Int
            {
                _ => {
                    pureValue: Result.ResultWith[String, Int] := Result.pure(5);
                    pureCollapsed := Result.unwrap_or(pureValue)(0);
                    applyFunction: Result[Int -> Int, String] := Ok(add(20));
                    applyInput: Result[Int, String] := Ok(4);
                    nestedSequenceInput: Result[Result[Int, String], String] := Ok(Ok(7));
                    applied := Result.apply(applyFunction)(applyInput);
                    appliedCollapsed := Result.unwrap_or(applied)(0);
                    sequencedValue := match Result.sequence(nestedSequenceInput)
                    {
                        Ok(inner) => Result.unwrap_or(inner)(0),
                        Err(_) => 0
                    };
                    pureCollapsed + appliedCollapsed + sequencedValue
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "result_sequence_after_apply.eidos",
            "result_sequence_after_apply");

        Assert.Equal(36, execution.ExitCode);
    }

    [Fact]
    public void SeqSequence_WithResultApplicative_NativeSmoke_ReturnsHead()
    {
        const string source = """
            import Std.Seq
            import Std.Result

            collapse_seq_result :: Result[Seq[Int], String] -> Int
            {
                Ok(values) => Seq.head_or(values)(0),
                Err(_) => 0
            }

            main :: Unit -> Int
            {
                _ => collapse_seq_result(Seq.sequence([Ok(2), Ok(3)]))
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "seq_sequence_result_applicative.eidos",
            "seq_sequence_result_applicative");

        Assert.Equal(2, execution.ExitCode);
    }

    [Fact]
    public void SeqPartition_WithTupleResult_NativeSmoke_ReturnsPartitionSizes()
    {
        const string source = """
            import Std.Seq

            is_small :: Int -> Bool
            {
                x => x <= 2
            }

            main :: Unit -> Int
            {
                _ => {
                    pieces := Seq.partition([1, 2, 3, 4])(is_small);
                    (left, right) := pieces;
                    Seq.len(left) + Seq.len(right)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "seq_partition_tuple_result.eidos",
            "seq_partition_tuple_result");

        Assert.Equal(4, execution.ExitCode);
    }

    [Fact]
    public void TupleReturnAndSeqTupleElement_NativeSmoke_PreservesAggregatePayload()
    {
        const string source = """
            import Std.Seq
            import Std.Option

            choose_pair :: Bool -> (Int, Int) -> (Int, Int) -> (Int, Int)
            {
                flag => left => right => if flag then { left } else { right }
            }

            main :: Unit -> Int
            {
                _ => {
                    chosen := choose_pair(true)((0, 7))((-1, -1));
                    (chosenIndex, chosenValue) := chosen;
                    pairs := [(0, 7), (1, 8), (2, 9)];
                    first := Option.unwrap_or(Seq.get_opt(pairs)(0))((-1, -1));
                    (firstIndex, firstValue) := first;
                    chosenIndex * 10 + chosenValue + firstIndex * 10 + firstValue
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "tuple_return_list_tuple_payload.eidos",
            "tuple_return_list_tuple_payload");

        Assert.Equal(14, execution.ExitCode);
    }

    [Fact]
    public void OptionShowSome_NativeSmoke_ReturnsTrue()
    {
        const string source = """
            import Std.Option
            import Std.Ordering

            main :: Unit -> Int
            {
                _ => if Option.show(Some(8)) == "Some(8)" then { 1 } else { 0 }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "option_show_some.eidos",
            "option_show_some");

        Assert.Equal(1, execution.ExitCode);
    }

    [Fact]
    public void OptionZipMapOr_WithTuplePatternFunction_NativeSmoke_ReturnsSum()
    {
        const string source = """
            import Std.Option
            import Std.Ordering

            pair_sum :: (Int, Int) -> Int
            {
                (left, right) => left + right
            }

            main :: Unit -> Int
            {
                _ => {
                    base: Option[Int] := Some(1);
                    Option.map_or(Option.zip(base)(Some(2)))(0)(pair_sum)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "option_zip_map_or_tuple_function.eidos",
            "option_zip_map_or_tuple_function");

        Assert.Equal(3, execution.ExitCode);
    }

    private const string ResultShowSource = """
        import Std.Result

        main :: Unit -> Int
        {
            _ => {
                ok: Result[Int, String] := Ok(3);
                err: Result[Int, String] := Err("oops");
                shownOk := if Result.show(ok) == "Ok(3)" then { 1 } else { 0 };
                shownErr := if Result.show(err) == "Err(oops)" then { 1 } else { 0 };
                shownOk + shownErr
            }
        }
        """;
}

