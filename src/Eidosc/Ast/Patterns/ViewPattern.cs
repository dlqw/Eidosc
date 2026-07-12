using System.Xml;
using Eidosc.Ast.Expressions;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// View 模式（(viewExpr -> innerPattern)）
/// </summary>
public record ViewPattern : Pattern
{
    /// <summary>
    /// View 函数表达式
    /// </summary>
    public EidosAstNode? ViewExpression { get; internal set; }

    /// <summary>
    /// 应用于 view 结果的内部模式
    /// </summary>
    public Pattern? InnerPattern { get; internal set; }

    /// <summary>
    /// 是否可被当作透明恒等 view（例如 `fn(x) x` 或已识别的命名 identity 函数）。
    /// </summary>
    public bool IsTransparentIdentityView { get; internal set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ViewExpression = null;
        InnerPattern = null;
        IsTransparentIdentityView = false;

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        var separatorIndex = FindArrowSeparatorIndex(ntNode.Children);
        if (separatorIndex < 0)
        {
            // 在标准语法中理论上不会发生，保留最小容错。
            separatorIndex = ntNode.Children.Count;
        }

        ViewExpression = ExtractViewExpression(ntNode.Children, separatorIndex);
        InnerPattern = ExtractInnerPattern(ntNode.Children, separatorIndex + 1);
    }

    internal void SetTransparentIdentityView(bool value) => IsTransparentIdentityView = value;

    private static int FindArrowSeparatorIndex(IReadOnlyList<ConcreteSyntaxNode> children)
    {
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is TerminalCstNode terminal &&
                string.Equals(GetTokenText(terminal), WellKnownStrings.Punctuation.RightArrow, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static EidosAstNode? ExtractViewExpression(IReadOnlyList<ConcreteSyntaxNode> children, int endExclusive)
    {
        // 优先命中直接挂载在左半段的表达式 AST，避免递归到更深层后丢失结构。
        for (var i = 0; i < endExclusive; i++)
        {
            if (children[i] is NonTerminalCstNode { AstNode: EidosAstNode exprNode } &&
                exprNode is not Pattern)
            {
                return exprNode;
            }
        }

        // 其次再递归提取，兼容部分 squeezed/nonterminal 包装形态。
        for (var i = 0; i < endExclusive; i++)
        {
            if (children[i] is NonTerminalCstNode childNt &&
                TryExtractExpressionNode(childNt, out var nestedExpr))
            {
                return nestedExpr;
            }
        }

        // 最后回退到终端构造（identifier/literal）。
        for (var i = 0; i < endExclusive; i++)
        {
            if (children[i] is TerminalCstNode terminal &&
                (IsIdentifierTerminal(terminal) || IsLiteralTerminal(terminal)))
            {
                return CreateExpressionFromTerminal(terminal);
            }
        }

        return null;
    }

    private static Pattern? ExtractInnerPattern(IReadOnlyList<ConcreteSyntaxNode> children, int startInclusive)
    {
        if (startInclusive < 0)
        {
            startInclusive = 0;
        }

        // 优先直接命中右半段 pattern AST。
        for (var i = startInclusive; i < children.Count; i++)
        {
            if (children[i] is NonTerminalCstNode { AstNode: Pattern patternNode })
            {
                return NormalizePatternNode(patternNode);
            }
        }

        // 其次递归提取 pattern。
        for (var i = startInclusive; i < children.Count; i++)
        {
            if (children[i] is NonTerminalCstNode childNt &&
                TryExtractPatternNode(childNt, out var nestedPattern))
            {
                return NormalizePatternNode(nestedPattern);
            }
        }

        // 最后回退到终端 pattern（identifier/literal）。
        for (var i = startInclusive; i < children.Count; i++)
        {
            if (children[i] is not TerminalCstNode terminal)
            {
                continue;
            }

            if (IsIdentifierTerminal(terminal))
            {
                return CreateVarPatternFromTerminal(terminal);
            }

            if (IsLiteralTerminal(terminal))
            {
                return CreateLiteralPatternFromTerminal(terminal);
            }
        }

        return null;
    }

    private static EidosAstNode CreateExpressionFromTerminal(TerminalCstNode terminal)
    {
        if (IsIdentifierTerminal(terminal))
        {
            var identifier = new IdentifierExpr();
            identifier.SetSpan(terminal.Span);
            identifier.SetName(GetTokenText(terminal));
            return identifier;
        }

        var literal = new LiteralExpr();
        literal.SetSpan(terminal.Span);
        literal.SetLiteral(GetTokenText(terminal));
        return literal;
    }

    private static bool TryExtractExpressionNode(NonTerminalCstNode node, out EidosAstNode expression)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode { AstNode: EidosAstNode expr } &&
                expr is not Pattern)
            {
                expression = expr;
                return true;
            }

            if (child is NonTerminalCstNode childNt &&
                TryExtractExpressionNode(childNt, out expression))
            {
                return true;
            }
        }

        expression = null!;
        return false;
    }

    private static bool TryExtractPatternNode(NonTerminalCstNode node, out Pattern pattern)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode { AstNode: Pattern patternNode })
            {
                pattern = patternNode;
                return true;
            }

            if (child is TerminalCstNode terminal)
            {
                if (IsIdentifierTerminal(terminal))
                {
                    pattern = CreateVarPatternFromTerminal(terminal);
                    return true;
                }

                if (IsLiteralTerminal(terminal))
                {
                    pattern = CreateLiteralPatternFromTerminal(terminal);
                    return true;
                }
            }

            if (child is NonTerminalCstNode childNt &&
                TryExtractPatternNode(childNt, out pattern))
            {
                return true;
            }
        }

        pattern = null!;
        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.ViewPattern);

        if (ViewExpression != null)
        {
            var viewExprElement = doc.CreateElement(WellKnownStrings.XmlElements.ViewExpression);
            viewExprElement.AppendChild(ViewExpression.ToXmlElement(doc));
            element.AppendChild(viewExprElement);
        }

        if (InnerPattern != null)
        {
            var innerPatternElement = doc.CreateElement(WellKnownStrings.XmlElements.InnerPattern);
            innerPatternElement.AppendChild(InnerPattern.ToXmlElement(doc));
            element.AppendChild(innerPatternElement);
        }

        return element;
    }
}
