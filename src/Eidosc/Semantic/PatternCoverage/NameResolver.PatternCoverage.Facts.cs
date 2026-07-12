using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Patterns;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private PatternCoverageFacts BuildPatternCoverageFacts(
        IReadOnlyList<PatternBranch> branches,
        string? guardSubjectName)
    {
        var hasBoolLiteralPattern = branches.Any(branch => PatternContainsBoolLiteral(branch.Pattern));
        var branchFacts = new List<PatternUsefulnessBranchFact>(branches.Count);
        var branchGuardsByIndex = new Dictionary<int, EidosAstNode>(branches.Count);

        for (var i = 0; i < branches.Count; i++)
        {
            if (CreatePatternCoverageFact(branches[i], i + 1, guardSubjectName, hasBoolLiteralPattern, branchGuardsByIndex) is { } fact)
            {
                branchFacts.Add(fact);
            }
        }

        return new PatternCoverageFacts(branchFacts, branchGuardsByIndex, hasBoolLiteralPattern);
    }

    private PatternUsefulnessBranchFact? CreatePatternCoverageFact(
        PatternBranch branch,
        int branchIndex,
        string? guardSubjectName,
        bool allowGuardSubjectInference,
        IDictionary<int, EidosAstNode> branchGuardsByIndex)
    {
        if (branch.Pattern == null)
        {
            return null;
        }

        var isGuarded = branch.Guard != null;
        bool? guardConstant = null;
        var branchBoolCoverage = new HashSet<bool>();
        var hasExactBoolCoverage = false;
        var branchTupleBoolCoverage = new HashSet<string>(StringComparer.Ordinal);
        var hasExactTupleBoolCoverage = false;
        var tupleBoolArity = 0;
        var branchListCoverage = new HashSet<ListCoverageCase>();
        var hasExactListCoverage = false;
        var branchAdtCoverageConstructors = new HashSet<SymbolId>();
        var hasExactAdtCoverage = false;
        var branchAdtCoverageAdt = SymbolId.None;

        if (isGuarded)
        {
            branchGuardsByIndex[branchIndex] = branch.Guard!;
            if (TryEvaluateGuardBooleanConstantForPattern(
                    branch.Pattern,
                    branch.Guard,
                    guardSubjectName,
                    out var guardValue) ||
                TryEvaluateGuardBooleanConstant(branch.Guard, out guardValue))
            {
                guardConstant = guardValue;
            }
            else
            {
                TryCollectBoolCoverageFromGuardedPattern(
                    branch.Pattern,
                    branch.Guard,
                    branchBoolCoverage,
                    guardSubjectName,
                    allowGuardSubjectInference);
                hasExactBoolCoverage = branchBoolCoverage.Count > 0;

                hasExactTupleBoolCoverage = TryCollectTupleBoolCoverageFromGuardedPattern(
                    branch.Pattern,
                    branch.Guard,
                    branchTupleBoolCoverage,
                    out tupleBoolArity);

                hasExactAdtCoverage = TryCollectAdtCoverageFromGuardedPattern(
                    branch.Pattern,
                    branch.Guard,
                    branchAdtCoverageConstructors,
                    out branchAdtCoverageAdt);

                hasExactListCoverage = TryCollectListCoverageFromGuardedPattern(
                    branch.Pattern,
                    branch.Guard,
                    branchListCoverage);
            }
        }

        if (!isGuarded || guardConstant == true)
        {
            hasExactBoolCoverage = TryGetExactBoolPatternCases(branch.Pattern, branchBoolCoverage);
            hasExactTupleBoolCoverage = TryGetExactTupleBoolPatternCases(
                branch.Pattern,
                out tupleBoolArity,
                branchTupleBoolCoverage);

            hasExactListCoverage = PatternUsefulnessAnalyzer.TryGetExactListCoverageCases(
                branch.Pattern,
                out var exactListCases);
            if (hasExactListCoverage)
            {
                branchListCoverage.UnionWith(exactListCases);
            }
        }

        return new PatternUsefulnessBranchFact(
            branchIndex,
            branch.Span,
            branch.Guard?.Span ?? SourceSpan.Empty,
            branch.Pattern,
            IsPatternIrrefutable(branch.Pattern),
            isGuarded,
            guardConstant,
            branchBoolCoverage,
            hasExactBoolCoverage,
            branchTupleBoolCoverage,
            hasExactTupleBoolCoverage,
            tupleBoolArity,
            branchListCoverage,
            hasExactListCoverage,
            branchAdtCoverageConstructors,
            hasExactAdtCoverage,
            branchAdtCoverageAdt);
    }
}
