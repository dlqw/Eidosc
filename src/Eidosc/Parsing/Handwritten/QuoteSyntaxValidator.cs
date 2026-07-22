using Eidosc.Ast.Expressions;
using Eidosc.Syntax;
using Eidosc.Utilities;

namespace Eidosc.Parsing.Handwritten;

internal static class QuoteSyntaxValidator
{
    public static bool Validate(QuoteExpr quote, ParserContext parentContext)
    {
        quote.GrammarValidated = false;
        if (quote.Kind is not { } kind)
        {
            return false;
        }

        if (kind == QuoteKind.Tokens)
        {
            quote.GrammarValidated = true;
            return true;
        }

        if (quote.Parts.Any(static part => part is QuoteSplicePart))
        {
            return false;
        }

        var tokens = quote.Parts
            .OfType<QuoteTokenPart>()
            .Select(CreateToken)
            .ToList();
        var entry = SyntaxSchema.Get(kind);
        var valid = SyntaxFragmentParser.TryParse(
            entry,
            tokens,
            parentContext.SourcePath,
            parentContext.LanguageVersion,
            sourceText: null,
            out var parseResult,
            out var reason);

        foreach (var diagnostic in parseResult.Diagnostics)
        {
            parentContext.AddDiagnostic(diagnostic);
        }

        if (!valid && parseResult.Diagnostics.Count == 0)
        {
            parentContext.Error(
                string.IsNullOrWhiteSpace(reason)
                    ? $"quote {entry.SourceName} does not contain a valid {entry.Category.ToString().ToLowerInvariant()} fragment"
                    : reason,
                quote.Span.Location);
        }

        quote.GrammarValidated = valid &&
                                 !parseResult.Diagnostics.Any(static diagnostic =>
                                     diagnostic.Level == Diagnostic.DiagnosticLevel.Error);
        return quote.GrammarValidated;
    }

    private static Token CreateToken(QuoteTokenPart part)
    {
        if (part.TokenKind == SyntaxKind.Comment)
        {
            return new CommentToken(part.Span.Location, part.Spelling);
        }

        return new ContentToken(
            part.Span.Location,
            part.TokenKind,
            new Terminal(0, part.TerminalName, part.TerminalFlags),
            part.Spelling.GetOrIntern(),
            part.Spelling.Length,
            part.Spelling);
    }

}
