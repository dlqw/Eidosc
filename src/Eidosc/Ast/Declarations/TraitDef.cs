using System.Xml;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// Trait 定义
/// </summary>
/// <example>
/// trait Show
/// {
///     func show: Self -> String
/// }
/// </example>
public record TraitDef : Declaration
{
    /// <summary>
    /// Trait 名称
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>
    /// 类型参数列表
    /// </summary>
    public List<Ast.Types.TypeParam> TypeParams { get; private set; } = [];

    /// <summary>
    /// 父 trait 列表（supertrait）
    /// </summary>
    public List<Ast.Types.TraitRef> SuperTraits { get; private set; } = [];

    /// <summary>
    /// Trait 方法列表（FuncDef.Body.Count == 0 表示签名，&gt; 0 表示默认实现）
    /// </summary>
    public List<FuncDef> Methods { get; private set; } = [];

    public List<AssociatedTypeDecl> AssociatedTypes { get; private set; } = [];

    public List<AssociatedConstDecl> AssociatedConsts { get; private set; } = [];

    public List<EidosAstNode> Members { get; private set; } = [];

    /// <summary>
    /// Trait proof member list.
    /// </summary>

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ExtractAttributes(context, node);
        ExtractExportModifier(node);
        Name = "";
        TypeParams.Clear();
        SuperTraits.Clear();
        Methods.Clear();
        AssociatedTypes.Clear();
        AssociatedConsts.Clear();
        Members.Clear();

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
                    CollectFromNode(childNt);
                }
            }
        }
    }

    private void CollectFromNode(NonTerminalCstNode node)
    {
        if (node.AstNode is FuncDef funcDef)
        {
            Methods.Add(funcDef);
            return;
        }

        if (node.AstNode is FuncDecl funcDecl)
        {
            // 兼容旧语法解析路径：签名无 body → 包装为空 body 的 FuncDef
            var wrapper = new FuncDef();
            wrapper.SetName(funcDecl.Name);
            wrapper.SetTypeParams(funcDecl.TypeParams);
            if (funcDecl.Signature.Count > 0)
                wrapper.SetSignature(funcDecl.Signature[0]);
            wrapper.SetRequiredAbilities(funcDecl.RequiredAbilities);
            Methods.Add(wrapper);
            return;
        }

        if (node.AstNode is AssociatedTypeDecl associatedType)
        {
            AssociatedTypes.Add(associatedType);
            return;
        }

        if (node.AstNode is AssociatedConstDecl associatedConst)
        {
            AssociatedConsts.Add(associatedConst);
            return;
        }

        // Proof collection removed during migration

        if (node.AstNode is Ast.Types.TypeParam typeParam)
        {
            TypeParams.Add(typeParam);
            return;
        }

        if (node.AstNode is Ast.Types.TraitRef traitRef)
        {
            SuperTraits.Add(traitRef);
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
        return text is WellKnownStrings.Keywords.Export
            or WellKnownStrings.Keywords.Trait
            or WellKnownStrings.Keywords.Func
;
    }

    internal void SetName(string name) => Name = name;
    internal void SetTypeParams(List<Ast.Types.TypeParam> typeParams) => TypeParams = typeParams;
    internal void SetSuperTraits(List<Ast.Types.TraitRef> superTraits) => SuperTraits = superTraits;
    internal void SetMethods(List<FuncDef> methods) => Methods = methods;
    internal void SetAssociatedTypes(List<AssociatedTypeDecl> associatedTypes) => AssociatedTypes = associatedTypes;
    internal void SetAssociatedConsts(List<AssociatedConstDecl> associatedConsts) => AssociatedConsts = associatedConsts;
    internal void SetMembers(List<EidosAstNode> members) => Members = members;
    internal void AppendMember(EidosAstNode member) => Members.Add(member);
    internal bool ReplaceMemberExpansion(ExpandDeclaration expansion, IReadOnlyList<EidosAstNode> members)
    {
        var index = Members.FindIndex(member => ReferenceEquals(member, expansion));
        if (index < 0)
        {
            return false;
        }

        Members.RemoveAt(index);
        Members.InsertRange(index, members);
        return true;
    }
    // SetProofs removed during migration

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.TraitDef);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (TypeParams.Count > 0)
        {
            var typeParamsElement = doc.CreateElement(WellKnownStrings.XmlElements.TypeParams);
            foreach (var param in TypeParams)
            {
                typeParamsElement.AppendChild(param.ToXmlElement(doc));
            }

            element.AppendChild(typeParamsElement);
        }

        if (SuperTraits.Count > 0)
        {
            var superTraitsElement = doc.CreateElement(WellKnownStrings.XmlElements.SuperTraits);
            foreach (var superTrait in SuperTraits)
            {
                superTraitsElement.AppendChild(superTrait.ToXmlElement(doc));
            }

            element.AppendChild(superTraitsElement);
        }

        if (Methods.Count > 0)
        {
            var methodsElement = doc.CreateElement(WellKnownStrings.XmlElements.Methods);
            foreach (var method in Methods)
            {
                methodsElement.AppendChild(method.ToXmlElement(doc));
            }
            element.AppendChild(methodsElement);
        }

        if (AssociatedTypes.Count > 0)
        {
            var associatedTypesElement = doc.CreateElement("AssociatedTypes");
            foreach (var associatedType in AssociatedTypes)
            {
                associatedTypesElement.AppendChild(associatedType.ToXmlElement(doc));
            }

            element.AppendChild(associatedTypesElement);
        }

        if (AssociatedConsts.Count > 0)
        {
            var associatedConstsElement = doc.CreateElement("AssociatedConsts");
            foreach (var associatedConst in AssociatedConsts)
            {
                associatedConstsElement.AppendChild(associatedConst.ToXmlElement(doc));
            }

            element.AppendChild(associatedConstsElement);
        }

        // Proof serialization removed

        return element;
    }
}
