using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;
using System.Text;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// MIR 泛型特化：根据调用点实参类型生成具体实例并重写调用。
/// </summary>
public sealed partial class MirGenericSpecializer : IMirOptimizationPass
{
    private readonly Func<TypeId, bool>? _hasCopyImplResolver;
    private readonly Func<string, IDisposable>? _measureSubphase;
    private readonly HashSet<int> _extraCopyLikeTypeIds = [];
    private readonly SpecializerTemplateRegistry _templateRegistry = new();
    private readonly SpecializerDynamicTypeTable _dynamicTypes = new();
    private readonly Dictionary<SymbolId, TypeId> _functionTypeIdBySymbol = [];
    private readonly Dictionary<string, TypeId> _functionTypeIdByName = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, string> _loweredFunctionNameBySymbol = [];
    private readonly Dictionary<SymbolId, MirFunc> _functionBySymbol = [];
    private readonly Dictionary<MirFunc, List<MirLocal>> _templateParametersByFunction = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionId, string> _functionIdentityKeyCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionId, string> _functionIdentityFallbackKeyCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, string> _templateKeyByFunctionIndex = [];
    private readonly HashSet<int> _clonedWorkingFunctionIndices = [];
    private readonly HashSet<string> _genericTemplateFunctionIdentityKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _genericTemplateFunctionNames = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolId, MirTraitInfo> _traitInfoById = [];
    private readonly Dictionary<SymbolId, MirTraitMethodInfo> _traitMethodInfoById = [];
    private readonly Dictionary<SymbolId, MirTypeConstructorInfo> _typeConstructorInfoBySymbol = [];
    private readonly Dictionary<int, MirTypeConstructorInfo> _typeConstructorInfoByTypeId = [];
    private readonly HashSet<int> _genericTypeParameterTypeIds = [];
    private readonly List<ImplSymbol> _moduleTraitImpls = [];
    private readonly List<MirTypeAliasInfo> _moduleTypeAliases = [];
    private readonly Dictionary<SpecializationCacheKey, MirFunc> _specializationsByTemplateAndSignature = [];
    private readonly Dictionary<SpecializationCacheKey, SpecializationBindings> _typeBindingsByTemplateAndSignature = [];
    private readonly Dictionary<SpecializationCacheKey, bool> _meaningfulSignatureByTemplateAndSignature = [];
    private readonly HashSet<SpecializationCacheKey> _rejectedSpecializationsByTemplateAndSignature = [];
    private readonly HashSet<string> _reportedRejectedSpecializationsByTemplateAndSignature = new(StringComparer.Ordinal);
    private readonly HashSet<string> _specializedTemplateKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<TraitDispatchLookupKey, TraitDispatchLookupResult> _traitDispatchLookupCache = [];
    private readonly Dictionary<int, FlattenedFunctionType> _flattenedFunctionTypesByTypeId = [];
    private readonly Dictionary<int, bool> _containsOpenTypeVariableByTypeId = [];
    private readonly HashSet<string> _usedFunctionNames = new(StringComparer.Ordinal);
    private readonly List<Diagnostic.Diagnostic> _diagnostics = [];
    private readonly List<SpecializationFailure> _failures = [];
    private readonly MirGenericSpecializerStats _stats = new();
    private readonly HashSet<LocalId> _dirtyLocalTypeIds = [];
    private int _nextSyntheticSymbolId;
    private int _nextSpecializationNameOrdinal = 1;

    public string Name => "MirGenericSpecializer";

    public List<Diagnostic.Diagnostic> Diagnostics => _diagnostics;

    internal IReadOnlyList<SpecializationFailure> Failures => _failures;

    public MirGenericSpecializerStats Stats => _stats;

    public MirGenericSpecializer()
        : this(null, null, null)
    {
    }

    public MirGenericSpecializer(
        Func<TypeId, bool>? hasCopyImplResolver = null,
        IReadOnlySet<TypeId>? extraCopyLikeTypeIds = null,
        SymbolTable? symbolTable = null,
        Func<string, IDisposable>? measureSubphase = null)
    {
        _hasCopyImplResolver = hasCopyImplResolver;
        _measureSubphase = measureSubphase;
        _ = symbolTable;
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

    internal sealed record TemplateInfo(string Key, MirFunc TemplateSource, MirFunc OriginalWorkingFunction);

    private sealed record SpecializationSignature(TypeId ReturnType, List<TypeId> ParameterTypes)
    {
        private SpecializationSignatureKey? _key;

        public SpecializationSignatureKey ToKey()
        {
            if (_key.HasValue)
            {
                return _key.GetValueOrDefault();
            }

            var parameterTypeValues = ParameterTypes.Count == 0
                ? []
                : new int[ParameterTypes.Count];
            for (var i = 0; i < ParameterTypes.Count; i++)
            {
                parameterTypeValues[i] = ParameterTypes[i].Value;
            }

            var key = new SpecializationSignatureKey(ReturnType.Value, parameterTypeValues);
            _key = key;
            return key;
        }

        public string ToKeyString()
        {
            return ToKey().ToString();
        }
    }

    private sealed record LocalCallBinding(
        MirFunctionRef FunctionRef,
        List<MirOperand> BoundArguments,
        string BoundArgumentKey,
        bool SupportsDirectApplication);

    private readonly record struct RecordedPartialBinding(LocalId TargetLocal, LocalCallBinding Binding);

    private sealed record FlattenedFunctionType(bool IsFunction, List<TypeId> ParameterTypes, TypeId ResultType);

    private readonly record struct SpecializationWork(bool NeedsRewrite, bool HasDroppableGenericTemplates);

    private readonly record struct RewriteQueueItem(int FunctionIndex, FunctionRewriteSummary Summary);

    private readonly record struct FunctionRewriteCandidateSite(int BlockIndex, int InstructionIndex);

    private sealed record FunctionRewriteSummary(
        bool NeedsRewrite,
        bool NeedsFullFunctionScan,
        int CandidateBlockCount,
        int CandidateInstructionCount,
        bool CanUseCandidateBlockScan,
        int[] CandidateBlockIndices,
        FunctionRewriteCandidateSite[] CandidateInstructionSites);

    public MirModule Run(MirModule module)
    {
        _diagnostics.Clear();
        _failures.Clear();
        _stats.Clear();
        ResetState(module);

        SpecializationWork work;
        using (MeasureSpecializerSubphase("classify_work"))
        {
            work = ClassifySpecializationWork(module);
        }
        if (!work.NeedsRewrite)
        {
            Dictionary<int, List<ConstructorTypeLayout>> constructorLayouts;
            using (MeasureSpecializerSubphase("fast_path.layouts"))
            {
                constructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>(module.ConstructorLayouts);
                PopulateSpecializedConstructorLayoutsMeasured(module, constructorLayouts);
            }

            List<MirFunc> functions;
            using (MeasureSpecializerSubphase("fast_path.drop_templates"))
            {
                functions = work.HasDroppableGenericTemplates
                    ? DropUnreferencedGenericTemplates(module.Functions)
                    : module.Functions;
            }

            return constructorLayouts.Count == module.ConstructorLayouts.Count &&
                   ReferenceEquals(functions, module.Functions)
                ? module
                : CreateMeasuredOutputModule(module, functions, constructorLayouts);
        }

        List<MirFunc> workingFunctions;
        using (MeasureSpecializerSubphase("prepare_working_functions"))
        {
            workingFunctions = new List<MirFunc>(module.Functions);
        }

        using (MeasureSpecializerSubphase("index_templates"))
        {
            IndexGenericTemplates(module, workingFunctions);
        }
        if (_templateRegistry.ByKeyDict.Count == 0)
        {
            using (MeasureSpecializerSubphase("rewrite_trait_dispatch_only"))
            {
                RewriteTraitDispatchOnly(workingFunctions);
            }
            var traitOnlyConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>(module.ConstructorLayouts);
            PopulateSpecializedConstructorLayoutsMeasured(module, traitOnlyConstructorLayouts);
            return CreateMeasuredOutputModule(module, workingFunctions, traitOnlyConstructorLayouts);
        }

        using (MeasureSpecializerSubphase("rewrite_queue"))
        {
            var queue = CreateInitialRewriteQueue(workingFunctions);
            while (queue.Count > 0)
            {
                _stats.RewriteQueueMaxDepth = Math.Max(_stats.RewriteQueueMaxDepth, queue.Count);
                _stats.RewriteQueueDequeues++;
                var item = queue.Dequeue();
                var current = EnsureClonedWorkingFunction(workingFunctions, item.FunctionIndex);
                RewriteFunctionCalls(current, item.Summary, workingFunctions, queue);
            }

        }

        using (MeasureSpecializerSubphase("rewrite_late_trait_dispatch"))
        {
            RewriteLateTraitDispatch(workingFunctions);
        }

        HashSet<string> liveTemplateKeys;
        using (MeasureSpecializerSubphase("collect_live_templates"))
        {
            liveTemplateKeys = CollectLiveTemplateKeys(workingFunctions);
        }

        List<MirFunc> filteredFunctions;
        using (MeasureSpecializerSubphase("filter_templates"))
        {
            filteredFunctions = new List<MirFunc>(workingFunctions.Count);
            foreach (var function in workingFunctions)
            {
                if (ShouldDropOriginalTemplate(function, liveTemplateKeys))
                {
                    continue;
                }

                filteredFunctions.Add(function);
            }
        }

        using (MeasureSpecializerSubphase("prune_specializations"))
        {
            filteredFunctions = PruneUnreferencedGeneratedSpecializations(filteredFunctions);
        }
        RetainSpecializationFailuresForOutputFunctions(filteredFunctions);

        var outputConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>(module.ConstructorLayouts);
        PopulateSpecializedConstructorLayoutsMeasured(module, outputConstructorLayouts);

        return CreateMeasuredOutputModule(module, filteredFunctions, outputConstructorLayouts);
    }

    private void PopulateSpecializedConstructorLayoutsMeasured(
        MirModule module,
        Dictionary<int, List<ConstructorTypeLayout>> constructorLayouts)
    {
        using (MeasureSpecializerSubphase("populate_constructor_layouts"))
        {
            PopulateSpecializedConstructorLayouts(module, constructorLayouts);
        }
    }

    private MirModule CreateMeasuredOutputModule(
        MirModule source,
        List<MirFunc> functions,
        Dictionary<int, List<ConstructorTypeLayout>> constructorLayouts)
    {
        using (MeasureSpecializerSubphase("create_output_module"))
        {
            return CreateOutputModule(source, functions, constructorLayouts);
        }
    }

    private IDisposable MeasureSpecializerSubphase(string name)
    {
        return _measureSubphase?.Invoke($"specializer.{name}") ?? NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        private NullDisposable()
        {
        }

        public void Dispose()
        {
        }
    }

    private SpecializationWork ClassifySpecializationWork(MirModule module)
    {
        var hasDroppableGenericTemplates = false;
        foreach (var function in module.Functions)
        {
            if (!IsExecutableEntryFunction(function) &&
                IsGenericTemplateCandidate(function) &&
                !IsGeneratedSpecialization(function.Name))
            {
                hasDroppableGenericTemplates = true;
                continue;
            }

            if (function.TraitInvokeHelper != TraitInvokeHelperKind.None)
            {
                return new SpecializationWork(true, hasDroppableGenericTemplates);
            }

            if (FunctionHasReferenceRequiringSpecialization(function))
            {
                return new SpecializationWork(true, hasDroppableGenericTemplates);
            }
        }

        return new SpecializationWork(false, hasDroppableGenericTemplates);
    }

    private bool RequiresSpecializationPass(MirFunctionRef functionRef)
    {
        return functionRef.TraitOwnerId.IsValid ||
               functionRef.SymbolId.IsValid && _traitMethodInfoById.ContainsKey(functionRef.SymbolId) ||
               functionRef.TraitSelfPosition != SelfPosition.Unknown ||
               functionRef.TraitSelfParameterIndices.Count > 0 ||
               functionRef.TraitSelfInResult ||
               functionRef.TraitMethodRole != TraitMethodRole.None;
    }

    private bool ReferencesGenericTemplateCandidate(MirFunctionRef functionRef)
    {
        var hasUnresolvedValidSymbol = false;
        if (functionRef.SymbolId.IsValid &&
            _functionBySymbol.TryGetValue(functionRef.SymbolId, out var function))
        {
            return IsGenericTemplateCandidate(function) &&
                   !IsGeneratedSpecialization(function.Name);
        }
        else if (functionRef.SymbolId.IsValid)
        {
            hasUnresolvedValidSymbol = true;
        }

        if (TryGetTemplateFunctionIdentityKey(functionRef.FunctionId, out var functionRefIdentityKey))
        {
            if (_genericTemplateFunctionIdentityKeys.Contains(functionRefIdentityKey))
            {
                return true;
            }
        }

        if (TryGetTemplateFunctionIdentityFallbackKey(functionRef.FunctionId, out var fallbackIdentityKey))
        {
            if (_genericTemplateFunctionIdentityKeys.Contains(fallbackIdentityKey))
            {
                return true;
            }
        }

        return !hasUnresolvedValidSymbol &&
               !string.IsNullOrWhiteSpace(functionRef.Name) &&
               _genericTemplateFunctionNames.Contains(functionRef.Name);
    }

    private List<MirFunc> DropUnreferencedGenericTemplates(List<MirFunc> functions)
    {
        var filtered = new List<MirFunc>(functions.Count);
        foreach (var function in functions)
        {
            if (!IsExecutableEntryFunction(function) &&
                IsGenericSignature(function) &&
                !IsGeneratedSpecialization(function.Name))
            {
                continue;
            }

            filtered.Add(function);
        }

        return filtered.Count == functions.Count ? functions : filtered;
    }

    private MirModule CreateOutputModule(
        MirModule source,
        List<MirFunc> functions,
        Dictionary<int, List<ConstructorTypeLayout>> constructorLayouts)
    {
        return new MirModule
        {
            Name = source.Name,
            PackageAlias = source.PackageAlias,
            PackageInstanceKey = source.PackageInstanceKey,
            Path = source.Path,
            Functions = functions,
            DynamicTypeKeys = new Dictionary<int, string>(_dynamicTypes.KeyByIdDict),
            TypeDescriptors = new Dictionary<int, TypeDescriptor>(_dynamicTypes.DescriptorByIdDict),
            ConstructorLayouts = constructorLayouts,
            TraitImpls = source.TraitImpls.ToList(),
            TraitInfos = source.TraitInfos.ToList(),
            TypeAliases = source.TypeAliases.ToList(),
            TypeConstructors = source.TypeConstructors.ToList(),
            LinkLibraries = source.LinkLibraries.ToList(),
            SpecializationFailures = MergeSpecializationFailures(source.SpecializationFailures, _failures),
            Span = source.Span
        };
    }

    private static List<MirSpecializationFailureInfo> MergeSpecializationFailures(
        IReadOnlyList<MirSpecializationFailureInfo> existingFailures,
        IReadOnlyList<SpecializationFailure> newFailures)
    {
        if (existingFailures.Count == 0 && newFailures.Count == 0)
        {
            return [];
        }

        var result = new List<MirSpecializationFailureInfo>(existingFailures.Count + newFailures.Count);
        var seen = new HashSet<MirSpecializationFailureInfo>();
        foreach (var failure in existingFailures)
        {
            if (seen.Add(failure))
            {
                result.Add(failure);
            }
        }

        foreach (var failure in newFailures)
        {
            var info = failure.ToMirInfo();
            if (seen.Add(info))
            {
                result.Add(info);
            }
        }

        return result;
    }

    private void ResetState(MirModule module)
    {
        _templateRegistry.Clear();
        _dynamicTypes.Clear();
        _functionTypeIdBySymbol.Clear();
        _functionTypeIdByName.Clear();
        _loweredFunctionNameBySymbol.Clear();
        _functionBySymbol.Clear();
        _templateParametersByFunction.Clear();
        _functionIdentityKeyCache.Clear();
        _functionIdentityFallbackKeyCache.Clear();
        _templateKeyByFunctionIndex.Clear();
        _clonedWorkingFunctionIndices.Clear();
        _genericTemplateFunctionIdentityKeys.Clear();
        _genericTemplateFunctionNames.Clear();
        _traitInfoById.Clear();
        _traitMethodInfoById.Clear();
        _typeConstructorInfoBySymbol.Clear();
        _typeConstructorInfoByTypeId.Clear();
        _genericTypeParameterTypeIds.Clear();
        _moduleTraitImpls.Clear();
        _moduleTypeAliases.Clear();
        _moduleTraitImpls.AddRange(module.TraitImpls);
        _moduleTypeAliases.AddRange(module.TypeAliases);
        foreach (var traitInfo in module.TraitInfos)
        {
            if (traitInfo.TraitId.IsValid)
            {
                _traitInfoById[traitInfo.TraitId] = traitInfo;
            }

            foreach (var typeParameterId in traitInfo.TypeParameterIds)
            {
                RegisterMirTypeParameter(typeParameterId);
            }

            foreach (var methodInfo in traitInfo.Methods)
            {
                if (methodInfo.MethodId.IsValid)
                {
                    _traitMethodInfoById[methodInfo.MethodId] = methodInfo;
                }
            }
        }
        foreach (var typeConstructor in module.TypeConstructors)
        {
            if (typeConstructor.SymbolId.IsValid)
            {
                _typeConstructorInfoBySymbol[typeConstructor.SymbolId] = typeConstructor;
            }

            if (typeConstructor.TypeId.IsValid)
            {
                _typeConstructorInfoByTypeId[typeConstructor.TypeId.Value] = typeConstructor;
            }
        }
        _specializationsByTemplateAndSignature.Clear();
        _rejectedSpecializationsByTemplateAndSignature.Clear();
        _specializedTemplateKeys.Clear();
        _traitDispatchLookupCache.Clear();
        _flattenedFunctionTypesByTypeId.Clear();
        _containsOpenTypeVariableByTypeId.Clear();
        _dirtyLocalTypeIds.Clear();
        _usedFunctionNames.Clear();
        _nextSpecializationNameOrdinal = 1;

        foreach (var function in module.Functions)
        {
            if (!string.IsNullOrWhiteSpace(function.Name))
            {
                _usedFunctionNames.Add(function.Name);
            }
        }

        foreach (var (typeIdValue, typeKey) in module.DynamicTypeKeys)
        {
            var typeId = new TypeId(typeIdValue);
            _dynamicTypes.KeyByIdDict[typeIdValue] = typeKey;
            _dynamicTypes.IdByKeyDict[typeKey] = typeId;
            if (TypeKeyParsing.TryParseTypeDescriptor(typeKey, out var descriptor))
            {
                _dynamicTypes.DescriptorByIdDict.TryAdd(typeIdValue, descriptor);
                _dynamicTypes.IdByDescriptorDict[descriptor] = typeId;
            }
        }

        foreach (var (typeIdValue, descriptor) in module.TypeDescriptors)
        {
            var typeId = new TypeId(typeIdValue);
            _dynamicTypes.DescriptorByIdDict[typeIdValue] = descriptor;
            _dynamicTypes.IdByDescriptorDict[descriptor] = typeId;
        }

        foreach (var aliasInfo in _moduleTypeAliases)
        {
            foreach (var typeParameterId in aliasInfo.TypeParameterIds)
            {
                RegisterMirTypeParameter(typeParameterId);
            }
        }

        foreach (var typeConstructor in module.TypeConstructors)
        {
            foreach (var typeParameterId in typeConstructor.TypeParameterIds)
            {
                RegisterMirTypeParameter(typeParameterId);
            }
        }

        _dynamicTypes.ResetNextId();

        foreach (var function in module.Functions)
        {
            foreach (var typeParameterId in function.GenericTypeParameterIds)
            {
                if (typeParameterId.IsValid)
                {
                    _genericTypeParameterTypeIds.Add(typeParameterId.Value);
                }
            }

            if (function.SymbolId.IsValid)
            {
                _functionBySymbol[function.SymbolId] = function;
                if (!string.IsNullOrWhiteSpace(function.Name))
                {
                    _loweredFunctionNameBySymbol[function.SymbolId] = function.Name;
                }
            }

            if (!TryResolveFunctionSignatureTypeId(function, out var functionTypeId))
            {
                continue;
            }

            if (function.SymbolId.IsValid)
            {
                _functionTypeIdBySymbol[function.SymbolId] = functionTypeId;
            }

            if (!string.IsNullOrWhiteSpace(function.Name))
            {
                _functionTypeIdByName[function.Name] = functionTypeId;
            }
        }

        IndexGenericTemplateCandidates(module);

        _nextSyntheticSymbolId = ComputeNextSyntheticSymbolId(module);
    }

    private void IndexGenericTemplateCandidates(MirModule module)
    {
        foreach (var function in module.Functions)
        {
            if (!IsGenericTemplateCandidate(function) ||
                IsGeneratedSpecialization(function.Name))
            {
                continue;
            }

            _genericTemplateFunctionIdentityKeys.Add(MirFunctionIdentity.GetStableKey(function));
            if (TryGetTemplateFunctionIdentityFallbackKey(function.FunctionId, out var fallbackIdentityKey))
            {
                _genericTemplateFunctionIdentityKeys.Add(fallbackIdentityKey);
            }

            if (string.IsNullOrWhiteSpace(function.Name))
            {
                continue;
            }

            VisitTemplateAlternateNames(function.Name, name => _genericTemplateFunctionNames.Add(name));
        }
    }

    private void RegisterMirTypeParameter(SymbolId typeParameterId)
    {
        if (!typeParameterId.IsValid)
        {
            return;
        }

        _genericTypeParameterTypeIds.Add(typeParameterId.Value);
        var typeParameterTypeId = new TypeId(typeParameterId.Value);
        var descriptor = new TypeDescriptor.TypeVar(typeParameterId.Value);
        if (_dynamicTypes.DescriptorByIdDict.TryAdd(typeParameterTypeId.Value, descriptor))
        {
            _dynamicTypes.IdByDescriptorDict.TryAdd(descriptor, typeParameterTypeId);
        }
    }

    private int ComputeNextSyntheticSymbolId(MirModule module)
    {
        var maxSymbol = module.Functions
            .Select(function => function.SymbolId)
            .Where(symbolId => symbolId.IsValid)
            .Select(symbolId => symbolId.Value)
            .DefaultIfEmpty(-1)
            .Max();

        foreach (var function in module.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                foreach (var instruction in block.Instructions.OfType<MirCall>())
                {
                    if (instruction.Function is MirFunctionRef { SymbolId: { IsValid: true } symbolId } &&
                        symbolId.Value > maxSymbol)
                    {
                        maxSymbol = symbolId.Value;
                    }
                }
            }
        }

        foreach (var impl in module.TraitImpls)
        {
            maxSymbol = MaxSymbol(maxSymbol, impl.Id, impl.Trait, impl.ImplementingTypeKey.SymbolId);
            foreach (var methodId in impl.Methods)
            {
                maxSymbol = MaxSymbol(maxSymbol, methodId);
            }

            foreach (var (traitMethodId, implMethodId) in impl.TraitMethodImplementations)
            {
                maxSymbol = MaxSymbol(maxSymbol, traitMethodId, implMethodId);
            }
        }

        foreach (var traitInfo in module.TraitInfos)
        {
            maxSymbol = MaxSymbol(maxSymbol, traitInfo.TraitId);
            foreach (var typeParameterId in traitInfo.TypeParameterIds)
            {
                maxSymbol = MaxSymbol(maxSymbol, typeParameterId);
            }

            foreach (var methodInfo in traitInfo.Methods)
            {
                maxSymbol = MaxSymbol(maxSymbol, methodInfo.TraitId, methodInfo.MethodId);
            }
        }

        foreach (var aliasInfo in module.TypeAliases)
        {
            maxSymbol = MaxSymbol(maxSymbol, aliasInfo.AliasId);
            foreach (var typeParameterId in aliasInfo.TypeParameterIds)
            {
                maxSymbol = MaxSymbol(maxSymbol, typeParameterId);
            }
        }

        foreach (var typeConstructor in module.TypeConstructors)
        {
            maxSymbol = MaxSymbol(maxSymbol, typeConstructor.SymbolId);
            foreach (var typeParameterId in typeConstructor.TypeParameterIds)
            {
                maxSymbol = MaxSymbol(maxSymbol, typeParameterId);
            }
        }

        return maxSymbol + 1;
    }

    private static int MaxSymbol(int currentMax, params SymbolId[] symbolIds)
    {
        foreach (var symbolId in symbolIds)
        {
            if (symbolId.IsValid && symbolId.Value > currentMax)
            {
                currentMax = symbolId.Value;
            }
        }

        return currentMax;
    }

    private void IndexGenericTemplates(MirModule sourceModule, IReadOnlyList<MirFunc> workingFunctions)
    {
        var genericNoSymbolNameCounts = sourceModule.Functions
            .Where(function =>
                !IsGeneratedSpecialization(function.Name) &&
                !function.SymbolId.IsValid &&
                !string.IsNullOrWhiteSpace(function.Name))
            .GroupBy(function => function.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var indexedFunctionIndices = new HashSet<int>();
        for (var i = 0; i < sourceModule.Functions.Count; i++)
        {
            var sourceFunction = sourceModule.Functions[i];
            if ((IsExecutableEntryFunction(sourceFunction) && !IsGenericSignature(sourceFunction)) ||
                !IsGenericTemplateCandidate(sourceFunction) ||
                IsGeneratedSpecialization(sourceFunction.Name))
            {
                continue;
            }

            TryIndexGenericTemplate(i);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < sourceModule.Functions.Count; i++)
            {
                var sourceFunction = sourceModule.Functions[i];
                if (indexedFunctionIndices.Contains(i) ||
                    IsGeneratedSpecialization(sourceFunction.Name) ||
                    !IsGeneratedTemplateClosureFunction(sourceFunction) ||
                    !ReferencesKnownTemplate(sourceFunction))
                {
                    continue;
                }

                changed |= TryIndexGenericTemplate(i);
            }
        }

        bool TryIndexGenericTemplate(int functionIndex)
        {
            if (indexedFunctionIndices.Contains(functionIndex))
            {
                return false;
            }

            var sourceFunction = sourceModule.Functions[functionIndex];
            var templateKey = GetTemplateKey(sourceFunction, genericNoSymbolNameCounts);
            if (templateKey == null)
            {
                return false;
            }

            var templateSource = sourceFunction;
            var workingFunction = workingFunctions[functionIndex];
            var template = new TemplateInfo(templateKey, templateSource, workingFunction);
            _templateRegistry.ByKeyDict[templateKey] = template;
            _templateKeyByFunctionIndex[functionIndex] = templateKey;
            RegisterTemplateAlternateNames(sourceFunction.Name, templateKey);
            RegisterTemplateFunctionIdentity(sourceFunction.FunctionId, templateKey);

            if (sourceFunction.SymbolId.IsValid)
            {
                _templateRegistry.KeyBySymbolDict[sourceFunction.SymbolId] = templateKey;
            }

            if (!sourceFunction.SymbolId.IsValid || IsSyntheticLambdaFunction(sourceFunction))
            {
                _templateRegistry.KeyByUniqueNameDict[sourceFunction.Name] = templateKey;
            }

            indexedFunctionIndices.Add(functionIndex);
            return true;
        }
    }

    private bool ReferencesKnownTemplate(MirFunc function)
    {
        return AnyFunctionRefReferencesKnownTemplate(function);
    }

    private bool IsGeneratedTemplateClosureFunction(MirFunc function)
    {
        return IsSyntheticLambdaFunction(function);
    }

    private static bool IsSyntheticLambdaFunction(MirFunc function)
    {
        return !string.IsNullOrWhiteSpace(function.Name) &&
               function.Name.StartsWith(WellKnownStrings.InternalNames.LambdaPrefix, StringComparison.Ordinal);
    }

    private static string? GetTemplateKey(MirFunc function, IReadOnlyDictionary<string, int> genericNoSymbolNameCounts)
    {
        if (MirFunctionIdentity.TryGetStableKey(function.FunctionId, out var functionIdKey))
        {
            return functionIdKey;
        }

        if (function.SymbolId.IsValid)
        {
            return MirFunctionIdentity.GetStableKey(function.Name, function.SymbolId);
        }

        if (string.IsNullOrWhiteSpace(function.Name))
        {
            return null;
        }

        if (!genericNoSymbolNameCounts.TryGetValue(function.Name, out var count) || count != 1)
        {
            return null;
        }

        return $"name:{function.Name}";
    }

    private void RegisterTemplateAlternateNames(string functionName, string templateKey)
    {
        VisitTemplateAlternateNames(functionName, alternateName => RegisterTemplateAlternateName(alternateName, templateKey));
    }

    private string ResolveLoweredFunctionName(SymbolId symbolId, string fallbackName)
    {
        return symbolId.IsValid &&
               _loweredFunctionNameBySymbol.TryGetValue(symbolId, out var loweredFunctionName) &&
               !string.IsNullOrWhiteSpace(loweredFunctionName)
            ? loweredFunctionName
            : fallbackName;
    }

    private SymbolKind ResolveFunctionSymbolKind(SymbolId symbolId, SymbolKind fallbackKind = SymbolKind.Function)
    {
        if (symbolId.IsValid && _functionBySymbol.ContainsKey(symbolId))
        {
            return SymbolKind.Function;
        }

        return fallbackKind;
    }

    private MirFunctionRef RewriteFunctionReference(
        MirFunctionRef functionRef,
        SymbolId targetSymbolId,
        string targetFunctionName,
        TypeId targetTypeId,
        TypeId? targetSignatureTypeId = null)
    {
        var targetSymbolKind = ResolveFunctionSymbolKind(targetSymbolId, functionRef.SymbolKind);
        var existingFunctionId = functionRef.FunctionId ?? new FunctionId();
        var targetFunctionId = existingFunctionId with
        {
            SymbolId = targetSymbolId,
            Kind = targetSymbolKind,
            Name = targetFunctionName,
            Module = targetSymbolId == functionRef.SymbolId ? existingFunctionId.Module : string.Empty,
            StableIdentityKey = targetSymbolId == functionRef.SymbolId
                ? existingFunctionId.StableIdentityKey
                : string.Empty,
            QualifiedName = targetSymbolId == functionRef.SymbolId ? existingFunctionId.QualifiedName : string.Empty,
            MangledName = targetSymbolId == functionRef.SymbolId ? existingFunctionId.MangledName : string.Empty
        };

        _stats.FunctionRefRewrites++;
        return functionRef with
        {
            SymbolId = targetSymbolId,
            Name = targetFunctionName,
            SymbolKind = targetSymbolKind,
            FunctionId = targetFunctionId,
            TypeId = targetTypeId,
            SignatureTypeId = targetSignatureTypeId is { IsValid: true } signatureTypeId
                ? signatureTypeId
                : functionRef.SignatureTypeId,
            TraitOwnerId = SymbolId.None,
            TraitSelfPosition = SelfPosition.Unknown,
            TraitSelfParameterIndices = [],
            TraitSelfInResult = false,
            TraitMethodRole = TraitMethodRole.None
        };
    }

    private MirFunctionRef RewriteFunctionReference(
        MirFunctionRef functionRef,
        MirFunc targetFunction,
        TypeId targetTypeId)
    {
        var signatureTypeId = TryResolveFunctionSignatureTypeId(targetFunction, out var resolvedSignatureTypeId)
            ? resolvedSignatureTypeId
            : TypeId.None;
        var rewritten = RewriteFunctionReference(
            functionRef,
            targetFunction.SymbolId,
            targetFunction.Name,
            targetTypeId,
            signatureTypeId);
        if (targetFunction.GenericParameterCount == 0 &&
            rewritten.TypeArgumentIds.Any(CanParticipateAsOpenInferenceType))
        {
            rewritten = rewritten with { TypeArgumentIds = [] };
        }

        return targetFunction.FunctionId.IsValid
            ? rewritten with { FunctionId = targetFunction.FunctionId }
            : rewritten;
    }

    private sealed record ConstructorArgSlot(int? PlaceholderIndex, TypeId? FixedTypeId)
    {
        public bool IsPlaceholder => PlaceholderIndex.HasValue;
    }

    private sealed record ConstructorBinding(TypeConstructorKey Constructor, List<ConstructorArgSlot> Slots);
    private sealed record ConstructorBindingMatch(List<int> PlaceholderPositions, int Score);

    private sealed record SpecializationBindings(
        Dictionary<int, TypeId> TypeBindings,
        Dictionary<int, ConstructorBinding> ConstructorBindings)
    {
        public int Count => TypeBindings.Count + ConstructorBindings.Count;
    }

    private void RegisterTemplateAlternateName(string alternateName, string templateKey)
    {
        if (string.IsNullOrWhiteSpace(alternateName) ||
            _templateRegistry.AmbiguousAlternateNamesSet.Contains(alternateName))
        {
            return;
        }

        if (_templateRegistry.KeyByAlternateNameDict.TryGetValue(alternateName, out var existingTemplateKey) &&
            !string.Equals(existingTemplateKey, templateKey, StringComparison.Ordinal))
        {
            _templateRegistry.KeyByAlternateNameDict.Remove(alternateName);
            _templateRegistry.AmbiguousAlternateNamesSet.Add(alternateName);
            return;
        }

        _templateRegistry.KeyByAlternateNameDict[alternateName] = templateKey;
    }

    private void RegisterTemplateFunctionIdentity(FunctionId? functionId, string templateKey)
    {
        if (TryGetTemplateFunctionIdentityKey(functionId, out var identityKey))
        {
            RegisterTemplateFunctionIdentityKey(identityKey, templateKey);
        }

        if (TryGetTemplateFunctionIdentityFallbackKey(functionId, out var fallbackIdentityKey) &&
            !string.Equals(fallbackIdentityKey, identityKey, StringComparison.Ordinal))
        {
            RegisterTemplateFunctionIdentityKey(fallbackIdentityKey, templateKey);
        }
    }

    private bool TryGetTemplateFunctionIdentityKey(FunctionId? functionId, out string identityKey)
    {
        identityKey = string.Empty;
        if (functionId == null)
        {
            return false;
        }

        if (_functionIdentityKeyCache.TryGetValue(functionId, out var cachedIdentityKey))
        {
            identityKey = cachedIdentityKey;
            return true;
        }

        if (!MirFunctionIdentity.TryGetStableKey(functionId, out identityKey))
        {
            return false;
        }

        _functionIdentityKeyCache[functionId] = identityKey;
        return true;
    }

    private bool TryGetTemplateFunctionIdentityFallbackKey(FunctionId? functionId, out string identityKey)
    {
        identityKey = string.Empty;
        if (functionId == null)
        {
            return false;
        }

        if (_functionIdentityFallbackKeyCache.TryGetValue(functionId, out var cachedIdentityKey))
        {
            identityKey = cachedIdentityKey;
            return true;
        }

        if (!MirFunctionIdentity.TryGetStableKeyIgnoringSymbolId(functionId, out identityKey))
        {
            return false;
        }

        _functionIdentityFallbackKeyCache[functionId] = identityKey;
        return true;
    }

    private void RegisterTemplateFunctionIdentityKey(string identityKey, string templateKey)
    {
        if (_templateRegistry.AmbiguousFunctionIdentityKeysSet.Contains(identityKey))
        {
            return;
        }

        if (_templateRegistry.KeyByFunctionIdentityDict.TryGetValue(identityKey, out var existingTemplateKey) &&
            !string.Equals(existingTemplateKey, templateKey, StringComparison.Ordinal))
        {
            _templateRegistry.KeyByFunctionIdentityDict.Remove(identityKey);
            _templateRegistry.AmbiguousFunctionIdentityKeysSet.Add(identityKey);
            return;
        }

        _templateRegistry.KeyByFunctionIdentityDict[identityKey] = templateKey;
    }

    private static bool TemplateAlternateNameMatches(string functionName, string candidate)
    {
        var matched = false;
        VisitTemplateAlternateNames(
            functionName,
            alternateName =>
            {
                if (string.Equals(alternateName, candidate, StringComparison.Ordinal))
                {
                    matched = true;
                }
            });
        return matched;
    }

    private static void VisitTemplateAlternateNames(string functionName, Action<string> visitor)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return;
        }

        visitor(functionName);

        var firstSeparator = functionName.IndexOf("__", StringComparison.Ordinal);
        if (firstSeparator < 0)
        {
            return;
        }

        var startIndices = new List<int>(4) { 0 };
        var cursor = firstSeparator;
        while (cursor >= 0 && cursor + 2 < functionName.Length)
        {
            var nextStart = cursor + 2;
            if (functionName[nextStart] != '_')
            {
                startIndices.Add(nextStart);
            }

            cursor = functionName.IndexOf("__", nextStart, StringComparison.Ordinal);
        }

        if (startIndices.Count < 2)
        {
            return;
        }

        for (var i = 0; i < startIndices.Count; i++)
        {
            visitor(BuildTemplateAlternateName(functionName, startIndices[i]));
        }
    }

    private static string BuildTemplateAlternateName(string functionName, int startIndex)
    {
        var builder = new StringBuilder(functionName.Length - startIndex);
        for (var i = startIndex; i < functionName.Length; i++)
        {
            if (i + 1 < functionName.Length &&
                functionName[i] == '_' &&
                functionName[i + 1] == '_')
            {
                builder.Append(WellKnownStrings.Separators.Path);
                i++;
                continue;
            }

            builder.Append(functionName[i]);
        }

        return builder.ToString();
    }

    private bool ShouldDropOriginalTemplate(MirFunc function, IReadOnlySet<string> liveTemplateKeys)
    {
        if (IsExecutableEntryFunction(function))
        {
            return false;
        }

        foreach (var (_, template) in _templateRegistry.ByKeyDict)
        {
            if (!ReferenceEquals(template.OriginalWorkingFunction, function))
            {
                continue;
            }

            return _specializedTemplateKeys.Contains(template.Key) ||
                   !liveTemplateKeys.Contains(template.Key);
        }

        return false;
    }

    private static bool IsExecutableEntryFunction(MirFunc function)
    {
        return function.IsEntry ||
               string.Equals(function.Name, WellKnownStrings.SpecialNames.Main, StringComparison.Ordinal);
    }

    private static List<MirFunc> PruneUnreferencedGeneratedSpecializations(IReadOnlyList<MirFunc> functions)
    {
        var generatedByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var generatedBySymbol = new Dictionary<SymbolId, int>();
        for (var index = 0; index < functions.Count; index++)
        {
            var function = functions[index];
            if (!IsGeneratedSpecialization(function.Name))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(function.Name))
            {
                generatedByName[function.Name] = index;
            }

            if (function.SymbolId.IsValid)
            {
                generatedBySymbol[function.SymbolId] = index;
            }
        }

        if (generatedByName.Count == 0 && generatedBySymbol.Count == 0)
        {
            return functions.ToList();
        }

        var reachableGenerated = new HashSet<int>();
        var pending = new Queue<int>();

        void MarkGenerated(MirFunctionRef functionRef)
        {
            int generatedIndex;
            if (functionRef.SymbolId.IsValid &&
                generatedBySymbol.TryGetValue(functionRef.SymbolId, out var symbolIndex))
            {
                generatedIndex = symbolIndex;
            }
            else if (!string.IsNullOrWhiteSpace(functionRef.Name) &&
                     generatedByName.TryGetValue(functionRef.Name, out var nameIndex))
            {
                generatedIndex = nameIndex;
            }
            else
            {
                return;
            }

            if (reachableGenerated.Add(generatedIndex))
            {
                pending.Enqueue(generatedIndex);
            }
        }

        for (var index = 0; index < functions.Count; index++)
        {
            if (IsGeneratedSpecialization(functions[index].Name))
            {
                continue;
            }

            VisitFunctionRefs(functions[index], MarkGenerated);
        }

        while (pending.Count > 0)
        {
            var generatedIndex = pending.Dequeue();
            VisitFunctionRefs(functions[generatedIndex], MarkGenerated);
        }

        var filtered = new List<MirFunc>(functions.Count);
        for (var index = 0; index < functions.Count; index++)
        {
            if (!IsGeneratedSpecialization(functions[index].Name) || reachableGenerated.Contains(index))
            {
                filtered.Add(functions[index]);
            }
        }

        return filtered;
    }

    private HashSet<string> CollectLiveTemplateKeys(IReadOnlyList<MirFunc> functions)
    {
        var liveTemplateKeys = new HashSet<string>(StringComparer.Ordinal);
        var referencedTemplateKeysByTemplate = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var function in functions)
        {
            if (IsGenericSignature(function))
            {
                if (!TryResolveTemplateKey(function, out var genericOwnerTemplateKey))
                {
                    continue;
                }

                HashSet<string>? genericOutgoingTemplateKeys = null;
                VisitFunctionRefs(
                    function,
                    functionRef =>
                    {
                        if (!TryResolveTemplateKey(functionRef, out var templateKey))
                        {
                            return;
                        }

                        genericOutgoingTemplateKeys ??= GetOrAddTemplateReferenceSet(
                            referencedTemplateKeysByTemplate,
                            genericOwnerTemplateKey);
                        genericOutgoingTemplateKeys.Add(templateKey);
                    });

                // Generic/template bodies are not LLVM-lowerable on their own. Only
                // monomorphic callers are allowed to keep template SCCs alive.
                continue;
            }

            if (TryResolveTemplateKey(function, out var ownerTemplateKey))
            {
                HashSet<string>? outgoingTemplateKeys = null;
                VisitFunctionRefs(
                    function,
                    functionRef =>
                    {
                        if (!TryResolveTemplateKey(functionRef, out var templateKey))
                        {
                            return;
                        }

                        outgoingTemplateKeys ??= GetOrAddTemplateReferenceSet(
                            referencedTemplateKeysByTemplate,
                            ownerTemplateKey);
                        outgoingTemplateKeys.Add(templateKey);
                    });
                continue;
            }

            VisitFunctionRefs(
                function,
                functionRef =>
                {
                    if (TryResolveTemplateKey(functionRef, out var templateKey))
                    {
                        liveTemplateKeys.Add(templateKey);
                    }
                });
        }

        if (liveTemplateKeys.Count == 0)
        {
            return liveTemplateKeys;
        }

        var pendingTemplateKeys = new Queue<string>(liveTemplateKeys);
        while (pendingTemplateKeys.Count > 0)
        {
            var templateKey = pendingTemplateKeys.Dequeue();
            if (!referencedTemplateKeysByTemplate.TryGetValue(templateKey, out var outgoingTemplateKeys))
            {
                continue;
            }

            foreach (var outgoingTemplateKey in outgoingTemplateKeys)
            {
                if (liveTemplateKeys.Add(outgoingTemplateKey))
                {
                    pendingTemplateKeys.Enqueue(outgoingTemplateKey);
                }
            }
        }

        return liveTemplateKeys;
    }

    private static HashSet<string> GetOrAddTemplateReferenceSet(
        Dictionary<string, HashSet<string>> referencedTemplateKeysByTemplate,
        string templateKey)
    {
        if (!referencedTemplateKeysByTemplate.TryGetValue(templateKey, out var outgoingTemplateKeys))
        {
            outgoingTemplateKeys = new HashSet<string>(StringComparer.Ordinal);
            referencedTemplateKeysByTemplate[templateKey] = outgoingTemplateKeys;
        }

        return outgoingTemplateKeys;
    }


    private void RewriteFunctionCalls(
        MirFunc function,
        FunctionRewriteSummary summary,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        _stats.RewriteVisitedFunctions++;
        if (!summary.NeedsRewrite)
        {
            _stats.DirtyRewriteQueueNoOpDequeues++;
            return;
        }

        if (summary.NeedsFullFunctionScan)
        {
            _stats.DirtyRewriteFullScanFallbacks++;
        }

        if (summary.CanUseCandidateBlockScan &&
            summary.CandidateInstructionSites.Length > 0 &&
            RewriteCandidateInstructionFunctionCalls(function, summary, workingFunctions, queue))
        {
            return;
        }

        if (summary.CanUseCandidateBlockScan &&
            summary.CandidateBlockIndices.Length > 0 &&
            RewriteCandidateBlockFunctionCalls(function, summary, workingFunctions, queue))
        {
            return;
        }

        if (function.BasicBlocks.Count == 1)
        {
            _stats.RewriteSingleBlockFunctions++;
            RewriteSingleBlockFunctionCalls(function, function.BasicBlocks[0], workingFunctions, queue);
            return;
        }

        _stats.RewriteMultiBlockFunctions++;
        var blocksById = BuildBlocksById(function);
        if (blocksById.Count == 0)
        {
            return;
        }

        var entryBlockId = ResolveEntryBlockId(function, blocksById);
        var predecessorsByBlock = BuildPredecessorMap(function);
        var localTypes = BuildLocalTypeMap(function);
        var transferStatePool = new Stack<Dictionary<LocalId, LocalCallBinding>>();

        var rerun = true;
        const int maxRewriteIterations = 8;
        for (var rewriteIteration = 0; rerun && rewriteIteration < maxRewriteIterations; rewriteIteration++)
        {
            _stats.RewriteIterations++;
            rerun = false;
            ConcretizeReusableLocalTypeMap(function, localTypes);
            if (PropagateReturnTypeToReturnedLocalMeasured(function, localTypes))
            {
                ConcretizeFunctionLocalTypes(function, localTypes);
            }

            var outgoingStates = new Dictionary<BlockId, Dictionary<LocalId, LocalCallBinding>>(function.BasicBlocks.Count);
            var pendingBlockIds = new Queue<BlockId>(function.BasicBlocks.Count);
            var queuedBlockIds = new HashSet<BlockId>(function.BasicBlocks.Count);
            var iterationLocalTypesChanged = false;

            pendingBlockIds.Enqueue(entryBlockId);
            queuedBlockIds.Add(entryBlockId);

            while (pendingBlockIds.Count > 0)
            {
                var blockId = pendingBlockIds.Dequeue();
                queuedBlockIds.Remove(blockId);
                if (!blocksById.TryGetValue(blockId, out var block))
                {
                    continue;
                }

                var mergedIncoming = MergeIncomingStateForBlock(
                    blockId,
                    entryBlockId,
                    predecessorsByBlock,
                    outgoingStates);

                var transferState = RentLocalFunctionState(mergedIncoming, transferStatePool);
                try
                {
                    if (RewriteBlockCallsAndTrackBindingsMeasured(function, block, transferState, localTypes, workingFunctions, queue))
                    {
                        iterationLocalTypesChanged = true;
                        rerun = true;
                    }

                    if (!TryGetLocalFunctionState(outgoingStates, blockId, out var previousOutgoing) ||
                        !AreSameLocalFunctionState(previousOutgoing, transferState))
                    {
                        outgoingStates[blockId] = CloneLocalFunctionStateForStorage(transferState);
                        EnqueueSuccessorBlockIds(block, queuedBlockIds, pendingBlockIds);
                    }
                }
                finally
                {
                    ReturnLocalFunctionState(transferState, transferStatePool);
                }
            }

            if (iterationLocalTypesChanged)
            {
                RefreshFunctionMetadataMeasured(function, localTypes);
            }
        }
    }

    private bool RewriteCandidateInstructionFunctionCalls(
        MirFunc function,
        FunctionRewriteSummary summary,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        if (summary.NeedsFullFunctionScan ||
            summary.CandidateInstructionSites.Length == 0)
        {
            return false;
        }

        var localTypes = BuildLocalTypeMap(function);
        ConcretizeReusableLocalTypeMap(function, localTypes);
        if (PropagateReturnTypeToReturnedLocalMeasured(function, localTypes))
        {
            ConcretizeFunctionLocalTypes(function, localTypes);
        }

        var localTypesChanged = false;
        var rewrittenAny = false;
        for (var i = 0; i < summary.CandidateInstructionSites.Length; i++)
        {
            var site = summary.CandidateInstructionSites[i];
            if ((uint)site.BlockIndex >= (uint)function.BasicBlocks.Count)
            {
                continue;
            }

            var block = function.BasicBlocks[site.BlockIndex];
            if ((uint)site.InstructionIndex >= (uint)block.Instructions.Count)
            {
                continue;
            }

            _stats.DirtyRewriteCandidateInstructionsVisited++;
            _stats.RewriteInstructionsScanned++;
            if (!TryRewriteDirectCompleteTemplateCallSite(
                    function,
                    block,
                    site.InstructionIndex,
                    localTypes,
                    workingFunctions,
                    queue,
                    out var instructionRewritten,
                    out var instructionLocalTypesChanged))
            {
                if (rewrittenAny || localTypesChanged)
                {
                    RefreshFunctionMetadataMeasured(function, localTypes);
                }

                return false;
            }

            rewrittenAny |= instructionRewritten;
            localTypesChanged |= instructionLocalTypesChanged;
        }

        if (localTypesChanged)
        {
            RefreshFunctionMetadataMeasured(function, localTypes);
        }

        _stats.DirtyRewriteCandidateInstructionFunctions++;
        return true;
    }

    private bool TryRewriteDirectCompleteTemplateCallSite(
        MirFunc function,
        MirBasicBlock block,
        int instructionIndex,
        Dictionary<LocalId, TypeId> localTypes,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue,
        out bool instructionRewritten,
        out bool localTypesChanged)
    {
        instructionRewritten = false;
        localTypesChanged = false;

        if (block.Instructions[instructionIndex] is not MirCall call)
        {
            return false;
        }

        if (call.Function is not MirFunctionRef sourceFunctionRef)
        {
            return false;
        }

        if (!FunctionRefRequiresSpecialization(sourceFunctionRef, includeKnownTemplateReferences: true))
        {
            return true;
        }

        if (RequiresSpecializationPass(sourceFunctionRef) ||
            !TryResolveTemplateKey(sourceFunctionRef, out var templateKey) ||
            !_templateRegistry.ByKeyDict.TryGetValue(templateKey, out var template) ||
            call.Arguments.Count != GetTemplateParameterCount(template.TemplateSource))
        {
            return false;
        }

        if (call.Target != null &&
            OperandHasReferenceRequiringSpecialization(call.Target, includeKnownTemplateReferences: true))
        {
            return false;
        }

        if (OperandsHaveReferenceRequiringSpecialization(call.Arguments, includeKnownTemplateReferences: true))
        {
            return false;
        }

        if ((!TryResolveSignature(call, template.TemplateSource, localTypes, out var signature) &&
             !TryResolveConcreteCallShapeSignature(call, template.TemplateSource, localTypes, out signature)) ||
            !HasMeaningfulSpecializationSignature(template, signature))
        {
            return false;
        }

        if (!IsMonomorphicSignature(signature) &&
            SignatureContainsOpenConstructorBinding(signature) &&
            TryBindConstructorBindingSignature(signature, out var boundConstructorBindingSignature))
        {
            signature = boundConstructorBindingSignature;
        }

        if (!IsMonomorphicSignature(signature) ||
            CallMayRequireFunctionValueArgumentRewrite(call, signature))
        {
            return false;
        }

        if (!TryGetOrCreateSpecialization(template, signature, workingFunctions, queue, out var specializedFunction))
        {
            return false;
        }

        if (sourceFunctionRef.SymbolId.Equals(specializedFunction.SymbolId) &&
            string.Equals(sourceFunctionRef.Name, specializedFunction.Name, StringComparison.Ordinal) &&
            sourceFunctionRef.TypeId.Equals(signature.ReturnType))
        {
            return true;
        }

        var rewrittenFunctionRef = RewriteFunctionReference(
            sourceFunctionRef,
            specializedFunction,
            specializedFunction.ReturnType);
        var rewrittenCall = call with { Function = rewrittenFunctionRef };
        block.Instructions[instructionIndex] = rewrittenCall;
        _stats.TemplateCallRewrites++;
        instructionRewritten = true;
        localTypesChanged = RefineLocalTypesFromInstruction(function, rewrittenCall, localTypes);
        return true;
    }

    private bool CallMayRequireFunctionValueArgumentRewrite(MirCall call, SpecializationSignature signature)
    {
        for (var i = 0; i < call.Arguments.Count && i < signature.ParameterTypes.Count; i++)
        {
            if (call.Arguments[i] is MirPlace { Kind: PlaceKind.Local } &&
                TryResolveFlattenedFunctionType(signature.ParameterTypes[i], out _, out _))
            {
                return true;
            }
        }

        return false;
    }

    private bool RewriteCandidateBlockFunctionCalls(
        MirFunc function,
        FunctionRewriteSummary summary,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        if (summary.NeedsFullFunctionScan ||
            summary.CandidateBlockIndices.Length == 0 ||
            summary.CandidateBlockIndices.Length >= function.BasicBlocks.Count)
        {
            return false;
        }

        _stats.DirtyRewriteCandidateBlockFunctions++;
        const int maxRewriteIterations = 8;
        var localTypes = BuildLocalTypeMap(function);
        for (var rewriteIteration = 0; rewriteIteration < maxRewriteIterations; rewriteIteration++)
        {
            _stats.RewriteIterations++;
            var localTypesChanged = false;
            ConcretizeReusableLocalTypeMap(function, localTypes);
            if (PropagateReturnTypeToReturnedLocalMeasured(function, localTypes))
            {
                ConcretizeFunctionLocalTypes(function, localTypes);
            }

            for (var i = 0; i < summary.CandidateBlockIndices.Length; i++)
            {
                var blockIndex = summary.CandidateBlockIndices[i];
                if ((uint)blockIndex >= (uint)function.BasicBlocks.Count)
                {
                    continue;
                }

                var localCallBindings = new Dictionary<LocalId, LocalCallBinding>(Math.Min(function.Locals.Count, 16));
                localTypesChanged |= RewriteBlockCallsAndTrackBindingsMeasured(
                    function,
                    function.BasicBlocks[blockIndex],
                    localCallBindings,
                    localTypes,
                    workingFunctions,
                    queue);
            }

            if (!localTypesChanged)
            {
                return true;
            }

            RefreshFunctionMetadataMeasured(function, localTypes);
        }

        return true;
    }

    private void RewriteSingleBlockFunctionCalls(
        MirFunc function,
        MirBasicBlock block,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        const int maxRewriteIterations = 8;
        var localTypes = BuildLocalTypeMap(function);
        for (var rewriteIteration = 0; rewriteIteration < maxRewriteIterations; rewriteIteration++)
        {
            _stats.RewriteIterations++;
            ConcretizeReusableLocalTypeMap(function, localTypes);
            if (PropagateReturnTypeToReturnedLocalMeasured(function, localTypes))
            {
                ConcretizeFunctionLocalTypes(function, localTypes);
            }

            var localCallBindings = new Dictionary<LocalId, LocalCallBinding>(Math.Min(function.Locals.Count, 16));
            if (!RewriteBlockCallsAndTrackBindingsMeasured(function, block, localCallBindings, localTypes, workingFunctions, queue))
            {
                return;
            }

            RefreshFunctionMetadataMeasured(function, localTypes);
        }
    }

    private void ConcretizeReusableLocalTypeMap(MirFunc function, Dictionary<LocalId, TypeId> localTypes)
    {
        SyncLocalTypeMapWithFunctionLocals(function, localTypes);
        ConcretizeFunctionLocalTypes(function, localTypes);
    }

    private static void SyncLocalTypeMapWithFunctionLocals(
        MirFunc function,
        Dictionary<LocalId, TypeId> localTypes)
    {
        foreach (var local in function.Locals)
        {
            localTypes.TryAdd(local.Id, local.TypeId);
        }
    }

    private bool PropagateReturnTypeToReturnedLocalMeasured(MirFunc function, Dictionary<LocalId, TypeId> localTypes)
    {
        var changed = PropagateReturnTypeToReturnedLocal(function, localTypes);
        if (changed)
        {
            _stats.ReturnTypePropagations++;
        }

        return changed;
    }

    private bool RewriteBlockCallsAndTrackBindingsMeasured(
        MirFunc function,
        MirBasicBlock block,
        Dictionary<LocalId, LocalCallBinding> localCallBindings,
        Dictionary<LocalId, TypeId> localTypes,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue)
    {
        _stats.RewriteBlocksScanned++;
        _stats.RewriteInstructionsScanned += block.Instructions.Count;
        return RewriteBlockCallsAndTrackBindings(function, block, localCallBindings, localTypes, workingFunctions, queue);
    }

    private void RefreshFunctionMetadataMeasured(MirFunc function, IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        RefreshFunctionMetadata(function, localTypes);
    }

    private void RefreshFunctionMetadata(MirFunc function, IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        RefreshFunctionLocalTypes(function, localTypes);
        RefreshFunctionOperandTypes(function, localTypes);
        _dirtyLocalTypeIds.Clear();
    }

    private bool PropagateReturnTypeToReturnedLocal(MirFunc function, Dictionary<LocalId, TypeId> localTypes)
    {
        if (!function.ReturnType.IsValid || ContainsOpenTypeVariable(function.ReturnType))
        {
            return false;
        }

        var changed = false;
        foreach (var block in function.BasicBlocks)
        {
            if (block.Terminator is not MirReturn { Value: not null } ret)
            {
                continue;
            }

            if (ret.Value is not MirPlace retPlace || retPlace.Kind != PlaceKind.Local || !retPlace.Local.IsValid)
            {
                continue;
            }

            var hasExisting = localTypes.TryGetValue(retPlace.Local, out var existingType);
            var needsPropagation = !hasExisting || !existingType.IsValid || ContainsOpenTypeVariable(existingType);

            if (needsPropagation)
            {
                localTypes[retPlace.Local] = function.ReturnType;
                _dirtyLocalTypeIds.Add(retPlace.Local);
                changed = true;
            }
        }

        return changed;
    }

    private static Dictionary<BlockId, MirBasicBlock> BuildBlocksById(MirFunc function)
    {
        var blocksById = new Dictionary<BlockId, MirBasicBlock>(function.BasicBlocks.Count);
        foreach (var block in function.BasicBlocks)
        {
            blocksById[block.Id] = block;
        }

        return blocksById;
    }

    private Dictionary<LocalId, TypeId> BuildLocalTypeMap(MirFunc function)
    {
        _stats.LocalTypeMapBuilds++;
        var localTypes = new Dictionary<LocalId, TypeId>(function.Locals.Count);
        foreach (var local in function.Locals)
        {
            localTypes[local.Id] = local.TypeId;
        }

        return localTypes;
    }



    private void RefineCallTargetTypeFromImmediateUse(
        MirBasicBlock block,
        int instructionIndex,
        MirCall call,
        Dictionary<LocalId, TypeId> localTypes)
    {
        if (call.Target is not { Kind: PlaceKind.Local } target ||
            instructionIndex + 1 >= block.Instructions.Count)
        {
            return;
        }

        var nextInstruction = block.Instructions[instructionIndex + 1];
        MirPlace? destination = nextInstruction switch
        {
            MirMove { Source.Kind: PlaceKind.Local } move when move.Source.Local.Equals(target.Local) => move.Target,
            MirCopy { Source.Kind: PlaceKind.Local } copy when copy.Source.Local.Equals(target.Local) => copy.Target,
            _ => null
        };
        if (destination == null)
        {
            return;
        }

        var destinationType = ResolvePlaceType(destination, localTypes);
        if (destinationType.IsValid &&
            !ContainsOpenTypeVariable(destinationType))
        {
            localTypes[target.Local] = destinationType;
        }
    }



    private bool TryGetOrCreateSpecialization(
        TemplateInfo template,
        SpecializationSignature signature,
        List<MirFunc> workingFunctions,
        Queue<RewriteQueueItem> queue,
        out MirFunc specialization)
    {
        specialization = default!;
        var specializationKey = CreateSpecializationCacheKey(template, signature);
        if (_specializationsByTemplateAndSignature.TryGetValue(specializationKey, out var existing))
        {
            _stats.SpecializationCacheHits++;
            specialization = existing;
            return true;
        }

        if (_rejectedSpecializationsByTemplateAndSignature.Contains(specializationKey))
        {
            _stats.SpecializationRejections++;
            return false;
        }

        var typeBindings = CollectTypeBindings(template, signature);
        specialization = CreateSpecializedFunction(template.TemplateSource, signature, typeBindings);
        if (ContainsOpenLocalTypes(specialization))
        {
            var reason = ContainsOpenConstructorBinding(specialization)
                ? SpecializationFailureReason.UnresolvedConstructorBinding
                : SpecializationFailureReason.UnresolvedTypes;
            RecordRejectedSpecialization(template, signature, reason);
            _stats.SpecializationRejections++;
            specialization = default!;
            return false;
        }

        _specializationsByTemplateAndSignature[specializationKey] = specialization;
        _specializedTemplateKeys.Add(template.Key);

        if (TryResolveFunctionSignatureTypeId(specialization, out var specializationFunctionTypeId))
        {
            if (specialization.SymbolId.IsValid)
            {
                _functionTypeIdBySymbol[specialization.SymbolId] = specializationFunctionTypeId;
            }

            if (!string.IsNullOrWhiteSpace(specialization.Name))
            {
                _functionTypeIdByName[specialization.Name] = specializationFunctionTypeId;
            }
        }

        var specializationIndex = workingFunctions.Count;
        workingFunctions.Add(specialization);
        _clonedWorkingFunctionIndices.Add(specializationIndex);
        var specializationSummary = BuildFunctionRewriteSummary(
            specialization,
            includeKnownTemplateReferences: true);
        if (specializationSummary.NeedsRewrite)
        {
            queue.Enqueue(new RewriteQueueItem(specializationIndex, specializationSummary));
            _stats.EnqueuedSpecializations++;
            _stats.DirtyRewriteQueueEntries++;
        }
        else if (AnyFunctionRef(specialization, FunctionRefRequiresLateTraitDispatch))
        {
            queue.Enqueue(new RewriteQueueItem(
                specializationIndex,
                new FunctionRewriteSummary(
                    NeedsRewrite: true,
                    NeedsFullFunctionScan: true,
                    CandidateBlockCount: 0,
                    CandidateInstructionCount: 0,
                    CanUseCandidateBlockScan: false,
                    CandidateBlockIndices: [],
                    CandidateInstructionSites: [])));
            _stats.EnqueuedSpecializations++;
            _stats.DirtyRewriteQueueEntries++;
        }
        else
        {
            _stats.DirtyRewriteQueueSkippedSpecializations++;
        }

        _stats.SpecializationsCreated++;
        _stats.RewriteQueueMaxDepth = Math.Max(_stats.RewriteQueueMaxDepth, queue.Count);
        return true;
    }

    private static bool IsCompleteTemplateApplication(MirFunc template, IReadOnlyList<MirOperand> combinedArguments)
    {
        return combinedArguments.Count == GetTemplateParameterCount(template);
    }

    private static bool IsPartialTemplateApplication(MirFunc template, IReadOnlyList<MirOperand> combinedArguments)
    {
        return combinedArguments.Count < GetTemplateParameterCount(template);
    }

    private IReadOnlyList<MirLocal> GetCachedTemplateParameters(MirFunc template)
    {
        if (_templateParametersByFunction.TryGetValue(template, out var parameters))
        {
            return parameters;
        }

        parameters = template.Locals.Where(static local => local.IsParameter).ToList();
        _templateParametersByFunction[template] = parameters;
        return parameters;
    }

    private static int GetTemplateParameterCount(MirFunc template)
    {
        return template.Locals.Count(static local => local.IsParameter);
    }

}
