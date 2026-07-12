namespace Eidosc.Symbols;

using Eidosc.Types;

/// <summary>
/// 函数符号
/// </summary>
public sealed record FuncSymbol : Symbol
{
    public FunctionEffectSummary? EffectSummary { get; set; }

    public override SymbolKind Kind => SymbolKind.Function;

    /// <summary>
    /// 类型参数
    /// </summary>
    public List<SymbolId> TypeParams { get; init; } = [];

    /// <summary>
    /// 参数列表
    /// </summary>
    public List<SymbolId> Parameters { get; init; } = [];

    /// <summary>
    /// 参数类型（类型推断后填充）
    /// </summary>
    public List<TypeId> ParamTypes { get; set; } = [];

    /// <summary>
    /// 返回类型
    /// </summary>
    public TypeId ReturnType { get; set; } = TypeId.None;

    /// <summary>
    /// 能力列表
    /// </summary>
    public List<EffectId> Effects { get; set; } = [];

    public bool IsComptime { get; init; }

    /// <summary>
    /// 所属 Trait（如果是 Trait 方法声明）
    /// </summary>
    public SymbolId? OwnerTrait { get; init; }

    /// <summary>
    /// For trait method declarations, records where this method mentions the trait self type.
    /// This is method-specific; mixed traits can have one method dispatch by receiver and another by result.
    /// </summary>
    public SelfPosition TraitSelfPosition { get; init; } = SelfPosition.Unknown;

    /// <summary>
    /// For trait method declarations, records the exact parameter indices that mention the trait self type.
    /// </summary>
    public List<int> TraitSelfParameterIndices { get; init; } = [];

    /// <summary>
    /// For trait method declarations, records whether the result type mentions the trait self type.
    /// </summary>
    public bool TraitSelfInResult { get; init; }

    /// <summary>
    /// Structured compiler role for trait methods that participate in builtin lowering.
    /// </summary>
    public TraitMethodRole TraitMethodRole { get; init; } = TraitMethodRole.None;

    /// <summary>
    /// 是否有函数体
    /// </summary>
    public bool HasBody { get; init; } = true;

    /// <summary>
    /// 是否是 trait 默认实现方法（有 body 的 trait 方法）。
    /// 当 impl 未提供该方法时，dispatch 会 fallback 到此默认体。
    /// </summary>
    public bool IsDefaultImplementation { get; init; }

    public bool IsTraitImplementation { get; init; }

    /// <summary>
    /// 是否是透明恒等函数，可被覆盖分析当作 identity view 使用。
    /// </summary>
    public bool IsTransparentIdentity { get; set; }

    /// <summary>
    /// 是否允许 proof checker 展开函数体进行定义相等归约。
    /// </summary>
    public bool IsProofTransparent { get; set; }

    /// <summary>
    /// Proof checker 使用的透明展开替身函数名；运行时仍使用原函数体。
    /// </summary>
    public string? ProofUnfoldTargetName { get; set; }

    /// <summary>
    /// 是否声明了 proof-only 展开替身。
    /// </summary>
    public bool HasProofUnfoldTarget { get; set; }

    /// <summary>
    /// 是否是 FFI 外部函数（通过 @ffi 属性声明）。
    /// 外部函数没有函数体，直接链接到 C 符号。
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>
    /// FFI 外部函数的 C 符号名。
    /// 如果 @ffi("name") 指定了名称则使用指定名称，否则使用 Eidos 函数名。
    /// 外部函数的 LLVM 符号名不加 eidos_ 前缀。
    /// </summary>
    public string? ExternalSymbolName { get; set; }

    /// <summary>
    /// FFI 外部函数的来源库名称（通过 @ffi("lib/symbol") 指定）。
    /// 用于编译期校验 link 声明，不影响 LLVM IR 生成。
    /// </summary>
    public string? ExternalLibrary { get; set; }

    /// <summary>
    /// 是否是 @cstruct 字段访问器内置函数
    /// </summary>
    public bool IsCStructAccessor { get; set; }

    /// <summary>
    /// C 结构体字段偏移量（仅当 IsCStructAccessor 为 true 时有效）
    /// </summary>
    public int CStructFieldOffset { get; set; }

    /// <summary>
    /// C 结构体字段类型 ID（仅当 IsCStructAccessor 为 true 时有效）
    /// </summary>
    public TypeId CStructFieldTypeId { get; set; } = TypeId.None;

    /// <summary>
    /// 是否是 getter（true）或 setter（false）。
    /// 仅当 IsCStructAccessor 为 true 时有效。
    /// </summary>
    public bool IsCStructGetter { get; set; }

    /// <summary>
    /// 隐式能力需求。
    /// @ffi 函数默认 ["FFI"]（类型签名声明效应时用声明值）。
    /// 运行时内置函数在 RegisterBuiltinFunctions() 中设置。
    /// 普通函数为空。
    /// </summary>
    public List<string> ImplicitAbilities { get; set; } = [];

    public BuiltinIntrinsicRole BuiltinIntrinsicRole { get; set; } = BuiltinIntrinsicRole.None;

    public string? IntrinsicName { get; set; }

    public bool IsCompilerIntrinsic => !string.IsNullOrWhiteSpace(IntrinsicName);
}

public enum TraitMethodRole
{
    None,
    Equality,
    Show
}

public enum BuiltinIntrinsicRole
{
    None,
    ValueBox,
    ValueUnbox,
    ValueBoxFree,
    SharedNew,
    SharedBorrow,
    SharedClone,
    SharedPtrEq
}
