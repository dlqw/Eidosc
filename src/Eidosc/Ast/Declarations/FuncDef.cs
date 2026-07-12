using System.Xml;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// 函数定义
/// </summary>
/// <example>
/// func length[T]: Seq[T] -> Int
/// {
///     Nil => 0,
///     Cons{head: _, tail: rest} => 1 + length(rest)
/// }
/// </example>
public record FuncDef : Declaration
{
    /// <summary>
    /// 函数名称
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>
    /// 类型参数
    /// </summary>
    public List<TypeParam> TypeParams { get; private set; } = [];

    /// <summary>
    /// 签名（类型列表）
    /// </summary>
    public List<TypeNode> Signature { get; private set; } = [];

    public List<EffectRequirementNode> RequiredAbilities { get; private set; } = [];

    public bool IsComptime { get; private set; }

    /// <summary>
    /// 函数体（模式分支列表）
    /// </summary>
    public List<PatternBranch> Body { get; private set; } = [];

    public bool IsPatternBodyExhaustive { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ExtractAttributes(context, node);
        ExtractExportModifier(node);

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (!IsPunctuation(text) && Name == "" && !IsKeyword(text))
                    {
                        Name = text;
                    }
                }
                else if (child is NonTerminalCstNode childNt)
                {
                    if (childNt.NonTerminal?.DebugName == WellKnownStrings.Keywords.Signature)
                    {
                        if (FunctionSignatureBuilder.BuildFromSignatureNode(childNt) is { } signature)
                        {
                            Signature.Add(signature);
                        }

                        continue;
                    }

                    if (childNt.NonTerminal?.DebugName == "funcBody")
                    {
                        CollectFuncBody(childNt);
                        continue;
                    }

                    if (childNt.NonTerminal?.DebugName == "needClause")
                    {
                        CollectNeedClause(childNt);
                        continue;
                    }

                    CollectFromNode(childNt);
                }
            }
        }
    }

    private void CollectFromNode(NonTerminalCstNode node)
    {
        var nodeName = node.NonTerminal.ToString();
        if (nodeName is WellKnownStrings.Keywords.Attribute or WellKnownStrings.Keywords.AttributeArgs or WellKnownStrings.Keywords.Signature)
        {
            // 忽略函数属性子树，避免将 @impl(...) 的类型路径误收集到函数签名。
            return;
        }

        if (node.AstNode is TypeParam typeParam)
        {
            TypeParams.Add(typeParam);
            // TypeParam 不需要递归遍历子节点
            return;
        }

        if (node.AstNode is TypeNode typeNode)
        {
            Signature.Add(typeNode);
            // TypeNode 不需要递归遍历子节点，避免将嵌套类型添加到 Signature
            return;
        }

        // 只对未知类型的节点递归遍历子节点
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                CollectFromNode(childNt);
            }
        }
    }

    private void CollectNeedClause(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode { AstNode: EffectRequirementNode requirement })
            {
                RequiredAbilities.Add(requirement);
                continue;
            }

            if (child is NonTerminalCstNode childNt)
            {
                CollectNeedClause(childNt);
            }
        }
    }

    private void CollectFuncBody(NonTerminalCstNode funcBodyNode)
    {
        foreach (var child in funcBodyNode.Children)
        {
            if (child is NonTerminalCstNode { AstNode: PatternBranch branch })
            {
                Body.Add(branch);
            }
            else if (child is NonTerminalCstNode childNt &&
                     childNt.NonTerminal?.DebugName == "patternBranchStarTail")
            {
                CollectFuncBodyTail(childNt);
            }
        }
    }

    private void CollectFuncBodyTail(NonTerminalCstNode tailNode)
    {
        foreach (var child in tailNode.Children)
        {
            if (child is NonTerminalCstNode { AstNode: PatternBranch branch })
            {
                Body.Add(branch);
            }
            else if (child is NonTerminalCstNode childNt &&
                     childNt.NonTerminal?.DebugName == "patternBranchStarTail")
            {
                CollectFuncBodyTail(childNt);
            }
        }
    }

    private static bool IsKeyword(string text)
    {
        return text is WellKnownStrings.Keywords.Export or WellKnownStrings.Keywords.Func or WellKnownStrings.Keywords.Let;
    }

    internal void SetName(string name) => Name = name;
    internal void SetTypeParams(List<Eidosc.Ast.Types.TypeParam> typeParams) => TypeParams = typeParams;
    internal void SetSignature(Eidosc.Ast.Types.TypeNode sig) => Signature = [sig];
    internal void SetRequiredAbilities(List<EffectRequirementNode> requiredAbilities) => RequiredAbilities = requiredAbilities;
    internal void SetComptime(bool isComptime) => IsComptime = isComptime;
    internal void SetBody(List<Eidosc.Ast.Patterns.PatternBranch> body) => Body = body;
    internal void SetPatternBodyExhaustive(bool isExhaustive) => IsPatternBodyExhaustive = isExhaustive;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.FuncDef);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);
        if (IsComptime)
        {
            element.SetAttribute("phase", "comptime");
        }

        if (TypeParams.Count > 0)
        {
            var typeParamsElement = doc.CreateElement(WellKnownStrings.XmlElements.TypeParams);
            foreach (var param in TypeParams)
            {
                typeParamsElement.AppendChild(param.ToXmlElement(doc));
            }
            element.AppendChild(typeParamsElement);
        }

        if (Signature.Count > 0)
        {
            var sigElement = doc.CreateElement(WellKnownStrings.XmlElements.Signature);
            foreach (var type in Signature)
            {
                sigElement.AppendChild(type.ToXmlElement(doc));
            }
            element.AppendChild(sigElement);
        }

        if (RequiredAbilities.Count > 0)
        {
            var requiredElement = doc.CreateElement(WellKnownStrings.XmlElements.RequiredAbilities);
            foreach (var ability in RequiredAbilities)
            {
                requiredElement.AppendChild(ability.ToXmlElement(doc));
            }
            element.AppendChild(requiredElement);
        }

        if (Body.Count > 0)
        {
            var bodyElement = doc.CreateElement(WellKnownStrings.XmlElements.Body);
            foreach (var branch in Body)
            {
                bodyElement.AppendChild(branch.ToXmlElement(doc));
            }
            element.AppendChild(bodyElement);
        }

        return element;
    }
}
