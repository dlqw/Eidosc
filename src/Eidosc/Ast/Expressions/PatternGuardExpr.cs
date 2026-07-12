using System.Xml;
using Eidosc.Ast.Patterns;
using Eidosc.Utils;
using AstPattern = Eidosc.Ast.Patterns.Pattern;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 模式守卫绑定表达式：`pattern <- expr`。
/// 仅用于 `when` guard 场景。
/// </summary>
public record PatternGuardExpr : Expression
{
    /// <summary>
    /// 守卫匹配模式。
    /// </summary>
    public AstPattern? Pattern { get; private set; }

    /// <summary>
    /// 守卫源表达式。
    /// </summary>
    public EidosAstNode? SourceExpression { get; private set; }

    internal void SetSpanValue(SourceSpan span) => Span = span;

    internal void SetPattern(AstPattern pattern) => Pattern = pattern;

    internal void SetSourceExpression(EidosAstNode sourceExpression) => SourceExpression = sourceExpression;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        var seenLeftArrow = false;
        AstPattern? terminalPatternFallback = null;
        EidosAstNode? terminalSourceFallback = null;

        foreach (var child in ntNode.Children)
        {
            if (child is TerminalCstNode terminal)
            {
                var text = GetTokenText(terminal);
                if (text == WellKnownStrings.Punctuation.LeftArrow)
                {
                    seenLeftArrow = true;
                    continue;
                }

                if (IsPunctuation(text) || string.Equals(text, WellKnownStrings.Keywords.When, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!seenLeftArrow && Pattern == null && TryCreatePatternFromTerminal(terminal, out var terminalPattern))
                {
                    terminalPatternFallback ??= terminalPattern;
                    continue;
                }

                if (seenLeftArrow && SourceExpression == null && TryCreateExpressionFromTerminal(terminal, out var terminalExpr))
                {
                    terminalSourceFallback ??= terminalExpr;
                }

                continue;
            }

            if (child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            var childName = childNt.NonTerminal?.DebugName ?? string.Empty;
            if (string.Equals(childName, "patternGuardSourceTail", StringComparison.Ordinal))
            {
                seenLeftArrow = true;
                if (SourceExpression == null && TryExtractExpression(childNt, out var tailExpression))
                {
                    SourceExpression = tailExpression;
                }

                continue;
            }

            if (!seenLeftArrow)
            {
                if (childNt.AstNode is AstPattern patternNode)
                {
                    Pattern ??= AstPattern.NormalizePatternNode(patternNode);
                    continue;
                }

                if (Pattern == null && TryExtractPattern(childNt, out var extractedPattern))
                {
                    Pattern = extractedPattern;
                }

                continue;
            }

            if (childNt.AstNode is EidosAstNode astNode && astNode is not AstPattern)
            {
                SourceExpression ??= astNode;
                continue;
            }

            if (SourceExpression == null && TryExtractExpression(childNt, out var extractedExpression))
            {
                SourceExpression = extractedExpression;
            }
        }

        Pattern ??= terminalPatternFallback;
        SourceExpression ??= terminalSourceFallback;
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

        var nodeName = ntNode.NonTerminal?.DebugName ?? string.Empty;
        if (TryCreatePatternFromNodeShape(ntNode, nodeName, out pattern))
        {
            return true;
        }

        if (ntNode.AstNode is AstPattern patternNode)
        {
            pattern = AstPattern.NormalizePatternNode(patternNode);
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

    private static bool TryCreatePatternFromNodeShape(NonTerminalCstNode node, string nodeName, out AstPattern pattern)
    {
        pattern = null!;

        if (string.Equals(nodeName, "ctorPattern", StringComparison.Ordinal) ||
            string.Equals(nodeName, "patternGuardCtorLhs", StringComparison.Ordinal))
        {
            var ctorPattern = new CtorPattern();
            ctorPattern.BuildFromCst(new AstContext(), node);
            pattern = ctorPattern;
            return true;
        }

        if (string.Equals(nodeName, "tuplePattern", StringComparison.Ordinal))
        {
            var tuplePattern = new TuplePattern();
            tuplePattern.BuildFromCst(new AstContext(), node);
            pattern = tuplePattern;
            return true;
        }

        if (string.Equals(nodeName, "listPattern", StringComparison.Ordinal))
        {
            var listPattern = new ListPattern();
            listPattern.BuildFromCst(new AstContext(), node);
            pattern = listPattern;
            return true;
        }

        if (string.Equals(nodeName, "viewPattern", StringComparison.Ordinal))
        {
            var viewPattern = new ViewPattern();
            viewPattern.BuildFromCst(new AstContext(), node);
            pattern = viewPattern;
            return true;
        }

        if (string.Equals(nodeName, "wildcardPattern", StringComparison.Ordinal))
        {
            var wildcardPattern = new WildcardPattern();
            wildcardPattern.BuildFromCst(new AstContext(), node);
            pattern = wildcardPattern;
            return true;
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

        if (ntNode.AstNode is EidosAstNode astNode && astNode is not AstPattern)
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

        if (text == WellKnownStrings.Punctuation.Underscore)
        {
            var wildcard = new WildcardPattern();
            wildcard.BuildFromCst(null!, terminal);
            pattern = wildcard;
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
        var element = CreateElement(doc, WellKnownStrings.XmlElements.PatternGuardExpr);

        if (Pattern != null)
        {
            var patternElement = doc.CreateElement(WellKnownStrings.XmlElements.Pattern);
            patternElement.AppendChild(Pattern.ToXmlElement(doc));
            element.AppendChild(patternElement);
        }

        if (SourceExpression != null)
        {
            var sourceElement = doc.CreateElement(WellKnownStrings.XmlElements.SourceExpression);
            sourceElement.AppendChild(SourceExpression.ToXmlElement(doc));
            element.AppendChild(sourceElement);
        }

        return element;
    }
}
