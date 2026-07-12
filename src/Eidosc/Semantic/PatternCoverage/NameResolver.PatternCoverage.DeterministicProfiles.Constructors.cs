using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static IReadOnlyList<CtorDeterministicIntConstraintProfile> CollectDeterministicTargetConstructorConstraintProfiles(
        Pattern pattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor)
    {
        var profiles = CollectDeterministicTargetConstructorConstraintProfilesCore(
            pattern,
            symbolTable,
            targetAdt,
            targetConstructor);
        if (profiles.Count == 0)
        {
            return [];
        }

        var uniqueProfiles = new Dictionary<string, CtorDeterministicIntConstraintProfile>(StringComparer.Ordinal);
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            uniqueProfiles[BuildDeterministicConstraintProfileStableKey(profile)] = profile;
        }

        return uniqueProfiles
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToList();
    }

    private static List<CtorDeterministicIntConstraintProfile> CollectDeterministicTargetConstructorConstraintProfilesCore(
        Pattern pattern,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor)
    {
        switch (pattern)
        {
            case CtorPattern ctorPattern when ctorPattern.SymbolId.IsValid:
                if (!PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                        ctorPattern,
                        symbolTable,
                        out var ctorAdt,
                        out var ctorIds) ||
                    ctorAdt != targetAdt ||
                    !ctorIds.Contains(targetConstructor) ||
                    !TryCollectDeterministicConstraintsFromConstructorPattern(
                        ctorPattern,
                        out var positionalIntConstraints,
                        out var namedIntConstraints,
                        out var positionalBoolConstraints,
                        out var namedBoolConstraints))
                {
                    return [];
                }

                return
                [
                    CreateDeterministicConstraintProfile(
                        positionalIntConstraints,
                        namedIntConstraints,
                        positionalBoolConstraints,
                        namedBoolConstraints)
                ];

            case AsPattern { InnerPattern: not null } asPattern:
                return CollectDeterministicTargetConstructorConstraintProfilesCore(
                    asPattern.InnerPattern,
                    symbolTable,
                    targetAdt,
                    targetConstructor);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                return CollectAlternativeDeterministicConstraintProfiles(
                    orPattern.Alternatives,
                    symbolTable,
                    targetAdt,
                    targetConstructor);

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                return CollectConjunctiveDeterministicConstraintProfiles(
                    andPattern.Conjuncts,
                    symbolTable,
                    targetAdt,
                    targetConstructor);

            case WildcardPattern:
            case VarPattern:
                return [CreateEmptyDeterministicConstraintProfile()];

            case NotPattern { InnerPattern: not null } notPattern:
                if (PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                        notPattern.InnerPattern,
                        symbolTable,
                        out _,
                        out var excludedConstructors) &&
                    excludedConstructors.Contains(targetConstructor))
                {
                    return [];
                }

                return [CreateEmptyDeterministicConstraintProfile()];

            default:
                return [];
        }
    }

    private static List<CtorDeterministicIntConstraintProfile> CollectAlternativeDeterministicConstraintProfiles(
        IReadOnlyList<Pattern> alternatives,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor)
    {
        var profiles = new List<CtorDeterministicIntConstraintProfile>();
        for (var i = 0; i < alternatives.Count; i++)
        {
            var alternative = alternatives[i];
            if (!CouldMatchTargetConstructor(
                    alternative,
                    symbolTable,
                    targetAdt,
                    targetConstructor))
            {
                continue;
            }

            profiles.AddRange(
                CollectDeterministicTargetConstructorConstraintProfilesCore(
                    alternative,
                    symbolTable,
                    targetAdt,
                    targetConstructor));
        }

        return profiles;
    }

    private static List<CtorDeterministicIntConstraintProfile> CollectConjunctiveDeterministicConstraintProfiles(
        IReadOnlyList<Pattern> conjuncts,
        SymbolTable symbolTable,
        SymbolId targetAdt,
        SymbolId targetConstructor)
    {
        List<CtorDeterministicIntConstraintProfile> mergedProfiles =
        [
            CreateEmptyDeterministicConstraintProfile()
        ];

        for (var i = 0; i < conjuncts.Count; i++)
        {
            var conjunct = conjuncts[i];
            if (!CouldMatchTargetConstructor(
                    conjunct,
                    symbolTable,
                    targetAdt,
                    targetConstructor))
            {
                return [];
            }

            var conjunctProfiles = CollectDeterministicTargetConstructorConstraintProfilesCore(
                conjunct,
                symbolTable,
                targetAdt,
                targetConstructor);
            mergedProfiles = MergeDeterministicConstraintProfileSets(
                mergedProfiles,
                conjunctProfiles.Count > 0
                    ? conjunctProfiles
                    : [CreateEmptyDeterministicConstraintProfile()]);
            if (mergedProfiles.Count == 0)
            {
                return [];
            }
        }

        return mergedProfiles;
    }

    private static List<CtorDeterministicIntConstraintProfile> MergeDeterministicConstraintProfileSets(
        IReadOnlyList<CtorDeterministicIntConstraintProfile> leftProfiles,
        IReadOnlyList<CtorDeterministicIntConstraintProfile> rightProfiles)
    {
        var mergedProfiles = new List<CtorDeterministicIntConstraintProfile>();
        for (var leftIndex = 0; leftIndex < leftProfiles.Count; leftIndex++)
        {
            for (var rightIndex = 0; rightIndex < rightProfiles.Count; rightIndex++)
            {
                if (!TryMergeDeterministicConstraintProfiles(
                        leftProfiles[leftIndex],
                        rightProfiles[rightIndex],
                        out var mergedProfile))
                {
                    continue;
                }

                mergedProfiles.Add(mergedProfile);
            }
        }

        return mergedProfiles;
    }
}
