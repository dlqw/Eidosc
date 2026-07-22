using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Utils;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private PatternCoverageContext CreatePatternCoverageContext(
        IReadOnlyList<PatternUsefulnessBranchFact> branchFacts,
        SymbolId preferredAdt)
    {
        var summary = PatternUsefulnessAnalyzer.Analyze(branchFacts, _symbolTable, preferredAdt);
        var unresolvedGuardBranchIndices = branchFacts
            .Where(branch => IsUnresolvedGuardBranchForCoverageTarget(branch, summary.CoverageTarget))
            .Select(branch => branch.BranchIndex)
            .Distinct()
            .OrderBy(index => index)
            .ToList();
        var unresolvedGuardBranchHints = BuildUnresolvedGuardBranchHints(
            unresolvedGuardBranchIndices,
            branchFacts,
            summary.CoverageTarget);
        var branchFactsByIndex = branchFacts
            .GroupBy(branch => branch.BranchIndex)
            .ToDictionary(group => group.Key, group => group.Last());

        return new PatternCoverageContext(
            summary,
            unresolvedGuardBranchIndices,
            unresolvedGuardBranchHints,
            summary.HasGuardedBranches || unresolvedGuardBranchIndices.Count > 0,
            RequiresHiddenCaseWildcard(preferredAdt),
            branchFactsByIndex,
            [],
            []);
    }

    private void EmitPatternUnreachableWarnings(PatternCoverageContext context)
    {
        foreach (var unreachable in context.Summary.UnreachableBranches)
        {
            switch (unreachable.Kind)
            {
                case PatternUnreachableKind.ConstantFalseGuard:
                    AddUnreachableFalseGuardWarning(unreachable.Span, unreachable.BranchIndex);
                    break;

                case PatternUnreachableKind.CoveredByPreviousFiniteCases:
                    EmitCoveredPatternWarning(unreachable, context);
                    break;

                case PatternUnreachableKind.EmptyFiniteCaseSet:
                    AddUnreachableUnsatisfiablePatternWarning(unreachable.Span, unreachable.BranchIndex);
                    break;

                default:
                    AddUnreachablePatternWarning(
                        unreachable.Span,
                        unreachable.BranchIndex,
                        unreachable.PreviousIrrefutableBranchIndex);
                    break;
            }
        }
    }

    private void EmitCoveredPatternWarning(
        PatternUnreachableBranch unreachable,
        PatternCoverageContext context)
    {
        if (ShouldSuppressConservativeListCoveredWarning(
                unreachable,
                context.BranchFactsByIndex,
                out var listSuppressionReasons))
        {
            context.HandledCoveredBranches.Add(unreachable.BranchIndex);
            context.SuppressedCoveredWarnings.Add(
                CreateSuppressedCoveredWarningTrace(
                    unreachable,
                    context.BranchFactsByIndex,
                    SuppressedCoveredWarningKind.List,
                    listSuppressionReasons));
            return;
        }

        if (ShouldSuppressConservativeAdtCoveredWarning(
                unreachable,
                context.BranchFactsByIndex,
                _symbolTable,
                out var additionalSuppressionReasons))
        {
            context.SuppressedCoveredWarnings.Add(
                CreateSuppressedCoveredWarningTrace(
                    unreachable,
                    context.BranchFactsByIndex,
                    SuppressedCoveredWarningKind.Adt,
                    additionalSuppressionReasons));
            return;
        }

        if (ShouldSuppressNonGuardedAdtConstructorRefinementCoveredWarning(
                unreachable,
                context.BranchFactsByIndex,
                _symbolTable,
                out var refinementSuppressionReasons))
        {
            context.SuppressedCoveredWarnings.Add(
                CreateSuppressedCoveredWarningTrace(
                    unreachable,
                    context.BranchFactsByIndex,
                    SuppressedCoveredWarningKind.Adt,
                    refinementSuppressionReasons));
            return;
        }

        AddUnreachableCoveredPatternWarning(
            unreachable.Span,
            unreachable.BranchIndex,
            unreachable.CoveringBranchIndices,
            unreachable.CoveringWitnesses,
            unreachable.CoveringWitnessTraces);
        context.HandledCoveredBranches.Add(unreachable.BranchIndex);
    }

    private void EmitAdditionalPatternCoveredWarnings(
        PatternCoverageFacts facts,
        PatternCoverageContext context)
    {
        EmitAdditionalDeterministicListCoveredWarnings(
            facts.BranchFacts,
            context.BranchFactsByIndex,
            facts.BranchGuardsByIndex,
            context.HandledCoveredBranches);

        EmitAdditionalDeterministicBoolCoveredWarnings(
            facts.BranchFacts,
            context.BranchFactsByIndex,
            facts.BranchGuardsByIndex,
            context.HandledCoveredBranches);

        EmitAdditionalDeterministicTupleBoolCoveredWarnings(
            facts.BranchFacts,
            context.BranchFactsByIndex,
            facts.BranchGuardsByIndex,
            context.HandledCoveredBranches);

        EmitAdditionalDeterministicAdtCoveredWarnings(
            facts.BranchFacts,
            context.BranchFactsByIndex,
            facts.BranchGuardsByIndex,
            context.HandledCoveredBranches);
    }

    private void EmitNonExhaustivePatternCoverageWarning(
        SourceSpan ownerSpan,
        string ownerDescription,
        PatternCoverageContext context)
    {
        if (context.Summary.IsExhaustive)
        {
            return;
        }

        if (context.RequiresHiddenCaseWildcard)
        {
            AddPatternCoverageWarning(
                ownerSpan,
                DiagnosticMessages.NonExhaustivePatternMatchingAddWildcardBranch(ownerDescription),
                context.HasGuardedBranchesForCoverage,
                [new PatternWitness(PatternWitnessKind.Wildcard, "_", "wildcard:hidden-closed-case")],
                context.UnresolvedGuardBranchIndices,
                context.UnresolvedGuardBranchHints,
                context.SuppressedCoveredWarnings);
            return;
        }

        AddPatternCoverageWarning(
            ownerSpan,
            GetNonExhaustivePatternCoverageMessage(ownerDescription, context.Summary),
            context.HasGuardedBranchesForCoverage,
            context.Summary.MissingWitnesses,
            context.UnresolvedGuardBranchIndices,
            context.UnresolvedGuardBranchHints,
            context.SuppressedCoveredWarnings);
    }

    private bool RequiresHiddenCaseWildcard(SymbolId preferredAdt)
    {
        if (!preferredAdt.IsValid ||
            _symbolTable.GetSymbol<AdtSymbol>(preferredAdt) is not { } preferred ||
            preferred.DirectCases.Count == 0)
        {
            return false;
        }

        var rootId = _symbolTable.GetClosedCaseRoot(preferredAdt);
        var isInsideOwningModule = _symbolTable.Modules.GetOwningModuleIds(rootId).Contains(_currentModule);
        if (isInsideOwningModule)
        {
            return false;
        }

        return HasHiddenLeaf(preferredAdt);

        bool HasHiddenLeaf(SymbolId ownerId)
        {
            if (_symbolTable.GetSymbol<AdtSymbol>(ownerId) is not { } owner)
            {
                return false;
            }

            foreach (var caseId in owner.DirectCases)
            {
                if (_symbolTable.GetSymbol<AdtSymbol>(caseId) is not { } caseType)
                {
                    continue;
                }

                if (caseType.DirectCases.Count == 0)
                {
                    if (!caseType.IsPublic)
                    {
                        return true;
                    }
                }
                else if (HasHiddenLeaf(caseId))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static string GetNonExhaustivePatternCoverageMessage(
        string ownerDescription,
        PatternUsefulnessSummary summary)
    {
        var missingCases = string.Join(", ", summary.MissingCases);
        return summary.CoverageTarget switch
        {
            PatternCoverageTargetKind.Bool =>
                DiagnosticMessages.NonExhaustivePatternMatchingMissingBoolCases(ownerDescription, missingCases),
            PatternCoverageTargetKind.TupleBool =>
                DiagnosticMessages.NonExhaustivePatternMatchingMissingTupleBoolCases(ownerDescription, missingCases),
            PatternCoverageTargetKind.TupleAdt =>
                DiagnosticMessages.NonExhaustivePatternMatchingMissingTupleAdtCases(ownerDescription, missingCases),
            PatternCoverageTargetKind.List =>
                DiagnosticMessages.NonExhaustivePatternMatchingMissingListCases(ownerDescription, missingCases),
            PatternCoverageTargetKind.Adt =>
                DiagnosticMessages.NonExhaustivePatternMatchingMissingAdtConstructors(ownerDescription, missingCases),
            _ =>
                DiagnosticMessages.NonExhaustivePatternMatchingAddWildcardBranch(ownerDescription)
        };
    }

    private void AddPatternCoverageWarning(
        SourceSpan span,
        string message,
        bool hasGuardedBranches,
        IReadOnlyList<PatternWitness>? missingWitnesses = null,
        IReadOnlyList<int>? unresolvedGuardBranchIndices = null,
        IReadOnlyList<string>? unresolvedGuardBranchHints = null,
        IReadOnlyList<SuppressedCoveredWarningTrace>? suppressedCoveredWarnings = null)
    {
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Warning, message, NonExhaustivePatternWarningCode);
        diag.WithLabel(span, DiagnosticMessages.PatternCoverageLabel);
        foreach (var note in PatternCoverageDiagnosticFormatter.BuildUnresolvedGuardNotes(
                     hasGuardedBranches,
                     unresolvedGuardBranchIndices,
                     unresolvedGuardBranchHints))
        {
            diag.WithNote(note);
        }

        if (suppressedCoveredWarnings is { Count: > 0 })
        {
            foreach (var note in PatternCoverageDiagnosticFormatter.BuildSuppressedCoveredWarningNotes(suppressedCoveredWarnings))
            {
                diag.WithNote(note);
            }
        }

        if (missingWitnesses is { Count: > 0 })
        {
            foreach (var note in PatternCoverageDiagnosticFormatter.BuildMissingWitnessNotes(missingWitnesses))
            {
                diag.WithNote(note);
            }
        }

        _diagnostics.Add(diag);
    }

    private void AddUnreachableCoveredPatternWarning(
        SourceSpan span,
        int branchIndex,
        IReadOnlyList<int>? coveringBranchIndices,
        IReadOnlyList<PatternWitness>? coveringWitnesses,
        IReadOnlyList<PatternWitnessTrace>? coveringWitnessTraces)
    {
        var branchList = coveringBranchIndices is { Count: > 0 }
            ? string.Join(", ", coveringBranchIndices.Select(index => $"#{index}"))
            : "earlier branches";
        var message = DiagnosticMessages.UnreachablePatternBranchCoveredByPrevious(branchIndex, branchList);
        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Warning, message, UnreachablePatternWarningCode);
        diag.WithLabel(span, DiagnosticMessages.UnreachablePatternBranchLabel);
        foreach (var note in PatternCoverageDiagnosticFormatter.BuildCoveredCaseNotes(
                     coveringWitnesses,
                     coveringWitnessTraces))
        {
            diag.WithNote(note);
        }

        diag.WithNote(DiagnosticMessages.UnreachablePatternMoveEarlierOrRefineNote);
        _diagnostics.Add(diag);
    }
}
