using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private const int MaxDeterministicCoverageAssignmentCount = 256;

    private static bool TryEnumerateDeterministicConstraintAssignments(
        CtorDeterministicIntConstraintProfile profile,
        out List<CtorDeterministicIntConstraintProfile> assignments,
        out bool overflow)
    {
        assignments = [];
        overflow = false;
        var overflowed = false;
        if (!profile.HasAnyConstraint)
        {
            return false;
        }

        var positionalEntries = profile.PositionalConstraints
            .OrderBy(pair => pair.Key)
            .Select(pair => (
                pair.Key,
                Values: pair.Value
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray()))
            .ToArray();
        var namedEntries = profile.NamedConstraints
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (
                pair.Key,
                Values: pair.Value
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray()))
            .ToArray();
        var positionalBoolEntries = profile.PositionalBoolConstraints
            .OrderBy(pair => pair.Key)
            .Select(pair => (
                pair.Key,
                Values: pair.Value
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray()))
            .ToArray();
        var namedBoolEntries = profile.NamedBoolConstraints
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (
                pair.Key,
                Values: pair.Value
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray()))
            .ToArray();

        for (var i = 0; i < positionalEntries.Length; i++)
        {
            if (positionalEntries[i].Values.Length == 0)
            {
                return false;
            }
        }

        for (var i = 0; i < namedEntries.Length; i++)
        {
            if (namedEntries[i].Values.Length == 0)
            {
                return false;
            }
        }

        for (var i = 0; i < positionalBoolEntries.Length; i++)
        {
            if (positionalBoolEntries[i].Values.Length == 0)
            {
                return false;
            }
        }

        for (var i = 0; i < namedBoolEntries.Length; i++)
        {
            if (namedBoolEntries[i].Values.Length == 0)
            {
                return false;
            }
        }

        var positionalSelection = new Dictionary<int, IReadOnlyCollection<long>>();
        var namedSelection = new Dictionary<string, IReadOnlyCollection<long>>(StringComparer.Ordinal);
        var positionalBoolSelection = new Dictionary<int, IReadOnlyCollection<bool>>();
        var namedBoolSelection = new Dictionary<string, IReadOnlyCollection<bool>>(StringComparer.Ordinal);
        var assignmentBuffer = new List<CtorDeterministicIntConstraintProfile>();

        bool ExpandPositional(int index)
        {
            if (index >= positionalEntries.Length)
            {
                return ExpandNamed(0);
            }

            var (position, values) = positionalEntries[index];
            for (var i = 0; i < values.Length; i++)
            {
                positionalSelection[position] = [values[i]];
                if (!ExpandPositional(index + 1))
                {
                    positionalSelection.Remove(position);
                    return false;
                }
            }

            positionalSelection.Remove(position);
            return true;
        }

        bool ExpandNamed(int index)
        {
            if (index >= namedEntries.Length)
            {
                return ExpandPositionalBool(0);
            }

            var (fieldName, values) = namedEntries[index];
            for (var i = 0; i < values.Length; i++)
            {
                namedSelection[fieldName] = [values[i]];
                if (!ExpandNamed(index + 1))
                {
                    namedSelection.Remove(fieldName);
                    return false;
                }
            }

            namedSelection.Remove(fieldName);
            return true;
        }

        bool ExpandPositionalBool(int index)
        {
            if (index >= positionalBoolEntries.Length)
            {
                return ExpandNamedBool(0);
            }

            var (position, values) = positionalBoolEntries[index];
            for (var i = 0; i < values.Length; i++)
            {
                positionalBoolSelection[position] = [values[i]];
                if (!ExpandPositionalBool(index + 1))
                {
                    positionalBoolSelection.Remove(position);
                    return false;
                }
            }

            positionalBoolSelection.Remove(position);
            return true;
        }

        bool ExpandNamedBool(int index)
        {
            if (index >= namedBoolEntries.Length)
            {
                if (assignmentBuffer.Count >= MaxDeterministicCoverageAssignmentCount)
                {
                    overflowed = true;
                    return false;
                }

                assignmentBuffer.Add(
                    CreateDeterministicConstraintProfile(
                        positionalSelection,
                        namedSelection,
                        positionalBoolSelection,
                        namedBoolSelection));
                return true;
            }

            var (fieldName, values) = namedBoolEntries[index];
            for (var i = 0; i < values.Length; i++)
            {
                namedBoolSelection[fieldName] = [values[i]];
                if (!ExpandNamedBool(index + 1))
                {
                    namedBoolSelection.Remove(fieldName);
                    return false;
                }
            }

            namedBoolSelection.Remove(fieldName);
            return true;
        }

        if (!ExpandPositional(0))
        {
            overflow = overflowed;
            return false;
        }

        assignments = assignmentBuffer;
        overflow = overflowed;
        return assignments.Count > 0;
    }

    private static bool TryCollectDeterministicConstraintsFromConstructorPattern(
        CtorPattern constructorPattern,
        out Dictionary<int, IReadOnlyCollection<long>> positionalIntConstraints,
        out Dictionary<string, IReadOnlyCollection<long>> namedIntConstraints,
        out Dictionary<int, IReadOnlyCollection<bool>> positionalBoolConstraints,
        out Dictionary<string, IReadOnlyCollection<bool>> namedBoolConstraints)
    {
        positionalIntConstraints = new Dictionary<int, IReadOnlyCollection<long>>();
        namedIntConstraints = new Dictionary<string, IReadOnlyCollection<long>>(StringComparer.Ordinal);
        positionalBoolConstraints = new Dictionary<int, IReadOnlyCollection<bool>>();
        namedBoolConstraints = new Dictionary<string, IReadOnlyCollection<bool>>(StringComparer.Ordinal);

        for (var i = 0; i < constructorPattern.PositionalPatterns.Count; i++)
        {
            if (!TryGetDeterministicTargetIntValues(
                    constructorPattern.PositionalPatterns[i],
                    out var values))
            {
                if (!TryGetDeterministicTargetBoolValues(
                        constructorPattern.PositionalPatterns[i],
                        out var boolValues))
                {
                    continue;
                }

                if (!TryAddOrIntersectDeterministicValues(positionalBoolConstraints, i, boolValues))
                {
                    positionalIntConstraints = [];
                    namedIntConstraints = [];
                    positionalBoolConstraints = [];
                    namedBoolConstraints = [];
                    return false;
                }

                continue;
            }

            if (!TryAddOrIntersectDeterministicValues(positionalIntConstraints, i, values))
            {
                positionalIntConstraints = [];
                namedIntConstraints = [];
                positionalBoolConstraints = [];
                namedBoolConstraints = [];
                return false;
            }
        }

        for (var i = 0; i < constructorPattern.NamedPatterns.Count; i++)
        {
            var namedPattern = constructorPattern.NamedPatterns[i];
            if (string.IsNullOrWhiteSpace(namedPattern.FieldName) ||
                namedPattern.Pattern == null)
            {
                continue;
            }

            if (TryGetDeterministicTargetIntValues(namedPattern.Pattern, out var intValues))
            {
                if (!TryAddOrIntersectDeterministicValues(namedIntConstraints, namedPattern.FieldName, intValues))
                {
                    positionalIntConstraints = [];
                    namedIntConstraints = [];
                    positionalBoolConstraints = [];
                    namedBoolConstraints = [];
                    return false;
                }

                continue;
            }

            if (!TryGetDeterministicTargetBoolValues(namedPattern.Pattern, out var boolValues))
            {
                continue;
            }

            if (!TryAddOrIntersectDeterministicValues(namedBoolConstraints, namedPattern.FieldName, boolValues))
            {
                positionalIntConstraints = [];
                namedIntConstraints = [];
                positionalBoolConstraints = [];
                namedBoolConstraints = [];
                return false;
            }
        }

        return true;
    }

    private static bool TryAddOrIntersectDeterministicValues<TKey, TValue>(
        IDictionary<TKey, IReadOnlyCollection<TValue>> constraints,
        TKey key,
        IReadOnlyCollection<TValue> values)
        where TKey : notnull
        where TValue : notnull
    {
        if (constraints.TryGetValue(key, out var existingValues))
        {
            var intersection = existingValues
                .Intersect(values)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
            if (intersection.Length == 0)
            {
                return false;
            }

            constraints[key] = intersection;
            return true;
        }

        constraints[key] = values
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        return true;
    }
}
