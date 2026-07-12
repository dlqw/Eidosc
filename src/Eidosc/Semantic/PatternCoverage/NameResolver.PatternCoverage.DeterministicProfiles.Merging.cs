using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static CtorDeterministicIntConstraintProfile CreateEmptyDeterministicConstraintProfile()
    {
        return new CtorDeterministicIntConstraintProfile(
            new Dictionary<int, IReadOnlyCollection<long>>(),
            new Dictionary<string, IReadOnlyCollection<long>>(StringComparer.Ordinal),
            new Dictionary<int, IReadOnlyCollection<bool>>(),
            new Dictionary<string, IReadOnlyCollection<bool>>(StringComparer.Ordinal));
    }

    private static CtorDeterministicIntConstraintProfile CreateDeterministicConstraintProfile(
        IReadOnlyDictionary<int, IReadOnlyCollection<long>> positionalConstraints,
        IReadOnlyDictionary<string, IReadOnlyCollection<long>> namedConstraints)
    {
        return CreateDeterministicConstraintProfile(
            positionalConstraints,
            namedConstraints,
            new Dictionary<int, IReadOnlyCollection<bool>>(),
            new Dictionary<string, IReadOnlyCollection<bool>>(StringComparer.Ordinal));
    }

    private static CtorDeterministicIntConstraintProfile CreateDeterministicConstraintProfile(
        IReadOnlyDictionary<int, IReadOnlyCollection<long>> positionalConstraints,
        IReadOnlyDictionary<string, IReadOnlyCollection<long>> namedConstraints,
        IReadOnlyDictionary<int, IReadOnlyCollection<bool>> positionalBoolConstraints,
        IReadOnlyDictionary<string, IReadOnlyCollection<bool>> namedBoolConstraints)
    {
        var positionalCopy = NormalizeDeterministicConstraintMap(positionalConstraints);
        var namedCopy = NormalizeDeterministicConstraintMap(namedConstraints, StringComparer.Ordinal);
        var positionalBoolCopy = NormalizeDeterministicConstraintMap(positionalBoolConstraints);
        var namedBoolCopy = NormalizeDeterministicConstraintMap(namedBoolConstraints, StringComparer.Ordinal);
        return new CtorDeterministicIntConstraintProfile(
            positionalCopy,
            namedCopy,
            positionalBoolCopy,
            namedBoolCopy);
    }

    private static Dictionary<TKey, IReadOnlyCollection<TValue>> NormalizeDeterministicConstraintMap<TKey, TValue>(
        IReadOnlyDictionary<TKey, IReadOnlyCollection<TValue>> constraints,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
        where TValue : IComparable<TValue>
    {
        return constraints.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyCollection<TValue>)NormalizeDeterministicValues(pair.Value),
            comparer ?? EqualityComparer<TKey>.Default);
    }

    private static TValue[] NormalizeDeterministicValues<TValue>(
        IEnumerable<TValue> values)
        where TValue : IComparable<TValue>
    {
        return values
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
    }

    private static bool TryMergeDeterministicConstraintProfiles(
        CtorDeterministicIntConstraintProfile left,
        CtorDeterministicIntConstraintProfile right,
        out CtorDeterministicIntConstraintProfile merged)
    {
        var positionalConstraints = CloneDeterministicConstraintMap(left.PositionalConstraints);
        var namedConstraints = CloneDeterministicConstraintMap(left.NamedConstraints, StringComparer.Ordinal);
        var positionalBoolConstraints = CloneDeterministicConstraintMap(left.PositionalBoolConstraints);
        var namedBoolConstraints = CloneDeterministicConstraintMap(left.NamedBoolConstraints, StringComparer.Ordinal);

        if (!TryMergeDeterministicConstraintMap(positionalConstraints, right.PositionalConstraints)
            || !TryMergeDeterministicConstraintMap(namedConstraints, right.NamedConstraints)
            || !TryMergeDeterministicConstraintMap(positionalBoolConstraints, right.PositionalBoolConstraints)
            || !TryMergeDeterministicConstraintMap(namedBoolConstraints, right.NamedBoolConstraints))
        {
            merged = default;
            return false;
        }

        merged = CreateDeterministicConstraintProfile(
            positionalConstraints,
            namedConstraints,
            positionalBoolConstraints,
            namedBoolConstraints);
        return true;
    }

    private static Dictionary<TKey, IReadOnlyCollection<TValue>> CloneDeterministicConstraintMap<TKey, TValue>(
        IReadOnlyDictionary<TKey, IReadOnlyCollection<TValue>> constraints,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        return constraints.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            comparer ?? EqualityComparer<TKey>.Default);
    }

    private static bool TryMergeDeterministicConstraintMap<TKey, TValue>(
        Dictionary<TKey, IReadOnlyCollection<TValue>> target,
        IReadOnlyDictionary<TKey, IReadOnlyCollection<TValue>> source)
        where TKey : notnull
        where TValue : IComparable<TValue>
    {
        foreach (var (key, values) in source)
        {
            if (!TryAddOrIntersectDeterministicValues(target, key, values))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildDeterministicConstraintProfileStableKey(
        CtorDeterministicIntConstraintProfile profile)
    {
        var positional = BuildDeterministicConstraintMapStableKey(profile.PositionalConstraints);
        var named = BuildDeterministicConstraintMapStableKey(profile.NamedConstraints, StringComparer.Ordinal);
        var positionalBool = BuildDeterministicConstraintMapStableKey(profile.PositionalBoolConstraints);
        var namedBool = BuildDeterministicConstraintMapStableKey(profile.NamedBoolConstraints, StringComparer.Ordinal);
        return $"p[{positional}]|n[{named}]|pb[{positionalBool}]|nb[{namedBool}]";
    }

    private static string BuildDeterministicConstraintMapStableKey<TKey, TValue>(
        IReadOnlyDictionary<TKey, IReadOnlyCollection<TValue>> constraints,
        IComparer<TKey>? comparer = null)
        where TKey : notnull
        where TValue : IComparable<TValue>
    {
        return string.Join(
            WellKnownStrings.Punctuation.Semicolon,
            constraints
                .OrderBy(pair => pair.Key, comparer ?? Comparer<TKey>.Default)
                .Select(pair => $"{pair.Key}:{string.Join(",", pair.Value.OrderBy(value => value))}"));
    }
}
