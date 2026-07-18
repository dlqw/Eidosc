using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.Text;
using Eidosc.Cli.Resources;
using Eidosc.Diagnostic;
using Eidosc.Debug;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Commands;

/// <summary>
/// 分析命令 - 分析源代码并输出诊断信息
/// </summary>
public static class AnalyzeCommand
{
    public static Command Create()
    {
        var importRootOption = ImportRootOptions.Create();
        var command = new Command("analyze", CliMessages.AnalyzeCommandDescription)
        {
            new Argument<string>("source", () => "", CliMessages.SourceArgumentDescription),
            new Option<string>("--project", CliMessages.ProjectOptionDescription),
            new Option<string>("--target-name", CliMessages.AnalyzeTargetNameOptionDescription),
            ProjectCommandSourceInputResolver.CreateSourceTextOption(),
            ProjectCommandSourceInputResolver.CreateStdinOption(),
            new Option<CompilePhase?>("--phase", CliMessages.AnalyzePhaseOptionDescription),
            new Option<DebugLevel>("--debug-level", () => DebugLevel.Normal, CliMessages.DebugLevelOptionDescription),
            MirOptimizationOptions.CreateEnableOption(),
            MirOptimizationOptions.CreateDisableOption(),
            new Option<bool>("--profile", CliMessages.AnalyzeProfileOptionDescription),
            new Option<ProfilingTableFormat>(
                "--profile-format",
                () => ProfilingTableFormat.Markdown,
                CliMessages.AnalyzeProfileFormatOptionDescription),
            new Option<string>("--profile-output", CliMessages.AnalyzeProfileOutputOptionDescription),
            new Option<string>(
                "--profile-snapshot-output",
                CliMessages.AnalyzeProfileSnapshotOutputOptionDescription),
            new Option<string>("--profile-baseline", CliMessages.AnalyzeProfileBaselineOptionDescription),
            new Option<bool>("--no-color", CliMessages.CliNoColorOptionDescription),
            new Option<string[]>("--werror", CliMessages.WerrorOptionDescription),
            new Option<bool>("--werror-all", CliMessages.WerrorAllOptionDescription),
            DenyOptionParser.Create(),
            importRootOption
        };

        command.Handler = CommandHandler.Create<AnalyzeOptions>(Execute);

        return command;
    }

    private sealed class AnalyzeOptions
    {
        public string Source { get; set; } = "";
        public string? Project { get; set; }
        public string? TargetName { get; set; }
        public string? SourceText { get; set; }
        public bool Stdin { get; set; }
        public CompilePhase? Phase { get; set; }
        public DebugLevel DebugLevel { get; set; } = DebugLevel.Normal;
        public bool MirOpt { get; set; }
        public bool NoMirOpt { get; set; }
        public bool Profile { get; set; }
        public ProfilingTableFormat ProfileFormat { get; set; } = ProfilingTableFormat.Markdown;
        public string? ProfileOutput { get; set; }
        public string? ProfileSnapshotOutput { get; set; }
        public string? ProfileBaseline { get; set; }
        public bool Verbose { get; set; }
        public bool NoColor { get; set; }
        public string[] Werror { get; set; } = [];
        public bool WerrorAll { get; set; }
        public string[] Deny { get; set; } = [];
        public string[] ImportRoot { get; set; } = [];
    }

    private static async Task<int> Execute(AnalyzeOptions options)
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
            CliOutput.WriteFinished(
                "analyze",
                false,
                commandStopwatch.Elapsed,
                !options.NoColor,
                CliMessages.InputResolutionFailedDetail);
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Error,
                ex.Message,
                !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "analyze",
                false,
                commandStopwatch.Elapsed,
                !options.NoColor,
                CliMessages.SourceFileMissingDetail);
            return 1;
        }

        var inputResolution = sourceInput.InputResolution;
        var sourcePath = sourceInput.SourceFilePath;
        var sourceCode = sourceInput.SourceText;
        CliOutput.WriteAction(
            CliMessages.CheckingAction,
            CliMessages.CheckingActionSubject(sourcePath, options.Phase),
            !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Info, CliMessages.AnalyzeSourceStatus(sourcePath), !options.NoColor);

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
                    CliMessages.WarningAsErrorCodesStatus(
                        string.Join(", ", warningCodes.OrderBy(code => code, StringComparer.Ordinal))),
                    !options.NoColor);
            }
        }

        // 映射 CLI 阶段到内部阶段
        var internalPhase = CliCompilationPhaseMapper.MapPhase(options.Phase);

        var projectConfig = inputResolution.ImportResolution.ProjectFilePath != null
            ? EidosProjectConfigurationLoader.TryLoadFromPath(inputResolution.ImportResolution.ProjectFilePath)?.Configuration
            : EidosProjectConfigurationLoader.TryLoadNearest(inputResolution.SourceFilePath)?.Configuration;

        // 创建编译选项
        var compileOptions = new CompilationOptions
        {
            InputFile = sourcePath,
            LanguageVersion = inputResolution.GetLanguageVersion(),
            StopAtPhase = internalPhase,
            DebugLevel = options.DebugLevel,
            EnableMirOptimizations = MirOptimizationOptions.IsEnabled(options.NoMirOpt),
            EnableDetailedProfiling = options.Profile,
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
            ,MetaConfiguration = projectConfig?.Meta
        };

        // 运行编译管道
        var pipeline = new CompilationPipeline(sourceCode, compileOptions);
        var result = pipeline.Run();

        // 输出阶段结果
        if (options.Phase.HasValue)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.AnalyzePhaseStatus(options.Phase.Value),
                !options.NoColor);
            OutputPhaseResult(result, options.Phase.Value);
            OutputProfilingSummary(result, options.Profile);
        }
        else
        {
            // 默认输出所有阶段结果
            OutputAllPhaseResults(result, options.Profile);
        }

        if (options.Profile)
        {
            await OutputProfilingArtifactsAsync(
                result,
                options.ProfileFormat,
                options.ProfileOutput,
                options.ProfileSnapshotOutput,
                options.ProfileBaseline,
                !options.NoColor);
        }

        CliOutput.RenderDiagnostics(result, !options.NoColor);
        CliOutput.WriteStatus(
            result.Success ? DiagnosticLevel.Info : DiagnosticLevel.Error,
            result.Success ? CliMessages.AnalyzeCompletedStatus : CliMessages.AnalyzeFailedStatus,
            !options.NoColor);
        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            "analyze",
            result.Success,
            commandStopwatch.Elapsed,
            !options.NoColor,
            CliMessages.AnalyzePhaseDiagnosticsDetails(result.CompletedPhase, result.Diagnostics.Count));
        return result.Success ? 0 : 1;
    }

    private static void OutputPhaseResult(CompilationResult result, CompilePhase phase)
    {
        switch (phase)
        {
            case CompilePhase.Lexer:
                Console.WriteLine(CliMessages.AnalyzeTokenListHeader);
                foreach (var token in result.Tokens ?? [])
                {
                    Console.WriteLine($"  {token}");
                }
                break;

            case CompilePhase.Parser:
                Console.WriteLine(CliMessages.AnalyzeAstStructureHeader);
                if (result.Ast != null)
                {
                    Console.WriteLine(FormatAst(result.Ast));
                }
                break;

            case CompilePhase.Types:
                Console.WriteLine(CliMessages.AnalyzeTypeInferenceHeader);
                if (result.TypeInferer != null)
                {
                    Console.WriteLine(CliMessages.AnalyzeTypeInferenceCompleted);
                    if (result.TypeInferer.Diagnostics.Count > 0)
                    {
                        foreach (var diag in result.TypeInferer.Diagnostics)
                        {
                            Console.WriteLine($"    {diag.Message}");
                        }
                    }
                }
                break;

            default:
                Console.WriteLine(CliMessages.AnalyzePhaseCompletedHeader(phase));
                break;
        }
    }

    private static void OutputAllPhaseResults(CompilationResult result, bool includeDetailedProfiling)
    {
        var tokens = result.Tokens ?? [];
        Console.WriteLine(CliMessages.AnalyzeCompilationSummaryHeader);
        Console.WriteLine(CliMessages.AnalyzeCompletedPhaseSummary(result.CompletedPhase));
        Console.WriteLine(CliMessages.AnalyzeTokenCountSummary(tokens.Count));
        Console.WriteLine(CliMessages.AnalyzeTotalTimeSummary(result.TotalTime.TotalMilliseconds));
        OutputProfilingSummary(result, includeDetailedProfiling);

        if (result.Ast != null)
        {
            Console.WriteLine(CliMessages.AnalyzeAstGeneratedSummary);
        }
        if (result.SymbolTable != null)
        {
            Console.WriteLine(CliMessages.AnalyzeSymbolCountSummary(result.SymbolTable.Symbols.Count));
        }
        if (result.TypeInferer != null)
        {
            Console.WriteLine(CliMessages.AnalyzeTypeInferenceCompletedSummary);
        }
    }

    private static void OutputProfilingSummary(CompilationResult result, bool includeDetailedProfiling)
    {
        if (result.PhaseTimes.Count == 0)
        {
            return;
        }

        Console.WriteLine(CliMessages.AnalyzePhaseTimingHeader);
        foreach (var (phase, time) in result.PhaseTimes.OrderBy(entry => entry.Key))
        {
            if (result.PhaseAllocations.TryGetValue(phase, out var allocatedBytes))
            {
                Console.WriteLine(
                    CliMessages.AnalyzePhaseTimeAllocationLine(
                        phase,
                        time.TotalMilliseconds,
                        CliFormatters.FormatBytes(allocatedBytes)));
            }
            else
            {
                Console.WriteLine(CliMessages.AnalyzePhaseTimeLine(phase, time.TotalMilliseconds));
            }
        }

        if (!includeDetailedProfiling || result.SubphaseMetrics.Count == 0)
        {
            return;
        }

        Console.WriteLine(CliMessages.AnalyzeSubphaseTimingHeader);
        foreach (var phaseGroup in result.SubphaseMetrics
                     .OrderBy(metric => metric.Phase)
                     .ThenByDescending(metric => metric.Elapsed)
                     .GroupBy(metric => metric.Phase))
        {
            Console.WriteLine($"    [{phaseGroup.Key}]");
            foreach (var metric in phaseGroup)
            {
                Console.WriteLine(
                    CliMessages.AnalyzeSubphaseMetric(
                        metric.Name,
                        metric.Elapsed.TotalMilliseconds,
                        CliFormatters.FormatBytes(metric.AllocatedBytes),
                        CliFormatters.FormatSignedBytes(metric.ManagedBytesDelta),
                        metric.Gen0Collections,
                        metric.Gen1Collections,
                        metric.Gen2Collections));
            }
        }
    }

    private static async Task OutputProfilingArtifactsAsync(
        CompilationResult result,
        ProfilingTableFormat format,
        string? outputPath,
        string? snapshotOutputPath,
        string? baselinePath,
        bool useColors)
    {
        var hotspotReport = CompilationProfilingFormatter.FormatHotspotReport(result);
        var tableText = CompilationProfilingFormatter.FormatTables(result, format);
        string? comparisonReport = null;

        Console.WriteLine(CliMessages.AnalyzeHotspotSummaryHeader);
        Console.WriteLine(hotspotReport);

        if (!string.IsNullOrWhiteSpace(baselinePath))
        {
            var baselineSnapshot = CompilationProfilingFormatter.LoadSnapshot(Path.GetFullPath(baselinePath));
            comparisonReport = CompilationProfilingFormatter.FormatComparisonReport(result, baselineSnapshot);
            Console.WriteLine(CliMessages.AnalyzeBaselineComparisonHeader);
            Console.WriteLine(comparisonReport);
        }

        var fileText = BuildProfilingFileText(result, format, tableText, comparisonReport);

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, fileText, Encoding.UTF8);
            CliOutput.WriteArtifact(CliMessages.ArtifactKindProfileTable, fullPath, useColors);
            Console.WriteLine(CliMessages.AnalyzeProfileTableWritten(fullPath));
        }

        if (!string.IsNullOrWhiteSpace(snapshotOutputPath))
        {
            var snapshotPath = Path.GetFullPath(snapshotOutputPath);
            var snapshotDirectory = Path.GetDirectoryName(snapshotPath);
            if (!string.IsNullOrWhiteSpace(snapshotDirectory))
            {
                Directory.CreateDirectory(snapshotDirectory);
            }

            var snapshotJson = CompilationProfilingFormatter.FormatTables(result, ProfilingTableFormat.Json);
            await File.WriteAllTextAsync(snapshotPath, snapshotJson, Encoding.UTF8);
            CliOutput.WriteArtifact(CliMessages.ArtifactKindProfileSnapshot, snapshotPath, useColors);
            Console.WriteLine(CliMessages.AnalyzeProfileSnapshotWritten(snapshotPath));
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.WriteLine(CliMessages.AnalyzeProfileTableHeader);
            Console.WriteLine(tableText);
        }
    }

    private static string BuildProfilingFileText(
        CompilationResult result,
        ProfilingTableFormat format,
        string tableText,
        string? comparisonReport)
    {
        if (format != ProfilingTableFormat.Markdown)
        {
            return tableText;
        }

        var markdown = CompilationProfilingFormatter.FormatMarkdownReportWithTables(result);
        if (string.IsNullOrWhiteSpace(comparisonReport))
        {
            return markdown;
        }

        return $"{comparisonReport.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{markdown.TrimStart()}";
    }

    private static string FormatAst(object ast, int indent = 0)
    {
        var prefix = new string(' ', indent * 2);
        var type = ast.GetType();
        var props = type.GetProperties()
            .Where(p => p.GetValue(ast) != null)
            .Select(p => $"{p.Name}={FormatValue(p.GetValue(ast)!, indent + 1)}");

        return $"{type.Name} {{\n{prefix}  {string.Join($",\n{prefix}  ", props)}\n{prefix}}}";
    }

    private static string FormatValue(object value, int indent)
    {
        if (value is string s) return $"\"{s}\"";
        if (value.GetType().IsPrimitive) return value.ToString() ?? "";
        if (value.GetType().Name.EndsWith("Decl") || value.GetType().Name.EndsWith("Expr"))
        {
            return FormatAst(value, indent);
        }
        return value.ToString() ?? "";
    }

}
