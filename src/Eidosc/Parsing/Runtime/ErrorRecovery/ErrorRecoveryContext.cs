namespace Eidosc.ErrorRecovery;

/// <summary>
/// 错误恢复上下文 - 管理各阶段的错误恢复配置和状态
/// </summary>
public sealed class ErrorRecoveryContext
{
    /// <summary>
    /// 最大错误数量限制
    /// </summary>
    public int MaxErrors { get; init; } = 100;

    /// <summary>
    /// 最大连续错误数量（达到此数量后停止）
    /// </summary>
    public int MaxConsecutiveErrors { get; init; } = 10;

    /// <summary>
    /// 当前错误计数
    /// </summary>
    public int ErrorCount { get; private set; }

    /// <summary>
    /// 连续错误计数（成功处理后重置）
    /// </summary>
    public int ConsecutiveErrors { get; private set; }

    /// <summary>
    /// 是否已达到错误限制
    /// </summary>
    public bool HasReachedLimit => ErrorCount >= MaxErrors || ConsecutiveErrors >= MaxConsecutiveErrors;

    /// <summary>
    /// 记录一个错误
    /// </summary>
    public void RecordError()
    {
        ErrorCount++;
        ConsecutiveErrors++;
    }

    /// <summary>
    /// 记录成功处理（重置连续错误计数）
    /// </summary>
    public void RecordSuccess()
    {
        ConsecutiveErrors = 0;
    }

    /// <summary>
    /// 重置状态
    /// </summary>
    public void Reset()
    {
        ErrorCount = 0;
        ConsecutiveErrors = 0;
    }

    /// <summary>
    /// 创建默认的 Lexer 错误恢复上下文
    /// </summary>
    public static ErrorRecoveryContext ForLexer() => new()
    {
        MaxErrors = 100,
        MaxConsecutiveErrors = 10
    };

    /// <summary>
    /// 创建默认的 Parser 错误恢复上下文
    /// </summary>
    public static ErrorRecoveryContext ForParser() => new()
    {
        MaxErrors = 100,
        MaxConsecutiveErrors = 10
    };

    /// <summary>
    /// 创建默认的类型推断错误恢复上下文
    /// </summary>
    public static ErrorRecoveryContext ForTypeInference() => new()
    {
        MaxErrors = 100,
        MaxConsecutiveErrors = 20 // 类型推断可能有更多级联错误
    };

    /// <summary>
    /// 创建默认的借用检查错误恢复上下文
    /// </summary>
    public static ErrorRecoveryContext ForBorrowCheck() => new()
    {
        MaxErrors = 100,
        MaxConsecutiveErrors = 20
    };
}
