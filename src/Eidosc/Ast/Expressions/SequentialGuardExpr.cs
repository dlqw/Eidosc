using System.Xml;
using Eidosc.Utils;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 顺序守卫链：`when guard1 when guard2 ...`
/// 前面的 pattern guard 绑定可被后面的守卫使用。
/// </summary>
public record SequentialGuardExpr : Expression
{
    public List<EidosAstNode> Guards { get; } = [];

    internal void SetSpanValue(SourceSpan span) => Span = span;

    internal void AddGuard(EidosAstNode guard) => Guards.Add(guard);

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.SequentialGuardExpr);
        foreach (var guard in Guards)
        {
            var guardElement = doc.CreateElement(WellKnownStrings.XmlElements.Guard);
            guardElement.AppendChild(guard.ToXmlElement(doc));
            element.AppendChild(guardElement);
        }

        return element;
    }
}
