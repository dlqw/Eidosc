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
    private Type InferTuple(TupleExpr tuple)
    {
        // Empty tuple () is the Unit value
        if (tuple.Elements.Count == 0)
        {
            return BaseTypes.Unit;
        }

        var elementTypes = new List<Type>();
        var hasRecovery = false;

        foreach (var elem in tuple.Elements)
        {
            var elementType = SafeInferExpression(elem);
            if (_substitution.Apply(elementType) is TyCon elementConstructor &&
                TryPromoteClosedCaseToRoot(elementConstructor, out var promotedElement))
            {
                RecordClosedCaseInjection(elementConstructor, promotedElement, elem.Span);
                elementType = promotedElement;
            }
            elementTypes.Add(elementType);
            hasRecovery |= ContainsErrorRecoveryType(elementType);
        }

        return hasRecovery
            ? CreateErrorRecoveryType()
            : new TyTuple { Elements = elementTypes };
    }

    /// <summary>
    /// 推断列表的类型
    /// </summary>
    private Type InferList(ListExpr list)
    {
        if (list.Elements.Count == 0)
        {
            // 空列表：List<'a>
            var elemType = _substitution.FreshTypeVariable();
            return new TyCon { Name = WellKnownStrings.BuiltinTypes.Seq, Args = [elemType] };
        }

        if (list.HasRest)
        {
            return InferRestList(list);
        }

        // 推断第一个元素的类型
        var firstType = SafeInferExpression(list.Elements[0]);
        if (_substitution.Apply(firstType) is TyCon firstConstructor &&
            TryPromoteClosedCaseToRoot(firstConstructor, out var promotedFirst))
        {
            RecordClosedCaseInjection(firstConstructor, promotedFirst, list.Elements[0].Span);
            firstType = promotedFirst;
        }
        var hasRecovery = ContainsErrorRecoveryType(firstType);

        var elementSpans = new List<SourceSpan> { list.Elements[0].Span };
        foreach (var elem in list.Elements.Skip(1))
        {
            var elemType = SafeInferExpression(elem);
            firstType = JoinControlFlowTypes(
                firstType,
                elemType,
                elem.Span,
                DiagnosticMessages.ListElementTypeMismatch,
                elementSpans,
                elem.Span);
            elementSpans.Add(elem.Span);
            hasRecovery |= ContainsErrorRecoveryType(elemType) || ContainsErrorRecoveryType(firstType);
        }

        return hasRecovery
            ? CreateErrorRecoveryType()
            : new TyCon { Name = WellKnownStrings.BuiltinTypes.Seq, Args = [firstType] };
    }

    private Type InferListWithExpectedType(ListExpr list, TyCon expectedListType)
    {
        var expectedElementType = expectedListType.Args[0];
        var prefixCount = list.HasRest
            ? Math.Max(0, list.Elements.Count - 1)
            : list.Elements.Count;
        var hasRecovery = false;

        for (var index = 0; index < prefixCount; index++)
        {
            var element = list.Elements[index];
            var actualElementType = InferExpressionWithExpectedType(element, expectedElementType);
            var unifiedElementType = TryUnify(
                expectedElementType,
                actualElementType,
                element.Span,
                DiagnosticMessages.ListElementTypeMismatch);
            hasRecovery |= ContainsErrorRecoveryType(actualElementType) ||
                           ContainsErrorRecoveryType(unifiedElementType);
        }

        if (list.HasRest && list.Elements.Count > 0)
        {
            var rest = list.Elements[^1];
            var actualRestType = InferExpressionWithExpectedType(rest, expectedListType);
            var unifiedRestType = TryUnify(
                expectedListType,
                actualRestType,
                rest.Span,
                DiagnosticMessages.ListRestExpressionTypeMismatch);
            hasRecovery |= ContainsErrorRecoveryType(actualRestType) ||
                           ContainsErrorRecoveryType(unifiedRestType);
        }

        if (hasRecovery)
        {
            var recovery = CreateErrorRecoveryType();
            list.InferredType = recovery;
            return recovery;
        }

        var resolvedListType = _substitution.Apply(expectedListType);
        list.InferredType = resolvedListType;
        return resolvedListType;
    }

    private Type InferRestList(ListExpr list)
    {
        if (list.Elements.Count == 0)
        {
            var elemType = _substitution.FreshTypeVariable();
            return new TyCon { Name = WellKnownStrings.BuiltinTypes.Seq, Args = [elemType] };
        }

        var prefixCount = list.Elements.Count - 1;
        Type elementType = _substitution.FreshTypeVariable();
        var hasRecovery = false;
        var prefixElementSpans = new List<SourceSpan>();

        if (prefixCount > 0)
        {
            elementType = SafeInferExpression(list.Elements[0]);
            if (_substitution.Apply(elementType) is TyCon firstConstructor &&
                TryPromoteClosedCaseToRoot(firstConstructor, out var promotedFirst))
            {
                RecordClosedCaseInjection(firstConstructor, promotedFirst, list.Elements[0].Span);
                elementType = promotedFirst;
            }
            hasRecovery |= ContainsErrorRecoveryType(elementType);
            prefixElementSpans.Add(list.Elements[0].Span);
            foreach (var elem in list.Elements.Skip(1).Take(prefixCount - 1))
            {
                var elemType = SafeInferExpression(elem);
                elementType = JoinControlFlowTypes(
                    elementType,
                    elemType,
                    elem.Span,
                    DiagnosticMessages.ListPrefixElementTypeMismatch,
                    prefixElementSpans,
                    elem.Span);
                prefixElementSpans.Add(elem.Span);
                hasRecovery |= ContainsErrorRecoveryType(elemType) || ContainsErrorRecoveryType(elementType);
            }
        }

        var restType = SafeInferExpression(list.Elements[^1]);
        hasRecovery |= ContainsErrorRecoveryType(restType);
        var resolvedRestType = _substitution.Apply(restType);
        if (prefixCount > 0 &&
            resolvedRestType is TyCon { Name: WellKnownStrings.BuiltinTypes.Seq, Args.Count: 1 } restSequence)
        {
            elementType = JoinControlFlowTypes(
                elementType,
                restSequence.Args[0],
                list.Elements[^1].Span,
                DiagnosticMessages.ListRestExpressionTypeMismatch,
                prefixElementSpans);
            hasRecovery |= ContainsErrorRecoveryType(elementType);
        }

        var expectedRestType = new TyCon
        {
            Name = WellKnownStrings.BuiltinTypes.Seq,
            Args = [elementType]
        };
        var restResult = TryUnify(expectedRestType, restType, list.Elements[^1].Span, DiagnosticMessages.ListRestExpressionTypeMismatch);
        hasRecovery |= ContainsErrorRecoveryType(restResult);

        return hasRecovery
            ? CreateErrorRecoveryType()
            : _substitution.Apply(expectedRestType);
    }

    /// <summary>
    /// 推断 return 表达式的类型
    /// </summary>
    private Type InferReturn(ReturnExpr ret)
    {
        var invalidReturnContext = !TryGetCurrentFunctionReturnType(out var expectedReturnType);
        var hasRecovery = invalidReturnContext;
        if (invalidReturnContext)
        {
            AddError(ret.Span, DiagnosticMessages.ReturnExpressionOutsideFunction);
            expectedReturnType = CreateErrorRecoveryType();
        }

        if (ret.Value != null)
        {
            var valueType = SafeInferExpression(ret.Value);
            if (ret.Value != null && !TryInsertAutoDeref(expectedReturnType, valueType, ret.Value, ret.SetValue))
            {
                var returnResult = TryUnify(expectedReturnType, valueType, ret.Value.Span, DiagnosticMessages.ReturnValueTypeMismatch);
                hasRecovery |= ContainsErrorRecoveryType(returnResult);
            }
        }
        else
        {
            var returnResult = TryUnify(expectedReturnType, BaseTypes.Unit, ret.Span, DiagnosticMessages.ReturnValueTypeMismatch);
            hasRecovery |= ContainsErrorRecoveryType(returnResult);
        }

        // return 会提前终止控制流，使用新类型变量避免与后续表达式形状冲突
        if (hasRecovery)
        {
            return CreateErrorRecoveryType();
        }

        return BaseTypes.Never;
    }

    private Type JoinControlFlowTypes(
        Type left,
        Type right,
        SourceSpan span,
        string context,
        IReadOnlyList<SourceSpan>? leftExpressionSpans = null,
        SourceSpan? rightExpressionSpan = null)
    {
        var resolvedLeft = _substitution.Apply(left);
        var resolvedRight = _substitution.Apply(right);

        if (BaseTypes.IsNever(resolvedLeft))
        {
            return resolvedRight;
        }

        if (BaseTypes.IsNever(resolvedRight))
        {
            return resolvedLeft;
        }

        if (TryJoinClosedCaseTypes(resolvedLeft, resolvedRight, out var closedJoin))
        {
            foreach (var leftSpan in leftExpressionSpans ?? [])
            {
                RecordClosedCaseInjection(resolvedLeft, closedJoin, leftSpan);
            }
            if (rightExpressionSpan is { } rightSpan)
            {
                RecordClosedCaseInjection(resolvedRight, closedJoin, rightSpan);
            }
            return closedJoin;
        }

        return TryUnify(resolvedLeft, resolvedRight, span, context);
    }

    private bool TryJoinClosedCaseTypes(Type left, Type right, out TyCon join)
    {
        join = null!;
        if (left is not TyCon leftConstructor ||
            right is not TyCon rightConstructor)
        {
            return false;
        }

        var hasLeftSymbol = TryResolveClosedCaseTypeSymbol(leftConstructor, out var leftSymbol);
        var hasRightSymbol = TryResolveClosedCaseTypeSymbol(rightConstructor, out var rightSymbol);
        if (!hasLeftSymbol || !hasRightSymbol)
        {
            return false;
        }

        if (_symbolTable.GetSymbol<AdtSymbol>(leftSymbol) is not { } leftAdt ||
            _symbolTable.GetSymbol<AdtSymbol>(rightSymbol) is not { } rightAdt ||
            !leftAdt.IsCaseType && !rightAdt.IsCaseType)
        {
            return false;
        }

        var common = _symbolTable.FindNearestClosedCommonAncestor(leftSymbol, rightSymbol);
        if (!common.IsValid || _symbolTable.GetSymbol<AdtSymbol>(common) is not { } commonSymbol)
        {
            return false;
        }

        if (!TryProjectClosedCaseToAncestor(leftConstructor, common, out var projectedLeft) ||
            !TryProjectClosedCaseToAncestor(rightConstructor, common, out var projectedRight))
        {
            return false;
        }

        try
        {
            if (projectedLeft.Args.Count != projectedRight.Args.Count)
            {
                return false;
            }

            var joinedArguments = new List<Type>(projectedLeft.Args.Count);
            for (var index = 0; index < projectedLeft.Args.Count; index++)
            {
                var leftArgument = _substitution.Apply(projectedLeft.Args[index]);
                var rightArgument = _substitution.Apply(projectedRight.Args[index]);
                if (TryJoinClosedCaseTypes(leftArgument, rightArgument, out var argumentJoin))
                {
                    joinedArguments.Add(argumentJoin);
                    continue;
                }

                _substitution.Unify(leftArgument, rightArgument);
                joinedArguments.Add(_substitution.Apply(leftArgument));
            }

            projectedLeft = projectedLeft with { Args = joinedArguments };
            projectedRight = projectedRight with { Args = joinedArguments };
            _substitution.Unify(projectedLeft, projectedRight);
        }
        catch (TypeInferenceException)
        {
            return false;
        }

        join = (TyCon)_substitution.Apply(projectedLeft);
        return true;
    }

    private void RecordClosedCaseInjection(Type source, TyCon target, SourceSpan span)
    {
        var resolved = _substitution.Apply(source);
        if (resolved is not TyCon sourceConstructor ||
            !TryResolveClosedCaseTypeSymbol(sourceConstructor, out var sourceSymbol) ||
            sourceSymbol == target.Symbol ||
            !_symbolTable.IsClosedCaseSubtype(sourceSymbol, target.Symbol))
        {
            return;
        }

        _closedCaseInjections[span] = new ClosedCaseInjectionFact(
            sourceSymbol,
            target.Symbol,
            sourceConstructor with
            {
                Symbol = sourceSymbol,
                Id = sourceConstructor.Id.IsValid
                    ? sourceConstructor.Id
                    : _symbolTable.GetSymbol(sourceSymbol)?.TypeId ?? TypeId.None
            },
            target);
    }

    private Type InferUnsupportedExpression(EidosAstNode expr)
    {
        AddError(expr.Span, DiagnosticMessages.UnsupportedExpressionKind(expr.GetType().Name));
        return CreateErrorRecoveryType();
    }

    private Type InferUnsupportedLiteral(LiteralExpr lit)
    {
        AddError(lit.Span, DiagnosticMessages.UnsupportedLiteralKind(lit.Kind));
        return CreateErrorRecoveryType();
    }

    /// <summary>
    /// 推断 break 表达式的类型
    /// </summary>
    private Type InferBreak(BreakExpr breakExpr)
    {
        var hasRecovery = _loopDepth <= 0;
        if (breakExpr.Value != null)
        {
            var valueType = SafeInferExpression(breakExpr.Value);
            hasRecovery |= ContainsErrorRecoveryType(valueType);
        }

        if (_loopDepth <= 0)
        {
            AddError(breakExpr.Span, DiagnosticMessages.BreakExpressionOutsideLoop);
        }

        return hasRecovery
            ? CreateErrorRecoveryType()
            : BaseTypes.Never;
    }

    private Type InferContinue(ContinueExpr continueExpr)
    {
        if (_loopDepth > 0)
        {
            return BaseTypes.Never;
        }

        AddError(continueExpr.Span, DiagnosticMessages.ContinueExpressionOutsideLoop);
        return CreateErrorRecoveryType();
    }

    private static Type ResolveFunctionReturnType(Type functionType)
    {
        if (functionType is TyFun funType)
        {
            var current = funType;
            while (current.Result is TyFun nested)
            {
                current = nested;
            }

            return current.Result;
        }

        return BaseTypes.Unit;
    }

    private void PushFunctionReturnType(Type returnType)
    {
        _functionReturnTypeStack.Push(returnType);
    }

    private void PopFunctionReturnType()
    {
        if (_functionReturnTypeStack.Count > 0)
        {
            _functionReturnTypeStack.Pop();
        }
    }

    private bool TryGetCurrentFunctionReturnType(out Type returnType)
    {
        if (_functionReturnTypeStack.Count > 0)
        {
            returnType = _functionReturnTypeStack.Peek();
            return true;
        }

        returnType = BaseTypes.Unit;
        return false;
    }

    /// <summary>
    /// 推断赋值语句
    /// </summary>
    private void InferAssignment(Assignment assign)
    {
        assign.InferredType = InferAssignmentExpression(assign);
    }

    private Type InferAssignmentExpression(Assignment assign)
    {
        if (assign.Value == null)
        {
            AddError(assign.Span, DiagnosticMessages.AssignmentRequiresValueExpression);
            return CreateErrorRecoveryType();
        }

        var targetType = InferAssignmentTarget(assign);
        var valueType = InferExpressionWithExpectedType(assign.Value, targetType);
        var assignmentType = (Type)BaseTypes.Unit;
        if (ContainsErrorRecoveryType(targetType) || ContainsErrorRecoveryType(valueType))
        {
            assignmentType = CreateErrorRecoveryType();
        }
        else
        {
            var resolvedValue = _substitution.Apply(valueType);
            var resolvedTarget = _substitution.Apply(targetType);

            if (resolvedValue is TyRef or TyMutRef && !IsRefType(resolvedTarget))
            {
                var innerType = resolvedValue switch
                {
                    TyRef r => _substitution.Apply(r.Inner),
                    TyMutRef mr => _substitution.Apply(mr.Inner),
                    _ => valueType
                };

                var syntheticDeref = new UnaryExpr();
                syntheticDeref.SetOperator(UnaryOp.Deref);
                syntheticDeref.SetOperand(assign.Value);
                syntheticDeref.SetSpan(assign.Value.Span);
                syntheticDeref.InferredType = innerType;
                assign.SetValue(syntheticDeref);

                valueType = innerType;
            }

            var assignmentResult = TryUnify(targetType, valueType, assign.Value.Span, DiagnosticMessages.AssignmentTypeMismatch);
            if (ContainsErrorRecoveryType(assignmentResult))
            {
                assignmentType = CreateErrorRecoveryType();
            }
        }

        return assignmentType;
    }

    private Type InferAssignmentTarget(Assignment assign)
    {
        if (assign.TargetExpression != null)
        {
            if (assign.TargetExpression is UnaryExpr { Operator: UnaryOp.Deref } derefTarget)
            {
                return InferDerefAssignmentTarget(derefTarget);
            }

            var targetType = SafeInferExpression(assign.TargetExpression);
            var resolvedTarget = _substitution.Apply(targetType);
            return resolvedTarget is TyMutRef mutRef
                ? _substitution.Apply(mutRef.Inner)
                : targetType;
        }

        if (!assign.TargetSymbolId.IsValid)
        {
            return CreateErrorRecoveryType();
        }

        var scheme = _env.Lookup(assign.TargetSymbolId);
        return scheme != null
            ? _substitution.Instantiate(scheme)
            : CreateErrorRecoveryType();
    }

    private Type InferDerefAssignmentTarget(UnaryExpr derefTarget)
    {
        if (derefTarget.Operand == null)
        {
            return CreateMissingShapeRecoveryType(derefTarget.Span, DiagnosticMessages.UnaryExpressionRequiresOperand);
        }

        var operandType = SafeInferExpression(derefTarget.Operand);
        var appliedOperandType = _substitution.Apply(operandType);
        if (ContainsErrorRecoveryType(appliedOperandType))
        {
            return CreateErrorRecoveryType();
        }

        switch (appliedOperandType)
        {
            case TyMutRef mutableReference:
                var innerType = _substitution.Apply(mutableReference.Inner);
                derefTarget.InferredType = innerType;
                return innerType;

            case TyRef:
                AddError(derefTarget.Span, DiagnosticMessages.CannotAssignToImmutableVariable("dereferenced Ref"));
                return CreateErrorRecoveryType();

            case TyVar:
                var freshInnerType = _substitution.FreshTypeVariable();
                _substitution.Unify(operandType, new TyMutRef { Inner = freshInnerType });
                var inferredInnerType = _substitution.Apply(freshInnerType);
                derefTarget.InferredType = inferredInnerType;
                return inferredInnerType;

            default:
                AddError(derefTarget.Span, DiagnosticMessages.CannotDereferenceNonReferenceType(appliedOperandType));
                return CreateErrorRecoveryType();
        }
    }

    private static bool IsRefType(Type type)
    {
        return type is TyRef or TyMutRef;
    }
}
