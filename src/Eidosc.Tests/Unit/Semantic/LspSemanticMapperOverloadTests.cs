using System;
using System.Linq;
using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspSemanticMapperOverloadTests
{
    [Fact]
    public void MapHover_ResolvedOverload_IncludesCompactOverloadList()
    {
        const string source = """
format :: Int -> String
{
    _ => "int"
}

format :: String -> String
{
    text => text
}

main :: Unit -> String
{
    _ => format(1)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "lsp_overload_hover.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}")));

        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var (line, character) = FindPosition(source, "format(1)");
        var hover = LspSemanticMapper.MapHover(snapshot, line, character);
        Assert.NotNull(hover);
        var markup = Assert.IsType<LspMarkupContent>(hover.Contents);

        Assert.Contains("overloads", markup.Value, StringComparison.Ordinal);
        Assert.Contains("format: Int -> String", markup.Value, StringComparison.Ordinal);
        Assert.Contains("format: String -> String", markup.Value, StringComparison.Ordinal);
    }

    private static (int Line, int Character) FindPosition(string source, string needle)
    {
        var index = source.LastIndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0);

        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < index; i++)
        {
            if (source[i] != '\n')
            {
                continue;
            }

            line++;
            lineStart = i + 1;
        }

        return (line, index - lineStart);
    }
}
