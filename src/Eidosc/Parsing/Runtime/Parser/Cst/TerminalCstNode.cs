using Eidosc.Utils;

namespace Eidosc;

public class TerminalCstNode : ConcreteSyntaxNode
{
    public readonly Token Token;
    public Terminal? Terminal;

    public TerminalCstNode(Token token)
    {
        Token = token;

        if (Token is ContentToken contentToken)
        {
            Terminal = contentToken.Terminal;
            Span = new SourceSpan(token.Location, contentToken.Length);
        }
    }

    public override string ToString()
    {
        return $"Terminal:{Terminal} Token:{Token}";
    }

    protected override bool ShouldBePruned()
    {
        if (Terminal is null) return true;
        return Terminal.HasFlag(TerminalFlag.IsPunctuation);
    }

    protected override bool ShouldBeUnpacked()
    {
        return false;
    }
}