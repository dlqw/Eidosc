using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Syntax;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Parsing.Handwritten;

internal sealed record SyntaxFragmentParseResult(
    IReadOnlyList<EidosAstNode> Nodes,
    IReadOnlyList<Diagnostic.Diagnostic> Diagnostics);

internal enum SyntaxMemberGrammar
{
    Any,
    Type,
    Trait,
    Instance,
    Module
}

internal static class SyntaxFragmentParser
{
    public static bool TryParse(
        SyntaxSchemaEntry schema,
        IReadOnlyList<Token> tokens,
        string sourcePath,
        string languageVersion,
        string? sourceText,
        out SyntaxFragmentParseResult result,
        out string reason,
        SyntaxMemberGrammar memberGrammar = SyntaxMemberGrammar.Any)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(tokens);

        if (schema.Cardinality == SyntaxCardinality.Tokens)
        {
            result = new SyntaxFragmentParseResult([], []);
            reason = string.Empty;
            return true;
        }

        if (schema.Category == SyntaxCategory.Member && memberGrammar == SyntaxMemberGrammar.Any)
        {
            SyntaxFragmentParseResult? firstFailure = null;
            string? firstReason = null;
            foreach (var grammar in new[]
                     {
                         SyntaxMemberGrammar.Type,
                         SyntaxMemberGrammar.Trait,
                         SyntaxMemberGrammar.Instance,
                         SyntaxMemberGrammar.Module
                     })
            {
                if (TryParse(
                        schema,
                        tokens,
                        sourcePath,
                        languageVersion,
                        sourceText,
                        out result,
                        out reason,
                        grammar))
                {
                    return true;
                }

                firstFailure ??= result;
                firstReason ??= reason;
            }

            result = firstFailure ?? new SyntaxFragmentParseResult([], []);
            reason = firstReason ?? "syntax is not valid in any member grammar";
            return false;
        }

        var context = new ParserContext(tokens, sourcePath, languageVersion, sourceText);
        IReadOnlyList<EidosAstNode> nodes = schema.Category switch
        {
            SyntaxCategory.Item => new DeclParser(context).ParseProgram(),
            SyntaxCategory.Member => ParseMembers(context, memberGrammar),
            SyntaxCategory.Expression => [new ExprParser(context).ParseExpr()],
            SyntaxCategory.Pattern => [new PatternParser(context).ParsePattern()],
            SyntaxCategory.Type => [new TypeParser(context).ParseType()],
            SyntaxCategory.Statement => ParseStatement(tokens, sourcePath, languageVersion, sourceText, out context),
            _ => []
        };

        var diagnostics = context.Diagnostics.ToArray();
        result = new SyntaxFragmentParseResult(nodes, diagnostics);
        var errors = diagnostics
            .Where(static diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error)
            .ToArray();
        if (errors.Length > 0)
        {
            reason = string.Join("; ", errors.Select(static diagnostic => diagnostic.Message));
            return false;
        }

        if (!context.IsEof)
        {
            reason = $"quote {schema.SourceName} contains trailing syntax outside its grammar fragment";
            return false;
        }

        if (schema.Cardinality == SyntaxCardinality.Singular && nodes.Count != 1)
        {
            reason = $"quote {schema.SourceName} requires exactly one {FormatCategory(schema.Category)} fragment; " +
                     $"parsed {nodes.Count}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static IReadOnlyList<EidosAstNode> ParseMembers(
        ParserContext context,
        SyntaxMemberGrammar grammar)
    {
        var parser = new DeclParser(context);
        return grammar switch
        {
            SyntaxMemberGrammar.Type => parser.ParseTypeMemberFragments(),
            SyntaxMemberGrammar.Trait => parser.ParseTraitMemberFragments(),
            SyntaxMemberGrammar.Instance => parser.ParseInstanceMemberFragments(),
            SyntaxMemberGrammar.Module => parser.ParseProgram(),
            _ => []
        };
    }

    private static IReadOnlyList<EidosAstNode> ParseStatement(
        IReadOnlyList<Token> tokens,
        string sourcePath,
        string languageVersion,
        string? sourceText,
        out ParserContext context)
    {
        var start = tokens.Count > 0 ? tokens[0].Location : new SourceLocation(1, 1, 0, sourcePath);
        var end = tokens.Count > 0
            ? tokens[^1].Location + tokens[^1].Length
            : start;
        var wrappedTokens = new List<Token>(tokens.Count + 2)
        {
            CreatePunctuation(
                "{",
                SyntaxKind.PtLBrace,
                new SourceLocation(
                    Math.Max(0, start.Position - 1),
                    start.Line,
                    Math.Max(0, start.Column - 1),
                    start.FilePath))
        };
        wrappedTokens.AddRange(tokens);
        wrappedTokens.Add(CreatePunctuation("}", SyntaxKind.PtRBrace, end));
        var wrappedSource = sourceText == null ? null : "{" + sourceText[1..] + "}";
        context = new ParserContext(wrappedTokens, sourcePath, languageVersion, wrappedSource);
        var block = new ExprParser(context).ParseExpr() as BlockExpr;
        return block?.Statements.Cast<EidosAstNode>().ToArray() ?? [];
    }

    private static string FormatCategory(SyntaxCategory category) =>
        category.ToString().ToLowerInvariant();

    private static ContentToken CreatePunctuation(
        string spelling,
        SyntaxKind kind,
        SourceLocation location) => new(
        location,
        kind,
        new Terminal(0, spelling, TerminalFlag.IsPunctuation),
        spelling.GetOrIntern(),
        spelling.Length,
        spelling);
}
