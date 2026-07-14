using Eidosc.ProjectSystem;
using Eidosc.BuildSystem;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eidosc.Cli.Resources;
using Eidosc.Diagnostic;
using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Eidosc.Debug;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Utils;

namespace Eidosc.Cli.Commands;

/// <summary>
/// 编译命令 - 编译 Eidos 源代码
/// </summary>
public static partial class BuildCommand
{
    private const string LlvmIrBuildArtifactKind = "llvm-ir-full-build";
    private const string NativeBuildArtifactKind = "native-full-build";
    private const string ModuleSignatureSnapshotArtifactKind = "module-signature-snapshot";
    private const string ModuleSemanticSignatureSnapshotArtifactKind = "module-semantic-signature-snapshot";
    internal const string ModuleTypedSemanticSignatureSnapshotArtifactKind = "module-typed-semantic-signature-snapshot";
    internal const string ModuleMirArtifactSnapshotArtifactKind = "module-mir-artifact-snapshot";
    internal const string ModuleArtifactRestorePlanSnapshotArtifactKind = "module-artifact-restore-plan-snapshot";
    internal const string ModuleMemberIndexSnapshotArtifactKind = "module-member-index-snapshot";
    internal const string ModuleSemanticSignatureArtifactKind = ProjectModuleArtifactKinds.SemanticSignature;
    internal const string ModuleNamerStatePayloadArtifactKind = ProjectModuleArtifactKinds.NamerStatePayload;
    internal const string ModuleTypesStatePayloadArtifactKind = ProjectModuleArtifactKinds.TypesStatePayload;
    internal const string ModuleHirStatePayloadArtifactKind = ProjectModuleArtifactKinds.HirStatePayload;
    internal const string ModuleMirStatePayloadArtifactKind = ProjectModuleArtifactKinds.MirStatePayload;
    internal const string ModuleTypedSemanticSignatureArtifactKind = ProjectModuleArtifactKinds.TypedSemanticSignature;
    internal const string ModuleMirArtifactKind = ProjectModuleArtifactKinds.MirArtifact;
    internal const string MirFunctionFingerprintSnapshotArtifactKind = "mir-function-fingerprint-snapshot";
    internal const string LlvmFunctionFingerprintSnapshotArtifactKind = "llvm-function-fingerprint-snapshot";
    internal const string LlvmFunctionFragmentSnapshotArtifactKind = "llvm-function-fragment-snapshot";
    internal const string LlvmModuleEnvelopeSnapshotArtifactKind = "llvm-module-envelope-snapshot";
    internal const string LlvmCodegenUnitPlanSnapshotArtifactKind = "llvm-codegen-unit-plan-snapshot";
    private const string LatestModuleSemanticSignatureSnapshotArtifactKind = "module-semantic-signature-latest";
    internal const string LatestModuleMemberIndexSnapshotArtifactKind = "module-member-index-latest";
    internal const string LatestModuleMemberIndexRestorePlanSnapshotArtifactKind = "module-member-index-restore-plan-latest";
    internal const string LatestModuleNamerStatePayloadsArtifactKind = "module-namer-state-payloads-latest";
    internal const string LatestModuleTypesStatePayloadsArtifactKind = "module-types-state-payloads-latest";
    internal const string LatestModuleHirStatePayloadsArtifactKind = "module-hir-state-payloads-latest";
    internal const string LatestModuleMirStatePayloadsArtifactKind = "module-mir-state-payloads-latest";
    internal const string LatestImplOverlapCheckSnapshotArtifactKind = "impl-overlap-check-latest";
    internal const string LatestModuleDependencySignatureSnapshotArtifactKind = "module-dependency-signature-latest";
    private const string LatestModuleTypedSemanticSignatureSnapshotArtifactKind = "module-typed-semantic-signature-latest";
    private const string LatestModuleMirArtifactSnapshotArtifactKind = "module-mir-artifact-latest";
    private const string LatestModuleArtifactRestorePlanSnapshotArtifactKind = "module-artifact-restore-plan-latest";
    private const string LatestModuleTypedArtifactRestorePlanSnapshotArtifactKind = "module-typed-artifact-restore-plan-latest";
    private const string LatestMirFunctionFingerprintSnapshotArtifactKind = "mir-function-fingerprint-latest";
    internal const string LatestSendAnalysisSnapshotArtifactKind = "send-analysis-latest";
    internal const string LatestBorrowDiagnosticSnapshotArtifactKind = "borrow-diagnostic-latest";
    internal const string LatestBorrowCodegenHintsSnapshotArtifactKind = "borrow-codegen-hints-latest";
    private const string LatestLlvmFunctionFingerprintSnapshotArtifactKind = "llvm-function-fingerprint-latest";
    private const string LatestLlvmFunctionFragmentSnapshotArtifactKind = "llvm-function-fragment-latest";
    internal const string LatestTypeDirectedCallableResolutionSnapshotArtifactKind = "type-directed-callable-resolution-latest";
    internal const string LatestAssociatedTypeProjectionSnapshotArtifactKind = "associated-type-projection-latest";
    internal const string LatestAssociatedConstProjectionSnapshotArtifactKind = "associated-const-projection-latest";
    internal const string LatestTraitCheckSnapshotArtifactKind = "trait-check-latest";

    /// <summary>build 子命令的选项引用，供 HelpCustomization 使用。</summary>
    private static Option<CompileTarget>? _targetOption;
    private static Option<string>? _outputOption;
    private static Option<CompilePhase?>? _phaseOption;
    private static Option<bool>? _emitLlvmOption;
    private static Option<string>? _nativeLinkModeOption;
    private static Option<string>? _codegenModeOption;
    private static Option<int>? _jobsOption;

    public static Command Create()
    {
        var importRootOption = ImportRootOptions.Create();

        _targetOption = new Option<CompileTarget>(
            ["--target", "-t"],
            () => CompileTarget.Native,
            CliMessages.BuildTargetOptionDescription);
        _outputOption = new Option<string>(["--output", "-o"], CliMessages.BuildOutputOptionDescription);
        _phaseOption = new Option<CompilePhase?>("--phase", CliMessages.BuildPhaseOptionDescription);
        _emitLlvmOption = new Option<bool>("--emit-llvm", CliMessages.BuildEmitLlvmOptionDescription);
        _nativeLinkModeOption = new Option<string>(
            "--native-link-mode",
            CliMessages.BuildNativeLinkModeOptionDescription);
        _codegenModeOption = new Option<string>(
            "--codegen-mode",
            () => NativeCodegenModes.Auto,
            "Select native LLVM codegen mode: auto, full-module, or object-groups.");
        _jobsOption = new Option<int>(
            ["--jobs", "-j"],
            () => Math.Max(1, Environment.ProcessorCount),
            "Maximum parallel compilation jobs.");
        _jobsOption.AddValidator(result =>
        {
            if (result.GetValueOrDefault<int>() <= 0)
            {
                result.ErrorMessage = "--jobs must be greater than zero.";
            }
        });
        var cacheMaxMiBOption = new Option<int>(
            "--cache-max-mib",
            () => 512,
            "Maximum project cache size in MiB; 0 disables automatic pruning.");
        cacheMaxMiBOption.AddValidator(result =>
        {
            if (result.GetValueOrDefault<int>() < 0)
            {
                result.ErrorMessage = "--cache-max-mib cannot be negative.";
            }
        });
        _codegenModeOption.AddValidator(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!IsSupportedNativeCodegenMode(value))
            {
                result.ErrorMessage = $"Invalid codegen mode '{value}'. Supported modes: auto, full-module, object-groups.";
            }
        });
        _nativeLinkModeOption.AddValidator(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (value is not "platform-default" and not "no-pie" and not "pie")
            {
                result.ErrorMessage = CliMessages.InvalidNativeLinkMode(value ?? string.Empty);
            }
        });

        var command = new Command("build", CliMessages.BuildCommandDescription)
        {
            // 参数
            new Argument<string>("source", () => "", CliMessages.SourceArgumentDescription),

            // 选项
            new Option<string>("--project", CliMessages.ProjectOptionDescription),
            new Option<string>("--target-name", CliMessages.BuildTargetNameOptionDescription),
            ProjectCommandSourceInputResolver.CreateSourceTextOption(),
            ProjectCommandSourceInputResolver.CreateStdinOption(),
            _outputOption,
            _targetOption,
            _phaseOption,
            new Option<string>("--debug-output", CliMessages.BuildDebugOutputOptionDescription),
            new Option<DebugLevel>("--debug-level", () => DebugLevel.Normal, CliMessages.DebugLevelOptionDescription),
            new Option<DebugGraphFormat>(
                "--debug-graph-format",
                () => DebugGraphFormat.None,
                CliMessages.BuildDebugGraphFormatOptionDescription),
            new Option<bool>("--no-color", CliMessages.CliNoColorOptionDescription),
            new Option<bool>("--emit-cfg", CliMessages.BuildEmitCfgOptionDescription),
            MirOptimizationOptions.CreateEnableOption(),
            MirOptimizationOptions.CreateDisableOption(),
            new Option<string>("--target-triple", CliMessages.BuildTargetTripleOptionDescription),
            _emitLlvmOption,
            new Option<string[]>("--werror", CliMessages.WerrorOptionDescription),
            new Option<bool>("--werror-all", CliMessages.WerrorAllOptionDescription),
            new Option<int?>("-O", CliMessages.BuildOptimizationLevelOptionDescription),
            new Option<bool>("--lto", CliMessages.BuildLtoOptionDescription),
            new Option<BuildMode>("--build-mode", () => BuildMode.Release, "Select build defaults: Dev favors compile speed; Release favors optimized output."),
            new Option<bool>("--native-cpu", CliMessages.BuildNativeCpuOptionDescription),
            new Option<string>("--profile-json", "Write build profiling data as JSON to the specified path."),
            new Option<bool>(
                "--profile-module-artifacts-only",
                "Disable exact full-build/backend artifact restore so profile-json observes module artifact gates."),
            new Option<bool>("--no-cache", "Disable all persistent build cache reads and writes."),
            new Option<string>("--emit-build-graph", "Write the canonical capability-constrained BuildGraph as JSON."),
            new Option<bool>("--trace-build", "Trace Build host capabilities, dependencies, graph execution, and cache decisions."),
            cacheMaxMiBOption,
            _jobsOption,
            _codegenModeOption,
            new Option<int>("--max-object-groups", () => 0, "Maximum LLVM object groups for --codegen-mode object-groups; 0 keeps the natural group count."),
            _nativeLinkModeOption,
            importRootOption
        };

        command.Handler = CommandHandler.Create<BuildOptions>(Execute);

        return command;
    }

    /// <summary>
    /// 对给定的 HelpBuilder 应用 build 命令选项级别的描述定制。
    /// 在 HelpCustomization.Apply 中调用。
    /// </summary>
    internal static void CustomizeHelp(HelpBuilder builder)
    {
        if (_targetOption is not null)
        {
            builder.CustomizeSymbol(_targetOption, secondColumnText: CliMessages.BuildTargetOptionHelp);
        }

        if (_outputOption is not null)
        {
            builder.CustomizeSymbol(_outputOption, secondColumnText: CliMessages.BuildOutputOptionHelp);
        }

        if (_phaseOption is not null)
        {
            builder.CustomizeSymbol(_phaseOption, secondColumnText: CliMessages.BuildPhaseOptionHelp);
        }

        if (_emitLlvmOption is not null)
        {
            builder.CustomizeSymbol(_emitLlvmOption, secondColumnText: CliMessages.BuildEmitLlvmOptionHelp);
        }

        if (_nativeLinkModeOption is not null)
        {
            builder.CustomizeSymbol(_nativeLinkModeOption, secondColumnText: CliMessages.BuildNativeLinkModeOptionHelp);
        }

        if (_codegenModeOption is not null)
        {
            builder.CustomizeSymbol(_codegenModeOption, secondColumnText: "auto uses object-groups in dev mode and full-module in release mode.");
        }

        if (_jobsOption is not null)
        {
            builder.CustomizeSymbol(_jobsOption, secondColumnText: "Defaults to the number of logical processors.");
        }
    }

    internal sealed class BuildOptions
    {
        public string Source { get; set; } = "";
        public string? Project { get; set; }
        public string? TargetName { get; set; }
        public string? SourceText { get; set; }
        public bool Stdin { get; set; }
        public string? Output { get; set; }
        public CompileTarget Target { get; set; } = CompileTarget.Native;
        public CompilePhase? Phase { get; set; }
        public string? DebugOutput { get; set; }
        public DebugLevel DebugLevel { get; set; } = DebugLevel.Normal;
        public DebugGraphFormat DebugGraphFormat { get; set; } = DebugGraphFormat.None;
        public bool EmitCfg { get; set; }
        public bool MirOpt { get; set; }
        public bool NoMirOpt { get; set; }
        public bool Verbose { get; set; }
        public bool NoColor { get; set; }
        public bool NoImplicitPrelude { get; set; }
        public string? TargetTriple { get; set; }
        public bool EmitLlvm { get; set; }
        public string[] Werror { get; set; } = [];
        public bool WerrorAll { get; set; }
        public int? O { get; set; }
        public bool Lto { get; set; }
        public BuildMode BuildMode { get; set; } = BuildMode.Release;
        public bool NativeCpu { get; set; }
        public string? ProfileJson { get; set; }
        public bool ProfileModuleArtifactsOnly { get; set; }
        public bool NoCache { get; set; }
        public string? EmitBuildGraph { get; set; }
        public bool TraceBuild { get; set; }
        public int CacheMaxMib { get; set; } = 512;
        public int Jobs { get; set; } = Math.Max(1, Environment.ProcessorCount);
        public string CodegenMode { get; set; } = NativeCodegenModes.Auto;
        public int MaxObjectGroups { get; set; }
        public string? NativeLinkMode { get; set; }
        public string[] ImportRoot { get; set; } = [];
    }

    private static async Task<int> Execute(BuildOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();

        // 如果指定了 --emit-llvm，自动设置目标为 LlvmIr
        if (options.EmitLlvm && options.Target == CompileTarget.Native)
        {
            options.Target = CompileTarget.LlvmIr;
        }

        var optimizationLevel = ResolveOptimizationLevel(options.BuildMode, options.O);
        var requestedCodegenMode = options.CodegenMode;
        options.MaxObjectGroups = ResolveMaxObjectGroups(options.BuildMode, requestedCodegenMode, options.MaxObjectGroups);
        options.CodegenMode = ResolveNativeCodegenMode(options.BuildMode, requestedCodegenMode);

        // 验证通过 CLI 显式传入的 --native-link-mode
        if (!string.IsNullOrWhiteSpace(options.NativeLinkMode) &&
            !TryParseNativeLinkModeOption(options.NativeLinkMode, out _))
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Error,
                CliMessages.InvalidNativeLinkMode(options.NativeLinkMode),
                !options.NoColor);
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "build",
                false,
                commandStopwatch.Elapsed,
                !options.NoColor,
                CliMessages.InvalidNativeLinkModeDetail);
            return 1;
        }

        TargetInfo? targetInfo = null;
        if (!string.IsNullOrWhiteSpace(options.TargetTriple))
        {
            if (!TargetInfo.TryParse(options.TargetTriple, out targetInfo))
            {
                CliOutput.WriteStatus(
                    DiagnosticLevel.Error,
                    CliMessages.UnknownTargetPlatform(options.TargetTriple),
                    !options.NoColor);
                CliOutput.WriteStatus(
                    DiagnosticLevel.Help,
                    CliMessages.SupportedTargetsStatus(string.Join(", ", TargetInfo.GetSupportedTargetStrings())),
                    !options.NoColor);
                commandStopwatch.Stop();
                CliOutput.WriteFinished(
                    "build",
                    false,
                    commandStopwatch.Elapsed,
                    !options.NoColor,
                    CliMessages.InvalidTargetTripleDetail);
                return 1;
            }
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
            CliOutput.WriteFinished(
                "build",
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
                "build",
                false,
                commandStopwatch.Elapsed,
                !options.NoColor,
                CliMessages.SourceFileMissingDetail);
            return 1;
        }

        var inputResolution = sourceInput.InputResolution;
        var sourcePath = sourceInput.SourceFilePath;
        var sourceCode = sourceInput.SourceText;
        var targetStopPhase = CliCompilationPhaseMapper.MapTargetToStopPhase(options.Target);
        var explicitStopPhase = CliCompilationPhaseMapper.MapPhase(options.Phase);
        var effectiveStopPhase = explicitStopPhase ?? targetStopPhase;
        var debugOutputPath = !string.IsNullOrWhiteSpace(options.DebugOutput)
            ? ProjectCommandPaths.ResolveDebugOutputPath(options.DebugOutput, inputResolution)
            : null;

        var projectConfig = inputResolution.ImportResolution.ProjectFilePath != null
            ? EidosProjectConfigurationLoader.TryLoadFromPath(inputResolution.ImportResolution.ProjectFilePath)?
                .Configuration
            : EidosProjectConfigurationLoader.TryLoadNearest(sourcePath)?
                .Configuration;
        var ffiConfig = inputResolution.ProjectTarget?.Ffi ?? projectConfig?.Ffi;

        EidosBuildHostResult? buildHostResult = null;
        if (projectConfig?.Build != null)
        {
            var projectDirectory = ProjectCommandPaths.ResolveProjectDirectory(inputResolution)
                ?? Path.GetDirectoryName(inputResolution.ImportResolution.ProjectFilePath!)!;
            buildHostResult = await EidosBuildHost.RunAsync(new EidosBuildHostOptions
            {
                ProjectDirectory = projectDirectory,
                Configuration = projectConfig.Build,
                LanguageVersion = inputResolution.GetLanguageVersion(),
                TargetName = inputResolution.ProjectTarget?.TargetName ?? options.TargetName ?? "main",
                TargetTriple = (targetInfo ?? TargetInfo.Default).Triple,
                ImportSearchRoots = inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                    inputResolution.ImportResolution.EffectiveSearchRoots,
                PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ??
                                     new Dictionary<string, string[]>(StringComparer.Ordinal),
                NoImplicitPrelude = projectConfig.NoImplicitStdlib,
                UseCache = !options.NoCache,
                TraceBuild = options.TraceBuild
            });

            if (buildHostResult.Graph != null && !string.IsNullOrWhiteSpace(options.EmitBuildGraph))
            {
                var graphPath = Path.GetFullPath(options.EmitBuildGraph);
                Directory.CreateDirectory(Path.GetDirectoryName(graphPath) ?? Directory.GetCurrentDirectory());
                await File.WriteAllTextAsync(graphPath, buildHostResult.Graph.ToCanonicalJson());
                CliOutput.WriteArtifact("BuildGraph", graphPath, !options.NoColor);
            }

            if (options.TraceBuild)
            {
                RenderBuildTrace(buildHostResult, !options.NoColor);
            }

            if (!buildHostResult.Success)
            {
                var buildProgramSource = await TryReadBuildProgramSourceAsync(buildHostResult.ProgramPath);
                CliOutput.RenderDiagnostics(
                    buildHostResult.Diagnostics,
                    buildProgramSource,
                    buildHostResult.ProgramPath,
                    !options.NoColor);
                commandStopwatch.Stop();
                CliOutput.WriteFinished(
                    "build",
                    false,
                    commandStopwatch.Elapsed,
                    !options.NoColor,
                    "Build host failed");
                return 1;
            }
        }

        // 解析 effective native link mode：CLI 显式传入 > 项目配置 > 默认 no-pie
        var effectiveNativeLinkMode = ResolveNativeLinkMode(
            options.NativeLinkMode,
            projectConfig?.NativeLinkMode);

        var compileOptions = new CompilationOptions
        {
            InputFile = sourcePath,
            LanguageVersion = inputResolution.GetLanguageVersion(),
            EntryFunctionName = inputResolution.ProjectTarget?.TargetName,
            Target = CliCompilationPhaseMapper.MapTarget(options.Target),
            StopAtPhase = effectiveStopPhase,
            DebugOutputPath = debugOutputPath,
            CleanDebugOutput = !string.IsNullOrWhiteSpace(debugOutputPath),
            DebugLevel = options.DebugLevel,
            DebugGraphFormat = options.DebugGraphFormat,
            EmitCfg = options.EmitCfg,
            EnableMirOptimizations = MirOptimizationOptions.IsEnabled(options.NoMirOpt),
            NoImplicitPrelude = options.NoImplicitPrelude,
            EnableDetailedProfiling = !string.IsNullOrWhiteSpace(options.ProfileJson),
            EnableIncrementalCompilation = !options.NoCache,
            MaxDegreeOfParallelism = options.Jobs,
            Verbose = options.Verbose,
            UseColors = !options.NoColor,
            EmitStyleSuggestions = true,
            AllowVirtualInputFile = sourceInput.IsInMemorySource,
            LlvmTargetTriple = targetInfo?.Triple ?? options.TargetTriple,
            BuildHostFingerprint = buildHostResult?.CacheFingerprint,
            BuildGraphFingerprint = buildHostResult?.Graph?.CanonicalHash,
            NativeLinkMode = effectiveNativeLinkMode,
            LlvmOptimizationLevel = optimizationLevel,
            LlvmEnableLto = options.Lto,
            AllowLlvmIrTextRestore = options.Target == CompileTarget.LlvmIr ||
                                     (options.Target == CompileTarget.Native &&
                                      !IsObjectGroupsCodegenMode(options.CodegenMode)),
            AllowNativeObjectGroupRestore = options.Target == CompileTarget.Native && IsObjectGroupsCodegenMode(options.CodegenMode),
            TreatWarningsAsErrors = options.WerrorAll,
            WarningCodesAsErrors = WarningOptionParser.ParseWarningCodes(options.Werror),
            ImportSearchRoots = (inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                 inputResolution.ImportResolution.EffectiveSearchRoots)
                .Concat(buildHostResult?.GeneratedSourceRoots ?? [])
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToArray(),
            PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ?? new Dictionary<string, string[]>(StringComparer.Ordinal),
            ConfigFfiLibraries = ffiConfig?.Libraries ?? [],
            ConfigFfiLibraryPaths = ffiConfig?.LibraryPaths ?? [],
            ConfigFfiIncludePaths = ffiConfig?.IncludePaths ?? [],
            ConfigFfiNativeSources = ffiConfig?.NativeSources ?? [],
            ConfigFfiLinkerFlags = ffiConfig?.LinkerFlags ?? []
        };

        CliOutput.WriteAction(
            CliMessages.CompilingAction,
            CliMessages.BuildActionSubject(sourcePath, options.Target),
            !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Info, CliMessages.CompileSourceStatus(sourcePath), !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.TargetStatus(options.Target), !options.NoColor);

        if (effectiveStopPhase.HasValue)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.StopPhaseStatus(effectiveStopPhase.Value),
                !options.NoColor);
        }

        if (!string.IsNullOrEmpty(options.TargetTriple))
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.TargetPlatformStatus(options.TargetTriple),
                !options.NoColor);
        }

        if (options.NativeCpu)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.NativeCpuTuningStatus, !options.NoColor);
        }

        if (options.Lto)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.LtoEnabledStatus, !options.NoColor);
        }

        if (optimizationLevel != 2)
        {
            CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.OptimizationLevelStatus(optimizationLevel), !options.NoColor);
        }

        ProjectImportResolutionCli.WriteSummary(
            inputResolution.ImportResolution,
            inputResolution.ProjectTarget,
            !options.NoColor);

        MirOptimizationOptions.WriteStatus(options.NoMirOpt, !options.NoColor);

        if (!string.IsNullOrWhiteSpace(debugOutputPath))
        {
            CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.DebugOutputStatus(debugOutputPath), !options.NoColor);
        }

        if (options.DebugGraphFormat != DebugGraphFormat.None)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.DebugGraphArtifactsStatus(options.DebugGraphFormat),
                !options.NoColor);
        }

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

        var buildProfile = new CodeGenProfile();
        var fullBuildArtifact = options.NoCache
            ? null
            : TryCreateFullBuildArtifact(
                sourceInput,
                inputResolution,
                compileOptions,
                options,
                optimizationLevel,
                targetInfo,
                buildProfile);
        var allowExactArtifactRestore = !options.ProfileModuleArtifactsOnly;
        if (allowExactArtifactRestore &&
            fullBuildArtifact != null &&
            options.Target is CompileTarget.LlvmIr or CompileTarget.Native &&
            TryRestoreFullBuildArtifactWithProfile(
                fullBuildArtifact,
                options.NoColor,
                options.ProfileJson,
                buildProfile))
        {
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "build",
                true,
                commandStopwatch.Elapsed,
                !options.NoColor,
                CliMessages.PhaseTargetDetails(CompilationPhase.Llvm, options.Target));
            return 0;
        }

        if (allowExactArtifactRestore &&
            fullBuildArtifact != null &&
            options.Target is CompileTarget.Resolved or CompileTarget.Typed &&
            TryRestoreAnalysisArtifactWithProfile(
                fullBuildArtifact,
                options.ProfileJson,
                buildProfile,
                out var exactAnalysisRestoreResult))
        {
            commandStopwatch.Stop();
            CliOutput.RenderDiagnostics(exactAnalysisRestoreResult, !options.NoColor);
            CliOutput.WriteStatus(DiagnosticLevel.Info, CliMessages.BuildSucceededStatus, !options.NoColor);
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.CompletedPhaseStatus(exactAnalysisRestoreResult.CompletedPhase),
                !options.NoColor);
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                CliMessages.TotalTimeStatus(exactAnalysisRestoreResult.TotalTime.TotalMilliseconds),
                !options.NoColor);
            CliOutput.WriteFinished(
                "build",
                true,
                commandStopwatch.Elapsed,
                !options.NoColor,
                CliMessages.PhaseTargetDetails(exactAnalysisRestoreResult.CompletedPhase, options.Target));
            return 0;
        }

        compileOptions.PreviousModuleSemanticSignatureSnapshot = TryLoadLatestModuleSemanticSignatureSnapshot(fullBuildArtifact);
        compileOptions.PreviousModuleTypedSemanticSnapshot = TryLoadLatestModuleTypedSemanticSignatureSnapshot(fullBuildArtifact);
        compileOptions.PreviousModuleMemberIndexSnapshot = TryLoadLatestModuleMemberIndexSnapshot(fullBuildArtifact);
        compileOptions.PreviousModuleDependencySignatureSnapshot = TryLoadLatestModuleDependencySignatureSnapshot(fullBuildArtifact);
        compileOptions.PreviousImplOverlapCheckSnapshot = TryLoadLatestImplOverlapCheckSnapshot(fullBuildArtifact);
        compileOptions.PreviousMirFunctionFingerprintSnapshot = TryLoadLatestMirFunctionFingerprintSnapshot(fullBuildArtifact);
        compileOptions.PreviousLlvmFunctionFingerprintSnapshot = TryLoadLatestLlvmFunctionFingerprintSnapshot(fullBuildArtifact);
        compileOptions.PreviousLlvmFunctionFragmentSnapshot = TryLoadLatestLlvmFunctionFragmentSnapshot(fullBuildArtifact);
        compileOptions.PreviousLlvmModuleEnvelopeSnapshot = TryLoadLatestLlvmModuleEnvelopeSnapshot(fullBuildArtifact);
        compileOptions.PreviousLlvmCodegenUnitPlanSnapshot = TryLoadLatestLlvmCodegenUnitPlanSnapshot(fullBuildArtifact);
        compileOptions.PreviousTypeDirectedCallableResolutionSnapshot = TryLoadLatestTypeDirectedCallableResolutionSnapshot(fullBuildArtifact);
        compileOptions.PreviousAssociatedTypeProjectionSnapshot = TryLoadLatestAssociatedTypeProjectionSnapshot(fullBuildArtifact);
        compileOptions.PreviousAssociatedConstProjectionSnapshot = TryLoadLatestAssociatedConstProjectionSnapshot(fullBuildArtifact);
        compileOptions.PreviousTraitCheckSnapshot = TryLoadLatestTraitCheckSnapshot(fullBuildArtifact);
        compileOptions.PreviousSendAnalysisSnapshot = TryLoadLatestSendAnalysisSnapshot(fullBuildArtifact);
        compileOptions.PreviousBorrowDiagnosticSnapshot = TryLoadLatestBorrowDiagnosticSnapshot(fullBuildArtifact);
        compileOptions.PreviousBorrowCodegenHintsSnapshot = TryLoadLatestBorrowCodegenHintsSnapshot(fullBuildArtifact);
        compileOptions.ModuleArtifactAvailability = fullBuildArtifact == null
            ? null
            : (moduleKey, kind, sourceHash, dependencySignatureHash) => IsModuleArtifactAvailable(
                fullBuildArtifact,
                moduleKey,
                kind,
                sourceHash,
                dependencySignatureHash);
        compileOptions.ModuleSemanticArtifactLoader = fullBuildArtifact == null
            ? null
            : (moduleKey, kind, sourceHash, dependencySignatureHash) => TryLoadModuleArtifactNode<ProjectModuleSemanticSignatureNode>(
                fullBuildArtifact,
                moduleKey,
                kind,
                sourceHash,
                dependencySignatureHash);
        compileOptions.ModuleNamerStatePayloadLoader = fullBuildArtifact == null
            ? null
            : (moduleKey, kind, sourceHash, dependencySignatureHash) => TryLoadModuleNamerStatePayloadArtifact(
                fullBuildArtifact,
                moduleKey,
                kind,
                sourceHash,
                dependencySignatureHash);
        compileOptions.ModuleTypesStatePayloadLoader = fullBuildArtifact == null
            ? null
            : (moduleKey, kind, sourceHash, dependencySignatureHash) => TryLoadModuleTypesStatePayloadArtifact(
                fullBuildArtifact,
                moduleKey,
                kind,
                sourceHash,
                dependencySignatureHash);
        compileOptions.ModuleHirStatePayloadLoader = fullBuildArtifact == null
            ? null
            : (moduleKey, kind, sourceHash, dependencySignatureHash) => TryLoadModuleHirStatePayloadArtifact(
                fullBuildArtifact,
                moduleKey,
                kind,
                sourceHash,
                dependencySignatureHash);
        compileOptions.ModuleMirStatePayloadLoader = fullBuildArtifact == null
            ? null
            : (moduleKey, kind, sourceHash, dependencySignatureHash) => TryLoadModuleMirStatePayloadArtifact(
                fullBuildArtifact,
                moduleKey,
                kind,
                sourceHash,
                dependencySignatureHash);
        compileOptions.ModuleTypedSemanticArtifactLoader = fullBuildArtifact == null
            ? null
            : (moduleKey, kind, sourceHash, dependencySignatureHash) => TryLoadModuleArtifactNode<ProjectModuleTypedSemanticNode>(
                fullBuildArtifact,
                moduleKey,
                kind,
                sourceHash,
                dependencySignatureHash);
        compileOptions.ModuleMirArtifactLoader = fullBuildArtifact == null
            ? null
            : (moduleKey, kind, sourceHash, dependencySignatureHash) => TryLoadModuleArtifactNode<ProjectModuleMirArtifactNode>(
                fullBuildArtifact,
                moduleKey,
                kind,
                sourceHash,
                dependencySignatureHash);
        if (allowExactArtifactRestore &&
            options.Target == CompileTarget.LlvmIr &&
            fullBuildArtifact != null &&
            TryRestoreLlvmIrFromBackendArtifactsWithProfile(fullBuildArtifact, options.NoColor, options.ProfileJson, buildProfile))
        {
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "build",
                true,
                commandStopwatch.Elapsed,
                !options.NoColor,
                CliMessages.PhaseTargetDetails(CompilationPhase.Llvm, options.Target));
            return 0;
        }

        if (allowExactArtifactRestore &&
            options.Target == CompileTarget.Native &&
            fullBuildArtifact != null &&
            TryRestoreNativeFromBackendArtifacts(
                fullBuildArtifact,
                options,
                compileOptions,
                ApplyNativeCpu(targetInfo ?? TargetInfo.Default, options.NativeCpu),
                optimizationLevel,
                buildProfile,
                out var nativeBackendRestored) &&
            nativeBackendRestored)
        {
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "build",
                true,
                commandStopwatch.Elapsed,
                !options.NoColor,
                CliMessages.PhaseTargetDetails(CompilationPhase.Llvm, options.Target));
            return 0;
        }

        // 执行编译管道
        var pipeline = new CompilationPipeline(sourceCode, compileOptions);
        var result = pipeline.Run();
        CodeGenProfile? codeGenProfile = buildProfile;

        // 处理 LLVM 输出
        if (result.Success &&
            options.Target == CompileTarget.LlvmIr &&
            (result.LlvmModule != null || result.LlvmIrText != null))
        {
            codeGenProfile = buildProfile;
            var outputPath = ProjectCommandPaths.ResolveLlvmIrOutputPath(options.Output, inputResolution);
            var effectiveTargetInfo = ApplyNativeCpu(targetInfo ?? TargetInfo.Default, options.NativeCpu);
            var llvmIr = result.LlvmIrText;
            if (llvmIr == null)
            {
                var llvmCompiler = new LlvmCompiler(effectiveTargetInfo,
                    optimizationLevel: optimizationLevel,
                    enableLto: options.Lto,
                    profile: codeGenProfile,
                    maxDegreeOfParallelism: options.Jobs);
                llvmIr = llvmCompiler.CompileToIr(result.LlvmModule!);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
            await File.WriteAllTextAsync(outputPath, llvmIr);
            fullBuildArtifact?.Cache.StoreArtifact(
                fullBuildArtifact.Key,
                fullBuildArtifact.Kind,
                ".ll",
                llvmIr);
            fullBuildArtifact?.Cache.StoreArtifact(
                fullBuildArtifact.OutputIndependentPayloadKey,
                fullBuildArtifact.Kind,
                ".ll",
                llvmIr);
            CliOutput.WriteArtifact(CliMessages.ArtifactKindLlvmIr, outputPath, !options.NoColor);
            CliOutput.WriteStatus(DiagnosticLevel.Help, CliMessages.LlvmIrWritten(outputPath), !options.NoColor);
        }

        // 处理本地代码生成
        if (result.Success &&
            options.Target == CompileTarget.Native &&
            (result.LlvmModule != null ||
             result.LlvmFunctionFragments != null ||
             IsObjectGroupsCodegenMode(options.CodegenMode)))
        {
            codeGenProfile = buildProfile;
            var effectiveTargetInfo = ApplyNativeCpu(targetInfo ?? TargetInfo.Default, options.NativeCpu);
            var outputPath = ProjectCommandPaths.ResolveNativeOutputPath(
                options.Output,
                inputResolution,
                effectiveTargetInfo);

            var llvmCompiler = new LlvmCompiler(effectiveTargetInfo,
                optimizationLevel: optimizationLevel,
                enableLto: options.Lto,
                linkMode: compileOptions.NativeLinkMode,
                profile: codeGenProfile,
                maxDegreeOfParallelism: options.Jobs);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
            var codeGenResult = IsObjectGroupsCodegenMode(options.CodegenMode)
                ? CompileNativeWithObjectGroups(
                    llvmCompiler,
                    result,
                    compileOptions,
                    outputPath,
                    Math.Max(0, options.MaxObjectGroups))
                : CompileNativeFullModule(
                    llvmCompiler,
                    result,
                    outputPath);

            if (codeGenResult.Success)
            {
                fullBuildArtifact?.Cache.StoreArtifactFile(
                    fullBuildArtifact.Key,
                    fullBuildArtifact.Kind,
                    effectiveTargetInfo.ExecutableExtension,
                    outputPath);
                fullBuildArtifact?.Cache.StoreArtifactFile(
                    fullBuildArtifact.OutputIndependentPayloadKey,
                    fullBuildArtifact.Kind,
                    effectiveTargetInfo.ExecutableExtension,
                    outputPath);
                CliOutput.WriteArtifact(CliMessages.ArtifactKindExecutable, outputPath, !options.NoColor);
                CliOutput.WriteStatus(DiagnosticLevel.Help, CliMessages.ExecutableGenerated(outputPath), !options.NoColor);
            }
            else
            {
                await WriteBuildProfileJsonAsync(options.ProfileJson, result, codeGenProfile);
                CliOutput.WriteStatus(
                    DiagnosticLevel.Error,
                    CliMessages.CodeGenerationFailed(codeGenResult.ErrorMessage ?? string.Empty),
                    !options.NoColor);
                commandStopwatch.Stop();
                CliOutput.WriteFinished(
                    "build",
                    false,
                    commandStopwatch.Elapsed,
                    !options.NoColor,
                    CliMessages.CodegenFailedDetail);
                return 1;
            }
        }

        if (result.Success)
        {
            StoreBuildAnalysisArtifacts(fullBuildArtifact, result);
            if (fullBuildArtifact != null && options.CacheMaxMib > 0)
            {
                fullBuildArtifact.Cache.Prune((long)options.CacheMaxMib * 1024 * 1024);
            }
        }

        await WriteBuildProfileJsonAsync(options.ProfileJson, result, codeGenProfile);

        CliOutput.RenderDiagnostics(result, !options.NoColor);
        CliOutput.WriteStatus(
            result.Success ? DiagnosticLevel.Info : DiagnosticLevel.Error,
            result.Success ? CliMessages.BuildSucceededStatus : CliMessages.BuildFailedStatus,
            !options.NoColor);
        CliOutput.WriteStatus(DiagnosticLevel.Note, CliMessages.CompletedPhaseStatus(result.CompletedPhase), !options.NoColor);
        CliOutput.WriteStatus(
            DiagnosticLevel.Note,
            CliMessages.TotalTimeStatus(result.TotalTime.TotalMilliseconds),
            !options.NoColor);
        if (result.PhaseTimes.Count > 0)
        {
            foreach (var (phase, time) in result.PhaseTimes.OrderBy(entry => entry.Key))
            {
                if (result.PhaseAllocations.TryGetValue(phase, out var allocatedBytes))
                {
                    CliOutput.WriteStatus(
                        DiagnosticLevel.Note,
                        CliMessages.PhaseTimeAllocationStatus(
                            phase,
                            time.TotalMilliseconds,
                            CliFormatters.FormatBytes(allocatedBytes)),
                        !options.NoColor);
                }
                else
                {
                    CliOutput.WriteStatus(
                        DiagnosticLevel.Note,
                        CliMessages.PhaseTimeStatus(phase, time.TotalMilliseconds),
                        !options.NoColor);
                }
            }
        }

        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            "build",
            result.Success,
            commandStopwatch.Elapsed,
            !options.NoColor,
            CliMessages.PhaseTargetDetails(result.CompletedPhase, options.Target));

        return result.Success ? 0 : 1;
    }

    private static void RenderBuildTrace(EidosBuildHostResult result, bool useColors)
    {
        CliOutput.WriteStatus(
            DiagnosticLevel.Note,
            $"Build host {result.HostTriple} -> {result.TargetTriple}; program={result.ProgramHash}; fingerprint={result.CacheFingerprint}",
            useColors);
        foreach (var dependency in result.Dependencies)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                $"Build dependency {dependency.Kind}:{dependency.Name} {dependency.Fingerprint}",
                useColors);
        }

        foreach (var access in result.CapabilityTrace)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                $"Build capability #{access.Sequence} {access.Kind}:{access.Name} {access.Fingerprint}",
                useColors);
        }

        if (result.Graph != null)
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                $"BuildGraph {result.Graph.CanonicalHash}: {result.Graph.Steps.Count} step(s), {result.Graph.Artifacts.Count} artifact(s)",
                useColors);
        }

        foreach (var step in result.Execution?.Steps ?? [])
        {
            CliOutput.WriteStatus(
                DiagnosticLevel.Note,
                $"Build step {step.Name}: tool={step.Tool}, exit={step.ExitCode}, cacheHit={step.CacheHit}, elapsed={step.Elapsed.TotalMilliseconds:F0}ms",
                useColors);
            if (!string.IsNullOrWhiteSpace(step.StandardOutput))
            {
                CliOutput.WriteStatus(DiagnosticLevel.Note, $"Build stdout {step.Name}: {step.StandardOutput.TrimEnd()}", useColors);
            }
            if (!string.IsNullOrWhiteSpace(step.StandardError))
            {
                CliOutput.WriteStatus(DiagnosticLevel.Warning, $"Build stderr {step.Name}: {step.StandardError.TrimEnd()}", useColors);
            }
        }
    }

    private static async Task<string> TryReadBuildProgramSourceAsync(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static async Task WriteBuildProfileJsonAsync(
        string? outputPath,
        CompilationResult result,
        CodeGenProfile? codeGenProfile)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var snapshot = CompilationProfilingFormatter.CreateSnapshot(result);
        snapshot.CodeGenEvents.AddRange(codeGenProfile?.Events ?? []);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void StoreBuildAnalysisArtifacts(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        StoreModuleSignatureSnapshotArtifact(artifact, result);
        StoreModuleSemanticSignatureSnapshotArtifact(artifact, result);
        StoreModuleTypedSemanticSignatureSnapshotArtifact(artifact, result);
        StoreModuleMirArtifactSnapshotArtifact(artifact, result);
        StoreModuleArtifactRestorePlanSnapshotArtifact(artifact, result);
        StoreModuleMemberIndexSnapshotArtifact(artifact, result);
        StorePerModuleSemanticArtifacts(artifact, result);
        StorePerModuleNamerStatePayloadArtifacts(artifact, result);
        StorePerModuleTypesStatePayloadArtifacts(artifact, result);
        StorePerModuleHirStatePayloadArtifacts(artifact, result);
        StorePerModuleMirStatePayloadArtifacts(artifact, result);
        StoreFunctionFingerprintSnapshotArtifacts(artifact, result);
        StoreLatestModuleSemanticSignatureSnapshotArtifact(artifact, result);
        StoreLatestModuleMemberIndexSnapshotArtifact(artifact, result);
        StoreLatestModuleMemberIndexRestorePlanSnapshotArtifact(artifact, result);
        StoreLatestModuleDependencySignatureSnapshotArtifact(artifact, result);
        StoreLatestImplOverlapCheckSnapshotArtifact(artifact, result);
        StoreLatestModuleTypedSemanticSignatureSnapshotArtifact(artifact, result);
        StoreLatestModuleMirArtifactSnapshotArtifact(artifact, result);
        StoreLatestModuleArtifactRestorePlanSnapshotArtifact(artifact, result);
        StoreLatestModuleTypedArtifactRestorePlanSnapshotArtifact(artifact, result);
        StoreLatestTypeDirectedCallableResolutionSnapshotArtifact(artifact, result);
        StoreLatestAssociatedTypeProjectionSnapshotArtifact(artifact, result);
        StoreLatestAssociatedConstProjectionSnapshotArtifact(artifact, result);
        StoreLatestTraitCheckSnapshotArtifact(artifact, result);
        StoreLatestSendAnalysisSnapshotArtifact(artifact, result);
        StoreLatestBorrowDiagnosticSnapshotArtifact(artifact, result);
        StoreLatestBorrowCodegenHintsSnapshotArtifact(artifact, result);
        StoreLatestFunctionFingerprintSnapshotArtifacts(artifact, result);
        StoreLatestBackendArtifactRestoreInputSnapshotArtifact(artifact, result);
    }

    internal sealed record FullBuildArtifact(
        ModuleArtifactCache Cache,
        ModuleArtifactKey Key,
        ModuleArtifactKey OutputIndependentPayloadKey,
        ModuleArtifactKey LatestSemanticSignatureKey,
        ModuleArtifactKey LatestTypedSemanticSignatureKey,
        ModuleArtifactKey LatestMirArtifactKey,
        ModuleArtifactKey LatestMirFunctionFingerprintKey,
        ModuleArtifactKey LatestLlvmFunctionFingerprintKey,
        ModuleArtifactKey LatestLlvmFunctionFragmentKey,
        string OutputPath,
        string Kind,
        CompileTarget Target);

    private static void StoreModuleSignatureSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleSignatureSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            artifact.Key,
            ModuleSignatureSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleSignatureSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    private static void StoreModuleSemanticSignatureSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleSemanticSignatureSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            artifact.Key,
            ModuleSemanticSignatureSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleSemanticSignatureSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static void StoreModuleTypedSemanticSignatureSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleTypedSemanticSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            artifact.Key,
            ModuleTypedSemanticSignatureSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleTypedSemanticSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static void StoreModuleMirArtifactSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleMirArtifactSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            artifact.Key,
            ModuleMirArtifactSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleMirArtifactSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static void StoreModuleArtifactRestorePlanSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleTypedArtifactRestorePlan == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            artifact.Key,
            ModuleArtifactRestorePlanSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleTypedArtifactRestorePlan, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static void StoreModuleMemberIndexSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleMemberIndexSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            artifact.Key,
            ModuleMemberIndexSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleMemberIndexSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static void StorePerModuleSemanticArtifacts(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null)
        {
            return;
        }

        if (result.ModuleSemanticSignatureSnapshot != null)
        {
            foreach (var node in result.ModuleSemanticSignatureSnapshot.Nodes)
            {
                artifact.Cache.StoreArtifact(
                    CreateModuleArtifactKey(artifact, node.ModuleKey, node.ExportSurfaceHash, node.DependencySemanticSignatureHash),
                    ModuleSemanticSignatureArtifactKind,
                    ".json",
                    JsonSerializer.Serialize(node, new JsonSerializerOptions
                    {
                        WriteIndented = false
                    }));
            }
        }

        if (result.ModuleTypedSemanticSnapshot != null)
        {
            foreach (var node in result.ModuleTypedSemanticSnapshot.Nodes)
            {
                artifact.Cache.StoreArtifact(
                    CreateModuleArtifactKey(artifact, node.ModuleKey, node.LocalSurfaceHash, node.DependencyTypedSemanticHash),
                    ModuleTypedSemanticSignatureArtifactKind,
                    ".json",
                    JsonSerializer.Serialize(node, new JsonSerializerOptions
                    {
                        WriteIndented = false
                    }));
            }
        }

        if (result.ModuleMirArtifactSnapshot != null)
        {
            foreach (var node in result.ModuleMirArtifactSnapshot.Nodes)
            {
                artifact.Cache.StoreArtifact(
                    CreateModuleArtifactKey(artifact, node.ModuleKey, node.TypedSemanticHash, node.MirArtifactHash),
                    ModuleMirArtifactKind,
                    ".json",
                    JsonSerializer.Serialize(node, new JsonSerializerOptions
                    {
                        WriteIndented = false
                    }));
            }
        }
    }

    internal static void StoreFunctionFingerprintSnapshotArtifacts(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null)
        {
            return;
        }

        if (result.MirFunctionFingerprints != null)
        {
            artifact.Cache.StoreArtifact(
                artifact.Key,
                MirFunctionFingerprintSnapshotArtifactKind,
                ".json",
                JsonSerializer.Serialize(result.MirFunctionFingerprints, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
        }

        if (result.LlvmFunctionFingerprints != null)
        {
            artifact.Cache.StoreArtifact(
                artifact.Key,
                LlvmFunctionFingerprintSnapshotArtifactKind,
                ".json",
                JsonSerializer.Serialize(result.LlvmFunctionFingerprints, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
        }

        if (result.LlvmFunctionFragments != null)
        {
            artifact.Cache.StoreArtifact(
                artifact.Key,
                LlvmFunctionFragmentSnapshotArtifactKind,
                ".json",
                JsonSerializer.Serialize(result.LlvmFunctionFragments, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
        }

        if (result.LlvmModuleEnvelope != null)
        {
            artifact.Cache.StoreArtifact(
                artifact.Key,
                LlvmModuleEnvelopeSnapshotArtifactKind,
                ".json",
                JsonSerializer.Serialize(result.LlvmModuleEnvelope, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
        }

        if (result.LlvmCodegenUnitPlan != null)
        {
            artifact.Cache.StoreArtifact(
                artifact.Key,
                LlvmCodegenUnitPlanSnapshotArtifactKind,
                ".json",
                JsonSerializer.Serialize(result.LlvmCodegenUnitPlan, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
        }
    }

    internal static void StoreLatestModuleSemanticSignatureSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleSemanticSignatureSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestModuleSemanticSignatureKey(artifact),
            LatestModuleSemanticSignatureSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleSemanticSignatureSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static void StoreLatestModuleTypedSemanticSignatureSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleTypedSemanticSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestModuleTypedSemanticSignatureKey(artifact),
            LatestModuleTypedSemanticSignatureSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleTypedSemanticSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static void StoreLatestModuleMirArtifactSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleMirArtifactSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestModuleMirArtifactKey(artifact),
            LatestModuleMirArtifactSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleMirArtifactSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    private static ProjectModuleSemanticSignatureSnapshot? TryLoadLatestModuleSemanticSignatureSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestModuleSemanticSignatureKey(artifact),
                    LatestModuleSemanticSignatureSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<ProjectModuleSemanticSignatureSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Nodes == null ||
                   !string.Equals(snapshot.SchemaVersion, ProjectModuleSemanticSignatureSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static ProjectModuleSemanticSignatureSnapshot? TryLoadModuleSemanticSignatureSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    artifact.Key,
                    ModuleSemanticSignatureSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<ProjectModuleSemanticSignatureSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Nodes == null ||
                   !string.Equals(snapshot.SchemaVersion, ProjectModuleSemanticSignatureSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static ProjectModuleTypedSemanticSnapshot? TryLoadLatestModuleTypedSemanticSignatureSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestModuleTypedSemanticSignatureKey(artifact),
                    LatestModuleTypedSemanticSignatureSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<ProjectModuleTypedSemanticSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Nodes == null ||
                   !string.Equals(snapshot.SchemaVersion, ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static ProjectModuleTypedSemanticSnapshot? TryLoadModuleTypedSemanticSignatureSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    artifact.Key,
                    ModuleTypedSemanticSignatureSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<ProjectModuleTypedSemanticSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Nodes == null ||
                   !string.Equals(snapshot.SchemaVersion, ProjectModuleTypedSemanticSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static ProjectModuleMirArtifactSnapshot? TryLoadLatestModuleMirArtifactSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestModuleMirArtifactKey(artifact),
                    LatestModuleMirArtifactSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<ProjectModuleMirArtifactSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Nodes == null ||
                   !string.Equals(snapshot.SchemaVersion, ProjectModuleMirArtifactSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    private static ModuleArtifactKey CreateLatestModuleSemanticSignatureKey(FullBuildArtifact artifact)
    {
        return artifact.LatestSemanticSignatureKey;
    }

    private static ModuleArtifactKey CreateLatestModuleTypedSemanticSignatureKey(FullBuildArtifact artifact)
    {
        return artifact.LatestTypedSemanticSignatureKey;
    }

    private static ModuleArtifactKey CreateLatestModuleMirArtifactKey(FullBuildArtifact artifact)
    {
        return artifact.LatestMirArtifactKey;
    }

    private static ModuleArtifactKey CreateLatestModuleArtifactRestorePlanKey(FullBuildArtifact artifact)
    {
        return artifact.LatestSemanticSignatureKey with
        {
            CacheSchema = "module-artifact-restore-plan-latest-v1"
        };
    }

    private static ModuleArtifactKey CreateLatestModuleNamerStatePayloadsKey(FullBuildArtifact artifact)
    {
        return artifact.LatestSemanticSignatureKey with
        {
            CacheSchema = "module-namer-state-payloads-latest-v1"
        };
    }

    private static ModuleArtifactKey CreateLatestModuleTypesStatePayloadsKey(FullBuildArtifact artifact)
    {
        return artifact.LatestTypedSemanticSignatureKey with
        {
            CacheSchema = "module-types-state-payloads-latest-v1"
        };
    }

    private static ModuleArtifactKey CreateLatestModuleHirStatePayloadsKey(FullBuildArtifact artifact)
    {
        return artifact.LatestTypedSemanticSignatureKey with
        {
            CacheSchema = "module-hir-state-payloads-latest-v1"
        };
    }

    private static ModuleArtifactKey CreateLatestModuleMirStatePayloadsKey(FullBuildArtifact artifact)
    {
        return artifact.LatestMirArtifactKey with
        {
            CacheSchema = "module-mir-state-payloads-latest-v1"
        };
    }

    private static ModuleArtifactKey CreateLatestModuleTypedArtifactRestorePlanKey(FullBuildArtifact artifact)
    {
        return artifact.LatestMirArtifactKey with
        {
            CacheSchema = "module-typed-artifact-restore-plan-latest-v1"
        };
    }

    private static ModuleArtifactKey CreateLatestMirFunctionFingerprintKey(FullBuildArtifact artifact)
    {
        return artifact.LatestMirFunctionFingerprintKey;
    }

    private static ModuleArtifactKey CreateLatestLlvmFunctionFingerprintKey(FullBuildArtifact artifact)
    {
        return artifact.LatestLlvmFunctionFingerprintKey;
    }

    private static ModuleArtifactKey CreateLatestLlvmFunctionFragmentKey(FullBuildArtifact artifact)
    {
        return artifact.LatestLlvmFunctionFragmentKey;
    }

    private static ModuleArtifactKey CreateLatestLlvmModuleEnvelopeKey(FullBuildArtifact artifact)
    {
        return artifact.LatestLlvmFunctionFragmentKey with
        {
            CacheSchema = "llvm-module-envelope-latest-v1"
        };
    }

    private static ModuleArtifactKey CreateLatestLlvmCodegenUnitPlanKey(FullBuildArtifact artifact)
    {
        return artifact.LatestLlvmFunctionFragmentKey with
        {
            CacheSchema = "llvm-codegen-unit-plan-latest-v2"
        };
    }

    private static bool IsModuleArtifactAvailable(
        FullBuildArtifact artifact,
        string moduleKey,
        string kind,
        string sourceHash,
        string dependencySignatureHash)
    {
        var key = CreateModuleArtifactKey(artifact, moduleKey, sourceHash, dependencySignatureHash);
        return artifact.Cache.IsArtifactUpToDate(key, kind);
    }

    private static ModuleArtifactKey CreateModuleArtifactKey(
        FullBuildArtifact artifact,
        string moduleKey,
        string sourceHash,
        string dependencySignatureHash)
    {
        return artifact.Key with
        {
            CacheSchema = "module-artifact-readiness-v1",
            ModuleKey = moduleKey,
            SourceHash = sourceHash,
            DependencySignatureHash = dependencySignatureHash,
            FlagsHash = artifact.OutputIndependentPayloadKey.FlagsHash
        };
    }

    private static FullBuildArtifact? TryCreateFullBuildArtifact(
        ProjectCommandSourceInput sourceInput,
        ProjectCommandInputResolution inputResolution,
        CompilationOptions compileOptions,
        BuildOptions options,
        int optimizationLevel,
        TargetInfo? targetInfo,
        CodeGenProfile? profile = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var success = false;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["target"] = options.Target.ToString(),
            ["inMemorySource"] = sourceInput.IsInMemorySource.ToString()
        };
        if (sourceInput.IsInMemorySource)
        {
            stopwatch.Stop();
            profile?.Record(
                "artifact_cache",
                "full_build_artifact_key",
                tool: null,
                stopwatch.Elapsed,
                success: true,
                cacheHit: false,
                metadata: metadata);
            return null;
        }

        var projectDirectory = ProjectCommandPaths.ResolveProjectDirectory(inputResolution);
        metadata["projectDirectory"] = projectDirectory ?? "";
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            stopwatch.Stop();
            profile?.Record(
                "artifact_cache",
                "full_build_artifact_key",
                tool: null,
                stopwatch.Elapsed,
                success: true,
                cacheHit: false,
                metadata: metadata);
            return null;
        }

        try
        {
            var effectiveTargetInfo = ApplyNativeCpu(targetInfo ?? TargetInfo.Default, options.NativeCpu);
            var outputPath = options.Target switch
            {
                CompileTarget.Native => ProjectCommandPaths.ResolveNativeOutputPath(options.Output, inputResolution, effectiveTargetInfo),
                CompileTarget.LlvmIr => ProjectCommandPaths.ResolveLlvmIrOutputPath(options.Output, inputResolution),
                _ => sourceInput.SourceFilePath
            };
            var artifactKind = options.Target switch
            {
                CompileTarget.Native => NativeBuildArtifactKind,
                CompileTarget.LlvmIr => LlvmIrBuildArtifactKind,
                _ => "profile-module-artifacts"
            };
            var sourceHash = ComputeFullBuildInputHash(
                sourceInput,
                inputResolution,
                out var inputHashStats,
                options.Jobs);
            metadata["sourceFiles"] = inputHashStats.SourceFileCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata["sourceRoots"] = inputHashStats.SourceRootCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata["sourceBytes"] = inputHashStats.SourceBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata["parallelReads"] = inputHashStats.ParallelReadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata["targetTriple"] = effectiveTargetInfo.Triple;
            metadata["artifactKind"] = artifactKind;

            var flagsHash = ModuleArtifactHash.ComputeFlagsHash(CreateFullBuildArtifactFlags(
                inputResolution,
                compileOptions,
                options,
                optimizationLevel,
                targetInfo,
                outputPath));
            var latestSemanticFlagsHash = ModuleArtifactHash.ComputeFlagsHash(CreateFullBuildArtifactFlags(
                inputResolution,
                compileOptions,
                options,
                optimizationLevel,
                targetInfo,
                outputPath,
                includeOutputPath: false));
            var cache = new ModuleArtifactCache(Path.Combine(projectDirectory, "build", ".eidos-cache"));
            var key = new ModuleArtifactKey
            {
                CacheSchema = options.Target switch
                {
                    CompileTarget.Native => "native-full-build-v2",
                    CompileTarget.LlvmIr => "llvm-ir-full-build-v2",
                    _ => "profile-module-artifacts-v2"
                },
                CompilerBuildId = CompilerBuildIdentity.Current,
                ModuleKey = CreateFullBuildModuleKey(inputResolution, options.Target),
                SourceHash = sourceHash,
                LanguageVersion = compileOptions.LanguageVersion,
                DependencySignatureHash = ModuleArtifactHash.ComputeDependencySignatureHash([]),
                TargetTriple = effectiveTargetInfo.Triple,
                FlagsHash = flagsHash
            };
            var latestSemanticKey = key with
            {
                CacheSchema = "module-semantic-signature-latest-v1",
                SourceHash = "latest",
                DependencySignatureHash = "latest",
                FlagsHash = latestSemanticFlagsHash
            };
            var latestTypedSemanticKey = key with
            {
                CacheSchema = "module-typed-semantic-signature-latest-v1",
                SourceHash = "latest",
                DependencySignatureHash = "latest",
                FlagsHash = latestSemanticFlagsHash
            };
            var latestMirFunctionFingerprintKey = key with
            {
                CacheSchema = "mir-function-fingerprint-latest-v1",
                SourceHash = "latest",
                DependencySignatureHash = "latest",
                FlagsHash = latestSemanticFlagsHash
            };
            var latestMirArtifactKey = key with
            {
                CacheSchema = "module-mir-artifact-latest-v1",
                SourceHash = "latest",
                DependencySignatureHash = "latest",
                FlagsHash = latestSemanticFlagsHash
            };
            var latestLlvmFunctionFingerprintKey = key with
            {
                CacheSchema = "llvm-function-fingerprint-latest-v1",
                SourceHash = "latest",
                DependencySignatureHash = "latest",
                FlagsHash = latestSemanticFlagsHash
            };
            var latestLlvmFunctionFragmentKey = key with
            {
                CacheSchema = "llvm-function-fragment-latest-v1",
                SourceHash = "latest",
                DependencySignatureHash = "latest",
                FlagsHash = latestSemanticFlagsHash
            };
            var outputIndependentPayloadKey = key with
            {
                CacheSchema = options.Target switch
                {
                    CompileTarget.Native => "native-full-build-output-independent-v1",
                    CompileTarget.LlvmIr => "llvm-ir-full-build-output-independent-v1",
                    _ => "profile-module-artifacts-output-independent-v1"
                },
                FlagsHash = latestSemanticFlagsHash
            };

            success = true;
            return new FullBuildArtifact(
                cache,
                key,
                outputIndependentPayloadKey,
                latestSemanticKey,
                latestTypedSemanticKey,
                latestMirArtifactKey,
                latestMirFunctionFingerprintKey,
                latestLlvmFunctionFingerprintKey,
                latestLlvmFunctionFragmentKey,
                outputPath,
                artifactKind,
                options.Target);
        }
        finally
        {
            stopwatch.Stop();
            profile?.Record(
                "artifact_cache",
                "full_build_artifact_key",
                tool: null,
                stopwatch.Elapsed,
                success: success,
                cacheHit: false,
                metadata: metadata);
        }
    }

    private static bool TryRestoreFullBuildArtifact(
        FullBuildArtifact artifact,
        bool noColor,
        string? profileJson) =>
        TryRestoreFullBuildArtifactWithProfile(artifact, noColor, profileJson, new CodeGenProfile());

    private static bool TryRestoreFullBuildArtifactWithProfile(
        FullBuildArtifact artifact,
        bool noColor,
        string? profileJson,
        CodeGenProfile codeGenProfile)
    {
        var stopwatch = Stopwatch.StartNew();
        var restored = false;
        var outputIndependentHit = false;
        try
        {
            if (!TryGetRestorableFullBuildArtifactManifest(artifact, out var manifest, out outputIndependentHit) ||
                manifest == null)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(artifact.OutputPath) ?? Directory.GetCurrentDirectory());
            File.Copy(manifest.PayloadPath, artifact.OutputPath, overwrite: true);
            restored = true;
            if (artifact.Target == CompileTarget.Native)
            {
                CliOutput.WriteArtifact(CliMessages.ArtifactKindExecutable, artifact.OutputPath, !noColor);
                CliOutput.WriteStatus(DiagnosticLevel.Help, CliMessages.ExecutableGenerated(artifact.OutputPath), !noColor);
            }
            else
            {
                CliOutput.WriteArtifact(CliMessages.ArtifactKindLlvmIr, artifact.OutputPath, !noColor);
                CliOutput.WriteStatus(DiagnosticLevel.Help, CliMessages.LlvmIrWritten(artifact.OutputPath), !noColor);
            }

            return true;
        }
        finally
        {
            stopwatch.Stop();
            codeGenProfile.Record(
                "artifact_cache",
                CreateFullBuildArtifactRestoreEventName(artifact.Target, outputIndependentHit),
                tool: null,
                stopwatch.Elapsed,
                success: restored,
                cacheHit: restored);
            if (restored)
            {
                WriteBuildProfileJsonAsync(
                        profileJson,
                        CreateFullBuildArtifactCacheHitResult(artifact, stopwatch.Elapsed, outputIndependentHit),
                        codeGenProfile)
                    .GetAwaiter()
                    .GetResult();
            }
        }
    }

    private static bool TryGetRestorableFullBuildArtifactManifest(
        FullBuildArtifact artifact,
        out ModuleArtifactManifest? manifest,
        out bool outputIndependentHit)
    {
        outputIndependentHit = false;
        if (artifact.Cache.TryGetArtifact(artifact.Key, artifact.Kind, out manifest) &&
            manifest != null &&
            File.Exists(manifest.PayloadPath))
        {
            return true;
        }

        if (artifact.Cache.TryGetArtifact(artifact.OutputIndependentPayloadKey, artifact.Kind, out manifest) &&
            manifest != null &&
            File.Exists(manifest.PayloadPath))
        {
            outputIndependentHit = true;
            return true;
        }

        manifest = null;
        return false;
    }

    private static bool TryRestoreAnalysisArtifact(
        FullBuildArtifact artifact,
        string? profileJson,
        out CompilationResult result) =>
        TryRestoreAnalysisArtifactWithProfile(artifact, profileJson, new CodeGenProfile(), out result);

    private static bool TryRestoreAnalysisArtifactWithProfile(
        FullBuildArtifact artifact,
        string? profileJson,
        CodeGenProfile codeGenProfile,
        out CompilationResult result)
    {
        result = null!;
        var stopwatch = Stopwatch.StartNew();
        var semanticSnapshot = TryLoadModuleSemanticSignatureSnapshot(artifact);
        if (semanticSnapshot == null)
        {
            return false;
        }

        var typedSnapshot = artifact.Target == CompileTarget.Typed
            ? TryLoadModuleTypedSemanticSignatureSnapshot(artifact)
            : null;
        if (artifact.Target == CompileTarget.Typed && typedSnapshot == null)
        {
            return false;
        }

        stopwatch.Stop();
        codeGenProfile.Record(
            "artifact_cache",
            CreateFullBuildArtifactRestoreEventName(artifact.Target, outputIndependentHit: false),
            tool: null,
            stopwatch.Elapsed,
            success: true,
            cacheHit: true);
        result = CreateFullBuildArtifactCacheHitResult(artifact, stopwatch.Elapsed, outputIndependentHit: false);
        WriteBuildProfileJsonAsync(profileJson, result, codeGenProfile).GetAwaiter().GetResult();
        return true;
    }

    private static string CreateFullBuildArtifactRestoreEventName(CompileTarget target, bool outputIndependentHit)
    {
        var suffix = outputIndependentHit ? "_output_independent" : "";
        return target switch
        {
            CompileTarget.Native => $"restore_native_full_build{suffix}",
            CompileTarget.LlvmIr => $"restore_llvm_ir_full_build{suffix}",
            CompileTarget.Typed => "restore_typed_analysis_full_build",
            CompileTarget.Resolved => "restore_resolved_analysis_full_build",
            _ => $"restore_analysis_full_build{suffix}"
        };
    }

    private static TargetInfo ApplyNativeCpu(TargetInfo targetInfo, bool nativeCpu)
    {
        return nativeCpu ? targetInfo.WithNativeCpu() : targetInfo;
    }

    internal static NativeLinkMode ParseNativeLinkModeOption(string value)
    {
        return TryParseNativeLinkModeOption(value, out var linkMode)
            ? linkMode
            : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    private static bool TryParseNativeLinkModeOption(string value, out NativeLinkMode linkMode)
    {
        linkMode = value switch
        {
            "platform-default" => NativeLinkMode.PlatformDefault,
            "no-pie" => NativeLinkMode.NonPieExecutable,
            "pie" => NativeLinkMode.PieExecutable,
            _ => default
        };

        return value is "platform-default" or "no-pie" or "pie";
    }

    private static NativeLinkMode ResolveNativeLinkMode(string? cliValue, string? projectValue)
    {
        if (!string.IsNullOrWhiteSpace(cliValue) &&
            TryParseNativeLinkModeOption(cliValue, out var cliMode))
        {
            return cliMode;
        }

        if (!string.IsNullOrWhiteSpace(projectValue) &&
            TryParseNativeLinkModeOption(projectValue, out var projectMode))
        {
            return projectMode;
        }

        return NativeLinkMode.NonPieExecutable;
    }

}
