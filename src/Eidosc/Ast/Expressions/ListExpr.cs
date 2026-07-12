using System.Xml;
using Eidosc.Ast.Types;
using Eidosc.Utilities;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 列表表达式
/// </summary>
/// <example>
/// []
/// [1, 2, 3]
/// [x, y, z]
/// </example>
public record ListExpr : Expression
{
    /// <summary>
    /// 元素列表
    /// </summary>
    public List<EidosAstNode> Elements { get; private set; } = [];

    public bool HasRest { get; private set; }

    /// <summary>
    /// 设置位置
    /// </summary>
    public void SetSpan(Utils.SourceSpan span) => Span = span;

    /// <summary>
    /// 添加元素
    /// </summary>
    public void AddElement(EidosAstNode element) => Elements.Add(element);

    internal void SetHasRest(bool value) => HasRest = value;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            CollectElements(ntNode);
        }
    }

    /// <summary>
    /// 递归收集列表元素
    /// </summary>
    private void CollectElements(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (IsDotDotToken(term))
                {
                    HasRest = true;
                    continue;
                }

                // 处理字面量终端节点
                if (IsLiteral(term))
                {
                    Elements.Add(CreateLiteralFromTerminal(term));
                }
                // 处理标识符终端节点
                else if (IsIdentifierTerminal(term))
                {
                    Elements.Add(CreateIdentifierFromTerminal(term));
                }
                // 跳过标点符号 (逗号、方括号等)
            }
            else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
            {
                // 有 AstNode 的非终结符（排除类型节点）
                if (expr is not TypeNode)
                {
                    Elements.Add(expr);
                }
            }
            else if (child is NonTerminalCstNode childNt)
            {
                // 没有 AstNode 的非终结符 (被 Squeezing/DisAstable 的节点)
                var nodeName = childNt.NonTerminal?.DebugName ?? "";

                // Tail 节点特殊处理 - 只提取表达式部分，跳过逗号
                if (nodeName.EndsWith("Tail"))
                {
                    CollectElementsFromTail(childNt);
                }
                // 检查是否是表达式节点
                else if (IsExpressionNode(nodeName))
                {
                    var createdExpr = TryCreateExpressionFromCst(childNt);
                    if (createdExpr != null)
                    {
                        Elements.Add(createdExpr);
                    }
                }
                else
                {
                    // 递归遍历其他非终结符
                    CollectElements(childNt);
                }
            }
        }
    }

    /// <summary>
    /// 从 tail 节点提取元素 (跳过逗号)
    /// </summary>
    private void CollectElementsFromTail(NonTerminalCstNode tailNode)
    {
        foreach (var child in tailNode.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (IsDotDotToken(term))
                {
                    HasRest = true;
                    continue;
                }

                // 只处理字面量和标识符，跳过逗号等标点
                if (IsLiteral(term))
                {
                    Elements.Add(CreateLiteralFromTerminal(term));
                }
                else if (IsIdentifierTerminal(term))
                {
                    Elements.Add(CreateIdentifierFromTerminal(term));
                }
            }
            else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
            {
                if (expr is not TypeNode)
                {
                    Elements.Add(expr);
                }
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var childName = childNt.NonTerminal?.DebugName ?? "";

                // 递归处理嵌套的 tail 节点
                if (childName.EndsWith("Tail"))
                {
                    CollectElementsFromTail(childNt);
                }
                else if (IsExpressionNode(childName))
                {
                    var createdExpr = TryCreateExpressionFromCst(childNt);
                    if (createdExpr != null)
                    {
                        Elements.Add(createdExpr);
                    }
                }
                else
                {
                    CollectElements(childNt);
                }
            }
        }
    }

    /// <summary>
    /// 检查是否是表达式节点名称
    /// </summary>
    private static bool IsExpressionNode(string name)
    {
        return name.EndsWith("Expr") || name.EndsWith("Tail");
    }

    private static bool IsDotDotToken(TerminalCstNode term)
    {
        var text = GetTokenText(term);
        return text == WellKnownStrings.Punctuation.DotDot ||
               (term.Token?.ToString()?.Contains("Token:..", StringComparison.Ordinal) ?? false);
    }

    /// <summary>
    /// 检查终端节点是否是字面量
    /// </summary>
    private static bool IsLiteral(TerminalCstNode term)
    {
        if (term.Terminal == null) return false;
        var terminalName = term.Terminal.ToString();
        return terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean;
    }

    /// <summary>
    /// 检查终端节点是否是标识符
    /// </summary>
    private static bool IsIdentifierTerminal(TerminalCstNode term)
    {
        if (term.Terminal == null) return false;
        var terminalName = term.Terminal.ToString();
        return terminalName == WellKnownStrings.Terminals.Identifier;
    }

    /// <summary>
    /// 从终端节点创建 LiteralExpr
    /// </summary>
    private static LiteralExpr CreateLiteralFromTerminal(TerminalCstNode term)
    {
        var literal = new LiteralExpr();
        literal.SetSpan(term.Span);

        string text;
        if (term.Token is ContentToken contentToken)
        {
            var textIdStr = contentToken.TextId.Resolve();
            if (textIdStr == "[string]" && contentToken.Value is string actualText)
            {
                text = actualText;
            }
            else
            {
                text = textIdStr;
            }
        }
        else
        {
            text = term.Token?.ToString() ?? "";
        }
        literal.SetLiteral(text);

        return literal;
    }

    /// <summary>
    /// 从终端节点创建 IdentifierExpr
    /// </summary>
    private static IdentifierExpr CreateIdentifierFromTerminal(TerminalCstNode term)
    {
        var identifier = new IdentifierExpr();
        identifier.SetSpan(term.Span);
        identifier.SetName(GetTokenText(term));
        return identifier;
    }

    /// <summary>
    /// 尝试从 CST 节点创建表达式
    /// </summary>
    private static EidosAstNode? TryCreateExpressionFromCst(NonTerminalCstNode node)
    {
        var nodeName = node.NonTerminal?.DebugName ?? "";

        if (nodeName == "unaryExpr")
        {
            return CreateUnaryExprFromCst(node);
        }

        // 二元表达式
        if (IsExpressionNode(nodeName))
        {
            return CreateBinaryExprFromCst(node);
        }

        return null;
    }

    /// <summary>
    /// 从 CST 创建 UnaryExpr
    /// </summary>
    private static UnaryExpr CreateUnaryExprFromCst(NonTerminalCstNode node)
    {
        var unaryExpr = new UnaryExpr();
        unaryExpr.SetSpan(node.Span);

        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                var terminalName = term.Terminal?.DebugName ?? "";
                var tokenText = GetTokenText(term);

                if (terminalName == WellKnownStrings.Operators.Not || tokenText == WellKnownStrings.Operators.Not)
                    unaryExpr.SetOperator(UnaryOp.Not);
                else if (terminalName == WellKnownStrings.Operators.Subtract || tokenText == WellKnownStrings.Operators.Subtract)
                    unaryExpr.SetOperator(UnaryOp.Negate);
                else if (terminalName == WellKnownStrings.Operators.AddressOf || tokenText == WellKnownStrings.Operators.AddressOf)
                    unaryExpr.SetOperator(UnaryOp.AddressOf);
                else if (terminalName == WellKnownStrings.Operators.Ref || tokenText == WellKnownStrings.Operators.Ref)
                    unaryExpr.SetOperator(UnaryOp.Ref);
                else if (terminalName == WellKnownStrings.Operators.MRef || tokenText == WellKnownStrings.Operators.MRef)
                    unaryExpr.SetOperator(UnaryOp.MRef);
                else if (IsLiteral(term))
                    unaryExpr.SetOperand(CreateLiteralFromTerminal(term));
                else if (IsIdentifierTerminal(term))
                    unaryExpr.SetOperand(CreateIdentifierFromTerminal(term));
            }
            else if (child is NonTerminalCstNode childNt)
            {
                if (childNt.AstNode is EidosAstNode expr)
                {
                    unaryExpr.SetOperand(expr);
                }
                else if (IsExpressionNode(childNt.NonTerminal?.DebugName ?? ""))
                {
                    var nestedExpr = TryCreateExpressionFromCst(childNt);
                    if (nestedExpr != null)
                        unaryExpr.SetOperand(nestedExpr);
                }
            }
        }

        return unaryExpr;
    }

    /// <summary>
    /// 从 CST 创建 BinaryExpr
    /// </summary>
    private static BinaryExpr CreateBinaryExprFromCst(NonTerminalCstNode node)
    {
        var binaryExpr = new BinaryExpr();
        binaryExpr.SetSpan(node.Span);

        var expressions = new List<EidosAstNode?>();

        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (IsOperator(text))
                {
                    binaryExpr.SetOperator(ParseBinaryOp(text));
                }
                else if (IsLiteral(term))
                {
                    expressions.Add(CreateLiteralFromTerminal(term));
                }
                else if (IsIdentifierTerminal(term))
                {
                    expressions.Add(CreateIdentifierFromTerminal(term));
                }
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var childName = childNt.NonTerminal?.DebugName ?? "";

                if (childNt.AstNode is EidosAstNode expr)
                {
                    expressions.Add(expr);
                }
                else if (childName.EndsWith("Tail"))
                {
                    ProcessTailNode(childNt, expressions, binaryExpr);
                }
                else if (childName.EndsWith("Op"))
                {
                    ProcessOperatorNode(childNt, binaryExpr);
                }
                else if (IsExpressionNode(childName))
                {
                    var nestedExpr = CreateBinaryExprFromCst(childNt);
                    expressions.Add(nestedExpr);
                }
            }
        }

        if (expressions.Count >= 1)
            binaryExpr.SetLeft(expressions[0]!);
        if (expressions.Count >= 2)
            binaryExpr.SetRight(expressions[1]!);

        return binaryExpr;
    }

    /// <summary>
    /// 处理运算符节点
    /// </summary>
    private static void ProcessOperatorNode(NonTerminalCstNode opNode, BinaryExpr binaryExpr)
    {
        foreach (var child in opNode.Children)
        {
            if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (IsOperator(text))
                {
                    binaryExpr.SetOperator(ParseBinaryOp(text));
                }
            }
        }
    }

    /// <summary>
    /// 处理 tail 节点
    /// </summary>
    private static void ProcessTailNode(NonTerminalCstNode tailNode, List<EidosAstNode?> expressions, BinaryExpr binaryExpr)
    {
        foreach (var child in tailNode.Children)
        {
            if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (IsOperator(text))
                {
                    binaryExpr.SetOperator(ParseBinaryOp(text));
                }
                else if (IsLiteral(term))
                {
                    expressions.Add(CreateLiteralFromTerminal(term));
                }
                else if (IsIdentifierTerminal(term))
                {
                    expressions.Add(CreateIdentifierFromTerminal(term));
                }
            }
            else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
            {
                expressions.Add(expr);
            }
            else if (child is NonTerminalCstNode childNt && IsExpressionNode(childNt.NonTerminal?.DebugName ?? ""))
            {
                var nestedExpr = CreateBinaryExprFromCst(childNt);
                expressions.Add(nestedExpr);
            }
        }
    }

    /// <summary>
    /// 检查文本是否是运算符
    /// </summary>
    private static bool IsOperator(string text)
    {
        return text is WellKnownStrings.Operators.Add or WellKnownStrings.Operators.Subtract or WellKnownStrings.Operators.Multiply or WellKnownStrings.Operators.Divide or WellKnownStrings.Operators.Modulo or WellKnownStrings.Operators.Prepend or WellKnownStrings.Operators.AppendLast
            or WellKnownStrings.Operators.Less or WellKnownStrings.Operators.Greater or WellKnownStrings.Operators.LessEqual or WellKnownStrings.Operators.GreaterEqual or WellKnownStrings.Operators.Equal or WellKnownStrings.Operators.NotEqual
            or WellKnownStrings.Operators.And or WellKnownStrings.Operators.Or;
    }

    private static BinaryOp ParseBinaryOp(string text) => text switch
    {
        WellKnownStrings.Operators.Multiply => BinaryOp.Multiply,
        WellKnownStrings.Operators.Divide => BinaryOp.Divide,
        WellKnownStrings.Operators.Modulo => BinaryOp.Modulo,
        WellKnownStrings.Operators.Add => BinaryOp.Add,
        WellKnownStrings.Operators.Subtract => BinaryOp.Subtract,
        WellKnownStrings.Operators.Prepend => BinaryOp.Prepend,
        WellKnownStrings.Operators.AppendLast => BinaryOp.AppendLast,
        WellKnownStrings.Operators.Less => BinaryOp.Less,
        WellKnownStrings.Operators.Greater => BinaryOp.Greater,
        WellKnownStrings.Operators.LessEqual => BinaryOp.LessEqual,
        WellKnownStrings.Operators.GreaterEqual => BinaryOp.GreaterEqual,
        WellKnownStrings.Operators.Equal => BinaryOp.Equal,
        WellKnownStrings.Operators.NotEqual => BinaryOp.NotEqual,
        WellKnownStrings.Operators.And => BinaryOp.And,
        WellKnownStrings.Operators.Or => BinaryOp.Or,
        _ => BinaryOp.Add
    };

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.ListExpr);

        foreach (var elem in Elements)
        {
            var elemElement = doc.CreateElement(WellKnownStrings.XmlElements.Element);
            elemElement.AppendChild(elem.ToXmlElement(doc));
            element.AppendChild(elemElement);
        }

        return element;
    }
}




