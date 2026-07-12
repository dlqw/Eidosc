using System.Xml;

namespace Eidosc.Ast.Types;

/// <summary>
/// 元组类型
/// </summary>
/// <example>
/// (Int, String)
/// (Int, String, Bool)
/// </example>
public record TupleType : TypeNode
{
    /// <summary>
    /// 元素类型列表
    /// </summary>
    public List<TypeNode> Elements { get; internal set; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                var typeNode = ExtractTypeNode(child);
                if (typeNode != null)
                {
                    Elements.Add(typeNode);
                }
            }
        }
    }

    /// <summary>
    /// 从 CST 节点提取 TypeNode
    /// </summary>
    private TypeNode? ExtractTypeNode(ConcreteSyntaxNode node)
    {
        // 情况 1: NonTerminal 且有 TypeNode AST
        if (node is NonTerminalCstNode { AstNode: TypeNode typeNode })
        {
            return typeNode;
        }

        // 情况 2: Terminal (typeIdentifier)，创建 TypePath
        if (node is TerminalCstNode term)
        {
            var text = GetTokenText(term);
            if (!IsPunctuation(text))
            {
                var typePath = new TypePath();
                typePath.SetTypeName(text);
                typePath.SetSpan(term.Span);
                return typePath;
            }
        }

        // 情况 3: NonTerminal 但没有 AST，递归查找
        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                var extracted = ExtractTypeNode(child);
                if (extracted != null)
                {
                    return extracted;
                }
            }
        }

        return null;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.TupleType);

        foreach (var elem in Elements)
        {
            var elemElement = doc.CreateElement(WellKnownStrings.XmlElements.Element);
            elemElement.AppendChild(elem.ToXmlElement(doc));
            element.AppendChild(elemElement);
        }

        return element;
    }
}
