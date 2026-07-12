namespace Eidosc.Doc;

/// <summary>
/// 结构化文档注释（从 /// 或 /** */ 提取）
/// </summary>
public sealed record DocComment
{
    /// <summary>
    /// 摘要文本（第一段非标签内容）
    /// </summary>
    public string Summary { get; init; } = "";

    /// <summary>
    /// @param 标签：参数名 → 描述
    /// </summary>
    public List<DocParamTag> Params { get; init; } = [];

    /// <summary>
    /// @return 标签描述
    /// </summary>
    public string? Returns { get; init; }

    /// <summary>
    /// @example 标签列表
    /// </summary>
    public List<string> Examples { get; init; } = [];

    /// <summary>
    /// @deprecated 标签（弃用说明）
    /// </summary>
    public string? Deprecated { get; init; }

    /// <summary>
    /// 原始文本（用于无结构解析时的回退）
    /// </summary>
    public string RawText { get; init; } = "";

    public static DocComment Empty { get; } = new();
}

public sealed record DocParamTag
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}
