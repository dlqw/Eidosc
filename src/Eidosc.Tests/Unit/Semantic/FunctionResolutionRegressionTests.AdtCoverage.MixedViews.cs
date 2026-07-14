using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrUnrelatedLiteral_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        None => 0,
        Some(((!(normalize -> (1..2))) | 3)) when true => 1,
        Some(4) => 2,
        Some(_) => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_unrelated_literal_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrLiteral_CoversLiteralBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        None => 0,
        Some(((!(normalize -> (1..2))) | 3)) when true => 1,
        Some(3) => 2,
        Some(_) => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_literal_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrRange_CoversRangeBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        None => 0,
        Some(((!(normalize -> (1..2))) | (3..4))) when true => 1,
        Some(3..4) => 2,
        Some(_) => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_range_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrPartialLiteral_DoesNotReportFalseCoveredRangeWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        None => 0,
        Some(((!(normalize -> (1..2))) | 3)) when true => 1,
        Some(3..4) => 2,
        Some(_) => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_partial_literal_no_false_covered_range.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrRangeOnNamedField_CoversRangeBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionN :: type {
    Some{value: Int} , None
}

classify :: OptionN -> Int
{
    x => match x
    {
        None => 0,
        Some{value: ((!(normalize -> (1..2))) | (3..4))} when true => 1,
        Some{value: 3..4} => 2,
        Some{value: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_range_named_field_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrPartialLiteralOnNamedField_DoesNotReportFalseCoveredRangeWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionN :: type {
    Some{value: Int} , None
}

classify :: OptionN -> Int
{
    x => match x
    {
        None => 0,
        Some{value: ((!(normalize -> (1..2))) | 3)} when true => 1,
        Some{value: 3..4} => 2,
        Some{value: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_partial_literal_named_field_no_false_covered_range.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrRange_AndTargetOrderSwapped_CoversRangeBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        None => 0,
        Some(((!(normalize -> (1..2))) | (3..4))) when true => 1,
        Some(_) & Some(3..4) => 2,
        Some(_) => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_range_and_target_order_swapped_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOnNamedFields_AndTargetSplit_CoversBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

PairN :: type {
    PairN{left: Int, right: Int} , None
}

classify :: PairN -> Int
{
    x => match x
    {
        None => 0,
        PairN{left: ((!(normalize -> (1..2))) | 3), right: 4} when true => 1,
        PairN{right: 4} & PairN{left: 3} => 2,
        PairN{left: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_named_fields_and_target_split_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOnNamedFields_AndTargetSplitWithDisjointAlternatives_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

PairN :: type {
    PairN{left: Int, right: Int} , None
}

classify :: PairN -> Int
{
    x => match x
    {
        None => 0,
        PairN{left: ((!(normalize -> (1..2))) | 3), right: 5} |
        PairN{left: 2, right: 4} when true => 1,
        PairN{right: 4} & PairN{left: 3} => 2,
        PairN{left: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_named_fields_and_target_split_disjoint_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewAndExtraFieldConstraint_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

PairN :: type {
    PairN{left: Int, right: Int} , None
}

classify :: PairN -> Int
{
    x => match x
    {
        None => 0,
        PairN{left: ((!(normalize -> (1..2))) | 3), right: 5} when true => 1,
        PairN{left: 3} => 2,
        PairN{left: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_extra_field_constraint_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewAndConjunctSplit_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

PairN :: type {
    PairN{left: Int, right: Int} , None
}

classify :: PairN -> Int
{
    x => match x
    {
        None => 0,
        PairN{left: ((!(normalize -> (1..2))) | 3), right: 4} &
        PairN{left: 2, right: 4} when true => 1,
        PairN{left: 3, right: 4} => 2,
        PairN{left: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_conjunct_split_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrAlternativesUnion_CoversRangeBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

PairN :: type {
    PairN{left: Int, right: Int} , None
}

classify :: PairN -> Int
{
    x => match x
    {
        None => 0,
        PairN{left: ((!(normalize -> (1..2))) | 3), right: 4} |
        PairN{left: ((!(normalize -> (1..2))) | 4), right: 4} when true => 1,
        PairN{right: 4} & PairN{left: 3..4} => 2,
        PairN{left: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_alternatives_union_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrAlternativesPartialUnion_DoesNotReportFalseCoveredRangeWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

PairN :: type {
    PairN{left: Int, right: Int} , None
}

classify :: PairN -> Int
{
    x => match x
    {
        None => 0,
        PairN{left: ((!(normalize -> (1..2))) | 3), right: 4} |
        PairN{left: ((!(normalize -> (1..2))) | 5), right: 4} when true => 1,
        PairN{right: 4} & PairN{left: 3..4} => 2,
        PairN{left: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_alternatives_partial_union_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrBoolAlternativesUnion_CoversBranch()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

PairB :: type {
    PairB{flag: Bool, right: Int} , None
}

classify :: PairB -> Int
{
    x => match x
    {
        None => 0,
        PairB{flag: ((!(normalizeBool -> true)) | true), right: 4} |
        PairB{flag: false, right: 4} when true => 1,
        PairB{right: 4} & PairB{flag: (true | false)} => 2,
        PairB{flag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_bool_alternatives_union_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrBoolAlternativesPartialUnion_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

PairB :: type {
    PairB{flag: Bool, right: Int} , None
}

classify :: PairB -> Int
{
    x => match x
    {
        None => 0,
        PairB{flag: ((!(normalizeBool -> true)) | true), right: 4} |
        PairB{flag: true, right: 4} when true => 1,
        PairB{right: 4} & PairB{flag: (true | false)} => 2,
        PairB{flag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_bool_alternatives_partial_union_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrCharAlternativesUnion_CoversBranch()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: ((!(normalizeChar -> 'a')) | 'a'), right: 4} |
        PairC{tag: 'b', right: 4} when true => 1,
        PairC{right: 4} & PairC{tag: ('a' | 'b')} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_char_alternatives_union_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrCharAlternativesPartialUnion_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: ((!(normalizeChar -> 'a')) | 'a'), right: 4} |
        PairC{tag: 'c', right: 4} when true => 1,
        PairC{right: 4} & PairC{tag: ('a' | 'b')} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_char_alternatives_partial_union_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrCharLiteralAsBinding_CoversBranch()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: (((normalizeChar -> ('a'..'b')) | 'b') as t), right: 4} when t == 'b' => 1,
        PairC{right: 4} & PairC{tag: 'b'} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_char_literal_as_binding_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedNestedUncertainViewOrUnrelatedCharLiteralAsBinding_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: (((normalizeChar -> ('a'..'b')) | 'c') as t), right: 4} when t == 'b' => 1,
        PairC{right: 4} & PairC{tag: 'b'} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_nested_uncertain_view_or_unrelated_char_literal_as_binding_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedTopLevelOrAndNamedShorthand_CoversBranch()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        (PairC{tag: (((normalizeChar -> ('a'..'b')) | 'b') as t), right} & PairC{tag: 'a', right: _}) |
        PairC{tag: ('b' as t), right} when t == 'b' && right == 4 => 1,
        PairC{tag: 'b', right: 4} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_top_level_or_and_named_shorthand_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedTopLevelOrAndNamedShorthandUnrelatedAlternative_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        (PairC{tag: (((normalizeChar -> ('a'..'b')) | 'b') as t), right} & PairC{tag: 'a', right: _}) |
        PairC{tag: ('c' as t), right} when t == 'b' && right == 4 => 1,
        PairC{tag: 'b', right: 4} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_top_level_or_and_named_shorthand_unrelated_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedTopLevelAndNamedShorthand_CoversBranch()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: (((normalizeChar -> ('a'..'b')) | 'b') as t), right} &
        PairC{tag: 'b', right: 4} when t == 'b' && right == 4 => 1,
        PairC{tag: 'b', right: 4} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_top_level_and_named_shorthand_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedUncertainViewOrCharAlternativesRangeTargetAndGuardDisjunction_CoversRangeBranch()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: (((normalizeChar -> ('a'..'b')) as t) | ('a' as t) | ('b' as t)), right: 4} when t == 'a' || t == 'b' => 1,
        PairC{right: 4} & PairC{tag: ('a' | 'b')} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_uncertain_view_or_char_alternatives_range_target_and_guard_disjunction_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedUncertainViewOrCharAlternativesRangeTargetAndPartialGuard_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: (((normalizeChar -> ('a'..'b')) as t) | ('a' as t) | ('b' as t)), right: 4} when t == 'a' => 1,
        PairC{right: 4} & PairC{tag: ('a' | 'b')} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_uncertain_view_or_char_alternatives_range_target_and_partial_guard_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedUncertainViewOrDeterministicHitAndIncompatibleAlternative_CoversBranch()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: ((!(normalizeChar -> 'b')) as t) | ('b' as t), right: 4} when t == 'b' => 1,
        PairC{right: 4} & PairC{tag: 'b'} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_uncertain_view_or_deterministic_hit_with_incompatible_alternative_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedUncertainViewOrIncompatibleAlternativeWithoutDeterministicHit_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

PairC :: type {
    PairC{tag: Char, right: Int} , None
}

classify :: PairC -> Int
{
    x => match x
    {
        None => 0,
        PairC{tag: ((!(normalizeChar -> 'b')) as t) | ('c' as t), right: 4} when t == 'b' => 1,
        PairC{right: 4} & PairC{tag: 'b'} => 2,
        PairC{tag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_uncertain_view_or_incompatible_alternative_without_deterministic_hit_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedUncertainViewOrDeterministicBoolHitAndIncompatibleAlternative_CoversBranch()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

PairB :: type {
    PairB{flag: Bool, right: Int} , None
}

classify :: PairB -> Int
{
    x => match x
    {
        None => 0,
        PairB{flag: ((!(normalizeBool -> true)) as f), right: 4} |
        PairB{flag: (false as f), right: 4} |
        PairB{flag: (true as f), right: 4} when f => 1,
        PairB{right: 4} & PairB{flag: true} => 2,
        PairB{flag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_uncertain_view_or_deterministic_bool_hit_with_incompatible_alternative_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedMixedUncertainViewOrIncompatibleBoolAlternativeWithoutDeterministicHit_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

PairB :: type {
    PairB{flag: Bool, right: Int} , None
}

classify :: PairB -> Int
{
    x => match x
    {
        None => 0,
        PairB{flag: ((!(normalizeBool -> true)) as f), right: 4} |
        PairB{flag: (false as f), right: 4} when f => 1,
        PairB{right: 4} & PairB{flag: true} => 2,
        PairB{flag: _, right: _} => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_mixed_uncertain_view_or_incompatible_bool_alternative_without_deterministic_hit_no_false_covered.eidos",
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

}
