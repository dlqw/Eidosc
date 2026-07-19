using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// MIR 构建器 - 将 HIR 转换为 MIR
/// </summary>
public sealed partial class MirBuilder
{
    private readonly Func<TypeId, bool>? _hasCopyImplResolver;
    private readonly SymbolTable? _symbolTable;
    private readonly HashSet<int> _extraCopyLikeTypeIds = [];
    private readonly Dictionary<int, string> _dynamicTypeKeysById = [];
    private readonly Dictionary<int, TypeDescriptor> _typeDescriptorsById = [];
    private readonly Dictionary<TypeDescriptor, TypeId> _dynamicTypeIdByDescriptor = new(TypeDescriptorStructuralComparer.Instance);
    private readonly Dictionary<int, bool> _copyTypeCache = [];
    private readonly Dictionary<int, List<ConstructorTypeLayout>> _constructorLayouts = [];
    private int _nextDynamicTypeId = 1000;
    private int _nextBlockId = 1;
    private int _nextLocalId = 1;
    private int _nextTempId = 1;
    private int _nextLambdaId = 1;
    private int _nextSyntheticFunctionId = 1;

    private MirFunc? _currentFunc;
    private MirBasicBlock? _currentBlock;
    private Dictionary<string, LocalId> _variableLocals = new();
    private Dictionary<SymbolId, LocalId> _symbolLocals = new();
    private readonly Dictionary<SymbolId, string> _functionSymbols = new();
    private readonly Dictionary<string, SymbolId> _functionNames = new();
    private readonly HashSet<string> _ambiguousFunctionNames = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, TypeId> _functionReturnTypesBySymbol = new();
    private readonly Dictionary<string, TypeId> _functionReturnTypesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, TypeId> _functionSignatureTypesBySymbol = new();
    private readonly Dictionary<string, TypeId> _functionSignatureTypesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, string> _moduleValueGetterBySymbol = new();
    private readonly Dictionary<string, string> _moduleValueGetterByName = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, FunctionId> _moduleValueGetterFunctionIdBySymbol = new();
    private readonly Dictionary<string, FunctionId> _moduleValueGetterFunctionIdByName = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, TypeId> _moduleValueGetterReturnTypeBySymbol = new();
    private readonly Dictionary<string, TypeId> _moduleValueGetterReturnTypeByName = new(StringComparer.Ordinal);
    private readonly HashSet<SymbolId> _blockedModuleValueSymbols = [];
    private readonly HashSet<string> _blockedModuleValueNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _constructorFieldOrderByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _uniqueNamedFieldOrdinal = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousNamedField = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeId, Dictionary<string, int>> _uniqueNamedFieldOrdinalByAdtType = new();
    private readonly Dictionary<TypeId, HashSet<string>> _ambiguousNamedFieldByAdtType = new();
    private readonly Dictionary<TypeId, HashSet<string>> _partialNamedFieldByAdtType = new();
    private readonly Dictionary<TypeId, HashSet<string>> _allNamedFieldByAdtType = new();
    private readonly Dictionary<TypeId, string> _adtDisplayNameByType = new();
    private Dictionary<LocalId, int> _knownListLengths = new();
    private HashSet<LocalId> _runtimeArrayLocals = [];
    private HashSet<LocalId> _comprehensionElementLocals = [];
    private readonly List<MirFunc> _generatedLambdaFunctions = new();
    private Stack<LoopLoweringContext> _loopContextStack = new();
    private readonly Stack<RecursiveClosureBindingContext> _recursiveClosureBindings = new();
    private readonly Stack<RecursiveClosureGroupContext> _recursiveClosureGroups = new();
    private List<string> _currentModulePath = [];

    private readonly Substitution? _substitution;

    public Substitution? Substitution => _substitution;

    public List<Diagnostic.Diagnostic> Diagnostics { get; } = [];

    private readonly record struct LoopLoweringContext(BlockId Header, BlockId Exit);
    private sealed record RecursiveClosureBindingContext(string Name, SymbolId SymbolId, TypeId TypeId);
    private sealed record RecursiveClosureBodyBinding(
        string Name,
        SymbolId SymbolId,
        TypeId TypeId,
        string LambdaName,
        SymbolId LambdaSymbolId,
        FunctionId LambdaFunctionId);
    private sealed record RecursiveClosureBodyContext(
        IReadOnlyList<RecursiveClosureBodyBinding> Bindings,
        IReadOnlyList<HirParam> PreboundCaptureParameters);
    private sealed record RecursiveClosureGroupBindingContext(
        string Name,
        SymbolId SymbolId,
        TypeId TypeId,
        LocalId LocalId,
        string LambdaName,
        SymbolId LambdaSymbolId,
        FunctionId LambdaFunctionId,
        HirLambda Lambda,
        PatternBindingMode BindingMode,
        bool IsMutable);
    private sealed class RecursiveClosureGroupContext
    {
        public List<RecursiveClosureGroupBindingContext> Bindings { get; init; } = [];
        public List<HirCapture> SharedCaptures { get; init; } = [];
        public IReadOnlyList<HirParam> SharedCaptureParameters { get; set; } = [];
        public IReadOnlyList<MirOperand> SharedCaptureArguments { get; set; } = [];
        public bool IsEnvironmentInitialized { get; set; }
    }

    public MirBuilder()
        : this(null, null, null, null)
    {
    }

    public MirBuilder(
        Func<TypeId, bool>? hasCopyImplResolver,
        IReadOnlySet<TypeId>? extraCopyLikeTypeIds = null,
        IReadOnlyDictionary<TypeId, string>? dynamicTypeKeys = null,
        SymbolTable? symbolTable = null,
        IReadOnlyDictionary<int, List<ConstructorTypeLayout>>? constructorLayouts = null,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors = null,
        ParameterEffectMap? parameterEffects = null,
        Substitution? substitution = null)
    {
        _substitution = substitution;
        _hasCopyImplResolver = hasCopyImplResolver;
        _symbolTable = symbolTable;
        if (symbolTable != null)
        {
            _nextDynamicTypeId = ComputeInitialDynamicTypeId(symbolTable);
        }

        if (dynamicTypeKeys != null)
        {
            foreach (var (typeId, typeKey) in dynamicTypeKeys)
            {
                if (typeId.IsValid && !string.IsNullOrWhiteSpace(typeKey))
                {
                    _dynamicTypeKeysById[typeId.Value] = typeKey;
                    ReserveDynamicTypeId(typeId.Value);
                    if (TypeKeyParsing.TryParseTypeDescriptor(typeKey, out var descriptor))
                    {
                        _dynamicTypeIdByDescriptor[descriptor] = typeId;
                        _typeDescriptorsById.TryAdd(typeId.Value, descriptor);
                    }
                }
            }
        }

        if (typeDescriptors != null)
        {
            foreach (var (typeIdValue, descriptor) in typeDescriptors)
            {
                var typeId = new TypeId(typeIdValue);
                _typeDescriptorsById[typeIdValue] = descriptor;
                _dynamicTypeIdByDescriptor[descriptor] = typeId;
                _dynamicTypeKeysById.TryAdd(typeIdValue, descriptor.ToString());
                ReserveDynamicTypeId(typeIdValue);
            }
        }

        _constructorLayouts = constructorLayouts != null
            ? new Dictionary<int, List<ConstructorTypeLayout>>(constructorLayouts)
            : [];
        AddClosedDeclaredCopyLayoutsForDynamicTypes();

        if (extraCopyLikeTypeIds == null)
        {
            return;
        }

        foreach (var typeId in extraCopyLikeTypeIds)
        {
            if (typeId.IsValid)
            {
                _extraCopyLikeTypeIds.Add(typeId.Value);
            }
        }
    }

    private static int ComputeInitialDynamicTypeId(SymbolTable symbolTable)
    {
        var nextTypeId = 1000;
        foreach (var symbol in symbolTable.Symbols.Values)
        {
            if (symbol.Id.IsValid && symbol.Id.Value >= nextTypeId)
            {
                nextTypeId = symbol.Id.Value + 1;
            }

            if (symbol.TypeId.IsValid && symbol.TypeId.Value >= nextTypeId)
            {
                nextTypeId = symbol.TypeId.Value + 1;
            }
        }

        return nextTypeId;
    }

    private void ReserveDynamicTypeId(int typeIdValue)
    {
        if (typeIdValue >= _nextDynamicTypeId)
        {
            _nextDynamicTypeId = typeIdValue + 1;
        }
    }

    private TypeId GetOrCreateDynamicTypeId(TypeDescriptor descriptor)
    {
        if (_dynamicTypeIdByDescriptor.TryGetValue(descriptor, out var existingTypeId))
        {
            return existingTypeId;
        }

        var typeId = new TypeId(_nextDynamicTypeId++);
        _dynamicTypeIdByDescriptor[descriptor] = typeId;
        _typeDescriptorsById[typeId.Value] = descriptor;
        _dynamicTypeKeysById[typeId.Value] = descriptor.ToString();
        return typeId;
    }

    private TypeId GetOrCreateFunctionSignatureTypeId(IReadOnlyList<TypeId> parameterTypes, TypeId returnType)
    {
        if (!returnType.IsValid || parameterTypes.Any(static typeId => !typeId.IsValid))
        {
            return TypeId.None;
        }

        return GetOrCreateDynamicTypeId(new TypeDescriptor.Function([.. parameterTypes], returnType));
    }

    /// <summary>
    /// Reset all per-build mutable state by creating fresh collections.
    /// Called at the start of Build() to ensure clean state without 20+ Clear() calls.
    /// </summary>
    private void ResetBuildState()
    {
        _functionSymbols.Clear();
        _functionNames.Clear();
        _functionReturnTypesBySymbol.Clear();
        _functionReturnTypesByName.Clear();
        _functionSignatureTypesBySymbol.Clear();
        _functionSignatureTypesByName.Clear();
        _moduleValueGetterBySymbol.Clear();
        _moduleValueGetterByName.Clear();
        _moduleValueGetterFunctionIdBySymbol.Clear();
        _moduleValueGetterFunctionIdByName.Clear();
        _moduleValueGetterReturnTypeBySymbol.Clear();
        _moduleValueGetterReturnTypeByName.Clear();
        _blockedModuleValueSymbols.Clear();
        _blockedModuleValueNames.Clear();
        _constructorFieldOrderByName.Clear();
        _uniqueNamedFieldOrdinal.Clear();
        _ambiguousNamedField.Clear();
        _uniqueNamedFieldOrdinalByAdtType.Clear();
        _ambiguousNamedFieldByAdtType.Clear();
        _partialNamedFieldByAdtType.Clear();
        _allNamedFieldByAdtType.Clear();
        _adtDisplayNameByType.Clear();
        _copyTypeCache.Clear();
        _generatedLambdaFunctions.Clear();
        _loopContextStack.Clear();
        _nextLambdaId = 1;
        _nextSyntheticFunctionId = 1;
    }

    /// <summary>
    /// 从 HIR 模块构建 MIR 模块
    /// </summary>
    public MirModule Build(HirModule hirModule, HirModule? resolutionContext = null)
    {
        Diagnostics.Clear();
        ResetBuildState();
        _currentModulePath = hirModule.Path.ToList();
        var resolutionDeclarations = resolutionContext?.Declarations ?? hirModule.Declarations;

        if (_symbolTable != null)
        {
            foreach (var symbol in _symbolTable.Symbols.Values)
            {
                if (symbol is not FuncSymbol functionSymbol || !functionSymbol.Id.IsValid)
                {
                    continue;
                }

                _functionSymbols[functionSymbol.Id] = functionSymbol.Name;
                if (functionSymbol.ReturnType.IsValid)
                {
                    _functionReturnTypesBySymbol[functionSymbol.Id] = functionSymbol.ReturnType;
                }

                RegisterFunctionSignature(
                    functionSymbol.Id,
                    functionSymbol.Name,
                    functionSymbol.ParamTypes,
                    functionSymbol.ReturnType);
            }
        }

        foreach (var func in resolutionDeclarations.OfType<HirFunc>())
        {
            if (func.SymbolId.IsValid)
            {
                _functionSymbols[func.SymbolId] = func.Name;
                _functionReturnTypesBySymbol[func.SymbolId] = func.ReturnType;
                RegisterFunctionNameFallback(func.Name, func.SymbolId, func.ReturnType);
                RegisterFunctionSignature(
                    func.SymbolId,
                    func.Name,
                    func.Parameters.Select(static parameter => parameter.TypeId).ToArray(),
                    func.ReturnType);
                RegisterQualifiedFunctionNameFallback(func);
            }
            else if (!string.IsNullOrEmpty(func.Name))
            {
                RegisterFunctionNameFallback(func.Name, SymbolId.None, func.ReturnType);
                RegisterFunctionSignature(
                    SymbolId.None,
                    func.Name,
                    func.Parameters.Select(static parameter => parameter.TypeId).ToArray(),
                    func.ReturnType);
                RegisterQualifiedFunctionNameFallback(func);
            }
        }

        var moduleValueDecls = resolutionDeclarations
            .OfType<HirVal>()
            .Where(static value => value.IsModuleLevel && value.Initializer is not HirLambda)
            .ToList();
        DetectModuleValueCycles(moduleValueDecls);


        foreach (var val in resolutionDeclarations.OfType<HirVal>())
        {
            if (!IsModuleLambdaValue(val, out var lambda))
            {
                RegisterModuleValueGetter(val);
                continue;
            }

            if (val.SymbolId.IsValid)
            {
                _functionSymbols[val.SymbolId] = val.Name;
            }

            RegisterFunctionNameFallback(val.Name, val.SymbolId, GetLambdaReturnType(lambda));
        }

        CollectConstructorFieldLayouts(hirModule);

        RegisterAdtTypeParameterDescriptors(hirModule);
        var typeAliases = CollectTypeAliases(hirModule);

        var mirModule = new MirModule
        {
            Name = hirModule.Name,
            PackageAlias = hirModule.PackageAlias,
            PackageInstanceKey = hirModule.PackageInstanceKey,
            Path = hirModule.Path,
            DynamicTypeKeys = new Dictionary<int, string>(_dynamicTypeKeysById),
            TypeDescriptors = new Dictionary<int, TypeDescriptor>(_typeDescriptorsById),
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>(_constructorLayouts),
            TraitImpls = hirModule.Declarations
                .OfType<HirImpl>()
                .Select(static impl => impl.ImplMetadata)
                .Where(static impl => impl != null)
                .Select(static impl => impl!)
                .ToList(),
            TraitInfos = CollectTraitInfos(),
            TypeAliases = typeAliases,
            TypeConstructors = CollectTypeConstructors(),
            LinkLibraries = hirModule.LinkLibraries.ToList(),
            Span = hirModule.Span
        };

        // 收集 @cstruct 字段访问器元数据
        CollectCStructAccessors(mirModule);

        // 转换函数
        foreach (var decl in hirModule.Declarations)
        {
            if (decl is HirFunc func)
            {
                if (func.IsComptime)
                {
                    continue;
                }

                if (func.IsExternal || func.IntrinsicName != null && func.Body == null)
                {
                    mirModule.Functions.Add(CreateDeclarationPlaceholder(func));
                    continue;
                }

                var mirFunc = ConvertFunc(func);
                if (mirFunc != null)
                {
                    mirModule.Functions.Add(mirFunc);
                }

                continue;
            }

            if (decl is HirVal val &&
                IsModuleLambdaValue(val, out var lambdaValue))
            {
                var mirFunc = ConvertModuleLambdaValue(val, lambdaValue);
                if (mirFunc != null)
                {
                    mirModule.Functions.Add(mirFunc);
                }

                continue;
            }

            if (decl is HirVal moduleValue &&
                TryGetModuleValueGetterName(moduleValue, out _))
            {
                var getter = ConvertModuleValueGetter(moduleValue);
                if (getter != null)
                {
                    mirModule.Functions.Add(getter);
                }
            }
        }

        if (_generatedLambdaFunctions.Count > 0)
        {
            mirModule.Functions.AddRange(_generatedLambdaFunctions);
        }

        RefreshModuleTypeMetadata(mirModule);
        return mirModule;
    }

    private void RefreshModuleTypeMetadata(MirModule mirModule)
    {
        mirModule.DynamicTypeKeys.Clear();
        foreach (var (typeId, typeKey) in _dynamicTypeKeysById)
        {
            mirModule.DynamicTypeKeys[typeId] = typeKey;
        }

        mirModule.TypeDescriptors.Clear();
        foreach (var (typeId, descriptor) in _typeDescriptorsById)
        {
            mirModule.TypeDescriptors[typeId] = descriptor;
        }

        mirModule.ConstructorLayouts.Clear();
        foreach (var (typeId, layouts) in _constructorLayouts)
        {
            mirModule.ConstructorLayouts[typeId] = layouts;
        }
    }

    private List<MirTraitInfo> CollectTraitInfos()
    {
        if (_symbolTable == null)
        {
            return [];
        }

        var symbols = _symbolTable.Symbols.Values.ToList();
        var traitMethods = symbols
            .OfType<FuncSymbol>()
            .Where(static method => method.OwnerTrait is { IsValid: true })
            .GroupBy(static method => method.OwnerTrait!.Value)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToList());

        return symbols
            .OfType<TraitSymbol>()
            .Where(static trait => trait.Id.IsValid)
            .Select(trait =>
            {
                var methods = traitMethods.TryGetValue(trait.Id, out var traitMethodsForTrait)
                    ? traitMethodsForTrait
                    : [];
                var typeParameterIds = GetTypeDomainParameterIds(trait.TypeParams);
                return new MirTraitInfo
                {
                    TraitId = trait.Id,
                    TypeParameterCount = typeParameterIds.Count,
                    TypeParameterIds = typeParameterIds,
                    SelfPosition = trait.SelfPosition,
                    ParentTraits = trait.ParentTraits.ToList(),
                    HasMethodDispatchMetadata = methods.Any(HasTraitMethodDispatchMetadata),
                    Methods = methods
                        .Where(static method => method.Id.IsValid)
                        .Select(method => new MirTraitMethodInfo
                        {
                            TraitId = trait.Id,
                            MethodId = method.Id,
                            Name = method.Name,
                            SelfPosition = method.TraitSelfPosition,
                            SelfParameterIndices = method.TraitSelfParameterIndices.ToList(),
                            SelfInResult = method.TraitSelfInResult,
                            MethodRole = method.TraitMethodRole,
                            HasDefaultImplementation = method.IsDefaultImplementation
                        })
                        .ToList()
                };
            })
            .ToList();
    }

    private List<MirTypeAliasInfo> CollectTypeAliases(HirModule hirModule)
    {
        if (_symbolTable == null)
        {
            return [];
        }

        var aliasTargetBySymbolId = hirModule.Declarations
            .OfType<HirAdt>()
            .Where(static adt => adt.SymbolId.IsValid && adt.AliasTarget.IsValid)
            .ToDictionary(static adt => adt.SymbolId, static adt => adt.AliasTarget);

        var aliases = _symbolTable.Symbols.Values
            .OfType<AdtSymbol>()
            .Where(static alias => alias.IsTypeAlias &&
                                   alias.Id.IsValid &&
                                   alias.TypeId.IsValid &&
                                   alias.AliasTarget is { IsValid: true } &&
                                   !string.IsNullOrWhiteSpace(alias.Name))
            .Select(alias => new MirTypeAliasInfo
            {
                AliasId = alias.Id,
                Name = alias.Name,
                TypeId = alias.TypeId,
                AliasTarget = aliasTargetBySymbolId.TryGetValue(alias.Id, out var targetTypeId)
                    ? targetTypeId
                    : alias.AliasTarget!.Value,
                TypeParameterIds = GetTypeDomainParameterIds(alias.TypeParams)
            })
            .ToList();

        foreach (var alias in aliases)
        {
            foreach (var typeParameterId in alias.TypeParameterIds)
            {
                if (typeParameterId.IsValid)
                {
                    _typeDescriptorsById.TryAdd(
                        typeParameterId.Value,
                        new TypeDescriptor.TypeVar(typeParameterId.Value));
                }
            }
        }

        return aliases;
    }

    private List<MirTypeConstructorInfo> CollectTypeConstructors()
    {
        if (_symbolTable == null)
        {
            return [];
        }

        return _symbolTable.Symbols.Values
            .Where(static symbol => symbol is AdtSymbol or TraitSymbol or EffectSymbol)
            .Where(static symbol => symbol.Id.IsValid &&
                                    symbol.TypeId.IsValid &&
                                    !string.IsNullOrWhiteSpace(symbol.Name))
            .Select(symbol => new MirTypeConstructorInfo
            {
                SymbolId = symbol.Id,
                Name = symbol.Name,
                TypeId = symbol.TypeId,
                TypeParameterIds = symbol switch
                {
                    AdtSymbol adt => GetTypeDomainParameterIds(adt.TypeParams),
                    TraitSymbol trait => GetTypeDomainParameterIds(trait.TypeParams),
                    EffectSymbol => [],
                    _ => []
                }
            })
            .ToList();
    }

    private void RegisterAdtTypeParameterDescriptors(HirModule hirModule)
    {
        foreach (var adt in hirModule.Declarations.OfType<HirAdt>())
        {
            foreach (var typeParameter in adt.TypeParams)
            {
                if (typeParameter.ParameterKind != GenericParameterKind.Type)
                {
                    continue;
                }

                var typeParameterTypeId = typeParameter.TypeId.IsValid
                    ? typeParameter.TypeId
                    : typeParameter.SymbolId.IsValid
                        ? new TypeId(typeParameter.SymbolId.Value)
                        : TypeId.None;
                if (typeParameterTypeId.IsValid)
                {
                    _typeDescriptorsById.TryAdd(
                        typeParameterTypeId.Value,
                        new TypeDescriptor.TypeVar(typeParameterTypeId.Value));
                }
            }
        }
    }

    private List<SymbolId> GetTypeDomainParameterIds(IEnumerable<SymbolId> parameterIds)
    {
        if (_symbolTable == null)
        {
            return [];
        }

        return parameterIds
            .Where(parameterId =>
                _symbolTable.GetSymbol<TypeParamSymbol>(parameterId)?.ParameterKind == GenericParameterKind.Type)
            .ToList();
    }

    private static bool HasTraitMethodDispatchMetadata(FuncSymbol method)
    {
        return method.TraitSelfPosition != SelfPosition.Unknown ||
               method.TraitSelfParameterIndices.Count > 0 ||
               method.TraitSelfInResult;
    }


    private MirFunc ConvertFunc(HirFunc func)
    {
        var (parameters, body) = NormalizeCurriedFunctionBody(func);

        // 重置状态
        _nextBlockId = 1;
        _nextLocalId = 1;
        _nextTempId = 1;
        _variableLocals.Clear();
        _symbolLocals.Clear();
        _knownListLengths.Clear();
        _runtimeArrayLocals.Clear();
        _comprehensionElementLocals.Clear();
        _loopContextStack.Clear();

        // 创建入口块
        var entryBlock = NewBlock(isEntry: true);
        _currentBlock = entryBlock;
        var traitInvokeHelper = GetTraitInvokeHelperKind(func);

        _currentFunc = new MirFunc
        {
            Name = func.Name,
            SourceName = ResolveSourceFunctionName(func),
            Span = func.Span,
            SymbolId = func.SymbolId,
            FunctionId = BuildFunctionId(func.SymbolId, func.Name, ResolveSymbolKind(func.SymbolId)),
            TraitInvokeHelper = traitInvokeHelper,
            TraitInvokeHelperTraitId = GetTraitInvokeHelperTraitId(func, traitInvokeHelper),
            GenericParameterCount = func.TypeParams.Count,
            GenericParameters = GetGenericParameters(func),
            GenericTypeParameterIds = GetGenericTypeParameterIds(func),
            ReturnType = func.ReturnType,
            OwnershipContract = func.OwnershipContract,
            EntryBlockId = entryBlock.Id,
            IsEntry = func.IsEntry,
            IntrinsicName = func.IntrinsicName,
            BuiltinIntrinsicRole = func.BuiltinIntrinsicRole
        };
        _currentFunc.BasicBlocks.Add(entryBlock);

        // 将函数参数建模为 MIR 局部变量，避免变量引用时退化为匿名临时值
        foreach (var param in parameters)
        {
            var localId = NewLocal(param.Name, param.TypeId, isParameter: true, isMutable: param.IsMutable);
            _variableLocals[param.Name] = localId;
            if (param.SymbolId.IsValid)
            {
                _symbolLocals[param.SymbolId] = localId;
            }
        }

        // 转换函数体
        if (body != null)
        {
            var result = ConvertExpr(body);

            // 仅在当前块未终止时补充默认返回
            if (_currentBlock!.Terminator == null)
            {
                _currentBlock.Terminator = new MirReturn
                {
                    Value = result,
                    Span = func.Span
                };
            }
        }
        else if (_currentBlock!.Terminator == null)
        {
            // 无函数体，返回 Unit
            _currentBlock.Terminator = new MirReturn
            {
                Value = null,
                Span = func.Span
            };
        }

        return _currentFunc;
    }

    private static (IReadOnlyList<HirParam> Parameters, HirNode? Body) NormalizeCurriedFunctionBody(HirFunc func)
    {
        if (func.Parameters.Count == 0 ||
            func.Body is not HirLambda lambda)
        {
            return (func.Parameters, func.Body);
        }

        var flattenedLambda = FlattenCurriedLambdaBody(lambda);
        var prefixCount = Math.Max(0, func.Parameters.Count - flattenedLambda.Parameters.Count);
        var parameters = new List<HirParam>(Math.Max(func.Parameters.Count, flattenedLambda.Parameters.Count));
        parameters.AddRange(func.Parameters.Take(prefixCount));

        for (var i = 0; i < flattenedLambda.Parameters.Count; i++)
        {
            var bodyParameter = flattenedLambda.Parameters[i];
            var signatureIndex = prefixCount + i;
            var signatureType = signatureIndex < func.Parameters.Count ? func.Parameters[signatureIndex].TypeId : TypeId.None;
            parameters.Add(bodyParameter with
            {
                TypeId = signatureType.IsValid ? signatureType : bodyParameter.TypeId
            });
        }

        return (parameters, flattenedLambda.Body);
    }

    private MirFunc CreateDeclarationPlaceholder(HirFunc func)
    {
        var traitInvokeHelper = GetTraitInvokeHelperKind(func);
        var paramLocals = new List<MirLocal>(func.Parameters.Count);
        for (var i = 0; i < func.Parameters.Count; i++)
        {
            paramLocals.Add(new MirLocal
            {
                Id = new LocalId { Value = i + 1 },
                Name = func.Parameters[i].Name,
                TypeId = func.Parameters[i].TypeId,
                IsParameter = true
            });
        }

        return new MirFunc
        {
            Name = func.Name,
            SourceName = ResolveSourceFunctionName(func),
            Span = func.Span,
            SymbolId = func.SymbolId,
            FunctionId = BuildFunctionId(func.SymbolId, func.Name, ResolveSymbolKind(func.SymbolId)),
            TraitInvokeHelper = traitInvokeHelper,
            TraitInvokeHelperTraitId = GetTraitInvokeHelperTraitId(func, traitInvokeHelper),
            ReturnType = func.ReturnType,
            GenericParameterCount = func.TypeParams.Count,
            GenericParameters = GetGenericParameters(func),
            GenericTypeParameterIds = GetGenericTypeParameterIds(func),
            EntryBlockId = new BlockId { Value = 0 },
            Locals = paramLocals,
            IsEntry = func.IsEntry,
            IsExternal = func.IsExternal,
            ExternalSymbolName = func.ExternalSymbolName,
            ExternalLibrary = func.ExternalLibrary,
            IntrinsicName = func.IntrinsicName,
            BuiltinIntrinsicRole = func.BuiltinIntrinsicRole
        };
    }

    private static List<TypeId> GetGenericTypeParameterIds(HirFunc func)
    {
        if (func.TypeParams.Count == 0)
        {
            return [];
        }

        return func.TypeParams
            .Where(static typeParam => typeParam.ParameterKind == GenericParameterKind.Type)
            .Select(static typeParam => typeParam.TypeId)
            .Where(static typeId => typeId.IsValid)
            .ToList();
    }

    private static List<MirGenericParameter> GetGenericParameters(HirFunc func)
    {
        return func.TypeParams
            .Select(static (parameter, index) => new MirGenericParameter
            {
                ParameterIndex = index,
                SymbolId = parameter.SymbolId,
                Name = parameter.Name,
                ParameterKind = parameter.ParameterKind,
                TypeId = parameter.TypeId
            })
            .ToList();
    }

    private SymbolId GetTraitInvokeHelperTraitId(HirFunc func, TraitInvokeHelperKind helperKind)
    {
        if (helperKind == TraitInvokeHelperKind.None)
        {
            return SymbolId.None;
        }

        var traitConstraints = func.TypeParams
            .SelectMany(static typeParam => typeParam.Constraints)
            .Select(static constraint => constraint.SymbolId)
            .Where(static symbolId => symbolId.IsValid)
            .Distinct()
            .ToList();
        if (traitConstraints.Count == 0 &&
            _symbolTable?.GetSymbol<FuncSymbol>(func.SymbolId) is { TypeParams.Count: > 0 } funcSymbol)
        {
            traitConstraints = funcSymbol.TypeParams
                .Select(typeParamId => _symbolTable.GetSymbol<TypeParamSymbol>(typeParamId))
                .Where(static typeParam => typeParam != null)
                .SelectMany(static typeParam => typeParam!.TraitConstraints)
                .Where(static symbolId => symbolId.IsValid)
                .Distinct()
                .ToList();
        }

        return traitConstraints.Count == 1 ? traitConstraints[0] : SymbolId.None;
    }

    private MirOperand ConvertExpr(HirNode node)
    {
        return node switch
        {
            HirError error => ReportHirErrorExpr(error),
            HirCaseInject injection => ConvertCaseInject(injection),
            HirLiteral lit => ConvertLiteral(lit),
            HirConstGenericValue constGeneric => ConvertConstGenericValue(constGeneric),
            HirVar var => ConvertVar(var),
            HirBinOp binOp => ConvertBinOp(binOp),
            HirUnaryOp unaryOp => ConvertHirUnaryOp(unaryOp),
            HirCall call => ConvertCall(call),
            HirIf ifExpr => ConvertIf(ifExpr),
            HirLoop loop => ConvertLoop(loop),
            HirReturn returnExpr => ConvertReturn(returnExpr),
            HirBreak breakExpr => ConvertBreak(breakExpr),
            HirContinue continueExpr => ConvertContinue(continueExpr),
            HirUnreachable unreachable => ConvertUnreachable(unreachable),
            HirMatch match => ConvertMatch(match),
            HirLambda lambda => ConvertLambda(lambda),
            HirBlock block => ConvertBlock(block),
            HirTuple tuple => ConvertTuple(tuple),
            HirList list => ConvertList(list),
            HirListComprehension comprehension => ConvertListComprehension(comprehension),
            HirFieldAccess fieldAccess => ConvertFieldAccess(fieldAccess),
            HirIndexAccess indexAccess => ConvertIndexAccess(indexAccess),
            _ => ReportUnsupportedExpr(node)
        };
    }

    private void AddClosedDeclaredCopyLayoutsForDynamicTypes()
    {
        if (_symbolTable == null || _constructorLayouts.Count == 0)
        {
            return;
        }

        foreach (var (typeIdValue, descriptor) in _typeDescriptorsById.ToArray())
        {
            if (descriptor is not TypeDescriptor.TyCon
                {
                    Constructor.Kind: TypeConstructorKeyKind.Symbol
                } tyCon ||
                _constructorLayouts.ContainsKey(typeIdValue) ||
                _symbolTable.GetSymbol(tyCon.Constructor.SymbolId) is not { TypeId.IsValid: true } constructorSymbol ||
                !_constructorLayouts.TryGetValue(constructorSymbol.TypeId.Value, out var declaredLayouts) ||
                declaredLayouts.Count == 0 ||
                declaredLayouts.Any(layout => layout.FieldTypeIds.Any(IsOpenCopyLayoutField)))
            {
                continue;
            }

            _constructorLayouts[typeIdValue] = declaredLayouts;
        }
    }

    private bool IsOpenCopyLayoutField(TypeId fieldTypeId)
    {
        return (_typeDescriptorsById.TryGetValue(fieldTypeId.Value, out var descriptor) &&
                descriptor is TypeDescriptor.TypeVar) ||
               _symbolTable?.GetSymbol(new SymbolId(fieldTypeId.Value)) is TypeParamSymbol;
    }

    private MirOperand ConvertCaseInject(HirCaseInject injection)
    {
        var operand = ConvertExpr(injection.Operand);
        operand = EnsureReadValue(operand, injection.SourceTypeId, injection.Span);
        var target = NewTemp(injection.TypeId);
        _currentBlock!.Instructions.Add(new MirCaseInject
        {
            Target = target,
            Operand = operand,
            SourceCase = injection.SourceCase,
            TargetAncestor = injection.TargetAncestor,
            SourceTypeId = injection.SourceTypeId,
            TargetTypeId = injection.TypeId,
            Span = injection.Span
        });
        return target;
    }

    private static MirConstGenericValue ConvertConstGenericValue(HirConstGenericValue value)
    {
        return new MirConstGenericValue
        {
            SymbolId = value.SymbolId,
            Name = value.Name,
            ParameterIndex = value.ParameterIndex,
            Span = value.Span,
            TypeId = value.TypeId
        };
    }

    private MirOperand ConvertLiteral(HirLiteral lit)
    {
        MirConstantValue constantValue = lit.LiteralKind switch
        {
            LiteralKind.Int => new MirConstantValue.IntValue(lit.Value is long l ? l : Convert.ToInt64(lit.Value)),
            LiteralKind.Float => new MirConstantValue.FloatValue(Convert.ToDouble(lit.Value)),
            LiteralKind.String => new MirConstantValue.StringValue(lit.Value?.ToString() ?? ""),
            LiteralKind.Char => new MirConstantValue.CharValue(lit.Value is char c ? c : Convert.ToChar(lit.Value)),
            LiteralKind.Bool => new MirConstantValue.BoolValue(lit.Value is bool b ? b : Convert.ToBoolean(lit.Value)),
            _ => new MirConstantValue.UnitValue()
        };

        return new MirConstant
        {
            Value = constantValue,
            Span = lit.Span,
            TypeId = lit.TypeId
        };
    }

    private MirFunctionRef AttachTraitMethodMetadata(MirFunctionRef functionRef)
    {
        if (!functionRef.SymbolId.IsValid ||
            _symbolTable?.GetSymbol<FuncSymbol>(functionRef.SymbolId) is not { OwnerTrait: { IsValid: true } ownerTrait } method)
        {
            return functionRef;
        }

        return functionRef with
        {
            TraitOwnerId = ownerTrait,
            TraitSelfPosition = method.TraitSelfPosition,
            TraitSelfParameterIndices = method.TraitSelfParameterIndices.ToList(),
            TraitSelfInResult = method.TraitSelfInResult,
            TraitMethodRole = method.TraitMethodRole
        };
    }

    private MirOperand ConvertVar(HirVar var)
    {
        if (var.SymbolId.IsValid && _functionSymbols.TryGetValue(var.SymbolId, out var funcName))
        {
            return AttachTraitMethodMetadata(new MirFunctionRef
            {
                SymbolId = var.SymbolId,
                Name = funcName,
                SymbolKind = ResolveSymbolKind(var.SymbolId),
                FunctionId = BuildFunctionId(var.SymbolId, funcName, ResolveSymbolKind(var.SymbolId)),
                Span = var.Span,
                TypeId = var.TypeId,
                SignatureTypeId = ResolveFunctionSignatureTypeId(var.SymbolId, funcName, var.TypeId),
                TypeArgumentIds = [.. var.TypeArgumentIds],
                ValueArguments = [.. var.ValueArguments]
            });
        }

        if (!var.SymbolId.IsValid &&
            !string.IsNullOrEmpty(var.Name) &&
            _functionNames.TryGetValue(var.Name, out var funcSymbol) &&
            funcSymbol.IsValid)
        {
            var resolvedFunctionName = _functionSymbols.TryGetValue(funcSymbol, out var canonicalName)
                ? canonicalName
                : var.Name;
            return AttachTraitMethodMetadata(new MirFunctionRef
            {
                SymbolId = funcSymbol,
                Name = resolvedFunctionName,
                SymbolKind = ResolveSymbolKind(funcSymbol),
                FunctionId = BuildFunctionId(funcSymbol, resolvedFunctionName, ResolveSymbolKind(funcSymbol)),
                Span = var.Span,
                TypeId = var.TypeId,
                SignatureTypeId = ResolveFunctionSignatureTypeId(funcSymbol, resolvedFunctionName, var.TypeId),
                TypeArgumentIds = [.. var.TypeArgumentIds],
                ValueArguments = [.. var.ValueArguments]
            });
        }

        if (var.SymbolId.IsValid && _symbolLocals.TryGetValue(var.SymbolId, out var symbolLocal))
        {
            return new MirPlace
            {
                Kind = PlaceKind.Local,
                Local = symbolLocal,
                Span = var.Span,
                TypeId = var.TypeId
            };
        }

        // 查找变量的 LocalId
        if (_variableLocals.TryGetValue(var.Name, out var localId))
        {
            return new MirPlace
            {
                Kind = PlaceKind.Local,
                Local = localId,
                Span = var.Span,
                TypeId = var.TypeId
            };
        }

        if (TryGetModuleValueGetterForVariable(var, out var getterName, out var getterFunctionId))
        {
            var getterReturnType = ResolveModuleValueGetterReturnType(var);
            var target = NewTemp(getterReturnType);
            var symbolKind = ResolveSymbolKind(var.SymbolId);
            _currentBlock!.Instructions.Add(new MirCall
            {
                Target = target,
                Function = new MirFunctionRef
                {
                    SymbolId = var.SymbolId,
                    Name = getterName,
                    SymbolKind = symbolKind,
                    FunctionId = getterFunctionId,
                    TypeId = getterReturnType
                },
                Arguments = [],
                Span = var.Span
            });
            return target;
        }

        if (IsBlockedModuleValueReference(var))
        {
            return CreatePoisonOperand(var.TypeId, var.Span, DiagnosticMessages.BlockedModuleValueReferenceReason(var.Name));
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.UnresolvedVariableDuringMirLowering(var.Name),
            "E5001");
        if (HasSpan(var.Span))
        {
            diagnostic.WithLabel(var.Span, DiagnosticMessages.UnresolvedVariableLabel);
        }

        Diagnostics.Add(diagnostic);
        return CreatePoisonOperand(var.TypeId, var.Span, DiagnosticMessages.UnresolvedVariableReason(var.Name));
    }

    private bool TryGetModuleValueGetterForVariable(HirVar variable, out string getterName, out FunctionId functionId)
    {
        if (variable.SymbolId.IsValid &&
            _moduleValueGetterBySymbol.TryGetValue(variable.SymbolId, out var getterBySymbol))
        {
            getterName = getterBySymbol;
            functionId = ResolveModuleValueGetterFunctionId(variable, getterName);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(variable.Name) &&
            _moduleValueGetterByName.TryGetValue(variable.Name, out var getterByName))
        {
            getterName = getterByName;
            functionId = ResolveModuleValueGetterFunctionId(variable, getterName);
            return true;
        }

        getterName = string.Empty;
        functionId = new FunctionId();
        return false;
    }

    private FunctionId ResolveModuleValueGetterFunctionId(HirVar variable, string getterName)
    {
        if (variable.SymbolId.IsValid &&
            _moduleValueGetterFunctionIdBySymbol.TryGetValue(variable.SymbolId, out var bySymbol))
        {
            return bySymbol;
        }

        if (!string.IsNullOrWhiteSpace(variable.Name) &&
            _moduleValueGetterFunctionIdByName.TryGetValue(variable.Name, out var byName))
        {
            return byName;
        }

        return BuildGeneratedFunctionId(
            variable.SymbolId,
            getterName,
            ResolveSymbolKind(variable.SymbolId),
            "module_value_getter");
    }

    private TypeId ResolveModuleValueGetterReturnType(HirVar variable)
    {
        if (variable.SymbolId.IsValid &&
            _moduleValueGetterReturnTypeBySymbol.TryGetValue(variable.SymbolId, out var bySymbol) &&
            bySymbol.IsValid)
        {
            return bySymbol;
        }

        if (!string.IsNullOrWhiteSpace(variable.Name) &&
            _moduleValueGetterReturnTypeByName.TryGetValue(variable.Name, out var byName) &&
            byName.IsValid)
        {
            return byName;
        }

        if (variable.TypeId.IsValid)
        {
            return variable.TypeId;
        }

        return TypeId.None;
    }

    private bool IsBlockedModuleValueReference(HirVar variable)
    {
        return (variable.SymbolId.IsValid && _blockedModuleValueSymbols.Contains(variable.SymbolId)) ||
               (!string.IsNullOrWhiteSpace(variable.Name) && _blockedModuleValueNames.Contains(variable.Name));
    }

    private MirOperand ConvertBinOp(HirBinOp binOp)
    {
        if (binOp.Operator is Eidosc.Hir.BinaryOp.And or Eidosc.Hir.BinaryOp.Or)
        {
            return ConvertLogicalBinOp(binOp);
        }

        var left = ConvertExpr(binOp.Left);
        var right = ConvertExpr(binOp.Right);

        if (binOp.Operator is Eidosc.Hir.BinaryOp.Eq or Eidosc.Hir.BinaryOp.Ne &&
            IsStringType(binOp.Left.TypeId, left.TypeId) &&
            IsStringType(binOp.Right.TypeId, right.TypeId))
        {
            var equals = EmitRuntimeStringEquals(left, right, binOp.Span);
            return binOp.Operator == Eidosc.Hir.BinaryOp.Eq
                ? equals
                : EmitBooleanNegation(equals, binOp.Span);
        }

        if (binOp.Operator is Eidosc.Hir.BinaryOp.Eq or Eidosc.Hir.BinaryOp.Ne &&
            TryEmitEqTraitOperatorCall(binOp, left, right, out var traitEqualityResult))
        {
            return traitEqualityResult;
        }

        left = EnsureReadValue(left, binOp.Left.TypeId, binOp.Span);
        right = EnsureReadValue(right, binOp.Right.TypeId, binOp.Span);
        if (!TryConvertHirBinaryOpEnum(binOp.Operator, out var mirOperator))
        {
            return ReportUnsupportedBinaryOperator(binOp);
        }

        var target = NewTemp(binOp.TypeId);

        var mirBinOp = new MirBinOp
        {
            Target = target,
            Operator = mirOperator,
            Left = left,
            Right = right,
            Span = binOp.Span
        };

        _currentBlock!.Instructions.Add(mirBinOp);
        return target;
    }

    private bool TryEmitEqTraitOperatorCall(
        HirBinOp binOp,
        MirOperand left,
        MirOperand right,
        out MirOperand result)
    {
        result = null!;
        var operandType = ResolveEqualityOperandType(binOp.Left.TypeId, left.TypeId);
        if (!operandType.IsValid || ShouldLowerEqualityAsPrimitive(operandType))
        {
            return false;
        }

        if (!TryResolveEqTraitMethod(operandType, out var eqMethod))
        {
            return false;
        }

        var boolType = new TypeId(BaseTypes.BoolId);
        var target = NewTemp(boolType);
        var eqMethodKind = ResolveSymbolKind(eqMethod.Id);
        _currentBlock!.Instructions.Add(new MirCall
        {
            Target = target,
            Function = AttachTraitMethodMetadata(new MirFunctionRef
            {
                SymbolId = eqMethod.Id,
                Name = eqMethod.Name,
                SymbolKind = eqMethodKind,
                FunctionId = BuildFunctionId(eqMethod.Id, eqMethod.Name, eqMethodKind),
                TypeId = eqMethod.TypeId,
                Span = binOp.Span
            }),
            Arguments =
            [
                PrepareCallArgument(left, binOp.Left.TypeId, binOp.Span, forceCopy: true),
                PrepareCallArgument(right, binOp.Right.TypeId, binOp.Span, forceCopy: true)
            ],
            Span = binOp.Span
        });

        ClearKnownListLength(target);
        ClearRuntimeArrayLocal(target);
        result = binOp.Operator == Eidosc.Hir.BinaryOp.Eq
            ? target
            : EmitBooleanNegation(target, binOp.Span);
        return true;
    }

    private static TypeId ResolveEqualityOperandType(TypeId primary, TypeId fallback) =>
        primary.IsValid ? primary : fallback;

    private static bool ShouldLowerEqualityAsPrimitive(TypeId typeId) =>
        BaseTypes.IsBuiltIn(typeId);

    private bool TryResolveEqTraitMethod(TypeId operandType, out FuncSymbol eqMethod)
    {
        eqMethod = null!;
        if (_symbolTable == null)
        {
            return false;
        }

        var matches = _symbolTable.Symbols.Values
            .OfType<FuncSymbol>()
            .Where(static symbol => symbol.TraitMethodRole == TraitMethodRole.Equality)
            .ToList();
        if (operandType.IsValid)
        {
            var applicableMatches = matches
                .Where(method => method.OwnerTrait is { IsValid: true } ownerTrait &&
                                 _symbolTable.LookupImplForTrait(operandType, ownerTrait) is { HasRuntimeMethods: true })
                .ToList();
            if (applicableMatches.Count == 1)
            {
                eqMethod = applicableMatches[0];
                return true;
            }
        }

        if (matches.Count != 1)
        {
            return false;
        }

        eqMethod = matches[0];
        return true;
    }

    private MirOperand ConvertLogicalBinOp(HirBinOp binOp)
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var resultType = binOp.TypeId.IsValid ? binOp.TypeId : boolType;
        var left = ConvertExpr(binOp.Left);
        left = EnsureReadValue(left, boolType, binOp.Left.Span);

        var evaluateRightBlock = NewBlock();
        var shortCircuitBlock = NewBlock();
        var mergeBlock = NewBlock();
        var resultPlace = NewTemp(resultType);
        var shortCircuitValue = binOp.Operator == Eidosc.Hir.BinaryOp.Or;
        var trueTarget = binOp.Operator == Eidosc.Hir.BinaryOp.Or
            ? shortCircuitBlock.Id
            : evaluateRightBlock.Id;
        var falseTarget = binOp.Operator == Eidosc.Hir.BinaryOp.Or
            ? evaluateRightBlock.Id
            : shortCircuitBlock.Id;

        _currentBlock!.Terminator = new MirSwitch
        {
            Discriminant = left,
            Branches =
            [
                new MirSwitchBranch
                {
                    Value = CreateBoolConstant(true, binOp.Span),
                    Target = trueTarget
                }
            ],
            DefaultTarget = falseTarget,
            Span = binOp.Span
        };

        _currentFunc!.BasicBlocks.Add(evaluateRightBlock);
        _currentBlock = evaluateRightBlock;
        var right = ConvertExpr(binOp.Right);
        right = EnsureReadValue(right, boolType, binOp.Right.Span);
        EmitInitialization(resultPlace, right, binOp.Span);
        _currentBlock.Terminator = new MirGoto
        {
            Target = mergeBlock.Id,
            Span = binOp.Span
        };

        _currentFunc.BasicBlocks.Add(shortCircuitBlock);
        _currentBlock = shortCircuitBlock;
        EmitInitialization(resultPlace, CreateBoolConstant(shortCircuitValue, binOp.Span), binOp.Span);
        _currentBlock.Terminator = new MirGoto
        {
            Target = mergeBlock.Id,
            Span = binOp.Span
        };

        _currentFunc.BasicBlocks.Add(mergeBlock);
        _currentBlock = mergeBlock;
        return resultPlace;
    }

    private MirOperand ConvertHirUnaryOp(HirUnaryOp unaryOp)
    {
        if (unaryOp.Operator is Eidosc.Hir.UnaryOp.AddressOf or Eidosc.Hir.UnaryOp.Ref or Eidosc.Hir.UnaryOp.MRef)
        {
            var place = TryConvertPlaceShapedExprPlace(unaryOp.Operand, out var borrowablePlace)
                ? borrowablePlace
                : EnsurePlaceOperand(ConvertExpr(unaryOp.Operand), unaryOp.Operand.TypeId, unaryOp.Span);
            return place with
            {
                Span = unaryOp.Span,
                TypeId = unaryOp.TypeId.IsValid ? unaryOp.TypeId : place.TypeId
            };
        }

        if (unaryOp.Operator == Eidosc.Hir.UnaryOp.Deref)
        {
            var derefOperand = ConvertExpr(unaryOp.Operand);
            var basePlace = EnsurePlaceOperand(derefOperand, unaryOp.Operand.TypeId, unaryOp.Span);
            var derefType = unaryOp.TypeId.IsValid ? unaryOp.TypeId : unaryOp.Operand.TypeId;
            var derefPlace = new MirPlace
            {
                Kind = PlaceKind.Deref,
                Base = basePlace,
                TypeId = derefType,
                Span = unaryOp.Span
            };

            return EnsureReadValue(derefPlace, derefType, unaryOp.Span);
        }

        var operand = ConvertExpr(unaryOp.Operand);
        operand = EnsureReadValue(operand, unaryOp.Operand.TypeId, unaryOp.Span);
        if (!TryConvertHirUnaryOpEnum(unaryOp.Operator, out var mirOperator))
        {
            return ReportUnsupportedUnaryOperator(unaryOp);
        }

        var target = NewTemp(unaryOp.TypeId);

        var mirUnaryOp = new MirUnaryOp
        {
            Target = target,
            Operator = mirOperator,
            Operand = operand,
            Span = unaryOp.Span
        };

        _currentBlock!.Instructions.Add(mirUnaryOp);
        return target;
    }

    private MirOperand ConvertCall(HirCall call)
    {
        if (TryFlattenDirectCurriedCall(call, out var flattenedOperand))
        {
            return flattenedOperand;
        }

        var func = ResolveCallFunctionOperand(call);
        var args = new List<MirOperand>(call.Arguments.Count);
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var argument = call.Arguments[i];
            args.Add(PrepareCallArgumentForNode(argument, call.Span, forceCopy: false));
        }
        var resultType = ResolveCallResultType(call, func);
        func = AttachCallSiteSignature(func, args, resultType);

        if (resultType.Value == BaseTypes.NeverId)
        {
            var divergingCall = new MirCall
            {
                Target = null,
                Function = func,
                Arguments = args,
                Span = call.Span
            };

            _currentBlock!.Instructions.Add(divergingCall);
            _currentBlock.Terminator = new MirUnreachable { Span = call.Span };
            MoveToSyntheticUnreachableBlock(call.Span);
            return CreatePoisonOperand(resultType, call.Span, WellKnownStrings.BuiltinTypes.Never);
        }

        var target = NewTemp(resultType);

        var mirCall = new MirCall
        {
            Target = target,
            Function = func,
            Arguments = args,
            Span = call.Span
        };

        _currentBlock!.Instructions.Add(mirCall);
        if (func is MirFunctionRef { Name: WellKnownStrings.InternalNames.ArrayNew or WellKnownStrings.InternalNames.ArrayPush })
        {
            RegisterRuntimeArrayLocal(target);
        }
        else
        {
            ClearRuntimeArrayLocal(target);
        }

        ClearKnownListLength(target);
        return target;
    }

    private MirOperand AttachCallSiteSignature(
        MirOperand functionOperand,
        IReadOnlyList<MirOperand> arguments,
        TypeId resultType)
    {
        if (functionOperand is not MirFunctionRef functionRef ||
            !resultType.IsValid ||
            arguments.Any(static argument => !argument.TypeId.IsValid))
        {
            return functionOperand;
        }

        var signatureTypeId = GetOrCreateFunctionSignatureTypeId(
            arguments.Select(static argument => argument.TypeId).ToArray(),
            resultType);
        if (!signatureTypeId.IsValid)
        {
            return functionRef;
        }

        return functionRef with
        {
            SignatureTypeId = signatureTypeId
        };
    }

    private FunctionId BuildGeneratedFunctionId(SymbolId symbolId, string displayName, SymbolKind kind, string role)
    {
        return symbolId.IsValid
            ? BuildFunctionId(symbolId, displayName, kind)
            : BuildSyntheticFunctionId(role, displayName);
    }

    private FunctionId BuildSyntheticFunctionId(string role, string displayName)
    {
        var moduleName = FormatModuleName(null, _currentModulePath);
        var moduleIdentityKey = ModuleRegistry.ToModuleIdentityKey(null, null, _currentModulePath);
        var ordinal = _nextSyntheticFunctionId++;
        var normalizedRole = NormalizeIdentifierSegment(role, "synthetic");
        var normalizedDisplayName = NormalizeIdentifierSegment(displayName, "function");
        var moduleKey = string.IsNullOrWhiteSpace(moduleName) ? "main" : moduleName;
        return new FunctionId
        {
            SymbolId = SymbolId.None,
            Kind = SymbolKind.Function,
            Name = displayName,
            Module = moduleName,
            ModuleIdentityKey = moduleIdentityKey,
            QualifiedName = $"synthetic:{moduleKey}:{ordinal}:{normalizedRole}:{normalizedDisplayName}"
        };
    }

    private MirOperand ResolveCallFunctionOperand(HirCall call)
    {
        if (call.Convention == CallConvention.Constructor &&
            TryResolveConstructorFunction(call, out var constructorFunction))
        {
            return constructorFunction;
        }

        if (call.Function is HirVar functionVar &&
            TryResolveReferencedFunction(functionVar, call.TypeId, out var referencedFunction))
        {
            return referencedFunction;
        }

        return ConvertExpr(call.Function);
    }

    // Collapse a direct curried application chain (e.g. f(a)(b)(c)) into a single
    // multi-argument MirCall. This mirrors FlattenCurriedLambdaBody on the definition
    // side: a curried definition flattens to one multi-parameter MirFunc, but the call
    // site otherwise lowers to nested single-arg MirCalls, which breaks tail-call
    // recognition (the self-recursive edge is the inner call, not in tail position).
    //
    // Conservative: only Normal-convention direct calls to a resolvable named function
    // are collapsed; anything else (constructors, ability ops, effect dispatch, with
    // handlers, closure/indirect targets, partial application producing a function
    // value) falls back to the original per-step lowering.
    private bool TryFlattenDirectCurriedCall(HirCall call, out MirOperand operand)
    {
        operand = default!;

        // Only worth flattening when the immediate Function is itself a call.
        if (call.Function is not HirCall)
        {
            return false;
        }

        // Bail on anything that is not a plain direct call. Handler chains and
        // non-Normal conventions must keep their existing lowering.
        if (call.Convention != CallConvention.Normal)
        {
            return false;
        }

        // Walk the application chain inner-most-first, collecting argument groups so
        // that the final order matches the multi-parameter function signature:
        //   f(a1)(a2)...(ak)  ->  f(a1, a2, ..., ak)
        var argumentGroups = new List<IReadOnlyList<HirNode>>(capacity: 4);
        HirVar? leafFunction = null;
        var current = call;
        while (true)
        {
            if (current.Convention != CallConvention.Normal)
            {
                return false;
            }

            argumentGroups.Add(current.Arguments);
            switch (current.Function)
            {
                case HirVar leaf:
                    leafFunction = leaf;
                    goto ChainCollected;
                case HirCall nested:
                    current = nested;
                    break;
                default:
                    // Indirect / non-call-non-var target: cannot flatten safely.
                    return false;
            }
        }

    ChainCollected:
        if (leafFunction is null)
        {
            return false;
        }

        // Resolve the leaf as a direct named function. Cross-module / FFI / closure
        // targets that cannot be resolved as a function ref fall back.
        if (!TryResolveReferencedFunction(leafFunction, call.TypeId, out var functionRef))
        {
            return false;
        }

        // Generic functions must NOT be flattened here: they are template candidates
        // for MirGenericSpecializer, which relies on the nested application shape to
        // detect and specialize partial application. Flattening a generic callee would
        // bypass specialization and regress trait-constrained higher-order calls.
        if (IsGenericFunctionCandidate(leafFunction))
        {
            return false;
        }

        // Flatten arguments inner-first so order matches parameter declaration order.
        var totalArgCount = 0;
        for (var i = 0; i < argumentGroups.Count; i++)
        {
            totalArgCount += argumentGroups[i].Count;
        }

        if (totalArgCount == 0)
        {
            return false;
        }

        var flattenedArgs = new List<MirOperand>(totalArgCount);
        for (var groupIndex = argumentGroups.Count - 1; groupIndex >= 0; groupIndex--)
        {
            var group = argumentGroups[groupIndex];
            for (var argIndex = 0; argIndex < group.Count; argIndex++)
            {
                flattenedArgs.Add(PrepareCallArgumentForNode(group[argIndex], call.Span, forceCopy: false));
            }
        }

        var resultType = ResolveCallResultType(call, functionRef);
        functionRef = (MirFunctionRef)AttachCallSiteSignature(functionRef, flattenedArgs, resultType);

        if (resultType.Value == BaseTypes.NeverId)
        {
            var divergingCall = new MirCall
            {
                Target = null,
                Function = functionRef,
                Arguments = flattenedArgs,
                Span = call.Span
            };

            _currentBlock!.Instructions.Add(divergingCall);
            _currentBlock.Terminator = new MirUnreachable { Span = call.Span };
            MoveToSyntheticUnreachableBlock(call.Span);
            operand = CreatePoisonOperand(resultType, call.Span, WellKnownStrings.BuiltinTypes.Never);
            return true;
        }

        var target = NewTemp(resultType);
        var mirCall = new MirCall
        {
            Target = target,
            Function = functionRef,
            Arguments = flattenedArgs,
            Span = call.Span
        };

        _currentBlock!.Instructions.Add(mirCall);
        if (functionRef is { Name: WellKnownStrings.InternalNames.ArrayNew or WellKnownStrings.InternalNames.ArrayPush })
        {
            RegisterRuntimeArrayLocal(target);
        }
        else
        {
            ClearRuntimeArrayLocal(target);
        }

        ClearKnownListLength(target);
        operand = target;
        return true;
    }

    // True if the named function is polymorphic (has type parameters) and therefore
    // a specialization template candidate. Such functions must keep their nested
    // curried-application shape so MirGenericSpecializer can specialize them.
    private bool IsGenericFunctionCandidate(HirVar functionVar)
    {
        if (functionVar.TypeArgumentIds.Count > 0 || functionVar.ValueArguments.Count > 0)
        {
            return true;
        }

        if (_symbolTable != null &&
            functionVar.SymbolId.IsValid &&
            _symbolTable.Symbols.TryGetValue(functionVar.SymbolId, out var symbol) &&
            symbol is FuncSymbol { TypeParams.Count: > 0 })
        {
            return true;
        }

        return false;
    }

    private bool TryResolveConstructorFunction(HirCall call, out MirFunctionRef constructorFunction)
    {
        if (call.Function is HirVar { Name: var name } constructorVar &&
            !string.IsNullOrWhiteSpace(name))
        {
            var constructorResultType = call.TypeId.IsValid
                ? call.TypeId
                : constructorVar.TypeId;

            constructorFunction = new MirFunctionRef
            {
                Name = name,
                SymbolId = constructorVar.SymbolId,
                SymbolKind = SymbolKind.Constructor,
                FunctionId = BuildConstructorFunctionId(constructorVar.SymbolId, name, constructorResultType),
                TypeId = constructorResultType,
                TypeArgumentIds = [.. constructorVar.TypeArgumentIds],
                ValueArguments = [.. constructorVar.ValueArguments],
                Span = constructorVar.Span
            };
            return true;
        }

        constructorFunction = default!;
        return false;
    }

    private bool TryResolveReferencedFunction(
        HirVar functionVar,
        TypeId callResultType,
        out MirFunctionRef functionRef)
    {
        if (!functionVar.SymbolId.IsValid ||
            _symbolLocals.ContainsKey(functionVar.SymbolId) ||
            string.IsNullOrWhiteSpace(functionVar.Name))
        {
            functionRef = default!;
            return false;
        }

        var functionType = functionVar.TypeId.IsValid
            ? functionVar.TypeId
            : callResultType;
        var loweredFunctionName = _functionSymbols.TryGetValue(functionVar.SymbolId, out var resolvedFunctionName) &&
                                  !string.IsNullOrWhiteSpace(resolvedFunctionName)
            ? resolvedFunctionName
            : functionVar.Name;

        functionRef = AttachTraitMethodMetadata(new MirFunctionRef
        {
            Name = loweredFunctionName,
            SymbolId = functionVar.SymbolId,
            SymbolKind = ResolveSymbolKind(functionVar.SymbolId),
            FunctionId = BuildFunctionId(functionVar.SymbolId, loweredFunctionName, ResolveSymbolKind(functionVar.SymbolId)),
            TypeId = functionType,
            SignatureTypeId = ResolveFunctionSignatureTypeId(functionVar.SymbolId, loweredFunctionName, functionType),
            TypeArgumentIds = [.. functionVar.TypeArgumentIds],
            ValueArguments = [.. functionVar.ValueArguments],
            Span = functionVar.Span
        });
        return true;
    }

}
