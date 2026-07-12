using System.Xml;
using Eidosc.Symbols;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// Explicit trait evidence selection at a call site.
/// </summary>
/// <example>
/// contains(names, "bob") given CaseInsensitiveStringEq
/// </example>
public record GivenExpr : Expression
{
    public EidosAstNode? Target { get; private set; }
    public List<string> EvidencePath { get; private set; } = [];
    public SymbolId EvidenceSymbolId { get; set; } = SymbolId.None;

    public void SetSpan(Utils.SourceSpan span) => Span = span;
    public void SetTarget(EidosAstNode target) => Target = target;
    public void SetEvidencePath(List<string> evidencePath) => EvidencePath = evidencePath;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "GivenExpr");
        element.SetAttribute("Evidence", string.Join("::", EvidencePath));

        if (Target != null)
        {
            var targetElement = doc.CreateElement("Target");
            targetElement.AppendChild(Target.ToXmlElement(doc));
            element.AppendChild(targetElement);
        }

        return element;
    }
}
