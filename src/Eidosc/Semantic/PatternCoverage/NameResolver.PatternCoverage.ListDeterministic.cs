using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private delegate bool DeterministicBindingMerger<TBinding>(
        IReadOnlyDictionary<string, TBinding> left,
        IReadOnlyDictionary<string, TBinding> right,
        out Dictionary<string, TBinding> merged);

    private static bool TryMatchDeterministicNonViewAlternativeInMixedUncertainOr(
        IReadOnlyList<Pattern> alternatives,
        ListIntCaseToken value,
        IReadOnlySet<long> domainValues,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        return TryMatchAnyDeterministicIntElementPattern(alternatives, value, domainValues, out bindings);
    }

    private static bool TryMatchListPatternWithBoolVectorViaDeterministicNonViewPath(
        Pattern pattern,
        IReadOnlyList<bool> boolValues)
    {
        return EvaluateListPatternWithBoolVectorDeterministicNonViewTruth(pattern, boolValues) is
            DeterministicNonViewMatchTruth.Match;
    }

    private static bool TryMatchListPatternWithBoolVectorViaDeterministicNonViewPath(
        ListPattern listPattern,
        IReadOnlyList<bool> boolValues)
    {
        return EvaluateListPatternWithBoolVectorDeterministicNonViewTruth(listPattern, boolValues) is
            DeterministicNonViewMatchTruth.Match;
    }

    private static bool TryMatchListPatternWithBoolElementViaDeterministicNonViewPath(
        Pattern pattern,
        bool value,
        out Dictionary<string, bool> bindings)
    {
        bindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        switch (pattern)
        {
            case LiteralPattern { Type: LiteralType.Boolean, Value: bool literalValue }:
                return value == literalValue;

            case WildcardPattern:
                return true;

            case VarPattern varPattern:
                return TryBindTupleBoolVariable(bindings, varPattern.Name, value);

            case AsPattern asPattern:
                return TryMatchDeterministicBoolAsPattern(asPattern, value, out bindings);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryMatchAnyDeterministicBoolElementPattern(orPattern.Alternatives, value, out bindings);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryMatchAllDeterministicBoolElementPattern(andPattern.Conjuncts, value, out bindings);

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerTruth = EvaluateBoolPatternDeterministicNonViewTruth(
                    notPattern.InnerPattern,
                    value);
                return innerTruth is DeterministicNonViewMatchTruth.NoMatch;
            }

            case ViewPattern { InnerPattern: not null, IsTransparentIdentityView: true } viewPattern:
                return TryMatchListPatternWithBoolElementViaDeterministicNonViewPath(
                    viewPattern.InnerPattern,
                    value,
                    out bindings);

            case ViewPattern:
                return false;

            default:
                return false;
        }
    }

    private static bool TryMatchListPatternWithIntVectorViaDeterministicNonViewPath(
        Pattern pattern,
        IReadOnlyList<ListIntCaseToken> intValues)
    {
        return EvaluateListPatternWithIntVectorDeterministicNonViewTruth(pattern, intValues) is
            DeterministicNonViewMatchTruth.Match;
    }

    private static bool TryMatchListPatternWithIntVectorViaDeterministicNonViewPath(
        ListPattern listPattern,
        IReadOnlyList<ListIntCaseToken> intValues)
    {
        return EvaluateListPatternWithIntVectorDeterministicNonViewTruth(listPattern, intValues) is
            DeterministicNonViewMatchTruth.Match;
    }

    private static DeterministicNonViewMatchTruth EvaluateListPatternWithBoolVectorDeterministicNonViewTruth(
        Pattern pattern,
        IReadOnlyList<bool> boolValues)
    {
        return EvaluateDeterministicListVectorTruth(
            pattern,
            boolValues,
            EvaluateListPatternWithBoolVectorDeterministicNonViewTruth,
            EvaluateListPatternWithBoolVectorDeterministicNonViewTruth);
    }

    private static DeterministicNonViewMatchTruth EvaluateListPatternWithBoolVectorDeterministicNonViewTruth(
        ListPattern listPattern,
        IReadOnlyList<bool> boolValues)
    {
        if (!HasCompatibleListPatternLength(listPattern, boolValues.Count))
        {
            return DeterministicNonViewMatchTruth.NoMatch;
        }

        return EvaluateDeterministicListPatternTruth(
            listPattern,
            boolValues,
            static (pattern, value, _) => EvaluateBoolPatternDeterministicNonViewTruth(pattern, value),
            static _ => null);
    }

    private static DeterministicNonViewMatchTruth EvaluateListPatternWithIntVectorDeterministicNonViewTruth(
        Pattern pattern,
        IReadOnlyList<ListIntCaseToken> intValues)
    {
        return EvaluateDeterministicListVectorTruth(
            pattern,
            intValues,
            EvaluateListPatternWithIntVectorDeterministicNonViewTruth,
            EvaluateListPatternWithIntVectorDeterministicNonViewTruth);
    }

    private static DeterministicNonViewMatchTruth EvaluateListPatternWithIntVectorDeterministicNonViewTruth(
        ListPattern listPattern,
        IReadOnlyList<ListIntCaseToken> intValues)
    {
        if (!HasCompatibleListPatternLength(listPattern, intValues.Count))
        {
            return DeterministicNonViewMatchTruth.NoMatch;
        }

        return EvaluateDeterministicListPatternTruth(
            listPattern,
            intValues,
            static (pattern, value, domainValues) =>
                EvaluateIntListElementDeterministicNonViewTruth(pattern, value, domainValues!),
            static value => value.IsOtherBucket
                ? []
                : new HashSet<long> { value.Value });
    }

    private static DeterministicNonViewMatchTruth EvaluateIntListElementDeterministicNonViewTruth(
        Pattern pattern,
        ListIntCaseToken value,
        IReadOnlySet<long> domainValues)
    {
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                if (!TryGetIntegerLiteralPatternValue(literalPattern, out var literalValue))
                {
                    return DeterministicNonViewMatchTruth.NoMatch;
                }

                if (!value.IsOtherBucket)
                {
                    return value.Value == literalValue
                        ? DeterministicNonViewMatchTruth.Match
                        : DeterministicNonViewMatchTruth.NoMatch;
                }

                return domainValues.Contains(literalValue)
                    ? DeterministicNonViewMatchTruth.NoMatch
                    : DeterministicNonViewMatchTruth.Unknown;

            case RangePattern rangePattern:
                if (!TryGetIntegerRangePatternBounds(rangePattern, out var start, out var end))
                {
                    return DeterministicNonViewMatchTruth.NoMatch;
                }

                if (!value.IsOtherBucket)
                {
                    return value.Value >= start && value.Value <= end
                        ? DeterministicNonViewMatchTruth.Match
                        : DeterministicNonViewMatchTruth.NoMatch;
                }

                return IsOtherBucketDefinitelyOutsideIntRange(start, end, domainValues)
                    ? DeterministicNonViewMatchTruth.NoMatch
                    : DeterministicNonViewMatchTruth.Unknown;

            case WildcardPattern:
            case VarPattern:
            case AsPattern { InnerPattern: null }:
                return DeterministicNonViewMatchTruth.Match;

            case AsPattern asPattern:
                return EvaluateIntListElementDeterministicNonViewTruth(
                    asPattern.InnerPattern,
                    value,
                    domainValues);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return EvaluateAnyDeterministicListTruth(
                    orPattern.Alternatives,
                    candidate => EvaluateIntListElementDeterministicNonViewTruth(candidate, value, domainValues));

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return EvaluateAllDeterministicListTruth(
                    andPattern.Conjuncts,
                    candidate => EvaluateIntListElementDeterministicNonViewTruth(candidate, value, domainValues));

            case NotPattern { InnerPattern: not null } notPattern:
                return InvertDeterministicNonViewMatchTruth(
                    EvaluateIntListElementDeterministicNonViewTruth(
                        notPattern.InnerPattern,
                        value,
                        domainValues));

            case ViewPattern { InnerPattern: not null, IsTransparentIdentityView: true } viewPattern:
                return EvaluateIntListElementDeterministicNonViewTruth(
                    viewPattern.InnerPattern,
                    value,
                    domainValues);

            case ViewPattern:
                return DeterministicNonViewMatchTruth.Unknown;

            default:
                return DeterministicNonViewMatchTruth.NoMatch;
        }
    }

    private static bool IsOtherBucketDefinitelyOutsideIntRange(
        long start,
        long end,
        IReadOnlySet<long> domainValues)
    {
        if (start > end)
        {
            return true;
        }

        if (domainValues.Count == 0 ||
            !TryGetInclusiveRangeLength(start, end, out var rangeLength) ||
            rangeLength > domainValues.Count)
        {
            return false;
        }

        for (var current = start; ; current++)
        {
            if (!domainValues.Contains(current))
            {
                return false;
            }

            if (current == end)
            {
                break;
            }
        }

        return true;
    }

    private static bool TryGetInclusiveRangeLength(long start, long end, out long length)
    {
        length = 0;
        if (start > end)
        {
            return true;
        }

        try
        {
            length = checked(end - start + 1);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryMatchListPatternWithIntElementBindingsViaDeterministicNonViewPath(
        Pattern pattern,
        ListIntCaseToken value,
        IReadOnlySet<long> domainValues,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        bindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                if (!TryGetIntegerLiteralPatternValue(literalPattern, out var literalValue) ||
                    value.IsOtherBucket)
                {
                    return false;
                }

                return value.Value == literalValue;

            case RangePattern rangePattern:
                if (!TryGetIntegerRangePatternBounds(rangePattern, out var start, out var end) ||
                    value.IsOtherBucket)
                {
                    return false;
                }

                return value.Value >= start && value.Value <= end;

            case WildcardPattern:
                return true;

            case VarPattern varPattern:
                return TryBindKnownIntValue(
                    bindings,
                    varPattern.Name,
                    ToListGuardIntBinding(value, domainValues));

            case AsPattern asPattern:
                return TryMatchDeterministicIntAsPattern(asPattern, value, domainValues, out bindings);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryMatchAnyDeterministicIntElementPattern(orPattern.Alternatives, value, domainValues, out bindings);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryMatchAllDeterministicIntElementPattern(andPattern.Conjuncts, value, domainValues, out bindings);

            case NotPattern { InnerPattern: not null } notPattern:
            {
                if (value.IsOtherBucket)
                {
                    return !HasAnyViewPattern(notPattern.InnerPattern) &&
                           !TryMatchListPatternWithIntElementBindingsViaDeterministicNonViewPath(
                               notPattern.InnerPattern,
                               value,
                               domainValues,
                               out _);
                }

                var innerTruth = EvaluateIntPatternDeterministicNonViewTruth(
                    notPattern.InnerPattern,
                    value.Value);
                return innerTruth is DeterministicNonViewMatchTruth.NoMatch;
            }

            case ViewPattern { InnerPattern: not null, IsTransparentIdentityView: true } viewPattern:
                return TryMatchListPatternWithIntElementBindingsViaDeterministicNonViewPath(
                    viewPattern.InnerPattern,
                    value,
                    domainValues,
                    out bindings);

            case ViewPattern:
                return false;

            default:
                return false;
        }
    }

    private static DeterministicNonViewMatchTruth EvaluateDeterministicListPatternTruth<TValue>(
        ListPattern listPattern,
        IReadOnlyList<TValue> values,
        Func<Pattern, TValue, IReadOnlySet<long>?, DeterministicNonViewMatchTruth> elementEvaluator,
        Func<TValue, IReadOnlySet<long>?> domainSelector)
    {
        var sawUnknown = false;
        for (var i = 0; i < listPattern.Elements.Count; i++)
        {
            var elementTruth = elementEvaluator(
                listPattern.Elements[i],
                values[i],
                domainSelector(values[i]));
            if (elementTruth is DeterministicNonViewMatchTruth.NoMatch)
            {
                return DeterministicNonViewMatchTruth.NoMatch;
            }

            if (elementTruth is DeterministicNonViewMatchTruth.Unknown)
            {
                sawUnknown = true;
            }
        }

        for (var i = 0; i < listPattern.SuffixElements.Count; i++)
        {
            var valueIndex = values.Count - listPattern.SuffixElements.Count + i;
            var elementTruth = elementEvaluator(
                listPattern.SuffixElements[i],
                values[valueIndex],
                domainSelector(values[valueIndex]));
            if (elementTruth is DeterministicNonViewMatchTruth.NoMatch)
            {
                return DeterministicNonViewMatchTruth.NoMatch;
            }

            if (elementTruth is DeterministicNonViewMatchTruth.Unknown)
            {
                sawUnknown = true;
            }
        }

        if (HasDeterministicListRestUncertainty(listPattern))
        {
            return DeterministicNonViewMatchTruth.Unknown;
        }

        return sawUnknown
            ? DeterministicNonViewMatchTruth.Unknown
            : DeterministicNonViewMatchTruth.Match;
    }

    private static DeterministicNonViewMatchTruth EvaluateDeterministicListVectorTruth<TValue>(
        Pattern pattern,
        IReadOnlyList<TValue> values,
        Func<Pattern, IReadOnlyList<TValue>, DeterministicNonViewMatchTruth> patternEvaluator,
        Func<ListPattern, IReadOnlyList<TValue>, DeterministicNonViewMatchTruth> listEvaluator)
    {
        switch (pattern)
        {
            case ListPattern listPattern:
                return listEvaluator(listPattern, values);

            case AsPattern { InnerPattern: not null } asPattern:
                return patternEvaluator(asPattern.InnerPattern, values);

            case AsPattern:
            case WildcardPattern:
            case VarPattern:
                return DeterministicNonViewMatchTruth.Match;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return EvaluateAnyDeterministicListTruth(
                    orPattern.Alternatives,
                    candidate => patternEvaluator(candidate, values));

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return EvaluateAllDeterministicListTruth(
                    andPattern.Conjuncts,
                    candidate => patternEvaluator(candidate, values));

            case NotPattern { InnerPattern: not null } notPattern:
                return InvertDeterministicNonViewMatchTruth(
                    patternEvaluator(notPattern.InnerPattern, values));

            case ViewPattern { InnerPattern: not null, IsTransparentIdentityView: true } viewPattern:
                return patternEvaluator(viewPattern.InnerPattern, values);

            case ViewPattern:
                return DeterministicNonViewMatchTruth.Unknown;

            default:
                return DeterministicNonViewMatchTruth.NoMatch;
        }
    }

    private static bool HasDeterministicListRestUncertainty(ListPattern listPattern)
    {
        return listPattern.RestPattern != null &&
               (HasAnyViewPattern(listPattern.RestPattern) ||
                !IsPatternIrrefutableForFiniteCoverage(listPattern.RestPattern));
    }

    private static bool TryBindTupleBoolVariable(IDictionary<string, bool> bindings, string? name, bool value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (bindings.TryGetValue(name, out var existing))
        {
            return existing == value;
        }

        bindings[name] = value;
        return true;
    }

    private static bool TryMergeTupleBoolBindings(
        IReadOnlyDictionary<string, bool> left,
        IReadOnlyDictionary<string, bool> right,
        out Dictionary<string, bool> merged)
    {
        merged = new Dictionary<string, bool>(left, StringComparer.Ordinal);
        foreach (var (name, value) in right)
        {
            if (!TryBindTupleBoolVariable(merged, name, value))
            {
                merged = [];
                return false;
            }
        }

        return true;
    }

    private static bool TryMatchDeterministicBoolAsPattern(
        AsPattern asPattern,
        bool value,
        out Dictionary<string, bool> bindings)
    {
        Dictionary<string, bool> innerBindings;
        if (asPattern.InnerPattern == null)
        {
            innerBindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        else if (!TryMatchListPatternWithBoolElementViaDeterministicNonViewPath(
                     asPattern.InnerPattern,
                     value,
                     out innerBindings))
        {
            bindings = [];
            return false;
        }

        if (!TryBindTupleBoolVariable(innerBindings, asPattern.BindingName, value))
        {
            bindings = [];
            return false;
        }

        bindings = innerBindings;
        return true;
    }

    private static bool TryMatchAnyDeterministicBoolElementPattern(
        IReadOnlyList<Pattern> patterns,
        bool value,
        out Dictionary<string, bool> bindings)
    {
        return TryMatchAnyDeterministicElementPattern(
            patterns,
            pattern => TryMatchListPatternWithBoolElementViaDeterministicNonViewPath(pattern, value, out var candidateBindings)
                ? candidateBindings
                : null,
            out bindings);
    }

    private static bool TryMatchAllDeterministicBoolElementPattern(
        IReadOnlyList<Pattern> patterns,
        bool value,
        out Dictionary<string, bool> bindings)
    {
        return TryMatchAllDeterministicElementPattern(
            patterns,
            new Dictionary<string, bool>(StringComparer.Ordinal),
            pattern => TryMatchListPatternWithBoolElementViaDeterministicNonViewPath(pattern, value, out var candidateBindings)
                ? candidateBindings
                : null,
            TryMergeTupleBoolBindings,
            out bindings);
    }

    private static bool TryMatchDeterministicIntAsPattern(
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
        else if (!TryMatchListPatternWithIntElementBindingsViaDeterministicNonViewPath(
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

    private static bool TryMatchAnyDeterministicIntElementPattern(
        IReadOnlyList<Pattern> patterns,
        ListIntCaseToken value,
        IReadOnlySet<long> domainValues,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        return TryMatchAnyDeterministicElementPattern(
            patterns,
            pattern => TryMatchListPatternWithIntElementBindingsViaDeterministicNonViewPath(
                    pattern,
                    value,
                    domainValues,
                    out var candidateBindings)
                ? candidateBindings
                : null,
            out bindings);
    }

    private static bool TryMatchAllDeterministicIntElementPattern(
        IReadOnlyList<Pattern> patterns,
        ListIntCaseToken value,
        IReadOnlySet<long> domainValues,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        return TryMatchAllDeterministicElementPattern(
            patterns,
            new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal),
            pattern => TryMatchListPatternWithIntElementBindingsViaDeterministicNonViewPath(
                    pattern,
                    value,
                    domainValues,
                    out var candidateBindings)
                ? candidateBindings
                : null,
            TryMergeKnownIntBindings,
            out bindings);
    }

    private static bool TryMatchAnyDeterministicElementPattern<TBinding>(
        IReadOnlyList<Pattern> patterns,
        Func<Pattern, Dictionary<string, TBinding>?> matcher,
        out Dictionary<string, TBinding> bindings)
    {
        bindings = [];
        for (var i = 0; i < patterns.Count; i++)
        {
            var candidateBindings = matcher(patterns[i]);
            if (candidateBindings != null)
            {
                bindings = candidateBindings;
                return true;
            }
        }

        return false;
    }

    private static bool TryMatchAllDeterministicElementPattern<TBinding>(
        IReadOnlyList<Pattern> patterns,
        Dictionary<string, TBinding> seed,
        Func<Pattern, Dictionary<string, TBinding>?> matcher,
        DeterministicBindingMerger<TBinding> merger,
        out Dictionary<string, TBinding> bindings)
    {
        var merged = seed;
        for (var i = 0; i < patterns.Count; i++)
        {
            var candidateBindings = matcher(patterns[i]);
            if (candidateBindings == null ||
                !merger(merged, candidateBindings, out merged))
            {
                bindings = [];
                return false;
            }
        }

        bindings = merged;
        return true;
    }

    private static DeterministicNonViewMatchTruth EvaluateAnyDeterministicListTruth(
        IReadOnlyList<Pattern> patterns,
        Func<Pattern, DeterministicNonViewMatchTruth> evaluator)
    {
        var sawUnknown = false;
        for (var i = 0; i < patterns.Count; i++)
        {
            var truth = evaluator(patterns[i]);
            if (truth is DeterministicNonViewMatchTruth.Match)
            {
                return DeterministicNonViewMatchTruth.Match;
            }

            if (truth is DeterministicNonViewMatchTruth.Unknown)
            {
                sawUnknown = true;
            }
        }

        return sawUnknown
            ? DeterministicNonViewMatchTruth.Unknown
            : DeterministicNonViewMatchTruth.NoMatch;
    }

    private static DeterministicNonViewMatchTruth EvaluateAllDeterministicListTruth(
        IReadOnlyList<Pattern> patterns,
        Func<Pattern, DeterministicNonViewMatchTruth> evaluator)
    {
        var sawUnknown = false;
        for (var i = 0; i < patterns.Count; i++)
        {
            var truth = evaluator(patterns[i]);
            if (truth is DeterministicNonViewMatchTruth.NoMatch)
            {
                return DeterministicNonViewMatchTruth.NoMatch;
            }

            if (truth is DeterministicNonViewMatchTruth.Unknown)
            {
                sawUnknown = true;
            }
        }

        return sawUnknown
            ? DeterministicNonViewMatchTruth.Unknown
            : DeterministicNonViewMatchTruth.Match;
    }
}
