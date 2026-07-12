using Eidosc.Parsing.Handwritten;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Tests.Unit.Parsing.Handwritten;

public sealed class ParserContextTests
{
    private static List<Token> MakeTokens(params string[] texts)
    {
        var tokens = new List<Token>();
        foreach (var t in texts)
        {
            tokens.Add(new TestToken(t));
        }
        tokens.Add(new EofToken(new SourceLocation(tokens.Count, 0, 0)));
        return tokens;
    }

    private static List<Token> MakeTokensWithoutEof(params string[] texts)
    {
        var tokens = new List<Token>();
        foreach (var t in texts)
        {
            tokens.Add(new TestToken(t));
        }

        return tokens;
    }

    [Fact]
    public void Advance_increments_position()
    {
        var ctx = new ParserContext(MakeTokens("a", "b", "c"), "test");
        Assert.Equal("a", ctx.GetText());
        ctx.Advance();
        Assert.Equal("b", ctx.GetText());
        ctx.Advance();
        Assert.Equal("c", ctx.GetText());
    }

    [Fact]
    public void Match_returns_true_on_match()
    {
        var ctx = new ParserContext(MakeTokens("func", "x"), "test");
        Assert.True(ctx.Match("func"));
        Assert.Equal("x", ctx.GetText());
    }

    [Fact]
    public void Match_returns_false_on_mismatch()
    {
        var ctx = new ParserContext(MakeTokens("func", "x"), "test");
        Assert.False(ctx.Match("let"));
        Assert.Equal("func", ctx.GetText());
    }

    [Fact]
    public void Expect_advances_on_match()
    {
        var ctx = new ParserContext(MakeTokens("func", "x"), "test");
        var token = ctx.Expect("func", "err");
        Assert.Equal("func", ctx.GetText(token));
        Assert.Equal("x", ctx.GetText());
    }

    [Fact]
    public void Expect_reports_error_on_mismatch()
    {
        var ctx = new ParserContext(MakeTokens("func"), "test");
        ctx.Expect("let", "expected let");
        Assert.Single(ctx.Diagnostics);
    }

    [Fact]
    public void Save_Restore_position()
    {
        var ctx = new ParserContext(MakeTokens("a", "b", "c"), "test");
        ctx.Advance();
        var saved = ctx.SavePosition();
        ctx.Advance();
        Assert.Equal("c", ctx.GetText());
        ctx.RestorePosition(saved);
        Assert.Equal("b", ctx.GetText());
    }

    [Fact]
    public void IsEof_at_end()
    {
        var ctx = new ParserContext(MakeTokens("a"), "test");
        Assert.False(ctx.IsEof);
        ctx.Advance();
        Assert.True(ctx.IsEof);
    }

    [Fact]
    public void Check_does_not_advance()
    {
        var ctx = new ParserContext(MakeTokens("func"), "test");
        Assert.True(ctx.Check("func"));
        Assert.Equal("func", ctx.GetText());
        Assert.False(ctx.Check("let"));
        Assert.Equal("func", ctx.GetText());
    }

    [Fact]
    public void Peek_looks_ahead()
    {
        var ctx = new ParserContext(MakeTokens("a", "b", "c"), "test");
        Assert.Equal("b", ctx.GetText(ctx.Peek(1)));
        Assert.Equal("c", ctx.GetText(ctx.Peek(2)));
    }

    [Fact]
    public void Peek_returns_eof_when_lookahead_exceeds_token_list_without_explicit_eof()
    {
        var ctx = new ParserContext(MakeTokensWithoutEof("a"), "test");
        Assert.Equal("<eof>", ctx.GetText(ctx.Peek(1)));
    }

    [Fact]
    public void Expect_returns_synthetic_eof_for_previous_token_when_token_list_is_empty()
    {
        var ctx = new ParserContext(MakeTokensWithoutEof(), "test");
        var token = ctx.Expect("b", "expected b");
        Assert.IsType<EofToken>(token);
    }

    private sealed class TestToken(string text) : ContentToken(
        new SourceLocation(0, 0, 0),
        SyntaxKind.None,
        new Terminal(0, text, TerminalFlag.None),
        text.GetOrIntern(), text.Length, text);
}
