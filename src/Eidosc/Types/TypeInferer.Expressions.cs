using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private enum EmptyCallResolutionKind
    {
        None,
        ZeroArgument,
        UnitSugar,
        FfiUnitElision
    }

    private readonly record struct EmptyCallResolution(
        EmptyCallResolutionKind Kind,
        Type ResultType,
        int SynthesizedUnitArgumentCount);

    /// <summary>
    /// 推断表达式的类型
    /// </summary>
    public Type InferExpression(EidosAstNode expr)
    {
        var type = expr switch
        {
            LiteralExpr lit => InferLiteral(lit),
            IdentifierExpr ident => InferIdentifier(ident),
            PathExpr path => InferPath(path),
            BlockExpr block => InferBlock(block),
            IfExpr ifExpr => InferIf(ifExpr),
            IfLetExpr ifLetExpr => InferIfLet(ifLetExpr),
            WhileLetExpr whileLetExpr => InferWhileLet(whileLetExpr),
            MatchExpr match => InferMatch(match),
            PatternGuardExpr patternGuard => InferPatternGuardExpr(patternGuard),
            SequentialGuardExpr sequentialGuard => InferSequentialGuardExpr(sequentialGuard),
            DoExpr doExpr => InferDoExpr(doExpr),
            LambdaExpr lambda => InferLambda(lambda),
            CallExpr call => InferCall(call),
            TupleExpr tuple => InferTuple(tuple),
            ListExpr list => InferList(list),
            ReturnExpr ret => InferReturn(ret),
            BreakExpr breakExpr => InferBreak(breakExpr),
            ContinueExpr continueExpr => InferContinue(continueExpr),
            BinaryExpr binary => InferBinary(binary),
            UnaryExpr unary => InferUnary(unary),
            CtorExpr ctor => InferCtor(ctor),
            RecordUpdateExpr recordUpdate => InferRecordUpdate(recordUpdate),
            ContextualRecordLiteralExpr contextualRecord => InferContextualRecordLiteralWithoutExpectedType(contextualRecord),
            MethodCallExpr method => InferMethodCall(method),
            InfixCallExpr infixCall => InferInfixCall(infixCall),
            IndexExpr index => InferIndex(index),
            LoopExpr loop => InferLoop(loop),
            ListComprehension comp => InferListComprehension(comp),
            GivenExpr given => InferGiven(given),
            AssociatedConstExpr associatedConst => InferAssociatedConstExpr(associatedConst),
            Assignment assign => InferAssignmentExpression(assign),
            UnreachableExpr => BaseTypes.Never,
            _ => InferUnsupportedExpression(expr)
        };

        var appliedType = _substitution.Apply(type);
        expr.InferredType = appliedType;
        return appliedType;
    }

    private Type InferDoExpr(DoExpr doExpr)
    {
        if (doExpr.Bindings.Count == 0)
        {
            return BaseTypes.Unit;
        }

        Type resultType = BaseTypes.Unit;
        var hasRecovery = false;
        foreach (var binding in doExpr.Bindings)
        {
            var valueType = binding.Value != null
                ? SafeInferExpression(binding.Value)
                : CreateMissingDoBindingValueRecoveryType(binding);
            var bindingHasRecovery = ContainsErrorRecoveryType(valueType);

            switch (binding.Kind)
            {
                case DoBindingKind.Bind:
                    if (binding.Pattern == null)
                    {
                        AddError(binding.Span, DiagnosticMessages.DoBindRequiresPattern);
                        bindingHasRecovery = true;
                        break;
                    }

                    var patternType = ExtractDoBindPatternType(valueType, binding.Span);
                    var patternResult = InferPattern(binding.Pattern, patternType);
                    bindingHasRecovery |= ContainsErrorRecoveryType(patternType) ||
                                          ContainsErrorRecoveryType(patternResult);
                    break;
                case DoBindingKind.Let:
                    if (string.IsNullOrWhiteSpace(binding.VarName))
                    {
                        AddError(binding.Span, DiagnosticMessages.DoLetBindingRequiresVariableName);
                        bindingHasRecovery = true;
                        break;
                    }

                    if (!binding.SymbolId.IsValid)
                    {
                        AddError(binding.Span, DiagnosticMessages.DoLetBindingMissingResolvedSymbol(binding.VarName));
                        bindingHasRecovery = true;
                        break;
                    }

                    var resolvedValueType = _substitution.Apply(valueType);
                    var scheme = _env.Generalize(resolvedValueType);
                    _env = _env.Extend(binding.SymbolId, scheme);
                    binding.InferredType = resolvedValueType;
                    break;
                case DoBindingKind.Expr:
                    break;
                default:
                    AddError(binding.Span, DiagnosticMessages.UnsupportedDoBindingKind(binding.Kind));
                    bindingHasRecovery = true;
                    break;
            }

            hasRecovery |= bindingHasRecovery;
            resultType = bindingHasRecovery ? CreateErrorRecoveryType() : valueType;
        }

        return hasRecovery
            ? CreateErrorRecoveryType()
            : _substitution.Apply(resultType);
    }

    private Type CreateMissingDoBindingValueRecoveryType(DoBinding binding)
    {
        return CreateMissingShapeRecoveryType(binding.Span, DiagnosticMessages.DoBindingRequiresValueExpression);
    }

    private Type ExtractDoBindPatternType(Type monadicType, SourceSpan span)
    {
        var resolvedType = _substitution.Apply(monadicType);
        if (ContainsErrorRecoveryType(resolvedType))
        {
            return CreateErrorRecoveryType();
        }

        if (resolvedType is TyCon { Args.Count: > 0 } tyCon)
        {
            return _substitution.Apply(tyCon.Args[^1]);
        }

        AddError(span, DiagnosticMessages.DoBindExpectsMonadicValue(resolvedType));
        return CreateErrorRecoveryType();
    }

    /// <summary>
    /// 推断字面量的类型
    /// </summary>
    private Type InferLiteral(LiteralExpr lit)
    {
        if (lit.IsRecoveredError)
        {
            return CreateErrorRecoveryType();
        }

        return lit.Kind switch
        {
            LiteralKind.Integer => BaseTypes.Int,
            LiteralKind.Float => BaseTypes.Float,
            LiteralKind.String => BaseTypes.String,
            LiteralKind.Char => BaseTypes.Char,
            LiteralKind.Boolean => BaseTypes.Bool,
            LiteralKind.Unit => BaseTypes.Unit,
            _ => InferUnsupportedLiteral(lit)
        };
    }

    /// <summary>
    /// 推断标识符的类型
    /// </summary>
    private Type InferIdentifier(IdentifierExpr ident)
    {
        if (ident.Name == WellKnownStrings.Keywords.ReflConstructor)
        {
            return new TyReflProof { Id = new TypeId(BaseTypes.TypeEqId) };
        }

        if (!ident.SymbolId.IsValid)
        {
            if (ident.ValueCandidateSymbolIds.Count > 0)
            {
                AddError(ident.Span, DiagnosticMessages.AmbiguousImportedValueRequiresCallSiteTypeInfo(ident.Name));
                return CreateErrorRecoveryType();
            }

            AddError(ident.Span, DiagnosticMessages.UndefinedIdentifier(ident.Name));
            return CreateErrorRecoveryType();
        }

        var scheme = _env.Lookup(ident.SymbolId);
        if (scheme != null)
        {
            if (HasValueGenericParameters(ident.SymbolId))
            {
                return ApplyImplicitFunctionEffects(
                    ident.SymbolId,
                    InstantiateSchemeWithGenericArgumentsAndConstraints(
                        scheme,
                        [],
                        ident.SymbolId,
                        ident,
                        ident.Span));
            }

            return ApplyImplicitFunctionEffects(
                ident.SymbolId,
                InstantiateSchemeWithConstraints(scheme, ident.Span));
        }

        // 从符号表查找
        var symbol = _symbolTable.GetSymbol(ident.SymbolId);
        if (symbol != null)
        {
            // 对于函数，创建函数类型
            if (symbol is FuncSymbol funcSymbol)
            {
                if (funcSymbol.IsComptime && !_allowComptimeFunctionReferences)
                {
                    AddComptimeFunctionRuntimeUseError(ident.Span, funcSymbol.Name);
                    return CreateErrorRecoveryType();
                }

                return ApplyImplicitFunctionEffects(ident.SymbolId, CreateFunctionType(funcSymbol));
            }

            if (symbol is CtorSymbol ctorSymbol)
            {
                return InferBareConstructor(ctorSymbol, ident.Span);
            }

            if (symbol is VarSymbol varSymbol)
            {
                if (varSymbol.Scheme != null)
                {
                    return InstantiateSchemeWithConstraints(varSymbol.Scheme, ident.Span);
                }

                if (TryGetDeclaredVariableScheme(ident.SymbolId, out var declaredScheme))
                {
                    return InstantiateSchemeWithConstraints(declaredScheme, ident.Span);
                }

                if (TryCreateTypeFromSymbolMetadataTypeId(varSymbol.Type, out var metadataType))
                {
                    return metadataType;
                }

                AddError(ident.Span, DiagnosticMessages.CannotInferVariableTypeUnavailable(ident.Name));
                return CreateErrorRecoveryType();
            }

            if (symbol is TypeParamSymbol { ParameterKind: GenericParameterKind.Value } valueParameter)
            {
                if (_valueGenericParameterTypesBySymbol.TryGetValue(ident.SymbolId, out var registeredValueType))
                {
                    return registeredValueType;
                }

                if (TryCreateTypeFromSymbolMetadataTypeId(valueParameter.TypeId, out var valueParameterType))
                {
                    return valueParameterType;
                }

                AddError(ident.Span, $"Cannot resolve the declared value type of generic parameter '{ident.Name}'.");
                return CreateErrorRecoveryType();
            }

            if (symbol is AdtSymbol or TraitSymbol or TypeParamSymbol { ParameterKind: GenericParameterKind.Type })
            {
                if (!_allowComptimeFunctionReferences)
                {
                    AddError(ident.Span, $"Type value '{ident.Name}' cannot escape compile-time evaluation.");
                    return CreateErrorRecoveryType();
                }

                return BaseTypes.TypeValue;
            }

            AddNonValueSymbolError(ident.Span, ident.Name, symbol);
            return CreateErrorRecoveryType();
        }

        AddError(ident.Span, DiagnosticMessages.CannotInferIdentifierTypeMissingSymbol(ident.Name));
        return CreateErrorRecoveryType();
    }

    /// <summary>
    /// 推断路径表达式的类型
    /// </summary>
    private Type InferPath(PathExpr path)
    {
        if (path.ModulePath.Count == 0 &&
            string.IsNullOrWhiteSpace(path.PackageAlias) &&
            path.Name == WellKnownStrings.Keywords.ReflConstructor)
        {
            if (path.TypeArgs.Count > 0)
            {
                var explicitTypeArgs = path.TypeArgs
                    .Select(ConvertTypeInCurrentTypeParamContext)
                    .ToList();
                if (explicitTypeArgs.Count != 1)
                {
                    AddError(path.Span, DiagnosticMessages.ConstructorExpectsTypeArguments(path.Name, 1, explicitTypeArgs.Count));
                    return CreateErrorRecoveryType();
                }

                return new TyReflProof
                {
                    Id = new TypeId(BaseTypes.TypeEqId),
                    WitnessType = explicitTypeArgs[0]
                };
            }

            return new TyReflProof { Id = new TypeId(BaseTypes.TypeEqId) };
        }

        if (!path.SymbolId.IsValid)
        {
            if (path.ValueCandidateSymbolIds.Count > 0)
            {
                AddError(path.Span, DiagnosticMessages.AmbiguousImportedValueRequiresCallSiteTypeInfo(path.Name));
                return CreateErrorRecoveryType();
            }

            AddError(path.Span, DiagnosticMessages.CannotResolvePath(FormatPath(path.Path)));
            return CreateErrorRecoveryType();
        }

        var scheme = _env.Lookup(path.SymbolId);
        if (scheme != null)
        {
            if (path.GenericArguments.Count > 0 || HasValueGenericParameters(path.SymbolId))
            {
                return ApplyImplicitFunctionEffects(
                    path.SymbolId,
                    InstantiateSchemeWithGenericArgumentsAndConstraints(
                        scheme,
                        path.GenericArguments,
                        path.SymbolId,
                        path,
                        path.Span));
            }

            if (path.TypeArgs.Count > 0)
            {
                return ApplyImplicitFunctionEffects(
                    path.SymbolId,
                    InstantiateSchemeWithExplicitTypeArgsAndConstraints(scheme, path.TypeArgs, path.Span));
            }

            return ApplyImplicitFunctionEffects(
                path.SymbolId,
                InstantiateSchemeWithConstraints(scheme, path.Span));
        }

        // 从符号表查找
        var symbol = _symbolTable.GetSymbol(path.SymbolId);
        if (symbol is FuncSymbol funcSymbol)
        {
            if (funcSymbol.IsComptime && !_allowComptimeFunctionReferences)
            {
                AddComptimeFunctionRuntimeUseError(path.Span, FormatPath(path.Path));
                return CreateErrorRecoveryType();
            }

            return ApplyImplicitFunctionEffects(path.SymbolId, CreateFunctionType(funcSymbol));
        }
        else if (symbol is CtorSymbol ctorSymbol)
        {
            // 构造器类型
            if (path.TypeArgs.Count > 0)
            {
                AddError(path.Span, DiagnosticMessages.ConstructorPathDoesNotAcceptExplicitTypeArguments(FormatPath(path.Path)));
                return CreateErrorRecoveryType();
            }

            return InferBareConstructor(ctorSymbol, path.Span);
        }
        else if (symbol is VarSymbol varSymbol)
        {
            if (varSymbol.Scheme != null)
            {
                if (path.TypeArgs.Count > 0)
                {
                    return InstantiateSchemeWithExplicitTypeArgsAndConstraints(varSymbol.Scheme, path.TypeArgs, path.Span);
                }

                return InstantiateSchemeWithConstraints(varSymbol.Scheme, path.Span);
            }

            if (TryGetDeclaredVariableScheme(path.SymbolId, out var declaredScheme))
            {
                if (path.TypeArgs.Count > 0)
                {
                    return InstantiateSchemeWithExplicitTypeArgsAndConstraints(declaredScheme, path.TypeArgs, path.Span);
                }

                return InstantiateSchemeWithConstraints(declaredScheme, path.Span);
            }

            if (path.TypeArgs.Count == 0 &&
                TryCreateTypeFromSymbolMetadataTypeId(varSymbol.Type, out var metadataType))
            {
                return metadataType;
            }

            AddError(path.Span, DiagnosticMessages.CannotInferVariablePathTypeUnavailable(FormatPath(path.Path)));
            return CreateErrorRecoveryType();
        }
        else if (symbol is TypeParamSymbol { ParameterKind: GenericParameterKind.Value } valueParameter)
        {
            if (path.TypeArgs.Count > 0)
            {
                AddError(path.Span, DiagnosticMessages.PathDoesNotAcceptExplicitTypeArguments(FormatPath(path.Path)));
                return CreateErrorRecoveryType();
            }

            if (_valueGenericParameterTypesBySymbol.TryGetValue(path.SymbolId, out var registeredValueType))
            {
                return registeredValueType;
            }

            if (TryCreateTypeFromSymbolMetadataTypeId(valueParameter.TypeId, out var valueParameterType))
            {
                return valueParameterType;
            }

            AddError(path.Span, $"Cannot resolve the declared value type of generic parameter '{FormatPath(path.Path)}'.");
            return CreateErrorRecoveryType();
        }
        else if (symbol is AdtSymbol or TraitSymbol or TypeParamSymbol { ParameterKind: GenericParameterKind.Type })
        {
            if (!_allowComptimeFunctionReferences)
            {
                AddError(path.Span, $"Type value '{FormatPath(path.Path)}' cannot escape compile-time evaluation.");
                return CreateErrorRecoveryType();
            }

            return BaseTypes.TypeValue;
        }

        if (path.TypeArgs.Count > 0)
        {
            AddError(path.Span, DiagnosticMessages.PathDoesNotAcceptExplicitTypeArguments(FormatPath(path.Path)));
            return CreateErrorRecoveryType();
        }

        if (symbol != null)
        {
            AddNonValueSymbolError(path.Span, FormatPath(path.Path), symbol);
            return CreateErrorRecoveryType();
        }

        AddError(path.Span, DiagnosticMessages.CannotInferPathTypeMissingSymbol(FormatPath(path.Path)));
        return CreateErrorRecoveryType();
    }

    private void AddNonValueSymbolError(SourceSpan span, string displayName, Symbol symbol)
    {
        AddError(span, DiagnosticMessages.SymbolIsNotValue(displayName, FormatNonValueSymbolKind(symbol.Kind)));
    }

    private static string FormatPath(IEnumerable<string> path) =>
        string.Join(WellKnownStrings.Separators.Path, path);

    private bool TryGetDeclaredVariableScheme(SymbolId symbolId, out TypeScheme scheme)
    {
        scheme = null!;
        if (!symbolId.IsValid ||
            !_valueTypeAnnotationsBySymbol.TryGetValue(symbolId, out var typeAnnotation))
        {
            return false;
        }

        var declaredType = _substitution.Apply(ConvertType(typeAnnotation, []));
        scheme = _env.Generalize(declaredType);
        UpdateVariableSymbolType(symbolId, declaredType, scheme);
        return true;
    }

    private static bool TryCreateTypeFromSymbolMetadataTypeId(TypeId typeId, out Type type)
    {
        type = null!;
        if (!typeId.IsValid)
        {
            return false;
        }

        type = typeId.Value switch
        {
            BaseTypes.IntId => BaseTypes.Int,
            BaseTypes.FloatId => BaseTypes.Float,
            BaseTypes.BoolId => BaseTypes.Bool,
            BaseTypes.StringId => BaseTypes.String,
            BaseTypes.CharId => BaseTypes.Char,
            BaseTypes.UnitId => BaseTypes.Unit,
            BaseTypes.RawPtrId => new TyCon { Name = WellKnownStrings.BuiltinTypes.RawPtr, Id = new TypeId(BaseTypes.RawPtrId) },
            BaseTypes.CfnId => BaseTypes.Cfn,
            BaseTypes.TypeEqId => BaseTypes.TypeEq,
            BaseTypes.NeverId => BaseTypes.Never,
            _ => null!
        };

        return type != null;
    }

    private static string FormatNonValueSymbolKind(SymbolKind kind)
    {
        return kind switch
        {
            SymbolKind.TypeParameter => DiagnosticMessages.SymbolKindTypeParameter,
            SymbolKind.Adt => DiagnosticMessages.SymbolKindType,
            SymbolKind.TypeAlias => DiagnosticMessages.SymbolKindTypeAlias,
            SymbolKind.Effect => DiagnosticMessages.SymbolKindEffect,
            SymbolKind.Trait => DiagnosticMessages.SymbolKindTrait,
            SymbolKind.Module => DiagnosticMessages.SymbolKindModule,
            SymbolKind.Field => DiagnosticMessages.SymbolKindField,
            SymbolKind.Impl => DiagnosticMessages.SymbolKindTraitImplementation,
            SymbolKind.Proof => DiagnosticMessages.SymbolKindProof,
            _ => kind.ToString().ToLowerInvariant()
        };
    }

    /// <summary>
    /// 推断块表达式的类型
    /// </summary>
    private Type InferBlock(BlockExpr block)
    {
        Type resultType = BaseTypes.Unit;
        var hasExplicitResult = block.ResultExpression != null;
        var explicitResultSeen = false;
        var hasRecovery = false;

        foreach (var stmt in block.Statements)
        {
            try
            {
                if (stmt is LetDecl letDecl)
                {
                    InferLetDecl(letDecl);
                    hasRecovery |= HasRecoveredInferredType(letDecl);
                }
                else if (stmt is LetQuestionDecl letQuestionDecl)
                {
                    InferLetQuestionDecl(letQuestionDecl);
                    hasRecovery |= HasRecoveredInferredType(letQuestionDecl);
                }
                else if (stmt is Assignment assign)
                {
                    InferAssignment(assign);
                    hasRecovery |= HasRecoveredInferredType(assign);
                }
                else
                {
                    var exprType = SafeInferExpression(stmt);
                    var statementValueType = exprType;

                    hasRecovery |= ContainsErrorRecoveryType(exprType) ||
                                   ContainsErrorRecoveryType(statementValueType);

                    if (hasExplicitResult && ReferenceEquals(stmt, block.ResultExpression))
                    {
                        resultType = statementValueType;
                        explicitResultSeen = true;
                    }
                }
            }
            catch (TypeInferenceException ex)
            {
                AddError(stmt.Span, ex.Message);
                hasRecovery = true;
            }
        }

        if (hasExplicitResult && !explicitResultSeen)
        {
            // 兜底：某些 CST 形态下显式结果表达式不在 Statements 集合中。
            var explicitResultType = SafeInferExpression(block.ResultExpression!);
            resultType = explicitResultType;
            hasRecovery |= ContainsErrorRecoveryType(resultType);
        }

        if (hasRecovery)
        {
            return CreateErrorRecoveryType();
        }

        return _substitution.Apply(resultType);
    }

    private bool HasRecoveredInferredType(EidosAstNode node)
    {
        return node.InferredType is Type type &&
               ContainsErrorRecoveryType(_substitution.Apply(type));
    }

    /// <summary>
    /// 推断 if 表达式的类型
    /// </summary>
    private Type InferIf(IfExpr ifExpr)
    {
        var conditionHasRecovery = false;
        if (ifExpr.Condition != null)
        {
            var condType = SafeInferExpression(ifExpr.Condition);
            var conditionResult = TryUnify(BaseTypes.Bool, condType, ifExpr.Condition.Span, DiagnosticMessages.IfConditionMustBeBool);
            conditionHasRecovery = ContainsErrorRecoveryType(conditionResult);
        }
        else
        {
            AddError(ifExpr.Span, DiagnosticMessages.IfExpressionMissingCondition);
            conditionHasRecovery = true;
        }

        Type thenType;
        if (ifExpr.ThenBranch != null)
        {
            thenType = SafeInferExpression(ifExpr.ThenBranch);
        }
        else
        {
            AddError(ifExpr.Span, DiagnosticMessages.IfExpressionRequiresThenBranch);
            thenType = CreateErrorRecoveryType();
        }

        var elseType = ifExpr.ElseBranch != null
            ? SafeInferExpression(ifExpr.ElseBranch)
            : BaseTypes.Unit;

        var branchResult = JoinControlFlowTypes(thenType, elseType, ifExpr.Span, DiagnosticMessages.IfBranchTypeMismatch);
        return conditionHasRecovery || ContainsErrorRecoveryType(branchResult)
            ? CreateErrorRecoveryType()
            : branchResult;
    }

    /// <summary>
    /// 推断 if-let 表达式的类型
    /// </summary>
    private Type InferIfLet(IfLetExpr ifLetExpr)
    {
        var scrutineeType = ifLetExpr.MatchedExpression != null
            ? SafeInferExpression(ifLetExpr.MatchedExpression)
            : CreateMissingScrutineeRecovery(ifLetExpr.Span, "If-let");
        var hasRecovery = ContainsErrorRecoveryType(scrutineeType);

        Type thenType = BaseTypes.Unit;
        if (ifLetExpr.Pattern != null)
        {
            var savedEnv = _env;
            var savedSubstitution = PatternHasGadtConstructor(ifLetExpr.Pattern)
                ? _substitution.Clone()
                : null;
            Type declaredThenType = _substitution.FreshTypeVariable();
            try
            {
                var patternType = InferPattern(ifLetExpr.Pattern, scrutineeType);
                scrutineeType = TryUnify(patternType, scrutineeType, ifLetExpr.Pattern.Span, DiagnosticMessages.IfLetPatternTypeMismatch);
                hasRecovery |= ContainsErrorRecoveryType(patternType) || ContainsErrorRecoveryType(scrutineeType);

                if (ifLetExpr.ThenBranch != null)
                {
                    var branchType = SafeInferExpression(ifLetExpr.ThenBranch);
                    var thenBranchResult = TryUnify(
                        declaredThenType,
                        branchType,
                        ifLetExpr.ThenBranch.Span,
                        DiagnosticMessages.IfLetBranchTypeMismatch);
                    hasRecovery |= ContainsErrorRecoveryType(branchType) || ContainsErrorRecoveryType(thenBranchResult);
                }
                else
                {
                    AddError(ifLetExpr.Span, DiagnosticMessages.IfLetExpressionRequiresThenBranch);
                    declaredThenType = CreateErrorRecoveryType();
                    hasRecovery = true;
                }
            }
            finally
            {
                // if-let 模式绑定仅在 then 分支作用域内生效。
                _env = savedEnv;
                if (savedSubstitution != null)
                {
                    _substitution.RestoreFrom(savedSubstitution);
                }
            }

            thenType = ContainsErrorRecoveryType(declaredThenType)
                ? declaredThenType
                : _substitution.Apply(declaredThenType);
        }
        else if (ifLetExpr.ThenBranch != null)
        {
            AddError(ifLetExpr.Span, DiagnosticMessages.IfLetExpressionMissingPattern);
            hasRecovery = true;
            thenType = SafeInferExpression(ifLetExpr.ThenBranch);
        }
        else
        {
            AddError(ifLetExpr.Span, DiagnosticMessages.IfLetExpressionMissingPattern);
            hasRecovery = true;
        }

        Type elseType = ifLetExpr.ElseBranch != null
            ? SafeInferExpression(ifLetExpr.ElseBranch)
            : BaseTypes.Unit;

        var branchResult = JoinControlFlowTypes(thenType, elseType, ifLetExpr.Span, DiagnosticMessages.IfLetBranchTypeMismatch);
        return hasRecovery || ContainsErrorRecoveryType(branchResult)
            ? CreateErrorRecoveryType()
            : branchResult;
    }

    /// <summary>
    /// 推断 while-let 表达式的类型
    /// </summary>
    private Type InferWhileLet(WhileLetExpr whileLetExpr)
    {
        var scrutineeType = whileLetExpr.MatchedExpression != null
            ? SafeInferExpression(whileLetExpr.MatchedExpression)
            : CreateMissingScrutineeRecovery(whileLetExpr.Span, "While-let");
        var hasRecovery = ContainsErrorRecoveryType(scrutineeType);

        if (whileLetExpr.Pattern != null)
        {
            var savedEnv = _env;
            var savedSubstitution = PatternHasGadtConstructor(whileLetExpr.Pattern)
                ? _substitution.Clone()
                : null;
            try
            {
                var patternType = InferPattern(whileLetExpr.Pattern, scrutineeType);
                var patternResult = TryUnify(patternType, scrutineeType, whileLetExpr.Pattern.Span, DiagnosticMessages.WhileLetPatternTypeMismatch);
                hasRecovery |= ContainsErrorRecoveryType(patternType) || ContainsErrorRecoveryType(patternResult);

                if (whileLetExpr.Body != null)
                {
                    _ = SafeInferExpression(whileLetExpr.Body);
                }
                else
                {
                    AddError(whileLetExpr.Span, DiagnosticMessages.WhileLetExpressionRequiresBody);
                    hasRecovery = true;
                }
            }
            finally
            {
                // while-let 模式绑定仅在循环体内可见。
                _env = savedEnv;
                if (savedSubstitution != null)
                {
                    _substitution.RestoreFrom(savedSubstitution);
                }
            }
        }
        else if (whileLetExpr.Body != null)
        {
            AddError(whileLetExpr.Span, DiagnosticMessages.WhileLetExpressionMissingPattern);
            hasRecovery = true;
            _ = SafeInferExpression(whileLetExpr.Body);
        }
        else
        {
            AddError(whileLetExpr.Span, DiagnosticMessages.WhileLetExpressionMissingPattern);
            hasRecovery = true;
        }

        return hasRecovery
            ? CreateErrorRecoveryType()
            : BaseTypes.Unit;
    }

    /// <summary>
    /// 推断 match 表达式的类型
    /// </summary>
    private Type InferMatch(MatchExpr match)
    {
        Type? resultType = null;
        var scrutineeType = match.MatchedExpression != null
            ? SafeInferExpression(match.MatchedExpression)
            : CreateMissingScrutineeRecovery(match.Span, "Match");
        var hasRecovery = ContainsErrorRecoveryType(scrutineeType);
        if (match.Branches.Count == 0)
        {
            AddError(match.Span, DiagnosticMessages.MatchExpressionRequiresBranch);
            hasRecovery = true;
        }

        foreach (var branch in match.Branches)
        {
            var expectedBranchResultType = resultType ?? _substitution.FreshTypeVariable();
            var branchSignature = new TyFun
            {
                Params = [scrutineeType],
                Result = expectedBranchResultType
            };
            var branchType = InferPatternBranchWithLocalRefinementIfNeeded(branch, branchSignature);

            if (resultType == null)
            {
                resultType = branchType;
            }
            else
            {
                resultType = JoinControlFlowTypes(resultType, branchType, branch.Span, DiagnosticMessages.MatchBranchTypeMismatch);
            }

            hasRecovery |= ContainsErrorRecoveryType(branchType) ||
                           (resultType != null && ContainsErrorRecoveryType(resultType));
        }

        return hasRecovery
            ? CreateErrorRecoveryType()
            : resultType ?? BaseTypes.Unit;
    }

    private Type InferPatternBranchWithLocalRefinementIfNeeded(
        PatternBranch branch,
        Type branchSignature,
        bool consumeWholeParameterList = false)
    {
        return PatternHasGadtConstructor(branch.Pattern)
            ? InferPatternBranchWithLocalRefinement(branch, branchSignature, consumeWholeParameterList)
            : InferPatternBranch(branch, branchSignature, consumeWholeParameterList);
    }

    private Type InferPatternBranchWithLocalRefinement(
        PatternBranch branch,
        Type branchSignature,
        bool consumeWholeParameterList)
    {
        var outer = _substitution.Clone();
        var trial = _substitution.Clone();
        _substitution.RestoreFrom(trial);
        Type? branchType = null;
        try
        {
            branchType = InferPatternBranch(branch, branchSignature, consumeWholeParameterList);
        }
        finally
        {
            _substitution.RestoreFrom(outer);
        }

        return ContainsErrorRecoveryType(branchType)
            ? branchType
            : GetPatternBranchDeclaredResultType(branchSignature);
    }

    private Type GetPatternBranchDeclaredResultType(Type branchSignature)
    {
        var result = branchSignature is TyFun function
            ? GetRemainingBranchResultType(function)
            : branchSignature;

        return _substitution.Apply(result);
    }

    private bool PatternHasGadtConstructor(Pattern? pattern)
    {
        return pattern switch
        {
            CtorPattern ctor => TryGetCtorTypeBinding(ctor.SymbolId, ctor.ConstructorName, out var binding) &&
                                binding.ReturnType != null,
            TuplePattern tuple => tuple.Elements.Any(PatternHasGadtConstructor),
            AsPattern asPattern => PatternHasGadtConstructor(asPattern.InnerPattern),
            OrPattern orPattern => orPattern.Alternatives.Any(PatternHasGadtConstructor),
            AndPattern andPattern => andPattern.Conjuncts.Any(PatternHasGadtConstructor),
            NotPattern notPattern => PatternHasGadtConstructor(notPattern.InnerPattern),
            ViewPattern viewPattern => PatternHasGadtConstructor(viewPattern.InnerPattern),
            _ => false
        };
    }

    private Type CreateMissingScrutineeRecovery(SourceSpan span, string constructName)
    {
        AddError(span, DiagnosticMessages.ConstructExpressionMissingScrutinee(constructName));
        return CreateErrorRecoveryType();
    }

    /// <summary>
    /// 推断模式分支的类型
    /// </summary>
    private Type InferPatternBranch(PatternBranch branch, Type funcOrResultType, bool consumeWholeParameterList = false)
    {
        // 提取期望的参数类型和结果类型
        Type? expectedParamType = null;
        Type expectedResultType;

        if (funcOrResultType is TyFun funcType)
        {
            var paramTypes = CollectParamTypes(funcType);
            var (branchExpectedParamType, consumedParameterCount) = GetPatternBranchParameterExpectation(
                branch,
                paramTypes,
                consumeWholeParameterList);

            expectedParamType = branchExpectedParamType;

            expectedResultType = GetBranchResultType(funcType, consumedParameterCount);
        }
        else
        {
            expectedResultType = funcOrResultType;
        }

        var hasRecovery = false;

        // 为模式中的绑定创建类型，并与期望类型统一
        if (branch.Pattern != null)
        {
            var patternType = InferPattern(branch.Pattern, expectedParamType);
            hasRecovery |= ContainsErrorRecoveryType(patternType);
        }

        // 推断 guard
        if (branch.Guard != null)
        {
            var guardType = SafeInferExpression(branch.Guard);
            var guardResult = TryUnify(BaseTypes.Bool, guardType, branch.Guard.Span, DiagnosticMessages.PatternBranchGuardMustBeBool);
            hasRecovery |= ContainsErrorRecoveryType(guardResult);
        }

        // 推断表达式
        if (branch.Expression != null)
        {
            var exprType = InferExpressionWithExpectedType(branch.Expression, expectedResultType);
            if (branch.Expression != null && !TryInsertAutoDeref(expectedResultType, exprType, branch.Expression, branch.SetExpression))
            {
                var expressionResult = TryUnify(expectedResultType, exprType, branch.Expression.Span, DiagnosticMessages.PatternBranchResultTypeMismatch);
                hasRecovery |= ContainsErrorRecoveryType(expressionResult);
            }
        }
        else
        {
            AddError(branch.Span, DiagnosticMessages.PatternBranchRequiresBodyExpression);
            hasRecovery = true;
        }

        return hasRecovery
            ? CreateErrorRecoveryType()
            : _substitution.Apply(expectedResultType);
    }

    private static Type GetRemainingBranchResultType(TyFun funcType)
    {
        if (funcType.Params.Count > 1)
        {
            return new TyFun
            {
                Params = CopyParamsFrom(funcType.Params, 1),
                Result = funcType.Result,
                Effects = funcType.Effects
            };
        }

        return funcType.Result;
    }

    private static bool IsDirectFunctionBranch(PatternBranch branch)
    {
        return branch.Guard == null &&
               branch.Pattern != null &&
               IsIrrefutablePattern(branch.Pattern);
    }

    private static bool IsIrrefutablePattern(Pattern pattern)
    {
        return pattern switch
        {
            VarPattern => true,
            WildcardPattern => true,
            TuplePattern tuplePattern => tuplePattern.Elements.All(IsIrrefutablePattern),
            AsPattern asPattern => !string.IsNullOrWhiteSpace(asPattern.BindingName) &&
                                   asPattern.InnerPattern != null &&
                                   IsIrrefutablePattern(asPattern.InnerPattern),
            _ => false
        };
    }

    private Type InferPatternGuardExpr(PatternGuardExpr patternGuard)
    {
        var sourceType = patternGuard.SourceExpression != null
            ? SafeInferExpression(patternGuard.SourceExpression)
            : CreateMissingPatternGuardSourceRecovery(patternGuard.Span);
        var hasRecovery = ContainsErrorRecoveryType(sourceType);

        if (patternGuard.Pattern != null)
        {
            var patternType = InferPattern(patternGuard.Pattern, sourceType);
            var patternResult = TryUnify(patternType, sourceType, patternGuard.Pattern.Span, DiagnosticMessages.PatternGuardSourceTypeMismatch);
            hasRecovery |= ContainsErrorRecoveryType(patternType) || ContainsErrorRecoveryType(patternResult);
        }
        else
        {
            AddError(patternGuard.Span, DiagnosticMessages.PatternGuardRequiresPattern);
            hasRecovery = true;
        }

        return hasRecovery
            ? CreateErrorRecoveryType()
            : BaseTypes.Bool;
    }

    private Type CreateMissingPatternGuardSourceRecovery(SourceSpan span)
    {
        AddError(span, DiagnosticMessages.PatternGuardRequiresSourceExpression);
        return CreateErrorRecoveryType();
    }

    private Type InferSequentialGuardExpr(SequentialGuardExpr sequentialGuard)
    {
        var hasRecovery = false;

        foreach (var guard in sequentialGuard.Guards)
        {
            var guardType = SafeInferExpression(guard);
            var guardResult = TryUnify(BaseTypes.Bool, guardType, guard.Span, DiagnosticMessages.SequentialGuardMustBeBool);
            hasRecovery |= ContainsErrorRecoveryType(guardType) || ContainsErrorRecoveryType(guardResult);
        }

        return hasRecovery
            ? CreateErrorRecoveryType()
            : BaseTypes.Bool;
    }

    /// <summary>
    /// 获取函数类型的最终返回类型（处理 curried 函数）
    /// </summary>
    private Type GetFinalResult(TyFun funcType)
    {
        var result = _substitution.Apply(funcType.Result);
        while (result is TyFun nested)
        {
            result = _substitution.Apply(nested.Result);
        }

        return result;
    }

    /// <summary>
    /// <summary>
    /// Strips leading Unit parameters from a function type.
    /// Unit -> T becomes T, Unit -> Unit -> T becomes Unit -> T, etc.
    /// This is used for bodyless function declarations (@ffi, trait methods)
    /// where Unit params carry no meaningful argument.
    /// </summary>
    private static Type StripLeadingUnitParams(TyFun funcType)
    {
        if (funcType.Params.Count >= 1 &&
            funcType.Params[0] is TyCon { Name: WellKnownStrings.BuiltinTypes.Unit or "()" })
        {
            var remaining = new TyFun
            {
                Params = CopyParamsFrom(funcType.Params, 1),
                Result = funcType.Result,
                Effects = funcType.Effects
            };
            if (remaining.Params.Count >= 1)
            {
                return StripLeadingUnitParams(remaining);
            }
            // Return a 0-param callable, not the raw result type,
            // so the zero-arg call unifies correctly.
            return remaining;
        }
        return funcType;
    }

    /// <summary>
    /// 收集函数类型的所有参数类型（处理 curried 函数）
    /// </summary>
    private List<Type> CollectParamTypes(TyFun funcType)
    {
        var result = new List<Type>();
        Type current = _substitution.Apply(funcType);

        while (current is TyFun function)
        {
            foreach (var param in function.Params)
            {
                result.Add(_substitution.Apply(param));
            }
            current = _substitution.Apply(function.Result);
        }

        return result;
    }

    /// <summary>
    /// 推断 lambda 表达式的类型
    /// </summary>
    private Type InferLambda(LambdaExpr lambda, TyFun? expectedFunctionType = null)
    {
        var paramTypes = new List<Type>();
        var hasRecovery = false;
        var expectedParams = expectedFunctionType is { Params.Count: var expectedCount } &&
                             expectedCount >= lambda.Parameters.Count
            ? expectedFunctionType.Params
            : null;

        for (var i = 0; i < lambda.Parameters.Count; i++)
        {
            var expectedParamType = expectedParams != null
                ? _substitution.Apply(expectedParams[i])
                : null;
            var paramType = InferPattern(lambda.Parameters[i], expectedParamType);
            paramTypes.Add(paramType);
            hasRecovery |= ContainsErrorRecoveryType(paramType);
        }

        var resultType = expectedFunctionType != null
            ? GetExpectedLambdaResultType(expectedFunctionType, lambda.Parameters.Count)
            : _substitution.FreshTypeVariable();
        PushFunctionReturnType(resultType);

        try
        {
            if (lambda.Body != null)
            {
                var bodyType = InferExpressionWithExpectedType(lambda.Body, resultType);
                var bodyResult = TryUnify(resultType, bodyType, lambda.Body.Span, DiagnosticMessages.LambdaBodyResultTypeMismatch);
                hasRecovery |= ContainsErrorRecoveryType(bodyType) || ContainsErrorRecoveryType(bodyResult);
            }
            else
            {
                AddError(lambda.Span, DiagnosticMessages.LambdaExpressionRequiresBody);
                hasRecovery = true;
            }
        }
        finally
        {
            PopFunctionReturnType();
        }

        if (hasRecovery)
        {
            return CreateErrorRecoveryType();
        }

        return new TyFun
        {
            Params = paramTypes,
            Result = _substitution.Apply(resultType)
        };
    }

    private Type GetExpectedLambdaResultType(TyFun expectedFunctionType, int consumedParameterCount)
    {
        if (consumedParameterCount < expectedFunctionType.Params.Count)
        {
            return _substitution.Apply(new TyFun
            {
                Params = CopyParamsFrom(expectedFunctionType.Params, consumedParameterCount),
                Result = expectedFunctionType.Result,
                Effects = expectedFunctionType.Effects
            });
        }

        return _substitution.Apply(expectedFunctionType.Result);
    }

    private Type InferExpressionWithExpectedType(EidosAstNode expr, Type expectedType)
    {
        var resolvedExpected = _substitution.Apply(expectedType);
        if (expr is LambdaExpr lambda && resolvedExpected is TyFun expectedFunctionType)
        {
            var lambdaType = InferLambda(lambda, expectedFunctionType);
            var unifiedType = TryUnify(resolvedExpected, lambdaType, expr.Span, DiagnosticMessages.CallArgumentTypeMismatch);
            if (ContainsErrorRecoveryType(unifiedType))
            {
                var recovered = CreateErrorRecoveryType();
                lambda.InferredType = recovered;
                return recovered;
            }

            var appliedLambdaType = _substitution.Apply(lambdaType);
            lambda.InferredType = appliedLambdaType;
            return appliedLambdaType;
        }

        if (resolvedExpected is TyFun &&
            expr is IdentifierExpr { ValueCandidateSymbolIds.Count: > 0 } candidateIdentifier &&
            TryResolveExpectedCallableCandidate(
                candidateIdentifier.ValueCandidateSymbolIds,
                resolvedExpected,
                candidateIdentifier.Name,
                candidateIdentifier.Span,
                out var identifierCandidate))
        {
            candidateIdentifier.SymbolId = identifierCandidate;
            var selectedType = InferFunctionSymbolType(identifierCandidate, candidateIdentifier.Span);
            return TryUnify(resolvedExpected, selectedType, expr.Span, DiagnosticMessages.LetPatternTypeMismatch);
        }

        if (resolvedExpected is TyFun &&
            expr is PathExpr { ValueCandidateSymbolIds.Count: > 0 } candidatePath &&
            TryResolveExpectedCallableCandidate(
                candidatePath.ValueCandidateSymbolIds,
                resolvedExpected,
                candidatePath.Name,
                candidatePath.Span,
                out var pathCandidate))
        {
            candidatePath.SymbolId = pathCandidate;
            var selectedType = InferFunctionSymbolType(pathCandidate, candidatePath.Span);
            return TryUnify(resolvedExpected, selectedType, expr.Span, DiagnosticMessages.LetPatternTypeMismatch);
        }

        if (expr is ContextualRecordLiteralExpr contextualRecord)
        {
            return InferContextualRecordLiteral(contextualRecord, resolvedExpected);
        }

        return SafeInferExpression(expr);
    }

    private bool TryResolveExpectedCallableCandidate(
        IReadOnlyList<SymbolId> candidates,
        Type expectedType,
        string callableName,
        SourceSpan span,
        out SymbolId selectedCandidate)
    {
        selectedCandidate = SymbolId.None;
        var viableCandidates = new List<SymbolId>();
        var bestCandidates = new List<SymbolId>();
        var bestScore = int.MinValue;
        foreach (var candidate in candidates)
        {
            if (!TryScoreExpectedCallableCandidate(candidate, expectedType, out var score))
            {
                continue;
            }

            viableCandidates.Add(candidate);
            if (score > bestScore)
            {
                bestScore = score;
                bestCandidates.Clear();
                bestCandidates.Add(candidate);
            }
            else if (score == bestScore)
            {
                bestCandidates.Add(candidate);
            }
        }

        if (bestCandidates.Count == 1)
        {
            selectedCandidate = bestCandidates[0];
            return true;
        }

        if (bestCandidates.Count > 1)
        {
            var resolution = TypeDirectedCandidateResolution.Ambiguous(
                bestCandidates,
                candidates.Count,
                viableCandidates.Count,
                bestScore);
            AddStructuredErrorDiagnostic(
                CreateAmbiguousCallableDiagnostic(
                    span,
                    callableName,
                    "function-reference",
                    resolution,
                    [expectedType]),
                span);
            return false;
        }

        AddError(span, DiagnosticMessages.NoImportedOverloadAcceptsArgumentTypes(callableName));
        return false;
    }

    private bool TryScoreExpectedCallableCandidate(SymbolId candidate, Type expectedType, out int score)
    {
        score = int.MinValue;
        var trial = _substitution.Clone();
        if (!TryGetFunctionTypeForTrial(candidate, trial, out var functionType))
        {
            return false;
        }

        var candidateTypeBeforeUnify = trial.Apply(functionType);
        var expectedTypeBeforeUnify = trial.Apply(expectedType);
        var scoringTrial = trial.Clone();
        try
        {
            trial.Unify(candidateTypeBeforeUnify, expectedTypeBeforeUnify);
        }
        catch (TypeInferenceException)
        {
            return false;
        }

        score = ScoreCallableSpecificity(candidateTypeBeforeUnify, expectedTypeBeforeUnify, scoringTrial);
        return true;
    }

    private static int ScoreCallableSpecificity(Type candidateType, Type expectedType, Substitution trial)
    {
        var candidateParams = CollectCallableParamTypes(candidateType, trial);
        var expectedParams = CollectCallableParamTypes(expectedType, trial);
        var score = 0;
        var count = Math.Min(candidateParams.Count, expectedParams.Count);
        for (var i = 0; i < count; i++)
        {
            score += ScoreExpectedTypeMatch(candidateParams[i], expectedParams[i], trial) * 8;
        }

        score += ScoreExpectedTypeMatch(
            ResolveCallableReturnType(candidateType, trial),
            ResolveCallableReturnType(expectedType, trial),
            trial);
        return score;
    }

    private static List<Type> CollectCallableParamTypes(Type type, Substitution trial)
    {
        var result = new List<Type>();
        var current = trial.Apply(type);
        while (current is TyFun function)
        {
            result.AddRange(function.Params.Select(param => trial.Apply(param)));
            current = trial.Apply(function.Result);
        }

        return result;
    }

    private static Type ResolveCallableReturnType(Type type, Substitution trial)
    {
        var current = trial.Apply(type);
        while (current is TyFun function)
        {
            current = trial.Apply(function.Result);
        }

        return current;
    }

    private static int ScoreExpectedTypeMatch(Type candidateType, Type expectedType, Substitution trial)
    {
        var candidate = trial.Apply(candidateType);
        var expected = trial.Apply(expectedType);
        return (candidate, expected) switch
        {
            (TyVar, _) => 1,
            (TyCon left, TyCon right) when left.Symbol.IsValid && left.Symbol.Equals(right.Symbol) =>
                8 + ScoreTypeArguments(left.Args, right.Args, trial),
            (TyCon left, TyCon right) when string.Equals(left.Name, right.Name, StringComparison.Ordinal) =>
                6 + ScoreTypeArguments(left.Args, right.Args, trial),
            (TyFun left, TyFun right) => 4 + ScoreFunctionTypeShape(left, right, trial),
            (TyRef left, TyRef right) => 4 + ScoreExpectedTypeMatch(left.Inner, right.Inner, trial),
            (TyMutRef left, TyMutRef right) => 4 + ScoreExpectedTypeMatch(left.Inner, right.Inner, trial),
            _ => 2
        };
    }

    private static int ScoreTypeArguments(
        IReadOnlyList<Type> candidateArgs,
        IReadOnlyList<Type> expectedArgs,
        Substitution trial)
    {
        var score = 0;
        var count = Math.Min(candidateArgs.Count, expectedArgs.Count);
        for (var i = 0; i < count; i++)
        {
            score += ScoreExpectedTypeMatch(candidateArgs[i], expectedArgs[i], trial);
        }

        return score;
    }

    private static int ScoreFunctionTypeShape(TyFun candidate, TyFun expected, Substitution trial)
    {
        var score = 0;
        var count = Math.Min(candidate.Params.Count, expected.Params.Count);
        for (var i = 0; i < count; i++)
        {
            score += ScoreExpectedTypeMatch(candidate.Params[i], expected.Params[i], trial);
        }

        return score + ScoreExpectedTypeMatch(candidate.Result, expected.Result, trial);
    }

    private Type CreateLambdaShape(LambdaExpr lambda)
    {
        return new TyFun
        {
            Params = lambda.Parameters.Select(_ => (Type)_substitution.FreshTypeVariable()).ToList(),
            Result = _substitution.FreshTypeVariable()
        };
    }

    /// <summary>
    /// 推断函数调用的类型
    /// </summary>
    private Type InferCall(CallExpr call)
    {
        call.InferredEffects = null;

        if (call.Function is IdentifierExpr { Name: "cfn_from" } &&
            call.PositionalArgs.Count == 1)
        {
            var callbackType = SafeInferExpression(call.PositionalArgs[0]);
            if (InferNamedArgumentValues(call.NamedArgs))
            {
                return CreateErrorRecoveryType();
            }

            if (TryBuildCfnType(callbackType, out var cfnType))
            {
                return cfnType;
            }

            AddError(
                call.Span,
                DiagnosticMessages.CfnFromArgumentNotFunction,
                TypeErrorCode);
            return CreateErrorRecoveryType();
        }

        // cfn_call 特殊处理：接受可变参数（fn_ptr + N 个调用参数）
        if (call.Function is IdentifierExpr { Name: "cfn_call" } &&
            call.PositionalArgs.Count >= 1)
        {
            // 推断所有参数类型（用于副作用，如名称解析验证）
            var positionalArgTypes = new List<Type>(call.PositionalArgs.Count);
            foreach (var arg in call.PositionalArgs)
            {
                positionalArgTypes.Add(SafeInferExpression(arg));
            }

            if (InferNamedArgumentValues(call.NamedArgs))
            {
                return CreateErrorRecoveryType();
            }

            // 从第一个参数的 Cfn[A..., Ret] 类型提取返回类型
            var firstArgType = _substitution.Apply(positionalArgTypes[0]);
            if (firstArgType is TyCon { Name: WellKnownStrings.BuiltinTypes.Cfn, Args.Count: > 0 } cfnTy)
            {
                return _substitution.Apply(cfnTy.Args[^1]);
            }

            AddError(
                call.Span,
                DiagnosticMessages.CfnCallFirstArgumentNotCfn,
                TypeErrorCode);
            return CreateErrorRecoveryType();
        }

        if (!_allowComptimeFunctionReferences &&
            TryRejectRuntimeComptimeFunctionCall(call, out var rejectedResultType))
        {
            return rejectedResultType;
        }

        // 推断函数类型
        if (call.Function is IdentifierExpr { ValueCandidateSymbolIds.Count: > 0 } candidateIdentifier &&
            !candidateIdentifier.SymbolId.IsValid)
        {
            return InferCandidateIdentifierCall(call, candidateIdentifier);
        }

        if (call.Function is PathExpr { ValueCandidateSymbolIds.Count: > 0 } candidatePath &&
            !candidatePath.SymbolId.IsValid)
        {
            return InferCandidatePathCall(call, candidatePath);
        }

        if (call.Function is IdentifierExpr { SymbolId.IsValid: false, ValueCandidateSymbolIds.Count: 0 } unresolvedIdentifier &&
            IsLowerIdentifierName(unresolvedIdentifier.Name))
        {
            return InferUnresolvedIdentifierCall(call, unresolvedIdentifier);
        }

        Type funcType;
        if (call.Function != null)
        {
            funcType = SafeInferExpression(call.Function);
        }
        else
        {
            AddError(call.Span, DiagnosticMessages.CallExpressionMissingTarget);
            funcType = CreateErrorRecoveryType();
        }

        var argumentExprs = new List<EidosAstNode?>();
        var argSpans = new List<SourceSpan>();
        foreach (var arg in call.PositionalArgs)
        {
            argumentExprs.Add(arg);
            argSpans.Add(arg.Span);
        }

        foreach (var arg in call.NamedArgs)
        {
            AddNamedArgument(arg, argumentExprs, argSpans);
        }

        // 逐参数应用，统一支持：
        // - 柯里化函数（A -> B -> C）
        // - 多参数占位函数（由符号表参数个数推导出的 TyFun(params=[...], result=...)）
        Type currentType;
        if (argumentExprs.Count == 0)
        {
            if (!TryResolveEmptyCall(funcType, call.Function, _substitution, out var emptyResolution))
            {
                AddError(call.Span, DiagnosticMessages.CallTargetIsNotZeroArgumentFunction);
                return CreateErrorRecoveryType();
            }

            ApplyEmptyCallResolution(call, emptyResolution);
            AccumulateResolvedFunctionEffects(call, funcType);
            currentType = _substitution.Apply(emptyResolution.ResultType);
        }
        else
        {
            call.ClearEmptyCallResolution();
            currentType = _substitution.Apply(funcType);
            for (var i = 0; i < argumentExprs.Count; i++)
            {
                var argumentExpr = argumentExprs[i];
                var argSpan = i < argSpans.Count ? argSpans[i] : call.Span;
                var argType = InferCallArgument(currentType, argumentExpr, argSpan);

                // Auto-deref: wrap Ref[T]/MRef[T] args in synthetic deref when param expects non-ref
                if (argumentExpr != null && i < call.PositionalArgs.Count)
                {
                    var resolvedFunc = _substitution.Apply(currentType);
                    if (resolvedFunc is TyFun { Params.Count: > 0 } fn)
                    {
                        var resolvedParam = _substitution.Apply(fn.Params[0]);
                        var resolvedArg = _substitution.Apply(argType);

                        if (resolvedParam is not (TyRef or TyMutRef) &&
                            resolvedArg is TyRef or TyMutRef)
                        {
                            var innerType = resolvedArg switch
                            {
                                TyRef r => _substitution.Apply(r.Inner),
                                TyMutRef mr => _substitution.Apply(mr.Inner),
                                _ => resolvedArg
                            };

                            var syntheticDeref = new UnaryExpr();
                            syntheticDeref.SetOperator(UnaryOp.Deref);
                            syntheticDeref.SetOperand(call.PositionalArgs[i]);
                            syntheticDeref.SetSpan(call.PositionalArgs[i].Span);
                            syntheticDeref.InferredType = innerType;

                            call.PositionalArgs[i] = syntheticDeref;
                            argType = innerType;
                        }
                    }
                }

                currentType = ApplyCallArgument(call, currentType, argType, argSpan);
            }
        }

        ResolveAccumulatedCallEffects(call);
        ValidateResolvedValueGenericArguments(call.Function, call.Span);
        return _substitution.Apply(currentType);
    }

    private bool TryResolveEmptyCall(
        Type functionType,
        EidosAstNode? callee,
        Substitution substitution,
        out EmptyCallResolution resolution)
    {
        var resolvedType = substitution.Apply(functionType);
        if (resolvedType is not TyFun function)
        {
            resolution = default;
            return false;
        }

        if (function.Params.Count == 0)
        {
            resolution = new EmptyCallResolution(
                EmptyCallResolutionKind.ZeroArgument,
                substitution.Apply(function.Result),
                SynthesizedUnitArgumentCount: 0);
            return true;
        }

        var firstParam = substitution.Apply(function.Params[0]);
        if (!IsUnitType(firstParam))
        {
            resolution = default;
            return false;
        }

        var resultType = function.Params.Count == 1
            ? function.Result
            : new TyFun
            {
                Params = CopyParamsFrom(function.Params, 1),
                Result = function.Result,
                Effects = function.Effects
            };

        var kind = IsExternalFfiCallee(callee)
            ? EmptyCallResolutionKind.FfiUnitElision
            : EmptyCallResolutionKind.UnitSugar;
        resolution = new EmptyCallResolution(
            kind,
            substitution.Apply(resultType),
            kind == EmptyCallResolutionKind.UnitSugar ? 1 : 0);
        return true;
    }

    private static List<Type> CopyParamsFrom(IReadOnlyList<Type> parameters, int startIndex)
    {
        if (startIndex >= parameters.Count)
        {
            return [];
        }

        var result = new List<Type>(parameters.Count - startIndex);
        for (var i = startIndex; i < parameters.Count; i++)
        {
            result.Add(parameters[i]);
        }

        return result;
    }

    private static List<Type> CopyParamsFrom(IReadOnlyList<Type> parameters, int startIndex, int count)
    {
        if (count <= 0 || startIndex >= parameters.Count)
        {
            return [];
        }

        var endExclusive = Math.Min(parameters.Count, startIndex + count);
        var result = new List<Type>(endExclusive - startIndex);
        for (var i = startIndex; i < endExclusive; i++)
        {
            result.Add(parameters[i]);
        }

        return result;
    }

    /// <summary>
    /// 推断元组的类型
    /// </summary>
}
