namespace Eidosc.Ast.Declarations;

/// <summary>
/// Versioned declaration attachment produced by the clause binder.
/// </summary>
public sealed record DeclarationAttachmentIR(
    string SchemaVersion,
    IReadOnlyList<ClauseIR> Clauses,
    IReadOnlyList<MetaInvocationIR> MetaInvocations,
    IReadOnlyDictionary<DeclarationAttachmentAdapterKind, IReadOnlyList<ClauseIR>> AdapterGroups,
    ForeignContractIR? ForeignContract)
{
    public static DeclarationAttachmentIR Empty { get; } = new(
        ClauseSchema.Version,
        [],
        [],
        EmptyAdapterGroups(),
        null);

    public IReadOnlyList<ClauseIR> GetAdapterEntries(DeclarationAttachmentAdapterKind adapter) =>
        AdapterGroups.TryGetValue(adapter, out var entries) ? entries : [];

    public static DeclarationAttachmentIR Create(
        IReadOnlyList<ClauseIR> clauses,
        IReadOnlyList<MetaInvocationIR> metaInvocations,
        IReadOnlyList<DeclarationClause> sourceClauses)
    {
        var groups = Enum.GetValues<DeclarationAttachmentAdapterKind>()
            .ToDictionary(
                static adapter => adapter,
                adapter => (IReadOnlyList<ClauseIR>)clauses
                    .Where(clause => ClauseSchema.TryGet(clause.Kind, out var spec) && spec.Adapter == adapter)
                    .ToArray());
        var foreignContract = sourceClauses
            .Where(static clause => clause.ClauseKind == DeclarationClauseKind.Extern)
            .Select(static clause => ForeignContractIR.TryCreate(clause, out var contract, out _) ? contract : null)
            .FirstOrDefault(static contract => contract != null);
        return new DeclarationAttachmentIR(ClauseSchema.Version, clauses, metaInvocations, groups, foreignContract);
    }

    private static IReadOnlyDictionary<DeclarationAttachmentAdapterKind, IReadOnlyList<ClauseIR>> EmptyAdapterGroups() =>
        Enum.GetValues<DeclarationAttachmentAdapterKind>()
            .ToDictionary(static adapter => adapter, static _ => (IReadOnlyList<ClauseIR>)[]);
}
