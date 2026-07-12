using System.Xml;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// Nominal compile-time effect tag declaration.
/// </summary>
/// <example>
/// IO :: effect;
/// </example>
public record EffectDef : Declaration
{
    /// <summary>
    /// Effect 名称
    /// </summary>
    public string Name { get; private set; } = "";

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
            }
        }
    }

    private static bool IsKeyword(string text)
    {
        return text is WellKnownStrings.Keywords.Export or WellKnownStrings.Keywords.Effect;
    }

    internal void SetName(string name) => Name = name;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.EffectDef);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        return element;
    }

    public override string ToString()
    {
        return $"{Name} :: effect;";
    }
}
