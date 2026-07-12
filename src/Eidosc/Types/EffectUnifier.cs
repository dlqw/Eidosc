namespace Eidosc.Types;

/// <summary>
/// Owns all substitutions for immutable effect-row variables.
/// </summary>
public sealed class EffectUnifier
{
    private readonly Dictionary<int, EffectRow> _bindings = [];

    public bool Bind(EffectVariable variable, EffectRow row)
    {
        var resolved = ApplySubstitution(row);
        if (resolved.Variables.Contains(variable))
        {
            return resolved.Effects.Count == 0 && resolved.Variables.Count == 1;
        }

        if (_bindings.TryGetValue(variable.Id, out var existing))
        {
            _bindings[variable.Id] = ApplySubstitution(existing).Union(resolved);
        }
        else
        {
            _bindings[variable.Id] = resolved;
        }
        return true;
    }

    public bool Unify(EffectRow left, EffectRow right)
    {
        if (left.Effects.Count == 0 && left.Variables.Count == 1)
        {
            return Bind(left.Variables.Single(), right);
        }
        if (right.Effects.Count == 0 && right.Variables.Count == 1)
        {
            return Bind(right.Variables.Single(), left);
        }

        left = ApplySubstitution(left);
        right = ApplySubstitution(right);
        if (left.Equals(right)) return true;

        if (left.Variables.Count > 0)
        {
            var variable = left.Variables.OrderBy(static item => item.Id).First();
            var fixedLeft = new EffectRow(left.Effects, left.Variables.Remove(variable));
            return Bind(variable, right.Union(fixedLeft));
        }
        if (right.Variables.Count > 0)
        {
            var variable = right.Variables.OrderBy(static item => item.Id).First();
            var fixedRight = new EffectRow(right.Effects, right.Variables.Remove(variable));
            return Bind(variable, left.Union(fixedRight));
        }

        return left.Effects.SetEquals(right.Effects);
    }

    public EffectRow ApplySubstitution(EffectRow row)
    {
        return ApplySubstitution(row, []);
    }

    private EffectRow ApplySubstitution(EffectRow row, HashSet<int> active)
    {
        var result = new EffectRow(row.Effects);
        foreach (var variable in row.Variables.OrderBy(static item => item.Id))
        {
            if (!_bindings.TryGetValue(variable.Id, out var binding))
            {
                result = result.Union(EffectRow.FromEffectVariable(variable));
                continue;
            }

            if (!active.Add(variable.Id))
            {
                throw new InvalidOperationException($"Cyclic effect-row substitution for {variable.Name}.");
            }
            result = result.Union(ApplySubstitution(binding, active));
            active.Remove(variable.Id);
        }
        return result;
    }

    public IReadOnlyDictionary<int, EffectRow> GetEffectBindings() => _bindings;
}
