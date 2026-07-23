using System.Xml;
using Eidosc.Utils;

namespace Eidosc.Ast.Expressions;

public enum SelectionSubjectKind
{
    Unknown,
    Bool,
    Option,
    Result,
    Either
}

public sealed record SelectionSubjectDesugaring
{
    public SelectionSubjectKind Kind { get; init; }
    public SymbolId PositiveConstructorSymbolId { get; init; } = SymbolId.None;
    public SymbolId NegativeConstructorSymbolId { get; init; } = SymbolId.None;
    public object? SubjectType { get; init; }
    public IReadOnlyList<object> PositivePayloadTypes { get; init; } = [];
    public IReadOnlyList<object> NegativePayloadTypes { get; init; } = [];
}

/// <summary>
/// Type-directed binary selection syntax: subject then positive else negative.
/// </summary>
public record SelectionExpr : Expression
{
    public EidosAstNode? Subject { get; private set; }
    public EidosAstNode? ThenArm { get; private set; }
    public EidosAstNode? ElseArm { get; private set; }
    public bool IsGroup { get; private set; }
    public List<int> ThenPlaceholderIndices { get; private set; } = [];
    public List<int> ElsePlaceholderIndices { get; private set; } = [];
    public Dictionary<int, SourceSpan> ThenPlaceholderSpans { get; private set; } = [];
    public Dictionary<int, SourceSpan> ElsePlaceholderSpans { get; private set; } = [];
    public Dictionary<int, SymbolId> ThenPlaceholderSymbols { get; private set; } = [];
    public Dictionary<int, SymbolId> ElsePlaceholderSymbols { get; private set; } = [];
    public List<SelectionSubjectDesugaring> Subjects { get; private set; } = [];

    internal void SetSpan(SourceSpan span) => Span = span;
    internal void SetSubject(EidosAstNode subject)
    {
        Subject = subject;
        IsGroup = subject is TupleExpr;
    }
    internal void SetThenArm(EidosAstNode arm, IReadOnlyDictionary<int, SourceSpan> placeholders)
    {
        ThenArm = arm;
        ThenPlaceholderIndices = placeholders.Keys.Order().ToList();
        ThenPlaceholderSpans = new Dictionary<int, SourceSpan>(placeholders);
    }
    internal void SetElseArm(EidosAstNode arm, IReadOnlyDictionary<int, SourceSpan> placeholders)
    {
        ElseArm = arm;
        ElsePlaceholderIndices = placeholders.Keys.Order().ToList();
        ElsePlaceholderSpans = new Dictionary<int, SourceSpan>(placeholders);
    }
    internal void SetPlaceholderSymbol(bool positiveArm, int index, SymbolId symbolId)
    {
        (positiveArm ? ThenPlaceholderSymbols : ElsePlaceholderSymbols)[index] = symbolId;
    }
    internal void SetDesugaring(IEnumerable<SelectionSubjectDesugaring> subjects)
    {
        Subjects = subjects.ToList();
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node) => Span = node.Span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.SelectionExpr);
        element.SetAttribute("group", IsGroup.ToString());
        if (Subject != null)
        {
            var subjectElement = doc.CreateElement(WellKnownStrings.XmlElements.SourceExpression);
            subjectElement.AppendChild(Subject.ToXmlElement(doc));
            element.AppendChild(subjectElement);
        }
        if (ThenArm != null)
        {
            var thenElement = doc.CreateElement(WellKnownStrings.XmlElements.ThenBranch);
            thenElement.AppendChild(ThenArm.ToXmlElement(doc));
            element.AppendChild(thenElement);
        }
        if (ElseArm != null)
        {
            var elseElement = doc.CreateElement(WellKnownStrings.XmlElements.ElseBranch);
            elseElement.AppendChild(ElseArm.ToXmlElement(doc));
            element.AppendChild(elseElement);
        }
        return element;
    }
}
