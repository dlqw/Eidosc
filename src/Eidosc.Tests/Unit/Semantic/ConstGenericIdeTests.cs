using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class ConstGenericIdeTests
{
    private const string Source = """
Vector[comptime N: Int, comptime T: Type] :: type
{
    Vector:: type(T)
}

identity[comptime N: Int, comptime T: Type] :: Vector[N, T] -> Vector[N, T]
{
    value => value
}

use :: Vector[4, Int] -> Vector[4, Int]
{
    value => identity[4, Int](value)
}
""";

    [Fact]
    public void Snapshot_ConstGenericSymbols_ExposeDomainsAndValueArguments()
    {
        var snapshot = BuildSnapshot();

        var identity = Assert.Single(snapshot.Symbols, symbol => symbol.Name == "identity" && symbol.Kind == "function");
        Assert.Equal("[comptime N: Int, comptime T: Type]", identity.GenericParameterText);
        Assert.Equal("Vector<N, T> -> Vector<N, T>", identity.TypeText);

        var use = Assert.Single(snapshot.Symbols, symbol => symbol.Name == "use" && symbol.Kind == "function");
        Assert.Equal("Vector<4, Int> -> Vector<4, Int>", use.TypeText);

        Assert.Contains(
            snapshot.Symbols,
            symbol => symbol.Name == "N" &&
                      symbol.Kind == "typeParameter" &&
                      symbol.Detail == "comptime value parameter: Int");
    }

    [Fact]
    public void Lsp_HoverAndCompletion_RenderConstGenericParameterDomains()
    {
        var snapshot = BuildSnapshot();
        var (line, character) = FindPosition(Source, "identity[4, Int]");

        var snapshotCompletion = Assert.Single(snapshot.Completions, item => item.Label == "identity");
        Assert.True(
            snapshotCompletion.VisibilitySpan == null,
            $"identity visibility: {snapshotCompletion.VisibilitySpan?.StartLine}:{snapshotCompletion.VisibilitySpan?.StartCharacter}-" +
            $"{snapshotCompletion.VisibilitySpan?.EndLine}:{snapshotCompletion.VisibilitySpan?.EndCharacter}");

        var hover = LspSemanticMapper.MapHover(snapshot, line, character);
        Assert.NotNull(hover);
        var markup = Assert.IsType<LspMarkupContent>(hover.Contents);
        Assert.Contains(
            "func identity[comptime N: Int, comptime T: Type]: Vector<N, T> -> Vector<N, T>",
            markup.Value,
            StringComparison.Ordinal);

        var (completionLine, completionCharacter) = FindPosition(Source, "identity[comptime");
        var completion = Assert.Single(
            LspSemanticMapper.MapCompletions(snapshot, completionLine, completionCharacter),
            item => item.Label == "identity");
        Assert.Equal(
            "identity[comptime N: Int, comptime T: Type]: Vector<N, T> -> Vector<N, T>",
            completion.Detail);
    }

    private static IdeSemanticSnapshot BuildSnapshot()
    {
        var result = new CompilationPipeline(Source, new CompilationOptions
        {
            InputFile = "const_generic_ide.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return IdeSemanticSnapshotBuilder.Build(result);
    }

    private static (int Line, int Character) FindPosition(string source, string needle)
    {
        var index = source.LastIndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0);

        var line = 0;
        var lineStart = 0;
        for (var current = 0; current < index; current++)
        {
            if (source[current] != '\n')
            {
                continue;
            }

            line++;
            lineStart = current + 1;
        }

        return (line, index - lineStart + 1);
    }
}
