using System.Xml;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 否定模式（!p）
/// </summary>
public record NotPattern : Pattern
{
    /// <summary>
    /// 被否定的内部模式
    /// </summary>
    public Pattern? InnerPattern { get; internal set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        InnerPattern = null;

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        foreach (var child in ntNode.Children)
        {
            if (child is TerminalCstNode terminal)
            {
                var text = GetTokenText(terminal);
                if (text == WellKnownStrings.Operators.Not || IsPunctuation(text))
                {
                    continue;
                }

                if (IsIdentifierTerminal(terminal))
                {
                    InnerPattern = CreateVarPatternFromTerminal(terminal);
                }
                else if (IsLiteralTerminal(terminal))
                {
                    InnerPattern = CreateLiteralPatternFromTerminal(terminal);
                }

                if (InnerPattern != null)
                {
                    return;
                }

                continue;
            }

            if (TryExtractInnerPattern(child, out var patternNode))
            {
                InnerPattern = NormalizePatternNode(patternNode);
                return;
            }
        }
    }

    private bool TryExtractInnerPattern(ConcreteSyntaxNode node, out Pattern pattern)
    {
        if (node is TerminalCstNode terminal)
        {
            if (IsIdentifierTerminal(terminal))
            {
                pattern = CreateVarPatternFromTerminal(terminal);
                return true;
            }

            if (IsLiteralTerminal(terminal))
            {
                pattern = CreateLiteralPatternFromTerminal(terminal);
                return true;
            }

            pattern = null!;
            return false;
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            pattern = null!;
            return false;
        }

        if (ntNode.AstNode is Pattern patternNode && !ReferenceEquals(patternNode, this))
        {
            pattern = patternNode;
            return true;
        }

        foreach (var child in ntNode.Children)
        {
            if (TryExtractInnerPattern(child, out pattern))
            {
                return true;
            }
        }

        pattern = null!;
        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.NotPattern);

        if (InnerPattern != null)
        {
            var innerElement = doc.CreateElement(WellKnownStrings.XmlElements.InnerPattern);
            innerElement.AppendChild(InnerPattern.ToXmlElement(doc));
            element.AppendChild(innerElement);
        }

        return element;
    }
}
