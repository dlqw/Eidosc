namespace Eidosc.Borrow;

/// <summary>
/// 部分借用分析结果
/// </summary>
public sealed class PartialBorrowResult
{
    /// <summary>
    /// 被借用的字段
    /// </summary>
    public HashSet<string> BorrowedFields { get; init; } = [];

    /// <summary>
    /// 仍然可用的字段
    /// </summary>
    public HashSet<string> RemainingFields { get; init; } = [];
}

/// <summary>
/// 结构体借用分析器 - 支持部分借用
/// </summary>
public sealed class StructBorrowAnalyzer
{
    private HashSet<string> _allFields = [];
    private HashSet<string> _borrowedFields = [];

    /// <summary>
    /// 设置所有字段
    /// </summary>
    public StructBorrowAnalyzer SetFields(IEnumerable<string> fields)
    {
        _allFields = [.. fields];
        return this;
    }

    /// <summary>
    /// 分析部分借用
    /// </summary>
    public PartialBorrowResult AnalyzePartialBorrow(string fieldName)
    {
        _borrowedFields.Add(fieldName);
        return new PartialBorrowResult
        {
            BorrowedFields = [.. _borrowedFields],
            RemainingFields = [.. _allFields.Except(_borrowedFields)]
        };
    }

    /// <summary>
    /// 分析完整借用
    /// </summary>
    public PartialBorrowResult AnalyzeFullBorrow()
    {
        _borrowedFields = [.. _allFields];
        return new PartialBorrowResult
        {
            BorrowedFields = [.. _allFields],
            RemainingFields = []
        };
    }

    /// <summary>
    /// 检查字段是否可用
    /// </summary>
    public bool IsFieldAvailable(string fieldName)
    {
        return !_borrowedFields.Contains(fieldName);
    }
}
