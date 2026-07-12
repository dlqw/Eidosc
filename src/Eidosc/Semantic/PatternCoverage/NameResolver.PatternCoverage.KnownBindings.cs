using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private delegate bool BoolBindingCollector(
        Pattern pattern,
        out Dictionary<string, bool> bindings);
    private delegate bool IntBindingCollector(
        Pattern pattern,
        out Dictionary<string, ListGuardIntBinding> bindings);
    private delegate bool AlternativeBindingCollector<TBinding>(
        Pattern pattern,
        out Dictionary<string, TBinding> bindings);

    private bool TryMatchAdtConstructorPatternWithKnownBindings(
        Pattern pattern,
        SymbolId constructorId,
        out Dictionary<string, bool> knownBoolBindings,
        out Dictionary<string, ListGuardIntBinding> knownIntBindings)
    {
        knownBoolBindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        knownIntBindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        switch (pattern)
        {
            case CtorPattern ctorPattern when ctorPattern.SymbolId.IsValid:
                if (ctorPattern.SymbolId != constructorId)
                {
                    return false;
                }

                return TryCollectKnownBoolBindingsFromPattern(ctorPattern, out knownBoolBindings) &&
                       TryCollectKnownIntBindingsFromPattern(ctorPattern, out knownIntBindings);

            case AsPattern asPattern:
            {
                Dictionary<string, bool> innerBindings;
                Dictionary<string, ListGuardIntBinding> innerIntBindings;
                if (asPattern.InnerPattern == null)
                {
                    innerBindings = new Dictionary<string, bool>(StringComparer.Ordinal);
                    innerIntBindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
                }
                else if (!TryMatchAdtConstructorPatternWithKnownBindings(
                             asPattern.InnerPattern,
                             constructorId,
                             out innerBindings,
                             out innerIntBindings))
                {
                    return false;
                }

                if (TryGetExactSingleBoolPatternValue(asPattern.InnerPattern, out var asValue) &&
                    !TryBindKnownBoolValue(innerBindings, asPattern.BindingName, asValue))
                {
                    knownBoolBindings = [];
                    knownIntBindings = [];
                    return false;
                }

                var hasExactSingleIntValue = TryGetExactSingleIntPatternValue(
                    asPattern.InnerPattern,
                    out var asIntValue);
                if (hasExactSingleIntValue &&
                    !TryBindKnownIntValue(
                        innerIntBindings,
                        asPattern.BindingName,
                        ListGuardIntBinding.FromExact(asIntValue)))
                {
                    knownBoolBindings = [];
                    knownIntBindings = [];
                    return false;
                }

                if (!hasExactSingleIntValue &&
                    TryGetFiniteIntPatternValues(asPattern.InnerPattern, out var finiteValues) &&
                    finiteValues.Count > 0 &&
                    !TryBindKnownIntValue(
                        innerIntBindings,
                        asPattern.BindingName,
                        ListGuardIntBinding.FromFiniteSet(finiteValues)))
                {
                    knownBoolBindings = [];
                    knownIntBindings = [];
                    return false;
                }

                if (!hasExactSingleIntValue &&
                    !TryGetFiniteIntPatternValues(asPattern.InnerPattern, out _) &&
                    TryGetFiniteIntOtherExclusions(asPattern.InnerPattern, out var excludedValues) &&
                    excludedValues.Count > 0 &&
                    !TryBindKnownIntValue(
                        innerIntBindings,
                        asPattern.BindingName,
                        ListGuardIntBinding.FromOther(excludedValues)))
                {
                    knownBoolBindings = [];
                    knownIntBindings = [];
                    return false;
                }

                knownBoolBindings = innerBindings;
                knownIntBindings = innerIntBindings;
                return true;
            }

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var alternativeBindings = new List<Dictionary<string, bool>>();
                var alternativeIntBindings = new List<Dictionary<string, ListGuardIntBinding>>();
                foreach (var alternative in orPattern.Alternatives)
                {
                    if (TryMatchAdtConstructorPatternWithKnownBindings(
                            alternative,
                            constructorId,
                            out var bindings,
                            out var intBindings))
                    {
                        alternativeBindings.Add(bindings);
                        alternativeIntBindings.Add(intBindings);
                    }
                }

                if (alternativeBindings.Count == 0)
                {
                    return false;
                }

                knownBoolBindings = IntersectKnownBoolBindingsAcrossAlternatives(alternativeBindings);
                knownIntBindings = MergeKnownIntBindingsAcrossAlternatives(alternativeIntBindings);
                return true;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                var merged = new Dictionary<string, bool>(StringComparer.Ordinal);
                var mergedIntBindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    if (!TryMatchAdtConstructorPatternWithKnownBindings(
                            conjunct,
                            constructorId,
                            out var conjunctBindings,
                            out var conjunctIntBindings) ||
                        !TryMergeKnownBoolBindings(merged, conjunctBindings, out merged) ||
                        !TryMergeKnownIntBindings(mergedIntBindings, conjunctIntBindings, out mergedIntBindings))
                    {
                        knownBoolBindings = [];
                        knownIntBindings = [];
                        return false;
                    }
                }

                knownBoolBindings = merged;
                knownIntBindings = mergedIntBindings;
                return true;
            }

            case NotPattern { InnerPattern: not null } notPattern:
                if (PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                        notPattern.InnerPattern,
                        _symbolTable,
                        out _,
                        out var excludedConstructors) &&
                    excludedConstructors.Contains(constructorId))
                {
                    return false;
                }

                return true;

            case NotPattern:
            case WildcardPattern:
            case VarPattern:
            case LiteralPattern:
            case RangePattern:
                return true;

            default:
                return false;
        }
    }

    private static bool TryCollectKnownBoolBindingsFromPattern(
        Pattern pattern,
        out Dictionary<string, bool> knownBoolBindings)
    {
        knownBoolBindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        switch (pattern)
        {
            case WildcardPattern:
            case VarPattern:
            case LiteralPattern:
            case RangePattern:
                return true;

            case ViewPattern viewPattern:
                return viewPattern.InnerPattern == null ||
                       TryCollectKnownBoolBindingsFromPattern(
                           viewPattern.InnerPattern,
                           out knownBoolBindings);

            case CtorPattern ctorPattern:
                return TryCollectKnownBoolBindingsFromPatternSequence(
                    EnumerateCtorChildPatterns(ctorPattern),
                    TryCollectKnownBoolBindingsFromPattern,
                    out knownBoolBindings);

            case TuplePattern tuplePattern:
                return TryCollectKnownBoolBindingsFromPatternSequence(
                    tuplePattern.Elements,
                    TryCollectKnownBoolBindingsFromPattern,
                    out knownBoolBindings);

            case ListPattern listPattern:
                return TryCollectKnownBoolBindingsFromPatternSequence(
                    EnumerateListChildPatterns(listPattern),
                    TryCollectKnownBoolBindingsFromPattern,
                    out knownBoolBindings);

            case AsPattern asPattern:
            {
                Dictionary<string, bool> innerBindings;
                if (asPattern.InnerPattern == null)
                {
                    innerBindings = new Dictionary<string, bool>(StringComparer.Ordinal);
                }
                else if (!TryCollectKnownBoolBindingsFromPattern(asPattern.InnerPattern, out innerBindings))
                {
                    return false;
                }

                if (TryGetExactSingleBoolPatternValue(asPattern.InnerPattern, out var asValue) &&
                    !TryBindKnownBoolValue(innerBindings, asPattern.BindingName, asValue))
                {
                    knownBoolBindings = [];
                    return false;
                }

                knownBoolBindings = innerBindings;
                return true;
            }

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryCollectKnownBoolBindingsFromAlternatives(
                    orPattern.Alternatives,
                    TryCollectKnownBoolBindingsFromPattern,
                    out knownBoolBindings);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryCollectKnownBoolBindingsFromPatternSequence(
                    andPattern.Conjuncts,
                    TryCollectKnownBoolBindingsFromPattern,
                    out knownBoolBindings);

            case NotPattern:
                return true;

            default:
                return true;
        }
    }

    private static bool TryMergeKnownBoolBindings(
        IReadOnlyDictionary<string, bool> left,
        IReadOnlyDictionary<string, bool> right,
        out Dictionary<string, bool> merged)
    {
        merged = new Dictionary<string, bool>(left, StringComparer.Ordinal);
        foreach (var (name, value) in right)
        {
            if (!TryBindKnownBoolValue(merged, name, value))
            {
                merged = [];
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, bool> IntersectKnownBoolBindingsAcrossAlternatives(
        IReadOnlyList<Dictionary<string, bool>> alternatives)
    {
        var intersection = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (alternatives.Count == 0)
        {
            return intersection;
        }

        var sharedNames = new HashSet<string>(alternatives[0].Keys, StringComparer.Ordinal);
        for (var i = 1; i < alternatives.Count; i++)
        {
            sharedNames.IntersectWith(alternatives[i].Keys);
        }

        foreach (var name in sharedNames)
        {
            var value = alternatives[0][name];
            var sameValue = true;
            for (var i = 1; i < alternatives.Count; i++)
            {
                if (alternatives[i][name] != value)
                {
                    sameValue = false;
                    break;
                }
            }

            if (sameValue)
            {
                intersection[name] = value;
            }
        }

        return intersection;
    }

    private static IEnumerable<Pattern> EnumerateCtorChildPatterns(CtorPattern ctorPattern)
    {
        foreach (var positional in ctorPattern.PositionalPatterns)
        {
            yield return positional;
        }

        foreach (var named in ctorPattern.NamedPatterns)
        {
            if (named.Pattern != null)
            {
                yield return named.Pattern;
            }
        }
    }

    private static IEnumerable<Pattern> EnumerateListChildPatterns(ListPattern listPattern)
    {
        foreach (var element in listPattern.Elements)
        {
            yield return element;
        }

        if (listPattern.RestPattern != null)
        {
            yield return listPattern.RestPattern;
        }

        foreach (var element in listPattern.SuffixElements)
        {
            yield return element;
        }
    }

    private static bool TryCollectKnownBoolBindingsFromPatternSequence(
        IEnumerable<Pattern> patterns,
        BoolBindingCollector collector,
        out Dictionary<string, bool> bindings)
    {
        var merged = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            if (!collector(pattern, out var candidateBindings) ||
                !TryMergeKnownBoolBindings(merged, candidateBindings, out merged))
            {
                bindings = [];
                return false;
            }
        }

        bindings = merged;
        return true;
    }

    private static bool TryCollectKnownBoolBindingsFromAlternatives(
        IReadOnlyList<Pattern> patterns,
        BoolBindingCollector collector,
        out Dictionary<string, bool> bindings)
    {
        if (!TryCollectSuccessfulAlternativeBindings(
                patterns,
                (Pattern pattern, out Dictionary<string, bool> candidateBindings) =>
                    collector(pattern, out candidateBindings),
                out List<Dictionary<string, bool>> alternatives))
        {
            bindings = [];
            return false;
        }

        bindings = IntersectKnownBoolBindingsAcrossAlternatives(alternatives);
        return true;
    }

    private static bool TryCollectKnownIntBindingsFromPatternSequence(
        IEnumerable<Pattern> patterns,
        IntBindingCollector collector,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        var merged = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            if (!collector(pattern, out var candidateBindings) ||
                !TryMergeKnownIntBindings(merged, candidateBindings, out merged))
            {
                bindings = [];
                return false;
            }
        }

        bindings = merged;
        return true;
    }

    private static bool TryCollectKnownIntBindingsFromAlternatives(
        IReadOnlyList<Pattern> patterns,
        IntBindingCollector collector,
        out Dictionary<string, ListGuardIntBinding> bindings)
    {
        if (!TryCollectSuccessfulAlternativeBindings(
                patterns,
                (Pattern pattern, out Dictionary<string, ListGuardIntBinding> candidateBindings) =>
                    collector(pattern, out candidateBindings),
                out List<Dictionary<string, ListGuardIntBinding>> alternatives))
        {
            bindings = [];
            return false;
        }

        bindings = MergeKnownIntBindingsAcrossAlternatives(alternatives);
        return true;
    }

    private static bool TryCollectSuccessfulAlternativeBindings<TBinding>(
        IReadOnlyList<Pattern> patterns,
        AlternativeBindingCollector<TBinding> collector,
        out List<Dictionary<string, TBinding>> alternatives)
    {
        alternatives = new List<Dictionary<string, TBinding>>();
        foreach (var pattern in patterns)
        {
            if (!collector(pattern, out var candidateBindings))
            {
                continue;
            }

            alternatives.Add(candidateBindings);
        }

        return alternatives.Count > 0;
    }

    private static bool TryCollectKnownIntBindingsFromPattern(
        Pattern pattern,
        out Dictionary<string, ListGuardIntBinding> knownIntBindings)
    {
        knownIntBindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        switch (pattern)
        {
            case WildcardPattern:
            case VarPattern:
            case LiteralPattern:
            case RangePattern:
            case NotPattern:
                return true;

            case ViewPattern viewPattern:
                return viewPattern.InnerPattern == null ||
                       TryCollectKnownIntBindingsFromPattern(
                           viewPattern.InnerPattern,
                           out knownIntBindings);

            case CtorPattern ctorPattern:
                return TryCollectKnownIntBindingsFromPatternSequence(
                    EnumerateCtorChildPatterns(ctorPattern),
                    TryCollectKnownIntBindingsFromPattern,
                    out knownIntBindings);

            case TuplePattern tuplePattern:
                return TryCollectKnownIntBindingsFromPatternSequence(
                    tuplePattern.Elements,
                    TryCollectKnownIntBindingsFromPattern,
                    out knownIntBindings);

            case ListPattern listPattern:
                return TryCollectKnownIntBindingsFromPatternSequence(
                    EnumerateListChildPatterns(listPattern),
                    TryCollectKnownIntBindingsFromPattern,
                    out knownIntBindings);

            case AsPattern asPattern:
            {
                Dictionary<string, ListGuardIntBinding> innerBindings;
                if (asPattern.InnerPattern == null)
                {
                    innerBindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
                }
                else if (!TryCollectKnownIntBindingsFromPattern(asPattern.InnerPattern, out innerBindings))
                {
                    return false;
                }

                if (TryGetExactSingleIntPatternValue(asPattern.InnerPattern, out var asValue) &&
                    !TryBindKnownIntValue(
                        innerBindings,
                        asPattern.BindingName,
                        ListGuardIntBinding.FromExact(asValue)))
                {
                    knownIntBindings = [];
                    return false;
                }

                if (!TryGetExactSingleIntPatternValue(asPattern.InnerPattern, out _) &&
                    TryGetFiniteIntPatternValues(asPattern.InnerPattern, out var finiteValues) &&
                    finiteValues.Count > 0 &&
                    !TryBindKnownIntValue(
                        innerBindings,
                        asPattern.BindingName,
                        ListGuardIntBinding.FromFiniteSet(finiteValues)))
                {
                    knownIntBindings = [];
                    return false;
                }

                if (!TryGetExactSingleIntPatternValue(asPattern.InnerPattern, out _) &&
                    !TryGetFiniteIntPatternValues(asPattern.InnerPattern, out _) &&
                    TryGetFiniteIntOtherExclusions(asPattern.InnerPattern, out var excludedValues) &&
                    excludedValues.Count > 0 &&
                    !TryBindKnownIntValue(
                        innerBindings,
                        asPattern.BindingName,
                        ListGuardIntBinding.FromOther(excludedValues)))
                {
                    knownIntBindings = [];
                    return false;
                }

                knownIntBindings = innerBindings;
                return true;
            }

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return TryCollectKnownIntBindingsFromAlternatives(
                    orPattern.Alternatives,
                    TryCollectKnownIntBindingsFromPattern,
                    out knownIntBindings);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return TryCollectKnownIntBindingsFromPatternSequence(
                    andPattern.Conjuncts,
                    TryCollectKnownIntBindingsFromPattern,
                    out knownIntBindings);

            default:
                return true;
        }
    }

    private static bool TryGetFiniteIntOtherExclusions(
        Pattern? pattern,
        out HashSet<long> excludedValues)
    {
        excludedValues = [];
        if (pattern == null)
        {
            return false;
        }

        switch (pattern)
        {
            case NotPattern { InnerPattern: not null } notPattern:
                return TryGetFiniteIntPatternValues(notPattern.InnerPattern, out excludedValues);

            case AsPattern { InnerPattern: not null } asPattern:
                return TryGetFiniteIntOtherExclusions(asPattern.InnerPattern, out excludedValues);

            case ViewPattern { InnerPattern: not null } viewPattern:
                return TryGetFiniteIntOtherExclusions(viewPattern.InnerPattern, out excludedValues);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                var unionExcluded = new HashSet<long>();
                var hasContributor = false;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    if (TryGetFiniteIntOtherExclusions(conjunct, out var conjunctExcluded))
                    {
                        unionExcluded.UnionWith(conjunctExcluded);
                        hasContributor = true;
                        continue;
                    }

                    if (IsPatternIrrefutableForFiniteCoverage(conjunct))
                    {
                        continue;
                    }

                    excludedValues = [];
                    return false;
                }

                if (!hasContributor || unionExcluded.Count == 0)
                {
                    return false;
                }

                excludedValues = unionExcluded;
                return true;
            }

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                HashSet<long>? intersectionExcluded = null;
                for (var i = 0; i < orPattern.Alternatives.Count; i++)
                {
                    var alternative = orPattern.Alternatives[i];
                    if (!TryGetFiniteIntOtherExclusions(alternative, out var alternativeExcluded))
                    {
                        // An irrefutable alternative covers all values, so unioning
                        // it with any exclusion-set alternative collapses to "all".
                        if (IsPatternIrrefutableForFiniteCoverage(alternative))
                        {
                            return false;
                        }

                        excludedValues = [];
                        return false;
                    }

                    if (intersectionExcluded == null)
                    {
                        intersectionExcluded = new HashSet<long>(alternativeExcluded);
                    }
                    else
                    {
                        intersectionExcluded.IntersectWith(alternativeExcluded);
                    }
                }

                if (intersectionExcluded == null || intersectionExcluded.Count == 0)
                {
                    return false;
                }

                excludedValues = intersectionExcluded;
                return true;
            }

            default:
                return false;
        }
    }

    private static Dictionary<string, ListGuardIntBinding> MergeKnownIntBindingsAcrossAlternatives(
        IReadOnlyList<Dictionary<string, ListGuardIntBinding>> alternatives)
    {
        var merged = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        if (alternatives.Count == 0)
        {
            return merged;
        }

        var sharedNames = new HashSet<string>(alternatives[0].Keys, StringComparer.Ordinal);
        for (var i = 1; i < alternatives.Count; i++)
        {
            sharedNames.IntersectWith(alternatives[i].Keys);
        }

        foreach (var name in sharedNames)
        {
            var candidateBindings = new List<ListGuardIntBinding>(alternatives.Count);
            for (var i = 0; i < alternatives.Count; i++)
            {
                if (!alternatives[i].TryGetValue(name, out var binding))
                {
                    candidateBindings.Clear();
                    break;
                }

                candidateBindings.Add(binding);
            }

            if (candidateBindings.Count == 0)
            {
                continue;
            }

            if (TryUnionAlternativeIntBindings(candidateBindings, out var unionBinding))
            {
                merged[name] = unionBinding;
            }
        }

        return merged;
    }

    private static bool TryUnionAlternativeIntBindings(
        IReadOnlyList<ListGuardIntBinding> alternatives,
        out ListGuardIntBinding merged)
    {
        merged = default;
        if (alternatives.Count == 0)
        {
            return false;
        }

        var finiteUnion = new HashSet<long>();
        var hasOtherAlternative = false;
        HashSet<long>? intersectionExcluded = null;

        for (var i = 0; i < alternatives.Count; i++)
        {
            var alternative = alternatives[i];
            if (alternative.IsOtherBucket)
            {
                hasOtherAlternative = true;
                if (intersectionExcluded == null)
                {
                    intersectionExcluded = new HashSet<long>(alternative.ExcludedValues);
                }
                else
                {
                    intersectionExcluded.IntersectWith(alternative.ExcludedValues);
                }

                continue;
            }

            var finiteValues = GetFiniteBindingValues(alternative);
            if (finiteValues.Count == 0)
            {
                return false;
            }

            finiteUnion.UnionWith(finiteValues);
        }

        if (hasOtherAlternative)
        {
            intersectionExcluded ??= new HashSet<long>();
            intersectionExcluded.ExceptWith(finiteUnion);
            merged = ListGuardIntBinding.FromOther(intersectionExcluded);
            return true;
        }

        if (finiteUnion.Count == 0)
        {
            return false;
        }

        merged = finiteUnion.Count == 1
            ? ListGuardIntBinding.FromExact(finiteUnion.First())
            : ListGuardIntBinding.FromFiniteSet(finiteUnion);

        return true;
    }
}
