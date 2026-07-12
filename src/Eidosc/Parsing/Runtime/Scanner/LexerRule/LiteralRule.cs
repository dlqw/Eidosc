using MemoryPack;

namespace Eidosc;

/// <summary>
/// 字面量规则基类：负责处理数字、字符串等产生“值”的 Token
/// </summary>
[MemoryPackable]
[MemoryPackUnion(0, typeof(StringLiteralRule))]
[MemoryPackUnion(1, typeof(NumberLiteralRule))]
public abstract partial class LiteralRule : LexerRule
{
    public int TerminalId { get; protected set; }

    protected LiteralRule(int terminalId)
    {
        TerminalId = terminalId;
    }

    public override void SetTerminalId(int terminalId) => TerminalId = terminalId;

    /// <summary>
    /// 解析过程中的瞬态上下文 (ref struct 保证零分配)
    /// </summary>
    protected ref struct LiteralContext
    {
        public ReadOnlySpan<char> BodySpan;
        public object? ResultValue;
        public LexerErrorCode Error;

        // 数字专用状态
        public TypeCode TargetType;
        public int Base; // 10, 16, 8, 2

        // 字符串/通用状态
        public bool IsCharMode; // 是否是字符 'a' 而非字符串 "a"
    }

    protected static string GetErrorMessage(LexerErrorCode code) => code switch
    {
        LexerErrorCode.InvalidNumber => "ErrInvNumber",
        LexerErrorCode.BadStringLiteral => "ErrBadStrLiteral",
        LexerErrorCode.BadEscape => "ErrInvEscape",
        LexerErrorCode.BadChar => "ErrBadChar",
        LexerErrorCode.UnexpectedEof => Diagnostic.DiagnosticMessages.UnexpectedEndOfFile,
        _ => Diagnostic.DiagnosticMessages.InvalidToken
    };
}
