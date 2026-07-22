using Eidosc.Ast.Expressions;
using Eidosc.Parsing.Handwritten;
using Eidosc.Parsing.Lexer;
using Eidosc.ProjectSystem;
using Eidosc.Utils;

namespace Eidosc.Tests.Unit.Parsing.Handwritten;

public sealed class QuoteParserTests
{
    [Fact]
    public void Quote_expr_preserves_comments_spelling_trivia_and_typed_splices()
    {
        const string source = "quote expr { /* lead */ value.$(field) + $(1) }";
        var context = Lex(source);

        var result = new ExprParser(context).ParseExpr();

        var quote = Assert.IsType<QuoteExpr>(result);
        Assert.Equal(QuoteKind.Expression, quote.Kind);
        Assert.False(quote.GrammarValidated);
        var comment = Assert.Single(quote.Parts.OfType<QuoteTokenPart>(), static part =>
            part.TokenKind == SyntaxKind.Comment);
        Assert.Equal("/* lead */", comment.Spelling);
        Assert.StartsWith(" ", comment.LeadingTrivia, StringComparison.Ordinal);
        var splices = quote.Parts.OfType<QuoteSplicePart>().ToArray();
        Assert.Equal(2, splices.Length);
        Assert.All(splices, static splice => Assert.False(splice.IsMany));
        Assert.Equal("field", Assert.IsType<IdentifierExpr>(splices[0].Value).Name);
        Assert.Equal(1, Assert.IsType<LiteralExpr>(splices[1].Value).Value);
        Assert.Equal(" ", quote.TrailingTrivia);
        Assert.Empty(context.Diagnostics);
    }

    [Fact]
    public void Quote_tokens_accepts_unparsed_balanced_token_trees()
    {
        const string source = "quote tokens { if ??? { value } }";
        var context = Lex(source);

        var result = new ExprParser(context).ParseExpr();

        var quote = Assert.IsType<QuoteExpr>(result);
        Assert.Equal(QuoteKind.Tokens, quote.Kind);
        Assert.True(quote.GrammarValidated);
        Assert.Empty(context.Diagnostics);
    }

    [Fact]
    public void Quote_expr_rejects_invalid_grammar_without_splices()
    {
        const string source = "quote expr { if }";
        var context = Lex(source);

        var quote = Assert.IsType<QuoteExpr>(new ExprParser(context).ParseExpr());

        Assert.False(quote.GrammarValidated);
        Assert.Contains(context.Diagnostics, static diagnostic =>
            diagnostic.Level == Eidosc.Diagnostic.DiagnosticLevel.Error);
    }

    [Fact]
    public void Quote_many_splice_is_lexed_as_range_dollar_paren_sequence()
    {
        const string source = "quote expr { { ..$(statements) } }";
        var context = Lex(source);

        var quote = Assert.IsType<QuoteExpr>(new ExprParser(context).ParseExpr());

        var splice = Assert.Single(quote.Parts.OfType<QuoteSplicePart>());
        Assert.True(splice.IsMany);
        Assert.Equal("statements", Assert.IsType<IdentifierExpr>(splice.Value).Name);
        Assert.Empty(context.Diagnostics);
    }

    private static ParserContext Lex(string source)
    {
        var (grammar, scanner) = LexerTableBuilder.Build();
        var sourceStream = new SourceStream(
            source,
            4,
            new SourceLocation(0, 0, 0, "quote_test.eidos"));
        var lexer = new LexerContext(sourceStream, scanner, grammar.Terminals);
        Scanner.Init(lexer);
        var tokens = new List<Token>();
        Token? token;
        while ((token = Scanner.GetToken(lexer)) != null)
        {
            tokens.Add(token);
        }

        return new ParserContext(
            tokens,
            "quote_test.eidos",
            EidosLanguageVersions.Current,
            source);
    }
}
