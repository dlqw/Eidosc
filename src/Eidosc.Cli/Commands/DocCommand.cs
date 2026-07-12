using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Doc;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

/// <summary>
/// 文档生成命令 - 从 Eidos 源码生成 API 文档
/// </summary>
public static class DocCommand
{
    public static Command Create()
    {
        var importRootOption = ImportRootOptions.Create();

        var command = new Command("doc", CliMessages.DocCommandDescription)
        {
            new Argument<string>("source", () => "", CliMessages.SourceArgumentDescription),

            new Option<string>("--project", CliMessages.ProjectOptionDescription),
            new Option<string>("--target-name", CliMessages.DocTargetNameOptionDescription),
            new Option<string>(["--output", "-o"], CliMessages.DocOutputOptionDescription),
            new Option<string>("--format", () => "markdown", CliMessages.DocFormatOptionDescription),
            importRootOption
        };

        command.Handler = CommandHandler.Create<DocOptions>(Execute);
        return command;
    }

    private sealed class DocOptions
    {
        public string Source { get; set; } = "";
        public string? Project { get; set; }
        public string? TargetName { get; set; }
        public string? Output { get; set; }
        public string Format { get; set; } = "markdown";
        public string[] ImportRoot { get; set; } = [];
    }

    private static async Task<int> Execute(DocOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();
        var normalizedFormat = (options.Format ?? "markdown").Trim().ToLowerInvariant();
        if (normalizedFormat is not ("markdown" or "md" or "html"))
        {
            CliOutput.WriteStatus(Diagnostic.DiagnosticLevel.Error, CliMessages.DocInvalidFormat, true);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("doc", false, commandStopwatch.Elapsed, true, CliMessages.DocInvalidFormatDetail);
            return 1;
        }

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
            CliOutput.WriteStatus(Diagnostic.DiagnosticLevel.Error, ex.Message, true);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("doc", false, commandStopwatch.Elapsed, true, CliMessages.InputResolutionFailedDetail);
            return 1;
        }

        if (!File.Exists(inputResolution.SourceFilePath))
        {
            CliOutput.WriteStatus(Diagnostic.DiagnosticLevel.Error,
                CliMessages.SourceFileNotFound(inputResolution.SourceFilePath), true);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("doc", false, commandStopwatch.Elapsed, true, CliMessages.SourceFileMissingDetail);
            return 1;
        }

        var sourcePath = inputResolution.SourceFilePath;
        var sourceCode = await File.ReadAllTextAsync(sourcePath);

        var compileOptions = new CompilationOptions
        {
            InputFile = sourcePath,
            LanguageVersion = inputResolution.GetLanguageVersion(),
            StopAtPhase = CompilationPhase.Types,
            DebugLevel = Eidosc.Debug.DebugLevel.Minimal,
            UseColors = false,
            ImportSearchRoots = inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                inputResolution.ImportResolution.EffectiveSearchRoots,
            PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ?? new Dictionary<string, string[]>(StringComparer.Ordinal)
        };

        CliOutput.WriteAction(CliMessages.DocumentingAction, CliMessages.DocActionSubject(sourcePath, normalizedFormat), true);
        CliOutput.WriteStatus(Diagnostic.DiagnosticLevel.Info, CliMessages.DocGeneratingStatus(sourcePath), true);

        var pipeline = new CompilationPipeline(sourceCode, compileOptions);
        var result = pipeline.Run();

        if (!result.Success)
        {
            CliOutput.RenderDiagnostics(result, true);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("doc", false, commandStopwatch.Elapsed, true, CliMessages.PhaseFinishedDetail(result.CompletedPhase.ToString()));
            return 1;
        }

        var docModule = DocGenerator.Generate(result);

        var output = normalizedFormat switch
        {
            "html" => HtmlDocRenderer.Render(docModule),
            _ => MarkdownDocRenderer.Render(docModule)
        };

        var extension = normalizedFormat switch
        {
            "html" => ".html",
            _ => ".md"
        };

        var outputPath = options.Output ?? Path.ChangeExtension(sourcePath, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(outputPath, output);

        CliOutput.WriteArtifact(CliMessages.ArtifactKindDocumentation, outputPath, true);
        CliOutput.WriteStatus(Diagnostic.DiagnosticLevel.Help,
            CliMessages.DocGeneratedStatus(outputPath), true);
        CliOutput.WriteStatus(Diagnostic.DiagnosticLevel.Note,
            CliMessages.DocModuleSummary(
                docModule.Name,
                docModule.Types.Count,
                docModule.Functions.Count,
                docModule.Traits.Count),
            true);

        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            "doc",
            true,
            commandStopwatch.Elapsed,
            true,
            CliMessages.DocFinishedDetail(docModule.Types.Count, docModule.Functions.Count, docModule.Traits.Count));

        return 0;
    }
}
