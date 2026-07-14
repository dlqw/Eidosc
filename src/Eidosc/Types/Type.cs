using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

/// <summary>
/// 类型表示基类
/// </summary>
public abstract record Type
{
    /// <summary>
    /// 类型 ID
    /// </summary>
    public TypeId Id { get; init; } = TypeId.None;

    /// <summary>
    /// 是否是具体类型（不含类型变量）
    /// </summary>
    public abstract bool IsConcrete { get; }

    /// <summary>
    /// 获取所有自由类型变量
    /// </summary>
    public abstract IEnumerable<int> FreeTypeVariables();
}

/// <summary>
/// 类型变量 (待推断)
/// </summary>
public sealed record TyVar : Type
{
    /// <summary>
    /// 类型变量索引
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// 实例化类型（链接到实际类型，用于合一）
    /// </summary>
    public Type? Instance { get; set; }

    /// <summary>
    /// Whether this variable was introduced only to recover from a type error.
    /// </summary>
    public bool IsErrorRecovery { get; set; }

    /// <summary>
    /// Whether this variable represents a constructor-local existential unpacked by a pattern.
    /// </summary>
    public bool IsRigidExistential { get; set; }

    public override bool IsConcrete => Instance?.IsConcrete ?? false;

    public override IEnumerable<int> FreeTypeVariables()
    {
        if (Instance != null)
        {
            foreach (var v in Instance.FreeTypeVariables())
                yield return v;
        }
        else
        {
            yield return Index;
        }
    }

    public override string ToString() => Instance?.ToString() ?? $"'t{Index}";
}

/// <summary>
/// 具体类型构造器
/// </summary>
public sealed record TyCon : Type
{
    /// <summary>
    /// 类型符号 (指向 ADT/Trait 等)
    /// </summary>
    public SymbolId Symbol { get; init; } = SymbolId.None;

    /// <summary>
    /// 类型参数
    /// </summary>
    public List<Type> Args { get; init; } = [];

    /// <summary>
    /// Canonical value arguments keyed by their declaration-order parameter index.
    /// </summary>
    public List<GenericValueArgument> ValueArgs { get; init; } = [];

    /// <summary>
    /// 类型名称 (用于显示)
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 类型构造器变量索引（用于 HKT 场景中的 F[A]）
    /// </summary>
    public int? ConstructorVarIndex { get; init; }

    public override bool IsConcrete =>
        !ConstructorVarIndex.HasValue &&
        Args.All(static argument => argument.IsConcrete) &&
        ValueArgs.All(static argument => argument.IsConcrete);

    public override IEnumerable<int> FreeTypeVariables()
    {
        if (ConstructorVarIndex.HasValue)
        {
            yield return ConstructorVarIndex.Value;
        }

        foreach (var v in Args.SelectMany(arg => arg.FreeTypeVariables()))
        {
            yield return v;
        }
    }

    public override string ToString()
    {
        var constructorName = !string.IsNullOrWhiteSpace(Name)
            ? Name
            : ConstructorVarIndex.HasValue
                ? $"'t{ConstructorVarIndex.Value}"
                : "<type>";

        if (Args.Count == 0 && ValueArgs.Count == 0)
            return constructorName;

        var args = FormatGenericArguments();
        return $"{constructorName}<{args}>";
    }

    private string FormatGenericArguments()
    {
        var valueByIndex = ValueArgs.ToDictionary(static argument => argument.ParameterIndex);
        var totalCount = Args.Count + ValueArgs.Count;
        var typeIndex = 0;
        var formatted = new List<string>(totalCount);
        for (var parameterIndex = 0; parameterIndex < totalCount; parameterIndex++)
        {
            if (valueByIndex.TryGetValue(parameterIndex, out var valueArgument))
            {
                formatted.Add(valueArgument.DisplayText);
            }
            else if (typeIndex < Args.Count)
            {
                formatted.Add(Args[typeIndex++].ToString());
            }
        }

        while (typeIndex < Args.Count)
        {
            formatted.Add(Args[typeIndex++].ToString());
        }

        return string.Join(", ", formatted);
    }
}

/// <summary>
/// Stable identity of a value-level generic argument.
/// </summary>
public sealed record GenericValueArgument(
    int ParameterIndex,
    string CanonicalText,
    string CanonicalHash,
    string DisplayText,
    TypeId TypeId,
    int ReferencedParameterIndex = -1,
    int ValueVariableIndex = -1)
{
    public bool IsInferenceVariable => ValueVariableIndex >= 0;

    public bool IsConcrete => ReferencedParameterIndex < 0 && !IsInferenceVariable;
}

/// <summary>
/// Internal erased proof produced by Refl. It can only be eliminated when the target TypeEq sides are already equal.
/// </summary>
public sealed record TyReflProof : Type
{
    public Type? WitnessType { get; init; }

    public override bool IsConcrete => WitnessType?.IsConcrete ?? true;

    public override IEnumerable<int> FreeTypeVariables()
    {
        if (WitnessType == null)
        {
            yield break;
        }

        foreach (var variable in WitnessType.FreeTypeVariables())
        {
            yield return variable;
        }
    }

    public override string ToString()
    {
        return WitnessType == null
            ? WellKnownStrings.Keywords.ReflConstructor
            : $"{WellKnownStrings.Keywords.ReflConstructor}[{WitnessType}]";
    }
}

/// <summary>
/// 函数类型
/// </summary>
public sealed record TyFun : Type
{
    /// <summary>
    /// 参数类型列表
    /// </summary>
    public List<Type> Params { get; init; } = [];

    /// <summary>
    /// 返回类型
    /// </summary>
    public required Type Result { get; init; }

    public EffectRow Effects { get; init; } = EffectRow.Pure;

    public override bool IsConcrete =>
        Params.All(p => p.IsConcrete) && Result.IsConcrete && Effects.IsConcrete;

    public override IEnumerable<int> FreeTypeVariables()
    {
        foreach (var param in Params)
        {
            foreach (var v in param.FreeTypeVariables())
                yield return v;
        }

        foreach (var v in Result.FreeTypeVariables())
            yield return v;

        foreach (var v in Effects.FreeTypeVariables())
            yield return v;
    }

    public override string ToString()
    {
        var paramsStr = Params.Count switch
        {
            0 => "()",
            1 => Params[0].ToString(),
            _ => $"({string.Join(", ", Params)})"
        };

        return Effects.IsPure
            ? $"{paramsStr} -> {Result}"
            : $"{paramsStr} -> {Result} need {Effects}";
    }
}

/// <summary>
/// 元组类型
/// </summary>
public sealed record TyTuple : Type
{
    /// <summary>
    /// 元素类型列表
    /// </summary>
    public List<Type> Elements { get; init; } = [];

    public override bool IsConcrete => Elements.All(e => e.IsConcrete);

    public override IEnumerable<int> FreeTypeVariables()
    {
        foreach (var elem in Elements)
        {
            foreach (var v in elem.FreeTypeVariables())
                yield return v;
        }
    }

    public override string ToString()
    {
        return $"({string.Join(", ", Elements)})";
    }
}

/// <summary>
/// 类型方案 (多态类型)
/// </summary>
public sealed record TypeScheme
{
    /// <summary>
    /// 量化的类型变量 (泛型参数)
    /// </summary>
    public HashSet<int> ForAll { get; init; } = [];

    /// <summary>
    /// Trait 约束
    /// </summary>
    public List<TypeConstraint> Constraints { get; init; } = [];

    /// <summary>
    /// 类型
    /// </summary>
    public required Type Type { get; init; }

    public override string ToString()
    {
        var parts = new List<string>();

        if (ForAll.Count > 0)
        {
            var vars = string.Join(", ", ForAll.OrderBy(v => v).Select(v => $"'t{v}"));
            parts.Add($"forall {vars}");
        }

        if (Constraints.Count > 0)
        {
            var constraints = string.Join(", ", Constraints);
            parts.Add(constraints);
        }

        parts.Add(Type.ToString());

        return parts.Count > 1
            ? string.Join(" => ", parts)
            : Type.ToString();
    }
}

/// <summary>
/// 基础类型常量和 TypeId 管理
/// </summary>
public static class BaseTypes
{
    // 内置类型 ID 常量 (0-99 保留给内置类型)
    /// <summary>
    /// 整数类型 ID
    /// </summary>
    public const int IntId = 1;

    /// <summary>
    /// 浮点数类型 ID
    /// </summary>
    public const int FloatId = 2;

    /// <summary>
    /// 布尔类型 ID
    /// </summary>
    public const int BoolId = 3;

    /// <summary>
    /// 字符串类型 ID
    /// </summary>
    public const int StringId = 4;

    /// <summary>
    /// 字符类型 ID
    /// </summary>
    public const int CharId = 5;

    /// <summary>
    /// 单元类型 ID
    /// </summary>
    public const int UnitId = 6;

    /// <summary>
    /// 擦除可调用类型 ID（用于动态函数值）。
    /// </summary>
    public const int ErasedCallableId = 7;

    /// <summary>
    /// 裸指针类型 ID（FFI 用，不参与引用计数管理）。
    /// </summary>
    public const int RawPtrId = 8;

    /// <summary>
    /// C 函数指针类型 ID（FFI 用，Cfn[A..., Ret] 表示 C 函数指针）。
    /// </summary>
    public const int CfnId = 9;

    /// <summary>
    /// 编译期类型等式证据 TypeEq[A, B]，运行时擦除。
    /// </summary>
    public const int TypeEqId = 10;

    /// <summary>
    /// 底类型：表示不会正常产生值的表达式。
    /// </summary>
    public const int NeverId = 11;

    public const int TypeValueId = 12;

    /// <summary>
    /// 整数类型
    /// </summary>
    public static TyCon Int { get; } = new() { Name = WellKnownStrings.BuiltinTypes.Int, Id = new TypeId(IntId) };

    /// <summary>
    /// 浮点数类型
    /// </summary>
    public static TyCon Float { get; } = new() { Name = WellKnownStrings.BuiltinTypes.Float, Id = new TypeId(FloatId) };

    /// <summary>
    /// 布尔类型
    /// </summary>
    public static TyCon Bool { get; } = new() { Name = WellKnownStrings.BuiltinTypes.Bool, Id = new TypeId(BoolId) };

    /// <summary>
    /// 字符串类型
    /// </summary>
    public static TyCon String { get; } = new() { Name = WellKnownStrings.BuiltinTypes.String, Id = new TypeId(StringId) };

    /// <summary>
    /// 字符类型
    /// </summary>
    public static TyCon Char { get; } = new() { Name = WellKnownStrings.BuiltinTypes.Char, Id = new TypeId(CharId) };

    /// <summary>
    /// 单元类型 (void)
    /// </summary>
    public static TyCon Unit { get; } = new() { Name = WellKnownStrings.BuiltinTypes.UnitSyntax, Id = new TypeId(UnitId) };

    /// <summary>
    /// 擦除可调用类型（运行时表示为指针）。
    /// </summary>
    public static TyCon ErasedCallable { get; } = new() { Name = "ErasedCallable", Id = new TypeId(ErasedCallableId) };

    /// <summary>
    /// C 函数指针类型（FFI 回调用）。
    /// </summary>
    public static TyCon Cfn { get; } = new() { Name = WellKnownStrings.BuiltinTypes.Cfn, Id = new TypeId(CfnId) };

    /// <summary>
    /// 类型等式证据类型。
    /// </summary>
    public static TyCon TypeEq { get; } = new() { Name = WellKnownStrings.BuiltinTypes.TypeEq, Id = new TypeId(TypeEqId) };

    /// <summary>
    /// 底类型。
    /// </summary>
    public static TyCon Never { get; } = new() { Name = WellKnownStrings.BuiltinTypes.Never, Id = new TypeId(NeverId) };

    public static TyCon TypeValue { get; } = new() { Name = WellKnownStrings.BuiltinTypes.Type, Id = new TypeId(TypeValueId) };

    /// <summary>
    /// 根据名称获取内置类型的 TypeId
    /// </summary>
    /// <param name="name">类型名称</param>
    /// <returns>TypeId，如果不是内置类型返回 None</returns>
    public static TypeId GetBuiltInTypeId(string name) => name switch
    {
        WellKnownStrings.BuiltinTypes.Int => new TypeId(IntId),
        WellKnownStrings.BuiltinTypes.Float => new TypeId(FloatId),
        WellKnownStrings.BuiltinTypes.Bool => new TypeId(BoolId),
        WellKnownStrings.BuiltinTypes.String => new TypeId(StringId),
        WellKnownStrings.BuiltinTypes.Char => new TypeId(CharId),
        "()" or WellKnownStrings.BuiltinTypes.Unit => new TypeId(UnitId),
        "ErasedCallable" => new TypeId(ErasedCallableId),
        WellKnownStrings.BuiltinTypes.RawPtr => new TypeId(RawPtrId),
        WellKnownStrings.BuiltinTypes.Cfn => new TypeId(CfnId),
        WellKnownStrings.BuiltinTypes.TypeEq => new TypeId(TypeEqId),
        WellKnownStrings.BuiltinTypes.Never => new TypeId(NeverId),
        WellKnownStrings.BuiltinTypes.Type => new TypeId(TypeValueId),
        _ => TypeId.None
    };

    /// <summary>
    /// 检查 TypeId 是否是内置类型
    /// </summary>
    /// <param name="id">TypeId</param>
    /// <returns>是否是内置类型</returns>
    public static bool IsBuiltIn(TypeId id) => id.Value is >= 1 and <= TypeValueId;

    public static bool IsCompilerMeta(TypeId id) =>
        id.Value is >= WellKnownTypeIds.MetaTypeInfoId and <= WellKnownTypeIds.MetaLayoutInfoId;

    public static bool IsReservedCompilerType(TypeId id) => IsBuiltIn(id) || IsCompilerMeta(id);

    public static bool IsNever(Type type)
    {
        return type is TyCon { Id.Value: NeverId } ||
               type is TyCon { Name: var name } && string.Equals(name, WellKnownStrings.BuiltinTypes.Never, StringComparison.Ordinal);
    }
}

/// <summary>
/// 不可变引用类型 Ref[T]
/// </summary>
public sealed record TyRef : Type
{
    /// <summary>
    /// 被引用的内部类型
    /// </summary>
    public required Type Inner { get; init; }

    /// <summary>
    /// 是否可变
    /// </summary>
    public bool IsMutable => false;

    public override bool IsConcrete => Inner.IsConcrete;

    public override IEnumerable<int> FreeTypeVariables()
    {
        foreach (var v in Inner.FreeTypeVariables())
            yield return v;
    }

    public override string ToString() => $"Ref[{Inner}]";
}

/// <summary>
/// 可变引用类型 MRef[T]（旧源码名 MutRef 仍可兼容解析）
/// </summary>
public sealed record TyMutRef : Type
{
    /// <summary>
    /// 被引用的内部类型
    /// </summary>
    public required Type Inner { get; init; }

    /// <summary>
    /// 是否可变
    /// </summary>
    public bool IsMutable => true;

    public override bool IsConcrete => Inner.IsConcrete;

    public override IEnumerable<int> FreeTypeVariables()
    {
        return Inner.FreeTypeVariables();
    }

    public override string ToString() => $"MRef[{Inner}]";
}

/// <summary>
/// 单线程共享所有权句柄 Shared[T]。
/// </summary>
public sealed record TyShared : Type
{
    /// <summary>
    /// 被共享拥有的内部类型。
    /// </summary>
    public required Type Inner { get; init; }

    public override bool IsConcrete => Inner.IsConcrete;

    public override IEnumerable<int> FreeTypeVariables()
    {
        return Inner.FreeTypeVariables();
    }

    public override string ToString() => $"Shared[{Inner}]";
}
