using System.Xml;
using Eidosc.Ast.Declarations;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 块表达式
/// </summary>
/// <example>
/// {
///     let x = 1;
///     let y = 2;
///     x + y
/// }
/// </example>
public record BlockExpr : Expression
{
    /// <summary>
    /// 语句列表
    /// </summary>
    public List<EidosAstNode> Statements { get; private set; } = [];

    /// <summary>
    /// 最终表达式（可选，作为块的值）
    /// </summary>
    public EidosAstNode? ResultExpression { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            CollectBlockItems(ntNode);
        }
    }

    private void CollectBlockItems(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (IsSemicolonTerminal(term))
                {
                    // 遇到分号时，最近一次表达式应视为语句而非块尾值。
                    if (ResultExpression != null &&
                        Statements.Count > 0 &&
                        ReferenceEquals(Statements[^1], ResultExpression))
                    {
                        ResultExpression = null;
                    }
                    continue;
                }

                if (TryCreateExpressionFromTerminal(term, out var terminalExpr))
                {
                    AddBlockItem(terminalExpr);
                }

                continue;
            }

            if (child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            if (childNt.AstNode is EidosAstNode astNode)
            {
                AddBlockItem(astNode);
                continue;
            }

            CollectBlockItems(childNt);
        }
    }

    private void AddBlockItem(EidosAstNode astNode)
    {
        if (astNode is LetDecl or LetQuestionDecl or Assignment)
        {
            Statements.Add(astNode);
            return;
        }

        if (astNode is Expression)
        {
            // block_expr ::= "{" stmt* expr? "}"，最后一个 expr 是块值
            Statements.Add(astNode);
            ResultExpression = astNode;
            return;
        }

        Statements.Add(astNode);
    }

    private static bool IsSemicolonTerminal(TerminalCstNode term)
    {
        var terminalName = term.Terminal?.ToString() ?? "";
        if (terminalName is WellKnownStrings.Punctuation.Semicolon or "semi")
        {
            return true;
        }

        var text = GetTokenText(term);
        return text == WellKnownStrings.Punctuation.Semicolon;
    }

    private bool TryCreateExpressionFromTerminal(TerminalCstNode term, out EidosAstNode expression)
    {
        expression = null!;
        var terminalName = term.Terminal?.ToString() ?? "";
        var text = GetTokenText(term);

        if (terminalName == WellKnownStrings.Terminals.Identifier)
        {
            var ident = new IdentifierExpr();
            ident.SetSpan(term.Span);
            ident.SetName(text);
            expression = ident;
            return true;
        }

        if (terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean)
        {
            var literal = new LiteralExpr();
            literal.SetSpan(term.Span);
            literal.SetLiteral(text);
            expression = literal;
            return true;
        }

        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.BlockExpr);

        foreach (var stmt in Statements)
        {
            var stmtElement = doc.CreateElement(WellKnownStrings.XmlElements.Statement);
            stmtElement.AppendChild(stmt.ToXmlElement(doc));
            element.AppendChild(stmtElement);
        }

        return element;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void AddStatement(EidosAstNode stmt) => Statements.Add(stmt);
    internal void SetResultExpression(EidosAstNode expr) => ResultExpression = expr;
}
