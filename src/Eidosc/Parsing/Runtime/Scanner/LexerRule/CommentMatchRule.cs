using System.Buffers;
using Eidosc.Utils;
using MemoryPack;

// 引用 CodePoints 所在的命名空间

namespace Eidosc;

[MemoryPackable]
public partial class CommentMatchRule : LexerRule
{
    public readonly string StartSymbol;
    public readonly string[] EndSymbols;

    private readonly char[] _firstsCache;

    // 标记：0 = LineComment, 1 = SingleBlock, 2 = MultiBlock
    private readonly byte _mode;

    // 针对单符号优化的缓存 
    private readonly int _singleEndLength;
    
    // SearchValues 不可序列化，但通过构造函数重建完全没问题
    private readonly SearchValues<string> _endSearchValues = null!;
    private readonly SearchValues<char> _newLineChars = null!;

    [MemoryPackConstructor]
    public CommentMatchRule(string startSymbol, string[] endSymbols)
    {
        StartSymbol = startSymbol;
        
        // [优化 1] 如果是多符号模式，按长度倒序排序，确保匹配最长符号
        // 例如：防止在 "ending" 中匹配到 "end" 而截断
        if (endSymbols.Length > 1)
        {
            // 创建副本以免修改传入的原始数组引用（如果需要保持原始顺序则不做副本）
            // 这里假设可以修改，或者为了安全起见：
            EndSymbols = [..endSymbols.OrderByDescending(s => s.Length)];
        }
        else
        {
            EndSymbols = endSymbols;
        }

        _firstsCache = [StartSymbol[0]];

        switch (EndSymbols.Length)
        {
            case 0:
                // --- 模式 0: 行注释 ---
                _mode = 0;
                // [优化 2] 使用 CodePoints 中的定义，保持全项目一致
                // 如果没有 CodePoints 类，保持你原来的写法 "\n\r\f\u0085\u2028\u2029" 也是对的
                _newLineChars = SearchValues.Create("\n\r\f\u0085\u2028\u2029");
                break;
            case 1:
                // --- 模式 1: 单符号块注释 (如 "*/") ---
                _mode = 1;
                _singleEndLength = EndSymbols[0].Length;
                _endSearchValues = SearchValues.Create(EndSymbols, StringComparison.Ordinal);
                break;
            default:
                // --- 模式 2: 兼容多符号块注释 ---
                _mode = 2;
                _endSearchValues = SearchValues.Create(EndSymbols, StringComparison.Ordinal);
                break;
        }
    }

    public override IList<char> GetFirsts() => _firstsCache;

    public override Token? Tokenize(LexerContext context)
    {
        var stream = context.Source;
        
        // 快速检查首字符
        if (stream.PreviewChar != StartSymbol[0]) return null;
        
        // 检查完整的开始符号
        if (!MatchLeft(stream)) return null;

        bool foundEnd = MatchRight(stream);

        // 如果是行注释(_mode == 0)，遇到 EOF 也是合法的
        // 如果是块注释，foundEnd 必须为 true
        if (foundEnd || (_mode == 0 && stream.Eof()))
        {
            return Token.CreateCommentToken(stream);
        }

        return Token.CreateErrorToken(stream, "ErrUnclosedComment");
    }

    private bool MatchLeft(ISourceStream stream)
    {
        if (!stream.MatchSymbol(StartSymbol)) return false;
        stream.PreviewPosition += StartSymbol.Length;
        return true;
    }

    private bool MatchRight(ISourceStream stream)
    {
        string text = stream.Text;
        int pos = stream.PreviewPosition;
        int maxLen = text.Length;
        
        ReadOnlySpan<char> remaining = text.AsSpan(pos);
        int offset;

        // 1. 使用 SIMD 查找最近的结束特征
        if (_mode == 0)
        {
            offset = remaining.IndexOfAny(_newLineChars);
        }
        else
        {
            // SearchValues<string> 查找的是任意一个结束符的起始位置
            offset = remaining.IndexOfAny(_endSearchValues);
        }

        if (offset < 0)
        {
            // 没找到结束符，直接跳到 EOF
            stream.PreviewPosition = maxLen;
            return false; // 行注释会在 Tokenize 中被修正为 True
        }

        // 2. 移动到匹配位置
        pos += offset;
        stream.PreviewPosition = pos;

        // 3. 确认并消耗结束符
        switch (_mode)
        {
            case 0:
                // 行注释：停在换行符之前，不消耗换行符
                // 换行符将由后续的 Scanner 循环作为 Whitespace 跳过
                return true;
                
            case 1:
                // 单符号块注释：消耗结束符
                stream.PreviewPosition += _singleEndLength;
                return true;
                
            default: // case 2
            {
                // 多符号检查：因为 SearchValues 告诉我们这里有匹配，
                // 但没告诉我们匹配的是哪个（尤其是包含关系时），我们需要确认。
                // 由于构造函数中已按长度排序，这里找到的第一个匹配即为最长匹配。
                foreach (var symbol in EndSymbols)
                {
                    // 检查 text[pos] 是否以 symbol 开头
                    if (string.CompareOrdinal(text, pos, symbol, 0, symbol.Length) == 0)
                    {
                        stream.PreviewPosition += symbol.Length;
                        return true;
                    }
                }
                
                // 理论上 IndexOfAny 命中后不应该进这里，除非并发修改了 text
                return false;
            }
        }
    }
}