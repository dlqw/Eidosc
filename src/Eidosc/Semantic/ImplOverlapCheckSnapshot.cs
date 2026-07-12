namespace Eidosc.Semantic;

public sealed record ImplOverlapCheckSnapshot(
    string SchemaVersion,
    IReadOnlyList<ImplOverlapCheckSnapshotEntry> Entries)
{
    public const string CurrentSchemaVersion = "impl-overlap-check-snapshot-v2";

    public static ImplOverlapCheckSnapshot Empty { get; } = new(CurrentSchemaVersion, []);
}

public sealed record ImplOverlapCheckSnapshotEntry(
    string QueryKey,
    string TraitKey,
    string CanonicalImplementingType,
    string CanonicalTraitTypeArgs,
    string RequestedTraitTypeArgs,
    int CandidateCount,
    string CandidateSetFingerprint,
    int NonOverlappingCandidateCount,
    int SpecializationAllowedCandidateCount,
    bool HasConflict,
    string? ConflictingImplKey,
    string? SpecializationRelation);
