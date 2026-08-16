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
    Person:: type(String)
}

PersonAlias :: type = Person;


ShowPerson :: instance Show {
    show :: Person -> String {
        p => "person"
    }
}


ShowPersonAlias :: instance Show {
    show :: PersonAlias -> String {
        p => "alias"
    }
}
""";

        var source = new SourceStream(sourceText, 4);
        const string instanceHead = "instance Show";
        var existingStart = sourceText.IndexOf(instanceHead, StringComparison.Ordinal);
        var requestedStart = sourceText.IndexOf(instanceHead, existingStart + 1, StringComparison.Ordinal);
        Assert.True(existingStart >= 0);
        Assert.True(requestedStart >= 0);

        var diagnostic = Eidosc.Diagnostic.Diagnostic.Error("Ambiguous overlapping instance registration", "E3004")
            .WithLabel(new SourceSpan(new SourceLocation(requestedStart, 10, 0), instanceHead.Length), "overlapping instance requested here: instance Show for PersonAlias")
            .WithNote("requested instance head: instance Show for PersonAlias")
            .WithNote("existing instance head: instance Show for Person")
            .WithNote("requested canonical head: instance Show for Person")
            .WithNote("existing canonical head: instance Show for Person")
            .WithHelp("Keep only one instance head per canonical trait/type shape.")
            .WithRelated(
                Eidosc.Diagnostic.Diagnostic.Note("existing overlapping instance registered here")
                    .WithLabel(new SourceSpan(new SourceLocation(existingStart, 10, 0), instanceHead.Length), "instance Show for Person"));

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
        Assert.Contains("error[E3004]: Ambiguous overlapping instance registration", text);
        Assert.Contains("note: requested instance head: instance Show for PersonAlias", text);
        Assert.Contains("note: existing canonical head: instance Show for Person", text);
        Assert.Contains("help: Keep only one instance head per canonical trait/type shape.", text);
        Assert.Contains("note: existing overlapping instance registered here", text);
        Assert.Contains("instance Show for Person", text);
        Assert.Contains("--> impl_overlap.eidos:11:1", text);
    }

    [Fact]
    public void Render_ForeignFileLabel_UsesLabelFilePathAndTextViaResolver()
    {
        var rootSource = new SourceStream("main :: Unit -> Int\n{\n    _ => 0\n}\n", 4);
        const string moduleText = "helper :: Unit -> Int\n{\n    _ => 1\n}\n";
        var labelSpan = new SourceSpan(
            new SourceLocation(moduleText.IndexOf("1", StringComparison.Ordinal), 2, 11, "D:\\project\\Ecc\\Owned.eidos"),
            1);

        var diagnostic = Eidosc.Diagnostic.Diagnostic.Error("变量被移动两次", "E1001")
            .WithLabel(labelSpan, "DoubleMove");

        var writer = new StringWriter();
        DiagnosticRenderer.Render(
            diagnostic,
            rootSource,
            writer,
            new DiagnosticRenderOptions
            {
                UseColors = false,
                FilePath = "main.eidos",
                SourceResolver = path => path.EndsWith("Owned.eidos", StringComparison.OrdinalIgnoreCase)
                    ? moduleText
                    : null
            });

        var text = writer.ToString();
        Assert.Contains("--> D:\\project\\Ecc\\Owned.eidos:3:12", text);
        Assert.Contains("    _ => 1", text);
        Assert.DoesNotContain("--> main.eidos", text);
    }

    [Fact]
    public void Render_ForeignFileLabelWithoutResolvableSource_ReportsLabelPathWithoutRootExcerpt()
    {
        var rootSource = new SourceStream("main :: Unit -> Int\n{\n    _ => 0\n}\n", 4);
        var labelSpan = new SourceSpan(
            new SourceLocation(0, 426, 103, "<precompiled:std.Core>"),
            1);

        var diagnostic = Eidosc.Diagnostic.Diagnostic.Error("值被借用中，无法修改", "E1002")
            .WithLabel(labelSpan, "MutateWhileBorrowed");

        var writer = new StringWriter();
        DiagnosticRenderer.Render(
            diagnostic,
            rootSource,
            writer,
            new DiagnosticRenderOptions
            {
                UseColors = false,
                FilePath = "main.eidos"
            });

        var text = writer.ToString();
        Assert.Contains("--> <precompiled:std.Core>:427:104", text);
        Assert.Contains("(source text unavailable)", text);
        Assert.DoesNotContain("--> main.eidos", text);
        Assert.DoesNotContain("    _ => 0", text);
    }

    [Fact]
    public void Render_RootFileLabel_StillUsesRootSource()
    {
        var rootSource = new SourceStream("x :: y\n", 4);
        var diagnostic = Eidosc.Diagnostic.Diagnostic.Error("cannot find value `y`", "E3000")
            .WithLabel(new SourceSpan(new SourceLocation(8, 0, 8, "test.eidos"), 1), "unknown symbol");

        var writer = new StringWriter();
        DiagnosticRenderer.Render(
            diagnostic,
            rootSource,
            writer,
            new DiagnosticRenderOptions
            {
                UseColors = false,
                FilePath = "test.eidos"
            });

        var text = writer.ToString();
        Assert.Contains("--> test.eidos:1:9", text);
        Assert.Contains("x :: y", text);
    }
}
