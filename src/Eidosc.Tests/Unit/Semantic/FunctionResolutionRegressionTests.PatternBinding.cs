using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_OrPatternBindingAliases_CanUseDifferentNamesForSameSlot()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        (1 as a) | (2 as b) => a + b,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "or_pattern_binding_aliases.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
    }

    [Fact]
    public void CompilationPipeline_OrPatternBindingSlotMismatch_ReportsAlternativeDiffDetails()
    {
        const string source = """
Pair :: type {
    Pair(Int, Int)
}

classify :: Pair -> Int
{
    x => match x
    {
        Pair(a, _) | Pair(_, b) => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "or_pattern_binding_slot_mismatch_details.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Or-pattern alternatives must bind the same value slots", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("alt#2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("slot#1", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("[context: branch#1 > or-pattern]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_OrPatternBindingAliases_CanComeFromDifferentAlternativeNames()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        (1 as left) | (2 as right) => left,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "or_pattern_binding_alias_names.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
    }

    [Fact]
    public void CompilationPipeline_OrPatternBindings_CanComeFromDifferentAlternativeShapes()
    {
        const string source = """
Tok :: type {
    TokA(Int) | TokB(Int)
}

classify :: Tok -> Int
{
    x => match x
    {
        TokA(n) | TokB(n) => n,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "or_pattern_binding_different_shapes.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Or-pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AndPatternBindings_CanMergeDisjointConjunctBindings()
    {
        const string source = """
Pair :: type {
    Pair(Int, Int)
}

classify :: Pair -> Int
{
    pair => match pair
    {
        Pair(a, _) & Pair(_, b) => a + b,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "and_pattern_binding_merge.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("And-pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AndPatternBindings_DuplicateNameAcrossConjunctsReportsDiagnostic()
    {
        const string source = """
Pair :: type {
    Pair(Int, Int)
}

classify :: Pair -> Int
{
    pair => match pair
    {
        Pair(n, _) & Pair(_, n) => n,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "and_pattern_binding_duplicate.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("And-pattern conjuncts cannot bind the same variable more than once", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("conjunct#2 repeats [n]", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("[context: branch#1 > and-pattern]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_NotPatternBindings_AreRejected()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        !(1 as n) => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "not_pattern_binding_rejected.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Not-pattern cannot bind variables", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("[context: branch#1 > not-pattern]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_NotPatternWithoutBindings_Passes()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        !0 => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "not_pattern_no_binding.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Not-pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_PatternBinding_DuplicateVariableInSamePatternScopeReportsDiagnostic()
    {
        const string source = """
Pair :: type {
    Pair(Int, Int)
}

classify :: Pair -> Int
{
    pair => match pair
    {
        Pair(n, n) => n,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_duplicate_binding_scope.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Pattern variable 'n' is bound more than once in the same scope", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("[context: branch#1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_LegacyCtorViewPattern_ReportsUndefinedConstructor()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    x => match x
    {
        View(normalize, MissingCtor(1)) => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_undefined_ctor_legacy_view_ctor_context.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined constructor 'View'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("[context: branch#1]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_OrPatternLegacyViewCtor_ReportsAlternativeContextPath()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        View(missingViewFn, 0) | 1 => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "or_pattern_undefined_view_fn_context.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined constructor 'View'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("[context: branch#1 > or-pattern > alternative#1]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TuplePatternUndefinedConstructor_ReportsElementContextPath()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        (MissingCtor(1), 0) => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "tuple_pattern_undefined_ctor_context.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined constructor 'MissingCtor'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("[context: branch#1 > element#1]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AsPatternInnerUndefinedConstructor_ReportsAsInnerContextPath()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        (MissingCtor(1) as n) => n,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "as_pattern_undefined_ctor_context.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined constructor 'MissingCtor'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("[context: branch#1 > as-inner]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ViewPatternInnerUndefinedConstructor_ReportsViewInnerContextPath()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    x => match x
    {
        (normalize -> MissingCtor(1)) => 1,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "view_pattern_undefined_ctor_context.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Undefined constructor 'MissingCtor'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("[context: branch#1 > view-inner]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CtorPatternNamedFieldDuplicate_ReportsDiagnostic()
    {
        const string source = """
Person :: type {
    Person{name: Int, age: Int}
}

project :: Person -> Int
{
    p => match p
    {
        Person{name: a, name: b} => a + b,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ctor_pattern_named_field_duplicate.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Duplicate named field 'name'", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("[context: branch#1]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CtorPatternPositionalArityMismatch_ReportsDiagnostic()
    {
        const string source = """
Pair :: type {
    Pair(Int, Int)
}

project :: Pair -> Int
{
    p => match p
    {
        Pair(a) => a,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ctor_pattern_positional_arity_mismatch.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("expects 2 positional pattern(s), but got 1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CtorPatternUnknownNamedField_ReportsDiagnostic()
    {
        const string source = """
Person :: type {
    Person{name: Int, age: Int}
}

project :: Person -> Int
{
    p => match p
    {
        Person{height: h} => h,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ctor_pattern_unknown_named_field.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("has no named field 'height'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CtorPatternNamedCtorWithPositionalPattern_ReportsDiagnostic()
    {
        const string source = """
Person :: type {
    Person{name: Int, age: Int}
}

project :: Person -> Int
{
    p => match p
    {
        Person(a, b) => a,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ctor_pattern_named_ctor_positional_usage.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("uses named-field form; positional patterns are not allowed", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CtorPatternPositionalCtorWithNamedPattern_ReportsDiagnostic()
    {
        const string source = """
Pair :: type {
    Pair(Int, Int)
}

project :: Pair -> Int
{
    p => match p
    {
        Pair{left: l} => l,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ctor_pattern_positional_ctor_named_usage.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("uses positional form; named field patterns are not allowed", StringComparison.Ordinal));
    }
}
