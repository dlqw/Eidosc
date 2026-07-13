using Eidosc.Symbols;
using Eidosc.ErrorRecovery;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Utils;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Types;

/// <summary>
/// 约束求解器 - 验证 Trait 约束是否满足
/// 实现类型推断错误恢复
/// </summary>
public sealed class ConstraintSolver
{
    private readonly SymbolTable _symbolTable;
    private readonly Substitution _substitution;
    private readonly IReadOnlyDictionary<SymbolId, Kind>? _typeConstructorKindsBySymbol;
    private readonly Dictionary<int, Kind> _kindByTypeVar = [];
    private KindInferer? _kindInferer;
    private readonly List<EidoscDiagnostic> _diagnostics = [];
    private readonly Dictionary<TraitCheckCacheKey, TraitCheckCacheEntry> _traitCheckCache = [];
    private readonly Dictionary<TraitCheckCacheKey, TraitCheckCacheEntry> _previousTraitCheckCache = [];
    private long _traitCheckCacheHits;
    private long _traitCheckCacheMisses;
    private long _traitCheckCacheSkipped;
    private long _traitCheckPreviousCacheHits;
    private long _traitCheckPreviousCacheMisses;
    private long _traitCheckPreviousCacheRestoreHits;
    private long _traitCheckPreviousCacheValidatedHits;
    private long _traitCheckPreviousCacheStaleHits;

    private readonly record struct TraitCheckCacheKey(
        string TypeKey,
        string TraitKey,
        string TraitName,
        string TraitArgs,
        string TraitArgKeys);

    private readonly record struct TraitCheckCacheEntry(
        bool Success,
        string? ErrorMessage,
        string CandidateSetFingerprint);

    private readonly record struct TraitConstraintLookupRequest(
        TypeId TypeId,
        ImplTypeRefKey ImplementingTypeKey,
        IReadOnlyList<ImplTypeRefKey> TraitArgKeys);

    /// <summary>
    /// 错误恢复上下文
    /// </summary>
    private readonly ErrorRecoveryContext _recoveryContext = ErrorRecoveryContext.ForTypeInference();

    /// <summary>
    /// 已报告错误的约束（用于抑制级联错误）
    /// </summary>
    private readonly HashSet<string> _reportedConstraints = [];

    /// <summary>
    /// 诊断信息
    /// </summary>
    public List<EidoscDiagnostic> Diagnostics => _diagnostics;

    public bool AnalysisIncomplete { get; private set; }

    public string? IncompleteReason { get; private set; }

    public int SuppressedConstraintCount { get; private set; }

    public IReadOnlyDictionary<string, long> GetProfilingCounters()
    {
        return new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["Types.traitCheckCache.entries"] = _traitCheckCache.Count,
            ["Types.traitCheckCache.hits"] = _traitCheckCacheHits,
            ["Types.traitCheckCache.misses"] = _traitCheckCacheMisses,
            ["Types.traitCheckCache.skipped"] = _traitCheckCacheSkipped,
            ["Types.traitCheckPreviousCache.entries"] = _previousTraitCheckCache.Count,
            ["Types.traitCheckPreviousCache.hits"] = _traitCheckPreviousCacheHits,
            ["Types.traitCheckPreviousCache.misses"] = _traitCheckPreviousCacheMisses,
            ["Types.traitCheckPreviousCache.restoreHits"] = _traitCheckPreviousCacheRestoreHits,
            ["Types.traitCheckPreviousCache.validatedHits"] = _traitCheckPreviousCacheValidatedHits,
            ["Types.traitCheckPreviousCache.staleHits"] = _traitCheckPreviousCacheStaleHits
        };
    }

    public ConstraintSolver(
        SymbolTable symbolTable,
        Substitution substitution,
        IReadOnlyDictionary<SymbolId, Kind>? typeConstructorKindsBySymbol = null)
    {
        _symbolTable = symbolTable;
        _substitution = substitution;
        _typeConstructorKindsBySymbol = typeConstructorKindsBySymbol;

        _substitution.TraitConstraintChecker ??= (type, constraint) =>
        {
            var resolvedTraitId = ResolveTraitId(constraint.Trait, constraint.TraitName);
            var normalizedArgs = NormalizeTraitConstraintArgs(constraint.TraitArgs);
            var traitArgKeys = GetTraitConstraintArgKeys(constraint);
            if (CheckTraitInternal(
                    type,
                    resolvedTraitId,
                    constraint.TraitName,
                    normalizedArgs,
                    traitArgKeys,
                    out var errorMessage,
                    constraint))
            {
                return null;
            }
            return errorMessage ?? DiagnosticMessages.TypeDoesNotImplementTrait(type, constraint.TraitName);
        };
        _substitution.ErrorReporter ??= AddError;
    }

    /// <summary>
    /// 清空诊断信息
    /// </summary>
    public void Clear()
    {
        _diagnostics.Clear();
        _reportedConstraints.Clear();
        _kindByTypeVar.Clear();
        _kindInferer = null;
        _traitCheckCache.Clear();
        _traitCheckCacheHits = 0;
        _traitCheckCacheMisses = 0;
        _traitCheckCacheSkipped = 0;
        _traitCheckPreviousCacheHits = 0;
        _traitCheckPreviousCacheMisses = 0;
        _traitCheckPreviousCacheRestoreHits = 0;
        _traitCheckPreviousCacheValidatedHits = 0;
        _traitCheckPreviousCacheStaleHits = 0;
        _recoveryContext.Reset();
        AnalysisIncomplete = false;
        IncompleteReason = null;
        SuppressedConstraintCount = 0;
    }

    public void LoadPreviousTraitCheckSnapshot(TraitCheckSnapshot? snapshot)
    {
        _previousTraitCheckCache.Clear();
        if (snapshot?.Entries == null ||
            !string.Equals(snapshot.SchemaVersion, TraitCheckSnapshot.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var entry in snapshot.Entries)
        {
            _previousTraitCheckCache[new TraitCheckCacheKey(
                entry.TypeKey,
                entry.TraitKey,
                entry.TraitName,
                entry.TraitArgs,
                entry.TraitArgKeys)] = new TraitCheckCacheEntry(
                    entry.Success,
                    entry.ErrorMessage,
                    entry.CandidateSetFingerprint);
        }
    }

    public TraitCheckSnapshot CreateTraitCheckSnapshot() =>
        new(
            TraitCheckSnapshot.CurrentSchemaVersion,
            _traitCheckCache
                .OrderBy(static pair => pair.Key.TypeKey, StringComparer.Ordinal)
                .ThenBy(static pair => pair.Key.TraitKey, StringComparer.Ordinal)
                .ThenBy(static pair => pair.Key.TraitArgs, StringComparer.Ordinal)
                .ThenBy(static pair => pair.Key.TraitArgKeys, StringComparer.Ordinal)
                .Select(static pair => new TraitCheckSnapshotEntry(
                    pair.Key.TypeKey,
                    pair.Key.TraitKey,
                    pair.Key.TraitName,
                    pair.Key.TraitArgs,
                    pair.Key.TraitArgKeys,
                    pair.Value.Success,
                    pair.Value.ErrorMessage,
                    pair.Value.CandidateSetFingerprint))
                .ToArray());

    /// <summary>
    /// 求解约束集合
    /// 约束不满足时记录错误继续
    /// 使用两阶段求解：先线性处理所有约束，再进行不动点迭代解析延迟约束。
    /// </summary>
    /// <param name="constraints">约束集合</param>
    /// <returns>是否所有约束都满足</returns>
    public bool Solve(ConstraintSet constraints)
    {
        var success = true;

        // Phase 1: 线性处理所有约束
        for (var i = 0; i < constraints.Constraints.Count; i++)
        {
            var constraint = constraints.Constraints[i];

            // 设置最大错误数量限制
            if (_recoveryContext.HasReachedLimit)
            {
                AnalysisIncomplete = true;
                IncompleteReason = DiagnosticMessages.TooManyConstraintErrors(_recoveryContext.MaxErrors);
                SuppressedConstraintCount += constraints.Constraints.Count - i;
                AddError(constraint.Span, IncompleteReason);
                break;
            }

            if (!SolveConstraint(constraint))
            {
                success = false;
            }
        }

        // Phase 2: 不动点迭代 — 解析因后续 unification 而可检查的延迟约束
        if (!ResolveDeferredConstraintsFixpoint())
        {
            success = false;
        }

        return success;
    }

    /// <summary>
    /// 反复尝试解析延迟的 Trait 约束，直到没有新的约束可以被解析。
    /// 这处理了主循环中 EqualityConstraint 绑定类型变量后、
    /// 先前延迟的 TraitConstraint 现在可以检查的情况。
    /// </summary>
    private bool ResolveDeferredConstraintsFixpoint()
    {
        var diagnosticCountBefore = _diagnostics.Count;

        if (_substitution.DeferredTraitConstraints.Count == 0)
            return true;

        const int maxIterations = 32;
        var prevCount = -1;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var currentCount = _substitution.DeferredTraitConstraints.Count;
            if (currentCount == 0 || currentCount == prevCount)
                break; // 不动点到达或全部解析完毕

            prevCount = currentCount;

            // 快照当前延迟变量索引（ApplyVar 可能修改字典）
            var deferredVars = _substitution.DeferredTraitConstraints.Keys.ToArray();

            foreach (var varIndex in deferredVars)
            {
                // Apply 会触发 ApplyVar 中的延迟约束检查：
                // 若变量解析为具体类型，延迟约束将被移除并检查
                _substitution.Apply(new TyVar { Index = varIndex });
            }
        }

        return _diagnostics.Count == diagnosticCountBefore;
    }

    /// <summary>
    /// 求解单个约束
    /// </summary>
    private bool SolveConstraint(TypeConstraint constraint)
    {
        return constraint switch
        {
            TraitConstraint trait => SolveTraitConstraint(trait),
            EqualityConstraint eq => SolveEqualityConstraint(eq),
            KindConstraint kind => SolveKindConstraint(kind),
            _ => true
        };
    }

    /// <summary>
    /// 求解 Trait 约束
    /// </summary>
    private bool SolveTraitConstraint(TraitConstraint constraint)
    {
        var type = _substitution.Apply(constraint.Type);

        // 使用约束中保存的 TraitName（比从 SymbolId 查找更可靠）
        var traitName = constraint.TraitName;
        if (string.IsNullOrEmpty(traitName))
        {
            traitName = GetTraitName(constraint.Trait);
        }

        var resolvedTraitId = ResolveTraitId(constraint.Trait, traitName);

        // Effect constraints (e.g. [T: Emitter]) are not checked by the trait
        // constraint solver — they are handled by the ability inferer and
        // authorization checker in later passes.
        if (resolvedTraitId.IsValid && _symbolTable.GetSymbol(resolvedTraitId) is EffectSymbol)
        {
            return true;
        }

        if (!ValidateTraitConstraintArguments(constraint, resolvedTraitId, traitName, out var traitArgsError))
        {
            AddError(constraint.Span, traitArgsError ?? DiagnosticMessages.InvalidTypeArgumentsForTrait(traitName));
            return false;
        }

        var normalizedTraitArgs = NormalizeTraitConstraintArgs(constraint.TraitArgs);
        var traitArgKeys = GetTraitConstraintArgKeys(constraint);

        // 1. 如果是类型变量，延迟求解
        if (type is TyVar tyVar)
        {
            DeferTraitConstraint(tyVar, constraint);
            return true;
        }

        if (CheckTraitCached(
                type,
                resolvedTraitId,
                traitName,
                normalizedTraitArgs,
                traitArgKeys,
                out var errorMessage,
                constraint))
        {
            return true;
        }

        AddError(constraint.Span, errorMessage ?? DiagnosticMessages.TypeDoesNotImplementTrait(type, traitName));
        return false;
    }

    private bool CheckTraitInternal(
        Type type,
        SymbolId traitId,
        string traitName,
        IReadOnlyList<string> traitArgs,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys,
        out string? errorMessage,
        TraitConstraint? deferredConstraint = null)
    {
        errorMessage = null;

        switch (type)
        {
            case TyVar tyVar:
                if (deferredConstraint != null)
                {
                    DeferTraitConstraint(tyVar, deferredConstraint);
                }

                return true;

            case TyCon con:
                return CheckTraitForTyCon(con, traitId, traitName, traitArgs, traitArgKeys, out errorMessage);

            case TyTuple tuple:
                return CheckTraitForTuple(
                    tuple,
                    traitId,
                    traitName,
                    traitArgs,
                    traitArgKeys,
                    out errorMessage,
                    deferredConstraint);

            case TyFun:
                errorMessage = DiagnosticMessages.FunctionTypeDoesNotImplementTrait(traitName);
                return false;

            default:
                errorMessage = DiagnosticMessages.TypeDoesNotImplementTrait(type, traitName);
                return false;
        }
    }

    private bool CheckTraitForTyCon(
        TyCon con,
        SymbolId traitId,
        string traitName,
        IReadOnlyList<string> traitArgs,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys,
        out string? errorMessage)
    {
        errorMessage = null;

        // 内置类型先走内置 trait 映射；未命中时继续走用户 impl 查找。
        if (BuiltinTraits.IsBuiltinType(con) &&
            BuiltinTraits.HasTrait(con, traitName))
        {
            return true;
        }

        // 检查用户定义类型（按 concrete head + TraitId）
        var lookupRequest = CreateTraitConstraintLookupRequest(con, traitArgKeys);
        if (lookupRequest.TypeId.IsValid &&
            traitId.IsValid &&
            _symbolTable.LookupImplForTraitByKeys(
                lookupRequest.TypeId,
                traitId,
                lookupRequest.ImplementingTypeKey,
                lookupRequest.TraitArgKeys) is { } impl)
        {
            if (CheckImplTypeRequirements(con, impl, out errorMessage))
            {
                return true;
            }
        }

        // Supertrait chain fallback: if no direct impl found for the requested trait,
        // check if there is an impl for a child trait that extends this trait.
        // E.g., if checking Eq and no @impl(Eq) exists, but @impl(Ord) does and Ord: Eq, accept it.
        if (lookupRequest.TypeId.IsValid &&
            TryFindImplViaSupertraitChain(
                lookupRequest.TypeId,
                lookupRequest.ImplementingTypeKey,
                traitId,
                out _))
        {
            return true;
        }

        errorMessage ??= DiagnosticMessages.TypeDoesNotImplementTrait(con.Name, traitName);
        return false;
    }

    /// <summary>
    /// Checks whether a type satisfies a trait through a supertrait chain.
    /// For example, if Ord: Eq and the type has @impl(Ord), it also satisfies Eq.
    /// </summary>
    private bool TryFindImplViaSupertraitChain(
        TypeId typeId,
        ImplTypeRefKey implementingTypeKey,
        SymbolId requiredTraitId,
        out ImplSymbol? foundImpl)
    {
        foundImpl = null;

        if (!requiredTraitId.IsValid)
        {
            return false;
        }

        // Collect all traits that have requiredTraitId as an ancestor (O(1) via reverse index)
        var childTraits = _symbolTable.GetChildTraits(requiredTraitId);
        if (childTraits.Count == 0)
        {
            return false;
        }

        // Check if any child trait has an impl for this type
        foreach (var childTraitId in childTraits)
        {
            var candidate = _symbolTable.LookupImplForTraitByKeys(
                typeId, childTraitId, implementingTypeKey, null);
            if (candidate is not null)
            {
                foundImpl = candidate;
                return true;
            }
        }

        return false;
    }

    private bool CheckImplTypeRequirements(
        TyCon implementingType,
        ImplSymbol impl,
        out string? errorMessage)
    {
        errorMessage = null;

        if (impl.ImplementingTypeRequirements.Count == 0)
        {
            return true;
        }

        for (var i = 0; i < impl.ImplementingTypeRequirements.Count; i++)
        {
            var requirement = impl.ImplementingTypeRequirements[i];
            if (requirement.TypeArgIndex < 0 || requirement.TypeArgIndex >= implementingType.Args.Count)
            {
                errorMessage =
                    DiagnosticMessages.TypeMissingTypeArgumentRequiredByTraitImpl(
                        implementingType.Name,
                        requirement.TypeArgIndex + 1,
                        impl.Name);
                return false;
            }

            var actualTypeArg = _substitution.Apply(implementingType.Args[requirement.TypeArgIndex]);
            var resolvedRequirementTraitId = ResolveTraitId(requirement.Trait, requirement.TraitName);
            if (actualTypeArg is TyVar requirementTypeVar)
            {
                DeferTraitConstraint(
                    requirementTypeVar,
                    new TraitConstraint
                    {
                        Type = requirementTypeVar,
                        Trait = resolvedRequirementTraitId,
                        TraitName = requirement.TraitName,
                        TraitArgKeys = BuildImplRequirementTraitArgKeys(requirement).ToList(),
                        Span = impl.Span
                    });
                continue;
            }

            if (CheckTraitCached(
                    actualTypeArg,
                    resolvedRequirementTraitId,
                    requirement.TraitName,
                    requirement.TraitTypeArgs,
                    BuildImplRequirementTraitArgKeys(requirement),
                    out _))
            {
                continue;
            }

            errorMessage =
                DiagnosticMessages.TypeArgumentDoesNotImplementTrait(
                    requirement.TypeArgIndex + 1,
                    implementingType.Name,
                    actualTypeArg,
                    FormatTraitRequirement(requirement));
            return false;
        }

        return true;
    }

    private static IReadOnlyList<ImplTypeRefKey> BuildImplRequirementTraitArgKeys(
        ImplTypeArgTraitRequirement requirement)
    {
        return requirement.TraitTypeArgKeys.Count > 0
            ? requirement.TraitTypeArgKeys
            : requirement.TraitTypeArgs.Select(ImplTypeRefKey.FromCanonicalText).ToList();
    }

    private IReadOnlyList<ImplTypeRefKey> GetTraitConstraintArgKeys(TraitConstraint constraint)
    {
        return constraint.TraitArgKeys.Count > 0
            ? constraint.TraitArgKeys
            : BuildTraitConstraintArgKeys(constraint.TraitArgs);
    }

    private void DeferTraitConstraint(TyVar tyVar, TraitConstraint constraint)
    {
        if (!_substitution.DeferredTraitConstraints.TryGetValue(tyVar.Index, out var list))
        {
            list = [];
            _substitution.DeferredTraitConstraints[tyVar.Index] = list;
        }

        list.Add(constraint with { Type = tyVar });
    }

    private bool CheckTraitForTuple(
        TyTuple tuple,
        SymbolId traitId,
        string traitName,
        IReadOnlyList<string> traitArgs,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys,
        out string? errorMessage,
        TraitConstraint? deferredConstraint = null)
    {
        errorMessage = null;

        // () 退化为 Unit 的 trait 语义
        if (tuple.Elements.Count == 0)
        {
            return CheckTraitForTyCon(BaseTypes.Unit, traitId, traitName, traitArgs, traitArgKeys, out errorMessage);
        }

        // 元组按元素结构化约束：元素都满足 trait 则元组满足
        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            var elementType = _substitution.Apply(tuple.Elements[i]);

            if (elementType is TyVar)
            {
                if (deferredConstraint != null)
                {
                    DeferTraitConstraint((TyVar)elementType, deferredConstraint with { Type = elementType });
                }

                continue;
            }

            if (CheckTraitCached(elementType, traitId, traitName, traitArgs, traitArgKeys, out _, deferredConstraint))
            {
                continue;
            }

            errorMessage = DiagnosticMessages.TupleElementTypeDoesNotImplementTrait(i + 1, elementType, traitName);
            return false;
        }

        return true;
    }

    private List<string> NormalizeTraitConstraintArgs(IReadOnlyList<Type> traitArgs)
    {
        if (traitArgs.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(traitArgs.Count);
        foreach (var traitArg in traitArgs)
        {
            var applied = _substitution.Apply(traitArg);
            normalized.Add(applied.ToString());
        }

        return normalized;
    }

    private bool CheckTraitCached(
        Type type,
        SymbolId traitId,
        string traitName,
        IReadOnlyList<string> traitArgs,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys,
        out string? errorMessage,
        TraitConstraint? deferredConstraint = null)
    {
        if (!TryCreateTraitCheckCacheKey(type, traitId, traitName, traitArgs, traitArgKeys, out var key))
        {
            _traitCheckCacheSkipped++;
            return CheckTraitInternal(type, traitId, traitName, traitArgs, traitArgKeys, out errorMessage, deferredConstraint);
        }

        if (_traitCheckCache.TryGetValue(key, out var cached))
        {
            _traitCheckCacheHits++;
            errorMessage = cached.ErrorMessage;
            return cached.Success;
        }

        if (_previousTraitCheckCache.TryGetValue(key, out var previousCached))
        {
            _traitCheckPreviousCacheHits++;
            var currentFingerprint = CreateTraitCheckCandidateSetFingerprint(type, traitId, traitArgKeys);
            if (string.Equals(previousCached.CandidateSetFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                _traitCheckPreviousCacheRestoreHits++;
                _traitCheckPreviousCacheValidatedHits++;
                _traitCheckCache[key] = previousCached;
                errorMessage = previousCached.ErrorMessage;
                return previousCached.Success;
            }

            _traitCheckPreviousCacheStaleHits++;
        }
        else
        {
            _traitCheckPreviousCacheMisses++;
        }

        _traitCheckCacheMisses++;
        var success = CheckTraitInternal(type, traitId, traitName, traitArgs, traitArgKeys, out errorMessage, deferredConstraint);
        _traitCheckCache[key] = new TraitCheckCacheEntry(
            success,
            errorMessage,
            CreateTraitCheckCandidateSetFingerprint(type, traitId, traitArgKeys));
        return success;
    }

    private string CreateTraitCheckCandidateSetFingerprint(
        Type type,
        SymbolId traitId,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys)
    {
        var applied = _substitution.Apply(type);
        if (applied is not TyCon con)
        {
            return "";
        }

        var lookupRequest = CreateTraitConstraintLookupRequest(con, traitArgKeys);
        if (!lookupRequest.TypeId.IsValid || !traitId.IsValid)
        {
            return "";
        }

        var candidates = _symbolTable.LookupImplCandidatesForTraitByKeys(
            lookupRequest.TypeId,
            traitId,
            lookupRequest.TraitArgKeys);
        return string.Join(
            ";",
            candidates
                .Select(static candidate => string.Join(
                    "|",
                    candidate.Id.IsValid ? candidate.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                    candidate.Trait.IsValid ? candidate.Trait.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                    candidate.CanonicalImplementingType,
                    string.Join(",", candidate.CanonicalTraitTypeArgs)))
                .OrderBy(static key => key, StringComparer.Ordinal));
    }

    private bool TryCreateTraitCheckCacheKey(
        Type type,
        SymbolId traitId,
        string traitName,
        IReadOnlyList<string> traitArgs,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys,
        out TraitCheckCacheKey key)
    {
        key = default;
        if (ContainsTypeVariable(type) ||
            traitArgs.Any(static arg => arg.Contains("TyVar", StringComparison.Ordinal)) ||
            traitArgKeys.Any(static arg => arg.ToString().Contains("TyVar", StringComparison.Ordinal)))
        {
            return false;
        }

        key = new TraitCheckCacheKey(
            type.ToString(),
            traitId.IsValid ? traitId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : traitName,
            traitName,
            string.Join(",", traitArgs),
            string.Join(",", traitArgKeys.Select(static arg => arg.ToString())));
        return true;
    }

    private static bool ContainsTypeVariable(Type type)
    {
        return type switch
        {
            TyVar => true,
            TyCon con => con.Args.Any(ContainsTypeVariable) ||
                         con.ValueArgs.Any(static argument => !argument.IsConcrete),
            TyTuple tuple => tuple.Elements.Any(ContainsTypeVariable),
            TyFun fun => fun.Params.Any(ContainsTypeVariable) || ContainsTypeVariable(fun.Result),
            _ => false
        };
    }

    private List<ImplTypeRefKey> BuildTraitConstraintArgKeys(IReadOnlyList<Type> traitArgs)
    {
        if (traitArgs.Count == 0)
        {
            return [];
        }

        var keys = new List<ImplTypeRefKey>(traitArgs.Count);
        foreach (var traitArg in traitArgs)
        {
            keys.Add(ImplLookupCanonicalizer.BuildTypeRefKey(_symbolTable, traitArg, type => _substitution.Apply(type)));
        }

        return keys;
    }

    private TraitConstraintLookupRequest CreateTraitConstraintLookupRequest(
        TyCon implementingType,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys)
    {
        return new TraitConstraintLookupRequest(
            ImplLookupCanonicalizer.ResolveLookupTypeId(_symbolTable, implementingType),
            ImplLookupCanonicalizer.BuildTypeRefKey(
                _symbolTable,
                implementingType,
                type => _substitution.Apply(type)),
            traitArgKeys);
    }

    private SymbolId ResolveTraitId(SymbolId traitId, string traitName)
    {
        if (traitId.IsValid)
        {
            return traitId;
        }

        if (!string.IsNullOrWhiteSpace(traitName))
        {
            var lookupByName = _symbolTable.LookupType(traitName);
            if (lookupByName.HasValue && lookupByName.Value.IsValid)
            {
                return lookupByName.Value;
            }
        }

        return traitId;
    }

    private bool ValidateTraitConstraintArguments(
        TraitConstraint constraint,
        SymbolId resolvedTraitId,
        string traitName,
        out string? errorMessage)
    {
        errorMessage = null;

        if (resolvedTraitId.IsValid &&
            _symbolTable.GetSymbol(resolvedTraitId) is EffectSymbol)
        {
            // Allow abilities as type-parameter constraints for ability polymorphism.
            // Skip trait argument validation for ability constraints — they are handled
            // by the ability inferer and authorization checker in later passes.
            return true;
        }

        if (!TryGetExpectedTraitArgumentKinds(resolvedTraitId, traitName, out var expectedTraitArgKinds))
        {
            return true;
        }

        var expectedCount = expectedTraitArgKinds.Count;
        var actualCount = constraint.TraitArgs.Count;
        if (expectedCount != actualCount)
        {
            errorMessage = DiagnosticMessages.TraitExpectsTypeArguments(traitName, expectedCount, actualCount);
            return false;
        }

        for (var i = 0; i < actualCount; i++)
        {
            var expected = expectedTraitArgKinds[i];
            var actualType = _substitution.Apply(constraint.TraitArgs[i]);
            var actualKind = GetKindInferer().Infer(actualType);

            try
            {
                GetKindInferer().UnifyKinds(expected.Kind, actualKind);
            }
            catch (KindUnificationException ex)
            {
                errorMessage =
                    DiagnosticMessages.KindMismatchForTraitArgument(
                        i + 1,
                        expected.Name,
                        traitName,
                        KindParser.ToKindText(expected.Kind),
                        KindParser.ToKindText(actualKind),
                        ex.Message);
                return false;
            }
        }

        return true;
    }

    private bool TryGetExpectedTraitArgumentKinds(
        SymbolId traitId,
        string traitName,
        out List<(string Name, Kind Kind)> expectedKinds)
    {
        expectedKinds = [];

        if (traitId.IsValid)
        {
            var symbol = _symbolTable.GetSymbol(traitId);
            IReadOnlyList<SymbolId>? typeParams = symbol switch
            {
                TraitSymbol trait => trait.TypeParams,
                _ => null
            };

            if (typeParams == null)
            {
                return true;
            }

            foreach (var typeParamId in typeParams)
            {
                if (_symbolTable.GetSymbol(typeParamId) is not TypeParamSymbol typeParamSymbol)
                {
                    expectedKinds.Add(($"T{expectedKinds.Count + 1}", Kind.KStar.Instance));
                    continue;
                }

                var kindText = string.IsNullOrWhiteSpace(typeParamSymbol.KindAnnotation)
                    ? "kind1"
                    : typeParamSymbol.KindAnnotation;
                if (!KindParser.TryParse(kindText, out var parsedKind, out _))
                {
                    parsedKind = Kind.KStar.Instance;
                }

                var paramName = string.IsNullOrWhiteSpace(typeParamSymbol.Name)
                    ? $"T{expectedKinds.Count + 1}"
                    : typeParamSymbol.Name;
                expectedKinds.Add((paramName, parsedKind));
            }

            return true;
        }

        if (BuiltinTraits.IsBuiltinTraitName(traitName))
        {
            return true;
        }

        return false;
    }

    private static string FormatTraitRequirement(ImplTypeArgTraitRequirement requirement)
    {
        if (requirement.TraitTypeArgs.Count == 0)
        {
            return requirement.TraitName;
        }

        return $"{requirement.TraitName}[{string.Join(", ", requirement.TraitTypeArgs)}]";
    }

    /// <summary>
    /// 求解相等约束
    /// </summary>
    private bool SolveEqualityConstraint(EqualityConstraint constraint)
    {
        try
        {
            var left = _substitution.Apply(constraint.Left);
            var right = _substitution.Apply(constraint.Right);
            _substitution.Unify(left, right);
            return true;
        }
        catch (TypeInferenceException ex)
        {
            AddError(constraint.Span, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 求解 Kind 约束
    /// </summary>
    private bool SolveKindConstraint(KindConstraint constraint)
    {
        var expectedKindStr = constraint.ExpectedKind;
        var type = _substitution.Apply(constraint.Type);
        var actualKind = GetKindInferer().Infer(type);

        if (!KindParser.TryParse(expectedKindStr, out var expectedKind, out var parseError))
        {
            AddError(constraint.Span, parseError ?? DiagnosticMessages.UnsupportedKindAnnotation(expectedKindStr));
            return false;
        }

        try
        {
            GetKindInferer().UnifyKinds(expectedKind, actualKind);
        }
        catch (KindUnificationException ex)
        {
            AddError(
                constraint.Span,
                DiagnosticMessages.KindMismatch(
                    KindParser.ToKindText(expectedKind),
                    KindParser.ToKindText(actualKind),
                    ex.Message));
            return false;
        }

        return true;
    }

    private KindInferer GetKindInferer()
    {
        _kindInferer ??= new KindInferer(
            _symbolTable,
            _kindByTypeVar,
            _typeConstructorKindsBySymbol);
        return _kindInferer;
    }

    /// <summary>
    /// 获取 Trait 名称
    /// </summary>
    private string GetTraitName(SymbolId traitId)
    {
        var symbol = _symbolTable.GetSymbol(traitId);
        return symbol?.Name ?? "<unknown>";
    }

    /// <summary>
    /// 添加错误诊断（带级联错误抑制）
    /// </summary>
    private void AddError(SourceSpan span, string message)
    {
        // 抑制级联错误
        var constraintKey = $"{span.Location.Position}:{message}";
        if (_reportedConstraints.Contains(constraintKey))
        {
            return; // 已报告过相同错误，跳过
        }

        _reportedConstraints.Add(constraintKey);
        _recoveryContext.RecordError();

        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, "E2001");
        diag.WithLabel(span, message);
        _diagnostics.Add(diag);
    }

    /// <summary>
    /// 检查是否应该跳过级联错误
    /// </summary>
    private bool ShouldSkipCascadingError(Type type)
    {
        // 如果类型是包含错误的类型变量，跳过
        if (type is TyVar var && var.Instance != null)
        {
            return ShouldSkipCascadingError(var.Instance);
        }

        return false;
    }

    /// <summary>
    /// 检查类型是否满足 Trait 约束（不生成错误）
    /// </summary>
    public bool CheckTrait(Type type, SymbolId traitId)
    {
        var appliedType = _substitution.Apply(type);
        var traitName = GetTraitName(traitId);
        var resolvedTraitId = ResolveTraitId(traitId, traitName);

        return CheckTraitInternal(appliedType, resolvedTraitId, traitName, [], [], out _);
    }
}
