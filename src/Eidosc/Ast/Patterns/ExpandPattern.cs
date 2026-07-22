using System.Xml;
using Eidosc.Ast.Declarations;
using Eidosc.Utils;

namespace Eidosc.Ast.Patterns;

public sealed record ExpandPattern : Pattern, IMetaSyntaxSite
{
    public MetaInvocationSyntax Invocation { get; private set; } = new();

    public Pattern? ExpandedPattern { get; private set; }

    IReadOnlyList<EidosAstNode> IMetaSyntaxSite.MaterializedNodes =>
        ExpandedPattern == null ? [] : [ExpandedPattern];

    bool IMetaSyntaxSite.IsMaterialized => ExpandedPattern != null;

    internal void SetInvocation(MetaInvocationSyntax invocation) => Invocation = invocation;

    internal void SetSpan(SourceSpan span) => Span = span;

    void IMetaSyntaxSite.SetMaterializedNodes(IReadOnlyList<EidosAstNode> nodes) =>
        ExpandedPattern = nodes.Count == 1 ? nodes[0] as Pattern : null;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node) => Span = node.Span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, nameof(ExpandPattern));
        element.AppendChild(Invocation.ToXmlElement(doc));
        if (ExpandedPattern != null)
        {
            element.AppendChild(ExpandedPattern.ToXmlElement(doc));
        }
        return element;
    }
}
