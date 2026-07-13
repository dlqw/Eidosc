using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Hir;

/// <summary>
/// HIR 声明抽象基类
/// </summary>
public abstract record HirDecl : HirNode
{
    protected HirDecl(HirKind kind) : base(kind) { }

    /// <summary>
    /// 声明名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 是否是模块级声明
    /// </summary>
    public bool IsModuleLevel { get; init; }
}

/// <summary>
/// 模块声明
/// </summary>
public sealed record HirModule : HirDecl
{
    public HirModule() : base(HirKind.Module) { }

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
    /// 模块中的声明列表
    /// </summary>
    public List<HirDecl> Declarations { get; init; } = [];

    /// <summary>
    /// 导出符号列表（用于显式导出）
    /// </summary>
    public List<SymbolId> Exports { get; init; } = [];

    /// <summary>
    /// 导入列表
    /// </summary>
    public List<HirImport> Imports { get; init; } = [];

    /// <summary>
    /// 通过 link 指令声明的外部库名称列表
    /// </summary>
    public List<string> LinkLibraries { get; init; } = [];

    public override string ToString() => $"Module({string.Join(WellKnownStrings.Separators.Path, Path)})";
}

/// <summary>
/// 导入声明
/// </summary>
public sealed record HirImport
{
    /// <summary>
    /// 导入路径
    /// </summary>
    public List<string> Path { get; init; } = [];

    /// <summary>
    /// 别名（可选）
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>
    /// 导入的特定符号（空表示导入全部）
    /// </summary>
    public List<string> SelectiveImports { get; init; } = [];

    /// <summary>
    /// 是否是隐式导入（use）
    /// </summary>
    public bool IsUse { get; init; }
}

/// <summary>
/// 函数定义
/// </summary>
public sealed record HirFunc : HirDecl
{
    public HirFunc() : base(HirKind.Func) { }

    /// <summary>
    /// Gets the source-visible function name before HIR/backend lowering.
    /// </summary>
    public string SourceName { get; init; } = "";

    /// <summary>
    /// 类型参数列表
    /// </summary>
    public List<HirTypeParam> TypeParams { get; init; } = [];

    /// <summary>
    /// 参数列表
    /// </summary>
    public List<HirParam> Parameters { get; init; } = [];

    /// <summary>
    /// 返回类型
    /// </summary>
    public TypeId ReturnType { get; init; } = TypeId.None;

    /// <summary>
    /// 函数体（抽象函数为 null）
    /// </summary>
    public HirNode? Body { get; init; }

    /// <summary>
    /// 能力集（所需能力）
    /// </summary>
    public List<SymbolId> RequiredAbilities { get; init; } = [];

    public bool IsComptime { get; init; }

    /// <summary>
    /// 是否是入口函数
    /// </summary>
    public bool IsEntry { get; init; }

    /// <summary>
    /// 是否是 FFI 外部函数（通过 @ffi 属性声明）
    /// </summary>
    public bool IsExternal { get; init; }

    /// <summary>
    /// FFI 外部函数的 C 符号名
    /// </summary>
    public string? ExternalSymbolName { get; init; }

    /// <summary>
    /// FFI 外部函数的来源库名称
    /// </summary>
    public string? ExternalLibrary { get; init; }

    public string? IntrinsicName { get; init; }

    public BuiltinIntrinsicRole BuiltinIntrinsicRole { get; init; } = BuiltinIntrinsicRole.None;

    public override string ToString() => $"Func({Name})";
}

/// <summary>
/// 类型参数
/// </summary>
public sealed record HirTypeParam
{
    /// <summary>
    /// 参数名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// Gets the interned type variable ID that represents this type parameter.
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// Semantic domain ranged over by this generic parameter.
    /// </summary>
    public GenericParameterKind ParameterKind { get; init; } = GenericParameterKind.Type;

    /// <summary>
    /// Kind 注解（已归一化后的文本）
    /// </summary>
    public string KindAnnotation { get; init; } = "kind1";

    /// <summary>
    /// Whether this generic parameter is marked as comptime.
    /// </summary>
    public bool IsComptime { get; init; }

    /// <summary>
    /// Source-level comptime type annotation.
    /// </summary>
    public string? ComptimeTypeAnnotation { get; init; }

    /// <summary>
    /// 类型约束（trait 约束）
    /// </summary>
    public List<HirTraitConstraint> Constraints { get; init; } = [];
}

/// <summary>
/// 类型参数上的 trait/ability 约束
/// </summary>
public sealed record HirTraitConstraint
{
    /// <summary>
    /// 约束符号（TraitSymbol / EffectSymbol）
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 约束名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 模块路径（可选）
    /// </summary>
    public List<string> ModulePath { get; init; } = [];

    /// <summary>
    /// 约束类型参数
    /// </summary>
    public List<HirTypeArg> TypeArgs { get; init; } = [];
}

/// <summary>
/// HIR 类型参数实参
/// </summary>
public sealed record HirTypeArg
{
    /// <summary>
    /// 类型 ID
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// 可读文本（用于调试输出）
    /// </summary>
    public string DisplayText { get; init; } = "";
}

/// <summary>
/// let 绑定（不可变）
/// </summary>
public sealed record HirVal : HirDecl
{
    public HirVal() : base(HirKind.Val) { }

    /// <summary>
    /// 模式绑定（支持解构）
    /// </summary>
    public HirPattern Pattern { get; init; } = null!;

    /// <summary>
    /// 类型注解（可选）
    /// </summary>
    public TypeId TypeAnnotation { get; init; } = TypeId.None;

    /// <summary>
    /// 初始化表达式
    /// </summary>
    public HirNode Initializer { get; init; } = null!;

    /// <summary>
    /// True when this value is a compile-time binding.
    /// </summary>
    public bool IsComptime { get; init; }

    public override string ToString() => $"Val({Name})";
}

/// <summary>
/// var 绑定（可变）
/// </summary>
public sealed record HirVarDecl : HirDecl
{
    public HirVarDecl() : base(HirKind.Var) { }

    /// <summary>
    /// 模式绑定（支持解构）
    /// </summary>
    public HirPattern Pattern { get; init; } = null!;

    /// <summary>
    /// 类型注解（可选）
    /// </summary>
    public TypeId TypeAnnotation { get; init; } = TypeId.None;

    /// <summary>
    /// 初始化表达式
    /// </summary>
    public HirNode Initializer { get; init; } = null!;

    public override string ToString() => $"Var({Name})";
}

/// <summary>
/// ADT 定义
/// </summary>
public sealed record HirAdt : HirDecl
{
    public HirAdt() : base(HirKind.Type) { }

    /// <summary>
    /// 类型参数列表
    /// </summary>
    public List<HirTypeParam> TypeParams { get; init; } = [];

    /// <summary>
    /// 构造器列表
    /// </summary>
    public List<HirCtor> Constructors { get; init; } = [];

    /// <summary>
    /// Gets the target type for a type alias declaration.
    /// </summary>
    public TypeId AliasTarget { get; init; } = TypeId.None;

    /// <summary>
    /// 是否是枚举（所有构造器无参数）
    /// </summary>
    public bool IsEnum { get; init; }

    /// <summary>
    /// 是否是记录（单构造器，命名字段）
    /// </summary>
    public bool IsRecord { get; init; }

    public override string ToString() => $"Adt({Name})";
}

/// <summary>
/// ADT 构造器
/// </summary>
public sealed record HirCtor
{
    /// <summary>
    /// 构造器名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 字段列表
    /// </summary>
    public List<HirField> Fields { get; init; } = [];

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// ADT 字段
/// </summary>
public sealed record HirField
{
    /// <summary>
    /// 字段名（可选，元组风格字段无名称）
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 字段类型
    /// </summary>
    public TypeId TypeId { get; init; } = TypeId.None;

    /// <summary>
    /// 字段符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;
}

/// <summary>Nominal compile-time effect marker.</summary>
public sealed record HirEffect : HirDecl
{
    public HirEffect() : base(HirKind.Type) { }

    public override string ToString() => $"Effect({Name})";
}

/// <summary>
/// Trait 定义
/// </summary>
public sealed record HirTrait : HirDecl
{
    public HirTrait() : base(HirKind.Type) { }

    /// <summary>
    /// 类型参数列表
    /// </summary>
    public List<HirTypeParam> TypeParams { get; init; } = [];

    /// <summary>
    /// 关联类型列表
    /// </summary>
    public List<HirAssocType> AssociatedTypes { get; init; } = [];

    /// <summary>
    /// 方法签名列表
    /// </summary>
    public List<HirFunc> Methods { get; init; } = [];

    /// <summary>
    /// 超级 trait 约束
    /// </summary>
    public List<SymbolId> SuperTraits { get; init; } = [];

    public override string ToString() => $"Trait({Name})";
}

/// <summary>
/// 关联类型
/// </summary>
public sealed record HirAssocType
{
    /// <summary>
    /// 类型名
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 默认类型（可选）
    /// </summary>
    public TypeId DefaultType { get; init; } = TypeId.None;
}

/// <summary>
/// Trait 实现
/// </summary>
public sealed record HirImpl : HirDecl
{
    public HirImpl() : base(HirKind.Type) { }

    /// <summary>
    /// 实现的 Trait
    /// </summary>
    public SymbolId TraitId { get; init; } = SymbolId.None;

    /// <summary>
    /// 实现类型
    /// </summary>
    public TypeId ImplementingType { get; init; } = TypeId.None;

    /// <summary>
    /// 方法实现列表
    /// </summary>
    public List<HirFunc> Methods { get; init; } = [];

    /// <summary>
    /// Gets the structured implementation metadata collected during name resolution.
    /// </summary>
    public ImplSymbol? ImplMetadata { get; init; }

    public override string ToString() => $"Impl({TraitId} for {ImplementingType})";
}

/// <summary>
/// 类型别名
/// </summary>
public sealed record HirTypeAlias : HirDecl
{
    public HirTypeAlias() : base(HirKind.Type) { }

    /// <summary>
    /// 类型参数
    /// </summary>
    public List<HirTypeParam> TypeParams { get; init; } = [];

    /// <summary>
    /// 目标类型
    /// </summary>
    public TypeId TargetType { get; init; } = TypeId.None;

    public override string ToString() => $"TypeAlias({Name})";
}
