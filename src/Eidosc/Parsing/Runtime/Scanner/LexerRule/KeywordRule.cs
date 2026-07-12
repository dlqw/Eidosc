using Eidosc.Utilities;
using MemoryPack;

namespace Eidosc;

[MemoryPackable]
public partial class KeywordRule(string text, int terminalId, SyntaxKind kind) : LexerRule
{
    public int TerminalId = terminalId;
    public SyntaxKind Kind = kind;
    public string Text { get; } = text;


    public override IList<char> GetFirsts()
    {
        return [Text.First()];
    }

    public override Token? Tokenize(LexerContext context)
    {
        var stream = context.Source;

        if (!stream.MatchSymbol(Text))
            return null;

        stream.PreviewPosition += Text.Length;

        var terminal = context.Terminals[TerminalId];

        if (terminal.Flags.HasFlag(TerminalFlag.IsKeyword))
        {
            // 修复：仅当关键字本身以字母、数字或下划线结尾时，才检查边界。
            // 这使得像 WellKnownStrings.Punctuation.Dot, WellKnownStrings.Punctuation.DotDot, WellKnownStrings.Punctuation.FatArrow, "@" 这样的符号关键字可以紧跟标识符（如 .map 或 @attr）。
            var lastChar = Text[^1];
            if (char.IsLetterOrDigit(lastChar) || lastChar == '_')
            {
                var previewChar = stream.PreviewChar;

                if (char.IsLetterOrDigit(previewChar) || previewChar == '_')
                {
                    stream.PreviewPosition -= Text.Length;
                    return null;
                }
            }
        }

        var token = Token.CreateContentToken(stream, Kind, terminal, Text.AsSpan().GetOrIntern());
        return token;
    }

    public override void SetTerminalId(int terminalId)
    {
        TerminalId = terminalId;
    }
}