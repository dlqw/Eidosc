using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_MatchAfterUnguardedCatchAll_ReportsUnreachablePatternWarning()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        _ => 0,
        1 => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_unreachable_after_catchall.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("branch #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithSingleLiteral_ReportsNonExhaustiveWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_non_exhaustive_bool.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing bool cases: false", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses: false", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case traces: false [bool:false]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case trace groups: bool=bool:false", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=bool;key=bool:false;display=false", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithTrueAndFalse_DoesNotReportNonExhaustiveWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_exhaustive_bool.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithDuplicateLiteralBranch_ReportsCoveredUnreachableWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true => 1,
        true => 2,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_duplicate_literal_branch_unreachable.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("#1", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case witnesses: true", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: true <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithFullyCoveredOrPattern_ReportsTracePerCoveredCase()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true => 1,
        false => 0,
        true | false => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_or_fully_covered_branch_unreachable.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("#1", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("#2", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case witnesses: false, true", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces:", StringComparison.Ordinal) &&
                    note.Contains("true <- #1", StringComparison.Ordinal) &&
                    note.Contains("false <- #2", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithUnknownGuardedCatchAll_ReportsCoverageNoteForGuards()
    {
        const string source = """
is_true :: Bool -> Bool
{
    v => v
}

classify :: Bool -> Int
{
    x => match x
    {
        v when is_true(v) => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guarded_unknown_branch_non_exhaustive_bool.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing bool cases: true", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Guarded branches are not considered exhaustive by coverage analysis.", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("unresolved predicates were conservatively excluded from exact coverage: #1", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #1@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("guard:not-provable", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithUnknownGuardedExactPattern_UsesPatternDerivedLowerBoundHint()
    {
        const string source = """
probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match true
    {
        true when probe(true) => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_guarded_unknown_exact_pattern_lower_bound_hint.eidos",
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
            note => note.Contains("[true]", StringComparison.Ordinal));
        Assert.DoesNotContain(
            warning.Notes,
            note => note.Contains("[?]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithUnknownGuardedNonFiniteView_ReportsPathTaggedUnresolvedHint()
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
    _ => match true
    {
        (normalize -> (0..100)) when probe(true) => 1,
        true => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_guarded_unknown_nonfinite_view_hint.eidos",
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
            note => note.Contains("reason=guard:not-provable", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("bool:target-domain-bool", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("bool:view-inner-nonfinite", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("bool:view-inner-nonfinite@pattern/view-inner", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithUnknownGuardedNestedUncertainViewNot_ReportsPathTaggedUnresolvedHint()
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
    _ => match true
    {
        !((normalize_bool -> true) & true) when probe(true) => 1,
        true => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_guarded_unknown_nested_uncertain_view_not_hint.eidos",
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
            note => note.Contains("reason=guard:not-provable", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("bool:target-domain-bool", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("bool:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "bool:view-inner-uncertain@pattern/not-inner/conjunct#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithUnknownGuardedAlternativeUncertainView_ReportsPathTaggedUnresolvedHint()
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
    _ => match true
    {
        ((normalize_bool -> true) | true) when probe(true) => 1,
        true => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_guarded_unknown_alternative_uncertain_view_hint.eidos",
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
            note => note.Contains("bool:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "bool:view-inner-uncertain@pattern/alternative#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithGuardedMixedUncertainViewOrLiteral_CoversLiteralBranch()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: Bool -> Int
{
    x => match x
    {
        ((!(normalize_bool -> true)) | true) when true => 1,
        true => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_guarded_mixed_uncertain_view_or_literal_covered.eidos",
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
            note => note.Contains("Covered-case witnesses: true", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: true <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithUnknownGuardedMixedUncertainViewOrLiteral_DoesNotReportFalseCoveredWarning()
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

classify :: Bool -> Int
{
    x => match x
    {
        ((!(normalize_bool -> true)) | true) when probe(x) => 1,
        true => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_guarded_unknown_mixed_uncertain_view_or_literal_no_false_covered.eidos",
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
    public void CompilationPipeline_BoolMatchWithGuardedMixedUncertainViewNotAndDeterministicNoMatchInner_CoversLiteralBranch()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: Bool -> Int
{
    x => match x
    {
        (!((normalize_bool -> true) & false)) when true => 1,
        true => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_guarded_mixed_uncertain_view_not_and_deterministic_nomatch_inner_covered.eidos",
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
    public void CompilationPipeline_BoolMatchWithGuardedMixedUncertainViewNotAndUncertainInner_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: Bool -> Int
{
    x => match x
    {
        (!((normalize_bool -> true) & true)) when true => 1,
        true => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_guarded_mixed_uncertain_view_not_and_uncertain_inner_no_false_covered.eidos",
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
}
