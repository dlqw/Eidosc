using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic.PatternCoverage;

internal static partial class PatternUsefulnessAnalyzer
{
    private enum ViewPatternFiniteClassification
    {
        Unknown,
        AlwaysMatch,
        NeverMatch
    }

    private enum FiniteDomainMatchTruth
    {
        Unknown,
        Match,
        NoMatch
    }

    private static bool TryGetExactTupleBoolCases(
        Pattern pattern,
        out int arity,
        ISet<string> witnesses)
    {
        return TryCollectExactTupleBoolCases(pattern, out arity, witnesses);
    }

    private static bool TryCollectExactTupleBoolCases(
        Pattern pattern,
        out int arity,
        ISet<string> witnesses)
    {
        arity = 0;
        switch (pattern)
        {
            case TuplePattern tuplePattern:
                return TryCollectTuplePatternCases(tuplePattern, out arity, witnesses);

            case AsPattern { InnerPattern: not null } asPattern:
                return TryCollectExactTupleBoolCases(asPattern.InnerPattern, out arity, witnesses);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var merged = new HashSet<string>(StringComparer.Ordinal);
                var mergedArity = -1;
                foreach (var alternative in orPattern.Alternatives)
                {
                    var alternativeWitnesses = new HashSet<string>(StringComparer.Ordinal);
                    if (!TryCollectExactTupleBoolCases(alternative, out var alternativeArity, alternativeWitnesses) ||
                        alternativeArity <= 0)
                    {
                        return false;
                    }

                    if (mergedArity < 0)
                    {
                        mergedArity = alternativeArity;
                    }
                    else if (mergedArity != alternativeArity)
                    {
                        return false;
                    }

                    merged.UnionWith(alternativeWitnesses);
                }

                if (mergedArity <= 0)
                {
                    return false;
                }

                arity = mergedArity;
                foreach (var witness in merged)
                {
                    witnesses.Add(witness);
                }

                return true;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                HashSet<string>? intersection = null;
                var intersectionArity = -1;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    var conjunctWitnesses = new HashSet<string>(StringComparer.Ordinal);
                    if (!TryCollectExactTupleBoolCases(conjunct, out var conjunctArity, conjunctWitnesses) ||
                        conjunctArity <= 0)
                    {
                        return false;
                    }

                    if (intersectionArity < 0)
                    {
                        intersectionArity = conjunctArity;
                    }
                    else if (intersectionArity != conjunctArity)
                    {
                        return false;
                    }

                    if (intersection == null)
                    {
                        intersection = conjunctWitnesses;
                    }
                    else
                    {
                        intersection.IntersectWith(conjunctWitnesses);
                    }
                }

                if (intersectionArity <= 0 || intersection == null)
                {
                    return false;
                }

                arity = intersectionArity;
                foreach (var witness in intersection)
                {
                    witnesses.Add(witness);
                }

                return true;
            }

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerWitnesses = new HashSet<string>(StringComparer.Ordinal);
                if (!TryCollectExactTupleBoolCases(notPattern.InnerPattern, out var innerArity, innerWitnesses) ||
                    innerArity <= 0 ||
                    innerArity > 6)
                {
                    return false;
                }

                arity = innerArity;
                var universe = GenerateTupleBoolWitnesses(innerArity);
                foreach (var candidate in universe)
                {
                    if (!innerWitnesses.Contains(candidate))
                    {
                        witnesses.Add(candidate);
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }

    private static bool TryCollectTuplePatternCases(
        TuplePattern tuplePattern,
        out int arity,
        ISet<string> witnesses)
    {
        arity = 0;
        if (tuplePattern.Elements.Count == 0)
        {
            return false;
        }

        var elementCases = new List<List<bool>>(tuplePattern.Elements.Count);
        foreach (var element in tuplePattern.Elements)
        {
            var cases = new HashSet<bool>();
            if (!TryGetExactBoolCases(element, cases) || cases.Count == 0)
            {
                return false;
            }

            elementCases.Add(cases.OrderBy(v => v).ToList());
        }

        arity = elementCases.Count;
        BuildTupleWitnesses(elementCases, 0, new List<bool>(arity), witnesses);
        return witnesses.Count > 0;
    }

    private static bool TryGetExactBoolCases(Pattern pattern, ISet<bool> cases)
    {
        switch (pattern)
        {
            case LiteralPattern { Type: LiteralType.Boolean, Value: bool value }:
                cases.Add(value);
                return true;

            case WildcardPattern:
            case VarPattern:
                cases.Add(false);
                cases.Add(true);
                return true;

            case AsPattern { InnerPattern: not null } asPattern:
                return TryGetExactBoolCases(asPattern.InnerPattern, cases);

            case AsPattern:
                cases.Add(false);
                cases.Add(true);
                return true;

            case OrPattern orPattern when orPattern.Alternatives.Count > 0:
            {
                var hasAlternative = false;
                foreach (var alternative in orPattern.Alternatives)
                {
                    var alternativeCases = new HashSet<bool>();
                    if (!TryGetExactBoolCases(alternative, alternativeCases))
                    {
                        return false;
                    }

                    hasAlternative = true;
                    foreach (var value in alternativeCases)
                    {
                        cases.Add(value);
                    }
                }

                return hasAlternative;
            }

            case AndPattern andPattern when andPattern.Conjuncts.Count > 0:
            {
                HashSet<bool>? intersection = null;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    var conjunctCases = new HashSet<bool>();
                    if (!TryGetExactBoolCases(conjunct, conjunctCases))
                    {
                        return false;
                    }

                    if (intersection == null)
                    {
                        intersection = conjunctCases;
                    }
                    else
                    {
                        intersection.IntersectWith(conjunctCases);
                    }
                }

                if (intersection == null)
                {
                    return false;
                }

                foreach (var value in intersection)
                {
                    cases.Add(value);
                }

                return true;
            }

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerCases = new HashSet<bool>();
                if (!TryGetExactBoolCases(notPattern.InnerPattern, innerCases))
                {
                    return false;
                }

                if (!innerCases.Contains(true))
                {
                    cases.Add(true);
                }

                if (!innerCases.Contains(false))
                {
                    cases.Add(false);
                }

                return true;
            }

            case ViewPattern viewPattern when IsPatternIrrefutableForFiniteCoverage(viewPattern.InnerPattern):
                cases.Add(false);
                cases.Add(true);
                return true;

            case ViewPattern { InnerPattern: not null, IsTransparentIdentityView: true } viewPattern:
                return TryGetExactBoolCases(viewPattern.InnerPattern, cases);

            case ViewPattern viewPattern:
            {
                var viewClass = ClassifyViewPatternOverBoolFiniteDomain(viewPattern.InnerPattern);
                if (viewClass is ViewPatternFiniteClassification.AlwaysMatch)
                {
                    cases.Add(false);
                    cases.Add(true);
                    return true;
                }

                if (viewClass is ViewPatternFiniteClassification.NeverMatch)
                {
                    return true;
                }

                return false;
            }

            default:
                return false;
        }
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
        if (!TryGetExactBoolCases(innerPattern, cases))
        {
            return ViewPatternFiniteClassification.Unknown;
        }

        if (cases.Count == 0)
        {
            return ViewPatternFiniteClassification.NeverMatch;
        }

        return cases.Contains(false) && cases.Contains(true)
            ? ViewPatternFiniteClassification.AlwaysMatch
            : ViewPatternFiniteClassification.Unknown;
    }

    private static ViewPatternFiniteClassification ClassifyViewPatternOverIntFiniteDomain(
        Pattern? innerPattern,
        IReadOnlyList<string> domainTokens)
    {
        if (IsPatternIrrefutableForFiniteCoverage(innerPattern))
        {
            return ViewPatternFiniteClassification.AlwaysMatch;
        }

        if (innerPattern == null)
        {
            return ViewPatternFiniteClassification.AlwaysMatch;
        }

        if (!TryGetExactIntTokenCases(innerPattern, domainTokens, out var cases))
        {
            return ViewPatternFiniteClassification.Unknown;
        }

        if (cases.Count == 0)
        {
            return ViewPatternFiniteClassification.NeverMatch;
        }

        return cases.Count == domainTokens.Count
            ? ViewPatternFiniteClassification.AlwaysMatch
            : ViewPatternFiniteClassification.Unknown;
    }

    private static bool IsPatternIrrefutableForFiniteCoverage(Pattern? pattern)
    {
        if (pattern == null)
        {
            return true;
        }

        return pattern switch
        {
            WildcardPattern => true,
            VarPattern => true,
            TuplePattern tuplePattern => tuplePattern.Elements.All(IsPatternIrrefutableForFiniteCoverage),
            AsPattern { InnerPattern: null } => true,
            AsPattern asPattern => IsPatternIrrefutableForFiniteCoverage(asPattern.InnerPattern),
            OrPattern { Alternatives.Count: > 0 } orPattern =>
                orPattern.Alternatives.Any(IsPatternIrrefutableForFiniteCoverage),
            AndPattern { Conjuncts.Count: > 0 } andPattern =>
                andPattern.Conjuncts.All(IsPatternIrrefutableForFiniteCoverage),
            ViewPattern viewPattern =>
                IsPatternIrrefutableForFiniteCoverage(viewPattern.InnerPattern),
            _ => false
        };
    }

    private static void BuildTupleWitnesses(
        IReadOnlyList<List<bool>> elementCases,
        int index,
        List<bool> current,
        ISet<string> witnesses)
    {
        if (index >= elementCases.Count)
        {
            witnesses.Add(FormatTupleWitness(current));
            return;
        }

        var cases = elementCases[index];
        for (var i = 0; i < cases.Count; i++)
        {
            current.Add(cases[i]);
            BuildTupleWitnesses(elementCases, index + 1, current, witnesses);
            current.RemoveAt(current.Count - 1);
        }
    }

    private static List<string> GenerateTupleBoolWitnesses(int arity)
    {
        var all = new List<string>();
        var current = new bool[arity];
        GenerateTupleBoolWitnessesRecursive(current, 0, all);
        return all;
    }

    private static void GenerateTupleBoolWitnessesRecursive(
        bool[] current,
        int index,
        ICollection<string> output)
    {
        if (index >= current.Length)
        {
            output.Add(FormatTupleWitness(current));
            return;
        }

        current[index] = false;
        GenerateTupleBoolWitnessesRecursive(current, index + 1, output);
        current[index] = true;
        GenerateTupleBoolWitnessesRecursive(current, index + 1, output);
    }

    private static string FormatTupleWitness(IReadOnlyList<bool> values)
    {
        return $"({string.Join(", ", values.Select(value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False))})";
    }
}
