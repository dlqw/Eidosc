namespace Eidosc.Types;

public sealed record AssociatedTypeProjectionSnapshot(
    string SchemaVersion,
    IReadOnlyList<AssociatedTypeProjectionSnapshotEntry> Entries)
{
    public const string CurrentSchemaVersion = "associated-type-projection-snapshot-v4";

    public static AssociatedTypeProjectionSnapshot Empty { get; } = new(CurrentSchemaVersion, []);
}

public sealed record AssociatedTypeProjectionSnapshotEntry(
    string TraitKey,
    string TraitName,
    string MemberName,
    string ImplementingTypeKey,
    string TraitArgKeys,
    bool AllowTypeConstructorReference,
    string ValueTypeSignature,
    string ReducedTypeKind,
    string ReducedTypeName,
    string ReducedTypeArgs,
    string ReducedType,
    AssociatedTypeProjectionReducedTypeShape? ReducedTypeShape = null);

public sealed record AssociatedTypeProjectionReducedTypeShape(
    string Kind,
    string Name,
    IReadOnlyList<AssociatedTypeProjectionReducedTypeShape> Args,
    string CanonicalKey = "",
    int SymbolId = 0,
    int TypeId = 0,
    int? ConstructorVarIndex = null);
