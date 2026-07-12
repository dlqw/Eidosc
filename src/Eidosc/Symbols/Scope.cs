namespace Eidosc.Symbols;

/// <summary>
/// 作用域
/// </summary>
public sealed class Scope
{
    private readonly Dictionary<string, SymbolId> _bindings = new();
    private readonly Dictionary<string, List<SymbolId>> _functionOverloads = new();
    private readonly Dictionary<string, SymbolId> _types = new();
    private readonly Dictionary<string, SymbolId> _traits = new();
    private readonly Dictionary<string, SymbolId> _abilities = new();
    private readonly Dictionary<string, SymbolId> _constructors = new();

    /// <summary>
    /// 父作用域
    /// </summary>
    public Scope? Parent { get; }

    /// <summary>
    /// 作用域深度
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// 作用域类型
    /// </summary>
    public ScopeKind Kind { get; init; } = ScopeKind.Block;

    public Scope(Scope? parent = null)
    {
        Parent = parent;
        Depth = parent?.Depth + 1 ?? 0;
    }

    /// <summary>
    /// 绑定变量/函数符号
    /// </summary>
    public bool BindValue(string name, SymbolId symbol)
    {
        if (_bindings.ContainsKey(name) || _functionOverloads.ContainsKey(name))
            return false;
        _bindings[name] = symbol;
        return true;
    }

    public bool BindFunction(string name, SymbolId symbol)
    {
        if (_bindings.TryGetValue(name, out var existing) &&
            !_functionOverloads.ContainsKey(name))
        {
            return existing == symbol;
        }

        if (!_functionOverloads.TryGetValue(name, out var overloads))
        {
            overloads = [];
            _functionOverloads[name] = overloads;
        }

        if (!overloads.Contains(symbol))
        {
            overloads.Add(symbol);
        }

        if (!_bindings.ContainsKey(name))
        {
            _bindings[name] = symbol;
        }

        return true;
    }

    /// <summary>
    /// 绑定类型符号
    /// </summary>
    public bool BindType(string name, SymbolId symbol)
    {
        if (_types.ContainsKey(name))
            return false;
        _types[name] = symbol;
        return true;
    }

    /// <summary>
    /// 绑定 trait 符号
    /// </summary>
    public bool BindTrait(string name, SymbolId symbol)
    {
        if (_traits.ContainsKey(name))
            return false;
        _traits[name] = symbol;
        return true;
    }

    /// <summary>
    /// 绑定能力符号
    /// </summary>
    public bool BindEffect(string name, SymbolId symbol)
    {
        if (_abilities.ContainsKey(name))
            return false;
        _abilities[name] = symbol;
        return true;
    }

    /// <summary>
    /// 绑定构造器符号
    /// </summary>
    public bool BindConstructor(string name, SymbolId symbol)
    {
        if (_constructors.ContainsKey(name))
            return false;
        _constructors[name] = symbol;
        return true;
    }

    /// <summary>
    /// 查找变量/函数
    /// </summary>
    public SymbolId? LookupValue(string name)
    {
        if (_bindings.TryGetValue(name, out var id))
            return id;
        return Parent?.LookupValue(name);
    }

    public IReadOnlyList<SymbolId> LookupValueCandidates(string name)
    {
        if (_functionOverloads.TryGetValue(name, out var overloads))
        {
            return overloads;
        }

        if (_bindings.TryGetValue(name, out var id))
        {
            return [id];
        }

        return Parent?.LookupValueCandidates(name) ?? [];
    }

    public IReadOnlyList<SymbolId> LookupLocalValueCandidates(string name)
    {
        if (_functionOverloads.TryGetValue(name, out var overloads))
        {
            return overloads;
        }

        if (_bindings.TryGetValue(name, out var id))
        {
            return [id];
        }

        return [];
    }

    /// <summary>
    /// 查找类型
    /// </summary>
    public SymbolId? LookupType(string name)
    {
        if (_types.TryGetValue(name, out var id))
            return id;
        return Parent?.LookupType(name);
    }

    /// <summary>
    /// 查找 trait
    /// </summary>
    public SymbolId? LookupTrait(string name)
    {
        if (_traits.TryGetValue(name, out var id))
            return id;
        return Parent?.LookupTrait(name);
    }

    /// <summary>
    /// 查找能力
    /// </summary>
    public SymbolId? LookupEffect(string name)
    {
        if (_abilities.TryGetValue(name, out var id))
            return id;
        return Parent?.LookupEffect(name);
    }

    /// <summary>
    /// 查找构造器
    /// </summary>
    public SymbolId? LookupConstructor(string name)
    {
        if (_constructors.TryGetValue(name, out var id))
            return id;
        return Parent?.LookupConstructor(name);
    }

    /// <summary>
    /// 获取当前作用域的所有绑定
    /// </summary>
    public IReadOnlyDictionary<string, SymbolId> GetLocalBindings() => _bindings;

    public IReadOnlyDictionary<string, IReadOnlyList<SymbolId>> GetLocalFunctionOverloads() =>
        _functionOverloads.ToDictionary(
            static entry => entry.Key,
            static entry => (IReadOnlyList<SymbolId>)entry.Value,
            StringComparer.Ordinal);

    /// <summary>
    /// 获取当前作用域的所有类型
    /// </summary>
    public IReadOnlyDictionary<string, SymbolId> GetLocalTypes() => _types;

    public IReadOnlyDictionary<string, SymbolId> GetLocalTraits() => _traits;

    public IReadOnlyDictionary<string, SymbolId> GetLocalAbilities() => _abilities;

    public IReadOnlyDictionary<string, SymbolId> GetLocalConstructors() => _constructors;
}

/// <summary>
/// 作用域类型
/// </summary>
public enum ScopeKind
{
    /// <summary>
    /// 模块级别
    /// </summary>
    Module,

    /// <summary>
    /// 函数体
    /// </summary>
    Function,

    /// <summary>
    /// 块
    /// </summary>
    Block,

    /// <summary>
    /// 模式匹配分支
    /// </summary>
    PatternBranch,

    /// <summary>
    /// Lambda 表达式
    /// </summary>
    Lambda
}
