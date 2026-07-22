namespace Eidosc.Symbols;

/// <summary>
/// 符号种类
/// </summary>
public enum SymbolKind
{
    /// <summary>
    /// 变量（let 绑定）
    /// </summary>
    Variable,

    /// <summary>
    /// 函数
    /// </summary>
    Function,

    /// <summary>
    /// 类型参数
    /// </summary>
    TypeParameter,

    /// <summary>
    /// ADT 类型
    /// </summary>
    Adt,

    /// <summary>
    /// 类型别名
    /// </summary>
    TypeAlias,

    /// <summary>
    /// ADT 构造器
    /// </summary>
    Constructor,

    /// <summary>
    /// 能力
    /// </summary>
    Effect,

    /// <summary>
    /// Trait
    /// </summary>
    Trait,

    /// <summary>
    /// 模块
    /// </summary>
    Module,

    /// <summary>
    /// 字段
    /// </summary>
    Field,

    /// <summary>
    /// Trait or instance associated type.
    /// </summary>
    AssociatedType,

    /// <summary>
    /// Trait or instance associated constant.
    /// </summary>
    AssociatedConst,

    /// <summary>
    /// Trait 实现 (impl 块)
    /// </summary>
    Impl,

    /// <summary>
    /// Compiler-only proof declaration.
    /// </summary>
    Proof
}
