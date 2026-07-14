using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_BoolMatchWithLiteralBranches_IsExhaustiveWithoutDuplicateBindingErrors()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true => 1,
        false => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_bool_literal_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("bound more than once in the same scope", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
    }

    [Fact]
    public void CompilationPipeline_StringMatchWithDuplicateLiteralBranch_ReportsCoveredWarning()
    {
        const string source = """
classify :: String -> Int
{
    x => match x
    {
        "a" => 1,
        "a" => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_string_duplicate_literal_covered.eidos",
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
    public void CompilationPipeline_CharMatchWithDuplicateLiteralBranch_ReportsCoveredWarning()
    {
        const string source = """
classify :: Char -> Int
{
    x => match x
    {
        'a' => 1,
        'a' => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_char_duplicate_literal_covered.eidos",
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
    public void CompilationPipeline_FloatMatchWithDuplicateLiteralBranch_ReportsCoveredWarning()
    {
        const string source = """
classify :: Float -> Int
{
    x => match x
    {
        1.5 => 1,
        1.5 => 2,
        _ => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_float_duplicate_literal_covered.eidos",
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
    public void CompilationPipeline_StringMatchWithoutOtherBranch_ReportsMissingOtherWitness()
    {
        const string source = """
classify :: String -> Int
{
    x => match x
    {
        "a" => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_string_missing_other_witness.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses: other", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchMissingConstructor_ReportsMissingConstructorWarning()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(v) => v
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_non_exhaustive_adt.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing constructors: None", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses: None", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case traces:", StringComparison.Ordinal) &&
                    note.Contains("None [ctor:", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case trace groups:", StringComparison.Ordinal) &&
                    note.Contains("ctor=ctor:", StringComparison.Ordinal) &&
                    note.Contains("(None)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=ctor;key=ctor:", StringComparison.Ordinal) &&
                    note.Contains(";display=None", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithDuplicateConstructorBranch_ReportsCoveredUnreachableWarning()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(v) => v,
        Some(v) => v + 1,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_duplicate_constructor_unreachable.eidos",
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
            note => note.Contains("Covered-case witnesses: Some(...)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: Some(...) <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithTopLevelNotPattern_ReportsMissingConstructor()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        !None => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_top_level_not_non_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing constructors: None", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses: None", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithTopLevelNotAndCoveredCtor_ReportsCoveredUnreachableWarning()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        !None => 1,
        Some(v) => v,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_top_level_not_covered_ctor.eidos",
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
            note => note.Contains("Covered-case witnesses: Some(...)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: Some(...) <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchMissingCases_ReportsWitnesses()
    {
        const string source = """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, true) => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_non_exhaustive_tuple_bool.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing tuple bool cases:", warning.Message, StringComparison.Ordinal);
        Assert.Contains("(false, false)", warning.Message, StringComparison.Ordinal);
        Assert.Contains("(false, true)", warning.Message, StringComparison.Ordinal);
        Assert.Contains("(true, false)", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses: (false, false), (false, true), (true, false)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case traces:", StringComparison.Ordinal) &&
                    note.Contains("(false, false) [tuple-bool:(false, false)]", StringComparison.Ordinal) &&
                    note.Contains("(false, true) [tuple-bool:(false, true)]", StringComparison.Ordinal) &&
                    note.Contains("(true, false) [tuple-bool:(true, false)]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case trace groups:", StringComparison.Ordinal) &&
                    note.Contains("tuple-bool=tuple-bool:(false, false)", StringComparison.Ordinal) &&
                    note.Contains("tuple-bool:(false, true)", StringComparison.Ordinal) &&
                    note.Contains("tuple-bool:(true, false)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=tuple-bool;key=tuple-bool:(false, false);display=(false, false)", StringComparison.Ordinal) &&
                    note.Contains("kind=tuple-bool;key=tuple-bool:(false, true);display=(false, true)", StringComparison.Ordinal) &&
                    note.Contains("kind=tuple-bool;key=tuple-bool:(true, false);display=(true, false)", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithWildcardPartialCoverage_ReportsSingleMissingWitness()
    {
        const string source = """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, _) => 1,
        (false, true) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_non_exhaustive_tuple_bool_wildcard.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing tuple bool cases:", warning.Message, StringComparison.Ordinal);
        Assert.Contains("(false, false)", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(false, true)", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(true, false)", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(true, true)", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses: (false, false)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case traces: (false, false) [tuple-bool:(false, false)]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case trace groups: tuple-bool=tuple-bool:(false, false)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case trace kv: kind=tuple-bool;key=tuple-bool:(false, false);display=(false, false)", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithWildcardExhaustive_DoesNotReportCoverageWarning()
    {
        const string source = """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, _) => 1,
        (false, _) => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_exhaustive_tuple_bool_wildcard.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithCoveredBranch_ReportsCoveredUnreachableWarning()
    {
        const string source = """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, _) => 1,
        (true, false) => 2,
        (false, _) => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_covered_branch_unreachable.eidos",
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
            note => note.Contains("Covered-case witnesses: (true, false)", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces: (true, false) <- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithTopLevelOrPatternExhaustive_DoesNotReportCoverageWarning()
    {
        const string source = """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, true) | (true, false) => 1,
        (false, _) => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_top_level_or_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithTopLevelNotPattern_ReportsSingleMissingWitness()
    {
        const string source = """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        !(false, false) => 1
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_top_level_not_non_exhaustive.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains("missing tuple bool cases:", warning.Message, StringComparison.Ordinal);
        Assert.Contains("(false, false)", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(true, false)", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(false, true)", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(true, true)", warning.Message, StringComparison.Ordinal);
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Missing-case witnesses: (false, false)", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithGuardedUnknownLowerBoundCase_CoversFollowingTupleBranch()
    {
        const string source = """
probe :: Bool -> Bool
{
    b => b
}

classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (a, b) when a || probe(a) => 1,
        (true, false) => 2,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_unknown_lower_bound_covered.eidos",
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
                    note.Contains("(true, false)", StringComparison.Ordinal) &&
                    note.Contains("<- #1", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_TupleBoolMatchWithGuardedUnknownWithoutLowerBound_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
probe :: Bool -> Bool
{
    b => b
}

classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (a, b) when (!a) && probe(a) => 1,
        (true, false) => 2,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_tuple_bool_guarded_unknown_no_lower_bound_no_false_covered.eidos",
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
}
