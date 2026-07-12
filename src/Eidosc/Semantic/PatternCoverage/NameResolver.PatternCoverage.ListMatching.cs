using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static bool TryMatchListPatternWithBoolVectorBindings(
        Pattern pattern,
        IReadOnlyList<bool> boolValues,
        out Dictionary<string, bool> bindings)
    {
        bindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        switch (pattern)
        {
            case ListPattern listPattern:
                return TryMatchListPatternWithBoolVectorBindings(listPattern, boolValues, out bindings);

            case AsPattern { InnerPattern: not null } asPattern:
                return TryMatchListPatternWithBoolVectorBindings(asPattern.InnerPattern, boolValues, out bindings);

            case AsPattern:
            case WildcardPattern:
            case VarPattern:
                return true;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryMatchAnyBoolListPattern(orPattern.Alternatives, boolValues, out bindings);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryMatchAllBoolListPattern(andPattern.Conjuncts, boolValues, out bindings);

            case NotPattern { InnerPattern: not null } notPattern:
                return !TryMatchListPatternWithBoolVectorBindings(notPattern.InnerPattern, boolValues, out _);

            default:
                return false;
        }
    }

    private static bool TryMatchListPatternWithBoolVectorBindings(
        ListPattern listPattern,
        IReadOnlyList<bool> boolValues,
        out Dictionary<string, bool> bindings)
    {
        bindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!HasCompatibleListPatternLength(listPattern, boolValues.Count))
        {
            return false;
        }

        for (var i = 0; i < listPattern.Elements.Count; i++)
        {
            if (!TryMatchTupleBoolElementPattern(listPattern.Elements[i], boolValues[i], out var elementBindings) ||
                !TryMergeKnownBoolBindings(bindings, elementBindings, out bindings))
            {
                bindings = [];
                return false;
            }
        }

        for (var i = 0; i < listPattern.SuffixElements.Count; i++)
        {
            var valueIndex = boolValues.Count - listPattern.SuffixElements.Count + i;
            if (!TryMatchTupleBoolElementPattern(
                    listPattern.SuffixElements[i],
                    boolValues[valueIndex],
                    out var elementBindings) ||
                !TryMergeKnownBoolBindings(bindings, elementBindings, out bindings))
            {
                bindings = [];
                return false;
            }
        }

        if (listPattern.RestPattern != null &&
            (!TryCollectKnownBoolBindingsFromPattern(listPattern.RestPattern, out var restBindings) ||
             !TryMergeKnownBoolBindings(bindings, restBindings, out bindings)))
        {
            bindings = [];
            return false;
        }

        return true;
    }

    private static bool TryMatchListPatternWithIntVectorBindings(
        Pattern pattern,
        IReadOnlyList<ListIntCaseToken> intValues,
        IReadOnlyDictionary<int, IReadOnlySet<long>> intDomainsByIndex,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        bindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        switch (pattern)
        {
            case ListPattern listPattern:
                return TryMatchListPatternWithIntVectorBindings(
                    listPattern,
                    intValues,
                    intDomainsByIndex,
                    out bindings);

            case AsPattern { InnerPattern: not null } asPattern:
                return TryMatchListPatternWithIntVectorBindings(
                    asPattern.InnerPattern,
                    intValues,
                    intDomainsByIndex,
                    out bindings);

            case AsPattern:
            case WildcardPattern:
            case VarPattern:
                return true;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryMatchAnyIntListPattern(
                    orPattern.Alternatives,
                    intValues,
                    intDomainsByIndex,
                    out bindings);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryMatchAllIntListPattern(
                    andPattern.Conjuncts,
                    intValues,
                    intDomainsByIndex,
                    out bindings);

            case NotPattern { InnerPattern: not null } notPattern:
                return !TryMatchListPatternWithIntVectorBindings(
                    notPattern.InnerPattern,
                    intValues,
                    intDomainsByIndex,
                    out _);

            default:
                return false;
        }
    }

    private static bool TryMatchListPatternWithIntVectorBindings(
        ListPattern listPattern,
        IReadOnlyList<ListIntCaseToken> intValues,
        IReadOnlyDictionary<int, IReadOnlySet<long>> intDomainsByIndex,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        bindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        if (!HasCompatibleListPatternLength(listPattern, intValues.Count))
        {
            return false;
        }

        for (var i = 0; i < listPattern.Elements.Count; i++)
        {
            var domainValues = intDomainsByIndex.TryGetValue(i, out var domain)
                ? domain
                : new HashSet<long>();
            if (!TryMatchListPatternWithIntElementBindings(
                    listPattern.Elements[i],
                    intValues[i],
                    domainValues,
                    out var elementBindings) ||
                !TryMergeKnownIntBindings(bindings, elementBindings, out bindings))
            {
                bindings = [];
                return false;
            }
        }

        for (var i = 0; i < listPattern.SuffixElements.Count; i++)
        {
            var valueIndex = intValues.Count - listPattern.SuffixElements.Count + i;
            var domainValues = intDomainsByIndex.TryGetValue(valueIndex, out var domain)
                ? domain
                : new HashSet<long>();
            if (!TryMatchListPatternWithIntElementBindings(
                    listPattern.SuffixElements[i],
                    intValues[valueIndex],
                    domainValues,
                    out var elementBindings) ||
                !TryMergeKnownIntBindings(bindings, elementBindings, out bindings))
            {
                bindings = [];
                return false;
            }
        }

        return true;
    }

    private static bool TryMatchListPatternWithIntElementBindings(
        Pattern pattern,
        ListIntCaseToken value,
        IReadOnlySet<long> domainValues,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        bindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                return !value.IsOtherBucket &&
                       TryGetIntegerLiteralPatternValue(literalPattern, out var literalValue) &&
                       value.Value == literalValue;

            case RangePattern rangePattern:
                return !value.IsOtherBucket &&
                       TryGetIntegerRangePatternBounds(rangePattern, out var start, out var end) &&
                       value.Value >= start &&
                       value.Value <= end;

            case WildcardPattern:
                return true;

            case VarPattern varPattern:
                return TryBindKnownIntValue(
                    bindings,
                    varPattern.Name,
                    ToListGuardIntBinding(value, domainValues));

            case AsPattern asPattern:
                return TryMatchAsIntElementPattern(asPattern, value, domainValues, out bindings);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryMatchOrIntElementPattern(orPattern, value, domainValues, out bindings);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryMatchAndIntElementPattern(andPattern, value, domainValues, out bindings);

            case NotPattern { InnerPattern: not null } notPattern:
                return !TryMatchListPatternWithIntElementBindings(
                    notPattern.InnerPattern,
                    value,
                    domainValues,
                    out _);

            case ViewPattern viewPattern:
                return viewPattern.InnerPattern == null ||
                       TryMatchListPatternWithIntElementBindings(
                           viewPattern.InnerPattern,
                           value,
                           domainValues,
                           out bindings);

            default:
                return false;
        }
    }

    private static bool TryMergeKnownIntBindings(
        IReadOnlyDictionary<string, ListGuardIntBinding> left,
        IReadOnlyDictionary<string, ListGuardIntBinding> right,
        out Dictionary<string, ListGuardIntBinding> merged)
    {
        merged = new Dictionary<string, ListGuardIntBinding>(left, StringComparer.Ordinal);
        foreach (var (name, value) in right)
        {
            if (!TryBindKnownIntValue(merged, name, value))
            {
                merged = [];
                return false;
            }
        }

        return true;
    }

    private static bool TryBindKnownIntValue(
        IDictionary<string, ListGuardIntBinding> bindings,
        string? name,
        ListGuardIntBinding value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (bindings.TryGetValue(name, out var existing))
        {
            if (!TryCombineListGuardIntBinding(existing, value, out var combined))
            {
                return false;
            }

            bindings[name] = combined;
            return true;
        }

        bindings[name] = value;
        return true;
    }

    private static bool TryCombineListGuardIntBinding(
        ListGuardIntBinding left,
        ListGuardIntBinding right,
        out ListGuardIntBinding combined)
    {
        if (!left.IsOtherBucket && !right.IsOtherBucket)
        {
            var leftValues = GetFiniteBindingValues(left);
            var rightValues = GetFiniteBindingValues(right);
            leftValues.IntersectWith(rightValues);
            if (leftValues.Count == 0)
            {
                combined = default;
                return false;
            }

            combined = leftValues.Count == 1
                ? ListGuardIntBinding.FromExact(leftValues.First())
                : ListGuardIntBinding.FromFiniteSet(leftValues);
            return true;
        }

        if (!left.IsOtherBucket && right.IsOtherBucket)
        {
            var values = GetFiniteBindingValues(left);
            values.RemoveWhere(value => right.ExcludedValues.Contains(value));
            if (values.Count == 0)
            {
                combined = default;
                return false;
            }

            combined = values.Count == 1
                ? ListGuardIntBinding.FromExact(values.First())
                : ListGuardIntBinding.FromFiniteSet(values);
            return true;
        }

        if (left.IsOtherBucket && !right.IsOtherBucket)
        {
            var values = GetFiniteBindingValues(right);
            values.RemoveWhere(value => left.ExcludedValues.Contains(value));
            if (values.Count == 0)
            {
                combined = default;
                return false;
            }

            combined = values.Count == 1
                ? ListGuardIntBinding.FromExact(values.First())
                : ListGuardIntBinding.FromFiniteSet(values);
            return true;
        }

        var excluded = new HashSet<long>(left.ExcludedValues);
        excluded.UnionWith(right.ExcludedValues);
        combined = ListGuardIntBinding.FromOther(excluded);
        return true;
    }

    private static HashSet<long> GetFiniteBindingValues(ListGuardIntBinding binding)
    {
        if (binding.FiniteValues.Count > 0)
        {
            return new HashSet<long>(binding.FiniteValues);
        }

        if (!binding.IsOtherBucket)
        {
            return [binding.Value];
        }

        return [];
    }

    private static bool HasCompatibleListPatternLength(ListPattern listPattern, int valueCount)
    {
        var minimumLength = listPattern.Elements.Count + listPattern.SuffixElements.Count;
        return listPattern.HasRestMarker
            ? valueCount >= minimumLength
            : valueCount == minimumLength;
    }

    private static bool TryMatchAnyBoolListPattern(
        IReadOnlyList<Pattern> patterns,
        IReadOnlyList<bool> boolValues,
        out Dictionary<string, bool> bindings)
    {
        bindings = [];
        for (var i = 0; i < patterns.Count; i++)
        {
            if (TryMatchListPatternWithBoolVectorBindings(patterns[i], boolValues, out var candidateBindings))
            {
                bindings = candidateBindings;
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchAllBoolListPattern(
        IReadOnlyList<Pattern> patterns,
        IReadOnlyList<bool> boolValues,
        out Dictionary<string, bool> bindings)
    {
        var merged = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var i = 0; i < patterns.Count; i++)
        {
            if (!TryMatchListPatternWithBoolVectorBindings(patterns[i], boolValues, out var candidateBindings) ||
                !TryMergeKnownBoolBindings(merged, candidateBindings, out merged))
            {
                bindings = [];
                return false;
            }
        }

        bindings = merged;
        return true;
    }

    private static bool TryMatchAnyIntListPattern(
        IReadOnlyList<Pattern> patterns,
        IReadOnlyList<ListIntCaseToken> intValues,
        IReadOnlyDictionary<int, IReadOnlySet<long>> intDomainsByIndex,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        bindings = [];
        for (var i = 0; i < patterns.Count; i++)
        {
            if (TryMatchListPatternWithIntVectorBindings(
                    patterns[i],
                    intValues,
                    intDomainsByIndex,
                    out var candidateBindings))
            {
                bindings = candidateBindings;
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchAllIntListPattern(
        IReadOnlyList<Pattern> patterns,
        IReadOnlyList<ListIntCaseToken> intValues,
        IReadOnlyDictionary<int, IReadOnlySet<long>> intDomainsByIndex,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        var merged = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        for (var i = 0; i < patterns.Count; i++)
        {
            if (!TryMatchListPatternWithIntVectorBindings(
                    patterns[i],
                    intValues,
                    intDomainsByIndex,
                    out var candidateBindings) ||
                !TryMergeKnownIntBindings(merged, candidateBindings, out merged))
            {
                bindings = [];
                return false;
            }
        }

        bindings = merged;
        return true;
    }

    private static bool TryMatchAsIntElementPattern(
        AsPattern asPattern,
        ListIntCaseToken value,
        IReadOnlySet<long> domainValues,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        Dictionary<string, ListGuardIntBinding> innerBindings;
        if (asPattern.InnerPattern == null)
        {
            innerBindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        }
        else if (!TryMatchListPatternWithIntElementBindings(
                     asPattern.InnerPattern,
                     value,
                     domainValues,
                     out innerBindings))
        {
            bindings = [];
            return false;
        }

        if (!TryBindKnownIntValue(
                innerBindings,
                asPattern.BindingName,
                ToListGuardIntBinding(value, domainValues)))
        {
            bindings = [];
            return false;
        }

        bindings = innerBindings;
        return true;
    }

    private static bool TryMatchOrIntElementPattern(
        OrPattern orPattern,
        ListIntCaseToken value,
        IReadOnlySet<long> domainValues,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        if (HasAnyUncertainViewPatternOverIntDomain(orPattern.Alternatives, domainValues) &&
            !HasIrrefutableNonViewPattern(orPattern.Alternatives))
        {
            if (TryMatchDeterministicNonViewAlternativeInMixedUncertainOr(
                    orPattern.Alternatives,
                    value,
                    domainValues,
                    out var deterministicBindings))
            {
                bindings = deterministicBindings;
                return true;
            }

            bindings = [];
            return false;
        }

        bindings = [];
        for (var i = 0; i < orPattern.Alternatives.Count; i++)
        {
            if (TryMatchListPatternWithIntElementBindings(
                    orPattern.Alternatives[i],
                    value,
                    domainValues,
                    out var alternativeBindings))
            {
                bindings = alternativeBindings;
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchAndIntElementPattern(
        AndPattern andPattern,
        ListIntCaseToken value,
        IReadOnlySet<long> domainValues,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        if (HasMixedUncertainViewAndNonViewPatterns(andPattern.Conjuncts, domainValues))
        {
            bindings = [];
            return false;
        }

        var merged = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        for (var i = 0; i < andPattern.Conjuncts.Count; i++)
        {
            if (!TryMatchListPatternWithIntElementBindings(
                    andPattern.Conjuncts[i],
                    value,
                    domainValues,
                    out var conjunctBindings) ||
                !TryMergeKnownIntBindings(merged, conjunctBindings, out merged))
            {
                bindings = [];
                return false;
            }
        }

        bindings = merged;
        return true;
    }

    private static ListGuardIntBinding ToListGuardIntBinding(ListIntCaseToken value, IReadOnlySet<long> domainValues)
    {
        return value.IsOtherBucket
            ? ListGuardIntBinding.FromOther(domainValues)
            : ListGuardIntBinding.FromExact(value.Value);
    }
}
