using System.Runtime.CompilerServices;
using Eidosc.Utils;
using MemoryPack;

namespace Eidosc;

[MemoryPackable]
public partial class BooleanMatchRule : LexerRule
{
    public int TerminalId;
    public bool CaseSensitive; // 新增：是否大小写敏感
    public SyntaxKind Kind;

    // 缓存 Firsts
    private readonly char[] _firsts;

    [MemoryPackConstructor]
    public BooleanMatchRule(int terminalId, bool caseSensitive = true, SyntaxKind kind = default)
    {
        TerminalId = terminalId;
        CaseSensitive = caseSensitive;
        Kind = kind;

        // 如果不区分大小写，首字符列表需要包含 T/F
        _firsts = caseSensitive ? ['t', 'f'] : ['t', 'f', 'T', 'F'];
    }

    public override IList<char> GetFirsts() => _firsts;

    public override Token? Tokenize(LexerContext context)
    {
        var stream = context.Source;
        if (stream.Eof()) return null;

        // 获取当前位置的字符以便快速分支
        char first = stream.PreviewChar;

        // ---------------------------------------------------------
        // 匹配 "true"
        // ---------------------------------------------------------
        if (IsChar(first, 't'))
        {
            // 检查剩余长度是否足够 & 快速比较
            if (MatchString(stream, "true"))
            {
                // [关键修复] 边界检查：确保 "true" 后面不是字母或数字
                // 例如防止匹配 "trueValue"
                if (!IsIdentifierPart(stream.PeekChar(4)))
                {
                    stream.PreviewPosition += 4;
                    return Token.CreateContentToken(stream, Kind, context.Terminals[TerminalId], true);
                }
            }
        }

        // ---------------------------------------------------------
        // 匹配 "false"
        // ---------------------------------------------------------
        else if (IsChar(first, 'f'))
        {
            if (MatchString(stream, "false"))
            {
                // [关键修复] 边界检查
                if (!IsIdentifierPart(stream.PeekChar(5)))
                {
                    // [Bug 修复] 长度改为 5
                    stream.PreviewPosition += 5;
                    return Token.CreateContentToken(stream, Kind, context.Terminals[TerminalId], false);
                }
            }
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsChar(char input, char targetLower)
    {
        if (CaseSensitive) return input == targetLower;
        // 简单的 ASCII 大小写不敏感比较
        return input == targetLower || input == char.ToUpperInvariant(targetLower);
    }

    private bool MatchString(ISourceStream stream, string targetLower)
    {
        // 如果区分大小写，直接利用 stream 现有的优化方法
        if (CaseSensitive) return stream.MatchSymbol(targetLower);

        // 如果不区分大小写，需要手动比较
        // 注意：RemainingSpan 是 Slice，访问开销极低
        var span = stream.RemainingSpan;
        if (span.Length < targetLower.Length) return false;

        return span.Slice(0, targetLower.Length)
           .Equals(targetLower, StringComparison.OrdinalIgnoreCase);
    }

    // 简单的边界检查辅助方法
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdentifierPart(char c)
    {
        // 允许后续是空格、符号、EOF(\0)，但不能是字母、数字或下划线
        return char.IsLetterOrDigit(c) || c == '_';
    }

    public override void SetTerminalId(int terminalId)
    {
        TerminalId = terminalId;
    }
}