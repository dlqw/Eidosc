using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static bool TryMatchAllBoolValuesViaDeterministicNonViewPath(
        Pattern pattern,
        IReadOnlyCollection<bool> values)
    {
        foreach (var value in values)
        {
            if (!TryMatchBoolPatternViaDeterministicNonViewPath(pattern, value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryMatchAllIntValuesViaDeterministicNonViewPath(
        Pattern pattern,
        IReadOnlyCollection<long> values)
    {
        foreach (var value in values)
        {
            if (!TryMatchIntPatternViaDeterministicNonViewPath(pattern, value))
            {
                return false;
            }
        }

        return true;
    }

    private enum DeterministicNonViewMatchTruth
    {
        NoMatch,
        Match,
        Unknown
    }

    private static bool TryMatchBoolPatternViaDeterministicNonViewPath(Pattern pattern, bool value)
    {
        return EvaluateBoolPatternDeterministicNonViewTruth(pattern, value) is DeterministicNonViewMatchTruth.Match;
    }

    private static bool TryMatchIntPatternViaDeterministicNonViewPath(Pattern pattern, long value)
    {
        return EvaluateIntPatternDeterministicNonViewTruth(pattern, value) is DeterministicNonViewMatchTruth.Match;
    }

    private static DeterministicNonViewMatchTruth EvaluateBoolPatternDeterministicNonViewTruth(
        Pattern pattern,
        bool value)
    {
        return EvaluateDeterministicScalarTruth(
            pattern,
            value,
            static (candidateValue, candidatePattern) => candidatePattern switch
            {
                LiteralPattern { Type: LiteralType.Boolean, Value: bool literalValue } =>
                    literalValue == candidateValue
                        ? DeterministicNonViewMatchTruth.Match
                        : DeterministicNonViewMatchTruth.NoMatch,
                _ => DeterministicNonViewMatchTruth.NoMatch
            },
            EvaluateBoolPatternDeterministicNonViewTruth);
    }

    private static DeterministicNonViewMatchTruth EvaluateIntPatternDeterministicNonViewTruth(
        Pattern pattern,
        long value)
    {
        return EvaluateDeterministicScalarTruth(
            pattern,
            value,
            static (candidateValue, candidatePattern) =>
            {
                switch (candidatePattern)
                {
                    case LiteralPattern literalPattern:
                        if (!TryGetIntegerLiteralPatternValue(literalPattern, out var literalValue))
                        {
                            return DeterministicNonViewMatchTruth.NoMatch;
                        }

                        return literalValue == candidateValue
                            ? DeterministicNonViewMatchTruth.Match
                            : DeterministicNonViewMatchTruth.NoMatch;

                    case RangePattern rangePattern:
                        if (!TryGetIntegerRangePatternBounds(rangePattern, out var start, out var end))
                        {
                            return DeterministicNonViewMatchTruth.NoMatch;
                        }

                        return candidateValue >= start && candidateValue <= end
                            ? DeterministicNonViewMatchTruth.Match
                            : DeterministicNonViewMatchTruth.NoMatch;

                    default:
                        return DeterministicNonViewMatchTruth.NoMatch;
                }
            },
            EvaluateIntPatternDeterministicNonViewTruth);
    }

    private static DeterministicNonViewMatchTruth EvaluateDeterministicScalarTruth<TValue>(
        Pattern pattern,
        TValue value,
        Func<TValue, Pattern, DeterministicNonViewMatchTruth> leafEvaluator,
        Func<Pattern, TValue, DeterministicNonViewMatchTruth> recursiveEvaluator)
    {
        switch (pattern)
        {
            case WildcardPattern:
            case VarPattern:
            case AsPattern { InnerPattern: null }:
                return DeterministicNonViewMatchTruth.Match;

            case AsPattern asPattern:
                return recursiveEvaluator(asPattern.InnerPattern, value);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return EvaluateAnyDeterministicListTruth(
                    orPattern.Alternatives,
                    candidate => recursiveEvaluator(candidate, value));

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return EvaluateAllDeterministicListTruth(
                    andPattern.Conjuncts,
                    candidate => recursiveEvaluator(candidate, value));

            case NotPattern { InnerPattern: not null } notPattern:
                return InvertDeterministicNonViewMatchTruth(
                    recursiveEvaluator(notPattern.InnerPattern, value));

            case ViewPattern { InnerPattern: not null, IsTransparentIdentityView: true } viewPattern:
                return recursiveEvaluator(viewPattern.InnerPattern, value);

            case ViewPattern:
                return DeterministicNonViewMatchTruth.Unknown;

            default:
                return leafEvaluator(value, pattern);
        }
    }

    private static DeterministicNonViewMatchTruth InvertDeterministicNonViewMatchTruth(
        DeterministicNonViewMatchTruth truth)
    {
        return truth switch
        {
            DeterministicNonViewMatchTruth.Match => DeterministicNonViewMatchTruth.NoMatch,
            DeterministicNonViewMatchTruth.NoMatch => DeterministicNonViewMatchTruth.Match,
            _ => DeterministicNonViewMatchTruth.Unknown
        };
    }
}
