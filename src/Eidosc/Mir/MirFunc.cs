using Eidosc.Symbols;
using Eidosc.Utils;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir;

/// <summary>
/// MIR 模块
/// </summary>
public sealed class MirModule
{
    /// <summary>
    /// 模块名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 所属 package alias。为空表示当前 package。
    /// </summary>
    public string? PackageAlias { get; init; }

    public string? PackageInstanceKey { get; init; }

    /// <summary>
    /// 模块路径
    /// </summary>
    public List<string> Path { get; init; } = [];

    /// <summary>
    /// 函数列表
    /// </summary>
    public List<MirFunc> Functions { get; init; } = [];

    /// <summary>
    /// 编译期动态类型键（例如 TyVar/Fun/Tuple）到 TypeId 的反查表。
    /// </summary>
    public Dictionary<int, string> DynamicTypeKeys { get; init; } = [];

    /// <summary>
    /// 结构化类型描述表（替代字符串 key 的查询方式）。
    /// Key: TypeId.Value, Value: TypeDescriptor
    /// </summary>
    public Dictionary<int, TypeDescriptor> TypeDescriptors { get; init; } = [];

    /// <summary>
    /// 通过 link 指令声明的外部库名称列表
    /// </summary>
    public List<string> LinkLibraries { get; init; } = [];

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// C 结构体字段访问器元数据。
    /// 键为访问器函数名（如 "point_x"），值为字段偏移、类型信息。
    /// </summary>
    public Dictionary<string, CStructAccessorInfo> CStructAccessors { get; init; } = [];

    /// <summary>
    /// ADT 构造器类型布局表。
    /// Key: 构造器 ADT 的 TypeId（即构造器调用结果的 TypeId）
    /// Value: 该类型的所有构造器布局列表
    /// 用于 LLVM 结构化类型生成（GEP field access）。
    /// </summary>
    public Dictionary<int, List<ConstructorTypeLayout>> ConstructorLayouts { get; init; } = [];

    /// <summary>
    /// Structured trait implementation metadata carried from HIR for MIR-level dispatch.
    /// </summary>
    public List<ImplSymbol> TraitImpls { get; init; } = [];

    /// <summary>
    /// Structured trait metadata needed by MIR-level dispatch.
    /// </summary>
    public List<MirTraitInfo> TraitInfos { get; init; } = [];

    /// <summary>
    /// Structured type alias metadata needed by MIR-level type projection.
    /// </summary>
    public List<MirTypeAliasInfo> TypeAliases { get; init; } = [];

    /// <summary>
    /// Structured type constructor metadata needed by MIR-level type identity.
    /// </summary>
    public List<MirTypeConstructorInfo> TypeConstructors { get; init; } = [];

    /// <summary>
    /// Gets the MIR specialization failures that should be visible to later lowering phases.
    /// </summary>
    public List<MirSpecializationFailureInfo> SpecializationFailures { get; init; } = [];

    public override string ToString() => $"module {Name}";
}

/// <summary>
/// Describes a rejected MIR generic specialization in a phase-independent form.
/// </summary>
public sealed record MirSpecializationFailureInfo
{
    /// <summary>
    /// Gets the stable failure reason key.
    /// </summary>
    public string Reason { get; init; } = "";

    /// <summary>
    /// Gets the stable template key, such as <c>sym:42</c> or <c>name:f</c>.
    /// </summary>
    public string TemplateKey { get; init; } = "";

    /// <summary>
    /// Gets the source-visible template name.
    /// </summary>
    public string TemplateName { get; init; } = "";

    /// <summary>
    /// Gets the stable specialization signature key.
    /// </summary>
    public string SignatureKey { get; init; } = "";

    /// <summary>
    /// Gets the source-visible specialization signature display text.
    /// </summary>
    public string SignatureDisplay { get; init; } = "";

    /// <summary>
    /// Gets the specialization name that would have been generated.
    /// </summary>
    public string PreviewName { get; init; } = "";
}

/// <summary>
/// Describes trait-level dispatch metadata in a phase-independent MIR form.
/// </summary>
public sealed record MirTraitInfo
{
    /// <summary>
    /// Gets the trait symbol identity.
    /// </summary>
    public SymbolId TraitId { get; init; } = SymbolId.None;

    /// <summary>
    /// Gets the number of type parameters declared by the trait.
    /// </summary>
    public int TypeParameterCount { get; init; }

    /// <summary>
    /// Gets the trait type parameter symbol identities.
    /// </summary>
    public List<SymbolId> TypeParameterIds { get; init; } = [];

    /// <summary>
    /// Gets the trait-level Self position.
    /// </summary>
    public SelfPosition SelfPosition { get; init; } = SelfPosition.Unknown;

    /// <summary>
    /// Gets a value indicating whether any method has method-level Self dispatch metadata.
    /// </summary>
    public bool HasMethodDispatchMetadata { get; init; }

    /// <summary>
    /// Gets method-level dispatch metadata keyed by trait method identity.
    /// </summary>
    public List<MirTraitMethodInfo> Methods { get; init; } = [];

    /// <summary>
    /// Gets the parent trait symbol identities (supertrait chain).
    /// Populated from TraitSymbol.ParentTraits during MIR lowering.
    /// </summary>
    public List<SymbolId> ParentTraits { get; init; } = [];
}

/// <summary>
/// Describes method-level trait dispatch metadata in a phase-independent MIR form.
/// </summary>
public sealed record MirTraitMethodInfo
{
    /// <summary>
    /// Gets the owning trait symbol identity.
    /// </summary>
    public SymbolId TraitId { get; init; } = SymbolId.None;

    /// <summary>
    /// Gets the trait method symbol identity.
    /// </summary>
    public SymbolId MethodId { get; init; } = SymbolId.None;

    /// <summary>
    /// Gets the trait method source-visible name.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Gets the method-level Self position.
    /// </summary>
    public SelfPosition SelfPosition { get; init; } = SelfPosition.Unknown;

    /// <summary>
    /// Gets method parameter indices that contain Self.
    /// </summary>
    public List<int> SelfParameterIndices { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether Self appears in the result type.
    /// </summary>
    public bool SelfInResult { get; init; }

    /// <summary>
    /// Gets the builtin role inferred for this trait method.
    /// </summary>
    public TraitMethodRole MethodRole { get; init; } = TraitMethodRole.None;

    /// <summary>
    /// Gets a value indicating whether this trait method has a default implementation.
    /// When no explicit impl provides this method, the dispatch falls back to the
    /// default body (which references Self as a type parameter to be monomorphized).
    /// </summary>
    public bool HasDefaultImplementation { get; init; }
}

/// <summary>
/// Describes type-alias metadata in a phase-independent MIR form.
/// </summary>
public sealed record MirTypeAliasInfo
{
    /// <summary>
    /// Gets the alias symbol identity.
    /// </summary>
    public SymbolId AliasId { get; init; } = SymbolId.None;

    /// <summary>
    /// Gets the source-visible alias name.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Gets the alias type constructor id.
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// Gets the target type descriptor id that the alias expands to.
    /// </summary>
    public TypeId AliasTarget { get; init; } = TypeId.None;

    /// <summary>
    /// Gets the alias type parameters in declaration order.
    /// </summary>
    public List<SymbolId> TypeParameterIds { get; init; } = [];
}

/// <summary>
/// Describes source-level type constructor metadata in a phase-independent MIR form.
/// </summary>
public sealed record MirTypeConstructorInfo
{
    /// <summary>
    /// Gets the source symbol identity for the constructor.
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// Gets the source-visible constructor name.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Gets the interned type identity for this constructor.
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// Gets the type parameters declared by this constructor in source order.
    /// </summary>
    public List<SymbolId> TypeParameterIds { get; init; } = [];
}

/// <summary>
/// Source-ordered generic parameter metadata preserved in MIR.
/// </summary>
public sealed record MirGenericParameter
{
    public int ParameterIndex { get; init; } = -1;

    public SymbolId SymbolId { get; init; } = SymbolId.None;

    public string Name { get; init; } = "";

    public GenericParameterKind ParameterKind { get; init; } = GenericParameterKind.Type;

    public TypeId TypeId { get; init; } = TypeId.None;
}

/// <summary>
/// ADT 构造器的类型布局信息，用于 LLVM 结构化类型生成。
/// </summary>
public sealed record ConstructorTypeLayout
{
    /// <summary>
    /// ADT 类型名称（用于生成 LLVM struct 名称）。
    /// 例如 "Option_Int" 表示 Option[Int]。
    /// </summary>
    public string TypeName { get; init; } = "";

    /// <summary>
    /// 构造器名称（如 "Some", "None"）。
    /// </summary>
    public string ConstructorName { get; init; } = "";

    /// <summary>
    /// 构造器的 tag 值（AdtConstructorTypeId 的 FNV-1a hash）。
    /// 单构造器 ADT 的 tag 为 0（不需要 tag 字段）。
    /// </summary>
    public uint TagValue { get; init; }

    /// <summary>
    /// Runtime allocation type id used by the destructor registry.
    /// Falls back to ConstructorName-derived hashing when unset.
    /// </summary>
    public int RuntimeTypeId { get; init; }

    /// <summary>
    /// 各字段的 TypeId 列表（按声明顺序）。
    /// </summary>
    public List<TypeId> FieldTypeIds { get; init; } = [];
}

/// <summary>
/// C 结构体字段访问器元数据
/// </summary>
public sealed record CStructAccessorInfo
{
    /// <summary>
    /// 字段在结构体中的字节偏移量
    /// </summary>
    public int FieldOffset { get; init; }

    /// <summary>
    /// 字段类型的 TypeId
    /// </summary>
    public int FieldTypeId { get; init; }

    /// <summary>
    /// 是否是 getter（true）或 setter（false）
    /// </summary>
    public bool IsGetter { get; init; }
}

/// <summary>
/// MIR 函数
/// </summary>
public sealed class MirFunc
{
    /// <summary>
    /// 函数名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Gets the source-visible function name before backend/lowering-specific renaming.
    /// </summary>
    public string SourceName { get; init; } = "";

    /// <summary>
    /// 局部变量列表
    /// </summary>
    public List<MirLocal> Locals { get; init; } = [];

    /// <summary>
    /// 基本块列表
    /// </summary>
    public List<MirBasicBlock> BasicBlocks { get; init; } = [];

    /// <summary>
    /// 入口块 ID
    /// </summary>
    public BlockId EntryBlockId { get; init; }

    /// <summary>
    /// 返回类型
    /// </summary>
    public TypeId ReturnType { get; init; } = TypeId.None;

    /// <summary>
    /// 泛型类型参数个数。
    /// </summary>
    public int GenericParameterCount { get; init; }

    /// <summary>
    /// Generic parameters in source declaration order, including their semantic domains.
    /// </summary>
    public List<MirGenericParameter> GenericParameters { get; init; } = [];

    /// <summary>
    /// Gets generic type parameter IDs in declaration order.
    /// </summary>
    public List<TypeId> GenericTypeParameterIds { get; init; } = [];

    /// <summary>
    /// 是否使用运行时机器字 (i64/uintptr_t) 调用约定。
    /// Handler 分支函数由运行时通过 <c>eidos_invoke_dispatch_branch</c> 以
    /// <c>uintptr_t</c> 参数和返回值调用，LLVM 侧需要匹配该约定。
    /// </summary>
    public bool IsRuntimeWordAbi { get; set; }

    /// <summary>
    /// 是否是 FFI 外部函数（通过 @ffi 属性声明）
    /// </summary>
    public bool IsExternal { get; init; }

    /// <summary>
    /// FFI 外部函数的 C 符号名
    /// </summary>
    public string? ExternalSymbolName { get; init; }

    /// <summary>
    /// FFI 外部函数的来源库名称（编译期校验用）
    /// </summary>
    public string? ExternalLibrary { get; init; }

    public string? IntrinsicName { get; init; }

    public BuiltinIntrinsicRole BuiltinIntrinsicRole { get; init; } = BuiltinIntrinsicRole.None;

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// 符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 结构化函数标识。优先通过 SymbolId 建立身份；无 SymbolId 的内部函数可逐步填充模块/限定名。
    /// </summary>
    public FunctionId FunctionId { get; init; } = new();

    /// <summary>
    /// Whether this function is the executable entry selected by the project or source target.
    /// </summary>
    public bool IsEntry { get; init; }

    /// <summary>
    /// Gets the structured identity of a standard-library trait invocation helper.
    /// </summary>
    public TraitInvokeHelperKind TraitInvokeHelper { get; init; } = TraitInvokeHelperKind.None;

    /// <summary>
    /// Gets the trait constrained by a standard-library trait invocation helper.
    /// </summary>
    public SymbolId TraitInvokeHelperTraitId { get; init; } = SymbolId.None;

    /// <summary>
    /// 获取入口块
    /// </summary>
    public MirBasicBlock? EntryBlock => BasicBlocks.FirstOrDefault(b => b.Id.Equals(EntryBlockId));

    public override string ToString() => $"func {Name}";
}

/// <summary>
/// Identifies standard-library helpers that dispatch through a trait method.
/// </summary>
public enum TraitInvokeHelperKind
{
    /// <summary>
    /// Indicates that the function is not a trait invocation helper.
    /// </summary>
    None,

    /// <summary>
    /// Identifies <c>Std::TraitInvoke::eq_value</c>.
    /// </summary>
    EqValue,

    /// <summary>
    /// Identifies <c>Std::TraitInvoke::compare_value</c>.
    /// </summary>
    CompareValue,

    /// <summary>
    /// Identifies <c>Std::TraitInvoke::show_value</c>.
    /// </summary>
    ShowValue,

    /// <summary>
    /// Identifies <c>Std::TraitInvoke::hash_value</c>.
    /// </summary>
    HashValue,

    /// <summary>
    /// Identifies <c>Std::TraitInvoke::clone_value</c>.
    /// </summary>
    CloneValue
}

/// <summary>
/// MIR 局部变量
/// </summary>
public sealed class MirLocal
{
    /// <summary>
    /// 局部变量 ID
    /// </summary>
    public LocalId Id { get; init; }

    /// <summary>
    /// 变量名（调试用）
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 类型
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// 是否可变
    /// </summary>
    public bool IsMutable { get; init; }

    /// <summary>
    /// 是否是参数
    /// </summary>
    public bool IsParameter { get; init; }

    /// <summary>
    /// 模式绑定模式（仅模式绑定生成的局部变量有效）。
    /// </summary>
    public PatternBindingMode BindingMode { get; init; } = PatternBindingMode.ByValue;

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    public override string ToString() => IsParameter ? $"param %{Id.Value}: {Name}" : $"local %{Id.Value}: {Name}";
}
