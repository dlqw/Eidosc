using System.Xml;
using Eidosc.Ast.Patterns;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 匹配表达式
/// </summary>
/// <example>
/// match expr
/// {
///     0 => "zero",
///     _ => "other"
/// }
/// </example>
public record MatchExpr : Expression
{
    /// <summary>
    /// 被匹配的表达式
    /// </summary>
    public EidosAstNode? MatchedExpression { get; private set; }

    /// <summary>
    /// 匹配分支列表
    /// </summary>
    public List<PatternBranch> Branches { get; private set; } = [];

    public bool IsPatternExhaustive { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    if (MatchedExpression == null)
                    {
                        var terminalExpr = TryCreateExpressionFromTerminal(term);
                        if (terminalExpr != null)
                        {
                            MatchedExpression = terminalExpr;
                        }
                    }

                    continue;
                }

                if (child is not NonTerminalCstNode childNt)
                {
                    continue;
                }

                if (childNt.AstNode is PatternBranch branch)
                {
                    Branches.Add(branch);
                    continue;
                }

                if (childNt.NonTerminal?.DebugName == "patternBranchTail")
                {
                    CollectPatternBranches(childNt);
                    continue;
                }

                if (MatchedExpression == null)
                {
                    if (childNt.AstNode is EidosAstNode exprNode)
                    {
                        MatchedExpression = exprNode;
                        continue;
                    }

                    var nestedExpr = TryExtractExpression(childNt);
                    if (nestedExpr != null)
                    {
                        MatchedExpression = nestedExpr;
                    }
                }
            }
        }
    }

    private void CollectPatternBranches(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode { AstNode: PatternBranch branch })
            {
                Branches.Add(branch);
            }
            else if (child is NonTerminalCstNode nested)
            {
                CollectPatternBranches(nested);
            }
        }
    }

    private static EidosAstNode? TryExtractExpression(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                var terminalExpr = TryCreateExpressionFromTerminal(term);
                if (terminalExpr != null)
                {
                    return terminalExpr;
                }
            }
            else if (child is NonTerminalCstNode childNt)
            {
                if (childNt.AstNode is PatternBranch)
                {
                    continue;
                }

                if (childNt.AstNode is EidosAstNode exprNode)
                {
                    return exprNode;
                }

                var nestedExpr = TryExtractExpression(childNt);
                if (nestedExpr != null)
                {
                    return nestedExpr;
                }
            }
        }

        return null;
    }

    private static EidosAstNode? TryCreateExpressionFromTerminal(TerminalCstNode term)
    {
        if (term.Terminal == null)
        {
            return null;
        }

        var terminalName = term.Terminal.ToString();
        var text = GetTokenText(term);

        if (terminalName == WellKnownStrings.Terminals.Identifier)
        {
            var ident = new IdentifierExpr();
            ident.SetSpan(term.Span);
            ident.SetName(text);
            return ident;
        }

        if (terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean)
        {
            var literal = new LiteralExpr();
            literal.SetSpan(term.Span);
            literal.SetLiteral(text);
            return literal;
        }

        return null;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.MatchExpr);

        if (MatchedExpression != null)
        {
            var matchedElement = doc.CreateElement(WellKnownStrings.XmlElements.MatchedExpression);
            matchedElement.AppendChild(MatchedExpression.ToXmlElement(doc));
            element.AppendChild(matchedElement);
        }

        foreach (var branch in Branches)
        {
            element.AppendChild(branch.ToXmlElement(doc));
        }

        return element;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetMatchedExpression(EidosAstNode expr) => MatchedExpression = expr;
    internal void AddBranch(PatternBranch branch) => Branches.Add(branch);
    internal void SetPatternExhaustive(bool isExhaustive) => IsPatternExhaustive = isExhaustive;
}
