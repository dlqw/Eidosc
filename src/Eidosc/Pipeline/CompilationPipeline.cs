using Eidosc.Symbols;
using Eidosc.ProjectSystem;
using Eidosc.Pipeline.TokenRewriting;
using System.Collections.Concurrent;
using System.Diagnostics;
using Eidosc.Ast.Declarations;
using Eidosc.Borrow;
using Eidosc.CodeGen;
using Eidosc.Debug;
using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Ide;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Pipeline;

/// <summary>
/// 编译管道 - 协调各编译阶段
/// </summary>
public sealed partial class CompilationPipeline
{
    internal const string GrammarCacheVersion = "2026-06-12.01";

    private readonly CompilationOptions _options;
    private readonly DebugContext _debugContext;
    private readonly List<Diagnostic.Diagnostic> _diagnostics = [];
    private readonly Dictionary<CompilationPhase, TimeSpan> _phaseTimes = [];
    private readonly Dictionary<CompilationPhase, long> _phaseAllocations = [];
    private readonly Dictionary<string, long> _profilingCounters = new(StringComparer.Ordinal);
    private readonly object _profilingCountersLock = new();
    private readonly ConcurrentDictionary<string, string> _moduleSourceTextCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _moduleLanguageVersionCache = new(StringComparer.Ordinal);
    private readonly CompilationProfiler _profiler;
    private readonly ComptimeExecutionOptions _comptimeExecution;
    private readonly string _sourceCode;
    private readonly ModuleDependencyGraph _moduleDependencyGraph = new();
    private ProjectModuleGraphSnapshot? _moduleGraphSnapshot;
    private ProjectModuleBuildSchedule? _moduleBuildSchedule;
    private ProjectModuleSignatureSnapshot? _moduleSignatureSnapshot;
    private ProjectModuleSemanticSignatureSnapshot? _moduleSemanticSignatureSnapshot;
    private ProjectModuleTypedSemanticSnapshot? _moduleTypedSemanticSnapshot;
    private ProjectModuleMirArtifactSnapshot? _moduleMirArtifactSnapshot;
    private ProjectModuleDependencySignatureSnapshot? _moduleDependencySignatureSnapshot;
    private ProjectModuleMemberIndexSnapshot? _moduleMemberIndexSnapshot;
    private IReadOnlyList<ModuleNamerStatePayload>? _moduleNamerStatePayloads;
    private IReadOnlyList<ModuleTypesStatePayload>? _moduleTypesStatePayloads;
    private IReadOnlyList<LiveStateSymbolIdentity>? _typesEntrySymbolIdentities;
    private SymbolTablePayload? _typesEntrySymbolTable;
    private LiveStateRemapPlan? _namerRestoreRemapPlan;
    private ProjectModuleMemberIndexRestorePlan? _moduleMemberIndexRestorePlan;
    private ProjectModuleMemberIndexRestorePayloadSnapshot? _moduleMemberIndexRestorePayload;
    private ProjectModuleInvalidationPlan? _moduleInvalidationPlan;
    private ProjectModuleInvalidationPlan? _moduleTypedInvalidationPlan;
    private ProjectModuleExecutionPlan? _moduleExecutionPlan;
    private ProjectModuleExecutionPlan? _moduleTypedExecutionPlan;
    private ProjectModuleParallelExecutionSnapshot? _moduleParallelExecution;
    private ProjectModuleParallelExecutionSnapshot? _moduleTypedParallelExecution;
    private ProjectModuleArtifactReadinessPlan? _moduleArtifactReadinessPlan;
    private ProjectModuleArtifactReadinessPlan? _moduleTypedArtifactReadinessPlan;
    private ProjectModuleArtifactRestorePlan? _moduleArtifactRestorePlan;
    private ProjectModuleArtifactRestorePlan? _moduleTypedArtifactRestorePlan;
    private ProjectModuleArtifactRestoreExecutionSnapshot? _moduleArtifactRestoreExecution;
    private ProjectModuleArtifactRestoreExecutionSnapshot? _moduleTypedArtifactRestoreExecution;
    private ProjectModuleArtifactRestorePayloadSnapshot? _moduleArtifactRestorePayload;
    private ProjectModuleArtifactRestorePayloadSnapshot? _moduleTypedArtifactRestorePayload;
    private ImplOverlapCheckSnapshot? _implOverlapCheckSnapshot;
    private TypeDirectedCallableResolutionSnapshot? _typeDirectedCallableResolutionSnapshot;
    private AssociatedTypeProjectionSnapshot? _associatedTypeProjectionSnapshot;
    private AssociatedConstProjectionSnapshot? _associatedConstProjectionSnapshot;
    private TraitCheckSnapshot? _traitCheckSnapshot;
    private IReadOnlySet<TypeId> _hirCopyLikeTypeIds = new HashSet<TypeId>();
    private IReadOnlyDictionary<TypeId, string> _hirDynamicTypeKeys = new Dictionary<TypeId, string>();
    private IReadOnlyDictionary<int, TypeDescriptor> _hirTypeDescriptors = new Dictionary<int, TypeDescriptor>();
    private IReadOnlyDictionary<int, List<ConstructorTypeLayout>> _hirConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>();

    // 编译数据
    private GrammarData? _grammarData;
    private ScannerData? _scannerData;
    private ModuleParseService? _moduleParseService;

    // 阶段结果
    private LexerContext? _compileContext;
    private ModuleDecl? _ast;
    private List<Token>? _tokens;
    private SymbolTable? _symbolTable;
    private NameResolver? _nameResolver;
    private TypeInferer? _typeInferer;
    private EffectInferer? _abilityInferer;
    private HirModule? _hirModule;
    private Mir.ParameterEffectMap? _hirParameterEffects;
    private MirModule? _mirModule;
    private MirModule? _borrowMirModule;
    private ModuleBorrowCheckResult? _borrowCheckResult;
    private LlvmModule? _llvmModule;
    private string? _llvmIrText;
    private MirFunctionFingerprintSnapshot? _mirFunctionFingerprints;
    private LlvmFunctionFingerprintSnapshot? _llvmFunctionFingerprints;
    private LlvmFunctionFragmentSnapshot? _llvmFunctionFragments;
    private LlvmFunctionFragmentRestorePlanSnapshot? _llvmFunctionFragmentRestorePlan;
    private LlvmFunctionFragmentRestoreResultSnapshot? _llvmFunctionFragmentRestoreResult;
    private LlvmModuleEnvelopeSnapshot? _llvmModuleEnvelope;
    private LlvmCodegenUnitPlanSnapshot? _llvmCodegenUnitPlan;
    private LlvmObjectGroupRestorePlanSnapshot? _llvmObjectGroupRestorePlan;
    private FunctionFingerprintDiffSnapshot? _mirFunctionFingerprintDiff;
    private FunctionFingerprintDiffSnapshot? _llvmFunctionFingerprintDiff;
    private FunctionWorklistSnapshot? _mirFunctionWorklist;
    private FunctionWorklistSnapshot? _llvmFunctionWorklist;
    private SendAnalysisSnapshot? _sendAnalysisSnapshot;
    private BorrowDiagnosticSnapshot? _borrowDiagnosticSnapshot;
    private string? _borrowDiagnosticDependencyHash;
    private string? _borrowCodegenDependencyHash;
    private BorrowCodegenHintsSnapshot? _borrowCodegenHintsSnapshot;
    private string? _liveStateFlagsHash;
    private CompilationLiveStatePayload? _compilationLiveStatePayload;

    public CompilationPipeline(string sourceCode, CompilationOptions options)
    {
        _sourceCode = sourceCode;
        _options = options;
        ResolveInputLanguageVersion(options);
        _profiler = new CompilationProfiler(options.EnableDetailedProfiling);
        _comptimeExecution = ComptimeExecutionOptions.Create(options);

        if (!string.IsNullOrEmpty(options.DebugOutputPath))
        {
            var emitter = new FileDebugEmitter(
                options.DebugOutputPath,
                options.DebugLevel,
                options.DebugGraphFormat,
                options.CleanDebugOutput);
            _debugContext = new DebugContext(emitter);
        }
        else
        {
            _debugContext = DebugContext.None;
        }
    }

    private record PhaseStep(
        CompilationPhase Phase,
        string DebugLabel,
        Func<bool> Execute,
        Action? AfterSuccess = null);

    private sealed record ImportModuleCandidate(
        string? PackageAlias,
        ResolvedWorkspaceModuleFile? WorkspaceModule,
        bool IsPrecompiled);

    private static void ResolveInputLanguageVersion(CompilationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.InputFile) ||
            !string.Equals(options.LanguageVersion, EidosLanguageVersions.DefaultForExistingProjects, StringComparison.Ordinal) ||
            options.AllowVirtualInputFile)
        {
            return;
        }

        var loaded = EidosProjectConfigurationLoader.TryLoadNearest(options.InputFile);
        if (loaded != null)
        {
            options.LanguageVersion = loaded.Configuration.LanguageVersion;
        }
    }

    /// <summary>
    /// 执行编译管道
    /// </summary>
    public CompilationResult Run()
    {
        var totalSw = Stopwatch.StartNew();
        var success = true;
        var completedPhase = CompilationPhase.Lexer;

        try
        {
            LoadOrBuildGrammarData();

            var steps = new List<PhaseStep>
            {
                new(CompilationPhase.Lexer,    "01_lexer",     RunLexer),
                new(CompilationPhase.Parser,   "02_parser",    RunParser,      PreloadImportedModules),
                new(CompilationPhase.Namer,    "03_namer",     RunNameResolver),
                new(CompilationPhase.Types,    "04_types",     RunTypeInfererAndFfi),
                new(CompilationPhase.Effects,"05_abilities", RunEffectInferer),
                new(CompilationPhase.Hir,      "06_hir",       RunHirBuilder),
                new(CompilationPhase.Mir,      "07_mir",       RunMirBuilder),
                new(CompilationPhase.Borrow,   "08_borrow",    RunBorrowChecker),
                new(CompilationPhase.Send,     "08_send",      RunSendCheck),
                new(CompilationPhase.Llvm,     "09_llvm",      RunLlvmGenerator),
            };

            foreach (var step in steps)
            {
                if (!RunPhase(step.Phase, step.DebugLabel, step.Execute))
                {
                    success = false;
                    completedPhase = step.Phase;
                    break;
                }

                completedPhase = step.Phase;
                step.AfterSuccess?.Invoke();

                if (ShouldStop(step.Phase))
                {
                    break;
                }
            }

            totalSw.Stop();
        }
        catch (Exception ex)
        {
            success = false;
            var includeExceptionDetails = ShouldExposeInternalExceptionDetails(_options);
            if (includeExceptionDetails)
            {
                _debugContext.LogDiagnostic($"Unhandled pipeline exception:{Environment.NewLine}{ex}");
            }

            _diagnostics.Add(CreateInternalErrorDiagnostic(ex, includeExceptionDetails));
        }

        ApplyWarningEscalation();
        AppendStyleSuggestions();
        if (_diagnostics.Any(diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error))
        {
            success = false;
        }

        RefreshFinalProfilingCounters();

        return new CompilationResult
        {
            Success = success,
            CompletedPhase = completedPhase,
            Diagnostics = _diagnostics,
            ComptimeTrace = _comptimeExecution.Trace.Snapshot(),
            InputFile = _options.InputFile,
            ImportSearchRoots = _options.ImportSearchRoots,
            NoImplicitPrelude = _options.NoImplicitPrelude,
            SourceText = _sourceCode,
            Tokens = _tokens ?? [],
            CstRoot = null,
            Ast = _ast,
            SymbolTable = _symbolTable,
            TypeInferer = _typeInferer,
            TypeAnalysisIncomplete = _typeInferer?.TypeAnalysisIncomplete ?? false,
            TypeAnalysisIncompleteReason = _typeInferer?.TypeAnalysisIncompleteReason,
            TypeErrorLimit = _typeInferer?.TypeErrorLimit ?? 0,
            SuppressedTypeDiagnosticCount = _typeInferer?.SuppressedTypeDiagnosticCount ?? 0,
            SuppressedTypeConstraintCount = _typeInferer?.SuppressedTypeConstraintCount ?? 0,
            EffectInferer = _abilityInferer,
            HirModule = _hirModule,
            MirModule = _mirModule,
            BorrowCheckResult = _borrowCheckResult,
            LlvmModule = _llvmModule,
            LlvmIrText = _llvmIrText,
            MirFunctionFingerprints = _mirFunctionFingerprints,
            LlvmFunctionFingerprints = _llvmFunctionFingerprints,
            LlvmFunctionFragments = _llvmFunctionFragments,
            LlvmFunctionFragmentRestorePlan = _llvmFunctionFragmentRestorePlan,
            LlvmFunctionFragmentRestoreResult = _llvmFunctionFragmentRestoreResult,
            LlvmModuleEnvelope = _llvmModuleEnvelope,
            LlvmCodegenUnitPlan = _llvmCodegenUnitPlan,
            LlvmObjectGroupRestorePlan = _llvmObjectGroupRestorePlan,
            MirFunctionFingerprintDiff = _mirFunctionFingerprintDiff,
            LlvmFunctionFingerprintDiff = _llvmFunctionFingerprintDiff,
            MirFunctionWorklist = _mirFunctionWorklist,
            LlvmFunctionWorklist = _llvmFunctionWorklist,
            SendAnalysisSnapshot = _sendAnalysisSnapshot,
            BorrowDiagnosticSnapshot = _borrowDiagnosticSnapshot,
            BorrowCodegenHintsSnapshot = _borrowCodegenHintsSnapshot,
            Documentation = Doc.DocCommentExtractor.Extract(_sourceCode),
            ModuleGraphSnapshot = _moduleGraphSnapshot,
            ModuleBuildSchedule = _moduleBuildSchedule,
            ModuleSignatureSnapshot = _moduleSignatureSnapshot,
            ModuleSemanticSignatureSnapshot = _moduleSemanticSignatureSnapshot,
            ModuleTypedSemanticSnapshot = _moduleTypedSemanticSnapshot,
            ModuleMirArtifactSnapshot = _moduleMirArtifactSnapshot,
            CompilationLiveStatePayload = _compilationLiveStatePayload,
            ModuleDependencySignatureSnapshot = _moduleDependencySignatureSnapshot,
            ModuleMemberIndexSnapshot = _moduleMemberIndexSnapshot,
            ModuleNamerStatePayloads = _moduleNamerStatePayloads,
            ModuleTypesStatePayloads = _moduleTypesStatePayloads,
            ModuleHirStatePayloads = ShouldCreateModuleStatePayloads()
                ? CreateModuleHirStatePayloads()
                : null,
            ModuleMirStatePayloads = ShouldCreateModuleStatePayloads()
                ? CreateModuleMirStatePayloads()
                : null,
            ModuleMemberIndexRestorePlan = _moduleMemberIndexRestorePlan,
            ModuleMemberIndexRestorePayload = _moduleMemberIndexRestorePayload,
            ModuleInvalidationPlan = _moduleInvalidationPlan,
            ModuleTypedInvalidationPlan = _moduleTypedInvalidationPlan,
            ModuleExecutionPlan = _moduleExecutionPlan,
            ModuleTypedExecutionPlan = _moduleTypedExecutionPlan,
            ModuleParallelExecution = _moduleParallelExecution,
            ModuleTypedParallelExecution = _moduleTypedParallelExecution,
            ModuleArtifactReadinessPlan = _moduleArtifactReadinessPlan,
            ModuleTypedArtifactReadinessPlan = _moduleTypedArtifactReadinessPlan,
            ModuleArtifactRestorePlan = _moduleArtifactRestorePlan,
            ModuleTypedArtifactRestorePlan = _moduleTypedArtifactRestorePlan,
            ModuleArtifactRestoreExecution = _moduleArtifactRestoreExecution,
            ModuleTypedArtifactRestoreExecution = _moduleTypedArtifactRestoreExecution,
            ModuleArtifactRestorePayload = _moduleArtifactRestorePayload,
            ModuleTypedArtifactRestorePayload = _moduleTypedArtifactRestorePayload,
            ImplOverlapCheckSnapshot = _implOverlapCheckSnapshot,
            TypeDirectedCallableResolutionSnapshot = _typeDirectedCallableResolutionSnapshot,
            AssociatedTypeProjectionSnapshot = _associatedTypeProjectionSnapshot,
            AssociatedConstProjectionSnapshot = _associatedConstProjectionSnapshot,
            TraitCheckSnapshot = _traitCheckSnapshot,
            TotalTime = totalSw.Elapsed,
            PhaseTimes = _phaseTimes,
            PhaseAllocations = _phaseAllocations,
            SubphaseMetrics = _profiler.Subphases.ToList(),
            ProfilingCounters = new Dictionary<string, long>(_profilingCounters, StringComparer.Ordinal)
        };
    }

    private void AppendStyleSuggestions()
    {
        if (!_options.EmitStyleSuggestions ||
            _ast == null ||
            _typeInferer == null ||
            _typeInferer?.TypeAnalysisIncomplete == true ||
            _diagnostics.Any(diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error))
        {
            return;
        }

        _diagnostics.AddRange(IdeStyleSuggestionBuilder.Build(
            _ast,
            _sourceCode,
            GetPrimarySourceName(),
            _symbolTable));
    }

    private bool RunPhase(CompilationPhase phase, string debugLabel, Func<bool> execute)
    {
        var sw = Stopwatch.StartNew();
        var allocatedBytesBefore = GetCurrentAllocatedBytes();
        using var phaseScope = _debugContext.PhaseScope(debugLabel);

        var result = execute();

        RecordPhaseMetrics(phase, sw, allocatedBytesBefore);
        return result;
    }

    private static long GetCurrentAllocatedBytes()
    {
        return GC.GetAllocatedBytesForCurrentThread();
    }

    private void RecordPhaseMetrics(CompilationPhase phase, Stopwatch stopwatch, long allocatedBytesBefore)
    {
        stopwatch.Stop();
        if (_phaseTimes.TryGetValue(phase, out var existingTime))
        {
            _phaseTimes[phase] = existingTime + stopwatch.Elapsed;
        }
        else
        {
            _phaseTimes[phase] = stopwatch.Elapsed;
        }

        var allocatedBytesAfter = GetCurrentAllocatedBytes();
        var allocatedBytes = Math.Max(0L, allocatedBytesAfter - allocatedBytesBefore);
        if (_phaseAllocations.TryGetValue(phase, out var existingAllocatedBytes))
        {
            _phaseAllocations[phase] = existingAllocatedBytes + allocatedBytes;
        }
        else
        {
            _phaseAllocations[phase] = allocatedBytes;
        }
    }

    private IDisposable MeasureSubphase(CompilationPhase phase, string name)
    {
        return _profiler.MeasureSubphase(phase, name);
    }

    private void SetProfilingCounter(string name, long value)
    {
        if (_options.EnableDetailedProfiling)
        {
            lock (_profilingCountersLock)
            {
                _profilingCounters[name] = value;
            }
        }
    }

    private void AddProfilingCounter(string name, long value)
    {
        if (!_options.EnableDetailedProfiling)
        {
            return;
        }

        lock (_profilingCountersLock)
        {
            _profilingCounters[name] = _profilingCounters.GetValueOrDefault(name) + value;
        }
    }

    private void AddProfilingCounters(IReadOnlyDictionary<string, long> counters)
    {
        if (!_options.EnableDetailedProfiling)
        {
            return;
        }

        foreach (var (name, value) in counters)
        {
            lock (_profilingCountersLock)
            {
                _profilingCounters[name] = value;
            }
        }
    }

    private void RefreshFinalProfilingCounters()
    {
        if (!_options.EnableDetailedProfiling)
        {
            return;
        }

        if (_nameResolver != null)
        {
            AddProfilingCounters(_nameResolver.GetProfilingCounters());
        }

        if (_typeInferer != null)
        {
            AddProfilingCounters(_typeInferer.GetProfilingCounters());
        }

        EnsureModuleStageCounters("Namer");
        EnsureModuleStageCounters("Types");
        EnsureModuleStageCounters("Hir");
        EnsureModuleStageCounters("Mir");
    }

    private static long StableCounterFromHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return 0;
        }

        var prefix = hash.Length > 15 ? hash[..15] : hash;
        return long.TryParse(
            prefix,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;
    }

    private FunctionWorklistSnapshot AddFunctionWorklistCounters(
        string prefix,
        FunctionFingerprintDiffSnapshot diff)
    {
        var worklist = FunctionWorklistSnapshot.FromDiff(diff);
        SetProfilingCounter($"{prefix}.worklist_restore_functions", worklist.Count(FunctionWorklistAction.Restore));
        SetProfilingCounter($"{prefix}.worklist_rebuild_functions", worklist.Count(FunctionWorklistAction.Rebuild));
        SetProfilingCounter($"{prefix}.worklist_remove_functions", worklist.Count(FunctionWorklistAction.Remove));
        return worklist;
    }

    internal static Diagnostic.Diagnostic CreateInternalErrorDiagnostic(
        Exception exception,
        bool includeExceptionDetails)
    {
        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.PipelineInternalError,
            "E0001")
            .WithHelp(DiagnosticMessages.PipelineInternalErrorHelp);

        if (!includeExceptionDetails)
        {
            return diagnostic;
        }

        diagnostic
            .WithNote(DiagnosticMessages.ExceptionNote(exception.GetType().FullName ?? exception.GetType().Name, exception.Message))
            .WithNote(DiagnosticMessages.StackTraceNote(exception.StackTrace ?? DiagnosticMessages.StackTraceUnavailable));

        return diagnostic;
    }

    internal static bool ShouldExposeInternalExceptionDetails(CompilationOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.DebugOutputPath) &&
               options.DebugLevel >= DebugLevel.Diagnostic;
    }

    private bool ShouldStop(CompilationPhase phase)
    {
        return _options.StopAtPhase.HasValue && _options.StopAtPhase.Value == phase;
    }

    private void ApplyWarningEscalation()
    {
        var escalateAll = _options.TreatWarningsAsErrors;
        var escalateCodes = _options.WarningCodesAsErrors;
        if (!escalateAll && escalateCodes.Count == 0)
        {
            return;
        }

        for (int i = 0; i < _diagnostics.Count; i++)
        {
            var diagnostic = _diagnostics[i];
            if (diagnostic.Level != Diagnostic.DiagnosticLevel.Warning)
            {
                continue;
            }

            var shouldEscalate = escalateAll ||
                                 (!string.IsNullOrWhiteSpace(diagnostic.Code) &&
                                  escalateCodes.Contains(diagnostic.Code!));
            if (!shouldEscalate)
            {
                continue;
            }

            _diagnostics[i] = CloneDiagnosticWithLevel(diagnostic, Diagnostic.DiagnosticLevel.Error);
        }
    }

    private static Diagnostic.Diagnostic CloneDiagnosticWithLevel(
        Diagnostic.Diagnostic source,
        Diagnostic.DiagnosticLevel targetLevel)
    {
        var clone = new Diagnostic.Diagnostic(targetLevel, source.Message, source.Code);
        if (source.Labels is List<Diagnostic.DiagnosticLabel> labels)
            labels.ForEach(l => clone.WithLabel(l.Span, l.Message));
        else
            foreach (var l in source.Labels) clone.WithLabel(l.Span, l.Message);

        foreach (var note in source.Notes) clone.WithNote(note);
        foreach (var help in source.Helps) clone.WithHelp(help);
        foreach (var (key, value) in source.Metadata) clone.WithMetadata(key, value);
        foreach (var suggestion in source.Suggestions)
        {
            clone.WithSuggestion(
                suggestion.Message,
                suggestion.Kind,
                suggestion.Span,
                suggestion.Replacement,
                suggestion.HelpUrl,
                suggestion.Confidence,
                suggestion.RequiresCleanTypes,
                suggestion.OriginalSymbolId);
        }
        foreach (var related in source.Related) clone.WithRelated(related);
        return clone;
    }

    private TargetInfo? ResolveLlvmTargetInfo()
    {
        if (string.IsNullOrWhiteSpace(_options.LlvmTargetTriple))
        {
            return null;
        }

        if (TargetInfo.TryParse(_options.LlvmTargetTriple, out var targetInfo))
        {
            return targetInfo;
        }

        return null;
    }

    #region 模块加载

    private void PreloadImportedModules()
    {
        if (_ast == null)
        {
            return;
        }

        if (!TryGetInputFilePath(out var entryFilePath))
        {
            return;
        }

        ApplyPrecompiledStdRootPackageIdentity(_ast, entryFilePath);

        var knownModuleDeclarations = CollectKnownModuleDeclarations(_ast);
        var loadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            entryFilePath
        };
        var attemptedImports = new HashSet<string>(StringComparer.Ordinal);
        var pendingImports = new Queue<(List<string> path, string? packageAlias, string? parentKey)>();
        var rootModuleKey = ToImportKey(_ast.PackageAlias, GetRootModulePath(_ast));
        EnqueueImports(_ast, pendingImports, null, rootModuleKey);

        // Auto-import Std.Prelude for non-stdlib modules unless disabled.
        if (!_options.NoImplicitPrelude)
        {
            var rootModulePath = GetRootModulePath(_ast);
            var isStdlibModule = IsStdPackageAlias(_ast.PackageAlias) ||
                                 (rootModulePath.Count > 0 &&
                                  string.Equals(rootModulePath[0], "Std", StringComparison.Ordinal));
            if (!isStdlibModule)
            {
                var preludePath = new List<string> { "Prelude" };
                var preludeKey = ToImportKey("Std", preludePath);
                var alreadyImportsPrelude = pendingImports
                    .Any(item => ToImportKey(item.packageAlias, item.path) == preludeKey);
                if (!alreadyImportsPrelude)
                {
                    pendingImports.Enqueue((preludePath, "Std", rootModuleKey));
                }
            }
        }

        var importParentMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(rootModuleKey))
        {
            importParentMap[rootModuleKey] = "<root>";
            _moduleDependencyGraph.RegisterModuleIdentity(entryFilePath, rootModuleKey);
        }

        while (pendingImports.Count > 0)
        {
            var (importPath, packageAlias, parentKey) = pendingImports.Dequeue();
            if (importPath.Count == 0)
            {
                continue;
            }

            var requestedImportKey = ToImportKey(packageAlias, importPath);
            if (knownModuleDeclarations.ContainsKey(requestedImportKey))
            {
                if (string.IsNullOrEmpty(requestedImportKey) || !attemptedImports.Add(requestedImportKey))
                {
                    continue;
                }

                if (importParentMap.ContainsKey(requestedImportKey) && !string.IsNullOrEmpty(parentKey))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(parentKey))
                {
                    importParentMap[requestedImportKey] = parentKey;
                    _moduleDependencyGraph.AddDependency(parentKey, requestedImportKey);
                }

                continue;
            }

            var importCandidates = ResolveImportModuleCandidates(entryFilePath, packageAlias, importPath);
            if (importCandidates.Count == 0)
            {
                AddUnresolvedImportDiagnostic(packageAlias, importPath, entryFilePath);
                continue;
            }

            if (TryAddDuplicateImportCandidateDiagnostic(importPath, importCandidates))
            {
                continue;
            }

            foreach (var importCandidate in importCandidates)
            {
                var effectivePackageAlias = importCandidate.PackageAlias;
                var importKey = ToImportKey(effectivePackageAlias, importPath);
                if (string.IsNullOrEmpty(importKey) || !attemptedImports.Add(importKey))
                {
                    continue;
                }

                if (importParentMap.ContainsKey(importKey) && !string.IsNullOrEmpty(parentKey))
                {
                    if (knownModuleDeclarations.ContainsKey(importKey))
                    {
                        continue;
                    }

                    var chain = BuildImportChain(importKey, parentKey, importParentMap);
                    _diagnostics.Add(Diagnostic.Diagnostic.Error(
                        DiagnosticMessages.CircularImportDetected(chain),
                        "E5001"));
                    continue;
                }

                if (!string.IsNullOrEmpty(parentKey))
                {
                    importParentMap[importKey] = parentKey;
                    _moduleDependencyGraph.AddDependency(parentKey, importKey);
                }

                var effectiveModulePath = BuildEffectiveModulePath(effectivePackageAlias, importPath);
                var effectiveModuleKey = ToImportKey(effectivePackageAlias, importPath);
                if (knownModuleDeclarations.ContainsKey(effectiveModuleKey))
                {
                    continue;
                }

                var resolvedImport = importCandidate.WorkspaceModule;
                var importFile = resolvedImport?.FilePath;
                ModuleDecl? importedRoot;
                List<Diagnostic.Diagnostic> parseDiagnostics;
                bool parseSuccess;

                if (!string.IsNullOrEmpty(importFile))
                {
                    if (!loadedFiles.Add(importFile))
                    {
                        continue;
                    }

                    parseSuccess = TryParseModuleFile(importFile, out importedRoot, out parseDiagnostics);
                    if (parseSuccess)
                    {
                        _moduleDependencyGraph.RegisterModuleIdentity(importFile, effectiveModuleKey);
                    }
                }
                else if (importCandidate.IsPrecompiled &&
                         TryGetPrecompiledModuleSource(effectiveModulePath, out var precompiledSource))
                {
                    var precompiledSourceName = PrecompiledModuleRegistry.TryGetSourceFilePath(effectiveModulePath, out var precompiledSourceFile)
                        ? precompiledSourceFile
                        : $"<precompiled:{importKey}>";
                    _moduleSourceTextCache[NormalizeModuleSourcePath(precompiledSourceName)] = precompiledSource;
                    _moduleSourceTextCache[NormalizeModuleSourcePath($"<precompiled:{importKey}>")] = precompiledSource;
                    AddProfilingCounter("Build.importSourceText.precompiledReads", 1);
                    parseSuccess = TryParsePrecompiledModuleSource(
                        precompiledSource,
                        precompiledSourceName,
                        EidosLanguageVersions.Current,
                        out importedRoot,
                        out parseDiagnostics);
                    if (parseSuccess)
                    {
                        _moduleDependencyGraph.RegisterModuleIdentity(precompiledSourceName, effectiveModuleKey);
                    }
                }
                else
                {
                    continue;
                }

                if (!parseSuccess)
                {
                    _diagnostics.AddRange(parseDiagnostics);
                    continue;
                }

                _diagnostics.AddRange(parseDiagnostics);

                if (!ValidateImportedModuleMatch(importPath, resolvedImport, importedRoot!))
                {
                    continue;
                }

                ApplyPackageIdentityToImportedModuleTree(
                    importedRoot!,
                    effectivePackageAlias,
                    BuildPackageInstanceKey(effectivePackageAlias, importCandidate));

                var hasAddedModule = false;
                foreach (var moduleDecl in importedRoot!.Declarations.OfType<ModuleDecl>())
                {
                    if (!TryRegisterModuleTree(moduleDecl, knownModuleDeclarations))
                    {
                        continue;
                    }

                    _ast.Declarations.Add(moduleDecl);
                    EnqueueImports(moduleDecl, pendingImports, effectivePackageAlias, importKey);
                    hasAddedModule = true;
                }

                if (!hasAddedModule)
                {
                    EnqueueImports(importedRoot, pendingImports, effectivePackageAlias, importKey);
                }
            }
        }

        _moduleGraphSnapshot = ProjectModuleGraphSnapshot.FromDependencyGraph(_moduleDependencyGraph);
        _moduleBuildSchedule = ProjectModuleBuildSchedule.FromGraphSnapshot(_moduleGraphSnapshot);
        _moduleSignatureSnapshot = ProjectModuleSignatureSnapshot.FromGraphSnapshot(
            _moduleGraphSnapshot,
            GetModuleSignatureSourceText,
            _options.LanguageVersion,
            CreateModuleSignatureFlagsHash());
        SetProfilingCounter("Build.moduleSignatures.sourceHashModules", _moduleGraphSnapshot.Nodes.Count);
        SetProfilingCounter(
            "Build.moduleSignatures.sourceHashParallelEnabled",
            _moduleGraphSnapshot.Nodes.Count >= 4 ? 1 : 0);
        _moduleSemanticSignatureSnapshot = ProjectModuleSemanticSignatureSnapshot.FromGraphSnapshot(
            _moduleGraphSnapshot,
            CollectModuleDeclarationsForSemanticSignature(),
            _options.LanguageVersion,
            CreateModuleSignatureFlagsHash());
        _moduleInvalidationPlan = ProjectModuleInvalidationPlan.FromSemanticSignatures(
            _options.PreviousModuleSemanticSignatureSnapshot,
            _moduleSemanticSignatureSnapshot);
        _moduleInvalidationPlan = ExpandInvalidationToCompilationUnits(
            _moduleInvalidationPlan,
            _moduleGraphSnapshot);
        _moduleExecutionPlan = ProjectModuleExecutionPlan.FromSchedule(
            _moduleBuildSchedule,
            _moduleInvalidationPlan,
            ProjectModuleExecutionPlan.IsPrecompiledReadyArtifact);
        _moduleArtifactReadinessPlan = CreateArtifactReadinessPlan(
            _moduleExecutionPlan,
            ProjectModuleArtifactRequirement.SemanticOnly);
        SetProfilingCounter("Build.moduleGraph.modules", _moduleGraphSnapshot.Nodes.Count);
        SetProfilingCounter("Build.moduleGraph.topologicalLayers", _moduleGraphSnapshot.TopologicalLayers.Count);
        SetProfilingCounter("Build.moduleSchedule.layers", _moduleBuildSchedule.Layers.Count);
        SetProfilingCounter("Build.moduleSchedule.maxParallelWidth", _moduleBuildSchedule.MaxParallelWidth);
        SetProfilingCounter(
            "Build.moduleGraph.edges",
            _moduleGraphSnapshot.Nodes.Sum(static node => node.Dependencies.Count));
        SetProfilingCounter("Build.moduleSignatures.modules", _moduleSignatureSnapshot.Nodes.Count);
        SetProfilingCounter("Build.moduleSemanticSignatures.modules", _moduleSemanticSignatureSnapshot.Nodes.Count);
        SetProfilingCounter(
            "Build.moduleSemanticSignatures.declarations",
            _moduleSemanticSignatureSnapshot.Nodes.Sum(static node => node.Declarations.Count));
        BuildModuleDependencySignatureSnapshot(CompilationPhase.Namer, "Build.moduleDependencySignatures");
        SetProfilingCounter("Build.moduleInvalidation.changes", _moduleInvalidationPlan.Changes.Count);
        SetProfilingCounter("Build.moduleInvalidation.affected", _moduleInvalidationPlan.AffectedModules.Count);
        SetProfilingCounter("Build.moduleInvalidation.unchanged", _moduleInvalidationPlan.UnchangedModules.Count);
        SetModuleExecutionPlanCounters("Build.moduleExecution", _moduleExecutionPlan);
        _moduleParallelExecution = CreateModuleParallelExecutionSnapshot(
            CompilationPhase.Namer,
            "moduleParallelExecution",
            _moduleExecutionPlan);
        if (_moduleArtifactReadinessPlan != null)
        {
            SetModuleArtifactReadinessCounters("Build.moduleArtifactReadiness", _moduleArtifactReadinessPlan);
            _moduleArtifactRestorePlan = ProjectModuleArtifactRestorePlan.FromExecutionAndReadiness(
                _moduleExecutionPlan,
                _moduleArtifactReadinessPlan,
                ProjectModuleArtifactRequirement.SemanticOnly);
            _moduleArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
                _moduleArtifactRestorePlan,
                ProjectModuleDependencySignatureRequirement.SemanticOnly);
            SetModuleArtifactRestoreCounters("Build.moduleArtifactRestore", _moduleArtifactRestorePlan);
            _moduleArtifactRestoreExecution = ProjectModuleArtifactRestoreExecutor.Execute(
                _moduleArtifactRestorePlan,
                currentDependencySignatures: _moduleDependencySignatureSnapshot,
                previousDependencySignatures: _options.PreviousModuleDependencySignatureSnapshot,
                dependencyRequirement: ProjectModuleDependencySignatureRequirement.SemanticOnly);
            SetModuleArtifactRestoreExecutionCounters(
                "Build.moduleArtifactRestoreExecution",
                _moduleArtifactRestoreExecution);
            SetModuleStageExecutionCounters(
                "Namer",
                _moduleArtifactRestoreExecution,
                hasRestorePayload: false);
            TryLoadModuleArtifactRestorePayload();
        }
    }

    private void SetModuleExecutionPlanCounters(string prefix, ProjectModuleExecutionPlan plan)
    {
        SetProfilingCounter($"{prefix}.modules", plan.TotalModules);
        SetProfilingCounter($"{prefix}.compileModules", plan.CompileModules);
        SetProfilingCounter($"{prefix}.restoreModules", plan.RestoreModules);
        SetProfilingCounter($"{prefix}.readyArtifactModules", plan.ReadyArtifactModules);
        SetProfilingCounter($"{prefix}.maxCompileParallelWidth", plan.MaxCompileParallelWidth);
        SetProfilingCounter($"{prefix}.maxRestoreParallelWidth", plan.MaxRestoreParallelWidth);
        SetProfilingCounter($"{prefix}.maxReadyArtifactParallelWidth", plan.MaxReadyArtifactParallelWidth);
        SetProfilingCounter($"{prefix}.layersWithCompile", plan.Layers.Count(static layer => layer.CompileCount > 0));
        SetProfilingCounter($"{prefix}.layersWithRestore", plan.Layers.Count(static layer => layer.RestoreCount > 0));
        SetProfilingCounter($"{prefix}.layersWithReadyArtifact", plan.Layers.Count(static layer => layer.ReadyArtifactCount > 0));
    }

    private void BuildModuleDependencySignatureSnapshot(CompilationPhase phase, string prefix)
    {
        if (!_options.EnableDetailedProfiling || _moduleGraphSnapshot == null)
        {
            return;
        }

        using (MeasureSubphase(phase, "module_dependency_signature_snapshot"))
        {
            _moduleDependencySignatureSnapshot = ProjectModuleDependencySignatureSnapshot.Create(
                _moduleGraphSnapshot,
                _moduleSemanticSignatureSnapshot,
                _moduleTypedSemanticSnapshot,
                _moduleMemberIndexSnapshot,
                _moduleMirArtifactSnapshot,
                _moduleSignatureSnapshot,
                _implOverlapCheckSnapshot);
        }

        SetProfilingCounter($"{prefix}.modules", _moduleDependencySignatureSnapshot.Nodes.Count);
        SetProfilingCounter(
            $"{prefix}.sourceAvailableModules",
            _moduleDependencySignatureSnapshot.Nodes.Count(static node => node.SourceAvailable));
        SetProfilingCounter(
            $"{prefix}.semanticAvailableModules",
            _moduleDependencySignatureSnapshot.Nodes.Count(static node => node.SemanticAvailable));
        SetProfilingCounter(
            $"{prefix}.typedAvailableModules",
            _moduleDependencySignatureSnapshot.Nodes.Count(static node => node.TypedAvailable));
        SetProfilingCounter(
            $"{prefix}.memberIndexAvailableModules",
            _moduleDependencySignatureSnapshot.Nodes.Count(static node => node.MemberIndexAvailable));
        SetProfilingCounter(
            $"{prefix}.mirAvailableModules",
            _moduleDependencySignatureSnapshot.Nodes.Count(static node => node.MirAvailable));
    }

    private ProjectModuleArtifactRestorePlan GateModuleArtifactRestorePlanWithDependencySignatures(
        ProjectModuleArtifactRestorePlan plan,
        ProjectModuleDependencySignatureRequirement requirement)
    {
        return plan.GateWithDependencySignatures(
            _moduleDependencySignatureSnapshot,
            _options.PreviousModuleDependencySignatureSnapshot,
            requirement);
    }

    private ProjectModuleParallelExecutionSnapshot CreateModuleParallelExecutionSnapshot(
        CompilationPhase phase,
        string prefix,
        ProjectModuleExecutionPlan plan)
    {
        using (MeasureSubphase(phase, $"{prefix}.module_task_execution"))
        {
            var maxDegreeOfParallelism = Math.Min(
                ResolveMaxDegreeOfParallelism(),
                Math.Max(1, plan.Layers.Count == 0
                ? 1
                : plan.Layers.Max(static layer => layer.Modules.Count)));
            var executor = new ProjectModuleParallelExecutor(maxDegreeOfParallelism);
            var snapshot = executor.ExecuteAsync(
                    plan,
                    static (item, _) => ValueTask.FromResult(item.Action == ProjectModuleExecutionAction.ReadyArtifact
                        ? ProjectModuleExecutionItemResult.Skipped("ready-artifact")
                        : ProjectModuleExecutionItemResult.Completed))
                .GetAwaiter()
                .GetResult();
            SetModuleParallelExecutionCounters($"Build.{prefix}", snapshot);
            SetProfilingCounter($"Build.{prefix}.hasRealTaskExecution", 1);
            return snapshot;
        }
    }

    private void SetModuleParallelExecutionCounters(
        string prefix,
        ProjectModuleParallelExecutionSnapshot snapshot)
    {
        SetProfilingCounter($"{prefix}.modules", snapshot.TotalModules);
        SetProfilingCounter($"{prefix}.completedModules", snapshot.CompletedModules);
        SetProfilingCounter($"{prefix}.failedModules", snapshot.FailedModules);
        SetProfilingCounter($"{prefix}.skippedModules", snapshot.SkippedModules);
        SetProfilingCounter($"{prefix}.maxObservedParallelism", snapshot.MaxObservedParallelism);
        SetProfilingCounter($"{prefix}.maxDegreeOfParallelism", snapshot.MaxDegreeOfParallelism);
    }

    private ProjectModuleArtifactReadinessPlan? CreateArtifactReadinessPlan(
        ProjectModuleExecutionPlan plan,
        ProjectModuleArtifactRequirement requirement = ProjectModuleArtifactRequirement.SemanticTypedAndMir)
    {
        return _options.ModuleArtifactAvailability == null
            ? null
            : ProjectModuleArtifactReadinessPlan.FromExecutionPlan(
                plan,
                _moduleSemanticSignatureSnapshot,
                _moduleTypedSemanticSnapshot,
                _moduleMirArtifactSnapshot,
                _options.ModuleArtifactAvailability,
                requirement);
    }

    private void SetModuleArtifactReadinessCounters(string prefix, ProjectModuleArtifactReadinessPlan plan)
    {
        SetProfilingCounter($"{prefix}.modules", plan.TotalModules);
        SetProfilingCounter($"{prefix}.compileModules", plan.CompileModules);
        SetProfilingCounter($"{prefix}.restoreModules", plan.RestoreModules);
        SetProfilingCounter($"{prefix}.readyArtifactModules", plan.ReadyArtifactModules);
        SetProfilingCounter($"{prefix}.semanticReadyModules", plan.SemanticReadyModules);
        SetProfilingCounter($"{prefix}.semanticMissingModules", plan.SemanticMissingModules);
        SetProfilingCounter($"{prefix}.typedSemanticReadyModules", plan.TypedSemanticReadyModules);
        SetProfilingCounter($"{prefix}.typedSemanticMissingModules", plan.TypedSemanticMissingModules);
        SetProfilingCounter($"{prefix}.mirReadyModules", plan.MirReadyModules);
        SetProfilingCounter($"{prefix}.mirMissingModules", plan.MirMissingModules);
    }

    private void SetModuleArtifactRestoreCounters(string prefix, ProjectModuleArtifactRestorePlan plan)
    {
        SetProfilingCounter($"{prefix}.modules", plan.TotalModules);
        SetProfilingCounter($"{prefix}.restoreModules", plan.RestoreModules);
        SetProfilingCounter($"{prefix}.blockedModules", plan.BlockedModules);
        SetProfilingCounter($"{prefix}.readyArtifactModules", plan.ReadyArtifactModules);
        SetProfilingCounter($"{prefix}.compileModules", plan.CompileModules);
        SetProfilingCounter($"{prefix}.maxRestoreParallelWidth", plan.MaxRestoreParallelWidth);
        SetProfilingCounter($"{prefix}.maxCompileParallelWidth", plan.MaxCompileParallelWidth);
    }

    private void SetModuleArtifactRestoreExecutionCounters(
        string prefix,
        ProjectModuleArtifactRestoreExecutionSnapshot snapshot)
    {
        SetProfilingCounter($"{prefix}.modules", snapshot.TotalModules);
        SetProfilingCounter($"{prefix}.restoredModules", snapshot.RestoredModules);
        SetProfilingCounter($"{prefix}.blockedModules", snapshot.BlockedModules);
        SetProfilingCounter($"{prefix}.compiledModules", snapshot.CompiledModules);
        SetProfilingCounter($"{prefix}.readyArtifactModules", snapshot.ReadyArtifactModules);
        SetProfilingCounter($"{prefix}.maxRestoredParallelWidth", snapshot.MaxRestoredParallelWidth);
        SetProfilingCounter($"{prefix}.maxCompiledParallelWidth", snapshot.MaxCompiledParallelWidth);
        SetProfilingCounter($"{prefix}.failedModules", snapshot.FailedModules);
        SetProfilingCounter($"{prefix}.skippedModules", snapshot.SkippedModules);
        SetProfilingCounter($"{prefix}.realTaskExecution", snapshot.HasRealTaskExecution ? 1 : 0);
        SetProfilingCounter($"{prefix}.maxObservedParallelism", snapshot.MaxObservedParallelism);
        SetProfilingCounter($"{prefix}.maxDegreeOfParallelism", snapshot.MaxDegreeOfParallelism);
    }

    private void TryLoadModuleArtifactRestorePayload()
    {
        if (_moduleArtifactRestorePlan == null ||
            _moduleSemanticSignatureSnapshot == null ||
            _options.ModuleSemanticArtifactLoader == null)
        {
            return;
        }

        using (MeasureSubphase(CompilationPhase.Namer, "module_artifact_restore_payload"))
        {
            _moduleArtifactRestorePayload = ProjectModuleArtifactRestorePayloadSnapshot.LoadSemantic(
                _moduleArtifactRestorePlan,
                _moduleSemanticSignatureSnapshot,
                _options.ModuleSemanticArtifactLoader);
        }

        _moduleArtifactRestorePlan = _moduleArtifactRestorePlan.GateWithPayload(
            _moduleArtifactRestorePayload);
        _moduleArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
            _moduleArtifactRestorePlan,
            ProjectModuleDependencySignatureRequirement.SemanticOnly);
        _moduleArtifactRestoreExecution = ExecuteModuleArtifactRestorePlan(
            _moduleArtifactRestorePlan,
            _moduleArtifactRestorePayload,
            ProjectModuleDependencySignatureRequirement.SemanticOnly);
        SetModuleArtifactRestoreCounters(
            "Build.moduleArtifactRestore",
            _moduleArtifactRestorePlan);
        SetModuleArtifactRestoreExecutionCounters(
            "Build.moduleArtifactRestoreExecution",
            _moduleArtifactRestoreExecution);
        SetModuleStageExecutionCounters(
            "Namer",
            _moduleArtifactRestoreExecution,
            hasRestorePayload: true);
        SetModuleArtifactRestorePayloadCounters(
            "Build.moduleArtifactRestorePayload",
            _moduleArtifactRestorePayload);
    }

    private void TryLoadModuleTypedArtifactRestorePayload()
    {
        if (_moduleTypedArtifactRestorePlan == null ||
            _moduleSemanticSignatureSnapshot == null ||
            _moduleTypedSemanticSnapshot == null ||
            _moduleMirArtifactSnapshot == null ||
            _options.ModuleSemanticArtifactLoader == null ||
            _options.ModuleTypedSemanticArtifactLoader == null ||
            _options.ModuleMirArtifactLoader == null)
        {
            return;
        }

        using (MeasureSubphase(CompilationPhase.Mir, "module_typed_artifact_restore_payload"))
        {
            _moduleTypedArtifactRestorePayload = ProjectModuleArtifactRestorePayloadSnapshot.Load(
                _moduleTypedArtifactRestorePlan,
                _moduleSemanticSignatureSnapshot,
                _moduleTypedSemanticSnapshot,
                _moduleMirArtifactSnapshot,
                _options.ModuleSemanticArtifactLoader,
                _options.ModuleTypedSemanticArtifactLoader,
                _options.ModuleMirArtifactLoader);
        }

        _moduleTypedArtifactRestorePlan = _moduleTypedArtifactRestorePlan.GateWithPayload(
            _moduleTypedArtifactRestorePayload);
        _moduleTypedArtifactRestorePlan = GateModuleArtifactRestorePlanWithDependencySignatures(
            _moduleTypedArtifactRestorePlan,
            ProjectModuleDependencySignatureRequirement.SemanticTypedMemberAndMir);
        _moduleTypedArtifactRestoreExecution = ExecuteModuleArtifactRestorePlan(
            _moduleTypedArtifactRestorePlan,
            _moduleTypedArtifactRestorePayload,
            ProjectModuleDependencySignatureRequirement.SemanticTypedMemberAndMir);
        SetModuleArtifactRestoreCounters(
            "Build.moduleTypedArtifactRestore",
            _moduleTypedArtifactRestorePlan);
        SetModuleArtifactRestoreExecutionCounters(
            "Build.moduleTypedArtifactRestoreExecution",
            _moduleTypedArtifactRestoreExecution);
        SetModuleStageExecutionCounters(
            "Types",
            _moduleTypedArtifactRestoreExecution,
            hasRestorePayload: true);
        SetModuleStageExecutionCounters(
            "Hir",
            _moduleTypedArtifactRestoreExecution,
            hasRestorePayload: true);
        SetModuleStageExecutionCounters(
            "Mir",
            _moduleTypedArtifactRestoreExecution,
            hasRestorePayload: true);
        SetModuleArtifactRestorePayloadCounters(
            "Build.moduleTypedArtifactRestorePayload",
            _moduleTypedArtifactRestorePayload);
    }

    private void SetModuleArtifactRestorePayloadCounters(
        string prefix,
        ProjectModuleArtifactRestorePayloadSnapshot payload)
    {
        SetProfilingCounter($"{prefix}.restoreModules", payload.RestoreModules);
        SetProfilingCounter($"{prefix}.loadedModules", payload.LoadedModules);
        SetProfilingCounter($"{prefix}.validatedModules", payload.ValidatedModules);
        SetProfilingCounter($"{prefix}.staleModules", payload.StaleModules);
        SetProfilingCounter($"{prefix}.missingModules", payload.MissingModules);
        SetProfilingCounter($"{prefix}.failedModules", payload.FailedModules);
    }

    private ProjectModuleArtifactRestoreExecutionSnapshot ExecuteModuleArtifactRestorePlan(
        ProjectModuleArtifactRestorePlan plan,
        ProjectModuleArtifactRestorePayloadSnapshot? payload,
        ProjectModuleDependencySignatureRequirement dependencyRequirement)
    {
        return ProjectModuleArtifactRestoreExecutor.ExecuteAsync(
                plan,
                RestoreValidatedModuleArtifactAsync,
                CompileCurrentModuleArtifactAsync,
                payload,
                _moduleDependencySignatureSnapshot,
                _options.PreviousModuleDependencySignatureSnapshot,
                dependencyRequirement,
                maxDegreeOfParallelism: GetModuleArtifactRestoreMaxDegreeOfParallelism(plan))
            .GetAwaiter()
            .GetResult();
    }

    private int GetModuleArtifactRestoreMaxDegreeOfParallelism(ProjectModuleArtifactRestorePlan plan)
    {
        if (plan.Layers.Count == 0)
        {
            return 1;
        }

        var maxLayerWidth = plan.Layers.Max(static layer => layer.Modules.Count);
        return Math.Max(1, Math.Min(ResolveMaxDegreeOfParallelism(), maxLayerWidth));
    }

    private int ResolveMaxDegreeOfParallelism() =>
        _options.MaxDegreeOfParallelism > 0
            ? _options.MaxDegreeOfParallelism
            : Math.Max(1, Environment.ProcessorCount);

    private static ValueTask<ProjectModuleExecutionItemResult> RestoreValidatedModuleArtifactAsync(
        ProjectModuleArtifactRestoreItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
    }

    private static ValueTask<ProjectModuleExecutionItemResult> CompileCurrentModuleArtifactAsync(
        ProjectModuleArtifactRestoreItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ProjectModuleExecutionItemResult.Completed);
    }

    private void BuildModuleMemberIndexSnapshot()
    {
        if (!_options.EnableDetailedProfiling || _symbolTable == null)
        {
            return;
        }

        using (MeasureSubphase(CompilationPhase.Namer, "module_member_index_snapshot"))
        {
            _moduleMemberIndexSnapshot = ProjectModuleMemberIndexSnapshot.FromSymbolTable(
                _symbolTable,
                _moduleGraphSnapshot);
        }

        AddModuleMemberIndexRestorePlanCounters();
        SetProfilingCounter("Namer.moduleMemberIndex.modules", _moduleMemberIndexSnapshot.Nodes.Count);
        SetProfilingCounter(
            "Namer.moduleMemberIndex.members",
            _moduleMemberIndexSnapshot.Nodes.Sum(static node => node.Members.Count));
        SetProfilingCounter(
            "Namer.moduleMemberIndex.exports",
            _moduleMemberIndexSnapshot.Nodes.Sum(static node => node.Exports.Count));
        SetProfilingCounter(
            "Namer.moduleMemberIndex.accessibleBindings",
            _moduleMemberIndexSnapshot.Nodes.Sum(static node => node.AccessibleBindings.Count));
    }

    private void AddModuleMemberIndexRestorePlanCounters()
    {
        if (_moduleMemberIndexSnapshot == null)
        {
            return;
        }

        var previous = _options.PreviousModuleMemberIndexSnapshot;
        _moduleMemberIndexRestorePlan = ProjectModuleMemberIndexRestorePlan.Create(
            previous,
            _moduleMemberIndexSnapshot);
        if (previous == null)
        {
            SetProfilingCounter("Namer.moduleMemberIndexPrevious.available", 0);
            SetProfilingCounter("Namer.moduleMemberIndexRestorePlan.available", 0);
            SetProfilingCounter("Namer.moduleMemberIndexRestorePlan.addedModules", _moduleMemberIndexRestorePlan.AddedModules);
            return;
        }

        _moduleMemberIndexRestorePayload = ProjectModuleMemberIndexRestorePayloadSnapshot.Load(
            _moduleMemberIndexRestorePlan,
            previous);
        _moduleMemberIndexRestorePlan = _moduleMemberIndexRestorePlan.GateWithPayload(
            _moduleMemberIndexRestorePayload);
        SetProfilingCounter("Namer.moduleMemberIndexPrevious.available", 1);
        SetProfilingCounter("Namer.moduleMemberIndexPrevious.unchangedModules", _moduleMemberIndexRestorePlan.RestoreModules);
        SetProfilingCounter("Namer.moduleMemberIndexPrevious.changedModules", _moduleMemberIndexRestorePlan.RebuildModules);
        SetProfilingCounter("Namer.moduleMemberIndexPrevious.addedModules", _moduleMemberIndexRestorePlan.AddedModules);
        SetProfilingCounter("Namer.moduleMemberIndexPrevious.removedModules", _moduleMemberIndexRestorePlan.RemovedModules);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePlan.available", 1);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePlan.modules", _moduleMemberIndexRestorePlan.TotalModules);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePlan.restoreModules", _moduleMemberIndexRestorePlan.RestoreModules);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePlan.rebuildModules", _moduleMemberIndexRestorePlan.RebuildModules);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePlan.addedModules", _moduleMemberIndexRestorePlan.AddedModules);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePlan.removedModules", _moduleMemberIndexRestorePlan.RemovedModules);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePayload.restoreModules", _moduleMemberIndexRestorePayload.RestoreModules);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePayload.loadedModules", _moduleMemberIndexRestorePayload.LoadedModules);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePayload.validatedModules", _moduleMemberIndexRestorePayload.ValidatedModules);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePayload.staleModules", _moduleMemberIndexRestorePayload.StaleModules);
        SetProfilingCounter("Namer.moduleMemberIndexRestorePayload.missingModules", _moduleMemberIndexRestorePayload.MissingModules);
    }

    private IReadOnlyDictionary<string, ModuleDecl> CollectModuleDeclarationsForSemanticSignature()
    {
        if (_ast == null)
        {
            return new Dictionary<string, ModuleDecl>(StringComparer.Ordinal);
        }

        return EnumerateModuleTree(_ast)
            .Select(static module => (Key: ToModuleDeclKey(module), Module: module))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(static item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => SelectSemanticSignatureModuleDeclaration(group.Select(static item => item.Module)),
                StringComparer.Ordinal);
    }

    private static ModuleDecl SelectSemanticSignatureModuleDeclaration(IEnumerable<ModuleDecl> candidates)
    {
        return candidates
            .OrderByDescending(static module => module.Declarations.Count(static declaration => declaration is not ModuleDecl))
            .ThenByDescending(static module => module.UsesExplicitExports)
            .ThenByDescending(static module => module.Declarations.Count)
            .First();
    }

    private string? GetModuleSignatureSourceText(string sourcePath)
    {
        var normalizedSourcePath = NormalizeModuleSourcePath(sourcePath);
        if (string.Equals(
                normalizedSourcePath,
                NormalizeModuleSourcePath(_options.InputFile),
                StringComparison.OrdinalIgnoreCase))
        {
            _moduleSourceTextCache[normalizedSourcePath] = _sourceCode;
            return _sourceCode;
        }

        if (_moduleSourceTextCache.TryGetValue(normalizedSourcePath, out var cachedSource))
        {
            AddProfilingCounter("Build.moduleSignatureSourceText.cacheHits", 1);
            return cachedSource;
        }

        if (sourcePath.StartsWith("<precompiled:", StringComparison.Ordinal) &&
            sourcePath.EndsWith('>'))
        {
            var moduleKey = sourcePath["<precompiled:".Length..^1];
            if (PrecompiledModuleRegistry.TryGetSource(moduleKey, out var source))
            {
                _moduleSourceTextCache[normalizedSourcePath] = source;
                AddProfilingCounter("Build.moduleSignatureSourceText.registryHits", 1);
                return source;
            }

            AddProfilingCounter("Build.moduleSignatureSourceText.misses", 1);
            return null;
        }

        if (!File.Exists(sourcePath))
        {
            AddProfilingCounter("Build.moduleSignatureSourceText.misses", 1);
            return null;
        }

        var sourceText = File.ReadAllText(sourcePath);
        _moduleSourceTextCache[normalizedSourcePath] = sourceText;
        AddProfilingCounter("Build.moduleSignatureSourceText.fileReads", 1);
        return sourceText;
    }

    private static string NormalizeModuleSourcePath(string sourcePath)
    {
        return SourcePathNormalizer.Normalize(sourcePath);
    }

    private string CreateModuleSignatureFlagsHash()
    {
        return ModuleArtifactHash.ComputeFlagsHash([
            $"syntax={_options.LanguageVersion}",
            $"target={_options.Target}",
            $"stop={_options.StopAtPhase?.ToString() ?? ""}",
            $"noImplicitPrelude={_options.NoImplicitPrelude}",
            $"mirOpt={_options.EnableMirOptimizations}",
            $"comptimeFuel={_options.ComptimeFuelBudget}",
            $"comptimeBytes={_options.ComptimeAllocatedValueBytesBudget}",
            $"comptimeDiagnostics={_options.ComptimeDiagnosticBudget}",
            $"triple={_options.LlvmTargetTriple ?? ""}",
            $"nativeLinkMode={_options.NativeLinkMode}",
            $"stdlib={PrecompiledModuleRegistry.GetStdlibImageFingerprint()}"
        ]);
    }

    private void ApplyPrecompiledStdRootPackageIdentity(ModuleDecl moduleDecl, string entryFilePath)
    {
        if (!IsPrecompiledStdInputPath(entryFilePath))
        {
            ApplyPackageInstanceKeyToModuleTree(moduleDecl, BuildCurrentPackageInstanceKey(entryFilePath));
            return;
        }

        ApplyPackageIdentityToImportedModuleTree(moduleDecl, "Std", "precompiled:Std");
    }

    private static void EnqueueImports(ModuleDecl moduleDecl, Queue<List<string>> queue)
    {
        foreach (var import in EnumerateImports(moduleDecl))
        {
            if (import.ModulePath.Count == 0)
            {
                continue;
            }

            queue.Enqueue(new List<string>(import.ModulePath));
        }
    }

    private static IEnumerable<ImportDecl> EnumerateImports(ModuleDecl moduleDecl)
    {
        foreach (var decl in moduleDecl.Declarations)
        {
            if (decl is ImportDecl import)
            {
                yield return import;
                continue;
            }

            if (decl is ModuleDecl childModule)
            {
                foreach (var childImport in EnumerateImports(childModule))
                {
                    yield return childImport;
                }
            }
        }
    }

    private void EnqueueImports(
        ModuleDecl moduleDecl,
        Queue<(List<string> path, string? packageAlias, string? parentKey)> queue,
        string? ambientPackageAlias,
        string? parentKey)
    {
        foreach (var import in EnumerateImports(moduleDecl))
        {
            if (import.ModulePath.Count == 0)
            {
                continue;
            }

            NormalizeDotNamespaceImport(import);
            var packageAlias = ResolveInheritedPackageAlias(import, ambientPackageAlias);
            queue.Enqueue((new List<string>(import.ModulePath), packageAlias, parentKey));
        }
    }

    private void NormalizeDotNamespaceImport(ImportDecl import)
    {
        if (!string.IsNullOrWhiteSpace(import.PackageAlias) || import.ModulePath.Count < 2)
        {
            return;
        }

        var namespaceRoot = import.ModulePath[0];
        if (!IsStdPackageAlias(namespaceRoot) && !_options.PackageImportRoots.ContainsKey(namespaceRoot))
        {
            return;
        }

        import.SetPackageAlias(namespaceRoot);
        import.SetModulePath(import.ModulePath.Skip(1).ToList());
    }

    private void ApplyPackageIdentityToImportedModuleTree(
        ModuleDecl moduleDecl,
        string? packageAlias,
        string? packageInstanceKey)
    {
        if (string.IsNullOrWhiteSpace(packageAlias))
        {
            ApplyPackageInstanceKeyToModuleTree(moduleDecl, packageInstanceKey);
            return;
        }

        foreach (var module in EnumerateModuleTree(moduleDecl))
        {
            module.SetPackageAlias(packageAlias);
            module.SetPackageInstanceKey(packageInstanceKey);

            foreach (var import in module.Declarations.OfType<ImportDecl>())
            {
                NormalizeDotNamespaceImport(import);
                if (ShouldInheritPackageAlias(import, packageAlias))
                {
                    import.SetPackageAlias(packageAlias);
                }
            }
        }
    }

    private static void ApplyPackageInstanceKeyToModuleTree(ModuleDecl moduleDecl, string? packageInstanceKey)
    {
        foreach (var module in EnumerateModuleTree(moduleDecl))
        {
            module.SetPackageInstanceKey(packageInstanceKey);
        }
    }

    private static string? ResolveInheritedPackageAlias(ImportDecl import, string? ambientPackageAlias)
    {
        if (!string.IsNullOrWhiteSpace(import.PackageAlias))
        {
            return import.PackageAlias;
        }

        return ShouldInheritPackageAlias(import, ambientPackageAlias)
            ? ambientPackageAlias
            : null;
    }

    private static bool ShouldInheritPackageAlias(ImportDecl import, string? ambientPackageAlias)
    {
        return !string.IsNullOrWhiteSpace(ambientPackageAlias) &&
               string.IsNullOrWhiteSpace(import.PackageAlias);
    }

    private static bool IsStdPackageAlias(string? packageAlias)
    {
        return string.Equals(packageAlias, "Std", StringComparison.Ordinal);
    }

    private string BuildCurrentPackageInstanceKey()
    {
        var entryFilePath = GetPrimarySourceName();
        if (string.IsNullOrWhiteSpace(entryFilePath) ||
            entryFilePath.StartsWith("<", StringComparison.Ordinal))
        {
            return ModuleIdentity.CurrentPackageInstanceKey;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(entryFilePath));
            return string.IsNullOrWhiteSpace(directory)
                ? ModuleIdentity.CurrentPackageInstanceKey
                : NormalizePackageInstanceRoot(directory);
        }
        catch
        {
            return ModuleIdentity.CurrentPackageInstanceKey;
        }
    }

    private static string BuildCurrentPackageInstanceKey(string entryFilePath)
    {
        if (string.IsNullOrWhiteSpace(entryFilePath) ||
            entryFilePath.StartsWith("<", StringComparison.Ordinal))
        {
            return ModuleIdentity.CurrentPackageInstanceKey;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(entryFilePath));
            return string.IsNullOrWhiteSpace(directory)
                ? ModuleIdentity.CurrentPackageInstanceKey
                : NormalizePackageInstanceRoot(directory);
        }
        catch
        {
            return ModuleIdentity.CurrentPackageInstanceKey;
        }
    }

    private static bool IsPrecompiledStdInputPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return Eidosc.Semantic.PrecompiledModuleRegistry.IsStdlibSourcePath(filePath);
    }

    private static string BuildPackageInstanceKey(string? packageAlias, ImportModuleCandidate importCandidate)
    {
        if (importCandidate.IsPrecompiled)
        {
            return $"precompiled:{packageAlias ?? ModuleIdentity.CurrentPackageInstanceKey}";
        }

        return string.IsNullOrWhiteSpace(importCandidate.WorkspaceModule?.RootDirectory)
            ? ModuleIdentity.CurrentPackageInstanceKey
            : NormalizePackageInstanceRoot(importCandidate.WorkspaceModule.RootDirectory);
    }

    private static string NormalizePackageInstanceRoot(string rootDirectory)
    {
        return Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
    }

    private static List<string> GetRootModulePath(ModuleDecl ast)
    {
        var path = new List<string>();
        if (ast.Path.Count > 0)
        {
            path.AddRange(ast.Path);
        }

        return path;
    }

    private static string BuildImportChain(
        string targetKey,
        string parentKey,
        Dictionary<string, string> parentMap)
    {
        var chain = new List<string> { targetKey };
        var current = parentKey;
        var visited = new HashSet<string>(StringComparer.Ordinal) { targetKey, parentKey };

        while (!string.IsNullOrEmpty(current) && current != "<root>")
        {
            chain.Add(current);
            if (!parentMap.TryGetValue(current, out var next))
            {
                break;
            }

            if (!visited.Add(next))
            {
                break;
            }

            current = next;
        }

        chain.Reverse();
        return string.Join(" -> ", chain);
    }

    private Dictionary<string, ModuleDecl> CollectKnownModuleDeclarations(ModuleDecl moduleDecl)
    {
        var result = new Dictionary<string, ModuleDecl>(StringComparer.Ordinal);
        foreach (var topLevelModule in moduleDecl.Declarations.OfType<ModuleDecl>())
        {
            RegisterModuleTree(topLevelModule, result);
        }

        return result;
    }

    private static IEnumerable<ModuleDecl> EnumerateModules(ModuleDecl moduleDecl)
    {
        foreach (var decl in moduleDecl.Declarations)
        {
            if (decl is not ModuleDecl childModule)
            {
                continue;
            }

            yield return childModule;
            foreach (var nested in EnumerateModules(childModule))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<ModuleDecl> EnumerateModuleTree(ModuleDecl moduleDecl)
    {
        yield return moduleDecl;

        foreach (var nestedModule in EnumerateModules(moduleDecl))
        {
            yield return nestedModule;
        }
    }

    private static string ToModulePathKey(IReadOnlyList<string> modulePath)
    {
        return string.Join(WellKnownStrings.Operators.Divide, modulePath.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string ToModuleDeclKey(ModuleDecl moduleDecl)
    {
        return ModuleRegistry.ToModuleKey(moduleDecl.PackageAlias, moduleDecl.Path);
    }

    private static string ToImportKey(string? packageAlias, IReadOnlyList<string> modulePath)
    {
        return ModuleRegistry.ToModuleKey(packageAlias, modulePath);
    }

    private static List<string> BuildEffectiveModulePath(string? packageAlias, IReadOnlyList<string> modulePath)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(packageAlias))
        {
            result.Add(packageAlias);
        }

        result.AddRange(modulePath);
        return result;
    }

    private ResolvedWorkspaceModuleFile? ResolveImportModule(
        string entryFilePath,
        string? packageAlias,
        IReadOnlyList<string> modulePath)
    {
        if (!string.IsNullOrWhiteSpace(packageAlias))
        {
            return _options.PackageImportRoots.TryGetValue(packageAlias, out var packageRoots)
                ? WorkspaceModuleLocator.ResolveImportModuleFromRoots(modulePath, packageRoots)
                : null;
        }

        return WorkspaceModuleLocator.ResolveImportModule(entryFilePath, modulePath, _options.ImportSearchRoots);
    }

    private List<ImportModuleCandidate> ResolveImportModuleCandidates(
        string entryFilePath,
        string? packageAlias,
        IReadOnlyList<string> modulePath)
    {
        if (!string.IsNullOrWhiteSpace(packageAlias))
        {
            var explicitModules = _options.PackageImportRoots.TryGetValue(packageAlias, out var packageRoots)
                ? WorkspaceModuleLocator.ResolveImportModuleCandidatesFromRoots(modulePath, packageRoots)
                : [];
            if (explicitModules.Count > 0)
            {
                return explicitModules
                    .Select(module => new ImportModuleCandidate(packageAlias, module, IsPrecompiled: false))
                    .ToList();
            }

            if (IsStdPackageAlias(packageAlias) &&
                TryGetPrecompiledModuleSource(BuildEffectiveModulePath(packageAlias, modulePath), out _))
            {
                return [new ImportModuleCandidate(packageAlias, WorkspaceModule: null, IsPrecompiled: true)];
            }

            return [];
        }

        var candidates = new List<ImportModuleCandidate>();
        var currentPackageModules = WorkspaceModuleLocator.ResolveImportModuleCandidates(
            entryFilePath,
            modulePath,
            _options.ImportSearchRoots);
        candidates.AddRange(currentPackageModules.Select(module => new ImportModuleCandidate(null, module, IsPrecompiled: false)));

        foreach (var (dependencyAlias, packageRoots) in _options.PackageImportRoots.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (IsStdPackageAlias(dependencyAlias))
            {
                continue;
            }

            var dependencyModules = WorkspaceModuleLocator.ResolveImportModuleCandidatesFromRoots(modulePath, packageRoots);
            candidates.AddRange(dependencyModules.Select(module => new ImportModuleCandidate(dependencyAlias, module, IsPrecompiled: false)));
        }

        var stdEffectivePath = BuildEffectiveModulePath("Std", modulePath);
        if (TryGetPrecompiledModuleSource(stdEffectivePath, out _))
        {
            candidates.Add(new ImportModuleCandidate("Std", WorkspaceModule: null, IsPrecompiled: true));
        }

        return candidates;
    }

    private bool ValidateImportedModuleMatch(
        IReadOnlyList<string> importPath,
        ResolvedWorkspaceModuleFile? resolvedImport,
        ModuleDecl importedRoot)
    {
        if (resolvedImport == null)
        {
            return true;
        }

        if (FindModuleDeclByPath(importedRoot, resolvedImport.ModulePath) != null)
        {
            return true;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.ImportedFileDoesNotDeclareModule(resolvedImport.FilePath, resolvedImport.ModulePath),
            "E3000");

        if (!importedRoot.Span.Equals(SourceSpan.Empty))
        {
            diagnostic.WithLabel(importedRoot.Span, DiagnosticMessages.LoadedImportedFileLabel);
        }

        diagnostic
            .WithNote(DiagnosticMessages.RequestedImportNote(string.Join(WellKnownStrings.Separators.Path, importPath)))
            .WithNote(DiagnosticMessages.ResolvedFromRootNote(resolvedImport.RootDirectory));

        var fileDerivedModulePath = WorkspaceModuleLocator.TryGetModulePathFromRoot(
            resolvedImport.RootDirectory,
            resolvedImport.FilePath);
        if (!string.IsNullOrWhiteSpace(fileDerivedModulePath))
        {
            diagnostic.WithNote(DiagnosticMessages.FilesystemModulePathNote(fileDerivedModulePath));
        }

        _diagnostics.Add(diagnostic);
        return false;
    }

    private bool TryAddDuplicateImportCandidateDiagnostic(
        IReadOnlyList<string> importPath,
        IReadOnlyList<ImportModuleCandidate> importCandidates)
    {
        var duplicateGroups = importCandidates
            .Where(static candidate => candidate.WorkspaceModule != null)
            .GroupBy(candidate => ToImportKey(candidate.PackageAlias, importPath), StringComparer.Ordinal)
            .Select(group => new
            {
                ModuleKey = group.Key,
                Candidates = group
                    .Select(static candidate => candidate.WorkspaceModule!)
                    .DistinctBy(static module => module.FilePath, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static module => module.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .Where(group => group.Candidates.Count > 1)
            .ToList();

        if (duplicateGroups.Count == 0)
        {
            return false;
        }

        foreach (var group in duplicateGroups)
        {
            var diagnostic = Diagnostic.Diagnostic.Error(
                    DiagnosticMessages.DuplicateModulePath(group.ModuleKey),
                    "E3000")
                .WithNote(DiagnosticMessages.RequestedImportNote(group.ModuleKey));

            foreach (var candidate in group.Candidates)
            {
                diagnostic
                    .WithNote(DiagnosticMessages.FileNote(candidate.FilePath))
                    .WithNote(DiagnosticMessages.ResolvedFromRootNote(candidate.RootDirectory));
            }

            _diagnostics.Add(diagnostic);
        }

        return true;
    }

    private void AddUnresolvedImportDiagnostic(
        string? packageAlias,
        IReadOnlyList<string> importPath,
        string entryFilePath)
    {
        var importText = string.Join(WellKnownStrings.Separators.Path, BuildEffectiveModulePath(packageAlias, importPath));
        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.UnableToResolveImportedModule(importText),
                "E3000")
            .WithNote(DiagnosticMessages.EntryFileNote(entryFilePath));

        foreach (var root in EnumerateImportFailureSearchRoots(packageAlias, entryFilePath))
        {
            diagnostic.WithNote(DiagnosticMessages.SearchedRootNote(root));
        }

        _diagnostics.Add(diagnostic);
    }

    private IEnumerable<string> EnumerateImportFailureSearchRoots(string? packageAlias, string entryFilePath)
    {
        if (!string.IsNullOrWhiteSpace(packageAlias))
        {
            return _options.PackageImportRoots.TryGetValue(packageAlias, out var packageRoots)
                ? packageRoots
                : [];
        }

        return WorkspaceModuleLocator.EnumerateImportSearchRoots(entryFilePath, _options.ImportSearchRoots);
    }

    private bool TryRegisterModuleTree(
        ModuleDecl moduleDecl,
        Dictionary<string, ModuleDecl> knownModuleDeclarations)
    {
        var discoveredModules = EnumerateModuleTree(moduleDecl)
            .Select(module => (Module: module, Key: ToModuleDeclKey(module)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToList();
        if (discoveredModules.Count == 0)
        {
            return false;
        }

        var pending = new Dictionary<string, ModuleDecl>(StringComparer.Ordinal);
        var hasConflict = false;

        foreach (var (module, key) in discoveredModules)
        {
            if (pending.TryGetValue(key, out var existingInTree))
            {
                AddDuplicateModulePathDiagnostic(key, existingInTree, module);
                hasConflict = true;
                continue;
            }

            if (knownModuleDeclarations.TryGetValue(key, out var existingKnownModule))
            {
                AddDuplicateModulePathDiagnostic(key, existingKnownModule, module);
                hasConflict = true;
                continue;
            }

            pending[key] = module;
        }

        if (hasConflict)
        {
            return false;
        }

        foreach (var (key, module) in pending)
        {
            knownModuleDeclarations[key] = module;
        }

        return true;
    }

    private void RegisterModuleTree(
        ModuleDecl moduleDecl,
        Dictionary<string, ModuleDecl> knownModuleDeclarations)
    {
        foreach (var module in EnumerateModuleTree(moduleDecl))
        {
            var key = ToModuleDeclKey(module);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (knownModuleDeclarations.TryGetValue(key, out var existingModule))
            {
                AddDuplicateModulePathDiagnostic(key, existingModule, module);
                continue;
            }

            knownModuleDeclarations[key] = module;
        }
    }

    private void AddDuplicateModulePathDiagnostic(string modulePath, ModuleDecl existingModule, ModuleDecl duplicateModule)
    {
        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.DuplicateModulePath(modulePath),
            "E3000");

        if (!duplicateModule.Span.Equals(SourceSpan.Empty))
        {
            diagnostic.WithLabel(duplicateModule.Span, DiagnosticMessages.DuplicateModuleDeclarationLabel);
        }

        if (!existingModule.Span.Equals(SourceSpan.Empty))
        {
            diagnostic.WithRelated(
                Diagnostic.Diagnostic.Note(DiagnosticMessages.FirstDeclarationOfModuleHere(modulePath))
                    .WithLabel(existingModule.Span, DiagnosticMessages.FirstModuleDeclarationLabel));
        }

        _diagnostics.Add(diagnostic);
    }

    private static ModuleDecl? FindModuleDeclByPath(ModuleDecl moduleDecl, string modulePath)
    {
        foreach (var module in EnumerateModules(moduleDecl))
        {
            if (string.Equals(ToModulePathKey(module.Path), modulePath, StringComparison.Ordinal))
            {
                return module;
            }
        }

        return null;
    }

    #endregion

    #region 工具方法

    private string GetPrimarySourceName()
    {
        if (string.IsNullOrWhiteSpace(_options.InputFile))
        {
            return "stdin.eidos";
        }

        try
        {
            return Path.GetFullPath(_options.InputFile);
        }
        catch
        {
            return _options.InputFile;
        }
    }

    private string GetCachePath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory!;
        return Path.Combine(baseDir, "cache", "grammar.bin");
    }

    #endregion
}
