using Eidosc.Symbols;
namespace Eidosc.Semantic;

/// <summary>
/// 导入的符号
/// </summary>
public sealed record ImportedSymbol
{
    /// <summary>
    /// 本地名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; }

    /// <summary>
    /// 解析类型
    /// </summary>
    public ResolutionKind Kind { get; init; }

    /// <summary>
    /// 是否有别名
    /// </summary>
    public bool IsAliased { get; init; }

    /// <summary>
    /// 是否由普通模块导入附带引入的短名。
    /// </summary>
    public bool IsImplicitModuleMember { get; init; }

    /// <summary>
    /// 是否为模块导入附带暴露的 trait 方法。
    /// </summary>
    public bool IsTraitMethod { get; init; }
}
