using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using Eidosc.Diagnostic;
using Eidosc.Utilities;
using MemoryPack;

namespace Eidosc;

/// <summary>
/// 定义标识符首字符的允许规则（位掩码，可组合）
/// </summary>
[Flags]
public enum IdentifierHead : byte
{
    None = 0,
    Lower = 1 << 0,      // 允许 'a'-'z'
    Upper = 1 << 1,      // 允许 'A'-'Z'
    Underscore = 1 << 2, // 允许 '_'
    
    // 预设组合
    PascalCase = Upper | Underscore, // 例如: MyClass, _Field
    CamelCase = Lower | Underscore,  // 例如: myVar, _field
    AnyLetter = Lower | Upper,       
    All = Lower | Upper | Underscore
}

[MemoryPackable]
public partial class UnicodeIdentifierRule : LexerRule
{
    // 公开字段供序列化，使用 byte 存储节省空间
    public int TerminalId;
    public IdentifierHead HeadPolicy;
    public SyntaxKind Kind;

    // 构造函数
    [MemoryPackConstructor]
    public UnicodeIdentifierRule(int terminalId, IdentifierHead headPolicy = IdentifierHead.All, SyntaxKind kind = default)
    {
        TerminalId = terminalId;
        HeadPolicy = headPolicy;
        Kind = kind;
    }

    // --- 静态优化：SIMD 搜索器 ---
    // 标识符的主体部分（Body）通常允许大小写混合和数字，即使首字母限制了大小写。
    // 因此这里保留全局静态的高效 SearchValues。
    private static readonly SearchValues<char> AsciiIdentifierBodyParts =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    public override IList<char> GetFirsts() => [];

    public override Token? Tokenize(LexerContext context)
    {
        var stream = context.Source;
        if (stream.Eof()) return null;

        string text = stream.Text;
        int pos = stream.PreviewPosition;
        
        // 1. 快速检查首字符 (使用实例配置策略)
        // 此时不检查边界，依靠 try-catch 或者由调用者保证不越界？
        // 通常 Source.Eof() 检查后 text[pos] 是安全的。
        char first = text[pos];
        if (!IsIdentifierStart(first)) return null;

        int startPos = pos;
        pos++; // 消耗首字符

        // 2. 扫描循环 (主体部分逻辑不变，依旧使用 SIMD 加速)
        int maxLen = text.Length;
        while (pos < maxLen)
        {
            // A. SIMD 批量跳过合法 ASCII 字符
            ReadOnlySpan<char> remaining = text.AsSpan(pos);
            int offset = remaining.IndexOfAnyExcept(AsciiIdentifierBodyParts);

            if (offset < 0)
            {
                pos = maxLen;
                break;
            }

            pos += offset;

            // B. 处理非法字符或 Unicode
            char c = text[pos];

            if (c < 128)
            {
                // 是 ASCII 且不在 AllowList 中，必定是分隔符，结束
                break;
            }

            if (IsUnicodeIdentifierPart(c))
            {
                pos++;
                continue;
            }

            break;
        }

        // 3. 生成 Token
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

    // --- 逻辑判断 (由静态改为实例方法) ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsIdentifierStart(char c)
    {
        // 优化路径：ASCII
        if (c < 128)
        {
            // 使用位掩码判断，避免多个 boolean 分支，性能更高
            if (c >= 'a' && c <= 'z') return (HeadPolicy & IdentifierHead.Lower) != 0;
            if (c >= 'A' && c <= 'Z') return (HeadPolicy & IdentifierHead.Upper) != 0;
            if (c == '_')             return (HeadPolicy & IdentifierHead.Underscore) != 0;
            return false;
        }

        // 慢速路径：Unicode
        return IsUnicodeStartSlow(c);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool IsUnicodeStartSlow(char c)
    {
        // 即使是 Unicode，我们也要尝试尊重大小写规则
        // 注意：char.IsLower/IsUpper 对希腊语/西里尔语等也有效
        
        // 1. 检查是否为字母
        bool isLetter = char.IsLetter(c);
        if (!isLetter && char.GetUnicodeCategory(c) != UnicodeCategory.LetterNumber)
        {
            return false;
        }

        // 2. 如果不需要区分大小写（HeadPolicy 包含 AnyLetter），只要是字母就行
        if ((HeadPolicy & IdentifierHead.AnyLetter) == IdentifierHead.AnyLetter)
        {
            return true;
        }

        // 3. 严格检查 Unicode 大小写
        if (char.IsLower(c)) return (HeadPolicy & IdentifierHead.Lower) != 0;
        if (char.IsUpper(c)) return (HeadPolicy & IdentifierHead.Upper) != 0;

        // 如果既不是大写也不是小写（某些无大小写区分的语言字符），默认为允许
        return true; 
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
