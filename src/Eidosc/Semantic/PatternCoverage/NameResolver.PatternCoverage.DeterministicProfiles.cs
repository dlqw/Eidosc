using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static bool CouldPatternPotentiallyMatchDeterministicTargetProfile(
        Pattern pattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor,
        CtorDeterministicIntConstraintProfile targetProfile)
    {
        var candidateProfiles = CollectDeterministicTargetConstructorConstraintProfiles(
            pattern,
            symbolTable,
            targetAdt,
            targetConstructor);
        if (candidateProfiles.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < candidateProfiles.Count; i++)
        {
            var candidateProfile = candidateProfiles[i];
            if (!candidateProfile.HasAnyConstraint ||
                TryMergeDeterministicConstraintProfiles(
                    targetProfile,
                    candidateProfile,
                    out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CouldPatternMatchAnyDeterministicTargetAssignment(
        Pattern pattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor,
        CtorDeterministicIntConstraintProfile targetProfile)
    {
        if (!TryEnumerateDeterministicConstraintAssignments(
                targetProfile,
                out var targetAssignments,
                out _) ||
            targetAssignments.Count == 0)
        {
            return true;
        }

        for (var i = 0; i < targetAssignments.Count; i++)
        {
            if (TryPatternDeterministicallyCoversTargetProfile(
                    pattern,
                    symbolTable,
                    targetAdt,
                    targetConstructor,
                    targetAssignments[i],
                    out _))
            {
                return true;
            }
        }

        return false;
    }

}
