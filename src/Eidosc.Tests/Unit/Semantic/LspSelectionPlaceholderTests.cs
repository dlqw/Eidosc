using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspSelectionPlaceholderTests
{
    private const string Source = """
import std.Result

choose :: Result[Int, String] -> Int
{
    value => value
        then _0 + _0
        else 0
}
""";

    [Fact]
    public void Snapshot_SelectionPlaceholder_ExposesTypedArmLocalSyntheticBinding()
    {
        var snapshot = BuildSnapshot();
        var symbol = Assert.Single(snapshot.Symbols, entry => entry.Name == "_0");

        Assert.Equal("variable", symbol.Kind);
        Assert.Equal("pattern binding", symbol.Detail);
        Assert.Equal("Int", symbol.TypeText);
        Assert.Equal("TypedClean", symbol.TypeConfidence);
        Assert.NotNull(symbol.VisibilitySpan);

        var occurrences = snapshot.Occurrences
            .Where(occurrence => occurrence.SymbolId == symbol.SymbolId)
            .OrderBy(occurrence => occurrence.Span.Start)
            .ToArray();
        Assert.Collection(
            occurrences,
            first => Assert.Equal("definition", first.Role),
            second => Assert.Equal("reference", second.Role));
    }

    [Fact]
    public void Lsp_SelectionPlaceholder_ProvidesHoverTokensDefinitionAndCompletion()
    {
        var snapshot = BuildSnapshot();
        var first = FindPosition(Source, "_0", occurrence: 1);
        var second = FindPosition(Source, "_0", occurrence: 2);

        var hover = LspSemanticMapper.MapHover(snapshot, second.Line, second.Character);
        Assert.NotNull(hover);
        var markup = Assert.IsType<LspMarkupContent>(hover.Contents);
        Assert.Contains("_0: Int", markup.Value, StringComparison.Ordinal);

        var definition = LspSemanticMapper.MapDefinition(snapshot, second.Line, second.Character);
        Assert.NotNull(definition);
        Assert.Equal(first.Line, definition.Range.Start.Line);
        Assert.Equal(first.Character, definition.Range.Start.Character);
        Assert.Equal(first.Character + 2, definition.Range.End.Character);

        var completions = LspSemanticMapper.MapCompletions(snapshot, second.Line, second.Character);
        var completion = Assert.Single(completions, item => item.Label == "_0");
        Assert.Contains("Int", completion.Detail, StringComparison.Ordinal);

        var tokens = Decode(LspSemanticMapper.MapSemanticTokens(
            snapshot,
            snapshot.InputFile,
            Source));
        Assert.Contains(tokens, token => token.Line == first.Line &&
                                         token.Character == first.Character &&
                                         token.Length == 2 &&
                                         token.Type == "parameter");
        Assert.Contains(tokens, token => token.Line == second.Line &&
                                         token.Character == second.Character &&
                                         token.Length == 2 &&
                                         token.Type == "variable");
    }

    private static IdeSemanticSnapshot BuildSnapshot()
    {
        var result = new CompilationPipeline(Source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath("projects/test/src/stdlib/std_result_import.eidos"),
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        return IdeSemanticSnapshotBuilder.Build(result);
    }

    private static (int Line, int Character) FindPosition(string source, string needle, int occurrence)
    {
        var index = -1;
        for (var count = 0; count < occurrence; count++)
        {
            index = source.IndexOf(needle, index + 1, StringComparison.Ordinal);
            Assert.True(index >= 0);
        }

        var line = 0;
        var lineStart = 0;
        for (var current = 0; current < index; current++)
        {
            if (source[current] == '\n')
            {
                line++;
                lineStart = current + 1;
            }
        }

        return (line, index - lineStart);
    }

    private static List<(int Line, int Character, int Length, string Type)> Decode(LspSemanticTokens tokens)
    {
        var result = new List<(int, int, int, string)>();
        var line = 0;
        var character = 0;
        for (var index = 0; index < tokens.Data.Count; index += 5)
        {
            var deltaLine = tokens.Data[index];
            line += deltaLine;
            character = deltaLine == 0 ? character + tokens.Data[index + 1] : tokens.Data[index + 1];
            result.Add((line, character, tokens.Data[index + 2], LspSemanticTokenTypes.All[tokens.Data[index + 3]]));
        }

        return result;
    }
}
