using System.Xml;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// As 模式（模式绑定）
/// </summary>
/// <example>
/// p as person
/// (x, y) as point
/// </example>
public record AsPattern : Pattern
{
    /// <summary>
    /// 内部模式
    /// </summary>
    public Pattern? InnerPattern { get; internal set; }

    /// <summary>
    /// 绑定的变量名
    /// </summary>
    public string BindingName { get; internal set; } = "";

    /// <summary>
    /// 绑定模式（按值/借用）。
    /// </summary>
    public PatternBindingMode BindingMode { get; internal set; } = PatternBindingMode.ByValue;

    /// <summary>
    /// True when the as-binding is explicitly mutable, for example <c>pat as mut name</c>.
    /// </summary>
    public bool IsMutableBinding { get; internal set; }

    internal void SetBindingNameForTesting(string bindingName) => BindingName = bindingName;

    internal void SetBindingModeForTesting(PatternBindingMode mode) => BindingMode = mode;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            var seenAs = false;
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (text == WellKnownStrings.AdditionalKeywords.As)
                    {
                        seenAs = true;
                        continue;
                    }

                    if (seenAs && text is WellKnownStrings.Operators.Ref or WellKnownStrings.Operators.MRef)
                    {
                        BindingMode = text is WellKnownStrings.Operators.MRef
                            ? PatternBindingMode.MutableBorrow
                            : BindingMode == PatternBindingMode.MutableBorrow
                                ? PatternBindingMode.MutableBorrow
                                : PatternBindingMode.SharedBorrow;
                        continue;
                    }

                    if (seenAs && text == WellKnownStrings.Keywords.Mut)
                    {
                        IsMutableBinding = true;
                        continue;
                    }

                    if (!IsPunctuation(text) && text != WellKnownStrings.AdditionalKeywords.As && text != WellKnownStrings.Operators.Ref && text != WellKnownStrings.Keywords.Mut && text != WellKnownStrings.Operators.MRef && BindingName == "")
                    {
                        BindingName = text;
                    }
                }
                else if (child is NonTerminalCstNode { AstNode: VarPattern bindingPattern } && seenAs)
                {
                    if (string.IsNullOrWhiteSpace(BindingName))
                    {
                        BindingName = bindingPattern.Name;
                    }

                    BindingMode = bindingPattern.BindingMode;
                    IsMutableBinding = bindingPattern.IsMutableBinding;
                }
                else if (child is NonTerminalCstNode { AstNode: Pattern pattern })
                {
                    InnerPattern = NormalizePatternNode(pattern);
                }
            }
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.AsPattern);
        element.SetAttribute(WellKnownStrings.XmlAttributes.BindingName, BindingName);
        element.SetAttribute(WellKnownStrings.XmlAttributes.BindingMode, BindingMode.ToDisplayText());
        if (IsMutableBinding)
        {
            element.SetAttribute("mutable", "true");
        }

        if (InnerPattern != null)
        {
            var innerElement = doc.CreateElement(WellKnownStrings.XmlElements.InnerPattern);
            innerElement.AppendChild(InnerPattern.ToXmlElement(doc));
            element.AppendChild(innerElement);
        }

        return element;
    }
}
