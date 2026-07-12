using System.Xml;
using Eidosc.Ast.Types;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 索引表达式
/// </summary>
/// <example>
/// arr[0]
/// map["key"]
/// matrix[i][j]
/// </example>
public record IndexExpr : Expression
{
    /// <summary>
    /// 被索引的对象
    /// </summary>
    public EidosAstNode? Object { get; private set; }

    /// <summary>
    /// 索引表达式
    /// </summary>
    public EidosAstNode? Index { get; private set; }

    /// <summary>
    /// 显式类型参数（用于 `f[T]` 语法）
    /// </summary>
    public List<TypeNode> TypeArgs { get; private set; } = [];

    public bool IsRecoveredMissingIndex { get; private set; }

    /// <summary>
    /// 是否为显式类型应用
    /// </summary>
    public bool IsTypeApplication => TypeArgs.Count > 0;

    /// <summary>
    /// 反糖化时清除类型参数列表
    /// </summary>
    internal void ClearTypeArgs() => TypeArgs.Clear();

    internal void SetTypeArgs(IEnumerable<TypeNode> typeArgs) => TypeArgs = [..typeArgs];

    /// <summary>
    /// 设置位置
    /// </summary>
    public void SetSpan(SourceSpan span) => Span = span;

    /// <summary>
    /// 设置被索引的对象
    /// </summary>
    public void SetObject(EidosAstNode obj) => Object = obj;

    /// <summary>
    /// 设置索引表达式
    /// </summary>
    public void SetIndex(EidosAstNode index) => Index = index;

    public void MarkRecoveredMissingIndex()
    {
        IsRecoveredMissingIndex = true;
        MarkRecovered(AstRecoveryReasons.ParserMissingIndexExpression);
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        TypeArgs.Clear();
        Object = null;
        Index = null;

        if (node is NonTerminalCstNode ntNode)
        {
            var expressions = new List<EidosAstNode>();

            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    // 处理字面量终端节点
                    if (IsLiteral(term))
                    {
                        expressions.Add(CreateLiteralFromTerminal(term));
                    }
                    // 处理标识符终端节点
                    else if (IsIdentifierTerminal(term))
                    {
                        expressions.Add(CreateIdentifierFromTerminal(term));
                    }
                    else if (IsTypeIdentifierTerminal(term))
                    {
                        TypeArgs.Add(CreateTypePathFromTerminal(term));
                    }
                }
                else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
                {
                    if (expr is TypeNode typeNode)
                    {
                        TypeArgs.Add(typeNode);
                    }
                    else
                    {
                        expressions.Add(expr);
                    }
                }
                else if (child is NonTerminalCstNode childNt)
                {
                    var childName = childNt.NonTerminal?.DebugName ?? "";

                    if (childName.EndsWith("Tail"))
                    {
                        var exprFromTail = ExtractExpressionFromTail(childNt);
                        if (exprFromTail != null)
                            expressions.Add(exprFromTail);
                    }
                    else if (IsExpressionNode(childName))
                    {
                        var createdExpr = TryCreateExpressionFromCst(childNt);
                        if (createdExpr != null)
                            expressions.Add(createdExpr);
                    }
                }
            }

            if (expressions.Count >= 1)
                Object = expressions[0];
            if (expressions.Count >= 2)
                Index = expressions[1];
        }
    }

    /// <summary>
    /// 从 tail 节点提取表达式
    /// </summary>
    private static EidosAstNode? ExtractExpressionFromTail(NonTerminalCstNode tailNode)
    {
        foreach (var child in tailNode.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (IsLiteral(term))
                    return CreateLiteralFromTerminal(term);
                if (IsIdentifierTerminal(term))
                    return CreateIdentifierFromTerminal(term);
            }
            else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
            {
                if (expr is not TypeNode)
                    return expr;
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var childName = childNt.NonTerminal?.DebugName ?? "";
                if (IsExpressionNode(childName))
                {
                    var createdExpr = TryCreateExpressionFromCst(childNt);
                    if (createdExpr != null)
                        return createdExpr;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 检查是否是表达式节点名称
    /// </summary>
    private static bool IsExpressionNode(string name)
    {
        return name.EndsWith("Expr") || name.EndsWith("Tail");
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
    /// 检查终端节点是否是类型标识符
    /// </summary>
    private static bool IsTypeIdentifierTerminal(TerminalCstNode term)
    {
        if (term.Terminal == null) return false;
        var terminalName = term.Terminal.ToString();
        return terminalName == WellKnownStrings.Terminals.TypeIdentifier;
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
    /// 从终端节点创建 TypePath
    /// </summary>
    private static TypePath CreateTypePathFromTerminal(TerminalCstNode term)
    {
        var typePath = new TypePath();
        typePath.SetSpan(term.Span);
        typePath.SetTypeName(GetTokenText(term));
        return typePath;
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
        var element = CreateElement(doc, WellKnownStrings.XmlElements.IndexExpr);

        if (Object != null)
        {
            var objElement = doc.CreateElement(WellKnownStrings.XmlElements.Object);
            objElement.AppendChild(Object.ToXmlElement(doc));
            element.AppendChild(objElement);
        }

        if (Index != null)
        {
            var indexElement = doc.CreateElement(WellKnownStrings.XmlElements.Index);
            indexElement.AppendChild(Index.ToXmlElement(doc));
            element.AppendChild(indexElement);
        }

        if (TypeArgs.Count > 0)
        {
            var typeArgsElement = doc.CreateElement(WellKnownStrings.XmlElements.TypeArgs);
            foreach (var typeArg in TypeArgs)
            {
                typeArgsElement.AppendChild(typeArg.ToXmlElement(doc));
            }

            element.AppendChild(typeArgsElement);
        }

        return element;
    }
}




