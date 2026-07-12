using System.Xml;
using System.Text;
using Eidosc.Utils;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 字面量表达式
/// </summary>
/// <example>
/// 42
/// 3.14
/// "hello"
/// 'c'
/// true
/// 0xFF
/// 0b1010
/// </example>
public record LiteralExpr : Expression
{
    /// <summary>
    /// 字面量值
    /// </summary>
    public object? Value { get; private set; }

    /// <summary>
    /// 字面量类型
    /// </summary>
    public LiteralKind Kind { get; private set; }

    /// <summary>
    /// 原始文本表示
    /// </summary>
    public string RawText { get; private set; } = "";

    /// <summary>
    /// Indicates that this literal was synthesized only to keep parsing after an invalid expression.
    /// </summary>
    public bool IsRecoveredError { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    RawText = GetTokenText(term);
                    Value = ParseLiteral(RawText, out var kind);
                    Kind = kind;
                }
            }
        }
    }

    /// <summary>
    /// 设置 span（用于从终端节点创建）
    /// </summary>
    public void SetSpan(SourceSpan span) => Span = span;

    /// <summary>
    /// 设置字面量值（用于从终端节点创建）
    /// </summary>
    public void SetLiteral(string rawText)
    {
        RawText = rawText;
        Value = ParseLiteral(rawText, out var kind);
        Kind = kind;
    }

    public void MarkRecoveredError(string recoveryReason = AstRecoveryReasons.ParserRecoveredLiteral)
    {
        IsRecoveredError = true;
        MarkRecovered(recoveryReason);
    }

    private static object? ParseLiteral(string text, out LiteralKind kind)
    {
        if (text == "()")
        {
            kind = LiteralKind.Unit;
            return text;
        }

        // 十六进制
        if (text.StartsWith("0x") || text.StartsWith("0X"))
        {
            kind = LiteralKind.Integer;
            if (int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexVal))
            {
                return hexVal;
            }
            return 0;
        }

        // 二进制
        if (text.StartsWith("0b") || text.StartsWith("0B"))
        {
            kind = LiteralKind.Integer;
            try
            {
                return Convert.ToInt32(text[2..], 2);
            }
            catch
            {
                return 0;
            }
        }

        // 八进制
        if (text.StartsWith("0o") || text.StartsWith("0O"))
        {
            kind = LiteralKind.Integer;
            try
            {
                return Convert.ToInt32(text[2..], 8);
            }
            catch
            {
                return 0;
            }
        }

        // 布尔值
        if (text == WellKnownStrings.AdditionalKeywords.True)
        {
            kind = LiteralKind.Boolean;
            return true;
        }
        if (text == WellKnownStrings.AdditionalKeywords.False)
        {
            kind = LiteralKind.Boolean;
            return false;
        }

        // 字符串
        if (text.StartsWith("\""))
        {
            kind = LiteralKind.String;
            return ParseStringLiteral(text);
        }

        // 字符
        if (text.StartsWith("'"))
        {
            kind = LiteralKind.Char;
            return ParseCharLiteral(text);
        }

        // 浮点数
        if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
        {
            kind = LiteralKind.Float;
            if (double.TryParse(text, out var doubleVal))
            {
                return doubleVal;
            }
            return 0.0;
        }

        // 整数
        if (int.TryParse(text, out var intVal))
        {
            kind = LiteralKind.Integer;
            return intVal;
        }
        if (long.TryParse(text, out var longVal))
        {
            kind = LiteralKind.Integer;
            return longVal;
        }

        kind = LiteralKind.String;
        return text;
    }

    private static string ParseStringLiteral(string text)
    {
        var content = StripOuterQuote(text, '"');
        return UnescapeLiteralContent(content);
    }

    private static char ParseCharLiteral(string text)
    {
        var content = StripOuterQuote(text, '\'');
        var unescaped = UnescapeLiteralContent(content);
        return unescaped.Length > 0 ? unescaped[0] : '\0';
    }

    private static string StripOuterQuote(string text, char quote)
    {
        if (text.Length >= 4 &&
            text[0] == '\\' &&
            text[1] == quote &&
            text[^2] == '\\' &&
            text[^1] == quote)
        {
            return text[2..^2];
        }

        if (text.Length >= 2 && text[0] == quote && text[^1] == quote)
        {
            return text[1..^1];
        }

        return text.Trim(quote);
    }

    private static string UnescapeLiteralContent(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (current != '\\' || i + 1 >= text.Length)
            {
                builder.Append(current);
                continue;
            }

            i++;
            builder.Append(text[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '0' => '\0',
                '\\' => '\\',
                '"' => '"',
                '\'' => '\'',
                _ => text[i]
            });
        }

        return builder.ToString();
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.LiteralExpr);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Kind, Kind.ToString());
        element.SetAttribute(WellKnownStrings.XmlAttributes.RawText, RawText);
        if (Value != null)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.Value, Value.ToString() ?? "");
        }
        return element;
    }
}

/// <summary>
/// 字面量类型
/// </summary>
public enum LiteralKind
{
    Integer,
    Float,
    String,
    Char,
    Boolean,
    Unit
}
