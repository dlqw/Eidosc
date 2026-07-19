using System.Xml;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;

namespace Eidosc.Ast;

/// <summary>
/// 属性
/// </summary>
/// <example>
/// @[repr(c)]
/// @[derive(Eq, Show)]
/// </example>
public record Attribute : EidosAstNode
{
    /// <summary>
    /// 属性名称
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>
    /// 属性参数
    /// </summary>
    public List<EidosAstNode> Arguments { get; private set; } = [];

    /// <summary>
    /// 参数文本，用于 typed attribute group 的结构化绑定。
    /// </summary>
    public List<string> ArgumentTexts { get; private set; } = [];

    public DeclarationClause? TypedClause { get; private set; }

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
                    if (!IsPunctuation(text) && text != "@" && Name == "")
                    {
                        Name = text;
                    }
                }
                else if (child is NonTerminalCstNode childNt)
                {
                    CollectArguments(childNt, allowTokenFallback: true);
                }
            }
        }
    }

    private void CollectArguments(NonTerminalCstNode node, bool allowTokenFallback)
    {
        var initialArgCount = Arguments.Count;
        var initialTextCount = ArgumentTexts.Count;

        if (node.AstNode is EidosAstNode argNode && argNode is not Attribute)
        {
            Arguments.Add(argNode);

            var text = RenderArgumentText(argNode);
            if (!string.IsNullOrWhiteSpace(text))
            {
                ArgumentTexts.Add(text);
            }
            return;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                CollectArguments(childNt, allowTokenFallback: false);
            }
        }

        // 当表达式链在 CST 压缩后不再保留 AST 节点时，回退到终结符文本提取。
        if (allowTokenFallback &&
            Arguments.Count == initialArgCount &&
            ArgumentTexts.Count == initialTextCount)
        {
            var tokens = new List<string>();
            CollectTerminalTokens(node, tokens);
            if (tokens.Count > 0)
            {
                ArgumentTexts.Add(string.Concat(tokens));
            }
        }
    }

    private static void CollectTerminalTokens(ConcreteSyntaxNode node, List<string> tokens)
    {
        if (node is TerminalCstNode term)
        {
            var text = GetTokenText(term);
            if (!string.IsNullOrWhiteSpace(text) && text != "@")
            {
                tokens.Add(text);
            }
            return;
        }

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                CollectTerminalTokens(child, tokens);
            }
        }
    }

    private static string RenderArgumentText(EidosAstNode node)
    {
        return node switch
        {
            PathExpr path when path.Path.Count > 0 => string.Join(WellKnownStrings.Separators.Path, path.Path),
            IdentifierExpr ident => ident.Name,
            TypePath typePath when typePath.ModulePath.Count > 0 => string.Join(WellKnownStrings.Separators.Path, typePath.ModulePath) + WellKnownStrings.Separators.Path + typePath.TypeName,
            TypePath typePath => typePath.TypeName,
            LiteralExpr literal => literal.RawText,
            _ => ""
        };
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetName(string name) => Name = name;
    internal void AddArgumentText(string text) => ArgumentTexts.Add(text);
    internal void SetTypedClause(DeclarationClause clause) => TypedClause = clause;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.Attribute);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (Arguments.Count > 0)
        {
            var argsElement = doc.CreateElement(WellKnownStrings.XmlElements.Arguments);
            foreach (var arg in Arguments)
            {
                argsElement.AppendChild(arg.ToXmlElement(doc));
            }
            element.AppendChild(argsElement);
        }

        return element;
    }
}
