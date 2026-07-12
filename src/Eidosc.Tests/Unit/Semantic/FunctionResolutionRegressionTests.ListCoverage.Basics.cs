using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_ListMatchWithEmptyOnly_ReportsNonExhaustiveWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1, 2]
    {
        [] => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_non_exhaustive_list_rest.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing list cases:", warning.Message, StringComparison.Ordinal);
        Assert.Contains("[_, ..]", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses: [_, ..]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case traces: [_, ..] [list-len>=:1]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case trace groups:", StringComparison.Ordinal) &&
                    note.Contains("list=list-len>=:1 ([_, ..])", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=list;key=list-len>=:1;display=[_, ..]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithEmptyAndRest_IsExhaustive()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1, 2]
    {
        [] => 0,
        [..] => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_exhaustive_list_rest.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithCoveredFixedLengthBranch_ReportsCoveredUnreachableWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1, 2]
    {
        [_, ..] => 1,
        [_] => 2,
        [] => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_covered_branch_unreachable.eidos",
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
            note => note.Contains("Covered-case witnesses: [_]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: [_] <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithTopLevelNotPattern_ReportsMissingEmptyListCase()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1, 2]
    {
        ![] => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_top_level_not_non_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing list cases:", warning.Message, StringComparison.Ordinal);
        Assert.Contains("[]", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("[_, ..]", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithTopLevelOrPatternExhaustive_DoesNotReportCoverageWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1, 2]
    {
        [] | [_, ..] => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_top_level_or_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithUnsatisfiableAndPattern_ReportsUnreachableWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1, 2]
    {
        [] & [_, ..] => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_unsatisfiable_and.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #1", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("unsatisfiable in finite coverage space", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithBoolElementCases_ReportsMissingBoolElementWitness()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [true] => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_bool_element_non_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing list cases:", warning.Message, StringComparison.Ordinal);
        Assert.Contains("[false]", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses:", StringComparison.Ordinal) &&
                    note.Contains("[false]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithBoolElementCasesAndRest_IsExhaustive()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [true] => 1,
        [false] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_bool_element_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithBoolElementCoveredBranch_ReportsCoveredUnreachableWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [true | false] => 1,
        [true] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_bool_element_covered_branch_unreachable.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("#2", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case witnesses: [true]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: [true] <- #2", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithDistinctIntElementBranches_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [1] => 1,
        [2] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_int_element_distinct_branches.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithDistinctCharElementBranches_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match ['b']
    {
        [] => 0,
        ['a'] => 1,
        ['b'] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_char_element_distinct_branches.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithCharElementRangeBranch_ReportsCoveredLiteralBranchWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match ['b']
    {
        [] => 0,
        ['a'..'c'] => 1,
        ['b'] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_char_element_range_covered_branch.eidos",
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
    public void CompilationPipeline_ListMatchWithDistinctTwoIntElementBranches_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [2, 1]
    {
        [] => 0,
        [1, 2] => 1,
        [2, 1] => 2,
        [_, _] => 3,
        [_, ..] => 4
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_two_int_element_distinct_branches.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithTwoIntElementRangeBranch_ReportsCoveredLiteralBranchWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [2, 3]
    {
        [] => 0,
        [1..2, 3..4] => 1,
        [1, 3] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_two_int_element_range_covered_branch.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedTwoIntElementBranchCoveringLiteral_ReportsCoveredUnreachableWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1, 2]
    {
        [] => 0,
        [1 as a, 2 as b] when a == 1 && b == 2 => 1,
        [1, 2] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_two_int_literal_covered_branch.eidos",
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
    public void CompilationPipeline_ListMatchWithIntRestPrefix_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [2, 1]
    {
        [] => 0,
        [1, ..] => 1,
        [2, 1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_int_rest_prefix_distinct_branch.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedIntRestPrefixContradiction_ReportsUnsatisfiableWarning()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1, 2]
    {
        [] => 0,
        [1 as x, ..] when x != 1 => 1,
        [1, 2] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_int_rest_prefix_unsatisfiable.eidos",
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
