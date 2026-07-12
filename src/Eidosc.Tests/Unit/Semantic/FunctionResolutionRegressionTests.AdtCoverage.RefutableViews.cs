using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewIntBindingCoverage_DoesNotReportCoverageWarnings()
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
        Some((normalize -> (1 as n))) when n == 1 => 1,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_int_binding_coverage.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewIntBindingContradiction_ReportsUnsatisfiableWarning()
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
        Some((normalize -> ((1 as n) | (2 as n)))) when n == 3 => 1,
        Some(_) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_int_binding_contradiction.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewIntOtherBindingCoverage_DoesNotReportCoverageWarnings()
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
        Some((normalize -> ((!2) as n))) when n != 2 => 1,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_int_other_binding_coverage.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewIntOtherBindingContradiction_ReportsUnsatisfiableWarning()
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
        Some((normalize -> ((!2) as n))) when n == 2 => 1,
        Some(_) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_int_other_binding_contradiction.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewIntOtherAndNotContradiction_ReportsUnsatisfiableWarning()
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
        Some((normalize -> ((!2 & !3) as n))) when n == 2 => 1,
        Some(_) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_int_other_and_not_contradiction.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedNestedAsViewIntOtherBindingContradiction_ReportsUnsatisfiableWarning()
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
        Some(((normalize -> ((!2 & !3) as n)) as m)) when m == 2 => 1,
        Some(_) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_nested_as_view_int_other_binding_contradiction.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedNestedAsViewIntOtherBindingCoverage_DoesNotReportCoverageWarnings()
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
        Some(((normalize -> ((!2 & !3) as n)) as m)) when m != 2 => 1,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_nested_as_view_int_other_binding_coverage.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4201");
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewFiniteInner_DoesNotReportFalseCoveredLiteralWarning()
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
        Some((normalize -> (1 as n))) when n == 1 => 1,
        Some(2) => 2,
        Some(_) => 3,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_finite_inner_no_false_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithDuplicateGuardedRefutableViewBranch_DoesNotReportFalseCoveredLiteralWarning()
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
        Some((normalize -> (1 as n))) when n == 1 => 1,
        Some((normalize -> (1 as n))) when n == 1 => 2,
        Some(_) => 3,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_duplicate_guarded_refutable_view_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewSuppressedCoveredBranch_EmitsSuppressionTraceNoteOnNonExhaustiveWarning()
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
        Some((normalize -> (1 as n))) when n == 1 => 1,
        Some(2) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_suppressed_covered_note.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#2 <- #1", StringComparison.Ordinal) &&
                    note.Contains("adt:refutable-view", StringComparison.Ordinal) &&
                    note.Contains("adt:target-domain-int", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit-ctor-name:Some", StringComparison.Ordinal) &&
                    note.Contains("reason: adt-guarded-refutable-view", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Suppressed-covered trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=adt;branch=2;covering=1;reason=", StringComparison.Ordinal) &&
                    note.Contains("adt:refutable-view", StringComparison.Ordinal));
        Assert.DoesNotContain(
            warning.Notes,
            note => note.Contains("adt:deterministic-assignment-overflow", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewSuppressedCoveredBranch_EmitsMultiCaseSuppressionReasonTags()
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
        Some(((normalize -> (1..2)) | (normalize -> 3))) when true => 1,
        (Some(1) | Some(2)) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_suppressed_covered_multi_case_reason_tags.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#2 <- #1", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit-ctor-name:Some-case1", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit-ctor-name:Some-case2", StringComparison.Ordinal) &&
                    note.Contains("-case1-key:p[0:1]", StringComparison.Ordinal) &&
                    note.Contains("-case2-key:p[0:2]", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Suppressed-covered trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=adt;branch=2;covering=1;reason=", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit-ctor-name:Some-case1-key:p[0:1]", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit-ctor-name:Some-case2-key:p[0:2]", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewSuppressedCoveredBranch_EmitsCharCaseKeyReasonTags()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

OptionC :: type {
    Some(Char) | None
}

classify :: OptionC -> Int
{
    x => match x
    {
        Some(((normalizeChar -> ('a'..'b')) | (normalizeChar -> 'c'))) when true => 1,
        (Some('a') | Some('b')) => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_suppressed_covered_char_case_reason_tags.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#2 <- #1", StringComparison.Ordinal) &&
                    note.Contains("adt:target-domain-char", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit-ctor-name:Some-case1", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit-ctor-name:Some-case2", StringComparison.Ordinal) &&
                    note.Contains("-case1-key:p[0:'a']", StringComparison.Ordinal) &&
                    note.Contains("-case2-key:p[0:'b']", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewSuppressedCoveredBranch_EscapesCommaInCharCaseKeyReasonToken()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

OptionC :: type {
    Some(Char) | None
}

classify :: OptionC -> Int
{
    x => match x
    {
        Some(((normalizeChar -> ',') | (normalizeChar -> '.'))) when true => 1,
        Some(',') => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_suppressed_covered_comma_char_case_reason_token.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit-ctor-name:Some-case1-key:p[0:',']", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Suppressed-covered trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=adt;branch=2;covering=1;reason=", StringComparison.Ordinal) &&
                    note.Contains("adt:no-deterministic-nonview-hit-ctor-name:Some-case1-key:p[0:'\\,']", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewSuppressedCoveredBranch_EmitsDeterministicOverflowReasonTag()
    {
        const string source = """
normalizeBool :: Bool -> Bool
{
    b => b
}

BigB :: type {
    BigB{a: Bool, b: Bool, c: Bool, d: Bool, e: Bool, f: Bool, g: Bool, h: Bool, i: Bool} | None
}

classify :: BigB -> Int
{
    x => match x
    {
        BigB{a: (normalizeBool -> true), b: _, c: _, d: _, e: _, f: _, g: _, h: _, i: _} |
        BigB{a: (normalizeBool -> false), b: _, c: _, d: _, e: _, f: _, g: _, h: _, i: _} when true => 1,
        BigB{
            a: (true | false),
            b: (true | false),
            c: (true | false),
            d: (true | false),
            e: (true | false),
            f: (true | false),
            g: (true | false),
            h: (true | false),
            i: (true | false)
        } => 2
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_suppressed_covered_overflow_reason.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Conservatively suppressed covered warnings:", StringComparison.Ordinal) &&
                    note.Contains("#2 <- #1", StringComparison.Ordinal) &&
                    note.Contains("adt:target-domain-bool", StringComparison.Ordinal) &&
                    note.Contains("adt:view-inner-uncertain@", StringComparison.Ordinal) &&
                    note.Contains("adt:deterministic-assignment-overflow", StringComparison.Ordinal) &&
                    note.Contains("adt:deterministic-assignment-overflow-ctor-name:BigB-case1", StringComparison.Ordinal) &&
                    note.Contains("adt:deterministic-assignment-overflow-ctor-name:BigB-case1-key:", StringComparison.Ordinal) &&
                    note.Contains("reason: adt-guarded-refutable-view", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Suppressed-covered trace kv:", StringComparison.Ordinal) &&
                    note.Contains("kind=adt;branch=2;covering=1;reason=", StringComparison.Ordinal) &&
                    note.Contains("adt:deterministic-assignment-overflow-ctor-name:BigB-case1", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #2", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewCharBindingContradiction_ReportsUnsatisfiableWarning()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

OptionC :: type {
    Some(Char) | None
}

classify :: OptionC -> Int
{
    x => match x
    {
        Some((normalizeChar -> ('a' as ch))) when ch != 'a' => 1,
        Some(_) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_char_binding_contradiction_unsat.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #1", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("unsatisfiable in finite coverage space", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewOrWildcardBranch_StillReportsCoveredLiteralWarning()
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
        Some(((normalize -> 1) | _)) when true => 1,
        Some(2) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_or_wildcard_still_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewNestedOrFallbackInAnd_DoesNotReportFalseCoveredLiteralWarning()
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
        Some(((normalize -> 1) & (_ | 1))) when true => 1,
        Some(2) => 2,
        None => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_nested_or_fallback_and_no_false_covered.eidos",
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
