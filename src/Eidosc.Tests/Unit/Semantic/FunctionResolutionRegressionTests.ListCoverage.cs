using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_ListMatchWithUnknownGuardedNonFiniteView_ReportsReasonTaggedUnresolvedHint()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

is_true :: Bool -> Bool
{
    v => v
}

classify :: Int -> Int
{
    _ => match [1]
    {
        [] => 0,
        [(normalize -> (0..100))] when is_true(true) => 1,
        [1] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unknown_nonfinite_view_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing list cases:", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #2@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("reason=guard:not-provable", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("list:view-inner-nonfinite", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "list:view-inner-nonfinite@pattern/list-element#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithUnknownGuardedNestedNonFiniteView_ReportsPathTaggedUnresolvedHint()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

is_true :: Bool -> Bool
{
    v => v
}

classify :: Int -> Int
{
    _ => match [1]
    {
        [] => 0,
        [((normalize -> (0..100)) | 1)] when is_true(true) => 1,
        [1] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unknown_nested_nonfinite_view_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #2@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "list:view-inner-nonfinite@pattern/list-element#1/alternative#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithUnknownGuardedNestedUncertainViewNot_ReportsPathTaggedUnresolvedHint()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [!((normalize -> (2..3)) & 2)] when probe(true) => 1,
        [2] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unknown_nested_uncertain_view_not_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #2@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("reason=guard:not-provable", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("list:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "list:view-inner-uncertain@pattern/list-element#1/not-inner/conjunct#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithUnknownGuardedAlternativeUncertainView_ReportsPathTaggedUnresolvedHint()
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
    _ => match [true]
    {
        [((normalize_bool -> true) | true)] when probe(true) => 1,
        [true] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unknown_alternative_uncertain_view_hint.eidos",
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
            note => note.Contains("[[true]]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("list:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "list:view-inner-uncertain@pattern/list-element#1/alternative#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithUnknownGuardedAlternativeUncertainCharView_ReportsCharDomainTaggedUnresolvedHint()
    {
        const string source = """
normalize_char :: Char -> Char
{
    c => c
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match ['a']
    {
        [((normalize_char -> 'a') | (normalize_char -> 'b'))] when probe(true) => 1,
        [((normalize_char -> 'a') | (normalize_char -> 'b'))] when true => 2,
        ['a'] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unknown_alternative_uncertain_char_view_hint.eidos",
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
            note => note.Contains("['a']", StringComparison.Ordinal));
        Assert.DoesNotContain(
            warning.Notes,
            note => note.Contains("[97]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("list:target-domain-char", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("list:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "list:view-inner-uncertain@pattern/list-element#1/alternative#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithUnknownGuardedNestedCharUncertainViewNot_ReportsPathTaggedUnresolvedHint()
    {
        const string source = """
normalize_char :: Char -> Char
{
    c => c
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match ['b']
    {
        [!((normalize_char -> ('a'..'c')) & 'b')] when probe(true) => 1,
        ['b'] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unknown_nested_char_uncertain_view_not_hint.eidos",
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
            note => note.Contains("list:target-domain-char", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("list:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "list:view-inner-uncertain@pattern/list-element#1/not-inner/conjunct#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithUnknownGuardedNonFiniteCharView_ReportsPathTaggedUnresolvedHint()
    {
        const string source = """
normalize_char :: Char -> Char
{
    c => c
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match ['b']
    {
        [(normalize_char -> ('A'..'z'))] when probe(true) => 1,
        ['b'] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unknown_nonfinite_char_view_hint.eidos",
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
            note => note.Contains("list:target-domain-char", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("list:view-inner-nonfinite", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "list:view-inner-nonfinite@pattern/list-element#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithUnknownGuardedUncertainCharView_EmitsCharDomainSuppressionAndUnresolvedHints()
    {
        const string source = """
normalize_char :: Char -> Char
{
    c => c
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match ['a']
    {
        [((normalize_char -> 'a') | (normalize_char -> 'b'))] when probe(true) => 1,
        [((normalize_char -> 'a') | (normalize_char -> 'b'))] when true => 2,
        ['a'] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unknown_uncertain_char_view_suppressed_and_unresolved_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #1@", StringComparison.Ordinal) &&
                    note.Contains("list:target-domain-char", StringComparison.Ordinal) &&
                    note.Contains("list:view-inner-uncertain@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#3 <- #2", StringComparison.Ordinal) &&
                    note.Contains("list:target-domain-char", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit-case1-key:char:'a'", StringComparison.Ordinal) &&
                    note.Contains("reason: list-guarded-uncertain-view", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Suppressed-covered trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=list;branch=3;covering=2;reason=", StringComparison.Ordinal) &&
                    note.Contains("list:target-domain-char", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit-case1-key:char:'a'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithUnknownGuardedUncertainCommaCharView_EscapesSuppressionKvReasonToken()
    {
        const string source = """
normalize_char :: Char -> Char
{
    c => c
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [',']
    {
        [((normalize_char -> ',') | (normalize_char -> '.'))] when probe(true) => 1,
        [((normalize_char -> ',') | (normalize_char -> '.'))] when true => 2,
        [','] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unknown_uncertain_comma_char_view_suppressed_and_unresolved_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#3 <- #2", StringComparison.Ordinal) &&
                    note.Contains("list:target-domain-char", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit-case1-key:char:','", StringComparison.Ordinal) &&
                    note.Contains("reason: list-guarded-uncertain-view", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Suppressed-covered trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=list;branch=3;covering=2;reason=", StringComparison.Ordinal) &&
                    note.Contains("list:target-domain-char", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit-case1-key:char:'\\,'", StringComparison.Ordinal) &&
                    note.Contains(",list:no-deterministic-nonview-hit-case1-key:char:'\\,'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithUnknownGuardedUncertainView_EmitsSuppressionAndUnresolvedHints()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

probe :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [3]
    {
        [] => 0,
        [((normalize -> (1..2)) | (normalize -> 3))] when probe(true) => 1,
        [((normalize -> (1..2)) | (normalize -> 3))] when true => 2,
        [3] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unknown_uncertain_view_suppressed_and_unresolved_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #2@", StringComparison.Ordinal) &&
                    note.Contains("list:target-domain-int", StringComparison.Ordinal) &&
                    note.Contains("list:view-inner-uncertain@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#4 <- #3", StringComparison.Ordinal) &&
                    note.Contains("list:target-domain-int", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Suppressed-covered trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=list;branch=4;covering=3;reason=", StringComparison.Ordinal) &&
                    note.Contains("list:target-domain-int", StringComparison.Ordinal));
    }
}
