using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.ErrorRecovery;
using Eidosc.Semantic;
using System.Diagnostics;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;
using System.Runtime.InteropServices;

namespace Eidosc.Types;

/// <summary>
/// 类型推断器 - 为所有表达式推断类型
/// 实现类型推断错误恢复
/// </summary>
public sealed partial class TypeInferer
{
    private const string TypeErrorCode = "E4000";
    private const string RangeMissingBoundaryCode = "E4010";
    private const string RangeInvalidOrderCode = "E4011";
    private const string RangeInvalidScrutineeCode = "E4012";
    private const string AsPatternTypeMismatchCode = "E4013";
    private const string ViewPatternInvalidViewExpressionCode = "E4014";
    private readonly SymbolTable _symbolTable;
    private readonly Substitution _substitution;
    private readonly List<EidoscDiagnostic> _diagnostics = [];
    private TypeEnv _env;
    private readonly ConstraintGenerator _constraintGenerator;
    private readonly ConstraintSolver _constraintSolver;

    /// <summary>
    /// 错误恢复上下文
    /// </summary>
    private readonly ErrorRecoveryContext _recoveryContext = ErrorRecoveryContext.ForTypeInference();

    /// <summary>
    /// 级联错误抑制 - 记录已报告错误的类型变量，避免重复报告
    /// </summary>
    private readonly HashSet<int> _reportedErrorVars = [];
    private readonly HashSet<string> _reportedDiagnostics = [];
    private int _nextKindVarId;
    private readonly Stack<Type> _functionReturnTypeStack = [];
    private readonly Stack<Dictionary<string, Type>> _typeParamEnvStack = [];
    private readonly Stack<Dictionary<string, Kind>> _typeParamKindStack = [];
    private readonly Stack<Dictionary<int, Kind>> _typeParamVarKindStack = [];
    private int _loopDepth;
    private readonly Dictionary<SymbolId, CtorTypeBinding> _ctorTypeBindings = [];
    private readonly Dictionary<SymbolId, AdtDef> _adtDefinitionsBySymbol = [];
    private readonly Dictionary<SymbolId, FuncDef> _functionDefinitionsBySymbol = [];
    private readonly Dictionary<SymbolId, TypeNode> _valueTypeAnnotationsBySymbol = [];
    private readonly Dictionary<SymbolId, TypeParamKindBinding> _typeParamKindBindingsBySymbol = [];
    private readonly Dictionary<SymbolId, Kind> _typeConstructorKindsBySymbol = [];
    private readonly Dictionary<SymbolId, AdtTypeParamConstraintBinding> _adtTypeParamConstraintBindings = [];
    private readonly Dictionary<SymbolId, TypeParamConstraintBinding> _ctorTypeParamConstraintBindings = [];
    private readonly Dictionary<SymbolId, IReadOnlyList<Type>> _functionTypeParametersBySymbol = [];
    private readonly Dictionary<(SymbolId ImplId, string AssociatedTypeName), TypeNode> _associatedTypeImplementations = [];
    private readonly Dictionary<(SymbolId ImplId, string AssociatedConstName), AssociatedConstDecl> _associatedConstImplementations = [];
    private readonly Dictionary<SymbolId, ComptimeValue> _comptimeValues = [];
    private readonly Dictionary<string, SymbolId[]> _precompiledCallableCandidateCache = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeDirectedCallableResolutionCacheKey, TypeDirectedCandidateResolution> _typeDirectedCallableResolutionCache = [];
    private readonly Dictionary<TypeDirectedCallableResolutionCacheKey, TypeDirectedCallableResolutionSnapshotEntry> _previousTypeDirectedCallableResolutionCache = [];
    private readonly Dictionary<TypeDirectedCallableResolutionCacheKey, TypeDirectedCallableResolutionSnapshotEntry> _typeDirectedCallableResolutionSnapshotEntries = [];
    private readonly Dictionary<AssociatedTypeProjectionCacheKey, AssociatedTypeProjectionCacheEntry> _associatedTypeProjectionCache = [];
    private readonly Dictionary<AssociatedTypeProjectionCacheKey, AssociatedTypeProjectionSnapshotEntry> _previousAssociatedTypeProjectionCache = [];
    private readonly Dictionary<AssociatedTypeProjectionCacheKey, AssociatedTypeProjectionSnapshotEntry> _associatedTypeProjectionSnapshotEntries = [];
    private readonly Dictionary<AssociatedConstProjectionCacheKey, AssociatedConstProjectionCacheEntry> _associatedConstProjectionCache = [];
    private readonly Dictionary<AssociatedConstProjectionCacheKey, AssociatedConstProjectionSnapshotEntry> _previousAssociatedConstProjectionCache = [];
    private readonly Dictionary<AssociatedConstProjectionCacheKey, AssociatedConstProjectionSnapshotEntry> _associatedConstProjectionSnapshotEntries = [];
    private readonly Dictionary<string, long> _profilingCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypesStepAccumulator> _typesStepAccumulators = new(StringComparer.Ordinal);
    private bool _allowComptimeFunctionReferences;
    private string? _rootInputFilePath;

    private sealed record CtorTypeBinding(
        SymbolId CtorId,
        SymbolId AdtId,
        List<string> AdtTypeParamNames,
        List<string> CtorTypeParamNames,
        List<TypeNode> PositionalArgTypes,
        Dictionary<string, TypeNode> NamedArgTypes,
        TypeNode? ReturnType);

    private sealed record AdtTypeParamTraitRequirement(
        SymbolId TraitId,
        string TraitName,
        List<TypeNode> TraitArgNodes);

    private sealed record TypeParamKindBinding(
        SymbolId OwnerId,
        List<string> TypeParamNames,
        List<Kind> ExpectedKinds);

    private sealed record TypeParamConstraintBinding(
        SymbolId OwnerId,
        List<string> TypeParamNames,
        List<List<AdtTypeParamTraitRequirement>> TraitRequirementsByIndex);

    private sealed record AdtTypeParamConstraintBinding(
        SymbolId AdtId,
        List<string> TypeParamNames,
        List<List<AdtTypeParamTraitRequirement>> TraitRequirementsByIndex);

    private readonly record struct TypeDirectedCallableResolutionCacheKey(
        string Candidates,
        string ArgumentTypes);

    private readonly record struct AssociatedTypeProjectionCacheKey(
        string TraitKey,
        string TraitName,
        string MemberName,
        string ImplementingTypeKey,
        string TraitArgKeys,
        bool AllowTypeConstructorReference);

    private sealed record AssociatedTypeProjectionCacheEntry(
        Type ReducedType,
        string ReducedTypeKey);

    private readonly record struct AssociatedConstProjectionCacheKey(
        string TraitKey,
        string TraitName,
        string MemberName,
        string ImplementingTypeKey,
        string TraitArgKeys);

    private sealed record AssociatedConstProjectionCacheEntry(
        AssociatedConstDecl Implementation,
        string ConstTypeSignature,
        string ConstValueSignature);

    private readonly record struct AssociatedProjectionLookupRequest(
        TraitSymbol TraitSymbol,
        TypeId ImplementingTypeId,
        ImplTypeRefKey ImplementingTypeKey,
        IReadOnlyList<ImplTypeRefKey> TraitArgKeys);

    /// <summary>
    /// 诊断信息
    /// </summary>
    public List<EidoscDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// 类型代换
    /// </summary>
    public Substitution Substitution => _substitution;

    /// <summary>
    /// 约束生成器
    /// </summary>
    public ConstraintGenerator ConstraintGenerator => _constraintGenerator;

    /// <summary>
    /// 约束求解器
    /// </summary>
    public ConstraintSolver ConstraintSolver => _constraintSolver;

    /// <summary>
    /// Gets function type parameters in declaration order, keyed by function symbol.
    /// </summary>
    public IReadOnlyDictionary<SymbolId, IReadOnlyList<Type>> FunctionTypeParametersBySymbol => _functionTypeParametersBySymbol;

    public IReadOnlyList<TypeEnvBindingSnapshot> TypeEnvBindings => _env.GetBindingsSnapshot();

    internal IReadOnlyDictionary<SymbolId, ComptimeValue> ComptimeValues => _comptimeValues;

    internal bool TryGetConstructorNamedFieldOrder(
        SymbolId constructorId,
        out IReadOnlyList<string> fieldNames)
    {
        fieldNames = [];
        if (!_ctorTypeBindings.TryGetValue(constructorId, out var binding) ||
            !_adtDefinitionsBySymbol.TryGetValue(binding.AdtId, out var adt))
        {
            return false;
        }

        var constructor = adt.Constructors.FirstOrDefault(candidate => candidate.SymbolId == constructorId);
        if (constructor == null)
        {
            return false;
        }

        fieldNames = constructor.NamedArgs
            .Where(static field => !string.IsNullOrWhiteSpace(field.Name))
            .Select(static field => field.Name)
            .ToArray();
        return true;
    }

    public bool TypeAnalysisIncomplete { get; private set; }

    public string? TypeAnalysisIncompleteReason { get; private set; }

    public int TypeErrorLimit => _recoveryContext.MaxErrors;

    public int SuppressedTypeDiagnosticCount { get; private set; }

    public int SuppressedTypeConstraintCount => _constraintSolver.SuppressedConstraintCount;

    public bool UsePrecompiledImportSignatureOnly { get; set; }

    public TypeDirectedCallableResolutionSnapshot? PreviousTypeDirectedCallableResolutionSnapshot
    {
        set
        {
            _previousTypeDirectedCallableResolutionCache.Clear();
            if (value?.Entries == null ||
                !string.Equals(value.SchemaVersion, TypeDirectedCallableResolutionSnapshot.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var entry in value.Entries)
            {
                _previousTypeDirectedCallableResolutionCache[
                    new TypeDirectedCallableResolutionCacheKey(entry.Candidates, entry.ArgumentTypes)] = entry;
            }
        }
    }

    public TypeDirectedCallableResolutionSnapshot CreateTypeDirectedCallableResolutionSnapshot() =>
        new(
            TypeDirectedCallableResolutionSnapshot.CurrentSchemaVersion,
            _typeDirectedCallableResolutionSnapshotEntries.Values
                .OrderBy(static entry => entry.Candidates, StringComparer.Ordinal)
                .ThenBy(static entry => entry.ArgumentTypes, StringComparer.Ordinal)
                .ToArray());

    public AssociatedTypeProjectionSnapshot? PreviousAssociatedTypeProjectionSnapshot
    {
        set
        {
            _previousAssociatedTypeProjectionCache.Clear();
            if (value?.Entries == null ||
                !string.Equals(value.SchemaVersion, AssociatedTypeProjectionSnapshot.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var entry in value.Entries)
            {
                _previousAssociatedTypeProjectionCache[
                    new AssociatedTypeProjectionCacheKey(
                        entry.TraitKey,
                        entry.TraitName,
                        entry.MemberName,
                        entry.ImplementingTypeKey,
                        entry.TraitArgKeys,
                        entry.AllowTypeConstructorReference)] = entry;
            }
        }
    }

    public AssociatedTypeProjectionSnapshot CreateAssociatedTypeProjectionSnapshot() =>
        new(
            AssociatedTypeProjectionSnapshot.CurrentSchemaVersion,
            _associatedTypeProjectionSnapshotEntries.Values
                .OrderBy(static entry => entry.TraitKey, StringComparer.Ordinal)
                .ThenBy(static entry => entry.ImplementingTypeKey, StringComparer.Ordinal)
                .ThenBy(static entry => entry.MemberName, StringComparer.Ordinal)
                .ThenBy(static entry => entry.TraitArgKeys, StringComparer.Ordinal)
                .ToArray());

    public AssociatedConstProjectionSnapshot? PreviousAssociatedConstProjectionSnapshot
    {
        set
        {
            _previousAssociatedConstProjectionCache.Clear();
            if (value?.Entries == null ||
                !string.Equals(value.SchemaVersion, AssociatedConstProjectionSnapshot.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var entry in value.Entries)
            {
                _previousAssociatedConstProjectionCache[
                    new AssociatedConstProjectionCacheKey(
                        entry.TraitKey,
                        entry.TraitName,
                        entry.MemberName,
                        entry.ImplementingTypeKey,
                        entry.TraitArgKeys)] = entry;
            }
        }
    }

    public AssociatedConstProjectionSnapshot CreateAssociatedConstProjectionSnapshot() =>
        new(
            AssociatedConstProjectionSnapshot.CurrentSchemaVersion,
            _associatedConstProjectionSnapshotEntries.Values
                .OrderBy(static entry => entry.TraitKey, StringComparer.Ordinal)
                .ThenBy(static entry => entry.ImplementingTypeKey, StringComparer.Ordinal)
                .ThenBy(static entry => entry.MemberName, StringComparer.Ordinal)
                .ThenBy(static entry => entry.TraitArgKeys, StringComparer.Ordinal)
                .ToArray());

    public TraitCheckSnapshot? PreviousTraitCheckSnapshot
    {
        set => _constraintSolver.LoadPreviousTraitCheckSnapshot(value);
    }

    public TraitCheckSnapshot CreateTraitCheckSnapshot() =>
        _constraintSolver.CreateTraitCheckSnapshot();

    internal void RestoreTypesState(
        IReadOnlyDictionary<SymbolId, TypeScheme> typeEnv,
        Substitution substitution,
        IReadOnlyDictionary<SymbolId, IReadOnlyList<Type>> functionTypeParameters,
        IReadOnlyDictionary<SymbolId, ComptimeValue> comptimeValues,
        IReadOnlyList<TypeConstraint> constraints)
    {
        ArgumentNullException.ThrowIfNull(typeEnv);
        ArgumentNullException.ThrowIfNull(substitution);
        ArgumentNullException.ThrowIfNull(functionTypeParameters);
        ArgumentNullException.ThrowIfNull(comptimeValues);
        ArgumentNullException.ThrowIfNull(constraints);

        _env = TypeEnv.FromSnapshot(typeEnv.Select(static binding => (binding.Key, binding.Value)));
        _substitution.RestoreFrom(substitution);
        _constraintGenerator.Constraints.RestoreFromSnapshot(constraints);
        _functionTypeParametersBySymbol.Clear();
        foreach (var (symbol, parameters) in functionTypeParameters.OrderBy(static binding => binding.Key.Value))
        {
            _functionTypeParametersBySymbol[symbol] = parameters.ToArray();
        }

        _comptimeValues.Clear();
        foreach (var (symbol, value) in comptimeValues.OrderBy(static binding => binding.Key.Value))
        {
            _comptimeValues[symbol] = value;
        }

        _diagnostics.Clear();
        _reportedDiagnostics.Clear();
        TypeAnalysisIncomplete = false;
        TypeAnalysisIncompleteReason = null;
        SuppressedTypeDiagnosticCount = 0;
        _profilingCounters.Clear();
        _typesStepAccumulators.Clear();
        _typeDirectedCallableResolutionSnapshotEntries.Clear();
        _associatedTypeProjectionSnapshotEntries.Clear();
        _associatedConstProjectionSnapshotEntries.Clear();
    }

    public IReadOnlyDictionary<string, long> GetProfilingCounters()
    {
        var counters = new Dictionary<string, long>(_profilingCounters, StringComparer.Ordinal)
        {
            ["Types.typeVariables.created"] = _substitution.NextFreshVarIndex,
            ["Types.substitution.bindings"] = _substitution.Count,
            ["Types.constraints.count"] = _constraintGenerator.Constraints.Count,
            ["Types.deferredTraitConstraintVars.count"] = _substitution.DeferredTraitConstraints.Count,
            ["Types.adtDefinitions.count"] = _adtDefinitionsBySymbol.Count,
            ["Types.functionDefinitions.count"] = _functionDefinitionsBySymbol.Count,
            ["Types.valueTypeAnnotations.count"] = _valueTypeAnnotationsBySymbol.Count,
            ["Types.typeConstructorKinds.count"] = _typeConstructorKindsBySymbol.Count,
            ["Types.functionTypeParameters.count"] = _functionTypeParametersBySymbol.Count,
            ["Types.associatedTypeImplementations.count"] = _associatedTypeImplementations.Count,
            ["Types.associatedConstImplementations.count"] = _associatedConstImplementations.Count,
            ["Types.associatedTypeProjectionCache.entries"] = _associatedTypeProjectionCache.Count,
            ["Types.associatedTypeProjectionSnapshot.entries"] = _associatedTypeProjectionSnapshotEntries.Count,
            ["Types.associatedTypeProjectionPreviousCache.entries"] = _previousAssociatedTypeProjectionCache.Count,
            ["Types.associatedConstProjectionCache.entries"] = _associatedConstProjectionCache.Count,
            ["Types.associatedConstProjectionSnapshot.entries"] = _associatedConstProjectionSnapshotEntries.Count,
            ["Types.associatedConstProjectionPreviousCache.entries"] = _previousAssociatedConstProjectionCache.Count,
            ["Types.diagnostics.count"] = _diagnostics.Count,
            ["Types.suppressedDiagnostics.count"] = SuppressedTypeDiagnosticCount,
            ["Types.suppressedConstraints.count"] = SuppressedTypeConstraintCount
        };
        foreach (var (name, value) in _typesStepAccumulators)
        {
            counters[$"Types.step.{name}.calls"] = value.Calls;
            counters[$"Types.step.{name}.ticks"] = value.ElapsedTicks;
            counters[$"Types.step.{name}.allocatedBytes"] = value.AllocatedBytes;
        }

        foreach (var (name, value) in _constraintSolver.GetProfilingCounters())
        {
            counters[name] = value;
        }

        return counters;
    }

    private bool _typeErrorLimitDiagnosticReported;

    private bool HasReachedTypeErrorLimit => _recoveryContext.ErrorCount >= _recoveryContext.MaxErrors;

    public TypeInferer(SymbolTable symbolTable)
    {
        _symbolTable = symbolTable;
        _substitution = new Substitution();
        _env = TypeEnv.Empty;
        _constraintGenerator = new ConstraintGenerator(symbolTable, _substitution);
        _constraintSolver = new ConstraintSolver(symbolTable, _substitution, _typeConstructorKindsBySymbol);
    }

    /// <summary>
    /// 推断模块中所有定义的类型
    /// 类型不匹配时记录错误继续分析
    /// </summary>
    public bool Infer(ModuleDecl module)
    {
        // 注册内置 Trait
        BuiltinTraits.RegisterBuiltinTraits(_symbolTable);
        _reportedDiagnostics.Clear();
        TypeAnalysisIncomplete = false;
        TypeAnalysisIncompleteReason = null;
        SuppressedTypeDiagnosticCount = 0;
        _typeErrorLimitDiagnosticReported = false;
        _nextKindVarId = 0;
        _profilingCounters.Clear();
        _typesStepAccumulators.Clear();

        _ctorTypeBindings.Clear();
        _adtDefinitionsBySymbol.Clear();
        _functionDefinitionsBySymbol.Clear();
        _valueTypeAnnotationsBySymbol.Clear();
        _typeParamKindBindingsBySymbol.Clear();
        _typeConstructorKindsBySymbol.Clear();
        _adtTypeParamConstraintBindings.Clear();
        _ctorTypeParamConstraintBindings.Clear();
        _functionTypeParametersBySymbol.Clear();
        _associatedTypeImplementations.Clear();
        _associatedConstImplementations.Clear();
        _comptimeValues.Clear();
        _precompiledCallableCandidateCache.Clear();
        _typeDirectedCallableResolutionCache.Clear();
        _typeDirectedCallableResolutionSnapshotEntries.Clear();
        _associatedTypeProjectionCache.Clear();
        _associatedTypeProjectionSnapshotEntries.Clear();
        _rootInputFilePath = module.Span.FilePath;
        using (MeasureTypesStep("index_declarations"))
        {
            IndexAdtConstructorBindings(module);
        }

        // 预注册函数签名（递归模块），保证前向引用/互相引用在类型推断阶段可用。
        using (MeasureTypesStep("predeclare_function_signatures"))
        {
            PredeclareFunctionSignatures(module);
        }

        using (MeasureTypesStep("infer_module_declarations"))
        {
            InferModuleDeclarations(module);
        }

        // 约束不满足时记录错误继续
        bool constraintsSatisfied;
        using (MeasureTypesStep("solve_constraints"))
        {
            constraintsSatisfied = _constraintSolver.Solve(_constraintGenerator.Constraints);
            foreach (var diag in _constraintSolver.Diagnostics)
            {
                _diagnostics.Add(diag);
                _recoveryContext.RecordError();
            }
        }

        if (!constraintsSatisfied && _constraintSolver.AnalysisIncomplete)
        {
            MarkTypeAnalysisIncomplete(
                _constraintSolver.IncompleteReason ??
                DiagnosticMessages.ConstraintSolvingStoppedBeforeAllConstraintsChecked);
        }

        return !_diagnostics.Exists(d => d.Level == EidoscDiagnosticLevel.Error);
    }

    private TypesStepScope MeasureTypesStep(string name) => new(this, name);

    private void RecordTypesStepMetric(string name, long elapsedTicks, long allocatedBytes)
    {
        ref var accumulator = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _typesStepAccumulators,
            name,
            out _);
        accumulator.Calls++;
        accumulator.ElapsedTicks += elapsedTicks;
        accumulator.AllocatedBytes += allocatedBytes;
    }

    private void IncrementProfilingCounter(string name, long delta = 1)
    {
        _profilingCounters[name] = _profilingCounters.TryGetValue(name, out var current)
            ? current + delta
            : delta;
    }

    private struct TypesStepAccumulator
    {
        public long Calls;
        public long ElapsedTicks;
        public long AllocatedBytes;
    }

    private readonly struct TypesStepScope : IDisposable
    {
        private readonly TypeInferer _inferer;
        private readonly string _name;
        private readonly long _startTimestamp;
        private readonly long _allocatedBytesBefore;

        public TypesStepScope(TypeInferer inferer, string name)
        {
            _inferer = inferer;
            _name = name;
            _startTimestamp = Stopwatch.GetTimestamp();
            _allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - _startTimestamp;
            var allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - _allocatedBytesBefore);
            _inferer.RecordTypesStepMetric(_name, elapsedTicks, allocatedBytes);
        }
    }

    private void IndexAdtConstructorBindings(ModuleDecl module)
    {
        foreach (var declaration in module.Declarations)
        {
            switch (declaration)
            {
                case ModuleDecl nestedModule:
                    IndexAdtConstructorBindings(nestedModule);
                    break;
                case AdtDef adt:
                    RegisterAdtConstructorBindings(adt);
                    break;
                case FuncDef { SymbolId.IsValid: true } func:
                    _functionDefinitionsBySymbol[func.SymbolId] = func;
                    break;
                case LetDecl { SymbolId.IsValid: true, TypeAnnotation: not null } letDecl:
                    _valueTypeAnnotationsBySymbol[letDecl.SymbolId] = letDecl.TypeAnnotation;
                    break;
                case EffectDef:
                    break;
                case TraitDef trait:
                    RegisterTraitTypeParamKinds(trait);
                    foreach (var method in trait.Methods.Where(method => method.SymbolId.IsValid))
                    {
                        _functionDefinitionsBySymbol[method.SymbolId] = method;
                    }
                    break;
                case InstanceDecl instance:
                    RegisterAssociatedTypeImplementations(instance);
                    RegisterAssociatedConstImplementations(instance);
                    foreach (var method in instance.Methods.Where(method => method.SymbolId.IsValid))
                    {
                        _functionDefinitionsBySymbol[method.SymbolId] = method;
                    }
                    break;
            }
        }
    }

    private void RegisterAssociatedTypeImplementations(InstanceDecl instance)
    {
        if (!instance.SymbolId.IsValid)
        {
            return;
        }

        foreach (var associatedType in instance.AssociatedTypes)
        {
            if (string.IsNullOrWhiteSpace(associatedType.Name) ||
                associatedType.ValueType == null)
            {
                continue;
            }

            _associatedTypeImplementations[(instance.SymbolId, associatedType.Name)] = associatedType.ValueType;
        }
    }

    private void RegisterAssociatedConstImplementations(InstanceDecl instance)
    {
        if (!instance.SymbolId.IsValid)
        {
            return;
        }

        foreach (var associatedConst in instance.AssociatedConsts)
        {
            if (string.IsNullOrWhiteSpace(associatedConst.Name) ||
                associatedConst.Type == null ||
                associatedConst.Value == null)
            {
                continue;
            }

            _associatedConstImplementations[(instance.SymbolId, associatedConst.Name)] = associatedConst;
        }
    }

    private void RegisterAdtConstructorBindings(AdtDef adt)
    {
        if (!adt.SymbolId.IsValid)
        {
            return;
        }

        _adtDefinitionsBySymbol[adt.SymbolId] = adt;
        var typeParamNames = GetAdtTypeParamNames(adt);
        RegisterAdtTypeParamKinds(adt, typeParamNames);
        RegisterAdtTypeParamConstraints(adt, typeParamNames);

        foreach (var ctor in adt.Constructors)
        {
            if (!ctor.SymbolId.IsValid)
            {
                continue;
            }

            var namedArgTypes = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
            foreach (var field in ctor.NamedArgs)
            {
                if (!string.IsNullOrWhiteSpace(field.Name) && field.Type != null)
                {
                    namedArgTypes.TryAdd(field.Name, field.Type);
                }
            }

            var ctorTypeParamNames = GetConstructorTypeParamNames(ctor);
            RegisterConstructorTypeParamKinds(adt, ctor, typeParamNames, ctorTypeParamNames);
            RegisterConstructorTypeParamConstraints(ctor, ctorTypeParamNames);

            _ctorTypeBindings[ctor.SymbolId] = new CtorTypeBinding(
                ctor.SymbolId,
                adt.SymbolId,
                typeParamNames,
                ctorTypeParamNames,
                [.. ctor.PositionalArgs],
                namedArgTypes,
                ctor.ReturnType);

            UpdateConstructorSymbolSignature(ctor, adt, typeParamNames, ctorTypeParamNames);
        }
    }

    private void UpdateConstructorSymbolSignature(
        Constructor ctor,
        AdtDef adt,
        IReadOnlyList<string> typeParamNames,
        IReadOnlyList<string> ctorTypeParamNames)
    {
        var symbol = _symbolTable.GetSymbol<CtorSymbol>(ctor.SymbolId);
        if (symbol == null)
        {
            return;
        }

        var typeVarEnv = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var typeParamName in typeParamNames)
        {
            typeVarEnv[typeParamName] = _substitution.FreshTypeVariable();
        }

        foreach (var typeParamName in ctorTypeParamNames)
        {
            typeVarEnv[typeParamName] = _substitution.FreshTypeVariable();
        }

        var kindEnvByName = CreateTypeParamKindMapForCtorBinding(adt.SymbolId, typeParamNames, ctor.SymbolId, ctorTypeParamNames);
        var returnType = ctor.ReturnType != null
            ? ConvertTypeWithAdditionalKindContext(ctor.ReturnType, typeVarEnv, kindEnvByName)
            : new TyCon
            {
                Name = adt.Name,
                Symbol = adt.SymbolId,
                Args = typeParamNames
                    .Select(name => typeVarEnv.TryGetValue(name, out var type) ? type : _substitution.FreshTypeVariable())
                    .ToList()
            };

        _symbolTable.UpdateSymbol(symbol with
        {
            SignatureText = FormatConstructorSignature(ctor.Name, ctorTypeParamNames, ctor.PositionalArgs, returnType)
        });
    }

    private static string FormatConstructorSignature(
        string name,
        IReadOnlyList<string> typeParamNames,
        IReadOnlyList<TypeNode> positionalArgs,
        Type returnType)
    {
        var typeParams = typeParamNames.Count == 0
            ? string.Empty
            : $"[{string.Join(", ", typeParamNames)}]";
        var args = positionalArgs.Count == 0
            ? "()"
            : $"({string.Join(", ", positionalArgs.Select(static type => type.ToString()))})";
        return $"{name}{typeParams}{args} -> {returnType}";
    }

    private void RegisterAdtTypeParamKinds(AdtDef adt, IReadOnlyList<string> typeParamNames)
    {
        var expectedKinds = InferAndFinalizeTypeParamKinds(
            adt.TypeParams,
            typeParamNames,
            EnumerateAdtKindInferenceTypeNodes(adt),
            ownerKind: "ADT",
            ownerName: adt.Name);

        _typeParamKindBindingsBySymbol[adt.SymbolId] = new TypeParamKindBinding(
            adt.SymbolId,
            [.. typeParamNames],
            expectedKinds);
        _typeConstructorKindsBySymbol[adt.SymbolId] = TypeConstructorKindResolver.BuildConstructorKind(expectedKinds);
    }

    private void RegisterTraitTypeParamKinds(TraitDef trait)
    {
        if (!trait.SymbolId.IsValid)
        {
            return;
        }

        var typeParamNames = GetTraitTypeParamNames(trait);
        var kindUnifier = new KindInferer(
            _symbolTable,
            typeConstructorKindsBySymbol: _typeConstructorKindsBySymbol);
        var expectedKinds = CreateExpectedKinds(
            trait.TypeParams,
            typeParamNames,
            kindUnifier);
        var kindByTypeParamName = CreateTypeParamKindBindings(typeParamNames, expectedKinds);

        foreach (var method in trait.Methods)
        {
            ApplyFuncDefTypeParamKindInference(
                method,
                kindByTypeParamName,
                kindUnifier,
                ownerKind: WellKnownStrings.Keywords.Trait,
                ownerName: trait.Name);
        }

        FinalizeExpectedKindsInPlace(expectedKinds);
        UpdateTypeParamSymbolsWithKinds(trait.TypeParams, expectedKinds);

        _typeParamKindBindingsBySymbol[trait.SymbolId] = new TypeParamKindBinding(
            trait.SymbolId,
            typeParamNames,
            expectedKinds);
        _typeConstructorKindsBySymbol[trait.SymbolId] = TypeConstructorKindResolver.BuildConstructorKind(expectedKinds);
    }
}
