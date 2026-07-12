using System.Xml;

namespace Eidosc.Ast.Types;

/// <summary>
/// 类型参数
/// </summary>
/// <example>
/// T
/// F: kind2
/// T: Eq + Ord
/// </example>
public record TypeParam : EidosAstNode
{
    /// <summary>
    /// 参数名称
    /// </summary>
    public string Name { get; internal set; } = "";

    /// <summary>
    /// Kind 注解
    /// </summary>
    public Kind? KindAnnotation { get; internal set; }

    /// <summary>
    /// Whether this parameter ranges over effect rows rather than value types.
    /// </summary>
    public bool IsEffectSet { get; internal set; }

    /// <summary>
    /// Whether this generic parameter is explicitly constrained to compile-time use.
    /// </summary>
    public bool IsComptime { get; internal set; }

    /// <summary>
    /// Value-level type annotation for comptime generic parameters.
    /// The phase-1 implementation accepts only Type as a type-level comptime parameter.
    /// </summary>
    public TypeNode? ComptimeTypeAnnotation { get; internal set; }

    /// <summary>
    /// Trait 约束
    /// </summary>
    public List<TraitRef> TraitConstraints { get; internal set; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Name = "";
        KindAnnotation = null;
        IsComptime = false;
        ComptimeTypeAnnotation = null;
        TraitConstraints.Clear();

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        foreach (var child in ntNode.Children)
        {
            CollectTypeParamParts(child);
        }

        if (TraitConstraints.Count == 0)
        {
            CollectTraitConstraintsFromNode(ntNode);
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "__T";
        }
    }

    private void CollectTypeParamParts(ConcreteSyntaxNode node)
    {
        switch (node)
        {
            case TerminalCstNode term:
            {
                var text = GetTokenText(term);
                if (string.IsNullOrWhiteSpace(Name) && IsTypeParamNameTerminal(term, text))
                {
                    Name = text;
                }

                break;
            }

            case NonTerminalCstNode { AstNode: Kind kind }:
                if (KindAnnotation == null)
                {
                    KindAnnotation = kind;
                }

                break;

            case NonTerminalCstNode { AstNode: TraitRef traitRef }:
                if (!string.IsNullOrWhiteSpace(traitRef.TraitName) &&
                    !TraitConstraints.Any(existing =>
                        string.Equals(
                            CreateTraitConstraintKey(existing),
                            CreateTraitConstraintKey(traitRef),
                            StringComparison.Ordinal)))
                {
                    TraitConstraints.Add(traitRef);
                }

                break;

            case NonTerminalCstNode ntNode:
                foreach (var child in ntNode.Children)
                {
                    CollectTypeParamParts(child);
                }

                break;
        }
    }

    private static bool IsTypeParamNameTerminal(TerminalCstNode term, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsPunctuation(text))
        {
            return false;
        }

        var terminalName = term.Terminal?.ToString();
        if (terminalName is not (WellKnownStrings.Terminals.Identifier or WellKnownStrings.Terminals.TypeIdentifier))
        {
            return false;
        }

        return text switch
        {
            WellKnownStrings.Keywords.Func or WellKnownStrings.XmlAttributes.Type or WellKnownStrings.Keywords.Trait or WellKnownStrings.Keywords.Effect or WellKnownStrings.Keywords.Module or WellKnownStrings.Keywords.Import => false,
            _ => true
        };
    }

    private void CollectTraitConstraintsFromNode(NonTerminalCstNode node)
    {
        var nodeName = node.NonTerminal?.DebugName ?? string.Empty;
        if (nodeName == "traitConstraints")
        {
            CollectTraitNamesFromTraitConstraints(node);
            return;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode nested)
            {
                CollectTraitConstraintsFromNode(nested);
            }
        }
    }

    private void CollectTraitNamesFromTraitConstraints(NonTerminalCstNode traitConstraintsNode)
    {
        foreach (var child in traitConstraintsNode.Children)
        {
            if (child is TerminalCstNode terminal)
            {
                var terminalName = terminal.Terminal?.ToString();
                if (terminalName is not (WellKnownStrings.Terminals.TypeIdentifier or WellKnownStrings.Terminals.Identifier))
                {
                    continue;
                }

                var traitName = GetTokenText(terminal);
                if (string.IsNullOrWhiteSpace(traitName))
                {
                    continue;
                }

                if (TraitConstraints.Any(existing => string.Equals(existing.TraitName, traitName, StringComparison.Ordinal)))
                {
                    continue;
                }

                var traitRef = new TraitRef();
                traitRef.SetSpan(terminal.Span);
                traitRef.SetTraitName(traitName);
                TraitConstraints.Add(traitRef);
                continue;
            }

            if (child is NonTerminalCstNode nestedNode)
            {
                CollectTraitNamesFromTraitConstraints(nestedNode);
            }
        }
    }

    private static string CreateTraitConstraintKey(TraitRef traitRef)
    {
        var modulePath = traitRef.ModulePath.Count == 0
            ? ""
            : string.Join(WellKnownStrings.Separators.Path, traitRef.ModulePath) + WellKnownStrings.Separators.Path;
        var typeArgCount = traitRef.TypeArgs.Count;
        return $"{modulePath}{traitRef.TraitName}[{typeArgCount}]";
    }

    public int GetKindArity()
    {
        return KindAnnotation?.GetArrowArity() ?? 0;
    }

    public string GetKindText()
    {
        if (IsEffectSet)
        {
            return WellKnownStrings.Keywords.Effects;
        }

        return KindAnnotation?.ToKindText() ?? Eidosc.Types.KindParser.ToKindText(Eidosc.Types.Kind.KStar.Instance);
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "TypeParam");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (KindAnnotation != null)
        {
            var kindElement = doc.CreateElement(WellKnownStrings.XmlElements.Kind);
            kindElement.AppendChild(KindAnnotation.ToXmlElement(doc));
            element.AppendChild(kindElement);
        }

        if (IsEffectSet)
        {
            element.SetAttribute(WellKnownStrings.Keywords.Effects, bool.TrueString);
        }

        if (IsComptime)
        {
            element.SetAttribute(WellKnownStrings.Keywords.Comptime, "true");
            if (ComptimeTypeAnnotation != null)
            {
                var comptimeTypeElement = doc.CreateElement("ComptimeType");
                comptimeTypeElement.AppendChild(ComptimeTypeAnnotation.ToXmlElement(doc));
                element.AppendChild(comptimeTypeElement);
            }
        }

        if (TraitConstraints.Count > 0)
        {
            var constraintsElement = doc.CreateElement(WellKnownStrings.XmlElements.TraitConstraints);
            foreach (var constraint in TraitConstraints)
            {
                constraintsElement.AppendChild(constraint.ToXmlElement(doc));
            }
            element.AppendChild(constraintsElement);
        }

        return element;
    }
}
