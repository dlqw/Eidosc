using System.Xml;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// 函数声明（无函数体）
/// 用于 effect 和 trait 中的函数签名声明
/// </summary>
/// <example>
/// func print: String -> Unit
/// </example>
public record FuncDecl : Declaration
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

    private void CollectFromNode(NonTerminalCstNode node)
    {
        if (string.Equals(node.NonTerminal?.DebugName, WellKnownStrings.Keywords.Signature, StringComparison.Ordinal))
        {
            return;
        }

        if (node.AstNode is TypeParam typeParam)
        {
            TypeParams.Add(typeParam);
            return;
        }

        if (node.AstNode is TypeNode typeNode)
        {
            Signature.Add(typeNode);
            return;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                CollectFromNode(childNt);
            }
        }
    }

    private static bool IsKeyword(string text)
    {
        return text is WellKnownStrings.Keywords.Export or WellKnownStrings.Keywords.Func or WellKnownStrings.Keywords.Let;
    }

    internal void SetName(string name) => Name = name;
    internal void SetTypeParams(List<TypeParam> typeParams) => TypeParams = typeParams;
    internal void SetSignature(TypeNode sig) => Signature = [sig];
    internal void SetRequiredAbilities(List<EffectRequirementNode> requiredAbilities) => RequiredAbilities = requiredAbilities;
    internal void SetComptime(bool isComptime) => IsComptime = isComptime;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.FuncDecl);
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

        return element;
    }
}
