using Eidosc.Utils;

namespace Eidosc.Ast.Declarations;

public readonly record struct ClauseOccurrenceId(
    string DeclarationIdentity,
    int ClauseIndex,
    int ArgumentSubIndex = -1)
{
    public override string ToString() => ArgumentSubIndex < 0
        ? $"{DeclarationIdentity}#clause:{ClauseIndex}"
        : $"{DeclarationIdentity}#clause:{ClauseIndex}:arg:{ArgumentSubIndex}";
}

public sealed record ClauseArgumentIR(
    int Index,
    ClauseCanonicalArgumentType Type,
    string CanonicalText,
    IReadOnlyList<string> Path);

public sealed record ClauseIR(
    string SchemaVersion,
    ClauseOccurrenceId OccurrenceId,
    DeclarationClauseKind Kind,
    string Keyword,
    ClauseStage Stage,
    ClauseSourceOrderBehavior SourceOrderBehavior,
    int SourceOrder,
    IReadOnlyList<ClauseArgumentIR> Arguments,
    SourceSpan Span,
    bool HasCompilerOwnedSourceGrant);

public enum MetaInvocationOwner
{
    UserExpand,
    CompilerDerive
}

public sealed record MetaInvocationIR(
    string SchemaVersion,
    ClauseOccurrenceId OccurrenceId,
    MetaInvocationOwner Owner,
    ClauseStage Stage,
    int SourceOrder,
    IReadOnlyList<string> GeneratorPath,
    IReadOnlyList<EidosAstNode> ExplicitArguments,
    SourceSpan Span,
    CompilerOwnedInvocationGrant? CompilerGrant);

public sealed class CompilerOwnedInvocationGrant
{
    private CompilerOwnedInvocationGrant()
    {
    }

    internal static CompilerOwnedInvocationGrant Create() => new();
}
