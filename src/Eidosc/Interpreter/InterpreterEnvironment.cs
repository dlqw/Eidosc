namespace Eidosc.Interpreter;

/// <summary>
/// 解释器环境：名称 → 值映射，支持嵌套作用域
/// </summary>
public sealed class InterpreterEnvironment
{
    private readonly Dictionary<string, RuntimeValue> _bindings = new(StringComparer.Ordinal);
    private readonly InterpreterEnvironment? _parent;

    public InterpreterEnvironment(InterpreterEnvironment? parent = null)
    {
        _parent = parent;
    }

    public RuntimeValue? Lookup(string name)
    {
        if (_bindings.TryGetValue(name, out var value))
            return value;
        return _parent?.Lookup(name);
    }

    public void Bind(string name, RuntimeValue value)
    {
        _bindings[name] = value;
    }

    public void Set(string name, RuntimeValue value)
    {
        if (_bindings.ContainsKey(name))
        {
            _bindings[name] = value;
            return;
        }

        if (_parent != null)
        {
            _parent.Set(name, value);
            return;
        }

        _bindings[name] = value;
    }

    public InterpreterEnvironment PushScope() => new(this);

    public IEnumerable<KeyValuePair<string, RuntimeValue>> AllBindings
    {
        get
        {
            foreach (var kvp in _bindings)
                yield return kvp;
            if (_parent != null)
            {
                foreach (var kvp in _parent.AllBindings)
                    yield return kvp;
            }
        }
    }
}
