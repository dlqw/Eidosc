using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic.PatternCoverage;

internal static partial class PatternUsefulnessAnalyzer
{
    internal static bool TryGetExactAdtConstructorCoverageCases(
        Pattern pattern,
        SymbolTable symbolTable,
        out SymbolId adtId,
        out IReadOnlyList<SymbolId> constructorIds)
    {
        var status = TryCollectExactAdtConstructorCoverageCases(
            pattern,
            symbolTable,
            SymbolId.None,
            out adtId,
            out var constructorHints);
        if (status is not PatternSpecializationStatus.ExactFinite || !adtId.IsValid)
        {
            adtId = SymbolId.None;
            constructorIds = [];
            return false;
        }

        constructorIds = constructorHints.Keys
            .OrderBy(id => id.Value)
            .ToList();
        return true;
    }

    private static PatternSpecializationStatus TryCollectExactAdtConstructorCoverageCases(
        Pattern pattern,
        SymbolTable symbolTable,
        SymbolId preferredAdt,
        out SymbolId resolvedAdt,
        out Dictionary<SymbolId, string> constructorHints)
    {
        resolvedAdt = SymbolId.None;
        constructorHints = [];

        switch (pattern)
        {
            case CtorPattern ctorPattern when ctorPattern.SymbolId.IsValid:
            {
                if (!ctorPattern.PositionalPatterns.All(child => IsIrrefutableForExactConstructorCoverage(child, symbolTable)) ||
                    !ctorPattern.NamedPatterns.All(named => IsIrrefutableForExactConstructorCoverage(named.Pattern, symbolTable)))
                {
                    return PatternSpecializationStatus.NotApplicable;
                }

                var ctorSymbol = symbolTable.GetSymbol<CtorSymbol>(ctorPattern.SymbolId);
                if (ctorSymbol == null || !ctorSymbol.OwnerAdt.IsValid)
                {
                    return PatternSpecializationStatus.NotApplicable;
                }

                resolvedAdt = ctorSymbol.OwnerAdt;
                constructorHints[ctorPattern.SymbolId] = AdtCoverageSpace.InferConstructorPatternWitness(ctorPattern, symbolTable);
                return PatternSpecializationStatus.ExactFinite;
            }

            case AsPattern { InnerPattern: not null } asPattern:
                return TryCollectExactAdtConstructorCoverageCases(
                    asPattern.InnerPattern,
                    symbolTable,
                    preferredAdt,
                    out resolvedAdt,
                    out constructorHints);

            case TuplePattern tuplePattern:
                return TryCollectExactTupleProjectedAdtConstructorCoverageCases(
                    tuplePattern,
                    symbolTable,
                    preferredAdt,
                    out resolvedAdt,
                    out constructorHints);

            case WildcardPattern:
            case VarPattern:
            {
                if (!preferredAdt.IsValid ||
                    !AdtCoverageSpace.TryGetAdtConstructors(symbolTable, preferredAdt, out var allConstructors))
                {
                    return PatternSpecializationStatus.NotApplicable;
                }

                resolvedAdt = preferredAdt;
                foreach (var ctorId in allConstructors)
                {
                    constructorHints[ctorId] = AdtCoverageSpace.InferConstructorWitnessFromSymbol(symbolTable, ctorId);
                }

                return constructorHints.Count > 0
                    ? PatternSpecializationStatus.ExactFinite
                    : PatternSpecializationStatus.NotApplicable;
            }

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var mergedAdt = preferredAdt;
                var mergedHints = new Dictionary<SymbolId, string>();

                foreach (var alternative in orPattern.Alternatives)
                {
                    var alternativeStatus = TryCollectExactAdtConstructorCoverageCases(
                        alternative,
                        symbolTable,
                        mergedAdt,
                        out var alternativeAdt,
                        out var alternativeHints);
                    if (alternativeStatus is not PatternSpecializationStatus.ExactFinite)
                    {
                        constructorHints = [];
                        resolvedAdt = SymbolId.None;
                        return alternativeStatus;
                    }

                    if (!mergedAdt.IsValid)
                    {
                        mergedAdt = alternativeAdt;
                    }
                    else if (alternativeAdt.IsValid && mergedAdt != alternativeAdt)
                    {
                        constructorHints = [];
                        resolvedAdt = SymbolId.None;
                        return PatternSpecializationStatus.Untrackable;
                    }

                    foreach (var (ctorId, hint) in alternativeHints)
                    {
                        AdtCoverageSpace.AddConstructorHint(mergedHints, ctorId, hint);
                    }
                }

                if (!mergedAdt.IsValid)
                {
                    return PatternSpecializationStatus.NotApplicable;
                }

                resolvedAdt = mergedAdt;
                constructorHints = mergedHints;
                return PatternSpecializationStatus.ExactFinite;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                var mergedAdt = preferredAdt;
                HashSet<SymbolId>? intersection = null;
                var candidateHints = new Dictionary<SymbolId, string>();

                foreach (var conjunct in andPattern.Conjuncts)
                {
                    var conjunctStatus = TryCollectExactAdtConstructorCoverageCases(
                        conjunct,
                        symbolTable,
                        mergedAdt,
                        out var conjunctAdt,
                        out var conjunctHints);
                    if (conjunctStatus is not PatternSpecializationStatus.ExactFinite)
                    {
                        return conjunctStatus;
                    }

                    if (!mergedAdt.IsValid)
                    {
                        mergedAdt = conjunctAdt;
                    }
                    else if (conjunctAdt.IsValid && mergedAdt != conjunctAdt)
                    {
                        return PatternSpecializationStatus.Untrackable;
                    }

                    var conjunctIds = conjunctHints.Keys.ToHashSet();
                    intersection = intersection == null ? conjunctIds : intersection.Intersect(conjunctIds).ToHashSet();

                    foreach (var (ctorId, hint) in conjunctHints)
                    {
                        AdtCoverageSpace.AddConstructorHint(candidateHints, ctorId, hint);
                    }
                }

                if (!mergedAdt.IsValid || intersection == null)
                {
                    return PatternSpecializationStatus.Untrackable;
                }

                foreach (var ctorId in intersection)
                {
                    constructorHints[ctorId] = candidateHints.TryGetValue(ctorId, out var hint)
                        ? hint
                        : AdtCoverageSpace.InferConstructorWitnessFromSymbol(symbolTable, ctorId);
                }

                resolvedAdt = mergedAdt;
                return PatternSpecializationStatus.ExactFinite;
            }

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerStatus = TryCollectExactAdtConstructorCoverageCases(
                    notPattern.InnerPattern,
                    symbolTable,
                    preferredAdt,
                    out var innerAdt,
                    out var innerHints);
                if (innerStatus is not PatternSpecializationStatus.ExactFinite)
                {
                    return innerStatus;
                }

                var targetAdt = preferredAdt.IsValid ? preferredAdt : innerAdt;
                if (!targetAdt.IsValid ||
                    !AdtCoverageSpace.TryGetAdtConstructors(symbolTable, targetAdt, out var allConstructors))
                {
                    return PatternSpecializationStatus.NotApplicable;
                }

                foreach (var ctorId in allConstructors)
                {
                    if (!innerHints.ContainsKey(ctorId))
                    {
                        constructorHints[ctorId] = AdtCoverageSpace.InferConstructorWitnessFromSymbol(symbolTable, ctorId);
                    }
                }

                resolvedAdt = targetAdt;
                return PatternSpecializationStatus.ExactFinite;
            }

            default:
                return PatternSpecializationStatus.NotApplicable;
        }
    }

    private static PatternSpecializationStatus TryCollectExactTupleProjectedAdtConstructorCoverageCases(
        TuplePattern tuplePattern,
        SymbolTable symbolTable,
        SymbolId preferredAdt,
        out SymbolId resolvedAdt,
        out Dictionary<SymbolId, string> constructorHints)
    {
        resolvedAdt = SymbolId.None;
        constructorHints = [];

        if (tuplePattern.Elements.Count == 0)
        {
            return PatternSpecializationStatus.NotApplicable;
        }

        var projectedElementIndex = -1;
        for (var i = 0; i < tuplePattern.Elements.Count; i++)
        {
            if (IsPatternIrrefutableForFiniteCoverage(tuplePattern.Elements[i]))
            {
                continue;
            }

            if (projectedElementIndex >= 0)
            {
                return PatternSpecializationStatus.NotApplicable;
            }

            projectedElementIndex = i;
        }

        if (projectedElementIndex < 0)
        {
            return PatternSpecializationStatus.NotApplicable;
        }

        return TryCollectExactAdtConstructorCoverageCases(
            tuplePattern.Elements[projectedElementIndex],
            symbolTable,
            preferredAdt,
            out resolvedAdt,
            out constructorHints);
    }

    private static bool IsIrrefutableForExactConstructorCoverage(Pattern? pattern, SymbolTable symbolTable)
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
            AsPattern asPattern => IsIrrefutableForExactConstructorCoverage(asPattern.InnerPattern, symbolTable),
            TuplePattern tuplePattern => tuplePattern.Elements.All(element => IsIrrefutableForExactConstructorCoverage(element, symbolTable)),
            OrPattern { Alternatives.Count: > 0 } orPattern =>
                orPattern.Alternatives.Any(alternative => IsIrrefutableForExactConstructorCoverage(alternative, symbolTable)),
            AndPattern { Conjuncts.Count: > 0 } andPattern =>
                andPattern.Conjuncts.All(conjunct => IsIrrefutableForExactConstructorCoverage(conjunct, symbolTable)),
            ViewPattern viewPattern => IsIrrefutableForExactConstructorCoverage(viewPattern.InnerPattern, symbolTable),
            CtorPattern ctorPattern when ctorPattern.SymbolId.IsValid =>
                symbolTable.GetSymbol<CtorSymbol>(ctorPattern.SymbolId) is { OwnerAdt: var ownerAdt } ctorSymbol &&
                ownerAdt.IsValid &&
                AdtCoverageSpace.TryGetAdtConstructors(symbolTable, ownerAdt, out var constructors) &&
                constructors.Count == 1 &&
                ctorPattern.PositionalPatterns.All(child => IsIrrefutableForExactConstructorCoverage(child, symbolTable)) &&
                ctorPattern.NamedPatterns.All(named => IsIrrefutableForExactConstructorCoverage(named.Pattern, symbolTable)),
            _ => false
        };
    }

    private sealed partial class AdtCoverageSpace
    {
        internal static PatternSpecializationStatus TryCollectAdtConstructorCases(
            Pattern pattern,
            SymbolTable symbolTable,
            SymbolId preferredAdt,
            out SymbolId resolvedAdt,
            out Dictionary<SymbolId, string> constructorHints)
        {
            resolvedAdt = SymbolId.None;
            constructorHints = new Dictionary<SymbolId, string>();

            switch (pattern)
            {
                case CtorPattern ctorPattern when ctorPattern.SymbolId.IsValid:
                {
                    var ctorSymbol = symbolTable.GetSymbol<CtorSymbol>(ctorPattern.SymbolId);
                    if (ctorSymbol == null || !ctorSymbol.OwnerAdt.IsValid)
                    {
                        return PatternSpecializationStatus.NotApplicable;
                    }

                    resolvedAdt = ctorSymbol.OwnerAdt;
                    constructorHints[ctorPattern.SymbolId] = InferConstructorPatternWitness(ctorPattern, symbolTable);
                    return PatternSpecializationStatus.ExactFinite;
                }

                case AsPattern { InnerPattern: not null } asPattern:
                    return TryCollectAdtConstructorCases(
                        asPattern.InnerPattern,
                        symbolTable,
                        preferredAdt,
                        out resolvedAdt,
                        out constructorHints);

                case TuplePattern tuplePattern:
                    return TryCollectTupleProjectedAdtConstructorCases(
                        tuplePattern,
                        symbolTable,
                        preferredAdt,
                        out resolvedAdt,
                        out constructorHints);

                case WildcardPattern:
                case VarPattern:
                {
                    if (!preferredAdt.IsValid ||
                        !TryGetAdtConstructors(symbolTable, preferredAdt, out var allConstructors))
                    {
                        return PatternSpecializationStatus.NotApplicable;
                    }

                    resolvedAdt = preferredAdt;
                    foreach (var ctorId in allConstructors)
                    {
                        constructorHints[ctorId] = InferConstructorWitnessFromSymbol(symbolTable, ctorId);
                    }

                    return constructorHints.Count > 0
                        ? PatternSpecializationStatus.ExactFinite
                        : PatternSpecializationStatus.NotApplicable;
                }

                case OrPattern { Alternatives.Count: > 0 } orPattern:
                {
                    var mergedAdt = preferredAdt;
                    var mergedHints = new Dictionary<SymbolId, string>();

                    foreach (var alternative in orPattern.Alternatives)
                    {
                        var alternativeStatus = TryCollectAdtConstructorCases(
                            alternative,
                            symbolTable,
                            mergedAdt,
                            out var alternativeAdt,
                            out var alternativeHints);
                        if (alternativeStatus is not PatternSpecializationStatus.ExactFinite)
                        {
                            constructorHints = [];
                            resolvedAdt = SymbolId.None;
                            return alternativeStatus;
                        }

                        if (!mergedAdt.IsValid)
                        {
                            mergedAdt = alternativeAdt;
                        }
                        else if (alternativeAdt.IsValid && mergedAdt != alternativeAdt)
                        {
                            constructorHints = [];
                            resolvedAdt = SymbolId.None;
                            return PatternSpecializationStatus.Untrackable;
                        }

                        foreach (var (ctorId, hint) in alternativeHints)
                        {
                            AddConstructorHint(mergedHints, ctorId, hint);
                        }
                    }

                    if (!mergedAdt.IsValid)
                    {
                        constructorHints = [];
                        resolvedAdt = SymbolId.None;
                        return PatternSpecializationStatus.NotApplicable;
                    }

                    resolvedAdt = mergedAdt;
                    constructorHints = mergedHints;
                    return PatternSpecializationStatus.ExactFinite;
                }

                case AndPattern { Conjuncts.Count: > 0 } andPattern:
                {
                    var mergedAdt = preferredAdt;
                    HashSet<SymbolId>? intersection = null;
                    var candidateHints = new Dictionary<SymbolId, string>();

                    foreach (var conjunct in andPattern.Conjuncts)
                    {
                        var conjunctStatus = TryCollectAdtConstructorCases(
                            conjunct,
                            symbolTable,
                            mergedAdt,
                            out var conjunctAdt,
                            out var conjunctHints);
                        if (conjunctStatus is not PatternSpecializationStatus.ExactFinite)
                        {
                            constructorHints = [];
                            resolvedAdt = SymbolId.None;
                            return conjunctStatus;
                        }

                        if (!mergedAdt.IsValid)
                        {
                            mergedAdt = conjunctAdt;
                        }
                        else if (conjunctAdt.IsValid && mergedAdt != conjunctAdt)
                        {
                            constructorHints = [];
                            resolvedAdt = SymbolId.None;
                            return PatternSpecializationStatus.Untrackable;
                        }

                        var conjunctIds = conjunctHints.Keys.ToHashSet();
                        if (intersection == null)
                        {
                            intersection = conjunctIds;
                        }
                        else
                        {
                            intersection.IntersectWith(conjunctIds);
                        }

                        foreach (var (ctorId, hint) in conjunctHints)
                        {
                            AddConstructorHint(candidateHints, ctorId, hint);
                        }
                    }

                    if (!mergedAdt.IsValid || intersection == null)
                    {
                        constructorHints = [];
                        resolvedAdt = SymbolId.None;
                        return PatternSpecializationStatus.Untrackable;
                    }

                    var intersectionHints = new Dictionary<SymbolId, string>();
                    foreach (var ctorId in intersection)
                    {
                        intersectionHints[ctorId] = candidateHints.TryGetValue(ctorId, out var hint)
                            ? hint
                            : InferConstructorWitnessFromSymbol(symbolTable, ctorId);
                    }

                    resolvedAdt = mergedAdt;
                    constructorHints = intersectionHints;
                    return PatternSpecializationStatus.ExactFinite;
                }

                case NotPattern { InnerPattern: not null } notPattern:
                {
                    var innerStatus = TryCollectAdtConstructorCases(
                        notPattern.InnerPattern,
                        symbolTable,
                        preferredAdt,
                        out var innerAdt,
                        out var innerHints);
                    if (innerStatus is not PatternSpecializationStatus.ExactFinite)
                    {
                        constructorHints = [];
                        resolvedAdt = SymbolId.None;
                        return innerStatus;
                    }

                    var targetAdt = preferredAdt.IsValid ? preferredAdt : innerAdt;
                    if (!targetAdt.IsValid ||
                        !TryGetAdtConstructors(symbolTable, targetAdt, out var allConstructors))
                    {
                        constructorHints = [];
                        resolvedAdt = SymbolId.None;
                        return PatternSpecializationStatus.NotApplicable;
                    }

                    var complementHints = new Dictionary<SymbolId, string>();
                    foreach (var ctorId in allConstructors)
                    {
                        if (!innerHints.ContainsKey(ctorId))
                        {
                            complementHints[ctorId] = InferConstructorWitnessFromSymbol(symbolTable, ctorId);
                        }
                    }

                    resolvedAdt = targetAdt;
                    constructorHints = complementHints;
                    return PatternSpecializationStatus.ExactFinite;
                }

                default:
                    return PatternSpecializationStatus.NotApplicable;
            }
        }
        private static PatternSpecializationStatus TryCollectTupleProjectedAdtConstructorCases(
            TuplePattern tuplePattern,
            SymbolTable symbolTable,
            SymbolId preferredAdt,
            out SymbolId resolvedAdt,
            out Dictionary<SymbolId, string> constructorHints)
        {
            resolvedAdt = SymbolId.None;
            constructorHints = new Dictionary<SymbolId, string>();

            if (tuplePattern.Elements.Count == 0)
            {
                return PatternSpecializationStatus.NotApplicable;
            }

            var projectedElementIndex = -1;
            for (var i = 0; i < tuplePattern.Elements.Count; i++)
            {
                if (IsPatternIrrefutableForFiniteCoverage(tuplePattern.Elements[i]))
                {
                    continue;
                }

                if (projectedElementIndex >= 0)
                {
                    return PatternSpecializationStatus.NotApplicable;
                }

                projectedElementIndex = i;
            }

            if (projectedElementIndex < 0)
            {
                return PatternSpecializationStatus.NotApplicable;
            }

            var status = TryCollectAdtConstructorCases(
                tuplePattern.Elements[projectedElementIndex],
                symbolTable,
                preferredAdt,
                out resolvedAdt,
                out constructorHints);
            if (status is not PatternSpecializationStatus.ExactFinite ||
                !resolvedAdt.IsValid ||
                constructorHints.Count == 0)
            {
                resolvedAdt = SymbolId.None;
                constructorHints = [];
                return status;
            }

            return PatternSpecializationStatus.ExactFinite;
        }

        internal static bool TryGetAdtConstructors(
            SymbolTable symbolTable,
            SymbolId adtId,
            out IReadOnlyList<SymbolId> constructors)
        {
            if (!adtId.IsValid ||
                symbolTable.GetSymbol<AdtSymbol>(adtId) is not { } adtSymbol ||
                adtSymbol.Constructors.Count == 0)
            {
                constructors = [];
                return false;
            }

            constructors = adtSymbol.Constructors
                .OrderBy(id => id.Value)
                .ToList();
            return true;
        }

        internal static void AddConstructorHint(
            IDictionary<SymbolId, string> hints,
            SymbolId ctorId,
            string hint)
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                return;
            }

            if (!hints.TryGetValue(ctorId, out var current))
            {
                hints[ctorId] = hint;
                return;
            }

            if (GetWitnessSpecificityRank(hint) > GetWitnessSpecificityRank(current))
            {
                hints[ctorId] = hint;
            }
        }

        private static int GetWitnessSpecificityRank(string witnessText)
        {
            if (string.IsNullOrWhiteSpace(witnessText))
            {
                return 0;
            }

            if (witnessText.Contains("{...}", StringComparison.Ordinal))
            {
                return 3;
            }

            if (witnessText.Contains("(...)"))
            {
                return 2;
            }

            return 1;
        }

        internal static string InferConstructorPatternWitness(CtorPattern pattern, SymbolTable symbolTable)
        {
            var ctorName = pattern.SymbolId.IsValid
                ? GetConstructorDisplayName(symbolTable, pattern.SymbolId)
                : pattern.ConstructorName;

            var hasNamed = pattern.NamedPatterns.Count > 0;
            var hasPositional = pattern.PositionalPatterns.Count > 0;

            if (hasNamed && !hasPositional)
            {
                return $"{ctorName}{{...}}";
            }

            if (hasNamed || hasPositional)
            {
                return $"{ctorName}(...)";
            }

            return ctorName;
        }

        internal static string InferConstructorWitnessFromSymbol(SymbolTable symbolTable, SymbolId ctorId)
        {
            var ctorName = GetConstructorDisplayName(symbolTable, ctorId);
            var ctorSymbol = symbolTable.GetSymbol<CtorSymbol>(ctorId);
            if (ctorSymbol == null || ctorSymbol.IsNullary)
            {
                return ctorName;
            }

            if (ctorSymbol.NamedFields.Count > 0 && ctorSymbol.PositionalArgs.Count == 0)
            {
                return $"{ctorName}{{...}}";
            }

            return $"{ctorName}(...)";
        }
    }
}
