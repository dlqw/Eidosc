using System.Xml;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// Associated const projection such as Bounded[Int].Min.
/// </summary>
public record AssociatedConstExpr : Expression
{
    public TypeNode? Target { get; private set; }
    public string MemberName { get; private set; } = "";
    public EidosAstNode? ImplementationValue { get; private set; }

    public void SetTarget(TypeNode target) => Target = target;
    public void SetMemberName(string memberName) => MemberName = memberName;
    public void SetImplementationValue(EidosAstNode? value) => ImplementationValue = value;
    public void SetSpan(Eidosc.Utils.SourceSpan span) => Span = span;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "AssociatedConstExpr");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, MemberName);

        if (Target != null)
        {
            var targetElement = doc.CreateElement("Target");
            targetElement.AppendChild(Target.ToXmlElement(doc));
            element.AppendChild(targetElement);
        }

        if (ImplementationValue != null)
        {
            var valueElement = doc.CreateElement(WellKnownStrings.XmlElements.Value);
            valueElement.AppendChild(ImplementationValue.ToXmlElement(doc));
            element.AppendChild(valueElement);
        }

        return element;
    }
}
