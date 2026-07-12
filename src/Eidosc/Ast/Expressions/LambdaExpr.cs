using System.Xml;
using Eidosc.Ast.Patterns;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// Lambda 表达式
/// </summary>
/// <example>
/// fn(x) x + 1
/// fn(x, y) { let z = x + y; z * 2 }
/// </example>
public record LambdaExpr : Expression
{
    /// <summary>
    /// 参数列表（模式）
    /// </summary>
    public List<Pattern> Parameters { get; private set; } = [];

    /// <summary>
    /// Lambda 体
    /// </summary>
    public EidosAstNode? Body { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Parameters.Clear();
        Body = null;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (text == WellKnownStrings.Keywords.Fn)
                    {
                        continue;
                    }

                    if (Parameters.Count > 0 &&
                        Body == null &&
                        !IsPunctuation(text))
                    {
                        Body = CreateBodyExpressionFromTerminal(term);
                    }

                    continue;
                }

                if (child is NonTerminalCstNode childNt)
                {
                    // 检查是否是表达式（lambda 体）
                    if (childNt.AstNode is Expression expr && Body == null)
                    {
                        Body = expr;
                        continue;
                    }

                    if (Body == null)
                    {
                        CollectParameters(childNt);
                    }
                }
            }
        }
    }

    private void CollectParameters(NonTerminalCstNode node)
    {
        // 如果节点有 Pattern AST，直接添加
        if (node.AstNode is Pattern pattern)
        {
            Parameters.Add(Pattern.NormalizePatternNode(pattern));
            return;
        }

        // 递归查找
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                CollectParameters(childNt);
            }
        }
    }

    private static EidosAstNode CreateBodyExpressionFromTerminal(TerminalCstNode terminal)
    {
        var text = GetTokenText(terminal);
        if (!LooksLikeLiteralToken(text))
        {
            var identifier = new IdentifierExpr();
            identifier.SetSpan(terminal.Span);
            identifier.SetName(text);
            return identifier;
        }

        var literal = new LiteralExpr();
        literal.SetSpan(terminal.Span);
        literal.SetLiteral(text);
        return literal;
    }

    private static bool LooksLikeLiteralToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text is WellKnownStrings.AdditionalKeywords.True or WellKnownStrings.AdditionalKeywords.False)
        {
            return true;
        }

        if ((text.StartsWith('"') && text.EndsWith('"')) ||
            (text.StartsWith('\'') && text.EndsWith('\'')))
        {
            return true;
        }

        return double.TryParse(text, out _);
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.LambdaExpr);

        if (Parameters.Count > 0)
        {
            var paramsElement = doc.CreateElement(WellKnownStrings.XmlElements.Parameters);
            foreach (var param in Parameters)
            {
                paramsElement.AppendChild(param.ToXmlElement(doc));
            }
            element.AppendChild(paramsElement);
        }

        if (Body != null)
        {
            var bodyElement = doc.CreateElement(WellKnownStrings.XmlElements.Body);
            bodyElement.AppendChild(Body.ToXmlElement(doc));
            element.AppendChild(bodyElement);
        }

        return element;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void AddParameter(Pattern param) => Parameters.Add(Pattern.NormalizePatternNode(param));
    internal void SetBody(EidosAstNode body) => Body = body;
}
