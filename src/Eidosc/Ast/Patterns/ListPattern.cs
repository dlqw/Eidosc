using System.Xml;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 列表模式
/// </summary>
/// <example>
/// []
/// [x, y]
/// [head, ..tail]
/// [head, ..middle, last]
/// [..rest]
/// </example>
public record ListPattern : Pattern
{
    /// <summary>
    /// 前缀元素模式
    /// </summary>
    public List<Pattern> Elements { get; internal set; } = [];

    /// <summary>
    /// 是否包含剩余模式标记（..）
    /// </summary>
    public bool HasRestMarker { get; internal set; }

    /// <summary>
    /// 剩余模式绑定（可选）
    /// </summary>
    public Pattern? RestPattern { get; internal set; }

    /// <summary>
    /// 剩余模式后的后缀元素模式
    /// </summary>
    public List<Pattern> SuffixElements { get; internal set; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Elements.Clear();
        HasRestMarker = false;
        RestPattern = null;
        SuffixElements.Clear();

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        foreach (var child in ntNode.Children)
        {
            if (child is not NonTerminalCstNode childNt)
            {
                continue;
            }

            var childName = childNt.NonTerminal?.DebugName ?? string.Empty;
            if (childName == "listPatternTail")
            {
                ProcessTailNode(childNt);
                continue;
            }

            if (childName == "listRestPattern")
            {
                ProcessRestNode(context, childNt);
                continue;
            }

            if (childNt.AstNode is Pattern patternNode)
            {
                Elements.Add(NormalizePatternNode(patternNode));
                continue;
            }

            if (TryExtractNestedPattern(childNt, out var nestedPattern))
            {
                Elements.Add(NormalizePatternNode(nestedPattern));
            }
        }
    }

    private void ProcessTailNode(NonTerminalCstNode tailNode)
    {
        foreach (var child in tailNode.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                var childName = childNt.NonTerminal?.DebugName ?? string.Empty;
                if (childName == "listPatternTail")
                {
                    ProcessTailNode(childNt);
                    continue;
                }

                if (childName == "listRestPattern")
                {
                    // 容错处理：如果缓存 grammar 结构变化导致 rest 落在 tail 内，统一按 rest 处理。
                    ProcessRestNode(new AstContext(), childNt);
                    continue;
                }

                if (childNt.AstNode is Pattern patternNode)
                {
                    Elements.Add(NormalizePatternNode(patternNode));
                    continue;
                }

                if (TryExtractNestedPattern(childNt, out var nestedPattern))
                {
                    Elements.Add(NormalizePatternNode(nestedPattern));
                }

                continue;
            }

            if (child is TerminalCstNode terminal)
            {
                if (IsIdentifierTerminal(terminal))
                {
                    Elements.Add(CreateVarPatternFromTerminal(terminal));
                }
                else if (IsLiteralTerminal(terminal))
                {
                    Elements.Add(CreateLiteralPatternFromTerminal(terminal));
                }
            }
        }
    }

    private void ProcessRestNode(AstContext context, NonTerminalCstNode restNode)
    {
        HasRestMarker = true;

        foreach (var child in restNode.Children)
        {
            if (child is TerminalCstNode terminal)
            {
                var text = GetTokenText(terminal);
                if (text == WellKnownStrings.Punctuation.DotDot || IsPunctuation(text))
                {
                    continue;
                }

                if (IsIdentifierTerminal(terminal))
                {
                    RestPattern = CreateVarPatternFromTerminal(terminal);
                    return;
                }

                if (text == WellKnownStrings.Punctuation.Underscore)
                {
                    var wildcard = new WildcardPattern();
                    wildcard.BuildFromCst(context, terminal);
                    RestPattern = wildcard;
                    return;
                }

                continue;
            }

            if (child is NonTerminalCstNode { AstNode: Pattern patternNode })
            {
                RestPattern = NormalizePatternNode(patternNode);
                return;
            }

            if (child is NonTerminalCstNode childNt &&
                TryExtractNestedPattern(childNt, out var nestedPattern))
            {
                RestPattern = NormalizePatternNode(nestedPattern);
                return;
            }
        }
    }

    private static bool TryExtractNestedPattern(NonTerminalCstNode node, out Pattern pattern)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode { AstNode: Pattern patternNode })
            {
                pattern = patternNode;
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

            if (child is NonTerminalCstNode childNt &&
                TryExtractNestedPattern(childNt, out pattern))
            {
                return true;
            }
        }

        pattern = null!;
        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.ListPattern);
        element.SetAttribute(WellKnownStrings.XmlAttributes.HasRest, HasRestMarker.ToString());

        foreach (var item in Elements)
        {
            var child = doc.CreateElement(WellKnownStrings.XmlElements.Element);
            child.AppendChild(item.ToXmlElement(doc));
            element.AppendChild(child);
        }

        if (RestPattern != null)
        {
            var rest = doc.CreateElement(WellKnownStrings.XmlElements.Rest);
            rest.AppendChild(RestPattern.ToXmlElement(doc));
            element.AppendChild(rest);
        }

        foreach (var item in SuffixElements)
        {
            var child = doc.CreateElement(WellKnownStrings.XmlElements.Element);
            child.SetAttribute("position", "suffix");
            child.AppendChild(item.ToXmlElement(doc));
            element.AppendChild(child);
        }

        return element;
    }
}
