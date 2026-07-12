using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private delegate bool TupleBoolArityCollector(
        Pattern pattern,
        out int arity,
        ISet<string> cases);

    private enum PatternMatchVisitResult
    {
        Continue,
        Exhausted,
        StopSuccess,
        StopFailure
    }

    private static bool TryGetExactTupleBoolPatternCases(
        Pattern? pattern,
        out int arity,
        ISet<string> cases)
    {
        if (pattern == null)
        {
            arity = 0;
            return false;
        }

        return TryCollectExactTupleBoolPatternCases(pattern, out arity, cases);
    }

    private static bool TryCollectExactTupleBoolPatternCases(
        Pattern pattern,
        out int arity,
        ISet<string> cases)
    {
        arity = 0;
        switch (pattern)
        {
            case TuplePattern tuplePattern:
                return TryCollectTuplePatternCases(tuplePattern, out arity, cases);

            case AsPattern { InnerPattern: not null } asPattern:
                return TryCollectExactTupleBoolPatternCases(asPattern.InnerPattern, out arity, cases);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryCollectTupleBoolCaseSetByAlternatives(
                    orPattern.Alternatives,
                    TryCollectExactTupleBoolPatternCases,
                    static (merged, current) => merged.UnionWith(current),
                    out arity,
                    cases);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryCollectTupleBoolCaseSetByAlternatives(
                    andPattern.Conjuncts,
                    TryCollectExactTupleBoolPatternCases,
                    static (merged, current) => merged.IntersectWith(current),
                    out arity,
                    cases);

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerCases = new HashSet<string>(StringComparer.Ordinal);
                if (!TryCollectExactTupleBoolPatternCases(notPattern.InnerPattern, out var innerArity, innerCases) ||
                    innerArity <= 0 ||
                    innerArity > 6)
                {
                    return false;
                }

                arity = innerArity;
                var universe = GenerateTupleBoolWitnesses(innerArity);
                foreach (var candidate in universe)
                {
                    if (!innerCases.Contains(candidate))
                    {
                        cases.Add(candidate);
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
        ISet<string> cases)
    {
        arity = 0;
        if (tuplePattern.Elements.Count == 0)
        {
            return false;
        }

        var elementCases = new List<List<bool>>(tuplePattern.Elements.Count);
        foreach (var element in tuplePattern.Elements)
        {
            var boolCases = new HashSet<bool>();
            if (!TryGetExactTupleBoolElementCases(element, boolCases))
            {
                return false;
            }

            elementCases.Add(boolCases.OrderBy(v => v).ToList());
        }

        arity = elementCases.Count;
        BuildTupleBoolWitnesses(elementCases, 0, new List<bool>(arity), cases);
        return true;
    }

    private static bool TryGetExactTupleBoolElementCases(Pattern pattern, ISet<bool> cases)
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
                return TryGetExactTupleBoolElementCases(asPattern.InnerPattern, cases);

            case AsPattern:
                cases.Add(false);
                cases.Add(true);
                return true;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryCollectTupleBoolElementCasesByAlternatives(
                    orPattern.Alternatives,
                    static (merged, current) => merged.UnionWith(current),
                    cases);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryCollectTupleBoolElementCasesByAlternatives(
                    andPattern.Conjuncts,
                    static (merged, current) => merged.IntersectWith(current),
                    cases);

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerCases = new HashSet<bool>();
                if (!TryGetExactTupleBoolElementCases(notPattern.InnerPattern, innerCases))
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

            default:
                return false;
        }
    }

    private static void BuildTupleBoolWitnesses(
        IReadOnlyList<List<bool>> elementCases,
        int index,
        List<bool> current,
        ISet<string> output)
    {
        if (index >= elementCases.Count)
        {
            output.Add(FormatTupleBoolWitness(current));
            return;
        }

        var cases = elementCases[index];
        for (var i = 0; i < cases.Count; i++)
        {
            current.Add(cases[i]);
            BuildTupleBoolWitnesses(elementCases, index + 1, current, output);
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
            output.Add(FormatTupleBoolWitness(current));
            return;
        }

        current[index] = false;
        GenerateTupleBoolWitnessesRecursive(current, index + 1, output);
        current[index] = true;
        GenerateTupleBoolWitnessesRecursive(current, index + 1, output);
    }

    private static string FormatTupleBoolWitness(IReadOnlyList<bool> values)
    {
        return $"({string.Join(", ", values.Select(value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False))})";
    }

    private static bool TryParseTupleBoolWitness(
        string witness,
        int arity,
        out IReadOnlyList<bool> values)
    {
        values = [];
        if (arity <= 0)
        {
            return false;
        }

        var trimmed = witness.Trim();
        if (!trimmed.StartsWith('(') || !trimmed.EndsWith(')'))
        {
            return false;
        }

        var inner = trimmed[1..^1];
        var segments = inner
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != arity)
        {
            return false;
        }

        var parsed = new bool[arity];
        for (var i = 0; i < segments.Length; i++)
        {
            if (string.Equals(segments[i], WellKnownStrings.AdditionalKeywords.True, StringComparison.Ordinal))
            {
                parsed[i] = true;
                continue;
            }

            if (string.Equals(segments[i], WellKnownStrings.AdditionalKeywords.False, StringComparison.Ordinal))
            {
                parsed[i] = false;
                continue;
            }

            return false;
        }

        values = parsed;
        return true;
    }

    private static bool TryMatchTupleBoolPatternWithBindings(
        Pattern pattern,
        IReadOnlyList<bool> tupleValues,
        out Dictionary<string, bool> bindings)
    {
        bindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        switch (pattern)
        {
            case TuplePattern tuplePattern:
                return TryMatchTuplePatternWithBindings(tuplePattern, tupleValues, out bindings);

            case AsPattern { InnerPattern: not null } asPattern:
                return TryMatchTupleBoolPatternWithBindings(asPattern.InnerPattern, tupleValues, out bindings);

            case AsPattern:
                return true;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryMatchAnyTupleBoolPattern(
                    orPattern.Alternatives,
                    candidate => TryMatchTupleBoolPatternWithBindings(candidate, tupleValues, out var candidateBindings)
                        ? candidateBindings
                        : null,
                    out bindings);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryMatchAllTupleBoolPatterns(
                    andPattern.Conjuncts,
                    candidate => TryMatchTupleBoolPatternWithBindings(candidate, tupleValues, out var candidateBindings)
                        ? candidateBindings
                        : null,
                    out bindings);

            case NotPattern { InnerPattern: not null } notPattern:
                if (TryMatchTupleBoolPatternWithBindings(notPattern.InnerPattern, tupleValues, out _))
                {
                    return false;
                }

                return true;

            default:
                return false;
        }
    }

    private static bool TryMatchTuplePatternWithBindings(
        TuplePattern tuplePattern,
        IReadOnlyList<bool> tupleValues,
        out Dictionary<string, bool> bindings)
    {
        bindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (tuplePattern.Elements.Count != tupleValues.Count)
        {
            return false;
        }

        for (var i = 0; i < tuplePattern.Elements.Count; i++)
        {
            if (!TryMatchTupleBoolElementPattern(tuplePattern.Elements[i], tupleValues[i], out var elementBindings) ||
                !TryMergeTupleBoolBindings(bindings, elementBindings, out bindings))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryMatchTupleBoolElementPattern(
        Pattern pattern,
        bool value,
        out Dictionary<string, bool> bindings)
    {
        bindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        switch (pattern)
        {
            case LiteralPattern { Type: LiteralType.Boolean, Value: bool literalValue }:
                return literalValue == value;

            case WildcardPattern:
                return true;

            case VarPattern varPattern:
                return TryBindTupleBoolVariable(bindings, varPattern.Name, value);

            case AsPattern asPattern:
            {
                Dictionary<string, bool> innerBindings;
                if (asPattern.InnerPattern == null)
                {
                    innerBindings = new Dictionary<string, bool>(StringComparer.Ordinal);
                }
                else if (!TryMatchTupleBoolElementPattern(asPattern.InnerPattern, value, out innerBindings))
                {
                    return false;
                }

                if (!TryBindTupleBoolVariable(innerBindings, asPattern.BindingName, value))
                {
                    return false;
                }

                bindings = innerBindings;
                return true;
            }

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryMatchAnyTupleBoolPattern(
                    orPattern.Alternatives,
                    candidate => TryMatchTupleBoolElementPattern(candidate, value, out var candidateBindings)
                        ? candidateBindings
                        : null,
                    out bindings);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryMatchAllTupleBoolPatterns(
                    andPattern.Conjuncts,
                    candidate => TryMatchTupleBoolElementPattern(candidate, value, out var candidateBindings)
                        ? candidateBindings
                        : null,
                    out bindings);

            case NotPattern { InnerPattern: not null } notPattern:
                if (TryMatchTupleBoolElementPattern(notPattern.InnerPattern, value, out _))
                {
                    return false;
                }

                return true;

            case ViewPattern { InnerPattern: not null, IsTransparentIdentityView: true } viewPattern:
                return TryMatchTupleBoolElementPattern(viewPattern.InnerPattern, value, out bindings);

            case ViewPattern viewPattern when IsPatternIrrefutableForFiniteCoverage(viewPattern.InnerPattern):
                return true;

            case ViewPattern viewPattern:
            {
                var classification = ClassifyViewPatternOverBoolFiniteDomain(viewPattern.InnerPattern);
                return classification switch
                {
                    ViewPatternFiniteClassification.AlwaysMatch => true,
                    ViewPatternFiniteClassification.NeverMatch => false,
                    _ => false
                };
            }

            default:
                return false;
        }
    }

    private static bool TryCollectTupleBoolCaseSetByAlternatives(
        IReadOnlyList<Pattern> patterns,
        TupleBoolArityCollector collector,
        Action<HashSet<string>, HashSet<string>> merger,
        out int arity,
        ISet<string> cases)
    {
        var resolvedArity = -1;
        var collected = TryMergeCollectedCaseSets(
            patterns,
            pattern =>
            {
                var candidateCases = new HashSet<string>(StringComparer.Ordinal);
                if (!collector(pattern, out var candidateArity, candidateCases) ||
                    candidateArity <= 0)
                {
                    return null;
                }

                if (resolvedArity < 0)
                {
                    resolvedArity = candidateArity;
                }
                else if (resolvedArity != candidateArity)
                {
                    return null;
                }

                return candidateCases;
            },
            merger,
            cases);

        arity = resolvedArity;
        return collected && arity > 0;
    }

    private static bool TryCollectTupleBoolElementCasesByAlternatives(
        IReadOnlyList<Pattern> patterns,
        Action<HashSet<bool>, HashSet<bool>> merger,
        ISet<bool> cases)
    {
        return TryMergeCollectedCaseSets(
            patterns,
            pattern =>
            {
                var candidateCases = new HashSet<bool>();
                return TryGetExactTupleBoolElementCases(pattern, candidateCases)
                    ? candidateCases
                    : null;
            },
            merger,
            cases);
    }

    private static bool TryMergeCollectedCaseSets<TCase>(
        IReadOnlyList<Pattern> patterns,
        Func<Pattern, HashSet<TCase>?> collector,
        Action<HashSet<TCase>, HashSet<TCase>> merger,
        ISet<TCase> output)
        where TCase : notnull
    {
        HashSet<TCase>? merged = null;
        foreach (var pattern in patterns)
        {
            var candidateCases = collector(pattern);
            if (candidateCases == null)
            {
                return false;
            }

            if (merged == null)
            {
                merged = candidateCases;
            }
            else
            {
                merger(merged, candidateCases);
            }
        }

        if (merged == null)
        {
            return false;
        }

        foreach (var value in merged)
        {
            output.Add(value);
        }

        return true;
    }

    private static bool TryMatchAnyTupleBoolPattern(
        IReadOnlyList<Pattern> patterns,
        Func<Pattern, Dictionary<string, bool>?> matcher,
        out Dictionary<string, bool> bindings)
    {
        Dictionary<string, bool>? matchedBindings = null;
        var result = TryVisitTupleBoolPatternMatches(
            patterns,
            matcher,
            candidateBindings =>
            {
                if (candidateBindings == null)
                {
                    return PatternMatchVisitResult.Continue;
                }

                matchedBindings = candidateBindings;
                return PatternMatchVisitResult.StopSuccess;
            });

        bindings = matchedBindings ?? [];
        return result == PatternMatchVisitResult.StopSuccess;
    }

    private static bool TryMatchAllTupleBoolPatterns(
        IReadOnlyList<Pattern> patterns,
        Func<Pattern, Dictionary<string, bool>?> matcher,
        out Dictionary<string, bool> bindings)
    {
        var current = new Dictionary<string, bool>(StringComparer.Ordinal);
        var matched = TryVisitTupleBoolPatternMatches(
            patterns,
            matcher,
            candidateBindings =>
            {
                if (candidateBindings == null ||
                    !TryMergeTupleBoolBindings(current, candidateBindings, out current))
                {
                    return PatternMatchVisitResult.StopFailure;
                }

                return PatternMatchVisitResult.Continue;
            });

        if (matched == PatternMatchVisitResult.StopFailure)
        {
            bindings = [];
            return false;
        }

        if (matched != PatternMatchVisitResult.Exhausted)
        {
            bindings = [];
            return false;
        }

        bindings = current;
        return true;
    }

    private static PatternMatchVisitResult TryVisitTupleBoolPatternMatches(
        IReadOnlyList<Pattern> patterns,
        Func<Pattern, Dictionary<string, bool>?> matcher,
        Func<Dictionary<string, bool>?, PatternMatchVisitResult> visitor)
    {
        foreach (var pattern in patterns)
        {
            var candidateBindings = matcher(pattern);
            var visitResult = visitor(candidateBindings);
            if (visitResult == PatternMatchVisitResult.StopSuccess)
            {
                return PatternMatchVisitResult.StopSuccess;
            }

            if (visitResult == PatternMatchVisitResult.StopFailure)
            {
                return PatternMatchVisitResult.StopFailure;
            }
        }

        return PatternMatchVisitResult.Exhausted;
    }
}
