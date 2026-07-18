using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Syntax;
using Eidosc.Utils;

namespace Eidosc.Parsing.Handwritten;

public sealed partial class ExprParser
{
    private QuoteExpr ParseQuoteExpr()
    {
        var startToken = ctx.Expect(WellKnownStrings.Keywords.Quote);
        QuoteKind? kind = null;
        if (SyntaxSchema.TryParseQuoteKind(ctx.GetText(), out var explicitKind))
        {
            var saved = ctx.SavePosition();
            ctx.Advance();
            if (ctx.Check("{"))
            {
                kind = explicitKind;
            }
            else
            {
                ctx.RestorePosition(saved);
            }
        }

        var openBrace = ctx.Expect("{");
        var previousEnd = openBrace.Location.Position + openBrace.Length;
        var delimiters = new Stack<string>();
        var parts = new List<QuotePart>();
        Token? closeBrace = null;

        while (ctx.RawCurrent is not EofToken)
        {
            var token = ctx.RawCurrent;
            var spelling = ctx.GetRawText(token);
            if (spelling == "}" && delimiters.Count == 0)
            {
                closeBrace = ctx.AdvanceRaw();
                break;
            }

            if (TryParseQuoteSplice(previousEnd, out var splice, out var spliceEnd))
            {
                parts.Add(splice);
                previousEnd = spliceEnd;
                continue;
            }

            UpdateQuoteDelimiterStack(delimiters, spelling, token);
            var leadingTrivia = ctx.GetSourceSlice(previousEnd, token.Location.Position);
            parts.Add(CreateQuoteTokenPart(token, spelling, leadingTrivia));
            previousEnd = token.Location.Position + token.Length;
            ctx.AdvanceRaw();
        }

        if (closeBrace == null)
        {
            ctx.Error("unterminated quote expression; expected '}'", startToken.Location);
            closeBrace = ctx.RawCurrent;
        }

        if (delimiters.Count > 0)
        {
            ctx.Error($"unbalanced delimiter in quote expression; expected '{delimiters.Peek()}'", closeBrace.Location);
        }

        var quote = new QuoteExpr();
        quote.SetKind(kind);
        quote.SetParts(parts);
        quote.SetTrailingTrivia(ctx.GetSourceSlice(previousEnd, closeBrace.Location.Position));
        quote.SetSpan(new SourceSpan(
            startToken.Location,
            Math.Max(0, closeBrace.Location.Position + closeBrace.Length - startToken.Location.Position)));
        QuoteSyntaxValidator.Validate(quote, ctx);
        return quote;
    }

    private bool TryParseQuoteSplice(
        int previousEnd,
        out QuoteSplicePart splice,
        out int spliceEnd)
    {
        splice = null!;
        spliceEnd = previousEnd;
        var isMany = ctx.GetRawText(ctx.RawCurrent) == ".." &&
                     ctx.GetRawText(ctx.RawPeek(1)) == "$" &&
                     ctx.GetRawText(ctx.RawPeek(2)) == "(";
        var isSingle = ctx.GetRawText(ctx.RawCurrent) == "$" &&
                       ctx.GetRawText(ctx.RawPeek(1)) == "(";
        if (!isMany && !isSingle)
        {
            return false;
        }

        var marker = ctx.RawCurrent;
        var leadingTrivia = ctx.GetSourceSlice(previousEnd, marker.Location.Position);
        if (isMany)
        {
            ctx.AdvanceRaw();
        }

        ctx.AdvanceRaw(); // $
        ctx.AdvanceRaw(); // (
        var innerTokens = new List<Token>();
        var parenDepth = 1;
        Token? closeParen = null;
        while (ctx.RawCurrent is not EofToken)
        {
            var token = ctx.RawCurrent;
            var spelling = ctx.GetRawText(token);
            if (spelling == "(")
            {
                parenDepth++;
            }
            else if (spelling == ")")
            {
                parenDepth--;
                if (parenDepth == 0)
                {
                    closeParen = ctx.AdvanceRaw();
                    break;
                }
            }

            innerTokens.Add(ctx.AdvanceRaw());
        }

        if (closeParen == null)
        {
            ctx.Error("unterminated quote splice; expected ')'", marker.Location);
            closeParen = ctx.RawCurrent;
        }

        var spliceContext = new ParserContext(innerTokens, ctx.SourcePath, ctx.LanguageVersion);
        var value = new ExprParser(spliceContext).ParseExpr();
        foreach (var diagnostic in spliceContext.Diagnostics)
        {
            ctx.AddDiagnostic(diagnostic);
        }

        if (!spliceContext.IsEof)
        {
            ctx.Error("quote splice must contain exactly one comptime expression", marker.Location);
        }

        spliceEnd = closeParen.Location.Position + closeParen.Length;
        splice = new QuoteSplicePart();
        splice.Initialize(
            isMany,
            value,
            leadingTrivia,
            new SourceSpan(marker.Location, Math.Max(0, spliceEnd - marker.Location.Position)));
        return true;
    }

    private void UpdateQuoteDelimiterStack(Stack<string> delimiters, string spelling, Token token)
    {
        switch (spelling)
        {
            case "(":
                delimiters.Push(")");
                return;
            case "[":
                delimiters.Push("]");
                return;
            case "{":
                delimiters.Push("}");
                return;
            case ")":
            case "]":
            case "}":
                if (delimiters.Count == 0 || delimiters.Pop() != spelling)
                {
                    ctx.Error($"mismatched delimiter '{spelling}' in quote expression", token.Location);
                }
                return;
        }
    }

    private static QuoteTokenPart CreateQuoteTokenPart(Token token, string spelling, string leadingTrivia)
    {
        var (kind, terminalName, terminalFlags) = token switch
        {
            ContentToken content => (content.Kind, content.Terminal.DebugName, content.Terminal.Flags),
            CommentToken => (SyntaxKind.Comment, "comment", TerminalFlag.None),
            ErrorToken => (SyntaxKind.Error, "error", TerminalFlag.None),
            _ => (SyntaxKind.None, string.Empty, TerminalFlag.None)
        };
        var part = new QuoteTokenPart();
        part.Initialize(
            kind,
            terminalName,
            terminalFlags,
            spelling,
            leadingTrivia,
            new SourceSpan(token.Location, token.Length));
        return part;
    }
}
