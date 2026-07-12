using System.Xml;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// Trait associated const declaration or instance associated const implementation.
/// </summary>
public record AssociatedConstDecl : EidosAstNode
{
    public string Name { get; private set; } = "";
    public TypeNode? Type { get; private set; }
    public EidosAstNode? Value { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    internal void SetName(string name) => Name = name;
    internal void SetType(TypeNode? type) => Type = type;
    internal void SetValue(EidosAstNode? value) => Value = value;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "AssociatedConstDecl");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (Type != null)
        {
            var typeElement = doc.CreateElement(WellKnownStrings.XmlElements.Type);
            typeElement.AppendChild(Type.ToXmlElement(doc));
            element.AppendChild(typeElement);
        }

        if (Value != null)
        {
            var valueElement = doc.CreateElement(WellKnownStrings.XmlElements.Value);
            valueElement.AppendChild(Value.ToXmlElement(doc));
            element.AppendChild(valueElement);
        }

        return element;
    }
}
