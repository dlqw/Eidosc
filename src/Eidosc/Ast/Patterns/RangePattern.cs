using System.Xml;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 范围模式（a .. b）
/// </summary>
public record RangePattern : Pattern
{
    /// <summary>
    /// 起始边界（当前要求字面量）
    /// </summary>
    public LiteralPattern? Start { get; internal set; }

    /// <summary>
    /// 结束边界（当前要求字面量）
    /// </summary>
    public LiteralPattern? End { get; internal set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Start = null;
        End = null;

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        var literals = new List<LiteralPattern>(capacity: 2);
        CollectLiteralBoundaries(ntNode, literals);

        if (literals.Count > 0)
        {
            Start = literals[0];
        }

        if (literals.Count > 1)
        {
            End = literals[1];
        }
    }

    private static void CollectLiteralBoundaries(NonTerminalCstNode node, List<LiteralPattern> literals)
    {
        foreach (var child in node.Children)
        {
            if (literals.Count >= 2)
            {
                return;
            }

            if (child is NonTerminalCstNode { AstNode: LiteralPattern literalPattern })
            {
                literals.Add(literalPattern);
                continue;
            }

            if (child is TerminalCstNode terminal && IsLiteralTerminal(terminal))
            {
                literals.Add(CreateLiteralPatternFromTerminal(terminal));
                continue;
            }

            if (child is NonTerminalCstNode childNt)
            {
                if (TryCreateLiteralPatternFromNode(childNt, out var nestedLiteral))
                {
                    literals.Add(nestedLiteral);
                    continue;
                }

                var childName = childNt.NonTerminal?.DebugName ?? "";
                if (childName is "rangePatternTail" or "literal")
                {
                    CollectLiteralBoundaries(childNt, literals);
                }
            }
        }
    }

    private static bool TryCreateLiteralPatternFromNode(NonTerminalCstNode node, out LiteralPattern literalPattern)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal && IsLiteralTerminal(terminal))
            {
                literalPattern = CreateLiteralPatternFromTerminal(terminal);
                return true;
            }

            if (child is NonTerminalCstNode childNt &&
                TryCreateLiteralPatternFromNode(childNt, out literalPattern))
            {
                return true;
            }
        }

        literalPattern = null!;
        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.RangePattern);

        if (Start != null)
        {
            var startElement = doc.CreateElement(WellKnownStrings.XmlElements.Start);
            startElement.AppendChild(Start.ToXmlElement(doc));
            element.AppendChild(startElement);
        }

        if (End != null)
        {
            var endElement = doc.CreateElement(WellKnownStrings.XmlElements.End);
            endElement.AppendChild(End.ToXmlElement(doc));
            element.AppendChild(endElement);
        }

        return element;
    }
}
