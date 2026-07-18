using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using Eidosc.Diagnostic;
using Eidosc.Utilities;
using MemoryPack;

namespace Eidosc;

[MemoryPackable]
public partial class UnicodeIdentifierRule : LexerRule
{
    public int TerminalId;
    public SyntaxKind Kind;

    [MemoryPackConstructor]
    public UnicodeIdentifierRule(int terminalId, SyntaxKind kind = default)
    {
        TerminalId = terminalId;
        Kind = kind;
    }

    private static readonly SearchValues<char> AsciiIdentifierBodyParts =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    public override IList<char> GetFirsts() => [];

    public override Token? Tokenize(LexerContext context)
    {
        var stream = context.Source;
        if (stream.Eof()) return null;

        string text = stream.Text;
        int pos = stream.PreviewPosition;
        
        char first = text[pos];
        if (!IsIdentifierStart(first)) return null;

        int startPos = pos;
        pos++; // 消耗首字符

        int maxLen = text.Length;
        while (pos < maxLen)
        {
            ReadOnlySpan<char> remaining = text.AsSpan(pos);
            int offset = remaining.IndexOfAnyExcept(AsciiIdentifierBodyParts);

            if (offset < 0)
            {
                pos = maxLen;
                break;
            }

            pos += offset;

            char c = text[pos];

            if (c < 128)
            {
                break;
            }

            if (IsUnicodeIdentifierPart(c))
            {
                pos++;
                continue;
            }

            break;
        }

        stream.PreviewPosition = pos;
        int length = pos - startPos;

        // 标识符长度硬限制：超过 1024 字符截断并报错
        const int MaxIdentifierLength = 1024;
        if (length > MaxIdentifierLength)
        {
            context.Report(Diagnostic.Diagnostic.Error(
                DiagnosticMessages.IdentifierLengthExceeded(MaxIdentifierLength, length),
                "E4001"));
            length = MaxIdentifierLength;
            pos = startPos + MaxIdentifierLength;
            stream.PreviewPosition = pos;
        }

        var body = text.AsSpan(startPos, length).GetOrIntern();
        return Token.CreateContentToken(stream, Kind, context.Terminals[TerminalId], body);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentifierStart(char c)
    {
        if (c < 128)
        {
            return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';
        }

        return IsUnicodeStartSlow(c);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsUnicodeStartSlow(char c)
    {
        return char.IsLetter(c) || char.GetUnicodeCategory(c) == UnicodeCategory.LetterNumber;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsUnicodeIdentifierPart(char c)
    {
        if (char.IsLetterOrDigit(c)) return true;

        var cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.Format;
    }

    public override void SetTerminalId(int terminalId)
    {
        TerminalId = terminalId;
    }
}
