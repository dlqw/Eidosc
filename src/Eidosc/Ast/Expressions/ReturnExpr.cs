using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// Return 表达式（Never 类型）
/// </summary>
/// <example>
/// return
/// return x + 1
/// </example>
public record ReturnExpr : Expression
{
    /// <summary>
    /// 返回值表达式（可选）
    /// </summary>
    public EidosAstNode? Value { get; private set; }

    public void SetValue(EidosAstNode value) => Value = value;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        foreach (var child in ntNode.Children)
        {
            if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
            {
                Value = expr;
                break;
            }

            if (child is TerminalCstNode terminal &&
                TryCreateExpressionFromTerminal(terminal, out var terminalExpr))
            {
                Value = terminalExpr;
                break;
            }
        }
    }

    private static bool TryCreateExpressionFromTerminal(TerminalCstNode terminal, out EidosAstNode expression)
    {
        expression = null!;

        var terminalName = terminal.Terminal?.ToString() ?? terminal.Terminal?.DebugName ?? string.Empty;
        if (terminalName == WellKnownStrings.Keywords.Return)
        {
            return false;
        }

        if (terminalName == WellKnownStrings.Terminals.Identifier)
        {
            var identifier = new IdentifierExpr();
            identifier.SetSpan(terminal.Span);
            identifier.SetName(GetTokenText(terminal));
            expression = identifier;
            return true;
        }

        if (terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean)
        {
            var literal = new LiteralExpr();
            literal.SetSpan(terminal.Span);
            literal.SetLiteral(GetTokenText(terminal));
            expression = literal;
            return true;
        }

        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.ReturnExpr);

        if (Value != null)
        {
            var valueElement = doc.CreateElement(WellKnownStrings.XmlElements.Value);
            valueElement.AppendChild(Value.ToXmlElement(doc));
            element.AppendChild(valueElement);
        }

        return element;
    }
}
