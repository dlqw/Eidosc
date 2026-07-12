using System.Xml;
using Eidosc.Ast.Patterns;
using Eidosc.Utils;
using AstPattern = Eidosc.Ast.Patterns.Pattern;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// while-let 循环表达式。
/// 语法：while let pattern = expr then block_expr
/// </summary>
public record WhileLetExpr : Expression
{
    /// <summary>
    /// 匹配模式。
    /// </summary>
    public AstPattern? Pattern { get; private set; }

    /// <summary>
    /// 被匹配表达式（scrutinee）。
    /// </summary>
    public EidosAstNode? MatchedExpression { get; private set; }

    /// <summary>
    /// 循环体。
    /// </summary>
    public EidosAstNode? Body { get; private set; }

    internal void SetSpanValue(SourceSpan span) => Span = span;

    internal void SetPattern(AstPattern pattern) => Pattern = AstPattern.NormalizePatternNode(pattern);

    internal void SetMatchedExpression(EidosAstNode expression) => MatchedExpression = expression;

    internal void SetBody(EidosAstNode body) => Body = body;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        var seenBind = false;
        var seenThen = false;

        foreach (var child in ntNode.Children)
        {
            if (child is TerminalCstNode terminal)
            {
                var text = GetTokenText(terminal);
                if (string.Equals(text, WellKnownStrings.Punctuation.Equals, StringComparison.Ordinal))
                {
                    seenBind = true;
                    continue;
                }

                if (string.Equals(text, WellKnownStrings.Keywords.Then, StringComparison.Ordinal))
                {
                    seenThen = true;
                    continue;
                }

                if (!seenBind && Pattern == null && TryCreatePatternFromTerminal(terminal, out var terminalPattern))
                {
                    Pattern = terminalPattern;
                    continue;
                }

                if (seenBind && !seenThen && MatchedExpression == null &&
                    TryCreateExpressionFromTerminal(terminal, out var matched))
                {
                    MatchedExpression = matched;
                    continue;
                }

                if (seenThen && Body == null && TryCreateExpressionFromTerminal(terminal, out var bodyExpr))
                {
                    Body = bodyExpr;
                }

                continue;
            }

            if (child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            if (string.Equals(childNt.NonTerminal?.DebugName, "whileLetCondition", StringComparison.Ordinal))
            {
                ExtractWhileLetCondition(childNt);
                seenBind = true;
                continue;
            }

            if (childNt.AstNode is AstPattern patternNode && Pattern == null)
            {
                Pattern = AstPattern.NormalizePatternNode(patternNode);
                continue;
            }

            if (childNt.AstNode is EidosAstNode astNode &&
                astNode is not AstPattern)
            {
                if (seenBind && !seenThen && MatchedExpression == null)
                {
                    MatchedExpression = astNode;
                    continue;
                }

                if (seenThen && Body == null)
                {
                    Body = astNode;
                    continue;
                }
            }

            if (!seenBind && Pattern == null && TryExtractPattern(childNt, out var extractedPattern))
            {
                Pattern = extractedPattern;
                continue;
            }

            if (seenBind && !seenThen && MatchedExpression == null && TryExtractExpression(childNt, out var extractedMatch))
            {
                MatchedExpression = extractedMatch;
                continue;
            }

            if (seenThen && Body == null && TryExtractExpression(childNt, out var extractedBody))
            {
                Body = extractedBody;
            }
        }
    }

    private void ExtractWhileLetCondition(NonTerminalCstNode conditionNode)
    {
        var seenBind = false;
        CollectConditionParts(conditionNode, ref seenBind);
    }

    private void CollectConditionParts(ConcreteSyntaxNode node, ref bool seenBind)
    {
        if (node is TerminalCstNode terminal)
        {
            var text = GetTokenText(terminal);
            if (string.Equals(text, WellKnownStrings.Punctuation.Equals, StringComparison.Ordinal))
            {
                seenBind = true;
                return;
            }

            if (!seenBind && Pattern == null && TryCreatePatternFromTerminal(terminal, out var terminalPattern))
            {
                Pattern = terminalPattern;
                return;
            }

            if (seenBind && MatchedExpression == null && TryCreateExpressionFromTerminal(terminal, out var terminalExpr))
            {
                MatchedExpression = terminalExpr;
            }

            return;
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        if (!seenBind && Pattern == null && ntNode.AstNode is AstPattern astPattern)
        {
            Pattern = AstPattern.NormalizePatternNode(astPattern);
        }

        if (seenBind &&
            MatchedExpression == null &&
            ntNode.AstNode is EidosAstNode astExpr &&
            astExpr is not AstPattern)
        {
            MatchedExpression = astExpr;
        }

        foreach (var child in ntNode.Children)
        {
            CollectConditionParts(child, ref seenBind);
        }
    }

    private static bool TryExtractPattern(ConcreteSyntaxNode node, out AstPattern pattern)
    {
        pattern = null!;

        if (node is TerminalCstNode terminal)
        {
            return TryCreatePatternFromTerminal(terminal, out pattern);
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return false;
        }

        if (ntNode.AstNode is AstPattern astPattern)
        {
            pattern = AstPattern.NormalizePatternNode(astPattern);
            return true;
        }

        foreach (var child in ntNode.Children)
        {
            if (TryExtractPattern(child, out pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractExpression(ConcreteSyntaxNode node, out EidosAstNode expression)
    {
        expression = null!;

        if (node is TerminalCstNode terminal)
        {
            return TryCreateExpressionFromTerminal(terminal, out expression);
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return false;
        }

        if (ntNode.AstNode is EidosAstNode astNode &&
            astNode is not AstPattern)
        {
            expression = astNode;
            return true;
        }

        foreach (var child in ntNode.Children)
        {
            if (TryExtractExpression(child, out expression))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCreatePatternFromTerminal(TerminalCstNode terminal, out AstPattern pattern)
    {
        pattern = null!;
        var terminalName = terminal.Terminal?.ToString() ?? string.Empty;
        var text = GetTokenText(terminal);

        if (terminalName == WellKnownStrings.Terminals.Identifier)
        {
            var varPattern = new VarPattern();
            varPattern.SetSpan(terminal.Span);
            varPattern.SetName(text);
            pattern = varPattern;
            return true;
        }

        if (terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean)
        {
            var literalPattern = new LiteralPattern();
            literalPattern.SetSpan(terminal.Span);
            literalPattern.SetLiteral(text);
            pattern = literalPattern;
            return true;
        }

        if (string.Equals(text, WellKnownStrings.Punctuation.Underscore, StringComparison.Ordinal))
        {
            pattern = new WildcardPattern();
            pattern.BuildFromCst(null!, terminal);
            return true;
        }

        return false;
    }

    private static bool TryCreateExpressionFromTerminal(TerminalCstNode terminal, out EidosAstNode expression)
    {
        expression = null!;
        var terminalName = terminal.Terminal?.ToString() ?? string.Empty;
        var text = GetTokenText(terminal);

        if (terminalName == WellKnownStrings.Terminals.Identifier)
        {
            var identifier = new IdentifierExpr();
            identifier.SetSpan(terminal.Span);
            identifier.SetName(text);
            expression = identifier;
            return true;
        }

        if (terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean)
        {
            var literal = new LiteralExpr();
            literal.SetSpan(terminal.Span);
            literal.SetLiteral(text);
            expression = literal;
            return true;
        }

        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.WhileLetExpr);

        if (Pattern != null)
        {
            var patternElement = doc.CreateElement(WellKnownStrings.XmlElements.Pattern);
            patternElement.AppendChild(Pattern.ToXmlElement(doc));
            element.AppendChild(patternElement);
        }

        if (MatchedExpression != null)
        {
            var matchedElement = doc.CreateElement(WellKnownStrings.XmlElements.MatchedExpression);
            matchedElement.AppendChild(MatchedExpression.ToXmlElement(doc));
            element.AppendChild(matchedElement);
        }

        if (Body != null)
        {
            var bodyElement = doc.CreateElement(WellKnownStrings.XmlElements.Body);
            bodyElement.AppendChild(Body.ToXmlElement(doc));
            element.AppendChild(bodyElement);
        }

        return element;
    }
}
