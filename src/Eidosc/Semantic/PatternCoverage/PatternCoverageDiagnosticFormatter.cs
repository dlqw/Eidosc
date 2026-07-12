using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Utils;

namespace Eidosc.Semantic.PatternCoverage;

internal static class PatternCoverageDiagnosticFormatter
{
    public static string FormatUnresolvedGuardBranchHint(
        int branchIndex,
        SourceSpan span,
        IReadOnlyList<string> lowerBoundCases,
        IReadOnlyList<string> reasonTags)
    {
        var line = span.Location.Line + 1;
        var column = span.Location.Column + 1;
        var lowerBoundText = lowerBoundCases.Count > 0
            ? string.Join(", ", lowerBoundCases)
            : "?";
        var reasonText = reasonTags.Count > 0
            ? $" {{reason={string.Join(",", reasonTags)}}}"
            : string.Empty;
        return $"#{branchIndex}@{line}:{column} [{lowerBoundText}]{reasonText}";
    }

    public static IReadOnlyList<string> BuildUnresolvedGuardNotes(
        bool hasGuardedBranches,
        IReadOnlyList<int>? unresolvedGuardBranchIndices,
        IReadOnlyList<string>? unresolvedGuardBranchHints)
    {
        if (!hasGuardedBranches)
        {
            return [];
        }

        var notes = new List<string>
        {
            DiagnosticMessages.GuardedBranchesNotExhaustiveNote
        };

        if (unresolvedGuardBranchIndices is { Count: > 0 })
        {
            var branches = string.Join(", ", unresolvedGuardBranchIndices.Select(index => $"#{index}"));
            notes.Add(DiagnosticMessages.GuardedBranchesExcludedFromExactCoverage(branches));

            if (unresolvedGuardBranchHints is { Count: > 0 })
            {
                notes.Add(DiagnosticMessages.UnresolvedGuardBranchHints(string.Join("; ", unresolvedGuardBranchHints)));
            }
        }

        return notes;
    }

    public static IReadOnlyList<string> BuildCoveredCaseNotes(
        IReadOnlyList<PatternWitness>? witnesses,
        IReadOnlyList<PatternWitnessTrace>? traces)
    {
        var notes = new List<string>();
        if (witnesses is { Count: > 0 })
        {
            notes.Add(DiagnosticMessages.CoveredCaseWitnesses(
                string.Join(", ", witnesses.Select(witness => witness.DisplayText))));
        }

        if (traces is { Count: > 0 })
        {
            var traceText = string.Join(
                "; ",
                traces.Select(trace => $"{trace.Witness.DisplayText} <- #{trace.CoveringBranchIndex}"));
            notes.Add(DiagnosticMessages.CoveredCaseTraces(traceText));

            var lowerBoundTraceText = string.Join(
                "; ",
                traces
                    .Where(trace => trace.Provenance == PatternWitnessTraceProvenance.LowerBound)
                    .Select(trace => $"{trace.Witness.DisplayText} <- #{trace.CoveringBranchIndex}"));
            if (!string.IsNullOrWhiteSpace(lowerBoundTraceText))
            {
                notes.Add(DiagnosticMessages.CoveredCaseLowerBoundTraces(lowerBoundTraceText));
            }
        }

        return notes;
    }

    public static IReadOnlyList<string> BuildSuppressedCoveredWarningNotes(
        IReadOnlyList<NameResolver.SuppressedCoveredWarningTrace> traces)
    {
        if (traces.Count == 0)
        {
            return [];
        }

        var notes = new List<string>();
        var groupedByKind = traces
            .GroupBy(trace => trace.Kind)
            .OrderBy(group => group.Key);
        foreach (var group in groupedByKind)
        {
            var renderedTraces = string.Join("; ", group.Select(FormatSuppressedCoveredWarningTrace));
            var reasonScope = GetSuppressedCoveredWarningScopeReason(group.Key);
            notes.Add(DiagnosticMessages.ConservativelySuppressedCoveredWarnings(renderedTraces, reasonScope));
        }

        var traceKv = FormatSuppressedCoveredWarningTraceKv(traces);
        if (!string.IsNullOrWhiteSpace(traceKv))
        {
            notes.Add(DiagnosticMessages.SuppressedCoveredTraceKv(traceKv));
        }

        return notes;
    }

    public static IReadOnlyList<string> BuildMissingWitnessNotes(IReadOnlyList<PatternWitness> witnesses)
    {
        if (witnesses.Count == 0)
        {
            return [];
        }

        var notes = new List<string>
        {
            DiagnosticMessages.MissingCaseWitnesses(string.Join(", ", witnesses.Select(witness => witness.DisplayText)))
        };

        var witnessTraces = string.Join("; ", witnesses.Select(FormatMissingWitnessTrace));
        notes.Add(DiagnosticMessages.MissingCaseTraces(witnessTraces));

        var groupedTraces = FormatMissingWitnessTraceGroups(witnesses);
        if (!string.IsNullOrWhiteSpace(groupedTraces))
        {
            notes.Add(DiagnosticMessages.MissingCaseTraceGroups(groupedTraces));
        }

        var machineTrace = FormatMissingWitnessTraceKv(witnesses);
        if (!string.IsNullOrWhiteSpace(machineTrace))
        {
            notes.Add(DiagnosticMessages.MissingCaseTraceKv(machineTrace));
        }

        return notes;
    }

    private static string FormatMissingWitnessTrace(PatternWitness witness)
    {
        var key = GetMissingWitnessStableKey(witness);
        return $"{witness.DisplayText} [{key}]";
    }

    private static string FormatMissingWitnessTraceGroups(IReadOnlyList<PatternWitness> witnesses)
    {
        var groups = new List<string>();
        AppendMissingWitnessGroup(groups, witnesses, PatternWitnessKind.BoolLiteral, "bool");
        AppendMissingWitnessGroup(groups, witnesses, PatternWitnessKind.TupleBool, "tuple-bool");
        AppendMissingWitnessGroup(groups, witnesses, PatternWitnessKind.TupleConstructor, "tuple-adt");
        AppendMissingWitnessGroup(groups, witnesses, PatternWitnessKind.ListShape, "list");
        AppendMissingWitnessGroup(groups, witnesses, PatternWitnessKind.Constructor, "ctor");
        AppendMissingWitnessGroup(groups, witnesses, PatternWitnessKind.Wildcard, "wildcard");
        return string.Join(" | ", groups);
    }

    private static void AppendMissingWitnessGroup(
        ICollection<string> groups,
        IEnumerable<PatternWitness> witnesses,
        PatternWitnessKind kind,
        string label)
    {
        var entries = witnesses
            .Where(witness => witness.Kind == kind)
            .GroupBy(GetMissingWitnessStableKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => FormatMissingWitnessGroupEntry(group.First()))
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        groups.Add($"{label}={string.Join(", ", entries)}");
    }

    private static string GetMissingWitnessStableKey(PatternWitness witness)
    {
        return string.IsNullOrWhiteSpace(witness.StableKey)
            ? "?"
            : witness.StableKey;
    }

    private static string FormatMissingWitnessGroupEntry(PatternWitness witness)
    {
        var key = GetMissingWitnessStableKey(witness);
        if (key == "?")
        {
            return $"? ({witness.DisplayText})";
        }

        var separatorIndex = key.IndexOf(':');
        if (separatorIndex >= 0 && separatorIndex + 1 < key.Length)
        {
            var suffix = key[(separatorIndex + 1)..];
            if (string.Equals(suffix, witness.DisplayText, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return $"{key} ({witness.DisplayText})";
    }

    private static string FormatMissingWitnessTraceKv(IReadOnlyList<PatternWitness> witnesses)
    {
        var entries = witnesses
            .Select(witness => (witness, key: GetMissingWitnessStableKey(witness)))
            .OrderBy(item => item.key, StringComparer.Ordinal)
            .ThenBy(item => item.witness.DisplayText, StringComparer.Ordinal)
            .Select(item =>
                $"kind={GetMissingWitnessKindKey(item.witness)};key={EscapeMissingWitnessKvValue(item.key)};display={EscapeMissingWitnessKvValue(item.witness.DisplayText)}")
            .ToList();
        return string.Join(" || ", entries);
    }

    private static string GetMissingWitnessKindKey(PatternWitness witness)
    {
        return witness.Kind switch
        {
            PatternWitnessKind.BoolLiteral => "bool",
            PatternWitnessKind.TupleBool => "tuple-bool",
            PatternWitnessKind.TupleConstructor => "tuple-adt",
            PatternWitnessKind.ListShape => "list",
            PatternWitnessKind.Constructor => "ctor",
            PatternWitnessKind.Wildcard => "wildcard",
            _ => "unknown"
        };
    }

    private static string EscapeMissingWitnessKvValue(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(WellKnownStrings.Punctuation.Semicolon, "\\;", StringComparison.Ordinal)
            .Replace(WellKnownStrings.Punctuation.Pipe, "\\|", StringComparison.Ordinal);
    }

    private static string FormatSuppressedCoveredWarningTrace(NameResolver.SuppressedCoveredWarningTrace trace)
    {
        var covering = trace.CoveringBranchIndices.Count > 0
            ? string.Join(", ", trace.CoveringBranchIndices.Select(index => $"#{index}"))
            : "earlier branches";
        var reasonText = trace.Reasons.Count > 0
            ? $" {{reason={string.Join(",", trace.Reasons)}}}"
            : string.Empty;
        return $"#{trace.BranchIndex} <- {covering}{reasonText}";
    }

    private static string GetSuppressedCoveredWarningScopeReason(NameResolver.SuppressedCoveredWarningKind kind)
    {
        return kind switch
        {
            NameResolver.SuppressedCoveredWarningKind.Adt => "adt-guarded-refutable-view",
            NameResolver.SuppressedCoveredWarningKind.List => "list-guarded-uncertain-view",
            _ => "guarded-conservative-covered-suppression"
        };
    }

    private static string FormatSuppressedCoveredWarningTraceKv(
        IReadOnlyList<NameResolver.SuppressedCoveredWarningTrace> traces)
    {
        var entries = traces
            .OrderBy(trace => trace.Kind)
            .ThenBy(trace => trace.BranchIndex)
            .Select(trace =>
            {
                var covering = trace.CoveringBranchIndices.Count > 0
                    ? string.Join(",", trace.CoveringBranchIndices)
                    : string.Empty;
                var reasons = trace.Reasons.Count > 0
                    ? string.Join(",", trace.Reasons.Select(EscapeSuppressedCoveredWarningKvReasonToken))
                    : string.Empty;
                return
                    $"kind={GetSuppressedCoveredWarningKindKey(trace.Kind)};branch={trace.BranchIndex};covering={EscapeSuppressedCoveredWarningKvValue(covering)};reason={reasons}";
            })
            .ToList();
        return string.Join(" || ", entries);
    }

    private static string GetSuppressedCoveredWarningKindKey(NameResolver.SuppressedCoveredWarningKind kind)
    {
        return kind switch
        {
            NameResolver.SuppressedCoveredWarningKind.Adt => "adt",
            NameResolver.SuppressedCoveredWarningKind.List => "list",
            _ => "unknown"
        };
    }

    private static string EscapeSuppressedCoveredWarningKvValue(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(WellKnownStrings.Punctuation.Semicolon, "\\;", StringComparison.Ordinal)
            .Replace(WellKnownStrings.Punctuation.Pipe, "\\|", StringComparison.Ordinal);
    }

    private static string EscapeSuppressedCoveredWarningKvReasonToken(string value)
    {
        return EscapeSuppressedCoveredWarningKvValue(value)
            .Replace(",", "\\,", StringComparison.Ordinal);
    }
}
