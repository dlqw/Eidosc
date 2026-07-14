using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Semantic;

namespace Eidosc.Types;

/// <summary>
/// Kind 推断器 - 推断类型的 Kind，支持高阶 Kind 和部分应用
/// </summary>
public sealed class KindInferer
{
    private readonly TypeConstructorKindResolver _typeConstructorKinds;
    private readonly IReadOnlyDictionary<int, Kind> _typeParamKindsByVar;
    private readonly Dictionary<int, Kind.KVar> _inferredVarKindsByTypeVar = [];
    private int _nextVarId;

    public KindInferer(
        SymbolTable symbolTable,
        IReadOnlyDictionary<int, Kind>? typeParamKindsByVar = null,
        IReadOnlyDictionary<SymbolId, Kind>? typeConstructorKindsBySymbol = null)
    {
        _typeConstructorKinds = new TypeConstructorKindResolver(symbolTable, typeConstructorKindsBySymbol);
        _typeParamKindsByVar = typeParamKindsByVar ?? new Dictionary<int, Kind>();
    }

    /// <summary>
    /// 推断类型的 Kind
    /// </summary>
    public Kind Infer(Type type)
    {
        return type switch
        {
            TyVar var => InferVar(var),
            TyCon con => InferTyCon(con),
            TyFun => Kind.KStar.Instance,
            TyTuple => Kind.KStar.Instance,
            TyRef or TyMutRef or TyShared or EffectRow or EffectTag => Kind.KStar.Instance,
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    /// <summary>
    /// 推断类型变量的 Kind
    /// </summary>
    private Kind InferVar(TyVar var)
    {
        // 如果变量已实例化，推断实例的 Kind
        if (var.Instance != null)
        {
            return Infer(var.Instance);
        }

        if (_typeParamKindsByVar.TryGetValue(var.Index, out var annotatedKind))
        {
            return annotatedKind;
        }

        if (_inferredVarKindsByTypeVar.TryGetValue(var.Index, out var inferredKindVar))
        {
            return inferredKindVar;
        }

        // 为同一 TyVar 复用同一个 Kind 变量，保证跨约束统一可累积。
        var newKindVar = new Kind.KVar { Id = _nextVarId++ };
        _inferredVarKindsByTypeVar[var.Index] = newKindVar;
        return newKindVar;
    }

    /// <summary>
    /// 推断类型构造器的 Kind
    /// </summary>
    private Kind InferTyCon(TyCon con)
    {
        Kind constructorKind;
        if (con.ConstructorVarIndex.HasValue)
        {
            constructorKind = _typeParamKindsByVar.TryGetValue(con.ConstructorVarIndex.Value, out var kindFromEnv)
                ? kindFromEnv
                : new Kind.KVar { Id = _nextVarId++ };
        }
        else if (IsBuiltinType(con.Name))
        {
            constructorKind = Kind.KStar.Instance;
        }
        else
        {
            constructorKind = _typeConstructorKinds.GetConstructorKind(con.Symbol);
        }

        if (con.Args.Count == 0)
        {
            return constructorKind;
        }

        var argumentKinds = con.Args.Select(Infer).ToList();
        if (KindParser.TryApply(constructorKind, argumentKinds, out var resultKind, out _))
        {
            return resultKind;
        }

        return Kind.KStar.Instance;
    }

    /// <summary>
    /// 检查是否是内置类型
    /// </summary>
    private static bool IsBuiltinType(string name)
    {
        return name switch
        {
            WellKnownStrings.BuiltinTypes.Int or WellKnownStrings.BuiltinTypes.Float or WellKnownStrings.BuiltinTypes.String or WellKnownStrings.BuiltinTypes.Bool or WellKnownStrings.BuiltinTypes.Char or WellKnownStrings.BuiltinTypes.Never or WellKnownStrings.BuiltinTypes.Type or "()" => true,
            _ => false
        };
    }

    /// <summary>
    /// 在类型统一时检查 Kind 兼容性
    /// </summary>
    /// <returns>如果 Kind 兼容返回 true，否则返回 false</returns>
    public bool CheckKindCompatibility(Type type1, Type type2)
    {
        var kind1 = Infer(type1);
        var kind2 = Infer(type2);

        return Kind.IsCompatible(kind1, kind2);
    }

    /// <summary>
    /// 统一两个 Kind
    /// </summary>
    /// <exception cref="KindUnificationException">当 Kind 无法统一时抛出</exception>
    public void UnifyKinds(Kind kind1, Kind kind2)
    {
        // 展开实例化的变量
        kind1 = kind1 is Kind.KVar v1 && v1.Instance != null ? v1.Instance : kind1;
        kind2 = kind2 is Kind.KVar v2 && v2.Instance != null ? v2.Instance : kind2;

        switch (kind1, kind2)
        {
            case (Kind.KStar, Kind.KStar):
                return;

            case (Kind.KArrow arr1, Kind.KArrow arr2):
                UnifyKinds(arr1.Param, arr2.Param);
                UnifyKinds(arr1.Result, arr2.Result);
                return;

            case (Kind.KVar var1, Kind.KVar var2) when var1.Id == var2.Id:
                return;

            case (Kind.KVar var1, _):
                // 发生检查
                if (OccursIn(var1.Id, kind2))
                {
                    throw new KindUnificationException(
                        DiagnosticMessages.KindOccursCheckFailed(var1.Id, kind2.Name));
                }
                var1.Instance = kind2;
                return;

            case (_, Kind.KVar var2):
                // 发生检查
                if (OccursIn(var2.Id, kind1))
                {
                    throw new KindUnificationException(
                        DiagnosticMessages.KindOccursCheckFailed(var2.Id, kind1.Name));
                }
                var2.Instance = kind1;
                return;

            case (Kind.KRow row1, Kind.KRow row2) when row1.Fields.Count == row2.Fields.Count:
                for (int i = 0; i < row1.Fields.Count; i++)
                {
                    UnifyKinds(row1.Fields[i], row2.Fields[i]);
                }
                return;

            default:
                throw new KindUnificationException(DiagnosticMessages.CannotUnifyKinds(kind1.Name, kind2.Name));
        }
    }

    /// <summary>
    /// 发生检查 - 防止无限 Kind
    /// </summary>
    private static bool OccursIn(int varId, Kind kind)
    {
        kind = kind is Kind.KVar v && v.Instance != null ? v.Instance : kind;

        return kind switch
        {
            Kind.KVar kv => kv.Id == varId || (kv.Instance != null && OccursIn(varId, kv.Instance)),
            Kind.KArrow arr => OccursIn(varId, arr.Param) || OccursIn(varId, arr.Result),
            Kind.KRow row => row.Fields.Any(f => OccursIn(varId, f)),
            _ => false
        };
    }

    /// <summary>
    /// 获取类型构造器的预期参数数量
    /// </summary>
    public int GetExpectedParamCount(TyCon con)
    {
        if (con.ConstructorVarIndex.HasValue &&
            _typeParamKindsByVar.TryGetValue(con.ConstructorVarIndex.Value, out var constructorKind))
        {
            return KindParser.GetTopLevelArity(constructorKind);
        }

        if (IsBuiltinType(con.Name))
        {
            return 0;
        }

        return _typeConstructorKinds.GetExpectedParamCount(con.Symbol);
    }

    /// <summary>
    /// 检查类型构造器是否被部分应用
    /// </summary>
    public bool IsPartiallyApplied(TyCon con)
    {
        var expectedParams = GetExpectedParamCount(con);
        return con.Args.Count < expectedParams;
    }

    /// <summary>
    /// 创建新鲜的 Kind 变量
    /// </summary>
    public Kind.KVar FreshKindVariable()
    {
        return new Kind.KVar { Id = _nextVarId++ };
    }
}

/// <summary>
/// Kind 统一异常
/// </summary>
public sealed class KindUnificationException : Exception
{
    public KindUnificationException(string message) : base(message) { }
}
