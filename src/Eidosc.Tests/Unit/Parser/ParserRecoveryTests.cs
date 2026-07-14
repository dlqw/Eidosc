using Eidosc;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Eidosc.Utils;
using Xunit;
using AstRecoveryReasons = Eidosc.Ast.AstRecoveryReasons;

namespace Eidosc.Tests.Unit.Parser;

public class ParserRecoveryTests
{
    public static IEnumerable<object[]> ErrorRecoveryFixtures()
    {
        var paths = TestPathConfig.Current;
        yield return [paths.Fixture("errors/parser/invalid_token.eidos"), new[] { "E4000", "E4001" }, "", ""];
        yield return [paths.Fixture("errors/parser/invalid_tuple_rest.eidos"), new[] { "E4000" }, "list-rest marker", "[head, ..tail]"];
        yield return [paths.Fixture("errors/parser/invalid_ctor_rest.eidos"), new[] { "E4000" }, "list-rest marker", "Some(x)"];
    }

    [Fact]
    public void CompilationPipeline_InvalidInput_ReportsSyntaxDiagnosticWithoutE0001()
    {
        const string source = "main :: Unit -> Unit { _ => ??? }";
        var options = new CompilationOptions
        {
            InputFile = "invalid.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code is "E4000" or "E4001");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E0001");
    }

    [Theory]
    [MemberData(nameof(ErrorRecoveryFixtures))]
    public void CompilationPipeline_ParserRecoveryFixtures_ReportExpectedDiagnostics(
        string fixturePath,
        string[] expectedCodes,
        string expectedNote,
        string expectedHelp)
    {
        var source = TestSourceLoader.Load(fixturePath);
        var options = new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(fixturePath),
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        var matchingDiagnostics = result.Diagnostics
            .Where(item => item.Code != null && expectedCodes.Contains(item.Code))
            .ToList();

        Assert.NotEmpty(matchingDiagnostics);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "E0001");
        var diagnostic = matchingDiagnostics[0];
        if (!string.IsNullOrWhiteSpace(expectedNote))
        {
            Assert.Contains(diagnostic.Notes, note => note.Contains(expectedNote, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(expectedHelp))
        {
            Assert.Contains(diagnostic.Helps, help => help.Contains(expectedHelp, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CompilationPipeline_MissingInitializer_MarksRecoveredAstReasonAndXml()
    {
        const string source = "a :: Int = ;";
        var options = new CompilationOptions
        {
            InputFile = "missing_initializer.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var declaration = Assert.Single(module.Declarations.OfType<LetDecl>());
        var recovered = Assert.IsType<LiteralExpr>(declaration.Value);

        Assert.True(recovered.IsRecovered);
        Assert.True(recovered.IsRecoveredError);
        Assert.Equal(AstRecoveryReasons.ParserExpectedExpression, recovered.RecoveryReason);

        var doc = new System.Xml.XmlDocument();
        var element = recovered.ToXmlElement(doc);
        Assert.Equal(WellKnownStrings.AdditionalKeywords.True, element.GetAttribute(WellKnownStrings.XmlAttributes.IsRecovered));
        Assert.Equal(AstRecoveryReasons.ParserExpectedExpression, element.GetAttribute(WellKnownStrings.XmlAttributes.RecoveryReason));
    }

    [Fact]
    public void CompilationPipeline_MissingInitializerFixture_MarksRecoveredAstReason()
    {
        var fixturePath = TestPathConfig.Current.Fixture("errors/parser/missing_initializer.eidos");
        var source = TestSourceLoader.Load(fixturePath);
        var options = new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(fixturePath),
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var declaration = Assert.Single(module.Declarations.OfType<LetDecl>());
        var recovered = Assert.IsType<LiteralExpr>(declaration.Value);

        Assert.True(recovered.IsRecoveredError);
        Assert.Equal(AstRecoveryReasons.ParserMissingInitializer, recovered.RecoveryReason);
    }

    [Fact]
    public void CompilationPipeline_InvalidTupleRestPattern_ReportsListRestGuidance()
    {
        const string source = """
f :: (Int, Int) -> Int
{
    pair => match pair
    {
        (a, ..rest) => a,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "invalid_tuple_rest.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        var diagnostic = result.Diagnostics.Single(item => item.Code == "E4000");

        Assert.Contains(diagnostic.Notes, note => note.Contains("list-rest marker", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Helps, help => help.Contains("[head, ..tail]", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_InvalidCtorRestPattern_ReportsListRestGuidance()
    {
        const string source = """
Option[T] :: type { Some(T) , None }

f :: Option[Int] -> Int
{
    value => match value
    {
        Some(..rest) => 1,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "invalid_ctor_rest.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        var diagnostic = result.Diagnostics.Single(item => item.Code == "E4000");

        Assert.Contains(diagnostic.Notes, note => note.Contains("list-rest marker", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Helps, help => help.Contains("Some(x)", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ListRestWithSuffix_ParsesWithoutBoundaryDiagnostic()
    {
        const string source = """
f :: Int -> Int
{
    _ => match [1, 2, 3]
    {
        [a, ..rest, b] => a,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "list_rest_with_suffix.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void CompilationPipeline_DuplicateListRest_ReportsSingleRestGuidance()
    {
        const string source = """
f :: Int -> Int
{
    _ => match [1, 2, 3]
    {
        [a, ..rest, ..tail] => a,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "duplicate_list_rest.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        var diagnostic = result.Diagnostics.Single(item => item.Code == "E4000");

        Assert.Contains(diagnostic.Notes, note => note.Contains("can only appear once", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Helps, help => help.Contains("[head, ..tail]", StringComparison.Ordinal));
    }

}
