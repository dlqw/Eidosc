using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// Explicit unreachable expression.
/// </summary>
public record UnreachableExpr : Expression
{
    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        return CreateElement(doc, WellKnownStrings.XmlElements.UnreachableExpr);
    }
}
