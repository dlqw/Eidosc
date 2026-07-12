using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private enum CoverageIntDomainMatchTruth
    {
        Unknown,
        Match,
        NoMatch
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

    private static bool HasMixedUncertainViewAndNonViewPatterns(
        IReadOnlyList<Pattern> patterns,
        IReadOnlySet<long> domainValues)
    {
        var hasUncertainView = false;
        var hasNonView = false;

        for (var i = 0; i < patterns.Count; i++)
        {
            var pattern = patterns[i];
            if (HasUncertainViewPatternOverIntDomain(pattern, domainValues))
            {
                hasUncertainView = true;
            }

            if (!HasAnyViewPattern(pattern))
            {
                hasNonView = true;
            }

            if (hasUncertainView && hasNonView)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUncertainViewPatternOverBoolDomain(Pattern? pattern)
    {
        switch (pattern)
        {
            case null:
                return false;

            case ViewPattern viewPattern:
                return IsViewPatternUncertainOverBoolDomain(viewPattern);

            case AsPattern asPattern:
                return HasUncertainViewPatternOverBoolDomain(asPattern.InnerPattern);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return HasUncertainViewPatternOverBoolDomain(orPattern.Alternatives);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return HasUncertainViewPatternOverBoolDomain(andPattern.Conjuncts);

            case NotPattern { InnerPattern: not null } notPattern:
                return HasUncertainViewPatternOverBoolDomain(notPattern.InnerPattern);

            case TuplePattern tuplePattern:
                return HasUncertainViewPatternOverBoolDomain(tuplePattern.Elements);

            case ListPattern listPattern:
                return HasUncertainViewPatternOverBoolDomain(listPattern.Elements) ||
                       HasUncertainViewPatternOverBoolDomain(listPattern.RestPattern) ||
                       HasUncertainViewPatternOverBoolDomain(listPattern.SuffixElements);

            case CtorPattern ctorPattern:
                return HasUncertainViewPatternOverBoolDomain(ctorPattern.PositionalPatterns) ||
                       HasUncertainViewPatternOverBoolDomain(ctorPattern.NamedPatterns);

            default:
                return false;
        }
    }

    private static bool HasAnyUncertainViewPatternOverIntDomain(
        IReadOnlyList<Pattern> patterns,
        IReadOnlySet<long> domainValues)
    {
        return AnyCollectionItemMatches(
            patterns,
            pattern => HasUncertainViewPatternOverIntDomain(pattern, domainValues));
    }

    private static bool HasUncertainViewPatternOverIntDomain(
        Pattern? pattern,
        IReadOnlySet<long> domainValues)
    {
        switch (pattern)
        {
            case null:
                return false;

            case ViewPattern viewPattern:
                return IsViewPatternUncertainOverIntDomain(viewPattern, domainValues);

            case AsPattern asPattern:
                return HasUncertainViewPatternOverIntDomain(asPattern.InnerPattern, domainValues);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return HasUncertainViewPatternOverIntDomain(orPattern.Alternatives, domainValues);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return HasUncertainViewPatternOverIntDomain(andPattern.Conjuncts, domainValues);

            case NotPattern { InnerPattern: not null } notPattern:
                return HasUncertainViewPatternOverIntDomain(notPattern.InnerPattern, domainValues);

            case TuplePattern tuplePattern:
                return HasUncertainViewPatternOverIntDomain(tuplePattern.Elements, domainValues);

            case ListPattern listPattern:
                return HasUncertainViewPatternOverIntDomain(listPattern.Elements, domainValues) ||
                       HasUncertainViewPatternOverIntDomain(listPattern.RestPattern, domainValues) ||
                       HasUncertainViewPatternOverIntDomain(listPattern.SuffixElements, domainValues);

            case CtorPattern ctorPattern:
                return HasUncertainViewPatternOverIntDomain(ctorPattern.PositionalPatterns, domainValues) ||
                       HasUncertainViewPatternOverIntDomain(ctorPattern.NamedPatterns, domainValues);

            default:
                return false;
        }
    }

    private static bool IsViewPatternUncertainOverIntDomain(
        ViewPattern viewPattern,
        IReadOnlySet<long> domainValues)
    {
        if (viewPattern is { IsTransparentIdentityView: true, InnerPattern: not null })
        {
            return HasUncertainViewPatternOverIntDomain(viewPattern.InnerPattern, domainValues);
        }

        if (IsPatternIrrefutableForFiniteCoverage(viewPattern.InnerPattern))
        {
            return false;
        }

        return ClassifyViewPatternOverIntFiniteDomain(viewPattern.InnerPattern, domainValues) is
            ViewPatternFiniteClassification.Unknown;
    }

    private static bool IsViewPatternUncertainOverBoolDomain(ViewPattern viewPattern)
    {
        if (viewPattern is { IsTransparentIdentityView: true, InnerPattern: not null })
        {
            return HasUncertainViewPatternOverBoolDomain(viewPattern.InnerPattern);
        }

        if (IsPatternIrrefutableForFiniteCoverage(viewPattern.InnerPattern))
        {
            return false;
        }

        return ClassifyViewPatternOverBoolFiniteDomain(viewPattern.InnerPattern) is
            ViewPatternFiniteClassification.Unknown;
    }

    private static bool HasIrrefutableNonViewPattern(IReadOnlyList<Pattern> patterns)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            var pattern = patterns[i];
            if (IsPatternIrrefutableForFiniteCoverage(pattern) &&
                !HasAnyViewPattern(pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUncertainViewPatternOverBoolDomain(IReadOnlyList<Pattern> patterns)
    {
        return AnyCollectionItemMatches(
            patterns,
            HasUncertainViewPatternOverBoolDomain);
    }

    private static bool HasUncertainViewPatternOverBoolDomain(IReadOnlyList<FieldPattern> patterns)
    {
        return AnyCollectionItemMatches(
            patterns,
            fieldPattern => HasUncertainViewPatternOverBoolDomain(fieldPattern.Pattern));
    }

    private static bool HasUncertainViewPatternOverIntDomain(
        IReadOnlyList<Pattern> patterns,
        IReadOnlySet<long> domainValues)
    {
        return AnyCollectionItemMatches(
            patterns,
            pattern => HasUncertainViewPatternOverIntDomain(pattern, domainValues));
    }

    private static bool HasUncertainViewPatternOverIntDomain(
        IReadOnlyList<FieldPattern> patterns,
        IReadOnlySet<long> domainValues)
    {
        return AnyCollectionItemMatches(
            patterns,
            fieldPattern => HasUncertainViewPatternOverIntDomain(fieldPattern.Pattern, domainValues));
    }

    private static bool AnyCollectionItemMatches<TItem>(
        IReadOnlyList<TItem> items,
        Func<TItem, bool> predicate)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (predicate(items[i]))
            {
                return true;
            }
        }

        return false;
    }
}
