using Eidosc.Symbols;

namespace Eidosc.Types;

/// <summary>
/// 类型环境 - 存储变量到类型方案的映射
/// </summary>
public sealed class TypeEnv
{
    private readonly Dictionary<SymbolId, TypeScheme> _bindings = new();
    private readonly TypeEnv? _parent;
    private HashSet<int>? _freeTypeVariablesCache;

    /// <summary>
    /// 创建空环境
    /// </summary>
    public static TypeEnv Empty { get; } = new();

    /// <summary>
    /// 创建子环境
    /// </summary>
    private TypeEnv(TypeEnv? parent = null)
    {
        _parent = parent;
    }

    public static TypeEnv FromSnapshot(IEnumerable<(SymbolId Symbol, TypeScheme Scheme)> bindings)
    {
        var env = new TypeEnv();
        foreach (var (symbol, scheme) in bindings.OrderBy(static binding => binding.Symbol.Value))
        {
            env._bindings[symbol] = scheme;
        }

        return env;
    }

    /// <summary>
    /// 查找符号的类型方案
    /// </summary>
    public TypeScheme? Lookup(SymbolId symbol)
    {
        if (_bindings.TryGetValue(symbol, out var scheme))
            return scheme;

        return _parent?.Lookup(symbol);
    }

    /// <summary>
    /// 导出当前环境链上的可见绑定快照；子环境绑定覆盖父环境同名 SymbolId。
    /// </summary>
    public IReadOnlyList<TypeEnvBindingSnapshot> GetBindingsSnapshot()
    {
        var result = new Dictionary<SymbolId, TypeScheme>();
        AddBindingsToSnapshot(result);
        return result
            .OrderBy(static binding => binding.Key.Value)
            .Select(static binding => new TypeEnvBindingSnapshot(binding.Key, binding.Value))
            .ToArray();
    }

    private void AddBindingsToSnapshot(Dictionary<SymbolId, TypeScheme> result)
    {
        _parent?.AddBindingsToSnapshot(result);
        foreach (var (symbol, scheme) in _bindings)
        {
            result[symbol] = scheme;
        }
    }

    /// <summary>
    /// 扩展环境（创建新环境并添加绑定）
    /// </summary>
    public TypeEnv Extend(SymbolId symbol, TypeScheme scheme)
    {
        var env = new TypeEnv(this)
        {
            _bindings =
            {
                [symbol] = scheme
            }
        };
        return env;
    }

    /// <summary>
    /// 扩展环境（添加多个绑定）
    /// </summary>
    public TypeEnv Extend(IEnumerable<(SymbolId Symbol, TypeScheme Scheme)> bindings)
    {
        var env = new TypeEnv(this);
        foreach (var (symbol, scheme) in bindings)
        {
            env._bindings[symbol] = scheme;
        }
        return env;
    }

    /// <summary>
    /// 扩展环境（非多态类型）
    /// </summary>
    public TypeEnv ExtendMono(SymbolId symbol, Type type)
    {
        return Extend(symbol, new TypeScheme { Type = type });
    }

    /// <summary>
    /// 获取所有自由类型变量
    /// </summary>
    public HashSet<int> FreeTypeVariables()
    {
        return new HashSet<int>(GetFreeTypeVariablesCached());
    }

    private HashSet<int> GetFreeTypeVariablesCached()
    {
        if (_freeTypeVariablesCache != null)
        {
            return _freeTypeVariablesCache;
        }

        var result = new HashSet<int>();

        foreach (var scheme in _bindings.Values)
        {
            foreach (var v in scheme.Type.FreeTypeVariables())
            {
                if (!scheme.ForAll.Contains(v))
                    result.Add(v);
            }
        }

        if (_parent != null)
        {
            result.UnionWith(_parent.GetFreeTypeVariablesCached());
        }

        _freeTypeVariablesCache = result;
        return _freeTypeVariablesCache;
    }

    /// <summary>
    /// 泛化类型 - 将不在环境中的自由变量量化
    /// </summary>
    public TypeScheme Generalize(Type type)
    {
        var envFreeVars = GetFreeTypeVariablesCached();
        var typeVariables = CollectTypeVariables(type);

        // 只量化不在环境中的自由变量；错误恢复变量不能泛化成普通多态变量。
        var forall = CreateGeneralizedVariableSet(typeVariables.Free, envFreeVars, typeVariables.ErrorRecovery);

        return new TypeScheme
        {
            ForAll = forall,
            Type = type,
            Constraints = [] // 约束在 TypeInferer 中收集
        };
    }

    /// <summary>
    /// 泛化类型（带约束）
    /// </summary>
    public TypeScheme Generalize(Type type, List<TypeConstraint> constraints)
    {
        var envFreeVars = GetFreeTypeVariablesCached();
        var typeVariables = CollectTypeVariables(type);

        // 只量化不在环境中的自由变量；错误恢复变量不能泛化成普通多态变量。
        var forall = CreateGeneralizedVariableSet(typeVariables.Free, envFreeVars, typeVariables.ErrorRecovery);

        // 过滤约束：只保留涉及量化变量的约束
        List<TypeConstraint> relevantConstraints = [];
        if (constraints.Count > 0 && forall.Count > 0)
        {
            relevantConstraints = new List<TypeConstraint>();
            foreach (var constraint in constraints)
            {
                if (InvolvesQuantifiedVars(constraint, forall))
                {
                    relevantConstraints.Add(constraint);
                }
            }
        }

        return new TypeScheme
        {
            ForAll = forall,
            Type = type,
            Constraints = relevantConstraints
        };
    }

    /// <summary>
    /// 检查约束是否涉及量化变量
    /// </summary>
    private static bool InvolvesQuantifiedVars(TypeConstraint constraint, HashSet<int> forall)
    {
        return constraint switch
        {
            TraitConstraint tc => TypeInvolvesVars(tc.Type, forall),
            EqualityConstraint ec => TypeInvolvesVars(ec.Left, forall) || TypeInvolvesVars(ec.Right, forall),
            KindConstraint kc => TypeInvolvesVars(kc.Type, forall),
            _ => false
        };
    }

    /// <summary>
    /// 检查类型是否涉及指定变量
    /// </summary>
    private static bool TypeInvolvesVars(Type type, HashSet<int> vars)
    {
        return TypeInvolvesVarsCore(type, vars);
    }

    private static HashSet<int> CreateGeneralizedVariableSet(
        HashSet<int> typeFreeVars,
        HashSet<int> envFreeVars,
        HashSet<int> errorRecoveryVars)
    {
        var result = new HashSet<int>();
        foreach (var varIndex in typeFreeVars)
        {
            if (!envFreeVars.Contains(varIndex) &&
                !errorRecoveryVars.Contains(varIndex))
            {
                result.Add(varIndex);
            }
        }

        return result;
    }

    private static TypeVariableCollection CollectTypeVariables(Type type)
    {
        var result = new TypeVariableCollection();
        CollectTypeVariables(type, result);
        return result;
    }

    private static void CollectTypeVariables(Type type, TypeVariableCollection result)
    {
        switch (type)
        {
            case TyVar { Instance: not null } variable:
                if (variable.IsErrorRecovery)
                {
                    result.ErrorRecovery.Add(variable.Index);
                }
                CollectTypeVariables(variable.Instance, result);
                break;
            case TyVar variable:
                result.Free.Add(variable.Index);
                if (variable.IsErrorRecovery)
                {
                    result.ErrorRecovery.Add(variable.Index);
                }
                break;
            case TyCon con:
                if (con.ConstructorVarIndex.HasValue)
                {
                    result.Free.Add(con.ConstructorVarIndex.Value);
                }

                foreach (var arg in con.Args)
                {
                    CollectTypeVariables(arg, result);
                }
                break;
            case TyReflProof { WitnessType: { } witness }:
                CollectTypeVariables(witness, result);
                break;
            case TyFun fun:
                foreach (var param in fun.Params)
                {
                    CollectTypeVariables(param, result);
                }

                CollectTypeVariables(fun.Result, result);
                if (fun.Effects is not null)
                {
                    CollectTypeVariables(fun.Effects, result);
                }
                break;
            case TyTuple tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectTypeVariables(element, result);
                }
                break;
            case TyRef reference:
                CollectTypeVariables(reference.Inner, result);
                break;
            case TyMutRef reference:
                CollectTypeVariables(reference.Inner, result);
                break;
            case TyShared shared:
                CollectTypeVariables(shared.Inner, result);
                break;
            case EffectRow abilitySet:
                foreach (var variable in abilitySet.Variables)
                {
                    result.Free.Add(variable.Id);
                }

                foreach (var ability in abilitySet.Effects)
                {
                    CollectTypeVariables(ability, result);
                }

                break;
            case EffectTag abilityType:
                foreach (var typeArg in abilityType.TypeArgs)
                {
                    CollectTypeVariables(typeArg, result);
                }
                break;
            case RequestType request:
                CollectTypeVariables(request.Effect, result);
                CollectTypeVariables(request.Result, result);
                if (request.Payload is not null)
                {
                    CollectTypeVariables(request.Payload, result);
                }

                if (request.ResumeArg is not null)
                {
                    CollectTypeVariables(request.ResumeArg, result);
                }
                break;
            default:
                foreach (var varIndex in type.FreeTypeVariables())
                {
                    result.Free.Add(varIndex);
                }
                break;
        }
    }

    private static bool TypeInvolvesVarsCore(Type type, HashSet<int> vars)
    {
        switch (type)
        {
            case TyVar { Instance: not null } variable:
                return TypeInvolvesVarsCore(variable.Instance, vars);
            case TyVar variable:
                return vars.Contains(variable.Index);
            case TyCon con:
                if (con.ConstructorVarIndex.HasValue &&
                    vars.Contains(con.ConstructorVarIndex.Value))
                {
                    return true;
                }

                foreach (var arg in con.Args)
                {
                    if (TypeInvolvesVarsCore(arg, vars))
                    {
                        return true;
                    }
                }

                return false;
            case TyReflProof { WitnessType: { } witness }:
                return TypeInvolvesVarsCore(witness, vars);
            case TyFun fun:
                foreach (var param in fun.Params)
                {
                    if (TypeInvolvesVarsCore(param, vars))
                    {
                        return true;
                    }
                }

                return TypeInvolvesVarsCore(fun.Result, vars) ||
                       TypeInvolvesVarsCore(fun.Effects, vars);
            case TyTuple tuple:
                foreach (var element in tuple.Elements)
                {
                    if (TypeInvolvesVarsCore(element, vars))
                    {
                        return true;
                    }
                }

                return false;
            case TyRef reference:
                return TypeInvolvesVarsCore(reference.Inner, vars);
            case TyMutRef reference:
                return TypeInvolvesVarsCore(reference.Inner, vars);
            case TyShared shared:
                return TypeInvolvesVarsCore(shared.Inner, vars);
            case EffectRow abilitySet:
                if (abilitySet.Variables.Any(variable => vars.Contains(variable.Id)))
                {
                    return true;
                }

                foreach (var ability in abilitySet.Effects)
                {
                    if (TypeInvolvesVarsCore(ability, vars))
                    {
                        return true;
                    }
                }

                return false;
            case EffectTag abilityType:
                foreach (var typeArg in abilityType.TypeArgs)
                {
                    if (TypeInvolvesVarsCore(typeArg, vars))
                    {
                        return true;
                    }
                }

                return false;
            case RequestType request:
                return TypeInvolvesVarsCore(request.Effect, vars) ||
                       TypeInvolvesVarsCore(request.Result, vars) ||
                       request.Payload is not null && TypeInvolvesVarsCore(request.Payload, vars) ||
                       request.ResumeArg is not null && TypeInvolvesVarsCore(request.ResumeArg, vars);
            default:
                foreach (var varIndex in type.FreeTypeVariables())
                {
                    if (vars.Contains(varIndex))
                    {
                        return true;
                    }
                }

                return false;
        }
    }

    private sealed class TypeVariableCollection
    {
        public HashSet<int> Free { get; } = [];

        public HashSet<int> ErrorRecovery { get; } = [];
    }
}

public sealed record TypeEnvBindingSnapshot(SymbolId Symbol, TypeScheme Scheme);
