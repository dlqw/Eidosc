using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static void AddViewPathReasons(
        ISet<string> reasons,
        string prefix,
        IReadOnlyList<string> nonFiniteViewPaths,
        IReadOnlyCollection<long> intDomainValues,
        bool includeIntDomain,
        bool includeBoolDomain,
        Pattern? pattern)
    {
        if (nonFiniteViewPaths.Count > 0)
        {
            AddNonFiniteViewPathReasons(reasons, prefix, nonFiniteViewPaths);
        }

        var uncertainViewPaths = GetUncertainRefutableViewPatternPathsForSuppression(
            pattern,
            intDomainValues.ToHashSet(),
            includeIntDomain,
            includeBoolDomain);
        if (uncertainViewPaths.Count > 0)
        {
            AddUncertainViewPathReasons(reasons, prefix, uncertainViewPaths);
        }
    }

    private static void AddNonFiniteViewPathReasons(
        ISet<string> reasons,
        string targetPrefix,
        IReadOnlyList<string> nonFiniteViewPaths)
    {
        if (nonFiniteViewPaths.Count == 0)
        {
            return;
        }

        reasons.Add($"{targetPrefix}:view-inner-nonfinite");
        const int maxDetailedPathReasonCount = 4;
        foreach (var path in nonFiniteViewPaths.Take(maxDetailedPathReasonCount))
        {
            reasons.Add($"{targetPrefix}:view-inner-nonfinite@{path}");
        }

        if (nonFiniteViewPaths.Count > maxDetailedPathReasonCount)
        {
            reasons.Add(
                $"{targetPrefix}:view-inner-nonfinite@...(+{nonFiniteViewPaths.Count - maxDetailedPathReasonCount})");
        }
    }

    private static void AddUncertainViewPathReasons(
        ISet<string> reasons,
        string targetPrefix,
        IReadOnlyList<string> uncertainViewPaths)
    {
        if (uncertainViewPaths.Count == 0)
        {
            return;
        }

        reasons.Add($"{targetPrefix}:view-inner-uncertain");
        const int maxDetailedPathReasonCount = 4;
        foreach (var path in uncertainViewPaths.Take(maxDetailedPathReasonCount))
        {
            reasons.Add($"{targetPrefix}:view-inner-uncertain@{path}");
        }

        if (uncertainViewPaths.Count > maxDetailedPathReasonCount)
        {
            reasons.Add(
                $"{targetPrefix}:view-inner-uncertain@...(+{uncertainViewPaths.Count - maxDetailedPathReasonCount})");
        }
    }

    private static IReadOnlyList<string> GetUncertainRefutableViewPatternPathsForSuppression(
        Pattern? pattern,
        IReadOnlySet<long> intDomainValues,
        bool includeIntDomain,
        bool includeBoolDomain)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        CollectUncertainRefutableViewPatternPaths(
            pattern,
            "pattern",
            intDomainValues,
            includeIntDomain,
            includeBoolDomain,
            paths);
        return paths
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectUncertainRefutableViewPatternPaths(
        Pattern? pattern,
        string path,
        IReadOnlySet<long> intDomainValues,
        bool includeIntDomain,
        bool includeBoolDomain,
        ISet<string> output)
    {
        if (pattern == null)
        {
            return;
        }

        switch (pattern)
        {
            case ViewPattern viewPattern:
            {
                if (viewPattern.InnerPattern == null)
                {
                    return;
                }

                var innerPath = $"{path}/view-inner";
                var innerPattern = viewPattern.InnerPattern;
                if (viewPattern.IsTransparentIdentityView)
                {
                    CollectUncertainRefutableViewPatternPaths(
                        innerPattern,
                        innerPath,
                        intDomainValues,
                        includeIntDomain,
                        includeBoolDomain,
                        output);
                    return;
                }

                if (!IsPatternIrrefutableForFiniteCoverage(innerPattern))
                {
                    var isUncertain = false;
                    if (includeIntDomain && intDomainValues.Count > 0)
                    {
                        isUncertain |= ClassifyViewPatternOverIntFiniteDomain(innerPattern, intDomainValues) is
                            ViewPatternFiniteClassification.Unknown;
                    }

                    if (includeBoolDomain)
                    {
                        isUncertain |= ClassifyViewPatternOverBoolFiniteDomain(innerPattern) is
                            ViewPatternFiniteClassification.Unknown;
                    }

                    if (isUncertain)
                    {
                        output.Add(innerPath);
                    }
                }

                CollectUncertainRefutableViewPatternPaths(
                    innerPattern,
                    innerPath,
                    intDomainValues,
                    includeIntDomain,
                    includeBoolDomain,
                    output);
                return;
            }
        }

        VisitNestedPatternPaths(pattern, path, VisitChild);
        return;

        void VisitChild(Pattern? childPattern, string childPath)
        {
            CollectUncertainRefutableViewPatternPaths(
                childPattern,
                childPath,
                intDomainValues,
                includeIntDomain,
                includeBoolDomain,
                output);
        }
    }

    private static IReadOnlyList<string> GetNonFiniteRefutableViewPatternPaths(Pattern? pattern)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        CollectNonFiniteRefutableViewPatternPaths(pattern, "pattern", paths);
        return paths
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectNonFiniteRefutableViewPatternPaths(
        Pattern? pattern,
        string path,
        ISet<string> output)
    {
        if (pattern == null)
        {
            return;
        }

        switch (pattern)
        {
            case ViewPattern viewPattern:
            {
                if (viewPattern.InnerPattern == null)
                {
                    return;
                }

                var innerPath = $"{path}/view-inner";
                var innerPattern = viewPattern.InnerPattern;
                if (viewPattern.IsTransparentIdentityView)
                {
                    CollectNonFiniteRefutableViewPatternPaths(innerPattern, innerPath, output);
                    return;
                }

                if (!IsPatternIrrefutableForFiniteCoverage(innerPattern))
                {
                    var exactBoolCases = new HashSet<bool>();
                    var hasExactBoolCases = TryGetExactBoolPatternCases(innerPattern, exactBoolCases);
                    var hasFiniteIntCases = TryGetFiniteIntPatternValues(innerPattern, out _);
                    if (!hasExactBoolCases && !hasFiniteIntCases)
                    {
                        output.Add(innerPath);
                    }
                }

                CollectNonFiniteRefutableViewPatternPaths(innerPattern, innerPath, output);
                return;
            }
        }

        VisitNestedPatternPaths(pattern, path, VisitChild);

        void VisitChild(Pattern? childPattern, string childPath)
        {
            CollectNonFiniteRefutableViewPatternPaths(childPattern, childPath, output);
        }
    }

    private static void VisitNestedPatternPaths(
        Pattern pattern,
        string path,
        Action<Pattern?, string> visitor)
    {
        switch (pattern)
        {
            case AsPattern asPattern:
                visitor(asPattern.InnerPattern, $"{path}/as-inner");
                return;

            case TuplePattern tuplePattern:
                for (var i = 0; i < tuplePattern.Elements.Count; i++)
                {
                    visitor(tuplePattern.Elements[i], $"{path}/tuple#{i + 1}");
                }

                return;

            case ListPattern listPattern:
                for (var i = 0; i < listPattern.Elements.Count; i++)
                {
                    visitor(listPattern.Elements[i], $"{path}/list-element#{i + 1}");
                }

                visitor(listPattern.RestPattern, $"{path}/list-rest");

                for (var i = 0; i < listPattern.SuffixElements.Count; i++)
                {
                    visitor(listPattern.SuffixElements[i], $"{path}/list-suffix#{i + 1}");
                }
                return;

            case CtorPattern ctorPattern:
                for (var i = 0; i < ctorPattern.PositionalPatterns.Count; i++)
                {
                    visitor(ctorPattern.PositionalPatterns[i], $"{path}/positional#{i + 1}");
                }

                foreach (var field in ctorPattern.NamedPatterns)
                {
                    visitor(field.Pattern, $"{path}/field#{GetPatternFieldNameSegment(field.FieldName)}");
                }

                return;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                for (var i = 0; i < orPattern.Alternatives.Count; i++)
                {
                    visitor(orPattern.Alternatives[i], $"{path}/alternative#{i + 1}");
                }

                return;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                for (var i = 0; i < andPattern.Conjuncts.Count; i++)
                {
                    visitor(andPattern.Conjuncts[i], $"{path}/conjunct#{i + 1}");
                }

                return;

            case NotPattern { InnerPattern: not null } notPattern:
                visitor(notPattern.InnerPattern, $"{path}/not-inner");
                return;
        }
    }

    private static string GetPatternFieldNameSegment(string? fieldName)
    {
        return string.IsNullOrWhiteSpace(fieldName)
            ? "<unnamed>"
            : fieldName;
    }
}
