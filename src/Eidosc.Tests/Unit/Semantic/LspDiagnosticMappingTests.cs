using Eidosc.Cli.Lsp;
using Eidosc.Ide;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspDiagnosticMappingTests
{
    [Fact]
    public void MapDiagnostics_OnlyPublishesDiagnosticsOwnedByRequestedDocument()
    {
        var activePath = Path.GetFullPath("active.eidos");
        var importedPath = Path.GetFullPath("imported.eidos");
        var snapshot = new IdeSemanticSnapshot
        {
            InputFile = activePath,
            Diagnostics =
            [
                Diagnostic("active", activePath),
                Diagnostic("imported", importedPath),
                new IdeDiagnosticEntry { Code = "E3", Message = "unspanned" }
            ]
        };

        var diagnostics = LspSemanticMapper.MapDiagnostics(snapshot, activePath);

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("active", diagnostic.Message),
            diagnostic => Assert.Equal("unspanned", diagnostic.Message));
    }

    private static IdeDiagnosticEntry Diagnostic(string message, string filePath) =>
        new()
        {
            Code = "E1",
            Message = message,
            Span = new IdeSpan
            {
                StartLine = 0,
                StartCharacter = 0,
                EndLine = 0,
                EndCharacter = 1,
                FilePath = filePath
            }
        };
}
