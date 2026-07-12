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
            UnifyExpectedType(expected, actual);
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

        _substitution.Unify(expected, actual);
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

        UnifyExpectedType(expectedRef.Inner, actualMutRef.Inner);
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
