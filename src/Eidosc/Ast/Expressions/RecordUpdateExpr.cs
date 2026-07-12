using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 基于已有 record 值的短更新表达式，例如 <c>state.{ tick: 0 }</c>。
/// </summary>
public record RecordUpdateExpr : Expression
{
    /// <summary>
    /// 被复制的基准值。
    /// </summary>
    public EidosAstNode? Base { get; private set; }

    /// <summary>
    /// 显式覆盖的字段。
    /// </summary>
    public List<FieldInit> NamedArgs { get; private set; } = [];

    /// <summary>
    /// Types 阶段解析出的等价构造器更新表达式。
    /// </summary>
    public CtorExpr? DesugaredCtor { get; private set; }

    /// <summary>
    /// Types 阶段为多构造器 ADT 生成的保留构造器更新表达式。
    /// </summary>
    public MatchExpr? DesugaredMatch { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Base = null;
        NamedArgs = [];
        DesugaredCtor = null;
        DesugaredMatch = null;

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        foreach (var child in ntNode.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (Base == null && TryCreateExpressionFromTerminal(term, out var expression))
                {
                    Base = expression;
                }

                continue;
            }

            if (child is not NonTerminalCstNode nested)
            {
                continue;
            }

            if (string.Equals(nested.NonTerminal?.DebugName, "recordUpdateBody", StringComparison.Ordinal))
            {
                CollectFieldInits(nested);
                continue;
            }

            Base ??= ExtractExpressionCandidate(nested);
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

    private static EidosAstNode? ExtractExpressionCandidate(NonTerminalCstNode node)
    {
        if (node.AstNode is EidosAstNode expr && expr is not FieldInit)
        {
            return expr;
        }

        foreach (var child in node.Children.OfType<NonTerminalCstNode>())
        {
            var candidate = ExtractExpressionCandidate(child);
            if (candidate != null)
            {
                return candidate;
            }
        }

        foreach (var term in node.Children.OfType<TerminalCstNode>())
        {
            if (TryCreateExpressionFromTerminal(term, out var expression))
            {
                return expression;
            }
        }

        return null;
    }

    private static bool TryCreateExpressionFromTerminal(TerminalCstNode term, out EidosAstNode expression)
    {
        expression = null!;
        var terminalName = term.Terminal?.ToString();
        if (terminalName == WellKnownStrings.Terminals.Identifier)
        {
            var identifier = new IdentifierExpr();
            identifier.SetSpan(term.Span);
            identifier.SetName(GetTokenText(term));
            expression = identifier;
            return true;
        }

        if (terminalName is WellKnownStrings.Terminals.Number
            or WellKnownStrings.Terminals.String
            or WellKnownStrings.Terminals.Char
            or WellKnownStrings.Terminals.Boolean)
        {
            var literal = new LiteralExpr();
            literal.SetSpan(term.Span);
            literal.SetLiteral(GetTokenText(term));
            expression = literal;
            return true;
        }

        return false;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetBase(EidosAstNode baseExpression) => Base = baseExpression;
    internal void AddNamedArg(FieldInit field) => NamedArgs.Add(field);
    internal void SetDesugaredCtor(CtorExpr ctor) => DesugaredCtor = ctor;
    internal void SetDesugaredMatch(MatchExpr match) => DesugaredMatch = match;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "RecordUpdateExpr");

        if (Base != null)
        {
            var baseElement = doc.CreateElement("Base");
            baseElement.AppendChild(Base.ToXmlElement(doc));
            element.AppendChild(baseElement);
        }

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
