using System.Xml;
using Eidosc.Ast.Declarations;
using Eidosc.Utils;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// A source-level expression syntax expansion. The invocation is evaluated by
/// the namer and <see cref="ExpandedExpression"/> is populated before types/HIR.
/// </summary>
public sealed record ExpandExpr : Expression, IMetaSyntaxSite
{
    public MetaInvocationSyntax Invocation { get; private set; } = new();

    public EidosAstNode? ExpandedExpression { get; private set; }

    IReadOnlyList<EidosAstNode> IMetaSyntaxSite.MaterializedNodes =>
        ExpandedExpression == null ? [] : [ExpandedExpression];

    bool IMetaSyntaxSite.IsMaterialized => ExpandedExpression != null;

    internal void SetInvocation(MetaInvocationSyntax invocation) => Invocation = invocation;

    internal void SetExpandedExpression(EidosAstNode expression) => ExpandedExpression = expression;

    void IMetaSyntaxSite.SetMaterializedNodes(IReadOnlyList<EidosAstNode> nodes) =>
        ExpandedExpression = nodes.Count == 1 ? nodes[0] : null;

    internal void SetSpan(SourceSpan span) => Span = span;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node) => Span = node.Span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, nameof(ExpandExpr));
        element.AppendChild(Invocation.ToXmlElement(doc));
        if (ExpandedExpression != null)
        {
            var expanded = doc.CreateElement("ExpandedExpression");
            expanded.AppendChild(ExpandedExpression.ToXmlElement(doc));
            element.AppendChild(expanded);
        }

        return element;
    }
}
