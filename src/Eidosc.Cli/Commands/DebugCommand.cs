using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Diagnostic;
using Eidosc.Debug;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

/// <summary>
/// 调试命令 - 生成详细的调试输出
/// </summary>
public static class DebugCommand
{
    public static Command Create()
    {
        var importRootOption = ImportRootOptions.Create();
        var command = new Command("debug", CliMessages.DebugCommandDescription)
        {
            new Argument<string>("source", () => "", CliMessages.SourceArgumentDescription),
            new Option<string>("--project", CliMessages.ProjectOptionDescription),
            new Option<string>("--target-name", CliMessages.DebugTargetNameOptionDescription),
            ProjectCommandSourceInputResolver.CreateSourceTextOption(),
            ProjectCommandSourceInputResolver.CreateStdinOption(),
            new Option<string>("--debug-output", CliMessages.DebugOutputOptionDescription),
            new Option<DebugLevel>("--debug-level", () => DebugLevel.Diagnostic, CliMessages.DebugLevelOptionDescription),
            new Option<DebugGraphFormat>("--debug-graph-format", () => DebugGraphFormat.None, CliMessages.BuildDebugGraphFormatOptionDescription),
            new Option<bool>("--no-color", CliMessages.CliNoColorOptionDescription),
            new Option<bool>("--emit-cfg", CliMessages.DebugEmitCfgOptionDescription),
            MirOptimizationOptions.CreateEnableOption(),
            MirOptimizationOptions.CreateDisableOption(),
            new Option<string[]>("--werror", CliMessages.WerrorOptionDescription),
            new Option<bool>("--werror-all", CliMessages.WerrorAllOptionDescription),
            DenyOptionParser.Create(),
            importRootOption
        };

        command.Handler = CommandHandler.Create<DebugOptions>(Execute);

        return command;
    }

    private sealed class DebugOptions
    {
        public string Source { get; set; } = "";
        public string? Project { get; set; }
        public string? TargetName { get; set; }
        public string? SourceText { get; set; }
        public bool Stdin { get; set; }
        public string? DebugOutput { get; set; }
        public DebugLevel DebugLevel { get; set; } = DebugLevel.Diagnostic;
        public DebugGraphFormat DebugGraphFormat { get; set; } = DebugGraphFormat.None;
        public bool EmitCfg { get; set; }
        public bool MirOpt { get; set; }
        public bool NoMirOpt { get; set; }
        public bool Verbose { get; set; }
        public bool NoColor { get; set; }
        public string[] Werror { get; set; } = [];
        public bool WerrorAll { get; set; }
        public string[] Deny { get; set; } = [];
        public string[] ImportRoot { get; set; } = [];
    }

    private static async Task<int> Execute(DebugOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();

        ProjectCommandSourceInput sourceInput;
        try
        {
            sourceInput = await ProjectCommandSourceInputResolver.ResolveAndLoadAsync(
                options.Source,
                options.Project,
                options.TargetName,
                options.ImportRoot,
                options.SourceText,
                options.Stdin);
        }
        catch (InvalidOperationException ex)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, ex.Message, !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("debug", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.InputResolutionFailedDetail);
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, ex.Message, !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("debug", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.SourceFileMissingDetail);
            return 1;
        }

        var inputResolution = sourceInput.InputResolution;
        var sourcePath = sourceInput.SourceFilePath;
        var sourceCode = sourceInput.SourceText;

        var debugOutputPath = ProjectCommandPaths.ResolveDebugOutputPath(
            options.DebugOutput,
            inputResolution);

        CliOutput.WriteAction(CliMessages.DebuggingAction, CliMessages.DebugActionSubject(sourcePath, options.DebugLevel), !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Info, CliMessages.DebugCompileStatus(sourcePath), !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.DebugOutputDirectoryStatus(debugOutputPath), !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.DebugLevelStatus(options.DebugLevel), !options.NoColor);
        if (options.DebugGraphFormat != DebugGraphFormat.None)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.DebugGraphArtifactsStatus(options.DebugGraphFormat), !options.NoColor);
        }
        ProjectImportResolutionCli.WriteSummary(
            inputResolution.ImportResolution,
            inputResolution.ProjectTarget,
            !options.NoColor);
        MirOptimizationOptions.WriteStatus(options.NoMirOpt, !options.NoColor);
        if (options.WerrorAll)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.WarningAsErrorAllStatus, !options.NoColor);
        }
        else
        {
            var warningCodes = WarningOptionParser.ParseWarningCodes(options.Werror);
            if (warningCodes.Count > 0)
            {
                CliOutput.WriteStatus(
                    DiagnosticLevel.Note,
                    CliMessages.WarningAsErrorCodesStatus(string.Join(", ", warningCodes.OrderBy(code => code, StringComparer.Ordinal))),
                    !options.NoColor);
            }
        }

        var compileOptions = new CompilationOptions
        {
            InputFile = sourcePath,
            LanguageVersion = inputResolution.GetLanguageVersion(),
            DebugOutputPath = debugOutputPath,
            CleanDebugOutput = true,
            DebugLevel = options.DebugLevel,
            DebugGraphFormat = options.DebugGraphFormat,
            EmitCfg = options.EmitCfg,
            EnableMirOptimizations = MirOptimizationOptions.IsEnabled(options.NoMirOpt),
            Verbose = options.Verbose,
            UseColors = !options.NoColor,
            EmitStyleSuggestions = true,
            AllowVirtualInputFile = sourceInput.IsInMemorySource,
            TreatWarningsAsErrors = options.WerrorAll,
            DenyStyle = DenyOptionParser.IncludesStyle(options.Deny),
            WarningCodesAsErrors = WarningOptionParser.ParseWarningCodes(options.Werror),
            ImportSearchRoots = inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                inputResolution.ImportResolution.EffectiveSearchRoots,
            PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ?? new Dictionary<string, string[]>(StringComparer.Ordinal)
        };

        // 创建编译管道
        var pipeline = new CompilationPipeline(sourceCode, compileOptions);
        var result = pipeline.Run();

        CliOutput.RenderDiagnostics(result, !options.NoColor);
        CliOutput.WriteStatus(
            result.Success ? DiagnosticLevel.Info : DiagnosticLevel.Error,
            result.Success ? CliMessages.DebugSucceededStatus : CliMessages.DebugFailedStatus,
            !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.CompletedPhaseStatus(result.CompletedPhase), !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.TotalTimeStatus(result.TotalTime.TotalMilliseconds), !options.NoColor);

        if (result.PhaseTimes.Count > 0)
        {
            foreach (var (phase, time) in result.PhaseTimes.OrderBy(entry => entry.Key))
            {
                if (result.PhaseAllocations.TryGetValue(phase, out var allocatedBytes))
                {
                    CliOutput.WriteStatus(
                        DiagnosticLevel.Note,
                        CliMessages.PhaseTimeAllocationStatus(phase, time.TotalMilliseconds, CliFormatters.FormatBytes(allocatedBytes)),
                        !options.NoColor);
                }
                else
                {
                    CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.PhaseTimeStatus(phase, time.TotalMilliseconds), !options.NoColor);
                }
            }
        }

        CliOutput.WriteStatus(DiagnosticLevel.Help, CliMessages.DebugSavedStatus(debugOutputPath), !options.NoColor);
        CliOutput.WriteArtifact(CliMessages.ArtifactKindDebugDirectory, debugOutputPath, !options.NoColor);
        if (options.DebugGraphFormat != DebugGraphFormat.None)
        {
            var graphPattern = options.DebugGraphFormat switch
            {
                DebugGraphFormat.D2 => "**/*.d2",
                DebugGraphFormat.Svg => "**/*.svg",
                _ => "**/*.{d2,svg}"
            };
            CliOutput.WriteArtifact(CliMessages.ArtifactKindDebugGraphs, $"{debugOutputPath} ({graphPattern})", !options.NoColor);
        }

        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            "debug",
            result.Success,
            commandStopwatch.Elapsed,
            !options.NoColor,
            CliMessages.PhaseFinishedDetail(result.CompletedPhase.ToString()));

        return result.Success ? 0 : 1;
    }

}
