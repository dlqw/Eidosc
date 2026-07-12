using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Utils;
using System.Diagnostics;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Semantic;

/// <summary>
/// 名称解析器 - 将所有标识符绑定到其定义
/// </summary>
public sealed partial class NameResolver
{
    private const string ReservedSelfTypeName = WellKnownStrings.Keywords.Self;
    private const string NonExhaustivePatternWarningCode = "W4200";
    private const string UnreachablePatternWarningCode = "W4201";
    private const string RedundantFunctionBodyMatchWarningCode = "W4300";
    private const string OverlappingImplRegistrationCode = "E3004";
    private const string ProofObligationCode = "E4104";
    private readonly string _sourceText;
    private readonly IReadOnlyList<string> _importSearchRoots;
    private readonly SymbolTable _symbolTable;
    private readonly PathResolver _pathResolver;
    private readonly ImportResolver _importResolver;
    private readonly NameLookupService _lookupService;
    private readonly AttributeBinder _attributeBinder;
    private readonly PatternCoveragePass _patternCoveragePass;
    private readonly Dictionary<SymbolId, ImportScope> _importScopes = new();
    private readonly HashSet<SymbolId> _importsProcessed = [];
    private readonly HashSet<SymbolId> _importsProcessing = [];
    private readonly Dictionary<SymbolId, Scope> _moduleScopes = new();
    private readonly Dictionary<SymbolId, ModuleDecl> _moduleDeclarations = new();
    private readonly Dictionary<SymbolId, AdtDef> _adtDefinitions = new();
    private readonly Dictionary<SymbolId, TraitDef> _traitDefinitions = new();
    private readonly Dictionary<string, InstanceDecl> _instanceDeclarations = new(StringComparer.Ordinal);
    private readonly Dictionary<Scope, Dictionary<string, List<FunctionOverloadDeclaration>>> _functionOverloadDeclarations = new();
    private readonly Dictionary<SymbolId, SymbolId> _traitOwnerModules = new();
    private readonly Dictionary<SymbolId, CtorPatternShape> _ctorPatternShapes = new();
    private readonly Dictionary<string, long> _profilingCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImplOverlapCheckSnapshotEntry> _implOverlapSnapshotEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImplOverlapCheckSnapshotEntry> _previousImplOverlapSnapshotEntries = new(StringComparer.Ordinal);
    private HashSet<SymbolId>? _traitImplMethodIds;
    private readonly List<string> _patternDiagnosticContext = [];
    private int _traitSignatureDepth;
    private int _instanceMethodDeclarationDepth;
    private SymbolId _rootModule = SymbolId.None;
    private SymbolId _currentModule = SymbolId.None;
    private string? _rootInputFilePath;
    private readonly List<EidoscDiagnostic> _diagnostics = [];

    public bool UsePrecompiledImportSignatureOnly { get; set; }

    public ImplOverlapCheckSnapshot? PreviousImplOverlapCheckSnapshot
    {
        set
        {
            _previousImplOverlapSnapshotEntries.Clear();
            if (value?.Entries == null ||
                !string.Equals(value.SchemaVersion, ImplOverlapCheckSnapshot.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var entry in value.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.QueryKey))
                {
                    _previousImplOverlapSnapshotEntries[entry.QueryKey] = entry;
                }
            }
        }
    }

    public ImplOverlapCheckSnapshot CreateImplOverlapCheckSnapshot() =>
        new(
            ImplOverlapCheckSnapshot.CurrentSchemaVersion,
            _implOverlapSnapshotEntries.Values
                .OrderBy(static entry => entry.QueryKey, StringComparer.Ordinal)
                .ToArray());

    /// <summary>
    /// 通过 link 指令收集的外部库名称列表
    /// </summary>
    private readonly List<string> _linkLibraries = [];

    /// <summary>
    /// 用户自定义操作符注册表
    /// </summary>
    private readonly CustomOperatorTable _customOperators = new();

    /// <summary>
    /// 获取自定义操作符表
    /// </summary>
    public CustomOperatorTable CustomOperators => _customOperators;

    /// <summary>
    /// 获取通过 eidos.toml 配置的外部库名称列表
    /// </summary>
    public List<string> LinkLibraries => _linkLibraries;

    /// <summary>
    /// 合并 eidos.toml 配置的 FFI 链接库。
    /// </summary>
    public void AddConfigLinkLibraries(IReadOnlyList<string> libraries)
    {
        foreach (var lib in libraries)
        {
            if (!_linkLibraries.Contains(lib))
                _linkLibraries.Add(lib);
        }
    }

    private sealed record CtorPatternShape(
        bool IsShapeKnown,
        int PositionalArity,
        HashSet<string> NamedFields);

    private sealed record ImportSuggestionCandidate(
        string Message,
        SourceSpan? Span,
        string? Replacement);

    private sealed record FunctionOverloadDeclaration(
        string Name,
        string SignatureKey,
        SourceSpan Span,
        SymbolId SymbolId);

    private sealed record ImplTraitReference(
        List<string> Path,
        List<string> TypeArgTexts,
        List<TypeNode> TypeArgs);

    private sealed record PatternCoverageFacts(
        List<PatternUsefulnessBranchFact> BranchFacts,
        Dictionary<int, EidosAstNode> BranchGuardsByIndex,
        bool HasBoolLiteralPattern);

    private sealed record PatternCoverageContext(
        PatternUsefulnessSummary Summary,
        IReadOnlyList<int> UnresolvedGuardBranchIndices,
        IReadOnlyList<string> UnresolvedGuardBranchHints,
        bool HasGuardedBranchesForCoverage,
        Dictionary<int, PatternUsefulnessBranchFact> BranchFactsByIndex,
        List<SuppressedCoveredWarningTrace> SuppressedCoveredWarnings,
        HashSet<int> HandledCoveredBranches);

    /// <summary>
    /// 符号表
    /// </summary>
    public SymbolTable SymbolTable => _symbolTable;

    /// <summary>
    /// 路径解析器
    /// </summary>
    public PathResolver PathResolver => _pathResolver;

    /// <summary>
    /// 导入解析器
    /// </summary>
    public ImportResolver ImportResolver => _importResolver;

    /// <summary>
    /// 诊断信息
    /// </summary>
    public List<EidoscDiagnostic> Diagnostics => _diagnostics;

    public IReadOnlyDictionary<string, long> GetProfilingCounters()
    {
        var counters = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["Namer.symbols.count"] = _symbolTable.Symbols.Count,
            ["Namer.modules.count"] = _moduleDeclarations.Count,
            ["Namer.importScopes.count"] = _importScopes.Count,
            ["Namer.processedImports.count"] = _importsProcessed.Count,
            ["Namer.moduleScopes.count"] = _moduleScopes.Count,
            ["Namer.adtDefinitions.count"] = _adtDefinitions.Count,
            ["Namer.traitDefinitions.count"] = _traitDefinitions.Count,
            ["Namer.instanceDeclarations.count"] = _instanceDeclarations.Count,
            ["Namer.functionOverloadScopes.count"] = _functionOverloadDeclarations.Count,
            ["Namer.implOverlapSnapshot.entries"] = _implOverlapSnapshotEntries.Count,
            ["Namer.implOverlapPreviousSnapshot.entries"] = _previousImplOverlapSnapshotEntries.Count,
            ["Namer.diagnostics.count"] = _diagnostics.Count
        };

        foreach (var (name, value) in _symbolTable.Modules.GetProfilingCounters())
        {
            counters[name] = value;
        }

        foreach (var (name, value) in _profilingCounters)
        {
            counters[name] = value;
        }

        return counters;
    }

    public NameResolver()
    {
        _sourceText = string.Empty;
        _importSearchRoots = [];
        _symbolTable = new SymbolTable();
        _pathResolver = _symbolTable.PathResolver;
        _importResolver = new ImportResolver(_symbolTable, _pathResolver, _moduleDeclarations);
        _lookupService = new NameLookupService(_symbolTable, _pathResolver);
        _attributeBinder = new AttributeBinder();
        _patternCoveragePass = new PatternCoveragePass(AnalyzePatternBranchCoverage);
        _symbolTable.InitializeGlobalScope();
    }

    public NameResolver(SymbolTable symbolTable)
        : this(symbolTable, string.Empty, null)
    {
    }

    public NameResolver(SymbolTable symbolTable, string sourceText)
        : this(symbolTable, sourceText, null)
    {
    }

    public NameResolver(SymbolTable symbolTable, string sourceText, IReadOnlyList<string>? importSearchRoots)
    {
        _sourceText = sourceText;
        _importSearchRoots = importSearchRoots ?? [];
        _symbolTable = symbolTable;
        _pathResolver = symbolTable.PathResolver;
        _importResolver = new ImportResolver(_symbolTable, _pathResolver, _moduleDeclarations);
        _lookupService = new NameLookupService(_symbolTable, _pathResolver);
        _attributeBinder = new AttributeBinder();
        _patternCoveragePass = new PatternCoveragePass(AnalyzePatternBranchCoverage);
        _symbolTable.InitializeGlobalScope();
    }

    #region 入口点

    /// <summary>
    /// 解析模块
    /// </summary>
    public bool Resolve(ModuleDecl module)
    {
        _rootInputFilePath = module.Span.FilePath;
        _implOverlapSnapshotEntries.Clear();
        using (MeasurePass("root_module"))
        {
            var moduleName = module.Path.Count > 0 ? module.Path[^1] : WellKnownStrings.SpecialNames.Main;
            _currentModule = _symbolTable.DeclareModule(
                moduleName,
                module.Path,
                module.Span,
                usesExplicitExports: module.UsesExplicitExports,
                packageAlias: module.PackageAlias,
                packageInstanceKey: module.PackageInstanceKey);
            _rootModule = _currentModule;
            module.SymbolId = _currentModule;
            _moduleDeclarations[_currentModule] = module;
            if (_symbolTable.CurrentScope != null)
            {
                _moduleScopes[_currentModule] = _symbolTable.CurrentScope;
            }
        }

        // 第一遍：先预声明整棵模块树，避免同一编译单元内后置模块无法被前置模块 import。
        using (MeasurePass("declare_nested_modules"))
        {
            DeclareNestedModules(module);
        }

        // 第二遍：收集所有模块成员声明
        using (MeasurePass("collect_declarations"))
        {
            CollectModuleDeclarationsRecursive(module, _currentModule);
        }

        // 第二遍半：检测 supertrait 环（所有 trait 声明已收集完毕，ParentTraits 已填充）
        using (MeasurePass("detect_supertrait_cycles"))
        {
            DetectSupertraitCycles();
        }

        // 第三遍：处理导入语句
        using (MeasurePass("process_imports"))
        {
            ProcessImportsRecursive(module, _currentModule);
        }

        // 第四遍：解析所有引用
        using (MeasurePass("resolve_references"))
        {
            ResolveModuleReferencesRecursive(module, _currentModule);
        }

        // Proof validation removed during migration

        return !_diagnostics.Exists(d => d.Level == EidoscDiagnosticLevel.Error);
    }

    private ProfilePassScope MeasurePass(string name) => new(this, name);

    private void RecordPassMetric(string name, long elapsedTicks, long allocatedBytes)
    {
        _profilingCounters[$"Namer.pass.{name}.ticks"] = elapsedTicks;
        _profilingCounters[$"Namer.pass.{name}.allocatedBytes"] = allocatedBytes;
    }

    private void AddCounter(string name, long delta = 1)
    {
        _profilingCounters.TryGetValue(name, out var current);
        _profilingCounters[name] = current + delta;
    }

    private void AddAllocationCounter(string name, long allocatedBytes)
    {
        if (allocatedBytes <= 0)
        {
            return;
        }

        AddCounter(name, allocatedBytes);
    }

    private readonly struct ProfilePassScope : IDisposable
    {
        private readonly NameResolver _resolver;
        private readonly string _name;
        private readonly long _startTimestamp;
        private readonly long _allocatedBytesBefore;

        public ProfilePassScope(NameResolver resolver, string name)
        {
            _resolver = resolver;
            _name = name;
            _startTimestamp = Stopwatch.GetTimestamp();
            _allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - _startTimestamp;
            var allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _allocatedBytesBefore);
            _resolver.RecordPassMetric(_name, elapsedTicks, allocatedBytes);
        }
    }

    #endregion

    #region 第二遍：解析引用

    private void ResolveDeclarationReferences(Declaration decl)
    {
        switch (decl)
        {
            case FuncDef func:
                ResolveFuncDefReferences(func);
                break;
            case FuncDecl funcDecl:
                ResolveFuncDeclReferences(funcDecl);
                break;
            case LetDecl letDecl:
                ResolveLetDeclReferences(letDecl);
                break;
            case LetQuestionDecl letQuestionDecl:
                ResolveLetQuestionDeclReferences(letQuestionDecl);
                break;
            case Assignment assign:
                ResolveAssignmentReferences(assign);
                break;
            case AdtDef adt:
                ResolveAdtDefReferences(adt);
                break;
            case EffectDef ability:
                ResolveEffectDefReferences(ability);
                break;
            case TraitDef trait:
                ResolveTraitDefReferences(trait);
                break;
            case InstanceDecl instance:
                ResolveInstanceDeclReferences(instance);
                break;
            case ProofDecl:
                // Proof resolution removed during migration
                break;
            case ImportDecl:
            case ModuleDecl:
                // 导入和子模块由递归模块遍历统一处理。
                break;
        }
    }

    private void ResolveFuncDefReferences(FuncDef func)
    {
        using var scopeGuard = _symbolTable.PushScopeGuard(ScopeKind.Function);

        foreach (var typeParam in func.TypeParams)
        {
            DeclareTypeParameterIfValid(typeParam);
            ResolveTypeParamReferences(typeParam);
        }
        UpdateFunctionTypeParamSymbols(func.SymbolId, func.TypeParams);

        foreach (var typeNode in func.Signature)
        {
            ResolveTypeReferences(typeNode);
        }

        ResolveEffectRequirements(func.RequiredAbilities);

        TryRegisterTraitImplFromAttributes(func);

        if (ShouldUseSignatureOnlyForTrustedPrecompiledFunction(func))
        {
            func.SetPatternBodyExhaustive(true);
            return;
        }

        for (var i = 0; i < func.Body.Count; i++)
        {
            WarnIfFunctionBranchUsesRedundantMatch(func.Body[i]);
            ResolvePatternBranchReferences(func.Body[i], i + 1, isParameterBranch: true);
        }

        func.SetPatternBodyExhaustive(ShouldSkipTrustedPrecompiledPatternCoverage(func.Span)
            ? true
            : _patternCoveragePass.Analyze(new PatternCoverageRequest(
                func.Body,
                func.Span,
                $"function '{func.Name}'",
                GuardSubjectName: null)));
    }

    private void UpdateFunctionTypeParamSymbols(SymbolId functionId, IReadOnlyList<TypeParam> typeParams)
    {
        if (!functionId.IsValid ||
            _symbolTable.GetSymbol(functionId) is not FuncSymbol functionSymbol)
        {
            return;
        }

        _symbolTable.UpdateSymbol(functionSymbol with
        {
            TypeParams = typeParams.Select(typeParam => typeParam.SymbolId).ToList()
        });
    }

    #endregion

    #region 表达式解析

    private bool AnalyzePatternBranchCoverage(
        IReadOnlyList<PatternBranch> branches,
        SourceSpan ownerSpan,
        string ownerDescription,
        string? guardSubjectName)
    {
        if (branches.Count == 0)
        {
            return false;
        }

        var facts = BuildPatternCoverageFacts(branches, guardSubjectName);
        var context = CreatePatternCoverageContext(facts.BranchFacts);

        EmitPatternUnreachableWarnings(context);
        EmitAdditionalPatternCoveredWarnings(facts, context);
        EmitNonExhaustivePatternCoverageWarning(ownerSpan, ownerDescription, context);
        return context.Summary.IsExhaustive;
    }

    private bool ShouldSkipTrustedPrecompiledPatternCoverage(SourceSpan span)
    {
        if (!IsTrustedPrecompiledImportedSpan(span))
        {
            return false;
        }

        return true;
    }

    private bool ShouldUseSignatureOnlyForTrustedPrecompiledFunction(FuncDef func)
    {
        if (!UsePrecompiledImportSignatureOnly ||
            func.Body.Count == 0 ||
            !IsTrustedPrecompiledImportedSpan(func.Span))
        {
            return false;
        }

        AddCounter("Namer.precompiledImportSignatureOnly.functions");
        return true;
    }

    private bool IsTrustedPrecompiledImportedSpan(SourceSpan span)
    {
        if (_currentModule == _rootModule)
        {
            return false;
        }

        var filePath = span.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_rootInputFilePath) &&
            string.Equals(
                Path.GetFullPath(filePath),
                Path.GetFullPath(_rootInputFilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return filePath.Replace('\\', '/').Contains("/Stdlib/Precompiled/", StringComparison.Ordinal);
    }

    private static PatternWitness CreateListCoverageWitness(ListCoverageCase listCase, bool preferCharLiteralHints)
    {
        var displayText = FormatListCoverageHintCase(listCase, preferCharLiteralHints);
        var stableKey = listCase.IsAtLeast
            ? $"list:at-least:{listCase.Length}"
            : !string.IsNullOrWhiteSpace(listCase.BoolVectorKey)
                ? $"list:exact:{listCase.BoolVectorKey}"
                : $"list:len:{listCase.Length}";
        return new PatternWitness(PatternWitnessKind.ListShape, displayText, stableKey);
    }

    private static bool TryResolveAdtCoverageTarget(
        Pattern pattern,
        SymbolTable symbolTable,
        out SymbolId adtId)
    {
        return PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
            pattern,
            symbolTable,
            out adtId,
            out _);
    }


    private static bool IsUnresolvedGuardBranchForCoverageTarget(
        PatternUsefulnessBranchFact branch,
        PatternCoverageTargetKind coverageTarget)
    {
        if (!branch.IsGuarded || branch.GuardConstant != null)
        {
            return false;
        }

        return coverageTarget switch
        {
            PatternCoverageTargetKind.None => false,
            PatternCoverageTargetKind.Bool or
            PatternCoverageTargetKind.TupleBool or
            PatternCoverageTargetKind.List or
            PatternCoverageTargetKind.Adt => !HasExactCoverageForTarget(branch, coverageTarget),
            _ => !HasAnyExactCoverage(branch)
        };
    }

    private static bool HasExactCoverageForTarget(
        PatternUsefulnessBranchFact branch,
        PatternCoverageTargetKind coverageTarget)
    {
        return coverageTarget switch
        {
            PatternCoverageTargetKind.Bool => branch.HasExactBoolCoverage,
            PatternCoverageTargetKind.TupleBool => branch.HasExactTupleBoolCoverage,
            PatternCoverageTargetKind.List => branch.HasExactListCoverage,
            PatternCoverageTargetKind.Adt => branch.HasExactAdtCoverage,
            _ => false
        };
    }

    private static bool HasAnyExactCoverage(PatternUsefulnessBranchFact branch)
    {
        return branch.HasExactBoolCoverage ||
               branch.HasExactTupleBoolCoverage ||
               branch.HasExactListCoverage ||
               branch.HasExactAdtCoverage;
    }


    internal enum SuppressedCoveredWarningKind
    {
        Adt,
        List
    }

    internal readonly record struct SuppressedCoveredWarningTrace(
        SuppressedCoveredWarningKind Kind,
        int BranchIndex,
        IReadOnlyList<int> CoveringBranchIndices,
        IReadOnlyList<string> Reasons);

    private void AddUnreachablePatternWarning(SourceSpan span, int currentBranchIndex, int previousIrrefutableBranchIndex)
    {
        var message = DiagnosticMessages.UnreachablePatternBranchPreviousIrrefutable(
            currentBranchIndex,
            previousIrrefutableBranchIndex);
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Warning, message, UnreachablePatternWarningCode);
        diag.WithLabel(span, DiagnosticMessages.UnreachablePatternBranchLabel);
        diag.WithNote(DiagnosticMessages.UnreachablePatternMoveEarlierOrAddGuardNote);
        _diagnostics.Add(diag);
    }

    private void AddUnreachableFalseGuardWarning(SourceSpan span, int branchIndex)
    {
        var message = DiagnosticMessages.UnreachablePatternBranchFalseGuard(branchIndex);
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Warning, message, UnreachablePatternWarningCode);
        diag.WithLabel(span, DiagnosticMessages.UnreachablePatternBranchLabel);
        diag.WithNote(DiagnosticMessages.UnreachablePatternRemoveOrChangeGuardNote);
        _diagnostics.Add(diag);
    }

    private void AddUnreachableUnsatisfiablePatternWarning(SourceSpan span, int branchIndex)
    {
        var message = DiagnosticMessages.UnreachablePatternBranchUnsatisfiable(branchIndex);
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Warning, message, UnreachablePatternWarningCode);
        diag.WithLabel(span, DiagnosticMessages.UnreachablePatternBranchLabel);
        diag.WithNote(DiagnosticMessages.UnreachablePatternCannotMatchNote);
        _diagnostics.Add(diag);
    }

    #endregion

    private static readonly HashSet<ResolutionKind> TypeResolutionKinds =
    [
        ResolutionKind.Type
    ];

    private static readonly HashSet<ResolutionKind> EffectResolutionKinds =
    [
        ResolutionKind.Effect
    ];

    private PathResolutionResult ResolvePathWithImports(IReadOnlyList<string> path)
        => ResolvePathWithImports(path, allowedKinds: null);

    private PathResolutionResult ResolvePathWithImports(
        IReadOnlyList<string> path,
        IReadOnlySet<ResolutionKind>? allowedKinds)
    {
        if (path.Count >= 3)
        {
            var packageQualifiedResult = ResolvePackageQualifiedPath(path[0], path.Skip(1).ToList(), allowedKinds);
            if (packageQualifiedResult.IsSuccess)
            {
                return packageQualifiedResult;
            }
        }

        if (TryResolveImportedModulePath(path, out var importedModuleResult, allowedKinds))
        {
            return importedModuleResult;
        }

        var result = _pathResolver.Resolve(path, _currentModule);
        if (result.IsSuccess && (allowedKinds == null || allowedKinds.Contains(result.Kind)))
        {
            return result;
        }

        if (TryResolveCurrentScopeEffectOperationPath(path, out var abilityOperationResult))
        {
            return abilityOperationResult;
        }

        if (TryResolveCurrentModuleQualifiedPath(path, out var currentModuleResult))
        {
            return currentModuleResult;
        }

        return result;
    }

    private bool TryResolveCurrentModuleQualifiedPath(
        IReadOnlyList<string> path,
        out PathResolutionResult result)
    {
        result = PathResolutionResult.NotFound(DiagnosticMessages.CannotResolvePath(
            string.Join(WellKnownStrings.Separators.Path, path)));
        if (path.Count < 2 ||
            !_currentModule.IsValid ||
            _symbolTable.Modules.GetModule(_currentModule) is not { } currentModule)
        {
            return false;
        }

        var qualifier = path.Take(path.Count - 1).ToList();
        if (!currentModule.Path.SequenceEqual(qualifier) &&
            !string.Equals(currentModule.Name, qualifier[^1], StringComparison.Ordinal))
        {
            return false;
        }

        var memberName = path[^1];
        if (TryLookupSameNamedTraitMember(_currentModule, memberName, out var traitMember))
        {
            result = traitMember;
            return true;
        }

        if (_symbolTable.Modules.TryLookupAccessibleBinding(
                _currentModule,
                memberName,
                _currentModule,
                out var binding))
        {
            result = PathResolutionResult.Found(binding.SymbolId, binding.Kind);
            return true;
        }

        if (TryLookupSameNamedEffectMember(_currentModule, memberName, out var abilityMember))
        {
            result = abilityMember;
            return true;
        }

        return false;
    }

    private PathResolutionResult ResolvePackageQualifiedPath(string? packageAlias, IReadOnlyList<string> pathAfterPackage)
        => ResolvePackageQualifiedPath(packageAlias, pathAfterPackage, allowedKinds: null);

    private PathResolutionResult ResolvePackageQualifiedPath(
        string? packageAlias,
        IReadOnlyList<string> pathAfterPackage,
        IReadOnlySet<ResolutionKind>? allowedKinds)
    {
        if (string.IsNullOrWhiteSpace(packageAlias) || pathAfterPackage.Count < 2)
        {
            return PathResolutionResult.NotFound(DiagnosticMessages.CannotResolvePath(
                string.Join(WellKnownStrings.Separators.Path, pathAfterPackage)));
        }

        for (var splitIndex = pathAfterPackage.Count - 1; splitIndex >= 1; splitIndex--)
        {
            var modulePath = pathAfterPackage.Take(splitIndex).ToList();
            var remainingSegments = pathAfterPackage.Skip(splitIndex).ToList();
            var moduleId = _symbolTable.Modules.LookupModuleByPath(packageAlias, modulePath);
            if (!moduleId.HasValue || !moduleId.Value.IsValid)
            {
                continue;
            }

            var result = ResolveImportedModulePath(moduleId.Value, remainingSegments, allowedKinds);
            if (result.IsSuccess)
            {
                return result;
            }
        }

        var displayPath = $"{packageAlias}{WellKnownStrings.Separators.Path}{string.Join(WellKnownStrings.Separators.ModulePath, pathAfterPackage)}";
        return PathResolutionResult.NotFound(DiagnosticMessages.CannotResolvePath(displayPath));
    }

    private string BuildUndefinedEffectDiagnostic(string abilityDisplayName)
    {
        var shortName = ExtractEffectShortName(abilityDisplayName);
        if (string.IsNullOrWhiteSpace(shortName))
        {
            return DiagnosticMessages.UndefinedEffect(abilityDisplayName);
        }

        var candidates = FindEffectCandidatesByShortName(shortName, maxCount: 5);
        if (candidates.Count == 0)
        {
            return DiagnosticMessages.UndefinedEffect(abilityDisplayName);
        }

        return DiagnosticMessages.UndefinedEffectWithCandidates(
            abilityDisplayName,
            shortName,
            string.Join(", ", candidates));
    }

    private List<string> FindEffectCandidatesByShortName(string shortName, int maxCount)
    {
        var result = new List<string>();
        foreach (var entry in _symbolTable.Symbols)
        {
            if (entry.Value is not EffectSymbol ability ||
                !string.Equals(ability.Name, shortName, StringComparison.Ordinal))
            {
                continue;
            }

            var displayName = TryFormatQualifiedEffectName(entry.Key, ability);
            if (result.Contains(displayName, StringComparer.Ordinal))
            {
                continue;
            }

            result.Add(displayName);
            if (result.Count >= maxCount)
            {
                break;
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private string TryFormatQualifiedEffectName(SymbolId abilityId, EffectSymbol ability)
    {
        foreach (var module in _symbolTable.Modules.ModulePaths.Values
                     .Distinct()
                     .Select(_symbolTable.Modules.GetModule)
                     .Where(static module => module?.Path is { Count: > 0 })
                     .OrderBy(static module => string.Join(WellKnownStrings.Operators.Divide, module!.Path), StringComparer.Ordinal))
        {
            var moduleId = module!.Id;
            var prefix = string.Join(WellKnownStrings.Separators.ModulePath, module.Path);
            var qualifiedPath = TryFindQualifiedEffectPath(
                moduleId,
                prefix,
                abilityId,
                new HashSet<SymbolId>());
            if (!string.IsNullOrWhiteSpace(qualifiedPath))
            {
                return qualifiedPath;
            }
        }

        return ability.Name;
    }

    private string? TryFindQualifiedEffectPath(
        SymbolId moduleId,
        string prefix,
        SymbolId abilityId,
        HashSet<SymbolId> visitedModules)
    {
        if (!moduleId.IsValid ||
            string.IsNullOrWhiteSpace(prefix) ||
            !visitedModules.Add(moduleId))
        {
            return null;
        }

        try
        {
            foreach (var binding in _symbolTable.Modules.GetAccessibleBindings(moduleId, requesterModuleId: null))
            {
                if (string.IsNullOrWhiteSpace(binding.Name) || !binding.SymbolId.IsValid)
                {
                    continue;
                }

                if (binding.Kind == ResolutionKind.Effect &&
                    binding.SymbolId == abilityId)
                {
                    return $"{prefix}::{binding.Name}";
                }

                if (binding.Kind != ResolutionKind.Module)
                {
                    continue;
                }

                var nestedPath = TryFindQualifiedEffectPath(
                    binding.SymbolId,
                    $"{prefix}{WellKnownStrings.Separators.ModulePath}{binding.Name}",
                    abilityId,
                    visitedModules);
                if (!string.IsNullOrWhiteSpace(nestedPath))
                {
                    return nestedPath;
                }
            }

            return null;
        }
        finally
        {
            visitedModules.Remove(moduleId);
        }
    }

    private static string ExtractEffectShortName(string abilityDisplayName)
    {
        if (string.IsNullOrWhiteSpace(abilityDisplayName))
        {
            return string.Empty;
        }

        var normalized = abilityDisplayName.Trim();
        var pathIndex = normalized.LastIndexOf(WellKnownStrings.Separators.Path, StringComparison.Ordinal);
        var moduleIndex = normalized.LastIndexOf(WellKnownStrings.Separators.ModulePath, StringComparison.Ordinal);
        var index = Math.Max(pathIndex, moduleIndex);
        var separatorLength = index == pathIndex
            ? WellKnownStrings.Separators.Path.Length
            : WellKnownStrings.Separators.ModulePath.Length;
        if (index < 0 || index + separatorLength >= normalized.Length)
        {
            return normalized;
        }

        return normalized[(index + separatorLength)..];
    }

    private void DeclareTypeParameterIfValid(TypeParam typeParam)
    {
        var name = typeParam.Name;
        var span = typeParam.Span;
        if (TryReportReservedSelfDeclaration(name, span, "type parameter"))
        {
            return;
        }

        if (TryReportReservedInternalNameDeclaration(name, span, "type parameter"))
        {
            return;
        }

        if (typeParam.IsComptime && !IsSupportedComptimeTypeParameter(typeParam.ComptimeTypeAnnotation))
        {
            AddError(
                typeParam.ComptimeTypeAnnotation?.Span ?? span,
                $"Comptime generic parameter '{name}' currently supports only Type; value-level const generics are not implemented yet.");
        }

        typeParam.SymbolId = _symbolTable.DeclareTypeParameter(
            name,
            span,
            typeParam.GetKindText(),
            typeParam.IsComptime,
            FormatComptimeTypeAnnotation(typeParam.ComptimeTypeAnnotation));
    }

    private static bool IsSupportedComptimeTypeParameter(TypeNode? typeAnnotation)
    {
        return typeAnnotation is TypePath
        {
            PackageAlias: null,
            ModulePath.Count: 0,
            TypeName: WellKnownStrings.BuiltinTypes.Type,
            TypeArgs.Count: 0
        };
    }

    private static string? FormatComptimeTypeAnnotation(TypeNode? typeAnnotation)
    {
        return typeAnnotation switch
        {
            TypePath path => string.Join(
                WellKnownStrings.Separators.Path,
                path.ToQualifiedPathParts()),
            null => null,
            _ => typeAnnotation.GetType().Name
        };
    }

    private SymbolId DeclarePatternVariable(
        string name,
        SourceSpan span,
        bool isParameter,
        bool isPatternBound,
        PatternBindingMode bindingMode = PatternBindingMode.ByValue,
        bool isMutable = false,
        bool isComptime = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SymbolId.None;
        }

        if (TryReportReservedInternalNameDeclaration(name, span, isParameter ? "parameter" : "pattern binding"))
        {
            return SymbolId.None;
        }

        if (_symbolTable.CurrentScope?.GetLocalBindings().TryGetValue(name, out var existingId) == true)
        {
            var message = DiagnosticMessages.PatternVariableBoundMoreThanOnce(name);
            if (_patternDiagnosticContext.Count > 0)
            {
                AddPatternError(span, message);
            }
            else
            {
                AddError(span, message);
            }

            return existingId;
        }

        return _symbolTable.DeclareVariable(
            name,
            span,
            isMutable: isMutable,
            isParameter: isParameter,
            isPatternBound: isPatternBound,
            bindingMode: bindingMode,
            isComptime: isComptime);
    }

    private bool TryReportReservedSelfDeclaration(string name, SourceSpan span, string declarationKind)
    {
        if (!string.Equals(name, ReservedSelfTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        AddError(span, DiagnosticMessages.ReservedSelfDeclaration(declarationKind));
        return true;
    }

    private bool TryReportReservedInternalNameDeclaration(string name, SourceSpan span, string declarationKind)
    {
        if (!ReservedInternalNames.TryMatch(name, out var prefix))
        {
            return false;
        }

        AddReservedInternalNameError(span, name, prefix, declarationKind);
        return true;
    }

}
