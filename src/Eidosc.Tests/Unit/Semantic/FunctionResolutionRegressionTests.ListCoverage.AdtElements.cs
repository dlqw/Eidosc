using System;
using System.Linq;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class FunctionResolutionRegressionTests
{
    [Fact]
    public void CompilationPipeline_ListMatchWithAdtElementConstructors_DoesNotTreatFirstBranchAsAllNonEmptyLists()
    {
        const string source = """
Tok :: type {
    TkKeyword:: type(String) , TkIdent:: type(String) , TkEof :: type {}
}

classify :: Seq[Tok] -> Int
{
    [TkKeyword("int"), ..rest] => 1,
    [TkKeyword("return"), ..rest] => 2,
    [TkIdent(name), ..rest] => 3,
    [TkEof(), ..rest] => 4,
    [] => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_adt_element_constructors.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithAdtElementConstructors_ReportsDuplicateLiteralBucket()
    {
        const string source = """
Tok :: type {
    TkKeyword:: type(String) , TkIdent:: type(String) , TkEof :: type {}
}

classify :: Seq[Tok] -> Int
{
    [TkKeyword("int"), ..rest] => 1,
    [TkKeyword("return"), ..rest] => 2,
    [TkKeyword("int"), ..rest] => 3,
    [TkIdent(name), ..rest] => 4,
    [TkEof(), ..rest] => 5,
    [] => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_adt_element_duplicate_literal_bucket.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithAdtElementConstructors_TracksKeywordOtherBucketForExhaustiveness()
    {
        const string source = """
Tok :: type {
    TkKeyword:: type(String) , TkIdent:: type(String) , TkEof :: type {}
}

classify :: Seq[Tok] -> Int
{
    [TkKeyword("int"), ..rest] => 1,
    [TkKeyword("return"), ..rest] => 2,
    [TkIdent(name), ..rest] => 3,
    [TkEof(), ..rest] => 4,
    [] => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_adt_element_keyword_other_bucket.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithAdtElementConstructors_WildcardConstructorFieldCoversOtherLiteralBucket()
    {
        const string source = """
Tok :: type {
    TkKeyword:: type(String) , TkIdent:: type(String) , TkEof :: type {}
}

classify :: Seq[Tok] -> Int
{
    [TkKeyword("int"), ..rest] => 1,
    [TkKeyword("return"), ..rest] => 2,
    [TkKeyword(other), ..rest] => 3,
    [TkIdent(name), ..rest] => 4,
    [TkEof(), ..rest] => 5,
    [] => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_adt_element_keyword_other_bucket_covered.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithAdtElementConstructors_TracksScalarLiteralFieldBuckets()
    {
        const string source = """
Tok :: type {
    TkCode:: type(Int) , TkWord:: type(String) , TkEof :: type {}
}

classify :: Seq[Tok] -> Int
{
    [TkCode(1), ..rest] => 1,
    [TkCode(2), ..rest] => 2,
    [TkCode(other), ..rest] => 3,
    [TkWord(word), ..rest] => 4,
    [TkEof(), ..rest] => 5,
    [] => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_adt_element_scalar_literal_bucket.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "W4201" &&
                          diagnostic.Message.Contains("already covered", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListMatchWithAdtElementConstructors_SplitsMultipleAdtElements()
    {
        const string source = """
Tok :: type {
    TkKeyword:: type(String) , TkIdent:: type(String) , TkEof :: type {}
}

classify :: Seq[Tok] -> Int
{
    [TkKeyword("fn"), TkIdent(name), ..rest] => 1,
    [TkKeyword("let"), TkIdent(name), ..rest] => 2,
    [TkKeyword(other), TkIdent(name), ..rest] => 3,
    [TkIdent(name), TkIdent(next), ..rest] => 4,
    [TkEof(), TkIdent(name), ..rest] => 5,
    [first, TkKeyword(text), ..rest] => 6,
    [first, TkEof(), ..rest] => 7,
    [TkKeyword("fn")] => 8,
    [TkKeyword("let")] => 9,
    [TkKeyword(other)] => 10,
    [TkIdent(name)] => 11,
    [TkEof()] => 12,
    [] => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pattern_list_adt_element_multiple_elements.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W4200");
    }
}
