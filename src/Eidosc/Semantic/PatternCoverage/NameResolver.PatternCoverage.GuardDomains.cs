using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static DeterministicNonViewMatchTruth EvaluateTupleBoolPatternDeterministicNonViewTruth(
        Pattern pattern,
        IReadOnlyList<bool> tupleValues)
    {
        switch (pattern)
        {
            case TuplePattern tuplePattern:
                return EvaluateTuplePatternDeterministicNonViewTruth(tuplePattern, tupleValues);

            case AsPattern { InnerPattern: not null } asPattern:
                return EvaluateTupleBoolPatternDeterministicNonViewTruth(
                    asPattern.InnerPattern,
                    tupleValues);

            case AsPattern:
                return DeterministicNonViewMatchTruth.Match;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var sawUnknown = false;
                for (var i = 0; i < orPattern.Alternatives.Count; i++)
                {
                    var alternativeTruth = EvaluateTupleBoolPatternDeterministicNonViewTruth(
                        orPattern.Alternatives[i],
                        tupleValues);
                    if (alternativeTruth is DeterministicNonViewMatchTruth.Match)
                    {
                        return DeterministicNonViewMatchTruth.Match;
                    }

                    if (alternativeTruth is DeterministicNonViewMatchTruth.Unknown)
                    {
                        sawUnknown = true;
                    }
                }

                return sawUnknown
                    ? DeterministicNonViewMatchTruth.Unknown
                    : DeterministicNonViewMatchTruth.NoMatch;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                var sawUnknown = false;
                for (var i = 0; i < andPattern.Conjuncts.Count; i++)
                {
                    var conjunctTruth = EvaluateTupleBoolPatternDeterministicNonViewTruth(
                        andPattern.Conjuncts[i],
                        tupleValues);
                    if (conjunctTruth is DeterministicNonViewMatchTruth.NoMatch)
                    {
                        return DeterministicNonViewMatchTruth.NoMatch;
                    }

                    if (conjunctTruth is DeterministicNonViewMatchTruth.Unknown)
                    {
                        sawUnknown = true;
                    }
                }

                return sawUnknown
                    ? DeterministicNonViewMatchTruth.Unknown
                    : DeterministicNonViewMatchTruth.Match;
            }

            case NotPattern { InnerPattern: not null } notPattern:
                return InvertDeterministicNonViewMatchTruth(
                    EvaluateTupleBoolPatternDeterministicNonViewTruth(
                        notPattern.InnerPattern,
                        tupleValues));

            default:
                return DeterministicNonViewMatchTruth.NoMatch;
        }
    }

    private static DeterministicNonViewMatchTruth EvaluateTuplePatternDeterministicNonViewTruth(
        TuplePattern tuplePattern,
        IReadOnlyList<bool> tupleValues)
    {
        if (tuplePattern.Elements.Count != tupleValues.Count)
        {
            return DeterministicNonViewMatchTruth.NoMatch;
        }

        var sawUnknown = false;
        for (var i = 0; i < tuplePattern.Elements.Count; i++)
        {
            var elementTruth = EvaluateBoolPatternDeterministicNonViewTruth(
                tuplePattern.Elements[i],
                tupleValues[i]);
            if (elementTruth is DeterministicNonViewMatchTruth.NoMatch)
            {
                return DeterministicNonViewMatchTruth.NoMatch;
            }

            if (elementTruth is DeterministicNonViewMatchTruth.Unknown)
            {
                sawUnknown = true;
            }
        }

        return sawUnknown
            ? DeterministicNonViewMatchTruth.Unknown
            : DeterministicNonViewMatchTruth.Match;
    }

    private enum ViewPatternFiniteClassification
    {
        Unknown,
        AlwaysMatch,
        NeverMatch
    }

    private static ViewPatternFiniteClassification ClassifyViewPatternOverBoolFiniteDomain(Pattern? innerPattern)
    {
        if (IsPatternIrrefutableForFiniteCoverage(innerPattern))
        {
            return ViewPatternFiniteClassification.AlwaysMatch;
        }

        if (innerPattern == null)
        {
            return ViewPatternFiniteClassification.AlwaysMatch;
        }

        var cases = new HashSet<bool>();
        if (!TryGetExactBoolPatternCases(innerPattern, cases))
        {
            return ViewPatternFiniteClassification.Unknown;
        }

        if (cases.Count == 0)
        {
            return ViewPatternFiniteClassification.NeverMatch;
        }

        return cases.Contains(true) && cases.Contains(false)
            ? ViewPatternFiniteClassification.AlwaysMatch
            : ViewPatternFiniteClassification.Unknown;
    }

    private static ViewPatternFiniteClassification ClassifyViewPatternOverIntFiniteDomain(
        Pattern? innerPattern,
        IReadOnlySet<long> domainValues)
    {
        if (IsPatternIrrefutableForFiniteCoverage(innerPattern))
        {
            return ViewPatternFiniteClassification.AlwaysMatch;
        }

        if (innerPattern == null)
        {
            return ViewPatternFiniteClassification.AlwaysMatch;
        }

        var sawMatch = false;
        var sawMiss = false;

        foreach (var value in domainValues.OrderBy(v => v))
        {
            if (!TryPatternMatchesIntDomainTokenForCoverage(
                    innerPattern,
                    value,
                    isOther: false,
                    domainValues,
                    out var matches))
            {
                return ViewPatternFiniteClassification.Unknown;
            }

            if (matches)
            {
                sawMatch = true;
            }
            else
            {
                sawMiss = true;
            }
        }

        if (!TryPatternMatchesIntDomainTokenForCoverage(
                innerPattern,
                value: 0,
                isOther: true,
                domainValues,
                out var otherMatches))
        {
            return ViewPatternFiniteClassification.Unknown;
        }

        if (otherMatches)
        {
            sawMatch = true;
        }
        else
        {
            sawMiss = true;
        }

        if (!sawMatch)
        {
            return ViewPatternFiniteClassification.NeverMatch;
        }

        return !sawMiss
            ? ViewPatternFiniteClassification.AlwaysMatch
            : ViewPatternFiniteClassification.Unknown;
    }

    private static bool TryPatternMatchesIntDomainTokenForCoverage(
        Pattern pattern,
        long value,
        bool isOther,
        IReadOnlySet<long> domainValues,
        out bool matches)
    {
        var truth = EvaluateIntDomainTokenTruthForCoverage(pattern, value, isOther, domainValues);
        matches = truth is CoverageIntDomainMatchTruth.Match;
        return truth is not CoverageIntDomainMatchTruth.Unknown;
    }

    private static CoverageIntDomainMatchTruth EvaluateIntDomainTokenTruthForCoverage(
        Pattern pattern,
        long value,
        bool isOther,
        IReadOnlySet<long> domainValues)
    {
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                if (!TryGetIntegerLiteralPatternValue(literalPattern, out var literalValue))
                {
                    return CoverageIntDomainMatchTruth.Unknown;
                }

                return !isOther && value == literalValue
                    ? CoverageIntDomainMatchTruth.Match
                    : CoverageIntDomainMatchTruth.NoMatch;

            case RangePattern rangePattern:
                if (!TryGetIntegerRangePatternBounds(rangePattern, out var start, out var end))
                {
                    return CoverageIntDomainMatchTruth.Unknown;
                }

                return !isOther && value >= start && value <= end
                    ? CoverageIntDomainMatchTruth.Match
                    : CoverageIntDomainMatchTruth.NoMatch;

            case WildcardPattern:
            case VarPattern:
                return CoverageIntDomainMatchTruth.Match;

            case AsPattern asPattern:
                if (asPattern.InnerPattern == null)
                {
                    return CoverageIntDomainMatchTruth.Match;
                }

                return EvaluateIntDomainTokenTruthForCoverage(
                    asPattern.InnerPattern,
                    value,
                    isOther,
                    domainValues);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var sawUnknown = false;
                foreach (var alternative in orPattern.Alternatives)
                {
                    var altTruth = EvaluateIntDomainTokenTruthForCoverage(
                        alternative,
                        value,
                        isOther,
                        domainValues);
                    if (altTruth is CoverageIntDomainMatchTruth.Match)
                    {
                        return CoverageIntDomainMatchTruth.Match;
                    }

                    if (altTruth is CoverageIntDomainMatchTruth.Unknown)
                    {
                        sawUnknown = true;
                    }
                }

                return sawUnknown
                    ? CoverageIntDomainMatchTruth.Unknown
                    : CoverageIntDomainMatchTruth.NoMatch;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                var sawUnknown = false;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    var conjunctTruth = EvaluateIntDomainTokenTruthForCoverage(
                        conjunct,
                        value,
                        isOther,
                        domainValues);
                    if (conjunctTruth is CoverageIntDomainMatchTruth.NoMatch)
                    {
                        return CoverageIntDomainMatchTruth.NoMatch;
                    }

                    if (conjunctTruth is CoverageIntDomainMatchTruth.Unknown)
                    {
                        sawUnknown = true;
                    }
                }

                return sawUnknown
                    ? CoverageIntDomainMatchTruth.Unknown
                    : CoverageIntDomainMatchTruth.Match;
            }

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerTruth = EvaluateIntDomainTokenTruthForCoverage(
                    notPattern.InnerPattern,
                    value,
                    isOther,
                    domainValues);
                return innerTruth switch
                {
                    CoverageIntDomainMatchTruth.Match => CoverageIntDomainMatchTruth.NoMatch,
                    CoverageIntDomainMatchTruth.NoMatch => CoverageIntDomainMatchTruth.Match,
                    _ => CoverageIntDomainMatchTruth.Unknown
                };
            }

            case ViewPattern viewPattern:
                if (viewPattern.InnerPattern == null)
                {
                    return CoverageIntDomainMatchTruth.Match;
                }

                return EvaluateIntDomainTokenTruthForCoverage(
                    viewPattern.InnerPattern,
                    value,
                    isOther,
                    domainValues);

            default:
                return CoverageIntDomainMatchTruth.Unknown;
        }
    }
}
