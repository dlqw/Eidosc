using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static IReadOnlyList<string> CollectAdtDeterministicNoHitConstructorReasonTags(
        Pattern coveringPattern,
        Pattern? targetPattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        IReadOnlySet<SymbolId> targetConstructors)
    {
        var reasonTags = new List<string>();
        if (targetPattern == null ||
            targetConstructors.Count == 0)
        {
            return reasonTags;
        }

        var preferCharLiteralReasonKeys = HasCharLiteralOrRangePattern(targetPattern);
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
                AddAdtDeterministicNoHitConstructorReasonTag(reasonTags, symbolTable, targetConstructor);
                continue;
            }

            var constructorMissed = false;
            for (var profileIndex = 0; profileIndex < targetProfiles.Count; profileIndex++)
            {
                var targetProfile = targetProfiles[profileIndex];
                var profileCovered = false;
                if (TryEnumerateDeterministicConstraintAssignments(
                        targetProfile,
                        out var targetAssignments,
                        out _) &&
                    targetAssignments.Count > 0)
                {
                    var allAssignmentsCovered = true;
                    for (var assignmentIndex = 0; assignmentIndex < targetAssignments.Count; assignmentIndex++)
                    {
                        if (TryPatternDeterministicallyCoversTargetProfile(
                                coveringPattern,
                                symbolTable,
                                targetAdt,
                                targetConstructor,
                                targetAssignments[assignmentIndex],
                                out _))
                        {
                            continue;
                        }

                        allAssignmentsCovered = false;
                        break;
                    }

                    if (allAssignmentsCovered)
                    {
                        profileCovered = true;
                    }
                }
                else if (TryPatternDeterministicallyCoversTargetProfile(
                             coveringPattern,
                             symbolTable,
                             targetAdt,
                             targetConstructor,
                             targetProfile,
                             out _))
                {
                    profileCovered = true;
                }

                if (profileCovered)
                {
                    continue;
                }

                constructorMissed = true;
                AddAdtDeterministicNoHitConstructorCaseReasonTags(
                    reasonTags,
                    symbolTable,
                    targetConstructor,
                    profileIndex + 1,
                    targetProfile,
                    preferCharLiteralReasonKeys);
            }

            if (constructorMissed)
            {
                AddAdtDeterministicNoHitConstructorReasonTag(reasonTags, symbolTable, targetConstructor);
            }
        }

        return FinalizeSuppressionReasonTags(reasonTags, distinct: true);
    }

    private static IReadOnlyList<string> CollectAdtDeterministicOverflowReasonTags(
        Pattern? targetPattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        IReadOnlySet<SymbolId> targetConstructors,
        bool preferCharLiteralReasonKeys)
    {
        var reasonTags = new List<string>();
        if (targetPattern == null ||
            targetConstructors.Count == 0)
        {
            return reasonTags;
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
                var targetProfile = targetProfiles[profileIndex];
                if (TryEnumerateDeterministicConstraintAssignments(
                        targetProfile,
                        out _,
                        out var overflow) ||
                    !overflow)
                {
                    continue;
                }

                AddAdtDeterministicOverflowConstructorCaseReasonTags(
                    reasonTags,
                    symbolTable,
                    targetConstructor,
                    profileIndex + 1,
                    targetProfile,
                    preferCharLiteralReasonKeys);
            }
        }

        return FinalizeSuppressionReasonTags(reasonTags, distinct: true);
    }

    private static void AddAdtDeterministicOverflowConstructorCaseReasonTags(
        ICollection<string> reasonTags,
        SymbolTable symbolTable,
        SymbolId constructorId,
        int caseIndex,
        CtorDeterministicIntConstraintProfile profile,
        bool preferCharLiteralReasonKeys)
    {
        var reasonKey = BuildDeterministicConstraintProfileReasonKey(
            profile,
            preferCharLiteralReasonKeys);
        reasonTags.Add($"adt:deterministic-assignment-overflow-ctor{constructorId.Value}");
        reasonTags.Add($"adt:deterministic-assignment-overflow-ctor{constructorId.Value}-case{caseIndex}");
        reasonTags.Add(
            $"adt:deterministic-assignment-overflow-ctor{constructorId.Value}-case{caseIndex}-key:{reasonKey}");

        var constructorName = symbolTable.GetSymbol<CtorSymbol>(constructorId)?.Name;
        if (!string.IsNullOrWhiteSpace(constructorName))
        {
            reasonTags.Add($"adt:deterministic-assignment-overflow-ctor-name:{constructorName}");
            reasonTags.Add($"adt:deterministic-assignment-overflow-ctor-name:{constructorName}-case{caseIndex}");
            reasonTags.Add(
                $"adt:deterministic-assignment-overflow-ctor-name:{constructorName}-case{caseIndex}-key:{reasonKey}");
        }
    }

    private static void AddAdtDeterministicNoHitConstructorCaseReasonTags(
        ICollection<string> reasonTags,
        SymbolTable symbolTable,
        SymbolId constructorId,
        int caseIndex,
        CtorDeterministicIntConstraintProfile profile,
        bool preferCharLiteralReasonKeys)
    {
        var reasonKey = BuildDeterministicConstraintProfileReasonKey(
            profile,
            preferCharLiteralReasonKeys);
        reasonTags.Add($"adt:no-deterministic-nonview-hit-ctor{constructorId.Value}-case{caseIndex}");
        reasonTags.Add(
            $"adt:no-deterministic-nonview-hit-ctor{constructorId.Value}-case{caseIndex}-key:{reasonKey}");

        var constructorName = symbolTable.GetSymbol<CtorSymbol>(constructorId)?.Name;
        if (!string.IsNullOrWhiteSpace(constructorName))
        {
            reasonTags.Add($"adt:no-deterministic-nonview-hit-ctor-name:{constructorName}-case{caseIndex}");
            reasonTags.Add(
                $"adt:no-deterministic-nonview-hit-ctor-name:{constructorName}-case{caseIndex}-key:{reasonKey}");
        }
    }

    private static string BuildDeterministicConstraintProfileReasonKey(
        CtorDeterministicIntConstraintProfile profile,
        bool preferCharLiteralHints)
    {
        var positional = BuildDeterministicConstraintReasonKeyPart(
            profile.PositionalConstraints,
            preferCharLiteralHints
                ? static value => FormatDeterministicConstraintReasonIntValue(value, true)
                : static value => FormatDeterministicConstraintReasonIntValue(value, false));
        var named = BuildDeterministicConstraintReasonKeyPart(
            profile.NamedConstraints,
            preferCharLiteralHints
                ? static value => FormatDeterministicConstraintReasonIntValue(value, true)
                : static value => FormatDeterministicConstraintReasonIntValue(value, false),
            StringComparer.Ordinal);
        var positionalBool = BuildDeterministicConstraintReasonKeyPart(
            profile.PositionalBoolConstraints,
            static value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False);
        var namedBool = BuildDeterministicConstraintReasonKeyPart(
            profile.NamedBoolConstraints,
            static value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False,
            StringComparer.Ordinal);
        return $"p[{positional}]|n[{named}]|pb[{positionalBool}]|nb[{namedBool}]";
    }

    private static string BuildDeterministicConstraintReasonKeyPart<TKey, TValue>(
        IReadOnlyDictionary<TKey, IReadOnlyCollection<TValue>> constraints,
        Func<TValue, string> formatValue,
        IComparer<TKey>? keyComparer = null)
        where TKey : notnull
        where TValue : IComparable<TValue>
    {
        return string.Join(
            WellKnownStrings.Punctuation.Semicolon,
            constraints
                .OrderBy(pair => pair.Key, keyComparer ?? Comparer<TKey>.Default)
                .Select(pair =>
                    $"{pair.Key}:{string.Join("~", pair.Value.OrderBy(value => value).Select(formatValue))}"));
    }

    private static string FormatDeterministicConstraintReasonIntValue(long value, bool preferCharLiteralHints)
    {
        if (preferCharLiteralHints &&
            value >= char.MinValue &&
            value <= char.MaxValue)
        {
            return FormatCharLiteral((char)value);
        }

        return value.ToString();
    }

    private static void AddAdtDeterministicNoHitConstructorReasonTag(
        ICollection<string> reasonTags,
        SymbolTable symbolTable,
        SymbolId constructorId)
    {
        reasonTags.Add($"adt:no-deterministic-nonview-hit-ctor{constructorId.Value}");
        var constructorName = symbolTable.GetSymbol<CtorSymbol>(constructorId)?.Name;
        if (!string.IsNullOrWhiteSpace(constructorName))
        {
            reasonTags.Add($"adt:no-deterministic-nonview-hit-ctor-name:{constructorName}");
        }
    }

    private readonly record struct CtorDeterministicIntConstraintProfile(
        IReadOnlyDictionary<int, IReadOnlyCollection<long>> PositionalConstraints,
        IReadOnlyDictionary<string, IReadOnlyCollection<long>> NamedConstraints,
        IReadOnlyDictionary<int, IReadOnlyCollection<bool>> PositionalBoolConstraints,
        IReadOnlyDictionary<string, IReadOnlyCollection<bool>> NamedBoolConstraints)
    {
        public bool HasAnyConstraint =>
            PositionalConstraints.Count > 0 ||
            NamedConstraints.Count > 0 ||
            PositionalBoolConstraints.Count > 0 ||
            NamedBoolConstraints.Count > 0;
    }

    private enum DeterministicCoverageFailureReason
    {
        None,
        AssignmentOverflow
    }
}
