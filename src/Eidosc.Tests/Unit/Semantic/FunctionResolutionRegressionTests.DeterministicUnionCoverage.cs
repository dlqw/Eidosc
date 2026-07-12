using System;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewDeterministicUnionAcrossBranches_CoversUnionTargetBranch()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [true]
    {
        [] => 0,
        [((({ b => b } -> true) as f) | (true as f))] when f => 1,
        [((({ b => b } -> false) as f) | (false as f))] when !f => 2,
        [(true | false)] => 3,
        [_, ..] => 4
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_deterministic_union_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #4", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces:", StringComparison.Ordinal) &&
                    note.Contains("<- #2", StringComparison.Ordinal) &&
                    note.Contains("<- #3", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewDeterministicUnionAcrossBranches_CoversUnionTargetBranch()
    {
        const string source = """
PairB :: type {
    PairB{flag: Bool, right: Int} | None
}

classify :: PairB -> Int
{
    x => match x
    {
        None => 0,
        PairB{flag: ((({ b => b } -> true) as f) | (true as f)), right: 4} when f => 1,
        PairB{flag: ((({ b => b } -> false) as f) | (false as f)), right: 4} when !f => 2,
        PairB{right: 4} & PairB{flag: (true | false)} => 3,
        PairB{flag: _, right: _} => 4
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_deterministic_union_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #4", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces:", StringComparison.Ordinal) &&
                    note.Contains("<- #2", StringComparison.Ordinal) &&
                    note.Contains("<- #3", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewDeterministicUnionAcrossIntCases_CoversUnionTargetBranch()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [(((normalize -> (1..2)) as x) | (1 as x))] when x == 1 => 1,
        [(((normalize -> (1..2)) as x) | (2 as x))] when x == 2 => 2,
        [(1 | 2)] => 3,
        [_, ..] => 4
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_deterministic_union_int_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #4", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces:", StringComparison.Ordinal) &&
                    note.Contains("<- #2", StringComparison.Ordinal) &&
                    note.Contains("<- #3", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewDeterministicPartialUnionAcrossIntCases_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    _ => match [2]
    {
        [] => 0,
        [(((normalize -> (1..2)) as x) | (1 as x))] when x == 1 => 1,
        [(1 | 2)] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_deterministic_partial_union_int_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewDeterministicUnionAcrossIntCases_CoversUnionTargetBranch()
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
        None => 0,
        Some((((normalize -> (1..2)) as t) | (1 as t))) when t == 1 => 1,
        Some((((normalize -> (1..2)) as t) | (2 as t))) when t == 2 => 2,
        Some((1 | 2)) => 3,
        Some(_) => 4
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_deterministic_union_int_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #4", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces:", StringComparison.Ordinal) &&
                    note.Contains("<- #2", StringComparison.Ordinal) &&
                    note.Contains("<- #3", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewDeterministicPartialUnionAcrossIntCases_DoesNotReportFalseCoveredWarning()
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
        None => 0,
        Some((((normalize -> (1..2)) as t) | (1 as t))) when t == 1 => 1,
        Some((1 | 2)) => 2,
        Some(_) => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_deterministic_partial_union_int_no_false_covered.eidos",
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
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewDeterministicUnionAcrossCharCases_CoversUnionTargetBranch()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

classify :: Int -> Int
{
    _ => match ['b']
    {
        [] => 0,
        [(((normalizeChar -> ('a'..'b')) as t) | ('a' as t))] when t == 'a' => 1,
        [(((normalizeChar -> ('a'..'b')) as t) | ('b' as t))] when t == 'b' => 2,
        [('a' | 'b')] => 3,
        [_, ..] => 4
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_deterministic_union_char_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #4", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces:", StringComparison.Ordinal) &&
                    note.Contains("<- #2", StringComparison.Ordinal) &&
                    note.Contains("<- #3", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithGuardedRefutableViewDeterministicPartialUnionAcrossCharCases_DoesNotReportFalseCoveredWarning()
    {
        const string source = """
normalizeChar :: Char -> Char
{
    c => c
}

classify :: Int -> Int
{
    _ => match ['b']
    {
        [] => 0,
        [(((normalizeChar -> ('a'..'b')) as t) | ('a' as t))] when t == 'a' => 1,
        [('a' | 'b')] => 2,
        [_, ..] => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_guarded_refutable_view_deterministic_partial_union_char_no_false_covered.eidos",
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
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewDeterministicUnionAcrossCharCases_CoversUnionTargetBranch()
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
        None => 0,
        Some((((normalizeChar -> ('a'..'b')) as t) | ('a' as t))) when t == 'a' => 1,
        Some((((normalizeChar -> ('a'..'b')) as t) | ('b' as t))) when t == 'b' => 2,
        Some(('a' | 'b')) => 3,
        Some(_) => 4
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_deterministic_union_char_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var warning = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("branch #4", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
        Assert.Contains(
            warning.Notes,
            note => note.Contains("Covered-case traces:", StringComparison.Ordinal) &&
                    note.Contains("<- #2", StringComparison.Ordinal) &&
                    note.Contains("<- #3", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_AdtMatchWithGuardedRefutableViewDeterministicPartialUnionAcrossCharCases_DoesNotReportFalseCoveredWarning()
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
        None => 0,
        Some((((normalizeChar -> ('a'..'b')) as t) | ('a' as t))) when t == 'a' => 1,
        Some(('a' | 'b')) => 2,
        Some(_) => 3
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_adt_guarded_refutable_view_deterministic_partial_union_char_no_false_covered.eidos",
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
