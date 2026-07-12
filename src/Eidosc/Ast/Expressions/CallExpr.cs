using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 函数调用表达式
/// </summary>
/// <example>
/// foo(1, 2)
/// print(value)
/// map(fn(x) x * 2, list)
/// </example>
public record CallExpr : Expression
{
    /// <summary>
    /// 被调用的函数表达式
    /// </summary>
    public EidosAstNode? Function { get; private set; }

    /// <summary>
    /// 位置参数
    /// </summary>
    public List<EidosAstNode> PositionalArgs { get; private set; } = [];

    /// <summary>
    /// 命名参数
    /// </summary>
    public List<NamedArg> NamedArgs { get; private set; } = [];

    /// <summary>
    /// 类型推断阶段为 empty call 合成的 Unit 实参数量。
    /// </summary>
    public int SynthesizedUnitArgumentCount { get; private set; }

    /// <summary>
    /// 该 empty call 是否使用 FFI 的 Unit 参数 ABI 省略。
    /// </summary>
    public bool UsesFfiUnitArgumentElision { get; private set; }

    public void SetSpan(Eidosc.Utils.SourceSpan span) => Span = span;

    public void SetFunction(EidosAstNode function) => Function = function;

    public void AddPositionalArg(EidosAstNode arg) => PositionalArgs.Add(arg);

    public void AddNamedArg(NamedArg arg) => NamedArgs.Add(arg);

    public void MarkSyntheticUnitArguments(int count)
    {
        SynthesizedUnitArgumentCount = Math.Max(0, count);
        UsesFfiUnitArgumentElision = false;
    }

    public void MarkFfiUnitArgumentElision()
    {
        SynthesizedUnitArgumentCount = 0;
        UsesFfiUnitArgumentElision = true;
    }

    public void ClearEmptyCallResolution()
    {
        SynthesizedUnitArgumentCount = 0;
        UsesFfiUnitArgumentElision = false;
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ClearEmptyCallResolution();

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        foreach (var child in ntNode.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (Function == null && (IsIdentifierTerminal(term) || IsResumeTerminal(term)))
                {
                    var ident = new IdentifierExpr();
                    ident.SetSpan(term.Span);
                    ident.SetName(GetTokenText(term));
                    Function = ident;
                }
                continue;
            }

            if (child is not NonTerminalCstNode nestedNode)
            {
                continue;
            }

            if (Function == null && !IsArgumentContainer(nestedNode))
            {
                    var candidate = ExtractExpressionCandidate(context, nestedNode);
                    if (candidate != null)
                    {
                        Function = candidate;
                        continue;
                    }
                }

            ExtractArgsFromNode(context, nestedNode);
        }
    }

    /// <summary>
    /// 从嵌套节点中提取参数
    /// </summary>
    private void ExtractArgsFromNode(AstContext context, NonTerminalCstNode node)
    {
        if (node.AstNode is NamedArg namedArg)
        {
            NamedArgs.Add(namedArg);
            return;
        }

        if (TryCreatePathExpr(context, node, out var pathExpr))
        {
            PositionalArgs.Add(pathExpr);
            return;
        }

        if (node.AstNode is EidosAstNode expr && !ReferenceEquals(expr, Function))
        {
            PositionalArgs.Add(expr);
            return;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode nestedNode)
            {
                if (nestedNode.AstNode is NamedArg nestedNamedArg)
                {
                    NamedArgs.Add(nestedNamedArg);
                }
                else if (TryCreatePathExpr(context, nestedNode, out var nestedPathExpr))
                {
                    PositionalArgs.Add(nestedPathExpr);
                }
                else if (nestedNode.AstNode is EidosAstNode nestedExpr && !ReferenceEquals(nestedExpr, Function))
                {
                    PositionalArgs.Add(nestedExpr);
                }
                else
                {
                    ExtractArgsFromNode(context, nestedNode);
                }
            }
            else if (child is TerminalCstNode term)
            {
                if (TryCreatePositionalArgFromTerminal(term, out var terminalExpr))
                {
                    PositionalArgs.Add(terminalExpr);
                }
            }
        }
    }

    private static bool TryCreatePositionalArgFromTerminal(TerminalCstNode term, out EidosAstNode expr)
    {
        expr = null!;
        var text = GetTokenText(term);
        if (string.IsNullOrWhiteSpace(text) || IsPunctuation(text) || text == WellKnownStrings.Keywords.With)
        {
            return false;
        }

        if (IsIdentifierTerminal(term))
        {
            var ident = new IdentifierExpr();
            ident.SetSpan(term.Span);
            ident.SetName(text);
            expr = ident;
            return true;
        }

        if (IsLiteralTerminal(term))
        {
            var literal = new LiteralExpr();
            literal.SetSpan(term.Span);
            literal.SetLiteral(text);
            expr = literal;
            return true;
        }

        return false;
    }

    private static EidosAstNode? ExtractExpressionCandidate(AstContext context, NonTerminalCstNode node)
    {
        if (TryCreatePathExpr(context, node, out var pathExpr))
        {
            return pathExpr;
        }

        if (node.AstNode is EidosAstNode expr &&
            expr is not NamedArg)
        {
            return expr;
        }

        if (IsArgumentContainer(node))
        {
            return null;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode nestedNode)
            {
                var nested = ExtractExpressionCandidate(context, nestedNode);
                if (nested != null)
                {
                    return nested;
                }
            }
            else if (child is TerminalCstNode term)
            {
                if (IsIdentifierTerminal(term))
                {
                    var ident = new IdentifierExpr();
                    ident.SetSpan(term.Span);
                    ident.SetName(GetTokenText(term));
                    return ident;
                }
            }
        }

        return null;
    }

    private static bool TryCreatePathExpr(AstContext context, NonTerminalCstNode node, out PathExpr pathExpr)
    {
        pathExpr = null!;
        var name = node.NonTerminal?.DebugName ?? string.Empty;
        if (!string.Equals(name, "funcPath", StringComparison.Ordinal))
        {
            return false;
        }

        pathExpr = new PathExpr();
        pathExpr.BuildFromCst(context, node);
        return true;
    }

    private static bool IsArgumentContainer(NonTerminalCstNode node)
    {
        var name = node.NonTerminal?.DebugName ?? string.Empty;
        return name is "argList" or "positionalArgs" or "namedArgs" or "mixedArgs"
            or "namedArg" or "namedArgTail" or "positionalArgTail" or "mixedNamedTail";
    }

    private static bool IsIdentifierTerminal(TerminalCstNode term)
    {
        return term.Terminal?.ToString() == WellKnownStrings.Terminals.Identifier;
    }

    private static bool IsResumeTerminal(TerminalCstNode term)
    {
        return term.Terminal?.ToString() == WellKnownStrings.Keywords.Resume;
    }

    private static bool IsLiteralTerminal(TerminalCstNode term)
    {
        return term.Terminal?.ToString() is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.CallExpr);

        if (Function != null)
        {
            var funcElement = doc.CreateElement(WellKnownStrings.XmlElements.Function);
            funcElement.AppendChild(Function.ToXmlElement(doc));
            element.AppendChild(funcElement);
        }

        if (PositionalArgs.Count > 0)
        {
            var argsElement = doc.CreateElement(WellKnownStrings.XmlElements.PositionalArgs);
            foreach (var arg in PositionalArgs)
            {
                argsElement.AppendChild(arg.ToXmlElement(doc));
            }
            element.AppendChild(argsElement);
        }

        if (NamedArgs.Count > 0)
        {
            var argsElement = doc.CreateElement(WellKnownStrings.XmlElements.NamedArgs);
            foreach (var arg in NamedArgs)
            {
                argsElement.AppendChild(arg.ToXmlElement(doc));
            }
            element.AppendChild(argsElement);
        }

        return element;
    }
}

/// <summary>
/// 命名参数
/// </summary>
public record NamedArg : EidosAstNode
{
    /// <summary>
    /// 参数名称
    /// </summary>
    public string Name { get; internal set; } = "";

    /// <summary>
    /// 参数值
    /// </summary>
    public EidosAstNode? Value { get; internal set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is NonTerminalCstNode ntNode)
        {
            var foundName = false;
            foreach (var child in ntNode.Children)
            {
                if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (!IsPunctuation(text) && !foundName)
                    {
                        Name = text;
                        foundName = true;
                    }
                }
                else if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
                {
                    Value = expr;
                }
            }
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.NamedArg);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (Value != null)
        {
            var valueElement = doc.CreateElement(WellKnownStrings.XmlElements.Value);
            valueElement.AppendChild(Value.ToXmlElement(doc));
            element.AppendChild(valueElement);
        }

        return element;
    }
}
