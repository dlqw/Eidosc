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
    private readonly Dictionary<PreludeInstanceResolutionKey, PreludeInstanceResolution> _preludeInstanceResolutions = [];
    private static readonly PreludeInstanceResolution NoPreludeInstance = new([], [], null);
    private long _traitCheckCacheHits;
    private long _traitCheckCacheMisses;
    private long _traitCheckCacheSkipped;
    private long _traitCheckPreviousCacheHits;
    private long _traitCheckPreviousCacheMisses;
    private long _traitCheckPreviousCacheRestoreHits;
    private long _traitCheckPreviousCacheValidatedHits;
    private long _traitCheckPreviousCacheStaleHits;
    private long _preludeInstanceResolutionCacheHits;
    private long _preludeInstanceResolutionCacheMisses;
    private long _preludeInstanceResolutionSkips;
    private long _preludeInstanceCandidateChecks;
    private readonly Dictionary<string, ObligationResult> _obligationTable = new(StringComparer.Ordinal);
    private long _obligationRootGoals;
    private long _obligationTableHits;
    private long _obligationTableMisses;
    private long _obligationDeferredGoals;
    private int _obligationDepth;

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

    private readonly record struct PreludeInstanceResolutionKey(
        string TraitName,
        ImplTypeRefKey TypeKey);

    private sealed record PreludeInstanceResolution(
        IReadOnlyList<PrecompiledInstanceCandidate> Candidates,
        IReadOnlyList<PrecompiledInstanceCandidate> ApplicableCandidates,
        string? ErrorMessage)
    {
        public PrecompiledInstanceCandidate? Selected =>
            ApplicableCandidates.Count == 1 ? ApplicableCandidates[0] : null;
    }

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

    public IReadOnlyCollection<ObligationResult> ObligationResults => _obligationTable.Values;

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
            ["Types.traitCheckPreviousCache.staleHits"] = _traitCheckPreviousCacheStaleHits,
            ["Types.preludeInstanceResolution.entries"] = _preludeInstanceResolutions.Count,
            ["Types.preludeInstanceResolution.cacheHits"] = _preludeInstanceResolutionCacheHits,
            ["Types.preludeInstanceResolution.cacheMisses"] = _preludeInstanceResolutionCacheMisses,
            ["Types.preludeInstanceResolution.skips"] = _preludeInstanceResolutionSkips,
            ["Types.preludeInstanceResolution.candidateChecks"] = _preludeInstanceCandidateChecks,
            ["Types.obligations.rootGoals"] = _obligationRootGoals,
            ["Types.obligations.tableEntries"] = _obligationTable.Count,
            ["Types.obligations.tableHits"] = _obligationTableHits,
            ["Types.obligations.tableMisses"] = _obligationTableMisses,
            ["Types.obligations.deferredGoals"] = _obligationDeferredGoals
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
        _preludeInstanceResolutions.Clear();
        _preludeInstanceResolutionCacheHits = 0;
        _preludeInstanceResolutionCacheMisses = 0;
        _preludeInstanceResolutionSkips = 0;
        _preludeInstanceCandidateChecks = 0;
        _obligationTable.Clear();
        _obligationRootGoals = 0;
        _obligationTableHits = 0;
        _obligationTableMisses = 0;
        _obligationDeferredGoals = 0;
        _obligationDepth = 0;
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
        _obligationTable.Clear();
        _obligationRootGoals = constraints.Constraints.Count + constraints.Goals.Count;
        _obligationTableHits = 0;
        _obligationTableMisses = 0;
        _obligationDeferredGoals = 0;
        var success = true;
        var worklist = new Queue<ObligationGoal>(
            constraints.Constraints.Select(ObligationGoalAdapter.FromConstraint).Concat(constraints.Goals));
        const int maximumRootGoals = 100_000;
        var processedGoals = 0;

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
        }

        while (worklist.Count > 0)
        {
            var goal = worklist.Dequeue();
            processedGoals++;
            if (processedGoals > maximumRootGoals)
            {
                AnalysisIncomplete = true;
                IncompleteReason = $"Obligation solver exceeded its {maximumRootGoals} root-goal budget.";
                AddError(goal.Span, IncompleteReason);
                success = false;
                break;
            }

            var key = ObligationCanonicalizer.Build(goal, _substitution);
            if (_obligationTable.TryGetValue(key, out var cached))
            {
                _obligationTableHits++;
                if (cached.State is ObligationState.Failed or ObligationState.Overflow)
                {
                    success = false;
                }

                continue;
            }

            _obligationTableMisses++;
            _obligationTable[key] = new ObligationResult(
                key,
                goal,
                ObligationState.Evaluating,
                null,
                null);
            var result = SolveGoal(goal, key);
            _obligationTable[key] = result;
            if (result.State is ObligationState.Failed or ObligationState.Overflow)
            {
                success = false;
            }
        }

        if (!ResolveDeferredConstraintsWorklist())
        {
            success = false;
        }

        foreach (var (key, pending) in _obligationTable
                     .Where(static entry => entry.Value.State == ObligationState.Ambiguous)
                     .ToArray())
        {
            if (pending.Goal is not ImplementsGoal trait ||
                _substitution.Apply(trait.Type) is TyVar)
            {
                continue;
            }

            var resolvedKey = ObligationCanonicalizer.Build(pending.Goal, _substitution);
            var resolved = SolveGoal(pending.Goal, resolvedKey);
            _obligationTable[key] = resolved with { CanonicalKey = key };
            if (resolved.State is ObligationState.Failed or ObligationState.Overflow)
            {
                success = false;
            }
        }

        return success;
    }

    private ObligationResult SolveGoal(ObligationGoal goal, string canonicalKey)
    {
        var diagnosticCount = _diagnostics.Count;
        return goal switch
        {
            EqualGoal equality => SolveEqualityGoal(equality, canonicalKey, diagnosticCount),
            ImplementsGoal trait => SolveImplementsGoal(trait, canonicalKey, diagnosticCount),
            HasKindGoal kind => SolveKindGoal(kind, canonicalKey, diagnosticCount),
            EffectSubsetGoal effect => SolveEffectSubsetGoal(effect, canonicalKey),
            NormalizeProjectionGoal projection => SolveNormalizeProjectionGoal(projection, canonicalKey),
            AllGoal all => SolveAllGoal(all, canonicalKey),
            _ => throw new ArgumentOutOfRangeException(nameof(goal), goal, "Unsupported obligation goal.")
        };
    }

    private ObligationResult SolveAllGoal(AllGoal goal, string canonicalKey)
    {
        var evidence = new List<ObligationEvidence>(goal.Goals.Count);
        var blockers = new List<string>();
        foreach (var child in goal.Goals)
        {
            var result = SolveNestedGoal(child);
            if (result.State is ObligationState.Failed or ObligationState.Overflow)
            {
                return new ObligationResult(
                    canonicalKey,
                    goal,
                    result.State,
                    null,
                    result.Explanation ?? $"Child obligation '{result.CanonicalKey}' failed.");
            }

            if (result.State != ObligationState.Proven || result.Evidence == null)
            {
                blockers.Add(result.Explanation ?? result.CanonicalKey);
                continue;
            }

            evidence.Add(result.Evidence);
        }

        if (blockers.Count > 0)
        {
            _obligationDeferredGoals++;
            return new ObligationResult(
                canonicalKey,
                goal,
                ObligationState.Ambiguous,
                new DeferredObligationEvidence(string.Join("; ", blockers)),
                string.Join("; ", blockers));
        }

        return new ObligationResult(
            canonicalKey,
            goal,
            ObligationState.Proven,
            new AllObligationEvidence(evidence),
            null);
    }

    private ObligationResult SolveNestedGoal(ObligationGoal goal)
    {
        const int maximumDepth = 256;
        var key = ObligationCanonicalizer.Build(goal, _substitution);
        if (_obligationTable.TryGetValue(key, out var cached))
        {
            _obligationTableHits++;
            return cached.State == ObligationState.Evaluating
                ? cached with
                {
                    State = ObligationState.Ambiguous,
                    Evidence = new DeferredObligationEvidence($"obligation cycle at '{key}'"),
                    Explanation = $"Obligation cycle detected at '{key}'."
                }
                : cached;
        }

        _obligationTableMisses++;
        if (++_obligationDepth > maximumDepth)
        {
            _obligationDepth--;
            return new ObligationResult(
                key,
                goal,
                ObligationState.Overflow,
                null,
                $"Obligation solver exceeded its depth budget of {maximumDepth}.");
        }

        _obligationTable[key] = new ObligationResult(key, goal, ObligationState.Evaluating, null, null);
        try
        {
            var result = SolveGoal(goal, key);
            _obligationTable[key] = result;
            return result;
        }
        finally
        {
            _obligationDepth--;
        }
    }

    private ObligationResult SolveEffectSubsetGoal(EffectSubsetGoal goal, string canonicalKey)
    {
        var required = _substitution.ApplyEffectSubstitution(goal.Required);
        var allowed = _substitution.ApplyEffectSubstitution(goal.Allowed);
        var missingEffects = required.Effects
            .Where(effect => !allowed.Effects.Any(candidate => EffectsEquivalent(candidate, effect)))
            .ToArray();
        var uncoveredVariables = required.Variables.Except(allowed.Variables).ToArray();

        if ((missingEffects.Length > 0 || uncoveredVariables.Length > 0) &&
            allowed.Variables.Count == 1)
        {
            var openAllowed = allowed.Variables.Single();
            _substitution.TryBindEffectVariable(
                openAllowed,
                new EffectRow(missingEffects, uncoveredVariables));
            required = _substitution.ApplyEffectSubstitution(required);
            allowed = _substitution.ApplyEffectSubstitution(allowed);
            missingEffects = required.Effects
                .Where(effect => !allowed.Effects.Any(candidate => EffectsEquivalent(candidate, effect)))
                .ToArray();
            uncoveredVariables = required.Variables.Except(allowed.Variables).ToArray();
        }

        if (missingEffects.Length == 0 && uncoveredVariables.Length == 0)
        {
            return new ObligationResult(
                canonicalKey,
                goal,
                ObligationState.Proven,
                new EffectInclusionObligationEvidence(
                    required,
                    allowed,
                    new Dictionary<int, EffectRow>(_substitution.GetEffectBindings())),
                null);
        }

        if (uncoveredVariables.Length > 0 || allowed.Variables.Count > 0)
        {
            _obligationDeferredGoals++;
            var blocker = $"effect inclusion depends on open rows: required {required}, allowed {allowed}";
            return new ObligationResult(
                canonicalKey,
                goal,
                ObligationState.Ambiguous,
                new DeferredObligationEvidence(blocker),
                blocker);
        }

        var explanation = $"Required effects {required} are not a subset of allowed effects {allowed}.";
        AddError(goal.Span, explanation);
        return new ObligationResult(
            canonicalKey,
            goal,
            ObligationState.Failed,
            null,
            explanation);
    }

    private static bool EffectsEquivalent(EffectTag left, EffectTag right)
    {
        if (string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            (string.Equals(left.Name, WellKnownStrings.BuiltinAbilities.IO, StringComparison.Ordinal) ||
             string.Equals(left.Name, WellKnownStrings.BuiltinAbilities.FFI, StringComparison.Ordinal)))
        {
            return true;
        }

        if (left.Symbol.IsValid || right.Symbol.IsValid)
        {
            return left.Symbol.IsValid && right.Symbol.IsValid &&
                   left.Symbol == right.Symbol;
        }

        return string.Equals(left.Name, right.Name, StringComparison.Ordinal);
    }

    private ObligationResult SolveNormalizeProjectionGoal(
        NormalizeProjectionGoal goal,
        string canonicalKey)
    {
        if (goal.NormalizedType == null)
        {
            _obligationDeferredGoals++;
            var blocker = $"associated projection '{goal.ProjectionIdentity}' has no unique normalization evidence";
            return new ObligationResult(
                canonicalKey,
                goal,
                ObligationState.Ambiguous,
                new DeferredObligationEvidence(blocker),
                blocker);
        }

        var equality = SolveNestedGoal(new EqualGoal
        {
            Left = goal.NormalizedType,
            Right = goal.ExpectedType,
            Span = goal.Span,
            Reason = $"normalized associated projection '{goal.ProjectionIdentity}'"
        });
        if (equality.State != ObligationState.Proven || equality.Evidence == null)
        {
            return new ObligationResult(
                canonicalKey,
                goal,
                equality.State,
                null,
                equality.Explanation);
        }

        return new ObligationResult(
            canonicalKey,
            goal,
            ObligationState.Proven,
            new ProjectionObligationEvidence(
                goal.ProjectionIdentity,
                goal.Instance,
                goal.Member,
                _substitution.Apply(goal.NormalizedType),
                equality.Evidence),
            null);
    }

    private ObligationResult SolveEqualityGoal(EqualGoal goal, string canonicalKey, int diagnosticCount)
    {
        var constraint = new EqualityConstraint
        {
            Left = goal.Left,
            Right = goal.Right,
            Span = goal.Span
        };
        var solved = SolveEqualityConstraint(constraint);
        var left = _substitution.Apply(goal.Left);
        var right = _substitution.Apply(goal.Right);
        return new ObligationResult(
            canonicalKey,
            goal,
            solved ? ObligationState.Proven : ObligationState.Failed,
            solved ? new EqualityObligationEvidence(left, right) : null,
            GetNewDiagnosticMessage(diagnosticCount));
    }

    private ObligationResult SolveImplementsGoal(ImplementsGoal goal, string canonicalKey, int diagnosticCount)
    {
        var constraint = new TraitConstraint
        {
            Type = goal.Type,
            Trait = goal.Trait,
            TraitName = goal.TraitName,
            TraitArgs = goal.TraitArgs,
            TraitArgKeys = goal.TraitArgKeys,
            Span = goal.Span
        };
        var solved = SolveTraitConstraint(constraint);
        var appliedType = _substitution.Apply(goal.Type);
        if (solved && appliedType is TyVar variable)
        {
            _obligationDeferredGoals++;
            return new ObligationResult(
                canonicalKey,
                goal,
                ObligationState.Ambiguous,
                new DeferredObligationEvidence($"type variable 't{variable.Index} is unresolved"),
                null);
        }

        return new ObligationResult(
            canonicalKey,
            goal,
            solved ? ObligationState.Proven : ObligationState.Failed,
            solved ? CreateTraitEvidence(goal, appliedType) : null,
            GetNewDiagnosticMessage(diagnosticCount));
    }

    private ObligationResult SolveKindGoal(HasKindGoal goal, string canonicalKey, int diagnosticCount)
    {
        var constraint = new KindConstraint
        {
            Type = goal.Type,
            ExpectedKind = goal.ExpectedKind,
            Span = goal.Span
        };
        var solved = SolveKindConstraint(constraint);
        return new ObligationResult(
            canonicalKey,
            goal,
            solved ? ObligationState.Proven : ObligationState.Failed,
            solved ? new KindObligationEvidence(_substitution.Apply(goal.Type), goal.ExpectedKind) : null,
            GetNewDiagnosticMessage(diagnosticCount));
    }

    private string? GetNewDiagnosticMessage(int diagnosticCount) =>
        _diagnostics.Count > diagnosticCount ? _diagnostics[^1].Message : null;

    private TraitObligationEvidence CreateTraitEvidence(ImplementsGoal goal, Type appliedType)
    {
        var traitName = string.IsNullOrWhiteSpace(goal.TraitName)
            ? GetTraitName(goal.Trait)
            : goal.TraitName;
        var traitId = ResolveTraitId(goal.Trait, traitName);
        var instanceId = SymbolId.None;
        var instanceIdentity = string.Empty;
        var isBuiltin = appliedType is TyCon builtin &&
                        BuiltinTraits.IsBuiltinType(builtin) &&
                        BuiltinTraits.HasTrait(builtin, traitName);
        var isSupertrait = false;

        if (!isBuiltin && appliedType is TyCon constructor)
        {
            instanceIdentity = ResolvePreludeInstance(traitName, constructor).Selected?.Identity ?? string.Empty;
            var lookup = CreateTraitConstraintLookupRequest(constructor, GetTraitConstraintArgKeys(new TraitConstraint
            {
                Type = goal.Type,
                Trait = goal.Trait,
                TraitName = goal.TraitName,
                TraitArgs = goal.TraitArgs,
                TraitArgKeys = goal.TraitArgKeys,
                Span = goal.Span
            }));
            if (lookup.TypeId.IsValid && traitId.IsValid)
            {
                var direct = _symbolTable.LookupImplForTraitByKeys(
                    lookup.TypeId,
                    traitId,
                    lookup.ImplementingTypeKey,
                    lookup.TraitArgKeys);
                if (direct != null)
                {
                    instanceId = direct.Id;
                    instanceIdentity = direct.Name;
                }
                else if (TryGetProductCaseRootType(constructor, out var productRoot) &&
                         TryLookupDirectTraitImpl(productRoot, traitId, lookup.TraitArgKeys, out var productDirect) &&
                         productDirect != null)
                {
                    instanceId = productDirect.Id;
                    instanceIdentity = productDirect.Name;
                }
                else if (TryFindImplViaSupertraitChain(
                             lookup.TypeId,
                             lookup.ImplementingTypeKey,
                             traitId,
                             out var inherited) &&
                         inherited != null)
                {
                    instanceId = inherited.Id;
                    instanceIdentity = inherited.Name;
                    isSupertrait = true;
                }
            }
        }

        return new TraitObligationEvidence(
            appliedType,
            traitId,
            traitName,
            instanceId,
            instanceIdentity,
            isBuiltin,
            isSupertrait);
    }

    private bool ResolveDeferredConstraintsWorklist()
    {
        var diagnosticCountBefore = _diagnostics.Count;

        if (_substitution.DeferredTraitConstraints.Count == 0)
            return true;

        var queued = new HashSet<int>(_substitution.DeferredTraitConstraints.Keys);
        var worklist = new Queue<int>(queued);
        while (worklist.Count > 0)
        {
            var variable = worklist.Dequeue();
            _substitution.Apply(new TyVar { Index = variable });
            foreach (var discovered in _substitution.DeferredTraitConstraints.Keys)
            {
                if (queued.Add(discovered))
                {
                    worklist.Enqueue(discovered);
                }
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

        var preludeResolution = ResolvePreludeInstance(traitName, con);
        if (preludeResolution.Selected != null)
        {
            return true;
        }

        if (preludeResolution.ApplicableCandidates.Count > 1)
        {
            errorMessage = preludeResolution.ErrorMessage;
            return false;
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

        if (TryGetProductCaseRootType(con, out var productRoot) &&
            TryLookupDirectTraitImpl(productRoot, traitId, traitArgKeys, out var productImpl) &&
            productImpl != null &&
            CheckImplTypeRequirements(con, productImpl, out errorMessage))
        {
            return true;
        }

        // Supertrait chain fallback: if no direct impl found for the requested trait,
        // check if there is an impl for a child trait that extends this trait.
        // E.g., if checking Eq and no Eq instance exists, but an Ord instance does and Ord: Eq, accept it.
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

    private bool TryLookupDirectTraitImpl(
        TyCon implementingType,
        SymbolId traitId,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys,
        out ImplSymbol? implementation)
    {
        implementation = null;
        var lookup = CreateTraitConstraintLookupRequest(implementingType, traitArgKeys);
        if (!lookup.TypeId.IsValid || !traitId.IsValid)
        {
            return false;
        }

        implementation = _symbolTable.LookupImplForTraitByKeys(
            lookup.TypeId,
            traitId,
            lookup.ImplementingTypeKey,
            lookup.TraitArgKeys);
        return implementation != null;
    }

    private bool TryGetProductCaseRootType(TyCon source, out TyCon rootType)
    {
        rootType = source;
        if (!source.Symbol.IsValid ||
            _symbolTable.GetSymbol<AdtSymbol>(source.Symbol) is not { IsCaseType: true } caseSymbol ||
            _symbolTable.GetSymbol<AdtSymbol>(caseSymbol.ParentAdt) is not { } rootSymbol ||
            !string.Equals(caseSymbol.Name, rootSymbol.Name, StringComparison.Ordinal) ||
            !rootSymbol.TypeId.IsValid)
        {
            return false;
        }

        var parameterIds = _symbolTable.GetClosedCaseEffectiveGenericParameterIds(rootSymbol.Id);
        var typeParameterCount = parameterIds.Count(parameterId =>
            _symbolTable.GetSymbol<TypeParamSymbol>(parameterId)?.ParameterKind == GenericParameterKind.Type);
        if (source.Args.Count < typeParameterCount)
        {
            return false;
        }

        rootType = source with
        {
            Name = rootSymbol.Name,
            Symbol = rootSymbol.Id,
            Id = rootSymbol.TypeId,
            Args = source.Args.Take(typeParameterCount).ToList(),
            ValueArgs = source.ValueArgs
                .Where(argument => argument.ParameterIndex >= 0 && argument.ParameterIndex < parameterIds.Count)
                .ToList(),
            EffectArgs = source.EffectArgs
                .Where(argument => argument.ParameterIndex >= 0 && argument.ParameterIndex < parameterIds.Count)
                .ToList()
        };
        return true;
    }

    private PreludeInstanceResolution ResolvePreludeInstance(string traitName, TyCon type)
    {
        if (!PreludeCoreImageRegistry.HasPotentialInstanceCandidate(traitName, type))
        {
            _preludeInstanceResolutionSkips++;
            return NoPreludeInstance;
        }

        var isCacheable = !ContainsTypeVariable(type);
        var key = new PreludeInstanceResolutionKey(
            traitName,
            ImplLookupCanonicalizer.BuildTypeRefKey(_symbolTable, type));
        if (isCacheable && _preludeInstanceResolutions.TryGetValue(key, out var cached))
        {
            _preludeInstanceResolutionCacheHits++;
            return cached;
        }

        _preludeInstanceResolutionCacheMisses++;
        var candidates = PreludeCoreImageRegistry.GetResolvedInstanceCandidates(traitName, type);
        List<PrecompiledInstanceCandidate>? applicable = null;
        foreach (var candidate in candidates)
        {
            _preludeInstanceCandidateChecks++;
            if (ArePreludeInstanceRequirementsSatisfied(candidate, out _))
            {
                applicable ??= new List<PrecompiledInstanceCandidate>();
                applicable.Add(candidate);
            }
        }

        var applicableCandidates = applicable?.ToArray() ?? [];
        var errorMessage = applicableCandidates.Length > 1
            ? $"Type '{type}' has ambiguous Prelude instances for trait '{traitName}': {string.Join(", ", applicableCandidates.Select(static candidate => candidate.Identity))}"
            : null;
        var resolved = new PreludeInstanceResolution(candidates, applicableCandidates, errorMessage);
        if (isCacheable)
        {
            _preludeInstanceResolutions[key] = resolved;
        }

        return resolved;
    }

    private bool ArePreludeInstanceRequirementsSatisfied(
        PrecompiledInstanceCandidate candidate,
        out string? errorMessage)
    {
        foreach (var requirement in candidate.Requirements)
        {
            var traitId = ResolveTraitId(SymbolId.None, requirement.TraitName);
            var traitArguments = requirement.TraitArguments
                .Select(argument => _substitution.Apply(argument).ToString() ?? argument.GetType().Name)
                .ToArray();
            if (!CheckTraitCached(
                    _substitution.Apply(requirement.Type),
                    traitId,
                    requirement.TraitName,
                    traitArguments,
                    [],
                    out errorMessage))
            {
                return false;
            }
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Checks whether a type satisfies a trait through a supertrait chain.
    /// For example, if Ord: Eq and the type has an Ord instance, it also satisfies Eq.
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
            var currentFingerprint = CreateTraitCheckCandidateSetFingerprint(type, traitId, traitName, traitArgKeys);
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
            CreateTraitCheckCandidateSetFingerprint(type, traitId, traitName, traitArgKeys));
        return success;
    }

    private string CreateTraitCheckCandidateSetFingerprint(
        Type type,
        SymbolId traitId,
        string traitName,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys)
    {
        var applied = _substitution.Apply(type);
        if (applied is not TyCon con)
        {
            return "";
        }

        var resolvedTraitName = traitId.IsValid
            ? _symbolTable.GetSymbol(traitId)?.Name ?? traitName
            : traitName;
        var precompiledCandidates = ResolvePreludeInstance(resolvedTraitName, con).Candidates;
        var lookupRequest = CreateTraitConstraintLookupRequest(con, traitArgKeys);
        if (!lookupRequest.TypeId.IsValid || !traitId.IsValid)
        {
            return string.Join(";", precompiledCandidates.Select(static candidate => candidate.Identity));
        }

        var candidates = _symbolTable.LookupImplCandidatesForTraitByKeys(
            lookupRequest.TypeId,
            traitId,
            lookupRequest.TraitArgKeys);
        var symbolCandidates = string.Join(
            ";",
            candidates
                .Select(static candidate => string.Join(
                    "|",
                    candidate.Id.IsValid ? candidate.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                    candidate.Trait.IsValid ? candidate.Trait.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                    candidate.CanonicalImplementingType,
                    string.Join(",", candidate.CanonicalTraitTypeArgs)))
                .OrderBy(static key => key, StringComparer.Ordinal));
        return string.Join(
            ";",
            new[] { symbolCandidates }
                .Concat(precompiledCandidates.Select(static candidate => candidate.Identity))
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
                .Order(StringComparer.Ordinal));
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
                         con.ValueArgs.Any(static argument => !argument.IsConcrete) ||
                         con.EffectArgs.Any(static argument => !argument.IsConcrete),
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
