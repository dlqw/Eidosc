using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedUncertainViewOrWildcard_CoversLiteralBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [((normalize -> (1..2)) | _)] when true => 1,
        [2] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_uncertain_view_or_wildcard_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedUncertainViewOrWildcardAsBinding_CoversLiteralBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [(((normalize -> (1..2)) | _) as x)] when x == 2 => 1,
        [2] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_uncertain_view_or_wildcard_as_binding_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedUncertainViewOrLiteralAsBinding_CoversLiteralBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [(((normalize -> (1..2)) | 2) as x)] when x == 2 => 1,
        [2] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_uncertain_view_or_literal_as_binding_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedUncertainViewOrCharLiteralAsBinding_CoversLiteralBranch()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

classify :: Int -> Int
{
    _ => match ['b']
    {
        [] => 0,
        [(((normalizeChar -> ('a'..'b')) | 'b') as x)] when x == 'b' => 1,
        ['b'] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_uncertain_view_or_char_literal_as_binding_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedUncertainViewOrUnrelatedCharLiteralAsBinding_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

classify :: Int -> Int
{
    _ => match ['b']
    {
        [] => 0,
        [(((normalizeChar -> ('a'..'b')) | 'c') as x)] when x == 'b' => 1,
        ['b'] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_uncertain_view_or_unrelated_char_literal_as_binding_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedNestedUncertainViewOrNestedLiteralAlternative_CoversLiteralBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [((((normalize -> (1..2)) | 2) | 4) as x)] when x == 2 => 1,
        [2] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_nested_uncertain_view_or_nested_literal_alternative_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedNestedUncertainViewOnlyAlternatives_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [3]
    {
        [] => 0,
        [((normalize -> (1..2)) | (normalize -> 3))] when true => 1,
        [3] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_nested_uncertain_view_only_alternatives_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedNestedUncertainViewOnlyAlternatives_EmitsSuppressionTraceNoteOnNonExhaustiveWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [3]
    {
        [] => 0,
        [((normalize -> (1..2)) | (normalize -> 3))] when true => 1,
        [3] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_nested_uncertain_view_only_alternatives_suppressed_covered_note.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#3 <- #2", StringComparison.Ordinal) &&
                    note.Contains("list:target-domain-int", StringComparison.Ordinal) &&
                    note.Contains("list:view-inner-uncertain@", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit-case1", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit-case1-key:int:3", StringComparison.Ordinal) &&
                    note.Contains("reason: list-guarded-uncertain-view", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Suppressed-covered trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=list;branch=3;covering=2;reason=", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedBoolNestedUncertainViewOnlyAlternatives_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [((normalizeBool -> true) | (normalizeBool -> false))] when true => 1,
        [true] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_bool_nested_uncertain_view_only_alternatives_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedBoolNestedUncertainViewOnlyAlternatives_EmitsSuppressionTraceNoteOnNonExhaustiveWarning()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [((normalizeBool -> true) | (normalizeBool -> false))] when true => 1,
        [true] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_bool_nested_uncertain_view_only_alternatives_suppressed_covered_note.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#3 <- #2", StringComparison.Ordinal) &&
                    note.Contains("list:target-domain-bool", StringComparison.Ordinal) &&
                    note.Contains("list:view-inner-uncertain@", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit-case1", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit-case1-key:bool:true", StringComparison.Ordinal) &&
                    note.Contains("reason: list-guarded-uncertain-view", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Suppressed-covered trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=list;branch=3;covering=2;reason=", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedNestedUncertainViewOnlyAlternatives_EmitsMultiCaseSuppressionReasonTags()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [((normalize -> (1..2)) | (normalize -> 3))] when true => 1,
        [(1 | 2)] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_nested_uncertain_view_only_alternatives_multi_case_reason_tags.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#3 <- #2", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit-case1", StringComparison.Ordinal) &&
                    note.Contains("list:no-deterministic-nonview-hit-case2", StringComparison.Ordinal) &&
                    note.Contains("-key:int:1", StringComparison.Ordinal) &&
                    note.Contains("-key:int:2", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedBoolMixedNestedUncertainViewOrLiteral_CoversLiteralBranch()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [false]
    {
        [] => 0,
        [((normalizeBool -> true) | false)] when true => 1,
        [false] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_bool_mixed_nested_uncertain_view_or_literal_covers_literal.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedBoolMixedNestedUncertainViewOrUnrelatedLiteral_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [false]
    {
        [] => 0,
        [((normalizeBool -> true) | true)] when true => 1,
        [false] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_bool_mixed_nested_uncertain_view_or_unrelated_literal_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedNestedUncertainViewNotAndConjunctDeterministicMiss_CoversLiteralBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [3]
    {
        [] => 0,
        [!((normalize -> (2..3)) & 2)] when true => 1,
        [3] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_nested_uncertain_view_not_and_conjunct_hit_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedNestedUncertainViewNotAndConjunctUncertainMiss_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [!((normalize -> (2..3)) & 2)] when true => 1,
        [2] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_nested_uncertain_view_not_and_conjunct_miss_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedUncertainViewNotAndDeterministicBoolNoMatchInnerAsBinding_CoversLiteralBranch()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [((!((normalizeBool -> true) & false)) as f)] when f => 1,
        [true] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_uncertain_view_not_and_deterministic_bool_nomatch_inner_as_binding_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedUncertainViewNotAndUncertainBoolInnerAsBinding_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [((!((normalizeBool -> true) & true)) as f)] when f => 1,
        [true] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_uncertain_view_not_and_uncertain_bool_inner_as_binding_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedUncertainViewNotAndDeterministicCharNoMatchInnerAsBinding_CoversLiteralBranch()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

classify :: Int -> Int
{
    _ => match ['b']
    {
        [] => 0,
        [((!((normalizeChar -> 'a') & 'c')) as t)] when t == 'b' => 1,
        ['b'] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_uncertain_view_not_and_deterministic_char_nomatch_inner_as_binding_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedUncertainViewNotAndUncertainCharInnerAsBinding_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

classify :: Int -> Int
{
    _ => match ['b']
    {
        [] => 0,
        [((!((normalizeChar -> 'a') & 'b')) as t)] when t == 'b' => 1,
        ['b'] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_uncertain_view_not_and_uncertain_char_inner_as_binding_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedUncertainViewOrUnrelatedLiteralAsBinding_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [1]
    {
        [] => 0,
        [(((normalize -> (1..2)) | 3) as x)] when x == 1 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_uncertain_view_or_unrelated_literal_as_binding_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedNestedUncertainViewOrUnrelatedLiteral_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [4]
    {
        [] => 0,
        [((!(normalize -> (1..2))) | 3)] when true => 1,
        [4] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_nested_uncertain_view_or_unrelated_literal_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedMixedNestedUncertainViewOrLiteral_CoversLiteralBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [3]
    {
        [] => 0,
        [((!(normalize -> (1..2))) | 3)] when true => 1,
        [3] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_mixed_nested_uncertain_view_or_literal_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewFiniteInner_DoesNotReportFalseCoveredLiteralWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [2]
    {
        [(normalize -> (1 as x))] when x == 1 => 1,
        [2] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_finite_inner_no_false_covered.eidos",
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
    public void CompilationPipeline_ListMatchWithNestedViewAlwaysOrPattern_CoversLiteralBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [1]
    {
        [] => 0,
        [((normalize -> (1 | !1)) | 2)] => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_nested_view_always_or_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("#2", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithNestedViewNeverAndPattern_ReportsUnsatisfiableWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [1]
    {
        [] => 0,
        [((normalize -> (1 & !1)) & 1)] => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_nested_view_never_and_unsat.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("unsatisfiable in finite coverage space", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedBooleanNegationPair_ReportsUnsatisfiableWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    b => match [1]
    {
        [] => 0,
        [1] when b && !b => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_boolean_negation_pair_unsat.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("unsatisfiable in finite coverage space", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithIntElementCases_ReportsOtherBucketWitness()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [1] => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_int_element_other_bucket_non_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing list cases:", warning.Message, StringComparison.Ordinal);
        Assert.Contains("[<other>]", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses:", StringComparison.Ordinal) &&
                    note.Contains("[<other>]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
                    note => note.Contains("Missing-case traces:", StringComparison.Ordinal) &&
                            note.Contains("list-elem:i:*", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedIntBranchCoveringLiteral_ReportsCoveredUnreachableWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1]
    {
        [1 as x] when x == 1 => 1,
        [1] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_int_literal_covered_branch.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var hasCoveredWarning = result.Diagnostics.Any(
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("#1", StringComparison.Ordinal));
        Assert.True(
            hasCoveredWarning,
            string.Join(" || ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedIntContradiction_ReportsUnsatisfiableWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1]
    {
        [1 as x] when x == 1 && x != 1 => 1,
        [1] => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_int_unsatisfiable.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var hasUnsatisfiableWarning = result.Diagnostics.Any(
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #1", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("unsatisfiable in finite coverage space", StringComparison.Ordinal));
        Assert.True(
            hasUnsatisfiableWarning,
            string.Join(" || ", result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Message}")));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedBoolVariableBranches_DoesNotReportCoverageWarnings()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [x] when x => 1,
        [x] when !x => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_bool_variable_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedUnsatisfiableBoolBranch_ReportsUnreachableWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [x] when x && !x => 1,
        [_] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_unsat_bool_branch.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("unsatisfiable in finite coverage space", StringComparison.Ordinal));
    }
}
