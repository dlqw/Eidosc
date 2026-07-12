using System.Xml;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// let 模式绑定（块级不可变绑定）
/// </summary>
/// <example>
/// let x = 1;
/// let (a, b) = pair;
/// </example>
public record LetDecl : Declaration
{
    /// <summary>
    /// 绑定模式
    /// </summary>
    public Pattern? Pattern { get; private set; }

    /// <summary>
    /// Optional type annotation for the bound pattern.
    /// </summary>
    public TypeNode? TypeAnnotation { get; private set; }

    /// <summary>
    /// True when this binding uses let mut and can be reassigned.
    /// </summary>
    public bool IsMutable { get; private set; }

    /// <summary>
    /// True when this binding is explicitly evaluated at compile time.
    /// </summary>
    public bool IsComptime { get; private set; }

    /// <summary>
    /// 绑定表达式
    /// </summary>
    public EidosAstNode? Value { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ExtractAttributes(context, node);

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        var seenBind = false;
        var seenTypeAnnotation = false;
        foreach (var child in ntNode.Children)
        {
            if (child is TerminalCstNode terminal &&
                string.Equals(GetTokenText(terminal), WellKnownStrings.Punctuation.Equals, StringComparison.Ordinal))
            {
                seenBind = true;
                continue;
            }

            if (!seenBind &&
                child is TerminalCstNode mutTerminal &&
                string.Equals(GetTokenText(mutTerminal), WellKnownStrings.Keywords.Mut, StringComparison.Ordinal))
            {
                IsMutable = true;
                continue;
            }

            if (child is NonTerminalCstNode { AstNode: Pattern patternNode } && Pattern == null)
            {
                Pattern = patternNode;
                continue;
            }

            if (!seenBind &&
                child is TerminalCstNode colonTerminal &&
                string.Equals(GetTokenText(colonTerminal), WellKnownStrings.Punctuation.Colon, StringComparison.Ordinal))
            {
                seenTypeAnnotation = true;
                continue;
            }

            if (!seenBind &&
                seenTypeAnnotation &&
                child is NonTerminalCstNode { AstNode: TypeNode typeNode })
            {
                TypeAnnotation = typeNode;
                continue;
            }

            if (!seenBind &&
                seenTypeAnnotation &&
                child is NonTerminalCstNode typeAnnotationNode &&
                DeclarationTypeAnnotationExtractor.TryExtract(typeAnnotationNode, out var typeAnnotation))
            {
                TypeAnnotation = typeAnnotation;
                continue;
            }

            if (!seenBind || Value != null)
            {
                continue;
            }

            if (child is NonTerminalCstNode { AstNode: EidosAstNode exprNode } &&
                exprNode is not Eidosc.Ast.Patterns.Pattern)
            {
                Value = exprNode;
                continue;
            }

            if (TryExtractExpressionNode(child, out var extracted))
            {
                Value = extracted;
            }
        }
    }

    private static bool TryExtractExpressionNode(ConcreteSyntaxNode node, out EidosAstNode expression)
    {
        expression = null!;

        if (node is TerminalCstNode terminal)
        {
            var terminalName = terminal.Terminal?.ToString() ?? string.Empty;
            if (terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean)
            {
                var literal = new LiteralExpr();
                literal.SetSpan(terminal.Span);
                literal.SetLiteral(GetTokenText(terminal));
                expression = literal;
                return true;
            }

            if (terminalName == WellKnownStrings.Terminals.Identifier)
            {
                var identifier = new IdentifierExpr();
                identifier.SetSpan(terminal.Span);
                identifier.SetName(GetTokenText(terminal));
                expression = identifier;
                return true;
            }

            return false;
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return false;
        }

        if (ntNode.AstNode is EidosAstNode astNode &&
            astNode is not Eidosc.Ast.Patterns.Pattern)
        {
            expression = astNode;
            return true;
        }

        foreach (var child in ntNode.Children)
        {
            if (TryExtractExpressionNode(child, out expression))
            {
                return true;
            }
        }

        return false;
    }

    internal void SetPattern(Eidosc.Ast.Patterns.Pattern pattern) => Pattern = pattern;
    internal void SetTypeAnnotation(TypeNode? type) => TypeAnnotation = type;
    internal void SetMutable(bool isMutable) => IsMutable = isMutable;
    internal void SetComptime(bool isComptime) => IsComptime = isComptime;
    internal void SetValue(EidosAstNode value) => Value = value;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.LetDecl);
        if (IsComptime)
        {
            element.SetAttribute("phase", "comptime");
        }

        if (Pattern != null)
        {
            var patternElement = doc.CreateElement(WellKnownStrings.XmlElements.Pattern);
            patternElement.AppendChild(Pattern.ToXmlElement(doc));
            element.AppendChild(patternElement);
        }

        if (TypeAnnotation != null)
        {
            var typeElement = doc.CreateElement(WellKnownStrings.XmlElements.Type);
            typeElement.AppendChild(TypeAnnotation.ToXmlElement(doc));
            element.AppendChild(typeElement);
        }

        if (Value != null)
        {
            var valueElement = doc.CreateElement(WellKnownStrings.XmlElements.Value);
            valueElement.AppendChild(Value.ToXmlElement(doc));
            element.AppendChild(valueElement);
        }

        return element;
    }
}
