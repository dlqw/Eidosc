using System.Buffers;
using System.Text;
using Eidosc.Diagnostic;
using Eidosc.Utilities;
using Eidosc.Utils;
using MemoryPack;

namespace Eidosc;

[MemoryPackable]
public partial class StringLiteralRule : LiteralRule
{
    public List<StringStyle> Styles { get; private set; }
    public SyntaxKind Kind;

    // 转义表 (全局共享)
    public Dictionary<char, char> EscapeMap { get; set; } = new()
    {
        { 'n', '\n' }, { 'r', '\r' }, { 't', '\t' }, { '\\', '\\' }, { '0', '\0' },
        { '"', '"' }, { '\'', '\'' }, { 'a', '\a' }, { 'b', '\b' }, { 'v', '\v' }, { 'f', '\f' }
    };

    private readonly char[] _firstsCache;
    private readonly StyleRuntime[] _runtimes;
    private static readonly StringId StringTypeId = "[string]".GetOrIntern();

    [MemoryPackConstructor]
    public StringLiteralRule(int terminalId, List<StringStyle> styles, SyntaxKind kind = default) : base(terminalId)
    {
        Styles = styles;
        Kind = kind;
        // 按前缀长度倒序排序，确保 """ 优先于 " 匹配
        Styles.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));

        var firsts = new HashSet<char>();
        _runtimes = new StyleRuntime[Styles.Count];

        for (int i = 0; i < Styles.Count; i++)
        {
            var s = Styles[i];
            if (!string.IsNullOrEmpty(s.Prefix)) firsts.Add(s.Prefix[0]);
            _runtimes[i] = new StyleRuntime(s);
        }

        _firstsCache = [.. firsts];
    }

    public override IList<char> GetFirsts() => _firstsCache;

    public override Token? Tokenize(LexerContext context)
    {
        var stream = context.Source;
        if (stream.Eof()) return null;

        LiteralContext ctx = default;

        // 1. 尝试匹配任意一种风格的前缀
        for (int i = 0; i < Styles.Count; i++)
        {
            var style = Styles[i];
            if (stream.MatchSymbol(style.Prefix))
            {
                // 命中风格
                ctx.IsCharMode = style.IsChar;
                stream.PreviewPosition += style.Prefix.Length; // 消耗前缀

                // 2. 读取主体
                if (ReadStringBody(stream, ref ctx, in _runtimes[i], style))
                {
                    if (ctx.Error != LexerErrorCode.None)
                        return Token.CreateErrorToken(stream, GetErrorMessage(ctx.Error));

                    // 传递 context 以便 ConvertString 报告未知转义警告
                    if (!ConvertString(ref ctx, style, context))
                        return Token.CreateErrorToken(stream, GetErrorMessage(ctx.Error));

                    return Token.CreateContentToken(stream, Kind, context.Terminals[TerminalId], ctx.ResultValue);
                }

                // 读取失败，回滚? 或者返回错误
                return Token.CreateErrorToken(stream, GetErrorMessage(LexerErrorCode.UnexpectedEof));
            }
        }

        return null;
    }

    private bool ReadStringBody(ISourceStream stream, ref LiteralContext ctx, in StyleRuntime runtime, StringStyle style)
    {
        string text = stream.Text;
        int startPos = stream.PreviewPosition; // 内容开始处
        int pos = startPos;
        int maxLen = text.Length;

        ReadOnlySpan<char> textSpan = text.AsSpan();
        ReadOnlySpan<char> endSpan = style.Suffix.AsSpan();
        char endFirst = endSpan.IsEmpty ? '\0' : endSpan[0];

        while (pos < maxLen)
        {
            // SIMD 跳过安全字符
            var remaining = textSpan[pos..];
            int offset = remaining.IndexOfAny(runtime.StopChars);

            if (offset < 0)
            {
                // 没找到结束符，也没找到转义符，直接到了 EOF -> 错误
                ctx.Error = LexerErrorCode.UnexpectedEof;
                stream.PreviewPosition = maxLen;
                return true;
            }

            pos += offset;
            char c = textSpan[pos];

            // A. 换行检查
            if (!style.MultiLine && CodePoints.IsNewLine(c))
            {
                ctx.Error = LexerErrorCode.BadStringLiteral;
                stream.PreviewPosition = pos; 
                return true;
            }

            // B. 结束符检查
            if (c == endFirst)
            {
                // 检查完整后缀
                if (textSpan[pos..].StartsWith(endSpan))
                {
                    // 检查是否是双倍转义 (例如 SQL 的 '')
                    if (style.EscapeByDoubling)
                    {
                        // 检查后面是否紧跟着又一个 Suffix
                        int nextPos = pos + endSpan.Length;
                        if (textSpan[nextPos..].StartsWith(endSpan))
                        {
                            pos += endSpan.Length * 2; // 跳过两个
                            continue;
                        }
                    }

                    // 真正的结束
                    ctx.BodySpan = textSpan[startPos..pos];
                    stream.PreviewPosition = pos + endSpan.Length; // 消耗后缀

                    // 解析值
                    return ConvertString(ref ctx, style);
                }
            }

            // C. 转义符检查 (\)
            if (c == '\\' && style.AllowEscape)
            {
                pos++; // 跳过 \
                if (pos < maxLen) pos++; // 跳过被转义的字符
                continue;
            }

            // D. 如果都不是 (可能是 StopChars 里的其他字符命中，或者 EndFirst 命中但后面不匹配)
            pos++;
        }

        ctx.Error = LexerErrorCode.UnexpectedEof;
        return true;
    }

    private bool ConvertString(ref LiteralContext ctx, StringStyle style, LexerContext? context = null)
    {
        var span = ctx.BodySpan;

        // 快速路径：无转义直接取值
        if (!style.AllowEscape && !style.EscapeByDoubling)
        {
            return SetResult(ref ctx, span.ToString(), style.IsChar);
        }

        // 慢速路径：StringBuilder 构建
        var sb = new StringBuilder(span.Length);
        ReadOnlySpan<char> suffix = style.Suffix.AsSpan();

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];

            // 处理反斜杠转义
            if (c == '\\' && style.AllowEscape)
            {
                if (i + 1 >= span.Length) break; // 悬空转义，忽略
                char next = span[i + 1];
                if (EscapeMap.TryGetValue(next, out char escaped))
                {
                    sb.Append(escaped);
                    i++;
                }
                else
                {
                    // 未知转义序列：保留原样并发出警告
                    sb.Append(c);
                    if (context != null)
                    {
                        context.Report(Diagnostic.Diagnostic.Warning(
                            DiagnosticMessages.UnknownEscapeSequence(next),
                            "W4001"));
                    }
                }

                continue;
            }

            // 处理双倍转义
            if (style.EscapeByDoubling && c == suffix[0])
            {
                // 既然能进到 BodySpan，说明之前解析器已经确认过这是双倍转义
                // 这里我们只需要检查是否匹配并跳过一个
                if (span[i..].StartsWith(suffix))
                {
                    // 追加一个 suffix 内容
                    sb.Append(suffix);
                    i += suffix.Length * 2 - 1;
                    continue;
                }
            }

            sb.Append(c);
        }

        return SetResult(ref ctx, sb.ToString(), style.IsChar);
    }

    private static bool SetResult(ref LiteralContext ctx, string val, bool isChar)
    {
        if (isChar)
        {
            if (val.Length != 1)
            {
                ctx.Error = LexerErrorCode.BadChar;
                return true;
            }

            ctx.ResultValue = val[0];
        }
        else
        {
            ctx.ResultValue = val;
        }

        return true;
    }

    // --- 内部辅助结构 ---

    /// <summary>
    /// 预计算的运行时查找表
    /// </summary>
    private readonly struct StyleRuntime
    {
        public readonly SearchValues<char> StopChars;

        public StyleRuntime(StringStyle style)
        {
            var stops = new HashSet<char>();
            if (!string.IsNullOrEmpty(style.Suffix)) stops.Add(style.Suffix[0]);

            if (!style.MultiLine)
            {
                stops.Add('\n');
                stops.Add('\r');
                stops.Add('\f');
                stops.Add('\u0085'); // Next Line
                stops.Add('\u2028'); // Line Separator
                stops.Add('\u2029'); // Paragraph Separator
            }

            if (style.AllowEscape) stops.Add('\\');

            StopChars = SearchValues.Create([.. stops]);
        }
    }
}

/// <summary>
/// 字符串风格配置
/// </summary>
[MemoryPackable]
public partial struct StringStyle
{
    public string Prefix; // ", ', @", """
    public string Suffix; // ", ', """
    public bool AllowEscape; // 是否允许 \n, \t
    public bool EscapeByDoubling; // 是否允许 "" 转义为 "
    public bool MultiLine; // 是否允许换行
    public bool IsChar; // 结果是否为 char
}
