using MemoryPack;

namespace Eidosc;

public enum LexerErrorCode
{
    None = 0,
    InvalidNumber,
    BadStringLiteral,
    BadEscape,
    BadChar,
    CannotConvert,
    UnexpectedEof
}

[MemoryPackable]
[MemoryPackUnion(0, typeof(UnicodeIdentifierRule))]
[MemoryPackUnion(1, typeof(KeywordRule))]
[MemoryPackUnion(2, typeof(CommentMatchRule))]
[MemoryPackUnion(3, typeof(LiteralRule))] // 抽象基类
[MemoryPackUnion(4, typeof(StringLiteralRule))]
[MemoryPackUnion(5, typeof(BooleanMatchRule))] // 假设你还有这个，或者合并进 Keyword
[MemoryPackUnion(6, typeof(NumberLiteralRule))]
[MemoryPackUnion(7, typeof(SymbolOperatorRule))]
public abstract partial class LexerRule
{
    public abstract IList<char> GetFirsts();
    public abstract Token? Tokenize(LexerContext context);
    public virtual void SetTerminalId(int terminalId) { }
}
