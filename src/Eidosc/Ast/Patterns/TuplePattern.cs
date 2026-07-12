using System.Xml;
using Eidosc.Utils;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 元组模式
/// </summary>
/// <example>
/// (x, y)
/// (1, _, name)
/// </example>
public record TuplePattern : Pattern
{
    /// <summary>
    /// 元素模式列表
    /// </summary>
    public List<Pattern> Elements { get; internal set; } = [];

    public void SetSpan(SourceSpan span) => Span = span;

    public void AddElement(Pattern element)
    {
        Elements.Add(NormalizePatternNode(element));
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is NonTerminalCstNode { AstNode: Pattern pattern })
                {
                    Elements.Add(NormalizePatternNode(pattern));
                }
                else if (child is TerminalCstNode term)
                {
                    // 从终端节点创建模式
                    if (GetTokenText(term) == WellKnownStrings.Punctuation.Underscore)
                    {
                        var wildcard = new WildcardPattern();
                        wildcard.BuildFromCst(context, term);
                        Elements.Add(NormalizePatternNode(wildcard));
                    }
                    else if (IsIdentifierTerminal(term))
                    {
                        Elements.Add(CreateVarPatternFromTerminal(term));
                    }
                    else if (IsLiteralTerminal(term))
                    {
                        Elements.Add(CreateLiteralPatternFromTerminal(term));
                    }
                }
                else if (child is NonTerminalCstNode childNt)
                {
                    var childName = childNt.NonTerminal?.DebugName ?? "";
                    // 处理 tuplePatternTail 节点
                    if (childName == "tuplePatternTail")
                    {
                        ProcessTailNode(childNt);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 处理 tuplePatternTail 节点
    /// </summary>
    private void ProcessTailNode(NonTerminalCstNode tailNode)
    {
        foreach (var child in tailNode.Children)
        {
            if (child is NonTerminalCstNode { AstNode: Pattern pattern })
            {
                Elements.Add(NormalizePatternNode(pattern));
            }
            else if (child is TerminalCstNode term)
            {
                if (GetTokenText(term) == WellKnownStrings.Punctuation.Underscore)
                {
                    var wildcard = new WildcardPattern();
                    wildcard.BuildFromCst(new AstContext(), term);
                    Elements.Add(NormalizePatternNode(wildcard));
                }
                else if (IsIdentifierTerminal(term))
                {
                    Elements.Add(CreateVarPatternFromTerminal(term));
                }
                else if (IsLiteralTerminal(term))
                {
                    Elements.Add(CreateLiteralPatternFromTerminal(term));
                }
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var childName = childNt.NonTerminal?.DebugName ?? "";
                // 递归处理嵌套的 tail 节点
                if (childName == "tuplePatternTail")
                {
                    ProcessTailNode(childNt);
                }
            }
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.TuplePattern);

        foreach (var elem in Elements)
        {
            var elemElement = doc.CreateElement(WellKnownStrings.XmlElements.Element);
            elemElement.AppendChild(elem.ToXmlElement(doc));
            element.AppendChild(elemElement);
        }

        return element;
    }
}
