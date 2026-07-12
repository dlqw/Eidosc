using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static readonly Lazy<HashSet<string>> PrecompiledValueNames = new(() =>
        PrecompiledModuleRegistry.GetAvailableModulePaths()
            .SelectMany(modulePath =>
            {
                var exports = PrecompiledModuleRegistry.GetExports(modulePath);
                return exports.Values.Concat(exports.Functions);
            })
            .ToHashSet(StringComparer.Ordinal));

    private void ResolveExpressionReferences(EidosAstNode expr)
    {
        switch (expr)
        {
            case LiteralExpr:
                break;

            case IdentifierExpr ident:
                ResolveIdentifierReference(ident);
                break;

            case PathExpr path:
                ResolvePathReference(path);
                break;

            case BlockExpr block:
                ResolveBlockReferences(block);
                break;

            case DoExpr doExpr:
                ResolveDoExprReferences(doExpr);
                break;

            case IfExpr ifExpr:
                ResolveIfReferences(ifExpr);
                break;

            case IfLetExpr ifLetExpr:
                ResolveIfLetReferences(ifLetExpr);
                break;

            case WhileLetExpr whileLetExpr:
                ResolveWhileLetReferences(whileLetExpr);
                break;

            case LoopExpr loop:
                ResolveLoopReferences(loop);
                break;

            case MatchExpr match:
                ResolveMatchReferences(match);
                break;

            case PatternGuardExpr patternGuard:
                ResolvePatternGuardReferences(patternGuard);
                break;

            case SequentialGuardExpr sequentialGuard:
                ResolveSequentialGuardReferences(sequentialGuard);
                break;

            case LambdaExpr lambda:
                ResolveLambdaReferences(lambda);
                break;

            case CtorExpr ctor:
                ResolveCtorReferences(ctor);
                break;

            case RecordUpdateExpr recordUpdate:
                ResolveRecordUpdateReferences(recordUpdate);
                break;

            case ContextualRecordLiteralExpr contextualRecord:
                ResolveContextualRecordLiteralReferences(contextualRecord);
                break;

            case CallExpr call:
                ResolveCallReferences(call);
                break;

            case MethodCallExpr methodCall:
                ResolveMethodCallReferences(methodCall);
                break;

            case TupleExpr tuple:
                ResolveTupleReferences(tuple);
                break;

            case ListExpr list:
                ResolveListReferences(list);
                break;

            case ReturnExpr ret:
                if (ret.Value != null)
                {
                    ResolveExpressionReferences(ret.Value);
                }
                break;

            case BreakExpr breakExpr:
                if (breakExpr.Value != null)
                {
                    ResolveExpressionReferences(breakExpr.Value);
                }
                break;

            case ContinueExpr:
                break;

            case UnreachableExpr:
                break;

            case BinaryExpr binary:
                if (binary.Left != null)
                {
                    ResolveExpressionReferences(binary.Left);
                }

                if (binary.Right != null)
                {
                    if (binary.Operator == BinaryOp.Pipe &&
                        binary.Right is IdentifierExpr pipeTargetIdentifier)
                    {
                        ResolveIdentifierCallTargetReference(pipeTargetIdentifier);
                    }
                    else
                    {
                        ResolveExpressionReferences(binary.Right);
                    }
                }
                break;

            case InfixCallExpr infixCall:
                if (infixCall.Left != null)
                {
                    ResolveExpressionReferences(infixCall.Left);
                }

                if (infixCall.Right != null)
                {
                    ResolveExpressionReferences(infixCall.Right);
                }

                if (!string.IsNullOrEmpty(infixCall.FunctionName))
                {
                    if (TryCollectVisibleFunctionCandidates(infixCall.FunctionName, out var visibleFunctionCandidates))
                    {
                        infixCall.ClearFunctionCandidates();
                        foreach (var candidate in visibleFunctionCandidates)
                        {
                            infixCall.AddFunctionCandidate(candidate);
                        }
                    }
                    else if (TryResolveValueSymbol(infixCall.FunctionName, allowConstructors: true, out var symbolId, out _, out _))
                    {
                        infixCall.FunctionSymbolId = symbolId;
                    }
                    else if (_lookupService.TryCollectAmbiguousImportedValueCandidates(
                                 infixCall.FunctionName,
                                 CreateLookupContext(),
                                 out var candidates))
                    {
                        infixCall.ClearFunctionCandidates();
                        foreach (var candidate in candidates)
                        {
                            infixCall.AddFunctionCandidate(candidate);
                        }
                    }
                    else if (IsLowerIdentifierName(infixCall.FunctionName) &&
                             IsKnownPrecompiledValueName(infixCall.FunctionName))
                    {
                        return;
                    }
                    else
                    {
                        AddUndefinedIdentifierError(infixCall.Span, infixCall.FunctionName);
                    }
                }
                break;

            case UnaryExpr unary:
                if (unary.Operand != null)
                {
                    ResolveExpressionReferences(unary.Operand);
                }
                break;

            case IndexExpr index:
                // 反糖化：ptr_load_as[Float](ptr) → IndexExpr { Object = IdentifierExpr("ptr_load_as"), TypeArgs = [Float] }
                if (index.IsTypeApplication && TryDesugarGenericPtrIntrinsicIndex(index))
                {
                    // 已反糖化，IndexExpr 的 Object 已被解析
                    break;
                }

                if (index.Object != null)
                {
                    ResolveExpressionReferences(index.Object);
                }

                if (index.Index != null)
                {
                    ResolveExpressionReferences(index.Index);
                }

                foreach (var typeArg in index.TypeArgs)
                {
                    ResolveTypeReferences(typeArg);
                }
                break;

            case ListComprehension listComp:
                ResolveListComprehensionReferences(listComp);
                break;

            case GivenExpr given:
                ResolveGivenReferences(given);
                break;

            case AssociatedConstExpr associatedConst:
                ResolveAssociatedConstExprReferences(associatedConst);
                break;

        }
    }


    /// <summary>
    /// 反糖化 ptr_load_as[T] / ptr_store_as[T] 类型应用。
    /// 当 IndexExpr 是类型应用（如 ptr_load_as[Float]）且目标是 ptr_load_as/ptr_store_as 时，
    /// 将整个 IndexExpr 替换为对具体 Phase A 函数的 PathExpr 引用。
    /// </summary>
    private bool TryDesugarGenericPtrIntrinsicIndex(IndexExpr index)
    {
        if (index.Object is not IdentifierExpr ident)
            return false;

        if (ident.Name is not (WellKnownStrings.InternalNames.PtrLoadAs or WellKnownStrings.InternalNames.PtrStoreAs))
            return false;

        if (index.TypeArgs.Count == 0)
        {
            AddError(index.Span, DiagnosticMessages.PtrIntrinsicRequiresExplicitTypeArgument(ident.Name));
            return false;
        }

        if (index.TypeArgs.Count > 1)
        {
            AddError(index.Span, DiagnosticMessages.PtrIntrinsicRequiresExactlyOneTypeArgument(ident.Name));
            return false;
        }

        var typeName = ExtractTypeArgName(index.TypeArgs[0]);
        var desugared = MapPtrIntrinsicTypeToFunc(ident.Name, typeName);

        if (desugared == null)
        {
            AddError(index.Span, DiagnosticMessages.UnsupportedPtrIntrinsicTypeArgument(typeName, ident.Name));
            return false;
        }

        // 将 IdentifierExpr 的名称改为反糖化后的函数名
        ident.SetName(desugared);
        // 清除类型参数，使 IndexExpr 不再被视为类型应用
        index.ClearTypeArgs();
        // 正常解析 IdentifierExpr
        ResolveIdentifierReference(ident);
        return true;
    }


    private void ResolveBlockReferences(BlockExpr block)
    {
        using var _ = _symbolTable.PushScopeGuard(ScopeKind.Block);

        foreach (var stmt in block.Statements)
        {
            if (stmt is Declaration decl)
            {
                CollectDeclaration(decl);
            }
        }

        foreach (var stmt in block.Statements)
        {
            if (stmt is Declaration decl)
            {
                ResolveDeclarationReferences(decl);
            }
            else
            {
                ResolveExpressionReferences(stmt);
            }
        }
    }

    private void ResolveGivenReferences(GivenExpr given)
    {
        if (given.Target != null)
        {
            ResolveExpressionReferences(given.Target);
        }

        if (given.EvidencePath.Count == 0)
        {
            return;
        }

        if (given.EvidencePath.Count == 1 &&
            _instanceDeclarations.TryGetValue(given.EvidencePath[0], out var instance))
        {
            given.EvidenceSymbolId = instance.SymbolId;
            ValidateGivenEvidenceSymbol(given);
            return;
        }

        var result = ResolvePathWithImports(given.EvidencePath);
        if (result.IsSuccess)
        {
            given.EvidenceSymbolId = result.SymbolId;
            ValidateGivenEvidenceSymbol(given);
        }
        else
        {
            AddUndefinedIdentifierError(given.Span, string.Join(WellKnownStrings.Separators.Path, given.EvidencePath));
        }
    }

    private void ValidateGivenEvidenceSymbol(GivenExpr given)
    {
        if (!given.EvidenceSymbolId.IsValid)
        {
            AddError(given.Span, $"Given evidence '{FormatGivenEvidencePath(given)}' does not resolve to a valid instance.");
            return;
        }

        if (_symbolTable.GetSymbol(given.EvidenceSymbolId) is ImplSymbol)
        {
            return;
        }

        AddError(given.Span, $"Given evidence '{FormatGivenEvidencePath(given)}' must resolve to a named instance.");
    }

    private static string FormatGivenEvidencePath(GivenExpr given)
        => given.EvidencePath.Count == 0
            ? "<missing>"
            : string.Join(WellKnownStrings.Separators.Path, given.EvidencePath);

    private void ResolveAssociatedConstExprReferences(AssociatedConstExpr associatedConst)
    {
        if (associatedConst.Target == null)
        {
            AddError(associatedConst.Span, $"Associated const projection '.{associatedConst.MemberName}' requires a target trait application.");
            return;
        }

        ResolveTypeReferences(associatedConst.Target);

        if (associatedConst.Target is not TypePath targetPath ||
            !targetPath.SymbolId.IsValid ||
            _symbolTable.GetSymbol(targetPath.SymbolId) is not TraitSymbol)
        {
            AddError(associatedConst.Span, $"Associated const projection '.{associatedConst.MemberName}' requires a trait type target.");
            return;
        }

        if (!_traitDefinitions.TryGetValue(targetPath.SymbolId, out var traitDefinition))
        {
            AddError(associatedConst.Span, $"Trait definition for associated const projection '{targetPath.TypeName}.{associatedConst.MemberName}' is unavailable.");
            return;
        }

        var associatedConstDecl = traitDefinition.AssociatedConsts
            .FirstOrDefault(item => string.Equals(item.Name, associatedConst.MemberName, StringComparison.Ordinal));
        if (associatedConstDecl == null)
        {
            AddError(associatedConst.Span, $"Trait '{traitDefinition.Name}' does not declare associated const '{associatedConst.MemberName}'.");
            return;
        }

        associatedConst.SymbolId = targetPath.SymbolId;
    }

    private void ResolveDoExprReferences(DoExpr doExpr)
    {
        using var _ = _symbolTable.PushScopeGuard(ScopeKind.Block);

        foreach (var binding in doExpr.Bindings)
        {
            if (binding.Value != null)
            {
                ResolveExpressionReferences(binding.Value);
            }

            if (binding.Kind == DoBindingKind.Bind && binding.Pattern != null)
            {
                using var context = PushPatternDiagnosticContext("do-bind");
                ResolvePatternBindings(binding.Pattern);
            }
            else if (binding.Kind == DoBindingKind.Let)
            {
                ResolveDoLetBinding(binding);
            }
        }
    }

    private void ResolveDoLetBinding(DoBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.VarName))
        {
            AddError(binding.Span, DiagnosticMessages.DoLetBindingRequiresVariableName);
            return;
        }

        if (TryReportReservedSelfDeclaration(binding.VarName, binding.Span, "value"))
        {
            binding.SymbolId = SymbolId.None;
            return;
        }

        if (TryReportReservedInternalNameDeclaration(binding.VarName, binding.Span, "value"))
        {
            binding.SymbolId = SymbolId.None;
            return;
        }

        binding.SymbolId = _symbolTable.DeclareVariable(
            binding.VarName,
            binding.Span,
            isMutable: false,
            isPublic: false);
    }

    private void ResolveIfReferences(IfExpr ifExpr)
    {
        if (ifExpr.Condition != null)
        {
            ResolveExpressionReferences(ifExpr.Condition);
        }

        if (ifExpr.ThenBranch != null)
        {
            ResolveExpressionReferences(ifExpr.ThenBranch);
        }

        if (ifExpr.ElseBranch != null)
        {
            ResolveExpressionReferences(ifExpr.ElseBranch);
        }
    }

    private void ResolveIfLetReferences(IfLetExpr ifLetExpr)
    {
        if (ifLetExpr.MatchedExpression != null)
        {
            ResolveExpressionReferences(ifLetExpr.MatchedExpression);
        }

        if (ifLetExpr.Pattern == null)
        {
            AddError(ifLetExpr.Span, DiagnosticMessages.IfLetExpressionRequiresBindingPattern);
        }
        else
        {
            using var _ = _symbolTable.PushScopeGuard(ScopeKind.PatternBranch);
            using var context = PushPatternDiagnosticContext("if-let-pattern");
            ResolvePatternBindings(ifLetExpr.Pattern);

            if (ifLetExpr.ThenBranch != null)
            {
                ResolveExpressionReferences(ifLetExpr.ThenBranch);
            }
        }

        if (ifLetExpr.ElseBranch != null)
        {
            ResolveExpressionReferences(ifLetExpr.ElseBranch);
        }
    }

    private void ResolveWhileLetReferences(WhileLetExpr whileLetExpr)
    {
        if (whileLetExpr.MatchedExpression != null)
        {
            ResolveExpressionReferences(whileLetExpr.MatchedExpression);
        }

        if (whileLetExpr.Pattern == null)
        {
            AddError(whileLetExpr.Span, DiagnosticMessages.WhileLetExpressionRequiresBindingPattern);
            if (whileLetExpr.Body != null)
            {
                ResolveExpressionReferences(whileLetExpr.Body);
            }

            return;
        }

        using var _ = _symbolTable.PushScopeGuard(ScopeKind.PatternBranch);
        using var context = PushPatternDiagnosticContext("while-let-pattern");
        ResolvePatternBindings(whileLetExpr.Pattern);

        if (whileLetExpr.Body != null)
        {
            ResolveExpressionReferences(whileLetExpr.Body);
        }
    }

    private void ResolveLoopReferences(LoopExpr loop)
    {
        if (loop.Body != null)
        {
            ResolveExpressionReferences(loop.Body);
        }
    }

    private void ResolveMatchReferences(MatchExpr match)
    {
        if (match.MatchedExpression != null)
        {
            ResolveExpressionReferences(match.MatchedExpression);
        }

        for (var i = 0; i < match.Branches.Count; i++)
        {
            ResolvePatternBranchReferences(match.Branches[i], i + 1);
        }

        match.SetPatternExhaustive(ShouldSkipTrustedPrecompiledPatternCoverage(match.Span)
            ? true
            : _patternCoveragePass.Analyze(new PatternCoverageRequest(
                match.Branches,
                match.Span,
                "match expression",
                TryGetIdentifierName(match.MatchedExpression, out var matchedIdentifier) ? matchedIdentifier : null)));
    }

    private void ResolveLambdaReferences(LambdaExpr lambda)
    {
        using var scopeGuard = _symbolTable.PushScopeGuard(ScopeKind.Lambda);

        foreach (var param in lambda.Parameters)
        {
            if (param is VarPattern varPattern)
            {
                var paramId = DeclarePatternVariable(
                    varPattern.Name,
                    param.Span,
                    isParameter: true,
                    isPatternBound: false,
                    bindingMode: varPattern.BindingMode,
                    isMutable: varPattern.IsMutableBinding);
                varPattern.SymbolId = paramId;
            }
            else
            {
                ResolvePatternBindings(param, isParameter: true);
            }
        }

        if (lambda.Body != null)
        {
            ResolveExpressionReferences(lambda.Body);
        }
    }

    private void ResolveCallReferences(CallExpr call)
    {
        if (call.Function != null)
        {
            ResolveCallTargetReferences(call.Function);
        }

        foreach (var arg in call.PositionalArgs)
        {
            ResolveExpressionReferences(arg);
        }

        foreach (var arg in call.NamedArgs)
        {
            if (arg.Value != null)
            {
                ResolveExpressionReferences(arg.Value);
            }
        }

    }

    private void ResolveCallTargetReferences(EidosAstNode target)
    {
        if (target is not IdentifierExpr identifier)
        {
            ResolveExpressionReferences(target);
            return;
        }

        ResolveIdentifierCallTargetReference(identifier);
    }

    private void ResolveIdentifierCallTargetReference(IdentifierExpr ident)
    {
        if (ident.Name == WellKnownStrings.Keywords.ReflConstructor)
        {
            ident.IsConstructor = true;
            return;
        }

        if (TryCollectVisibleFunctionCandidates(ident.Name, out var visibleFunctionCandidates))
        {
            ident.ClearValueCandidates();
            foreach (var candidate in visibleFunctionCandidates)
            {
                ident.AddValueCandidate(candidate);
            }

            return;
        }

        var result = _lookupService.Lookup(
            ident.Name,
            LookupKind.Value | LookupKind.Constructor,
            CreateLookupContext());
        if (result.IsSuccess)
        {
            ident.SymbolId = result.SymbolId;
            ident.IsConstructor = result.IsConstructor;
            return;
        }

        if (_lookupService.TryCollectAmbiguousImportedValueCandidates(ident.Name, CreateLookupContext(), out var candidates))
        {
            ident.ClearValueCandidates();
            foreach (var candidate in candidates)
            {
                ident.AddValueCandidate(candidate);
            }

            return;
        }

        if (IsLowerIdentifierName(ident.Name) && IsKnownPrecompiledValueName(ident.Name))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            AddError(ident.Span, result.ErrorMessage);
            return;
        }

        AddUndefinedIdentifierError(ident.Span, ident.Name);
    }

    private static bool IsLowerIdentifierName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && char.IsLower(name[0]);
    }

    private static bool IsKnownPrecompiledValueName(string name)
    {
        return PrecompiledValueNames.Value.Contains(name);
    }

    private void ResolveCtorReferences(CtorExpr ctor)
    {
        if (!string.IsNullOrWhiteSpace(ctor.ConstructorName))
        {
            var ctorSymbol = _symbolTable.LookupConstructor(ctor.ConstructorName);
            if (ctorSymbol != null)
            {
                ctor.SymbolId = ctorSymbol.Value;
            }
            else
            {
                AddError(ctor.Span, DiagnosticMessages.UndefinedConstructor(ctor.ConstructorName));
            }
        }

        foreach (var arg in ctor.PositionalArgs)
        {
            ResolveExpressionReferences(arg);
        }

        if (ctor.UpdateBase != null)
        {
            ResolveExpressionReferences(ctor.UpdateBase);
        }

        foreach (var field in ctor.NamedArgs)
        {
            if (field.Value != null)
            {
                ResolveExpressionReferences(field.Value);
            }
        }
    }

    private void ResolveRecordUpdateReferences(RecordUpdateExpr recordUpdate)
    {
        if (recordUpdate.Base != null)
        {
            ResolveExpressionReferences(recordUpdate.Base);
        }

        foreach (var field in recordUpdate.NamedArgs)
        {
            if (field.Value != null)
            {
                ResolveExpressionReferences(field.Value);
            }
        }
    }

    private void ResolveContextualRecordLiteralReferences(ContextualRecordLiteralExpr contextualRecord)
    {
        foreach (var field in contextualRecord.NamedArgs)
        {
            if (field.Value != null)
            {
                ResolveExpressionReferences(field.Value);
            }
        }
    }

    private void ResolveMethodCallReferences(MethodCallExpr methodCall)
    {
        if (methodCall.Receiver != null)
        {
            ResolveExpressionReferences(methodCall.Receiver);
        }

        foreach (var arg in methodCall.PositionalArgs)
        {
            ResolveExpressionReferences(arg);
        }

        foreach (var arg in methodCall.NamedArgs)
        {
            if (arg.Value != null)
            {
                ResolveExpressionReferences(arg.Value);
            }
        }

        if (string.IsNullOrWhiteSpace(methodCall.MethodName))
        {
            AddError(methodCall.Span, DiagnosticMessages.MethodCallMissingMethodName);
            return;
        }

        if (CanUseTypeDirectedMethodLookup(methodCall))
        {
            CollectTypeDirectedMethodCandidates(methodCall);
            if (methodCall.MethodCandidateSymbolIds.Count > 0)
            {
                if (methodCall.MethodCandidateSymbolIds.Count == 1)
                {
                    methodCall.SymbolId = methodCall.MethodCandidateSymbolIds[0];
                }

                return;
            }
        }

        if (TryResolveValueSymbol(methodCall.MethodName, allowConstructors: false, out var methodSymbolId, out _, out _))
        {
            var resolvedSymbol = _symbolTable.GetSymbol(methodSymbolId);

            // 对于裸点访问（a.b 无括号无参数），仅当解析到函数符号时才设置 SymbolId；
            // 解析到 VarSymbol（局部变量/参数）时应推迟到 TypeInferer 处理字段访问。
            if (CanDeferBareDotMemberResolution(methodCall))
            {
                if (resolvedSymbol is FuncSymbol)
                {
                    methodCall.SymbolId = methodSymbolId;
                    return;
                }

                // 非函数符号（如局部变量）→ 推迟到 TypeInferer
                return;
            }

            if (resolvedSymbol is not FuncSymbol)
            {
                AddError(methodCall.Span, DiagnosticMessages.UndefinedFunction(methodCall.MethodName));
                return;
            }

            methodCall.SymbolId = methodSymbolId;
            return;
        }

        if (CanDeferBareDotMemberResolution(methodCall))
        {
            return;
        }

        if (CanUseTypeDirectedMethodLookup(methodCall))
        {
            return;
        }

        AddError(methodCall.Span, DiagnosticMessages.UndefinedFunction(methodCall.MethodName));
    }

    private static bool CanUseTypeDirectedMethodLookup(MethodCallExpr methodCall)
    {
        return methodCall.Receiver != null && methodCall.HasExplicitCallSyntax;
    }

    private void CollectTypeDirectedMethodCandidates(MethodCallExpr methodCall)
    {
        methodCall.ClearMethodCandidates();

        foreach (var candidate in _symbolTable.LookupValueCandidates(methodCall.MethodName))
        {
            AddMethodCandidateIfFunction(methodCall, candidate);
        }

        if (_currentModule.IsValid && _importScopes.TryGetValue(_currentModule, out var importScope))
        {
            foreach (var detail in importScope.GetImportDetails(methodCall.MethodName))
            {
                AddMethodCandidateIfFunction(methodCall, detail.SymbolId);
            }

            AddMethodCandidateIfFunction(methodCall, importScope.LookupImportedSymbol(methodCall.MethodName));
        }
    }

    private void AddMethodCandidateIfFunction(MethodCallExpr methodCall, SymbolId? symbolId)
    {
        if (symbolId is not { IsValid: true } candidateId)
        {
            return;
        }

        if (_symbolTable.GetSymbol(candidateId) is FuncSymbol { IsCStructAccessor: false })
        {
            methodCall.AddMethodCandidate(candidateId);
        }
    }

    private static bool CanDeferBareDotMemberResolution(MethodCallExpr methodCall)
    {
        return !methodCall.HasExplicitCallSyntax &&
               methodCall.Receiver != null &&
               methodCall.PositionalArgs.Count == 0 &&
               methodCall.NamedArgs.Count == 0;
    }


    private void ResolveTupleReferences(TupleExpr tuple)
    {
        foreach (var element in tuple.Elements)
        {
            ResolveExpressionReferences(element);
        }
    }

    private void ResolveListReferences(ListExpr list)
    {
        foreach (var element in list.Elements)
        {
            ResolveExpressionReferences(element);
        }
    }

    private void ResolveListComprehensionReferences(ListComprehension listComp)
    {
        using var scopeGuard = _symbolTable.PushScopeGuard(ScopeKind.Block);

        foreach (var qualifier in listComp.Qualifiers)
        {
            if (qualifier.Kind == QualifierKind.Generator)
            {
                if (qualifier.GeneratorExpression != null)
                {
                    ResolveExpressionReferences(qualifier.GeneratorExpression);
                }

                if (qualifier.GeneratorPattern != null)
                {
                    ResolvePatternBindings(qualifier.GeneratorPattern);
                }
            }
            else if (qualifier.Kind == QualifierKind.Guard)
            {
                if (qualifier.GuardExpression != null)
                {
                    ResolveExpressionReferences(qualifier.GuardExpression);
                }
            }
        }

        if (listComp.Output != null)
        {
            ResolveExpressionReferences(listComp.Output);
        }
    }
}
