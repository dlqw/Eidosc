using System.Xml;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Utilities;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 列表推导式（Haskell 风格）
/// </summary>
/// <example>
/// [x * 2 | x <- [1, 2, 3]]
/// [(x, y) | x <- [1, 2, 3], y <- [4, 5], x + y > 5]
/// </example>
public record ListComprehension : Expression
{
    /// <summary>
    /// 输出表达式
    /// </summary>
    public EidosAstNode? Output { get; private set; }

    /// <summary>
    /// 限定符列表（生成器和守卫）
    /// </summary>
    public List<Qualifier> Qualifiers { get; private set; } = [];

    internal void SetOutput(EidosAstNode? output) => Output = output;
    internal void SetQualifiers(List<Qualifier> qualifiers) => Qualifiers = qualifiers;
    internal void AddQualifier(Qualifier qualifier) => Qualifiers.Add(qualifier);

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            var foundPipe = false;

            foreach (var child in ntNode.Children)
            {
                // 检查是否是管道符号
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (text == WellKnownStrings.Punctuation.Pipe)
                    {
                        foundPipe = true;
                        continue;
                    }
                }

                if (!foundPipe)
                {
                    // 管道之前 - 输出表达式
                    if (child is TerminalCstNode outputTerm)
                    {
                        if (IsLiteral(outputTerm))
                        {
                            Output = CreateLiteralFromTerminal(outputTerm);
                        }
                        else if (IsIdentifierTerminal(outputTerm))
                        {
                            Output = CreateIdentifierFromTerminal(outputTerm);
                        }
                    }
                    else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
                    {
                        if (expr is not TypeNode)
                            Output = expr;
                    }
                    else if (child is NonTerminalCstNode childNt)
                    {
                        var nodeName = childNt.NonTerminal?.DebugName ?? "";
                        if (IsExpressionNode(nodeName))
                        {
                            var createdExpr = TryCreateExpressionFromCst(childNt);
                            if (createdExpr != null)
                                Output = createdExpr;
                        }
                        else
                        {
                            // 递归查找
                            ExtractOutputExpression(childNt);
                        }
                    }
                }
                else
                {
                    // 管道之后 - 限定符
                    CollectQualifiers(child);
                }
            }
        }
    }

    private void CollectQualifiers(ConcreteSyntaxNode node)
    {
        if (node is NonTerminalCstNode { AstNode: Qualifier qualifier })
        {
            Qualifiers.Add(qualifier);
            return;
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        var nodeName = ntNode.NonTerminal?.DebugName ?? "";
        if (nodeName is WellKnownStrings.Keywords.Qualifier or WellKnownStrings.Keywords.Generator)
        {
            var createdQualifier = CreateQualifierFromCst(ntNode);
            if (createdQualifier != null)
            {
                Qualifiers.Add(createdQualifier);
            }

            foreach (var child in ntNode.Children)
            {
                if (child is NonTerminalCstNode childNt &&
                    string.Equals(childNt.NonTerminal?.DebugName, "qualifierTail", StringComparison.Ordinal))
                {
                    CollectQualifiers(childNt);
                }
            }

            return;
        }

        if (nodeName == "qualifierTail")
        {
            foreach (var child in ntNode.Children)
            {
                CollectQualifiers(child);
            }

            return;
        }

        if (ntNode.AstNode is EidosAstNode expr && expr is not TypeNode)
        {
            Qualifiers.Add(new Qualifier
            {
                GuardExpression = expr,
                Kind = QualifierKind.Guard
            });

            return;
        }

        if (IsExpressionNode(nodeName))
        {
            var guardExpr = TryCreateExpressionFromCst(ntNode);
            if (guardExpr != null)
            {
                Qualifiers.Add(new Qualifier
                {
                    GuardExpression = guardExpr,
                    Kind = QualifierKind.Guard
                });
            }

            return;
        }

        foreach (var child in ntNode.Children)
        {
            CollectQualifiers(child);
        }
    }

    /// <summary>
    /// 递归提取输出表达式
    /// </summary>
    private void ExtractOutputExpression(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (IsLiteral(term))
                {
                    Output = CreateLiteralFromTerminal(term);
                    return;
                }
                if (IsIdentifierTerminal(term))
                {
                    Output = CreateIdentifierFromTerminal(term);
                    return;
                }
            }
            else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
            {
                if (expr is not TypeNode)
                {
                    Output = expr;
                    return;
                }
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var nodeName = childNt.NonTerminal?.DebugName ?? "";
                if (IsExpressionNode(nodeName))
                {
                    var createdExpr = TryCreateExpressionFromCst(childNt);
                    if (createdExpr != null)
                    {
                        Output = createdExpr;
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 从 CST 创建 Qualifier
    /// </summary>
    private Qualifier? CreateQualifierFromCst(NonTerminalCstNode node)
    {
        var nodeName = node.NonTerminal?.DebugName ?? "";

        // qualifierTail 包含逗号，需要跳过
        if (nodeName == "qualifierTail")
        {
            foreach (var child in node.Children)
            {
                if (child is NonTerminalCstNode childNt)
                {
                    var childName = childNt.NonTerminal?.DebugName ?? "";
                    if (childName == WellKnownStrings.Keywords.Qualifier || childName == WellKnownStrings.Keywords.Generator)
                    {
                        return CreateQualifierFromCst(childNt);
                    }
                    else if (childNt.AstNode is EidosAstNode expr && expr is not TypeNode)
                    {
                        return new Qualifier
                        {
                            GuardExpression = expr,
                            Kind = QualifierKind.Guard
                        };
                    }
                    else if (IsExpressionNode(childName))
                    {
                        // 守卫表达式
                        var guardExpr = TryCreateExpressionFromCst(childNt);
                        if (guardExpr != null)
                        {
                            return new Qualifier { GuardExpression = guardExpr, Kind = QualifierKind.Guard };
                        }
                    }
                }
            }
            return null;
        }

        // 检查是否是生成器 (pattern <- expr)
        var qualifier = new Qualifier();
        var hasLeftArrow = false;

        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (text == WellKnownStrings.Punctuation.LeftArrow)
                {
                    hasLeftArrow = true;
                    qualifier.Kind = QualifierKind.Generator;
                }
                else if (IsIdentifierTerminal(term) && qualifier.GeneratorPattern == null && !hasLeftArrow)
                {
                    // 在 <- 之前的标识符是生成器模式变量
                    var varPattern = new VarPattern();
                    varPattern.SetSpan(term.Span);
                    varPattern.SetName(text);
                    qualifier.GeneratorPattern = varPattern;
                    qualifier.Kind = QualifierKind.Generator;
                }
                else if (hasLeftArrow && IsIdentifierTerminal(term) && qualifier.GeneratorExpression == null)
                {
                    // 在 <- 之后的标识符是生成器源表达式（例如 x <- nums）
                    qualifier.GeneratorExpression = CreateIdentifierFromTerminal(term);
                    qualifier.Kind = QualifierKind.Generator;
                }
                else if (hasLeftArrow && IsLiteral(term) && qualifier.GeneratorExpression == null)
                {
                    qualifier.GeneratorExpression = CreateLiteralFromTerminal(term);
                    qualifier.Kind = QualifierKind.Generator;
                }
            }
            else if (child is NonTerminalCstNode { AstNode: Pattern pat })
            {
                qualifier.GeneratorPattern = pat;
                qualifier.Kind = QualifierKind.Generator;
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var childName = childNt.NonTerminal?.DebugName ?? "";

                if (childName == WellKnownStrings.Keywords.Generator)
                {
                    return CreateQualifierFromCst(childNt);
                }
                else if (childNt.AstNode is Pattern pat2)
                {
                    qualifier.GeneratorPattern = pat2;
                    qualifier.Kind = QualifierKind.Generator;
                }
                else if (childNt.AstNode is EidosAstNode expr)
                {
                    if (expr is not TypeNode)
                    {
                        if (hasLeftArrow || qualifier.GeneratorPattern != null || qualifier.Kind == QualifierKind.Generator)
                        {
                            qualifier.GeneratorExpression = expr;
                            qualifier.Kind = QualifierKind.Generator;
                        }
                        else
                        {
                            qualifier.GuardExpression = expr;
                            qualifier.Kind = QualifierKind.Guard;
                        }
                    }
                }
                else if (IsExpressionNode(childName))
                {
                    var createdExpr = TryCreateExpressionFromCst(childNt);
                    if (createdExpr != null)
                    {
                        if (hasLeftArrow || qualifier.Kind == QualifierKind.Generator)
                        {
                            qualifier.GeneratorExpression = createdExpr;
                        }
                        else
                        {
                            qualifier.GuardExpression = createdExpr;
                            qualifier.Kind = QualifierKind.Guard;
                        }
                    }
                }
                else if (IsPatternNode(childName))
                {
                    var createdPat = TryCreatePatternFromCst(childNt);
                    if (createdPat != null)
                    {
                        qualifier.GeneratorPattern = createdPat;
                        qualifier.Kind = QualifierKind.Generator;
                    }
                }
            }
        }

        // 如果没有设置类型，默认为守卫
        if (qualifier.Kind == default && qualifier.GuardExpression != null)
        {
            qualifier.Kind = QualifierKind.Guard;
        }

        return qualifier;
    }

    /// <summary>
    /// 尝试从 CST 创建 Pattern
    /// </summary>
    private static Pattern? TryCreatePatternFromCst(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term && IsIdentifierTerminal(term))
            {
                var varPattern = new VarPattern();
                varPattern.SetSpan(term.Span);
                varPattern.SetName(GetTokenText(term));
                return varPattern;
            }
            else if (child is NonTerminalCstNode { AstNode: Pattern pattern })
            {
                return pattern;
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var nested = TryCreatePatternFromCst(childNt);
                if (nested != null)
                    return nested;
            }
        }
        return null;
    }

    /// <summary>
    /// 检查是否是模式节点
    /// </summary>
    private static bool IsPatternNode(string name)
    {
        return name.EndsWith(WellKnownStrings.XmlElements.Pattern) || name == "pattern";
    }

    /// <summary>
    /// 检查是否是表达式节点
    /// </summary>
    private static bool IsExpressionNode(string name)
    {
        return name.EndsWith("Expr") || name.EndsWith("Tail") || name.EndsWith("Inner");
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

        if (nodeName == "listExpr")
        {
            return CreateListExprFromCst(node);
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
    /// 从 CST 创建 ListExpr
    /// </summary>
    private static ListExpr CreateListExprFromCst(NonTerminalCstNode node)
    {
        var listExpr = new ListExpr();
        listExpr.SetSpan(node.Span);

        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (IsLiteral(term))
                    listExpr.AddElement(CreateLiteralFromTerminal(term));
                else if (IsIdentifierTerminal(term))
                    listExpr.AddElement(CreateIdentifierFromTerminal(term));
            }
            else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
            {
                if (expr is not TypeNode)
                    listExpr.AddElement(expr);
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var childName = childNt.NonTerminal?.DebugName ?? "";
                if (IsExpressionNode(childName))
                {
                    var nestedExpr = TryCreateExpressionFromCst(childNt);
                    if (nestedExpr != null)
                        listExpr.AddElement(nestedExpr);
                }
            }
        }

        return listExpr;
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
        return text is WellKnownStrings.Operators.Add or WellKnownStrings.Operators.Subtract or WellKnownStrings.Operators.Multiply or WellKnownStrings.Operators.Divide or WellKnownStrings.Operators.Modulo or WellKnownStrings.Operators.Prepend or WellKnownStrings.Operators.AppendLast or WellKnownStrings.Operators.Concat
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
        var element = CreateElement(doc, WellKnownStrings.XmlElements.ListComprehension);

        if (Output != null)
        {
            var outputElement = doc.CreateElement(WellKnownStrings.XmlElements.Output);
            outputElement.AppendChild(Output.ToXmlElement(doc));
            element.AppendChild(outputElement);
        }

        foreach (var qualifier in Qualifiers)
        {
            element.AppendChild(qualifier.ToXmlElement(doc));
        }

        return element;
    }
}

/// <summary>
/// 限定符（生成器或守卫）
/// </summary>
public record Qualifier : EidosAstNode
{
    /// <summary>
    /// 限定符类型
    /// </summary>
    public QualifierKind Kind { get; set; }

    /// <summary>
    /// 生成器模式（仅生成器有效）
    /// </summary>
    public Pattern? GeneratorPattern { get; set; }

    /// <summary>
    /// 生成器表达式（仅生成器有效）
    /// </summary>
    public EidosAstNode? GeneratorExpression { get; set; }

    /// <summary>
    /// 守卫表达式（仅守卫有效）
    /// </summary>
    public EidosAstNode? GuardExpression { get; set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (text == WellKnownStrings.Punctuation.LeftArrow)
                    {
                        Kind = QualifierKind.Generator;
                    }
                }
                else if (child is NonTerminalCstNode { AstNode: Pattern pattern })
                {
                    GeneratorPattern = Pattern.NormalizePatternNode(pattern);
                    Kind = QualifierKind.Generator;
                }
                else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
                {
                    if (Kind == QualifierKind.Generator)
                    {
                        GeneratorExpression = expr;
                    }
                    else
                    {
                        GuardExpression = expr;
                        Kind = QualifierKind.Guard;
                    }
                }
            }
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.Qualifier);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Kind, Kind.ToString());

        if (GeneratorPattern != null)
        {
            var patternElement = doc.CreateElement(WellKnownStrings.XmlElements.GeneratorPattern);
            patternElement.AppendChild(GeneratorPattern.ToXmlElement(doc));
            element.AppendChild(patternElement);
        }

        if (GeneratorExpression != null)
        {
            var exprElement = doc.CreateElement(WellKnownStrings.XmlElements.GeneratorExpression);
            exprElement.AppendChild(GeneratorExpression.ToXmlElement(doc));
            element.AppendChild(exprElement);
        }

        if (GuardExpression != null)
        {
            var guardElement = doc.CreateElement(WellKnownStrings.XmlElements.GuardExpression);
            guardElement.AppendChild(GuardExpression.ToXmlElement(doc));
            element.AppendChild(guardElement);
        }

        return element;
    }
}

/// <summary>
/// 限定符类型
/// </summary>
public enum QualifierKind
{
    Generator,  // x <- [1, 2, 3]
    Guard       // x > 0
}




