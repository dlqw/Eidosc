using System.Buffers;
using System.Runtime.CompilerServices;
using Eidosc.Diagnostic;
using Eidosc.Utils;

// 引用接口

namespace Eidosc;

public class SourceStream : ISourceStream
{
    private readonly int _tabWidth;
    private readonly int _textLength;

    // .NET 9 SIMD 搜索器
    private static readonly SearchValues<char> LineSeparators =
        SearchValues.Create("\n\r\t");

    private SourceLocation _location;

    public SourceStream(string text, int tabWidth, SourceLocation initialLocation = new())
    {
        Text = text ?? string.Empty;
        _textLength = Text.Length;
        _tabWidth = tabWidth <= 0 ? 4 : tabWidth;

        _location = initialLocation;
        PreviewPosition = _location.Position;
    }

    public string Text { get; }

    // [新增] 用于 Grammar 优化的核心属性
    public ReadOnlySpan<char> RemainingSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Text.AsSpan(PreviewPosition);
    }

    public SourceLocation Location
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _location;
        // 允许通过 Location 直接回溯 (自动调用 Reset)
        set => Reset(value);
    }

    public int Position
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _location.Position;
        set
        {
            if (value == _location.Position) return;

            // 禁止通过 int 索引回退 (必须用 Reset)
            if (value < _location.Position)
            {
                throw new InvalidOperationException(DiagnosticMessages.CannotMoveBackInSource(value, _location.Position));
            }

            // 向前快速移动
            SetNewPosition(value);
        }
    }

    public int PreviewPosition { get; set; }

    public char PreviewChar
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (uint)PreviewPosition < (uint)_textLength ? Text[PreviewPosition] : '\0';
    }

    public char NextPreviewChar
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (uint)(PreviewPosition + 1) < (uint)_textLength ? Text[PreviewPosition + 1] : '\0';
    }

    // [新增] 支持任意距离的 Lookahead
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public char PeekChar(int offset)
    {
        int index = PreviewPosition + offset;
        return (uint)index < (uint)_textLength ? Text[index] : '\0';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Eof() => PreviewPosition >= _textLength;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchSymbol(string symbol)
    {
        // 极速比较
        return Text.AsSpan(PreviewPosition).StartsWith(symbol.AsSpan(), StringComparison.Ordinal);
    }

    public string GetPreviewText()
    {
        int start = _location.Position;
        int len = PreviewPosition - start;
        if (len <= 0) return string.Empty;

        // 防御性检查
        if (start + len > _textLength) len = _textLength - start;

        return Text.Substring(start, len);
    }

    public void Reset(SourceLocation location)
    {
        // 1. 恢复物理位置
        _location = location;

        // 2. 强制同步预览游标
        PreviewPosition = location.Position;
    }

    public override string ToString()
    {
        int p = Location.Position;
        if (p >= _textLength)
        {
            return string.Format("MsgSrcPosToString LabelEofMark {0}", Location);
        }

        int len = Math.Min(20, _textLength - p);
        ReadOnlySpan<char> snippet = Text.AsSpan(p, len);

        string display = len == 20
            ? string.Concat(snippet, "LabelSrcHaveMore")
            : snippet.ToString();

        return string.Format("MsgSrcPosToString {0} {1}", display, Location);
    }


    private void SetNewPosition(int newPosition)
    {
        int currentPos = _location.Position;
        int line = _location.Line;
        int col = _location.Column;
        string text = Text;
        int maxLen = _textLength;

        while (currentPos < newPosition)
        {
            ReadOnlySpan<char> remaining = text.AsSpan(currentPos, newPosition - currentPos);

            int offset = remaining.IndexOfAny(LineSeparators);

            if (offset < 0)
            {
                col += remaining.Length;
                currentPos += remaining.Length;
                break;
            }

            col += offset;
            currentPos += offset;

            char c = text[currentPos];
            if (c == '\n')
            {
                line++;
                col = 0;
            }
            else if (c == '\r')
            {
                if (currentPos + 1 < maxLen && text[currentPos + 1] == '\n')
                {
                    if (currentPos + 1 < newPosition)
                    {
                        currentPos++;
                    }
                }

                line++;
                col = 0;
            }
            else if (c == '\t')
            {
                col = (col / _tabWidth + 1) * _tabWidth;
            }

            currentPos++;
        }

        _location = new SourceLocation(currentPos, line, col, _location.FilePath);

        if (PreviewPosition < currentPos)
        {
            PreviewPosition = currentPos;
        }
    }
}
