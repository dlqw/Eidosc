using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static void AddIntAndBoolTargetDomainReasons(
        ISet<string> reasons,
        string prefix,
        Pattern? pattern,
        IReadOnlyCollection<long> intDomainValues,
        bool hasBoolDomain)
    {
        if (intDomainValues.Count > 0)
        {
            reasons.Add($"{prefix}:target-domain-int");
            if (HasCharLiteralOrRangePattern(pattern))
            {
                reasons.Add($"{prefix}:target-domain-char");
            }
        }
        else if (HasCharLiteralOrRangePattern(pattern))
        {
            reasons.Add($"{prefix}:target-domain-char");
        }

        if (hasBoolDomain)
        {
            reasons.Add($"{prefix}:target-domain-bool");
        }
    }

    private static bool TryCollectListUnresolvedGuardTargetDomains(
        Pattern? pattern,
        out HashSet<long> intDomainValues,
        out bool hasBoolDomain)
    {
        intDomainValues = [];
        hasBoolDomain = false;
        if (pattern == null ||
            !PatternUsefulnessAnalyzer.TryGetExactListCoverageCases(
                pattern,
                out var exactCases,
                preferBoolVectorSplit: true))
        {
            return false;
        }

        foreach (var listCase in exactCases)
        {
            if (TryParseListCaseIntVector(listCase, out var intTokens))
            {
                for (var i = 0; i < intTokens.Count; i++)
                {
                    if (!intTokens[i].IsOtherBucket)
                    {
                        intDomainValues.Add(intTokens[i].Value);
                    }
                }

                continue;
            }

            if (TryParseListCaseBoolVector(listCase, out var boolValues) &&
                boolValues.Count > 0)
            {
                hasBoolDomain = true;
            }
        }

        return exactCases.Count > 0;
    }

    private bool TryCollectAdtUnresolvedGuardTargetDomains(
        PatternUsefulnessBranchFact branch,
        out HashSet<long> intDomainValues,
        out bool hasBoolDomain)
    {
        intDomainValues = [];
        hasBoolDomain = false;
        if (branch.Pattern == null)
        {
            return false;
        }

        var targetAdt = branch.AdtCoverageAdt;
        if (!targetAdt.IsValid &&
            !TryResolveAdtCoverageTarget(branch.Pattern, _symbolTable, out targetAdt))
        {
            return false;
        }

        HashSet<SymbolId> targetConstructors;
        if (branch.AdtCoverageConstructors.Count > 0)
        {
            targetConstructors = branch.AdtCoverageConstructors.ToHashSet();
        }
        else if (PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                     branch.Pattern,
                     _symbolTable,
                     out var exactAdt,
                     out var exactConstructors) &&
                 exactAdt == targetAdt &&
                 exactConstructors.Count > 0)
        {
            targetConstructors = exactConstructors.ToHashSet();
        }
        else
        {
            return false;
        }

        CollectAdtSuppressionTargetDomains(
            branch.Pattern,
            _symbolTable,
            targetAdt,
            targetConstructors,
            intDomainValues,
            out hasBoolDomain);

        if (intDomainValues.Count == 0)
        {
            CollectFiniteIntDomainCandidatesFromPattern(branch.Pattern, intDomainValues);
        }

        return true;
    }

    private static void CollectFiniteIntDomainCandidatesFromPattern(
        Pattern? pattern,
        ISet<long> output,
        int maxCandidateCount = 64)
    {
        if (pattern == null || output.Count >= maxCandidateCount)
        {
            return;
        }

        if (TryGetFiniteIntPatternValues(pattern, out var finiteValues))
        {
            foreach (var value in finiteValues.OrderBy(v => v))
            {
                output.Add(value);
                if (output.Count >= maxCandidateCount)
                {
                    return;
                }
            }

            if (finiteValues.Count > 0)
            {
                return;
            }
        }

        VisitChildPatterns(pattern, VisitChild);

        void VisitChild(Pattern? childPattern)
        {
            if (output.Count >= maxCandidateCount)
            {
                return;
            }

            CollectFiniteIntDomainCandidatesFromPattern(childPattern, output, maxCandidateCount);
        }
    }

    private static bool TryCollectFiniteBoolDomainCandidatesFromPattern(
        Pattern? pattern,
        out HashSet<bool> values)
    {
        values = [];
        if (pattern == null)
        {
            return false;
        }

        CollectFiniteBoolDomainCandidatesFromPattern(pattern, values);
        return values.Count > 0;
    }

    private static void CollectFiniteBoolDomainCandidatesFromPattern(
        Pattern? pattern,
        ISet<bool> output)
    {
        if (pattern == null)
        {
            return;
        }

        var exactBoolCases = new HashSet<bool>();
        if (TryGetExactBoolPatternCases(pattern, exactBoolCases) &&
            exactBoolCases.Count > 0)
        {
            foreach (var boolCase in exactBoolCases)
            {
                output.Add(boolCase);
            }
        }

        VisitChildPatterns(pattern, childPattern => CollectFiniteBoolDomainCandidatesFromPattern(childPattern, output));
    }

    private static void VisitChildPatterns(Pattern pattern, Action<Pattern?> visitor)
    {
        switch (pattern)
        {
            case AsPattern asPattern:
                visitor(asPattern.InnerPattern);
                return;

            case ViewPattern viewPattern:
                visitor(viewPattern.InnerPattern);
                return;

            case NotPattern notPattern:
                visitor(notPattern.InnerPattern);
                return;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                for (var i = 0; i < orPattern.Alternatives.Count; i++)
                {
                    visitor(orPattern.Alternatives[i]);
                }

                return;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                for (var i = 0; i < andPattern.Conjuncts.Count; i++)
                {
                    visitor(andPattern.Conjuncts[i]);
                }

                return;

            case TuplePattern tuplePattern:
                for (var i = 0; i < tuplePattern.Elements.Count; i++)
                {
                    visitor(tuplePattern.Elements[i]);
                }

                return;

            case ListPattern listPattern:
                for (var i = 0; i < listPattern.Elements.Count; i++)
                {
                    visitor(listPattern.Elements[i]);
                }

                visitor(listPattern.RestPattern);

                for (var i = 0; i < listPattern.SuffixElements.Count; i++)
                {
                    visitor(listPattern.SuffixElements[i]);
                }
                return;

            case CtorPattern ctorPattern:
                for (var i = 0; i < ctorPattern.PositionalPatterns.Count; i++)
                {
                    visitor(ctorPattern.PositionalPatterns[i]);
                }

                for (var i = 0; i < ctorPattern.NamedPatterns.Count; i++)
                {
                    visitor(ctorPattern.NamedPatterns[i].Pattern);
                }

                return;
        }
    }

    private static bool HasCharLiteralOrRangePattern(Pattern? pattern)
    {
        if (pattern == null)
        {
            return false;
        }

        switch (pattern)
        {
            case LiteralPattern literalPattern:
                return literalPattern.Value is char;

            case RangePattern rangePattern:
                return rangePattern.Start?.Value is char || rangePattern.End?.Value is char;
        }

        var foundCharPattern = false;
        VisitChildPatterns(pattern, VisitChild);
        return foundCharPattern;

        void VisitChild(Pattern? childPattern)
        {
            if (!foundCharPattern && HasCharLiteralOrRangePattern(childPattern))
            {
                foundCharPattern = true;
            }
        }
    }
}
