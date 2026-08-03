using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void ResultMapHigherOrderClosure_NativeSmoke_ReturnsMappedValue()
    {
        const string source = """
            import std.Result

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
            import std.Result

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
            import std.Option
            import std.Result

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
            import std.Result

            positive_result :: Int -> Result.With[String, Int]
            {
                x => if x > 0 then { Ok(x + 1) } else { Err("bad") }
            }

            main :: Unit -> Int
            {
                _ => {
                    input: Result.With[String, Int] := Ok(2);
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
            import std.Result

            add :: Int -> Int -> Int
            {
                left => right => left + right
            }

            main :: Unit -> Int
            {
                _ => {
                    pureValue: Result.With[String, Int] := Result.pure(5);
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
            import std.Result

            add :: Int -> Int -> Int
            {
                left => right => left + right
            }

            main :: Unit -> Int
            {
                _ => {
                    pureValue: Result.With[String, Int] := Result.pure(5);
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
            import std.Seq
            import std.Result

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
            import std.Seq

            is_small :: Ref[Int] -> Bool
            {
                x => *x <= 2
            }

            main :: Unit -> Int
            {
                _ => {
                    pieces := Seq.partition([1, 2, 3, 4])(is_small);
                    (left, right) := pieces;
                    Seq.len(ref left) + Seq.len(ref right)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "seq_partition_tuple_result.eidos",
            "seq_partition_tuple_result");

        Assert.True(
            execution.ExitCode == 4,
            $"Expected exit code 4, got {execution.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{execution.StandardOutput}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{execution.StandardError}");
    }

    [Fact]
    public void SeqFindAndPartition_BorrowedPredicates_ObserveExactVisitCounts()
    {
        const string source = """
            import std.Seq

            main :: Unit -> Int
            {
                _ => {
                    mut find_calls := 0;
                    found := Seq.find([1, 2, 3, 4])(value => {
                        find_calls = find_calls + 1;
                        *value == 3
                    });
                    mut partition_calls := 0;
                    parts := Seq.partition([1, 2, 3, 4])(value => {
                        partition_calls = partition_calls + 1;
                        *value <= 2
                    });
                    (left, right) := parts;
                    if find_calls == 3 && partition_calls == 4 &&
                        Seq.len(ref left) == 2 && Seq.len(ref right) == 2
                    then { 0 }
                    else { find_calls * 10 + partition_calls }
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "seq_borrowed_predicate_visit_counts.eidos",
            "seq_borrowed_predicate_visit_counts");

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void SeqRetainingOperations_MoveOnlyElements_DoNotRequireClone()
    {
        const string source = """
            import std.Seq

            Item :: type {
                tag :: Int,
                text :: String
            }

            keep_even :: Ref[Item] -> Bool
            {
                item => (*item).tag % 2 == 0
            }

            found_tag :: Option[Item] -> Int
            {
                Some(item) => item.tag,
                None() => 0
            }

            main :: Unit -> Int
            {
                _ => {
                    filtered := Seq.filter([
                        Item { tag: 1, text: "one" },
                        Item { tag: 2, text: "two" },
                        Item { tag: 4, text: "four" }
                    ])(keep_even);
                    found := Seq.find([
                        Item { tag: 1, text: "one" },
                        Item { tag: 2, text: "two" },
                        Item { tag: 4, text: "four" }
                    ])(keep_even);
                    parts := Seq.partition([
                        Item { tag: 1, text: "one" },
                        Item { tag: 2, text: "two" },
                        Item { tag: 4, text: "four" }
                    ])(keep_even);
                    (accepted, rejected) := parts;
                    Seq.len(ref filtered) * 100 + found_tag(found) * 10 +
                        Seq.len(ref accepted) + Seq.len(ref rejected)
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "seq_retaining_move_only_elements.eidos",
            "seq_retaining_move_only_elements");

        Assert.Equal(223, execution.ExitCode);
    }

    [Fact]
    public void SeqMapFilterFold_PureCallbacks_FusesToSingleSourceLoop()
    {
        var result = RunSourceAtMir(
            SeqMapFilterFoldPureSource,
            "seq_map_filter_fold_fusion.eidos",
            enableDetailedProfiling: true);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var module = Assert.IsType<MirModule>(result.MirModule);
        var main = Assert.Single(module.Functions, static function => function.Name == "main");
        var calls = main.BasicBlocks
            .SelectMany(static block => block.Instructions)
            .OfType<MirCall>()
            .ToList();

        Assert.DoesNotContain(calls, static call => call.Function is MirFunctionRef
        {
            CompilerSemanticRole: CompilerSemanticRole.SequenceMap or
                CompilerSemanticRole.SequenceFilter or
                CompilerSemanticRole.SequenceFoldLeft
        });
        Assert.Equal(1, result.ProfilingCounters["Mir.optimizer.sequence.pipelines_formed"]);
        Assert.Equal(2, result.ProfilingCounters["Mir.optimizer.sequence.intermediates_elided"]);
        Assert.Contains(main.Locals, static local => local.Name == "__sequence_index");
        Assert.Contains(result.SubphaseMetrics, static metric =>
            string.Equals(metric.Name, "loop.optimizer.sequence.analyze", StringComparison.Ordinal));
        Assert.Contains(result.SubphaseMetrics, static metric =>
            string.Equals(metric.Name, "loop.optimizer.sequence.plan", StringComparison.Ordinal));
        Assert.Contains(result.SubphaseMetrics, static metric =>
            string.Equals(metric.Name, "loop.optimizer.sequence.rewrite", StringComparison.Ordinal));
    }

    [Fact]
    public void SeqMapFilterFold_PureCallbacks_NativeSmoke_ReturnsChecksum()
    {
        var execution = CompileAndRunSourceAtNative(
            SeqMapFilterFoldPureSource,
            "seq_map_filter_fold_fusion_native.eidos",
            "seq_map_filter_fold_fusion_native");

        Assert.Equal(12, execution.ExitCode);
    }

    [Fact]
    public void SeqMapFilterFold_PanicCapablePredicate_DoesNotFuse()
    {
        const string source = """
            import std.Seq

            increment :: Int -> Int { value => value + 1 }
            is_even :: Ref[Int] -> Bool { value => *value % 2 == 0 }
            add :: Int -> Int -> Int { total, value => total + value }

            main :: Unit -> Int
            {
                Seq.fold_left(Seq.filter(Seq.map([1, 2, 3, 4])(increment))(is_even))(0)(add)
            }
            """;

        var result = RunSourceAtMir(
            source,
            "seq_map_filter_fold_panic_fallback.eidos",
            enableDetailedProfiling: true);

        Assert.True(result.Success);
        Assert.Equal(0, result.ProfilingCounters["Mir.optimizer.sequence.pipelines_formed"]);
        Assert.Equal(1, result.ProfilingCounters["Mir.optimizer.sequence.fallback.panic_or_divergence"]);
        AssertSequencePipelineCallsRemain(result);
    }

    [Fact]
    public void SeqMapFilterFold_DeclaredEffectPredicate_DoesNotFuse()
    {
        const string source = """
            import std.Seq

            Observe :: effect;

            increment :: Int -> Int { value => value + 1 }
            is_large :: Ref[Int] -> Bool need Observe { value => *value > 2 }
            add :: Int -> Int -> Int { total, value => total + value }

            main :: Unit -> Int
            {
                Seq.fold_left(Seq.filter(Seq.map([1, 2, 3, 4])(increment))(is_large))(0)(add)
            }
            """;

        var result = RunSourceAtMir(
            source,
            "seq_map_filter_fold_effect_fallback.eidos",
            enableDetailedProfiling: true);

        Assert.True(result.Success);
        Assert.Equal(0, result.ProfilingCounters["Mir.optimizer.sequence.pipelines_formed"]);
        Assert.True(
            result.ProfilingCounters["Mir.optimizer.sequence.fallback.effect"] == 1,
            string.Join(
                Environment.NewLine,
                result.ProfilingCounters
                    .Where(static pair => pair.Key.Contains("sequence", StringComparison.Ordinal))
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => $"{pair.Key}={pair.Value}")));
        AssertSequencePipelineCallsRemain(result);
    }

    [Fact]
    public void SeqMapFilterFold_RecursivePredicate_DoesNotFuse()
    {
        const string source = """
            import std.Seq

            increment :: Int -> Int { value => value + 1 }
            recurse :: Ref[Int] -> Bool { value => recurse(value) }
            add :: Int -> Int -> Int { total, value => total + value }

            main :: Unit -> Int
            {
                Seq.fold_left(Seq.filter(Seq.map([1, 2, 3, 4])(increment))(recurse))(0)(add)
            }
            """;

        var result = RunSourceAtMir(
            source,
            "seq_map_filter_fold_recursive_fallback.eidos",
            enableDetailedProfiling: true);

        Assert.True(result.Success);
        Assert.Equal(0, result.ProfilingCounters["Mir.optimizer.sequence.pipelines_formed"]);
        Assert.Equal(1, result.ProfilingCounters["Mir.optimizer.sequence.fallback.panic_or_divergence"]);
        AssertSequencePipelineCallsRemain(result);
    }

    [Fact]
    public void SeqMapFilterFold_LocalClosure_DoesNotFuse()
    {
        const string source = """
            import std.Seq

            increment :: Int -> Int { value => value + 1 }
            add :: Int -> Int -> Int { total, value => total + value }

            main :: Unit -> Int
            {
                predicate := { value => *value > 2 };
                Seq.fold_left(Seq.filter(Seq.map([1, 2, 3, 4])(increment))(predicate))(0)(add)
            }
            """;

        var result = RunSourceAtMir(
            source,
            "seq_map_filter_fold_closure_fallback.eidos",
            enableDetailedProfiling: true);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal(0, result.ProfilingCounters["Mir.optimizer.sequence.pipelines_formed"]);
        Assert.Equal(1, result.ProfilingCounters["Mir.optimizer.sequence.fallback.shape_after_map"]);
        AssertSequencePipelineCallsRemain(result);
    }

    [Fact]
    public void SeqMapFilterFold_MoveOnlyMappedElements_DoesNotFuse()
    {
        const string source = """
            import std.Seq

            Item :: type { tag :: Int, text :: String }

            make_item :: Int -> Item { value => Item { tag: value, text: "item" } }
            is_large :: Ref[Item] -> Bool { item => (*item).tag > 2 }
            add_tag :: Int -> Item -> Int { total, item => total + item.tag }

            main :: Unit -> Int
            {
                Seq.fold_left(Seq.filter(Seq.map([1, 2, 3, 4])(make_item))(is_large))(0)(add_tag)
            }
            """;

        var result = RunSourceAtMir(
            source,
            "seq_map_filter_fold_move_only_fallback.eidos",
            enableDetailedProfiling: true);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal(0, result.ProfilingCounters["Mir.optimizer.sequence.pipelines_formed"]);
        Assert.Equal(1, result.ProfilingCounters["Mir.optimizer.sequence.fallback.ownership"]);
        AssertSequencePipelineCallsRemain(result);
    }

    private static void AssertSequencePipelineCallsRemain(CompilationResult result)
    {
        var module = Assert.IsType<MirModule>(result.MirModule);
        var main = Assert.Single(module.Functions, static function => function.Name == "main");
        var roles = main.BasicBlocks
            .SelectMany(static block => block.Instructions)
            .OfType<MirCall>()
            .Select(static call => Assert.IsType<MirFunctionRef>(call.Function).CompilerSemanticRole)
            .ToHashSet();

        Assert.Contains(CompilerSemanticRole.SequenceMap, roles);
        Assert.Contains(CompilerSemanticRole.SequenceFilter, roles);
        Assert.Contains(CompilerSemanticRole.SequenceFoldLeft, roles);
    }

    [Fact]
    public void TupleReturnAndSeqTupleElement_NativeSmoke_PreservesAggregatePayload()
    {
        const string source = """
            import std.Seq
            import std.Option

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
            import std.Option
            import std.Ordering

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
            import std.Option
            import std.Ordering

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
        import std.Result

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

    private const string SeqMapFilterFoldPureSource = """
        import std.Seq

        increment :: Int -> Int { value => value + 1 }
        greater_than_two :: Ref[Int] -> Bool { value => *value > 2 }
        add :: Int -> Int -> Int { total, value => total + value }

        main :: Unit -> Int
        {
            Seq.fold_left(
                Seq.filter(Seq.map([1, 2, 3, 4])(increment))(greater_than_two)
            )(0)(add)
        }
        """;
}

