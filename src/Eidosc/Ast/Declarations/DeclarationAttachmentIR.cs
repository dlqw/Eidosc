namespace Eidosc.Ast.Declarations;

/// <summary>
/// Versioned declaration attachment produced by the clause binder.
/// </summary>
public sealed record DeclarationAttachmentIR(
    string SchemaVersion,
    IReadOnlyList<ClauseIR> Clauses,
    IReadOnlyList<MetaInvocationIR> MetaInvocations)
{
    public static DeclarationAttachmentIR Empty { get; } = new(
        ClauseSchema.Version,
        [],
        []);
}
