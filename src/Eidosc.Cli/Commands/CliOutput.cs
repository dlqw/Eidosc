using Eidosc.Diagnostic;
using Eidosc;
using Eidosc.Cli.Resources;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

internal static class CliOutput
{
    public static void WriteAction(
        string action,
        string message,
        bool useColors,
        TextWriter? output = null)
    {
        output ??= Console.Out;
        var previous = Console.ForegroundColor;

        if (useColors)
        {
            Console.ForegroundColor = action switch
            {
                var value when value == CliMessages.FinishedAction => ConsoleColor.Green,
                var value when value == CliMessages.FailedAction => ConsoleColor.Red,
                var value when value == CliMessages.ArtifactAction => ConsoleColor.Cyan,
                _ => ConsoleColor.Blue
            };
        }

        output.Write($"{action,12} ");

        if (useColors)
        {
            Console.ForegroundColor = ConsoleColor.White;
        }

        output.WriteLine(message);

        if (useColors)
        {
            Console.ForegroundColor = previous;
        }
    }

    public static void WriteFinished(
        string command,
        bool success,
        TimeSpan elapsed,
        bool useColors,
        string? details = null,
        TextWriter? output = null)
    {
        var action = success ? CliMessages.FinishedAction : CliMessages.FailedAction;
        var message = string.IsNullOrWhiteSpace(details)
            ? CliMessages.CommandFinishedMessage(command, FormatDuration(elapsed))
            : CliMessages.CommandFinishedMessageWithDetails(command, FormatDuration(elapsed), details);
        WriteAction(action, message, useColors, output);
    }

    public static void WriteArtifact(
        string kind,
        string path,
        bool useColors,
        TextWriter? output = null)
    {
        WriteAction(CliMessages.ArtifactAction, CliMessages.ArtifactMessage(kind, path), useColors, output);
    }

    public static void WriteStatus(DiagnosticLevel level, string message, bool useColors, TextWriter? output = null)
    {
        output ??= level == DiagnosticLevel.Error ? Console.Error : Console.Out;
        var previous = Console.ForegroundColor;

        if (useColors)
        {
            Console.ForegroundColor = level switch
            {
                DiagnosticLevel.Error => ConsoleColor.Red,
                DiagnosticLevel.Warning => ConsoleColor.Yellow,
                DiagnosticLevel.Info => ConsoleColor.Blue,
                DiagnosticLevel.Note => ConsoleColor.Cyan,
                DiagnosticLevel.Help => ConsoleColor.Green,
                _ => ConsoleColor.White
            };
        }

        output.Write(GetDiagnosticLevelLabel(level));
        output.Write(": ");

        if (useColors)
        {
            Console.ForegroundColor = ConsoleColor.White;
        }

        output.WriteLine(message);

        if (useColors)
        {
            Console.ForegroundColor = previous;
        }
    }

    public static void RenderDiagnostics(CompilationResult result, bool useColors, TextWriter? output = null)
    {
        if (result.Diagnostics.Count == 0)
        {
            return;
        }

        output ??= Console.Error;
        var source = new SourceStream(result.SourceText, 4);
        var options = new DiagnosticRenderOptions
        {
            UseColors = useColors,
            FilePath = string.IsNullOrWhiteSpace(result.InputFile) ? null : result.InputFile
        };

        foreach (var diagnostic in result.Diagnostics)
        {
            DiagnosticRenderer.Render(diagnostic, source, output, options);
        }
    }

    public static void RenderDiagnostics(
        IReadOnlyList<Diagnostic.Diagnostic> diagnostics,
        string sourceText,
        string? filePath,
        bool useColors,
        TextWriter? output = null)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        output ??= Console.Error;
        var source = new SourceStream(sourceText, 4);
        var options = new DiagnosticRenderOptions
        {
            UseColors = useColors,
            FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath
        };
        foreach (var diagnostic in diagnostics)
        {
            DiagnosticRenderer.Render(diagnostic, source, output, options);
        }
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds >= 1
            ? $"{elapsed.TotalSeconds:F2}s"
            : $"{elapsed.TotalMilliseconds:F0}ms";
    }

    private static string GetDiagnosticLevelLabel(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Error => CliMessages.DiagnosticLevelError,
        DiagnosticLevel.Warning => CliMessages.DiagnosticLevelWarning,
        DiagnosticLevel.Info => CliMessages.DiagnosticLevelInfo,
        DiagnosticLevel.Note => CliMessages.DiagnosticLevelNote,
        DiagnosticLevel.Help => CliMessages.DiagnosticLevelHelp,
        _ => level.ToString().ToLowerInvariant()
    };
}
