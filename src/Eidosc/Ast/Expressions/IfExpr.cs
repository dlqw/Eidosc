using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 条件表达式
/// </summary>
/// <example>
/// if x > 0 { x } else { -x }
/// if a { 1 } else if b { 2 } else { 3 }
/// </example>
public record IfExpr : Expression
{
    /// <summary>
    /// 条件表达式
    /// </summary>
    public EidosAstNode? Condition { get; private set; }

    /// <summary>
    /// then 分支
    /// </summary>
    public EidosAstNode? ThenBranch { get; private set; }

    /// <summary>
    /// else 分支（可选）
    /// </summary>
    public EidosAstNode? ElseBranch { get; private set; }

    /// <summary>
    /// 是否有 else 分支
    /// </summary>
    public bool HasElse { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            var foundThen = false;
            var foundElse = false;

            foreach (var child in ntNode.Children)
            {
                // 检查关键字
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (text == WellKnownStrings.Keywords.Then)
                    {
                        foundThen = true;
                        continue;
                    }
                    if (text == WellKnownStrings.Keywords.Else)
                    {
                        foundElse = true;
                        HasElse = true;
                        continue;
                    }

                    // 如果在 then 之前，是条件表达式的一部分
                    if (!foundThen)
                    {
                        // 处理终端节点作为条件（如 identifier, booleanLiteral）
                        if (Condition == null)
                        {
                            Condition = CreateExpressionFromTerminal(term);
                        }
                    }
                    else if (!foundElse && ThenBranch == null)
                    {
                        ThenBranch = CreateExpressionFromTerminal(term);
                    }
                    else if (foundElse && ElseBranch == null)
                    {
                        ElseBranch = CreateExpressionFromTerminal(term);
                        HasElse = ElseBranch != null;
                    }
                }
                else if (child is NonTerminalCstNode childNt)
                {
                    if (childNt.AstNode is Expression expr)
                    {
                        if (!foundThen && Condition == null)
                        {
                            Condition = expr;
                        }
                        else if (foundThen && !foundElse && ThenBranch == null)
                        {
                            ThenBranch = expr;
                        }
                        else if (foundElse && ElseBranch == null)
                        {
                            ElseBranch = expr;
                        }
                    }
                    else if (childNt.AstNode is BlockExpr blockExpr)
                    {
                        if (foundThen && !foundElse && ThenBranch == null)
                        {
                            ThenBranch = blockExpr;
                        }
                        else if (foundElse && ElseBranch == null)
                        {
                            ElseBranch = blockExpr;
                        }
                    }
                    else
                    {
                        // 递归查找表达式
                        var childName = childNt.NonTerminal?.DebugName ?? "";
                        if (childName == "elseClause")
                        {
                            // 处理 else 分支
                            ExtractElseClause(childNt);
                        }
                        else if (!foundThen && Condition == null)
                        {
                            // 尝试从子节点提取条件表达式
                            Condition = TryExtractExpression(childNt);
                        }
                    }
                }
            }

            // 如果 ThenBranch 是 BlockExpr，提取其最后一个表达式作为结果
            if (ThenBranch is BlockExpr thenBlock && thenBlock.Statements.Count > 0)
            {
                // 保留 BlockExpr，类型推断会处理
            }
        }
    }

    private void ExtractElseClause(NonTerminalCstNode elseNode)
    {
        foreach (var child in elseNode.Children)
        {
            if (child is TerminalCstNode terminal && ElseBranch == null)
            {
                ElseBranch = CreateExpressionFromTerminal(terminal);
                HasElse = ElseBranch != null;
                if (HasElse)
                {
                    return;
                }
            }

            if (child is NonTerminalCstNode childNt)
            {
                if (childNt.AstNode is Expression expr && ElseBranch == null)
                {
                    ElseBranch = expr;
                    HasElse = true;
                }
                else if (childNt.AstNode is BlockExpr blockExpr && ElseBranch == null)
                {
                    ElseBranch = blockExpr;
                    HasElse = true;
                }
            }
        }
    }

    private EidosAstNode? TryExtractExpression(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                return CreateExpressionFromTerminal(term);
            }
            else if (child is NonTerminalCstNode childNt)
            {
                if (childNt.AstNode is Expression expr)
                {
                    return expr;
                }
                var nested = TryExtractExpression(childNt);
                if (nested != null)
                    return nested;
            }
        }
        return null;
    }

    private EidosAstNode? CreateExpressionFromTerminal(TerminalCstNode term)
    {
        var terminalName = term.Terminal?.ToString() ?? "";
        var text = GetTokenText(term);

        if (terminalName == WellKnownStrings.Terminals.Identifier)
        {
            var ident = new IdentifierExpr();
            ident.SetSpan(term.Span);
            ident.SetName(text);
            return ident;
        }
        else if (terminalName == WellKnownStrings.Terminals.Boolean || terminalName == WellKnownStrings.Terminals.Number ||
                 terminalName == WellKnownStrings.Terminals.String || terminalName == WellKnownStrings.Terminals.String || terminalName == WellKnownStrings.Terminals.Char)
        {
            var lit = new LiteralExpr();
            lit.SetSpan(term.Span);
            lit.SetLiteral(text);
            return lit;
        }

        return null;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.IfExpr);

        if (Condition != null)
        {
            var condElement = doc.CreateElement(WellKnownStrings.XmlElements.Condition);
            condElement.AppendChild(Condition.ToXmlElement(doc));
            element.AppendChild(condElement);
        }

        if (ThenBranch != null)
        {
            var thenElement = doc.CreateElement(WellKnownStrings.XmlElements.ThenBranch);
            thenElement.AppendChild(ThenBranch.ToXmlElement(doc));
            element.AppendChild(thenElement);
        }

        if (ElseBranch != null)
        {
            var elseElement = doc.CreateElement(WellKnownStrings.XmlElements.ElseBranch);
            elseElement.AppendChild(ElseBranch.ToXmlElement(doc));
            element.AppendChild(elseElement);
        }

        return element;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetCondition(EidosAstNode cond) => Condition = cond;
    internal void SetThenBranch(EidosAstNode branch) => ThenBranch = branch;
    internal void SetElseBranch(EidosAstNode branch) { ElseBranch = branch; HasElse = true; }
}
