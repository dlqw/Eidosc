using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosc.Cli.Resources;
using Eidosc.CodeGen;
using Eidosc.Diagnostic;
using Eidosc.Debug;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

public static class ProfileBatchCommand
{
    public static Command Create()
    {
        var command = new Command("profile-batch", CliMessages.ProfileBatchCommandDescription)
        {
            new Argument<string>("manifest", CliMessages.ProfileBatchManifestArgumentDescription),
            new Option<ProfilingTableFormat>("--format", () => ProfilingTableFormat.Markdown, CliMessages.ProfileBatchFormatOptionDescription),
            new Option<string>("--output", CliMessages.ProfileBatchOutputOptionDescription),
            new Option<int>("--iterations", () => 1, CliMessages.ProfileBatchIterationsOptionDescription),
            new Option<int>("--warmup", () => 0, CliMessages.ProfileBatchWarmupOptionDescription),
            new Option<int>("--top-phases", () => 10, CliMessages.ProfileBatchTopPhasesOptionDescription),
            new Option<int>("--top-subphases", () => 20, CliMessages.ProfileBatchTopSubphasesOptionDescription),
            new Option<bool>("--no-color", CliMessages.CliNoColorOptionDescription)
        };

        command.Handler = CommandHandler.Create<ProfileBatchOptions>(Execute);
        return command;
    }

    private sealed class ProfileBatchOptions
    {
        public string Manifest { get; set; } = "";
        public ProfilingTableFormat Format { get; set; } = ProfilingTableFormat.Markdown;
        public string? Output { get; set; }
        public int Iterations { get; set; } = 1;
        public int Warmup { get; set; }
        public int TopPhases { get; set; } = 10;
        public int TopSubphases { get; set; } = 20;
        public bool NoColor { get; set; }
    }

    private static async Task<int> Execute(ProfileBatchOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();

        if (options.Format is not (ProfilingTableFormat.Markdown or ProfilingTableFormat.Json))
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, CliMessages.ProfileBatchInvalidFormat, !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("profile-batch", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.ProfileBatchInvalidFormatDetail);
            return 1;
        }

        if (options.Iterations <= 0)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, CliMessages.ProfileBatchInvalidIterations, !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("profile-batch", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.ProfileBatchInvalidIterationsDetail);
            return 1;
        }

        if (options.Warmup < 0)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, CliMessages.ProfileBatchInvalidWarmup, !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("profile-batch", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.ProfileBatchInvalidWarmupDetail);
            return 1;
        }

        var manifestPath = Path.GetFullPath(options.Manifest);
        if (!File.Exists(manifestPath))
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, CliMessages.ProfileBatchManifestMissing(manifestPath), !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("profile-batch", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.ProfileBatchManifestMissingDetail);
            return 1;
        }

        var manifest = await LoadManifestAsync(manifestPath);
        if (manifest.Cases.Count == 0)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, CliMessages.ProfileBatchNoCases, !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("profile-batch", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.ProfileBatchNoCasesDetail);
            return 1;
        }

        var batchName = manifest.Name ?? Path.GetFileNameWithoutExtension(manifestPath);
        CliOutput.WriteAction(CliMessages.ProfilingAction, CliMessages.ProfileBatchSubject(batchName, manifestPath), !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Info, CliMessages.ProfileBatchStatus(batchName), !options.NoColor);
        CliOutput.WriteStatus(
            DiagnosticLevel.Note,
            CliMessages.ProfileBatchCaseCountStatus(manifest.Cases.Count, options.Iterations, options.Warmup),
            !options.NoColor);

        var caseResults = new List<BatchProfilingCaseResult>(manifest.Cases.Count);
        foreach (var benchmarkCase in manifest.Cases)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Info, CliMessages.ProfileBatchRunningCase(benchmarkCase.Name), !options.NoColor);
            var result = await RunCaseAsync(benchmarkCase, options.Iterations, options.Warmup);
            caseResults.Add(result);

            var statusLevel = result.Success ? DiagnosticLevel.Note : DiagnosticLevel.Error;
            CliOutput.WriteStatus(
                statusLevel,
                result.Success
                    ? CliMessages.ProfileBatchCaseSucceeded(benchmarkCase.Name, result.AverageTotalTimeMs, result.HottestPhaseByTime ?? "n/a")
                    : CliMessages.ProfileBatchCaseFailed(benchmarkCase.Name, result.FailureReason ?? "unknown"),
                !options.NoColor);
        }

        var snapshot = CreateBatchSnapshot(
            manifest,
            manifestPath,
            options.Iterations,
            options.Warmup,
            caseResults);
        var outputText = options.Format == ProfilingTableFormat.Json
            ? JsonSerializer.Serialize(snapshot, CreateJsonOptions())
            : FormatBatchMarkdown(snapshot, options.TopPhases, options.TopSubphases);

        if (!string.IsNullOrWhiteSpace(options.Output))
        {
            var outputPath = Path.GetFullPath(options.Output);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(outputPath, outputText, Encoding.UTF8);
            CliOutput.WriteArtifact(CliMessages.ArtifactKindProfileReport, outputPath, !options.NoColor);
            CliOutput.WriteStatus(DiagnosticLevel.Help, CliMessages.ProfileBatchReportWritten(outputPath), !options.NoColor);
        }
        else
        {
            Console.WriteLine(outputText);
        }

        var success = caseResults.All(result => result.Success);
        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            "profile-batch",
            success,
            commandStopwatch.Elapsed,
            !options.NoColor,
            CliMessages.ProfileBatchFinishedDetail(caseResults.Count(result => result.Success), caseResults.Count));
        return success ? 0 : 1;
    }

    private static async Task<ProfilingBatchManifest> LoadManifestAsync(string manifestPath)
    {
        var json = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<ProfilingBatchManifest>(json, CreateJsonOptions()) ??
               throw new InvalidOperationException(CliMessages.ProfileBatchParseManifestFailed(manifestPath));
        NormalizeManifestPaths(manifest, manifestPath);
        return manifest;
    }

    private static void NormalizeManifestPaths(ProfilingBatchManifest manifest, string manifestPath)
    {
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();
        foreach (var benchmarkCase in manifest.Cases)
        {
            benchmarkCase.Source = NormalizeManifestPath(benchmarkCase.Source, manifestDirectory);
            benchmarkCase.Project = NormalizeOptionalManifestPath(benchmarkCase.Project, manifestDirectory);
            for (var i = 0; i < benchmarkCase.ImportRoot.Length; i++)
            {
                benchmarkCase.ImportRoot[i] = NormalizeManifestPath(benchmarkCase.ImportRoot[i], manifestDirectory);
            }

            for (var i = 0; i < benchmarkCase.ObjectFiles.Length; i++)
            {
                benchmarkCase.ObjectFiles[i] = NormalizeManifestPath(benchmarkCase.ObjectFiles[i], manifestDirectory);
            }

            for (var i = 0; i < benchmarkCase.LibraryPaths.Length; i++)
            {
                benchmarkCase.LibraryPaths[i] = NormalizeManifestPath(benchmarkCase.LibraryPaths[i], manifestDirectory);
            }
        }
    }

    private static string? NormalizeOptionalManifestPath(string? path, string manifestDirectory)
    {
        return string.IsNullOrWhiteSpace(path)
            ? path
            : NormalizeManifestPath(path, manifestDirectory);
    }

    private static string NormalizeManifestPath(string path, string manifestDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(manifestDirectory, path));
    }

    private static async Task<BatchProfilingCaseResult> RunCaseAsync(
        ProfilingBatchCaseManifest benchmarkCase,
        int iterations,
        int warmup)
    {
        ProjectCommandInputResolution inputResolution;
        try
        {
            inputResolution = benchmarkCase.Kind == ProfilingBatchCaseKind.LinkOnly
                ? new ProjectCommandInputResolution(
                    string.IsNullOrWhiteSpace(benchmarkCase.Source)
                        ? Path.Combine(Directory.GetCurrentDirectory(), "link-only.eidos")
                        : benchmarkCase.Source,
                    new ProjectSystem.ProjectImportSearchResolution([], [], [], null, UsesExplicitImportRoots: false),
                    ProjectTarget: null)
                : ProjectCommandInputResolver.Resolve(
                    benchmarkCase.Source,
                    benchmarkCase.Project,
                    benchmarkCase.TargetName,
                    benchmarkCase.ImportRoot);
        }
        catch (Exception ex)
        {
            return BatchProfilingCaseResult.CreateFailure(benchmarkCase.Name, ex.Message);
        }

        var sourcePath = inputResolution.SourceFilePath;
        if (benchmarkCase.Kind != ProfilingBatchCaseKind.LinkOnly && !File.Exists(sourcePath))
        {
            return BatchProfilingCaseResult.CreateFailure(benchmarkCase.Name, CliMessages.SourceFileNotFound(sourcePath));
        }

        var sourceCode = benchmarkCase.Kind == ProfilingBatchCaseKind.LinkOnly ? "" : await File.ReadAllTextAsync(sourcePath);
        var successfulRuns = new List<CompilationProfilingSnapshot>(iterations);
        var peakWorkingSetBytes = 0L;
        var peakPrivateBytes = 0L;

        if (benchmarkCase.ClearObjectCacheBeforeRun)
        {
            LlvmCompiler.ClearObjectCacheForProfile();
        }

        for (var i = 0; i < warmup; i++)
        {
            RunCaseIteration(sourceCode, sourcePath, benchmarkCase, inputResolution);
            SampleProcessMemory(ref peakWorkingSetBytes, ref peakPrivateBytes);
        }

        for (var i = 0; i < iterations; i++)
        {
            var result = RunCaseIteration(sourceCode, sourcePath, benchmarkCase, inputResolution);
            SampleProcessMemory(ref peakWorkingSetBytes, ref peakPrivateBytes);
            if (!result.Result.Success)
            {
                var failure = result.Result.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Level == DiagnosticLevel.Error)?.Message ??
                              CliMessages.ProfileBatchCompileFailed;
                return BatchProfilingCaseResult.CreateFailure(benchmarkCase.Name, failure);
            }

            if (result.CodeGenResult is { Success: false } codeGenResult)
            {
                return BatchProfilingCaseResult.CreateFailure(
                    benchmarkCase.Name,
                    codeGenResult.ErrorMessage ?? CliMessages.ProfileBatchCompileFailed);
            }

            successfulRuns.Add(CreateSnapshot(result.Result, result.CodeGenProfile));
        }

        return BatchProfilingCaseResult.CreateSuccess(
            benchmarkCase.Name,
            sourcePath,
            successfulRuns,
            ResolveStopAtPhase(benchmarkCase)?.ToString() ?? CompilationPhase.Llvm.ToString(),
            peakWorkingSetBytes,
            peakPrivateBytes);
    }

    private static void SampleProcessMemory(ref long peakWorkingSetBytes, ref long peakPrivateBytes)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        peakWorkingSetBytes = Math.Max(
            peakWorkingSetBytes,
            Math.Max(process.WorkingSet64, process.PeakWorkingSet64));
        peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
    }

    private static ProfileBatchIterationResult RunCaseIteration(
        string sourceCode,
        string sourcePath,
        ProfilingBatchCaseManifest benchmarkCase,
        ProjectCommandInputResolution inputResolution)
    {
        if (benchmarkCase.Kind == ProfilingBatchCaseKind.LinkOnly)
        {
            return RunLinkOnlyCase(benchmarkCase, sourcePath);
        }

        var projectConfig = inputResolution.ImportResolution.ProjectFilePath != null
            ? ProjectSystem.EidosProjectConfigurationLoader.TryLoadFromPath(inputResolution.ImportResolution.ProjectFilePath)?.Configuration
            : ProjectSystem.EidosProjectConfigurationLoader.TryLoadNearest(sourcePath)?.Configuration;
        var ffiConfig = inputResolution.ProjectTarget?.Ffi ?? projectConfig?.Ffi;
        var compileOptions = new CompilationOptions
        {
            InputFile = sourcePath,
            LanguageVersion = inputResolution.GetLanguageVersion(),
            EntryFunctionName = inputResolution.ProjectTarget?.TargetName,
            Target = benchmarkCase.Target == CompileTarget.Native
                ? CompilationTarget.LlvmIr
                : CliCompilationPhaseMapper.MapTarget(benchmarkCase.Target),
            StopAtPhase = ResolveStopAtPhase(benchmarkCase),
            DebugLevel = DebugLevel.Normal,
            EnableMirOptimizations = benchmarkCase.MirOpt ?? true,
            EnableDetailedProfiling = true,
            UseColors = false,
            ImportSearchRoots = inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                inputResolution.ImportResolution.EffectiveSearchRoots,
            PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ?? new Dictionary<string, string[]>(StringComparer.Ordinal),
            LlvmTargetTriple = benchmarkCase.TargetTriple,
            NativeLinkMode = ResolveNativeLinkMode(benchmarkCase.NativeLinkMode),
            LlvmOptimizationLevel = ResolveOptimizationLevel(benchmarkCase),
            LlvmEnableLto = benchmarkCase.Lto ?? false,
            ConfigFfiLibraries = ffiConfig?.Libraries ?? [],
            ConfigFfiLibraryPaths = ffiConfig?.LibraryPaths ?? [],
            ConfigFfiIncludePaths = ffiConfig?.IncludePaths ?? [],
            ConfigFfiNativeSources = ffiConfig?.NativeSources ?? [],
            ConfigFfiLinkerFlags = ffiConfig?.LinkerFlags ?? []
        };

        var pipeline = new CompilationPipeline(sourceCode, compileOptions);
        var pipelineResult = pipeline.Run();
        CodeGenResult? codeGenResult = null;
        CodeGenProfile? codeGenProfile = null;
        if (pipelineResult.Success && benchmarkCase.Target == CompileTarget.Native)
        {
            codeGenProfile = new CodeGenProfile();
            var outputPath = ResolveProfileNativeOutputPath(benchmarkCase, sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
            var targetInfo = string.IsNullOrWhiteSpace(benchmarkCase.TargetTriple) ||
                             !TargetInfo.TryParse(benchmarkCase.TargetTriple, out var parsedTarget)
                ? TargetInfo.Default
                : parsedTarget;
            if (benchmarkCase.NativeCpu == true)
            {
                targetInfo = targetInfo.WithNativeCpu();
            }

            var compiler = new LlvmCompiler(
                targetInfo,
                optimizationLevel: ResolveOptimizationLevel(benchmarkCase),
                enableLto: benchmarkCase.Lto ?? false,
                linkMode: compileOptions.NativeLinkMode,
                profile: codeGenProfile);
            codeGenResult = benchmarkCase.CodegenMode == ProfileBatchCodegenMode.ObjectGroups
                ? compiler.CompileToExecutableWithObjectGroups(
                    pipelineResult.LlvmModule!,
                    outputPath,
                    benchmarkCase.MaxObjectGroups)
                : compiler.CompileToExecutable(pipelineResult.LlvmModule!, outputPath);
        }

        return new ProfileBatchIterationResult(pipelineResult, codeGenResult, codeGenProfile);
    }

    private static ProfileBatchIterationResult RunLinkOnlyCase(
        ProfilingBatchCaseManifest benchmarkCase,
        string sourcePath)
    {
        var profile = new CodeGenProfile();
        var targetInfo = string.IsNullOrWhiteSpace(benchmarkCase.TargetTriple) ||
                         !TargetInfo.TryParse(benchmarkCase.TargetTriple, out var parsedTarget)
            ? TargetInfo.Default
            : parsedTarget;
        var compiler = new LlvmCompiler(
            targetInfo,
            optimizationLevel: ResolveOptimizationLevel(benchmarkCase),
            enableLto: benchmarkCase.Lto ?? false,
            linkMode: ResolveNativeLinkMode(benchmarkCase.NativeLinkMode),
            profile: profile);
        var outputPath = ResolveProfileNativeOutputPath(benchmarkCase, sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        var result = compiler.LinkExecutable(
            benchmarkCase.ObjectFiles,
            outputPath,
            benchmarkCase.Libraries,
            benchmarkCase.LibraryPaths,
            benchmarkCase.LinkerFlags);
        var compilationResult = new CompilationResult
        {
            Success = result.Success,
            CompletedPhase = CompilationPhase.Llvm,
            InputFile = sourcePath,
            TotalTime = TimeSpan.Zero
        };
        return new ProfileBatchIterationResult(compilationResult, result, profile);
    }

    private static CompilationProfilingSnapshot CreateSnapshot(CompilationResult result, CodeGenProfile? codeGenProfile)
    {
        var snapshot = CompilationProfilingFormatter.CreateSnapshot(result);
        snapshot.CodeGenEvents.AddRange(codeGenProfile?.Events ?? []);
        return snapshot;
    }

    private static CompilationPhase? ResolveStopAtPhase(ProfilingBatchCaseManifest benchmarkCase)
    {
        if (benchmarkCase.StopAtPhase.HasValue)
        {
            return benchmarkCase.StopAtPhase;
        }

        return benchmarkCase.Target switch
        {
            CompileTarget.Native or CompileTarget.LlvmIr => CompilationPhase.Llvm,
            CompileTarget.Mir => CompilationPhase.Mir,
            CompileTarget.Hir => CompilationPhase.Hir,
            CompileTarget.Typed => CompilationPhase.Types,
            CompileTarget.Resolved => CompilationPhase.Namer,
            CompileTarget.Ast => CompilationPhase.Parser,
            CompileTarget.Tokens => CompilationPhase.Lexer,
            _ => benchmarkCase.StopAtPhase
        };
    }

    private static NativeLinkMode ResolveNativeLinkMode(string? value)
    {
        return value switch
        {
            "platform-default" => NativeLinkMode.PlatformDefault,
            "pie" => NativeLinkMode.PieExecutable,
            _ => NativeLinkMode.NonPieExecutable
        };
    }

    private static int ResolveOptimizationLevel(ProfilingBatchCaseManifest benchmarkCase)
    {
        if (benchmarkCase.OptimizationLevel.HasValue)
        {
            return benchmarkCase.OptimizationLevel.Value;
        }

        return benchmarkCase.BuildMode == BuildMode.Dev ? 0 : 2;
    }

    private static string ResolveProfileNativeOutputPath(ProfilingBatchCaseManifest benchmarkCase, string sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(benchmarkCase.Output))
        {
            return Path.GetFullPath(benchmarkCase.Output);
        }

        var directory = Path.Combine(Path.GetTempPath(), "eidosc-profile-batch-native");
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        return Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}-{Guid.NewGuid():N}{extension}");
    }

    private static ProfilingBatchSnapshot CreateBatchSnapshot(
        ProfilingBatchManifest manifest,
        string manifestPath,
        int iterations,
        int warmup,
        IReadOnlyList<BatchProfilingCaseResult> caseResults)
    {
        return new ProfilingBatchSnapshot
        {
            Name = manifest.Name ?? Path.GetFileNameWithoutExtension(manifestPath),
            ManifestPath = manifestPath,
            Iterations = iterations,
            Warmup = warmup,
            Cases = caseResults
                .Select(result => new ProfilingBatchCaseSnapshot
                {
                    Name = result.Name,
                    InputFile = result.InputFile ?? "",
                    Success = result.Success,
                    CompletedPhase = result.CompletedPhase ?? "",
                    AverageTotalTimeMs = result.AverageTotalTimeMs,
                    HottestPhaseByTime = result.HottestPhaseByTime,
                    HottestSubphaseByTime = result.HottestSubphaseByTime,
                    PeakWorkingSetBytes = result.PeakWorkingSetBytes,
                    PeakPrivateBytes = result.PeakPrivateBytes,
                    FailureReason = result.FailureReason,
                    Phases = result.Phases,
                    Subphases = result.Subphases,
                    SubphaseAggregates = result.SubphaseAggregates,
                    Counters = result.Counters,
                    CodeGenEvents = result.CodeGenEvents
                })
                .ToList()
        };
    }

    private static string FormatBatchMarkdown(
        ProfilingBatchSnapshot snapshot,
        int topPhases,
        int topSubphases)
    {
        var sb = new StringBuilder();
        var successfulCases = snapshot.Cases.Where(@case => @case.Success).ToList();

        sb.AppendLine(CliMessages.ProfileBatchMarkdownTitle);
        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchMarkdownName(snapshot.Name));
        sb.AppendLine(CliMessages.ProfileBatchMarkdownManifest(snapshot.ManifestPath));
        sb.AppendLine(CliMessages.ProfileBatchMarkdownCases(snapshot.Cases.Count));
        sb.AppendLine(CliMessages.ProfileBatchMarkdownSuccessfulCases(successfulCases.Count));
        sb.AppendLine(CliMessages.ProfileBatchMarkdownIterations(snapshot.Iterations));
        sb.AppendLine(CliMessages.ProfileBatchMarkdownWarmup(snapshot.Warmup));
        sb.AppendLine();

        sb.AppendLine(CliMessages.ProfileBatchCaseSummaryHeading);
        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchCaseSummaryHeader);
        sb.AppendLine(CliMessages.ProfileBatchCaseSummarySeparator);
        foreach (var @case in snapshot.Cases.OrderByDescending(@case => @case.AverageTotalTimeMs))
        {
            sb.AppendLine(
                $"| {EscapeMarkdown(@case.Name)} | {(@case.Success ? CliMessages.ProfileBatchStatusOk : CliMessages.ProfileBatchStatusFailed)} | {@case.AverageTotalTimeMs:F2} | {EscapeMarkdown(@case.CompletedPhase)} | {EscapeMarkdown(@case.HottestPhaseByTime ?? "")} | {EscapeMarkdown(@case.HottestSubphaseByTime ?? "")} |");
        }

        if (successfulCases.Count > 0)
        {
            AppendPhaseAggregateTables(sb, successfulCases, topPhases);
            AppendSubphaseAggregateTables(sb, successfulCases, topSubphases);
            AppendSubphaseAnalyzerAggregateTables(sb, successfulCases, topSubphases);
            AppendCounterAggregateTable(sb, successfulCases);
            AppendCodeGenEventAggregateTable(sb, successfulCases);
        }

        var failedCases = snapshot.Cases.Where(@case => !@case.Success).ToList();
        if (failedCases.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(CliMessages.ProfileBatchFailuresHeading);
            sb.AppendLine();
            sb.AppendLine(CliMessages.ProfileBatchFailuresHeader);
            sb.AppendLine(CliMessages.ProfileBatchFailuresSeparator);
            foreach (var failedCase in failedCases)
            {
                sb.AppendLine($"| {EscapeMarkdown(failedCase.Name)} | {EscapeMarkdown(failedCase.FailureReason ?? "")} |");
            }
        }

        return sb.ToString();
    }

    private static void AppendPhaseAggregateTables(
        StringBuilder sb,
        IReadOnlyList<ProfilingBatchCaseSnapshot> successfulCases,
        int topPhases)
    {
        var phaseRows = successfulCases
            .SelectMany(@case => @case.Phases.Select(phase => new
            {
                Case = @case.Name,
                phase.Phase,
                phase.ElapsedMs,
                phase.AllocatedBytes
            }))
            .GroupBy(row => row.Phase, StringComparer.Ordinal)
            .Select(group => new BatchAggregatePhaseRow(
                group.Key,
                group.Count(),
                group.Average(row => row.ElapsedMs),
                group.Max(row => row.ElapsedMs),
                (long)group.Average(row => row.AllocatedBytes),
                group.Max(row => row.AllocatedBytes)))
            .ToList();

        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchAggregatePhasesByTimeHeading);
        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchAggregatePhaseTimeHeader);
        sb.AppendLine(CliMessages.ProfileBatchAggregatePhaseSeparator);
        foreach (var row in phaseRows.OrderByDescending(row => row.AverageElapsedMs).Take(Math.Max(1, topPhases)))
        {
            sb.AppendLine(
                $"| {row.Phase} | {row.CaseCount} | {row.AverageElapsedMs:F2} | {row.MaxElapsedMs:F2} | {CliFormatters.FormatBytes(row.AverageAllocatedBytes)} | {CliFormatters.FormatBytes(row.MaxAllocatedBytes)} |");
        }

        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchAggregatePhasesByAllocationHeading);
        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchAggregatePhaseAllocationHeader);
        sb.AppendLine(CliMessages.ProfileBatchAggregatePhaseSeparator);
        foreach (var row in phaseRows.OrderByDescending(row => row.AverageAllocatedBytes).Take(Math.Max(1, topPhases)))
        {
            sb.AppendLine(
                $"| {row.Phase} | {row.CaseCount} | {CliFormatters.FormatBytes(row.AverageAllocatedBytes)} | {CliFormatters.FormatBytes(row.MaxAllocatedBytes)} | {row.AverageElapsedMs:F2} | {row.MaxElapsedMs:F2} |");
        }
    }

    private static void AppendSubphaseAggregateTables(
        StringBuilder sb,
        IReadOnlyList<ProfilingBatchCaseSnapshot> successfulCases,
        int topSubphases)
    {
        var subphaseRows = successfulCases
            .SelectMany(@case => @case.Subphases.Select(subphase => new
            {
                Key = $"{subphase.Phase}.{subphase.Name}",
                subphase.Phase,
                subphase.Name,
                subphase.ElapsedMs,
                subphase.AllocatedBytes
            }))
            .GroupBy(row => row.Key, StringComparer.Ordinal)
            .Select(group => new BatchAggregateSubphaseRow(
                group.First().Phase,
                group.First().Name,
                group.Count(),
                group.Average(row => row.ElapsedMs),
                group.Max(row => row.ElapsedMs),
                (long)group.Average(row => row.AllocatedBytes),
                group.Max(row => row.AllocatedBytes)))
            .ToList();

        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchAggregateSubphasesByTimeHeading);
        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchAggregateSubphaseTimeHeader);
        sb.AppendLine(CliMessages.ProfileBatchAggregateSubphaseSeparator);
        foreach (var row in subphaseRows.OrderByDescending(row => row.AverageElapsedMs).Take(Math.Max(1, topSubphases)))
        {
            sb.AppendLine(
                $"| {row.Phase} | {EscapeMarkdown(row.Name)} | {row.CaseCount} | {row.AverageElapsedMs:F2} | {row.MaxElapsedMs:F2} | {CliFormatters.FormatBytes(row.AverageAllocatedBytes)} | {CliFormatters.FormatBytes(row.MaxAllocatedBytes)} |");
        }

        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchAggregateSubphasesByAllocationHeading);
        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchAggregateSubphaseAllocationHeader);
        sb.AppendLine(CliMessages.ProfileBatchAggregateSubphaseSeparator);
        foreach (var row in subphaseRows.OrderByDescending(row => row.AverageAllocatedBytes).Take(Math.Max(1, topSubphases)))
        {
            sb.AppendLine(
                $"| {row.Phase} | {EscapeMarkdown(row.Name)} | {row.CaseCount} | {CliFormatters.FormatBytes(row.AverageAllocatedBytes)} | {CliFormatters.FormatBytes(row.MaxAllocatedBytes)} | {row.AverageElapsedMs:F2} | {row.MaxElapsedMs:F2} |");
        }
    }

    private static void AppendSubphaseAnalyzerAggregateTables(
        StringBuilder sb,
        IReadOnlyList<ProfilingBatchCaseSnapshot> successfulCases,
        int topSubphases)
    {
        var rows = successfulCases
            .SelectMany(@case => @case.SubphaseAggregates.Select(aggregate => new
            {
                aggregate.Phase,
                aggregate.Name,
                aggregate.Records,
                aggregate.ElapsedMs,
                aggregate.AllocatedBytes
            }))
            .GroupBy(row => $"{row.Phase}.{row.Name}", StringComparer.Ordinal)
            .Select(group => new BatchAggregateSubphaseRow(
                group.First().Phase,
                group.First().Name,
                group.Sum(row => row.Records),
                group.Average(row => row.ElapsedMs),
                group.Max(row => row.ElapsedMs),
                (long)group.Average(row => row.AllocatedBytes),
                group.Max(row => row.AllocatedBytes)))
            .Where(row => row.CaseCount > 1)
            .OrderByDescending(row => row.AverageElapsedMs)
            .Take(Math.Max(1, topSubphases))
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## Aggregate analyzer groups by time");
        sb.AppendLine();
        sb.AppendLine(CliMessages.ProfileBatchAggregateSubphaseTimeHeader);
        sb.AppendLine(CliMessages.ProfileBatchAggregateSubphaseSeparator);
        foreach (var row in rows)
        {
            sb.AppendLine(
                $"| {row.Phase} | {EscapeMarkdown(row.Name)} | {row.CaseCount} | {row.AverageElapsedMs:F2} | {row.MaxElapsedMs:F2} | {CliFormatters.FormatBytes(row.AverageAllocatedBytes)} | {CliFormatters.FormatBytes(row.MaxAllocatedBytes)} |");
        }
    }

    private static void AppendCounterAggregateTable(
        StringBuilder sb,
        IReadOnlyList<ProfilingBatchCaseSnapshot> successfulCases)
    {
        var rows = successfulCases
            .SelectMany(@case => @case.Counters.Select(counter => new
            {
                counter.Name,
                counter.Value
            }))
            .GroupBy(row => row.Name, StringComparer.Ordinal)
            .Select(group => new BatchAggregateCounterRow(
                group.Key,
                group.Count(),
                group.Average(row => row.Value),
                group.Max(row => row.Value)))
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## Aggregate counters");
        sb.AppendLine();
        sb.AppendLine("| Counter | Cases | Avg | Max |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            sb.AppendLine($"| {EscapeMarkdown(row.Name)} | {row.CaseCount} | {row.AverageValue:F2} | {row.MaxValue} |");
        }
    }

    private static void AppendCodeGenEventAggregateTable(
        StringBuilder sb,
        IReadOnlyList<ProfilingBatchCaseSnapshot> successfulCases)
    {
        var rows = successfulCases
            .SelectMany(@case => @case.CodeGenEvents.Select(profileEvent => new
            {
                profileEvent.Category,
                profileEvent.Name,
                Tool = profileEvent.Tool ?? "",
                profileEvent.ElapsedMs,
                profileEvent.Success,
                profileEvent.CacheHit
            }))
            .GroupBy(row => $"{row.Category}.{row.Name}.{row.Tool}.{row.CacheHit}", StringComparer.Ordinal)
            .Select(group => new
            {
                group.First().Category,
                group.First().Name,
                group.First().Tool,
                group.First().CacheHit,
                Count = group.Count(),
                AverageElapsedMs = group.Average(row => row.ElapsedMs),
                MaxElapsedMs = group.Max(row => row.ElapsedMs),
                Success = group.All(row => row.Success)
            })
            .OrderByDescending(row => row.AverageElapsedMs)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## Aggregate codegen/external tool events");
        sb.AppendLine();
        sb.AppendLine("| Category | Name | Tool | Count | Avg ms | Max ms | Cache | Success |");
        sb.AppendLine("| --- | --- | --- | ---: | ---: | ---: | --- | --- |");
        foreach (var row in rows)
        {
            sb.AppendLine(
                $"| {EscapeMarkdown(row.Category)} | {EscapeMarkdown(row.Name)} | {EscapeMarkdown(row.Tool)} | {row.Count} | {row.AverageElapsedMs:F2} | {row.MaxElapsedMs:F2} | {(row.CacheHit ? "hit" : "miss")} | {row.Success} |");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string EscapeMarkdown(string value)
    {
        return string.IsNullOrEmpty(value)
            ? ""
            : value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private sealed class ProfilingBatchManifest
    {
        public string? Name { get; set; }
        public List<ProfilingBatchCaseManifest> Cases { get; set; } = [];
    }

    private sealed class ProfilingBatchCaseManifest
    {
        public string Name { get; set; } = "";
        public ProfilingBatchCaseKind Kind { get; set; } = ProfilingBatchCaseKind.Compile;
        public string Source { get; set; } = "";
        public string? Project { get; set; }
        public string? TargetName { get; set; }
        public string[] ImportRoot { get; set; } = [];
        public bool? MirOpt { get; set; }
        public CompilationPhase? StopAtPhase { get; set; }
        public CompileTarget Target { get; set; } = CompileTarget.Typed;
        public string? Output { get; set; }
        public string? TargetTriple { get; set; }
        public int? OptimizationLevel { get; set; }
        public bool? Lto { get; set; }
        public bool? NativeCpu { get; set; }
        public string? NativeLinkMode { get; set; }
        public ProfileBatchCodegenMode CodegenMode { get; set; } = ProfileBatchCodegenMode.FullModule;
        public int MaxObjectGroups { get; set; }
        public bool ClearObjectCacheBeforeRun { get; set; }
        public BuildMode BuildMode { get; set; } = BuildMode.Release;
        public string[] ObjectFiles { get; set; } = [];
        public string[] Libraries { get; set; } = [];
        public string[] LibraryPaths { get; set; } = [];
        public string[] LinkerFlags { get; set; } = [];
    }

    private enum ProfilingBatchCaseKind
    {
        Compile,
        LinkOnly
    }

    private enum ProfileBatchCodegenMode
    {
        FullModule,
        ObjectGroups
    }

    private sealed class ProfilingBatchSnapshot
    {
        public string SchemaVersion { get; init; } = "1";
        public string Name { get; init; } = "";
        public string ManifestPath { get; init; } = "";
        public int Iterations { get; init; }
        public int Warmup { get; init; }
        public IReadOnlyList<ProfilingBatchCaseSnapshot> Cases { get; init; } = [];
    }

    private sealed class ProfilingBatchCaseSnapshot
    {
        public string Name { get; init; } = "";
        public string InputFile { get; init; } = "";
        public bool Success { get; init; }
        public string CompletedPhase { get; init; } = "";
        public double AverageTotalTimeMs { get; init; }
        public string? HottestPhaseByTime { get; init; }
        public string? HottestSubphaseByTime { get; init; }
        public long PeakWorkingSetBytes { get; init; }
        public long PeakPrivateBytes { get; init; }
        public string? FailureReason { get; init; }
        public IReadOnlyList<CompilationProfilingPhaseSnapshot> Phases { get; init; } = [];
        public IReadOnlyList<CompilationProfilingSubphaseSnapshot> Subphases { get; init; } = [];
        public IReadOnlyList<CompilationProfilingSubphaseAggregateSnapshot> SubphaseAggregates { get; init; } = [];
        public IReadOnlyList<CompilationProfilingCounterSnapshot> Counters { get; init; } = [];
        public IReadOnlyList<CodeGenProfileEvent> CodeGenEvents { get; init; } = [];
    }

    private sealed record ProfileBatchIterationResult(
        CompilationResult Result,
        CodeGenResult? CodeGenResult,
        CodeGenProfile? CodeGenProfile);

    private sealed class BatchProfilingCaseResult
    {
        public string Name { get; init; } = "";
        public string? InputFile { get; init; }
        public bool Success { get; init; }
        public string? CompletedPhase { get; init; }
        public double AverageTotalTimeMs { get; init; }
        public string? HottestPhaseByTime { get; init; }
        public string? HottestSubphaseByTime { get; init; }
        public long PeakWorkingSetBytes { get; init; }
        public long PeakPrivateBytes { get; init; }
        public string? FailureReason { get; init; }
        public IReadOnlyList<CompilationProfilingPhaseSnapshot> Phases { get; init; } = [];
        public IReadOnlyList<CompilationProfilingSubphaseSnapshot> Subphases { get; init; } = [];
        public IReadOnlyList<CompilationProfilingSubphaseAggregateSnapshot> SubphaseAggregates { get; init; } = [];
        public IReadOnlyList<CompilationProfilingCounterSnapshot> Counters { get; init; } = [];
        public IReadOnlyList<CodeGenProfileEvent> CodeGenEvents { get; init; } = [];

        public static BatchProfilingCaseResult CreateFailure(string name, string failureReason)
        {
            return new BatchProfilingCaseResult
            {
                Name = name,
                Success = false,
                FailureReason = failureReason
            };
        }

        public static BatchProfilingCaseResult CreateSuccess(
            string name,
            string inputFile,
            IReadOnlyList<CompilationProfilingSnapshot> snapshots,
            string completedPhase,
            long peakWorkingSetBytes,
            long peakPrivateBytes)
        {
            var phases = snapshots
                .SelectMany(snapshot => snapshot.Phases)
                .GroupBy(phase => phase.Phase, StringComparer.Ordinal)
                .Select(group => new CompilationProfilingPhaseSnapshot
                {
                    Phase = group.Key,
                    ElapsedMs = group.Average(phase => phase.ElapsedMs),
                    TotalPercent = group.Average(phase => phase.TotalPercent),
                    AllocatedBytes = (long)group.Average(phase => phase.AllocatedBytes),
                    AllocPercent = group.Average(phase => phase.AllocPercent)
                })
                .OrderByDescending(phase => phase.ElapsedMs)
                .ToList();

            var subphases = snapshots
                .SelectMany(snapshot => snapshot.Subphases)
                .GroupBy(subphase => $"{subphase.Phase}.{subphase.Name}", StringComparer.Ordinal)
                .Select(group => new CompilationProfilingSubphaseSnapshot
                {
                    Phase = group.First().Phase,
                    Name = group.First().Name,
                    ElapsedMs = group.Average(subphase => subphase.ElapsedMs),
                    PhasePercent = group.Average(subphase => subphase.PhasePercent),
                    TotalPercent = group.Average(subphase => subphase.TotalPercent),
                    AllocatedBytes = (long)group.Average(subphase => subphase.AllocatedBytes),
                    AllocPercent = group.Average(subphase => subphase.AllocPercent),
                    ManagedBytesBefore = (long)group.Average(subphase => subphase.ManagedBytesBefore),
                    ManagedBytesAfter = (long)group.Average(subphase => subphase.ManagedBytesAfter),
                    ManagedBytesDelta = (long)group.Average(subphase => subphase.ManagedBytesDelta),
                    Gen0Collections = (int)Math.Round(group.Average(subphase => subphase.Gen0Collections)),
                    Gen1Collections = (int)Math.Round(group.Average(subphase => subphase.Gen1Collections)),
                    Gen2Collections = (int)Math.Round(group.Average(subphase => subphase.Gen2Collections))
                })
                .OrderByDescending(subphase => subphase.ElapsedMs)
                .ToList();

            var subphaseAggregates = snapshots
                .SelectMany(snapshot => snapshot.SubphaseAggregates)
                .GroupBy(aggregate => $"{aggregate.Phase}.{aggregate.Name}", StringComparer.Ordinal)
                .Select(group => new CompilationProfilingSubphaseAggregateSnapshot
                {
                    Phase = group.First().Phase,
                    Name = group.First().Name,
                    Records = (int)Math.Round(group.Average(aggregate => aggregate.Records)),
                    ElapsedMs = group.Average(aggregate => aggregate.ElapsedMs),
                    AllocatedBytes = (long)group.Average(aggregate => aggregate.AllocatedBytes),
                    Gen0Collections = (int)Math.Round(group.Average(aggregate => aggregate.Gen0Collections)),
                    Gen1Collections = (int)Math.Round(group.Average(aggregate => aggregate.Gen1Collections)),
                    Gen2Collections = (int)Math.Round(group.Average(aggregate => aggregate.Gen2Collections))
                })
                .OrderByDescending(aggregate => aggregate.ElapsedMs)
                .ToList();

            var counters = snapshots
                .SelectMany(snapshot => snapshot.Counters)
                .GroupBy(counter => counter.Name, StringComparer.Ordinal)
                .Select(group => new CompilationProfilingCounterSnapshot
                {
                    Name = group.Key,
                    Value = (long)Math.Round(group.Average(counter => counter.Value))
                })
                .OrderBy(counter => counter.Name, StringComparer.Ordinal)
                .ToList();
            var codeGenEvents = snapshots
                .SelectMany(snapshot => snapshot.CodeGenEvents)
                .GroupBy(
                    profileEvent => $"{profileEvent.Category}.{profileEvent.Name}.{profileEvent.Tool ?? ""}.{profileEvent.CacheHit}.{CreateCodeGenMetadataKey(profileEvent.Metadata)}",
                    StringComparer.Ordinal)
                .Select(group => new CodeGenProfileEvent
                {
                    Category = group.First().Category,
                    Name = group.First().Name,
                    Tool = group.First().Tool,
                    ElapsedMs = group.Average(profileEvent => profileEvent.ElapsedMs),
                    Success = group.All(profileEvent => profileEvent.Success),
                    ExitCode = group.Select(profileEvent => profileEvent.ExitCode).FirstOrDefault(exitCode => exitCode.HasValue),
                    CacheHit = group.First().CacheHit,
                    Metadata = new Dictionary<string, string>(group.First().Metadata, StringComparer.Ordinal)
                })
                .OrderByDescending(profileEvent => profileEvent.ElapsedMs)
                .ToList();

            return new BatchProfilingCaseResult
            {
                Name = name,
                InputFile = inputFile,
                Success = true,
                CompletedPhase = completedPhase,
                AverageTotalTimeMs = snapshots.Average(GetEffectiveSnapshotTotalTimeMs),
                HottestPhaseByTime = phases.FirstOrDefault()?.Phase,
                HottestSubphaseByTime = subphases.FirstOrDefault() is { } hottestSubphase
                    ? $"{hottestSubphase.Phase}.{hottestSubphase.Name}"
                    : null,
                PeakWorkingSetBytes = peakWorkingSetBytes,
                PeakPrivateBytes = peakPrivateBytes,
                Phases = phases,
                Subphases = subphases,
                SubphaseAggregates = subphaseAggregates,
                Counters = counters,
                CodeGenEvents = codeGenEvents
            };
        }

    }

    private static string CreateCodeGenMetadataKey(IReadOnlyDictionary<string, string> metadata)
    {
        return string.Join(
            "\u001f",
            metadata
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    internal static double GetEffectiveSnapshotTotalTimeMs(CompilationProfilingSnapshot snapshot)
    {
        if (snapshot.TotalTimeMs > 0)
        {
            return snapshot.TotalTimeMs;
        }

        return snapshot.CodeGenEvents.Count == 0
            ? 0
            : snapshot.CodeGenEvents.Sum(profileEvent => profileEvent.ElapsedMs);
    }

    private readonly record struct BatchAggregatePhaseRow(
        string Phase,
        int CaseCount,
        double AverageElapsedMs,
        double MaxElapsedMs,
        long AverageAllocatedBytes,
        long MaxAllocatedBytes);

    private readonly record struct BatchAggregateSubphaseRow(
        string Phase,
        string Name,
        int CaseCount,
        double AverageElapsedMs,
        double MaxElapsedMs,
        long AverageAllocatedBytes,
        long MaxAllocatedBytes);

    private readonly record struct BatchAggregateCounterRow(
        string Name,
        int CaseCount,
        double AverageValue,
        long MaxValue);
}
