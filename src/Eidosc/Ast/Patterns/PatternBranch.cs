using System.Xml;
using Eidosc.Utils;

namespace Eidosc.Ast.Patterns;

/// <summary>
/// 模式分支
/// </summary>
/// <example>
/// Nil => 0
/// Cons{head: h, tail: t} when h > 0 => h + length(t)
/// </example>
public record PatternBranch : EidosAstNode
{
    /// <summary>
    /// 模式
    /// </summary>
    public Pattern? Pattern { get; private set; }

    /// <summary>
    /// 守卫条件（可选）
    /// </summary>
    public EidosAstNode? Guard { get; private set; }

    /// <summary>
    /// 结果表达式
    /// </summary>
    public EidosAstNode? Expression { get; private set; }

    public void SetExpression(EidosAstNode expr) => Expression = expr;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        Pattern = null;
        Guard = null;
        Expression = null;

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        if (TryExtractCurriedBranch(context, ntNode))
        {
            return;
        }

        BuildStandardBranch(context, ntNode);
    }

    private void BuildStandardBranch(AstContext context, NonTerminalCstNode ntNode)
    {
        var foundArrow = false;
        var guards = new List<EidosAstNode>();

        foreach (var child in ntNode.Children)
        {
            var childName = (child as NonTerminalCstNode)?.NonTerminal?.DebugName ?? "";

            // === 阶段1: 在 => 之前，提取模式 ===
            if (!foundArrow)
            {
                if (TryCollectGuardExpressions(child, guards))
                {
                    continue;
                }

                // 处理有 AstNode 的模式节点
                if (child is NonTerminalCstNode { AstNode: Pattern patternNode } && Pattern == null)
                {
                    Pattern = Pattern.NormalizePatternNode(patternNode);
                }
                // pattern ::= literal，literal 节点默认构建为 LiteralExpr，这里提升为 LiteralPattern
                else if (child is NonTerminalCstNode literalNode && childName == "literal" && Pattern == null)
                {
                    Pattern = TryCreateLiteralPatternFromNode(literalNode);
                }
                // wildcard pattern 在当前 grammar 的 squeezing 路径下可能变成空 patternAtom，
                // 此处兜底将其还原为 WildcardPattern，避免后续 HIR 丢失分支模式。
                else if (child is NonTerminalCstNode patternAtomNode &&
                         childName == "patternAtom" &&
                         Pattern == null &&
                         patternAtomNode.Children.Count == 0)
                {
                    var wildcard = new WildcardPattern();
                    wildcard.BuildFromCst(context, patternAtomNode);
                    Pattern = wildcard;
                }
                // 处理 tuplePattern 节点
                else if (childName == "tuplePattern" && Pattern == null)
                {
                    Pattern = Pattern.NormalizePatternNode(CreateTuplePatternFromCst((NonTerminalCstNode)child));
                }
                // 处理终端节点 - 从 identifier 创建 VarPattern 或检测 =>
                else if (child is TerminalCstNode term)
                {
                    var text = GetTokenText(term);
                    if (text == WellKnownStrings.Punctuation.FatArrow)
                    {
                        foundArrow = true;
                    }
                    else if (Pattern == null && text != WellKnownStrings.Keywords.When && !IsPunctuation(text))
                    {
                        if (IsIdentifierTerminal(term))
                        {
                            Pattern = CreateVarPatternFromTerminal(term);
                        }
                        else if (IsLiteralTerminal(term))
                        {
                            Pattern = CreateLiteralPatternFromTerminal(term);
                        }
                    }
                }
            }
            // === 阶段2: 在 => 之后，提取表达式 ===
            else
            {
                // 处理有 AstNode 的表达式节点
                if (child is NonTerminalCstNode { AstNode: EidosAstNode expr } && Expression == null)
                {
                    Expression = expr;
                }
                // 处理终端节点 - 从 identifier/literal 创建表达式
                else if (child is TerminalCstNode term && Expression == null)
                {
                    var text = GetTokenText(term);
                    if (!IsPunctuation(text))
                    {
                        Expression = CreateExprFromTerminal(term);
                    }
                }
                // 处理没有 AstNode 的表达式非终结符
                else if (child is NonTerminalCstNode childNt && Expression == null && IsExpressionNode(childName))
                {
                    Expression = CreateExprFromCst(childNt);
                }
            }
        }

        Guard = CombineGuards(guards);
    }

    private bool TryExtractCurriedBranch(AstContext context, NonTerminalCstNode node)
    {
        if (!TryFindCurriedBranchRhs(node, out var rhsNode) ||
            !HasCurriedArrowChain(rhsNode))
        {
            return false;
        }

        var leadingPattern = ExtractLeadingPattern(context, node);
        if (leadingPattern == null)
        {
            return false;
        }

        var headPatterns = new List<Pattern> { leadingPattern };
        var guards = new List<EidosAstNode>();
        if (!TryExtractCurriedBranchRhs(context, rhsNode, headPatterns, guards, out var finalExpression))
        {
            return false;
        }

        Pattern = headPatterns.Count == 1
            ? headPatterns[0]
            : CreateTuplePatternFromElements(headPatterns);
        Guard = CombineGuards(guards);
        Expression = finalExpression;
        return true;
    }

    private static bool TryFindCurriedBranchRhs(NonTerminalCstNode branchNode, out NonTerminalCstNode rhsNode)
    {
        foreach (var child in branchNode.Children)
        {
            if (child is NonTerminalCstNode childNt &&
                string.Equals(childNt.NonTerminal?.DebugName, "curriedBranchRhs", StringComparison.Ordinal))
            {
                rhsNode = childNt;
                return true;
            }
        }

        rhsNode = null!;
        return false;
    }

    private static bool HasDirectFatArrow(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (IsFatArrowTerminal(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCurriedArrowChain(NonTerminalCstNode node)
    {
        if (HasDirectFatArrow(node))
        {
            return true;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt &&
                IsCurriedBranchNode(childNt.NonTerminal?.DebugName) &&
                HasCurriedArrowChain(childNt))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCollectCurriedBranchSegments(NonTerminalCstNode rhsNode, out List<EidosAstNode> segments)
    {
        segments = [];

        if (!HasDirectFatArrow(rhsNode))
        {
            foreach (var child in rhsNode.Children)
            {
                if (child is NonTerminalCstNode childNt &&
                    IsCurriedBranchNode(childNt.NonTerminal?.DebugName) &&
                    HasCurriedArrowChain(childNt))
                {
                    return TryCollectCurriedBranchSegments(childNt, out segments);
                }
            }

            if (TryExtractExpressionSegment(rhsNode, out var terminalExpr))
            {
                segments.Add(terminalExpr);
                return true;
            }

            return false;
        }

        var seenArrow = false;
        EidosAstNode? headExpr = null;

        foreach (var child in rhsNode.Children)
        {
            if (!seenArrow)
            {
                if (IsFatArrowTerminal(child))
                {
                    seenArrow = true;
                    continue;
                }

                if (headExpr == null && TryExtractExpressionSegment(child, out var extractedHead))
                {
                    headExpr = extractedHead;
                }

                continue;
            }

            if (child is NonTerminalCstNode childNt &&
                IsCurriedBranchNode(childNt.NonTerminal?.DebugName))
            {
                if (headExpr == null || !TryCollectCurriedBranchSegments(childNt, out var tailSegments))
                {
                    return false;
                }

                segments.Add(headExpr);
                segments.AddRange(tailSegments);
                return true;
            }

            if (TryExtractExpressionSegment(child, out var terminalTail))
            {
                if (headExpr == null)
                {
                    return false;
                }

                segments.Add(headExpr);
                segments.Add(terminalTail);
                return true;
            }
        }

        return false;
    }

    private bool TryExtractCurriedBranchRhs(
        AstContext context,
        NonTerminalCstNode rhsNode,
        List<Pattern> headPatterns,
        List<EidosAstNode> guards,
        out EidosAstNode finalExpression)
    {
        if (!HasDirectFatArrow(rhsNode))
        {
            foreach (var child in rhsNode.Children)
            {
                if (child is NonTerminalCstNode childNt &&
                    IsCurriedBranchNode(childNt.NonTerminal?.DebugName) &&
                    HasCurriedArrowChain(childNt))
                {
                    return TryExtractCurriedBranchRhs(context, childNt, headPatterns, guards, out finalExpression);
                }
            }

            return TryExtractExpressionSegment(rhsNode, out finalExpression);
        }

        var seenArrow = false;
        var headNodes = new List<ConcreteSyntaxNode>();

        foreach (var child in rhsNode.Children)
        {
            if (!seenArrow)
            {
                if (IsFatArrowTerminal(child))
                {
                    if (!TryBuildCurriedHead(headNodes, out var currentPattern, out var currentGuard))
                    {
                        finalExpression = null!;
                        return false;
                    }

                    headPatterns.Add(currentPattern);
                    if (currentGuard != null)
                    {
                        guards.Add(currentGuard);
                    }

                    seenArrow = true;
                    continue;
                }

                headNodes.Add(child);
                continue;
            }

            if (child is NonTerminalCstNode rhsChildNt &&
                IsCurriedBranchNode(rhsChildNt.NonTerminal?.DebugName))
            {
                return TryExtractCurriedBranchRhs(context, rhsChildNt, headPatterns, guards, out finalExpression);
            }

            if (TryExtractExpressionSegment(child, out finalExpression))
            {
                return true;
            }
        }

        finalExpression = null!;
        return false;
    }

    private static bool TryExtractPatternSegment(AstContext context, ConcreteSyntaxNode node, out Pattern pattern)
    {
        pattern = null!;

        if (node is TerminalCstNode terminal)
        {
            var text = GetTokenText(terminal);
            if (text == WellKnownStrings.Punctuation.Underscore)
            {
                var wildcard = new WildcardPattern();
                wildcard.BuildFromCst(context, terminal);
                pattern = wildcard;
                return true;
            }

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

            return false;
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            return false;
        }

        var nodeName = ntNode.NonTerminal?.DebugName ?? string.Empty;
        if (ntNode.AstNode is Pattern patternNode)
        {
            pattern = Pattern.NormalizePatternNode(patternNode);
            return true;
        }

        if (ntNode.AstNode is EidosAstNode exprAstNode &&
            TryConvertCurriedExprToPattern(exprAstNode, out pattern))
        {
            return true;
        }

        if (string.Equals(nodeName, "literal", StringComparison.Ordinal))
        {
            var literalPattern = TryCreateLiteralPatternFromNode(ntNode);
            if (literalPattern != null)
            {
                pattern = literalPattern;
                return true;
            }
        }

        if (string.Equals(nodeName, "tuplePattern", StringComparison.Ordinal))
        {
            pattern = Pattern.NormalizePatternNode(CreateTuplePatternFromCst(ntNode));
            return true;
        }

        if (string.Equals(nodeName, "listPattern", StringComparison.Ordinal))
        {
            var listPattern = new ListPattern();
            listPattern.BuildFromCst(context, ntNode);
            pattern = Pattern.NormalizePatternNode(listPattern);
            return true;
        }

        if (string.Equals(nodeName, "ctorPattern", StringComparison.Ordinal) ||
            string.Equals(nodeName, "patternGuardCtorLhs", StringComparison.Ordinal))
        {
            var ctorPattern = new CtorPattern();
            ctorPattern.BuildFromCst(context, ntNode);
            pattern = Pattern.NormalizePatternNode(ctorPattern);
            return true;
        }

        if (string.Equals(nodeName, "patternAtom", StringComparison.Ordinal) && ntNode.Children.Count == 0)
        {
            var wildcard = new WildcardPattern();
            wildcard.BuildFromCst(context, ntNode);
            pattern = wildcard;
            return true;
        }

        if (TryCreateExpressionFromNodeShape(ntNode, nodeName, out var shapedExpr) &&
            TryConvertCurriedExprToPattern(shapedExpr, out pattern))
        {
            return true;
        }

        foreach (var child in ntNode.Children)
        {
            if (TryExtractPatternSegment(context, child, out pattern))
            {
                return true;
            }
        }

        return false;
    }

    private Pattern? ExtractLeadingPattern(AstContext context, NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (IsFatArrowTerminal(child))
            {
                break;
            }

            if (child is NonTerminalCstNode childNt)
            {
                var childName = childNt.NonTerminal?.DebugName ?? string.Empty;
                if (string.Equals(childName, "patternGuard", StringComparison.Ordinal) ||
                    string.Equals(childName, "patternGuardSeq", StringComparison.Ordinal))
                {
                    return null;
                }

                if (childNt.AstNode is Pattern patternNode)
                {
                    return Pattern.NormalizePatternNode(patternNode);
                }

                if (string.Equals(childName, "literal", StringComparison.Ordinal))
                {
                    var literalPattern = TryCreateLiteralPatternFromNode(childNt);
                    if (literalPattern != null)
                    {
                        return literalPattern;
                    }
                }

                if (string.Equals(childName, "tuplePattern", StringComparison.Ordinal))
                {
                    return Pattern.NormalizePatternNode(CreateTuplePatternFromCst(childNt));
                }

                if (string.Equals(childName, "patternAtom", StringComparison.Ordinal) && childNt.Children.Count == 0)
                {
                    var wildcard = new WildcardPattern();
                    wildcard.BuildFromCst(context, childNt);
                    return wildcard;
                }
            }

            if (child is not TerminalCstNode terminal)
            {
                continue;
            }

            if (IsIdentifierTerminal(terminal))
            {
                return CreateVarPatternFromTerminal(terminal);
            }

            if (IsLiteralTerminal(terminal))
            {
                return CreateLiteralPatternFromTerminal(terminal);
            }
        }

        return null;
    }

    private bool TryExtractCurriedExprSegments(NonTerminalCstNode branchNode, out List<EidosAstNode> segments)
    {
        segments = [];
        var seenFirstArrow = false;
        var capturedFirstSegment = false;

        foreach (var child in branchNode.Children)
        {
            if (IsFatArrowTerminal(child))
            {
                seenFirstArrow = true;
                continue;
            }

            if (!seenFirstArrow)
            {
                continue;
            }

            if (!capturedFirstSegment && TryExtractExpressionSegment(child, out var firstSegment))
            {
                segments.Add(firstSegment);
                capturedFirstSegment = true;
                continue;
            }

            if (child is NonTerminalCstNode childNt &&
                IsCurriedExprTailNode(childNt.NonTerminal?.DebugName))
            {
                CollectCurriedExprTailSegments(childNt, segments);
            }
        }

        return segments.Count > 0;
    }

    private static void CollectCurriedExprTailSegments(NonTerminalCstNode tailNode, List<EidosAstNode> target)
    {
        var seenArrow = false;

        foreach (var child in tailNode.Children)
        {
            if (IsFatArrowTerminal(child))
            {
                seenArrow = true;
                continue;
            }

            if (seenArrow && TryExtractExpressionSegment(child, out var segment))
            {
                target.Add(segment);
                seenArrow = false;
                continue;
            }

            if (child is NonTerminalCstNode childNt &&
                IsCurriedExprTailNode(childNt.NonTerminal?.DebugName))
            {
                CollectCurriedExprTailSegments(childNt, target);
            }
        }
    }

    private static bool TryExtractExpressionSegment(ConcreteSyntaxNode node, out EidosAstNode expression)
    {
        if (node is NonTerminalCstNode ntNodeWithAst &&
            ntNodeWithAst.AstNode is EidosAstNode exprNode)
        {
            expression = exprNode;
            return true;
        }

        if (node is TerminalCstNode terminal)
        {
            if (string.Equals(GetTokenText(terminal), WellKnownStrings.Punctuation.Underscore, StringComparison.Ordinal))
            {
                var wildcard = new WildcardPattern();
                wildcard.BuildFromCst(new AstContext(), terminal);
                expression = wildcard;
                return true;
            }

            if (IsIdentifierTerminal(terminal) || IsLiteralTerminal(terminal))
            {
                expression = CreateExprFromTerminal(terminal);
                return true;
            }

            expression = null!;
            return false;
        }

        if (node is not NonTerminalCstNode ntNode)
        {
            expression = null!;
            return false;
        }

        var nodeName = ntNode.NonTerminal?.DebugName ?? string.Empty;
        if (TryCreateExpressionFromNodeShape(ntNode, nodeName, out expression))
        {
            return true;
        }

        if (string.Equals(nodeName, "wildcardPattern", StringComparison.Ordinal))
        {
            var wildcard = new WildcardPattern();
            wildcard.BuildFromCst(new AstContext(), ntNode);
            expression = wildcard;
            return true;
        }

        if (IsExpressionNode(nodeName))
        {
            expression = CreateExprFromCst(ntNode);
            return true;
        }

        foreach (var child in ntNode.Children)
        {
            if (TryExtractExpressionSegment(child, out expression))
            {
                return true;
            }
        }

        expression = null!;
        return false;
    }

    private static bool TryCreateExpressionFromNodeShape(NonTerminalCstNode node, string nodeName, out EidosAstNode expression)
    {
        expression = null!;

        if (string.Equals(nodeName, "patternGuardCtorLhs", StringComparison.Ordinal) ||
            string.Equals(nodeName, "ctorPattern", StringComparison.Ordinal))
        {
            var ctorExpr = new Expressions.CtorExpr();
            ctorExpr.BuildFromCst(new AstContext(), node);
            expression = ctorExpr;
            return true;
        }

        if (string.Equals(nodeName, "tuplePattern", StringComparison.Ordinal))
        {
            var tupleExpr = new Expressions.TupleExpr();
            tupleExpr.BuildFromCst(new AstContext(), node);
            expression = tupleExpr;
            return true;
        }

        if (string.Equals(nodeName, "listPattern", StringComparison.Ordinal))
        {
            var listExpr = new Expressions.ListExpr();
            listExpr.BuildFromCst(new AstContext(), node);
            expression = listExpr;
            return true;
        }

        return false;
    }

    private static bool TryConvertCurriedExprToPattern(EidosAstNode exprNode, out Pattern pattern)
    {
        switch (exprNode)
        {
            case Pattern patternNode:
                pattern = Pattern.NormalizePatternNode(patternNode);
                return true;

            case Expressions.IdentifierExpr identifier when
                string.Equals(identifier.Name, WellKnownStrings.Punctuation.Underscore, StringComparison.Ordinal):
                var wildcard = new WildcardPattern();
                pattern = wildcard;
                return true;

            case Expressions.IdentifierExpr identifier:
                var varPattern = new VarPattern();
                varPattern.SetSpan(identifier.Span);
                varPattern.SetName(identifier.Name);
                pattern = varPattern;
                return true;

            case Expressions.LiteralExpr literal:
                var literalPattern = new LiteralPattern();
                literalPattern.SetSpan(literal.Span);
                var rawText = string.IsNullOrWhiteSpace(literal.RawText)
                    ? literal.Value?.ToString() ?? string.Empty
                    : literal.RawText;
                literalPattern.SetLiteral(rawText);
                pattern = literalPattern;
                return true;

            case Expressions.TupleExpr tupleExpr:
                return TryConvertTupleExprToPattern(tupleExpr, out pattern);

            case Expressions.CtorExpr ctorExpr:
                return TryConvertCtorExprToPattern(ctorExpr, out pattern);

            default:
                pattern = null!;
                return false;
        }
    }

    private static bool TryConvertTupleExprToPattern(Expressions.TupleExpr tupleExpr, out Pattern pattern)
    {
        var tuplePattern = new TuplePattern();
        tuplePattern.SetSpan(tupleExpr.Span);

        foreach (var element in tupleExpr.Elements)
        {
            if (!TryConvertCurriedExprToPattern(element, out var elementPattern))
            {
                pattern = null!;
                return false;
            }

            tuplePattern.AddElement(elementPattern);
        }

        pattern = tuplePattern;
        return true;
    }

    private static bool TryConvertCtorExprToPattern(Expressions.CtorExpr ctorExpr, out Pattern pattern)
    {
        var ctorPattern = new CtorPattern();
        ctorPattern.SetSpan(ctorExpr.Span);
        ctorPattern.SetConstructorName(ctorExpr.ConstructorName);

        if (ctorExpr.ConstructorPath?.ModulePath.Count > 0)
        {
            ctorPattern.SetModulePath(ctorExpr.ConstructorPath.ModulePath);
        }

        foreach (var arg in ctorExpr.PositionalArgs)
        {
            if (!TryConvertCurriedExprToPattern(arg, out var positionalPattern))
            {
                pattern = null!;
                return false;
            }

            ctorPattern.AddPositionalPattern(positionalPattern);
        }

        foreach (var namedArg in ctorExpr.NamedArgs)
        {
            if (!TryConvertFieldInitToPattern(namedArg, out var fieldPattern))
            {
                pattern = null!;
                return false;
            }

            ctorPattern.AddNamedPattern(fieldPattern);
        }

        pattern = ctorPattern;
        return true;
    }

    private static bool TryConvertFieldInitToPattern(Expressions.FieldInit fieldInit, out FieldPattern fieldPattern)
    {
        fieldPattern = new FieldPattern();
        fieldPattern.SetSpan(fieldInit.Span);
        fieldPattern.SetFieldName(fieldInit.FieldName);

        if (fieldInit.Value == null)
        {
            fieldPattern.SetPattern(null);
            return true;
        }

        if (!TryConvertCurriedExprToPattern(fieldInit.Value, out var valuePattern))
        {
            fieldPattern = null!;
            return false;
        }

        fieldPattern.SetPattern(valuePattern);
        return true;
    }

    private TuplePattern CreateTuplePatternFromElements(IEnumerable<Pattern> elements)
    {
        var tuplePattern = new TuplePattern();
        tuplePattern.SetSpan(Span);

        foreach (var element in elements)
        {
            tuplePattern.AddElement(element);
        }

        return tuplePattern;
    }

    private static bool HasCurriedExprTail(NonTerminalCstNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt &&
                IsCurriedExprTailNode(childNt.NonTerminal?.DebugName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCurriedExprTailNode(string? nodeName)
    {
        return !string.IsNullOrWhiteSpace(nodeName) &&
               nodeName.Contains("curriedExprTail", StringComparison.Ordinal);
    }

    private static bool IsCurriedBranchNode(string? nodeName)
    {
        return string.Equals(nodeName, "curriedBranchRhs", StringComparison.Ordinal) ||
               string.Equals(nodeName, "guardedCurriedBranchRhs", StringComparison.Ordinal);
    }

    private static bool IsFatArrowTerminal(ConcreteSyntaxNode node)
    {
        return node is TerminalCstNode term && string.Equals(GetTokenText(term), WellKnownStrings.Punctuation.FatArrow, StringComparison.Ordinal);
    }

    /// <summary>
    /// 从 patternGuard 节点提取守卫表达式
    /// </summary>
    private void ExtractGuardFromNode(NonTerminalCstNode guardNode)
    {
        Guard = ExtractGuardExpressionFromNode(guardNode);
    }

    private static EidosAstNode? ExtractGuardExpressionFromNode(NonTerminalCstNode guardNode)
    {
        if (TryBuildGuardExpression(guardNode.Children, out var guardExpression))
        {
            return guardExpression;
        }

        foreach (var child in guardNode.Children)
        {
            if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
            {
                return expr;
            }
            else if (child is NonTerminalCstNode childNt)
            {
                var childName = childNt.NonTerminal?.DebugName ?? "";
                if (IsExpressionNode(childName))
                {
                    return CreateExprFromCst(childNt);
                }
            }
            else if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (text != WellKnownStrings.Keywords.When && !IsPunctuation(text) && !IsOperatorToken(text))
                {
                    return CreateExprFromTerminal(term);
                }
            }
        }

        return null;
    }

    private static bool TryBuildCurriedHead(
        IReadOnlyList<ConcreteSyntaxNode> headNodes,
        out Pattern pattern,
        out EidosAstNode? guard)
    {
        pattern = null!;
        var guards = new List<EidosAstNode>();
        var patternContext = new AstContext();

        foreach (var node in headNodes)
        {
            if (TryCollectGuardExpressions(node, guards))
            {
                continue;
            }

            if (TryExtractPatternSegment(patternContext, node, out pattern))
            {
                guard = CombineGuards(guards);
                return true;
            }
        }

        EidosAstNode? headExpression = null;

        foreach (var node in headNodes)
        {
            if (TryCollectGuardExpressions(node, guards))
            {
                continue;
            }

            if (headExpression == null && TryExtractExpressionSegment(node, out var expressionSegment))
            {
                headExpression = expressionSegment;
            }
        }

        guard = CombineGuards(guards);
        return headExpression != null &&
               TryConvertCurriedExprToPattern(headExpression, out pattern);
    }

    private static bool TryBuildGuardExpression(
        IReadOnlyList<ConcreteSyntaxNode> nodes,
        out EidosAstNode? guardExpression)
    {
        if (!TrySplitGuardLikeSequence(nodes, out _, out var guardValue, out var guardSource))
        {
            guardExpression = null;
            return false;
        }

        return TryComposeGuardExpression(guardValue, guardSource, nodes, out guardExpression);
    }

    private static bool TryComposeGuardExpression(
        EidosAstNode? guardValue,
        EidosAstNode? guardSource,
        IReadOnlyList<ConcreteSyntaxNode> nodes,
        out EidosAstNode? guardExpression)
    {
        if (guardValue == null)
        {
            guardExpression = null;
            return true;
        }

        if (guardSource == null)
        {
            guardExpression = guardValue;
            return true;
        }

        if (!TryConvertCurriedExprToPattern(guardValue, out var guardPattern))
        {
            guardExpression = null;
            return false;
        }

        var patternGuard = new Expressions.PatternGuardExpr();
        patternGuard.SetSpanValue(GetNodesSpan(nodes, guardValue.Span));
        patternGuard.SetPattern(guardPattern);
        patternGuard.SetSourceExpression(guardSource);
        guardExpression = patternGuard;
        return true;
    }

    private static bool TrySplitGuardLikeSequence(
        IReadOnlyList<ConcreteSyntaxNode> nodes,
        out EidosAstNode? headExpression,
        out EidosAstNode? guardValue,
        out EidosAstNode? guardSource)
    {
        EidosAstNode? localHeadExpression = null;
        EidosAstNode? localGuardValue = null;
        EidosAstNode? localGuardSource = null;

        const int headMode = 0, guardMode = 1, sourceMode = 2;
        var mode = headMode;
        foreach (var node in nodes)
        {
            VisitNode(node);
        }

        headExpression = localHeadExpression;
        guardValue = localGuardValue;
        guardSource = localGuardSource;
        return localGuardValue != null || localHeadExpression != null;

        void VisitNode(ConcreteSyntaxNode node)
        {
            if (node is TerminalCstNode terminal)
            {
                var text = GetTokenText(terminal);
                if (text == WellKnownStrings.Keywords.When)
                {
                    mode = guardMode;
                    return;
                }

                if (text == WellKnownStrings.Punctuation.LeftArrow)
                {
                    mode = sourceMode;
                    return;
                }

                if (!TryExtractExpressionSegment(terminal, out var terminalExpression))
                {
                    return;
                }

                AssignExpression(terminalExpression);
                return;
            }

            if (node is not NonTerminalCstNode ntNode)
            {
                return;
            }

            var nodeName = ntNode.NonTerminal?.DebugName ?? string.Empty;
            if (string.Equals(nodeName, "patternGuard", StringComparison.Ordinal) ||
                string.Equals(nodeName, "patternGuardSourceTail", StringComparison.Ordinal))
            {
                foreach (var child in ntNode.Children)
                {
                    VisitNode(child);
                }

                return;
            }

            if (!TryExtractExpressionSegment(ntNode, out var expression))
            {
                foreach (var child in ntNode.Children)
                {
                    VisitNode(child);
                }

                return;
            }

            AssignExpression(expression);
        }

        void AssignExpression(EidosAstNode expression)
        {
            switch (mode)
            {
                case headMode when localHeadExpression == null:
                    localHeadExpression = expression;
                    break;

                case guardMode when localGuardValue == null:
                    localGuardValue = expression;
                    break;

                case sourceMode when localGuardSource == null:
                    localGuardSource = expression;
                    break;
            }
        }
    }

    private static SourceSpan GetNodesSpan(IReadOnlyList<ConcreteSyntaxNode> nodes, SourceSpan fallback)
    {
        return nodes.Count > 0 ? nodes[0].Span : fallback;
    }

    private static EidosAstNode? CombineGuards(IReadOnlyList<EidosAstNode> guards)
    {
        if (guards.Count == 0)
        {
            return null;
        }

        if (guards.Count == 1)
        {
            return guards[0];
        }

        var sequence = new Expressions.SequentialGuardExpr();
        sequence.SetSpanValue(guards[0].Span);
        foreach (var guard in guards)
        {
            sequence.AddGuard(guard);
        }

        return sequence;
    }

    private static bool TryCollectGuardExpressions(ConcreteSyntaxNode node, List<EidosAstNode> guards)
    {
        if (node is not NonTerminalCstNode ntNode)
        {
            return false;
        }

        var nodeName = ntNode.NonTerminal?.DebugName ?? string.Empty;
        if (string.Equals(nodeName, "patternGuard", StringComparison.Ordinal))
        {
            var guard = ExtractGuardExpressionFromNode(ntNode);
            if (guard != null)
            {
                guards.Add(guard);
            }

            return true;
        }

        if (!string.Equals(nodeName, "patternGuardSeq", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var child in ntNode.Children)
        {
            _ = TryCollectGuardExpressions(child, guards);
        }

        return true;
    }

    /// <summary>
    /// 检查是否是表达式节点
    /// </summary>
    private static bool IsExpressionNode(string name)
    {
        return name.EndsWith("Expr") ||
               name.EndsWith("Tail") ||
               name == "literal" ||
               name == "patternGuardBinding" ||
               name == "curriedBranchRhs" ||
               name == "curriedBranchHeadExpr";
    }

    /// <summary>
    /// 从终端节点创建表达式
    /// </summary>
    private static EidosAstNode CreateExprFromTerminal(TerminalCstNode term)
    {
        var text = GetTokenText(term);
        var terminalName = term.Terminal?.ToString() ?? "";

        if (terminalName == WellKnownStrings.Terminals.Identifier)
        {
            var ident = new Expressions.IdentifierExpr();
            ident.SetSpan(term.Span);
            ident.SetName(text);
            return ident;
        }
        else
        {
            var literal = new Expressions.LiteralExpr();
            literal.SetSpan(term.Span);
            literal.SetLiteral(text);
            return literal;
        }
    }

    /// <summary>
    /// 从 CST 创建表达式（简化版本，处理基本表达式）
    /// </summary>
    private static EidosAstNode CreateExprFromCst(NonTerminalCstNode node)
    {
        var nodeName = node.NonTerminal?.DebugName ?? string.Empty;
        if (TryCreateExpressionFromNodeShape(node, nodeName, out var shapedExpr))
        {
            return shapedExpr;
        }

        if (string.Equals(nodeName, "unaryExpr", StringComparison.Ordinal))
        {
            var unary = new Expressions.UnaryExpr();
            unary.BuildFromCst(null!, node);
            return unary;
        }

        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode { AstNode: EidosAstNode expr })
            {
                return expr;
            }
            else if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (!IsPunctuation(text) && !IsOperatorToken(text))
                {
                    return CreateExprFromTerminal(term);
                }
            }
        }
        // 返回一个空的标识符表达式作为 fallback
        var fallback = new Expressions.IdentifierExpr();
        fallback.SetSpan(node.Span);
        fallback.SetName(WellKnownStrings.Punctuation.Underscore);
        return fallback;
    }

    private static bool IsOperatorToken(string text)
    {
        return text is WellKnownStrings.Operators.Not
            or WellKnownStrings.Operators.Subtract
            or WellKnownStrings.Operators.Add
            or WellKnownStrings.Operators.Multiply
            or WellKnownStrings.Operators.Divide
            or WellKnownStrings.Operators.Modulo
            or WellKnownStrings.Operators.And
            or WellKnownStrings.Operators.Or
            or WellKnownStrings.Operators.Equal
            or WellKnownStrings.Operators.NotEqual
            or WellKnownStrings.Operators.Less
            or WellKnownStrings.Operators.LessEqual
            or WellKnownStrings.Operators.Greater
            or WellKnownStrings.Operators.GreaterEqual;
    }

    /// <summary>
    /// 检查终端节点是否是标识符
    /// </summary>
    private static bool IsIdentifierTerminal(TerminalCstNode term)
    {
        if (term.Terminal == null) return false;
        var terminalName = term.Terminal.ToString();
        return terminalName == WellKnownStrings.Terminals.Identifier;
    }

    /// <summary>
    /// 检查终端节点是否是字面量
    /// </summary>
    private static bool IsLiteralTerminal(TerminalCstNode term)
    {
        if (term.Terminal == null) return false;
        var terminalName = term.Terminal.ToString();
        return terminalName is WellKnownStrings.Terminals.Number or WellKnownStrings.Terminals.String or WellKnownStrings.Terminals.Char or WellKnownStrings.Terminals.Boolean;
    }

    /// <summary>
    /// 从终端节点创建 VarPattern
    /// </summary>
    private static VarPattern CreateVarPatternFromTerminal(TerminalCstNode term)
    {
        var pattern = new VarPattern();
        pattern.SetSpan(term.Span);
        pattern.SetName(GetTokenText(term));
        return pattern;
    }

    /// <summary>
    /// 从终端节点创建 LiteralPattern
    /// </summary>
    private static LiteralPattern CreateLiteralPatternFromTerminal(TerminalCstNode term)
    {
        var pattern = new LiteralPattern();
        pattern.SetSpan(term.Span);
        pattern.SetLiteral(GetTokenText(term));
        return pattern;
    }

    /// <summary>
    /// 从 literal 非终结符创建 LiteralPattern
    /// </summary>
    private static LiteralPattern? TryCreateLiteralPatternFromNode(NonTerminalCstNode literalNode)
    {
        foreach (var child in literalNode.Children)
        {
            if (child is TerminalCstNode term && IsLiteralTerminal(term))
            {
                return CreateLiteralPatternFromTerminal(term);
            }

            if (child is NonTerminalCstNode nested)
            {
                var nestedPattern = TryCreateLiteralPatternFromNode(nested);
                if (nestedPattern != null)
                {
                    return nestedPattern;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 从 CST 创建 TuplePattern
    /// </summary>
    private static TuplePattern CreateTuplePatternFromCst(NonTerminalCstNode node)
    {
        var tuplePattern = new TuplePattern();
        tuplePattern.BuildFromCst(null!, node);
        return tuplePattern;
    }

    private bool IsGuardExpression(NonTerminalCstNode parent, ConcreteSyntaxNode node)
    {
        // 检查节点之前是否有 WellKnownStrings.Keywords.When 关键字
        var children = parent.Children;
        var nodeIndex = children.IndexOf(node);

        for (var i = nodeIndex - 1; i >= 0; i--)
        {
            if (children[i] is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (text == WellKnownStrings.Keywords.When)
                {
                    return true;
                }
                if (text == WellKnownStrings.Punctuation.FatArrow)
                {
                    return false;
                }
            }
        }
        return false;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.PatternBranch);

        if (Pattern != null)
        {
            var patternElement = doc.CreateElement(WellKnownStrings.XmlElements.Pattern);
            patternElement.AppendChild(Pattern.ToXmlElement(doc));
            element.AppendChild(patternElement);
        }

        if (Guard != null)
        {
            var guardElement = doc.CreateElement(WellKnownStrings.XmlElements.Guard);
            guardElement.AppendChild(Guard.ToXmlElement(doc));
            element.AppendChild(guardElement);
        }

        if (Expression != null)
        {
            var exprElement = doc.CreateElement(WellKnownStrings.XmlElements.Expression);
            exprElement.AppendChild(Expression.ToXmlElement(doc));
            element.AppendChild(exprElement);
        }

        return element;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetPatterns(List<Pattern> patterns) => Pattern = patterns.Count == 1 ? patterns[0] : new TuplePattern { Elements = patterns };
    internal void SetPattern(Pattern pattern) => Pattern = pattern;
    internal void SetGuard(EidosAstNode guard) => Guard = guard;
    internal void SetBody(EidosAstNode body) => Expression = body;
}
