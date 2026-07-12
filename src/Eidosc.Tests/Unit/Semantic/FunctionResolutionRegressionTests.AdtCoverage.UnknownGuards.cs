using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_AdtMatchWithUnknownGuardedNonFiniteView_ReportsPathTaggedUnresolvedHint()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

is_true :: Bool -> Bool
{
    b => b
}

OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some((normalize -> (0..100))) when is_true(true) => 1,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_unknown_nonfinite_view_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing constructors: Some", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #1@", StringComparison.Ordinal) &&
                    note.Contains("[Some]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("adt:pattern-or-guard-nonfinite", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("adt:view-inner-nonfinite", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "adt:view-inner-nonfinite@pattern/positional#1/view-inner",
                StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("guard:not-provable", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithUnknownGuardedNestedNonFiniteView_ReportsPathTaggedUnresolvedHint()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

is_true :: Bool -> Bool
{
    b => b
}

OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(((normalize -> (0..100)) | 1)) when is_true(true) => 1,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_unknown_nested_nonfinite_view_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing constructors: Some", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #1@", StringComparison.Ordinal) &&
                    note.Contains("[Some]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "adt:view-inner-nonfinite@pattern/positional#1/alternative#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithUnknownGuardedNestedUncertainViewNot_ReportsPathTaggedUnresolvedHint()
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

OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(!((normalize -> (2..3)) & 2)) when probe(true) => 1,
        Some(2) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_unknown_nested_uncertain_view_not_hint.eidos",
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
            note => note.Contains("adt:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "adt:view-inner-uncertain@pattern/positional#1/not-inner/conjunct#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithUnknownGuardedNestedBoolUncertainViewNot_ReportsPathTaggedUnresolvedHint()
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

OptionB :: type {
    Some(Bool) | None
}

classify :: OptionB -> Int
{
    x => match x
    {
        Some(!((normalize_bool -> true) & true)) when probe(true) => 1,
        Some(true) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_unknown_nested_bool_uncertain_view_not_hint.eidos",
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
            note => note.Contains("adt:target-domain-bool", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("adt:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "adt:view-inner-uncertain@pattern/positional#1/not-inner/conjunct#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithUnknownGuardedAlternativeUncertainView_ReportsPathTaggedUnresolvedHint()
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

OptionB :: type {
    Some(Bool) | None
}

classify :: OptionB -> Int
{
    x => match x
    {
        Some(((normalize_bool -> true) | true)) when probe(true) => 1,
        Some(true) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_unknown_alternative_uncertain_view_hint.eidos",
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
            note => note.Contains("adt:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "adt:view-inner-uncertain@pattern/positional#1/alternative#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithUnknownGuardedUncertainCharView_EmitsCharDomainSuppressionAndUnresolvedHints()
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

OptionC :: type {
    Some(Char) | None
}

classify :: OptionC -> Int
{
    x => match x
    {
        Some(((normalize_char -> 'a') | (normalize_char -> 'b'))) when probe(true) => 1,
        Some(((normalize_char -> 'a') | (normalize_char -> 'b'))) when true => 2,
        Some('a') => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_unknown_uncertain_char_view_suppressed_and_unresolved_hint.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Unresolved-guard branch hints: #1@", StringComparison.Ordinal) &&
                    note.Contains("adt:target-domain-char", StringComparison.Ordinal) &&
                    note.Contains("adt:view-inner-uncertain@", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#3 <- #2", StringComparison.Ordinal) &&
                    note.Contains("adt:target-domain-char", StringComparison.Ordinal) &&
                    note.Contains("reason: adt-guarded-refutable-view", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Suppressed-covered trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=adt;branch=3;covering=2;reason=", StringComparison.Ordinal) &&
                    note.Contains("adt:target-domain-char", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithUnknownGuardedNestedCharUncertainViewNot_ReportsPathTaggedUnresolvedHint()
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

OptionC :: type {
    Some(Char) | None
}

classify :: OptionC -> Int
{
    x => match x
    {
        Some(!((normalize_char -> ('a'..'c')) & 'b')) when probe(true) => 1,
        Some('b') => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_unknown_nested_char_uncertain_view_not_hint.eidos",
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
            note => note.Contains("adt:target-domain-char", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("adt:view-inner-uncertain", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "adt:view-inner-uncertain@pattern/positional#1/not-inner/conjunct#1/view-inner",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithUnknownGuardedNonFiniteCharView_ReportsPathTaggedUnresolvedHint()
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

OptionC :: type {
    Some(Char) | None
}

classify :: OptionC -> Int
{
    x => match x
    {
        Some((normalize_char -> ('A'..'z'))) when probe(true) => 1,
        Some('b') => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_unknown_nonfinite_char_view_hint.eidos",
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
            note => note.Contains("adt:target-domain-char", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("adt:view-inner-nonfinite", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains(
                "adt:view-inner-nonfinite@pattern/positional#1/view-inner",
                StringComparison.Ordinal));
    }
}
