using Eidosc.Utils;

namespace Eidosc;

public abstract class TokenFilter
{
    public virtual IEnumerable<Token> Filter(LexerContext context, IEnumerable<Token> tokens)
    {
        yield break;
    }

    public virtual void Reset()
    {
    }

    protected internal virtual void OnSetSourceLocation(SourceLocation location)
    {
    }
}