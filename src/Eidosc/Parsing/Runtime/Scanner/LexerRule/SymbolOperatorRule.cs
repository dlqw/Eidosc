using Eidosc.Utilities;
using MemoryPack;

namespace Eidosc;

/// <summary>
/// Tokenizes user-defined symbolic operator identifiers without consuming reserved syntax tokens.
/// </summary>
[MemoryPackable]
public partial class SymbolOperatorRule : LexerRule
{
    private static readonly char[] OperatorFirstChars =
    [
        '!', '$', '%', '&', '*', '+', '/', '<', '=', '>', '?', '^', '|', '-', '~', ':'
    ];

    private static readonly HashSet<string> ReservedOperatorTokens =
    [
        "->", "=>", ":=", "=", ":", "::", "::*", "|", "&", "+", "++", "+:", ":+", "-", "*", "/", "%", "==", "!=",
        "<", ">", "<=", ">=", "&&", "||", "<-", "!", "|>", ">>=", ">>>", "<<<", "<$>", "<*>",
        "<>", "?", "??"
    ];

    public int TerminalId;
    public SyntaxKind Kind;

    [MemoryPackConstructor]
    public SymbolOperatorRule(int terminalId, SyntaxKind kind = default)
    {
        TerminalId = terminalId;
        Kind = kind;
    }

    public override IList<char> GetFirsts() => OperatorFirstChars;

    public override Token? Tokenize(LexerContext context)
    {
        var stream = context.Source;
        if (stream.Eof())
        {
            return null;
        }

        var text = stream.Text;
        var start = stream.PreviewPosition;
        var pos = start;

        while (pos < text.Length && IsOperatorChar(text[pos]))
        {
            pos++;
        }

        if (pos == start)
        {
            return null;
        }

        var operatorText = text.AsSpan(start, pos - start).ToString();
        if (ReservedOperatorTokens.Contains(operatorText) ||
            StartsWithReservedQuestionOperator(operatorText))
        {
            return null;
        }

        stream.PreviewPosition = pos;
        return Token.CreateContentToken(stream, Kind, context.Terminals[TerminalId], operatorText.AsSpan().GetOrIntern());
    }

    public override void SetTerminalId(int terminalId)
    {
        TerminalId = terminalId;
    }

    private static bool IsOperatorChar(char c)
    {
        return c is '!' or '$' or '%' or '&' or '*' or '+' or '/' or '<' or '=' or '>' or '?' or '^' or '|' or '-' or '~' or ':';
    }

    private static bool StartsWithReservedQuestionOperator(string text)
    {
        return text.Length > 1 && text[0] == '?';
    }
}
