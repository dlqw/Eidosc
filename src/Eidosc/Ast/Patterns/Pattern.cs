using System.Xml;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 模式节点的抽象基类
/// </summary>
public abstract record Pattern : EidosAstNode
{
    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        return CreateElement(doc, GetType().Name);
    }

    /// <summary>
    /// 检查终端节点是否是标识符
    /// </summary>
    protected static bool IsIdentifierTerminal(TerminalCstNode term)
    {
        if (term.Terminal == null) return false;
        var terminalName = term.Terminal.ToString();
        return terminalName == WellKnownStrings.Terminals.Identifier;
    }

    /// <summary>
    /// 检查终端节点是否是字面量
    /// </summary>
    protected static bool IsLiteralTerminal(TerminalCstNode term)
    {
        if (term.Terminal == null) return false;
        var terminalName = term.Terminal.ToString();
        return terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean;
    }

    /// <summary>
    /// 从终端节点创建 VarPattern
    /// </summary>
    protected static VarPattern CreateVarPatternFromTerminal(TerminalCstNode term)
    {
        var pattern = new VarPattern();
        pattern.SetSpan(term.Span);
        pattern.SetName(GetTokenText(term));
        return pattern;
    }

    /// <summary>
    /// 从终端节点创建 LiteralPattern
    /// </summary>
    protected static LiteralPattern CreateLiteralPatternFromTerminal(TerminalCstNode term)
    {
        var pattern = new LiteralPattern();
        pattern.SetSpan(term.Span);
        pattern.SetLiteral(GetTokenText(term));
        return pattern;
    }

    /// <summary>
    /// 规范化模式节点，去掉仅包裹单子节点的 And/Or 包装层。
    /// </summary>
    internal static Pattern NormalizePatternNode(Pattern pattern)
    {
        while (true)
        {
            switch (pattern)
            {
                case VarPattern { Name: WellKnownStrings.Punctuation.Underscore or "" } wildcardVar:
                    var wildcard = new WildcardPattern();
                    wildcard.SetSpan(wildcardVar.Span);
                    pattern = wildcard;
                    continue;
                case AndPattern { Conjuncts.Count: 1 } andPattern:
                    pattern = andPattern.Conjuncts[0];
                    continue;
                case OrPattern { Alternatives.Count: 1 } orPattern:
                    pattern = orPattern.Alternatives[0];
                    continue;
                default:
                    return pattern;
            }
        }
    }
}
