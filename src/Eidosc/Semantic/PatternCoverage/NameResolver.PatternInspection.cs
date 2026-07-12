using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static bool HasDominantIrrefutableNonViewOrFallbackForConstructor(
        Pattern? pattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor)
    {
        if (pattern == null)
        {
            return true;
        }

        if (IsPatternIrrefutableForFiniteCoverage(pattern) &&
            !HasAnyViewPattern(pattern))
        {
            return true;
        }

        switch (pattern)
        {
            case AsPattern asPattern:
                return HasDominantIrrefutableNonViewOrFallbackForConstructor(
                    asPattern.InnerPattern,
                    symbolTable,
                    targetAdt,
                    targetConstructor);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return AnyNonNullPattern(
                    orPattern.Alternatives,
                    alternative =>
                        CouldMatchTargetConstructor(alternative, symbolTable, targetAdt, targetConstructor) &&
                        HasDominantIrrefutableNonViewOrFallbackForConstructor(
                            alternative,
                            symbolTable,
                            targetAdt,
                            targetConstructor));

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return AllNonNullPattern(
                    andPattern.Conjuncts,
                    conjunct => MatchesDominantAndPatternForTargetConstructor(
                        conjunct,
                        symbolTable,
                        targetAdt,
                        targetConstructor));

            case CtorPattern ctorPattern:
                return MatchesDominantCtorPatternForTargetConstructor(
                    ctorPattern,
                    symbolTable,
                    targetAdt,
                    targetConstructor);

            default:
                return false;
        }
    }

    private static bool CouldMatchTargetConstructor(
        Pattern pattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor)
    {
        if (!PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                pattern,
                symbolTable,
                out var patternAdt,
                out var patternCtorIds))
        {
            return true;
        }

        return patternAdt == targetAdt &&
               patternCtorIds.Contains(targetConstructor);
    }

    private static bool IsPatternDominatedByIrrefutableNonViewFallback(Pattern? pattern)
    {
        if (pattern == null)
        {
            return true;
        }

        if (IsPatternIrrefutableForFiniteCoverage(pattern) &&
            !HasAnyViewPattern(pattern))
        {
            return true;
        }

        switch (pattern)
        {
            case AsPattern asPattern:
                return IsPatternDominatedByIrrefutableNonViewFallback(asPattern.InnerPattern);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return AnyNonNullPattern(orPattern.Alternatives, IsPatternDominatedByIrrefutableNonViewFallback);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return AllNonNullPattern(andPattern.Conjuncts, IsPatternDominatedByIrrefutableNonViewFallback);

            case TuplePattern tuplePattern:
                return AllNonNullPattern(tuplePattern.Elements, IsPatternDominatedByIrrefutableNonViewFallback);

            case ListPattern listPattern:
                return AllNonNullPattern(listPattern.Elements, IsPatternDominatedByIrrefutableNonViewFallback) &&
                       IsPatternDominatedByIrrefutableNonViewFallback(listPattern.RestPattern) &&
                       AllNonNullPattern(listPattern.SuffixElements, IsPatternDominatedByIrrefutableNonViewFallback);

            case CtorPattern ctorPattern:
                return AllNonNullPattern(ctorPattern.PositionalPatterns, IsPatternDominatedByIrrefutableNonViewFallback) &&
                       AllFieldPatterns(ctorPattern.NamedPatterns, IsPatternDominatedByIrrefutableNonViewFallback);

            case ViewPattern:
            case NotPattern:
                return false;

            default:
                return false;
        }
    }

    private static bool HasRefutableViewPattern(Pattern? pattern)
    {
        if (pattern == null)
        {
            return false;
        }

        switch (pattern)
        {
            case ViewPattern viewPattern:
                return viewPattern.InnerPattern != null &&
                       (!IsPatternIrrefutableForFiniteCoverage(viewPattern.InnerPattern) ||
                        HasRefutableViewPattern(viewPattern.InnerPattern));

            case AsPattern asPattern:
                return HasRefutableViewPattern(asPattern.InnerPattern);

            case OrPattern orPattern:
                return AnyNonNullPattern(orPattern.Alternatives, HasRefutableViewPattern);

            case AndPattern andPattern:
                return AnyNonNullPattern(andPattern.Conjuncts, HasRefutableViewPattern);

            case NotPattern notPattern:
                return HasRefutableViewPattern(notPattern.InnerPattern);

            case TuplePattern tuplePattern:
                return AnyNonNullPattern(tuplePattern.Elements, HasRefutableViewPattern);

            case ListPattern listPattern:
                return AnyNonNullPattern(listPattern.Elements, HasRefutableViewPattern) ||
                       HasRefutableViewPattern(listPattern.RestPattern) ||
                       AnyNonNullPattern(listPattern.SuffixElements, HasRefutableViewPattern);

            case CtorPattern ctorPattern:
                return AnyNonNullPattern(ctorPattern.PositionalPatterns, HasRefutableViewPattern) ||
                       AnyFieldPatterns(ctorPattern.NamedPatterns, HasRefutableViewPattern);

            default:
                return false;
        }
    }

    private static bool HasAnyViewPattern(Pattern? pattern)
    {
        if (pattern == null)
        {
            return false;
        }

        switch (pattern)
        {
            case ViewPattern:
                return true;

            case AsPattern asPattern:
                return HasAnyViewPattern(asPattern.InnerPattern);

            case OrPattern orPattern:
                return AnyNonNullPattern(orPattern.Alternatives, HasAnyViewPattern);

            case AndPattern andPattern:
                return AnyNonNullPattern(andPattern.Conjuncts, HasAnyViewPattern);

            case NotPattern notPattern:
                return HasAnyViewPattern(notPattern.InnerPattern);

            case TuplePattern tuplePattern:
                return AnyNonNullPattern(tuplePattern.Elements, HasAnyViewPattern);

            case ListPattern listPattern:
                return AnyNonNullPattern(listPattern.Elements, HasAnyViewPattern) ||
                       HasAnyViewPattern(listPattern.RestPattern) ||
                       AnyNonNullPattern(listPattern.SuffixElements, HasAnyViewPattern);

            case CtorPattern ctorPattern:
                return AnyNonNullPattern(ctorPattern.PositionalPatterns, HasAnyViewPattern) ||
                       AnyFieldPatterns(ctorPattern.NamedPatterns, HasAnyViewPattern);

            default:
                return false;
        }
    }

    private static bool HasNegatedRefutableViewPattern(Pattern? pattern)
    {
        if (pattern == null)
        {
            return false;
        }

        switch (pattern)
        {
            case NotPattern { InnerPattern: not null } notPattern:
                return HasRefutableViewPattern(notPattern.InnerPattern) ||
                       HasNegatedRefutableViewPattern(notPattern.InnerPattern);

            case AsPattern asPattern:
                return HasNegatedRefutableViewPattern(asPattern.InnerPattern);

            case OrPattern orPattern:
                return AnyNonNullPattern(orPattern.Alternatives, HasNegatedRefutableViewPattern);

            case AndPattern andPattern:
                return AnyNonNullPattern(andPattern.Conjuncts, HasNegatedRefutableViewPattern);

            case TuplePattern tuplePattern:
                return AnyNonNullPattern(tuplePattern.Elements, HasNegatedRefutableViewPattern);

            case ListPattern listPattern:
                return AnyNonNullPattern(listPattern.Elements, HasNegatedRefutableViewPattern) ||
                       HasNegatedRefutableViewPattern(listPattern.RestPattern) ||
                       AnyNonNullPattern(listPattern.SuffixElements, HasNegatedRefutableViewPattern);

            case CtorPattern ctorPattern:
                return AnyNonNullPattern(ctorPattern.PositionalPatterns, HasNegatedRefutableViewPattern) ||
                       AnyFieldPatterns(ctorPattern.NamedPatterns, HasNegatedRefutableViewPattern);

            default:
                return false;
        }
    }

    private static bool MatchesDominantAndPatternForTargetConstructor(
        Pattern conjunct,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor)
    {
        if (!CouldMatchTargetConstructor(conjunct, symbolTable, targetAdt, targetConstructor))
        {
            return false;
        }

        if (TryResolveAdtCoverageTarget(conjunct, symbolTable, out var conjunctAdt) &&
            conjunctAdt == targetAdt)
        {
            return HasDominantIrrefutableNonViewOrFallbackForConstructor(
                conjunct,
                symbolTable,
                targetAdt,
                targetConstructor);
        }

        return IsPatternDominatedByIrrefutableNonViewFallback(conjunct);
    }

    private static bool MatchesDominantCtorPatternForTargetConstructor(
        CtorPattern ctorPattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor)
    {
        if (!PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                ctorPattern,
                symbolTable,
                out var ctorAdt,
                out var ctorIds) ||
            ctorAdt != targetAdt ||
            !ctorIds.Contains(targetConstructor))
        {
            return false;
        }

        return AllNonNullPattern(ctorPattern.PositionalPatterns, IsPatternDominatedByIrrefutableNonViewFallback) &&
               AllFieldPatterns(ctorPattern.NamedPatterns, IsPatternDominatedByIrrefutableNonViewFallback);
    }

    private static bool AnyNonNullPattern(IReadOnlyList<Pattern> patterns, Func<Pattern, bool> predicate)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (predicate(patterns[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AllNonNullPattern(IReadOnlyList<Pattern> patterns, Func<Pattern, bool> predicate)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (!predicate(patterns[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnyFieldPatterns(IReadOnlyList<FieldPattern> patterns, Func<Pattern?, bool> predicate)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (predicate(patterns[i].Pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AllFieldPatterns(IReadOnlyList<FieldPattern> patterns, Func<Pattern?, bool> predicate)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (!predicate(patterns[i].Pattern))
            {
                return false;
            }
        }

        return true;
    }
}
