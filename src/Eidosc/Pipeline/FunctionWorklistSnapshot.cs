namespace Eidosc.Pipeline;

public sealed record FunctionWorklistSnapshot(
    string SchemaVersion,
    string Kind,
    IReadOnlyList<FunctionWorklistEntry> Functions)
{
    public static FunctionWorklistSnapshot FromDiff(FunctionFingerprintDiffSnapshot diff)
    {
        var entries = diff.Functions
            .Select(static entry => new FunctionWorklistEntry(
                entry.FunctionKey,
                MapAction(entry.Status),
                entry.Status,
                entry.PreviousBodyHash,
                entry.CurrentBodyHash))
            .OrderBy(static entry => entry.Action)
            .ThenBy(static entry => entry.FunctionKey, StringComparer.Ordinal)
            .ToArray();

        return new FunctionWorklistSnapshot(
            "function-worklist-snapshot-v1",
            diff.Kind,
            entries);
    }

    public int Count(FunctionWorklistAction action) =>
        Functions.Count(entry => entry.Action == action);

    private static FunctionWorklistAction MapAction(FunctionFingerprintDiffStatus status)
    {
        return status switch
        {
            FunctionFingerprintDiffStatus.Unchanged => FunctionWorklistAction.Restore,
            FunctionFingerprintDiffStatus.Removed => FunctionWorklistAction.Remove,
            _ => FunctionWorklistAction.Rebuild
        };
    }
}

public sealed record FunctionWorklistEntry(
    string FunctionKey,
    FunctionWorklistAction Action,
    FunctionFingerprintDiffStatus DiffStatus,
    string PreviousBodyHash,
    string CurrentBodyHash);

public enum FunctionWorklistAction
{
    Restore,
    Rebuild,
    Remove
}
