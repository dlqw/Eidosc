using System.Xml;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// 声明节点的抽象基类
/// </summary>
public abstract record Declaration : EidosAstNode
{
    /// <summary>
    /// 属性列表
    /// </summary>
    public List<Attribute> Attributes { get; protected set; } = [];

    /// <summary>
    /// 是否显式标记为 export。
    /// </summary>
    public bool IsExported { get; protected set; }

    protected XmlElement CreateDeclarationElement(XmlDocument doc, string name)
    {
        var element = CreateElement(doc, name);
        if (Attributes.Count > 0)
        {
            var attrsElement = doc.CreateElement(WellKnownStrings.XmlElements.Attributes);
            foreach (var attr in Attributes)
            {
                attrsElement.AppendChild(attr.ToXmlElement(doc));
            }
            element.AppendChild(attrsElement);
        }
        return element;
    }

    /// <summary>
    /// 从 CST 节点中提取属性列表
    /// </summary>
    protected void ExtractAttributes(AstContext context, ConcreteSyntaxNode node)
    {
        if (node is not NonTerminalCstNode ntNode) return;

        foreach (var child in ntNode.Children)
        {
            if (child is NonTerminalCstNode { AstNode: Attribute attr })
            {
                Attributes.Add(attr);
            }
        }
    }

    protected void ExtractExportModifier(ConcreteSyntaxNode node)
    {
        IsExported = node is NonTerminalCstNode ntNode && ContainsKeyword(ntNode, WellKnownStrings.Keywords.Export);
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetAttributes(List<Attribute> attrs) => Attributes = attrs;
    internal void SetExported(bool exported) => IsExported = exported;

    protected static bool ContainsKeyword(NonTerminalCstNode node, string keyword)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal &&
                string.Equals(terminal.Token.ToString(), keyword, StringComparison.Ordinal))
            {
                return true;
            }

            if (child is NonTerminalCstNode childNt &&
                ContainsKeyword(childNt, keyword))
            {
                return true;
            }
        }

        return false;
    }
}
