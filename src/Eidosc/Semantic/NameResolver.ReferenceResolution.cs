using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void ResolveFuncDeclReferences(FuncDecl func)
    {
        ResolveFunctionSignatureReferences(func.SymbolId, func.TypeParams, func.Signature, func.RequiredAbilities);
    }


    private void ResolveProofPropositionReferences(ProofPropositionClause? proposition)
    {
        if (proposition == null)
        {
            return;
        }

        if (proposition.Kind == ProofPropositionKind.Exists)
        {
            ResolveProofQuantifiedPropositionReferences(proposition.ExistsParameter, proposition.ExistsBody);
            return;
        }

        if (proposition.Kind == ProofPropositionKind.Forall)
        {
            ResolveProofQuantifiedPropositionReferences(proposition.ForallParameter, proposition.ForallBody);
            return;
        }

        ResolveOptionalExpression(proposition.LeftExpression);
        ResolveOptionalExpression(proposition.RightExpression);
        ResolveProofPropositionReferences(proposition.Premise);
        ResolveProofPropositionReferences(proposition.Conclusion);
    }

    private void ResolveProofQuantifiedPropositionReferences(
        ProofParameter? parameter,
        ProofPropositionClause? body)
    {
        if (parameter == null)
        {
            ResolveProofPropositionReferences(body);
            return;
        }

        using var scopeGuard = _symbolTable.PushScopeGuard(ScopeKind.Function);
        var typeAnnotation = parameter.TypeAnnotation ?? TryRecoverProofParameterTypeAnnotation(parameter);
        if (typeAnnotation == null)
        {
            AddError(parameter.Span, DiagnosticMessages.ProofParameterRequiresTypeAnnotation(parameter.Name));
            ResolveProofPropositionReferences(body);
            return;
        }

        ResolveTypeReferences(typeAnnotation);
        if (TryReportReservedInternalNameDeclaration(parameter.Name, parameter.Span, "parameter"))
        {
            parameter.SymbolId = SymbolId.None;
            ResolveProofPropositionReferences(body);
            return;
        }

        parameter.SymbolId = _symbolTable.DeclareVariable(
            parameter.Name,
            parameter.Span,
            isParameter: true);
        ResolveProofPropositionReferences(body);
    }

    private void ResolveProofTermReferences(
        ProofTermClause? term,
        ProofPropositionClause? goal,
        IReadOnlyList<ProofParameter>? topLevelParameters = null)
    {
        if (term == null)
        {
            return;
        }

        ResolveProofTermExpressionReferences(term);

        switch (term.Kind)
        {
            case ProofTermKind.Intro:
                ResolveProofIntroTermReferences(term, goal, topLevelParameters);
                return;

            case ProofTermKind.Constructor:
                ResolveProofTermReferences(term.LeftProof, goal?.Kind == ProofPropositionKind.And ? goal.Premise : null);
                ResolveProofTermReferences(term.RightProof, goal?.Kind == ProofPropositionKind.And ? goal.Conclusion : null);
                return;

            case ProofTermKind.Left:
                ResolveProofTermReferences(term.Inner, goal?.Kind == ProofPropositionKind.Or ? goal.Premise : null);
                return;

            case ProofTermKind.Right:
                ResolveProofTermReferences(term.Inner, goal?.Kind == ProofPropositionKind.Or ? goal.Conclusion : null);
                return;

            case ProofTermKind.OrCases:
                ResolveProofTermReferences(term.LeftProof, goal);
                ResolveProofTermReferences(term.RightProof, goal);
                return;

            case ProofTermKind.Exists:
                ResolveProofTermReferences(term.Inner, goal?.Kind == ProofPropositionKind.Exists ? goal.ExistsBody : null);
                return;

            case ProofTermKind.ExistsLet:
                ResolveProofExistsLetTermReferences(term, goal);
                return;

            case ProofTermKind.Let:
                ResolveProofLetTermReferences(term, goal);
                return;

            case ProofTermKind.Have:
                ResolveProofPropositionReferences(term.HaveSourceProposition);
                ResolveProofTermReferences(term.HaveProof, term.HaveSourceProposition);
                ResolveProofTermReferences(term.HaveBody, goal);
                return;
        }

        foreach (var argumentProof in term.ArgumentProofs)
        {
            ResolveProofTermReferences(argumentProof, null);
        }

        ResolveProofTermReferences(term.Inner, goal);
        ResolveProofTermReferences(term.Then, goal);
        ResolveProofTermReferences(term.LeftProof, null);
        ResolveProofTermReferences(term.RightProof, null);
    }

    private void ResolveProofTermExpressionReferences(ProofTermClause term)
    {
        foreach (var typeArgument in term.TypeArguments)
        {
            ResolveTypeReferences(typeArgument);
        }

        foreach (var argument in term.ValueArguments)
        {
            ResolveOptionalExpression(argument);
        }

        ResolveOptionalExpression(term.MiddleExpression);
        ResolveOptionalExpression(term.LetValueExpression);
        if (term.HaveSourceProposition == null)
        {
            ResolveOptionalExpression(term.HaveLeft);
            ResolveOptionalExpression(term.HaveRight);
        }
        ResolveOptionalExpression(term.WitnessExpression);
        ResolveOptionalExpression(term.CalcStart);
        foreach (var step in term.CalcSteps)
        {
            ResolveOptionalExpression(step.Target);
            foreach (var typeArgument in step.TypeArguments)
            {
                ResolveTypeReferences(typeArgument);
            }

            foreach (var argument in step.ValueArguments)
            {
                ResolveOptionalExpression(argument);
            }
        }
    }

    private void ResolveProofIntroTermReferences(
        ProofTermClause term,
        ProofPropositionClause? goal,
        IReadOnlyList<ProofParameter>? topLevelParameters)
    {
        if (goal?.Kind != ProofPropositionKind.Forall)
        {
            if (goal?.Kind != ProofPropositionKind.Implies &&
                topLevelParameters is { Count: > 0 })
            {
                using var topLevelScopeGuard = _symbolTable.PushScopeGuard(ScopeKind.Block);
                term.SetIntroSymbolId(BindTopLevelProofIntroAlias(term.IntroName, topLevelParameters[0]));
                ResolveProofTermReferences(term.IntroBody, goal, topLevelParameters.Skip(1).ToList());
                return;
            }

            ResolveProofTermReferences(
                term.IntroBody,
                goal?.Kind == ProofPropositionKind.Implies ? goal.Conclusion : null);
            return;
        }

        using var scopeGuard = _symbolTable.PushScopeGuard(ScopeKind.Block);
        term.SetIntroSymbolId(BindProofForallIntroVariable(term.IntroName, goal.ForallParameter, term.Span));
        ResolveProofTermReferences(term.IntroBody, goal.ForallBody);
    }

    private SymbolId BindProofForallIntroVariable(
        string introName,
        ProofParameter? parameter,
        SourceSpan span)
    {
        if (string.IsNullOrWhiteSpace(introName))
        {
            return SymbolId.None;
        }

        if (parameter != null &&
            parameter.SymbolId.IsValid &&
            string.Equals(parameter.Name, introName, StringComparison.Ordinal))
        {
            _symbolTable.CurrentScope?.BindValue(introName, parameter.SymbolId);
            return parameter.SymbolId;
        }

        return DeclareProofExpressionVariable(introName, span);
    }

    private SymbolId BindTopLevelProofIntroAlias(
        string introName,
        ProofParameter parameter)
    {
        if (string.IsNullOrWhiteSpace(introName) || !parameter.SymbolId.IsValid)
        {
            return SymbolId.None;
        }

        _symbolTable.CurrentScope?.BindValue(introName, parameter.SymbolId);
        return parameter.SymbolId;
    }

    private void ResolveProofExistsLetTermReferences(ProofTermClause term, ProofPropositionClause? goal)
    {
        using var scopeGuard = _symbolTable.PushScopeGuard(ScopeKind.Block);
        DeclareProofExpressionVariable(term.ExistsElimWitnessName, term.Span);
        ResolveProofTermReferences(term.ExistsElimBody, goal);
    }

    private void ResolveProofLetTermReferences(ProofTermClause term, ProofPropositionClause? goal)
    {
        using var scopeGuard = _symbolTable.PushScopeGuard(ScopeKind.Block);
        DeclareProofExpressionVariable(term.LetName, term.Span);
        ResolveProofTermReferences(term.LetBody, goal);
    }

    private SymbolId DeclareProofExpressionVariable(string name, SourceSpan span)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SymbolId.None;
        }

        if (TryReportReservedInternalNameDeclaration(name, span, "proof binding"))
        {
            return SymbolId.None;
        }

        return _symbolTable.DeclareVariable(
            name,
            span,
            isParameter: true);
    }

    private void ResolveProofCaseReferences(
        ProofCase proofCase,
        int caseIndex,
        ProofPropositionClause? goal)
    {
        using var scopeGuard = _symbolTable.PushScopeGuard(ScopeKind.PatternBranch);

        if (proofCase.Pattern == null)
        {
            AddError(proofCase.Span, DiagnosticMessages.ProofCaseIndexRequiresPattern(caseIndex + 1));
            return;
        }

        using var context = PushPatternDiagnosticContext($"proof-case#{caseIndex + 1}");
        ResolvePatternBindings(proofCase.Pattern);
        ResolveProofTermReferences(proofCase.BodyTerm, goal);
    }

    private static TypeNode? TryRecoverProofParameterTypeAnnotation(ProofParameter parameter)
    {
        var filePath = parameter.Span.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var source = File.ReadAllText(filePath);
        if (parameter.Span.Position < 0 || parameter.Span.Position >= source.Length)
        {
            return null;
        }

        var length = Math.Min(parameter.Span.Length, source.Length - parameter.Span.Position);
        if (length <= 0)
        {
            return null;
        }

        var segment = source.Substring(parameter.Span.Position, length);
        var colonIndex = segment.IndexOf(':');
        if (colonIndex < 0 || colonIndex + 1 >= segment.Length)
        {
            return null;
        }

        var typeText = segment[(colonIndex + 1)..].Trim();
        var typeName = new string(typeText
            .TakeWhile(static ch => char.IsLetterOrDigit(ch) || ch == '_')
            .ToArray());
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var typePath = new TypePath();
        typePath.SetTypeName(typeName);
        typePath.SetSpan(parameter.Span);
        parameter.SetTypeAnnotation(typePath);
        return typePath;
    }

    private void ResolveLetDeclReferences(LetDecl letDecl)
    {
        ResolveOptionalTypeReference(letDecl.TypeAnnotation);
        ResolveOptionalExpression(letDecl.Value);

        if (letDecl.IsComptime && letDecl.IsMutable)
        {
            AddError(letDecl.Span, "comptime mutable bindings are not supported in 0.6.0-alpha.1 phase 1.");
        }

        if (letDecl.Pattern == null)
        {
            AddError(letDecl.Span, DiagnosticMessages.LetDeclarationRequiresBindingPattern);
            return;
        }

        using var context = PushPatternDiagnosticContext("let-pattern");
        if (IsRejectedPredeclaredLetPattern(letDecl))
        {
            ResolvePatternReferencesWithoutBinding(letDecl.Pattern);
        }
        else if (letDecl.SymbolId.IsValid)
        {
            BindPredeclaredLetPattern(letDecl.Pattern, letDecl.SymbolId);
        }
        else
        {
            ResolvePatternBindings(letDecl.Pattern, letDecl.IsMutable, letDecl.IsComptime);
            letDecl.SymbolId = GetSingleLetPatternSymbol(letDecl.Pattern);
        }

        // 当前 let 语义为严格绑定：要求不可反驳模式，避免运行时静默错配。
        if (!IsPatternIrrefutable(letDecl.Pattern))
        {
            AddPatternError(
                letDecl.Pattern.Span,
                DiagnosticMessages.LetDeclarationRequiresIrrefutablePattern);
        }
    }

    private bool IsRejectedPredeclaredLetPattern(LetDecl letDecl)
    {
        return _symbolTable.CurrentScope?.Kind == ScopeKind.Module &&
               letDecl.SymbolId == SymbolId.None &&
               letDecl.Pattern is VarPattern { SymbolId: var patternSymbolId } &&
               patternSymbolId == SymbolId.None;
    }

    private void BindPredeclaredLetPattern(Pattern pattern, SymbolId symbolId)
    {
        if (pattern is VarPattern varPattern &&
            !string.IsNullOrWhiteSpace(varPattern.Name) &&
            !string.Equals(varPattern.Name, WellKnownStrings.Punctuation.Underscore, StringComparison.Ordinal))
        {
            varPattern.SymbolId = symbolId;
            return;
        }

        ResolvePatternBindings(pattern);
    }

    private static SymbolId GetSingleLetPatternSymbol(Pattern pattern)
    {
        return pattern switch
        {
            VarPattern varPattern => varPattern.SymbolId,
            AsPattern asPattern => asPattern.SymbolId,
            _ => SymbolId.None
        };
    }

    private void ResolveLetQuestionDeclReferences(LetQuestionDecl letQuestionDecl)
    {
        ResolveOptionalExpression(letQuestionDecl.Value);
        EnsureLetQuestionFailureBindingSymbol(letQuestionDecl);

        if (letQuestionDecl.Pattern == null)
        {
            AddError(letQuestionDecl.Span, DiagnosticMessages.LetQuestionRequiresBindingPattern);
            return;
        }

        using var context = PushPatternDiagnosticContext("let-question-pattern");
        ResolvePatternBindings(letQuestionDecl.Pattern);

        if (!IsPatternIrrefutable(letQuestionDecl.Pattern))
        {
            AddPatternError(
                letQuestionDecl.Pattern.Span,
                DiagnosticMessages.LetQuestionRequiresIrrefutablePattern);
        }
    }

    private void EnsureLetQuestionFailureBindingSymbol(LetQuestionDecl letQuestionDecl)
    {
        if (letQuestionDecl.FailureBindingSymbolId.IsValid)
        {
            return;
        }

        var name = $"{WellKnownStrings.InternalNames.LetQuestionErrorPrefix}{letQuestionDecl.Span.Position}";
        var symbolId = _symbolTable.RegisterSymbol(new VarSymbol
        {
            Name = name,
            Span = letQuestionDecl.Span,
            IsMutable = false,
            IsParameter = false,
            IsPatternBound = true,
            IsPublic = false
        });
        letQuestionDecl.SetFailureBindingSymbol(symbolId);
    }

    private void ResolveAssignmentReferences(Assignment assign)
    {
        ResolveAssignmentTarget(assign);
        ResolveOptionalExpression(assign.Value);
    }

    private void ResolveFunctionSignatureReferences(
        SymbolId functionId,
        IReadOnlyList<TypeParam> typeParams,
        IReadOnlyList<TypeNode> signature,
        IReadOnlyList<EffectRequirementNode>? requiredAbilities = null)
    {
        if (typeParams.Count == 0)
        {
            ResolveTypeReferenceList(signature);
            ResolveEffectRequirements(requiredAbilities ?? []);
            return;
        }

        using var scopeGuard = _symbolTable.PushScopeGuard(ScopeKind.Function);
        foreach (var typeParam in typeParams)
        {
            DeclareTypeParameterIfValid(typeParam);
            ResolveTypeParamReferences(typeParam);
        }
        UpdateFunctionTypeParamSymbols(functionId, typeParams);

        ResolveTypeReferenceList(signature);
        ResolveEffectRequirements(requiredAbilities ?? []);
    }

    private void ResolveValueDeclarationReferences(TypeNode? typeAnnotation, EidosAstNode? value)
    {
        ResolveOptionalTypeReference(typeAnnotation);
        ResolveOptionalExpression(value);
    }

    private void ResolveOptionalTypeReference(TypeNode? typeAnnotation)
    {
        if (typeAnnotation != null)
        {
            ResolveTypeReferences(typeAnnotation);
        }
    }

    private void ResolveOptionalExpression(EidosAstNode? value)
    {
        if (value != null)
        {
            ResolveExpressionReferences(value);
        }
    }

    private void UpdateVariableDeclaredType(SymbolId symbolId, TypeNode? typeAnnotation)
    {
        if (!symbolId.IsValid ||
            typeAnnotation == null ||
            _symbolTable.GetSymbol<VarSymbol>(symbolId) is not { } variableSymbol ||
            !TryResolveDeclaredVariableTypeId(typeAnnotation, out var typeId))
        {
            return;
        }

        variableSymbol.Type = typeId;
    }

    private bool TryResolveDeclaredVariableTypeId(TypeNode typeAnnotation, out TypeId typeId)
    {
        typeId = TypeId.None;
        if (typeAnnotation is not TypePath typePath || typePath.TypeArgs.Count > 0)
        {
            return false;
        }

        var builtInTypeId = BaseTypes.GetBuiltInTypeId(typePath.TypeName);
        if (builtInTypeId.IsValid)
        {
            typeId = builtInTypeId;
            return true;
        }

        var symbolId = typePath.SymbolId;
        if (!symbolId.IsValid && !string.IsNullOrWhiteSpace(typePath.TypeName))
        {
            symbolId = _symbolTable.LookupType(typePath.TypeName) ?? SymbolId.None;
        }

        if (!symbolId.IsValid ||
            _symbolTable.GetSymbol(symbolId) is not { TypeId.IsValid: true } symbol)
        {
            return false;
        }

        typeId = symbol.TypeId;
        return true;
    }

    private void ResolveTypeReferenceList(IReadOnlyList<TypeNode> typeNodes)
    {
        foreach (var typeNode in typeNodes)
        {
            ResolveTypeReferences(typeNode);
        }
    }

    private void ResolveAssignmentTarget(Assignment assign)
    {
        if (assign.TargetExpression != null)
        {
            ResolveExpressionReferences(assign.TargetExpression);
            if (assign.TargetExpression is IdentifierExpr identifier)
            {
                assign.TargetSymbolId = identifier.SymbolId;
                ValidateAssignmentTargetMutability(assign, identifier.Name, identifier.SymbolId);
            }
            else
            {
                ValidatePlaceAssignmentRootMutability(assign, assign.TargetExpression);
            }

            return;
        }

        var targetSymbol = _symbolTable.LookupValue(assign.Target);
        if (targetSymbol == null)
        {
            AddError(assign.Span, DiagnosticMessages.UndefinedVariable(assign.Target));
            return;
        }

        assign.TargetSymbolId = targetSymbol.Value;
        ValidateAssignmentTargetMutability(assign, assign.Target, targetSymbol.Value);
    }

    private void ValidateAssignmentTargetMutability(Assignment assign, string targetName, SymbolId targetSymbol)
    {
        if (!targetSymbol.IsValid)
        {
            return;
        }

        if (_symbolTable.GetSymbol(targetSymbol) is VarSymbol { IsComptime: true })
        {
            AddError(assign.Span, $"Cannot assign to comptime binding '{targetName}'.");
        }
        else if (_symbolTable.GetSymbol(targetSymbol) is VarSymbol { IsMutable: false })
        {
            AddError(assign.Span, DiagnosticMessages.CannotAssignToImmutableVariable(targetName));
        }
    }

    private void ValidatePlaceAssignmentRootMutability(Assignment assign, EidosAstNode target)
    {
        if (!TryGetAssignmentRootIdentifier(target, out var root))
        {
            return;
        }

        if (!root.SymbolId.IsValid)
        {
            return;
        }

        assign.TargetSymbolId = root.SymbolId;
        if (_symbolTable.GetSymbol(root.SymbolId) is not VarSymbol varSymbol)
        {
            return;
        }

        if (varSymbol.IsComptime)
        {
            AddError(assign.Span, $"Cannot assign to comptime binding '{root.Name}'.");
            return;
        }

        if (varSymbol.IsMutable)
        {
            return;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
            varSymbol.IsParameter
                ? DiagnosticMessages.CannotAssignThroughImmutableParameter(root.Name)
                : DiagnosticMessages.CannotAssignThroughImmutableBinding(root.Name),
            "E3000");
        if (assign.Span.Length > 0)
        {
            diagnostic.WithLabel(assign.Span, "assignment requires a writable place");
        }

        if (varSymbol.Span.Length > 0)
        {
            diagnostic.WithLabel(varSymbol.Span, "immutable binding declared here");
        }

        diagnostic.WithHelp(varSymbol.IsParameter
            ? DiagnosticMessages.CannotAssignThroughImmutableParameterHelp(root.Name)
            : DiagnosticMessages.CannotAssignThroughImmutableBindingHelp(root.Name));
        _diagnostics.Add(diagnostic);
    }

    private static bool TryGetAssignmentRootIdentifier(EidosAstNode target, out IdentifierExpr root)
    {
        switch (target)
        {
            case IdentifierExpr identifier:
                root = identifier;
                return true;

            case MethodCallExpr methodCall when methodCall.Receiver != null &&
                                                !methodCall.HasExplicitCallSyntax &&
                                                methodCall.PositionalArgs.Count == 0 &&
                                                methodCall.NamedArgs.Count == 0:
                return TryGetAssignmentRootIdentifier(methodCall.Receiver, out root);

            case IndexExpr indexAccess when indexAccess.Object != null:
                return TryGetAssignmentRootIdentifier(indexAccess.Object, out root);

            case UnaryExpr { Operator: UnaryOp.Deref }:
                root = null!;
                return false;

            default:
                root = null!;
                return false;
        }
    }
}
