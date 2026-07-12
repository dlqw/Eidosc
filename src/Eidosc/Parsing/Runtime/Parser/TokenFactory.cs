using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc;

public partial class Token
{
    public static CommentToken CreateCommentToken(ISourceStream stream)
    {
        return new CommentToken(stream.Location, stream.GetPreviewText());
    }

    public static ErrorToken CreateErrorToken(ISourceStream stream, string message)
    {
        return new ErrorToken(stream.Location, message);
    }

    public static EofToken CreateEofToken(ISourceStream stream)
    {
        return new EofToken(stream.Location);
    }

    public static ContentToken CreateContentToken(ISourceStream stream, SyntaxKind kind, Terminal terminal, object? value = null)
    {
        var id = stream.GetPreviewText().GetOrIntern();
        return new ContentToken(stream.Location, kind, terminal, id, id.Length, value);
    }

    public static ContentToken CreateContentTokenString(ISourceStream stream, SyntaxKind kind, Terminal terminal, StringId specId)
    {
        var value = stream.GetPreviewText();
        return new ContentToken(stream.Location, kind, terminal, specId, value.Length, value);
    }
}
