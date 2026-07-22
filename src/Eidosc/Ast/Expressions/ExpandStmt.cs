using System.Xml;
using Eidosc.Ast.Declarations;
using Eidosc.Utils;

namespace Eidosc.Ast.Expressions;

public sealed record ExpandStmt : EidosAstNode, IMetaSyntaxSite
{
    private bool _isMaterialized;

    public MetaInvocationSyntax Invocation { get; private set; } = new();

    public IReadOnlyList<EidosAstNode> ExpandedStatements { get; private set; } = [];

    IReadOnlyList<EidosAstNode> IMetaSyntaxSite.MaterializedNodes => ExpandedStatements;

    bool IMetaSyntaxSite.IsMaterialized => _isMaterialized;

    internal void SetInvocation(MetaInvocationSyntax invocation) => Invocation = invocation;

    internal void SetSpan(SourceSpan span) => Span = span;

    void IMetaSyntaxSite.SetMaterializedNodes(IReadOnlyList<EidosAstNode> nodes)
    {
        ExpandedStatements = nodes;
        _isMaterialized = true;
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node) => Span = node.Span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, nameof(ExpandStmt));
        element.AppendChild(Invocation.ToXmlElement(doc));
        foreach (var statement in ExpandedStatements)
        {
            element.AppendChild(statement.ToXmlElement(doc));
        }

        return element;
    }
}
