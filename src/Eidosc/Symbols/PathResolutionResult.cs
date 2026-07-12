namespace Eidosc.Symbols;

/// <summary>
/// 路径解析结果
/// </summary>
public sealed record PathResolutionResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// 解析到的符号 ID
    /// </summary>
    public SymbolId SymbolId { get; init; }

    /// <summary>
    /// 解析类型
    /// </summary>
    public ResolutionKind Kind { get; init; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 创建成功结果
    /// </summary>
    public static PathResolutionResult Found(SymbolId id, ResolutionKind kind)
        => new() { IsSuccess = true, SymbolId = id, Kind = kind };

    /// <summary>
    /// 创建失败结果
    /// </summary>
    public static PathResolutionResult NotFound(string message)
        => new() { IsSuccess = false, ErrorMessage = message };
}

/// <summary>
/// 解析结果类型
/// </summary>
public enum ResolutionKind
{
    /// <summary>
    /// 变量/函数
    /// </summary>
    Value,

    /// <summary>
    /// 类型 (ADT/Trait)
    /// </summary>
    Type,

    /// <summary>
    /// 构造器
    /// </summary>
    Constructor,

    /// <summary>
    /// 模块
    /// </summary>
    Module,

    /// <summary>
    /// 能力 (Effect)
    /// </summary>
    Effect,

    /// <summary>
    /// Compiler-only proof.
    /// </summary>
    Proof
}
