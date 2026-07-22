using System.Xml;
using Eidosc.Ast.Declarations;
using Eidosc.Utils;

namespace Eidosc.Ast.Types;

public sealed record ExpandType : TypeNode, IMetaSyntaxSite
{
    public MetaInvocationSyntax Invocation { get; private set; } = new();

    public TypeNode? ExpandedType { get; private set; }

    IReadOnlyList<EidosAstNode> IMetaSyntaxSite.MaterializedNodes =>
        ExpandedType == null ? [] : [ExpandedType];

    bool IMetaSyntaxSite.IsMaterialized => ExpandedType != null;

    internal void SetInvocation(MetaInvocationSyntax invocation) => Invocation = invocation;

    internal void SetSpan(SourceSpan span) => Span = span;

    void IMetaSyntaxSite.SetMaterializedNodes(IReadOnlyList<EidosAstNode> nodes) =>
        ExpandedType = nodes.Count == 1 ? nodes[0] as TypeNode : null;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node) => Span = node.Span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, nameof(ExpandType));
        element.AppendChild(Invocation.ToXmlElement(doc));
        if (ExpandedType != null)
        {
            element.AppendChild(ExpandedType.ToXmlElement(doc));
        }
        return element;
    }
}
