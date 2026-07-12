using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedMixedUncertainViewNotAndDeterministicNoMatchInner_CoversBranch()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

PairB :: type {
    PairB{flag: Bool, right: Int} | None
}

classify :: PairB -> Int
{
    x => match x
    {
        None => 0,
        PairB{flag: ((!((normalizeBool -> true) & false)) as f), right: 4} when f => 1,
        PairB{right: 4} & PairB{flag: true} => 2,
        PairB{flag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_uncertain_view_not_and_deterministic_nomatch_inner_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedUncertainViewNotAndUncertainInner_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

PairB :: type {
    PairB{flag: Bool, right: Int} | None
}

classify :: PairB -> Int
{
    x => match x
    {
        None => 0,
        PairB{flag: ((!((normalizeBool -> true) & true)) as f), right: 4} when f => 1,
        PairB{right: 4} & PairB{flag: true} => 2,
        PairB{flag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_uncertain_view_not_and_uncertain_inner_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedUncertainViewCharNotAndDeterministicNoMatchInner_CoversBranch()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} | None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: ((!((normalizeChar -> 'a') & 'c')) as t), right: 4} when t == 'b' => 1,
        PairC{right: 4} & PairC{tag: 'b'} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_uncertain_view_char_not_and_deterministic_nomatch_inner_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedUncertainViewCharNotAndUncertainInner_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} | None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: ((!((normalizeChar -> 'a') & 'b')) as t), right: 4} when t == 'b' => 1,
        PairC{right: 4} & PairC{tag: 'b'} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_uncertain_view_char_not_and_uncertain_inner_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewOrOtherCtorFallback_SuppressesOnlyRelevantCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some((normalize -> 1)) | None when true => 1,
        Some(2) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_or_other_ctor_fallback_target_aware.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewOrOtherCtorFallback_DoesNotDisableSuppressionForMultiCtorTarget()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some((normalize -> (0..100))) | None when true => 1,
        _ => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_or_other_ctor_fallback_multi_ctor_target_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewAndConjunctEmbeddedNonViewHit_CoversLiteralBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some((((normalize -> 1) | 2) & _)) when true => 1,
        Some(2) => 2,
        Some(_) => 3,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_and_conjunct_embedded_nonview_hit_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewAndConjunctEmbeddedNonViewMiss_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some((((normalize -> 1) | 3) & _)) when true => 1,
        Some(2) => 2,
        Some(_) => 3,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_and_conjunct_embedded_nonview_miss_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewNotAndConjunctDeterministicMiss_CoversLiteralBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(!((normalize -> (2..3)) & 2)) when true => 1,
        Some(3) => 2,
        Some(_) => 3,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_not_and_conjunct_hit_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewNotAndConjunctUncertainMiss_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(!((normalize -> (2..3)) & 2)) when true => 1,
        Some(2) => 2,
        Some(_) => 3,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_not_and_conjunct_miss_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedUnknownLowerBoundConstructor_CoversFollowingConstructorBranch()
    {
        const string source = """
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
        (Some((true as b)) | (None as b)) when b || probe(b) => 1,
        Some(2) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_unknown_lower_bound_constructor_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case lower-bound traces:", StringComparison.Ordinal) &&
                    note.Contains("Some(...)", StringComparison.Ordinal) &&
                    note.Contains("<- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedUnknownWithoutLowerBound_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
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
        (Some((true as b)) | (None as b)) when (!b) && probe(b) => 1,
        Some(2) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_unknown_no_lower_bound_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedIrrefutableViewBranch_StillReportsCoveredLiteralWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some((normalize -> _)) when true => 1,
        Some(2) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_irrefutable_view_still_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }
}
