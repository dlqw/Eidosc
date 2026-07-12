using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static bool TryPatternDeterministicallyCoversTargetProfile(
        Pattern pattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor,
        CtorDeterministicIntConstraintProfile targetProfile,
        out DeterministicCoverageFailureReason failureReason)
    {
        failureReason = DeterministicCoverageFailureReason.None;
        switch (pattern)
        {
            case CtorPattern ctorPattern when ctorPattern.SymbolId.IsValid:
                if (PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                        ctorPattern,
                        symbolTable,
                        out var ctorAdt,
                        out var ctorIds) &&
                    ctorAdt == targetAdt &&
                    ctorIds.Contains(targetConstructor))
                {
                    return TryCtorPatternDeterministicallyCoversTargetProfile(
                        ctorPattern,
                        targetProfile);
                }

                return false;

            case AsPattern { InnerPattern: not null } asPattern:
                return TryPatternDeterministicallyCoversTargetProfile(
                    asPattern.InnerPattern,
                    symbolTable,
                    targetAdt,
                    targetConstructor,
                    targetProfile,
                    out failureReason);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                if (!TryEnumerateDeterministicConstraintAssignments(
                        targetProfile,
                        out var targetAssignments,
                        out var assignmentOverflow))
                {
                    if (assignmentOverflow)
                    {
                        failureReason = DeterministicCoverageFailureReason.AssignmentOverflow;
                    }

                    return false;
                }

                var relevantAlternatives = new List<Pattern>();
                for (var i = 0; i < orPattern.Alternatives.Count; i++)
                {
                    if (!CouldMatchTargetConstructor(
                            orPattern.Alternatives[i],
                            symbolTable,
                            targetAdt,
                            targetConstructor))
                    {
                        continue;
                    }

                    relevantAlternatives.Add(orPattern.Alternatives[i]);
                }

                if (relevantAlternatives.Count == 0)
                {
                    return false;
                }

                for (var assignmentIndex = 0; assignmentIndex < targetAssignments.Count; assignmentIndex++)
                {
                    var assignmentProfile = targetAssignments[assignmentIndex];
                    var assignmentCovered = false;
                    var assignmentFailureReason = DeterministicCoverageFailureReason.None;

                    for (var alternativeIndex = 0;
                         alternativeIndex < relevantAlternatives.Count;
                         alternativeIndex++)
                    {
                        if (!TryPatternDeterministicallyCoversTargetProfile(
                                relevantAlternatives[alternativeIndex],
                                symbolTable,
                                targetAdt,
                                targetConstructor,
                                assignmentProfile,
                                out var alternativeFailureReason))
                        {
                            if (alternativeFailureReason == DeterministicCoverageFailureReason.AssignmentOverflow)
                            {
                                assignmentFailureReason = DeterministicCoverageFailureReason.AssignmentOverflow;
                            }

                            continue;
                        }

                        assignmentCovered = true;
                        break;
                    }

                    if (!assignmentCovered)
                    {
                        failureReason = assignmentFailureReason;
                        return false;
                    }
                }

                return true;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                var sawRelevantConjunct = false;
                for (var i = 0; i < andPattern.Conjuncts.Count; i++)
                {
                    if (!CouldMatchTargetConstructor(
                            andPattern.Conjuncts[i],
                            symbolTable,
                            targetAdt,
                            targetConstructor))
                    {
                        return false;
                    }

                    sawRelevantConjunct = true;
                    if (!TryPatternDeterministicallyCoversTargetProfile(
                            andPattern.Conjuncts[i],
                            symbolTable,
                            targetAdt,
                            targetConstructor,
                            targetProfile,
                            out var conjunctFailureReason))
                    {
                        failureReason = conjunctFailureReason;
                        return false;
                    }
                }

                return sawRelevantConjunct;

            case WildcardPattern:
            case VarPattern:
                return true;

            case NotPattern { InnerPattern: not null } notPattern:
                if (PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                        notPattern.InnerPattern,
                        symbolTable,
                        out var excludedAdt,
                        out var excludedConstructors) &&
                    excludedAdt == targetAdt)
                {
                    return !excludedConstructors.Contains(targetConstructor);
                }

                return false;

            default:
                return false;
        }
    }

    private static bool TryCtorPatternDeterministicallyCoversTargetProfile(
        CtorPattern coveringPattern,
        CtorDeterministicIntConstraintProfile targetProfile)
    {
        foreach (var (position, values) in targetProfile.PositionalConstraints)
        {
            if (position < 0 ||
                position >= coveringPattern.PositionalPatterns.Count ||
                !TryMatchAllIntValuesViaDeterministicNonViewPath(
                    coveringPattern.PositionalPatterns[position],
                    values))
            {
                return false;
            }
        }

        foreach (var (position, values) in targetProfile.PositionalBoolConstraints)
        {
            if (position < 0 ||
                position >= coveringPattern.PositionalPatterns.Count ||
                !TryMatchAllBoolValuesViaDeterministicNonViewPath(
                    coveringPattern.PositionalPatterns[position],
                    values))
            {
                return false;
            }
        }

        for (var i = 0; i < coveringPattern.PositionalPatterns.Count; i++)
        {
            if (targetProfile.PositionalConstraints.ContainsKey(i))
            {
                continue;
            }

            if (targetProfile.PositionalBoolConstraints.ContainsKey(i))
            {
                continue;
            }

            if (!IsPatternDominatedByIrrefutableNonViewFallback(coveringPattern.PositionalPatterns[i]))
            {
                return false;
            }
        }

        var coveringNamedPatterns = new Dictionary<string, Pattern?>(StringComparer.Ordinal);
        for (var i = 0; i < coveringPattern.NamedPatterns.Count; i++)
        {
            var namedPattern = coveringPattern.NamedPatterns[i];
            if (string.IsNullOrWhiteSpace(namedPattern.FieldName))
            {
                continue;
            }

            // Duplicate named fields are rejected by semantic checks.
            coveringNamedPatterns[namedPattern.FieldName] = namedPattern.Pattern;
        }

        foreach (var (fieldName, values) in targetProfile.NamedConstraints)
        {
            if (!coveringNamedPatterns.TryGetValue(fieldName, out var fieldPattern))
            {
                return false;
            }

            // Shorthand named patterns (field-only) are irrefutable non-view.
            if (fieldPattern == null)
            {
                continue;
            }

            if (!TryMatchAllIntValuesViaDeterministicNonViewPath(fieldPattern, values))
            {
                return false;
            }
        }

        foreach (var (fieldName, values) in targetProfile.NamedBoolConstraints)
        {
            if (!coveringNamedPatterns.TryGetValue(fieldName, out var fieldPattern))
            {
                return false;
            }

            // Shorthand named patterns (field-only) are irrefutable non-view.
            if (fieldPattern == null)
            {
                continue;
            }

            if (!TryMatchAllBoolValuesViaDeterministicNonViewPath(fieldPattern, values))
            {
                return false;
            }
        }

        foreach (var (fieldName, fieldPattern) in coveringNamedPatterns)
        {
            if (targetProfile.NamedConstraints.ContainsKey(fieldName))
            {
                continue;
            }

            if (targetProfile.NamedBoolConstraints.ContainsKey(fieldName))
            {
                continue;
            }

            if (!IsPatternDominatedByIrrefutableNonViewFallback(fieldPattern))
            {
                return false;
            }
        }

        return true;
    }

}
