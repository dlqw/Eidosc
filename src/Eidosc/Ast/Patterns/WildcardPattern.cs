using System.Xml;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 通配符模式
/// </summary>
/// <example>
/// _
/// </example>
public record WildcardPattern : Pattern
{
    internal void SetSpan(Utils.SourceSpan span) => Span = span;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        return CreateElement(doc, WellKnownStrings.XmlElements.WildcardPattern);
    }
}
