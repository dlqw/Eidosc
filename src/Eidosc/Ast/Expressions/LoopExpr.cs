using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 循环表达式
/// </summary>
/// <example>
/// loop {
///     let input = read();
///     if input == "quit" { break }
///     process(input)
/// }
/// </example>
public record LoopExpr : Expression
{
    /// <summary>
    /// 循环体
    /// </summary>
    public EidosAstNode? Body { get; private set; }

    internal void SetBody(EidosAstNode body) => Body = body;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
                {
                    Body = expr;
                    break;
                }
            }
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.LoopExpr);

        if (Body != null)
        {
            var bodyElement = doc.CreateElement(WellKnownStrings.XmlElements.Body);
            bodyElement.AppendChild(Body.ToXmlElement(doc));
            element.AppendChild(bodyElement);
        }

        return element;
    }
}
