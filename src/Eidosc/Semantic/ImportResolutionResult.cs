using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Semantic;

/// <summary>
/// 导入解析结果
/// </summary>
public sealed record ImportResolutionResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// 导入的符号列表
    /// </summary>
    public List<ImportedSymbol> ImportedSymbols { get; init; } = [];

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 错误位置
    /// </summary>
    public SourceSpan? ErrorSpan { get; init; }

    /// <summary>
    /// 创建成功结果
    /// </summary>
    public static ImportResolutionResult Success(List<ImportedSymbol> symbols)
        => new() { IsSuccess = true, ImportedSymbols = symbols };

    /// <summary>
    /// 创建失败结果
    /// </summary>
    public static ImportResolutionResult Error(string message, SourceSpan span)
        => new() { IsSuccess = false, ErrorMessage = message, ErrorSpan = span };
}
