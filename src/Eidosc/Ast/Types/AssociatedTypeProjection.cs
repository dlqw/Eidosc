using System.Xml;

namespace Eidosc.Ast.Types;

/// <summary>
/// Associated type projection such as Iterator[I].Item.
/// </summary>
public record AssociatedTypeProjection : TypeNode
{
    public TypeNode? Target { get; private set; }
    public string MemberName { get; private set; } = "";
    public List<TypeNode> TypeArgs { get; private set; } = [];
    public List<GenericArgumentNode> GenericArguments { get; private set; } = [];

    public void SetTarget(TypeNode target) => Target = target;
    public void SetMemberName(string memberName) => MemberName = memberName;
    public void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetGenericArguments(IEnumerable<GenericArgumentNode> arguments)
    {
        GenericArguments = [.. arguments];
        TypeArgs = GenericArguments
            .Select(static argument => argument switch
            {
                UnresolvedGenericArgumentNode { TypeCandidate: { } type } => type,
                TypeGenericArgumentNode typeArgument => typeArgument.Type,
                _ => null
            })
            .OfType<TypeNode>()
            .ToList();
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "AssociatedTypeProjection");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, MemberName);

        if (Target != null)
        {
            var targetElement = doc.CreateElement("Target");
            targetElement.AppendChild(Target.ToXmlElement(doc));
            element.AppendChild(targetElement);
        }

        if (GenericArguments.Count > 0)
        {
            var arguments = doc.CreateElement("GenericArguments");
            foreach (var argument in GenericArguments)
            {
                arguments.AppendChild(argument.ToXmlElement(doc));
            }
            element.AppendChild(arguments);
        }

        return element;
    }
}
