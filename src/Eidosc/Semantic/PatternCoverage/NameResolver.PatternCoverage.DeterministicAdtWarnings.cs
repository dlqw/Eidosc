using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void EmitAdditionalDeterministicAdtCoveredWarnings(
        IReadOnlyList<PatternUsefulnessBranchFact> branchFacts,
        IReadOnlyDictionary<int, PatternUsefulnessBranchFact> branchFactsByIndex,
        IReadOnlyDictionary<int, EidosAstNode> branchGuardsByIndex,
        ISet<int> handledCoveredBranches)
    {
        if (branchFacts.Count == 0)
        {
            return;
        }

        var orderedBranches = OrderPatternUsefulnessBranchesByIndex(branchFacts);

        for (var i = 0; i < orderedBranches.Count; i++)
        {
            var targetBranch = orderedBranches[i];
            if (handledCoveredBranches.Contains(targetBranch.BranchIndex))
            {
                continue;
            }

            if (targetBranch.Pattern == null)
            {
                continue;
            }

            if (HasAnyViewPattern(targetBranch.Pattern))
            {
                continue;
            }

            if (!PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                    targetBranch.Pattern,
                    _symbolTable,
                    out var targetAdt,
                    out var targetConstructors))
            {
                continue;
            }

            if (targetConstructors.Count == 0)
            {
                continue;
            }

            var targetConstructorSet = targetConstructors
                .ToHashSet();
            var candidateCoveringBranches = CollectCandidateDeterministicCoveredBranches(
                orderedBranches,
                i,
                branchGuardsByIndex,
                coveringBranch =>
                    TryResolveAdtCoverageTarget(coveringBranch.Pattern!, _symbolTable, out var coveringAdt) &&
                    coveringAdt == targetAdt);

            if (candidateCoveringBranches.Count == 0 ||
                !TryCollectDeterministicAdtCoveringBranchesForTargetPattern(
                    candidateCoveringBranches,
                    branchGuardsByIndex,
                    targetBranch.Pattern,
                    _symbolTable,
                    targetAdt,
                    targetConstructorSet,
                    out var coveringBranchIndices,
                    out var coveringWitnesses,
                    out var coveringWitnessTraces) ||
                !TryEmitDeterministicCoveredWarning(
                    branchFactsByIndex,
                    handledCoveredBranches,
                    targetBranch.BranchIndex,
                    coveringBranchIndices,
                    coveringWitnesses,
                    coveringWitnessTraces))
            {
                continue;
            }
        }
    }

    private static bool TryCollectDeterministicAdtCoveringBranchesForTargetPattern(
        IReadOnlyList<PatternUsefulnessBranchFact> candidateCoveringBranches,
        IReadOnlyDictionary<int, EidosAstNode> branchGuardsByIndex,
        Pattern targetPattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        IReadOnlySet<SymbolId> targetConstructors,
        out IReadOnlyList<int> coveringBranchIndices,
        out IReadOnlyList<PatternWitness> coveringWitnesses,
        out IReadOnlyList<PatternWitnessTrace> coveringWitnessTraces)
    {
        var perCaseCoveringBranchIndices = new HashSet<int>();
        var perCaseWitnessTraces = new List<PatternWitnessTrace>();
        if (!TryVisitDeterministicTargetProfilesAndAssignments(
                targetPattern,
                symbolTable,
                targetAdt,
                targetConstructors,
                (SymbolId targetConstructor, CtorDeterministicIntConstraintProfile targetProfile) =>
                {
                    if (!TryFindDeterministicAdtCoveringBranchForTargetProfile(
                            candidateCoveringBranches,
                            branchGuardsByIndex,
                            symbolTable,
                            targetAdt,
                            targetConstructor,
                            targetProfile,
                            out var coveringBranchIndex))
                    {
                        return false;
                    }

                    perCaseCoveringBranchIndices.Add(coveringBranchIndex);
                    var witness = CreateAdtCoverageWitnessForTargetProfile(
                        symbolTable,
                        targetConstructor,
                        targetProfile);
                    perCaseWitnessTraces.Add(new PatternWitnessTrace(
                        witness,
                        coveringBranchIndex));
                    return true;
                }))
        {
            coveringBranchIndices = [];
            coveringWitnesses = [];
            coveringWitnessTraces = [];
            return false;
        }

        return TryFinalizeDeterministicCoveringData(
            perCaseCoveringBranchIndices,
            perCaseWitnessTraces,
            out coveringBranchIndices,
            out coveringWitnesses,
            out coveringWitnessTraces);
    }

    private static PatternWitness CreateAdtCoverageWitnessForTargetProfile(
        SymbolTable symbolTable,
        SymbolId targetConstructor,
        CtorDeterministicIntConstraintProfile targetProfile)
    {
        var ctorName = symbolTable.GetSymbol(targetConstructor)?.Name;
        if (string.IsNullOrWhiteSpace(ctorName))
        {
            ctorName = $"Ctor#{targetConstructor.Value}";
        }

        var profileText = FormatDeterministicConstraintProfileForWitness(targetProfile);
        if (string.IsNullOrEmpty(profileText))
        {
            return new PatternWitness(
                PatternWitnessKind.Constructor,
                ctorName!,
                $"ctor:{targetConstructor.Value}");
        }

        return new PatternWitness(
            PatternWitnessKind.Constructor,
            $"{ctorName}[{profileText}]",
            $"ctor:{targetConstructor.Value}:{BuildDeterministicConstraintProfileStableKey(targetProfile)}");
    }

    private static string FormatDeterministicConstraintProfileForWitness(CtorDeterministicIntConstraintProfile profile)
    {
        var parts = new List<string>();
        AppendDeterministicConstraintWitnessParts(
            parts,
            profile.PositionalConstraints,
            position => $"p{position}",
            value => value.ToString());
        AppendDeterministicConstraintWitnessParts(
            parts,
            profile.NamedConstraints,
            fieldName => fieldName,
            value => value.ToString(),
            StringComparer.Ordinal);
        AppendDeterministicConstraintWitnessParts(
            parts,
            profile.PositionalBoolConstraints,
            position => $"p{position}",
            value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False);
        AppendDeterministicConstraintWitnessParts(
            parts,
            profile.NamedBoolConstraints,
            fieldName => fieldName,
            value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False,
            StringComparer.Ordinal);

        return string.Join(",", parts);
    }

    private static void AppendDeterministicConstraintWitnessParts<TKey, TValue>(
        List<string> parts,
        IReadOnlyDictionary<TKey, IReadOnlyCollection<TValue>> constraints,
        Func<TKey, string> formatKey,
        Func<TValue, string> formatValue,
        IComparer<TKey>? keyComparer = null)
        where TKey : notnull
        where TValue : IComparable<TValue>
    {
        foreach (var (key, values) in constraints.OrderBy(pair => pair.Key, keyComparer ?? Comparer<TKey>.Default))
        {
            parts.Add($"{formatKey(key)}={string.Join(WellKnownStrings.Punctuation.Pipe, NormalizeDeterministicValues(values).Select(formatValue))}");
        }
    }

    private static bool TryFindDeterministicAdtCoveringBranchForTargetProfile(
        IReadOnlyList<PatternUsefulnessBranchFact> candidateCoveringBranches,
        IReadOnlyDictionary<int, EidosAstNode> branchGuardsByIndex,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor,
        CtorDeterministicIntConstraintProfile targetProfile,
        out int coveringBranchIndex)
    {
        coveringBranchIndex = 0;
        for (var i = 0; i < candidateCoveringBranches.Count; i++)
        {
            var coveringBranch = candidateCoveringBranches[i];
            if (coveringBranch.Pattern == null ||
                !branchGuardsByIndex.TryGetValue(coveringBranch.BranchIndex, out var guardExpression))
            {
                continue;
            }

            if (!TryPatternDeterministicallyCoversTargetProfile(
                    coveringBranch.Pattern,
                    symbolTable,
                    targetAdt,
                    targetConstructor,
                    targetProfile,
                    out _))
            {
                continue;
            }

            if (!IsGuardProvablyTrueForDeterministicTargetProfile(
                    coveringBranch.Pattern,
                    guardExpression,
                    symbolTable,
                    targetAdt,
                    targetConstructor,
                    targetProfile))
            {
                continue;
            }

            coveringBranchIndex = coveringBranch.BranchIndex;
            return true;
        }

        return false;
    }

    private static bool IsGuardProvablyTrueForDeterministicAdtCoverage(
        Pattern coveringPattern,
        EidosAstNode guardExpression,
        Pattern targetPattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        IReadOnlySet<SymbolId> targetConstructors)
    {
        return TryVisitDeterministicTargetProfilesAndAssignments(
            targetPattern,
            symbolTable,
            targetAdt,
            targetConstructors,
            (SymbolId targetConstructor, CtorDeterministicIntConstraintProfile targetProfile) =>
                IsGuardProvablyTrueForDeterministicTargetProfile(
                    coveringPattern,
                    guardExpression,
                    symbolTable,
                    targetAdt,
                    targetConstructor,
                    targetProfile));
    }

    private static bool IsGuardProvablyTrueForDeterministicTargetProfile(
        Pattern coveringPattern,
        EidosAstNode guardExpression,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor,
        CtorDeterministicIntConstraintProfile targetProfile)
    {
        if (!TryCollectDeterministicGuardBindingsForTargetProfile(
                coveringPattern,
                symbolTable,
                targetAdt,
                targetConstructor,
                targetProfile,
                out var knownBoolBindings,
                out var knownIntBindings))
        {
            return false;
        }

        return EvaluateGuardTruthWithBindings(
            guardExpression,
            knownBoolBindings,
            knownIntBindings) is GuardTruth.True;
    }

    private static bool TryCollectDeterministicGuardBindingsForTargetProfile(
        Pattern pattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor,
        CtorDeterministicIntConstraintProfile targetProfile,
        out Dictionary<string, bool> knownBoolBindings,
        out Dictionary<string, ListGuardIntBinding> knownIntBindings)
    {
        knownBoolBindings = new Dictionary<string, bool>(StringComparer.Ordinal);
        knownIntBindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
        switch (pattern)
        {
            case AsPattern { InnerPattern: not null } asPattern:
                return TryCollectDeterministicGuardBindingsForTargetProfile(
                    asPattern.InnerPattern,
                    symbolTable,
                    targetAdt,
                    targetConstructor,
                    targetProfile,
                    out knownBoolBindings,
                    out knownIntBindings);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var alternativeBoolBindings = new List<Dictionary<string, bool>>();
                var alternativeIntBindings = new List<Dictionary<string, ListGuardIntBinding>>();
                for (var i = 0; i < orPattern.Alternatives.Count; i++)
                {
                    var alternative = orPattern.Alternatives[i];
                    if (!CouldPatternPotentiallyMatchDeterministicTargetProfile(
                            alternative,
                            symbolTable,
                            targetAdt,
                            targetConstructor,
                            targetProfile))
                    {
                        continue;
                    }

                    if (!CouldPatternMatchAnyDeterministicTargetAssignment(
                            alternative,
                            symbolTable,
                            targetAdt,
                            targetConstructor,
                            targetProfile))
                    {
                        continue;
                    }

                    if (!TryCollectDeterministicGuardBindingsForTargetProfile(
                            alternative,
                            symbolTable,
                            targetAdt,
                            targetConstructor,
                            targetProfile,
                            out var alternativeKnownBoolBindings,
                            out var alternativeKnownIntBindings))
                    {
                        knownBoolBindings = [];
                        knownIntBindings = [];
                        return false;
                    }

                    alternativeBoolBindings.Add(alternativeKnownBoolBindings);
                    alternativeIntBindings.Add(alternativeKnownIntBindings);
                }

                if (alternativeBoolBindings.Count == 0 ||
                    alternativeIntBindings.Count == 0)
                {
                    knownBoolBindings = [];
                    knownIntBindings = [];
                    return false;
                }

                knownBoolBindings = IntersectKnownBoolBindingsAcrossAlternatives(alternativeBoolBindings);
                knownIntBindings = MergeKnownIntBindingsAcrossAlternatives(alternativeIntBindings);
                return true;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                var mergedBoolBindings = new Dictionary<string, bool>(StringComparer.Ordinal);
                var mergedIntBindings = new Dictionary<string, ListGuardIntBinding>(StringComparer.Ordinal);
                for (var i = 0; i < andPattern.Conjuncts.Count; i++)
                {
                    var conjunct = andPattern.Conjuncts[i];
                    if (!CouldPatternPotentiallyMatchDeterministicTargetProfile(
                            conjunct,
                            symbolTable,
                            targetAdt,
                            targetConstructor,
                            targetProfile) ||
                        !TryCollectDeterministicGuardBindingsForTargetProfile(
                            conjunct,
                            symbolTable,
                            targetAdt,
                            targetConstructor,
                            targetProfile,
                            out var conjunctKnownBoolBindings,
                            out var conjunctKnownIntBindings) ||
                        !TryMergeKnownBoolBindings(
                            mergedBoolBindings,
                            conjunctKnownBoolBindings,
                            out mergedBoolBindings) ||
                        !TryMergeKnownIntBindings(
                            mergedIntBindings,
                            conjunctKnownIntBindings,
                            out mergedIntBindings))
                    {
                        knownBoolBindings = [];
                        knownIntBindings = [];
                        return false;
                    }
                }

                knownBoolBindings = mergedBoolBindings;
                knownIntBindings = mergedIntBindings;
                return true;
            }

            case CtorPattern ctorPattern when ctorPattern.SymbolId.IsValid:
                if (!PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                        ctorPattern,
                        symbolTable,
                        out var ctorAdt,
                        out var ctorIds) ||
                    ctorAdt != targetAdt ||
                    !ctorIds.Contains(targetConstructor) ||
                    !TryCollectKnownBoolBindingsFromPattern(ctorPattern, out knownBoolBindings) ||
                    !TryCollectKnownIntBindingsFromPattern(ctorPattern, out knownIntBindings))
                {
                    knownBoolBindings = [];
                    knownIntBindings = [];
                    return false;
                }

                if (!TryNarrowCtorGuardBindingsForTargetProfile(
                        ctorPattern,
                        targetProfile,
                        ref knownBoolBindings,
                        ref knownIntBindings))
                {
                    knownBoolBindings = [];
                    knownIntBindings = [];
                    return false;
                }

                return true;

            default:
                knownBoolBindings = [];
                knownIntBindings = [];
                return false;
        }
    }

    private delegate bool TryVisitDeterministicTargetProfile(
        SymbolId targetConstructor,
        CtorDeterministicIntConstraintProfile targetProfile);

    private static bool TryVisitDeterministicTargetProfiles(
        Pattern targetPattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        IReadOnlySet<SymbolId> targetConstructors,
        TryVisitDeterministicTargetProfile visitTargetProfile)
    {
        foreach (var targetConstructor in targetConstructors)
        {
            var targetProfiles = CollectDeterministicTargetConstructorConstraintProfiles(
                targetPattern,
                symbolTable,
                targetAdt,
                targetConstructor);
            if (targetProfiles.Count == 0 ||
                targetProfiles.Any(profile => !profile.HasAnyConstraint))
            {
                return false;
            }

            for (var profileIndex = 0; profileIndex < targetProfiles.Count; profileIndex++)
            {
                if (!visitTargetProfile(targetConstructor, targetProfiles[profileIndex]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryVisitDeterministicTargetProfilesAndAssignments(
        Pattern targetPattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        IReadOnlySet<SymbolId> targetConstructors,
        TryVisitDeterministicTargetProfile visitTargetProfile)
    {
        return TryVisitDeterministicTargetProfiles(
            targetPattern,
            symbolTable,
            targetAdt,
            targetConstructors,
            (SymbolId targetConstructor, CtorDeterministicIntConstraintProfile targetProfile) =>
            {
                if (TryEnumerateDeterministicConstraintAssignments(
                        targetProfile,
                        out var targetAssignments,
                        out _) &&
                    targetAssignments.Count > 0)
                {
                    for (var assignmentIndex = 0; assignmentIndex < targetAssignments.Count; assignmentIndex++)
                    {
                        if (!visitTargetProfile(targetConstructor, targetAssignments[assignmentIndex]))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                return visitTargetProfile(targetConstructor, targetProfile);
            });
    }
}
