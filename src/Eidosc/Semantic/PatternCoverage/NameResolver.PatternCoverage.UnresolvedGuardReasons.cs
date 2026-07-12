using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private IReadOnlyList<string> GetUnresolvedGuardReasonTags(
        PatternUsefulnessBranchFact branch,
        PatternCoverageTargetKind coverageTarget)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var nonFiniteViewPaths = GetNonFiniteRefutableViewPatternPaths(branch.Pattern);
        if (branch.IsGuarded && branch.GuardConstant == null)
        {
            reasons.Add("guard:not-provable");
        }

        AddCoverageTargetUnresolvedGuardReasonTags(reasons, branch, coverageTarget, nonFiniteViewPaths);

        return reasons
            .OrderBy(
                reason => reason.StartsWith("guard:", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(reason => reason, StringComparer.Ordinal)
            .ToList();
    }

    private void AddCoverageTargetUnresolvedGuardReasonTags(
        ISet<string> reasons,
        PatternUsefulnessBranchFact branch,
        PatternCoverageTargetKind coverageTarget,
        IReadOnlyList<string> nonFiniteViewPaths)
    {
        switch (coverageTarget)
        {
            case PatternCoverageTargetKind.Bool:
                AddBoolUnresolvedGuardReasonTags(reasons, branch, nonFiniteViewPaths);
                return;

            case PatternCoverageTargetKind.TupleBool:
                AddTupleBoolUnresolvedGuardReasonTags(reasons, branch, nonFiniteViewPaths);
                return;

            case PatternCoverageTargetKind.List:
                AddListUnresolvedGuardReasonTags(reasons, branch, nonFiniteViewPaths);
                return;

            case PatternCoverageTargetKind.Adt:
                AddAdtUnresolvedGuardReasonTags(reasons, branch, nonFiniteViewPaths);
                return;

            case PatternCoverageTargetKind.None:
            case PatternCoverageTargetKind.Generic:
            default:
                AddGenericUnresolvedGuardReasonTags(reasons, branch);
                return;
        }
    }

    private void AddBoolUnresolvedGuardReasonTags(
        ISet<string> reasons,
        PatternUsefulnessBranchFact branch,
        IReadOnlyList<string> nonFiniteViewPaths)
    {
        if (branch.HasExactBoolCoverage)
        {
            return;
        }

        reasons.Add("bool:pattern-or-guard-nonfinite");
        reasons.Add("bool:target-domain-bool");
        AddViewPathReasons(
            reasons,
            "bool",
            nonFiniteViewPaths,
            intDomainValues: [],
            includeIntDomain: false,
            includeBoolDomain: true,
            branch.Pattern);
    }

    private void AddTupleBoolUnresolvedGuardReasonTags(
        ISet<string> reasons,
        PatternUsefulnessBranchFact branch,
        IReadOnlyList<string> nonFiniteViewPaths)
    {
        if (branch.HasExactTupleBoolCoverage)
        {
            return;
        }

        reasons.Add("tuple-bool:pattern-or-guard-nonfinite");
        if (!TryCollectFiniteBoolDomainCandidatesFromPattern(
                branch.Pattern,
                out var tupleBoolDomainValues) ||
            tupleBoolDomainValues.Count == 0)
        {
            if (nonFiniteViewPaths.Count > 0)
            {
                AddNonFiniteViewPathReasons(reasons, "tuple-bool", nonFiniteViewPaths);
            }

            return;
        }

        reasons.Add("tuple-bool:target-domain-bool");
        AddViewPathReasons(
            reasons,
            "tuple-bool",
            nonFiniteViewPaths,
            intDomainValues: [],
            includeIntDomain: false,
            includeBoolDomain: true,
            branch.Pattern);
    }

    private void AddListUnresolvedGuardReasonTags(
        ISet<string> reasons,
        PatternUsefulnessBranchFact branch,
        IReadOnlyList<string> nonFiniteViewPaths)
    {
        if (branch.HasExactListCoverage)
        {
            return;
        }

        if (!PatternUsefulnessAnalyzer.TryGetListCoverageHintCases(branch.Pattern, out _))
        {
            reasons.Add("list:pattern-nonfinite");
        }

        if (!TryCollectListUnresolvedGuardTargetDomains(
                branch.Pattern,
                out var intDomainValues,
                out var hasBoolDomain))
        {
            if (nonFiniteViewPaths.Count > 0)
            {
                AddNonFiniteViewPathReasons(reasons, "list", nonFiniteViewPaths);
            }

            return;
        }

        if (!hasBoolDomain &&
            TryCollectFiniteBoolDomainCandidatesFromPattern(branch.Pattern, out var boolDomainValues) &&
            boolDomainValues.Count > 0)
        {
            hasBoolDomain = true;
        }

        AddIntAndBoolTargetDomainReasons(reasons, "list", branch.Pattern, intDomainValues, hasBoolDomain);
        AddViewPathReasons(
            reasons,
            "list",
            nonFiniteViewPaths,
            intDomainValues,
            includeIntDomain: intDomainValues.Count > 0,
            includeBoolDomain: hasBoolDomain,
            branch.Pattern);
    }

    private void AddAdtUnresolvedGuardReasonTags(
        ISet<string> reasons,
        PatternUsefulnessBranchFact branch,
        IReadOnlyList<string> nonFiniteViewPaths)
    {
        if (branch.HasExactAdtCoverage)
        {
            return;
        }

        reasons.Add("adt:pattern-or-guard-nonfinite");
        TryCollectAdtUnresolvedGuardTargetDomains(
            branch,
            out var intDomainValues,
            out var hasBoolDomain);
        if (intDomainValues.Count == 0)
        {
            CollectFiniteIntDomainCandidatesFromPattern(branch.Pattern, intDomainValues);
        }

        if (!hasBoolDomain &&
            TryCollectFiniteBoolDomainCandidatesFromPattern(branch.Pattern, out var boolDomainValues) &&
            boolDomainValues.Count > 0)
        {
            hasBoolDomain = true;
        }

        AddIntAndBoolTargetDomainReasons(reasons, "adt", branch.Pattern, intDomainValues, hasBoolDomain);
        AddViewPathReasons(
            reasons,
            "adt",
            nonFiniteViewPaths,
            intDomainValues,
            includeIntDomain: intDomainValues.Count > 0,
            includeBoolDomain: hasBoolDomain,
            branch.Pattern);
    }

    private static void AddGenericUnresolvedGuardReasonTags(
        ISet<string> reasons,
        PatternUsefulnessBranchFact branch)
    {
        if (!branch.HasExactBoolCoverage &&
            !branch.HasExactTupleBoolCoverage &&
            !branch.HasExactListCoverage &&
            !branch.HasExactAdtCoverage)
        {
            reasons.Add("pattern-or-guard-nonfinite");
        }
    }

}
