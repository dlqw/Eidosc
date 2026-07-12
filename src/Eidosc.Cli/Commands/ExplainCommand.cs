using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Text.Json;
using Eidosc.Cli.Resources;
using Eidosc.Diagnostic;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.Utils;

namespace Eidosc.Cli.Commands;

public static class ExplainCommand
{
    private static readonly HashSet<string> PatternCoverageCodes = ["W4200", "W4201"];

    public static Command Create()
    {
        var command = new Command("explain", "输出编译器分析解释")
        {
            CreatePatternCoverageCommand(),
        };

        return command;
    }

    internal static PatternCoverageExplainReport BuildPatternCoverageReport(
        CompilationResult result,
        string inputFile)
    {
        var entries = result.Diagnostics
            .Where(diagnostic => diagnostic.Code != null && PatternCoverageCodes.Contains(diagnostic.Code))
            .Select(diagnostic => BuildPatternCoverageEntry(diagnostic, inputFile))
            .ToArray();

        return new PatternCoverageExplainReport(inputFile, entries);
    }

    internal static string FormatPatternCoverageReport(PatternCoverageExplainReport report)
    {
        using var writer = new StringWriter();
        writer.WriteLine($"Pattern coverage explanations: {report.Entries.Count}");
        writer.WriteLine($"Input: {report.InputFile}");

        if (report.Entries.Count == 0)
        {
            writer.WriteLine("No pattern coverage diagnostics.");
            return writer.ToString();
        }

        for (var index = 0; index < report.Entries.Count; index++)
        {
            var entry = report.Entries[index];
            writer.WriteLine();
            writer.WriteLine($"{index + 1}. [{entry.Code}] {entry.Message}");
            if (entry.Location != null)
            {
                writer.WriteLine(
                    $"   location: {entry.Location.File}:{entry.Location.Line}:{entry.Location.Column}");
            }

            if (entry.Notes.Count > 0)
            {
                writer.WriteLine("   notes:");
                foreach (var note in entry.Notes)
                {
                    writer.WriteLine($"   - {note}");
                }
            }
        }

        return writer.ToString();
    }

    internal static string FormatPatternCoverageReportJson(PatternCoverageExplainReport report, bool pretty)
    {
        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = pretty
        });
    }

    private static Command CreatePatternCoverageCommand()
    {
        var importRootOption = ImportRootOptions.Create();
        var command = new Command("pattern-coverage", "解释模式覆盖诊断 W4200/W4201")
        {
            new Argument<string>("source", () => "", CliMessages.SourceArgumentDescription),
            new Option<string>("--project", CliMessages.ProjectOptionDescription),
            new Option<string>("--target-name", "要分析的入口目标名称"),
            new Option<bool>("--json", "输出机器可读 JSON"),
            new Option<bool>("--pretty", "格式化 JSON 输出"),
            importRootOption
        };

        command.Handler = CommandHandler.Create<PatternCoverageOptions>(ExecutePatternCoverageAsync);
        return command;
    }

    private static async Task<int> ExecutePatternCoverageAsync(PatternCoverageOptions options)
    {
        ProjectCommandInputResolution inputResolution;
        try
        {
            inputResolution = ProjectCommandInputResolver.Resolve(
                options.Source,
                options.Project,
                options.TargetName,
                options.ImportRoot);
        }
        catch (InvalidOperationException ex)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, ex.Message, useColors: false);
            return 1;
        }

        if (!File.Exists(inputResolution.SourceFilePath))
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Error,
                CliMessages.SourceFileNotFound(inputResolution.SourceFilePath),
                useColors: false);
            return 1;
        }

        var sourceCode = await File.ReadAllTextAsync(inputResolution.SourceFilePath);
        var result = new CompilationPipeline(sourceCode, new CompilationOptions
        {
            InputFile = inputResolution.SourceFilePath,
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false,
            ImportSearchRoots = inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                inputResolution.ImportResolution.EffectiveSearchRoots,
            PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ??
                                 new Dictionary<string, string[]>(StringComparer.Ordinal)
        }).Run();

        if (result.Diagnostics.Any(diagnostic => diagnostic.Level == DiagnosticLevel.Error))
        {
            CliOutput.RenderDiagnostics(result, useColors: false);
            return 1;
        }

        var report = BuildPatternCoverageReport(result, inputResolution.SourceFilePath);
        var output = options.Json
            ? FormatPatternCoverageReportJson(report, options.Pretty)
            : FormatPatternCoverageReport(report);
        Console.Out.Write(output);
        if (options.Json)
        {
            Console.Out.WriteLine();
        }

        return 0;
    }

    private static PatternCoverageExplainEntry BuildPatternCoverageEntry(
        Diagnostic.Diagnostic diagnostic,
        string inputFile)
    {
        var label = diagnostic.Labels.FirstOrDefault();
        return new PatternCoverageExplainEntry(
            diagnostic.Code ?? "",
            diagnostic.Level.ToString().ToLowerInvariant(),
            diagnostic.Message,
            label != null && HasMeaningfulSpan(label.Span)
                ? PatternCoverageExplainLocation.FromSpan(inputFile, label.Span)
                : null,
            diagnostic.Notes.ToArray());
    }

    private static bool HasMeaningfulSpan(SourceSpan span)
    {
        return span.Length > 0 ||
               span.Location.Position > 0 ||
               span.Location.Line > 0 ||
               span.Location.Column > 0;
    }

    private static void WriteIndentedBlock(TextWriter writer, string content, int spaces)
    {
        var indent = new string(' ', spaces);
        foreach (var line in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            writer.Write(indent);
            writer.WriteLine(line);
        }
    }

    private sealed class PatternCoverageOptions
    {
        public string Source { get; set; } = "";
        public string? Project { get; set; }
        public string? TargetName { get; set; }
        public bool Json { get; set; }
        public bool Pretty { get; set; }
        public string[] ImportRoot { get; set; } = [];
    }

}

public sealed record PatternCoverageExplainReport(
    string InputFile,
    IReadOnlyList<PatternCoverageExplainEntry> Entries);

public sealed record PatternCoverageExplainEntry(
    string Code,
    string Severity,
    string Message,
    PatternCoverageExplainLocation? Location,
    IReadOnlyList<string> Notes);

public sealed record PatternCoverageExplainLocation(
    string File,
    int Line,
    int Column)
{
    public static PatternCoverageExplainLocation FromSpan(string inputFile, SourceSpan span)
    {
        return new PatternCoverageExplainLocation(
            string.IsNullOrWhiteSpace(span.FilePath) ? inputFile : span.FilePath,
            span.Location.Line + 1,
            span.Location.Column + 1);
    }
}

