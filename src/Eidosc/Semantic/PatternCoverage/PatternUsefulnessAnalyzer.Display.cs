using Eidosc.Symbols;

namespace Eidosc.Semantic.PatternCoverage;

internal static partial class PatternUsefulnessAnalyzer
{
    private static string FormatListLengthShape(int minLength, bool hasRest)
    {
        if (minLength <= 0)
        {
            return hasRest ? "[..]" : "[]";
        }

        var parts = Enumerable.Repeat("_", minLength).ToList();
        if (hasRest)
        {
            parts.Add(WellKnownStrings.Punctuation.DotDot);
        }

        return $"[{string.Join(", ", parts)}]";
    }

    private static string GetListLengthCaseStableKey(ListCoverageCase @case)
    {
        if (!string.IsNullOrWhiteSpace(@case.BoolVectorKey))
        {
            return IsBoolVectorEncoding(@case.BoolVectorKey)
                ? $"list-bool:{@case.BoolVectorKey}"
                : $"list-elem:{@case.BoolVectorKey}";
        }

        return @case.IsAtLeast
            ? $"list-len>=:{@case.Length}"
            : $"list-len:{@case.Length}";
    }

    private static string FormatListLengthCaseDisplay(ListCoverageCase @case)
    {
        return FormatListLengthCaseDisplay(@case, symbolTable: null);
    }

    private static string FormatListLengthCaseDisplay(ListCoverageCase @case, SymbolTable? symbolTable)
    {
        if (!string.IsNullOrWhiteSpace(@case.BoolVectorKey))
        {
            if (IsBoolVectorEncoding(@case.BoolVectorKey))
            {
                var values = DecodeBoolVector(@case.BoolVectorKey);
                return $"[{string.Join(", ", values.Select(value => value ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False))}]";
            }

            var tokens = DecodeTokenVector(@case.BoolVectorKey);
            var body = string.Join(", ", tokens.Select(token => FormatListElementToken(token, symbolTable)));
            if (@case.IsAtLeast)
            {
                return $"[{body}, {WellKnownStrings.Punctuation.DotDot}]";
            }

            return $"[{body}]";
        }

        return FormatListLengthShape(@case.Length, @case.IsAtLeast);
    }

    private static string EncodeBoolVector(IReadOnlyList<bool> values)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(",", values.Select(value => value ? "1" : "0"));
    }

    private static string EncodeTokenVector(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(",", tokens);
    }

    private static IReadOnlyList<string> DecodeTokenVector(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return [];
        }

        return encoded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsBoolVectorEncoding(string encoded)
    {
        var parts = DecodeTokenVector(encoded);
        return parts.Count > 0 && parts.All(part => part is "0" or "1");
    }

    private static string FormatListElementToken(string token, SymbolTable? symbolTable = null)
    {
        if (token.StartsWith("i:", StringComparison.Ordinal))
        {
            return token.Length > 2 && token[2..] != WellKnownStrings.Operators.Multiply
                ? token[2..]
                : "<other>";
        }

        if (TryParseAdtDomainToken(token, out var ctorId, out var fieldCase, out var isOther))
        {
            var ctorName = symbolTable != null
                ? GetConstructorDisplayName(symbolTable, ctorId)
                : $"ctor#{ctorId.Value}";

            if (!isOther && fieldCase is { } scalarCase)
            {
                return $"{ctorName}({scalarCase.DisplayText})";
            }

            return $"{ctorName}(...)";
        }

        return token;
    }

    private static IReadOnlyList<bool> DecodeBoolVector(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return [];
        }

        var parts = encoded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<bool>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            result.Add(parts[i] == "1");
        }

        return result;
    }

    private static IReadOnlyList<PatternWitness> DistinctWitnesses(IEnumerable<PatternWitness> witnesses)
    {
        var unique = new Dictionary<string, PatternWitness>(StringComparer.Ordinal);
        foreach (var witness in witnesses)
        {
            var key = GetWitnessKey(witness);
            if (!unique.ContainsKey(key))
            {
                unique[key] = witness;
            }
        }

        return unique.Values
            .OrderBy(witness => witness.DisplayText, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<PatternWitnessTrace> DistinctWitnessTraces(IEnumerable<PatternWitnessTrace> traces)
    {
        var unique = new Dictionary<string, PatternWitnessTrace>(StringComparer.Ordinal);
        foreach (var trace in traces)
        {
            var key = $"{trace.CoveringBranchIndex}:{trace.Provenance}:{GetWitnessKey(trace.Witness)}";
            if (!unique.ContainsKey(key))
            {
                unique[key] = trace;
            }
        }

        return unique.Values
            .OrderBy(trace => trace.Witness.DisplayText, StringComparer.Ordinal)
            .ThenBy(trace => trace.CoveringBranchIndex)
            .ToList();
    }

    private static string GetWitnessKey(PatternWitness witness)
    {
        return string.IsNullOrWhiteSpace(witness.StableKey)
            ? witness.DisplayText
            : witness.StableKey;
    }
}
