using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static bool HasDeterministicNonViewCoverageForTargetBranch(
        Pattern? coveringPattern,
        Pattern? targetPattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        IReadOnlySet<SymbolId> targetConstructors,
        out DeterministicCoverageFailureReason failureReason)
    {
        failureReason = DeterministicCoverageFailureReason.None;
        if (coveringPattern == null ||
            targetPattern == null ||
            targetConstructors.Count == 0)
        {
            return false;
        }

        var resolvedFailureReason = DeterministicCoverageFailureReason.None;
        var hasCoverage = TryVisitDeterministicTargetProfiles(
            targetPattern,
            symbolTable,
            targetAdt,
            targetConstructors,
            (SymbolId targetConstructor, CtorDeterministicIntConstraintProfile targetProfile) =>
            {
                if (!TryPatternDeterministicallyCoversTargetProfile(
                        coveringPattern,
                        symbolTable,
                        targetAdt,
                        targetConstructor,
                        targetProfile,
                        out var coverageFailureReason))
                {
                    if (coverageFailureReason == DeterministicCoverageFailureReason.AssignmentOverflow)
                    {
                        resolvedFailureReason = DeterministicCoverageFailureReason.AssignmentOverflow;
                    }

                    return false;
                }

                return true;
            });
        failureReason = resolvedFailureReason;
        return hasCoverage;
    }

    private void EmitAdditionalDeterministicListCoveredWarnings(
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

            if (!PatternUsefulnessAnalyzer.TryGetExactListCoverageCases(
                    targetBranch.Pattern,
                    out var targetCases,
                    preferBoolVectorSplit: true))
            {
                continue;
            }

            if (targetCases.Count == 0)
            {
                continue;
            }

            if (!TryParseListTargetCases(targetCases, out var parsedTargetCases) ||
                parsedTargetCases.Count != targetCases.Count)
            {
                continue;
            }

            var intDomainsByIndex = CollectListIntDomainsByIndex(targetCases);
            var candidateCoveringBranches = CollectCandidateDeterministicCoveredBranches(
                orderedBranches,
                i,
                branchGuardsByIndex);

            if (candidateCoveringBranches.Count == 0 ||
                !TryCollectDeterministicListCoveringBranchesForTargetCases(
                    candidateCoveringBranches,
                    branchGuardsByIndex,
                    targetCases,
                    parsedTargetCases,
                    intDomainsByIndex,
                    HasCharLiteralOrRangePattern(targetBranch.Pattern),
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

    private static bool TryCollectDeterministicListCoveringBranchesForTargetCases(
        IReadOnlyList<PatternUsefulnessBranchFact> candidateCoveringBranches,
        IReadOnlyDictionary<int, EidosAstNode> branchGuardsByIndex,
        IReadOnlyList<ListCoverageCase> targetCases,
        IReadOnlyList<ParsedListTargetCase> parsedTargetCases,
        IReadOnlyDictionary<int, IReadOnlySet<long>> intDomainsByIndex,
        bool preferCharLiteralHints,
        out IReadOnlyList<int> coveringBranchIndices,
        out IReadOnlyList<PatternWitness> coveringWitnesses,
        out IReadOnlyList<PatternWitnessTrace> coveringWitnessTraces)
    {
        if (targetCases.Count == 0 ||
            parsedTargetCases.Count != targetCases.Count)
        {
            coveringBranchIndices = [];
            coveringWitnesses = [];
            coveringWitnessTraces = [];
            return false;
        }

        return TryCollectDeterministicCoveringBranchesForCases(
            targetCases,
            (ListCoverageCase targetCase, int caseIndex, out int coveringBranchIndex) =>
                TryFindDeterministicListCoveringBranchForTargetCase(
                    candidateCoveringBranches,
                    branchGuardsByIndex,
                    targetCases,
                    parsedTargetCases,
                    intDomainsByIndex,
                    caseIndex,
                    out coveringBranchIndex),
            (targetCase, _) => CreateListCoverageWitness(targetCase, preferCharLiteralHints),
            out coveringBranchIndices,
            out coveringWitnesses,
            out coveringWitnessTraces);
    }

    private static bool TryFindDeterministicListCoveringBranchForTargetCase(
        IReadOnlyList<PatternUsefulnessBranchFact> candidateCoveringBranches,
        IReadOnlyDictionary<int, EidosAstNode> branchGuardsByIndex,
        IReadOnlyList<ListCoverageCase> targetCases,
        IReadOnlyList<ParsedListTargetCase> parsedTargetCases,
        IReadOnlyDictionary<int, IReadOnlySet<long>> intDomainsByIndex,
        int targetCaseIndex,
        out int coveringBranchIndex)
    {
        coveringBranchIndex = 0;
        if (targetCaseIndex < 0 ||
            targetCaseIndex >= targetCases.Count ||
            targetCaseIndex >= parsedTargetCases.Count)
        {
            return false;
        }

        var parsedCase = parsedTargetCases[targetCaseIndex];
        for (var i = 0; i < candidateCoveringBranches.Count; i++)
        {
            var coveringBranch = candidateCoveringBranches[i];
            if (coveringBranch.Pattern == null ||
                !branchGuardsByIndex.TryGetValue(coveringBranch.BranchIndex, out var guardExpression))
            {
                continue;
            }

            var deterministicallyCovered = parsedCase.Kind switch
            {
                ParsedListTargetCaseKind.Int when parsedCase.IntTokens is not null =>
                    TryMatchListPatternWithIntVectorViaDeterministicNonViewPath(
                        coveringBranch.Pattern,
                        parsedCase.IntTokens),
                ParsedListTargetCaseKind.Bool when parsedCase.BoolValues is not null =>
                    TryMatchListPatternWithBoolVectorViaDeterministicNonViewPath(
                        coveringBranch.Pattern,
                        parsedCase.BoolValues),
                _ => false
            };

            if (!deterministicallyCovered ||
                EvaluateGuardTruthForListCoverageCase(
                    coveringBranch.Pattern,
                    targetCases[targetCaseIndex],
                    guardExpression,
                    intDomainsByIndex) is not GuardTruth.True)
            {
                continue;
            }

            coveringBranchIndex = coveringBranch.BranchIndex;
            return true;
        }

        return false;
    }

    private void EmitAdditionalDeterministicBoolCoveredWarnings(
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
            var targetBoolCases = new HashSet<bool>();
            if (handledCoveredBranches.Contains(targetBranch.BranchIndex) ||
                targetBranch.Pattern == null ||
                HasAnyViewPattern(targetBranch.Pattern) ||
                !TryGetExactBoolPatternCases(targetBranch.Pattern, targetBoolCases) ||
                targetBoolCases.Count == 0)
            {
                continue;
            }

            var candidateCoveringBranches = CollectCandidateDeterministicCoveredBranches(
                orderedBranches,
                i,
                branchGuardsByIndex);

            if (candidateCoveringBranches.Count == 0 ||
                !TryCollectDeterministicBoolCoveringBranchesForTargetCases(
                    candidateCoveringBranches,
                    branchGuardsByIndex,
                    targetBoolCases,
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

    private static bool TryCollectDeterministicBoolCoveringBranchesForTargetCases(
        IReadOnlyList<PatternUsefulnessBranchFact> candidateCoveringBranches,
        IReadOnlyDictionary<int, EidosAstNode> branchGuardsByIndex,
        IReadOnlyCollection<bool> targetBoolCases,
        out IReadOnlyList<int> coveringBranchIndices,
        out IReadOnlyList<PatternWitness> coveringWitnesses,
        out IReadOnlyList<PatternWitnessTrace> coveringWitnessTraces)
    {
        if (targetBoolCases.Count == 0)
        {
            coveringBranchIndices = [];
            coveringWitnesses = [];
            coveringWitnessTraces = [];
            return false;
        }

        return TryCollectDeterministicCoveringBranchesForCases(
            targetBoolCases.OrderBy(value => value ? 0 : 1).ToArray(),
            (bool targetCase, int _, out int coveringBranchIndex) =>
                TryFindDeterministicBoolCoveringBranchForTargetCase(
                    candidateCoveringBranches,
                    branchGuardsByIndex,
                    targetCase,
                    out coveringBranchIndex),
            (targetCase, _) => CreateBoolCoverageWitness(targetCase),
            out coveringBranchIndices,
            out coveringWitnesses,
            out coveringWitnessTraces);
    }

    private void EmitAdditionalDeterministicTupleBoolCoveredWarnings(
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
            var targetTupleCases = new HashSet<string>(StringComparer.Ordinal);
            if (handledCoveredBranches.Contains(targetBranch.BranchIndex) ||
                targetBranch.Pattern == null ||
                HasAnyViewPattern(targetBranch.Pattern) ||
                !TryGetExactTupleBoolPatternCases(targetBranch.Pattern, out var tupleArity, targetTupleCases) ||
                tupleArity <= 0 ||
                targetTupleCases.Count == 0)
            {
                continue;
            }

            var candidateCoveringBranches = CollectCandidateDeterministicCoveredBranches(
                orderedBranches,
                i,
                branchGuardsByIndex);

            if (candidateCoveringBranches.Count == 0 ||
                !TryCollectDeterministicTupleBoolCoveringBranchesForTargetCases(
                    candidateCoveringBranches,
                    branchGuardsByIndex,
                    targetTupleCases,
                    tupleArity,
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

    private static bool TryCollectDeterministicTupleBoolCoveringBranchesForTargetCases(
        IReadOnlyList<PatternUsefulnessBranchFact> candidateCoveringBranches,
        IReadOnlyDictionary<int, EidosAstNode> branchGuardsByIndex,
        IReadOnlyCollection<string> targetTupleCases,
        int tupleArity,
        out IReadOnlyList<int> coveringBranchIndices,
        out IReadOnlyList<PatternWitness> coveringWitnesses,
        out IReadOnlyList<PatternWitnessTrace> coveringWitnessTraces)
    {
        if (targetTupleCases.Count == 0 ||
            tupleArity <= 0)
        {
            coveringBranchIndices = [];
            coveringWitnesses = [];
            coveringWitnessTraces = [];
            return false;
        }

        return TryCollectDeterministicCoveringBranchesForCases(
            targetTupleCases.OrderBy(@case => @case, StringComparer.Ordinal).ToArray(),
            (string targetCase, int _, out int coveringBranchIndex) =>
            {
                if (!TryParseTupleBoolWitness(targetCase, tupleArity, out var tupleValues))
                {
                    coveringBranchIndex = 0;
                    return false;
                }

                return TryFindDeterministicTupleBoolCoveringBranchForTargetCase(
                    candidateCoveringBranches,
                    branchGuardsByIndex,
                    tupleValues,
                    out coveringBranchIndex);
            },
            (targetCase, _) => CreateTupleBoolCoverageWitness(targetCase),
            out coveringBranchIndices,
            out coveringWitnesses,
            out coveringWitnessTraces);
    }

    private static bool TryFindDeterministicTupleBoolCoveringBranchForTargetCase(
        IReadOnlyList<PatternUsefulnessBranchFact> candidateCoveringBranches,
        IReadOnlyDictionary<int, EidosAstNode> branchGuardsByIndex,
        IReadOnlyList<bool> tupleValues,
        out int coveringBranchIndex)
    {
        coveringBranchIndex = 0;
        for (var i = 0; i < candidateCoveringBranches.Count; i++)
        {
            var coveringBranch = candidateCoveringBranches[i];
            if (coveringBranch.Pattern == null ||
                !branchGuardsByIndex.TryGetValue(coveringBranch.BranchIndex, out var guardExpression) ||
                EvaluateGuardTruthForTupleBoolCoverageCase(
                    coveringBranch.Pattern,
                    tupleValues,
                    guardExpression) is not GuardTruth.True)
            {
                continue;
            }

            coveringBranchIndex = coveringBranch.BranchIndex;
            return true;
        }

        return false;
    }

    private static PatternWitness CreateTupleBoolCoverageWitness(string tupleCase)
    {
        return new PatternWitness(
            PatternWitnessKind.TupleBool,
            tupleCase,
            $"tuple-bool:{tupleCase}");
    }

    private static bool TryFindDeterministicBoolCoveringBranchForTargetCase(
        IReadOnlyList<PatternUsefulnessBranchFact> candidateCoveringBranches,
        IReadOnlyDictionary<int, EidosAstNode> branchGuardsByIndex,
        bool targetCase,
        out int coveringBranchIndex)
    {
        coveringBranchIndex = 0;
        for (var i = 0; i < candidateCoveringBranches.Count; i++)
        {
            var coveringBranch = candidateCoveringBranches[i];
            if (coveringBranch.Pattern == null ||
                !branchGuardsByIndex.TryGetValue(coveringBranch.BranchIndex, out var guardExpression) ||
                !TryMatchBoolPatternViaDeterministicNonViewPath(coveringBranch.Pattern, targetCase) ||
                EvaluateGuardTruthForBoolCoverageCase(
                    coveringBranch.Pattern,
                    targetCase,
                    guardExpression) is not GuardTruth.True)
            {
                continue;
            }

            coveringBranchIndex = coveringBranch.BranchIndex;
            return true;
        }

        return false;
    }

    private static PatternWitness CreateBoolCoverageWitness(bool targetCase)
    {
        return new PatternWitness(
            PatternWitnessKind.BoolLiteral,
            targetCase ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False,
            $"bool:{(targetCase ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False)}");
    }

    private static bool TryFinalizeDeterministicCoveringData(
        IReadOnlyCollection<int> perCaseCoveringBranchIndices,
        IReadOnlyCollection<PatternWitnessTrace> perCaseWitnessTraces,
        out IReadOnlyList<int> coveringBranchIndices,
        out IReadOnlyList<PatternWitness> coveringWitnesses,
        out IReadOnlyList<PatternWitnessTrace> coveringWitnessTraces)
    {
        coveringBranchIndices = perCaseCoveringBranchIndices
            .Distinct()
            .OrderBy(index => index)
            .ToList();
        coveringWitnessTraces = perCaseWitnessTraces
            .Distinct()
            .OrderBy(trace => trace.Witness.DisplayText, StringComparer.Ordinal)
            .ThenBy(trace => trace.CoveringBranchIndex)
            .ToList();
        coveringWitnesses = coveringWitnessTraces
            .Select(trace => trace.Witness)
            .Distinct()
            .OrderBy(witness => witness.DisplayText, StringComparer.Ordinal)
            .ToList();
        return coveringBranchIndices.Count > 0 &&
               coveringWitnessTraces.Count > 0;
    }

    private delegate bool TryFindDeterministicCoveringBranch<TCase>(
        TCase targetCase,
        int caseIndex,
        out int coveringBranchIndex);

    private static bool TryCollectDeterministicCoveringBranchesForCases<TCase>(
        IReadOnlyList<TCase> orderedCases,
        TryFindDeterministicCoveringBranch<TCase> tryFindCoveringBranch,
        Func<TCase, int, PatternWitness> createWitness,
        out IReadOnlyList<int> coveringBranchIndices,
        out IReadOnlyList<PatternWitness> coveringWitnesses,
        out IReadOnlyList<PatternWitnessTrace> coveringWitnessTraces)
    {
        if (orderedCases.Count == 0)
        {
            coveringBranchIndices = [];
            coveringWitnesses = [];
            coveringWitnessTraces = [];
            return false;
        }

        var perCaseCoveringBranchIndices = new List<int>(orderedCases.Count);
        var perCaseWitnessTraces = new List<PatternWitnessTrace>(orderedCases.Count);
        for (var caseIndex = 0; caseIndex < orderedCases.Count; caseIndex++)
        {
            var targetCase = orderedCases[caseIndex];
            if (!tryFindCoveringBranch(targetCase, caseIndex, out var coveringBranchIndex))
            {
                coveringBranchIndices = [];
                coveringWitnesses = [];
                coveringWitnessTraces = [];
                return false;
            }

            perCaseCoveringBranchIndices.Add(coveringBranchIndex);
            perCaseWitnessTraces.Add(new PatternWitnessTrace(
                createWitness(targetCase, caseIndex),
                coveringBranchIndex));
        }

        return TryFinalizeDeterministicCoveringData(
            perCaseCoveringBranchIndices,
            perCaseWitnessTraces,
            out coveringBranchIndices,
            out coveringWitnesses,
            out coveringWitnessTraces);
    }

    private List<PatternUsefulnessBranchFact> CollectCandidateDeterministicCoveredBranches(
        IReadOnlyList<PatternUsefulnessBranchFact> orderedBranches,
        int targetBranchIndex,
        IReadOnlyDictionary<int, EidosAstNode> branchGuardsByIndex,
        Func<PatternUsefulnessBranchFact, bool>? additionalFilter = null)
    {
        var candidateCoveringBranches = new List<PatternUsefulnessBranchFact>();
        for (var i = 0; i < targetBranchIndex; i++)
        {
            var coveringBranch = orderedBranches[i];
            if (!coveringBranch.IsGuarded ||
                coveringBranch.Pattern == null ||
                !HasRefutableViewPattern(coveringBranch.Pattern) ||
                !branchGuardsByIndex.ContainsKey(coveringBranch.BranchIndex) ||
                (additionalFilter != null && !additionalFilter(coveringBranch)))
            {
                continue;
            }

            candidateCoveringBranches.Add(coveringBranch);
        }

        return candidateCoveringBranches;
    }

    private static List<PatternUsefulnessBranchFact> OrderPatternUsefulnessBranchesByIndex(
        IReadOnlyList<PatternUsefulnessBranchFact> branchFacts)
    {
        return branchFacts
            .OrderBy(branch => branch.BranchIndex)
            .ToList();
    }

    private bool TryEmitDeterministicCoveredWarning(
        IReadOnlyDictionary<int, PatternUsefulnessBranchFact> branchFactsByIndex,
        ISet<int> handledCoveredBranches,
        int targetBranchIndex,
        IReadOnlyList<int> coveringBranchIndices,
        IReadOnlyList<PatternWitness> coveringWitnesses,
        IReadOnlyList<PatternWitnessTrace> coveringWitnessTraces)
    {
        if (coveringBranchIndices.Count == 0 ||
            !branchFactsByIndex.TryGetValue(targetBranchIndex, out var targetBranchFact))
        {
            return false;
        }

        AddUnreachableCoveredPatternWarning(
            targetBranchFact.Span,
            targetBranchFact.BranchIndex,
            coveringBranchIndices,
            coveringWitnesses,
            coveringWitnessTraces);
        handledCoveredBranches.Add(targetBranchFact.BranchIndex);
        return true;
    }
}
