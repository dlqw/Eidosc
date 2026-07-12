using System.Xml;
using Eidosc.Utils;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 标识符表达式
/// </summary>
/// <example>
/// x
/// name
/// foo
/// </example>
public record IdentifierExpr : Expression
{
    /// <summary>
    /// 标识符名称
    /// </summary>
    public string Name { get; internal set; } = "";

    /// <summary>
    /// 是否解析为构造器
    /// </summary>
    public bool IsConstructor { get; set; }

    /// <summary>
    /// Imported value candidates preserved for type-directed resolution.
    /// </summary>
    public List<SymbolId> ValueCandidateSymbolIds { get; private set; } = [];

    /// <summary>
    /// 设置 span
    /// </summary>
    public void SetSpan(SourceSpan span) => Span = span;

    /// <summary>
    /// 设置名称
    /// </summary>
    public void SetName(string name) => Name = name;

    public void ClearValueCandidates() => ValueCandidateSymbolIds.Clear();

    public void AddValueCandidate(SymbolId symbolId)
    {
        if (symbolId.IsValid && !ValueCandidateSymbolIds.Contains(symbolId))
        {
            ValueCandidateSymbolIds.Add(symbolId);
        }
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (!IsPunctuation(text) && Name == "")
                    {
                        Name = text;
                    }
                }
            }
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.IdentifierExpr);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);
        return element;
    }
}
