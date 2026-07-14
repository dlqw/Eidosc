using Eidosc.Symbols;

namespace Eidosc.Types;

/// <summary>
/// 结构化类型描述 —— 替代字符串 key（如 "Fun(T10,T15)->T20"）的类型表示。
/// 遵循 Zig InternPool.Key 的设计思路：每个 TypeId 对应一个 TypeDescriptor，
/// 提供 O(1) 结构化访问，无需按需解析字符串。
/// </summary>
public abstract record TypeDescriptor
{
    /// <summary>
    /// 内置类型（Int, Float, Bool, String, Char, Unit, ErasedCallable, Cfn）
    /// </summary>
    public sealed record Builtin(int TypeIdValue) : TypeDescriptor
    {
        public override string ToString() => $"Builtin(T{TypeIdValue})";
    }

    /// <summary>
    /// 函数类型：Fun(paramTypes) -> returnType
    /// Effects 字段为 effect 注解（纯函数为 null/empty）
    /// </summary>
    public sealed record Function(TypeId[] ParamTypes, TypeId ReturnType, string? Effects = null) : TypeDescriptor
    {
        public override string ToString()
        {
            var abilitiesSuffix = !string.IsNullOrEmpty(Effects) ? Effects : "";
            return $"Fun({string.Join(",", ParamTypes.Select(t => t.ToString()))})->{ReturnType}{abilitiesSuffix}";
        }
    }

    /// <summary>
    /// 元组类型：Tuple(fieldTypes)
    /// </summary>
    public sealed record Tuple(TypeId[] FieldTypes) : TypeDescriptor
    {
        public override string ToString() =>
            $"Tuple({string.Join(",", FieldTypes.Select(t => t.ToString()))})";
    }

    /// <summary>
    /// ADT 构造器类型：TyCon(constructor, typeArgs)
    /// </summary>
    public sealed record TyCon(TypeConstructorKey Constructor, TypeId[] TypeArgs) : TypeDescriptor
    {
        public GenericValueArgumentDescriptor[] ValueArgs { get; init; } = [];

        public TyCon(string constructorDescriptor, TypeId[] typeArgs)
            : this(TypeConstructorKey.Parse(constructorDescriptor), typeArgs)
        {
        }

        public string ConstructorDescriptor => Constructor.ToDescriptorString();

        public override string ToString() =>
            $"TyCon({ConstructorDescriptor};{string.Join(",", TypeArgs.Select(t => t.ToString()))};values={string.Join(",", ValueArgs.Select(static value => value.ToString()))})";
    }

    /// <summary>
    /// 不可变引用类型：Ref(inner)
    /// 注意：旧格式使用 int 值（如 "Ref(5)"），而非 TypeId.ToString()（如 "Ref(T5)"）
    /// </summary>
    public sealed record Ref(TypeId Inner) : TypeDescriptor
    {
        public override string ToString() => $"Ref({Inner.Value})";
    }

    /// <summary>
    /// 可变引用类型：MRef(inner)
    /// 注意：旧格式使用 int 值（如 "MRef(5)"）
    /// </summary>
    public sealed record MutRef(TypeId Inner) : TypeDescriptor
    {
        public override string ToString() => $"MRef({Inner.Value})";
    }

    /// <summary>
    /// 共享所有权句柄类型：Shared(inner)。
    /// </summary>
    public sealed record Shared(TypeId Inner) : TypeDescriptor
    {
        public override string ToString() => $"Shared({Inner.Value})";
    }

    /// <summary>
    /// 类型变量（未解析的泛型参数）
    /// </summary>
    public sealed record TypeVar(int Index) : TypeDescriptor
    {
        public override string ToString() => $"TyVar_{Index}";
    }
}

public sealed record GenericValueArgumentDescriptor(
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

    public override string ToString() =>
        $"{ParameterIndex}:{CanonicalHash}:{TypeId.Value}:ref={ReferencedParameterIndex}:var={ValueVariableIndex}";
}
