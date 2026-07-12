using System.Xml;
using Eidosc.Ast.Patterns;

namespace Eidosc.Ast.Expressions;

public record DoExpr : Expression
{
    public List<DoBinding> Bindings { get; } = [];

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            CollectDoItems(ntNode);
        }
    }

    private void CollectDoItems(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (IsSemicolonTerminal(term))
                    continue;
                continue;
            }

            if (child is not NonTerminalCstNode childNt)
                continue;

            if (childNt.AstNode is DoBinding binding)
            {
                Bindings.Add(binding);
                continue;
            }

            if (childNt.AstNode is Expression expr)
            {
                var implicitBinding = DoBinding.CreateExpr(expr);
                Bindings.Add(implicitBinding);
                continue;
            }

            CollectDoItems(childNt);
        }
    }

    private static bool IsSemicolonTerminal(TerminalCstNode term)
    {
        var terminalName = term.Terminal?.ToString() ?? "";
        if (terminalName is WellKnownStrings.Punctuation.Semicolon or "semi")
            return true;
        var text = GetTokenText(term);
        return text == WellKnownStrings.Punctuation.Semicolon;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.DoExpr);

        foreach (var binding in Bindings)
        {
            element.AppendChild(binding.ToXmlElement(doc));
        }

        return element;
    }
}

public enum DoBindingKind
{
    Bind,
    Let,
    Expr
}

public record DoBinding : EidosAstNode
{
    public DoBindingKind Kind { get; private set; }
    public Pattern? Pattern { get; private set; }
    public string? VarName { get; private set; }
    public EidosAstNode? Value { get; private set; }

    public static DoBinding CreateExpr(EidosAstNode expr)
    {
        return new DoBinding { Kind = DoBindingKind.Expr, Value = expr, Span = expr.Span };
    }

    public static DoBinding CreateBind(Pattern pattern, EidosAstNode value)
    {
        return new DoBinding { Kind = DoBindingKind.Bind, Pattern = pattern, Value = value, Span = value.Span };
    }

    public static DoBinding CreateLet(string varName, EidosAstNode value)
    {
        return new DoBinding { Kind = DoBindingKind.Let, VarName = varName, Value = value, Span = value.Span };
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is not NonTerminalCstNode ntNode)
            return;

        var containsLeftArrow = ContainsTerminalText(ntNode, WellKnownStrings.Punctuation.LeftArrow);
        var hasKind = false;
        var phase = 0;
        foreach (var child in ntNode.Children)
        {
            if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (text == WellKnownStrings.Punctuation.LeftArrow)
                {
                    Kind = DoBindingKind.Bind;
                    hasKind = true;
                    phase = 2;
                    continue;
                }
                if (text == "=")
                {
                    Kind = DoBindingKind.Let;
                    if (VarName == null && TryExtractLetName(ntNode, out var varName))
                    {
                        VarName = varName;
                    }
                    hasKind = true;
                    phase = 2;
                    continue;
                }
                if (text == WellKnownStrings.Punctuation.Semicolon)
                    continue;
                if (phase == 0 && !hasKind && !containsLeftArrow)
                {
                    Kind = DoBindingKind.Expr;
                    hasKind = true;
                }
                continue;
            }

            if (child is not NonTerminalCstNode childNt)
                continue;

            if (containsLeftArrow && phase == 0 && Pattern == null && TryExtractPattern(childNt, out var extractedPattern))
            {
                Pattern = Pattern.NormalizePatternNode(extractedPattern);
                phase = 1;
                continue;
            }

            if (childNt.AstNode is Pattern pat2 && Kind == DoBindingKind.Bind)
            {
                Pattern = Pattern.NormalizePatternNode(pat2);
                phase = 1;
                continue;
            }

            if (childNt.AstNode is Expression expr)
            {
                if (phase >= 2 || Kind == DoBindingKind.Expr)
                {
                    Value = expr;
                    continue;
                }
                Value = expr;
                continue;
            }
        }

        if (!hasKind)
        {
            Kind = DoBindingKind.Expr;
        }

        if (Kind == DoBindingKind.Let && VarName == null && TryExtractLetName(ntNode, out var fallbackName))
        {
            VarName = fallbackName;
        }
    }

    private static bool ContainsTerminalText(ConcreteSyntaxNode node, string expectedText)
    {
        if (node is TerminalCstNode terminal)
        {
            return string.Equals(GetTokenText(terminal), expectedText, StringComparison.Ordinal) ||
                   string.Equals(terminal.Terminal?.ToString(), expectedText, StringComparison.Ordinal);
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return false;
        }

        return ntNode.Children.Any(child => ContainsTerminalText(child, expectedText));
    }

    private static bool TryExtractPattern(ConcreteSyntaxNode node, out Pattern pattern)
    {
        pattern = null!;

        if (node is TerminalCstNode terminal)
        {
            return TryCreatePatternFromTerminal(terminal, out pattern);
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return false;
        }

        if (ntNode.AstNode is Pattern astPattern)
        {
            pattern = Pattern.NormalizePatternNode(astPattern);
            return true;
        }

        foreach (var child in ntNode.Children)
        {
            if (TryExtractPattern(child, out pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractLetName(ConcreteSyntaxNode node, out string name)
    {
        name = "";
        var sawLet = false;
        var foundEquals = false;
        return TryExtractLetNameCore(node, ref sawLet, ref foundEquals, out name);
    }

    private static bool TryExtractLetNameCore(
        ConcreteSyntaxNode node,
        ref bool sawLet,
        ref bool foundEquals,
        out string name)
    {
        name = "";

        if (foundEquals)
        {
            return false;
        }

        if (node is TerminalCstNode terminal)
        {
            var terminalName = terminal.Terminal?.ToString() ?? string.Empty;
            var text = GetTokenText(terminal);
            if (text == "=")
            {
                foundEquals = true;
                return false;
            }

            if (terminalName == WellKnownStrings.Keywords.Let || text == WellKnownStrings.Keywords.Let)
            {
                sawLet = true;
                return false;
            }

            if (sawLet && terminalName == WellKnownStrings.Terminals.Identifier)
            {
                name = text;
                return !string.IsNullOrWhiteSpace(name);
            }

            return false;
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return false;
        }

        foreach (var child in ntNode.Children)
        {
            if (TryExtractLetNameCore(child, ref sawLet, ref foundEquals, out name))
            {
                return true;
            }

            if (foundEquals)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryCreatePatternFromTerminal(TerminalCstNode terminal, out Pattern pattern)
    {
        pattern = null!;
        var terminalName = terminal.Terminal?.ToString() ?? string.Empty;
        var text = GetTokenText(terminal);

        if (terminalName == WellKnownStrings.Terminals.Identifier)
        {
            var varPattern = new VarPattern();
            varPattern.SetSpan(terminal.Span);
            varPattern.SetName(text);
            pattern = varPattern;
            return true;
        }

        if (terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or
            WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean)
        {
            var literalPattern = new LiteralPattern();
            literalPattern.SetSpan(terminal.Span);
            literalPattern.SetLiteral(text);
            pattern = literalPattern;
            return true;
        }

        if (string.Equals(text, WellKnownStrings.Punctuation.Underscore, StringComparison.Ordinal))
        {
            pattern = new WildcardPattern();
            pattern.BuildFromCst(null!, terminal);
            return true;
        }

        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.DoBinding);
        element.SetAttribute("kind", Kind.ToString());

        if (Pattern != null)
        {
            var patElement = doc.CreateElement(WellKnownStrings.XmlElements.Pattern);
            patElement.AppendChild(Pattern.ToXmlElement(doc));
            element.AppendChild(patElement);
        }

        if (VarName != null)
        {
            element.SetAttribute("name", VarName);
        }

        if (Value != null)
        {
            var valElement = doc.CreateElement(WellKnownStrings.XmlElements.Value);
            valElement.AppendChild(Value.ToXmlElement(doc));
            element.AppendChild(valElement);
        }

        return element;
    }
}
