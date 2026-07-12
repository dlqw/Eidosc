using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 元组表达式
/// </summary>
/// <example>
/// (1, 2)
/// (x, y, z)
/// (1,)  -- 单元素元组
/// </example>
public record TupleExpr : Expression
{
    /// <summary>
    /// 元素列表
    /// </summary>
    public List<EidosAstNode> Elements { get; internal set; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                var expr = ExtractExpression(child);
                if (expr != null)
                {
                    Elements.Add(expr);
                }
            }
        }
    }

    /// <summary>
    /// 从 CST 节点提取表达式
    /// </summary>
    private EidosAstNode? ExtractExpression(ConcreteSyntaxNode node)
    {
        // 情况 1: NonTerminal 且有 AST（如 BinaryExpr, IdentifierExpr 等）
        if (node is NonTerminalCstNode { AstNode: EidosAstNode expr })
        {
            return expr;
        }

        // 情况 2: Terminal (identifier)，创建 IdentifierExpr
        if (node is TerminalCstNode term)
        {
            var text = GetTokenText(term);
            if (!IsPunctuation(text) && text != WellKnownStrings.Punctuation.FatArrow)
            {
                var identExpr = new IdentifierExpr();
                identExpr.SetName(text);
                identExpr.SetSpan(term.Span);
                return identExpr;
            }
        }

        // 情况 3: NonTerminal 但没有 AST，递归查找
        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                var extracted = ExtractExpression(child);
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
        var element = CreateElement(doc, WellKnownStrings.XmlElements.TupleExpr);

        foreach (var elem in Elements)
        {
            var elemElement = doc.CreateElement(WellKnownStrings.XmlElements.Element);
            elemElement.AppendChild(elem.ToXmlElement(doc));
            element.AppendChild(elemElement);
        }

        return element;
    }
}
