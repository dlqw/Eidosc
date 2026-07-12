using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static IReadOnlyDictionary<int, IReadOnlySet<long>> CollectListIntDomainsByIndex(
        IEnumerable<ListCoverageCase> exactCases)
    {
        var domains = new Dictionary<int, HashSet<long>>();
        foreach (var listCase in exactCases)
        {
            if (!TryParseListCaseIntVector(listCase, out var tokens))
            {
                continue;
            }

            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].IsOtherBucket)
                {
                    continue;
                }

                if (!domains.TryGetValue(i, out var domain))
                {
                    domain = [];
                    domains[i] = domain;
                }

                domain.Add(tokens[i].Value);
            }
        }

        return domains.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<long>)pair.Value,
            EqualityComparer<int>.Default);
    }

    private static bool TryParseListCaseIntVector(
        ListCoverageCase listCoverageCase,
        out IReadOnlyList<ListIntCaseToken> values)
    {
        values = [];
        if (listCoverageCase.IsAtLeast || string.IsNullOrWhiteSpace(listCoverageCase.BoolVectorKey))
        {
            return false;
        }

        var segments = listCoverageCase.BoolVectorKey
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != listCoverageCase.Length)
        {
            return false;
        }

        var parsed = new List<ListIntCaseToken>(segments.Length);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (string.Equals(segment, "i:*", StringComparison.Ordinal))
            {
                parsed.Add(new ListIntCaseToken(0, IsOtherBucket: true));
                continue;
            }

            if (!segment.StartsWith("i:", StringComparison.Ordinal) ||
                !long.TryParse(segment[2..], out var value))
            {
                return false;
            }

            parsed.Add(new ListIntCaseToken(value, IsOtherBucket: false));
        }

        values = parsed;
        return true;
    }

    private static bool TryParseListCaseBoolVector(
        ListCoverageCase listCoverageCase,
        out IReadOnlyList<bool> values)
    {
        values = [];
        if (listCoverageCase.IsAtLeast || string.IsNullOrWhiteSpace(listCoverageCase.BoolVectorKey))
        {
            return false;
        }

        var segments = listCoverageCase.BoolVectorKey
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != listCoverageCase.Length)
        {
            return false;
        }

        var parsed = new bool[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i] == "1" ||
                string.Equals(segments[i], WellKnownStrings.AdditionalKeywords.True, StringComparison.Ordinal))
            {
                parsed[i] = true;
                continue;
            }

            if (segments[i] == "0" ||
                string.Equals(segments[i], WellKnownStrings.AdditionalKeywords.False, StringComparison.Ordinal))
            {
                parsed[i] = false;
                continue;
            }

            return false;
        }

        values = parsed;
        return true;
    }

    private static bool TryGetIntegerLiteralPatternValue(LiteralPattern literalPattern, out long value)
    {
        value = 0;
        if (literalPattern.Type is not (LiteralType.Integer or LiteralType.Char))
        {
            return false;
        }

        switch (literalPattern.Value)
        {
            case long longValue:
                value = longValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            case short shortValue:
                value = shortValue;
                return true;
            case byte byteValue:
                value = byteValue;
                return true;
            case char charValue:
                value = charValue;
                return true;
            case string text when literalPattern.Type == LiteralType.Char && text.Length == 1:
                value = text[0];
                return true;
            case string text when long.TryParse(text, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetIntegerRangePatternBounds(
        RangePattern rangePattern,
        out long start,
        out long end)
    {
        start = 0;
        end = 0;
        return rangePattern.Start != null &&
               rangePattern.End != null &&
               TryGetIntegerLiteralPatternValue(rangePattern.Start, out start) &&
               TryGetIntegerLiteralPatternValue(rangePattern.End, out end) &&
               start <= end;
    }

    private static bool TryBindKnownBoolValue(
        IDictionary<string, bool> bindings,
        string? name,
        bool value)
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

    private static bool TryGetExactSingleBoolPatternValue(Pattern? pattern, out bool value)
    {
        value = false;
        if (pattern == null)
        {
            return false;
        }

        var cases = new HashSet<bool>();
        if (!TryGetExactBoolPatternCases(pattern, cases) || cases.Count != 1)
        {
            return false;
        }

        value = cases.First();
        return true;
    }

    private static bool TryGetExactSingleIntPatternValue(Pattern? pattern, out long value)
    {
        value = 0;
        if (pattern == null)
        {
            return false;
        }

        switch (pattern)
        {
            case LiteralPattern literalPattern:
                return TryGetIntegerLiteralPatternValue(literalPattern, out value);

            case AsPattern { InnerPattern: not null } asPattern:
                return TryGetExactSingleIntPatternValue(asPattern.InnerPattern, out value);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                long? exact = null;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    if (!TryGetExactSingleIntPatternValue(conjunct, out var conjunctValue))
                    {
                        return false;
                    }

                    if (exact.HasValue && exact.Value != conjunctValue)
                    {
                        return false;
                    }

                    exact = conjunctValue;
                }

                if (!exact.HasValue)
                {
                    return false;
                }

                value = exact.Value;
                return true;
            }

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                long? exact = null;
                foreach (var alternative in orPattern.Alternatives)
                {
                    if (!TryGetExactSingleIntPatternValue(alternative, out var alternativeValue))
                    {
                        return false;
                    }

                    if (exact.HasValue && exact.Value != alternativeValue)
                    {
                        return false;
                    }

                    exact = alternativeValue;
                }

                if (!exact.HasValue)
                {
                    return false;
                }

                value = exact.Value;
                return true;
            }

            default:
                return false;
        }
    }

    private const int MaxFiniteIntPatternCaseCount = 32;

    private static bool TryGetFiniteIntPatternValues(Pattern? pattern, out HashSet<long> values)
    {
        values = [];
        if (pattern == null)
        {
            return false;
        }

        return TryCollectFiniteIntPatternValues(
            pattern,
            values,
            MaxFiniteIntPatternCaseCount) && values.Count > 0;
    }

    private static bool TryCollectFiniteIntPatternValues(
        Pattern pattern,
        ISet<long> values,
        int maxCaseCount)
    {
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                if (!TryGetIntegerLiteralPatternValue(literalPattern, out var literalValue))
                {
                    return false;
                }

                values.Add(literalValue);
                return values.Count <= maxCaseCount;

            case RangePattern rangePattern:
                if (!TryGetIntegerRangePatternBounds(rangePattern, out var start, out var end))
                {
                    return false;
                }

                var width = end - start + 1;
                if (width <= 0 || width > maxCaseCount)
                {
                    return false;
                }

                for (var value = start; value <= end; value++)
                {
                    values.Add(value);
                    if (values.Count > maxCaseCount)
                    {
                        return false;
                    }
                }

                return true;

            case AsPattern { InnerPattern: not null } asPattern:
                return TryCollectFiniteIntPatternValues(asPattern.InnerPattern, values, maxCaseCount);

            case ViewPattern { InnerPattern: not null } viewPattern:
                return TryCollectFiniteIntPatternValues(viewPattern.InnerPattern, values, maxCaseCount);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var union = new HashSet<long>();
                foreach (var alternative in orPattern.Alternatives)
                {
                    var alternativeValues = new HashSet<long>();
                    if (!TryCollectFiniteIntPatternValues(alternative, alternativeValues, maxCaseCount))
                    {
                        return false;
                    }

                    union.UnionWith(alternativeValues);
                    if (union.Count > maxCaseCount)
                    {
                        return false;
                    }
                }

                foreach (var value in union)
                {
                    values.Add(value);
                }

                return true;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                if (!TryCollectFiniteIntConjunctionValues(
                        andPattern.Conjuncts,
                        maxCaseCount,
                        out var filteredValues) ||
                    filteredValues.Count == 0)
                {
                    return false;
                }

                foreach (var value in filteredValues)
                {
                    values.Add(value);
                    if (values.Count > maxCaseCount)
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }

    private static bool TryCollectFiniteIntConjunctionValues(
        IReadOnlyList<Pattern> conjuncts,
        int maxCaseCount,
        out IReadOnlyCollection<long> values)
    {
        values = [];
        if (conjuncts.Count == 0)
        {
            return false;
        }

        HashSet<long>? candidates = null;
        foreach (var conjunct in conjuncts)
        {
            var conjunctValues = new HashSet<long>();
            if (!TryCollectFiniteIntPatternValues(conjunct, conjunctValues, maxCaseCount) ||
                conjunctValues.Count == 0)
            {
                continue;
            }

            if (candidates == null)
            {
                candidates = conjunctValues;
            }
            else
            {
                candidates.IntersectWith(conjunctValues);
            }
        }

        if (candidates == null || candidates.Count == 0)
        {
            return false;
        }

        var filtered = new HashSet<long>();
        foreach (var candidate in candidates.OrderBy(value => value))
        {
            foreach (var conjunct in conjuncts)
            {
                if (!TryPatternMatchesFiniteIntValue(conjunct, candidate, out var matches))
                {
                    return false;
                }

                if (matches)
                {
                    continue;
                }

                goto NextCandidate;
            }

            filtered.Add(candidate);
            if (filtered.Count > maxCaseCount)
            {
                return false;
            }

        NextCandidate:
            continue;
        }

        if (filtered.Count == 0)
        {
            return false;
        }

        values = filtered;
        return true;
    }

    private static bool TryPatternMatchesFiniteIntValue(Pattern pattern, long value, out bool matches)
    {
        matches = false;
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                if (!TryGetIntegerLiteralPatternValue(literalPattern, out var literalValue))
                {
                    return false;
                }

                matches = value == literalValue;
                return true;

            case RangePattern rangePattern:
                if (!TryGetIntegerRangePatternBounds(rangePattern, out var start, out var end))
                {
                    return false;
                }

                matches = value >= start && value <= end;
                return true;

            case WildcardPattern:
            case VarPattern:
                matches = true;
                return true;

            case AsPattern asPattern:
                if (asPattern.InnerPattern == null)
                {
                    matches = true;
                    return true;
                }

                return TryPatternMatchesFiniteIntValue(asPattern.InnerPattern, value, out matches);

            case ViewPattern viewPattern:
                if (viewPattern.InnerPattern == null)
                {
                    matches = true;
                    return true;
                }

                return TryPatternMatchesFiniteIntValue(viewPattern.InnerPattern, value, out matches);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var any = false;
                foreach (var alternative in orPattern.Alternatives)
                {
                    if (!TryPatternMatchesFiniteIntValue(alternative, value, out var alternativeMatches))
                    {
                        return false;
                    }

                    any |= alternativeMatches;
                }

                matches = any;
                return true;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                var all = true;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    if (!TryPatternMatchesFiniteIntValue(conjunct, value, out var conjunctMatches))
                    {
                        return false;
                    }

                    all &= conjunctMatches;
                }

                matches = all;
                return true;
            }

            case NotPattern { InnerPattern: not null } notPattern:
                if (!TryPatternMatchesFiniteIntValue(notPattern.InnerPattern, value, out var innerMatches))
                {
                    return false;
                }

                matches = !innerMatches;
                return true;

            default:
                return false;
        }
    }
}
