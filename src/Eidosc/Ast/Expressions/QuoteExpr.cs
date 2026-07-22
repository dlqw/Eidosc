using System.Xml;
using Eidosc.Utils;

namespace Eidosc.Ast.Expressions;

public enum QuoteKind
{
    Items,
    Item,
    Members,
    Member,
    Statement,
    Expression,
    Pattern,
    Type,
    Tokens
}

public abstract record QuotePart : EidosAstNode;

public sealed record QuoteTokenPart : QuotePart
{
    public SyntaxKind TokenKind { get; private set; }
    public string TerminalName { get; private set; } = string.Empty;
    public TerminalFlag TerminalFlags { get; private set; }
    public string Spelling { get; private set; } = string.Empty;
    public string LeadingTrivia { get; private set; } = string.Empty;

    internal void Initialize(
        SyntaxKind tokenKind,
        string terminalName,
        TerminalFlag terminalFlags,
        string spelling,
        string leadingTrivia,
        SourceSpan span)
    {
        TokenKind = tokenKind;
        TerminalName = terminalName;
        TerminalFlags = terminalFlags;
        Spelling = spelling;
        LeadingTrivia = leadingTrivia;
        Span = span;
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node) => Span = node.Span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, nameof(QuoteTokenPart));
        element.SetAttribute("kind", TokenKind.ToString());
        element.SetAttribute("terminal", TerminalName);
        element.SetAttribute("spelling", Spelling);
        if (LeadingTrivia.Length > 0)
        {
            element.SetAttribute("leadingTrivia", LeadingTrivia);
        }

        return element;
    }
}

public sealed record QuoteSplicePart : QuotePart
{
    public bool IsMany { get; private set; }
    public EidosAstNode? Value { get; private set; }
    public string LeadingTrivia { get; private set; } = string.Empty;

    internal void Initialize(bool isMany, EidosAstNode value, string leadingTrivia, SourceSpan span)
    {
        IsMany = isMany;
        Value = value;
        LeadingTrivia = leadingTrivia;
        Span = span;
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node) => Span = node.Span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, nameof(QuoteSplicePart));
        element.SetAttribute("many", IsMany.ToString());
        if (LeadingTrivia.Length > 0)
        {
            element.SetAttribute("leadingTrivia", LeadingTrivia);
        }

        if (Value != null)
        {
            element.AppendChild(Value.ToXmlElement(doc));
        }

        return element;
    }
}

public sealed record QuoteExpr : Expression
{
    public QuoteKind? Kind { get; private set; }
    public List<QuotePart> Parts { get; private set; } = [];
    public string TrailingTrivia { get; private set; } = string.Empty;
    public bool GrammarValidated { get; internal set; }

    internal void SetKind(QuoteKind? kind) => Kind = kind;
    internal void SetParts(List<QuotePart> parts) => Parts = parts;
    internal void SetTrailingTrivia(string trailingTrivia) => TrailingTrivia = trailingTrivia;
    internal void SetSpan(SourceSpan span) => Span = span;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node) => Span = node.Span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, nameof(QuoteExpr));
        element.SetAttribute("kind", Kind?.ToString() ?? "inferred");
        element.SetAttribute("grammarValidated", GrammarValidated.ToString());
        foreach (var part in Parts)
        {
            element.AppendChild(part.ToXmlElement(doc));
        }

        if (TrailingTrivia.Length > 0)
        {
            element.SetAttribute("trailingTrivia", TrailingTrivia);
        }

        return element;
    }
}
