using System.Collections;
using System.Reflection;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Utils;

namespace Eidosc.Ide;

internal static class IdeStyleSuggestionBuilder
{
    private const string PrefixCallStyleCode = "S1001";
    private const string CurriedCallStyleCode = "S1002";
    private const string InfixCallStyleCode = "S1003";
    private const string PatternGuardStyleCode = "S1004";

    public static IReadOnlyList<Diagnostic.Diagnostic> Build(
        ModuleDecl module,
        string sourceText,
        string? sourceFilePath = null,
        SymbolTable? symbolTable = null,
        Func<SourceSpan, string, int?, bool>? validateReplacement = null)
    {
        if (string.IsNullOrEmpty(sourceText))
        {
            return [];
        }

        var diagnostics = new List<Diagnostic.Diagnostic>();
        var visited = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        Visit(module, parent: null, sourceText, sourceFilePath, diagnostics, visited, symbolTable, validateReplacement);
        return diagnostics;
    }

    private static void Visit(
        EidosAstNode node,
        EidosAstNode? parent,
        string sourceText,
        string? sourceFilePath,
        List<Diagnostic.Diagnostic> diagnostics,
        HashSet<EidosAstNode> visited,
        SymbolTable? symbolTable,
        Func<SourceSpan, string, int?, bool>? validateReplacement)
    {
        if (!visited.Add(node))
        {
            return;
        }

        if (node is CallExpr call && IsInRequestedSource(call.Span, sourceText, sourceFilePath))
        {
            if (parent is not CallExpr parentCall || !ReferenceEquals(parentCall.Function, node))
            {
                if (AddCallStyleDiagnostics(call, sourceText, diagnostics, symbolTable, validateReplacement))
                {
                    return;
                }
            }
        }

        if (node is FuncDef func && IsInRequestedSource(func.Span, sourceText, sourceFilePath))
        {
            AddPatternGuardStyleDiagnostic(func, diagnostics);
        }

        foreach (var child in EnumerateChildNodes(node))
        {
            Visit(child, node, sourceText, sourceFilePath, diagnostics, visited, symbolTable, validateReplacement);
        }
    }

    private static bool IsInRequestedSource(SourceSpan span, string sourceText, string? sourceFilePath)
    {
        if (span.Location.Position < 0 ||
            span.Length <= 0 ||
            span.Location.Position + span.Length > sourceText.Length)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sourceFilePath) &&
            !string.IsNullOrWhiteSpace(span.FilePath) &&
            !IsSameSourcePath(span.FilePath, sourceFilePath))
        {
            return false;
        }

        return true;
    }

    private static bool IsSameSourcePath(string left, string right)
    {
        var normalizedLeft = NormalizeSourcePath(left);
        var normalizedRight = NormalizeSourcePath(right);
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Path.GetFileName(normalizedLeft),
            Path.GetFileName(normalizedRight),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSourcePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/').Trim();
        }
    }

    private static bool AddCallStyleDiagnostics(
        CallExpr call,
        string sourceText,
        List<Diagnostic.Diagnostic> diagnostics,
        SymbolTable? symbolTable,
        Func<SourceSpan, string, int?, bool>? validateReplacement)
    {
        if (!TryFlattenCall(call, out var rootFunction, out var argumentGroups) ||
            argumentGroups.Count == 0 ||
            argumentGroups.Any(group => group.Count == 0) ||
            !TryGetCallableName(rootFunction, out var methodName) ||
            !IsLowerIdentifier(methodName))
        {
            return false;
        }

        var allArguments = argumentGroups.SelectMany(static group => group).ToList();
        if (allArguments.Count == 0 ||
            !TrySliceSingleLine(call.Span, sourceText, out _))
        {
            return false;
        }

        if (argumentGroups.Count == 1)
        {
            return false;
        }

        var chainedReplacement = "";
        var chainedReplacementUsesNestedReceiver = false;
        var hasChainedReplacement = CanOfferCallableStyleReplacement(rootFunction, symbolTable) &&
            TryBuildChainedReplacement(
                methodName,
                allArguments,
                sourceText,
                symbolTable,
                out chainedReplacement,
                out chainedReplacementUsesNestedReceiver);
        var originalSymbolId = TryGetOriginalSymbolId(rootFunction);
        var canOfferChainedReplacement = hasChainedReplacement &&
            IsReplacementAccepted(call.Span, chainedReplacement, originalSymbolId, validateReplacement);
        var infixReplacement = "";
        var hasInfixReplacement = CanOfferCallableStyleReplacement(rootFunction, symbolTable) &&
            TryBuildInfixReplacement(
                rootFunction,
                methodName,
                argumentGroups,
                sourceText,
                out infixReplacement);
        var canOfferInfixReplacement = hasInfixReplacement &&
            IsReplacementAccepted(call.Span, infixReplacement, originalSymbolId, validateReplacement);

        if (argumentGroups.Count > 1 &&
            TryBuildTupleReplacement(rootFunction, allArguments, sourceText, out var tupleReplacement))
        {
            var canOfferTupleReplacement = IsReplacementAccepted(call.Span, tupleReplacement, originalSymbolId, validateReplacement);
            if (!canOfferChainedReplacement && !canOfferTupleReplacement && !canOfferInfixReplacement)
            {
                return false;
            }

            var message = canOfferChainedReplacement
                ? DiagnosticMessages.StyleSuggestionCurriedPrefixCallsUseFluentOrGroupedCallSyntax
                : DiagnosticMessages.StyleSuggestionCurriedQualifiedCallsUseGroupedCallSyntax;
            var help = canOfferChainedReplacement
                ? DiagnosticMessages.CurriedCallUseBestLocalStyleHelp
                : DiagnosticMessages.GroupedCallPreservesQualifiedFunctionPathHelp;

            var diagnostic = Diagnostic.Diagnostic.Help(
                    message,
                    CurriedCallStyleCode)
                .WithLabel(call.Span, DiagnosticMessages.CurriedPrefixCallCanBeRewrittenLabel)
                .WithHelp(help);

            if (canOfferChainedReplacement)
            {
                diagnostic = diagnostic.WithSuggestion(
                    DiagnosticMessages.RewriteAsSuggestion(chainedReplacement),
                    SuggestionKind.StyleRewrite,
                    call.Span,
                    chainedReplacement,
                    confidence: "medium",
                    requiresCleanTypes: true,
                    originalSymbolId: originalSymbolId);
            }

            if (canOfferTupleReplacement)
            {
                diagnostic = diagnostic.WithSuggestion(
                    DiagnosticMessages.RewriteAsSuggestion(tupleReplacement),
                    SuggestionKind.StyleRewrite,
                    call.Span,
                    tupleReplacement,
                    confidence: "medium",
                    requiresCleanTypes: true,
                    originalSymbolId: originalSymbolId);
            }

            if (canOfferInfixReplacement)
            {
                diagnostic = diagnostic.WithSuggestion(
                    DiagnosticMessages.RewriteAsInfixSuggestion,
                    SuggestionKind.StyleRewrite,
                    call.Span,
                    infixReplacement,
                    confidence: "medium",
                    requiresCleanTypes: true,
                    originalSymbolId: originalSymbolId);
            }

            diagnostics.Add(diagnostic);
            return false;
        }

        if (canOfferInfixReplacement && !chainedReplacementUsesNestedReceiver)
        {
            diagnostics.Add(Diagnostic.Diagnostic.Help(
                    DiagnosticMessages.StyleSuggestionPreferInfixBinaryCalls,
                    InfixCallStyleCode)
                .WithLabel(call.Span, DiagnosticMessages.PrefixCallCanBeRewrittenLabel)
                .WithHelp(DiagnosticMessages.InfixCallRewriteHelp)
                .WithSuggestion(
                    DiagnosticMessages.RewriteAsInfixSuggestion,
                    SuggestionKind.StyleRewrite,
                    call.Span,
                    infixReplacement,
                    confidence: "medium",
                    requiresCleanTypes: true,
                    originalSymbolId: originalSymbolId));
        }

        if (!canOfferChainedReplacement)
        {
            return canOfferInfixReplacement;
        }

        diagnostics.Add(Diagnostic.Diagnostic.Help(
                DiagnosticMessages.StyleSuggestionPreferFluentPrefixCalls,
                PrefixCallStyleCode)
            .WithLabel(call.Span, DiagnosticMessages.PrefixCallCanBeRewrittenLabel)
            .WithHelp(DiagnosticMessages.PrefixCallRewriteHelp)
            .WithSuggestion(
                DiagnosticMessages.RewriteAsSuggestion(chainedReplacement),
                SuggestionKind.StyleRewrite,
                call.Span,
                chainedReplacement,
                confidence: "medium",
                requiresCleanTypes: true,
                originalSymbolId: originalSymbolId));

        return chainedReplacementUsesNestedReceiver;
    }

    private static void AddPatternGuardStyleDiagnostic(
        FuncDef func,
        List<Diagnostic.Diagnostic> diagnostics)
    {
        if (!FunctionReturnsBool(func.Signature) ||
            func.Body.Count != 1 ||
            func.Body[0] is not { Pattern: VarPattern varPattern, Guard: null, Expression: BinaryExpr expression } branch ||
            varPattern.BindingMode != PatternBindingMode.ByValue ||
            string.IsNullOrWhiteSpace(varPattern.Name) ||
            varPattern.Name == "_" ||
            expression.Operator != BinaryOp.Or)
        {
            return;
        }

        var terms = new List<EidosAstNode>();
        CollectOrTerms(expression, terms);
        if (terms.Count < 2 ||
            terms.Any(term => !ReferencesIdentifier(term, varPattern.Name)))
        {
            return;
        }

        diagnostics.Add(Diagnostic.Diagnostic.Help(
                DiagnosticMessages.StyleSuggestionPreferPatternGuardBranches,
                PatternGuardStyleCode)
            .WithLabel(branch.Expression.Span, DiagnosticMessages.PatternGuardBranchConditionChainLabel)
            .WithHelp(DiagnosticMessages.PatternGuardBranchRewriteHelp)
            .WithMetadata("style", "pattern-guard-branches"));
    }

    private static bool FunctionReturnsBool(IReadOnlyList<TypeNode> signature)
    {
        if (signature.Count == 0)
        {
            return false;
        }

        var current = signature[^1];
        while (current is ArrowType arrow)
        {
            current = arrow.ReturnType;
        }

        return current is TypePath { TypeName: "Bool", ModulePath.Count: 0, TypeArgs.Count: 0 };
    }

    private static void CollectOrTerms(EidosAstNode node, List<EidosAstNode> terms)
    {
        if (node is BinaryExpr { Operator: BinaryOp.Or, Left: not null, Right: not null } binary)
        {
            CollectOrTerms(binary.Left, terms);
            CollectOrTerms(binary.Right, terms);
            return;
        }

        terms.Add(node);
    }

    private static bool ReferencesIdentifier(EidosAstNode node, string name)
    {
        if (node is IdentifierExpr identifier &&
            string.Equals(identifier.Name, name, StringComparison.Ordinal))
        {
            return true;
        }

        return EnumerateChildNodes(node).Any(child => ReferencesIdentifier(child, name));
    }

    private static bool IsReplacementAccepted(
        SourceSpan span,
        string replacement,
        int? originalSymbolId,
        Func<SourceSpan, string, int?, bool>? validateReplacement)
    {
        return validateReplacement == null || validateReplacement(span, replacement, originalSymbolId);
    }

    private static int? TryGetOriginalSymbolId(EidosAstNode function)
    {
        return function switch
        {
            IdentifierExpr { SymbolId.IsValid: true } identifier => identifier.SymbolId.Value,
            PathExpr { SymbolId.IsValid: true } path => path.SymbolId.Value,
            MethodCallExpr { SymbolId.IsValid: true } method => method.SymbolId.Value,
            CallExpr call when call.Function != null => TryGetOriginalSymbolId(call.Function),
            _ => null
        };
    }

    private static bool TryFlattenCall(
        CallExpr call,
        out EidosAstNode rootFunction,
        out List<List<EidosAstNode>> argumentGroups)
    {
        argumentGroups = [];
        var current = call;
        while (true)
        {
            if (current.NamedArgs.Count > 0 || current.Function == null)
            {
                rootFunction = current.Function ?? current;
                argumentGroups.Clear();
                return false;
            }

            argumentGroups.Insert(0, current.PositionalArgs);
            if (current.Function is not CallExpr inner)
            {
                rootFunction = current.Function;
                return true;
            }

            current = inner;
        }
    }

    private static bool TryGetCallableName(EidosAstNode function, out string name)
    {
        switch (function)
        {
            case PathExpr { TypeArgs.Count: 0 } path:
                name = path.Name;
                return !string.IsNullOrWhiteSpace(name);
            case IdentifierExpr identifier:
                name = identifier.Name;
                return !string.IsNullOrWhiteSpace(name);
            default:
                name = "";
                return false;
        }
    }

    private static bool CanBuildChainedReplacement(EidosAstNode function)
    {
        return function is IdentifierExpr or PathExpr { TypeArgs.Count: 0 };
    }

    private static bool CanOfferCallableStyleReplacement(EidosAstNode function, SymbolTable? symbolTable)
    {
        if (!CanBuildChainedReplacement(function))
        {
            return false;
        }

        return symbolTable == null || IsResolvedModuleLevelFunction(function, symbolTable);
    }

    private static bool IsResolvedModuleLevelFunction(EidosAstNode function, SymbolTable symbolTable)
    {
        var symbolId = TryGetOriginalSymbolId(function);
        if (!symbolId.HasValue)
        {
            return false;
        }

        return symbolTable.GetSymbol(new SymbolId(symbolId.Value)) is FuncSymbol { IsModuleLevel: true };
    }

    private static bool TryBuildChainedReplacement(
        string methodName,
        IReadOnlyList<EidosAstNode> arguments,
        string sourceText,
        SymbolTable? symbolTable,
        out string replacement,
        out bool usedNestedReceiver)
    {
        replacement = "";
        usedNestedReceiver = false;
        if (!TryBuildReceiverReplacement(arguments[0], sourceText, symbolTable, out var receiverText, out usedNestedReceiver))
        {
            return false;
        }

        var methodArguments = new List<string>(Math.Max(0, arguments.Count - 1));
        for (var i = 1; i < arguments.Count; i++)
        {
            if (!TrySliceSingleLine(arguments[i].Span, sourceText, out var argumentText))
            {
                return false;
            }

            methodArguments.Add(argumentText);
        }

        replacement = $"{receiverText}.{methodName}({string.Join(", ", methodArguments)})";
        return true;
    }

    private static bool TryBuildReceiverReplacement(
        EidosAstNode receiver,
        string sourceText,
        SymbolTable? symbolTable,
        out string replacement,
        out bool usedNestedReceiver)
    {
        replacement = "";
        usedNestedReceiver = false;
        if (receiver is CallExpr nestedCall &&
            TryBuildNestedUnaryChainReplacement(nestedCall, sourceText, symbolTable, out var nestedReplacement))
        {
            replacement = nestedReplacement;
            usedNestedReceiver = true;
            return true;
        }

        if (!TrySliceSingleLine(receiver.Span, sourceText, out var receiverText))
        {
            return false;
        }

        replacement = FormatReceiver(receiver, receiverText);
        return true;
    }

    private static bool TryBuildNestedUnaryChainReplacement(
        CallExpr call,
        string sourceText,
        SymbolTable? symbolTable,
        out string replacement)
    {
        replacement = "";
        if (!TryFlattenUnaryPrefixChain(call, symbolTable, out var receiver, out var methodNames) ||
            !TrySliceSingleLine(call.Span, sourceText, out _) ||
            !TrySliceSingleLine(receiver.Span, sourceText, out var receiverText))
        {
            return false;
        }

        var builder = new System.Text.StringBuilder(FormatReceiver(receiver, receiverText));
        foreach (var methodName in methodNames)
        {
            builder.Append('.');
            builder.Append(methodName);
            builder.Append("()");
        }

        replacement = builder.ToString();
        return true;
    }

    private static bool TryFlattenUnaryPrefixChain(
        CallExpr call,
        SymbolTable? symbolTable,
        out EidosAstNode receiver,
        out List<string> methodNames)
    {
        methodNames = [];
        var current = call;
        while (true)
        {
            if (current.NamedArgs.Count > 0 ||
                current.PositionalArgs.Count != 1 ||
                current.Function == null ||
                !TryGetCallableName(current.Function, out var methodName) ||
                !IsLowerIdentifier(methodName) ||
                !CanOfferCallableStyleReplacement(current.Function, symbolTable))
            {
                receiver = current;
                methodNames.Clear();
                return false;
            }

            methodNames.Insert(0, methodName);
            var argument = current.PositionalArgs[0];
            if (argument is not CallExpr nestedCall)
            {
                receiver = argument;
                return true;
            }

            current = nestedCall;
        }
    }

    private static string FormatReceiver(EidosAstNode receiver, string receiverText)
    {
        return receiver switch
        {
            IdentifierExpr or PathExpr or LiteralExpr or CtorExpr or ListExpr or TupleExpr or CallExpr or MethodCallExpr or IndexExpr or RecordUpdateExpr
                => receiverText,
            _ => $"({receiverText})"
        };
    }

    private static bool TryBuildTupleReplacement(
        EidosAstNode function,
        IReadOnlyList<EidosAstNode> arguments,
        string sourceText,
        out string replacement)
    {
        replacement = "";
        if (!TrySliceSingleLine(function.Span, sourceText, out var functionText))
        {
            return false;
        }

        var argumentTexts = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            if (!TrySliceSingleLine(argument.Span, sourceText, out var argumentText))
            {
                return false;
            }

            argumentTexts.Add(argumentText);
        }

        replacement = $"{functionText}({string.Join(", ", argumentTexts)})";
        return true;
    }

    private static bool TryBuildInfixReplacement(
        EidosAstNode function,
        string methodName,
        IReadOnlyList<List<EidosAstNode>> argumentGroups,
        string sourceText,
        out string replacement)
    {
        replacement = "";
        if (function is not IdentifierExpr ||
            argumentGroups.Count == 0 ||
            argumentGroups.Sum(static group => group.Count) != 2)
        {
            return false;
        }

        var arguments = argumentGroups.SelectMany(static group => group).ToArray();
        if (!TrySliceSingleLine(arguments[0].Span, sourceText, out var leftText) ||
            !TrySliceSingleLine(arguments[1].Span, sourceText, out var rightText))
        {
            return false;
        }

        replacement = $"{FormatInfixOperand(arguments[0], leftText)} `{methodName}` {FormatInfixOperand(arguments[1], rightText)}";
        return true;
    }

    private static string FormatInfixOperand(EidosAstNode operand, string operandText)
    {
        return operand switch
        {
            IdentifierExpr or PathExpr or LiteralExpr or CtorExpr or ListExpr or TupleExpr or CallExpr or MethodCallExpr or IndexExpr
                => operandText,
            _ => $"({operandText})"
        };
    }

    private static bool TrySliceSingleLine(Eidosc.Utils.SourceSpan span, string sourceText, out string text)
    {
        text = "";
        var start = span.Location.Position;
        var length = span.Length;
        if (start < 0 || length <= 0 || start + length > sourceText.Length)
        {
            return false;
        }

        text = sourceText.Substring(start, length).Trim();
        return text.Length > 0 && !text.Contains('\n') && !text.Contains('\r');
    }

    private static bool IsLowerIdentifier(string value)
    {
        return value.Length > 0 && (char.IsLower(value[0]) || value[0] == '_');
    }

    private static IEnumerable<EidosAstNode> EnumerateChildNodes(EidosAstNode node)
    {
        foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var value = property.GetValue(node);
            switch (value)
            {
                case EidosAstNode child:
                    yield return child;
                    break;
                case IEnumerable enumerable and not string:
                    foreach (var item in enumerable)
                    {
                        if (item is EidosAstNode childItem)
                        {
                            yield return childItem;
                        }
                    }
                    break;
            }
        }
    }
}
