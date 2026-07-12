using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static bool TryNarrowCtorGuardBindingsForTargetProfile(
        CtorPattern ctorPattern,
        CtorDeterministicIntConstraintProfile targetProfile,
        ref Dictionary<string, bool> knownBoolBindings,
        ref Dictionary<string, ListGuardIntBinding> knownIntBindings)
    {
        if (!TryApplyPositionalDeterministicBindings(
                ctorPattern.PositionalPatterns,
                targetProfile.PositionalConstraints,
                ref knownIntBindings,
                TryCollectDeterministicIntBindingsForTargetValues,
                TryMergeKnownIntBindings) ||
            !TryApplyPositionalDeterministicBindings(
                ctorPattern.PositionalPatterns,
                targetProfile.PositionalBoolConstraints,
                ref knownBoolBindings,
                TryCollectDeterministicBoolBindingsForTargetValues,
                TryMergeKnownBoolBindings))
        {
            return false;
        }

        var namedPatterns = new Dictionary<string, FieldPattern>(StringComparer.Ordinal);
        for (var i = 0; i < ctorPattern.NamedPatterns.Count; i++)
        {
            var namedPattern = ctorPattern.NamedPatterns[i];
            if (!string.IsNullOrWhiteSpace(namedPattern.FieldName))
            {
                namedPatterns[namedPattern.FieldName] = namedPattern;
            }
        }

        return TryApplyNamedDeterministicBindings(
                   namedPatterns,
                   targetProfile.NamedConstraints,
                   ref knownIntBindings,
                   TryCollectDeterministicIntBindingsForTargetValues,
                   TryCollectDeterministicIntBindingsForShorthandTargetValues,
                   TryMergeKnownIntBindings) &&
               TryApplyNamedDeterministicBindings(
                   namedPatterns,
                   targetProfile.NamedBoolConstraints,
                   ref knownBoolBindings,
                   TryCollectDeterministicBoolBindingsForTargetValues,
                   TryCollectDeterministicBoolBindingsForShorthandTargetValues,
                   TryMergeKnownBoolBindings);
    }

    private static bool TryApplyPositionalDeterministicBindings<TValue, TBinding>(
        IReadOnlyList<Pattern> positionalPatterns,
        IReadOnlyDictionary<int, IReadOnlyCollection<TValue>> constraints,
        ref Dictionary<string, TBinding> knownBindings,
        TryCollectPatternBindings<TValue, TBinding> tryCollectBindings,
        TryMergeBindings<TBinding> tryMergeBindings)
        where TBinding : notnull
    {
        foreach (var (position, values) in constraints)
        {
            if (position < 0 ||
                position >= positionalPatterns.Count ||
                !tryCollectBindings(
                    positionalPatterns[position],
                    values,
                    out var constrainedBindings) ||
                !tryMergeBindings(
                    knownBindings,
                    constrainedBindings,
                    out knownBindings))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyNamedDeterministicBindings<TValue, TBinding>(
        IReadOnlyDictionary<string, FieldPattern> namedPatterns,
        IReadOnlyDictionary<string, IReadOnlyCollection<TValue>> constraints,
        ref Dictionary<string, TBinding> knownBindings,
        TryCollectPatternBindings<TValue, TBinding> tryCollectPatternBindings,
        TryCollectShorthandBindings<TValue, TBinding> tryCollectShorthandBindings,
        TryMergeBindings<TBinding> tryMergeBindings)
        where TBinding : notnull
    {
        foreach (var (fieldName, values) in constraints)
        {
            if (!namedPatterns.TryGetValue(fieldName, out var fieldPattern))
            {
                return false;
            }

            if (fieldPattern.Pattern != null)
            {
                if (!tryCollectPatternBindings(
                        fieldPattern.Pattern,
                        values,
                        out var constrainedBindings) ||
                    !tryMergeBindings(
                        knownBindings,
                        constrainedBindings,
                        out knownBindings))
                {
                    return false;
                }

                continue;
            }

            if ((!fieldPattern.IsShorthand && string.IsNullOrWhiteSpace(fieldPattern.FieldName)) ||
                !tryCollectShorthandBindings(
                    fieldPattern.FieldName,
                    values,
                    out var shorthandBindings) ||
                !tryMergeBindings(
                    knownBindings,
                    shorthandBindings,
                    out knownBindings))
            {
                return false;
            }
        }

        return true;
    }

    private delegate bool TryCollectPatternBindings<TValue, TBinding>(
        Pattern pattern,
        IReadOnlyCollection<TValue> values,
        out Dictionary<string, TBinding> bindings)
        where TBinding : notnull;

    private delegate bool TryCollectShorthandBindings<TValue, TBinding>(
        string? bindingName,
        IReadOnlyCollection<TValue> values,
        out Dictionary<string, TBinding> bindings)
        where TBinding : notnull;

    private delegate bool TryMergeBindings<TBinding>(
        Dictionary<string, TBinding> existing,
        Dictionary<string, TBinding> incoming,
        out Dictionary<string, TBinding> merged)
        where TBinding : notnull;

    private static bool TryCollectDeterministicIntBindingsForShorthandTargetValues(
        string? bindingName,
        IReadOnlyCollection<long> values,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        return TryCollectDeterministicShorthandBindingsCore(
            bindingName,
            values,
            out bindings,
            CreateIntShorthandBindings);

        Dictionary<string, ListGuardIntBinding>? CreateIntShorthandBindings(long[] distinctValues)
        {
            var candidateBindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
            var bindingValue = distinctValues.Length == 1
                ? ListGuardIntBinding.FromExact(distinctValues[0])
                : ListGuardIntBinding.FromFiniteSet(distinctValues.ToHashSet());
            return TryBindKnownIntValue(candidateBindings, bindingName, bindingValue)
                ? candidateBindings
                : null;
        }
    }

    private static bool TryCollectDeterministicBoolBindingsForShorthandTargetValues(
        string? bindingName,
        IReadOnlyCollection<bool> values,
        out Dictionary<string, bool> bindings)
    {
        return TryCollectDeterministicShorthandBindingsCore(
            bindingName,
            values,
            out bindings,
            CreateBoolShorthandBindings);

        Dictionary<string, bool>? CreateBoolShorthandBindings(bool[] distinctValues)
        {
            var perValueBindings = new List<Dictionary<string, bool>>(distinctValues.Length);
            for (var i = 0; i < distinctValues.Length; i++)
            {
                var candidateBindings = new Dictionary<string, bool>(StringComparer.Ordinal);
                if (!TryBindKnownBoolValue(candidateBindings, bindingName, distinctValues[i]))
                {
                    return null;
                }

                perValueBindings.Add(candidateBindings);
            }

            return IntersectKnownBoolBindingsAcrossAlternatives(perValueBindings);
        }
    }

    private static bool TryCollectDeterministicShorthandBindingsCore<TValue, TBinding>(
        string? bindingName,
        IReadOnlyCollection<TValue> values,
        out Dictionary<string, TBinding> bindings,
        Func<TValue[], Dictionary<string, TBinding>?> collectBindings)
        where TValue : IComparable<TValue>
        where TBinding : notnull
    {
        bindings = new Dictionary<string, TBinding>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(bindingName) ||
            values.Count == 0)
        {
            return false;
        }

        var distinctValues = NormalizeDeterministicValues(values);
        if (distinctValues.Length == 0)
        {
            return false;
        }

        var collectedBindings = collectBindings(distinctValues);
        if (collectedBindings == null)
        {
            bindings = [];
            return false;
        }

        bindings = collectedBindings;
        return true;
    }

    private static bool TryCollectDeterministicIntBindingsForTargetValues(
        Pattern pattern,
        IReadOnlyCollection<long> values,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        return TryCollectDeterministicBindingsForTargetValuesCore(
            values,
            out bindings,
            distinctValues =>
            {
                var domainValues = distinctValues.ToHashSet();
                return TryCollectDeterministicBindingsForTargetValues(
                    distinctValues,
                    out var collectedBindings,
                    value => TryMatchListPatternWithIntElementBindingsViaDeterministicNonViewPath(
                        pattern,
                        new ListIntCaseToken(value, IsOtherBucket: false),
                        domainValues,
                        out var valueBindings)
                        ? valueBindings
                        : null,
                    MergeKnownIntBindingsAcrossAlternatives)
                    ? collectedBindings
                    : null;
            });
    }

    private static bool TryCollectDeterministicBoolBindingsForTargetValues(
        Pattern pattern,
        IReadOnlyCollection<bool> values,
        out Dictionary<string, bool> bindings)
    {
        return TryCollectDeterministicBindingsForTargetValuesCore(
            values,
            out bindings,
            distinctValues =>
            {
                return TryCollectDeterministicBindingsForTargetValues(
                    distinctValues,
                    out var collectedBindings,
                    value => TryMatchListPatternWithBoolElementViaDeterministicNonViewPath(
                        pattern,
                        value,
                        out var valueBindings)
                        ? valueBindings
                        : null,
                    IntersectKnownBoolBindingsAcrossAlternatives)
                    ? collectedBindings
                    : null;
            });
    }

    private static bool TryCollectDeterministicBindingsForTargetValuesCore<TValue, TBinding>(
        IReadOnlyCollection<TValue> values,
        out Dictionary<string, TBinding> bindings,
        Func<TValue[], Dictionary<string, TBinding>?> tryCollectDistinctBindings)
        where TValue : IComparable<TValue>
        where TBinding : notnull
    {
        bindings = new Dictionary<string, TBinding>(StringComparer.Ordinal);
        if (values.Count == 0)
        {
            return false;
        }

        var distinctValues = NormalizeDeterministicValues(values);
        var collectedBindings = tryCollectDistinctBindings(distinctValues);
        if (collectedBindings == null)
        {
            bindings = [];
            return false;
        }

        bindings = collectedBindings;
        return true;
    }

    private static bool TryCollectDeterministicBindingsForTargetValues<TValue, TBinding>(
        IReadOnlyList<TValue> distinctValues,
        out Dictionary<string, TBinding> bindings,
        Func<TValue, Dictionary<string, TBinding>?> tryCollectValueBindings,
        Func<IReadOnlyList<Dictionary<string, TBinding>>, Dictionary<string, TBinding>> mergeBindings)
        where TBinding : notnull
    {
        bindings = new Dictionary<string, TBinding>(StringComparer.Ordinal);
        if (distinctValues.Count == 0)
        {
            return false;
        }

        var perValueBindings = new List<Dictionary<string, TBinding>>(distinctValues.Count);
        for (var i = 0; i < distinctValues.Count; i++)
        {
            var valueBindings = tryCollectValueBindings(distinctValues[i]);
            if (valueBindings == null)
            {
                bindings = [];
                return false;
            }

            perValueBindings.Add(valueBindings);
        }

        bindings = mergeBindings(perValueBindings);
        return true;
    }

    private static bool TryGetDeterministicTargetIntValues(
        Pattern pattern,
        out IReadOnlyCollection<long> values)
    {
        values = Array.Empty<long>();
        if (TryGetExactSingleIntPatternValue(pattern, out var exactValue))
        {
            values = [exactValue];
            return true;
        }

        if (TryGetFiniteIntPatternValues(pattern, out var finiteValues) &&
            finiteValues.Count > 0)
        {
            values = NormalizeDeterministicValues(finiteValues);
            return true;
        }

        return false;
    }

    private static bool TryGetDeterministicTargetBoolValues(
        Pattern pattern,
        out IReadOnlyCollection<bool> values)
    {
        values = Array.Empty<bool>();
        var boolCases = new HashSet<bool>();
        if (!TryGetExactBoolPatternCases(pattern, boolCases) ||
            boolCases.Count == 0)
        {
            return false;
        }

        values = NormalizeDeterministicValues(boolCases);
        return true;
    }
}
