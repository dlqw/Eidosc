namespace Eidosc.Pipeline;

public sealed record NamerStateMergeResult(
    bool IsApplied,
    SymbolTableStateBuildResult? BuildResult,
    int PayloadModules,
    IReadOnlyList<string> Failures)
{
    public static NamerStateMergeResult Blocked(IReadOnlyList<string> failures) =>
        new(false, null, 0, failures);
}

public static class NamerStateMerger
{
    public static NamerStateMergeResult Merge(
        IReadOnlyList<ModuleNamerStatePayload> payloads)
    {
        if (payloads.Count == 0)
        {
            return NamerStateMergeResult.Blocked(["missing-namer-state-payload"]);
        }

        var failures = new List<string>();
        var selected = new Dictionary<string, ModuleNamerStatePayload>(StringComparer.Ordinal);
        foreach (var payload in payloads
                     .OrderBy(static payload => payload.ModuleIdentityKey, StringComparer.Ordinal)
                     .ThenBy(static payload => payload.ModuleKey, StringComparer.Ordinal))
        {
            if (!string.Equals(payload.SchemaVersion, ModuleNamerStatePayload.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                failures.Add($"unsupported-namer-payload-schema:{payload.ModuleKey}:{payload.SchemaVersion}");
                continue;
            }

            if (!payload.HasValidPayloadHash())
            {
                failures.Add($"invalid-namer-payload-hash:{payload.ModuleKey}");
                continue;
            }

            if (!selected.TryAdd(payload.ModuleIdentityKey, payload) &&
                !string.Equals(selected[payload.ModuleIdentityKey].PayloadHash, payload.PayloadHash, StringComparison.Ordinal))
            {
                failures.Add($"conflicting-namer-payload:{payload.ModuleIdentityKey}");
            }
        }

        if (failures.Count > 0)
        {
            return NamerStateMergeResult.Blocked(failures);
        }

        var orderedPayloads = selected.Values
            .OrderBy(static payload => payload.ModuleKey, StringComparer.Ordinal)
            .ThenBy(static payload => payload.ModuleIdentityKey, StringComparer.Ordinal)
            .ToArray();
        var build = SymbolTableStateBuilder.BuildFromNamerPayloads(orderedPayloads);
        return new NamerStateMergeResult(
            build.IsApplied,
            build,
            orderedPayloads.Length,
            build.Failures);
    }
}
