using System.Reflection;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Hir;
using Eidosc.Parsing.Handwritten;
using Eidosc.Utils;

namespace Eidosc.Tests.Unit.Parsing.Handwritten;

public sealed class QuoteAstInfrastructureTests
{
    [Fact]
    public void Quote_clone_visitor_and_xml_preserve_parts_splice_and_trivia()
    {
        var tokenSpan = Span(10, 5);
        var spliceSpan = Span(16, 8);
        var identifier = new IdentifierExpr();
        identifier.SetSpan(Span(19, 4));
        identifier.SetName("seed");

        var token = new QuoteTokenPart();
        token.Initialize(
            SyntaxKind.Identifier,
            "identifier",
            TerminalFlag.None,
            "value",
            " /* leading */ ",
            tokenSpan);
        var splice = new QuoteSplicePart();
        splice.Initialize(isMany: true, identifier, "\n    ", spliceSpan);
        var quote = new QuoteExpr();
        quote.SetKind(QuoteKind.Expression);
        quote.SetParts([token, splice]);
        quote.SetTrailingTrivia(" // trailing");
        quote.SetSpan(Span(0, 32));
        quote.GrammarValidated = true;

        var cloneMethod = typeof(ExprParser).GetMethod(
            "CloneExpression",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(cloneMethod);
        var clone = Assert.IsType<QuoteExpr>(cloneMethod!.Invoke(null, [quote, null]));

        Assert.NotSame(quote, clone);
        Assert.Equal(quote.Kind, clone.Kind);
        Assert.Equal(quote.TrailingTrivia, clone.TrailingTrivia);
        Assert.True(clone.GrammarValidated);
        Assert.Collection(
            clone.Parts,
            clonedPart =>
            {
                var clonedToken = Assert.IsType<QuoteTokenPart>(clonedPart);
                Assert.NotSame(token, clonedToken);
                Assert.Equal(token.Spelling, clonedToken.Spelling);
                Assert.Equal(token.LeadingTrivia, clonedToken.LeadingTrivia);
            },
            clonedPart =>
            {
                var clonedSplice = Assert.IsType<QuoteSplicePart>(clonedPart);
                Assert.NotSame(splice, clonedSplice);
                Assert.True(clonedSplice.IsMany);
                Assert.Equal(splice.LeadingTrivia, clonedSplice.LeadingTrivia);
                Assert.NotSame(identifier, clonedSplice.Value);
                Assert.Equal("seed", Assert.IsType<IdentifierExpr>(clonedSplice.Value).Name);
            });

        Assert.Equal(2, AstNodeCollector<QuotePart>.Collect(clone).Count);
        Assert.Single(AstNodeCollector<IdentifierExpr>.Collect(clone));
        var xml = clone.ToXml();
        Assert.Contains("QuoteTokenPart", xml, StringComparison.Ordinal);
        Assert.Contains("QuoteSplicePart", xml, StringComparison.Ordinal);
        Assert.Contains("leadingTrivia", xml, StringComparison.Ordinal);
        Assert.Contains("trailingTrivia", xml, StringComparison.Ordinal);
        Assert.Contains("IdentifierExpr", xml, StringComparison.Ordinal);
    }

    private static SourceSpan Span(int position, int length) => new(
        new SourceLocation(position, line: 1, column: position + 1, "quote_clone.eidos"),
        length);
}
