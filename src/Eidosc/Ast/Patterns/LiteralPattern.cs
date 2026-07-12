using System.Xml;
using System.Text;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 字面量模式
/// </summary>
/// <example>
/// 0
/// "hello"
/// true
/// </example>
public record LiteralPattern : Pattern
{
    /// <summary>
    /// 字面量值
    /// </summary>
    public object? Value { get; private set; }

    /// <summary>
    /// 字面量类型
    /// </summary>
    public LiteralType Type { get; private set; }

    /// <summary>
    /// 设置位置
    /// </summary>
    internal void SetSpan(Utils.SourceSpan span) => Span = span;

    /// <summary>
    /// 设置字面量值
    /// </summary>
    internal void SetLiteral(string text)
    {
        ParseLiteralValue(text);
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    ParseLiteralValue(text);
                }
            }
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.LiteralPattern);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Type, Type.ToString());
        if (Value != null)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.Value, Value.ToString() ?? "");
        }
        return element;
    }

    private void ParseLiteralValue(string text)
    {
        // 尝试解析字面量
        if (long.TryParse(text, out var intVal))
        {
            Value = intVal;
            Type = LiteralType.Integer;
            return;
        }

        if (double.TryParse(text, out var doubleVal))
        {
            Value = doubleVal;
            Type = LiteralType.Float;
            return;
        }

        if (text == WellKnownStrings.AdditionalKeywords.True || text == WellKnownStrings.AdditionalKeywords.False)
        {
            Value = text == WellKnownStrings.AdditionalKeywords.True;
            Type = LiteralType.Boolean;
            return;
        }

        if (text.StartsWith("\""))
        {
            Value = ParseStringLiteral(text);
            Type = LiteralType.String;
            return;
        }

        if (text.StartsWith("'"))
        {
            Value = ParseCharLiteral(text);
            Type = LiteralType.Char;
            return;
        }

        Value = text;
        Type = LiteralType.String;
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
}

/// <summary>
/// 字面量类型
/// </summary>
public enum LiteralType
{
    Integer,
    Float,
    String,
    Char,
    Boolean
}
