using System.Xml;
using Eidosc.Ast.Types;
using Eidosc.Utils;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// Named trait instance declaration for 0.6.0-alpha.1 name-first syntax.
/// </summary>
/// <example>
/// EqInt :: instance Eq[Int] { ... }
/// </example>
public record InstanceDecl : Declaration
{
    public string Name { get; private set; } = "";
    public List<TypeParam> TypeParams { get; private set; } = [];
    public TraitRef? Trait { get; private set; }
    public TypeNode? TargetType { get; private set; }
    public bool UsesConstructorBridge { get; private set; }
    public List<ConstructorBridgeFact> ConstructorBridgeFacts { get; private set; } = [];
    public List<FuncDef> Methods { get; private set; } = [];
    public List<AssociatedTypeDecl> AssociatedTypes { get; private set; } = [];
    public List<AssociatedConstDecl> AssociatedConsts { get; private set; } = [];
    public List<EidosAstNode> Members { get; private set; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ExtractAttributes(context, node);
        ExtractExportModifier(node);
    }

    internal void SetName(string name) => Name = name;
    internal void SetTypeParams(List<TypeParam> typeParams) => TypeParams = typeParams;
    internal void SetTrait(TraitRef trait) => Trait = trait;
    internal void SetTargetType(TypeNode targetType) => TargetType = targetType;
    internal void SetUsesConstructorBridge(bool value) => UsesConstructorBridge = value;
    internal void SetConstructorBridgeFacts(List<ConstructorBridgeFact> facts) => ConstructorBridgeFacts = facts;
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

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, "InstanceDecl");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (TypeParams.Count > 0)
        {
            var typeParamsElement = doc.CreateElement("TypeParams");
            foreach (var typeParam in TypeParams)
            {
                typeParamsElement.AppendChild(typeParam.ToXmlElement(doc));
            }

            element.AppendChild(typeParamsElement);
        }

        if (Trait != null)
        {
            var traitElement = doc.CreateElement("Trait");
            traitElement.AppendChild(Trait.ToXmlElement(doc));
            element.AppendChild(traitElement);
        }

        if (TargetType != null)
        {
            var targetTypeElement = doc.CreateElement("TargetType");
            targetTypeElement.AppendChild(TargetType.ToXmlElement(doc));
            element.AppendChild(targetTypeElement);
        }

        if (UsesConstructorBridge)
        {
            element.SetAttribute("usesConstructorBridge", WellKnownStrings.AdditionalKeywords.True);
        }

        if (Methods.Count > 0)
        {
            var methodsElement = doc.CreateElement("Methods");
            foreach (var method in Methods)
            {
                methodsElement.AppendChild(method.ToXmlElement(doc));
            }

            element.AppendChild(methodsElement);
        }

        if (ConstructorBridgeFacts.Count > 0)
        {
            var factsElement = doc.CreateElement("ConstructorBridgeFacts");
            foreach (var fact in ConstructorBridgeFacts)
            {
                factsElement.AppendChild(fact.ToXmlElement(doc));
            }

            element.AppendChild(factsElement);
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

        return element;
    }
}

public record ConstructorBridgeFact : EidosAstNode
{
    public string ConstructorName { get; private set; } = "";
    public List<ConstructorConstant> Constants { get; private set; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    internal void SetConstructorName(string constructorName) => ConstructorName = constructorName;
    internal void SetConstants(List<ConstructorConstant> constants) => Constants = constants;
    internal void SetSpan(SourceSpan span) => Span = span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "ConstructorBridgeFact");
        element.SetAttribute("constructor", ConstructorName);

        foreach (var constant in Constants)
        {
            element.AppendChild(constant.ToXmlElement(doc));
        }

        return element;
    }
}
