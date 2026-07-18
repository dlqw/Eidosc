using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public class CurriedPatternBranchResolutionTests
{
    [Fact]
    public void CompilationPipeline_CurriedFunctionBodyBranch_BindsIntermediatePatternVariables()
    {
        const string source = """
OptionString :: type { SomeString:: type(String) , NoneString :: type {} }

optionStringMap :: OptionString -> (String -> String) -> OptionString
{
    SomeString(value) => mapper => SomeString(mapper(value)),
    NoneString() => _ => NoneString()
}
""";

        var options = new CompilationOptions
        {
            InputFile = "semantic_curried_pattern_branch_bindings_tests.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Namer, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3000");
    }

    [Fact]
    public void CompilationPipeline_NameFirstCurriedFunctionBodyBranch_TypesNestedSegments()
    {
        const string source = """
apply :: (Int -> Int) -> Int -> Int
{
    f => x => f(x)
}

compose :: (Int -> Int) -> (Int -> Int) -> Int -> Int
{
    f => g => x => f(g(x))
}

addTuplePair :: (Int, Int) -> (Int, Int) -> Int
{
    (leftA, leftB) => (rightA, rightB) => leftA + leftB + rightA + rightB
}
""";

        var options = new CompilationOptions
        {
            InputFile = "name_first_curried_pattern_branch_types_tests.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = Eidosc.ProjectSystem.EidosLanguageVersions.Current,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Types, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("Function arity mismatch", System.StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_StandardFunctionBodyBranch_DoesNotTreatRhsIdentifierAsPattern()
    {
        const string source = """
project :: Int -> Int {
    x => y
}
""";

        var options = new CompilationOptions
        {
            InputFile = "semantic_standard_pattern_branch_rhs_identifier_tests.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Undefined identifier 'y'", System.StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CurriedFunctionBodyBranch_BindsCtorVariablesFromLaterSegments()
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

        var options = new CompilationOptions
        {
            InputFile = "semantic_curried_ctor_pattern_branch_bindings_tests.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Namer, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Undefined identifier 'right'", System.StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_NameFirstCurriedFunctionBodyBranch_PreservesLiteralCtorPatternsInLaterSegments()
    {
        const string source = """
Token :: type { TkKeyword:: type(String) , TkEof :: type {} }

matchKeyword :: Int -> Seq[Token] -> Int
{
    base => [TkKeyword("typedef"), .._] => base,
    _ => _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "name_first_curried_literal_ctor_pattern_branch_tests.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = Eidosc.ProjectSystem.EidosLanguageVersions.Current,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Types, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("expects 1 positional pattern", System.StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CurriedFunctionBodyBranch_BindsCtorVariablesAcrossFourSegments()
    {
        const string source = """
OptionInt :: type { SomeInt:: type(Int) , NoneInt :: type {} }

quad_sum :: OptionInt -> OptionInt -> OptionInt -> OptionInt -> Int
{
    SomeInt(first) => SomeInt(second) => SomeInt(third) => SomeInt(fourth) => first + second + third + fourth,
    _ => _ => _ => _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "semantic_curried_quad_ctor_pattern_branch_bindings_tests.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Namer, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E3000" &&
                          diagnostic.Message.Contains("Undefined identifier 'fourth'", System.StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_CurriedFunctionBodyBranch_AllowsGuardOnLaterSegment()
    {
        const string source = """
pickPositive :: Int -> Int -> Int
{
    n => i when i > n => i,
    _ => _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "semantic_curried_pattern_branch_guard_tests.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Namer, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3000");
    }

    [Fact]
    public void CompilationPipeline_CurriedFunctionBodyBranch_AllowsPatternGuardOnLaterSegment()
    {
        const string source = """
OptionInt :: type { SomeInt:: type(Int) , NoneInt :: type {} }

addIfSome :: Int -> OptionInt -> Int
{
    base => opt when SomeInt(n) <- opt => base + n,
    _ => _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "semantic_curried_pattern_branch_pattern_guard_tests.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Namer, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3000");
    }

    [Fact]
    public void CompilationPipeline_CurriedFunctionBodyBranch_AllowsMultipleGuardsOnLaterSegment()
    {
        const string source = """
OptionInt :: type { SomeInt:: type(Int) , NoneInt :: type {} }

addIfPositive :: Int -> OptionInt -> Int
{
    base => opt when SomeInt(n) <- opt when n > 0 => base + n,
    _ => _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "semantic_curried_pattern_branch_multiple_guard_tests.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Namer, result.CompletedPhase);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E3000");
    }
}
