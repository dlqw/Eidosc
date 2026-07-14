using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_BoolMatchWithGuardedBindingPredicate_DoesNotReportNonExhaustiveWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        v when v => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guarded_binding_predicate_bool.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithWildcardGuardedByMatchedIdentifier_DoesNotReportNonExhaustiveWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        _ when x => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guarded_wildcard_with_subject_identifier.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithLiteralGuardedByMatchedIdentifier_DoesNotReportNonExhaustiveWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true when x => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guarded_literal_with_subject_identifier.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithVarPatternGuardedByMatchedIdentifier_DoesNotReportNonExhaustiveWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        v when x => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guarded_var_with_subject_identifier.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_MatchPatternGuardBinding_ResolvesGuardBindingInBranchBody()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        _ when Some(n) <- x => n,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guard_binding_scope_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined identifier 'n'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_MatchPatternGuardBinding_DoesNotLeakBindingToOtherBranch()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        _ when Some(n) <- x => n,
        _ => n
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guard_binding_scope_leak.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined identifier 'n'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternGuardBinding_ResolvesGuardBindingInBranchBody()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x when Some(n) <- x => n,
    _ => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_pattern_guard_binding_scope_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined identifier 'n'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternMultipleWhenGuards_ResolvesEarlierGuardBindingInLaterGuard()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x when Some(n) <- x when n > 0 => n,
    _ => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_pattern_multiple_when_binding_scope_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined identifier 'n'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternGuardBinding_DoesNotLeakBindingToOtherBranch()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x when Some(n) <- x => n,
    _ => n
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_pattern_guard_binding_scope_leak.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined identifier 'n'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithPatternAwareFalseGuard_ReportsUnreachableBranchWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true when !x => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guarded_pattern_aware_false_branch.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4200" &&
                          diagnostic.Message.Contains("missing bool cases: true", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("guard is constant false", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithExhaustiveLiteralOrPatternGuardedByMatchedIdentifier_DoesNotReportCoverageWarnings()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true | false when x => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guarded_or_literal_with_subject_identifier.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithExhaustiveLiteralOrPatternUnsatGuard_ReportsUnreachableBranchWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true | false when !x && x => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guarded_or_literal_unsat_guard.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4200" &&
                          diagnostic.Message.Contains("missing bool cases: true", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("guard is constant false", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithConstTrueGuard_TreatsBranchAsUnguardedForCoverage()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        _ when true => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_const_true_guard_coverage.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithConstFalseGuard_DoesNotCountBranchCoverage()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true when false => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_const_false_guard_coverage.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var nonExhaustive = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing bool cases: true", nonExhaustive.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            nonExhaustive.Notes,
            note => note.Contains("Guarded branches are not considered exhaustive by coverage analysis.", StringComparison.Ordinal));

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("guard is constant false", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithConstFalseCompositeGuard_ReportsUnreachableBranchWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true when true && false => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_const_false_composite_guard_coverage.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("guard is constant false", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithShortCircuitTrueGuard_TreatsBranchAsUnguardedForCoverage()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        _ when x || true => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_short_circuit_true_guard_coverage.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithShortCircuitFalseGuard_ReportsUnreachableBranchWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true when x && false => 1,
        false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_short_circuit_false_guard_coverage.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4200" &&
                          diagnostic.Message.Contains("missing bool cases: true", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("guard is constant false", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_BoolMatchWithGuardedEqualityPair_DoesNotReportNonExhaustiveWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        v when v == true => 1,
        v when v == false => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_guarded_bool_equality_pair.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }
}
