using Eidosc.Utils;

namespace Eidosc.Diagnostic;

public sealed class DiagnosticRenderOptions
{
    public bool UseColors { get; init; } = true;
    public string? FilePath { get; init; }

    /// <summary>
    /// 按文件路径解析标签源文本。返回 null 表示无法解析（例如虚拟源名）。
    /// 未提供时，渲染器对磁盘上存在的绝对路径标签文件做受限回退读取。
    /// </summary>
    public Func<string, string?>? SourceResolver { get; init; }
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

        var foreignSourceCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var label in diagnostic.Labels)
        {
            RenderSnippet(label, source, output, diagnostic.Level, options, foreignSourceCache);
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
                RenderSnippet(new DiagnosticLabel(suggestionSpan, suggestion.Replacement ?? string.Empty), source, output, DiagnosticLevel.Help, options, foreignSourceCache);
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
                RenderSnippet(label, source, output, related.Level, options, foreignSourceCache);
            }
        }

        output.WriteLine();
    }

    private static void RenderSnippet(
        DiagnosticLabel label,
        ISourceStream source,
        TextWriter output,
        DiagnosticLevel level,
        DiagnosticRenderOptions options,
        Dictionary<string, string?>? foreignSourceCache = null)
    {
        var span = label.Span;
        var startLoc = span.Location;

        // 标签可能指向根输入之外的文件（import 的模块）。打印标签自己的文件路径，
        // 并解析该文件的文本；解析不到时退回根文本但不再谎报根文件的位置。
        var labelFilePath = span.FilePath;
        var isForeignLabel = !string.IsNullOrEmpty(labelFilePath) &&
                             !string.Equals(labelFilePath, options.FilePath, StringComparison.OrdinalIgnoreCase);
        string fullText = source.Text;
        string filePath = options.FilePath ?? DiagnosticMessages.DiagnosticMemoryFilePath;
        if (isForeignLabel)
        {
            filePath = labelFilePath!;
            var foreignText = TryResolveForeignSourceText(labelFilePath!, options, foreignSourceCache);
            if (foreignText != null)
            {
                fullText = foreignText;
            }
            else
            {
                WriteSnippetHeader(output, filePath, startLoc, options, sourceUnavailable: true);
                return;
            }
        }

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

        WriteSnippetHeader(output, filePath, startLoc, options, sourceUnavailable: false);
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

    private static void WriteSnippetHeader(
        TextWriter output,
        string filePath,
        SourceLocation startLoc,
        DiagnosticRenderOptions options,
        bool sourceUnavailable)
    {
        var lineDisplay = startLoc.Line + 1;
        var gutter = new string(' ', lineDisplay.ToString().Length);
        WriteColored(
            output,
            $" {gutter}--> {filePath}:{lineDisplay}:{startLoc.Column + 1}",
            ConsoleColor.DarkCyan,
            options.UseColors,
            lineBreak: true);
        if (sourceUnavailable)
        {
            WriteColored(output, $" {gutter} |", ConsoleColor.DarkCyan, options.UseColors, lineBreak: true);
            WriteColored(
                output,
                $" {gutter} | {DiagnosticMessages.DiagnosticForeignSourceUnavailable}",
                ConsoleColor.DarkCyan,
                options.UseColors,
                lineBreak: true);
        }
    }

    /// <summary>
    /// 解析根输入之外的标签文件文本：优先使用调用方提供的解析器，
    /// 其次对磁盘上存在的绝对路径做一次受限读取（结果在单次渲染内缓存）。
    /// </summary>
    private static string? TryResolveForeignSourceText(
        string filePath,
        DiagnosticRenderOptions options,
        Dictionary<string, string?>? cache)
    {
        if (cache != null && cache.TryGetValue(filePath, out var cached))
        {
            return cached;
        }

        string? resolved = null;
        if (options.SourceResolver != null)
        {
            resolved = options.SourceResolver(filePath);
        }

        if (resolved == null && Path.IsPathRooted(filePath) && File.Exists(filePath))
        {
            try
            {
                resolved = File.ReadAllText(filePath);
            }
            catch (IOException)
            {
                resolved = null;
            }
            catch (UnauthorizedAccessException)
            {
                resolved = null;
            }
        }

        if (cache != null)
        {
            cache[filePath] = resolved;
        }

        return resolved;
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
