using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithUnknownGuardedNestedUncertainViewNot_ReportsPathTaggedUnresolvedHint()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match (true, false)
    {
        (!((normalize_bool -> true) & true), false) when probe(true) => 1,
        (true, false) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_unknown_nested_uncertain_view_not_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing tuple bool cases:", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #1@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("reason=guard:not-provable", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("tuple-bool:target-domain-bool", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("tuple-bool:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "tuple-bool:view-inner-uncertain@pattern/tuple#1/not-inner/conjunct#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithUnknownGuardedAlternativeUncertainView_ReportsPathTaggedUnresolvedHint()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match (true, false)
    {
        (((normalize_bool -> true) | true), false) when probe(true) => 1,
        (true, false) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_unknown_alternative_uncertain_view_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #1@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("tuple-bool:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "tuple-bool:view-inner-uncertain@pattern/tuple#1/alternative#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithGuardedMixedUncertainViewOrLiteral_CoversLiteralBranch()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: (Bool, Bool) -> Int
{
    t => match t
    {
        (((normalize_bool -> true) | true), false) when true => 1,
        (true, false) => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_mixed_uncertain_view_or_literal_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case witnesses: (true, false)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: (true, false) <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithUnknownGuardedMixedUncertainViewOrLiteral_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

probe :: (Bool, Bool) -> Bool
{
    t => true
}

classify :: (Bool, Bool) -> Int
{
    t => match t
    {
        (((normalize_bool -> true) | true), false) when probe(t) => 1,
        (true, false) => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_unknown_mixed_uncertain_view_or_literal_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithGuardedMixedUncertainViewNotAndDeterministicNoMatchInner_CoversLiteralBranch()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: (Bool, Bool) -> Int
{
    t => match t
    {
        ((!((normalize_bool -> true) & false)), false) when true => 1,
        (true, false) => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_mixed_uncertain_view_not_and_deterministic_nomatch_inner_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithGuardedMixedUncertainViewNotAndUncertainInner_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: (Bool, Bool) -> Int
{
    t => match t
    {
        ((!((normalize_bool -> true) & true)), false) when true => 1,
        (true, false) => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_mixed_uncertain_view_not_and_uncertain_inner_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithUnknownGuardedNonFiniteView_ReportsPathTaggedUnresolvedHint()
    {
        const string source = """
normalize :: Bool -> Int
{
    b => match b
    {
        true => 1,
        false => 0
    }
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match (true, false)
    {
        ((normalize -> (0..100)), false) when probe(true) => 1,
        (true, false) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_unknown_nonfinite_view_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing tuple bool cases:", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #1@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("reason=guard:not-provable", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("tuple-bool:view-inner-nonfinite", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "tuple-bool:view-inner-nonfinite@pattern/tuple#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithUnknownGuardedNestedNonFiniteView_ReportsPathTaggedUnresolvedHint()
    {
        const string source = """
normalize :: Bool -> Int
{
    b => match b
    {
        true => 1,
        false => 0
    }
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match (true, false)
    {
        (((normalize -> (0..100)) | true), false) when probe(true) => 1,
        (true, false) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_unknown_nested_nonfinite_view_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #1@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("tuple-bool:view-inner-nonfinite", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "tuple-bool:view-inner-nonfinite@pattern/tuple#1/alternative#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithUnknownGuardedExactPattern_UsesPatternDerivedLowerBoundHint()
    {
        const string source = """
probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match (true, false)
    {
        (true, false) when probe(true) => 1,
        (false, false) => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_unknown_exact_pattern_lower_bound_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #1@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("[(true, false)]", StringComparison.Ordinal));
        Assert.DoesNotContain(
            warning.Notes,
            note => note.Contains("[?]", StringComparison.Ordinal));
    }
}
