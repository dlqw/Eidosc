using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_BoolMatchWithUnsatisfiableAndPattern_ReportsUnreachableWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true & false => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_unsatisfiable_and.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #1", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("unsatisfiable", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithUnsatisfiableAndPattern_ReportsUnreachableWarning()
    {
        const string source = """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, false) & (false, true) => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_unsatisfiable_and.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #1", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("unsatisfiable", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithUnsatisfiableAndNotPattern_ReportsUnreachableWarning()
    {
        const string source = """
OptionI :: type {
    Some:: type(Int) , None :: type {}
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(v) & !Some(_) => v,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_unsatisfiable_and_not.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #1", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("unsatisfiable", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternBodyNonExhaustive_ReportsWarning()
    {
        const string source = """
classify :: Bool -> Int
{
    true => 1
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_non_exhaustive_bool.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4200" &&
                          diagnostic.Message.Contains("function 'classify'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternBodyWithCurriedAdtHead_TracksConstructorCoverage()
    {
        const string source = """
OptionString :: type { SomeString:: type(String) , NoneString :: type {} }

optionStringMap :: OptionString -> (String -> String) -> OptionString
{
    SomeString(value) => mapper => SomeString(mapper(value)),
    NoneString() => _ => NoneString()
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_curried_adt_head_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternBodyWithCurriedTupledAdtParameters_TracksProductCoverage()
    {
        const string source = """
OptionInt :: type { SomeInt:: type(Int) , NoneInt :: type {} }

zip_sum :: OptionInt -> OptionInt -> Int
{
    SomeInt(left) => SomeInt(right) => left + right,
    SomeInt(_) => NoneInt() => 0,
    NoneInt() => _ => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_curried_tupled_adt_product_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternBodyWithThreeCurriedAdtParameters_TracksProductCoverage()
    {
        const string source = """
OptionInt :: type { SomeInt:: type(Int) , NoneInt :: type {} }

zip3_sum :: OptionInt -> OptionInt -> OptionInt -> Int
{
    SomeInt(first) => SomeInt(second) => SomeInt(third) => first + second + third,
    SomeInt(firstRest) => SomeInt(secondRest) => NoneInt() => 0,
    SomeInt(firstRest) => NoneInt() => _ => 0,
    NoneInt() => _ => _ => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_curried_triple_adt_product_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternBodyWithCurriedAdtWildcardThenConstructor_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
Link :: type { Empty :: type {} , Node:: type(Int) }

merge :: Link -> Link -> Int
{
    Empty() => right => 1,
    left => Empty() => 2,
    left => right => 3
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_curried_adt_wildcard_then_constructor_no_false_covered.eidos",
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
    public void CompilationPipeline_FunctionPatternBodyWithCurriedAdtHeadNonExhaustive_ReportsMissingConstructor()
    {
        const string source = """
OptionString :: type { SomeString:: type(String) , NoneString :: type {} }

optionStringMap :: OptionString -> (String -> String) -> OptionString
{
    SomeString(value) => mapper => SomeString(mapper(value))
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_curried_adt_head_non_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("function 'optionStringMap'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("missing constructors: NoneString", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses:", StringComparison.Ordinal) &&
                    note.Contains("NoneString", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternBodyWithGuardedMixedUncertainViewOrLiteral_CoversLiteralBranch()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: Bool -> Int
{
    ((!(normalize_bool -> true)) | true) when true => 1,
    true => 2,
    _ => 3
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_bool_guarded_mixed_uncertain_view_or_literal_covered.eidos",
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
            note => note.Contains("Covered-case witnesses: true", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: true <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternBodyWithGuardedMixedUncertainViewNotAndDeterministicNoMatchInner_CoversLiteralBranch()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: Bool -> Int
{
    (!((normalize_bool -> true) & false)) when true => 1,
    true => 2,
    _ => 3
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_bool_guarded_mixed_uncertain_view_not_and_deterministic_nomatch_inner_covered.eidos",
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
            note => note.Contains("Covered-case witnesses: true", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: true <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternBodyWithGuardedMixedUncertainViewNotAndUncertainInner_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: Bool -> Int
{
    (!((normalize_bool -> true) & true)) when true => 1,
    true => 2,
    _ => 3
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_bool_guarded_mixed_uncertain_view_not_and_uncertain_inner_no_false_covered.eidos",
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
    public void CompilationPipeline_FunctionPatternBodyWithGuardedMixedUncertainTupleBoolViewOrLiteral_CoversLiteralBranch()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: (Bool, Bool) -> Int
{
    (((normalize_bool -> true) | true), false) when true => 1,
    (true, false) => 2,
    _ => 3
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_tuple_bool_guarded_mixed_uncertain_view_or_literal_covered.eidos",
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
            note => note.Contains("Covered-case witnesses: (true, false)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: (true, false) <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternBodyWithGuardedMixedUncertainTupleBoolViewNotAndDeterministicNoMatchInner_CoversLiteralBranch()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: (Bool, Bool) -> Int
{
    ((!((normalize_bool -> true) & false)), false) when true => 1,
    (true, false) => 2,
    _ => 3
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_tuple_bool_guarded_mixed_uncertain_view_not_and_deterministic_nomatch_inner_covered.eidos",
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
            note => note.Contains("Covered-case witnesses: (true, false)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: (true, false) <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_FunctionPatternBodyWithGuardedMixedUncertainTupleBoolViewNotAndUncertainInner_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize_bool :: Bool -> Bool
{
    b => b
}

classify :: (Bool, Bool) -> Int
{
    ((!((normalize_bool -> true) & true)), false) when true => 1,
    (true, false) => 2,
    _ => 3
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "function_body_tuple_bool_guarded_mixed_uncertain_view_not_and_uncertain_inner_no_false_covered.eidos",
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
    public void CompilationPipeline_LetTuplePattern_BindsVariablesForFollowingExpressions()
    {
        const string source = """
sum_pair :: (Int, Int) -> Int
{
    pair => {
        (a, b) := pair;
        a + b
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "let_tuple_pattern_binds_variables.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined identifier", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_LetRefutablePattern_ReportsIrrefutableRequirement()
    {
        const string source = """
invalid_let :: Int -> Int
{
    x => {
        1 := x;
        x
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "let_refutable_pattern_irrefutable_requirement.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains(
                              "expected expression",
                              StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_IfLetPattern_BindsVariablesInsideThenBranch()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }

unwrap_or_zero :: Option[Int] -> Int
{
    value => if let Some(n) = value then { n } else { 0 }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "if_let_pattern_then_scope.eidos",
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined identifier", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CompilationPipeline_IfLetPattern_DoesNotLeakBindingsIntoElseBranch()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }

bad_if_let :: Option[Int] -> Int
{
    value => if let Some(n) = value then { n } else { n }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "if_let_pattern_no_else_leak.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined identifier 'n'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_WhileLetPattern_BindsVariablesInsideLoopBody()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }

accumulate :: Option[Int] -> Int
{
    value => {
        mut total := 0;
        while let Some(n) = value then {
            total := total + n;
        };
        total
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "while_let_pattern_loop_scope.eidos",
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined identifier", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CompilationPipeline_WhileLetPattern_DoesNotLeakBindingsOutsideLoopBody()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }

bad_while_let :: Option[Int] -> Int
{
    value => {
        while let Some(n) = value then { n };
        n
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "while_let_pattern_no_scope_leak.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined identifier 'n'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_OrPatternWithBindingModeMismatch_ReportsError()
    {
        const string source = """
demo :: Int -> Int
{
    x => match x
    {
        ref a | mref a => a,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "or_pattern_binding_mode_mismatch.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains(
                "Or-pattern alternatives must use the same binding mode",
                StringComparison.Ordinal) &&
                          diagnostic.Message.Contains(
                              "expected ref but got mref",
                StringComparison.Ordinal));
    }
}
