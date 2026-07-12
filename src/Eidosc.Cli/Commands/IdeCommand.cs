using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.Text.Json;
using Eidosc.Cli.Resources;
using Eidosc.Ide;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

/// <summary>
/// IDE 语义快照命令（JSON）
/// </summary>
public static class IdeCommand
{
    public static Command Create()
    {
        var importRootOption = ImportRootOptions.Create();
        var command = new Command("ide", CliMessages.IdeCommandDescription)
        {
            new Argument<string>("source", () => "", CliMessages.IdeSourceArgumentDescription),
            new Option<string>("--project", CliMessages.IdeProjectOptionDescription),
            new Option<string>("--target-name", CliMessages.IdeTargetNameOptionDescription),
            new Option<bool>("--stdin", CliMessages.IdeStdinOptionDescription),
            new Option<CompilePhase?>("--phase", () => CompilePhase.Types, CliMessages.IdePhaseOptionDescription),
            new Option<bool>("--pretty", CliMessages.IdePrettyOptionDescription),
            importRootOption
        };

        command.Handler = CommandHandler.Create<IdeOptions>(Execute);
        return command;
    }

    private sealed class IdeOptions
    {
        public string Source { get; set; } = "";
        public string? Project { get; set; }
        public string? TargetName { get; set; }
        public bool Stdin { get; set; }
        public CompilePhase? Phase { get; set; } = CompilePhase.Types;
        public bool Pretty { get; set; }
        public string[] ImportRoot { get; set; } = [];
    }

    private static async Task<int> Execute(IdeOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();
        var inputFile = NormalizeInputFile(options.Source);
        try
        {
            var inputResolution = ProjectCommandInputResolver.ResolveDocument(
                options.Source,
                options.Project,
                options.TargetName,
                options.ImportRoot);
            inputFile = inputResolution.SourceFilePath;
            CliOutput.WriteAction(
                CliMessages.IdeCheckingAction,
                CliMessages.IdeCheckingDetail(inputFile, options.Phase),
                useColors: false,
                output: Console.Error);
            var sourceText = await LoadSourceTextAsync(options, inputFile);
            var stopAtPhase = CliCompilationPhaseMapper.MapPhase(options.Phase, CompilationPhase.Types);

            var pipeline = new CompilationPipeline(sourceText, new CompilationOptions
            {
                InputFile = inputFile,
                LanguageVersion = inputResolution.GetLanguageVersion(),
                StopAtPhase = stopAtPhase,
                DebugLevel = Eidosc.Debug.DebugLevel.Minimal,
                UseColors = false,
                Verbose = false,
                ImportSearchRoots = inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                    inputResolution.ImportResolution.EffectiveSearchRoots,
                PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ?? new Dictionary<string, string[]>(StringComparer.Ordinal)
            });

            var result = pipeline.Run();
            var snapshot = IdeSemanticSnapshotBuilder.Build(result);
            await WriteJsonAsync(snapshot, options.Pretty);
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "ide",
                result.Success,
                commandStopwatch.Elapsed,
                useColors: false,
                details: CliMessages.IdeFinishedDetails(result.CompletedPhase, result.Diagnostics.Count),
                output: Console.Error);
            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            var fallback = new IdeSemanticSnapshot
            {
                Success = false,
                InputFile = inputFile,
                CompletedPhase = CliMessages.PhaseNone,
                Diagnostics =
                [
                    new IdeDiagnosticEntry
                    {
                        Severity = "error",
                        Code = "E0001",
                        Message = CliMessages.IdeCommandFailed(ex.Message)
                    }
                ]
            };

            await WriteJsonAsync(fallback, options.Pretty);
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "ide",
                false,
                commandStopwatch.Elapsed,
                useColors: false,
                details: CliMessages.IdeSnapshotFailedDetail,
                output: Console.Error);
            return 1;
        }
    }

    private static async Task<string> LoadSourceTextAsync(IdeOptions options, string inputFile)
    {
        if (options.Stdin)
        {
            var sourceText = await Console.In.ReadToEndAsync();
            if (!string.IsNullOrEmpty(sourceText))
            {
                return sourceText;
            }
        }

        if (!File.Exists(inputFile))
        {
            return "";
        }

        return await File.ReadAllTextAsync(inputFile);
    }

    private static async Task WriteJsonAsync(IdeSemanticSnapshot snapshot, bool pretty)
    {
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = pretty
        });
        await Console.Out.WriteAsync(json);
    }

    private static string NormalizeInputFile(string inputFile)
    {
        if (string.IsNullOrWhiteSpace(inputFile))
        {
            return "stdin.eidos";
        }

        try
        {
            return Path.GetFullPath(inputFile);
        }
        catch
        {
            return inputFile;
        }
    }

}
