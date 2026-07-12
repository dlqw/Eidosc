using System.Xml;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 一元运算表达式
/// </summary>
/// <example>
/// -x
/// !flag
/// *refValue
/// </example>
public record UnaryExpr : Expression
{
    /// <summary>
    /// 运算符
    /// </summary>
    public UnaryOp Operator { get; private set; }

    /// <summary>
    /// 操作数
    /// </summary>
    public EidosAstNode? Operand { get; private set; }

    /// <summary>
    /// 设置 span
    /// </summary>
    public void SetSpan(SourceSpan span) => Span = span;

    /// <summary>
    /// 设置运算符
    /// </summary>
    public void SetOperator(UnaryOp op) => Operator = op;

    /// <summary>
    /// 设置操作数
    /// </summary>
    public void SetOperand(EidosAstNode operand) => Operand = operand;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var terminalName = term.Terminal?.DebugName ?? "";
                    var tokenText = GetTokenText(term);

                    if (terminalName == WellKnownStrings.Operators.Not || tokenText == WellKnownStrings.Operators.Not)
                    {
                        Operator = UnaryOp.Not;
                    }
                    else if (terminalName == WellKnownStrings.Operators.Subtract || tokenText == WellKnownStrings.Operators.Subtract)
                    {
                        Operator = UnaryOp.Negate;
                    }
                    else if (terminalName == WellKnownStrings.Operators.AddressOf || tokenText == WellKnownStrings.Operators.AddressOf)
                    {
                        Operator = UnaryOp.AddressOf;
                    }
                    else if (terminalName == WellKnownStrings.Operators.Ref || tokenText == WellKnownStrings.Operators.Ref)
                    {
                        Operator = UnaryOp.Ref;
                    }
                    else if (terminalName == WellKnownStrings.Operators.MRef || tokenText == WellKnownStrings.Operators.MRef)
                    {
                        Operator = UnaryOp.MRef;
                    }
                    // 检查是否是字面量 - 如果是，设置操作数
                    else if (IsLiteralTerminal(term))
                    {
                        Operand = CreateLiteralFromTerminal(term);
                    }
                    else if (terminalName == WellKnownStrings.Terminals.Identifier)
                    {
                        var ident = new IdentifierExpr();
                        ident.SetSpan(term.Span);
                        ident.SetName(tokenText);
                        Operand = ident;
                    }
                }
                else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
                {
                    Operand = expr;
                }
            }
        }
    }

    /// <summary>
    /// 检查终端节点是否是字面量
    /// </summary>
    private static bool IsLiteralTerminal(TerminalCstNode term)
    {
        if (term.Terminal == null) return false;
        var terminalName = term.Terminal.ToString();
        return terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean;
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

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.UnaryExpr);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Operator, Operator.ToSymbol());

        if (Operand != null)
        {
            var operandElement = doc.CreateElement(WellKnownStrings.XmlElements.Operand);
            operandElement.AppendChild(Operand.ToXmlElement(doc));
            element.AppendChild(operandElement);
        }

        return element;
    }
}





