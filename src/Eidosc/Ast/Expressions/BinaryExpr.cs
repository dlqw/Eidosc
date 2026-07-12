using System.Xml;
using Eidosc.Utilities;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 二元运算表达式
/// </summary>
/// <example>
/// a + b
/// x * y
/// head +: tail
/// a && b
/// </example>
public record BinaryExpr : Expression
{
    /// <summary>
    /// 左操作数
    /// </summary>
    public EidosAstNode? Left { get; private set; }

    /// <summary>
    /// 运算符
    /// </summary>
    public BinaryOp Operator { get; private set; }

    /// <summary>
    /// 右操作数
    /// </summary>
    public EidosAstNode? Right { get; private set; }

    /// <summary>
    /// 设置 span
    /// </summary>
    public void SetSpan(Utils.SourceSpan span) => Span = span;

    /// <summary>
    /// 设置左操作数
    /// </summary>
    public void SetLeft(EidosAstNode left) => Left = left;

    /// <summary>
    /// 设置右操作数
    /// </summary>
    public void SetRight(EidosAstNode right) => Right = right;

    /// <summary>
    /// 设置运算符
    /// </summary>
    public void SetOperator(BinaryOp op) => Operator = op;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            var operands = new List<EidosAstNode>();
            var operators = new List<BinaryOp>();

            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    // 检查是否是运算符
                    if (IsOperator(text))
                    {
                        operators.Add(ParseBinaryOp(text));
                    }
                    // 否则可能是字面量
                    else if (IsLiteralTerminal(term))
                    {
                        operands.Add(CreateLiteralFromTerminal(term));
                    }
                    // 标识符
                    else if (term.Terminal?.ToString() == WellKnownStrings.Terminals.Identifier)
                    {
                        var ident = new IdentifierExpr();
                        ident.SetSpan(term.Span);
                        ident.SetName(text);
                        operands.Add(ident);
                    }
                }
                else if (child is NonTerminalCstNode childNt)
                {
                    var childName = childNt.NonTerminal?.DebugName ?? "";

                    // Tail 节点特殊处理 - 不管是否有 AstNode
                    if (childName.EndsWith("Tail"))
                    {
                        ProcessTailNode(childNt, operands, operators);
                    }
                    // Op 节点处理 (如 comparisonOp, additiveOp 等)
                    else if (childName.EndsWith("Op"))
                    {
                        ProcessOperatorNode(childNt, operators);
                    }
                    // 如果有 AstNode，直接使用
                    else if (childNt.AstNode is EidosAstNode expr)
                    {
                        operands.Add(expr);
                    }
                }
            }

            if (operands.Count == 0)
            {
                return;
            }

            if (operands.Count == 1)
            {
                Left = operands[0];
                Right = null;
                return;
            }

            var folded = BuildLeftAssociative(operands, operators, Span);
            if (folded is BinaryExpr binary)
            {
                Left = binary.Left;
                Right = binary.Right;
                Operator = binary.Operator;
            }
            else
            {
                Left = folded;
                Right = null;
            }
        }
    }

    /// <summary>
    /// 处理 tail 节点 (如 additiveTail, multiplicativeTail)
    /// </summary>
    private void ProcessTailNode(
        NonTerminalCstNode tailNode,
        List<EidosAstNode> operands,
        List<BinaryOp> operators)
    {
        foreach (var child in tailNode.Children)
        {
            if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (IsOperator(text))
                {
                    operators.Add(ParseBinaryOp(text));
                }
                else if (IsLiteralTerminal(term))
                {
                    operands.Add(CreateLiteralFromTerminal(term));
                }
                else if (term.Terminal?.ToString() == WellKnownStrings.Terminals.Identifier)
                {
                    var ident = new IdentifierExpr();
                    ident.SetSpan(term.Span);
                    ident.SetName(text);
                    operands.Add(ident);
                }
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var childName = childNt.NonTerminal?.DebugName ?? "";
                if (childName.EndsWith("Op"))
                {
                    ProcessOperatorNode(childNt, operators);
                    continue;
                }

                if (childNt.AstNode is EidosAstNode expr)
                {
                    operands.Add(expr);
                }
            }
        }
    }

    /// <summary>
    /// 处理运算符节点 (如 comparisonOp, additiveOp 等)
    /// </summary>
    private static void ProcessOperatorNode(NonTerminalCstNode opNode, List<BinaryOp> operators)
    {
        foreach (var child in opNode.Children)
        {
            if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (IsOperator(text))
                {
                    operators.Add(ParseBinaryOp(text));
                }
            }
        }
    }

    private static EidosAstNode BuildLeftAssociative(
        IReadOnlyList<EidosAstNode> operands,
        IReadOnlyList<BinaryOp> operators,
        Utils.SourceSpan span)
    {
        var current = operands[0];
        for (var index = 0; index < operands.Count - 1; index++)
        {
            var op = operators.Count > 0
                ? operators[Math.Min(index, operators.Count - 1)]
                : BinaryOp.Add;
            var binary = new BinaryExpr();
            binary.SetSpan(span);
            binary.SetLeft(current);
            binary.SetOperator(op);
            binary.SetRight(operands[index + 1]);
            current = binary;
        }

        return current;
    }

    /// <summary>
    /// 检查文本是否是运算符
    /// </summary>
    private static bool IsOperator(string text)
    {
        return text is WellKnownStrings.Operators.PipeForward or WellKnownStrings.Operators.Bind
            or WellKnownStrings.Operators.Fmap or WellKnownStrings.Operators.Ap or WellKnownStrings.Operators.Append
            or WellKnownStrings.Operators.ComposeRight or WellKnownStrings.Operators.ComposeLeft
            or WellKnownStrings.Operators.Coalesce
            or WellKnownStrings.Operators.Add or WellKnownStrings.Operators.Subtract
            or WellKnownStrings.Operators.Multiply or WellKnownStrings.Operators.Divide
            or WellKnownStrings.Operators.Modulo or WellKnownStrings.Operators.Prepend or WellKnownStrings.Operators.AppendLast or WellKnownStrings.Operators.Concat
            or WellKnownStrings.Operators.Less or WellKnownStrings.Operators.Greater
            or WellKnownStrings.Operators.LessEqual or WellKnownStrings.Operators.GreaterEqual
            or WellKnownStrings.Operators.Equal or WellKnownStrings.Operators.NotEqual
            or WellKnownStrings.Operators.And or WellKnownStrings.Operators.Or;
    }

    /// <summary>
    /// 检查终端节点是否是字面量
    /// </summary>
    private static bool IsLiteralTerminal(TerminalCstNode term)
    {
        if (term.Terminal == null) return false;
        var terminalName = term.Terminal.ToString();
        return terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String
            or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean;
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

    private static BinaryOp ParseBinaryOp(string text) => text switch
    {
        WellKnownStrings.Operators.PipeForward => BinaryOp.Pipe,
        WellKnownStrings.Operators.Bind => BinaryOp.Bind,
        WellKnownStrings.Operators.Coalesce => BinaryOp.Coalesce,
        WellKnownStrings.Operators.Fmap => BinaryOp.Fmap,
        WellKnownStrings.Operators.Ap => BinaryOp.Ap,
        WellKnownStrings.Operators.Append => BinaryOp.Append,
        WellKnownStrings.Operators.ComposeRight => BinaryOp.ComposeRight,
        WellKnownStrings.Operators.ComposeLeft => BinaryOp.ComposeLeft,
        WellKnownStrings.Operators.Multiply => BinaryOp.Multiply,
        WellKnownStrings.Operators.Divide => BinaryOp.Divide,
        WellKnownStrings.Operators.Modulo => BinaryOp.Modulo,
        WellKnownStrings.Operators.Add => BinaryOp.Add,
        WellKnownStrings.Operators.Subtract => BinaryOp.Subtract,
        WellKnownStrings.Operators.Concat => BinaryOp.Concat,
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
        var element = CreateElement(doc, WellKnownStrings.XmlElements.BinaryExpr);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Operator, Operator.ToSymbol());

        if (Left != null)
        {
            var leftElement = doc.CreateElement(WellKnownStrings.XmlElements.Left);
            leftElement.AppendChild(Left.ToXmlElement(doc));
            element.AppendChild(leftElement);
        }

        if (Right != null)
        {
            var rightElement = doc.CreateElement(WellKnownStrings.XmlElements.Right);
            rightElement.AppendChild(Right.ToXmlElement(doc));
            element.AppendChild(rightElement);
        }

        return element;
    }
}
