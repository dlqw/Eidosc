using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;

namespace Eidosc.Syntax;

public enum SyntaxCategory
{
    Item,
    Member,
    Statement,
    Expression,
    Pattern,
    Type,
    Tokens,
    Nested
}

public enum SyntaxCardinality
{
    Singular,
    Sequence,
    Tokens
}

public sealed record SyntaxSchemaEntry(
    QuoteKind QuoteKind,
    string SourceName,
    SyntaxCategory Category,
    SyntaxCardinality Cardinality,
    string MetaMarkerType);

public static class SyntaxSchema
{
    public const int Version = 1;

    private static readonly SyntaxSchemaEntry[] Entries =
    [
        new(QuoteKind.Items, "items", SyntaxCategory.Item, SyntaxCardinality.Sequence, WellKnownStrings.Meta.Types.Item),
        new(QuoteKind.Item, "item", SyntaxCategory.Item, SyntaxCardinality.Singular, WellKnownStrings.Meta.Types.Item),
        new(QuoteKind.Members, "members", SyntaxCategory.Member, SyntaxCardinality.Sequence, WellKnownStrings.Meta.Types.Member),
        new(QuoteKind.Member, "member", SyntaxCategory.Member, SyntaxCardinality.Singular, WellKnownStrings.Meta.Types.Member),
        new(QuoteKind.Statement, "stmt", SyntaxCategory.Statement, SyntaxCardinality.Singular, WellKnownStrings.Meta.Types.Stmt),
        new(QuoteKind.Expression, "expr", SyntaxCategory.Expression, SyntaxCardinality.Singular, WellKnownStrings.Meta.Types.Expr),
        new(QuoteKind.Pattern, "pattern", SyntaxCategory.Pattern, SyntaxCardinality.Singular, WellKnownStrings.Meta.Types.Pattern),
        new(QuoteKind.Type, "type", SyntaxCategory.Type, SyntaxCardinality.Singular, WellKnownStrings.Meta.Types.TypeSyntax),
        new(QuoteKind.Tokens, "tokens", SyntaxCategory.Tokens, SyntaxCardinality.Tokens, WellKnownStrings.Meta.Types.Tokens)
    ];

    private static readonly IReadOnlyDictionary<QuoteKind, SyntaxSchemaEntry> ByKind =
        Entries.ToDictionary(static entry => entry.QuoteKind);

    private static readonly IReadOnlyDictionary<string, SyntaxSchemaEntry> BySourceName =
        Entries.ToDictionary(static entry => entry.SourceName, StringComparer.Ordinal);

    public static IReadOnlyList<SyntaxSchemaEntry> All => Entries;

    public static SyntaxSchemaEntry Get(QuoteKind kind) => ByKind[kind];

    public static bool TryParseQuoteKind(string sourceName, out QuoteKind kind)
    {
        if (BySourceName.TryGetValue(sourceName, out var entry))
        {
            kind = entry.QuoteKind;
            return true;
        }

        kind = default;
        return false;
    }

    public static IReadOnlyList<SyntaxCategory> GetNodeCategories(Type nodeType)
    {
        ArgumentNullException.ThrowIfNull(nodeType);
        if (typeof(Declaration).IsAssignableFrom(nodeType))
        {
            return [SyntaxCategory.Item, SyntaxCategory.Member, SyntaxCategory.Statement];
        }

        if (typeof(Field).IsAssignableFrom(nodeType) ||
            typeof(Constructor).IsAssignableFrom(nodeType) ||
            typeof(AssociatedTypeDecl).IsAssignableFrom(nodeType) ||
            typeof(AssociatedConstDecl).IsAssignableFrom(nodeType))
        {
            return [SyntaxCategory.Member];
        }

        if (typeof(Expression).IsAssignableFrom(nodeType))
        {
            return [SyntaxCategory.Expression, SyntaxCategory.Statement];
        }

        if (typeof(ExpandStmt).IsAssignableFrom(nodeType))
        {
            return [SyntaxCategory.Statement];
        }

        if (typeof(Pattern).IsAssignableFrom(nodeType))
        {
            return [SyntaxCategory.Pattern];
        }

        if (typeof(TypeNode).IsAssignableFrom(nodeType))
        {
            return [SyntaxCategory.Type];
        }

        if (typeof(EidosAstNode).IsAssignableFrom(nodeType))
        {
            return [SyntaxCategory.Nested];
        }

        return [];
    }

    public static bool CanEmbed(SyntaxCategory source, SyntaxCategory target) =>
        source == target || source == SyntaxCategory.Expression && target == SyntaxCategory.Statement;
}
