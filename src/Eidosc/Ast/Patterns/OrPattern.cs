using System.Xml;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 或模式（p1 | p2 | ...）
/// </summary>
public record OrPattern : Pattern
{
    /// <summary>
    /// 备选模式列表（至少两个）
    /// </summary>
    public List<Pattern> Alternatives { get; internal set; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Alternatives.Clear();

        if (node is NonTerminalCstNode ntNode)
        {
            CollectAlternatives(ntNode);
        }
    }

    private void CollectAlternatives(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt &&
                TryExtractAlternativePattern(childNt, out var pattern))
            {
                pattern = NormalizePatternNode(pattern);
                if (pattern is OrPattern nested)
                {
                    Alternatives.AddRange(nested.Alternatives);
                }
                else
                {
                    Alternatives.Add(pattern);
                }
                continue;
            }

            if (child is TerminalCstNode terminal)
            {
                if (IsIdentifierTerminal(terminal))
                {
                    Alternatives.Add(CreateVarPatternFromTerminal(terminal));
                }
                else if (IsLiteralTerminal(terminal))
                {
                    Alternatives.Add(CreateLiteralPatternFromTerminal(terminal));
                }

                continue;
            }
        }
    }

    private static bool TryExtractAlternativePattern(NonTerminalCstNode node, out Pattern pattern)
    {
        if (node.AstNode is Pattern patternNode)
        {
            pattern = patternNode;
            return true;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt &&
                TryExtractAlternativePattern(childNt, out pattern))
            {
                return true;
            }

            if (child is TerminalCstNode terminal)
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
            }
        }

        pattern = null!;
        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.OrPattern);

        foreach (var alternative in Alternatives)
        {
            var alt = doc.CreateElement(WellKnownStrings.XmlElements.Alternative);
            alt.AppendChild(alternative.ToXmlElement(doc));
            element.AppendChild(alt);
        }

        return element;
    }
}
