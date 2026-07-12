using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic.PatternCoverage;

internal static partial class PatternUsefulnessAnalyzer
{
    private static bool CanExpandIntSplitCases(int length, int tokenCount)
    {
        if (length <= 0 || tokenCount <= 0)
        {
            return false;
        }

        var estimatedCases = 1L;
        for (var i = 0; i < length; i++)
        {
            if (estimatedCases > MaxListIntSplitCaseCount / tokenCount)
            {
                return false;
            }

            estimatedCases *= tokenCount;
        }

        return estimatedCases <= MaxListIntSplitCaseCount;
    }

    private static bool CanExpandAdtSplitCases(int length, int tokenCount)
    {
        if (length <= 0 || tokenCount <= 0)
        {
            return false;
        }

        var estimatedCases = 1L;
        for (var i = 0; i < length; i++)
        {
            if (estimatedCases > MaxListAdtSplitCaseCount / tokenCount)
            {
                return false;
            }

            estimatedCases *= tokenCount;
        }

        return estimatedCases <= MaxListAdtSplitCaseCount;
    }

    private static void CollectListPatterns(Pattern? pattern, ICollection<ListPattern> output)
    {
        if (pattern == null)
        {
            return;
        }

        switch (pattern)
        {
            case ListPattern listPattern:
                output.Add(listPattern);
                return;

            case TuplePattern tuplePattern when TryProjectTupleListCoveragePattern(tuplePattern, out var projectedListPattern):
                CollectListPatterns(projectedListPattern, output);
                return;

            case AsPattern { InnerPattern: not null } asPattern:
                CollectListPatterns(asPattern.InnerPattern, output);
                return;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    CollectListPatterns(alternative, output);
                }
                return;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    CollectListPatterns(conjunct, output);
                }
                return;

            case NotPattern { InnerPattern: not null } notPattern:
                CollectListPatterns(notPattern.InnerPattern, output);
                return;
        }
    }

    private static bool TryCollectIntDomainCandidatesFromPattern(
        Pattern pattern,
        ISet<long> domainValues,
        ref bool hasConstrainedCase)
    {
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                if (!TryGetIntegerLiteralValue(literalPattern, out var literalValue))
                {
                    return false;
                }

                domainValues.Add(literalValue);
                hasConstrainedCase = true;
                return true;

            case RangePattern rangePattern:
                if (!TryGetIntegerRangeBounds(rangePattern, out var start, out var end))
                {
                    return false;
                }

                var width = end - start + 1;
                if (width <= 0 || width > MaxListIntSplitDomainSize)
                {
                    return false;
                }

                for (var value = start; value <= end; value++)
                {
                    domainValues.Add(value);
                }

                hasConstrainedCase = true;
                return true;

            case WildcardPattern:
            case VarPattern:
                return true;

            case AsPattern asPattern:
                return asPattern.InnerPattern == null ||
                       TryCollectIntDomainCandidatesFromPattern(
                           asPattern.InnerPattern,
                           domainValues,
                           ref hasConstrainedCase);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    if (!TryCollectIntDomainCandidatesFromPattern(alternative, domainValues, ref hasConstrainedCase))
                    {
                        return false;
                    }
                }

                return true;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    if (!TryCollectIntDomainCandidatesFromPattern(conjunct, domainValues, ref hasConstrainedCase))
                    {
                        return false;
                    }
                }

                return true;

            case NotPattern { InnerPattern: not null } notPattern:
                return TryCollectIntDomainCandidatesFromPattern(notPattern.InnerPattern, domainValues, ref hasConstrainedCase);

            case ViewPattern viewPattern when IsPatternIrrefutableForFiniteCoverage(viewPattern.InnerPattern):
                return true;

            case ViewPattern viewPattern:
                return viewPattern.InnerPattern == null ||
                       TryCollectIntDomainCandidatesFromPattern(
                           viewPattern.InnerPattern,
                           domainValues,
                           ref hasConstrainedCase);

            default:
                return false;
        }
    }

    private static bool TryGetIntegerLiteralValue(LiteralPattern literalPattern, out long value)
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

    private static bool TryGetIntegerRangeBounds(
        RangePattern rangePattern,
        out long start,
        out long end)
    {
        start = 0;
        end = 0;
        return rangePattern.Start != null &&
               rangePattern.End != null &&
               TryGetIntegerLiteralValue(rangePattern.Start, out start) &&
               TryGetIntegerLiteralValue(rangePattern.End, out end) &&
               start <= end;
    }

    private static bool TryParseIntDomainToken(string token, out long value, out bool isOther)
    {
        value = 0;
        isOther = false;
        if (string.Equals(token, ListIntOtherToken, StringComparison.Ordinal))
        {
            isOther = true;
            return true;
        }

        if (!token.StartsWith("i:", StringComparison.Ordinal) ||
            !long.TryParse(token[2..], out value))
        {
            return false;
        }

        return true;
    }

    private static bool TryDetermineListBoolSplitLengths(
        IEnumerable<Pattern> patterns,
        int maxTrackedLength,
        out HashSet<int> splitLengths)
    {
        splitLengths = [];
        if (maxTrackedLength <= 0)
        {
            return false;
        }

        var hasAnyListPattern = false;
        var hasBoolConstrainedListPattern = false;
        foreach (var pattern in patterns)
        {
            if (!CollectListBoolSplitLengths(
                    pattern,
                    maxTrackedLength,
                    splitLengths,
                    ref hasAnyListPattern,
                    ref hasBoolConstrainedListPattern))
            {
                splitLengths.Clear();
                return false;
            }
        }

        if (!hasAnyListPattern || !hasBoolConstrainedListPattern || splitLengths.Count == 0)
        {
            splitLengths.Clear();
            return false;
        }

        return true;
    }

    private static bool TryDetermineListBoolSplitLengthsForGuard(
        IEnumerable<Pattern> patterns,
        int maxTrackedLength,
        out HashSet<int> splitLengths)
    {
        splitLengths = [];
        if (maxTrackedLength <= 0)
        {
            return false;
        }

        var hasAnyListPattern = false;
        foreach (var pattern in patterns)
        {
            if (!CollectListBoolSplitLengthsForGuard(
                    pattern,
                    maxTrackedLength,
                    splitLengths,
                    ref hasAnyListPattern))
            {
                splitLengths.Clear();
                return false;
            }
        }

        if (!hasAnyListPattern || splitLengths.Count == 0)
        {
            splitLengths.Clear();
            return false;
        }

        return true;
    }

    private static bool CollectListBoolSplitLengthsForGuard(
        Pattern? pattern,
        int maxTrackedLength,
        ISet<int> splitLengths,
        ref bool hasAnyListPattern)
    {
        if (pattern == null)
        {
            return true;
        }

        switch (pattern)
        {
            case ListPattern listPattern:
            {
                hasAnyListPattern = true;
                var fixedElements = listPattern.Elements.Concat(listPattern.SuffixElements).ToList();
                if (!TryCollectListElementBoolCaseSets(fixedElements, out _))
                {
                    return true;
                }

                if (listPattern.HasRestMarker)
                {
                    var start = fixedElements.Count;
                    var end = Math.Min(maxTrackedLength, MaxListBoolSplitLength);
                    for (var length = start; length <= end; length++)
                    {
                        splitLengths.Add(length);
                    }
                }
                else if (fixedElements.Count <= MaxListBoolSplitLength)
                {
                    splitLengths.Add(fixedElements.Count);
                }

                return true;
            }

            case AsPattern { InnerPattern: not null } asPattern:
                return CollectListBoolSplitLengthsForGuard(
                    asPattern.InnerPattern,
                    maxTrackedLength,
                    splitLengths,
                    ref hasAnyListPattern);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    if (!CollectListBoolSplitLengthsForGuard(
                            alternative,
                            maxTrackedLength,
                            splitLengths,
                            ref hasAnyListPattern))
                    {
                        return false;
                    }
                }

                return true;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    if (!CollectListBoolSplitLengthsForGuard(
                            conjunct,
                            maxTrackedLength,
                            splitLengths,
                            ref hasAnyListPattern))
                    {
                        return false;
                    }
                }

                return true;

            case NotPattern { InnerPattern: not null } notPattern:
                return CollectListBoolSplitLengthsForGuard(
                    notPattern.InnerPattern,
                    maxTrackedLength,
                    splitLengths,
                    ref hasAnyListPattern);

            default:
                return true;
        }
    }

    private static bool CollectListBoolSplitLengths(
        Pattern? pattern,
        int maxTrackedLength,
        ISet<int> splitLengths,
        ref bool hasAnyListPattern,
        ref bool hasBoolConstrainedListPattern)
    {
        if (pattern == null)
        {
            return true;
        }

        switch (pattern)
        {
            case ListPattern listPattern:
            {
                hasAnyListPattern = true;
                var fixedElements = listPattern.Elements.Concat(listPattern.SuffixElements).ToList();
                if (!TryCollectListElementBoolCaseSets(fixedElements, out var elementCases))
                {
                    return true;
                }

                var hasConstrainedElement = elementCases.Any(element => element.Count < 2);
                if (!hasConstrainedElement)
                {
                    return true;
                }

                hasBoolConstrainedListPattern = true;
                if (listPattern.HasRestMarker)
                {
                    var start = fixedElements.Count;
                    var end = Math.Min(maxTrackedLength, MaxListBoolSplitLength);
                    for (var length = start; length <= end; length++)
                    {
                        splitLengths.Add(length);
                    }
                }
                else if (fixedElements.Count <= MaxListBoolSplitLength)
                {
                    splitLengths.Add(fixedElements.Count);
                }

                return true;
            }

            case AsPattern { InnerPattern: not null } asPattern:
                return CollectListBoolSplitLengths(
                    asPattern.InnerPattern,
                    maxTrackedLength,
                    splitLengths,
                    ref hasAnyListPattern,
                    ref hasBoolConstrainedListPattern);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    if (!CollectListBoolSplitLengths(
                            alternative,
                            maxTrackedLength,
                            splitLengths,
                            ref hasAnyListPattern,
                            ref hasBoolConstrainedListPattern))
                    {
                        return false;
                    }
                }

                return true;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    if (!CollectListBoolSplitLengths(
                            conjunct,
                            maxTrackedLength,
                            splitLengths,
                            ref hasAnyListPattern,
                            ref hasBoolConstrainedListPattern))
                    {
                        return false;
                    }
                }

                return true;

            case NotPattern { InnerPattern: not null } notPattern:
                return CollectListBoolSplitLengths(
                    notPattern.InnerPattern,
                    maxTrackedLength,
                    splitLengths,
                    ref hasAnyListPattern,
                    ref hasBoolConstrainedListPattern);

            default:
                return true;
        }
    }

    private static IReadOnlyList<ListCoverageCase> BuildListLengthUniverse(
        int maxTrackedLength,
        IReadOnlySet<int> boolSplitLengths,
        bool enableBoolElementSplits,
        IReadOnlyDictionary<int, IReadOnlyList<string>> intSplitDomainsByLength,
        bool enableIntElementSplits,
        IReadOnlyDictionary<int, IReadOnlyList<string>> adtSplitDomainsByLength,
        bool enableAdtElementSplits,
        int overflowStartLength)
    {
        if (maxTrackedLength < 0)
        {
            return [];
        }

        var universe = new List<ListCoverageCase>();
        for (var length = 0; length <= maxTrackedLength; length++)
        {
            if (!enableBoolElementSplits || !boolSplitLengths.Contains(length))
            {
                if (enableAdtElementSplits &&
                    adtSplitDomainsByLength.TryGetValue(length, out var adtDomainTokens))
                {
                    var adtTokenSets = Enumerable.Repeat((IReadOnlyList<string>)adtDomainTokens, length).ToList();
                    BuildListTokenVectorCases(adtTokenSets, 0, new List<string>(length), universe, length);
                    continue;
                }

                if (!enableIntElementSplits ||
                    !intSplitDomainsByLength.TryGetValue(length, out var intDomainTokens))
                {
                    universe.Add(new ListCoverageCase(IsAtLeast: false, Length: length));
                    continue;
                }

                var tokenSets = Enumerable.Repeat((IReadOnlyList<string>)intDomainTokens, length).ToList();
                BuildListTokenVectorCases(tokenSets, 0, new List<string>(length), universe, length);
                continue;
            }

            IReadOnlyList<bool> boolDomain = [false, true];
            var values = Enumerable.Repeat(boolDomain, length).ToList();
            BuildListBoolVectorCases(values, 0, new List<bool>(length), universe, length);
        }

        if (enableAdtElementSplits &&
            maxTrackedLength > 0 &&
            adtSplitDomainsByLength.TryGetValue(maxTrackedLength, out var overflowAdtDomainTokens))
        {
            var overflowAdtTokenSets = Enumerable
                .Repeat((IReadOnlyList<string>)overflowAdtDomainTokens, maxTrackedLength)
                .ToList();
            BuildListAtLeastTokenVectorCases(
                overflowAdtTokenSets,
                0,
                new List<string>(maxTrackedLength),
                universe,
                overflowStartLength);
        }
        else
        {
            universe.Add(new ListCoverageCase(IsAtLeast: true, Length: overflowStartLength));
        }

        return universe
            .OrderBy(@case => @case, ListLengthCaseComparer.Instance)
            .ToList();
    }

    private static bool TryGetListPatternMaxPrefixLength(Pattern pattern, out int maxPrefixLength)
    {
        maxPrefixLength = 0;
        var found = false;
        CollectListPatternPrefixLengths(pattern, ref found, ref maxPrefixLength);
        return found;
    }

    private static void CollectListPatternPrefixLengths(
        Pattern? pattern,
        ref bool found,
        ref int maxPrefixLength)
    {
        if (pattern == null)
        {
            return;
        }

        switch (pattern)
        {
            case ListPattern listPattern:
                found = true;
                var fixedLength = listPattern.Elements.Count + listPattern.SuffixElements.Count;
                if (fixedLength > maxPrefixLength)
                {
                    maxPrefixLength = fixedLength;
                }

                return;

            case TuplePattern tuplePattern when TryProjectTupleListCoveragePattern(tuplePattern, out var projectedListPattern):
                CollectListPatternPrefixLengths(projectedListPattern, ref found, ref maxPrefixLength);
                return;

            case AsPattern { InnerPattern: not null } asPattern:
                CollectListPatternPrefixLengths(asPattern.InnerPattern, ref found, ref maxPrefixLength);
                return;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    CollectListPatternPrefixLengths(alternative, ref found, ref maxPrefixLength);
                }

                return;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    CollectListPatternPrefixLengths(conjunct, ref found, ref maxPrefixLength);
                }

                return;

            case NotPattern { InnerPattern: not null } notPattern:
                CollectListPatternPrefixLengths(notPattern.InnerPattern, ref found, ref maxPrefixLength);
                return;
        }
    }

    private static bool TryProjectTupleListCoveragePattern(TuplePattern tuplePattern, out Pattern projectedPattern)
    {
        projectedPattern = null!;
        Pattern? candidate = null;

        foreach (var element in tuplePattern.Elements)
        {
            if (ContainsListCoveragePattern(element))
            {
                if (candidate != null)
                {
                    return false;
                }

                candidate = element;
                continue;
            }

            if (!IsPatternIrrefutableForFiniteCoverage(element))
            {
                return false;
            }
        }

        if (candidate == null)
        {
            return false;
        }

        projectedPattern = candidate;
        return true;
    }

    private static bool ContainsListCoveragePattern(Pattern? pattern)
    {
        if (pattern == null)
        {
            return false;
        }

        switch (pattern)
        {
            case ListPattern:
                return true;

            case TuplePattern tuplePattern:
                return tuplePattern.Elements.Any(ContainsListCoveragePattern);

            case AsPattern { InnerPattern: not null } asPattern:
                return ContainsListCoveragePattern(asPattern.InnerPattern);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return orPattern.Alternatives.Any(ContainsListCoveragePattern);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return andPattern.Conjuncts.Any(ContainsListCoveragePattern);

            case NotPattern { InnerPattern: not null } notPattern:
                return ContainsListCoveragePattern(notPattern.InnerPattern);

            default:
                return false;
        }
    }
}
