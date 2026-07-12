using System.Xml;

namespace Eidosc.Ast.Declarations;

public sealed record EffectRequirementNode : EidosAstNode
{
    public List<string> Path { get; init; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Path.Clear();
        Path.AddRange(ExtractPath(node));
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.EffectRequirement);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Path, string.Join(WellKnownStrings.Separators.Path, Path));
        return element;
    }
}
