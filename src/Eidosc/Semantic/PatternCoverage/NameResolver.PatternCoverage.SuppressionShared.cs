using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static IReadOnlyList<string> FinalizeSuppressionReasonTags(
        IEnumerable<string> reasonTags,
        bool distinct = false)
    {
        var orderedReasons = distinct
            ? reasonTags.Distinct(StringComparer.Ordinal)
            : reasonTags;

        return orderedReasons
            .OrderBy(reason => reason, StringComparer.Ordinal)
            .ToList();
    }

    private static SuppressedCoveredWarningTrace CreateSuppressedCoveredWarningTrace(
        PatternUnreachableBranch unreachable,
        IReadOnlyDictionary<int, PatternUsefulnessBranchFact> branchFactsByIndex,
        SuppressedCoveredWarningKind kind,
        IReadOnlyCollection<string>? additionalReasons = null)
    {
        var coveringBranchIndices = unreachable.CoveringBranchIndices is { Count: > 0 }
            ? unreachable.CoveringBranchIndices
                .Where(index => index > 0)
                .Distinct()
                .OrderBy(index => index)
                .ToList()
            : [];

        var reasonTags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < coveringBranchIndices.Count; i++)
        {
            if (!branchFactsByIndex.TryGetValue(coveringBranchIndices[i], out var coveringBranch))
            {
                continue;
            }

            if (coveringBranch.IsGuarded && coveringBranch.GuardConstant == null)
            {
                reasonTags.Add("guard:not-provable");
            }

            if (HasRefutableViewPattern(coveringBranch.Pattern))
            {
                reasonTags.Add("adt:refutable-view");
            }

            var nonFiniteViewPaths = GetNonFiniteRefutableViewPatternPaths(coveringBranch.Pattern);
            if (nonFiniteViewPaths.Count > 0)
            {
                AddNonFiniteViewPathReasons(reasonTags, "adt", nonFiniteViewPaths);
            }
        }

        if (additionalReasons is { Count: > 0 })
        {
            foreach (var reason in additionalReasons)
            {
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    reasonTags.Add(reason);
                }
            }
        }

        return new SuppressedCoveredWarningTrace(
            kind,
            unreachable.BranchIndex,
            coveringBranchIndices,
            FinalizeSuppressionReasonTags(reasonTags));
    }

    private enum ParsedListTargetCaseKind
    {
        Int,
        Bool
    }

    private readonly record struct ParsedListTargetCase(
        ParsedListTargetCaseKind Kind,
        IReadOnlyList<ListIntCaseToken>? IntTokens = null,
        IReadOnlyList<bool>? BoolValues = null);

    private static bool TryParseListTargetCases(
        IReadOnlyList<ListCoverageCase> targetCases,
        out IReadOnlyList<ParsedListTargetCase> parsedCases)
    {
        var parsed = new List<ParsedListTargetCase>(targetCases.Count);
        for (var i = 0; i < targetCases.Count; i++)
        {
            var targetCase = targetCases[i];
            if (TryParseListCaseBoolVector(targetCase, out var boolValues))
            {
                parsed.Add(new ParsedListTargetCase(
                    Kind: ParsedListTargetCaseKind.Bool,
                    BoolValues: boolValues));
                continue;
            }

            if (TryParseListCaseIntVector(targetCase, out var intTokens))
            {
                parsed.Add(new ParsedListTargetCase(
                    Kind: ParsedListTargetCaseKind.Int,
                    IntTokens: intTokens));
                continue;
            }

            parsedCases = [];
            return false;
        }

        parsedCases = parsed;
        return parsedCases.Count > 0;
    }

    private static string BuildListDeterministicMissCaseReasonKey(ParsedListTargetCase targetCase, bool preferCharLiteralHints)
    {
        return targetCase.Kind switch
        {
            ParsedListTargetCaseKind.Int when targetCase.IntTokens is not null =>
                $"{(preferCharLiteralHints ? "char" : "int")}:{string.Join("~", targetCase.IntTokens.Select(token => FormatListDeterministicMissToken(token, preferCharLiteralHints)))}",
            ParsedListTargetCaseKind.Bool when targetCase.BoolValues is not null =>
                $"bool:{string.Join("~", targetCase.BoolValues.Select(value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False))}",
            _ => "unknown"
        };
    }

    private static string FormatListDeterministicMissToken(ListIntCaseToken token, bool preferCharLiteralHints)
    {
        if (token.IsOtherBucket)
        {
            return WellKnownStrings.Operators.Multiply;
        }

        if (preferCharLiteralHints &&
            token.Value >= char.MinValue &&
            token.Value <= char.MaxValue)
        {
            return FormatCharLiteral((char)token.Value);
        }

        return token.Value.ToString();
    }

    private static HashSet<long> CollectListIntDomainValues(IEnumerable<ParsedListTargetCase> targetCases)
    {
        var domainValues = new HashSet<long>();
        foreach (var targetCase in targetCases)
        {
            if (targetCase.Kind != ParsedListTargetCaseKind.Int ||
                targetCase.IntTokens is null)
            {
                continue;
            }

            for (var i = 0; i < targetCase.IntTokens.Count; i++)
            {
                if (!targetCase.IntTokens[i].IsOtherBucket)
                {
                    domainValues.Add(targetCase.IntTokens[i].Value);
                }
            }
        }

        return domainValues;
    }
}
