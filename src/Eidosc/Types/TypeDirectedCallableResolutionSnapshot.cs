namespace Eidosc.Types;

public sealed record TypeDirectedCallableResolutionSnapshot(
    string SchemaVersion,
    IReadOnlyList<TypeDirectedCallableResolutionSnapshotEntry> Entries)
{
    public const string CurrentSchemaVersion = "type-directed-callable-resolution-snapshot-v3";

    public static TypeDirectedCallableResolutionSnapshot Empty { get; } =
        new(CurrentSchemaVersion, []);
}

public sealed record TypeDirectedCallableResolutionSnapshotEntry(
    string Candidates,
    string ArgumentTypes,
    string SelectedCandidate,
    int CandidateCount,
    int ViableCandidateCount,
    int BestScore);
