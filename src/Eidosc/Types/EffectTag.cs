using Eidosc.Symbols;
using System.Text;

namespace Eidosc.Types;

/// <summary>
/// 能力类型 - 替代 EffectLabel
/// 表示一个具体的能力，如 Emitter, Store[Int]
/// </summary>
public sealed record EffectTag : Type, IEquatable<EffectTag>
{
    /// <summary>
    /// 能力符号 ID
    /// </summary>
    public SymbolId Symbol { get; init; } = SymbolId.None;

    /// <summary>
    /// 能力名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 类型参数（用于参数化能力，如 Store[Int]）
    /// </summary>
    public List<Type> TypeArgs { get; init; } = [];

    public EffectTag() { }

    public EffectTag(SymbolId symbol, string name, List<Type>? typeArgs = null)
    {
        Symbol = symbol;
        Name = name;
        TypeArgs = typeArgs ?? [];
    }

    public override bool IsConcrete => TypeArgs.All(a => a.IsConcrete);

    public override IEnumerable<int> FreeTypeVariables()
    {
        foreach (var arg in TypeArgs)
        {
            foreach (var v in arg.FreeTypeVariables())
                yield return v;
        }
    }

    public override string ToString()
    {
        return TypeArgs.Count == 0
            ? Name
            : new StringBuilder().Append(Name).Append('<').AppendJoin(", ", TypeArgs).Append('>').ToString();
    }

    public bool Equals(EffectTag? other)
    {
        if (other is null) return false;

        if (Symbol.IsValid && other.Symbol.IsValid)
        {
            return Symbol == other.Symbol &&
                   TypeArgs.SequenceEqual(other.TypeArgs);
        }

        if (!Symbol.IsValid && !other.Symbol.IsValid)
        {
            return string.Equals(Name, other.Name, StringComparison.Ordinal) &&
                   TypeArgs.SequenceEqual(other.TypeArgs);
        }

        return false;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        if (Symbol.IsValid)
        {
            hash.Add(Symbol);
        }
        else
        {
            hash.Add(Name, StringComparer.Ordinal);
        }

        foreach (var typeArg in TypeArgs)
        {
            hash.Add(typeArg);
        }

        return hash.ToHashCode();
    }
}
