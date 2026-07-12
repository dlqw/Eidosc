using System.Xml;
using Eidosc.Ast.Declarations;

namespace Eidosc.Ast.Types;

/// <summary>
/// 箭头类型（函数类型）
/// </summary>
/// <example>
/// Int -> String
/// (Int, String) -> Bool
/// </example>
public record ArrowType : TypeNode
{
    /// <summary>
    /// 参数类型
    /// </summary>
    public TypeNode ParamType { get; private set; } = null!;

    /// <summary>
    /// 返回类型
    /// </summary>
    public TypeNode ReturnType { get; private set; } = null!;

    /// <summary>
    /// Effects performed when this arrow is invoked.
    /// </summary>
    public List<EffectRequirementNode> RequiredEffects { get; private set; } = [];

    internal void SetSpan(Utils.SourceSpan span) => Span = span;

    internal void SetParamType(TypeNode paramType) => ParamType = paramType;

    internal void SetReturnType(TypeNode returnType) => ReturnType = returnType;

    internal void SetRequiredEffects(IEnumerable<EffectRequirementNode> requiredEffects) =>
        RequiredEffects = [.. requiredEffects];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            // arrowType 的 CST 结构: child0 (类型) -> child1 (->) -> child2 (类型)
            // 由于 Squeezing，类型可能是:
            // - TerminalCstNode (typeIdentifier，被压缩的 typePath)
            // - NonTerminalCstNode with TypeNode AST (嵌套的 ArrowType, TupleType 等)

            var children = ntNode.Children;
            if (children.Count >= 3)
            {
                // 第一个子节点是 ParamType
                var paramType = ExtractTypeNode(children[0]);
                if (paramType != null)
                {
                    ParamType = paramType;
                }

                // 第三个子节点是 ReturnType（跳过 WellKnownStrings.Punctuation.RightArrow 终端）
                var returnType = ExtractTypeNode(children[2]);
                if (returnType != null)
                {
                    ReturnType = returnType;
                }
            }
        }
    }

    /// <summary>
    /// 从 CST 节点提取 TypeNode
    /// </summary>
    private TypeNode? ExtractTypeNode(ConcreteSyntaxNode node)
    {
        // 情况 1: NonTerminal 且有 TypeNode AST（如嵌套的 ArrowType）
        if (node is NonTerminalCstNode { AstNode: TypeNode typeNode })
        {
            return typeNode;
        }

        // 情况 2: Terminal (typeIdentifier)，创建 TypePath
        if (node is TerminalCstNode term)
        {
            var text = GetTokenText(term);
            if (!IsPunctuation(text))
            {
                var typePath = new TypePath();
                typePath.SetTypeName(text);
                typePath.SetSpan(term.Span);
                return typePath;
            }
        }

        // 情况 3: NonTerminal 但没有 AST（如 DisAstable 的 type 节点），递归查找
        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                var extracted = ExtractTypeNode(child);
                if (extracted != null)
                {
                    return extracted;
                }
            }
        }

        return null;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.ArrowType);

        if (ParamType != null)
        {
            var paramElement = doc.CreateElement(WellKnownStrings.XmlElements.ParamType);
            paramElement.AppendChild(ParamType.ToXmlElement(doc));
            element.AppendChild(paramElement);
        }

        if (ReturnType != null)
        {
            var returnElement = doc.CreateElement(WellKnownStrings.XmlElements.ReturnType);
            returnElement.AppendChild(ReturnType.ToXmlElement(doc));
            element.AppendChild(returnElement);
        }

        if (RequiredEffects.Count > 0)
        {
            var effectsElement = doc.CreateElement(WellKnownStrings.XmlElements.RequiredAbilities);
            foreach (var effect in RequiredEffects)
            {
                effectsElement.AppendChild(effect.ToXmlElement(doc));
            }
            element.AppendChild(effectsElement);
        }

        return element;
    }
}
