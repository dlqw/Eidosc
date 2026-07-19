using System.Xml;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// 声明节点的抽象基类
/// </summary>
public abstract record Declaration : EidosAstNode
{
    /// <summary>
    /// 属性列表
    /// </summary>
    public List<Attribute> Attributes { get; protected set; } = [];

    /// <summary>
    /// Typed pre-body clauses in source order. This is the public 0.7
    /// declaration metadata surface; Attributes only represents legacy input.
    /// </summary>
    public List<DeclarationClause> Clauses { get; protected set; } = [];

    /// <summary>
    /// Versioned, typed clause occurrences produced by ClauseBinder in source order.
    /// </summary>
    public IReadOnlyList<ClauseIR> BoundClauses { get; private set; } = [];

    /// <summary>
    /// Unified derive/expand invocations lowered from <see cref="BoundClauses"/>.
    /// </summary>
    public IReadOnlyList<MetaInvocationIR> MetaInvocations { get; private set; } = [];

    /// <summary>
    /// Unified versioned attachment consumed by semantic and meta phases.
    /// </summary>
    public DeclarationAttachmentIR Attachment { get; private set; } = DeclarationAttachmentIR.Empty;

    /// <summary>
    /// 是否显式标记为 export。
    /// </summary>
    public bool IsExported { get; protected set; }

    protected XmlElement CreateDeclarationElement(XmlDocument doc, string name)
    {
        var element = CreateElement(doc, name);
        if (Attributes.Count > 0)
        {
            var attrsElement = doc.CreateElement(WellKnownStrings.XmlElements.Attributes);
            foreach (var attr in Attributes)
            {
                attrsElement.AppendChild(attr.ToXmlElement(doc));
            }
            element.AppendChild(attrsElement);
        }
        if (Clauses.Count > 0)
        {
            var clausesElement = doc.CreateElement("Clauses");
            foreach (var clause in Clauses)
            {
                clausesElement.AppendChild(clause.ToXmlElement(doc));
            }
            element.AppendChild(clausesElement);
        }
        return element;
    }

    /// <summary>
    /// 从 CST 节点中提取属性列表
    /// </summary>
    protected void ExtractAttributes(AstContext context, ConcreteSyntaxNode node)
    {
        if (node is not NonTerminalCstNode ntNode) return;

        foreach (var child in ntNode.Children)
        {
            if (child is NonTerminalCstNode { AstNode: Attribute attr })
            {
                Attributes.Add(attr);
            }
        }
    }

    protected void ExtractExportModifier(ConcreteSyntaxNode node)
    {
        IsExported = node is NonTerminalCstNode ntNode && ContainsKeyword(ntNode, WellKnownStrings.Keywords.Export);
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetAttributes(List<Attribute> attrs) => Attributes = attrs;
    internal void SetClauses(List<DeclarationClause> clauses) => Clauses = clauses;
    internal void SetBoundClauses(
        IReadOnlyList<ClauseIR> clauses,
        IReadOnlyList<MetaInvocationIR> invocations)
    {
        BoundClauses = clauses;
        MetaInvocations = invocations;
        Attachment = DeclarationAttachmentIR.Create(clauses, invocations);
    }
    internal void SetExported(bool exported) => IsExported = exported;

    protected static bool ContainsKeyword(NonTerminalCstNode node, string keyword)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode terminal &&
                string.Equals(terminal.Token.ToString(), keyword, StringComparison.Ordinal))
            {
                return true;
            }

            if (child is NonTerminalCstNode childNt &&
                ContainsKeyword(childNt, keyword))
            {
                return true;
            }
        }

        return false;
    }
}
