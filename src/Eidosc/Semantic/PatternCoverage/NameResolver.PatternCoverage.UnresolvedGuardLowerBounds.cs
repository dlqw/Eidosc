using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private IReadOnlyList<string> GetUnresolvedGuardLowerBoundCases(
        PatternUsefulnessBranchFact branch,
        PatternCoverageTargetKind coverageTarget)
    {
        return coverageTarget switch
        {
            PatternCoverageTargetKind.Bool => GetBoolUnresolvedLowerBoundCases(branch),
            PatternCoverageTargetKind.TupleBool => GetTupleBoolUnresolvedLowerBoundCases(branch),
            PatternCoverageTargetKind.List => GetListUnresolvedLowerBoundHints(branch),
            PatternCoverageTargetKind.Adt => GetAdtUnresolvedLowerBoundCases(branch),
            _ => GetGenericUnresolvedLowerBoundCases(branch)
        };
    }

    private static IReadOnlyList<string> GetBoolUnresolvedLowerBoundCases(PatternUsefulnessBranchFact branch)
    {
        var boolCases = new HashSet<bool>(branch.BoolCoverageCases);
        if (boolCases.Count == 0 &&
            TryGetExactBoolPatternCases(branch.Pattern, boolCases))
        {
            // Mirror ADT/tuple-bool unresolved-hint fallback:
            // if guard-proven lower-bound is empty but pattern still
            // determines finite bool candidates, show those candidates
            // instead of a raw `?`.
        }

        return boolCases
            .OrderBy(value => value)
            .Select(value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False)
            .ToList();
    }

    private static IReadOnlyList<string> GetTupleBoolUnresolvedLowerBoundCases(PatternUsefulnessBranchFact branch)
    {
        var tupleCases = new HashSet<string>(branch.TupleBoolCoverageCases, StringComparer.Ordinal);
        if (tupleCases.Count == 0 &&
            TryGetExactTupleBoolPatternCases(branch.Pattern, out var tupleArity, tupleCases) &&
            tupleArity > 0)
        {
            // For unresolved guarded tuple-bool branches, we may still be
            // able to infer pattern-side tuple candidates even when no
            // guard-proven lower-bound case exists.
        }

        return ToOrderedOrdinalStrings(tupleCases);
    }

    private IReadOnlyList<string> GetAdtUnresolvedLowerBoundCases(PatternUsefulnessBranchFact branch)
    {
        var constructorIds = new HashSet<SymbolId>(branch.AdtCoverageConstructors);
        if (constructorIds.Count == 0 &&
            PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                branch.Pattern,
                _symbolTable,
                out _,
                out var hintConstructors))
        {
            constructorIds.UnionWith(hintConstructors);
        }

        return constructorIds
            .OrderBy(id => id.Value)
            .Select(GetConstructorCoverageHint)
            .ToList();
    }

    private IReadOnlyList<string> GetGenericUnresolvedLowerBoundCases(PatternUsefulnessBranchFact branch)
    {
        var genericCases = new List<string>();
        genericCases.AddRange(GetBoolUnresolvedLowerBoundCases(branch));
        genericCases.AddRange(GetTupleBoolUnresolvedLowerBoundCases(branch));
        genericCases.AddRange(GetAdtUnresolvedLowerBoundCases(branch));
        genericCases.AddRange(GetListUnresolvedLowerBoundHints(branch));
        return genericCases;
    }

    private IReadOnlyList<string> GetListUnresolvedLowerBoundHints(PatternUsefulnessBranchFact branch)
    {
        var listHints = new HashSet<string>(StringComparer.Ordinal);
        var preferCharLiteralHints = HasCharLiteralOrRangePattern(branch.Pattern);
        var seedCases = new List<ListCoverageCase>();
        if (branch.ListCoverageCases.Count > 0)
        {
            seedCases.AddRange(branch.ListCoverageCases);
        }
        else if (PatternUsefulnessAnalyzer.TryGetExactListCoverageCases(branch.Pattern, out var patternCases))
        {
            seedCases.AddRange(patternCases);
        }

        foreach (var listCase in seedCases)
        {
            if (TryExpandListShapeCaseToDeterministicBoolHints(branch.Pattern, listCase, out var expandedHints))
            {
                foreach (var expandedHint in expandedHints)
                {
                    listHints.Add(expandedHint);
                }

                continue;
            }

            listHints.Add(FormatListCoverageHintCase(listCase, preferCharLiteralHints));
        }

        if (listHints.Count == 0 &&
            PatternUsefulnessAnalyzer.TryGetListCoverageHintCases(branch.Pattern, out var patternListHints))
        {
            listHints.UnionWith(patternListHints);
        }

        return ToOrderedOrdinalStrings(listHints);
    }

    private static bool TryExpandListShapeCaseToDeterministicBoolHints(
        Pattern pattern,
        ListCoverageCase listCoverageCase,
        out IReadOnlyList<string> hints)
    {
        hints = [];
        if (listCoverageCase.IsAtLeast ||
            !string.IsNullOrWhiteSpace(listCoverageCase.BoolVectorKey) ||
            listCoverageCase.Length <= 0 ||
            listCoverageCase.Length > 6)
        {
            return false;
        }

        var candidates = new List<string>();
        var current = new bool[listCoverageCase.Length];
        EnumerateBoolVectors(
            index: 0,
            current,
            pattern,
            candidates);

        if (candidates.Count == 0)
        {
            return false;
        }

        hints = ToDistinctOrderedOrdinalStrings(candidates);
        return true;
    }

    private static IReadOnlyList<string> ToOrderedOrdinalStrings(IEnumerable<string> values)
    {
        return values
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> ToDistinctOrderedOrdinalStrings(IEnumerable<string> values)
    {
        return values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static void EnumerateBoolVectors(
        int index,
        bool[] current,
        Pattern pattern,
        ICollection<string> output)
    {
        if (index >= current.Length)
        {
            if (TryMatchListPatternWithBoolVectorViaDeterministicNonViewPath(pattern, current))
            {
                output.Add($"[{string.Join(", ", current.Select(value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False))}]");
            }

            return;
        }

        current[index] = false;
        EnumerateBoolVectors(index + 1, current, pattern, output);
        current[index] = true;
        EnumerateBoolVectors(index + 1, current, pattern, output);
    }

    private static string FormatListCoverageHintCase(
        ListCoverageCase listCoverageCase,
        bool preferCharLiteralHints = false)
    {
        if (TryParseListCaseBoolVector(listCoverageCase, out var boolValues))
        {
            return $"[{string.Join(", ", boolValues.Select(value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False))}]";
        }

        if (TryParseListCaseIntVector(listCoverageCase, out var intTokens))
        {
            return $"[{string.Join(", ", intTokens.Select(token => FormatListIntTokenHint(token, preferCharLiteralHints)))}]";
        }

        if (!string.IsNullOrWhiteSpace(listCoverageCase.BoolVectorKey))
        {
            var tokens = listCoverageCase.BoolVectorKey
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return $"[{string.Join(", ", tokens.Select(FormatListCoverageHintToken))}]";
        }

        if (listCoverageCase.Length <= 0)
        {
            return listCoverageCase.IsAtLeast ? "[..]" : "[]";
        }

        var placeholders = Enumerable.Repeat("_", listCoverageCase.Length).ToList();
        if (listCoverageCase.IsAtLeast)
        {
            placeholders.Add(WellKnownStrings.Punctuation.DotDot);
        }

        return $"[{string.Join(", ", placeholders)}]";
    }

    private static string FormatListIntTokenHint(ListIntCaseToken token, bool preferCharLiteralHints)
    {
        if (token.IsOtherBucket)
        {
            return "<other>";
        }

        if (!preferCharLiteralHints ||
            token.Value < char.MinValue ||
            token.Value > char.MaxValue)
        {
            return token.Value.ToString();
        }

        return FormatCharLiteral((char)token.Value);
    }

    private static string FormatCharLiteral(char value)
    {
        var escaped = value switch
        {
            '\'' => "\\'",
            '\\' => "\\\\",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            '\0' => "\\0",
            _ when char.IsControl(value) => $"\\u{(int)value:x4}",
            _ => value.ToString()
        };
        return $"'{escaped}'";
    }

    private static string FormatListCoverageHintToken(string token)
    {
        if (string.Equals(token, "i:*", StringComparison.Ordinal))
        {
            return "<other>";
        }

        if (token.StartsWith("i:", StringComparison.Ordinal) && token.Length > 2)
        {
            return token[2..];
        }

        if (token == "1")
        {
            return WellKnownStrings.AdditionalKeywords.True;
        }

        if (token == "0")
        {
            return WellKnownStrings.AdditionalKeywords.False;
        }

        return token;
    }

    private string GetConstructorCoverageHint(SymbolId ctorId)
    {
        var ctorSymbol = _symbolTable.GetSymbol<CtorSymbol>(ctorId);
        return ctorSymbol?.Name ?? $"ctor#{ctorId.Value}";
    }
}
