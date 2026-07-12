using System.Xml;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// 赋值语句
/// </summary>
/// <example>
/// counter := counter + 1;
/// </example>
public record Assignment : Declaration
{
    /// <summary>
    /// 目标变量名
    /// </summary>
    public string Target { get; private set; } = "";

    internal void SetTarget(string target) => Target = target;

    /// <summary>
    /// 目标表达式（左值）
    /// </summary>
    public EidosAstNode? TargetExpression { get; private set; }

    internal void SetTargetExpression(EidosAstNode target)
    {
        TargetExpression = target;
        if (target is Ast.Expressions.IdentifierExpr identifier)
        {
            Target = identifier.Name;
        }
    }

    /// <summary>
    /// 目标变量的符号 ID（名称解析后填充）
    /// </summary>
    public SymbolId TargetSymbolId { get; set; } = SymbolId.None;

    /// <summary>
    /// 赋值的表达式
    /// </summary>
    public EidosAstNode? Value { get; private set; }

    public void SetValue(EidosAstNode value) => Value = value;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            var foundTarget = false;

            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (!IsPunctuation(text) && !foundTarget && !IsKeyword(text))
                    {
                        Target = text;
                        foundTarget = true;
                    }
                }
                else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
                {
                    Value = expr;
                }
            }
        }
    }

    private static bool IsKeyword(string text)
    {
        return text is WellKnownStrings.Keywords.Let or WellKnownStrings.Keywords.Func or WellKnownStrings.Keywords.If or WellKnownStrings.Keywords.Else or WellKnownStrings.Keywords.Return;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.Assignment);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Target, Target);

        if (TargetExpression != null)
        {
            var targetElement = doc.CreateElement("Target");
            targetElement.AppendChild(TargetExpression.ToXmlElement(doc));
            element.AppendChild(targetElement);
        }

        if (Value != null)
        {
            var valueElement = doc.CreateElement(WellKnownStrings.XmlElements.Value);
            valueElement.AppendChild(Value.ToXmlElement(doc));
            element.AppendChild(valueElement);
        }

        return element;
    }
}
