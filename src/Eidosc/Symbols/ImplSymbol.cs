namespace Eidosc.Symbols;

public sealed record ImplTypeArgTraitRequirement
{
    /// <summary>
    /// implementing type 的第几个类型实参需要满足 trait。
    /// </summary>
    public int TypeArgIndex { get; init; }

    /// <summary>
    /// 需要满足的 trait。
    /// </summary>
    public SymbolId Trait { get; init; }

    /// <summary>
    /// trait 名称（用于诊断与回退解析）。
    /// </summary>
    public string TraitName { get; init; } = "";

    /// <summary>
    /// trait 类型实参（文本归一化）。
    /// </summary>
    public List<string> TraitTypeArgs { get; init; } = [];

    /// <summary>
    /// trait 类型实参的结构化 lookup key。
    /// </summary>
    public List<ImplTypeRefKey> TraitTypeArgKeys { get; init; } = [];
}

/// <summary>
/// Trait 实现符号 - 表示一个类型实现了某个 Trait
/// </summary>
public sealed record ImplSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Impl;

    /// <summary>
    /// 实现的 Trait
    /// </summary>
    public SymbolId Trait { get; init; }

    /// <summary>
    /// 实现该 Trait 的具体类型
    /// </summary>
    public TypeId ImplementingType { get; init; }

    /// <summary>
    /// implementing type 的 canonical 形状（展开 alias 后）
    /// </summary>
    public string CanonicalImplementingType { get; init; } = "";

    /// <summary>
    /// implementing type 的声明头文本（未 canonicalize）
    /// </summary>
    public string ImplementingTypeDisplay { get; init; } = "";

    /// <summary>
    /// implementing type 的结构化 lookup key。
    /// </summary>
    public ImplTypeRefKey ImplementingTypeKey { get; init; } = ImplTypeRefKey.Empty;

    /// <summary>
    /// 实现的方法列表 (SymbolId 指向 FuncSymbol)
    /// </summary>
    public List<SymbolId> Methods { get; init; } = [];

    public List<SymbolId> AssociatedTypes { get; init; } = [];

    public List<SymbolId> AssociatedConsts { get; init; } = [];

    /// <summary>
    /// True when this impl contributes callable runtime methods.
    /// Proof-only impl registrations are used for trait proof obligations and
    /// must not participate in MIR trait dispatch.
    /// </summary>
    public bool HasRuntimeMethods => Methods.Count > 0;

    /// <summary>
    /// Maps trait method symbols to their concrete implementation method symbols.
    /// </summary>
    public Dictionary<SymbolId, SymbolId> TraitMethodImplementations { get; init; } = [];

    /// <summary>
    /// Trait 类型实参（文本归一化）
    /// </summary>
    public List<string> TraitTypeArgs { get; init; } = [];

    /// <summary>
    /// Trait 类型实参的结构化 lookup key。
    /// </summary>
    public List<ImplTypeRefKey> TraitTypeArgKeys { get; init; } = [];

    /// <summary>
    /// Trait 类型实参的 canonical 形状（展开 alias 后，用于检测重叠 impl 头）
    /// </summary>
    public List<string> CanonicalTraitTypeArgs { get; init; } = [];

    /// <summary>
    /// Trait 类型实参 canonical 形状的结构化 lookup key。
    /// </summary>
    public List<ImplTypeRefKey> CanonicalTraitTypeArgKeys { get; init; } = [];

    /// <summary>
    /// Trait 类型实参的注册期结构化 shape。
    /// </summary>
    public List<ImplTypeShapeNode> TraitTypeArgShapes { get; init; } = [];

    /// <summary>
    /// implementing type 的注册期结构化 shape。
    /// </summary>
    public ImplTypeShapeNode? ImplementingTypeShape { get; init; }

    /// <summary>
    /// 类型参数映射 (用于泛型 impl)
    /// </summary>
    public Dictionary<TypeId, TypeId> TypeArguments { get; init; } = new();

    /// <summary>
    /// implementing type 的类型实参约束。
    /// 例如 `@impl(Eq) func eq[T: Eq]: Option[T] -> ...`
    /// 会记录 `Option` 的第 1 个类型实参需要满足 `Eq`。
    /// </summary>
    public List<ImplTypeArgTraitRequirement> ImplementingTypeRequirements { get; init; } = [];

    /// <summary>
    /// True when this impl was auto-derived from a supertrait chain
    /// (e.g., @impl(Ord) automatically generates an Eq impl).
    /// Auto-derived impls have lower priority than explicit @impl annotations.
    /// </summary>
    public bool IsAutoDerived { get; init; }
}
