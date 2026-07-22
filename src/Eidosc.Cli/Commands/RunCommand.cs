using Eidosc.ProjectSystem;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.CodeGen;
using Eidosc.Cli.Resources;
using Eidosc.Debug;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

/// <summary>
/// Builds and runs an Eidos executable target.
/// </summary>
public static class RunCommand
{
    public static Command Create()
    {
        var importRootOption = ImportRootOptions.Create();
        var programArgs = new Argument<string[]>("args", () => [], CliMessages.RunArgsArgumentDescription)
        {
            Arity = ArgumentArity.ZeroOrMore
        };

        var command = new Command("run", CliMessages.RunCommandDescription)
        {
            new Argument<string>("source", () => "", CliMessages.SourceArgumentDescription),
            programArgs,
            new Option<string>("--project", CliMessages.ProjectOptionDescription),
            new Option<string>("--target-name", CliMessages.RunTargetNameOptionDescription),
            ProjectCommandSourceInputResolver.CreateSourceTextOption(),
            ProjectCommandSourceInputResolver.CreateStdinOption(),
            new Option<string>(["--output", "-o"], CliMessages.RunOutputOptionDescription),
            new Option<string>("--debug-output", CliMessages.BuildDebugOutputOptionDescription),
            new Option<DebugLevel>("--debug-level", () => DebugLevel.Normal, CliMessages.DebugLevelOptionDescription),
            new Option<DebugGraphFormat>("--debug-graph-format", () => DebugGraphFormat.None, CliMessages.BuildDebugGraphFormatOptionDescription),
            new Option<bool>("--emit-cfg", CliMessages.BuildEmitCfgOptionDescription),
            MirOptimizationOptions.CreateEnableOption(),
            MirOptimizationOptions.CreateDisableOption(),
            new Option<string>("--target-triple", CliMessages.BuildTargetTripleOptionDescription),
            new Option<string[]>("--werror", CliMessages.WerrorOptionDescription),
            new Option<bool>("--werror-all", CliMessages.WerrorAllOptionDescription),
            DenyOptionParser.Create(),
            new Option<int>("-O", () => 2, CliMessages.BuildOptimizationLevelOptionDescription),
            new Option<bool>("--lto", CliMessages.BuildLtoOptionDescription),
            new Option<bool>("--native-cpu", CliMessages.BuildNativeCpuOptionDescription),
            new Option<bool>("--no-color", CliMessages.CliNoColorOptionDescription),
            importRootOption
        };

        command.Handler = CommandHandler.Create<RunOptions>(Execute);
        return command;
    }

    private sealed class RunOptions
    {
        public string Source { get; set; } = "";
        public string[] Args { get; set; } = [];
        public string? Project { get; set; }
        public string? TargetName { get; set; }
        public string? SourceText { get; set; }
        public bool Stdin { get; set; }
        public string? Output { get; set; }
        public string? DebugOutput { get; set; }
        public DebugLevel DebugLevel { get; set; } = DebugLevel.Normal;
        public DebugGraphFormat DebugGraphFormat { get; set; } = DebugGraphFormat.None;
        public bool EmitCfg { get; set; }
        public bool MirOpt { get; set; }
        public bool NoMirOpt { get; set; }
        public string? TargetTriple { get; set; }
        public string[] Werror { get; set; } = [];
        public bool WerrorAll { get; set; }
        public string[] Deny { get; set; } = [];
        public int O { get; set; } = 2;
        public bool Lto { get; set; }
        public bool NativeCpu { get; set; }
        public bool Verbose { get; set; }
        public bool NoColor { get; set; }
        public string[] ImportRoot { get; set; } = [];
    }

    private static async Task<int> Execute(RunOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();

        TargetInfo? targetInfo = null;
        if (!string.IsNullOrWhiteSpace(options.TargetTriple) &&
            !TargetInfo.TryParse(options.TargetTriple, out targetInfo))
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, CliMessages.UnknownTargetPlatform(options.TargetTriple), !options.NoColor);
            CliOutput.WriteStatus(
                DiagnosticLevel.Help,
                CliMessages.SupportedTargetsStatus(string.Join(", ", TargetInfo.GetSupportedTargetStrings())),
                !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("run", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.InvalidTargetTripleDetail);
            return 1;
        }

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
            CliOutput.WriteFinished("run", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.InputResolutionFailedDetail);
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, ex.Message, !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("run", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.SourceFileMissingDetail);
            return 1;
        }

        var inputResolution = sourceInput.InputResolution;

        if (inputResolution.ProjectTarget is { Kind: not "executable" })
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Error,
                CliMessages.RunTargetNotExecutable(inputResolution.ProjectTarget.TargetName),
                !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("run", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.RunTargetNotExecutableDetail);
            return 1;
        }

        var sourceCode = sourceInput.SourceText;
        var effectiveTargetInfo = ApplyNativeCpu(targetInfo ?? TargetInfo.Default, options.NativeCpu);
        var outputPath = ProjectCommandPaths.ResolveNativeOutputPath(
            options.Output,
            inputResolution,
            effectiveTargetInfo);
        var debugOutputPath = !string.IsNullOrWhiteSpace(options.DebugOutput)
            ? ProjectCommandPaths.ResolveDebugOutputPath(options.DebugOutput, inputResolution)
            : null;

        var projectConfig = inputResolution.ImportResolution.ProjectFilePath != null
            ? EidosProjectConfigurationLoader.TryLoadFromPath(inputResolution.ImportResolution.ProjectFilePath)?
                .Configuration
            : EidosProjectConfigurationLoader.TryLoadNearest(inputResolution.SourceFilePath)?
                .Configuration;
        var ffiConfig = inputResolution.ProjectTarget?.Ffi ?? projectConfig?.Ffi;

        var compileOptions = new CompilationOptions
        {
            InputFile = inputResolution.SourceFilePath,
            LanguageVersion = inputResolution.GetLanguageVersion(),
            EntryFunctionName = inputResolution.ProjectTarget?.TargetName,
            Target = CompilationTarget.LlvmIr,
            StopAtPhase = CompilationPhase.Llvm,
            DebugOutputPath = debugOutputPath,
            CleanDebugOutput = !string.IsNullOrWhiteSpace(debugOutputPath),
            DebugLevel = options.DebugLevel,
            DebugGraphFormat = options.DebugGraphFormat,
            EmitCfg = options.EmitCfg,
            EnableMirOptimizations = MirOptimizationOptions.IsEnabled(options.NoMirOpt),
            Verbose = options.Verbose,
            UseColors = !options.NoColor,
            EmitStyleSuggestions = true,
            AllowVirtualInputFile = sourceInput.IsInMemorySource,
            LlvmTargetTriple = effectiveTargetInfo.Triple,
            LlvmOptimizationLevel = options.O,
            LlvmEnableLto = options.Lto,
            TreatWarningsAsErrors = options.WerrorAll,
            DenyStyle = DenyOptionParser.IncludesStyle(options.Deny),
            WarningCodesAsErrors = WarningOptionParser.ParseWarningCodes(options.Werror),
            ImportSearchRoots = inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                inputResolution.ImportResolution.EffectiveSearchRoots,
            PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ?? new Dictionary<string, string[]>(StringComparer.Ordinal),
            ConfigFfiLibraries = ffiConfig?.Libraries ?? [],
            ConfigFfiLibraryPaths = ffiConfig?.LibraryPaths ?? [],
            ConfigFfiIncludePaths = ffiConfig?.IncludePaths ?? [],
            ConfigFfiNativeSources = ffiConfig?.NativeSources ?? [],
            ConfigFfiLinkerFlags = ffiConfig?.LinkerFlags ?? [],
            MetaConfiguration = projectConfig?.Meta
        };

        CliOutput.WriteAction(CliMessages.CompilingAction, CliMessages.RunActionSubject(inputResolution.SourceFilePath), !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Info, CliMessages.RunSourceStatus(inputResolution.SourceFilePath), !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.RunOutputStatus(outputPath), !options.NoColor);
        ProjectImportResolutionCli.WriteSummary(
            inputResolution.ImportResolution,
            inputResolution.ProjectTarget,
            !options.NoColor);
        MirOptimizationOptions.WriteStatus(options.NoMirOpt, !options.NoColor);

        var pipeline = new CompilationPipeline(sourceCode, compileOptions);
        var result = pipeline.Run();
        CliOutput.RenderDiagnostics(result, !options.NoColor);

        if (!result.Success || result.LlvmModule == null)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, CliMessages.RunCompileFailedNoRun, !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("run", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.RunCompileFailedDetail);
            return 1;
        }

        var llvmCompiler = new LlvmCompiler(
            effectiveTargetInfo,
            optimizationLevel: options.O,
            enableLto: options.Lto);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        var codeGenResult = llvmCompiler.CompileToExecutable(result.LlvmModule, outputPath);
        if (!codeGenResult.Success)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Error, CliMessages.CodeGenerationFailed(codeGenResult.ErrorMessage ?? string.Empty), !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished("run", false, commandStopwatch.Elapsed, !options.NoColor, CliMessages.CodegenFailedDetail);
            return 1;
        }

        CliOutput.WriteArtifact(CliMessages.ArtifactKindExecutable, outputPath, !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Help, CliMessages.RunExecutingStatus(outputPath), !options.NoColor);
        CliOutput.WriteAction(CliMessages.RunningAction, FormatRunCommand(outputPath, options.Args), !options.NoColor);
        var runStopwatch = Stopwatch.StartNew();
        var exitCode = RunExecutable(outputPath, options.Args);
        runStopwatch.Stop();
        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            "run",
            exitCode == 0,
            commandStopwatch.Elapsed,
            !options.NoColor,
            CliMessages.RunFinishedDetail(exitCode, runStopwatch.Elapsed.TotalMilliseconds));
        return exitCode;
    }

    private static int RunExecutable(string outputPath, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = outputPath,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return 1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static TargetInfo ApplyNativeCpu(TargetInfo targetInfo, bool nativeCpu)
    {
        return nativeCpu ? targetInfo.WithNativeCpu() : targetInfo;
    }

    private static string FormatRunCommand(string outputPath, IReadOnlyList<string> args)
    {
        return args.Count == 0
            ? outputPath
            : $"{outputPath} {string.Join(" ", args)}";
    }
}
