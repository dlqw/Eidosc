using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Utils;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class ImportSuggestionComposerTests
{
    [Fact]
    public void TryCreateModuleSuggestion_UsesActualCrLfInsertionCoordinates()
    {
        const string source = "import Foo.Bar\r\n\r\nvalue :: 1;\r\n";
        var filePath = Path.GetFullPath("import_suggestion_coordinates.eidos");
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = filePath,
            StopAtPhase = CompilationPhase.Parser,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static item => item.Message)));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var referenceOffset = source.IndexOf("value", StringComparison.Ordinal);

        var suggestion = ImportSuggestionComposer.TryCreateModuleSuggestion(
            module,
            source,
            new SourceSpan(new SourceLocation(referenceOffset, 2, 0, filePath), 5),
            "Std.Seq");

        Assert.NotNull(suggestion);
        Assert.NotNull(suggestion.Span);
        Assert.Equal(source.IndexOf("\r\n", StringComparison.Ordinal) + 2, suggestion.Span.Value.Location.Position);
        Assert.Equal(1, suggestion.Span.Value.Location.Line);
        Assert.Equal(0, suggestion.Span.Value.Location.Column);
        Assert.Equal(0, suggestion.Span.Value.Length);
        Assert.Equal("import Std.Seq\r\n", suggestion.Replacement);
    }
}
