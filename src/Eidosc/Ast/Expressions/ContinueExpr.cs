using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// Continue 表达式（Never 类型）
/// </summary>
/// <example>
/// continue
/// </example>
public record ContinueExpr : Expression
{
    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        return CreateElement(doc, WellKnownStrings.XmlElements.ContinueExpr);
    }
}
