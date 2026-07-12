using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class StdlibParserRegressionTests
{
    public static IEnumerable<object[]> StdlibFiles()
    {
        foreach (var file in EidosFixtureInventory.StdlibPrecompiledFiles())
        {
            yield return [file];
        }
    }

    [Theory]
    [MemberData(nameof(StdlibFiles))]
    public void StdlibFile_ParsesWithoutErrors(string filePath)
    {
        var source = File.ReadAllText(filePath);
        var result = CompileParser(source, filePath);
        var errors = result.Diagnostics
            .Where(static diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
            .ToArray();

        Assert.NotNull(result.Ast);
        Assert.True(
            errors.Length == 0,
            $"{Path.GetFileName(filePath)}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    }

    private static CompilationResult CompileParser(string source, string filePath)
    {
        var options = new CompilationOptions
        {
            InputFile = filePath,
            LanguageVersion = TestSourceLoader.GetLanguageVersion(filePath),
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }
}
