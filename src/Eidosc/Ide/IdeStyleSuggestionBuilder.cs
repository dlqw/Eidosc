using System.Collections;
using System.Reflection;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Ide;

internal static class IdeStyleSuggestionBuilder
{
    private const string PrefixCallStyleCode = "S1001";
    private const string CurriedCallStyleCode = "S1002";
    private const string InfixCallStyleCode = "S1003";
    private const string PatternGuardStyleCode = "S1004";
    private const string SelectionToMatchMigrationCode = "S1005";
    private const string MatchToSelectionMigrationCode = "S1006";

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
        diagnostics.AddRange(NamingStyleDiagnosticBuilder.Build(module, sourceText, sourceFilePath, symbolTable));
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

        if (node is SelectionExpr selection &&
            IsInRequestedSource(selection.Span, sourceText, sourceFilePath))
        {
            AddSelectionToMatchMigration(
                selection,
                sourceText,
                diagnostics,
                symbolTable,
                validateReplacement);
        }

        if (node is MatchExpr match &&
            IsInRequestedSource(match.Span, sourceText, sourceFilePath))
        {
            AddMatchToSelectionMigration(
                match,
                sourceText,
                diagnostics,
                symbolTable,
                validateReplacement);
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
            !TryGetCallableName(rootFunction, out var methodName))
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

    private static void AddSelectionToMatchMigration(
        SelectionExpr selection,
        string sourceText,
        List<Diagnostic.Diagnostic> diagnostics,
        SymbolTable? symbolTable,
        Func<SourceSpan, string, int?, bool>? validateReplacement)
    {
        if (selection.IsGroup ||
            selection.Subject == null ||
            selection.Subjects.Count != 1 ||
            selection.Subjects[0] is not { Kind: not SelectionSubjectKind.Unknown } subject ||
            !TrySlice(selection.Subject.Span, sourceText, out var subjectText) ||
            !TryRenderSelectionArm(
                selection.ThenArm,
                selection.ThenPlaceholderSymbols,
                "selected_value",
                sourceText,
                symbolTable,
                out var thenArm,
                out var positiveBindings) ||
            !TryRenderSelectionArm(
                selection.ElseArm,
                selection.ElsePlaceholderSymbols,
                "selected_error",
                sourceText,
                symbolTable,
                out var elseArm,
                out var negativeBindings) ||
            !TryBuildSelectionPatterns(subject, symbolTable, positiveBindings, negativeBindings, out var positivePattern, out var negativePattern))
        {
            return;
        }

        var replacement = $"match {subjectText} {{\n    {positivePattern} => {thenArm},\n    {negativePattern} => {elseArm}\n}}";
        if (!IsReplacementAccepted(selection.Span, replacement, null, validateReplacement))
        {
            return;
        }

        diagnostics.Add(Diagnostic.Diagnostic.Help(
                DiagnosticMessages.SelectionCanBeExpandedToMatch,
                SelectionToMatchMigrationCode)
            .WithLabel(selection.Span, DiagnosticMessages.SelectionToMatchLabel)
            .WithHelp(DiagnosticMessages.SelectionMigrationPreservesBranchSemantics)
            .WithSuggestion(
                DiagnosticMessages.RewriteAsExplicitMatchSuggestion,
                SuggestionKind.StyleRewrite,
                selection.Span,
                replacement,
                confidence: "high",
                requiresCleanTypes: true)
            .WithMetadata("migration", "selection-to-match"));
    }

    private static void AddMatchToSelectionMigration(
        MatchExpr match,
        string sourceText,
        List<Diagnostic.Diagnostic> diagnostics,
        SymbolTable? symbolTable,
        Func<SourceSpan, string, int?, bool>? validateReplacement)
    {
        if (symbolTable == null ||
            match.MatchedExpression == null ||
            match.Branches.Count != 2 ||
            match.Branches.Any(static branch => branch.Guard != null || branch.Expression == null) ||
            !TrySlice(match.MatchedExpression.Span, sourceText, out var subjectText) ||
            !TryClassifyBinaryMatch(match, symbolTable, out var positiveBranch, out var positiveBinding, out var negativeBranch, out var negativeBinding) ||
            !TryRenderMatchArm(positiveBranch.Expression!, positiveBinding, sourceText, out var thenArm) ||
            !TryRenderMatchArm(negativeBranch.Expression!, negativeBinding, sourceText, out var elseArm))
        {
            return;
        }

        var replacement = $"{subjectText}\n    then {thenArm}\n    else {elseArm}";
        if (!IsReplacementAccepted(match.Span, replacement, null, validateReplacement))
        {
            return;
        }

        diagnostics.Add(Diagnostic.Diagnostic.Help(
                DiagnosticMessages.MatchCanBeCollapsedToSelection,
                MatchToSelectionMigrationCode)
            .WithLabel(match.Span, DiagnosticMessages.MatchToSelectionLabel)
            .WithHelp(DiagnosticMessages.SelectionMigrationPreservesBranchSemantics)
            .WithSuggestion(
                DiagnosticMessages.RewriteAsSelectionSuggestion,
                SuggestionKind.StyleRewrite,
                match.Span,
                replacement,
                confidence: "high",
                requiresCleanTypes: true)
            .WithMetadata("migration", "match-to-selection"));
    }

    private static bool TryBuildSelectionPatterns(
        SelectionSubjectDesugaring subject,
        SymbolTable? symbolTable,
        IReadOnlyDictionary<int, string> positiveBindings,
        IReadOnlyDictionary<int, string> negativeBindings,
        out string positivePattern,
        out string negativePattern)
    {
        positivePattern = "";
        negativePattern = "";
        if (subject.Kind == SelectionSubjectKind.Bool)
        {
            positivePattern = "true";
            negativePattern = "false";
            return positiveBindings.Count == 0 && negativeBindings.Count == 0;
        }

        if (symbolTable?.GetSymbol(subject.PositiveConstructorSymbolId) is not CtorSymbol positiveConstructor ||
            symbolTable.GetSymbol(subject.NegativeConstructorSymbolId) is not CtorSymbol negativeConstructor)
        {
            return false;
        }

        positivePattern = FormatConstructorPattern(
            positiveConstructor.Name,
            subject.PositivePayloadTypes.Count,
            positiveBindings);
        negativePattern = FormatConstructorPattern(
            negativeConstructor.Name,
            subject.NegativePayloadTypes.Count,
            negativeBindings);
        return true;
    }

    private static string FormatConstructorPattern(
        string constructorName,
        int payloadCount,
        IReadOnlyDictionary<int, string> bindings) =>
        $"{constructorName}({string.Join(", ", Enumerable.Range(0, payloadCount).Select(index => bindings.GetValueOrDefault(index, "_")))})";

    private static bool TryRenderSelectionArm(
        EidosAstNode? arm,
        IReadOnlyDictionary<int, SymbolId> placeholderSymbols,
        string bindingPrefix,
        string sourceText,
        SymbolTable? symbolTable,
        out string rendered,
        out IReadOnlyDictionary<int, string> bindings)
    {
        if (arm == null)
        {
            rendered = "()";
            bindings = new Dictionary<int, string>();
            return true;
        }

        var names = new Dictionary<SymbolId, string>();
        var orderedBindings = new Dictionary<int, string>();
        foreach (var entry in placeholderSymbols.OrderBy(static entry => entry.Key))
        {
            var name = CreateUniqueBindingName($"{bindingPrefix}_{entry.Key}", sourceText, symbolTable, orderedBindings.Values);
            names[entry.Value] = name;
            orderedBindings[entry.Key] = name;
        }

        bindings = orderedBindings;
        return TryRenderNodeWithSymbolRenames(arm, sourceText, names, out rendered);
    }

    private static bool TryRenderMatchArm(
        EidosAstNode arm,
        VarPattern? binding,
        string sourceText,
        out string rendered)
    {
        var renames = binding is { SymbolId.IsValid: true }
            ? new Dictionary<SymbolId, string> { [binding.SymbolId] = "_0" }
            : [];
        return TryRenderNodeWithSymbolRenames(arm, sourceText, renames, out rendered);
    }

    private static string CreateUniqueBindingName(
        string seed,
        string sourceText,
        SymbolTable? symbolTable,
        IReadOnlyCollection<string> reserved)
    {
        var candidate = seed;
        while (reserved.Contains(candidate, StringComparer.Ordinal) ||
               symbolTable?.Symbols.Values.Any(symbol => string.Equals(symbol.Name, candidate, StringComparison.Ordinal)) == true ||
               ContainsIdentifier(sourceText, candidate))
        {
            candidate += "_";
        }

        return candidate;
    }

    private static bool ContainsIdentifier(string sourceText, string candidate)
    {
        var index = 0;
        while ((index = sourceText.IndexOf(candidate, index, StringComparison.Ordinal)) >= 0)
        {
            var beforeIsIdentifier = index > 0 && IsIdentifierCharacter(sourceText[index - 1]);
            var end = index + candidate.Length;
            var afterIsIdentifier = end < sourceText.Length && IsIdentifierCharacter(sourceText[end]);
            if (!beforeIsIdentifier && !afterIsIdentifier)
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool TryClassifyBinaryMatch(
        MatchExpr match,
        SymbolTable symbolTable,
        out PatternBranch positiveBranch,
        out VarPattern? positiveBinding,
        out PatternBranch negativeBranch,
        out VarPattern? negativeBinding)
    {
        positiveBranch = null!;
        negativeBranch = null!;
        positiveBinding = null;
        negativeBinding = null;

        if (match.MatchedExpression?.InferredType is TyCon { Name: "Bool" })
        {
            foreach (var branch in match.Branches)
            {
                if (branch.Pattern is not LiteralPattern { Type: LiteralType.Boolean, Value: bool value })
                {
                    return false;
                }

                if (value)
                {
                    positiveBranch = branch;
                }
                else
                {
                    negativeBranch = branch;
                }
            }

            return positiveBranch != null && negativeBranch != null;
        }

        if (match.MatchedExpression?.InferredType is not TyCon matchedType ||
            !TryGetCanonicalSelectionKind(matchedType, symbolTable, out var matchedKind) ||
            match.Branches[0].Pattern is not CtorPattern first ||
            match.Branches[1].Pattern is not CtorPattern second ||
            !TryGetCanonicalSelectionPattern(first, matchedKind, out var firstPositive, out var firstBinding) ||
            !TryGetCanonicalSelectionPattern(second, matchedKind, out var secondPositive, out var secondBinding) ||
            firstPositive == secondPositive)
        {
            return false;
        }

        if (firstPositive)
        {
            positiveBranch = match.Branches[0];
            positiveBinding = firstBinding;
            negativeBranch = match.Branches[1];
            negativeBinding = secondBinding;
        }
        else
        {
            positiveBranch = match.Branches[1];
            positiveBinding = secondBinding;
            negativeBranch = match.Branches[0];
            negativeBinding = firstBinding;
        }

        return true;
    }

    private static bool TryGetCanonicalSelectionKind(
        TyCon matchedType,
        SymbolTable symbolTable,
        out SelectionSubjectKind kind)
    {
        kind = matchedType.Name switch
        {
            "Option" when matchedType.Args.Count == 1 => SelectionSubjectKind.Option,
            "Result" when matchedType.Args.Count == 2 => SelectionSubjectKind.Result,
            "Either" when matchedType.Args.Count == 2 => SelectionSubjectKind.Either,
            _ => SelectionSubjectKind.Unknown
        };
        if (kind == SelectionSubjectKind.Unknown ||
            !matchedType.Symbol.IsValid ||
            symbolTable.GetSymbol<AdtSymbol>(matchedType.Symbol) is not { } owner ||
            !string.Equals(owner.Name, matchedType.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (PrecompiledModuleRegistry.TryGetModulePathFromSourcePath(owner.Span.FilePath, out var precompiledPath) &&
            string.Equals(precompiledPath, owner.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return symbolTable.Modules.TryGetOwningModule(owner.Id, out var module) &&
               string.Equals(module.PackageAlias, WellKnownStrings.Std.Module, StringComparison.OrdinalIgnoreCase) &&
               module.Path.Count > 0 &&
               string.Equals(module.Path[^1], owner.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetCanonicalSelectionPattern(
        CtorPattern pattern,
        SelectionSubjectKind kind,
        out bool positive,
        out VarPattern? binding)
    {
        positive = false;
        binding = null;
        if (pattern.NamedPatterns.Count != 0 ||
            pattern.HasRecordRest)
        {
            return false;
        }

        var classifiedKind = (kind, pattern.ConstructorName) switch
        {
            (SelectionSubjectKind.Option, "Some") => (SelectionSubjectKind.Option, true),
            (SelectionSubjectKind.Option, "None") => (SelectionSubjectKind.Option, false),
            (SelectionSubjectKind.Result, "Ok") => (SelectionSubjectKind.Result, true),
            (SelectionSubjectKind.Result, "Err") => (SelectionSubjectKind.Result, false),
            (SelectionSubjectKind.Either, "Right") => (SelectionSubjectKind.Either, true),
            (SelectionSubjectKind.Either, "Left") => (SelectionSubjectKind.Either, false),
            _ => (SelectionSubjectKind.Unknown, false)
        };
        if (classifiedKind.Item1 == SelectionSubjectKind.Unknown)
        {
            return false;
        }

        positive = classifiedKind.Item2;

        var expectedPayloadCount = kind == SelectionSubjectKind.Option && !positive ? 0 : 1;
        if (pattern.PositionalPatterns.Count != expectedPayloadCount)
        {
            return false;
        }

        if (expectedPayloadCount == 0)
        {
            return true;
        }

        binding = pattern.PositionalPatterns[0] switch
        {
            VarPattern { BindingMode: PatternBindingMode.ByValue, IsMutableBinding: false } variable => variable,
            WildcardPattern => null,
            _ => null
        };
        return binding != null || pattern.PositionalPatterns[0] is WildcardPattern;
    }

    private static bool TryRenderNodeWithSymbolRenames(
        EidosAstNode node,
        string sourceText,
        IReadOnlyDictionary<SymbolId, string> renames,
        out string rendered)
    {
        if (!TrySlice(node.Span, sourceText, out rendered, trim: false))
        {
            return false;
        }

        var start = node.Span.Location.Position;
        var sourceSliceLength = rendered.Length;
        var edits = new List<(int Start, int Length, string Replacement)>();
        var visited = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        Collect(node);
        foreach (var edit in edits.OrderByDescending(static edit => edit.Start))
        {
            rendered = rendered.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        }

        rendered = rendered.Trim();
        return rendered.Length > 0;

        void Collect(EidosAstNode current)
        {
            if (!visited.Add(current))
            {
                return;
            }

            if (current is IdentifierExpr { SymbolId.IsValid: true } identifier &&
                renames.TryGetValue(identifier.SymbolId, out var replacement))
            {
                var relativeStart = identifier.Span.Location.Position - start;
                if (relativeStart >= 0 && relativeStart + identifier.Span.Length <= sourceSliceLength)
                {
                    edits.Add((relativeStart, identifier.Span.Length, replacement));
                }
            }

            foreach (var child in EnumerateChildNodes(current))
            {
                Collect(child);
            }
        }
    }

    private static bool TrySlice(SourceSpan span, string sourceText, out string text, bool trim = true)
    {
        text = "";
        var start = span.Location.Position;
        var length = span.Length;
        if (start < 0 || length <= 0 || start + length > sourceText.Length)
        {
            return false;
        }

        text = sourceText.Substring(start, length);
        if (trim)
        {
            text = text.Trim();
        }

        return text.Length > 0;
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
