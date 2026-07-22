using System.Xml;
using Eidosc.Syntax;
using Eidosc.Utils;

namespace Eidosc.Ast.Declarations;

public sealed record ExpandDeclaration : Declaration, IMetaSyntaxSite
{
    private bool _isMaterialized;

    public MetaInvocationSyntax Invocation { get; private set; } = new();

    public SyntaxCategory SiteCategory { get; private set; } = SyntaxCategory.Item;

    public IReadOnlyList<EidosAstNode> ExpandedNodes { get; private set; } = [];

    IReadOnlyList<EidosAstNode> IMetaSyntaxSite.MaterializedNodes => ExpandedNodes;

    bool IMetaSyntaxSite.IsMaterialized => _isMaterialized;

    internal void SetInvocation(MetaInvocationSyntax invocation) => Invocation = invocation;

    internal void SetSiteCategory(SyntaxCategory category) => SiteCategory = category;

    void IMetaSyntaxSite.SetMaterializedNodes(IReadOnlyList<EidosAstNode> nodes)
    {
        ExpandedNodes = nodes;
        _isMaterialized = true;
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node) => Span = node.Span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, nameof(ExpandDeclaration));
        element.SetAttribute("category", SiteCategory.ToString());
        element.AppendChild(Invocation.ToXmlElement(doc));
        foreach (var node in ExpandedNodes)
        {
            element.AppendChild(node.ToXmlElement(doc));
        }

        return element;
    }
}
