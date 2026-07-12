using System.Collections.Immutable;

namespace Eidosc.Types;

/// <summary>
/// Immutable effect-row expression containing nominal tags and open row variables.
/// </summary>
public sealed record EffectRow : Type, IEquatable<EffectRow>
{
    public ImmutableHashSet<EffectTag> Effects { get; init; }

    public ImmutableHashSet<EffectVariable> Variables { get; init; }

    public EffectVariable? EffectVariable => Variables.Count == 1 ? Variables.Single() : null;

    public static readonly EffectRow Empty = new();

    public static readonly EffectRow Pure = Empty;

    public EffectRow()
        : this([], [])
    {
    }

    public EffectRow(IEnumerable<EffectTag> effects)
        : this(effects, [])
    {
    }

    public EffectRow(IEnumerable<EffectTag> effects, EffectVariable? variable)
        : this(effects, variable == null ? [] : [variable])
    {
    }

    public EffectRow(IEnumerable<EffectTag> effects, IEnumerable<EffectVariable> variables)
    {
        Effects = effects.ToImmutableHashSet();
        Variables = variables.ToImmutableHashSet();
    }

    public bool IsPure => Effects.Count == 0 && Variables.Count == 0;

    public bool IsPolymorphic => Variables.Count > 0;

    public EffectRow Add(EffectTag effect) => this with { Effects = Effects.Add(effect) };

    public EffectRow Remove(EffectTag effect) => this with { Effects = Effects.Remove(effect) };

    public EffectRow Union(EffectRow other) => new(
        Effects.Union(other.Effects),
        Variables.Union(other.Variables));

    public EffectRow Difference(EffectRow other) => new(
        Effects.Except(other.Effects),
        Variables);

    public bool Contains(EffectTag effect) => Effects.Contains(effect);

    public bool ContainsName(string name) => Effects.Any(effect => effect.Name == name);

    public override bool IsConcrete => Variables.Count == 0 && Effects.All(effect => effect.IsConcrete);

    public override IEnumerable<int> FreeTypeVariables() =>
        Variables
            .Select(static variable => variable.Id)
            .Concat(Effects.SelectMany(static effect => effect.FreeTypeVariables()))
            .Distinct();

    public bool Equals(EffectRow? other) =>
        other is not null &&
        Effects.SetEquals(other.Effects) &&
        Variables.SetEquals(other.Variables);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var effect in Effects.OrderBy(static effect => effect.Name, StringComparer.Ordinal))
        {
            hash.Add(effect);
        }
        foreach (var variable in Variables.OrderBy(static variable => variable.Id))
        {
            hash.Add(variable.Id);
        }
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        if (IsPure) return "{}";

        var parts = Variables
            .OrderBy(static variable => variable.Id)
            .Select(static variable => variable.Name)
            .Concat(Effects.OrderBy(static effect => effect.Name, StringComparer.Ordinal).Select(static effect => effect.ToString()));
        return $"{{{string.Join(" + ", parts)}}}";
    }

    public static EffectRow FromEffectVariable(EffectVariable variable) => new([], variable);
}
