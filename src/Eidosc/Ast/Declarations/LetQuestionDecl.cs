using System.Xml;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;

namespace Eidosc.Ast.Declarations;

public enum LetQuestionBindingKind
{
    Unknown,
    Option,
    Result
}

/// <summary>
/// let? binding syntax for Option / Result short-circuit binding.
/// </summary>
public record LetQuestionDecl : Declaration
{
    public Pattern? Pattern { get; private set; }

    public EidosAstNode? Value { get; private set; }

    public LetQuestionBindingKind BindingKind { get; private set; } = LetQuestionBindingKind.Unknown;

    public SymbolId SuccessConstructorSymbolId { get; private set; } = SymbolId.None;

    public SymbolId FailureConstructorSymbolId { get; private set; } = SymbolId.None;

    public SymbolId FailureBindingSymbolId { get; private set; } = SymbolId.None;

    public object? SuccessPayloadType { get; private set; }

    public object? FailurePayloadType { get; private set; }

    public object? ShortCircuitReturnType { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ExtractAttributes(context, node);

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        var seenBind = false;
        foreach (var child in ntNode.Children)
        {
            if (child is TerminalCstNode terminal &&
                string.Equals(GetTokenText(terminal), WellKnownStrings.Punctuation.Equals, StringComparison.Ordinal))
            {
                seenBind = true;
                continue;
            }

            if (child is NonTerminalCstNode { AstNode: Pattern patternNode } && Pattern == null)
            {
                Pattern = patternNode;
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

    internal void SetPattern(Pattern pattern) => Pattern = pattern;

    internal void SetValue(EidosAstNode value) => Value = value;

    internal void SetFailureBindingSymbol(SymbolId symbolId) => FailureBindingSymbolId = symbolId;

    internal void SetDesugaring(
        LetQuestionBindingKind bindingKind,
        SymbolId successConstructorSymbolId,
        SymbolId failureConstructorSymbolId,
        object? successPayloadType,
        object? failurePayloadType,
        object? shortCircuitReturnType)
    {
        BindingKind = bindingKind;
        SuccessConstructorSymbolId = successConstructorSymbolId;
        FailureConstructorSymbolId = failureConstructorSymbolId;
        SuccessPayloadType = successPayloadType;
        FailurePayloadType = failurePayloadType;
        ShortCircuitReturnType = shortCircuitReturnType;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.LetQuestionDecl);
        element.SetAttribute("kind", BindingKind.ToString());

        if (Pattern != null)
        {
            var patternElement = doc.CreateElement(WellKnownStrings.XmlElements.Pattern);
            patternElement.AppendChild(Pattern.ToXmlElement(doc));
            element.AppendChild(patternElement);
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
