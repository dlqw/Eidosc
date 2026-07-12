using System.Xml;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 与模式（p1 & p2 & ...）
/// </summary>
public record AndPattern : Pattern
{
    /// <summary>
    /// 合取子模式列表（至少两个）
    /// </summary>
    public List<Pattern> Conjuncts { get; internal set; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Conjuncts.Clear();

        if (node is NonTerminalCstNode ntNode)
        {
            CollectConjuncts(ntNode);
        }
    }

    private void CollectConjuncts(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt &&
                TryExtractConjunctPattern(childNt, out var pattern))
            {
                pattern = NormalizePatternNode(pattern);
                if (pattern is AndPattern nested)
                {
                    Conjuncts.AddRange(nested.Conjuncts);
                }
                else
                {
                    Conjuncts.Add(pattern);
                }

                continue;
            }

            if (child is TerminalCstNode terminal)
            {
                if (IsIdentifierTerminal(terminal))
                {
                    Conjuncts.Add(CreateVarPatternFromTerminal(terminal));
                }
                else if (IsLiteralTerminal(terminal))
                {
                    Conjuncts.Add(CreateLiteralPatternFromTerminal(terminal));
                }
            }
        }
    }

    private static bool TryExtractConjunctPattern(NonTerminalCstNode node, out Pattern pattern)
    {
        if (node.AstNode is Pattern patternNode)
        {
            pattern = patternNode;
            return true;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt &&
                TryExtractConjunctPattern(childNt, out pattern))
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
        var element = CreateElement(doc, WellKnownStrings.XmlElements.AndPattern);

        foreach (var conjunct in Conjuncts)
        {
            var alt = doc.CreateElement(WellKnownStrings.XmlElements.Conjunct);
            alt.AppendChild(conjunct.ToXmlElement(doc));
            element.AppendChild(alt);
        }

        return element;
    }
}
