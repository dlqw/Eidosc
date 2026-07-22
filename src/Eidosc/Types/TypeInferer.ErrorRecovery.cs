using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Utils;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    /// <summary>
    /// 添加错误诊断
    /// 抑制级联错误
    /// </summary>
    private void AddError(SourceSpan span, string message, string code = TypeErrorCode)
    {
        AddError(span, message, code, bypassLimit: false);
    }

    private void AddError(SourceSpan span, string message, string code, bool bypassLimit)
    {
        var dedupKey = $"{code}:{span.Location.Position}:{message}";
        if (!_reportedDiagnostics.Add(dedupKey))
        {
            return;
        }

        if (!bypassLimit && HasReachedTypeErrorLimit)
        {
            SuppressedTypeDiagnosticCount++;
            EnsureTypeErrorLimitReported(span);
            return;
        }

        var diag = new EidoscDiagnostic(EidoscDiagnosticLevel.Error, message, code);
        diag.WithLabel(span, message);
        _diagnostics.Add(diag);
        if (!bypassLimit)
        {
            _recoveryContext.RecordError();
        }
    }

    private void EnsureTypeErrorLimitReported(SourceSpan span)
    {
        var reason = DiagnosticMessages.TooManyTypeErrors(_recoveryContext.MaxErrors);
        MarkTypeAnalysisIncomplete(reason);
        if (_typeErrorLimitDiagnosticReported)
        {
            return;
        }

        _typeErrorLimitDiagnosticReported = true;
        AddError(span, reason, TypeErrorCode, bypassLimit: true);
    }

    private void AddStructuredErrorDiagnostic(EidoscDiagnostic diagnostic, SourceSpan span)
    {
        if (HasReachedTypeErrorLimit)
        {
            SuppressedTypeDiagnosticCount++;
            EnsureTypeErrorLimitReported(span);
            return;
        }

        _diagnostics.Add(diagnostic);
        _recoveryContext.RecordError();
    }

    private Type CreateMissingShapeRecoveryType(SourceSpan span, string message)
    {
        AddError(span, message);
        return CreateErrorRecoveryType();
    }

    private bool ReportMissingShape(SourceSpan span, string message)
    {
        AddError(span, message);
        return true;
    }

    /// <summary>
    /// 尝试统一类型，失败时记录错误并返回错误类型
    /// </summary>
    private Type TryUnify(Type expected, Type actual, SourceSpan span, string context = "", string errorCode = TypeErrorCode)
    {
        if (ContainsErrorRecoveryType(expected) || ContainsErrorRecoveryType(actual))
        {
            return CreateErrorRecoveryType();
        }

        try
        {
            var hasCaseInjection = TryDescribeClosedCaseInjection(expected, actual, out var caseInjection);
            UnifyExpectedType(expected, actual);
            if (!hasCaseInjection)
            {
                hasCaseInjection = TryDescribeClosedCaseInjection(expected, actual, out caseInjection);
            }
            if (hasCaseInjection)
            {
                _closedCaseInjections[span] = caseInjection;
            }
            _recoveryContext.RecordSuccess();
            return _substitution.Apply(expected);
        }
        catch (TypeInferenceException ex)
        {
            // 抑制级联错误 - 检查是否已经报告过相关错误
            if (IsCascadingError(expected, actual))
            {
                return CreateErrorRecoveryType();
            }

            var message = string.IsNullOrEmpty(context)
                ? ex.Message
                : $"{context}: {ex.Message}";

            AddError(span, message, errorCode);
            return CreateErrorRecoveryType();
        }
    }

    private void UnifyExpectedType(Type expected, Type actual)
    {
        var resolvedExpected = ExpandInferredTypeAliases(expected);
        expected = resolvedExpected;
        actual = ExpandInferredTypeAliases(actual);

        if (expected is TyVar { IsGenericInstantiation: true } &&
            actual is TyCon inferredConstructor &&
            TryPromoteClosedCaseToRoot(inferredConstructor, out var inferredRoot))
        {
            actual = inferredRoot;
        }

        if (expected is TyCon
            {
                ConstructorVarIndex: not null,
                IsGenericInstantiationConstructor: true
            } &&
            actual is TyCon)
        {
            actual = NormalizeClosedCaseArgumentForExpectedType(expected, actual, _substitution);
        }

        if (TryUnifyReadableReferenceCompatibility(expected, actual))
        {
            return;
        }

        if (TryUnifyFfiPointerCompatibility(expected, actual))
        {
            return;
        }

        if (TryUnifyAutoDeref(expected, actual))
        {
            return;
        }

        if (TryUnifyClosedCaseInjection(expected, actual))
        {
            return;
        }

        if (TryUnifyExpectedFunctionType(expected, actual))
        {
            return;
        }

        if (TryUnifyExpectedTypeStructure(expected, actual))
        {
            return;
        }

        _substitution.Unify(expected, actual);
    }

    private bool TryUnifyExpectedFunctionType(Type expected, Type actual)
    {
        var resolvedExpected = ExpandInferredTypeAliases(expected);
        var resolvedActual = ExpandInferredTypeAliases(actual);
        if (resolvedExpected is not TyFun expectedFunction ||
            resolvedActual is not TyFun actualFunction)
        {
            return false;
        }

        _substitution.Unify(expectedFunction, actualFunction);
        return true;
    }

    private bool TryUnifyExpectedTypeStructure(Type expected, Type actual)
    {
        var resolvedExpected = _substitution.Apply(expected);
        var resolvedActual = _substitution.Apply(actual);
        if (resolvedExpected is TyCon expectedConstructor &&
            resolvedActual is TyCon actualConstructor &&
            IsMetaSequenceDomain(expectedConstructor, actualConstructor))
        {
            return true;
        }

        if (resolvedExpected is TyCon expectedCon &&
            resolvedActual is TyCon actualCon &&
            expectedCon.Args.Count == actualCon.Args.Count &&
            HaveSameTypeConstructorIdentity(expectedCon, actualCon))
        {
            var unifiedArguments = new List<Type>(expectedCon.Args.Count);
            for (var index = 0; index < expectedCon.Args.Count; index++)
            {
                _substitution.Unify(expectedCon.Args[index], actualCon.Args[index]);
                unifiedArguments.Add(_substitution.Apply(expectedCon.Args[index]));
            }

            _substitution.Unify(
                expectedCon with { Args = unifiedArguments },
                actualCon with { Args = unifiedArguments });
            return true;
        }

        if (resolvedExpected is TyTuple expectedTuple &&
            resolvedActual is TyTuple actualTuple &&
            expectedTuple.Elements.Count == actualTuple.Elements.Count)
        {
            var unifiedElements = new List<Type>(expectedTuple.Elements.Count);
            for (var index = 0; index < expectedTuple.Elements.Count; index++)
            {
                UnifyExpectedType(expectedTuple.Elements[index], actualTuple.Elements[index]);
                unifiedElements.Add(_substitution.Apply(expectedTuple.Elements[index]));
            }

            _substitution.Unify(
                expectedTuple with { Elements = unifiedElements },
                actualTuple with { Elements = unifiedElements });
            return true;
        }

        return false;
    }

    private static bool IsMetaSequenceDomain(TyCon left, TyCon right) =>
        ((left.Id == new TypeId(WellKnownTypeIds.MetaItemsId) ||
          left.Id == new TypeId(WellKnownTypeIds.MetaModulesId) ||
          string.Equals(left.Name, WellKnownStrings.Meta.Types.Items, StringComparison.Ordinal) ||
          string.Equals(left.Name, WellKnownStrings.Meta.Types.Modules, StringComparison.Ordinal)) &&
         string.Equals(right.Name, WellKnownStrings.BuiltinTypes.Seq, StringComparison.Ordinal)) ||
        ((right.Id == new TypeId(WellKnownTypeIds.MetaItemsId) ||
          right.Id == new TypeId(WellKnownTypeIds.MetaModulesId) ||
          string.Equals(right.Name, WellKnownStrings.Meta.Types.Items, StringComparison.Ordinal) ||
          string.Equals(right.Name, WellKnownStrings.Meta.Types.Modules, StringComparison.Ordinal)) &&
         string.Equals(left.Name, WellKnownStrings.BuiltinTypes.Seq, StringComparison.Ordinal));

    private static bool HaveSameTypeConstructorIdentity(TyCon left, TyCon right)
    {
        if (left.Symbol.IsValid && right.Symbol.IsValid)
        {
            return left.Symbol == right.Symbol;
        }

        if (left.Id.IsValid && right.Id.IsValid)
        {
            return left.Id == right.Id;
        }

        if (left.ConstructorVarIndex.HasValue || right.ConstructorVarIndex.HasValue)
        {
            return left.ConstructorVarIndex == right.ConstructorVarIndex;
        }

        return string.Equals(left.Name, right.Name, StringComparison.Ordinal);
    }

    private bool TryUnifyClosedCaseInjection(Type expected, Type actual)
    {
        if (!TryDescribeClosedCaseInjection(expected, actual, out _, out var expectedTyCon, out var actualTyCon))
        {
            return false;
        }

        if (!TryProjectClosedCaseToAncestor(actualTyCon, expectedTyCon.Symbol, out var projected))
        {
            return false;
        }

        return TryUnifyExpectedTypeStructure(expectedTyCon, projected);
    }

    private bool TryDescribeClosedCaseInjection(
        Type expected,
        Type actual,
        out ClosedCaseInjectionFact injection)
    {
        if (!TryDescribeClosedCaseInjection(expected, actual, out injection, out _, out _))
        {
            injection = null!;
            return false;
        }
        return true;
    }

    private bool TryDescribeClosedCaseInjection(
        Type expected,
        Type actual,
        out ClosedCaseInjectionFact injection,
        out TyCon expectedTyCon,
        out TyCon actualTyCon)
    {
        var resolvedExpected = ExpandInferredTypeAliases(expected);
        var resolvedActual = ExpandInferredTypeAliases(actual);
        if (resolvedExpected is not TyCon expectedConstructor ||
            resolvedActual is not TyCon actualConstructor ||
            !TryResolveClosedCaseTypeSymbol(expectedConstructor, out var expectedSymbol) ||
            !TryResolveClosedCaseTypeSymbol(actualConstructor, out var actualSymbol) ||
            !_symbolTable.IsClosedCaseSubtype(actualSymbol, expectedSymbol) ||
            actualSymbol == expectedSymbol)
        {
            injection = null!;
            expectedTyCon = null!;
            actualTyCon = null!;
            return false;
        }

        expectedTyCon = expectedConstructor with
        {
            Symbol = expectedSymbol,
            Id = expectedConstructor.Id.IsValid
                ? expectedConstructor.Id
                : _symbolTable.GetSymbol(expectedSymbol)?.TypeId ?? TypeId.None
        };
        actualTyCon = actualConstructor with
        {
            Symbol = actualSymbol,
            Id = actualConstructor.Id.IsValid
                ? actualConstructor.Id
                : _symbolTable.GetSymbol(actualSymbol)?.TypeId ?? TypeId.None
        };
        injection = new ClosedCaseInjectionFact(
            actualSymbol,
            expectedSymbol,
            actualTyCon,
            expectedTyCon);
        return true;
    }

    private bool TryResolveClosedCaseTypeSymbol(TyCon type, out SymbolId symbol)
    {
        if (type.Symbol.IsValid)
        {
            if (_symbolTable.GetSymbol<AdtSymbol>(type.Symbol) != null)
            {
                symbol = type.Symbol;
                return true;
            }

            if (_symbolTable.GetSymbol<CtorSymbol>(type.Symbol) is { OwnerAdt.IsValid: true } constructor)
            {
                symbol = constructor.OwnerAdt;
                return true;
            }
        }

        if (type.Id.IsValid && _symbolTable.GetSymbolByTypeId(type.Id) is { Id.IsValid: true } byTypeId)
        {
            if (byTypeId is AdtSymbol)
            {
                symbol = byTypeId.Id;
                return true;
            }

            if (byTypeId is CtorSymbol { OwnerAdt.IsValid: true } constructor)
            {
                symbol = constructor.OwnerAdt;
                return true;
            }
        }

        symbol = SymbolId.None;
        return false;
    }

    private bool TryPromoteClosedCaseToRoot(TyCon type, out TyCon promoted)
    {
        promoted = type;
        if (!TryResolveClosedCaseTypeSymbol(type, out var caseId) ||
            _symbolTable.GetSymbol<AdtSymbol>(caseId) is not { IsCaseType: true } caseSymbol)
        {
            return false;
        }

        var rootId = caseSymbol.ParentAdt;
        while (_symbolTable.GetSymbol<AdtSymbol>(rootId) is { IsCaseType: true } parentCase)
        {
            rootId = parentCase.ParentAdt;
        }

        if (_symbolTable.GetSymbol<AdtSymbol>(rootId) is not { })
        {
            return false;
        }

        return TryProjectClosedCaseToAncestor(
            type with
            {
                Symbol = caseId,
                Id = type.Id.IsValid ? type.Id : caseSymbol.TypeId
            },
            rootId,
            out promoted);
    }

    private bool TryProjectClosedCaseToAncestor(
        TyCon source,
        SymbolId ancestorId,
        out TyCon projected)
    {
        projected = null!;
        if (!_symbolTable.IsClosedCaseSubtype(source.Symbol, ancestorId) ||
            _symbolTable.GetSymbol<AdtSymbol>(ancestorId) is not { } ancestor)
        {
            return false;
        }

        var parameterIds = _symbolTable.GetClosedCaseEffectiveGenericParameterIds(ancestorId);
        var typeParameterCount = parameterIds.Count(parameterId =>
            _symbolTable.GetSymbol<TypeParamSymbol>(parameterId)?.ParameterKind == GenericParameterKind.Type);
        if (source.Args.Count < typeParameterCount)
        {
            return false;
        }

        projected = source with
        {
            Name = ancestor.IsCaseType ? GetQualifiedCaseTypeName(ancestorId) : ancestor.Name,
            Symbol = ancestorId,
            Id = ancestor.TypeId,
            Args = source.Args.Take(typeParameterCount).ToList(),
            ValueArgs = source.ValueArgs
                .Where(argument => argument.ParameterIndex >= 0 && argument.ParameterIndex < parameterIds.Count)
                .ToList(),
            EffectArgs = source.EffectArgs
                .Where(argument => argument.ParameterIndex >= 0 && argument.ParameterIndex < parameterIds.Count)
                .ToList(),
            ConstructorVarIndex = null,
            IsGenericInstantiationConstructor = false
        };
        return true;
    }

    private bool TryUnifyFfiPointerCompatibility(Type expected, Type actual)
    {
        var resolvedExpected = _substitution.Apply(expected);
        var resolvedActual = _substitution.Apply(actual);

        return IsFfiPointerCompatible(resolvedExpected, resolvedActual) ||
               IsFfiPointerCompatible(resolvedActual, resolvedExpected);
    }

    private static bool IsFfiPointerCompatible(Type left, Type right)
    {
        return left is TyCon { Name: WellKnownStrings.BuiltinTypes.RawPtr } &&
               right is TyCon { Name: WellKnownStrings.BuiltinTypes.Cfn };
    }

    private bool TryUnifyReadableReferenceCompatibility(Type expected, Type actual)
    {
        var resolvedExpected = _substitution.Apply(expected);
        var resolvedActual = _substitution.Apply(actual);

        if (resolvedExpected is not TyRef expectedRef ||
            resolvedActual is not TyMutRef actualMutRef)
        {
            return false;
        }

        _substitution.Unify(expectedRef.Inner, actualMutRef.Inner);
        return true;
    }

    private bool TryUnifyAutoDeref(Type expected, Type actual)
    {
        var resolvedExpected = _substitution.Apply(expected);
        var resolvedActual = _substitution.Apply(actual);

        if (resolvedExpected is TyVar or TyRef or TyMutRef)
            return false;

        var innerType = resolvedActual switch
        {
            TyRef r => _substitution.Apply(r.Inner),
            TyMutRef mr => _substitution.Apply(mr.Inner),
            _ => null as Type
        };

        if (innerType == null)
            return false;

        _substitution.Unify(expected, innerType);
        return true;
    }

    private bool TryInsertAutoDeref(Type expected, Type actual, EidosAstNode expr, Action<EidosAstNode> replace)
    {
        var resolvedExpected = _substitution.Apply(expected);
        var resolvedActual = _substitution.Apply(actual);

        if (resolvedExpected is TyVar or TyRef or TyMutRef)
            return false;

        var innerType = resolvedActual switch
        {
            TyRef r => _substitution.Apply(r.Inner),
            TyMutRef mr => _substitution.Apply(mr.Inner),
            _ => null as Type
        };

        if (innerType == null)
            return false;

        try
        {
            UnifyExpectedType(expected, innerType);
        }
        catch (TypeInferenceException)
        {
            return false;
        }

        var syntheticDeref = new UnaryExpr();
        syntheticDeref.SetOperator(UnaryOp.Deref);
        syntheticDeref.SetOperand(expr);
        syntheticDeref.SetSpan(expr.Span);
        syntheticDeref.InferredType = innerType;

        replace(syntheticDeref);
        return true;
    }

    /// <summary>
    /// 检查是否是级联错误
    /// </summary>
    private bool IsCascadingError(Type expected, Type actual)
    {
        // 如果任一类型是已报告错误的类型变量，则认为是级联错误
        if (expected is TyVar expectedVar && _reportedErrorVars.Contains(expectedVar.Index))
        {
            return true;
        }

        if (actual is TyVar actualVar && _reportedErrorVars.Contains(actualVar.Index))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 标记类型变量为错误类型
    /// </summary>
    private void MarkAsErrorType(Type type)
    {
        if (type is TyVar var)
        {
            var.IsErrorRecovery = true;
            _reportedErrorVars.Add(var.Index);
        }
    }

    private void MarkTypeAnalysisIncomplete(string reason)
    {
        TypeAnalysisIncomplete = true;
        TypeAnalysisIncompleteReason = string.IsNullOrWhiteSpace(TypeAnalysisIncompleteReason)
            ? reason
            : TypeAnalysisIncompleteReason;
    }

    private static bool ContainsErrorRecoveryType(Type type)
    {
        return type switch
        {
            TyVar { IsErrorRecovery: true } => true,
            TyVar { Instance: not null } variable => ContainsErrorRecoveryType(variable.Instance),
            TyVar => false,
            TyCon con => con.Args.Any(ContainsErrorRecoveryType),
            TyFun fun => fun.Params.Any(ContainsErrorRecoveryType) ||
                         ContainsErrorRecoveryType(fun.Result) ||
                         ContainsErrorRecoveryType(fun.Effects),
            TyTuple tuple => tuple.Elements.Any(ContainsErrorRecoveryType),
            TyRef reference => ContainsErrorRecoveryType(reference.Inner),
            TyMutRef reference => ContainsErrorRecoveryType(reference.Inner),
            TyShared shared => ContainsErrorRecoveryType(shared.Inner),
            TyReflProof reflProof => reflProof.WitnessType != null && ContainsErrorRecoveryType(reflProof.WitnessType),
            EffectRow abilitySet => abilitySet.Effects.Any(ContainsErrorRecoveryType),
            EffectTag abilityType => abilityType.TypeArgs.Any(ContainsErrorRecoveryType),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private Type CreateErrorRecoveryType()
    {
        var errorType = _substitution.FreshTypeVariable();
        MarkAsErrorType(errorType);
        return errorType;
    }

    /// <summary>
    /// 安全地推断表达式类型，捕获异常并返回错误类型
    /// </summary>
    private Type SafeInferExpression(EidosAstNode expr)
    {
        try
        {
            return InferExpression(expr);
        }
        catch (TypeInferenceException ex)
        {
            AddError(expr.Span, ex.Message);

            // 返回一个新鲜类型变量，避免级联错误
            return CreateErrorRecoveryType();
        }
    }

}
