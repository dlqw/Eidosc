namespace Eidosc.Mir.Closure;

/// <summary>
/// 扁平化结果
/// </summary>
public sealed class FlattenResult
{
    /// <summary>
    /// 捕获的变量
    /// </summary>
    public HashSet<string> CapturedVariables { get; init; } = [];

    /// <summary>
    /// 是否已扁平化
    /// </summary>
    public bool IsFlattened { get; init; }
}

/// <summary>
/// 嵌套闭包扁平化器
/// </summary>
public sealed class NestedClosureFlattener
{
    private readonly Dictionary<string, HashSet<string>> _captures = [];

    /// <summary>
    /// 添加捕获关系
    /// </summary>
    public void AddCapture(string closureName, IEnumerable<string> variables)
    {
        _captures[closureName] = [.. variables];
    }

    /// <summary>
    /// 扁平化闭包
    /// </summary>
    public FlattenResult Flatten(string closureName)
    {
        if (_captures.TryGetValue(closureName, out var vars))
        {
            return new FlattenResult
            {
                CapturedVariables = vars,
                IsFlattened = true
            };
        }

        return new FlattenResult
        {
            CapturedVariables = [],
            IsFlattened = false
        };
    }
}
