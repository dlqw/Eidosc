using Eidosc.Utils;

namespace Eidosc.Borrow;

/// <summary>
/// 参数借用模式
/// </summary>
public enum ParamBorrowMode
{
    /// <summary>
    /// 获取所有权（移动语义）
    /// </summary>
    Own,

    /// <summary>
    /// 不可变借用（共享引用）
    /// </summary>
    BorrowShared,

    /// <summary>
    /// 可变借用（独占引用）
    /// </summary>
    BorrowMutable,

    /// <summary>
    /// 复制语义（Copy 类型）
    /// </summary>
    Copy
}

/// <summary>
/// Describes how reliable a borrow signature inference result is.
/// </summary>
public enum LoanInferenceConfidence
{
    High,
    Low
}

/// <summary>
/// 参数借用要求
/// </summary>
public sealed class ParamBorrowRequirement
{
    /// <summary>
    /// 参数索引
    /// </summary>
    public int ParamIndex { get; init; }

    /// <summary>
    /// 参数名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 借用模式
    /// </summary>
    public ParamBorrowMode Mode { get; init; }

    /// <summary>
    /// 生命周期变量（用于引用类型）
    /// </summary>
    public LifetimeId Lifetime { get; init; }

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    public override string ToString() => Mode switch
    {
        ParamBorrowMode.Own => $"{Name}: own",
        ParamBorrowMode.BorrowShared => $"{Name}: &'{Lifetime.Value}",
        ParamBorrowMode.BorrowMutable => $"{Name}: &mut '{Lifetime.Value}",
        ParamBorrowMode.Copy => $"{Name}: copy",
        _ => $"{Name}: ?"
    };
}

/// <summary>
/// 返回值借用约束
/// </summary>
public sealed class ReturnBorrowConstraint
{
    /// <summary>
    /// 是否返回借用
    /// </summary>
    public bool IsBorrow { get; init; }

    /// <summary>
    /// 是否可变借用
    /// </summary>
    public bool IsMutable { get; init; }

    /// <summary>
    /// 返回值的生命周期
    /// </summary>
    public LifetimeId Lifetime { get; init; }

    /// <summary>
    /// 生命周期可能绑定的参数索引列表
    /// （表示返回值的生命周期至少与这些参数一样长）
    /// </summary>
    public List<int> BoundToParams { get; init; } = [];

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// Gets the confidence for this return borrow inference.
    /// </summary>
    public LoanInferenceConfidence Confidence { get; init; } = LoanInferenceConfidence.High;

    /// <summary>
    /// Gets internal notes that explain low-confidence inference sources.
    /// </summary>
    public List<string> InternalNotes { get; init; } = [];

    public override string ToString()
    {
        if (!IsBorrow)
            return "own";

        var boundStr = BoundToParams.Count > 0
            ? $" (bound to params: {string.Join(", ", BoundToParams)})"
            : "";

        return IsMutable
            ? $"&mut '{Lifetime.Value}{boundStr}"
            : $"&'{Lifetime.Value}{boundStr}";
    }
}

/// <summary>
/// 生命周期参数
/// </summary>
public sealed class LifetimeParam
{
    /// <summary>
    /// 生命周期 ID
    /// </summary>
    public LifetimeId Id { get; init; }

    /// <summary>
    /// 生命周期名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 生命周期约束（该生命周期必须长于这些生命周期）
    /// </summary>
    public List<LifetimeId> Outlives { get; init; } = [];

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    public override string ToString()
    {
        if (Outlives.Count == 0)
            return $"'{Name}";

        var outlivesStr = string.Join(", ", Outlives.Select(l => $"'{l.Value}"));
        return $"'{Name}: {outlivesStr}";
    }
}

/// <summary>
/// 函数借用签名 - 描述函数参数和返回值的借用约束
/// </summary>
public sealed class LoanSignature
{
    /// <summary>
    /// 函数名
    /// </summary>
    public string FunctionName { get; init; } = "";

    /// <summary>
    /// 函数符号 ID
    /// </summary>
    public SymbolId FunctionSymbol { get; init; }

    /// <summary>
    /// 生命周期参数列表
    /// </summary>
    public List<LifetimeParam> LifetimeParams { get; init; } = [];

    /// <summary>
    /// 参数借用要求列表
    /// </summary>
    public List<ParamBorrowRequirement> ParamRequirements { get; init; } = [];

    /// <summary>
    /// 返回值借用约束
    /// </summary>
    public ReturnBorrowConstraint ReturnConstraint { get; init; } = new();

    /// <summary>
    /// 生命周期约束列表（'a: 'b 形式）
    /// </summary>
    public List<LifetimeConstraint> LifetimeConstraints { get; init; } = [];

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// 获取指定参数的借用要求
    /// </summary>
    public ParamBorrowRequirement? GetParamRequirement(int paramIndex)
    {
        return ParamRequirements.FirstOrDefault(p => p.ParamIndex == paramIndex);
    }

    /// <summary>
    /// 获取所有需要所有权的参数索引
    /// </summary>
    public IEnumerable<int> GetOwnedParams()
    {
        return ParamRequirements
            .Where(p => p.Mode == ParamBorrowMode.Own)
            .Select(p => p.ParamIndex);
    }

    /// <summary>
    /// 获取所有借用参数索引
    /// </summary>
    public IEnumerable<int> GetBorrowedParams()
    {
        return ParamRequirements
            .Where(p => p.Mode is ParamBorrowMode.BorrowShared or ParamBorrowMode.BorrowMutable)
            .Select(p => p.ParamIndex);
    }

    /// <summary>
    /// 检查返回值是否是借用
    /// </summary>
    public bool ReturnsBorrow() => ReturnConstraint.IsBorrow;

    /// <summary>
    /// 检查返回值是否绑定到指定参数
    /// </summary>
    public bool ReturnBoundToParam(int paramIndex)
    {
        return ReturnConstraint.BoundToParams.Contains(paramIndex);
    }

    public override string ToString()
    {
        var lifetimeParams = LifetimeParams.Count > 0
            ? $"<{string.Join(", ", LifetimeParams)}>"
            : "";

        var paramReqs = string.Join(", ", ParamRequirements.Select(p => p.ToString()));
        var returnReq = ReturnConstraint.ToString();
        var constraints = LifetimeConstraints.Count > 0
            ? $" where {string.Join(", ", LifetimeConstraints)}"
            : "";

        return $"fn{lifetimeParams}({paramReqs}) -> {returnReq}{constraints}";
    }
}

/// <summary>
/// 借用签名缓存 - 存储已推断的函数借用签名
/// </summary>
public sealed class LoanSignatureCache
{
    private readonly Dictionary<SymbolId, LoanSignature> _signatures = new();

    /// <summary>
    /// 获取函数的借用签名
    /// </summary>
    public LoanSignature? GetSignature(SymbolId functionSymbol)
    {
        return _signatures.TryGetValue(functionSymbol, out var signature)
            ? signature
            : null;
    }

    /// <summary>
    /// 设置函数的借用签名
    /// </summary>
    public void SetSignature(SymbolId functionSymbol, LoanSignature signature)
    {
        _signatures[functionSymbol] = signature;
    }

    /// <summary>
    /// 检查是否已推断指定函数的借用签名
    /// </summary>
    public bool HasSignature(SymbolId functionSymbol)
    {
        return _signatures.ContainsKey(functionSymbol);
    }

    /// <summary>
    /// 清除所有缓存的签名
    /// </summary>
    public void Clear()
    {
        _signatures.Clear();
    }
}
