using Eidosc.Utils;

namespace Eidosc.Diagnostic;

public sealed class DiagnosticRenderOptions
{
    public bool UseColors { get; init; } = true;
    public string? FilePath { get; init; }
}

public static class DiagnosticRenderer
{
    public static void Render(Diagnostic diagnostic, ISourceStream source, TextWriter output, DiagnosticRenderOptions? options = null)
    {
        options ??= new DiagnosticRenderOptions();

        WriteLevel(output, diagnostic.Level, options.UseColors);
        if (!string.IsNullOrEmpty(diagnostic.Code))
        {
            output.Write($"[{diagnostic.Code}]");
        }

        output.Write(": ");
        WriteColored(output, diagnostic.Message, ConsoleColor.White, options.UseColors, lineBreak: true);

        foreach (var label in diagnostic.Labels)
        {
            RenderSnippet(label, source, output, diagnostic.Level, options);
        }

        foreach (var note in diagnostic.Notes)
        {
            WriteAnnotation(
                output,
                DiagnosticMessages.DiagnosticLevelLabel(DiagnosticLevel.Note),
                note,
                ConsoleColor.Cyan,
                options.UseColors);
        }

        foreach (var help in diagnostic.Helps)
        {
            WriteAnnotation(
                output,
                DiagnosticMessages.DiagnosticLevelLabel(DiagnosticLevel.Help),
                help,
                ConsoleColor.Green,
                options.UseColors);
        }

        foreach (var suggestion in diagnostic.Suggestions)
        {
            WriteAnnotation(
                output,
                DiagnosticMessages.DiagnosticLevelLabel(DiagnosticLevel.Help),
                suggestion.Message,
                ConsoleColor.Green,
                options.UseColors);
            if (suggestion.Span is { } suggestionSpan)
            {
                RenderSnippet(new DiagnosticLabel(suggestionSpan, suggestion.Replacement ?? string.Empty), source, output, DiagnosticLevel.Help, options);
            }

            if (!string.IsNullOrEmpty(suggestion.HelpUrl))
            {
                WriteAnnotation(
                    output,
                    DiagnosticMessages.DiagnosticLevelLabel(DiagnosticLevel.Info),
                    DiagnosticMessages.DiagnosticSuggestionHelpUrl(suggestion.HelpUrl),
                    ConsoleColor.Blue,
                    options.UseColors);
            }
        }

        foreach (var related in diagnostic.Related)
        {
            WriteAnnotation(
                output,
                DiagnosticMessages.DiagnosticLevelLabel(related.Level),
                related.Message,
                GetColor(related.Level),
                options.UseColors);
            foreach (var label in related.Labels)
            {
                RenderSnippet(label, source, output, related.Level, options);
            }
        }

        output.WriteLine();
    }

    private static void RenderSnippet(
        DiagnosticLabel label,
        ISourceStream source,
        TextWriter output,
        DiagnosticLevel level,
        DiagnosticRenderOptions options)
    {
        var span = label.Span;
        var startLoc = span.Location;
        var fullText = source.Text;
        if (fullText.Length == 0)
        {
            return;
        }

        var safePos = Math.Clamp(startLoc.Position, 0, Math.Max(0, fullText.Length - 1));
        if (safePos > 0 && safePos < fullText.Length && (fullText[safePos] == '\n' || fullText[safePos] == '\r'))
        {
            safePos--;
        }

        var lineStart = fullText.LastIndexOf('\n', safePos);
        lineStart = lineStart == -1 ? 0 : lineStart + 1;

        var lineEnd = fullText.IndexOf('\n', safePos);
        if (lineEnd == -1 || lineEnd < lineStart)
        {
            lineEnd = fullText.Length;
        }

        var lineLength = Math.Max(0, lineEnd - lineStart);
        var lineContent = fullText.Substring(lineStart, lineLength).TrimEnd('\r', '\n');
        var lineNumber = startLoc.Line + 1;
        var lineNumStr = lineNumber.ToString();
        var gutterWidth = lineNumStr.Length;
        var blankGutter = new string(' ', gutterWidth);
        var filePath = options.FilePath ?? DiagnosticMessages.DiagnosticMemoryFilePath;

        WriteColored(output, $" {blankGutter}--> {filePath}:{lineNumber}:{startLoc.Column + 1}", ConsoleColor.DarkCyan, options.UseColors, true);
        WriteColored(output, $" {blankGutter} |", ConsoleColor.DarkCyan, options.UseColors, true);

        output.Write($" {lineNumStr} |");
        output.Write(' ');
        output.WriteLine(lineContent.Replace("\t", "    "));

        var caretPosInLine = Math.Min(Math.Max(0, safePos - lineStart), lineContent.Length);
        var prefixText = lineContent.Substring(0, caretPosInLine);
        var visualIndent = prefixText.Replace("\t", "    ").Length;
        var remainingLineLength = Math.Max(1, lineContent.Length - caretPosInLine);
        var pointerLen = Math.Clamp(span.Length, 1, remainingLineLength);

        WriteColored(output, $" {blankGutter} |", ConsoleColor.DarkCyan, options.UseColors, false);
        output.Write(' ');
        output.Write(new string(' ', visualIndent));
        WriteColored(output, new string('^', pointerLen), GetColor(level), options.UseColors, false);
        if (!string.IsNullOrEmpty(label.Message))
        {
            output.Write(' ');
            WriteColored(output, label.Message, GetColor(level), options.UseColors, false);
        }

        output.WriteLine();
    }

    private static void WriteAnnotation(TextWriter output, string prefix, string message, ConsoleColor color, bool useColors)
    {
        output.Write(" = ");
        WriteColored(output, prefix, color, useColors, false);
        output.Write(": ");
        output.WriteLine(message);
    }

    private static void WriteLevel(TextWriter output, DiagnosticLevel level, bool useColors)
    {
        WriteColored(output, DiagnosticMessages.DiagnosticLevelLabel(level), GetColor(level), useColors, false);
    }

    private static void WriteColored(TextWriter output, string text, ConsoleColor color, bool useColors, bool lineBreak)
    {
        var previous = Console.ForegroundColor;
        if (useColors)
        {
            Console.ForegroundColor = color;
        }

        if (lineBreak)
        {
            output.WriteLine(text);
        }
        else
        {
            output.Write(text);
        }

        if (useColors)
        {
            Console.ForegroundColor = previous;
        }
    }

    private static ConsoleColor GetColor(DiagnosticLevel level)
    {
        return level switch
        {
            DiagnosticLevel.Error => ConsoleColor.Red,
            DiagnosticLevel.Warning => ConsoleColor.Yellow,
            DiagnosticLevel.Info => ConsoleColor.Blue,
            DiagnosticLevel.Note => ConsoleColor.Cyan,
            DiagnosticLevel.Help => ConsoleColor.Green,
            _ => ConsoleColor.White
        };
    }
}
