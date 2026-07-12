using System.Xml;
using Eidosc.Types;

namespace Eidosc.Ast.Types;

/// <summary>
/// Kind 表达式（类型的类型）
/// </summary>
/// <example>
/// kind1                -- 普通类型
/// kind2                -- 单参数类型构造器
/// kind3                -- 双参数类型构造器
/// </example>
public record Kind : EidosAstNode
{
    /// <summary>
    /// Kind 文本（规范化后）
    /// </summary>
    public string KindText { get; internal set; } = "kind1";

    /// <summary>
    /// Kind 的参数列表（对于函数 kind）
    /// </summary>
    public List<Kind> Parameters { get; private set; } = [];

    /// <summary>
    /// 是否是基础 kind (*)
    /// </summary>
    public bool IsStar { get; internal set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        IsStar = false;
        Parameters.Clear();
        KindText = "kind1";

        var kindText = ExtractKindText(node);
        if (!KindParser.TryParse(kindText, out var kind, out _))
        {
            return;
        }

        IsStar = true;
        KindText = KindParser.ToKindText(kind);
        var topLevelArity = KindParser.GetTopLevelArity(kind);
        for (var i = 0; i < topLevelArity; i++)
        {
            Parameters.Add(new Kind
            {
                IsStar = true,
                KindText = "kind1",
                Span = node.Span
            });
        }
    }

    private static string ExtractKindText(ConcreteSyntaxNode node)
    {
        if (node is TerminalCstNode term)
        {
            var text = NormalizeKindTokenText(term);
            return text is WellKnownStrings.Punctuation.RightArrow or "(" or ")" ||
                   KindParser.IsCompactKindName(text)
                ? text
                : "";
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return "";
        }

        var nodeName = ntNode.NonTerminal?.DebugName ?? string.Empty;
        if (nodeName == "kindAtom")
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode terminal &&
                    KindParser.IsCompactKindName(NormalizeKindTokenText(terminal)))
                {
                    return NormalizeKindTokenText(terminal);
                }
            }

            foreach (var child in ntNode.Children)
            {
                if (child is NonTerminalCstNode nested &&
                    string.Equals(nested.NonTerminal?.DebugName, WellKnownStrings.XmlAttributes.Kind, StringComparison.Ordinal))
                {
                    var nestedText = ExtractKindText(nested);
                    return string.IsNullOrWhiteSpace(nestedText) ? "" : $"({nestedText})";
                }
            }
        }

        var pieces = new List<string>();
        foreach (var child in ntNode.Children)
        {
            var childText = ExtractKindText(child);
            if (!string.IsNullOrWhiteSpace(childText))
            {
                pieces.Add(childText);
            }
        }

        return string.Join(" ", pieces);
    }

    private static string NormalizeKindTokenText(TerminalCstNode term)
    {
        var text = GetTokenText(term);
        if (text is "(" or ")" or WellKnownStrings.Punctuation.RightArrow)
        {
            return text;
        }

        var terminalName = term.Terminal?.ToString();
        return terminalName switch
        {
            "lparen" => "(",
            "rparen" => ")",
            _ => text
        };
    }

    public int GetArrowArity()
    {
        if (!KindParser.TryParse(KindText, out var kind, out _))
        {
            return 0;
        }

        return KindParser.GetTopLevelArity(kind);
    }

    public string ToKindText()
    {
        return KindText;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.Kind);
        element.SetAttribute(WellKnownStrings.XmlAttributes.IsStar, IsStar.ToString());
        element.SetAttribute(WellKnownStrings.XmlAttributes.Text, KindText);

        foreach (var param in Parameters)
        {
            var paramElement = doc.CreateElement(WellKnownStrings.XmlElements.Parameter);
            paramElement.AppendChild(param.ToXmlElement(doc));
            element.AppendChild(paramElement);
        }

        return element;
    }
}
