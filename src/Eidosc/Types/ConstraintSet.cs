using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

/// <summary>
/// 约束集合 - 管理类型约束的收集和查询
/// </summary>
public sealed class ConstraintSet
{
    private readonly List<TypeConstraint> _constraints = [];

    /// <summary>
    /// 按类型变量索引分组的 Trait 约束
    /// </summary>
    private readonly Dictionary<int, List<TraitConstraint>> _traitConstraintsByVar = [];

    /// <summary>
    /// 所有约束
    /// </summary>
    public List<TypeConstraint> Constraints => _constraints;

    /// <summary>
    /// 是否为空
    /// </summary>
    public bool IsEmpty => _constraints.Count == 0;

    /// <summary>
    /// 约束数量
    /// </summary>
    public int Count => _constraints.Count;

    /// <summary>
    /// 添加约束
    /// </summary>
    public void Add(TypeConstraint constraint)
    {
        _constraints.Add(constraint);

        // 如果是 TraitConstraint 且类型是 TyVar，按变量索引分组
        if (constraint is TraitConstraint tc && tc.Type is TyVar tv)
        {
            if (!_traitConstraintsByVar.TryGetValue(tv.Index, out var list))
            {
                list = [];
                _traitConstraintsByVar[tv.Index] = list;
            }
            list.Add(tc);
        }
    }

    /// <summary>
    /// 添加 Trait 约束
    /// </summary>
    public void AddTrait(
        Type type,
        SymbolId trait,
        string traitName,
        SourceSpan span,
        IReadOnlyList<Type>? traitArgs = null)
    {
        Add(new TraitConstraint
        {
            Type = type,
            Trait = trait,
            TraitName = traitName,
            TraitArgs = traitArgs?.ToList() ?? [],
            Span = span
        });
    }

    /// <summary>
    /// 添加多个约束
    /// </summary>
    public void AddRange(IEnumerable<TypeConstraint> constraints)
    {
        foreach (var constraint in constraints)
        {
            Add(constraint);
        }
    }

    public void RestoreFromSnapshot(IEnumerable<TypeConstraint> constraints)
    {
        Clear();
        AddRange(constraints);
    }

    /// <summary>
    /// 获取类型变量的 Trait 约束
    /// </summary>
    public List<TraitConstraint> GetTraitConstraintsForVar(int varIndex)
    {
        if (_traitConstraintsByVar.TryGetValue(varIndex, out var list))
            return list;
        return [];
    }

    /// <summary>
    /// 获取所有 Trait 约束
    /// </summary>
    public IEnumerable<TraitConstraint> GetTraitConstraints()
    {
        return _constraints.OfType<TraitConstraint>();
    }

    /// <summary>
    /// 检查约束是否涉及指定类型
    /// </summary>
    private static bool InvolvesType(TypeConstraint constraint, Type type)
    {
        return constraint switch
        {
            TraitConstraint tc => TypesEqual(tc.Type, type),
            EqualityConstraint ec => TypesEqual(ec.Left, type) || TypesEqual(ec.Right, type),
            KindConstraint kc => TypesEqual(kc.Type, type),
            _ => false
        };
    }

    /// <summary>
    /// 检查两个类型是否相等（简单比较）
    /// </summary>
    private static bool TypesEqual(Type a, Type b)
    {
        if (a is TyVar varA && b is TyVar varB)
            return varA.Index == varB.Index;
        if (a is TyCon conA && b is TyCon conB)
            return conA.Name == conB.Name;
        return ReferenceEquals(a, b);
    }

    /// <summary>
    /// 清空所有约束
    /// </summary>
    public void Clear()
    {
        _constraints.Clear();
        _traitConstraintsByVar.Clear();
    }

    /// <summary>
    /// 克隆约束集合
    /// </summary>
    public ConstraintSet Clone()
    {
        var clone = new ConstraintSet();
        clone.AddRange(_constraints);
        return clone;
    }

    /// <summary>
    /// 过滤出满足条件的约束
    /// </summary>
    public ConstraintSet Where(Func<TypeConstraint, bool> predicate)
    {
        var result = new ConstraintSet();
        result.AddRange(_constraints.Where(predicate));
        return result;
    }

    public override string ToString()
    {
        if (_constraints.Count == 0)
            return "[]";
        return $"[{string.Join(", ", _constraints)}]";
    }
}
