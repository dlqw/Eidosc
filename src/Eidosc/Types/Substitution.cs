using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Utils;

namespace Eidosc.Types;

/// <summary>
/// 类型代换 - 管理类型变量到类型的映射
/// </summary>
public sealed class Substitution
{
    private sealed record ConstructorArgSlot(int? PlaceholderIndex, Type? FixedType)
    {
        public bool IsPlaceholder => PlaceholderIndex.HasValue;
    }

    private sealed record ConstructorBinding(string Name, SymbolId Symbol, List<ConstructorArgSlot> Slots);
    private sealed record ConstructorBindingMatch(List<int> PlaceholderPositions, int Score);

    private readonly Dictionary<int, Type> _mapping = new();
    private readonly Dictionary<int, ConstructorBinding> _constructorMapping = new();
    private readonly Dictionary<int, int> _constructorAliases = new();
    private readonly EffectUnifier _effectUnifier = new();
    private int _nextVarIndex;

    /// <summary>
    /// Thread-local cached HashSet for cycle detection in Apply/ApplyCon.
    /// Pattern: take (set to null) → use → clear + return (??=).
    /// Reentrant calls (e.g. TraitConstraintChecker → Apply) allocate a fresh set
    /// because the cache is null while the outer call is in-flight.
    /// </summary>
    [ThreadStatic]
    private static HashSet<int>? _cachedActiveVars;

    /// <summary>
    /// Deferred trait constraints keyed by TyVar index
    /// </summary>
    public Dictionary<int, List<TraitConstraint>> DeferredTraitConstraints { get; } = new();

    /// <summary>
    /// Allows constructor patterns to refine an unpacked existential within the pattern only.
    /// </summary>
    public bool AllowRigidExistentialRefinement { get; set; }

    /// <summary>
    /// Callback to check a trait constraint against a concrete type.
    /// </summary>
    public Func<Type, TraitConstraint, string?>? TraitConstraintChecker { get; set; }

    /// <summary>
    /// Callback to report an error.
    /// </summary>
    public Action<SourceSpan, string>? ErrorReporter { get; set; }

    /// <summary>
    /// 当前代换中的映射数量
    /// </summary>
    public int Count => _mapping.Count;

    /// <summary>
    /// 下一个新鲜类型变量索引（调试用）
    /// </summary>
    public int NextFreshVarIndex => _nextVarIndex;

    /// <summary>
    /// 创建新的类型变量
    /// </summary>
    public TyVar FreshTypeVariable()
    {
        return new TyVar { Index = _nextVarIndex++ };
    }

    /// <summary>
    /// 快速检查两个类型列表是否等价。
    /// 先 ReferenceEquals（代换未修改时的 O(1) 快速路径），不等时再回退到记录值相等。
    /// </summary>
    private static bool TypesUnchanged(IReadOnlyList<Type> resolved, IReadOnlyList<Type> original)
    {
        if (resolved.Count != original.Count)
            return false;
        for (var i = 0; i < resolved.Count; i++)
        {
            if (!ReferenceEquals(resolved[i], original[i]) && resolved[i] != original[i])
                return false;
        }
        return true;
    }

    private List<Type> ApplyTypeList(List<Type> original, HashSet<int> activeTypeVariables)
    {
        if (original.Count == 0)
        {
            return original;
        }

        List<Type>? resolved = null;
        for (var i = 0; i < original.Count; i++)
        {
            var current = original[i];
            var applied = Apply(current, activeTypeVariables);
            if (resolved == null &&
                !ReferenceEquals(applied, current) &&
                applied != current)
            {
                resolved = new List<Type>(original.Count);
                for (var j = 0; j < i; j++)
                {
                    resolved.Add(original[j]);
                }
            }

            resolved?.Add(applied);
        }

        return resolved ?? original;
    }

    private List<Type> InstantiateTypeList(List<Type> original, Dictionary<int, Type> varMap)
    {
        if (original.Count == 0)
        {
            return original;
        }

        List<Type>? instantiated = null;
        for (var i = 0; i < original.Count; i++)
        {
            var current = original[i];
            var next = InstantiateType(current, varMap);
            if (instantiated == null &&
                !ReferenceEquals(next, current) &&
                next != current)
            {
                instantiated = new List<Type>(original.Count);
                for (var j = 0; j < i; j++)
                {
                    instantiated.Add(original[j]);
                }
            }

            instantiated?.Add(next);
        }

        return instantiated ?? original;
    }

    /// <summary>
    /// 应用代换到类型
    /// </summary>
    public Type Apply(Type type)
    {
        var active = _cachedActiveVars ?? new HashSet<int>();
        _cachedActiveVars = null;
        try
        {
            return Apply(type, active);
        }
        finally
        {
            active.Clear();
            _cachedActiveVars ??= active;
        }
    }

    private Type Apply(Type type, HashSet<int> activeTypeVariables)
    {
        return type switch
        {
            TyVar var => ApplyVar(var, activeTypeVariables),
            TyCon con => ApplyCon(con, activeTypeVariables),
            TyFun fun => ApplyFun(fun, activeTypeVariables),
            TyTuple tuple => ApplyTuple(tuple, activeTypeVariables),
            TyRef reference => ApplyRef(reference, activeTypeVariables),
            TyMutRef mutReference => ApplyMutRef(mutReference, activeTypeVariables),
            TyShared shared => ApplyShared(shared, activeTypeVariables),
            TyReflProof reflProof => ApplyReflProof(reflProof, activeTypeVariables),
            EffectRow abilitySet => ApplyEffectRow(abilitySet, activeTypeVariables),
            EffectTag abilityType => ApplyEffectTag(abilityType, activeTypeVariables),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private Type ApplyVar(TyVar var, HashSet<int> activeTypeVariables)
    {
        if (!activeTypeVariables.Add(var.Index))
        {
            throw new TypeInferenceException(
                $"Recursive substitution detected while resolving 't{var.Index}'");
        }

        Type? result = null;

        if (var.Instance != null)
        {
            try
            {
                // 递归应用（处理链接的类型变量）
                result = Apply(var.Instance, activeTypeVariables);
                var.Instance = result; // 路径压缩优化
            }
            finally
            {
                activeTypeVariables.Remove(var.Index);
            }
        }
        else if (_mapping.TryGetValue(var.Index, out var mapped))
        {
            try
            {
                result = Apply(mapped, activeTypeVariables);
                _mapping[var.Index] = result; // 路径压缩
            }
            finally
            {
                activeTypeVariables.Remove(var.Index);
            }
        }
        else
        {
            activeTypeVariables.Remove(var.Index);
            return var;
        }

        // Re-evaluate deferred constraints if the variable resolved to a concrete type
        if (result is { IsConcrete: true } && DeferredTraitConstraints.Remove(var.Index, out var constraints))
        {
            // Remove first to prevent re-entrancy issues if checkers recurse

            if (TraitConstraintChecker != null && ErrorReporter != null)
            {
                foreach (var constraint in constraints)
                {
                    var errorMessage = TraitConstraintChecker(result, constraint);
                    if (errorMessage != null)
                    {
                        ErrorReporter(constraint.Span, errorMessage);
                    }
                }
            }
        }

        return result ?? throw new TypeInferenceException($"Substitution for 't{var.Index}' resolved to null");
    }

    private Type ApplyCon(TyCon con)
    {
        var active = _cachedActiveVars ?? new HashSet<int>();
        _cachedActiveVars = null;
        try
        {
            return ApplyCon(con, active);
        }
        finally
        {
            active.Clear();
            _cachedActiveVars ??= active;
        }
    }

    private Type ApplyCon(TyCon con, HashSet<int> activeTypeVariables)
    {
        var normalizedConstructorVar = con.ConstructorVarIndex.HasValue
            ? FindConstructorRoot(con.ConstructorVarIndex.Value)
            : (int?)null;

        ConstructorBinding? constructorBinding = null;
        var hasConstructorBinding = normalizedConstructorVar.HasValue &&
                                    _constructorMapping.TryGetValue(normalizedConstructorVar.Value, out constructorBinding);
        var resolvedName = hasConstructorBinding ? constructorBinding!.Name : con.Name;
        var resolvedSymbol = hasConstructorBinding ? constructorBinding!.Symbol : con.Symbol;
        List<Type>? boundArgs = null;
        var canApplyConstructorBinding = hasConstructorBinding &&
                                         TryApplyConstructorBinding(constructorBinding!, con.Args, activeTypeVariables, out boundArgs);
        var resolvedConstructorVar = canApplyConstructorBinding ? (int?)null : normalizedConstructorVar;
        var resolvedArgs = canApplyConstructorBinding
            ? boundArgs!
            : ApplyTypeList(con.Args, activeTypeVariables);

        if (con.Args.Count == 0)
        {
            if (resolvedArgs.Count == 0 &&
                resolvedName == con.Name &&
                resolvedSymbol == con.Symbol &&
                resolvedConstructorVar == con.ConstructorVarIndex)
            {
                return con;
            }

            return new TyCon
            {
                Name = resolvedName,
                Symbol = resolvedSymbol,
                ConstructorVarIndex = resolvedConstructorVar,
                Args = resolvedArgs
            };
        }

        if (TypesUnchanged(resolvedArgs, con.Args) &&
            resolvedName == con.Name &&
            resolvedSymbol == con.Symbol &&
            resolvedConstructorVar == con.ConstructorVarIndex)
        {
            return con;
        }

        return con with
        {
            Name = resolvedName,
            Symbol = resolvedSymbol,
            Args = resolvedArgs,
            ConstructorVarIndex = resolvedConstructorVar
        };
    }

    private bool TryApplyConstructorBinding(
        ConstructorBinding binding,
        IReadOnlyList<Type> appliedArgs,
        HashSet<int> activeTypeVariables,
        out List<Type>? boundArgs)
    {
        boundArgs = new List<Type>(binding.Slots.Count);
        foreach (var slot in binding.Slots)
        {
            if (slot.PlaceholderIndex is { } placeholderIndex)
            {
                if (placeholderIndex < 0 || placeholderIndex >= appliedArgs.Count)
                {
                    boundArgs = null;
                    return false;
                }

                boundArgs.Add(Apply(appliedArgs[placeholderIndex], activeTypeVariables));
                continue;
            }

            if (slot.FixedType == null)
            {
                boundArgs = null;
                return false;
            }

            boundArgs.Add(Apply(slot.FixedType, activeTypeVariables));
        }

        return true;
    }

    private Type ApplyFun(TyFun fun, HashSet<int> activeTypeVariables)
    {
        var newParams = ApplyTypeList(fun.Params, activeTypeVariables);
        var newResult = Apply(fun.Result, activeTypeVariables);
        var newAbilities = (EffectRow)Apply(fun.Effects, activeTypeVariables);

        var paramsChanged = !ReferenceEquals(newParams, fun.Params);
        var resultChanged = newResult != fun.Result;
        var abilitiesChanged = newAbilities != fun.Effects;

        if (!paramsChanged && !resultChanged && !abilitiesChanged)
            return fun;

        return fun with
        {
            Params = newParams,
            Result = newResult,
            Effects = newAbilities
        };
    }

    private Type ApplyTuple(TyTuple tuple, HashSet<int> activeTypeVariables)
    {
        if (tuple.Elements.Count == 0)
            return tuple;

        var newElements = ApplyTypeList(tuple.Elements, activeTypeVariables);

        if (ReferenceEquals(newElements, tuple.Elements))
            return tuple;

        return tuple with { Elements = newElements };
    }

    private Type ApplyRef(TyRef reference, HashSet<int> activeTypeVariables)
    {
        var newInner = Apply(reference.Inner, activeTypeVariables);
        return newInner == reference.Inner
            ? reference
            : reference with { Inner = newInner };
    }

    private Type ApplyMutRef(TyMutRef mutReference, HashSet<int> activeTypeVariables)
    {
        var newInner = Apply(mutReference.Inner, activeTypeVariables);
        return newInner == mutReference.Inner
            ? mutReference
            : mutReference with { Inner = newInner };
    }

    private Type ApplyShared(TyShared shared, HashSet<int> activeTypeVariables)
    {
        var newInner = Apply(shared.Inner, activeTypeVariables);
        return newInner == shared.Inner
            ? shared
            : shared with { Inner = newInner };
    }

    private Type ApplyReflProof(TyReflProof reflProof, HashSet<int> activeTypeVariables)
    {
        if (reflProof.WitnessType == null)
        {
            return reflProof;
        }

        var witness = Apply(reflProof.WitnessType, activeTypeVariables);
        return witness == reflProof.WitnessType
            ? reflProof
            : reflProof with { WitnessType = witness };
    }

    private Type ApplyEffectRow(EffectRow abilitySet, HashSet<int> activeTypeVariables)
    {
        abilitySet = _effectUnifier.ApplySubstitution(abilitySet);

        if (abilitySet.Effects.Count == 0)
        {
            return abilitySet;
        }

        HashSet<EffectTag>? newAbilities = null;
        foreach (var ability in abilitySet.Effects)
        {
            var applied = (EffectTag)ApplyEffectTag(ability, activeTypeVariables);
            if (newAbilities == null &&
                !ReferenceEquals(applied, ability) &&
                applied != ability)
            {
                newAbilities = new HashSet<EffectTag>(abilitySet.Effects.Count);
                foreach (var previous in abilitySet.Effects)
                {
                    if (ReferenceEquals(previous, ability))
                    {
                        break;
                    }

                    newAbilities.Add(previous);
                }
            }

            newAbilities?.Add(applied);
        }

        if (newAbilities == null)
        {
            return abilitySet;
        }

        return new EffectRow(newAbilities, abilitySet.Variables);
    }

    private Type ApplyEffectTag(EffectTag abilityType, HashSet<int> activeTypeVariables)
    {
        if (abilityType.TypeArgs.Count == 0)
        {
            return abilityType;
        }

        var newTypeArgs = ApplyTypeList(abilityType.TypeArgs, activeTypeVariables);
        if (ReferenceEquals(newTypeArgs, abilityType.TypeArgs))
        {
            return abilityType;
        }

        return abilityType with { TypeArgs = newTypeArgs };
    }

    /// <summary>
    /// 合一两个类型
    /// </summary>
    public void Unify(Type t1, Type t2)
    {
        while (true)
        {
            var type1 = Apply(t1);
            var type2 = Apply(t2);

            if (type1 == type2) return;

            switch (type1, type2)
            {
                case (TyReflProof reflProof, TyCon typeEq):
                    UnifyReflProof(typeEq, reflProof);
                    return;

                case (TyCon typeEq, TyReflProof reflProof):
                    UnifyReflProof(typeEq, reflProof);
                    return;

                case (TyReflProof, TyReflProof):
                    return;

                case (TyReflProof proof, _):
                    throw new TypeInferenceException(DiagnosticMessages.CannotUnifyTypes(proof, type2));

                case (_, TyReflProof proof):
                    throw new TypeInferenceException(DiagnosticMessages.CannotUnifyTypes(type1, proof));

                case (TyVar var1, TyVar var2) when var1.Index == var2.Index:
                    return;

                case (_, _) when BaseTypes.IsNever(type2):
                    return;

                case (TyVar { IsRigidExistential: true } rigid, TyVar other) when !other.IsRigidExistential:
                    Bind(other.Index, rigid);
                    return;

                case (TyVar other, TyVar { IsRigidExistential: true } rigid) when !other.IsRigidExistential:
                    Bind(other.Index, rigid);
                    return;

                case (TyVar { IsRigidExistential: true } rigid, _) when AllowRigidExistentialRefinement:
                    Bind(rigid.Index, type2);
                    return;

                case (_, TyVar { IsRigidExistential: true } rigid) when AllowRigidExistentialRefinement:
                    Bind(rigid.Index, type1);
                    return;

                case (TyVar { IsRigidExistential: true } rigid, _):
                    throw new TypeInferenceException(DiagnosticMessages.CannotUnifyTypes(rigid, type2));

                case (_, TyVar { IsRigidExistential: true } rigid):
                    throw new TypeInferenceException(DiagnosticMessages.CannotUnifyTypes(type1, rigid));

                case (TyVar var1, _):
                    Bind(var1.Index, type2);
                    return;

                case (_, TyVar var2):
                    Bind(var2.Index, type1);
                    return;

                case (TyCon con1, TyCon con2):
                    UnifyCon(con1, con2);
                    return;

                case (TyFun fun1, TyFun fun2):
                    UnifyFun(fun1, fun2);
                    return;

                case (TyTuple tuple1, TyTuple tuple2):
                    UnifyTuple(tuple1, tuple2);
                    return;

                case (TyRef ref1, TyRef ref2):
                    t1 = ref1.Inner;
                    t2 = ref2.Inner;
                    continue;

                case (TyMutRef mutRef1, TyMutRef mutRef2):
                    t1 = mutRef1.Inner;
                    t2 = mutRef2.Inner;
                    continue;

                case (TyShared shared1, TyShared shared2):
                    t1 = shared1.Inner;
                    t2 = shared2.Inner;
                    continue;

                default:
                    throw new TypeInferenceException(DiagnosticMessages.CannotUnifyTypes(type1, type2));
            }
        }
    }

    private void Bind(int varIndex, Type type)
    {
        // 发生检查 (Occurs Check) - 防止无限类型
        if (OccursIn(varIndex, type))
        {
            throw new TypeInferenceException(DiagnosticMessages.OccursCheckFailed(varIndex, type));
        }

        _mapping[varIndex] = type;
    }

    private bool OccursIn(int varIndex, Type type)
    {
        type = Apply(type);

        return type switch
        {
            TyVar var => var.Index != varIndex && var.Instance != null && OccursIn(varIndex, var.Instance),
            TyCon con => (con.ConstructorVarIndex.HasValue &&
                          FindConstructorRoot(con.ConstructorVarIndex.Value) == varIndex) ||
                         AnyOccursIn(con.Args, varIndex) ||
                         (con.ConstructorVarIndex.HasValue &&
                          _constructorMapping.TryGetValue(FindConstructorRoot(con.ConstructorVarIndex.Value), out var binding) &&
                          AnyOccursInSlots(binding.Slots, varIndex)),
            TyFun fun => AnyOccursIn(fun.Params, varIndex) ||
                         OccursIn(varIndex, fun.Result) ||
                         OccursIn(varIndex, fun.Effects),
            TyTuple tuple => AnyOccursIn(tuple.Elements, varIndex),
            TyRef reference => OccursIn(varIndex, reference.Inner),
            TyMutRef mutReference => OccursIn(varIndex, mutReference.Inner),
            TyShared shared => OccursIn(varIndex, shared.Inner),
            TyReflProof reflProof => reflProof.WitnessType != null && OccursIn(varIndex, reflProof.WitnessType),
            EffectRow abilitySet => abilitySet.Variables.Any(variable => variable.Id == varIndex) ||
                                    abilitySet.Effects.Any(effect => OccursIn(varIndex, effect)),
            EffectTag abilityType => AnyOccursIn(abilityType.TypeArgs, varIndex),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    // C6: Explicit loops to avoid enumerator allocation from LINQ .Any()
    private bool AnyOccursIn<T>(IReadOnlyList<T> types, int varIndex) where T : Type
    {
        for (int i = 0; i < types.Count; i++)
        {
            if (OccursIn(varIndex, types[i]))
            {
                return true;
            }
        }
        return false;
    }

    private bool AnyOccursIn<T>(HashSet<T> types, int varIndex) where T : Type
    {
        foreach (var t in types)
        {
            if (OccursIn(varIndex, t))
            {
                return true;
            }
        }
        return false;
    }

    private bool AnyOccursInSlots(List<ConstructorArgSlot> slots, int varIndex)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].FixedType is { } fixedType && OccursIn(varIndex, fixedType))
            {
                return true;
            }
        }
        return false;
    }

    private void UnifyCon(TyCon con1, TyCon con2)
    {
        var left = (TyCon)ApplyCon(con1);
        var right = (TyCon)ApplyCon(con2);

        if (!TryUnifyConstructorHeads(left, right))
        {
            throw new TypeInferenceException(DiagnosticMessages.CannotUnifyTypeConstructors(left, right));
        }

        left = (TyCon)ApplyCon(left);
        right = (TyCon)ApplyCon(right);

        if (left.Args.Count != right.Args.Count)
        {
            throw new TypeInferenceException(DiagnosticMessages.TypeArgumentCountMismatch(
                left,
                left.Args.Count,
                right,
                right.Args.Count));
        }

        for (int i = 0; i < left.Args.Count; i++)
        {
            Unify(left.Args[i], right.Args[i]);
        }
    }

    private void UnifyReflProof(TyCon typeEq, TyReflProof proof)
    {
        var resolved = (TyCon)ApplyCon(typeEq);
        if (!IsTypeEq(resolved) || resolved.Args.Count != 2)
        {
            throw new TypeInferenceException(DiagnosticMessages.CannotUnifyTypes(proof, resolved));
        }

        var left = Apply(resolved.Args[0]);
        var right = Apply(resolved.Args[1]);
        if (proof.WitnessType is { } witnessType)
        {
            var witness = Apply(witnessType);
            if (AreTypesAlreadyEqual(left, witness) && AreTypesAlreadyEqual(right, witness))
            {
                return;
            }

            throw new TypeInferenceException(DiagnosticMessages.CannotUnifyTypes(proof, resolved));
        }

        if (AreTypesAlreadyEqual(left, right))
        {
            return;
        }

        throw new TypeInferenceException(DiagnosticMessages.CannotUnifyTypes(proof, resolved));
    }

    private static bool IsTypeEq(TyCon tyCon)
    {
        return tyCon.Id.Value == BaseTypes.TypeEqId ||
               string.Equals(tyCon.Name, WellKnownStrings.BuiltinTypes.TypeEq, StringComparison.Ordinal);
    }

    private bool AreTypesAlreadyEqual(Type left, Type right)
    {
        left = Apply(left);
        right = Apply(right);

        if (ReferenceEquals(left, right) || left == right)
        {
            return true;
        }

        return (left, right) switch
        {
            (TyVar leftVar, TyVar rightVar) => leftVar.Index == rightVar.Index,
            (TyCon leftCon, TyCon rightCon) => AreTyConsAlreadyEqual(leftCon, rightCon),
            (TyFun leftFun, TyFun rightFun) => leftFun.Params.Count == rightFun.Params.Count &&
                                               leftFun.Params.Zip(rightFun.Params, AreTypesAlreadyEqual).All(static equal => equal) &&
                                               AreTypesAlreadyEqual(leftFun.Result, rightFun.Result),
            (TyTuple leftTuple, TyTuple rightTuple) => leftTuple.Elements.Count == rightTuple.Elements.Count &&
                                                      leftTuple.Elements.Zip(rightTuple.Elements, AreTypesAlreadyEqual).All(static equal => equal),
            (TyRef leftRef, TyRef rightRef) => AreTypesAlreadyEqual(leftRef.Inner, rightRef.Inner),
            (TyMutRef leftRef, TyMutRef rightRef) => AreTypesAlreadyEqual(leftRef.Inner, rightRef.Inner),
            (TyShared leftShared, TyShared rightShared) => AreTypesAlreadyEqual(leftShared.Inner, rightShared.Inner),
            _ => false
        };
    }

    private bool AreTyConsAlreadyEqual(TyCon left, TyCon right)
    {
        if (left.Id.IsValid && right.Id.IsValid && left.Id != right.Id)
        {
            return false;
        }

        if (left.Symbol.IsValid && right.Symbol.IsValid && left.Symbol != right.Symbol)
        {
            return false;
        }

        if (left.ConstructorVarIndex != right.ConstructorVarIndex)
        {
            return false;
        }

        if (!left.Id.IsValid && !right.Id.IsValid &&
            !left.Symbol.IsValid && !right.Symbol.IsValid &&
            !string.Equals(left.Name, right.Name, StringComparison.Ordinal))
        {
            return false;
        }

        return left.Args.Count == right.Args.Count &&
               left.Args.Zip(right.Args, AreTypesAlreadyEqual).All(static equal => equal);
    }

    private bool TryUnifyConstructorHeads(TyCon left, TyCon right)
    {
        if (left.ConstructorVarIndex.HasValue)
        {
            if (!TryBindConstructorVariable(left, right))
            {
                return false;
            }
        }

        if (right.ConstructorVarIndex.HasValue)
        {
            if (!TryBindConstructorVariable(right, left))
            {
                return false;
            }
        }

        var normalizedLeft = (TyCon)ApplyCon(left);
        var normalizedRight = (TyCon)ApplyCon(right);

        if (normalizedLeft.ConstructorVarIndex.HasValue || normalizedRight.ConstructorVarIndex.HasValue)
        {
            return true;
        }

        // WellKnownStrings.BuiltinTypes.Unit and "()" are the same type
        var leftName = normalizedLeft.Name is "()" ? WellKnownStrings.BuiltinTypes.Unit : normalizedLeft.Name;
        var rightName = normalizedRight.Name is "()" ? WellKnownStrings.BuiltinTypes.Unit : normalizedRight.Name;

        return string.Equals(leftName, rightName, StringComparison.Ordinal);
    }

    private bool TryBindConstructorVariable(TyCon pattern, TyCon candidate)
    {
        if (!pattern.ConstructorVarIndex.HasValue)
        {
            return false;
        }

        var root = FindConstructorRoot(pattern.ConstructorVarIndex.Value);
        var normalizedCandidate = (TyCon)ApplyCon(candidate);
        if (normalizedCandidate.ConstructorVarIndex.HasValue)
        {
            var candidateRoot = FindConstructorRoot(normalizedCandidate.ConstructorVarIndex.Value);
            return TryMergeConstructorVariables(root, candidateRoot);
        }

        if (string.IsNullOrWhiteSpace(normalizedCandidate.Name))
        {
            return true;
        }

        if (normalizedCandidate.Args.Count < pattern.Args.Count)
        {
            return false;
        }

        var slots = TryCreateConstructorBindingSlots(pattern.Args, normalizedCandidate.Args);
        var binding = new ConstructorBinding(
            normalizedCandidate.Name,
            normalizedCandidate.Symbol,
            slots);
        if (_constructorMapping.TryGetValue(root, out var existing))
        {
            return IsSameConstructorBinding(existing, binding);
        }

        _constructorMapping[root] = binding;
        return true;
    }

    private bool TryMergeConstructorVariables(int leftIndex, int rightIndex)
    {
        var leftRoot = FindConstructorRoot(leftIndex);
        var rightRoot = FindConstructorRoot(rightIndex);
        if (leftRoot == rightRoot)
        {
            return true;
        }

        var leftHasBinding = _constructorMapping.TryGetValue(leftRoot, out var leftBinding);
        var rightHasBinding = _constructorMapping.TryGetValue(rightRoot, out var rightBinding);
        if (leftHasBinding && rightHasBinding && !IsSameConstructorBinding(leftBinding!, rightBinding!))
        {
            return false;
        }

        _constructorAliases[rightRoot] = leftRoot;

        if (!leftHasBinding && rightHasBinding)
        {
            _constructorMapping[leftRoot] = rightBinding!;
        }

        _constructorMapping.Remove(rightRoot);
        return true;
    }

    private static bool IsSameConstructorBinding(ConstructorBinding left, ConstructorBinding right)
    {
        if (left.Symbol.IsValid && right.Symbol.IsValid)
        {
            return left.Symbol == right.Symbol &&
                   left.Slots.SequenceEqual(right.Slots);
        }

        return string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
               left.Slots.SequenceEqual(right.Slots);
    }

    private List<ConstructorArgSlot> TryCreateConstructorBindingSlots(
        IReadOnlyList<Type> patternArgs,
        IReadOnlyList<Type> candidateArgs)
    {
        if (patternArgs.Count == 0)
        {
            return candidateArgs
                .Select(static arg => new ConstructorArgSlot(null, arg))
                .ToList();
        }

        if (TryMatchPlaceholderPositions(patternArgs, candidateArgs, out var placeholderPositions))
        {
            var placeholderIndexByCandidatePosition = new Dictionary<int, int>(placeholderPositions.Count);
            for (var patternIndex = 0; patternIndex < placeholderPositions.Count; patternIndex++)
            {
                placeholderIndexByCandidatePosition[placeholderPositions[patternIndex]] = patternIndex;
            }

            var slots = new List<ConstructorArgSlot>(candidateArgs.Count);
            for (var candidateIndex = 0; candidateIndex < candidateArgs.Count; candidateIndex++)
            {
                if (placeholderIndexByCandidatePosition.TryGetValue(candidateIndex, out var placeholderIndex))
                {
                    slots.Add(new ConstructorArgSlot(placeholderIndex, null));
                }
                else
                {
                    slots.Add(new ConstructorArgSlot(null, candidateArgs[candidateIndex]));
                }
            }

            return slots;
        }

        throw new TypeInferenceException(DiagnosticMessages.UnableToInferConstructorPlaceholderPositions);
    }

    private bool TryMatchPlaceholderPositions(
        IReadOnlyList<Type> patternArgs,
        IReadOnlyList<Type> candidateArgs,
        out List<int> positions)
    {
        positions = [];
        var trial = Clone();
        if (!trial.TryFindBestConstructorBindingMatch(patternArgs, candidateArgs, 0, 0, [], out var match))
        {
            return false;
        }

        CopyStateFrom(trial);
        positions = match!.PlaceholderPositions;
        return true;
    }

    private bool TryFindBestConstructorBindingMatch(
        IReadOnlyList<Type> patternArgs,
        IReadOnlyList<Type> candidateArgs,
        int patternIndex,
        int nextCandidateIndex,
        List<int> currentPositions,
        out ConstructorBindingMatch? bestMatch)
    {
        bestMatch = null;

        if (patternIndex >= patternArgs.Count)
        {
            bestMatch = new ConstructorBindingMatch([.. currentPositions], 0);
            return true;
        }

        var remainingPatterns = patternArgs.Count - patternIndex;
        if (candidateArgs.Count - nextCandidateIndex < remainingPatterns)
        {
            return false;
        }

        var baselineState = Clone();
        Substitution? bestState = null;
        for (var candidateIndex = nextCandidateIndex; candidateIndex < candidateArgs.Count; candidateIndex++)
        {
            var remainingCandidatesAfterCurrent = candidateArgs.Count - (candidateIndex + 1);
            if (remainingCandidatesAfterCurrent < remainingPatterns - 1)
            {
                break;
            }

            var branch = baselineState.Clone();
            if (!branch.TryMatchConstructorPlaceholder(patternArgs[patternIndex], candidateArgs[candidateIndex], out var matchScore))
            {
                continue;
            }

            var branchPositions = new List<int>(currentPositions) { candidateIndex };
            if (!branch.TryFindBestConstructorBindingMatch(
                    patternArgs,
                    candidateArgs,
                    patternIndex + 1,
                    candidateIndex + 1,
                    branchPositions,
                    out var tailMatch))
            {
                continue;
            }

            var totalScore = matchScore +
                             tailMatch!.Score +
                             GetConstructorPlaceholderPositionBonus(patternArgs[patternIndex], candidateIndex, candidateArgs.Count);
            if (bestMatch == null || totalScore > bestMatch.Score)
            {
                bestMatch = new ConstructorBindingMatch(tailMatch.PlaceholderPositions, totalScore);
                bestState = branch;
            }
        }

        if (bestState != null)
        {
            CopyStateFrom(bestState);
        }

        return bestMatch != null;
    }

    private bool TryMatchConstructorPlaceholder(Type patternArg, Type candidateArg, out int score)
    {
        score = 0;
        var originalPattern = Apply(patternArg);
        var originalCandidate = Apply(candidateArg);

        try
        {
            Unify(patternArg, candidateArg);
            score = GetConstructorPlaceholderMatchScore(originalPattern, originalCandidate);
            return true;
        }
        catch (TypeInferenceException)
        {
            return false;
        }
    }

    private int GetConstructorPlaceholderMatchScore(Type patternArg, Type candidateArg)
    {
        if (patternArg.Equals(candidateArg))
        {
            return patternArg switch
            {
                TyVar when candidateArg is TyVar => 80,
                TyVar => 20,
                _ => 100
            };
        }

        if (patternArg is TyVar)
        {
            return candidateArg switch
            {
                TyVar => 80,
                TyCon { IsConcrete: true } => 20,
                _ => 60
            };
        }

        return 40;
    }

    private int GetConstructorPlaceholderPositionBonus(Type patternArg, int candidateIndex, int candidateCount)
    {
        if (candidateCount <= 1)
        {
            return 0;
        }

        var normalizedPatternArg = Apply(patternArg);
        var preferredIndex = (candidateCount - 1) / 2;
        var distanceScore = Math.Max(0, candidateCount - Math.Abs(candidateIndex - preferredIndex));
        if (normalizedPatternArg is TyVar)
        {
            // For plain higher-kinded placeholders like G[A], slot choice must dominate over
            // fresh-variable noise from neighboring constructor arguments.
            return distanceScore * 100;
        }

        return distanceScore;
    }

    private int FindConstructorRoot(int index)
    {
        if (!_constructorAliases.TryGetValue(index, out var parent))
        {
            _constructorAliases[index] = index;
            return index;
        }

        if (parent == index)
        {
            return index;
        }

        var root = FindConstructorRoot(parent);
        _constructorAliases[index] = root;
        return root;
    }

    private void UnifyFun(TyFun fun1, TyFun fun2)
    {
        if (fun1.Params.Count != fun2.Params.Count)
        {
            throw new TypeInferenceException(
                DiagnosticMessages.FunctionArityMismatch(fun1.Params.Count, fun2.Params.Count));
        }

        for (int i = 0; i < fun1.Params.Count; i++)
        {
            Unify(fun1.Params[i], fun2.Params[i]);
        }

        UnifyEffectRow(fun1.Effects, fun2.Effects);

        Unify(fun1.Result, fun2.Result);
    }

    private static TyFun FlattenFunctionPrefix(TyFun function)
    {
        var parameters = new List<Type>(function.Params);
        var result = function.Result;
        while (result is TyFun nested)
        {
            parameters.AddRange(nested.Params);
            result = nested.Result;
        }

        return parameters.Count == function.Params.Count && ReferenceEquals(result, function.Result)
            ? function
            : function with
            {
                Params = parameters,
                Result = result
            };
    }

    private void UnifyEffectRow(EffectRow left, EffectRow right)
    {
        if (!_effectUnifier.Unify(left, right))
        {
            throw new TypeInferenceException(
                DiagnosticMessages.FunctionEffectMismatch(left, right));
        }
    }

    private static bool IsSameEffect(EffectTag left, EffectTag right)
    {
        if (left.Symbol.IsValid && right.Symbol.IsValid)
        {
            return left.Symbol == right.Symbol;
        }

        if (!string.IsNullOrWhiteSpace(left.Name) && !string.IsNullOrWhiteSpace(right.Name))
        {
            return string.Equals(left.Name, right.Name, StringComparison.Ordinal);
        }

        return left.Equals(right);
    }

    private void UnifyTuple(TyTuple tuple1, TyTuple tuple2)
    {
        if (tuple1.Elements.Count != tuple2.Elements.Count)
        {
            throw new TypeInferenceException(
                DiagnosticMessages.TupleSizeMismatch(tuple1.Elements.Count, tuple2.Elements.Count));
        }

        for (int i = 0; i < tuple1.Elements.Count; i++)
        {
            Unify(tuple1.Elements[i], tuple2.Elements[i]);
        }
    }

    /// <summary>
    /// 实例化类型方案 - 为量化变量创建新鲜类型变量
    /// </summary>
    public Type Instantiate(TypeScheme scheme)
    {
        return InstantiateScheme(scheme).Type;
    }

    /// <summary>
    /// 使用显式类型参数实例化类型方案
    /// </summary>
    public InstantiatedTypeScheme InstantiateSchemeWithTypeArgs(TypeScheme scheme, IReadOnlyList<Type> explicitTypeArgs)
    {
        var quantifiedVars = CreateSortedQuantifiedVariableList(scheme.ForAll);
        if (quantifiedVars.Count != explicitTypeArgs.Count)
        {
            throw new TypeInferenceException(DiagnosticMessages.ExpectedTypeArgumentCount(
                quantifiedVars.Count,
                explicitTypeArgs.Count));
        }

        if (quantifiedVars.Count == 0)
        {
            return new InstantiatedTypeScheme
            {
                Type = Apply(scheme.Type),
                Constraints = ApplyConstraints(scheme.Constraints)
            };
        }

        var varMap = new Dictionary<int, Type>(quantifiedVars.Count);
        for (var i = 0; i < quantifiedVars.Count; i++)
        {
            varMap[quantifiedVars[i]] = Apply(explicitTypeArgs[i]);
        }

        return new InstantiatedTypeScheme
        {
            Type = InstantiateType(scheme.Type, varMap),
            Constraints = InstantiateConstraints(scheme.Constraints, varMap)
        };
    }

    /// <summary>
    /// 实例化类型方案（包含实例化后的约束）
    /// </summary>
    public InstantiatedTypeScheme InstantiateScheme(TypeScheme scheme)
    {
        if (scheme.ForAll.Count == 0)
        {
            return new InstantiatedTypeScheme
            {
                Type = Apply(scheme.Type),
                Constraints = ApplyConstraints(scheme.Constraints)
            };
        }

        var varMap = new Dictionary<int, Type>(scheme.ForAll.Count);

        foreach (var varIndex in scheme.ForAll)
        {
            varMap[varIndex] = FreshTypeVariable();
        }

        return new InstantiatedTypeScheme
        {
            Type = InstantiateType(scheme.Type, varMap),
            Constraints = InstantiateConstraints(scheme.Constraints, varMap)
        };
    }

    private static List<int> CreateSortedQuantifiedVariableList(HashSet<int> quantifiedVariables)
    {
        if (quantifiedVariables.Count == 0)
        {
            return [];
        }

        var result = new List<int>(quantifiedVariables.Count);
        foreach (var variable in quantifiedVariables)
        {
            result.Add(variable);
        }

        result.Sort();
        return result;
    }

    private Type InstantiateType(Type type, Dictionary<int, Type> varMap)
    {
        type = Apply(type);

        return type switch
        {
            TyVar var => varMap.TryGetValue(var.Index, out var newType) ? newType : var,
            TyCon con => InstantiateCon(con, varMap),
            TyFun fun => InstantiateFun(fun, varMap),
            TyTuple tuple => InstantiateTuple(tuple, varMap),
            TyShared shared => InstantiateShared(shared, varMap),
            EffectRow abilitySet => InstantiateEffectRow(abilitySet, varMap),
            EffectTag abilityType => InstantiateEffectTag(abilityType, varMap),
            _ => type // TyRef/TyMutRef: Apply() at entry already handles substitution
        };
    }

    private Type InstantiateCon(TyCon con, Dictionary<int, Type> varMap)
    {
        var constructorVarIndex = con.ConstructorVarIndex;
        var constructorName = con.Name;
        var constructorSymbol = con.Symbol;
        List<Type>? capturedPrefixArgs = null;
        if (constructorVarIndex.HasValue &&
            varMap.TryGetValue(constructorVarIndex.Value, out var constructorReplacement))
        {
            switch (constructorReplacement)
            {
                case TyVar replacementVar:
                    constructorVarIndex = replacementVar.Index;
                    break;
                case TyCon replacementCon:
                    constructorVarIndex = replacementCon.ConstructorVarIndex;
                    constructorName = replacementCon.Name;
                    constructorSymbol = replacementCon.Symbol;
                    if (replacementCon.Args.Count > 0)
                    {
                        capturedPrefixArgs = InstantiateTypeList(replacementCon.Args, varMap);
                    }
                    break;
            }
        }

        if (con.Args.Count == 0)
        {
            if ((capturedPrefixArgs == null || capturedPrefixArgs.Count == 0) &&
                constructorVarIndex == con.ConstructorVarIndex &&
                constructorName == con.Name &&
                constructorSymbol == con.Symbol)
            {
                return con;
            }

            return new TyCon
            {
                ConstructorVarIndex = constructorVarIndex,
                Name = constructorName,
                Symbol = constructorSymbol,
                Args = capturedPrefixArgs ?? []
            };
        }

        var newArgs = InstantiateTypeList(con.Args, varMap);
        if (capturedPrefixArgs != null && capturedPrefixArgs.Count > 0)
        {
            if (ReferenceEquals(newArgs, con.Args))
            {
                newArgs = [.. capturedPrefixArgs, .. con.Args];
            }
            else
            {
                newArgs.InsertRange(0, capturedPrefixArgs);
            }

            constructorVarIndex = null;
        }

        if (ReferenceEquals(newArgs, con.Args) &&
            constructorVarIndex == con.ConstructorVarIndex &&
            constructorName == con.Name &&
            constructorSymbol == con.Symbol)
        {
            return con;
        }

        return con with
        {
            ConstructorVarIndex = constructorVarIndex,
            Name = constructorName,
            Symbol = constructorSymbol,
            Args = newArgs
        };
    }

    private Type InstantiateFun(TyFun fun, Dictionary<int, Type> varMap)
    {
        var newParams = InstantiateTypeList(fun.Params, varMap);
        var newResult = InstantiateType(fun.Result, varMap);
        var newAbilities = (EffectRow)InstantiateType(fun.Effects, varMap);

        if (ReferenceEquals(newParams, fun.Params) &&
            newResult == fun.Result &&
            newAbilities == fun.Effects)
        {
            return fun;
        }

        return fun with
        {
            Params = newParams,
            Result = newResult,
            Effects = newAbilities
        };
    }

    private Type InstantiateShared(TyShared shared, Dictionary<int, Type> varMap)
    {
        var newInner = InstantiateType(shared.Inner, varMap);
        return newInner == shared.Inner
            ? shared
            : shared with { Inner = newInner };
    }

    private Type InstantiateTuple(TyTuple tuple, Dictionary<int, Type> varMap)
    {
        if (tuple.Elements.Count == 0)
            return tuple;

        var newElements = InstantiateTypeList(tuple.Elements, varMap);
        if (ReferenceEquals(newElements, tuple.Elements))
        {
            return tuple;
        }

        return tuple with { Elements = newElements };
    }

    private Type InstantiateEffectRow(EffectRow abilitySet, Dictionary<int, Type> varMap)
    {
        var instantiatedVariables = abilitySet.Variables
            .Select(variable => varMap.TryGetValue(variable.Id, out var mapped) && mapped is TyVar mappedVariable
                ? new EffectVariable { Id = mappedVariable.Index }
                : variable)
            .ToArray();

        if (abilitySet.Effects.Count == 0)
        {
            return instantiatedVariables.SequenceEqual(abilitySet.Variables.OrderBy(static variable => variable.Id))
                ? abilitySet
                : new EffectRow([], instantiatedVariables);
        }

        HashSet<EffectTag>? newAbilities = null;
        foreach (var ability in abilitySet.Effects)
        {
            var instantiated = (EffectTag)InstantiateEffectTag(ability, varMap);
            if (newAbilities == null &&
                !ReferenceEquals(instantiated, ability) &&
                instantiated != ability)
            {
                newAbilities = new HashSet<EffectTag>(abilitySet.Effects.Count);
                foreach (var previous in abilitySet.Effects)
                {
                    if (ReferenceEquals(previous, ability))
                    {
                        break;
                    }

                    newAbilities.Add(previous);
                }
            }

            newAbilities?.Add(instantiated);
        }

        if (newAbilities == null)
        {
            return abilitySet;
        }

        return new EffectRow(newAbilities, instantiatedVariables);
    }

    private Type InstantiateEffectTag(EffectTag abilityType, Dictionary<int, Type> varMap)
    {
        if (abilityType.TypeArgs.Count == 0)
        {
            return abilityType;
        }

        var newTypeArgs = InstantiateTypeList(abilityType.TypeArgs, varMap);
        if (ReferenceEquals(newTypeArgs, abilityType.TypeArgs))
        {
            return abilityType;
        }

        return abilityType with { TypeArgs = newTypeArgs };
    }

    private TypeConstraint ApplyConstraint(TypeConstraint constraint)
    {
        switch (constraint)
        {
            case TraitConstraint traitConstraint:
            {
                var type = Apply(traitConstraint.Type);
                var traitArgs = ApplyTypeList(traitConstraint.TraitArgs, []);
                return type == traitConstraint.Type && ReferenceEquals(traitArgs, traitConstraint.TraitArgs)
                    ? constraint
                    : traitConstraint with
                    {
                        Type = type,
                        TraitArgs = traitArgs
                    };
            }
            case EqualityConstraint equalityConstraint:
            {
                var left = Apply(equalityConstraint.Left);
                var right = Apply(equalityConstraint.Right);
                return left == equalityConstraint.Left && right == equalityConstraint.Right
                    ? constraint
                    : equalityConstraint with
                    {
                        Left = left,
                        Right = right
                    };
            }
            case KindConstraint kindConstraint:
            {
                var type = Apply(kindConstraint.Type);
                return type == kindConstraint.Type
                    ? constraint
                    : kindConstraint with { Type = type };
            }
            default:
                return constraint;
        }
    }

    private List<TypeConstraint> ApplyConstraints(List<TypeConstraint> constraints)
    {
        if (constraints.Count == 0)
        {
            return [];
        }

        var result = new List<TypeConstraint>(constraints.Count);
        foreach (var constraint in constraints)
        {
            result.Add(ApplyConstraint(constraint));
        }

        return result;
    }

    private TypeConstraint InstantiateConstraint(TypeConstraint constraint, Dictionary<int, Type> varMap)
    {
        switch (constraint)
        {
            case TraitConstraint traitConstraint:
            {
                var type = InstantiateType(traitConstraint.Type, varMap);
                var traitArgs = InstantiateTypeList(traitConstraint.TraitArgs, varMap);
                return type == traitConstraint.Type && ReferenceEquals(traitArgs, traitConstraint.TraitArgs)
                    ? constraint
                    : traitConstraint with
                    {
                        Type = type,
                        TraitArgs = traitArgs
                    };
            }
            case EqualityConstraint equalityConstraint:
            {
                var left = InstantiateType(equalityConstraint.Left, varMap);
                var right = InstantiateType(equalityConstraint.Right, varMap);
                return left == equalityConstraint.Left && right == equalityConstraint.Right
                    ? constraint
                    : equalityConstraint with
                    {
                        Left = left,
                        Right = right
                    };
            }
            case KindConstraint kindConstraint:
            {
                var type = InstantiateType(kindConstraint.Type, varMap);
                return type == kindConstraint.Type
                    ? constraint
                    : kindConstraint with { Type = type };
            }
            default:
                return constraint;
        }
    }

    private List<TypeConstraint> InstantiateConstraints(
        List<TypeConstraint> constraints,
        Dictionary<int, Type> varMap)
    {
        if (constraints.Count == 0)
        {
            return [];
        }

        var result = new List<TypeConstraint>(constraints.Count);
        foreach (var constraint in constraints)
        {
            result.Add(InstantiateConstraint(constraint, varMap));
        }

        return result;
    }

    /// <summary>
    /// 复制当前代换
    /// </summary>
    public Substitution Clone()
    {
        var clone = new Substitution();
        foreach (var (k, v) in _mapping)
        {
            clone._mapping[k] = v;
        }
        foreach (var (k, v) in _constructorMapping)
        {
            clone._constructorMapping[k] = v;
        }
        foreach (var (k, v) in _constructorAliases)
        {
            clone._constructorAliases[k] = v;
        }
        foreach (var (k, v) in DeferredTraitConstraints)
        {
            clone.DeferredTraitConstraints[k] = [.. v];
        }
        clone.TraitConstraintChecker = TraitConstraintChecker;
        clone.ErrorReporter = ErrorReporter;
        clone.AllowRigidExistentialRefinement = AllowRigidExistentialRefinement;
        clone._nextVarIndex = _nextVarIndex;
        return clone;
    }

    private void CopyStateFrom(Substitution source)
    {
        _mapping.Clear();
        foreach (var (k, v) in source._mapping)
        {
            _mapping[k] = v;
        }

        _constructorMapping.Clear();
        foreach (var (k, v) in source._constructorMapping)
        {
            _constructorMapping[k] = v;
        }

        _constructorAliases.Clear();
        foreach (var (k, v) in source._constructorAliases)
        {
            _constructorAliases[k] = v;
        }

        DeferredTraitConstraints.Clear();
        foreach (var (k, v) in source.DeferredTraitConstraints)
        {
            DeferredTraitConstraints[k] = [.. v];
        }
        TraitConstraintChecker = source.TraitConstraintChecker;
        ErrorReporter = source.ErrorReporter;
        AllowRigidExistentialRefinement = source.AllowRigidExistentialRefinement;

        _nextVarIndex = source._nextVarIndex;
    }

    public void RestoreFrom(Substitution source)
    {
        CopyStateFrom(source);
    }

    public void RestoreFromSnapshot(
        IEnumerable<SubstitutionBinding> bindings,
        int nextFreshVarIndex)
    {
        _mapping.Clear();
        foreach (var binding in bindings.OrderBy(static binding => binding.TypeVarIndex))
        {
            _mapping[binding.TypeVarIndex] = binding.RawType;
        }

        _constructorMapping.Clear();
        _constructorAliases.Clear();
        DeferredTraitConstraints.Clear();
        _nextVarIndex = Math.Max(nextFreshVarIndex, _mapping.Count == 0 ? 0 : _mapping.Keys.Max() + 1);
    }

    /// <summary>
    /// 导出代换绑定快照（用于调试输出）。
    /// </summary>
    public List<SubstitutionBinding> GetBindingsSnapshot()
    {
        var result = new List<SubstitutionBinding>(_mapping.Count);

        foreach (var (index, rawType) in _mapping.OrderBy(entry => entry.Key))
        {
            var resolvedType = Apply(rawType);
            result.Add(new SubstitutionBinding
            {
                TypeVarIndex = index,
                RawType = rawType,
                ResolvedType = resolvedType,
                Chain = BuildBindingChain(index)
            });
        }

        return result;
    }

    private string BuildBindingChain(int startIndex)
    {
        var segments = new List<string> { $"'t{startIndex}" };
        var visited = new HashSet<int> { startIndex };

        if (!_mapping.TryGetValue(startIndex, out var current))
        {
            return string.Join(" => ", segments);
        }

        while (true)
        {
            segments.Add(current.ToString());

            if (current is TyVar var)
            {
                if (var.Instance != null)
                {
                    current = var.Instance;
                    continue;
                }

                if (_mapping.TryGetValue(var.Index, out var mapped) && visited.Add(var.Index))
                {
                    current = mapped;
                    continue;
                }
            }

            break;
        }

        return string.Join(" => ", segments);
    }
}

/// <summary>
/// 代换绑定快照（调试输出用）
/// </summary>
public sealed record SubstitutionBinding
{
    public required int TypeVarIndex { get; init; }
    public required Type RawType { get; init; }
    public required Type ResolvedType { get; init; }
    public required string Chain { get; init; }
}

public sealed record InstantiatedTypeScheme
{
    public required Type Type { get; init; }
    public List<TypeConstraint> Constraints { get; init; } = [];
}

/// <summary>
/// 类型推断异常
/// </summary>
public sealed class TypeInferenceException : Exception
{
    public TypeInferenceException(string message) : base(message) { }
}
