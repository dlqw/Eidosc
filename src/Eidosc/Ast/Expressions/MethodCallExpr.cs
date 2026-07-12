using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 方法调用表达式
/// </summary>
/// <example>
/// obj.foo(a, b)
/// list.map(fn(x) x * 2)
/// </example>
public record MethodCallExpr : Expression
{
    /// <summary>
    /// 接收者对象
    /// </summary>
    public EidosAstNode? Receiver { get; private set; }

    /// <summary>
    /// 方法名称
    /// </summary>
    public string MethodName { get; private set; } = "";

    /// <summary>
    /// 位置参数
    /// </summary>
    public List<EidosAstNode> PositionalArgs { get; private set; } = [];

    /// <summary>
    /// 命名参数
    /// </summary>
    public List<NamedArg> NamedArgs { get; private set; } = [];

    /// <summary>
    /// 是否显式写出了调用参数括号
    /// </summary>
    public bool HasExplicitCallSyntax { get; private set; }

    /// <summary>
    /// Types 阶段是否已把裸点访问判定为字段读取
    /// </summary>
    public bool ResolvedAsFieldAccess { get; private set; }

    /// <summary>
    /// 若按字段读取解析，记录对应字段符号
    /// </summary>
    public SymbolId FieldSymbolId { get; private set; } = SymbolId.None;

    /// <summary>
    /// 若裸点访问被解析为 CStruct 字段 getter，记录 getter 函数名（如 "point_x"）。
    /// </summary>
    public string? CStructGetterName { get; private set; }

    /// <summary>
    /// 若裸点访问被解析为 CStruct 字段 getter，记录 getter 函数的 SymbolId。
    /// </summary>
    public SymbolId CStructGetterSymbolId { get; private set; } = SymbolId.None;

    /// <summary>
    /// Gets the visible function candidates that share <see cref="MethodName"/> for type-directed lookup.
    /// </summary>
    public List<SymbolId> MethodCandidateSymbolIds { get; private set; } = [];

    /// <summary>
    /// 类型推断阶段为 empty method call 合成的 Unit 实参数量。
    /// </summary>
    public int SynthesizedUnitArgumentCount { get; private set; }

    /// <summary>
    /// 该 empty method call 是否使用 FFI 的 Unit 参数 ABI 省略。
    /// </summary>
    public bool UsesFfiUnitArgumentElision { get; private set; }

    /// <summary>
    /// 设置源码范围。
    /// </summary>
    public void SetSpan(Eidosc.Utils.SourceSpan span) => Span = span;

    /// <summary>
    /// 设置接收者表达式。
    /// </summary>
    public void SetReceiver(EidosAstNode receiver) => Receiver = receiver;

    /// <summary>
    /// 设置方法名称。
    /// </summary>
    public void SetMethodName(string methodName) => MethodName = methodName;

    /// <summary>
    /// 添加位置参数。
    /// </summary>
    public void AddPositionalArg(EidosAstNode arg) => PositionalArgs.Add(arg);

    /// <summary>
    /// 添加命名参数。
    /// </summary>
    public void AddNamedArg(NamedArg arg) => NamedArgs.Add(arg);

    /// <summary>
    /// 标记调用显式写出了参数括号。
    /// </summary>
    public void MarkExplicitCallSyntax() => HasExplicitCallSyntax = true;

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

    public CallExpr ToDesugaredCall()
    {
        var call = new CallExpr();
        call.InferredType = InferredType;
        var function = new IdentifierExpr();
        function.SetSpan(Span);
        function.SetName(CStructGetterName ?? MethodName);
        function.SymbolId = CStructGetterSymbolId.IsValid
            ? CStructGetterSymbolId
            : SymbolId;

        call.SetFunction(function);
        call.SetSpan(Span);

        if (Receiver != null)
        {
            call.AddPositionalArg(Receiver);
        }

        foreach (var arg in PositionalArgs)
        {
            call.AddPositionalArg(arg);
        }

        foreach (var arg in NamedArgs)
        {
            call.AddNamedArg(arg);
        }

        if (SynthesizedUnitArgumentCount > 0)
        {
            call.MarkSyntheticUnitArguments(SynthesizedUnitArgumentCount);
        }
        else if (UsesFfiUnitArgumentElision)
        {
            call.MarkFfiUnitArgumentElision();
        }

        return call;
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Receiver = null;
        MethodName = "";
        PositionalArgs = [];
        NamedArgs = [];
        HasExplicitCallSyntax = false;
        ResolvedAsFieldAccess = false;
        FieldSymbolId = SymbolId.None;
        CStructGetterName = null;
        CStructGetterSymbolId = SymbolId.None;
        MethodCandidateSymbolIds = [];
        ClearEmptyCallResolution();

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        var identifierTerms = ntNode.Children
            .OfType<TerminalCstNode>()
            .Where(IsIdentifierTerminal)
            .ToList();
        var methodNameTerm = identifierTerms.LastOrDefault();
        if (methodNameTerm != null)
        {
            MethodName = GetTokenText(methodNameTerm);
        }

        foreach (var child in ntNode.Children)
        {
            if (child is TerminalCstNode term)
            {
                if (IsIdentifierTerminal(term) &&
                    methodNameTerm != null &&
                    !ReferenceEquals(term, methodNameTerm) &&
                    Receiver == null)
                {
                    Receiver = CreateIdentifierExpr(term);
                }

                continue;
            }

            if (child is not NonTerminalCstNode nestedNode)
            {
                continue;
            }

            if (IsArgumentContainer(nestedNode))
            {
                if (string.Equals(nestedNode.NonTerminal?.DebugName, "methodCallArgs", StringComparison.Ordinal))
                {
                    HasExplicitCallSyntax = true;
                }

                ExtractArgsFromNode(nestedNode);
                continue;
            }

            if (Receiver == null && !IsArgumentContainer(nestedNode))
            {
                var candidate = ExtractExpressionCandidate(nestedNode);
                if (candidate != null)
                {
                    Receiver = candidate;
                    continue;
                }
            }

            ExtractArgsFromNode(nestedNode);
        }
    }

    private void ExtractArgsFromNode(NonTerminalCstNode node)
    {
        if (node.AstNode is NamedArg namedArg)
        {
            NamedArgs.Add(namedArg);
            return;
        }

        if (node.AstNode is EidosAstNode expr && !ReferenceEquals(expr, Receiver))
        {
            PositionalArgs.Add(expr);
            return;
        }

        foreach (var child in node.Children)
        {
            if (child is TerminalCstNode term &&
                TryCreateArgumentExpressionFromTerminal(term, out var terminalArgument))
            {
                PositionalArgs.Add(terminalArgument);
                continue;
            }

            if (child is not NonTerminalCstNode nestedNode)
            {
                continue;
            }

            if (nestedNode.AstNode is NamedArg nestedNamedArg)
            {
                NamedArgs.Add(nestedNamedArg);
            }
            else if (nestedNode.AstNode is EidosAstNode nestedExpr && !ReferenceEquals(nestedExpr, Receiver))
            {
                PositionalArgs.Add(nestedExpr);
            }
            else
            {
                ExtractArgsFromNode(nestedNode);
            }
        }
    }

    private static EidosAstNode? ExtractExpressionCandidate(NonTerminalCstNode node)
    {
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
                var nested = ExtractExpressionCandidate(nestedNode);
                if (nested != null)
                {
                    return nested;
                }
            }
            else if (child is TerminalCstNode term && IsIdentifierTerminal(term))
            {
                var ident = new IdentifierExpr();
                ident.SetSpan(term.Span);
                ident.SetName(GetTokenText(term));
                return ident;
            }
        }

        return null;
    }

    private static bool IsArgumentContainer(NonTerminalCstNode node)
    {
        var name = node.NonTerminal?.DebugName ?? string.Empty;
        return name is "argList" or "positionalArgs" or "namedArgs" or "mixedArgs"
            or "namedArg" or "namedArgTail" or "positionalArgTail" or "mixedNamedTail"
            or "methodCallArgs";
    }

    private static bool IsIdentifierTerminal(TerminalCstNode term)
    {
        return term.Terminal?.ToString() == WellKnownStrings.Terminals.Identifier;
    }

    private static IdentifierExpr CreateIdentifierExpr(TerminalCstNode term)
    {
        var ident = new IdentifierExpr();
        ident.SetSpan(term.Span);
        ident.SetName(GetTokenText(term));
        return ident;
    }

    private static bool TryCreateArgumentExpressionFromTerminal(
        TerminalCstNode term,
        out EidosAstNode expression)
    {
        expression = null!;
        var terminalName = term.Terminal?.ToString();
        if (terminalName == WellKnownStrings.Terminals.Identifier)
        {
            expression = CreateIdentifierExpr(term);
            return true;
        }

        if (terminalName is WellKnownStrings.Terminals.Number
            or WellKnownStrings.Terminals.String
            or WellKnownStrings.Terminals.Char
            or WellKnownStrings.Terminals.Boolean)
        {
            var literal = new LiteralExpr();
            literal.SetSpan(term.Span);
            literal.SetLiteral(GetTokenText(term));
            expression = literal;
            return true;
        }

        return false;
    }

    public void MarkResolvedAsFieldAccess(SymbolId fieldSymbolId)
    {
        ResolvedAsFieldAccess = true;
        FieldSymbolId = fieldSymbolId;
    }

    /// <summary>
    /// 标记此裸点访问已解析为 CStruct getter 函数。
    /// </summary>
    public void MarkResolvedAsCStructAccess(string getterName, SymbolId getterSymbolId)
    {
        CStructGetterName = getterName;
        CStructGetterSymbolId = getterSymbolId;
    }

    /// <summary>
    /// Clears candidates collected during name resolution.
    /// </summary>
    public void ClearMethodCandidates() => MethodCandidateSymbolIds.Clear();

    /// <summary>
    /// Adds a visible method candidate for type-directed lookup.
    /// </summary>
    public void AddMethodCandidate(SymbolId symbolId)
    {
        if (symbolId.IsValid && !MethodCandidateSymbolIds.Contains(symbolId))
        {
            MethodCandidateSymbolIds.Add(symbolId);
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.MethodCallExpr);
        element.SetAttribute(WellKnownStrings.XmlAttributes.MethodName, MethodName);
        if (HasExplicitCallSyntax)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.HasExplicitCallSyntax, WellKnownStrings.AdditionalKeywords.True);
        }
        if (ResolvedAsFieldAccess)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.ResolvedAsFieldAccess, WellKnownStrings.AdditionalKeywords.True);
        }
        if (CStructGetterName != null)
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.CstructGetterName, CStructGetterName);
        }

        if (Receiver != null)
        {
            var receiverElement = doc.CreateElement(WellKnownStrings.XmlElements.Receiver);
            receiverElement.AppendChild(Receiver.ToXmlElement(doc));
            element.AppendChild(receiverElement);
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
