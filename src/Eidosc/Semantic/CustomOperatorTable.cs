using Eidosc.Symbols;

namespace Eidosc.Semantic;

/// <summary>
/// 用户定义操作符注册表。
/// 存储操作符符号到 (fixity, precedence, functionName) 的映射。
/// </summary>
public sealed class CustomOperatorTable
{
    private readonly Dictionary<string, CustomOperatorEntry> _operators = new(StringComparer.Ordinal);

    public void Register(string symbol, CustomOperatorFixity fixity, int precedence, string functionName)
    {
        _operators[symbol] = new CustomOperatorEntry(symbol, fixity, precedence, functionName);
    }

    public bool TryGetOperator(string symbol, out CustomOperatorEntry entry)
    {
        return _operators.TryGetValue(symbol, out entry!);
    }

    public IReadOnlyCollection<CustomOperatorEntry> GetAllOperators() => _operators.Values;

    public bool IsRegistered(string symbol) => _operators.ContainsKey(symbol);

    /// <summary>
    /// 合并另一个操作符表（导入时使用）
    /// </summary>
    public void MergeFrom(CustomOperatorTable other)
    {
        foreach (var entry in other._operators)
            _operators.TryAdd(entry.Key, entry.Value);
    }
}

public sealed record CustomOperatorEntry
{
    public string Symbol { get; init; }
    public CustomOperatorFixity Fixity { get; init; }
    public int Precedence { get; init; }
    public string FunctionName { get; init; }

    public CustomOperatorEntry(string symbol, CustomOperatorFixity fixity, int precedence, string functionName)
    {
        Symbol = symbol;
        Fixity = fixity;
        Precedence = precedence;
        FunctionName = functionName;
    }
}

public enum CustomOperatorFixity
{
    InfixL,
    InfixR,
    Prefix,
    Postfix
}
