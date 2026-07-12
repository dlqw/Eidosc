using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// Contextual record literal, for example <c>.{ x: 1, y: 2 }</c>.
/// The concrete constructor is selected during type checking from the expected type.
/// </summary>
public record ContextualRecordLiteralExpr : Expression
{
    public List<FieldInit> NamedArgs { get; private set; } = [];

    /// <summary>
    /// Types stage desugaring to the selected constructor call.
    /// </summary>
    public CtorExpr? DesugaredCtor { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        NamedArgs = [];
        DesugaredCtor = null;

        if (node is NonTerminalCstNode ntNode)
        {
            CollectFieldInits(ntNode);
        }
    }

    private void CollectFieldInits(NonTerminalCstNode node)
    {
        if (node.AstNode is FieldInit field)
        {
            NamedArgs.Add(field);
            return;
        }

        foreach (var child in node.Children.OfType<NonTerminalCstNode>())
        {
            CollectFieldInits(child);
        }
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void AddNamedArg(FieldInit field) => NamedArgs.Add(field);
    internal void SetDesugaredCtor(CtorExpr ctor) => DesugaredCtor = ctor;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "ContextualRecordLiteralExpr");

        if (NamedArgs.Count > 0)
        {
            var argsElement = doc.CreateElement(WellKnownStrings.XmlElements.NamedArgs);
            foreach (var field in NamedArgs)
            {
                argsElement.AppendChild(field.ToXmlElement(doc));
            }

            element.AppendChild(argsElement);
        }

        return element;
    }
}
