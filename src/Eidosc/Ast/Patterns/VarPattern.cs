using System.Xml;
using Eidosc.Utils;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 变量模式
/// </summary>
/// <example>
/// x
/// name
/// </example>
public record VarPattern : Pattern
{
    /// <summary>
    /// 变量名称
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>
    /// 绑定模式（按值/借用）。
    /// </summary>
    public PatternBindingMode BindingMode { get; private set; } = PatternBindingMode.ByValue;

    /// <summary>
    /// True when the binding is explicitly mutable, for example <c>mut name</c>.
    /// </summary>
    public bool IsMutableBinding { get; private set; }

    /// <summary>
    /// True for a bare identifier parsed without an explicit binding marker.
    /// Name resolution may replace it with a zero-field constructor pattern.
    /// </summary>
    public bool MayResolveToConstructor { get; private set; }

    /// <summary>
    /// 设置位置
    /// </summary>
    internal void SetSpan(SourceSpan span) => Span = span;

    /// <summary>
    /// 设置变量名称（用于外部构建)
    /// </summary>
    internal void SetName(string name) => Name = name;

    /// <summary>
    /// 设置绑定模式（用于外部构建)
    /// </summary>
    internal void SetBindingMode(PatternBindingMode mode) => BindingMode = mode;

    internal void SetMutableBinding(bool isMutable) => IsMutableBinding = isMutable;

    internal void SetMayResolveToConstructor(bool value) => MayResolveToConstructor = value;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            var sawRef = false;
            var sawMutableBorrow = false;
            var sawMutableBinding = false;
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (text == WellKnownStrings.Keywords.Mut)
                    {
                        sawMutableBinding = true;
                        continue;
                    }

                    if (text == WellKnownStrings.Operators.Ref)
                    {
                        sawRef = true;
                        continue;
                    }

                    if (text is WellKnownStrings.Operators.MRef)
                    {
                        sawMutableBorrow = true;
                        continue;
                    }

                    if (!IsPunctuation(text) && !IsKeyword(text) && Name == "")
                    {
                        Name = text;
                    }
                }
            }

            BindingMode = ResolveBindingMode(sawRef, sawMutableBorrow);
            IsMutableBinding = sawMutableBinding && BindingMode == PatternBindingMode.ByValue;
        }
    }

    private static bool IsKeyword(string text)
    {
        return text is WellKnownStrings.Punctuation.Underscore or WellKnownStrings.AdditionalKeywords.True or WellKnownStrings.AdditionalKeywords.False or WellKnownStrings.Operators.Ref or WellKnownStrings.Operators.MRef;
    }

    private static PatternBindingMode ResolveBindingMode(bool sawRef, bool sawMutableBorrow)
    {
        if (sawMutableBorrow)
        {
            return PatternBindingMode.MutableBorrow;
        }

        if (sawRef)
        {
            return PatternBindingMode.SharedBorrow;
        }

        return PatternBindingMode.ByValue;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.VarPattern);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);
        element.SetAttribute(WellKnownStrings.XmlAttributes.BindingMode, BindingMode.ToDisplayText());
        if (IsMutableBinding)
        {
            element.SetAttribute("mutable", "true");
        }
        if (MayResolveToConstructor)
        {
            element.SetAttribute("unresolvedName", "true");
        }
        return element;
    }
}
