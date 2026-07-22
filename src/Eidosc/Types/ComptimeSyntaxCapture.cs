using System.Security.Cryptography;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Parsing.Lexer;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Syntax;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Types;

internal static class ComptimeSyntaxCapture
{
    public static bool TryCapture(
        EidosAstNode node,
        SyntaxCategory category,
        string sourceText,
        SymbolTable symbolTable,
        string expansionTrace,
        out ComptimeSyntaxValue syntax,
        out string reason)
    {
        syntax = null!;
        if (node.Span.Position < 0 ||
            node.Span.EndPosition > sourceText.Length ||
            node.Span.Length <= 0)
        {
            reason = "syntax capture requires a concrete source span";
            return false;
        }

        var sourceSlice = sourceText.Substring(node.Span.Position, node.Span.Length);
        var (grammarData, scannerData) = LexerTableBuilder.Build();
        var stream = new SourceStream(
            sourceSlice,
            4,
            new SourceLocation(
                0,
                node.Span.Location.Line,
                node.Span.Location.Column,
                node.Span.FilePath));
        var lexer = new LexerContext(stream, scannerData, grammarData.Terminals);
        Scanner.Init(lexer);

        var rawTokens = new List<Token>();
        while (lexer.TokenStream!.MoveNext())
        {
            if (lexer.TokenStream.Current is not EofToken)
            {
                rawTokens.Add(lexer.TokenStream.Current);
            }
        }

        if (lexer.Diagnostics.Any(static diagnostic =>
                diagnostic.Level == Diagnostic.DiagnosticLevel.Error) ||
            rawTokens.Any(static token => token is ErrorToken))
        {
            reason = "syntax capture could not lex the source fragment";
            return false;
        }

        var identityNodes = CollectIdentityNodes(node, symbolTable);
        var tokens = new List<ComptimeSyntaxToken>(rawTokens.Count);
        var previousEnd = node.Span.Position;
        foreach (var token in rawTokens)
        {
            var absolutePosition = node.Span.Position + token.Location.Position;
            var absoluteLocation = new SourceLocation(
                absolutePosition,
                token.Location.Line,
                token.Location.Column,
                token.Location.FilePath);
            var spelling = ReadTokenText(token, sourceSlice);
            var leadingTrivia = ReadSourceSlice(sourceText, previousEnd, absolutePosition);
            var (kind, terminalName, terminalFlags) = token switch
            {
                ContentToken content => (content.Kind, content.Terminal.DebugName, content.Terminal.Flags),
                CommentToken => (SyntaxKind.Comment, "comment", TerminalFlag.None),
                ErrorToken => (SyntaxKind.Error, "error", TerminalFlag.None),
                _ => (SyntaxKind.None, string.Empty, TerminalFlag.None)
            };
            tokens.Add(new ComptimeSyntaxToken(
                kind,
                terminalName,
                terminalFlags,
                spelling,
                leadingTrivia,
                string.Empty,
                new SourceSpan(absoluteLocation, token.Length),
                FindIdentity(identityNodes, absolutePosition, token.Length, spelling)));
            previousEnd = absolutePosition + token.Length;
        }

        var trailingTrivia = ReadSourceSlice(sourceText, previousEnd, node.Span.EndPosition);
        var sourceUri = MetaComptimeIntrinsics.CreatePublicSourceUri(node.Span, symbolTable);
        var origin = new ComptimeSyntaxOrigin(
            sourceUri,
            node.Span.Position,
            node.Span.Location.Line,
            node.Span.Location.Column,
            node.Span.Length,
            expansionTrace);
        var material = $"capture:{SyntaxSchema.Version}:{category}:{origin.CanonicalText}:" +
                       string.Join(";", tokens.Select(static token => token.CanonicalText));
        var hygiene = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        syntax = new ComptimeSyntaxValue(category, tokens, trailingTrivia, origin, hygiene)
        {
            StaticType = CreateSyntaxType(category)
        };
        reason = string.Empty;
        return true;
    }

    private static IReadOnlyList<CaptureIdentityNode> CollectIdentityNodes(
        EidosAstNode root,
        SymbolTable symbolTable)
    {
        var result = new List<CaptureIdentityNode>();
        var pending = new Stack<EidosAstNode>();
        var visited = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!visited.Add(node))
            {
                continue;
            }

            if (node.SymbolId.IsValid &&
                symbolTable.GetSymbol(node.SymbolId) is { } symbol &&
                TryGetTerminalName(node, out var terminalName))
            {
                var identityKind = symbol is AdtSymbol or TraitSymbol or TypeParamSymbol
                    ? ComptimeSyntaxIdentityKind.Type
                    : ComptimeSyntaxIdentityKind.Declaration;
                result.Add(new CaptureIdentityNode(
                    node.Span,
                    terminalName,
                    new ComptimeSyntaxIdentity(
                        identityKind,
                        MetaComptimeIntrinsics.CreateStableIdentity(symbol, symbolTable),
                        symbol.Id,
                        symbol.TypeId)));
            }

            foreach (var child in AstStableNodeTraversal.GetStructuralChildren(node))
            {
                pending.Push(child);
            }
        }

        return result;
    }

    private static ComptimeSyntaxIdentity? FindIdentity(
        IReadOnlyList<CaptureIdentityNode> nodes,
        int position,
        int length,
        string spelling)
    {
        var end = position + length;
        return nodes
            .Where(candidate =>
                position >= candidate.Span.Position &&
                end <= candidate.Span.EndPosition &&
                string.Equals(candidate.TerminalName, spelling, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Span.Length)
            .Select(static candidate => candidate.Identity)
            .FirstOrDefault();
    }

    private static bool TryGetTerminalName(EidosAstNode node, out string name)
    {
        name = node switch
        {
            IdentifierExpr identifier => identifier.Name,
            PathExpr path => path.Name,
            TypePath path => path.TypeName,
            CtorExpr constructor => constructor.ConstructorName,
            CtorPattern constructor => constructor.ConstructorName,
            MethodCallExpr method => method.MethodName,
            VarPattern variable => variable.Name,
            AsPattern binding => binding.BindingName,
            _ => string.Empty
        };
        return !string.IsNullOrWhiteSpace(name);
    }

    private static string ReadTokenText(Token token, string sourceText)
    {
        if (token.Location.Position >= 0 &&
            token.Location.Position + token.Length <= sourceText.Length &&
            token.Length > 0)
        {
            return sourceText.Substring(token.Location.Position, token.Length);
        }

        return token switch
        {
            CommentToken comment => comment.Comment,
            ContentToken content => content.Value as string ?? content.TextId.Resolve(),
            ErrorToken error => error.Message,
            _ => token.ToString() ?? string.Empty
        };
    }

    private static string ReadSourceSlice(string sourceText, int start, int end)
    {
        var safeStart = Math.Clamp(start, 0, sourceText.Length);
        var safeEnd = Math.Clamp(end, safeStart, sourceText.Length);
        return sourceText.Substring(safeStart, safeEnd - safeStart);
    }

    internal static Type CreateSyntaxType(SyntaxCategory category)
    {
        var marker = category switch
        {
            SyntaxCategory.Item => MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Item,
                WellKnownTypeIds.MetaItemId),
            SyntaxCategory.Member => MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Member,
                WellKnownTypeIds.MetaMemberId),
            SyntaxCategory.Statement => MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Stmt,
                WellKnownTypeIds.MetaStmtId),
            SyntaxCategory.Expression => MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Expr,
                WellKnownTypeIds.MetaExprId),
            SyntaxCategory.Pattern => MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Pattern,
                WellKnownTypeIds.MetaPatternId),
            SyntaxCategory.Type => MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.TypeSyntax,
                WellKnownTypeIds.MetaTypeSyntaxId),
            _ => MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Tokens,
                WellKnownTypeIds.MetaTokensId)
        };
        return MetaSchemaRegistry.MetaType(
            WellKnownStrings.Meta.Types.Syntax,
            WellKnownTypeIds.MetaSyntaxId) with
        {
            Args = [marker]
        };
    }

    private sealed record CaptureIdentityNode(
        SourceSpan Span,
        string TerminalName,
        ComptimeSyntaxIdentity Identity);
}
