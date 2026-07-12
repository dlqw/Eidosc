namespace Eidosc.Mir.Closure;

/// <summary>
/// Handler 环境链 - 管理嵌套的效应处理器
/// </summary>
public sealed class HandlerEnvironment
{
    private readonly Stack<string> _handlerStack = new();

    /// <summary>
    /// Handler 链
    /// </summary>
    public IEnumerable<string> Chain => _handlerStack;

    /// <summary>
    /// 添加 Handler
    /// </summary>
    public void Push(string handlerName)
    {
        _handlerStack.Push(handlerName);
    }

    /// <summary>
    /// 移除 Handler
    /// </summary>
    public string Pop()
    {
        return _handlerStack.Pop();
    }

    /// <summary>
    /// 查找 Handler
    /// </summary>
    public string? FindHandler(Func<string, bool> predicate)
    {
        return _handlerStack.FirstOrDefault(predicate);
    }

    /// <summary>
    /// 是否为空
    /// </summary>
    public bool IsEmpty => _handlerStack.Count == 0;

    /// <summary>
    /// 深度
    /// </summary>
    public int Depth => _handlerStack.Count;
}
