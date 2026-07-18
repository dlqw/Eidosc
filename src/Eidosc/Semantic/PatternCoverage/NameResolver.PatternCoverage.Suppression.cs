using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static bool ShouldSuppressConservativeListCoveredWarning(
        PatternUnreachableBranch unreachable,
        IReadOnlyDictionary<int, PatternUsefulnessBranchFact> branchFactsByIndex,
        out IReadOnlyList<string> additionalSuppressionReasons)
    {
        additionalSuppressionReasons = [];
        if (!branchFactsByIndex.TryGetValue(unreachable.BranchIndex, out var targetBranch) ||
            targetBranch.Pattern == null ||
            HasAnyViewPattern(targetBranch.Pattern) ||
            unreachable.CoveringBranchIndices == null ||
            unreachable.CoveringBranchIndices.Count == 0 ||
            !PatternUsefulnessAnalyzer.TryGetExactListCoverageCases(
                targetBranch.Pattern,
                out var targetCases,
                preferBoolVectorSplit: true) ||
            targetCases.Count == 0)
        {
            return false;
        }

        if (!TryParseListTargetCases(targetCases, out var parsedTargetCases))
        {
            return false;
        }

        var hasIntTargetCases = parsedTargetCases.Any(parsed => parsed.Kind == ParsedListTargetCaseKind.Int);
        var hasBoolTargetCases = parsedTargetCases.Any(parsed => parsed.Kind == ParsedListTargetCaseKind.Bool);
        var targetDomainValues = hasIntTargetCases
            ? CollectListIntDomainValues(parsedTargetCases)
            : [];
        if (hasIntTargetCases && targetDomainValues.Count == 0)
        {
            return false;
        }

        var reasonTags = new HashSet<string>(StringComparer.Ordinal);
        AddIntAndBoolTargetDomainReasons(
            reasonTags,
            "list",
            targetBranch.Pattern,
            targetDomainValues,
            hasBoolTargetCases);

        var coveringPatterns = new List<Pattern>(unreachable.CoveringBranchIndices.Count);
        var hasUncertainViewCovering = false;
        for (var i = 0; i < unreachable.CoveringBranchIndices.Count; i++)
        {
            var coveringBranchIndex = unreachable.CoveringBranchIndices[i];
            if (!branchFactsByIndex.TryGetValue(coveringBranchIndex, out var coveringBranch) ||
                coveringBranch.Pattern == null)
            {
                return false;
            }

            coveringPatterns.Add(coveringBranch.Pattern);
            if (coveringBranch.IsGuarded && coveringBranch.GuardConstant == null)
            {
                reasonTags.Add("guard:not-provable");
            }

            if (HasRefutableViewPattern(coveringBranch.Pattern))
            {
                reasonTags.Add("list:refutable-view");
            }

            var nonFiniteViewPaths = GetNonFiniteRefutableViewPatternPaths(coveringBranch.Pattern);
            if (nonFiniteViewPaths.Count > 0)
            {
                AddNonFiniteViewPathReasons(reasonTags, "list", nonFiniteViewPaths);
            }

            var uncertainViewPaths = GetUncertainRefutableViewPatternPathsForSuppression(
                coveringBranch.Pattern,
                targetDomainValues,
                includeIntDomain: hasIntTargetCases,
                includeBoolDomain: hasBoolTargetCases);
            if (uncertainViewPaths.Count > 0)
            {
                AddUncertainViewPathReasons(reasonTags, "list", uncertainViewPaths);
            }

            if (!HasAnyViewPattern(coveringBranch.Pattern))
            {
                continue;
            }

            var hasUncertainViewForTargetDomain = false;
            if (hasIntTargetCases &&
                HasUncertainViewPatternOverIntDomain(coveringBranch.Pattern, targetDomainValues))
            {
                hasUncertainViewForTargetDomain = true;
            }

            if (hasBoolTargetCases &&
                HasUncertainViewPatternOverBoolDomain(coveringBranch.Pattern))
            {
                hasUncertainViewForTargetDomain = true;
            }

            var hasNegatedRefutableView = HasNegatedRefutableViewPattern(coveringBranch.Pattern);
            if (hasNegatedRefutableView)
            {
                reasonTags.Add("list:negated-refutable-view");
            }

            if (!hasUncertainViewForTargetDomain &&
                !hasNegatedRefutableView)
            {
                return false;
            }

            hasUncertainViewCovering = true;
        }

        if (!hasUncertainViewCovering)
        {
            return false;
        }

        var missedTargetCaseIndices = new List<int>();
        for (var targetCaseIndex = 0; targetCaseIndex < parsedTargetCases.Count; targetCaseIndex++)
        {
            var targetCase = parsedTargetCases[targetCaseIndex];
            var deterministicallyCovered = false;
            for (var i = 0; i < coveringPatterns.Count; i++)
            {
                var coveringPattern = coveringPatterns[i];
                switch (targetCase.Kind)
                {
                    case ParsedListTargetCaseKind.Int when targetCase.IntTokens is not null:
                        if (TryMatchListPatternWithIntVectorViaDeterministicNonViewPath(
                                coveringPattern,
                                targetCase.IntTokens))
                        {
                            deterministicallyCovered = true;
                        }

                        break;
                    case ParsedListTargetCaseKind.Bool when targetCase.BoolValues is not null:
                        if (TryMatchListPatternWithBoolVectorViaDeterministicNonViewPath(
                                coveringPattern,
                                targetCase.BoolValues))
                        {
                            deterministicallyCovered = true;
                        }

                        break;
                }

                if (deterministicallyCovered)
                {
                    break;
                }
            }

            if (!deterministicallyCovered)
            {
                missedTargetCaseIndices.Add(targetCaseIndex);
            }
        }

        if (missedTargetCaseIndices.Count == 0)
        {
            return false;
        }

        reasonTags.Add("list:no-deterministic-nonview-hit");
        var preferCharLiteralReasonKeys =
            hasIntTargetCases &&
            HasCharLiteralOrRangePattern(targetBranch.Pattern);
        for (var i = 0; i < missedTargetCaseIndices.Count; i++)
        {
            var caseIndex = missedTargetCaseIndices[i];
            reasonTags.Add($"list:no-deterministic-nonview-hit-case{caseIndex + 1}");
            reasonTags.Add(
                $"list:no-deterministic-nonview-hit-case{caseIndex + 1}-key:{BuildListDeterministicMissCaseReasonKey(parsedTargetCases[caseIndex], preferCharLiteralReasonKeys)}");
        }

        additionalSuppressionReasons = FinalizeSuppressionReasonTags(reasonTags);
        return true;
    }

    private static bool ShouldSuppressConservativeAdtCoveredWarning(
        PatternUnreachableBranch unreachable,
        IReadOnlyDictionary<int, PatternUsefulnessBranchFact> branchFactsByIndex,
        SymbolTable symbolTable,
        out IReadOnlyList<string> additionalSuppressionReasons)
    {
        additionalSuppressionReasons = [];
        if (!branchFactsByIndex.TryGetValue(unreachable.BranchIndex, out var targetBranch) ||
            unreachable.CoveringBranchIndices == null ||
            unreachable.CoveringBranchIndices.Count == 0)
        {
            return false;
        }

        if (!TryResolveAdtCoverageTargetConstructorsForSuppression(
                unreachable,
                targetBranch,
                symbolTable,
                out var targetAdt,
                out var targetConstructors) ||
            targetConstructors.Count == 0)
        {
            return false;
        }

        if (HasAnyViewPattern(targetBranch.Pattern))
        {
            return false;
        }

        var reasonTags = new HashSet<string>(StringComparer.Ordinal);
        var targetIntDomainValues = new HashSet<long>();
        CollectAdtSuppressionTargetDomains(
            targetBranch.Pattern,
            symbolTable,
            targetAdt,
            targetConstructors,
            targetIntDomainValues,
            out var hasBoolTargetDomain);
        if (targetIntDomainValues.Count > 0)
        {
            reasonTags.Add("adt:target-domain-int");
            if (HasCharLiteralOrRangePattern(targetBranch.Pattern))
            {
                reasonTags.Add("adt:target-domain-char");
            }
        }

        if (hasBoolTargetDomain)
        {
            reasonTags.Add("adt:target-domain-bool");
        }

        for (var i = 0; i < unreachable.CoveringBranchIndices.Count; i++)
        {
            if (!branchFactsByIndex.TryGetValue(unreachable.CoveringBranchIndices[i], out var coveringBranch))
            {
                return false;
            }

            if (!TryResolveAdtCoverageTarget(coveringBranch.Pattern, symbolTable, out var coveringAdt) ||
                coveringAdt != targetAdt ||
                !coveringBranch.IsGuarded ||
                !HasRefutableViewPattern(coveringBranch.Pattern) ||
                HasDominantIrrefutableNonViewOrFallback(
                    coveringBranch.Pattern,
                    symbolTable,
                    targetAdt,
                    targetConstructors))
            {
                return false;
            }

            if (HasDeterministicNonViewCoverageForTargetBranch(
                    coveringBranch.Pattern,
                    targetBranch.Pattern,
                    symbolTable,
                    targetAdt,
                    targetConstructors,
                    out var deterministicFailureReason))
            {
                return false;
            }

            if (deterministicFailureReason == DeterministicCoverageFailureReason.AssignmentOverflow)
            {
                reasonTags.Add("adt:deterministic-assignment-overflow");
                var preferCharLiteralReasonKeys = HasCharLiteralOrRangePattern(targetBranch.Pattern);
                var overflowReasonTags = CollectAdtDeterministicOverflowReasonTags(
                    targetBranch.Pattern,
                    symbolTable,
                    targetAdt,
                    targetConstructors,
                    preferCharLiteralReasonKeys);
                AddReasonTags(reasonTags, overflowReasonTags);
            }
            else
            {
                reasonTags.Add("adt:no-deterministic-nonview-hit");
                var constructorNoHitTags = CollectAdtDeterministicNoHitConstructorReasonTags(
                    coveringBranch.Pattern,
                    targetBranch.Pattern,
                    symbolTable,
                    targetAdt,
                    targetConstructors);
                AddReasonTags(reasonTags, constructorNoHitTags);
            }

            var uncertainViewPaths = GetUncertainRefutableViewPatternPathsForSuppression(
                coveringBranch.Pattern,
                targetIntDomainValues,
                includeIntDomain: targetIntDomainValues.Count > 0,
                includeBoolDomain: hasBoolTargetDomain);
            if (uncertainViewPaths.Count > 0)
            {
                AddUncertainViewPathReasons(reasonTags, "adt", uncertainViewPaths);
            }
        }

        additionalSuppressionReasons = FinalizeSuppressionReasonTags(reasonTags);
        return true;
    }

    private bool ShouldSuppressNonGuardedAdtConstructorRefinementCoveredWarning(
        PatternUnreachableBranch unreachable,
        IReadOnlyDictionary<int, PatternUsefulnessBranchFact> branchFactsByIndex,
        SymbolTable symbolTable,
        out IReadOnlyList<string> additionalSuppressionReasons)
    {
        additionalSuppressionReasons = [];
        if (!branchFactsByIndex.TryGetValue(unreachable.BranchIndex, out var targetBranch) ||
            targetBranch.IsGuarded ||
            targetBranch.Pattern == null ||
            unreachable.CoveringBranchIndices == null ||
            unreachable.CoveringBranchIndices.Count == 0)
        {
            return false;
        }

        if (!TryResolveAdtCoverageTargetConstructorsForSuppression(
                unreachable,
                targetBranch,
                symbolTable,
                out var targetAdt,
                out var targetConstructors) ||
            targetConstructors.Count == 0)
        {
            return false;
        }

        var reasonTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "adt:nonguarded-refined-constructor"
        };

        for (var i = 0; i < unreachable.CoveringBranchIndices.Count; i++)
        {
            if (!branchFactsByIndex.TryGetValue(unreachable.CoveringBranchIndices[i], out var coveringBranch) ||
                coveringBranch.IsGuarded ||
                coveringBranch.Pattern == null ||
                !HasNestedRefutableConstructorRefinement(coveringBranch.Pattern) ||
                !TryResolveAdtCoverageTarget(coveringBranch.Pattern, symbolTable, out var coveringAdt) ||
                coveringAdt != targetAdt)
            {
                return false;
            }

            if (HasDeterministicNonViewCoverageForTargetBranch(
                    coveringBranch.Pattern,
                    targetBranch.Pattern,
                    symbolTable,
                    targetAdt,
                    targetConstructors,
                    out _))
            {
                return false;
            }
        }

        additionalSuppressionReasons = FinalizeSuppressionReasonTags(reasonTags);
        return true;
    }

    private static bool HasNestedRefutableConstructorRefinement(Pattern? pattern)
    {
        return pattern switch
        {
            CtorPattern ctorPattern =>
                ctorPattern.PositionalPatterns.Any(IsNestedRefutableConstructorChild) ||
                ctorPattern.NamedPatterns.Any(named => IsNestedRefutableConstructorChild(named.Pattern)),
            AsPattern asPattern => HasNestedRefutableConstructorRefinement(asPattern.InnerPattern),
            OrPattern orPattern => orPattern.Alternatives.Any(HasNestedRefutableConstructorRefinement),
            AndPattern andPattern => andPattern.Conjuncts.Any(HasNestedRefutableConstructorRefinement),
            TuplePattern tuplePattern => tuplePattern.Elements.Any(HasNestedRefutableConstructorRefinement),
            _ => false
        };
    }

    private static bool IsNestedRefutableConstructorChild(Pattern? pattern)
    {
        return pattern switch
        {
            CtorPattern ctorPattern => !IsPatternIrrefutableForFiniteCoverage(ctorPattern),
            AsPattern asPattern => IsNestedRefutableConstructorChild(asPattern.InnerPattern),
            OrPattern orPattern => orPattern.Alternatives.Any(IsNestedRefutableConstructorChild),
            AndPattern andPattern => andPattern.Conjuncts.Any(IsNestedRefutableConstructorChild),
            TuplePattern tuplePattern => tuplePattern.Elements.Any(IsNestedRefutableConstructorChild),
            _ => false
        };
    }

    private static void CollectAdtSuppressionTargetDomains(
        Pattern? targetPattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        IReadOnlySet<SymbolId> targetConstructors,
        ISet<long> intDomainValues,
        out bool hasBoolDomain)
    {
        hasBoolDomain = false;
        if (targetPattern == null || targetConstructors.Count == 0)
        {
            return;
        }

        foreach (var targetConstructor in targetConstructors)
        {
            var targetProfiles = CollectDeterministicTargetConstructorConstraintProfiles(
                targetPattern,
                symbolTable,
                targetAdt,
                targetConstructor);
            for (var profileIndex = 0; profileIndex < targetProfiles.Count; profileIndex++)
            {
                CollectProfileTargetDomainValues(
                    targetProfiles[profileIndex],
                    intDomainValues,
                    ref hasBoolDomain);
            }
        }
    }

    private static void AddReasonTags(ISet<string> target, IReadOnlyList<string> tags)
    {
        for (var i = 0; i < tags.Count; i++)
        {
            target.Add(tags[i]);
        }
    }

    private static void CollectProfileTargetDomainValues(
        CtorDeterministicIntConstraintProfile profile,
        ISet<long> intDomainValues,
        ref bool hasBoolDomain)
    {
        AddConstraintValues(intDomainValues, profile.PositionalConstraints.Values);
        AddConstraintValues(intDomainValues, profile.NamedConstraints.Values);
        hasBoolDomain |= HasAnyConstraintValues(profile.PositionalBoolConstraints.Values);
        hasBoolDomain |= HasAnyConstraintValues(profile.NamedBoolConstraints.Values);
    }

    private static void AddConstraintValues(
        ISet<long> output,
        IEnumerable<IReadOnlyCollection<long>> constraintGroups)
    {
        foreach (var values in constraintGroups)
        {
            foreach (var value in values)
            {
                output.Add(value);
            }
        }
    }

    private static bool HasAnyConstraintValues<TValue>(
        IEnumerable<IReadOnlyCollection<TValue>> constraintGroups)
    {
        foreach (var values in constraintGroups)
        {
            if (values.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveAdtCoverageTargetConstructorsForSuppression(
        PatternUnreachableBranch unreachable,
        PatternUsefulnessBranchFact targetBranch,
        SymbolTable symbolTable,
        out SymbolId targetAdt,
        out HashSet<SymbolId> targetConstructors)
    {
        targetConstructors = [];

        if (PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                targetBranch.Pattern,
                symbolTable,
                out targetAdt,
                out var targetConstructorIds) &&
            targetConstructorIds.Count > 0)
        {
            targetConstructors = targetConstructorIds.ToHashSet();
            return true;
        }

        targetAdt = SymbolId.None;
        if (unreachable.CoveringWitnesses is not { Count: > 0 })
        {
            return false;
        }

        for (var i = 0; i < unreachable.CoveringWitnesses.Count; i++)
        {
            var witness = unreachable.CoveringWitnesses[i];
            if (witness.Kind != PatternWitnessKind.Constructor ||
                !TryParseConstructorWitnessStableKey(witness, out var ctorId))
            {
                continue;
            }

            if (symbolTable.GetSymbol<CtorSymbol>(ctorId) is not { } ctorSymbol)
            {
                continue;
            }

            var constructorAdt = symbolTable.GetClosedCaseRoot(ctorSymbol.OwnerAdt);
            if (!targetAdt.IsValid)
            {
                targetAdt = constructorAdt;
            }
            else if (targetAdt != constructorAdt)
            {
                targetConstructors.Clear();
                targetAdt = SymbolId.None;
                return false;
            }

            targetConstructors.Add(ctorId);
        }

        return targetAdt.IsValid && targetConstructors.Count > 0;
    }

    private static bool TryParseConstructorWitnessStableKey(PatternWitness witness, out SymbolId constructorId)
    {
        constructorId = SymbolId.None;
        if (string.IsNullOrWhiteSpace(witness.StableKey) ||
            !witness.StableKey.StartsWith("ctor:", StringComparison.Ordinal) ||
            !int.TryParse(witness.StableKey[5..], out var rawId) ||
            rawId <= 0)
        {
            return false;
        }

        constructorId = new SymbolId(rawId);
        return constructorId.IsValid;
    }

    private static bool HasDominantIrrefutableNonViewOrFallback(
        Pattern? pattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        IReadOnlySet<SymbolId> targetConstructors)
    {
        if (pattern == null || targetConstructors.Count == 0)
        {
            return false;
        }

        foreach (var targetConstructor in targetConstructors)
        {
            if (!HasDominantIrrefutableNonViewOrFallbackForConstructor(
                    pattern,
                    symbolTable,
                    targetAdt,
                    targetConstructor))
            {
                return false;
            }
        }

        return true;
    }

}
