using System.Xml;

namespace Eidosc.Ast.Types;

/// <summary>
/// 通配符类型（用于类型推断）
/// </summary>
/// <example>
/// _
/// </example>
public record WildcardType : TypeNode
{
    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        return CreateElement(doc, WellKnownStrings.XmlElements.WildcardType);
    }
}
