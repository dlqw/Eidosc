using System.IO;
using Eidosc.Diagnostic;
using Eidosc;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Diagnostic;

public class DiagnosticRendererTests
{
    [Fact]
    public void Render_IncludesFilePathNoteAndHelp()
    {
        var source = new SourceStream("x :: y\n", 4);
        var diagnostic = Eidosc.Diagnostic.Diagnostic.Error("cannot find value `y`", "E3000")
            .WithLabel(new SourceSpan(new SourceLocation(8, 0, 8), 1), "unknown symbol")
            .WithNote("function: main")
            .WithHelp("import or declare `y`");

        var writer = new StringWriter();
        DiagnosticRenderer.Render(
            diagnostic,
            source,
            writer,
            new DiagnosticRenderOptions
            {
                UseColors = false,
                FilePath = "test.eidos"
            });

        var text = writer.ToString();
        Assert.Contains("error[E3000]: cannot find value `y`", text);
        Assert.Contains("--> test.eidos:1:9", text);
        Assert.Contains("unknown symbol", text);
        Assert.Contains("note: function: main", text);
        Assert.Contains("help: import or declare `y`", text);
    }

    [Fact]
    public void Render_LabelAtTrailingNewline_DoesNotThrow()
    {
        var source = new SourceStream("x :: undefined_name\n", 4);
        var eofSpan = new SourceSpan(new SourceLocation(source.Text.Length - 1, 0, source.Text.Length - 1), 1);
        var diagnostic = Eidosc.Diagnostic.Diagnostic.Error("Unexpected end of file", "E4001")
            .WithLabel(eofSpan, "expected more tokens");

        var writer = new StringWriter();
        var exception = Record.Exception(() =>
            DiagnosticRenderer.Render(
                diagnostic,
                source,
                writer,
                new DiagnosticRenderOptions
                {
                    UseColors = false,
                    FilePath = "invalid.eidos"
                }));

        Assert.Null(exception);
        var text = writer.ToString();
        Assert.Contains("error[E4001]: Unexpected end of file", text);
        Assert.Contains("--> invalid.eidos:1:", text);
    }

    [Fact]
    public void Render_LabelLongerThanLine_ClampsCaretToLine()
    {
        var source = new SourceStream("abc\n", 4);
        var diagnostic = Eidosc.Diagnostic.Diagnostic.Error("wide span", "E9999")
            .WithLabel(new SourceSpan(new SourceLocation(1, 0, 1), 100), "wide");

        var writer = new StringWriter();
        DiagnosticRenderer.Render(
            diagnostic,
            source,
            writer,
            new DiagnosticRenderOptions
            {
                UseColors = false,
                FilePath = "wide.eidos"
            });

        var caretLine = writer.ToString()
            .Split(Environment.NewLine, StringSplitOptions.None)
            .Single(line => line.Contains('^', StringComparison.Ordinal));

        Assert.Equal(2, caretLine.Count(static ch => ch == '^'));
    }

    [Fact]
    public void Render_OverlappingImplDiagnostic_IncludesNotesHelpAndRelatedSnippet()
    {
        const string sourceText = """
Show :: trait {
    show :: Self -> String
}

Person :: type {
    Person(String)
}

PersonAlias :: type = Person;

@impl(Show)
show :: Person -> String
{
    p => "person"
}

@impl(Show)
show :: PersonAlias -> String
{
    p => "alias"
}
""";

        var source = new SourceStream(sourceText, 4);
        var existingStart = sourceText.IndexOf("@impl(Show)", StringComparison.Ordinal);
        var requestedStart = sourceText.IndexOf("@impl(Show)", existingStart + 1, StringComparison.Ordinal);
        Assert.True(existingStart >= 0);
        Assert.True(requestedStart >= 0);

        var diagnostic = Eidosc.Diagnostic.Diagnostic.Error("Ambiguous overlapping impl registration", "E3004")
            .WithLabel(new SourceSpan(new SourceLocation(requestedStart, 10, 0), "@impl(Show)".Length), "overlapping impl requested here: @impl(Show) on PersonAlias")
            .WithNote("requested impl head: @impl(Show) on PersonAlias")
            .WithNote("existing impl head: @impl(Show) on Person")
            .WithNote("requested canonical head: @impl(Show) on Person")
            .WithNote("existing canonical head: @impl(Show) on Person")
            .WithHelp("Keep only one impl head per canonical trait/type shape.")
            .WithRelated(
                Eidosc.Diagnostic.Diagnostic.Note("existing overlapping impl registered here")
                    .WithLabel(new SourceSpan(new SourceLocation(existingStart, 10, 0), "@impl(Show)".Length), "@impl(Show) on Person"));

        var writer = new StringWriter();
        DiagnosticRenderer.Render(
            diagnostic,
            source,
            writer,
            new DiagnosticRenderOptions
            {
                UseColors = false,
                FilePath = "impl_overlap.eidos"
            });

        var text = writer.ToString();
        Assert.Contains("error[E3004]: Ambiguous overlapping impl registration", text);
        Assert.Contains("note: requested impl head: @impl(Show) on PersonAlias", text);
        Assert.Contains("note: existing canonical head: @impl(Show) on Person", text);
        Assert.Contains("help: Keep only one impl head per canonical trait/type shape.", text);
        Assert.Contains("note: existing overlapping impl registered here", text);
        Assert.Contains("@impl(Show) on Person", text);
        Assert.Contains("--> impl_overlap.eidos:11:1", text);
    }
}
