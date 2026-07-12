using Eidosc.Symbols;

namespace Eidosc.Types;

/// <summary>
/// Request 类型 - 表示能力操作调用产生的请求
/// Request[Effect, Result] - 例如 Request[Emitter, ()]
/// Handler 接收 Request 值并处理它们
/// </summary>
public sealed record RequestType : Type, IEquatable<RequestType>
{
    /// <summary>
    /// 能力类型
    /// </summary>
    public required Type Effect { get; init; }

    /// <summary>
    /// 操作结果类型
    /// </summary>
    public required Type Result { get; init; }

    /// <summary>
    /// 操作调用时携带的参数类型（payload），通常为操作的参数元组。
    /// 在效应推断阶段填充。
    /// </summary>
    public Type? Payload { get; init; }

    /// <summary>
    /// resume 续体期望的参数类型。
    /// 在效应推断阶段填充。
    /// </summary>
    public Type? ResumeArg { get; init; }

    public RequestType() { }

    public RequestType(Type ability, Type result)
    {
        Effect = ability;
        Result = result;
    }

    public override bool IsConcrete => Effect.IsConcrete && Result.IsConcrete;

    public override IEnumerable<int> FreeTypeVariables()
    {
        foreach (var v in Effect.FreeTypeVariables())
            yield return v;

        foreach (var v in Result.FreeTypeVariables())
            yield return v;

        if (Payload is not null)
        {
            foreach (var v in Payload.FreeTypeVariables())
                yield return v;
        }

        if (ResumeArg is not null)
        {
            foreach (var v in ResumeArg.FreeTypeVariables())
                yield return v;
        }
    }

    public override string ToString()
    {
        return $"Request[{Effect}, {Result}]";
    }

    public bool Equals(RequestType? other)
    {
        if (other is null) return false;
        return Effect.Equals(other.Effect) &&
               Result.Equals(other.Result) &&
               Equals(Payload, other.Payload) &&
               Equals(ResumeArg, other.ResumeArg);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Effect, Result, Payload, ResumeArg);
    }
}
