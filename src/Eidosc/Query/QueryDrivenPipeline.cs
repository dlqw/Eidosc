using Eidosc.Symbols;
using Eidosc.Pipeline.TokenRewriting;
using System.Diagnostics;
using Eidosc.Ast.Declarations;
using Eidosc.Borrow;
using Eidosc.Mir.Closure;
using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Pipeline;
using Eidosc.Parsing.Handwritten;
using Eidosc.Parsing.Lexer;
using Eidosc.Semantic;
using Eidosc.ProjectSystem;
using Eidosc.Types;
using Eidosc.Utils;
using MemoryPack;

namespace Eidosc.Query;

public sealed partial class QueryDrivenPipeline
{
    private readonly QueryEngine _engine;
    private readonly bool _ownsEngine;
    private readonly CompilationOptions _options;
    private readonly string _sourceText;
    private readonly string _sourcePath;
    private readonly CancellationToken _cancellationToken;
    private readonly List<Diagnostic.Diagnostic> _diagnostics = [];
    private readonly Dictionary<CompilationPhase, TimeSpan> _phaseTimes = new();
    private readonly Dictionary<CompilationPhase, long> _phaseAllocations = new();
    private readonly Dictionary<string, long> _profilingCounters = new(StringComparer.Ordinal);
    private readonly IDictionary<string, (string Stamp, string SourceText)> _importSourceCache;
    private readonly ComptimeExecutionOptions _comptimeExecution;

    private GrammarData? _grammarData;
    private ScannerData? _scannerData;
    private ModuleParseService? _moduleParseService;

    public QueryEngine Engine => _engine;

    public QueryDrivenPipeline(string sourcePath, string sourceText, CompilationOptions options)
        : this(sourcePath, sourceText, options, null, CancellationToken.None)
    {
    }

    public QueryDrivenPipeline(string sourcePath, string sourceText, CompilationOptions options, QueryEngine? sharedEngine)
        : this(sourcePath, sourceText, options, sharedEngine, CancellationToken.None)
    {
    }

    public QueryDrivenPipeline(
        string sourcePath,
        string sourceText,
        CompilationOptions options,
        QueryEngine? sharedEngine,
        CancellationToken cancellationToken)
        : this(sourcePath, sourceText, options, sharedEngine, null, cancellationToken)
    {
    }

    public QueryDrivenPipeline(
        string sourcePath,
        string sourceText,
        CompilationOptions options,
        QueryEngine? sharedEngine,
        IDictionary<string, (string Stamp, string SourceText)>? importSourceCache,
        CancellationToken cancellationToken)
    {
        _sourcePath = sourcePath;
        _sourceText = sourceText;
        _options = options;
        _comptimeExecution = ComptimeExecutionOptions.Create(options);
        _cancellationToken = cancellationToken;
        _importSourceCache = importSourceCache ?? new Dictionary<string, (string Stamp, string SourceText)>(StringComparer.OrdinalIgnoreCase);

        if (sharedEngine != null)
        {
            _engine = sharedEngine;
            _ownsEngine = false;
        }
        else
        {
            _engine = new QueryEngine();
            _ownsEngine = true;
        }

        if (_ownsEngine)
        {
            _engine.Register(new ParseDescriptor(), DepKind.ParseModule);
            _engine.Register(new NameResolutionDescriptor(), DepKind.ResolveNames);
            _engine.Register(new TypeInferenceDescriptor(), DepKind.InferTypes);
            _engine.Register(new EffectInferenceDescriptor(), DepKind.InferAbilities);
            _engine.Register(new HirDescriptor(), DepKind.BuildHir);
            _engine.Register(new MirDescriptor(), DepKind.BuildMir);
            _engine.Register(new BorrowDescriptor(), DepKind.CheckBorrow);
            _engine.Register(new CodeGenDescriptor(), DepKind.CodeGen);
        }
    }

    public CompilationResult Run()
    {
        var totalSw = Stopwatch.StartNew();
        var success = true;
        var completedPhase = CompilationPhase.Lexer;

        try
        {
            ThrowIfCancellationRequested();
            LoadGrammarData();

            var targetOutput = ExecuteTargetPhase();

            success = !targetOutput.IsIncomplete;
            completedPhase = DetermineCompletedPhase();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            success = false;
            _diagnostics.Add(CompilationPipeline.CreateInternalErrorDiagnostic(
                ex,
                CompilationPipeline.ShouldExposeInternalExceptionDetails(_options)));
        }

        if (_diagnostics.Any(d => d.Level == Diagnostic.DiagnosticLevel.Error))
            success = false;

        totalSw.Stop();
        return BuildResult(success, completedPhase, totalSw.Elapsed);
    }

    #region Grammar Data

    private void LoadGrammarData()
    {
        ThrowIfCancellationRequested();

        const string grammarCacheVersion = CompilationPipeline.GrammarCacheVersion;
        var cachePath = GetCachePath();

        if (GrammarDataCache.TryGet(cachePath, grammarCacheVersion, out var cachedGrammarData, out var cachedScannerData))
        {
            _grammarData = cachedGrammarData;
            _scannerData = cachedScannerData;
            _moduleParseService = new ModuleParseService(_scannerData, _grammarData);
            return;
        }

        (_grammarData, _scannerData) = LexerTableBuilder.Build();
        _moduleParseService = new ModuleParseService(_scannerData, _grammarData);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var cacheData = new CacheData(_grammarData, _scannerData, grammarCacheVersion);
            GrammarDataCache.Store(cacheData);
            File.WriteAllBytes(cachePath, MemoryPackSerializer.Serialize(cacheData));
        }
        catch { /* non-critical */ }
    }

    private static string GetCachePath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory!;
        return Path.Combine(baseDir, "cache", "grammar.bin");
    }

    #endregion

    #region Providers

    private ParseOutput ProvideParse(string sourcePath)
    {
        ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();

        var parseResult = _moduleParseService!.ParseSource(
            _sourceText,
            _sourcePath,
            _options.LanguageVersion,
            _cancellationToken);
        var handwrittenAst = parseResult.Ast;
        var tokens = parseResult.Tokens;
        _diagnostics.AddRange(parseResult.Diagnostics);

        if (handwrittenAst != null)
        {
            ApplyPackageInstanceKeyToModuleTree(handwrittenAst, BuildCurrentPackageInstanceKey());
            PreloadImportedModules(handwrittenAst, tokens);
        }

        RecordPhase(CompilationPhase.Parser, sw, allocatedBytesBefore);
        return new ParseOutput
        {
            Ast = handwrittenAst,
            Tokens = tokens,
            IsIncomplete = handwrittenAst == null,
            IncompleteReason = handwrittenAst == null ? DiagnosticMessages.ParserDidNotProduceAst : null
        };
    }

    private NameResolutionOutput ProvideNameResolution(string sourcePath)
    {
        ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var parse = _engine.Execute(sourcePath, ProvideParse, _cancellationToken);
        if (parse.IsIncomplete || parse.Ast == null)
        {
            RecordPhase(CompilationPhase.Namer, sw, allocatedBytesBefore);
            return new NameResolutionOutput
            {
                IsIncomplete = true,
                IncompleteReason = parse.IncompleteReason ?? DiagnosticMessages.ParsePhaseIncomplete
            };
        }

        var symbolTable = new SymbolTable();
        var nameResolver = new NameResolver(symbolTable, _sourceText, _options.ImportSearchRoots)
        {
            ComptimeExecution = _comptimeExecution,
            UsePrecompiledImportSignatureOnly = ShouldUsePrecompiledImportSignaturesOnly()
        };
        nameResolver.Resolve(parse.Ast);
        _diagnostics.AddRange(nameResolver.Diagnostics);

        SetProfilingCounters(nameResolver.GetProfilingCounters());
        RecordPhase(CompilationPhase.Namer, sw, allocatedBytesBefore);
        return new NameResolutionOutput { SymbolTable = symbolTable, NameResolver = nameResolver };
    }

    private TypeInferenceOutput ProvideTypeInference(string sourcePath)
    {
        ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var parse = _engine.Execute(sourcePath, ProvideParse, _cancellationToken);
        var nameRes = _engine.Execute(sourcePath, ProvideNameResolution, _cancellationToken);
        if (parse.IsIncomplete || parse.Ast == null || nameRes.IsIncomplete || nameRes.SymbolTable == null)
        {
            RecordPhase(CompilationPhase.Types, sw, allocatedBytesBefore);
            return new TypeInferenceOutput
            {
                IsIncomplete = true,
                IncompleteReason = nameRes.IncompleteReason ?? parse.IncompleteReason ?? DiagnosticMessages.NameResolutionIncomplete
            };
        }

        var typeInferer = new TypeInferer(nameRes.SymbolTable)
        {
            ComptimeExecution = _comptimeExecution,
            UsePrecompiledImportSignatureOnly = ShouldUsePrecompiledImportSignaturesOnly()
        };
        typeInferer.Infer(parse.Ast);
        _diagnostics.AddRange(typeInferer.Diagnostics);

        var validator = new FfiTypeValidator();
        if (!validator.Validate(parse.Ast, nameRes.NameResolver?.LinkLibraries))
        {
            _diagnostics.AddRange(validator.Diagnostics);
            RecordPhase(CompilationPhase.Types, sw, allocatedBytesBefore);
            return new TypeInferenceOutput
            {
                TypeInferer = typeInferer,
                IsIncomplete = true,
                IncompleteReason = DiagnosticMessages.FfiTypeValidationFailed
            };
        }
        if (validator.Diagnostics.Count > 0)
            _diagnostics.AddRange(validator.Diagnostics);

        var effectInferer = new EffectInferer(nameRes.SymbolTable);
        effectInferer.Infer(parse.Ast);

        SetProfilingCounters(typeInferer.GetProfilingCounters());
        RecordPhase(CompilationPhase.Types, sw, allocatedBytesBefore);
        return new TypeInferenceOutput
        {
            TypeInferer = typeInferer,
            EffectInferer = effectInferer
        };
    }

    private EffectInferenceOutput ProvideEffectInference(string sourcePath)
    {
        ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var parse = _engine.Execute(sourcePath, ProvideParse, _cancellationToken);
        var nameRes = _engine.Execute(sourcePath, ProvideNameResolution, _cancellationToken);
        var typeInf = _engine.Execute(sourcePath, ProvideTypeInference, _cancellationToken);
        if (parse.IsIncomplete ||
            parse.Ast == null ||
            nameRes.IsIncomplete ||
            nameRes.SymbolTable == null ||
            typeInf.IsIncomplete ||
            typeInf.TypeInferer == null)
        {
            RecordPhase(CompilationPhase.Effects, sw);
            return new EffectInferenceOutput
            {
                IsIncomplete = true,
                IncompleteReason = FirstIncompleteReason(typeInf, nameRes, parse)
            };
        }

        var abilityInferer = typeInf.EffectInferer ?? new EffectInferer(nameRes.SymbolTable);
        if (typeInf.EffectInferer == null)
        {
            abilityInferer.Infer(parse.Ast);
        }

        var authChecker = new EffectAuthorizationChecker(nameRes.SymbolTable, abilityInferer.FunctionSummaries);
        authChecker.Check(parse.Ast);
        _diagnostics.AddRange(authChecker.Diagnostics);

        RecordPhase(CompilationPhase.Effects, sw);
        return new EffectInferenceOutput { EffectInferer = abilityInferer };
    }

    private HirOutput ProvideHir(string sourcePath)
    {
        ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var parse = _engine.Execute(sourcePath, ProvideParse, _cancellationToken);
        var nameRes = _engine.Execute(sourcePath, ProvideNameResolution, _cancellationToken);
        var typeInf = _engine.Execute(sourcePath, ProvideTypeInference, _cancellationToken);
        var abilityInf = _engine.Execute(sourcePath, ProvideEffectInference, _cancellationToken);
        if (parse.IsIncomplete ||
            parse.Ast == null ||
            nameRes.IsIncomplete ||
            nameRes.SymbolTable == null ||
            typeInf.IsIncomplete ||
            typeInf.TypeInferer == null ||
            abilityInf.IsIncomplete ||
            abilityInf.EffectInferer == null)
        {
            RecordPhase(CompilationPhase.Hir, sw);
            return new HirOutput
            {
                IsIncomplete = true,
                IncompleteReason = FirstIncompleteReason(parse, nameRes, typeInf, abilityInf)
            };
        }

        var hirBuilder = new HirBuilder(nameRes.SymbolTable, typeInf.TypeInferer, abilityInf.EffectInferer);
        var hirModule = hirBuilder.Build(parse.Ast, nameRes.NameResolver?.LinkLibraries);
        _diagnostics.AddRange(hirBuilder.Diagnostics);

        Mir.ParameterEffectMap? paramEffects = null;
        if (hirModule != null)
        {
            var effectAnalysis = new HirParameterEffectAnalysis(hirModule);
            effectAnalysis.Analyze();
            paramEffects = effectAnalysis.Results;
        }

        if (hirBuilder.Diagnostics.Any(d => d.Level == Diagnostic.DiagnosticLevel.Error))
        {
            RecordPhase(CompilationPhase.Hir, sw);
            return new HirOutput
            {
                HirModule = hirModule,
                CopyLikeTypeIds = hirBuilder.CopyLikeTypeIds,
                DynamicTypeKeys = hirBuilder.DynamicTypeKeys,
                TypeDescriptors = hirBuilder.TypeDescriptors,
                ConstructorLayouts = hirBuilder.ConstructorLayouts,
                ParameterEffects = paramEffects ?? new Mir.ParameterEffectMap(),
                IsIncomplete = true,
                IncompleteReason = DiagnosticMessages.HirBuilderReportedErrors
            };
        }

        RecordPhase(CompilationPhase.Hir, sw);
        return new HirOutput
        {
            HirModule = hirModule!,
            CopyLikeTypeIds = hirBuilder.CopyLikeTypeIds,
            DynamicTypeKeys = hirBuilder.DynamicTypeKeys,
            TypeDescriptors = hirBuilder.TypeDescriptors,
            ConstructorLayouts = hirBuilder.ConstructorLayouts,
            ParameterEffects = paramEffects ?? new Mir.ParameterEffectMap()
        };
    }

    private MirOutput ProvideMir(string sourcePath)
    {
        ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var hirOut = _engine.Execute(sourcePath, ProvideHir, _cancellationToken);
        var nameRes = _engine.Execute(sourcePath, ProvideNameResolution, _cancellationToken);
        var typeInf = _engine.Execute(sourcePath, ProvideTypeInference, _cancellationToken);
        if (hirOut.IsIncomplete ||
            hirOut.HirModule == null ||
            nameRes.IsIncomplete ||
            nameRes.SymbolTable == null ||
            typeInf.IsIncomplete ||
            typeInf.TypeInferer == null)
        {
            RecordPhase(CompilationPhase.Mir, sw);
            return new MirOutput
            {
                IsIncomplete = true,
                IncompleteReason = FirstIncompleteReason(hirOut, nameRes, typeInf)
            };
        }

        var mirBuilder = new MirBuilder(
            CopyTypeSemantics.CreateSymbolTableCopyResolver(nameRes.SymbolTable, hirOut.TypeDescriptors),
            hirOut.CopyLikeTypeIds,
            hirOut.DynamicTypeKeys,
            nameRes.SymbolTable,
            hirOut.ConstructorLayouts,
            hirOut.TypeDescriptors,
            hirOut.ParameterEffects,
            typeInf.TypeInferer.Substitution);
        var mirModule = mirBuilder.Build(hirOut.HirModule);
        _diagnostics.AddRange(mirBuilder.Diagnostics);

        var hasErrors = mirBuilder.Diagnostics.Any(d => d.Level == Diagnostic.DiagnosticLevel.Error);

        if (!hasErrors)
        {
            var mirEffectAnalysis = new ParameterEffectAnalysis(mirModule);
            mirEffectAnalysis.Analyze();
            ParameterEffectAnalysis.ApplyCallSiteEffectFixup(mirModule, mirEffectAnalysis.Results);
            ParameterEffectAnalysis.ApplyReadOnlyParameterFix(mirModule, mirEffectAnalysis.Results);
        }

        MirModule borrowMirModule = mirModule;

        if (!hasErrors)
        {
            var genericSpecializer = new MirGenericSpecializer(
                CopyTypeSemantics.CreateSymbolTableCopyResolver(nameRes.SymbolTable, hirOut.TypeDescriptors),
                hirOut.CopyLikeTypeIds,
                nameRes.SymbolTable);

            MirOptimizer? optimizer = null;
            if (_options.EnableMirOptimizations)
            {
                optimizer = MirOptimizer.CreateDefault(
                    effectSummaries: typeInf.EffectInferer?.FunctionSummariesBySymbol);
            }

            var specializationResult = CompilationPipeline.RunSpecializationLoop(mirModule, genericSpecializer, optimizer);
            mirModule = specializationResult.Module;
            _diagnostics.AddRange(specializationResult.Diagnostics);
            hasErrors |= specializationResult.Diagnostics.Any(d => d.Level == Diagnostic.DiagnosticLevel.Error);
        }

        if (!hasErrors)
        {
            var validator = new MirValidator();
            if (!validator.Validate(mirModule))
            {
                _diagnostics.AddRange(validator.Diagnostics);
                hasErrors = validator.Diagnostics.Any(d => d.Level == Diagnostic.DiagnosticLevel.Error);
            }
        }

        RecordPhase(CompilationPhase.Mir, sw);
        return hasErrors
            ? new MirOutput
            {
                IsIncomplete = true,
                IncompleteReason = "MIR validation failed",
                MirModule = mirModule,
                BorrowMirModule = borrowMirModule
            }
            : new MirOutput { MirModule = mirModule, BorrowMirModule = borrowMirModule };
    }

    private BorrowOutput ProvideBorrow(string sourcePath)
    {
        ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var mirOut = _engine.Execute(sourcePath, ProvideMir, _cancellationToken);
        var nameRes = _engine.Execute(sourcePath, ProvideNameResolution, _cancellationToken);
        var abilityInf = _engine.Execute(sourcePath, ProvideEffectInference, _cancellationToken);
        if (mirOut.IsIncomplete ||
            mirOut.MirModule == null ||
            mirOut.BorrowMirModule == null ||
            nameRes.IsIncomplete ||
            nameRes.SymbolTable == null ||
            abilityInf.IsIncomplete ||
            abilityInf.EffectInferer == null)
        {
            RecordPhase(CompilationPhase.Borrow, sw);
            return new BorrowOutput
            {
                IsIncomplete = true,
                IncompleteReason = FirstIncompleteReason(mirOut, nameRes, abilityInf)
            };
        }

        var borrowModule = mirOut.BorrowMirModule;
        var borrowResult = new ModuleBorrowCheckResult();
        var borrowAnalysisContext = new BorrowModuleAnalysisContext(borrowModule);
        var runStackPromotionHints = ShouldRunStackPromotionHints();

        var signatureCache = new LoanSignatureCache();
        var inferredSignatures = new Dictionary<string, LoanSignature>();
        var infererByFunc = new Dictionary<MirFunc, LoanSignatureInferer>();

        foreach (var func in borrowModule.Functions)
        {
            var inferer = new LoanSignatureInferer(func, signatureCache, nameRes.SymbolTable, borrowModule.DynamicTypeKeys);
            infererByFunc[func] = inferer;
            inferer.Infer(includeCallConstraints: false, force: true);
        }

        foreach (var (func, inferer) in infererByFunc)
        {
            var signature = inferer.Infer(includeCallConstraints: true, force: true);
            if (!string.IsNullOrEmpty(func.Name))
                inferredSignatures[borrowAnalysisContext.GetStableKey(func)] = signature;
        }

        var capabilitySnapshots = new Dictionary<SymbolId, BorrowCapabilitySnapshot>();
        IReadOnlyDictionary<string, FieldEscapeSummary> fieldEscapeSummaries = new Dictionary<string, FieldEscapeSummary>();
        if (runStackPromotionHints)
        {
            var fieldEscapeAnalyzer = new ModuleFieldEscapeAnalyzer(borrowModule, borrowAnalysisContext);
            fieldEscapeAnalyzer.Analyze();
            fieldEscapeSummaries = fieldEscapeAnalyzer.Summaries;
        }

        foreach (var func in borrowModule.Functions)
        {
            capabilitySnapshots.TryGetValue(func.SymbolId, out var capabilitySnapshot);

            var usageAnalyzer = new VariableUsageAnalyzer(func);
            usageAnalyzer.Analyze();

            var livenessAnalyzer = new LivenessAnalyzer(func, usageAnalyzer);
            livenessAnalyzer.Analyze();

            var affineChecker = new AffineTypeChecker(func, usageAnalyzer, false, borrowModule.DynamicTypeKeys);
            affineChecker.Check();

            var borrowChecker = new BorrowChecker(func, livenessAnalyzer, signatureCache, nameRes.SymbolTable, capabilitySnapshot, false, borrowModule.DynamicTypeKeys);
            borrowChecker.Check();

            var perceusAnalyzer = new PerceusAnalyzer(func, livenessAnalyzer, usageAnalyzer);
            perceusAnalyzer.Analyze();

            var reuseAnalyzer = new ReuseAnalyzer(func, perceusAnalyzer.Hints);
            reuseAnalyzer.Analyze();

            StackPromotionAnalyzer? stackPromo = null;
            if (runStackPromotionHints)
            {
                stackPromo = new StackPromotionAnalyzer(func);
                if (StackPromotionAnalyzer.MayHavePromotableConstructorCalls(func))
                {
                    stackPromo.Analyze();
                }
            }

            UnifiedStackPromotionAnalyzer? unifiedStackPromo = null;
            if (runStackPromotionHints)
            {
                unifiedStackPromo = new UnifiedStackPromotionAnalyzer(func, fieldEscapeSummaries, borrowAnalysisContext);
                if (UnifiedStackPromotionAnalyzer.MayHavePromotableAllocations(func, borrowAnalysisContext))
                {
                    unifiedStackPromo.Analyze();
                }
            }

            var loanVerifier = new LoanConstraintVerifier(signatureCache, nameRes.SymbolTable, capabilitySnapshot, false, borrowModule.DynamicTypeKeys);
            var loanResults = loanVerifier.VerifyFunction(func);

            LoanSignature? loanSignature = null;
            if (func.SymbolId.IsValid)
                loanSignature = signatureCache.GetSignature(func.SymbolId);
            if (loanSignature == null && !string.IsNullOrEmpty(func.Name))
                inferredSignatures.TryGetValue(borrowAnalysisContext.GetStableKey(func), out loanSignature);

            var funcResult = new BorrowCheckResult
            {
                FunctionName = func.Name,
                FunctionSymbolId = func.SymbolId,
                LivenessAnalyzer = livenessAnalyzer,
                AffineTypeChecker = affineChecker,
                BorrowChecker = borrowChecker,
                LoanSignature = loanSignature,
                LoanConstraintVerifier = loanVerifier,
                LoanConstraintResults = loanResults,
                PerceusAnalyzer = perceusAnalyzer,
                ReuseAnalyzer = reuseAnalyzer,
                StackPromotionAnalyzer = stackPromo,
                UnifiedStackPromotionAnalyzer = unifiedStackPromo
            };

            borrowResult.AddResult(funcResult);

            foreach (var diag in affineChecker.Diagnostics)
                _diagnostics.Add(ConvertAffineDiagnostic(func.Name, diag));
            foreach (var diag in infererByFunc[func].Diagnostics)
                _diagnostics.Add(ConvertBorrowDiagnostic(func.Name, diag));
            foreach (var diag in borrowChecker.Diagnostics)
                _diagnostics.Add(ConvertBorrowDiagnostic(func.Name, diag));
            foreach (var diag in loanVerifier.Diagnostics)
                _diagnostics.Add(ConvertBorrowDiagnostic(func.Name, diag));
        }

        // Send check
        foreach (var func in mirOut.MirModule.Functions)
        {
            var sendChecker = new SendChecker(func, mirOut.MirModule);
            sendChecker.Check();
            foreach (var err in sendChecker.Errors)
            {
                _diagnostics.Add(new Diagnostic.Diagnostic(
                    Diagnostic.DiagnosticLevel.Error,
                    DiagnosticMessages.SendCheckFailed(func.Name, err.Message),
                    "E0200"));
            }
        }

        RecordPhase(CompilationPhase.Borrow, sw);
        return new BorrowOutput { BorrowCheckResult = borrowResult };
    }

    private bool ShouldRunStackPromotionHints()
    {
        return !_options.StopAtPhase.HasValue ||
               _options.StopAtPhase.Value == CompilationPhase.Llvm;
    }

    private ICompilationQueryOutput ExecuteTargetPhase()
    {
        return _options.StopAtPhase switch
        {
            CompilationPhase.Parser => _engine.Execute(_sourcePath, ProvideParse, _cancellationToken),
            CompilationPhase.Namer => _engine.Execute(_sourcePath, ProvideNameResolution, _cancellationToken),
            CompilationPhase.Types => _engine.Execute(_sourcePath, ProvideTypeInference, _cancellationToken),
            CompilationPhase.Effects => _engine.Execute(_sourcePath, ProvideEffectInference, _cancellationToken),
            CompilationPhase.Hir => _engine.Execute(_sourcePath, ProvideHir, _cancellationToken),
            CompilationPhase.Mir => _engine.Execute(_sourcePath, ProvideMir, _cancellationToken),
            CompilationPhase.Borrow => _engine.Execute(_sourcePath, ProvideBorrow, _cancellationToken),
            _ => _engine.Execute(_sourcePath, ProvideCodeGen, _cancellationToken)
        };
    }

    private CodeGenOutput ProvideCodeGen(string sourcePath)
    {
        ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var mirOut = _engine.Execute(sourcePath, ProvideMir, _cancellationToken);
        var borrowOut = _engine.Execute(sourcePath, ProvideBorrow, _cancellationToken);
        var nameRes = _engine.Execute(sourcePath, ProvideNameResolution, _cancellationToken);
        if (mirOut.IsIncomplete ||
            mirOut.MirModule == null ||
            borrowOut.IsIncomplete ||
            borrowOut.BorrowCheckResult == null)
        {
            RecordPhase(CompilationPhase.Llvm, sw);
            return new CodeGenOutput
            {
                IsIncomplete = true,
                IncompleteReason = FirstIncompleteReason(mirOut, borrowOut)
            };
        }

        var converter = new MirToLlvmConverter();
        converter.SetPerceusHints(borrowOut.BorrowCheckResult);
        converter.SetReuseHints(borrowOut.BorrowCheckResult);
        converter.SetStackPromotionHints(borrowOut.BorrowCheckResult);
        converter.SetUnifiedStackPromotionHints(borrowOut.BorrowCheckResult);

        if (!nameRes.IsIncomplete && nameRes.SymbolTable != null)
        {
            var cstructAccessors = new Dictionary<string, CStructAccessorInfo>();
            foreach (var symbol in nameRes.SymbolTable.Symbols.Values)
            {
                if (symbol is FuncSymbol { IsCStructAccessor: true } funcSymbol)
                {
                    cstructAccessors[funcSymbol.Name] = new CStructAccessorInfo
                    {
                        FieldOffset = funcSymbol.CStructFieldOffset,
                        FieldTypeId = funcSymbol.CStructFieldTypeId.Value,
                        IsGetter = funcSymbol.IsCStructGetter
                    };
                }
            }
            converter.SetCStructAccessors(cstructAccessors);
        }

        var llvmModule = converter.Convert(mirOut.MirModule);
        _diagnostics.AddRange(converter.Diagnostics);

        var emitter = new LlvmEmitter();
        var llvmIrText = emitter.Emit(llvmModule);

        RecordPhase(CompilationPhase.Llvm, sw);
        return new CodeGenOutput { LlvmModule = llvmModule, LlvmIrText = llvmIrText };
    }

    #endregion

    #region Helpers

    private CompilationPhase DetermineCompletedPhase()
    {
        var codeGen = _engine.TryGetCached<string, CodeGenOutput>(_sourcePath);
        if (codeGen is { IsIncomplete: false })
            return CompilationPhase.Llvm;

        var borrow = _engine.TryGetCached<string, BorrowOutput>(_sourcePath);
        if (borrow is { IsIncomplete: false })
            return CompilationPhase.Borrow;

        var mir = _engine.TryGetCached<string, MirOutput>(_sourcePath);
        if (mir is { IsIncomplete: false })
            return CompilationPhase.Mir;

        var hir = _engine.TryGetCached<string, HirOutput>(_sourcePath);
        if (hir is { IsIncomplete: false })
            return CompilationPhase.Hir;

        var abilities = _engine.TryGetCached<string, EffectInferenceOutput>(_sourcePath);
        if (abilities is { IsIncomplete: false })
            return CompilationPhase.Effects;

        var types = _engine.TryGetCached<string, TypeInferenceOutput>(_sourcePath);
        if (types is { IsIncomplete: false })
            return CompilationPhase.Types;

        var names = _engine.TryGetCached<string, NameResolutionOutput>(_sourcePath);
        if (names is { IsIncomplete: false })
            return CompilationPhase.Namer;

        var parse = _engine.TryGetCached<string, ParseOutput>(_sourcePath);
        return parse is { IsIncomplete: false }
            ? CompilationPhase.Parser
            : CompilationPhase.Lexer;
    }

    private void ThrowIfCancellationRequested()
    {
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private static string FirstIncompleteReason(params ICompilationQueryOutput[] outputs)
    {
        foreach (var output in outputs)
        {
            if (output.IsIncomplete && !string.IsNullOrWhiteSpace(output.IncompleteReason))
                return output.IncompleteReason;
        }

        return DiagnosticMessages.DependencyPhaseIncomplete;
    }

    private void PreloadImportedModules(ModuleDecl ast, List<Token> tokens)
    {
        if (!TryGetInputFilePath(out var entryFilePath)) return;

        var knownModules = CollectKnownModuleDeclarations(ast);
        var loadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entryFilePath };
        var attemptedImports = new HashSet<string>(StringComparer.Ordinal);
        var pendingImports = new Queue<(List<string> path, string? packageAlias, string? parentKey)>();

        EnqueueImports(ast, pendingImports, null, null);

        // Auto-import Prelude
        if (!_options.NoImplicitPrelude)
        {
            var rootPath = GetRootModulePath(ast);
            var isStdlib = rootPath.Count > 0 && string.Equals(rootPath[0], "Std", StringComparison.Ordinal);
            if (!isStdlib)
            {
                var preludePath = new List<string> { "Prelude" };
                var preludeKey = ToImportKey("Std", preludePath);
                var alreadyImportsPrelude = pendingImports
                    .Any(item => ToImportKey(item.packageAlias, item.path) == preludeKey);
                if (!alreadyImportsPrelude)
                    pendingImports.Enqueue((preludePath, "Std", null));
            }
        }

        var importParentMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var rootModuleKey = ToModulePathKey(GetRootModulePath(ast));
        if (!string.IsNullOrEmpty(rootModuleKey))
            importParentMap[rootModuleKey] = "<root>";

        while (pendingImports.Count > 0)
        {
            ThrowIfCancellationRequested();
            var (importPath, packageAlias, parentKey) = pendingImports.Dequeue();
            if (importPath.Count == 0) continue;
            var importKey = ToImportKey(packageAlias, importPath);
            if (string.IsNullOrEmpty(importKey) || !attemptedImports.Add(importKey)) continue;

            if (importParentMap.ContainsKey(importKey) && !string.IsNullOrEmpty(parentKey))
            {
                var chain = BuildImportChain(importKey, parentKey, importParentMap);
                _diagnostics.Add(Diagnostic.Diagnostic.Error(
                    DiagnosticMessages.CircularImportDetected(chain),
                    "E5001"));
                continue;
            }

            if (!string.IsNullOrEmpty(parentKey))
                importParentMap[importKey] = parentKey;

            var effectiveModulePath = BuildEffectiveModulePath(packageAlias, importPath);
            var effectiveModuleKey = ToImportKey(packageAlias, importPath);
            if (knownModules.ContainsKey(effectiveModuleKey)) continue;

            var resolvedCandidates = ResolveImportModuleCandidates(entryFilePath, packageAlias, importPath);
            if (TryAddDuplicateImportCandidateDiagnostic(packageAlias, importPath, resolvedCandidates))
                continue;

            var resolved = resolvedCandidates.FirstOrDefault();
            var importFile = resolved?.FilePath;
            ModuleDecl? importedRoot;
            List<Diagnostic.Diagnostic> parseDiagnostics;
            bool parseSuccess;

            if (!string.IsNullOrEmpty(importFile))
            {
                if (!loadedFiles.Add(importFile)) continue;
                parseSuccess = TryParseModuleFile(importFile, out importedRoot, out parseDiagnostics);
            }
            else if (IsStdPackageAlias(packageAlias) &&
                     PrecompiledModuleCache.TryGetSource(
                         effectiveModulePath,
                         ShouldUsePrecompiledImportSignaturesOnly()
                             ? PrecompiledModuleSourcePolicy.SignatureOnly
                             : PrecompiledModuleSourcePolicy.FullBody,
                         out var precompiledSource))
            {
                AddProfilingCounter(
                    "Query.precompiledSignatureSource.replacedFunctionBodies",
                    precompiledSource.FunctionBodyReplacementCount);
                AddProfilingCounter(
                    "Query.precompiledSignatureSource.replacedValueInitializers",
                    precompiledSource.ValueInitializerReplacementCount);
                AddProfilingCounter(
                    "Query.precompiledSignatureSource.removedNonExportImports",
                    precompiledSource.ImportRemovalCount);
                var sourceName = PrecompiledModuleCache.TryGetSourceFilePath(effectiveModulePath, out var srcFile)
                    ? srcFile
                    : $"<precompiled:{importKey}>";
                parseSuccess = TryParseModuleSource(
                    precompiledSource.Source,
                    sourceName,
                    EidosLanguageVersions.Current,
                    out importedRoot,
                    out parseDiagnostics);

            }
            else
            {
                AddUnresolvedImportDiagnostic(packageAlias, importPath, entryFilePath);
                continue;
            }

            if (!parseSuccess)
            {
                AddImportParseFailedDiagnostic(importPath, importFile, parseDiagnostics);
                _diagnostics.AddRange(parseDiagnostics);
                continue;
            }

            _diagnostics.AddRange(parseDiagnostics);

            if (!ValidateImportMatch(importPath, resolved, importedRoot!)) continue;
            ApplyPackageIdentityToImportedModuleTree(
                importedRoot!,
                packageAlias,
                BuildPackageInstanceKey(packageAlias, resolved));

            var hasAddedModule = false;
            foreach (var moduleDecl in importedRoot!.Declarations.OfType<ModuleDecl>())
            {
                if (TryRegisterModuleTree(moduleDecl, knownModules))
                {
                    ast.Declarations.Add(moduleDecl);
                    EnqueueImports(moduleDecl, pendingImports, packageAlias, importKey);
                    hasAddedModule = true;
                }
            }

            if (!hasAddedModule)
                EnqueueImports(importedRoot, pendingImports, packageAlias, importKey);
        }
    }

    private bool ShouldUsePrecompiledImportSignaturesOnly()
    {
        return _options.StopAtPhase == CompilationPhase.Types;
    }

    private static bool IsPrecompiledStdSourcePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return Eidosc.Semantic.PrecompiledModuleRegistry.IsStdlibSourcePath(filePath);
    }

    private bool TryGetInputFilePath(out string inputFilePath)
    {
        inputFilePath = string.Empty;
        if (string.IsNullOrWhiteSpace(_options.InputFile)) return false;

        var normalized = Path.GetFullPath(_options.InputFile);
        if (!File.Exists(normalized)) return false;

        inputFilePath = normalized;
        return true;
    }

    private bool TryParseModuleFile(
        string filePath,
        out ModuleDecl? moduleDecl,
        out List<Diagnostic.Diagnostic> diagnostics)
    {
        moduleDecl = null;
        diagnostics = [];

        string sourceText;
        try
        {
            sourceText = ReadImportedSourceText(filePath);
            if (ShouldUsePrecompiledImportSignaturesOnly() && IsPrecompiledStdSourcePath(filePath))
            {
                var signatureSource = PrecompiledModuleCache.GetOrCreateSignatureSource(filePath, sourceText);
                sourceText = signatureSource.Source;
                AddProfilingCounter(
                    "Query.precompiledSignatureSource.replacedFunctionBodies",
                    signatureSource.FunctionBodyReplacementCount);
                AddProfilingCounter(
                    "Query.precompiledSignatureSource.replacedValueInitializers",
                    signatureSource.ValueInitializerReplacementCount);
                AddProfilingCounter(
                    "Query.precompiledSignatureSource.removedNonExportImports",
                    signatureSource.ImportRemovalCount);
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic.Diagnostic.Error(
                DiagnosticMessages.FailedToLoadImportedModuleFile(filePath, ex.Message),
                "E0002"));
            return false;
        }

        var languageVersion = EidosProjectConfigurationLoader.TryLoadNearest(filePath)?.Configuration.LanguageVersion
            ?? _options.LanguageVersion;
        return TryParseModuleSource(sourceText, filePath, languageVersion, out moduleDecl, out diagnostics);
    }

    private string ReadImportedSourceText(string filePath)
    {
        var absolutePath = Path.GetFullPath(filePath);
        var normalizedPath = SourcePathNormalizer.NormalizeForCacheKey(absolutePath);
        var fileInfo = new FileInfo(absolutePath);
        var fingerprint = $"{fileInfo.LastWriteTimeUtc.Ticks}:{fileInfo.Length}";
        if (_importSourceCache.TryGetValue(normalizedPath, out var cached) &&
            string.Equals(cached.Stamp, fingerprint, StringComparison.Ordinal))
        {
            return cached.SourceText;
        }

        var sourceText = File.ReadAllText(absolutePath);
        _importSourceCache[normalizedPath] = (fingerprint, sourceText);
        return sourceText;
    }

    private bool TryParseModuleSource(
        string sourceText,
        string sourceName,
        string languageVersion,
        out ModuleDecl? moduleDecl,
        out List<Diagnostic.Diagnostic> diagnostics)
    {
        diagnostics = [];
        moduleDecl = null;
        if (ShouldUsePrecompiledImportSignaturesOnly() &&
            IsPrecompiledStdSourcePath(sourceName) &&
            TryGetOrCreatePrecompiledSignatureTokens(sourceText, sourceName, out var cachedTokens, out var cachedLexerDiagnostics))
        {
            var parseResult = _moduleParseService!.ParseTokenList(
                cachedTokens,
                sourceName,
                languageVersion,
                cachedLexerDiagnostics);
            diagnostics = parseResult.Diagnostics;
            moduleDecl = parseResult.Ast;
            return parseResult.Success;
        }

        var result = _moduleParseService!.ParseSource(
            sourceText,
            sourceName,
            languageVersion,
            _cancellationToken);
        moduleDecl = result.Ast;
        diagnostics = result.Diagnostics;
        return result.Success;
    }

    private void EnqueueImports(
        ModuleDecl moduleDecl,
        Queue<(List<string> path, string? packageAlias, string? parentKey)> queue,
        string? ambientPackageAlias,
        string? parentKey)
    {
        foreach (var import in EnumerateImports(moduleDecl))
        {
            if (import.ModulePath.Count == 0) continue;
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
        if (string.IsNullOrWhiteSpace(_sourcePath) ||
            _sourcePath.StartsWith("<", StringComparison.Ordinal))
        {
            return ModuleIdentity.CurrentPackageInstanceKey;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_sourcePath));
            return string.IsNullOrWhiteSpace(directory)
                ? ModuleIdentity.CurrentPackageInstanceKey
                : NormalizePackageInstanceRoot(directory);
        }
        catch
        {
            return ModuleIdentity.CurrentPackageInstanceKey;
        }
    }

    private static string BuildPackageInstanceKey(string? packageAlias, ResolvedWorkspaceModuleFile? resolved)
    {
        if (resolved == null)
        {
            return $"precompiled:{packageAlias ?? ModuleIdentity.CurrentPackageInstanceKey}";
        }

        return string.IsNullOrWhiteSpace(resolved.RootDirectory)
            ? ModuleIdentity.CurrentPackageInstanceKey
            : NormalizePackageInstanceRoot(resolved.RootDirectory);
    }

    private static string NormalizePackageInstanceRoot(string rootDirectory)
    {
        return Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
    }

    private static IEnumerable<ImportDecl> EnumerateImports(ModuleDecl decl)
    {
        foreach (var d in decl.Declarations)
        {
            if (d is ImportDecl import) yield return import;
            else if (d is ModuleDecl child)
                foreach (var ci in EnumerateImports(child)) yield return ci;
        }
    }

    private static List<string> GetRootModulePath(ModuleDecl ast)
    {
        var path = new List<string>();
        if (ast.Path.Count > 0) path.AddRange(ast.Path);
        return path;
    }

    private Dictionary<string, ModuleDecl> CollectKnownModuleDeclarations(ModuleDecl moduleDecl)
    {
        var result = new Dictionary<string, ModuleDecl>(StringComparer.Ordinal);
        foreach (var topLevelModule in moduleDecl.Declarations.OfType<ModuleDecl>())
            RegisterModuleTree(topLevelModule, result);

        return result;
    }

    private void RegisterModuleTree(ModuleDecl module, Dictionary<string, ModuleDecl> known)
    {
        foreach (var m in EnumerateModuleTree(module))
        {
            var key = ToModuleDeclKey(m);
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (known.TryGetValue(key, out var existing))
            {
                AddDuplicateModulePathDiagnostic(key, existing, m);
                continue;
            }

            known[key] = m;
        }
    }

    private bool TryRegisterModuleTree(ModuleDecl module, Dictionary<string, ModuleDecl> known)
    {
        var discovered = EnumerateModuleTree(module)
            .Select(m => (Module: m, Key: ToModuleDeclKey(m)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToList();

        if (discovered.Count == 0)
            return false;

        var pending = new Dictionary<string, ModuleDecl>(StringComparer.Ordinal);
        var hasConflict = false;

        foreach (var (moduleDecl, key) in discovered)
        {
            if (pending.TryGetValue(key, out var existingInTree))
            {
                AddDuplicateModulePathDiagnostic(key, existingInTree, moduleDecl);
                hasConflict = true;
                continue;
            }

            if (known.TryGetValue(key, out var existingKnown))
            {
                AddDuplicateModulePathDiagnostic(key, existingKnown, moduleDecl);
                hasConflict = true;
                continue;
            }

            pending[key] = moduleDecl;
        }

        if (hasConflict)
            return false;

        foreach (var (key, moduleDecl) in pending)
            known[key] = moduleDecl;
        return true;
    }

    private static IEnumerable<ModuleDecl> EnumerateModuleTree(ModuleDecl decl)
    {
        yield return decl;
        foreach (var d in decl.Declarations)
        {
            if (d is ModuleDecl child)
            {
                yield return child;
                foreach (var nested in EnumerateModules(child))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<ModuleDecl> EnumerateModules(ModuleDecl decl)
    {
        foreach (var d in decl.Declarations)
        {
            if (d is ModuleDecl child)
            {
                yield return child;
                foreach (var n in EnumerateModules(child))
                    yield return n;
            }
        }
    }

    private bool ValidateImportMatch(IReadOnlyList<string> importPath, ResolvedWorkspaceModuleFile? resolved, ModuleDecl importedRoot)
    {
        if (resolved == null) return true;

        var targetKey = resolved.ModulePath;
        if (EnumerateModuleTree(importedRoot).Any(module => string.Equals(ToModulePathKey(module.Path), targetKey, StringComparison.Ordinal)))
            return true;

        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.ImportedFileDoesNotDeclareModule(resolved.FilePath, targetKey),
            "E3000");

        if (!importedRoot.Span.Equals(SourceSpan.Empty))
            diagnostic.WithLabel(importedRoot.Span, DiagnosticMessages.LoadedImportedFileLabel);

        diagnostic
            .WithNote(DiagnosticMessages.RequestedImportNote(string.Join(WellKnownStrings.Separators.Path, importPath)))
            .WithNote(DiagnosticMessages.ResolvedFromRootNote(resolved.RootDirectory));

        var fileDerivedModulePath = WorkspaceModuleLocator.TryGetModulePathFromRoot(
            resolved.RootDirectory,
            resolved.FilePath);
        if (!string.IsNullOrWhiteSpace(fileDerivedModulePath))
            diagnostic.WithNote(DiagnosticMessages.FilesystemModulePathNote(fileDerivedModulePath));

        _diagnostics.Add(diagnostic);
        return false;
    }

    private bool TryAddDuplicateImportCandidateDiagnostic(
        string? packageAlias,
        IReadOnlyList<string> importPath,
        IReadOnlyList<ResolvedWorkspaceModuleFile> candidates)
    {
        var distinctCandidates = candidates
            .DistinctBy(static candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctCandidates.Count <= 1)
            return false;

        var moduleKey = ToImportKey(packageAlias, importPath);
        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.DuplicateModulePath(moduleKey),
                "E3000")
            .WithNote(DiagnosticMessages.RequestedImportNote(moduleKey));

        foreach (var candidate in distinctCandidates)
        {
            diagnostic
                .WithNote(DiagnosticMessages.FileNote(candidate.FilePath))
                .WithNote(DiagnosticMessages.ResolvedFromRootNote(candidate.RootDirectory));
        }

        _diagnostics.Add(diagnostic);
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
            diagnostic.WithNote(DiagnosticMessages.SearchedRootNote(root));

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

    private void AddImportParseFailedDiagnostic(
        IReadOnlyList<string> importPath,
        string? importFile,
        IReadOnlyList<Diagnostic.Diagnostic> parseDiagnostics)
    {
        var importText = string.Join(WellKnownStrings.Separators.Path, importPath);
        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.ImportedModuleFailedToParse(importText),
            "E4001");

        if (!string.IsNullOrWhiteSpace(importFile))
            diagnostic.WithNote(DiagnosticMessages.FileNote(importFile));

        if (parseDiagnostics.Count > 0)
            diagnostic.WithNote(DiagnosticMessages.ReportedDiagnosticsNote(parseDiagnostics.Count));

        _diagnostics.Add(diagnostic);
    }

    private void AddDuplicateModulePathDiagnostic(string modulePath, ModuleDecl existingModule, ModuleDecl duplicateModule)
    {
        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.DuplicateModulePath(modulePath),
            "E3000");

        if (!duplicateModule.Span.Equals(SourceSpan.Empty))
            diagnostic.WithLabel(duplicateModule.Span, DiagnosticMessages.DuplicateModuleDeclarationLabel);

        if (!existingModule.Span.Equals(SourceSpan.Empty))
        {
            diagnostic.WithRelated(
                Diagnostic.Diagnostic.Note(DiagnosticMessages.FirstDeclarationOfModuleHere(modulePath))
                    .WithLabel(existingModule.Span, DiagnosticMessages.FirstModuleDeclarationLabel));
        }

        _diagnostics.Add(diagnostic);
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
                break;

            if (!visited.Add(next))
                break;

            current = next;
        }

        chain.Reverse();
        return string.Join(" -> ", chain);
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
        return ResolveImportModuleCandidates(entryFilePath, packageAlias, modulePath).FirstOrDefault();
    }

    private IReadOnlyList<ResolvedWorkspaceModuleFile> ResolveImportModuleCandidates(
        string entryFilePath,
        string? packageAlias,
        IReadOnlyList<string> modulePath)
    {
        if (!string.IsNullOrWhiteSpace(packageAlias))
        {
            return _options.PackageImportRoots.TryGetValue(packageAlias, out var packageRoots)
                ? WorkspaceModuleLocator.ResolveImportModuleCandidatesFromRoots(modulePath, packageRoots)
                : [];
        }

        return WorkspaceModuleLocator.ResolveImportModuleCandidates(entryFilePath, modulePath, _options.ImportSearchRoots);
    }

    private void RecordPhase(CompilationPhase phase, Stopwatch sw)
    {
        RecordPhase(phase, sw, GC.GetAllocatedBytesForCurrentThread());
    }

    private void RecordPhase(CompilationPhase phase, Stopwatch sw, long allocatedBytesBefore)
    {
        sw.Stop();
        _phaseTimes[phase] = sw.Elapsed;
        _phaseAllocations[phase] = Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore);
    }

    private void SetProfilingCounter(string name, long value)
    {
        if (_options.EnableDetailedProfiling)
        {
            _profilingCounters[name] = value;
        }
    }

    private void AddProfilingCounter(string name, long value)
    {
        if (_options.EnableDetailedProfiling)
        {
            _profilingCounters[name] = _profilingCounters.GetValueOrDefault(name) + value;
        }
    }

    private void SetProfilingCounters(IReadOnlyDictionary<string, long> counters)
    {
        if (!_options.EnableDetailedProfiling)
        {
            return;
        }

        foreach (var (name, value) in counters)
        {
            _profilingCounters[name] = value;
        }
    }

    private static Diagnostic.Diagnostic ConvertAffineDiagnostic(string fnName, AffineDiagnostic diag)
    {
        var d = Diagnostic.Diagnostic.Error(diag.Message, "E1001").WithNote(DiagnosticMessages.FunctionNote(fnName));
        if (diag.Variable.IsValid) d.WithNote(DiagnosticMessages.LocalNote(diag.Variable.Value));
        return d;
    }

    private static Diagnostic.Diagnostic ConvertBorrowDiagnostic(string fnName, BorrowDiagnostic diag)
    {
        var d = Diagnostic.Diagnostic.Error(diag.Message, "E1003")
            .WithNote(DiagnosticMessages.FunctionNote(fnName))
            .WithNote(DiagnosticMessages.MirLocationShortNote(diag.Location.Block.Value, diag.Location.Index));
        if (!string.IsNullOrEmpty(diag.Hint)) d.WithHelp(diag.Hint);
        return d;
    }

    #endregion

    #region Result Builder

    private CompilationResult BuildResult(bool success, CompilationPhase completedPhase, TimeSpan totalTime)
    {
        ParseOutput? parse = null;
        NameResolutionOutput? nameRes = null;
        TypeInferenceOutput? typeInf = null;
        EffectInferenceOutput? abilityInf = null;
        HirOutput? hirOut = null;
        MirOutput? mirOut = null;
        BorrowOutput? borrowOut = null;
        CodeGenOutput? codeGenOut = null;

        var parseCache = _engine.TryGetCached<string, ParseOutput>(_sourcePath);
        if (parseCache != null) parse = parseCache;

        var nameCache = _engine.TryGetCached<string, NameResolutionOutput>(_sourcePath);
        if (nameCache != null) nameRes = nameCache;

        var typeCache = _engine.TryGetCached<string, TypeInferenceOutput>(_sourcePath);
        if (typeCache != null) typeInf = typeCache;

        var abilityCache = _engine.TryGetCached<string, EffectInferenceOutput>(_sourcePath);
        if (abilityCache != null) abilityInf = abilityCache;

        var hirCache = _engine.TryGetCached<string, HirOutput>(_sourcePath);
        if (hirCache != null) hirOut = hirCache;

        var mirCache = _engine.TryGetCached<string, MirOutput>(_sourcePath);
        if (mirCache != null) mirOut = mirCache;

        var borrowCache = _engine.TryGetCached<string, BorrowOutput>(_sourcePath);
        if (borrowCache != null) borrowOut = borrowCache;

        var codeGenCache = _engine.TryGetCached<string, CodeGenOutput>(_sourcePath);
        if (codeGenCache != null) codeGenOut = codeGenCache;

        return new CompilationResult
        {
            Success = success,
            CompletedPhase = completedPhase,
            Diagnostics = _diagnostics,
            ComptimeTrace = _comptimeExecution.Trace.Snapshot(),
            InputFile = _sourcePath,
            ImportSearchRoots = _options.ImportSearchRoots,
            NoImplicitPrelude = _options.NoImplicitPrelude,
            SourceText = _sourceText,
            Tokens = parse?.Tokens,
            Ast = parse?.Ast,
            SymbolTable = nameRes?.SymbolTable,
            TypeInferer = typeInf?.TypeInferer,
            TypeAnalysisIncomplete = typeInf?.TypeInferer?.TypeAnalysisIncomplete ?? false,
            TypeAnalysisIncompleteReason = typeInf?.TypeInferer?.TypeAnalysisIncompleteReason,
            TypeErrorLimit = typeInf?.TypeInferer?.TypeErrorLimit ?? 0,
            SuppressedTypeDiagnosticCount = typeInf?.TypeInferer?.SuppressedTypeDiagnosticCount ?? 0,
            SuppressedTypeConstraintCount = typeInf?.TypeInferer?.SuppressedTypeConstraintCount ?? 0,
            EffectInferer = abilityInf?.EffectInferer,
            HirModule = hirOut?.HirModule,
            MirModule = mirOut?.MirModule,
            BorrowCheckResult = borrowOut?.BorrowCheckResult,
            LlvmModule = codeGenOut?.LlvmModule,
            LlvmIrText = codeGenOut?.LlvmIrText,
            Documentation = Doc.DocCommentExtractor.Extract(_sourceText),
            TotalTime = totalTime,
            PhaseTimes = _phaseTimes,
            PhaseAllocations = _phaseAllocations,
            ProfilingCounters = new Dictionary<string, long>(_profilingCounters, StringComparer.Ordinal)
        };
    }

    #endregion

    #region Descriptors

    internal sealed class ParseDescriptor : QueryDescriptor<string, ParseOutput>
    {
        public override IQueryCache<string, ParseOutput> CreateCache() => new DefaultQueryCache<string, ParseOutput>();
    }

    internal sealed class NameResolutionDescriptor : QueryDescriptor<string, NameResolutionOutput>
    {
        public override IQueryCache<string, NameResolutionOutput> CreateCache() => new DefaultQueryCache<string, NameResolutionOutput>();
    }

    internal sealed class TypeInferenceDescriptor : QueryDescriptor<string, TypeInferenceOutput>
    {
        public override IQueryCache<string, TypeInferenceOutput> CreateCache() => new DefaultQueryCache<string, TypeInferenceOutput>();
    }

    internal sealed class EffectInferenceDescriptor : QueryDescriptor<string, EffectInferenceOutput>
    {
        public override IQueryCache<string, EffectInferenceOutput> CreateCache() => new DefaultQueryCache<string, EffectInferenceOutput>();
    }

    internal sealed class HirDescriptor : QueryDescriptor<string, HirOutput>
    {
        public override IQueryCache<string, HirOutput> CreateCache() => new DefaultQueryCache<string, HirOutput>();
    }

    internal sealed class MirDescriptor : QueryDescriptor<string, MirOutput>
    {
        public override IQueryCache<string, MirOutput> CreateCache() => new DefaultQueryCache<string, MirOutput>();
    }

    internal sealed class BorrowDescriptor : QueryDescriptor<string, BorrowOutput>
    {
        public override IQueryCache<string, BorrowOutput> CreateCache() => new DefaultQueryCache<string, BorrowOutput>();
    }

    internal sealed class CodeGenDescriptor : QueryDescriptor<string, CodeGenOutput>
    {
        public override IQueryCache<string, CodeGenOutput> CreateCache() => new DefaultQueryCache<string, CodeGenOutput>();
    }

    #endregion
}
