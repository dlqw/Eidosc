namespace Eidosc.Borrow;

/// <summary>
/// 借用检查结果
/// </summary>
public sealed class BorrowCheckResult
{
    /// <summary>
    /// 函数名
    /// </summary>
    public string FunctionName { get; init; } = "";

    /// <summary>
    /// 函数符号 ID
    /// </summary>
    public SymbolId FunctionSymbolId { get; init; } = SymbolId.None;

    /// <summary>
    /// 活性分析器
    /// </summary>
    public LivenessAnalyzer? LivenessAnalyzer { get; init; }

    /// <summary>
    /// 仿射类型检查器
    /// </summary>
    public AffineTypeChecker? AffineTypeChecker { get; init; }

    /// <summary>
    /// 借用检查器
    /// </summary>
    public BorrowChecker? BorrowChecker { get; init; }

    /// <summary>
    /// 借用签名
    /// </summary>
    public LoanSignature? LoanSignature { get; init; }

    /// <summary>
    /// 调用约束验证器
    /// </summary>
    public LoanConstraintVerifier? LoanConstraintVerifier { get; init; }

    /// <summary>
    /// 调用约束验证结果
    /// </summary>
    public List<LoanConstraintResult> LoanConstraintResults { get; init; } = [];

    /// <summary>
    /// Perceus 分析器
    /// </summary>
    public PerceusAnalyzer? PerceusAnalyzer { get; init; }

    /// <summary>
    /// Restored or directly supplied Perceus hints.
    /// </summary>
    public PerceusHints? PerceusHints { get; init; }

    /// <summary>
    /// Reuse 分析器（drop-then-alloc 内存复用）
    /// </summary>
    public ReuseAnalyzer? ReuseAnalyzer { get; init; }

    /// <summary>
    /// Restored or directly supplied reuse hints.
    /// </summary>
    public ReuseHints? ReuseHints { get; init; }

    /// <summary>
    /// Stack Promotion 分析器（heap-to-stack 提升）
    /// </summary>
    public StackPromotionAnalyzer? StackPromotionAnalyzer { get; init; }

    /// <summary>
    /// Restored or directly supplied stack-promotion hints.
    /// </summary>
    public StackPromotionHints? StackPromotionHints { get; init; }

    /// <summary>
    /// 统一栈提升分析器（ADT + 闭包，字段级逃逸）
    /// </summary>
    public UnifiedStackPromotionAnalyzer? UnifiedStackPromotionAnalyzer { get; init; }

    /// <summary>
    /// Restored or directly supplied unified stack-promotion hints.
    /// </summary>
    public UnifiedStackPromotionHints? UnifiedStackPromotionHints { get; init; }

    /// <summary>
    /// 是否有错误
    /// </summary>
    public bool HasErrors =>
        (AffineTypeChecker?.Diagnostics.Count ?? 0) > 0 ||
        (BorrowChecker?.Diagnostics.Count ?? 0) > 0 ||
        (LoanConstraintVerifier?.Diagnostics.Count ?? 0) > 0 ||
        (LoanConstraintResults.Count > 0);
}

/// <summary>
/// Identifies a borrow-check result by compiler function identity.
/// </summary>
/// <remarks>
/// A valid <see cref="SymbolId" /> is preferred. The function name is retained only as a
/// compatibility fallback for synthetic or legacy MIR functions that do not carry a symbol.
/// </remarks>
public readonly record struct BorrowFunctionKey
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BorrowFunctionKey" /> struct.
    /// </summary>
    /// <param name="symbolId">The resolved function symbol.</param>
    /// <param name="name">The lowered function name.</param>
    /// <param name="disambiguator">The duplicate-name disambiguator.</param>
    private BorrowFunctionKey(SymbolId symbolId, string name, int disambiguator)
    {
        SymbolId = symbolId;
        Name = name;
        Disambiguator = disambiguator;
    }

    /// <summary>
    /// Gets the resolved function symbol.
    /// </summary>
    public SymbolId SymbolId { get; }

    /// <summary>
    /// Gets the lowered function name used when no symbol is available.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the suffix used to keep fallback name keys unique.
    /// </summary>
    public int Disambiguator { get; }

    /// <summary>
    /// Gets a value that indicates whether this key is backed by a resolved symbol.
    /// </summary>
    public bool IsSymbolBacked => SymbolId.IsValid;

    /// <summary>
    /// Gets the stable text form retained for compatibility with older consumers.
    /// </summary>
    public string StableText
    {
        get
        {
            var baseText = SymbolId.IsValid
                ? $"sym:{SymbolId.Value}"
                : !string.IsNullOrWhiteSpace(Name)
                    ? $"name:{Name}"
                    : "anon:<unknown>";

            return Disambiguator > 0 ? $"{baseText}#{Disambiguator}" : baseText;
        }
    }

    /// <summary>
    /// Creates a key for a MIR function identity.
    /// </summary>
    /// <param name="functionName">The lowered function name.</param>
    /// <param name="symbolId">The resolved function symbol.</param>
    /// <returns>A key that prefers the resolved symbol when available.</returns>
    public static BorrowFunctionKey From(string? functionName, SymbolId symbolId)
    {
        return new BorrowFunctionKey(symbolId, functionName ?? string.Empty, disambiguator: 0);
    }

    /// <summary>
    /// Creates a key with a duplicate fallback suffix.
    /// </summary>
    /// <param name="disambiguator">The duplicate-name disambiguator.</param>
    /// <returns>A key with the same identity and the requested disambiguator.</returns>
    public BorrowFunctionKey WithDisambiguator(int disambiguator)
    {
        return new BorrowFunctionKey(SymbolId, Name, disambiguator);
    }

    /// <inheritdoc />
    public override string ToString() => StableText;
}

/// <summary>
/// 模块借用检查结果
/// </summary>
public sealed class ModuleBorrowCheckResult
{
    private readonly Dictionary<SymbolId, BorrowFunctionKey> _resultKeyBySymbol = [];
    private readonly Dictionary<string, BorrowFunctionKey> _resultKeyByName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the borrow-check results keyed by structured function identity.
    /// </summary>
    public Dictionary<BorrowFunctionKey, BorrowCheckResult> ResultsByFunctionKey { get; init; } = [];

    /// <summary>
    /// 每个函数的借用检查结果。
    /// </summary>
    /// <remarks>
    /// This string-keyed view is retained for compatibility. New code should use
    /// <see cref="ResultsByFunctionKey" /> or <see cref="TryGetFunctionResult(SymbolId, string?, out BorrowCheckResult?)" />.
    /// </remarks>
    public Dictionary<string, BorrowCheckResult> FunctionResults { get; init; } = [];

    public void AddResult(BorrowCheckResult result)
    {
        var baseKey = BorrowFunctionKey.From(result.FunctionName, result.FunctionSymbolId);
        var key = baseKey;
        var suffix = 1;
        while (ResultsByFunctionKey.ContainsKey(key))
        {
            key = baseKey.WithDisambiguator(suffix++);
        }

        ResultsByFunctionKey[key] = result;
        FunctionResults[key.StableText] = result;

        if (result.FunctionSymbolId.IsValid)
        {
            _resultKeyBySymbol[result.FunctionSymbolId] = key;
        }

        if (string.IsNullOrWhiteSpace(result.FunctionName))
        {
            return;
        }

        if (_ambiguousNames.Contains(result.FunctionName))
        {
            return;
        }

        if (_resultKeyByName.TryGetValue(result.FunctionName, out var existingKey) &&
            !existingKey.Equals(key))
        {
            _resultKeyByName.Remove(result.FunctionName);
            _ambiguousNames.Add(result.FunctionName);
            return;
        }

        _resultKeyByName[result.FunctionName] = key;
    }

    public bool TryGetFunctionResult(SymbolId symbolId, string? functionName, out BorrowCheckResult? result)
    {
        if (symbolId.IsValid &&
            _resultKeyBySymbol.TryGetValue(symbolId, out var symbolKey) &&
            ResultsByFunctionKey.TryGetValue(symbolKey, out result))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(functionName) &&
            !_ambiguousNames.Contains(functionName) &&
            _resultKeyByName.TryGetValue(functionName, out var nameKey) &&
            ResultsByFunctionKey.TryGetValue(nameKey, out result))
        {
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Gets a borrow-check result by structured function identity.
    /// </summary>
    /// <param name="key">The function identity.</param>
    /// <param name="result">When this method returns, contains the result if found.</param>
    /// <returns><see langword="true" /> if the result was found; otherwise, <see langword="false" />.</returns>
    public bool TryGetFunctionResult(BorrowFunctionKey key, out BorrowCheckResult? result)
    {
        return ResultsByFunctionKey.TryGetValue(key, out result);
    }

    /// <summary>
    /// 是否有错误
    /// </summary>
    public bool HasErrors => ResultsByFunctionKey.Values.Any(r => r.HasErrors);
}
