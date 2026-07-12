using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc;

public class ErrorToken(SourceLocation location, string message) : Token(location, 0)
{
    public readonly string Message = message;
    
    public override string ToString() => $"ErrorToken({Message})";
}

public class CommentToken(SourceLocation location, string comment) : Token(location, comment.Length)
{
    public string Comment = comment;
}

public class EofToken(SourceLocation location) : Token(location, 0);


public class ContentToken(SourceLocation location, SyntaxKind kind, Terminal terminal, StringId textId, int length, object? value = null)
    : Token(location, length)
{
    public SyntaxKind Kind { get; } = kind;
    public Terminal Terminal { get; set; } = terminal;
    public readonly StringId TextId = textId;
    public readonly object? Value = value;

    public override string ToString()
    {
        if(Value is StringId stringId) return stringId.Resolve();
        return Value?.ToString() ?? "";
    }
}

public abstract partial class Token(SourceLocation location, int length)
{
    public readonly SourceLocation Location = location;
    public readonly int Length = length;
    public bool IsLexerOnly = false;
}