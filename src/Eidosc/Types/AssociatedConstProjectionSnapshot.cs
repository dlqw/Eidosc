namespace Eidosc.Types;

public sealed record AssociatedConstProjectionSnapshot(
    string SchemaVersion,
    IReadOnlyList<AssociatedConstProjectionSnapshotEntry> Entries)
{
    public const string CurrentSchemaVersion = "associated-const-projection-snapshot-v1";

    public static AssociatedConstProjectionSnapshot Empty { get; } = new(CurrentSchemaVersion, []);
}

public sealed record AssociatedConstProjectionSnapshotEntry(
    string TraitKey,
    string TraitName,
    string MemberName,
    string ImplementingTypeKey,
    string TraitArgKeys,
    string ConstTypeSignature,
    string ConstValueSignature);
