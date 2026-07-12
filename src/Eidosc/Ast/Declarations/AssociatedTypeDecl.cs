using System.Xml;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// Trait associated type declaration or instance associated type implementation.
/// </summary>
public record AssociatedTypeDecl : EidosAstNode
{
    public string Name { get; private set; } = "";
    public List<TypeParam> TypeParams { get; private set; } = [];
    public TypeNode? ValueType { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    internal void SetName(string name) => Name = name;
    internal void SetTypeParams(List<TypeParam> typeParams) => TypeParams = typeParams;
    internal void SetValueType(TypeNode? valueType) => ValueType = valueType;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "AssociatedTypeDecl");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (TypeParams.Count > 0)
        {
            var typeParamsElement = doc.CreateElement(WellKnownStrings.XmlElements.TypeParams);
            foreach (var typeParam in TypeParams)
            {
                typeParamsElement.AppendChild(typeParam.ToXmlElement(doc));
            }

            element.AppendChild(typeParamsElement);
        }

        if (ValueType != null)
        {
            var valueElement = doc.CreateElement("ValueType");
            valueElement.AppendChild(ValueType.ToXmlElement(doc));
            element.AppendChild(valueElement);
        }

        return element;
    }
}
