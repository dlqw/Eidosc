using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private IReadOnlyList<string> BuildUnresolvedGuardBranchHints(
        IReadOnlyList<int> unresolvedGuardBranchIndices,
        IReadOnlyList<PatternUsefulnessBranchFact> branchFacts,
        PatternCoverageTargetKind coverageTarget)
    {
        if (unresolvedGuardBranchIndices.Count == 0 || branchFacts.Count == 0)
        {
            return [];
        }

        var branchByIndex = branchFacts
            .GroupBy(branch => branch.BranchIndex)
            .ToDictionary(group => group.Key, group => group.First());
        var hints = new List<string>(unresolvedGuardBranchIndices.Count);
        foreach (var branchIndex in unresolvedGuardBranchIndices)
        {
            if (!branchByIndex.TryGetValue(branchIndex, out var branch))
            {
                continue;
            }

            hints.Add(FormatUnresolvedGuardBranchHint(branchIndex, branch, coverageTarget));
        }

        return hints;
    }

    private string FormatUnresolvedGuardBranchHint(
        int branchIndex,
        PatternUsefulnessBranchFact branch,
        PatternCoverageTargetKind coverageTarget)
    {
        var span = GetUnresolvedGuardHintSpan(branch);
        var explanation = GetUnresolvedGuardExplanation(branch, coverageTarget);
        return PatternCoverageDiagnosticFormatter.FormatUnresolvedGuardBranchHint(
            branchIndex,
            span,
            explanation.LowerBoundCases,
            explanation.ReasonTags);
    }

    private static SourceSpan GetUnresolvedGuardHintSpan(PatternUsefulnessBranchFact branch)
    {
        return branch.GuardSpan.Length > 0
            ? branch.GuardSpan
            : branch.Span;
    }

    private UnresolvedGuardExplanation GetUnresolvedGuardExplanation(
        PatternUsefulnessBranchFact branch,
        PatternCoverageTargetKind coverageTarget)
    {
        return new UnresolvedGuardExplanation(
            GetUnresolvedGuardLowerBoundCases(branch, coverageTarget),
            GetUnresolvedGuardReasonTags(branch, coverageTarget));
    }

    private readonly record struct UnresolvedGuardExplanation(
        IReadOnlyList<string> LowerBoundCases,
        IReadOnlyList<string> ReasonTags);
}
