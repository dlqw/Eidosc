namespace Eidosc.Types;

public sealed record TraitCheckSnapshot(
    string SchemaVersion,
    IReadOnlyList<TraitCheckSnapshotEntry> Entries)
{
    public const string CurrentSchemaVersion = "trait-check-snapshot-v2";

    public static TraitCheckSnapshot Empty { get; } = new(CurrentSchemaVersion, []);
}

public sealed record TraitCheckSnapshotEntry(
    string TypeKey,
    string TraitKey,
    string TraitName,
    string TraitArgs,
    string TraitArgKeys,
    bool Success,
    string? ErrorMessage,
    string CandidateSetFingerprint = "");
