namespace Eidosc.Pipeline;

public sealed record FunctionFingerprintDiffSnapshot(
    string SchemaVersion,
    string Kind,
    string PreviousModuleFingerprint,
    string CurrentModuleFingerprint,
    IReadOnlyList<FunctionFingerprintDiffEntry> Functions)
{
    public static FunctionFingerprintDiffSnapshot Create(
        string kind,
        string previousModuleFingerprint,
        string currentModuleFingerprint,
        IEnumerable<(string FunctionKey, string BodyHash)> previous,
        IEnumerable<(string FunctionKey, string BodyHash)> current)
    {
        var previousByFunctionKey = GroupByFunctionKey(previous);
        var currentByFunctionKey = GroupByFunctionKey(current);
        var entries = new List<FunctionFingerprintDiffEntry>();

        var functionKeys = previousByFunctionKey.Keys
            .Concat(currentByFunctionKey.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static key => key, StringComparer.Ordinal);
        foreach (var functionKey in functionKeys)
        {
            previousByFunctionKey.TryGetValue(functionKey, out var previousHashes);
            currentByFunctionKey.TryGetValue(functionKey, out var currentHashes);
            AddEntries(entries, functionKey, previousHashes ?? [], currentHashes ?? []);
        }

        return new FunctionFingerprintDiffSnapshot(
            "function-fingerprint-diff-snapshot-v1",
            kind,
            previousModuleFingerprint,
            currentModuleFingerprint,
            entries);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> GroupByFunctionKey(
        IEnumerable<(string FunctionKey, string BodyHash)> fingerprints)
    {
        return fingerprints
            .GroupBy(static fingerprint => fingerprint.FunctionKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<string>)group
                    .Select(static fingerprint => fingerprint.BodyHash)
                    .OrderBy(static hash => hash, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    private static void AddEntries(
        List<FunctionFingerprintDiffEntry> entries,
        string functionKey,
        IReadOnlyList<string> previousHashes,
        IReadOnlyList<string> currentHashes)
    {
        if (previousHashes.Count == 0)
        {
            foreach (var currentHash in currentHashes)
            {
                entries.Add(new FunctionFingerprintDiffEntry(
                    functionKey,
                    FunctionFingerprintDiffStatus.Added,
                    PreviousBodyHash: "",
                    CurrentBodyHash: currentHash));
            }

            return;
        }

        if (currentHashes.Count == 0)
        {
            foreach (var previousHash in previousHashes)
            {
                entries.Add(new FunctionFingerprintDiffEntry(
                    functionKey,
                    FunctionFingerprintDiffStatus.Removed,
                    previousHash,
                    CurrentBodyHash: ""));
            }

            return;
        }

        if (previousHashes.Count == 1 && currentHashes.Count == 1)
        {
            var previousHash = previousHashes[0];
            var currentHash = currentHashes[0];
            entries.Add(new FunctionFingerprintDiffEntry(
                functionKey,
                string.Equals(previousHash, currentHash, StringComparison.Ordinal)
                    ? FunctionFingerprintDiffStatus.Unchanged
                    : FunctionFingerprintDiffStatus.Changed,
                previousHash,
                currentHash));
            return;
        }

        var remainingPrevious = CountByHash(previousHashes);
        var remainingCurrent = CountByHash(currentHashes);
        foreach (var hash in remainingPrevious.Keys
                     .Intersect(remainingCurrent.Keys, StringComparer.Ordinal)
                     .OrderBy(static hash => hash, StringComparer.Ordinal))
        {
            var unchangedCount = Math.Min(remainingPrevious[hash], remainingCurrent[hash]);
            for (var i = 0; i < unchangedCount; i++)
            {
                entries.Add(new FunctionFingerprintDiffEntry(
                    functionKey,
                    FunctionFingerprintDiffStatus.Unchanged,
                    hash,
                    hash));
            }

            remainingPrevious[hash] -= unchangedCount;
            remainingCurrent[hash] -= unchangedCount;
        }

        var changedPrevious = ExpandRemainingHashes(remainingPrevious);
        var changedCurrent = ExpandRemainingHashes(remainingCurrent);
        var changedCount = Math.Min(changedPrevious.Count, changedCurrent.Count);
        for (var i = 0; i < changedCount; i++)
        {
            entries.Add(new FunctionFingerprintDiffEntry(
                functionKey,
                FunctionFingerprintDiffStatus.Changed,
                changedPrevious[i],
                changedCurrent[i]));
        }

        for (var i = changedCount; i < changedCurrent.Count; i++)
        {
            entries.Add(new FunctionFingerprintDiffEntry(
                functionKey,
                FunctionFingerprintDiffStatus.Added,
                PreviousBodyHash: "",
                CurrentBodyHash: changedCurrent[i]));
        }

        for (var i = changedCount; i < changedPrevious.Count; i++)
        {
            entries.Add(new FunctionFingerprintDiffEntry(
                functionKey,
                FunctionFingerprintDiffStatus.Removed,
                changedPrevious[i],
                CurrentBodyHash: ""));
        }
    }

    private static Dictionary<string, int> CountByHash(IReadOnlyList<string> hashes)
    {
        return hashes
            .GroupBy(static hash => hash, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> ExpandRemainingHashes(IReadOnlyDictionary<string, int> counts)
    {
        var hashes = new List<string>();
        foreach (var (hash, count) in counts.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            for (var i = 0; i < count; i++)
            {
                hashes.Add(hash);
            }
        }

        return hashes;
    }

    public int Count(FunctionFingerprintDiffStatus status) =>
        Functions.Count(entry => entry.Status == status);
}

public sealed record FunctionFingerprintDiffEntry(
    string FunctionKey,
    FunctionFingerprintDiffStatus Status,
    string PreviousBodyHash,
    string CurrentBodyHash);

public enum FunctionFingerprintDiffStatus
{
    Unchanged,
    Changed,
    Added,
    Removed
}
