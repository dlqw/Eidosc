namespace Eidosc.Mir.Closure;

/// <summary>
/// 递归闭包解析结果
/// </summary>
public sealed class RecursiveClosureResult
{
    /// <summary>
    /// 函数名称
    /// </summary>
    public string FunctionName { get; init; } = "";

    /// <summary>
    /// 是否需要 Fix 点
    /// </summary>
    public bool RequiresFixPoint { get; init; }

    /// <summary>
    /// 自由变量列表
    /// </summary>
    public List<string> FreeVariables { get; init; } = [];
}

/// <summary>
/// 递归闭包解析器
/// </summary>
public sealed class RecursiveClosureResolver
{
    /// <summary>
    /// 检测自引用
    /// </summary>
    public bool DetectSelfReference(string paramName, IEnumerable<string> capturedVars)
    {
        return capturedVars.Contains(paramName);
    }

    /// <summary>
    /// 创建 Fix 点
    /// </summary>
    public RecursiveClosureResult CreateFixPoint(string functionName)
    {
        return new RecursiveClosureResult
        {
            FunctionName = functionName,
            RequiresFixPoint = true
        };
    }
}
