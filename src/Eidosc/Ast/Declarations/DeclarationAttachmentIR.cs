namespace Eidosc.Ast.Declarations;

/// <summary>
/// Versioned declaration attachment produced by the clause binder.
/// </summary>
public sealed record DeclarationAttachmentIR(
    string SchemaVersion,
    IReadOnlyList<ClauseIR> Clauses,
    IReadOnlyList<MetaInvocationIR> MetaInvocations,
    IReadOnlyDictionary<DeclarationAttachmentAdapterKind, IReadOnlyList<ClauseIR>> AdapterGroups)
{
    public static DeclarationAttachmentIR Empty { get; } = new(
        ClauseSchema.Version,
        [],
        [],
        EmptyAdapterGroups());

    public IReadOnlyList<ClauseIR> GetAdapterEntries(DeclarationAttachmentAdapterKind adapter) =>
        AdapterGroups.TryGetValue(adapter, out var entries) ? entries : [];

    public static DeclarationAttachmentIR Create(
        IReadOnlyList<ClauseIR> clauses,
        IReadOnlyList<MetaInvocationIR> metaInvocations)
    {
        var groups = Enum.GetValues<DeclarationAttachmentAdapterKind>()
            .ToDictionary(
                static adapter => adapter,
                adapter => (IReadOnlyList<ClauseIR>)clauses
                    .Where(clause => ClauseSchema.TryGet(clause.Kind, out var spec) && spec.Adapter == adapter)
                    .ToArray());
        return new DeclarationAttachmentIR(ClauseSchema.Version, clauses, metaInvocations, groups);
    }

    private static IReadOnlyDictionary<DeclarationAttachmentAdapterKind, IReadOnlyList<ClauseIR>> EmptyAdapterGroups() =>
        Enum.GetValues<DeclarationAttachmentAdapterKind>()
            .ToDictionary(static adapter => adapter, static _ => (IReadOnlyList<ClauseIR>)[]);
}
