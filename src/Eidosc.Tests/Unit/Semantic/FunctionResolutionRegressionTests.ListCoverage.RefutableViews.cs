using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_ListMatchWithViewPatternBranch_DoesNotDisableIntFiniteCoverage()
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
        [] => 9,
        [(normalize -> 0)] => 0,
        [1 | 2] => 1,
        [3] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_view_branch_int_finite_coverage.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #4", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithViewPatternBranchAndGuardedIntContradiction_ReportsUnsatisfiableWarning()
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
        [(normalize -> 0)] => 9,
        [1 as x] when x != 1 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_view_branch_guarded_int_unsat.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("unsatisfiable in finite coverage space", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithIrrefutableViewPattern_CoversLiteralBranch()
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
        [] => 9,
        [(normalize -> _)] => 0,
        [3] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_irrefutable_view_covers_literal_branch.eidos",
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
    public void CompilationPipeline_ListMatchWithViewPatternInnerIntExhaustiveAlgebra_CoversLiteralBranch()
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
        [(normalize -> (1 | !1))] => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_view_inner_int_algebra_exhaustive.eidos",
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
    public void CompilationPipeline_ListMatchWithViewPatternInnerIntUnsatisfiableAlgebra_ReportsUnsatisfiableWarning()
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
        [(normalize -> (1 & !1))] => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_view_inner_int_algebra_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithViewPatternInnerBoolExhaustiveAlgebra_CoversLiteralBranch()
    {
        const string source = """
normalize :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [(normalize -> (true | !true))] => 1,
        [true] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_view_inner_bool_algebra_exhaustive.eidos",
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
    public void CompilationPipeline_ListMatchWithViewPatternInnerBoolUnsatisfiableAlgebra_ReportsUnsatisfiableWarning()
    {
        const string source = """
normalize :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [(normalize -> (true & !true))] => 1,
        [true] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_view_inner_bool_algebra_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewBoolBindingContradiction_ReportsUnsatisfiableWarning()
    {
        const string source = """
normalize :: Bool -> Bool
{
    b => b
}

classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [(normalize -> (true as b))] when !b => 1,
        [true] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_bool_binding_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewIntBindingContradiction_ReportsUnsatisfiableWarning()
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
        [(normalize -> (1 as x))] when x != 1 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_int_binding_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewIntFiniteSetContradiction_ReportsUnsatisfiableWarning()
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
        [(normalize -> ((1 | 2) as x))] when x == 3 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_int_finite_set_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewIntAlternativeBindingContradiction_ReportsUnsatisfiableWarning()
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
        [(normalize -> ((1 as x) | (2 as x)))] when x == 3 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_int_alternative_binding_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewIntAlternativeArithmeticContradiction_ReportsUnsatisfiableWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [1, 0, 0]
    {
        [] => 0,
        [(normalize -> ((1 as x) | (2 as x))), _, _] when x + 1 == 4 => 1,
        [1, 0, 0] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_int_alternative_arithmetic_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewMixedOrContradiction_ReportsUnsatisfiableWarning()
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
        [(normalize -> ((((1 as x) | (2 as x)) | ((3..4) as x))))] when x == 5 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_mixed_or_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewMixedAndNotContradiction_ReportsUnsatisfiableWarning()
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
        [(normalize -> ((((1 | 2) & !2) as x)))] when x != 1 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_mixed_and_not_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewMixedRangeNotContradiction_ReportsUnsatisfiableWarning()
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
        [(normalize -> ((((1..3) & !(1 | 2)) as x)))] when x != 3 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_mixed_range_not_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewIntOtherAndNotContradiction_ReportsUnsatisfiableWarning()
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
        [(normalize -> ((!2 & !3) as n))] when n == 2 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_int_other_and_not_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewIntOtherOrNarrowingContradiction_ReportsUnsatisfiableWarning()
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
        [(normalize -> (((!2) | ((!2 & !3))) as n))] when n == 2 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_int_other_or_narrowing_unsat.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewMixedRangeOr_DoesNotReportFalseCoveredWarning()
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
        [(((normalize -> (1 | 2)) | (3..4)) as x)] when x == 1 => 1,
        [1] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_mixed_range_or_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #3", StringComparison.Ordinal));
    }
}
