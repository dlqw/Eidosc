using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Parsing.Handwritten;

/// <summary>
/// Token 类型判断辅助方法。
/// 所有匹配均基于 ContentToken.Kind (SyntaxKind 枚举)，零字符串比较。
/// </summary>
public static class TokenKind
{
    public static bool IsKeyword(Token token, string keyword)
        => token is ContentToken ct && GetText(ct) == keyword;

    public static bool IsAnyKeyword(Token token)
        => token is ContentToken { Kind: var k } && k.IsKeyword();

    public static bool IsIdentifier(Token token)
        => token is ContentToken { Kind: SyntaxKind.Identifier } || IsContextualLowerIdentifier(token);

    public static bool IsAnyIdentifier(Token token)
        => IsIdentifier(token);

    public static bool IsOperatorIdentifier(Token token)
        => token is ContentToken { Kind: SyntaxKind.OperatorIdentifier };

    public static bool IsNumber(Token token)
        => token is ContentToken { Kind: SyntaxKind.NumberLiteral };

    public static bool IsString(Token token)
        => token is ContentToken { Kind: SyntaxKind.StringLiteral };

    public static bool IsChar(Token token)
        => token is ContentToken { Kind: SyntaxKind.CharLiteral };

    public static bool IsBoolean(Token token)
        => token is ContentToken { Kind: SyntaxKind.BooleanLiteral };

    public static bool IsAnyLiteral(Token token)
        => token is ContentToken { Kind: var k } && k.IsLiteral();

    public static bool IsOperator(Token token, string op)
        => token is ContentToken ct && GetText(ct) == op;

    public static bool IsPunctuation(Token token, string punc)
        => token is ContentToken ct && GetText(ct) == punc;

    public static bool IsEof(Token token) => token is EofToken;

    public static bool IsError(Token token) => token is ErrorToken;

    private static bool IsContextualLowerIdentifier(Token token)
        => token is ContentToken contentToken &&
           string.Equals(GetText(contentToken), WellKnownStrings.Keywords.By, StringComparison.Ordinal);

    private static string GetText(ContentToken ct)
    {
        if (ct.Kind is SyntaxKind.StringLiteral or SyntaxKind.CharLiteral ||
            ct.Terminal.DebugName is "stringLiteral" or "charLiteral")
        {
            return ct.TextId.Resolve();
        }

        return ct.Value switch
        {
            string text => text,
            StringId id => id.Resolve(),
            _ => ct.TextId.Resolve()
        };
    }
}
