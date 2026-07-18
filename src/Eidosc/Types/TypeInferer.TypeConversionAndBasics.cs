using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Semantic;
using Eidosc.Utilities;
using Eidosc.Utils;

using Eidosc.Diagnostic;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private Type CreateFunctionType(FuncSymbol funcSymbol)
    {
        if (MetaSchemaRegistry.IsMetaIntrinsic(funcSymbol, out _))
        {
            return MetaSchemaRegistry.CreateFunctionType(funcSymbol, _substitution, _symbolTable);
        }

        if (BuildSchemaRegistry.IsBuildIntrinsic(funcSymbol, out _))
        {
            return BuildSchemaRegistry.CreateFunctionType(funcSymbol, _substitution, _symbolTable);
        }

        return CreateFunctionType(funcSymbol, _substitution);
    }

    private static Type CreateFunctionType(FuncSymbol funcSymbol, Substitution substitution)
    {
        if (funcSymbol.BuiltinIntrinsicRole == BuiltinIntrinsicRole.ValueBox)
        {
            return new TyFun
            {
                Params = [substitution.FreshTypeVariable()],
                Result = new TyCon
                {
                    Name = WellKnownStrings.BuiltinTypes.RawPtr,
                    Id = new TypeId(BaseTypes.RawPtrId)
                }
            };
        }

        if (funcSymbol.BuiltinIntrinsicRole == BuiltinIntrinsicRole.ValueUnbox)
        {
            return new TyFun
            {
                Params =
                [
                    new TyCon
                    {
                        Name = WellKnownStrings.BuiltinTypes.RawPtr,
                        Id = new TypeId(BaseTypes.RawPtrId)
                    }
                ],
                Result = substitution.FreshTypeVariable()
            };
        }

        if (funcSymbol.BuiltinIntrinsicRole == BuiltinIntrinsicRole.SharedNew)
        {
            var payloadType = substitution.FreshTypeVariable();
            return new TyFun
            {
                Params = [payloadType],
                Result = new TyShared { Inner = payloadType }
            };
        }

        if (funcSymbol.BuiltinIntrinsicRole == BuiltinIntrinsicRole.SharedBorrow)
        {
            var payloadType = substitution.FreshTypeVariable();
            return new TyFun
            {
                Params = [new TyShared { Inner = payloadType }],
                Result = new TyRef { Inner = payloadType }
            };
        }

        if (funcSymbol.BuiltinIntrinsicRole == BuiltinIntrinsicRole.SharedClone)
        {
            var payloadType = substitution.FreshTypeVariable();
            return new TyFun
            {
                Params = [new TyShared { Inner = payloadType }],
                Result = new TyShared { Inner = payloadType }
            };
        }

        if (funcSymbol.BuiltinIntrinsicRole == BuiltinIntrinsicRole.SharedPtrEq)
        {
            var payloadType = substitution.FreshTypeVariable();
            return new TyFun
            {
                Params =
                [
                    new TyShared { Inner = payloadType },
                    new TyShared { Inner = payloadType }
                ],
                Result = BaseTypes.Bool
            };
        }

        var paramTypes = Enumerable.Range(0, funcSymbol.Parameters.Count)
            .Select(index => index < funcSymbol.ParamTypes.Count && funcSymbol.ParamTypes[index].IsValid
                ? CreateMetadataType(funcSymbol.ParamTypes[index])
                : (Type)substitution.FreshTypeVariable())
            .ToList();

        var resultType = funcSymbol.ReturnType.IsValid
            ? CreateMetadataType(funcSymbol.ReturnType)
            : substitution.FreshTypeVariable();

        return new TyFun
        {
            Params = paramTypes,
            Result = resultType
        };
    }

    private static Type CreateMetadataType(TypeId typeId)
    {
        return typeId.Value switch
        {
            BaseTypes.IntId => BaseTypes.Int,
            BaseTypes.FloatId => BaseTypes.Float,
            BaseTypes.BoolId => BaseTypes.Bool,
            BaseTypes.StringId => BaseTypes.String,
            BaseTypes.CharId => BaseTypes.Char,
            BaseTypes.UnitId => BaseTypes.Unit,
            BaseTypes.ErasedCallableId => BaseTypes.ErasedCallable,
            BaseTypes.RawPtrId => new TyCon
            {
                Name = WellKnownStrings.BuiltinTypes.RawPtr,
                Id = new TypeId(BaseTypes.RawPtrId)
            },
            BaseTypes.CfnId => BaseTypes.Cfn,
            BaseTypes.TypeValueId => BaseTypes.TypeValue,
            BaseTypes.NeverId => BaseTypes.Never,
            _ => new TyCon { Name = $"T{typeId.Value}", Id = typeId }
        };
    }

    private Type InferInfixCall(InfixCallExpr infixCall)
    {
        infixCall.InferredEffects = null;

        if (infixCall.Left == null || infixCall.Right == null)
        {
            if (infixCall.Left == null)
            {
                ReportMissingShape(infixCall.Span, DiagnosticMessages.InfixCallRequiresLeftOperand);
            }

            if (infixCall.Right == null)
            {
                ReportMissingShape(infixCall.Span, DiagnosticMessages.InfixCallRequiresRightOperand);
            }

            return CreateErrorRecoveryType();
        }

        var leftType = infixCall.Left != null
            ? InferExpression(infixCall.Left)
            : null;
        var rightType = infixCall.Right != null
            ? InferExpression(infixCall.Right)
            : null;

        if (!infixCall.FunctionSymbolId.IsValid)
        {
            var argumentTypes = new[] { leftType!, rightType! };
            var candidates = GetTypeDirectedCallableCandidates(
                infixCall.FunctionName,
                infixCall.FunctionCandidateSymbolIds,
                infixCall.FunctionSymbolId);
            TypeDirectedCandidateResolution resolution = default;

            if (candidates.Count > 0 &&
                !TryResolveTypeDirectedMethodCandidate(candidates, argumentTypes, out resolution))
            {
                ReportCallableResolutionFailure(
                    infixCall.Span,
                    infixCall.FunctionName,
                    "infix",
                    resolution,
                    argumentTypes,
                    DiagnosticMessages.NoImportedOverloadAcceptsArgumentTypes(infixCall.FunctionName));
                return CreateErrorRecoveryType();
            }

            if (candidates.Count > 0)
            {
                infixCall.FunctionSymbolId = resolution.SelectedSymbolId;
            }
        }

        if (!infixCall.FunctionSymbolId.IsValid)
        {
            return CreateErrorRecoveryType();
        }

        var funcType = InferFunctionSymbolType(infixCall.FunctionSymbolId, infixCall.Span);

        try
        {
            if (leftType != null)
            {
                funcType = ApplyCallArgument(infixCall, funcType, leftType, infixCall.Left?.Span ?? infixCall.Span);
            }

            if (rightType != null)
            {
                funcType = ApplyCallArgument(infixCall, funcType, rightType, infixCall.Right?.Span ?? infixCall.Span);
            }
        }
        catch (TypeInferenceException ex)
        {
            AddError(infixCall.Span, DiagnosticMessages.InfixOperatorCannotBeApplied(infixCall.FunctionName, ex.Message));
            return CreateErrorRecoveryType();
        }

        return funcType;
    }

    private Type ApplyFunctionType(Type funcType, Type argType, SourceSpan span)
    {
        if (ContainsErrorRecoveryType(funcType) || ContainsErrorRecoveryType(argType))
        {
            return CreateErrorRecoveryType();
        }

        if (funcType is TyFun fun)
        {
            var paramType = fun.Params.Count > 0 ? fun.Params[0] : _substitution.FreshTypeVariable();
            _substitution.Unify(_substitution.Apply(paramType), _substitution.Apply(argType));
            if (fun.Params.Count == 1)
            {
                return _substitution.Apply(fun.Result);
            }

            return new TyFun
            {
                Params = CopyParamsFrom(fun.Params, 1),
                Result = fun.Result,
                Effects = fun.Effects
            };
        }

        var param = _substitution.FreshTypeVariable();
        var ret = _substitution.FreshTypeVariable();
        _substitution.Unify(funcType, new TyFun { Params = [param], Result = ret });
        _substitution.Unify(_substitution.Apply(param), _substitution.Apply(argType));
        return _substitution.Apply(ret);
    }

    private Type InferFunctionSymbolType(SymbolId symbolId, SourceSpan span)
    {
        var scheme = _env.Lookup(symbolId);
        if (scheme != null)
        {
            return ApplyImplicitFunctionEffects(symbolId, InstantiateSchemeWithConstraints(scheme, span));
        }

        var symbol = _symbolTable.GetSymbol(symbolId);
        if (symbol is FuncSymbol funcSymbol)
        {
            return ApplyImplicitFunctionEffects(symbolId, CreateFunctionType(funcSymbol));
        }

        if (symbol is VarSymbol varSymbol)
        {
            AddError(span, DiagnosticMessages.CannotInferCallableTypeForVariable(varSymbol.Name));
            return CreateErrorRecoveryType();
        }

        if (symbol != null)
        {
            AddNonValueSymbolError(span, symbol.Name, symbol);
            return CreateErrorRecoveryType();
        }

        AddError(span, DiagnosticMessages.CannotInferCallableTypeMissingSymbol(symbolId));
        return CreateErrorRecoveryType();
    }

    private Type ApplyImplicitFunctionEffects(SymbolId symbolId, Type functionType)
    {
        if (_symbolTable.GetSymbol(symbolId) is not FuncSymbol { ImplicitAbilities.Count: > 0 } function)
        {
            return functionType;
        }

        var effects = new EffectRow(function.ImplicitAbilities.Select(name => ResolveEffectTag([name])));
        return ApplyRequiredAbilitiesToFunction(functionType, effects);
    }

    private Type TryInsertBinaryDeref(BinaryExpr binary, bool isLeft, Type type)
    {
        var resolved = _substitution.Apply(type);
        if (resolved is not (TyRef or TyMutRef)) return type;

        var innerType = resolved switch
        {
            TyRef r => _substitution.Apply(r.Inner),
            TyMutRef mr => _substitution.Apply(mr.Inner),
            _ => type
        };

        var operand = isLeft ? binary.Left : binary.Right;
        if (operand == null) return type;

        var syntheticDeref = new UnaryExpr();
        syntheticDeref.SetOperator(UnaryOp.Deref);
        syntheticDeref.SetOperand(operand);
        syntheticDeref.SetSpan(operand.Span);
        syntheticDeref.InferredType = innerType;

        if (isLeft)
            binary.SetLeft(syntheticDeref);
        else
            binary.SetRight(syntheticDeref);

        return innerType;
    }

    private Type InferBinary(BinaryExpr binary)
    {
        if (binary.Left == null || binary.Right == null)
        {
            if (binary.Left == null)
            {
                ReportMissingShape(binary.Span, DiagnosticMessages.BinaryExpressionRequiresLeftOperand);
            }

            if (binary.Right == null)
            {
                ReportMissingShape(binary.Span, DiagnosticMessages.BinaryExpressionRequiresRightOperand);
            }

            return CreateErrorRecoveryType();
        }

        var leftType = SafeInferExpression(binary.Left);
        if (binary.Operator == BinaryOp.Pipe &&
            binary.Right is IdentifierExpr pipeTarget)
        {
            leftType = TryInsertBinaryDeref(binary, isLeft: true, leftType);
            return InferPipeToCandidateIdentifier(leftType, pipeTarget, binary.Span);
        }

        var rightType = SafeInferExpression(binary.Right);

        // Auto-deref: Ref[T]/MRef[T] → T for binary operands
        leftType = TryInsertBinaryDeref(binary, isLeft: true, leftType);
        rightType = TryInsertBinaryDeref(binary, isLeft: false, rightType);

        return binary.Operator switch
        {
            BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or
            BinaryOp.Divide or BinaryOp.Modulo => InferArithmeticBinary(leftType, rightType, binary.Span),
            BinaryOp.Concat => InferConcatBinary(leftType, rightType, binary.Span),
            BinaryOp.Less or BinaryOp.Greater or BinaryOp.LessEqual or
            BinaryOp.GreaterEqual or BinaryOp.Equal or BinaryOp.NotEqual
                => InferComparisonBinary(leftType, rightType, binary.Span),
            BinaryOp.And or BinaryOp.Or => InferLogicalBinary(leftType, rightType, binary.Span),
            BinaryOp.Prepend => InferPrependBinary(leftType, rightType, binary.Span),
            BinaryOp.AppendLast => InferAppendLastBinary(leftType, rightType, binary.Span),
            BinaryOp.Coalesce => InferCoalesceBinary(leftType, rightType, binary.Span),
            BinaryOp.Pipe => InferPipeBinary(leftType, rightType, binary.Span),
            BinaryOp.ComposeRight => InferComposeBinary(leftType, rightType, leftToRight: true, binary.Span),
            BinaryOp.ComposeLeft => InferComposeBinary(leftType, rightType, leftToRight: false, binary.Span),
            BinaryOp.Append => InferAppendBinary(leftType, rightType, binary.Span),
            BinaryOp.Fmap => InferFmapBinary(leftType, rightType, binary.Span),
            BinaryOp.Ap => InferApplicativeApplyBinary(leftType, rightType, binary.Span),
            BinaryOp.Bind => InferBindBinary(leftType, rightType, binary.Span),
            _ => InferUnsupportedBinary(binary)
        };
    }

    private Type InferPipeToCandidateIdentifier(Type leftType, IdentifierExpr pipeTarget, SourceSpan span)
    {
        var candidates = GetTypeDirectedCallableCandidates(
            pipeTarget.Name,
            pipeTarget.ValueCandidateSymbolIds,
            pipeTarget.SymbolId);

        if (candidates.Count == 0)
        {
            return InferPipeBinary(leftType, SafeInferExpression(pipeTarget), span);
        }

        if (!TryResolveTypeDirectedMethodCandidate(candidates, [leftType], out var resolution))
        {
            ReportCallableResolutionFailure(
                span,
                pipeTarget.Name,
                "pipe",
                resolution,
                [leftType],
                DiagnosticMessages.NoImportedOverloadAcceptsArgumentTypes(pipeTarget.Name));
            return CreateErrorRecoveryType();
        }

        pipeTarget.SymbolId = resolution.SelectedSymbolId;
        var functionType = InferFunctionSymbolType(resolution.SelectedSymbolId, pipeTarget.Span);
        return ApplyFunctionArgument(functionType, leftType, span);
    }

    private Type InferPipeBinary(Type leftType, Type rightType, SourceSpan span)
    {
        var resolvedLeft = _substitution.Apply(leftType);
        var resolvedRight = _substitution.Apply(rightType);
        if (ContainsErrorRecoveryType(resolvedLeft) || ContainsErrorRecoveryType(resolvedRight))
        {
            return CreateErrorRecoveryType();
        }

        try
        {
            return ApplyFunctionType(resolvedRight, resolvedLeft, span);
        }
        catch (TypeInferenceException ex)
        {
            AddError(span, DiagnosticMessages.PipeTargetNotCallable(ex.Message));
            return CreateErrorRecoveryType();
        }
    }

    private Type InferComposeBinary(Type leftType, Type rightType, bool leftToRight, SourceSpan span)
    {
        var resolvedLeft = _substitution.Apply(leftType);
        var resolvedRight = _substitution.Apply(rightType);
        if (ContainsErrorRecoveryType(resolvedLeft) || ContainsErrorRecoveryType(resolvedRight))
        {
            return CreateErrorRecoveryType();
        }

        var inputType = _substitution.FreshTypeVariable();
        var middleType = _substitution.FreshTypeVariable();
        var outputType = _substitution.FreshTypeVariable();

        var leftExpected = leftToRight
            ? CreateUnaryFunctionType(inputType, middleType)
            : CreateUnaryFunctionType(middleType, outputType);
        var rightExpected = leftToRight
            ? CreateUnaryFunctionType(middleType, outputType)
            : CreateUnaryFunctionType(inputType, middleType);

        if (!TryUnifyOperatorType(leftExpected, resolvedLeft, span, DiagnosticMessages.ComposeLeftOperandMustBeCallable) ||
            !TryUnifyOperatorType(rightExpected, resolvedRight, span, DiagnosticMessages.ComposeRightOperandMustBeCallable))
        {
            return CreateErrorRecoveryType();
        }

        return CreateUnaryFunctionType(_substitution.Apply(inputType), _substitution.Apply(outputType));
    }

    private Type InferAppendBinary(Type leftType, Type rightType, SourceSpan span)
    {
        var resolvedLeft = _substitution.Apply(leftType);
        var resolvedRight = _substitution.Apply(rightType);
        if (ContainsErrorRecoveryType(resolvedLeft) || ContainsErrorRecoveryType(resolvedRight))
        {
            return CreateErrorRecoveryType();
        }

        var result = TryUnify(resolvedLeft, resolvedRight, span, DiagnosticMessages.AppendOperandsMustHaveSameType);
        return ContainsErrorRecoveryType(result)
            ? CreateErrorRecoveryType()
            : _substitution.Apply(result);
    }

    private Type InferFmapBinary(Type functionType, Type containerType, SourceSpan span)
    {
        var resolvedFunction = _substitution.Apply(functionType);
        var resolvedContainer = _substitution.Apply(containerType);
        if (ContainsErrorRecoveryType(resolvedFunction) || ContainsErrorRecoveryType(resolvedContainer))
        {
            return CreateErrorRecoveryType();
        }

        var inputType = _substitution.FreshTypeVariable();
        var outputType = _substitution.FreshTypeVariable();

        if (!TryUnifyOperatorType(
                CreateUnaryFunctionType(inputType, outputType),
                resolvedFunction,
                span,
                DiagnosticMessages.FmapLeftOperandMustBeCallable))
        {
            return CreateErrorRecoveryType();
        }

        if (!TryCreateUnaryContainerTypes(
                resolvedContainer,
                inputType,
                outputType,
                out var expectedContainer,
                out var resultContainer))
        {
            AddError(span, DiagnosticMessages.FmapRightOperandMustBeUnaryTypeConstructorValue(resolvedContainer));
            return CreateErrorRecoveryType();
        }

        if (!TryUnifyOperatorType(expectedContainer, resolvedContainer, span, DiagnosticMessages.FmapRightOperandTypeMismatch))
        {
            return CreateErrorRecoveryType();
        }

        return _substitution.Apply(resultContainer);
    }

    private Type InferApplicativeApplyBinary(Type functionContainerType, Type valueContainerType, SourceSpan span)
    {
        var resolvedFunctionContainer = _substitution.Apply(functionContainerType);
        var resolvedValueContainer = _substitution.Apply(valueContainerType);
        if (ContainsErrorRecoveryType(resolvedFunctionContainer) || ContainsErrorRecoveryType(resolvedValueContainer))
        {
            return CreateErrorRecoveryType();
        }

        var inputType = _substitution.FreshTypeVariable();
        var outputType = _substitution.FreshTypeVariable();
        var functionType = CreateUnaryFunctionType(inputType, outputType);

        if (!TryCreateBinaryContainerTypes(
                resolvedFunctionContainer,
                resolvedValueContainer,
                functionType,
                inputType,
                outputType,
                out var expectedFunctionContainer,
                out var expectedValueContainer,
                out var resultContainer))
        {
            AddError(
                span,
                DiagnosticMessages.ApplicativeApplyOperandsMustShareUnaryTypeConstructor(resolvedFunctionContainer, resolvedValueContainer));
            return CreateErrorRecoveryType();
        }

        if (!TryUnifyOperatorType(expectedFunctionContainer, resolvedFunctionContainer, span, DiagnosticMessages.ApplicativeApplyLeftOperandTypeMismatch) ||
            !TryUnifyOperatorType(expectedValueContainer, resolvedValueContainer, span, DiagnosticMessages.ApplicativeApplyRightOperandTypeMismatch))
        {
            return CreateErrorRecoveryType();
        }

        return _substitution.Apply(resultContainer);
    }

    private Type InferBindBinary(Type monadicType, Type binderType, SourceSpan span)
    {
        var resolvedMonadic = _substitution.Apply(monadicType);
        var resolvedBinder = _substitution.Apply(binderType);
        if (ContainsErrorRecoveryType(resolvedMonadic) || ContainsErrorRecoveryType(resolvedBinder))
        {
            return CreateErrorRecoveryType();
        }

        var inputType = _substitution.FreshTypeVariable();
        var outputType = _substitution.FreshTypeVariable();
        if (!TryCreateUnaryContainerTypes(
                resolvedMonadic,
                inputType,
                outputType,
                out var expectedMonadic,
                out var resultContainer))
        {
            AddError(span, DiagnosticMessages.BindLeftOperandMustBeUnaryTypeConstructorValue(resolvedMonadic));
            return CreateErrorRecoveryType();
        }

        var expectedBinder = CreateUnaryFunctionType(inputType, resultContainer);
        if (!TryUnifyOperatorType(expectedMonadic, resolvedMonadic, span, DiagnosticMessages.BindLeftOperandTypeMismatch) ||
            !TryUnifyOperatorType(expectedBinder, resolvedBinder, span, DiagnosticMessages.BindRightOperandMustReturnSameMonadicType))
        {
            return CreateErrorRecoveryType();
        }

        return _substitution.Apply(resultContainer);
    }

    private bool TryUnifyOperatorType(Type expected, Type actual, SourceSpan span, string context)
    {
        var result = TryUnify(expected, actual, span, context);
        return !ContainsErrorRecoveryType(result);
    }

    private static TyFun CreateUnaryFunctionType(Type parameterType, Type resultType)
    {
        return new TyFun
        {
            Params = [parameterType],
            Result = resultType
        };
    }

    private bool TryCreateUnaryContainerTypes(
        Type containerType,
        Type expectedElementType,
        Type resultElementType,
        out Type expectedContainerType,
        out Type resultContainerType)
    {
        var resolvedContainer = _substitution.Apply(containerType);
        if (TryGetUnaryContainerShape(resolvedContainer, out var shape))
        {
            expectedContainerType = ReplaceUnaryContainerElement(shape, expectedElementType);
            resultContainerType = ReplaceUnaryContainerElement(shape, resultElementType);
            return true;
        }

        if (resolvedContainer is TyVar)
        {
            var constructor = _substitution.FreshTypeVariable();
            expectedContainerType = new TyCon
            {
                ConstructorVarIndex = constructor.Index,
                Args = [expectedElementType]
            };
            resultContainerType = new TyCon
            {
                ConstructorVarIndex = constructor.Index,
                Args = [resultElementType]
            };
            return true;
        }

        expectedContainerType = BaseTypes.Unit;
        resultContainerType = BaseTypes.Unit;
        return false;
    }

    private bool TryCreateBinaryContainerTypes(
        Type firstContainerType,
        Type secondContainerType,
        Type firstElementType,
        Type secondElementType,
        Type resultElementType,
        out Type expectedFirstContainerType,
        out Type expectedSecondContainerType,
        out Type resultContainerType)
    {
        var resolvedFirst = _substitution.Apply(firstContainerType);
        var resolvedSecond = _substitution.Apply(secondContainerType);
        if (!TryGetUnaryContainerShape(resolvedFirst, out var shape) &&
            !TryGetUnaryContainerShape(resolvedSecond, out shape))
        {
            if (resolvedFirst is not TyVar && resolvedSecond is not TyVar)
            {
                expectedFirstContainerType = BaseTypes.Unit;
                expectedSecondContainerType = BaseTypes.Unit;
                resultContainerType = BaseTypes.Unit;
                return false;
            }

            var constructor = _substitution.FreshTypeVariable();
            shape = new TyCon
            {
                ConstructorVarIndex = constructor.Index,
                Args = [_substitution.FreshTypeVariable()]
            };
        }

        expectedFirstContainerType = ReplaceUnaryContainerElement(shape, firstElementType);
        expectedSecondContainerType = ReplaceUnaryContainerElement(shape, secondElementType);
        resultContainerType = ReplaceUnaryContainerElement(shape, resultElementType);
        return true;
    }

    private bool TryGetUnaryContainerShape(Type type, out TyCon shape)
    {
        if (type is TyCon { Args.Count: > 0 } tyCon)
        {
            if (TryPromoteClosedCaseToRoot(tyCon, out var rootType))
            {
                if (rootType.Args.Count == 0)
                {
                    shape = new TyCon();
                    return false;
                }

                shape = rootType;
                return true;
            }

            shape = tyCon;
            return true;
        }

        shape = new TyCon();
        return false;
    }

    private static TyCon ReplaceUnaryContainerElement(TyCon shape, Type elementType)
    {
        var args = shape.Args.ToList();
        if (args.Count == 0)
        {
            args.Add(elementType);
        }
        else
        {
            args[^1] = elementType;
        }

        return shape with { Args = args };
    }

    private Type InferUnsupportedBinary(BinaryExpr binary)
    {
        AddError(binary.Span, DiagnosticMessages.UnsupportedBinaryOperator(binary.Operator));
        return CreateErrorRecoveryType();
    }

    private Type InferArithmeticBinary(Type leftType, Type rightType, SourceSpan span)
    {
        return TryUnify(leftType, rightType, span, DiagnosticMessages.ArithmeticOperandTypeMismatch);
    }

    private Type InferConcatBinary(Type leftType, Type rightType, SourceSpan span)
    {
        var leftResult = TryUnify(leftType, BaseTypes.String, span, DiagnosticMessages.StringConcatenationLeftOperandMustBeString);
        if (ContainsErrorRecoveryType(leftResult))
        {
            return CreateErrorRecoveryType();
        }

        var rightResult = TryUnify(rightType, BaseTypes.String, span, DiagnosticMessages.StringConcatenationRightOperandMustBeString);
        if (ContainsErrorRecoveryType(rightResult))
        {
            return CreateErrorRecoveryType();
        }

        return BaseTypes.String;
    }

    private Type InferComparisonBinary(Type leftType, Type rightType, SourceSpan span)
    {
        var result = TryUnify(leftType, rightType, span, DiagnosticMessages.ComparisonOperandTypeMismatch);
        if (ContainsErrorRecoveryType(result))
        {
            return CreateErrorRecoveryType();
        }

        return BaseTypes.Bool;
    }

    private Type InferLogicalBinary(Type leftType, Type rightType, SourceSpan span)
    {
        var leftResult = TryUnify(leftType, BaseTypes.Bool, span, DiagnosticMessages.LogicalLeftOperandMustBeBool);
        if (ContainsErrorRecoveryType(leftResult))
        {
            return CreateErrorRecoveryType();
        }

        var rightResult = TryUnify(rightType, BaseTypes.Bool, span, DiagnosticMessages.LogicalRightOperandMustBeBool);
        if (ContainsErrorRecoveryType(rightResult))
        {
            return CreateErrorRecoveryType();
        }

        return BaseTypes.Bool;
    }

    private Type InferPrependBinary(Type leftType, Type rightType, SourceSpan span)
    {
        var expectedListType = new TyCon { Name = WellKnownStrings.BuiltinTypes.Seq, Args = [_substitution.Apply(leftType)] };
        var result = TryUnify(rightType, expectedListType, span, DiagnosticMessages.PrependRightOperandMustBeList);
        if (ContainsErrorRecoveryType(result))
        {
            return CreateErrorRecoveryType();
        }

        return _substitution.Apply(expectedListType);
    }

    private Type InferAppendLastBinary(Type leftType, Type rightType, SourceSpan span)
    {
        var expectedListType = new TyCon { Name = WellKnownStrings.BuiltinTypes.Seq, Args = [_substitution.Apply(rightType)] };
        var result = TryUnify(leftType, expectedListType, span, DiagnosticMessages.AppendLastLeftOperandMustBeList);
        if (ContainsErrorRecoveryType(result))
        {
            return CreateErrorRecoveryType();
        }

        return _substitution.Apply(expectedListType);
    }

    private Type InferCoalesceBinary(Type leftType, Type rightType, SourceSpan span)
    {
        var fallbackType = _substitution.Apply(rightType);
        var expectedOptionType = new TyCon { Name = "Option", Args = [fallbackType] };
        var result = TryUnify(leftType, expectedOptionType, span, DiagnosticMessages.CoalesceLeftOperandMustBeOption);
        if (ContainsErrorRecoveryType(result))
        {
            return CreateErrorRecoveryType();
        }

        return _substitution.Apply(fallbackType);
    }

    private Type InferUnary(UnaryExpr unary)
    {
        if (unary.Operand == null)
        {
            return CreateMissingShapeRecoveryType(unary.Span, DiagnosticMessages.UnaryExpressionRequiresOperand);
        }

        var operandType = SafeInferExpression(unary.Operand);
        var hasValidReferenceBorrowOperand = ValidateReferenceBorrowOperand(unary);

        return unary.Operator switch
        {
            UnaryOp.Negate => _substitution.Apply(operandType),
            UnaryOp.Not => InferNotUnary(operandType, unary.Span),
            UnaryOp.Deref => InferDerefUnary(unary, operandType),
            UnaryOp.AddressOf => hasValidReferenceBorrowOperand ? InferAddressOfUnary(operandType) : CreateErrorRecoveryType(),
            UnaryOp.Ref => hasValidReferenceBorrowOperand ? InferRefUnary(operandType) : CreateErrorRecoveryType(),
            UnaryOp.MRef => hasValidReferenceBorrowOperand ? InferMRefUnary(operandType) : CreateErrorRecoveryType(),
            _ => InferUnsupportedUnary(unary)
        };
    }

    private Type InferUnsupportedUnary(UnaryExpr unary)
    {
        AddError(unary.Span, DiagnosticMessages.UnsupportedUnaryOperator(unary.Operator));
        return CreateErrorRecoveryType();
    }

    private Type InferNotUnary(Type operandType, SourceSpan span)
    {
        var result = TryUnify(operandType, BaseTypes.Bool, span, DiagnosticMessages.LogicalNegationOperandMustBeBool);
        if (ContainsErrorRecoveryType(result))
        {
            return CreateErrorRecoveryType();
        }

        return BaseTypes.Bool;
    }

    private Type InferDerefUnary(UnaryExpr unary, Type operandType)
    {
        var appliedOperandType = _substitution.Apply(operandType);
        if (ContainsErrorRecoveryType(appliedOperandType))
        {
            return CreateErrorRecoveryType();
        }

        switch (appliedOperandType)
        {
            case TyRef reference:
                return _substitution.Apply(reference.Inner);

            case TyMutRef mutReference:
                return _substitution.Apply(mutReference.Inner);

            case TyVar:
            {
                var innerType = _substitution.FreshTypeVariable();
                _substitution.Unify(operandType, new TyRef { Inner = innerType });
                return _substitution.Apply(innerType);
            }

            default:
                AddError(unary.Span, DiagnosticMessages.CannotDereferenceNonReferenceType(appliedOperandType));
                return CreateErrorRecoveryType();
            }
    }

    private Type InferAddressOfUnary(Type operandType)
    {
        return new TyRef { Inner = _substitution.Apply(operandType) };
    }

    private Type InferRefUnary(Type operandType)
    {
        return new TyRef { Inner = _substitution.Apply(operandType) };
    }

    private Type InferMRefUnary(Type operandType)
    {
        return new TyMutRef { Inner = _substitution.Apply(operandType) };
    }

    private bool ValidateReferenceBorrowOperand(UnaryExpr unary)
    {
        if (unary.Operand == null ||
            unary.Operator is not UnaryOp.AddressOf and not UnaryOp.Ref and not UnaryOp.MRef)
        {
            return true;
        }

        if (BorrowablePlaceClassifier.IsBorrowable(unary.Operand))
        {
            return true;
        }

        var op = unary.Operator.ToSymbol();
        AddError(unary.Span, DiagnosticMessages.BorrowRequiresStablePlace(op));
        return false;
    }

    private Type InferCtor(CtorExpr ctor, Type? expectedResultType = null)
    {
        var constructorSymbolId = ctor.ConstructorPath?.SymbolId is { IsValid: true } pathSymbolId
            ? pathSymbolId
            : ctor.SymbolId;
        var bindingFound = TryGetCtorTypeBinding(constructorSymbolId, ctor.ConstructorName, out var binding);
        Dictionary<string, Type>? typeVarEnv = null;
        var recordUpdateBaseIsValid = true;
        var hasRecovery = false;

        if (bindingFound)
        {
            typeVarEnv = CreateCtorTypeVarEnv(binding);
            ApplyExplicitCtorTypeArgs(ctor, binding, typeVarEnv);
            recordUpdateBaseIsValid = ApplyRecordUpdateBase(ctor, binding, typeVarEnv);
            if (expectedResultType != null)
            {
                var contextualResultType = CreateAdtTypeFromBinding(binding, typeVarEnv, ctor.Span);
                var unifiedResultType = TryUnify(
                    expectedResultType,
                    contextualResultType,
                    ctor.Span,
                    DiagnosticMessages.PatternBranchResultTypeMismatch);
                hasRecovery |= ContainsErrorRecoveryType(unifiedResultType);
            }
        }
        else if (ctor.UpdateBase != null)
        {
            AddError(ctor.UpdateBase.Span, DiagnosticMessages.ConstructorCannotUseRecordUpdateUnknownFields(ctor.ConstructorName));
            recordUpdateBaseIsValid = false;
        }

        var positionalArgTypes = new List<Type>(ctor.PositionalArgs.Count);
        foreach (var arg in ctor.PositionalArgs)
        {
            var argType = SafeInferExpression(arg);
            positionalArgTypes.Add(argType);
            hasRecovery |= ContainsErrorRecoveryType(argType);
        }

        var namedArgTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var field in ctor.NamedArgs)
        {
            hasRecovery |= AddFieldInitType(field, namedArgTypes);
        }

        if (bindingFound && typeVarEnv != null)
        {
            UnifyCtorArgumentTypes(binding, typeVarEnv, positionalArgTypes, namedArgTypes);
            ApplyAdtTypeParamConstraints(binding.AdtId, typeVarEnv, ctor.Span);
            ApplyConstructorTypeParamConstraints(binding, typeVarEnv, ctor.Span);
            return recordUpdateBaseIsValid && !hasRecovery
                ? CreateAdtTypeFromBinding(binding, typeVarEnv, ctor.Span)
                : CreateErrorRecoveryType();
        }

        if (!recordUpdateBaseIsValid)
        {
            return CreateErrorRecoveryType();
        }

        if (hasRecovery)
        {
            return CreateErrorRecoveryType();
        }

        if (TryInferAdtTypeFromConstructor(constructorSymbolId, ctor.ConstructorName) is { } ctorType)
        {
            return ctorType;
        }

        AddError(ctor.Span, DiagnosticMessages.CannotInferAdtTypeForConstructor(ctor.ConstructorName));
        return CreateErrorRecoveryType();
    }

    private Type InferBareConstructor(CtorSymbol ctorSymbol, SourceSpan span)
    {
        if (TryGetCtorTypeBinding(ctorSymbol.Id, ctorSymbol.Name, out var binding))
        {
            if (binding.PositionalArgTypes.Count > 0 || binding.NamedArgTypes.Count > 0)
            {
                AddError(span, DiagnosticMessages.ConstructorRequiresArgumentsInExpressionPosition(ctorSymbol.Name));
                return CreateErrorRecoveryType();
            }

            var typeVarEnv = CreateCtorTypeVarEnv(binding);
            ApplyAdtTypeParamConstraints(binding.AdtId, typeVarEnv, span);
            ApplyConstructorTypeParamConstraints(binding, typeVarEnv, span);
            return CreateAdtTypeFromBinding(binding, typeVarEnv, span);
        }

        if (TryInferAdtTypeFromConstructor(ctorSymbol.Id, ctorSymbol.Name) is { } ctorType)
        {
            return ctorType;
        }

        AddError(span, DiagnosticMessages.CannotInferAdtTypeForConstructor(ctorSymbol.Name));
        return CreateErrorRecoveryType();
    }

    private Type InferRecordUpdate(RecordUpdateExpr update)
    {
        if (update.Base == null)
        {
            AddError(update.Span, DiagnosticMessages.RecordUpdateShorthandMissingBaseExpression);
            return CreateErrorRecoveryType();
        }

        if (!BorrowablePlaceClassifier.IsBorrowable(update.Base))
        {
            AddError(update.Base.Span, DiagnosticMessages.RecordUpdateRequiresStableBasePlace);
            return CreateErrorRecoveryType();
        }

        var baseType = InferExpression(update.Base);
        if (ContainsErrorRecoveryType(baseType))
        {
            return CreateErrorRecoveryType();
        }

        var resolvedBaseType = _substitution.Apply(baseType);
        if (resolvedBaseType is not TyCon { Symbol.IsValid: true } baseCon)
        {
            AddError(update.Base.Span, DiagnosticMessages.RecordUpdateRequiresAdtRecordBase(resolvedBaseType));
            return CreateErrorRecoveryType();
        }

        var bindings = GetRecordCtorTypeBindings(baseCon.Symbol);
        if (bindings.Count == 0)
        {
            if (_symbolTable.GetSymbol<AdtSymbol>(baseCon.Symbol) is { } adtSymbol)
            {
                AddError(update.Span, DiagnosticMessages.TypeHasNoRecordConstructorForUpdate(adtSymbol.Name));
            }
            else
            {
                AddError(update.Span, DiagnosticMessages.RecordUpdateCouldNotResolveBaseAdt);
            }

            return CreateErrorRecoveryType();
        }

        if (bindings.Count > 1)
        {
            return InferVariantPreservingRecordUpdate(update, baseCon, bindings);
        }

        var binding = bindings[0];
        var typeVarEnv = CreateCtorTypeVarEnv(binding, baseCon.Args, baseCon.ValueArgs, baseCon.EffectArgs);
        var ctor = CreateDesugaredRecordUpdateCtor(update, binding);
        var recordUpdateBaseIsValid = ApplyRecordUpdateBase(ctor, binding, typeVarEnv);

        var namedArgTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        var hasRecovery = false;
        foreach (var field in ctor.NamedArgs)
        {
            hasRecovery |= AddFieldInitType(field, namedArgTypes);
        }

        UnifyCtorArgumentTypes(binding, typeVarEnv, [], namedArgTypes);
        ApplyAdtTypeParamConstraints(binding.AdtId, typeVarEnv, update.Span);
        ApplyConstructorTypeParamConstraints(binding, typeVarEnv, update.Span);

        var resultType = CreateAdtTypeFromBinding(binding, typeVarEnv, update.Span);
        ctor.InferredType = resultType;
        update.SetDesugaredCtor(ctor);
        return recordUpdateBaseIsValid && !hasRecovery
            ? resultType
            : CreateErrorRecoveryType();
    }

    private bool AddFieldInitType(FieldInit field, Dictionary<string, Type> namedArgTypes)
    {
        var hasRecovery = false;
        if (string.IsNullOrWhiteSpace(field.FieldName))
        {
            AddError(field.Span, DiagnosticMessages.FieldInitializerRequiresFieldName);
            hasRecovery = true;
        }

        if (field.Value == null)
        {
            var name = string.IsNullOrWhiteSpace(field.FieldName) ? "<missing>" : field.FieldName;
            AddError(field.Span, DiagnosticMessages.FieldInitializerRequiresValueExpression(name));
            return true;
        }

        var fieldType = SafeInferExpression(field.Value);
        hasRecovery |= ContainsErrorRecoveryType(fieldType);
        if (!string.IsNullOrWhiteSpace(field.FieldName))
        {
            namedArgTypes[field.FieldName] = fieldType;
        }

        return hasRecovery;
    }

    private List<CtorTypeBinding> GetRecordCtorTypeBindings(SymbolId adtId)
    {
        return _ctorTypeBindings.Values
            .Where(candidate =>
                (candidate.ResultAdtId == adtId ||
                 candidate.AdtId == adtId && candidate.ResultAdtId != candidate.AdtId) &&
                candidate.PositionalArgTypes.Count == 0 &&
                candidate.NamedArgTypes.Count > 0)
            .ToList();
    }

    private Type InferVariantPreservingRecordUpdate(
        RecordUpdateExpr update,
        TyCon baseCon,
        IReadOnlyList<CtorTypeBinding> bindings)
    {
        if (!ValidateCommonRecordUpdateFields(update, baseCon, bindings))
        {
            return CreateErrorRecoveryType();
        }

        var match = CreateVariantPreservingRecordUpdateMatch(update, baseCon, bindings);
        var resultType = InferExpression(match);
        update.SetDesugaredMatch(match);
        return resultType;
    }

    private bool ValidateCommonRecordUpdateFields(
        RecordUpdateExpr update,
        TyCon baseCon,
        IReadOnlyList<CtorTypeBinding> bindings)
    {
        var ok = true;
        var explicitFieldNames = update.NamedArgs
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldName))
            .Select(static field => field.FieldName)
            .ToList();

        foreach (var fieldName in explicitFieldNames)
        {
            if (bindings.All(binding => binding.NamedArgTypes.ContainsKey(fieldName)))
            {
                continue;
            }

            AddError(update.Span, DiagnosticMessages.RecordUpdateFieldNotPresentOnEveryConstructor(fieldName, baseCon.Name));
            ok = false;
        }

        return ok;
    }

    private Dictionary<string, Type> CreateCtorTypeVarEnv(
        CtorTypeBinding binding,
        IReadOnlyList<Type> typeArgs,
        IReadOnlyList<GenericValueArgument>? valueArgs = null,
        IReadOnlyList<GenericEffectArgument>? effectArgs = null)
    {
        var env = CreateCtorTypeVarEnv(binding);
        var count = Math.Min(binding.AdtTypeParamNames.Count, typeArgs.Count);
        for (var i = 0; i < count; i++)
        {
            env[binding.AdtTypeParamNames[i]] = typeArgs[i];
        }

        if (valueArgs != null && _valueGenericArgumentsByTypeEnv.TryGetValue(env, out var scopedValueArguments))
        {
            var valueArgumentsByParameterIndex = valueArgs.ToDictionary(static argument => argument.ParameterIndex);
            for (var parameterIndex = 0; parameterIndex < binding.AdtGenericParameters.Count; parameterIndex++)
            {
                var parameter = binding.AdtGenericParameters[parameterIndex];
                if (parameter.ParameterKind == GenericParameterKind.Value &&
                    valueArgumentsByParameterIndex.TryGetValue(parameterIndex, out var valueArgument))
                {
                    scopedValueArguments[parameter.SymbolId] = valueArgument;
                }
            }
        }

        if (effectArgs != null)
        {
            var effectArgumentsByParameterIndex = effectArgs.ToDictionary(static argument => argument.ParameterIndex);
            for (var parameterIndex = 0; parameterIndex < binding.AdtGenericParameters.Count; parameterIndex++)
            {
                var parameter = binding.AdtGenericParameters[parameterIndex];
                if (parameter.ParameterKind == GenericParameterKind.EffectRow &&
                    effectArgumentsByParameterIndex.TryGetValue(parameterIndex, out var effectArgument))
                {
                    env[parameter.Name] = effectArgument.Argument;
                }
            }
        }

        return env;
    }

    private CtorExpr CreateDesugaredRecordUpdateCtor(RecordUpdateExpr update, CtorTypeBinding binding)
    {
        var ctor = new CtorExpr();
        ctor.SetSpan(update.Span);
        ctor.SymbolId = binding.CtorId;
        ctor.SetConstructorName(_symbolTable.GetSymbol<CtorSymbol>(binding.CtorId)?.Name ?? "");
        if (update.Base != null)
        {
            ctor.SetUpdateBase(update.Base);
        }

        foreach (var field in update.NamedArgs)
        {
            ctor.AddNamedArg(field);
        }

        return ctor;
    }

    private MatchExpr CreateVariantPreservingRecordUpdateMatch(
        RecordUpdateExpr update,
        TyCon baseCon,
        IReadOnlyList<CtorTypeBinding> bindings)
    {
        var match = new MatchExpr();
        match.SetSpan(update.Span);
        match.SetMatchedExpression(update.Base!);

        foreach (var binding in bindings)
        {
            match.AddBranch(CreateVariantPreservingRecordUpdateBranch(update, baseCon, binding));
        }

        return match;
    }

    private PatternBranch CreateVariantPreservingRecordUpdateBranch(
        RecordUpdateExpr update,
        TyCon baseCon,
        CtorTypeBinding binding)
    {
        var ctorName = _symbolTable.GetSymbol<CtorSymbol>(binding.CtorId)?.Name ?? "";
        var pattern = new CtorPattern();
        pattern.SetSpan(update.Span);
        pattern.SetConstructorName(ctorName);
        pattern.SymbolId = binding.CtorId;

        var boundFields = new Dictionary<string, IdentifierExpr>(StringComparer.Ordinal);
        foreach (var fieldName in binding.NamedArgTypes.Keys)
        {
            var fieldSymbol = CreateSyntheticRecordUpdateFieldSymbol(update, ctorName, fieldName);
            var fieldPattern = new FieldPattern();
            fieldPattern.SetSpan(update.Span);
            fieldPattern.SetFieldName(fieldName);

            var variable = new VarPattern();
            variable.SetSpan(update.Span);
            variable.SetName(_symbolTable.GetSymbol<VarSymbol>(fieldSymbol)?.Name ?? fieldName);
            variable.SymbolId = fieldSymbol;
            fieldPattern.SetPattern(variable);
            pattern.AddNamedPattern(fieldPattern);

            var identifier = new IdentifierExpr();
            identifier.SetSpan(update.Span);
            identifier.SetName(variable.Name);
            identifier.SymbolId = fieldSymbol;
            boundFields[fieldName] = identifier;
        }

        var ctor = new CtorExpr();
        ctor.SetSpan(update.Span);
        ctor.SetConstructorName(ctorName);
        ctor.SymbolId = binding.CtorId;

        var explicitFields = update.NamedArgs
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldName))
            .ToDictionary(static field => field.FieldName, StringComparer.Ordinal);

        foreach (var fieldName in binding.NamedArgTypes.Keys)
        {
            if (explicitFields.TryGetValue(fieldName, out var explicitField) && explicitField.Value != null)
            {
                ctor.AddNamedArg(CreateRecordUpdateFieldInit(fieldName, explicitField.Value, explicitField.Span));
                continue;
            }

            ctor.AddNamedArg(CreateRecordUpdateFieldInit(fieldName, boundFields[fieldName], update.Span));
        }

        var branch = new PatternBranch();
        branch.SetSpan(update.Span);
        branch.SetPattern(pattern);
        branch.SetBody(ctor);
        return branch;
    }

    private SymbolId CreateSyntheticRecordUpdateFieldSymbol(
        RecordUpdateExpr update,
        string ctorName,
        string fieldName)
    {
        var symbolId = _symbolTable.NewSymbolId();
        var name = $"__record_update_{ctorName}_{fieldName}_{symbolId.Value}";
        return _symbolTable.RegisterSymbol(new VarSymbol
        {
            Id = symbolId,
            Name = name,
            Span = update.Span,
            IsPatternBound = true
        });
    }

    private static FieldInit CreateRecordUpdateFieldInit(string fieldName, EidosAstNode value, SourceSpan span)
    {
        var field = new FieldInit();
        field.SetSpan(span);
        field.SetFieldName(fieldName);
        field.SetValue(value);
        return field;
    }

    private bool ApplyRecordUpdateBase(
        CtorExpr ctor,
        CtorTypeBinding binding,
        Dictionary<string, Type> typeVarEnv)
    {
        if (ctor.UpdateBase == null)
        {
            return true;
        }

        if (ctor.PositionalArgs.Count > 0)
        {
            AddError(ctor.Span, DiagnosticMessages.RecordUpdateCannotMixPositionalArgumentsWithBase);
            return false;
        }

        if (!BorrowablePlaceClassifier.IsBorrowable(ctor.UpdateBase))
        {
            AddError(ctor.UpdateBase.Span, DiagnosticMessages.RecordUpdateSpreadRequiresStableBasePlace);
            return false;
        }

        var baseType = InferExpression(ctor.UpdateBase);
        var expectedType = CreateAdtTypeFromBinding(binding, typeVarEnv, ctor.UpdateBase.Span);
        var unifiedBaseType = TryUnify(expectedType, baseType, ctor.UpdateBase.Span, DiagnosticMessages.RecordUpdateSpreadBaseTypeMismatch);
        var baseIsValid = !ContainsErrorRecoveryType(unifiedBaseType);

        var explicitFields = ctor.NamedArgs
            .Where(static field => !string.IsNullOrWhiteSpace(field.FieldName))
            .ToDictionary(static field => field.FieldName, StringComparer.Ordinal);

        var unknownExplicitFields = ctor.NamedArgs
            .Where(field => string.IsNullOrWhiteSpace(field.FieldName) || !binding.NamedArgTypes.ContainsKey(field.FieldName))
            .ToList();

        ctor.NamedArgs.Clear();

        foreach (var fieldName in binding.NamedArgTypes.Keys)
        {
            if (explicitFields.TryGetValue(fieldName, out var explicitField))
            {
                ctor.AddNamedArg(explicitField);
                continue;
            }

            ctor.AddNamedArg(CreateRecordUpdateFieldInit(ctor.UpdateBase, fieldName, ctor.Span));
        }

        foreach (var field in unknownExplicitFields)
        {
            ctor.AddNamedArg(field);
        }

        return baseIsValid;
    }

    private static FieldInit CreateRecordUpdateFieldInit(EidosAstNode updateBase, string fieldName, SourceSpan span)
    {
        var fieldAccess = new MethodCallExpr();
        fieldAccess.SetSpan(span);
        fieldAccess.SetReceiver(CloneRecordUpdateBaseExpression(updateBase));
        fieldAccess.SetMethodName(fieldName);

        var field = new FieldInit();
        field.SetSpan(span);
        field.SetFieldName(fieldName);
        field.SetValue(fieldAccess);
        return field;
    }

    private static EidosAstNode CloneRecordUpdateBaseExpression(EidosAstNode expression)
    {
        return expression switch
        {
            IdentifierExpr identifier => new IdentifierExpr
            {
                Name = identifier.Name,
                IsConstructor = identifier.IsConstructor,
                Span = identifier.Span,
                SymbolId = identifier.SymbolId,
                InferredType = identifier.InferredType
            },
            _ => expression
        };
    }

    private Type? TryInferAdtTypeFromConstructor(SymbolId ctorSymbolId, string? ctorName)
    {
        var resolvedCtorId = ctorSymbolId;
        if (!resolvedCtorId.IsValid && !string.IsNullOrWhiteSpace(ctorName))
        {
            var lookupId = _symbolTable.LookupConstructor(ctorName);
            resolvedCtorId = lookupId is { } ctorId && ctorId.IsValid ? ctorId : SymbolId.None;
        }

        if (!resolvedCtorId.IsValid)
        {
            return null;
        }

        if (_symbolTable.GetSymbol<CtorSymbol>(resolvedCtorId) is not { } ctorSymbol)
        {
            return null;
        }

        if (_symbolTable.GetSymbol<AdtSymbol>(ctorSymbol.OwnerAdt) is not { } adtSymbol)
        {
            return null;
        }

        var typeArgs = new List<Type>();
        var valueArgs = new List<GenericValueArgument>();
        var effectArgs = new List<GenericEffectArgument>();
        for (var i = 0; i < adtSymbol.TypeParams.Count; i++)
        {
            if (_symbolTable.GetSymbol<TypeParamSymbol>(adtSymbol.TypeParams[i]) is not { } parameter)
            {
                continue;
            }

            switch (parameter.ParameterKind)
            {
                case GenericParameterKind.Type:
                    typeArgs.Add(_substitution.FreshTypeVariable());
                    break;
                case GenericParameterKind.Value:
                    valueArgs.Add(_substitution.FreshValueVariable(
                        CreateValueGenericArgumentTemplate(
                            parameter.Name,
                            new TyCon { Id = parameter.TypeId },
                            i)));
                    break;
                case GenericParameterKind.EffectRow:
                    effectArgs.Add(new GenericEffectArgument(i, _substitution.FreshTypeVariable()));
                    break;
            }
        }

        return new TyCon
        {
            Name = adtSymbol.Name,
            Symbol = adtSymbol.Id,
            Args = typeArgs,
            ValueArgs = valueArgs,
            EffectArgs = effectArgs
        };
    }

    private bool TryGetCtorTypeBinding(SymbolId ctorSymbolId, string? ctorName, out CtorTypeBinding binding)
    {
        var resolvedCtorId = ctorSymbolId;
        if (!resolvedCtorId.IsValid && !string.IsNullOrWhiteSpace(ctorName))
        {
            var lookupId = _symbolTable.LookupConstructor(ctorName);
            resolvedCtorId = lookupId is { } ctorId && ctorId.IsValid ? ctorId : SymbolId.None;
        }

        if (resolvedCtorId.IsValid &&
            _ctorTypeBindings.TryGetValue(resolvedCtorId, out var resolvedBinding) &&
            resolvedBinding != null)
        {
            binding = resolvedBinding;
            return true;
        }

        binding = null!;
        return false;
    }

    private Dictionary<string, Type> CreateCtorTypeVarEnv(CtorTypeBinding binding)
    {
        return CreateCtorTypeVarEnv(binding, rigidExistentialCtorParams: false);
    }

    private Dictionary<string, Type> CreateCtorTypeVarEnv(CtorTypeBinding binding, bool rigidExistentialCtorParams)
    {
        var typeVarEnv = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var name in binding.AdtTypeParamNames)
        {
            if (!typeVarEnv.ContainsKey(name))
            {
                typeVarEnv[name] = _substitution.FreshTypeVariable() with
                {
                    IsGenericInstantiation = !rigidExistentialCtorParams
                };
            }
        }

        var returnTypeLocalNames = rigidExistentialCtorParams
            ? GetCtorLocalTypeParamNamesMentionedInReturnType(binding)
            : [];
        foreach (var name in binding.CtorTypeParamNames)
        {
            if (!typeVarEnv.ContainsKey(name))
            {
                var typeVariable = _substitution.FreshTypeVariable();
                typeVariable.IsGenericInstantiation = !rigidExistentialCtorParams;
                if (rigidExistentialCtorParams && !returnTypeLocalNames.Contains(name))
                {
                    typeVariable.IsRigidExistential = true;
                }

                typeVarEnv[name] = typeVariable;
            }
        }

        PopulateValueGenericArgumentEnv(typeVarEnv, binding.AdtGenericParameters);
        PopulateValueGenericArgumentEnv(typeVarEnv, binding.CtorGenericParameters);
        PopulateEffectGenericArgumentEnv(typeVarEnv, binding.AdtGenericParameters);
        PopulateEffectGenericArgumentEnv(typeVarEnv, binding.CtorGenericParameters);

        return typeVarEnv;
    }

    private void PopulateValueGenericArgumentEnv(
        Dictionary<string, Type> typeVarEnv,
        IReadOnlyList<Ast.Types.TypeParam> genericParameters)
    {
        foreach (var parameter in genericParameters)
        {
            if (parameter.ParameterKind != GenericParameterKind.Value ||
                !parameter.SymbolId.IsValid ||
                !_valueGenericParameterTypesBySymbol.TryGetValue(parameter.SymbolId, out var valueType) ||
                !_valueGenericParameterOrdinalsBySymbol.TryGetValue(parameter.SymbolId, out var parameterIndex))
            {
                continue;
            }

            if (!_valueGenericArgumentsByTypeEnv.TryGetValue(typeVarEnv, out var valueArguments))
            {
                valueArguments = [];
                _valueGenericArgumentsByTypeEnv[typeVarEnv] = valueArguments;
            }

            valueArguments[parameter.SymbolId] = _substitution.FreshValueVariable(
                CreateValueGenericArgumentTemplate(parameter.Name, valueType, parameterIndex));
        }
    }

    private void PopulateEffectGenericArgumentEnv(
        Dictionary<string, Type> typeVarEnv,
        IReadOnlyList<Ast.Types.TypeParam> genericParameters)
    {
        foreach (var parameter in genericParameters)
        {
            if (parameter.ParameterKind != GenericParameterKind.EffectRow ||
                string.IsNullOrWhiteSpace(parameter.Name))
            {
                continue;
            }

            typeVarEnv.TryAdd(parameter.Name, _substitution.FreshTypeVariable());
        }
    }

    private static HashSet<string> GetCtorLocalTypeParamNamesMentionedInReturnType(CtorTypeBinding binding)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (binding.ReturnType == null || binding.CtorTypeParamNames.Count == 0)
        {
            return result;
        }

        var localNames = binding.CtorTypeParamNames.ToHashSet(StringComparer.Ordinal);
        CollectMentionedTypeParamNames(binding.ReturnType, localNames, result);
        return result;
    }

    private static void CollectMentionedTypeParamNames(
        TypeNode typeNode,
        IReadOnlySet<string> localNames,
        HashSet<string> result)
    {
        switch (typeNode)
        {
            case TypePath path:
                if (path.ModulePath.Count == 0 && localNames.Contains(path.TypeName))
                {
                    result.Add(path.TypeName);
                }

                foreach (var typeArg in path.TypeArgs)
                {
                    CollectMentionedTypeParamNames(typeArg, localNames, result);
                }

                break;
            case ArrowType arrow:
                if (arrow.ParamType != null)
                {
                    CollectMentionedTypeParamNames(arrow.ParamType, localNames, result);
                }

                if (arrow.ReturnType != null)
                {
                    CollectMentionedTypeParamNames(arrow.ReturnType, localNames, result);
                }

                break;
            case EffectfulType effectful:
                if (effectful.InputType != null)
                {
                    CollectMentionedTypeParamNames(effectful.InputType, localNames, result);
                }

                if (effectful.OutputType != null)
                {
                    CollectMentionedTypeParamNames(effectful.OutputType, localNames, result);
                }

                break;
            case TupleType tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectMentionedTypeParamNames(element, localNames, result);
                }

                break;
        }
    }

    private void ApplyExplicitCtorTypeArgs(CtorExpr ctor, CtorTypeBinding binding, Dictionary<string, Type> typeVarEnv)
    {
        var explicitArguments = ctor.ConstructorPath?.GenericArguments;
        if (explicitArguments == null || explicitArguments.Count == 0)
        {
            return;
        }

        var targetParameters = binding.CtorGenericParameters.Count > 0
            ? binding.CtorGenericParameters
            : binding.AdtGenericParameters;

        if (targetParameters.Count == 0)
        {
            AddError(ctor.Span, DiagnosticMessages.ConstructorDoesNotAcceptTypeArguments(ctor.ConstructorName));
            return;
        }

        if (explicitArguments.Count != targetParameters.Count)
        {
            AddError(
                ctor.Span,
                DiagnosticMessages.ConstructorExpectsTypeArguments(
                    ctor.ConstructorName,
                    targetParameters.Count,
                    explicitArguments.Count));
        }

        var matchCount = Math.Min(explicitArguments.Count, targetParameters.Count);
        for (var i = 0; i < matchCount; i++)
        {
            var parameter = targetParameters[i];
            switch (parameter.ParameterKind, explicitArguments[i])
            {
                case (GenericParameterKind.Type, TypeGenericArgumentNode typeArgument):
                {
                    var explicitType = ConvertTypeInCurrentTypeParamContext(typeArgument.Type);
                    if (typeVarEnv.TryGetValue(parameter.Name, out var inferredType))
                    {
                        _substitution.Unify(inferredType, explicitType);
                    }
                    else
                    {
                        typeVarEnv[parameter.Name] = explicitType;
                    }
                    break;
                }
                case (GenericParameterKind.Value, ValueGenericArgumentNode valueArgument):
                    ApplyExplicitCtorValueArgument(valueArgument, parameter, i, typeVarEnv, ctor.Span);
                    break;
                case (GenericParameterKind.EffectRow, EffectGenericArgumentNode effectArgument):
                {
                    var explicitEffect = ConvertTypeInCurrentTypeParamContext(effectArgument.EffectRow);
                    if (typeVarEnv.TryGetValue(parameter.Name, out var inferredEffect))
                    {
                        _substitution.Unify(inferredEffect, explicitEffect);
                    }
                    else
                    {
                        typeVarEnv[parameter.Name] = explicitEffect;
                    }
                    break;
                }
                default:
                    AddError(
                        explicitArguments[i].Span,
                        $"Generic argument {i + 1} for constructor '{ctor.ConstructorName}' does not match parameter domain '{parameter.ParameterKind}'.");
                    break;
            }
        }
    }

    private void ApplyExplicitCtorValueArgument(
        ValueGenericArgumentNode valueArgument,
        Ast.Types.TypeParam parameter,
        int parameterIndex,
        Dictionary<string, Type> ctorTypeVarEnv,
        SourceSpan span)
    {
        if (!_valueGenericParameterTypesBySymbol.TryGetValue(parameter.SymbolId, out var declaredType))
        {
            AddError(span, $"Cannot resolve value parameter type for '{parameter.Name}'.");
            return;
        }
        var sourceTypeVarEnv = _typeParamEnvStack.TryPeek(out var currentTypeVarEnv)
            ? currentTypeVarEnv
            : ctorTypeVarEnv;
        if (!TryResolveExplicitValueArgument(
                valueArgument,
                parameterIndex,
                declaredType,
                sourceTypeVarEnv,
                out var explicitArgument))
        {
            return;
        }

        if (!_valueGenericArgumentsByTypeEnv.TryGetValue(ctorTypeVarEnv, out var scopedValueArguments) ||
            !scopedValueArguments.TryGetValue(parameter.SymbolId, out var inferredArgument))
        {
            AddError(span, $"Cannot bind value parameter '{parameter.Name}' for constructor specialization.");
            return;
        }

        try
        {
            _substitution.UnifyValueArguments(
                inferredArgument with { ParameterIndex = parameterIndex },
                explicitArgument);
        }
        catch (TypeInferenceException ex)
        {
            AddError(valueArgument.Span, ex.Message);
        }
    }

    private bool TryResolveExplicitValueArgument(
        ValueGenericArgumentNode valueArgument,
        int parameterIndex,
        Type declaredType,
        Dictionary<string, Type> sourceTypeVarEnv,
        out GenericValueArgument explicitArgument)
    {
        var expressionType = InferExpression(valueArgument.Expression);
        try
        {
            _substitution.Unify(expressionType, declaredType);
        }
        catch (TypeInferenceException ex)
        {
            AddError(valueArgument.Span, ex.Message);
            explicitArgument = null!;
            return false;
        }

        if (ComptimeEvaluator.TryEvaluate(
                valueArgument.Expression,
                _comptimeValues,
                _functionDefinitionsBySymbol,
                _substitution.Apply,
                out var value,
                out var reason))
        {
            if (value is ComptimeFloatValue)
            {
                AddError(valueArgument.Span, "Floating-point values cannot be used as specialization keys.");
                explicitArgument = null!;
                return false;
            }

            if (!ComptimePhaseValueValidator.TryValidate(value, out reason))
            {
                AddError(valueArgument.Span, $"Value generic argument {parameterIndex + 1} cannot cross the comptime phase boundary: {reason}");
                explicitArgument = null!;
                return false;
            }

            explicitArgument = new GenericValueArgument(
                parameterIndex,
                value.CanonicalText,
                value.CanonicalHash,
                FormatComptimeGenericArgument(value),
                ResolveSymbolMetadataTypeId(_substitution.Apply(declaredType)));
            return true;
        }

        if (TryCreateSymbolicValueGenericArgument(
                valueArgument.Expression,
                parameterIndex,
                _substitution.Apply(declaredType),
                sourceTypeVarEnv,
                out explicitArgument))
        {
            return true;
        }

        AddError(
            valueArgument.Span,
            $"Value generic argument {parameterIndex + 1} is not compile-time evaluable: {reason}");
        return false;
    }

    private Type ConvertTypeInCurrentTypeParamContext(TypeNode typeNode)
    {
        if (_typeParamEnvStack.Count == 0)
        {
            return ConvertType(typeNode, []);
        }

        return ConvertType(typeNode, _typeParamEnvStack.Peek());
    }

    private Type ConvertTypeWithAdditionalKindContext(
        TypeNode typeNode,
        Dictionary<string, Type> typeVarEnv,
        IReadOnlyDictionary<string, Kind> additionalKindEnvByName,
        bool allowTypeConstructorReference = false)
    {
        if (additionalKindEnvByName.Count == 0)
        {
            return ConvertType(typeNode, typeVarEnv, allowTypeConstructorReference);
        }

        var mergedKindEnvByName = _typeParamKindStack.Count == 0
            ? new Dictionary<string, Kind>(StringComparer.Ordinal)
            : new Dictionary<string, Kind>(_typeParamKindStack.Peek(), StringComparer.Ordinal);
        foreach (var (name, kind) in additionalKindEnvByName)
        {
            mergedKindEnvByName[name] = kind;
        }

        var mergedKindEnvByTypeVar = _typeParamVarKindStack.Count == 0
            ? new Dictionary<int, Kind>()
            : new Dictionary<int, Kind>(_typeParamVarKindStack.Peek());
        foreach (var (name, type) in typeVarEnv)
        {
            if (type is TyVar typeVar &&
                additionalKindEnvByName.TryGetValue(name, out var kind))
            {
                mergedKindEnvByTypeVar[typeVar.Index] = kind;
            }
        }

        _typeParamKindStack.Push(mergedKindEnvByName);
        _typeParamVarKindStack.Push(mergedKindEnvByTypeVar);
        try
        {
            return ConvertType(typeNode, typeVarEnv, allowTypeConstructorReference);
        }
        finally
        {
            _typeParamVarKindStack.Pop();
            _typeParamKindStack.Pop();
        }
    }

    private Dictionary<string, Kind> CreateTypeParamKindMap(IReadOnlyList<TypeParam> typeParams)
    {
        var result = new Dictionary<string, Kind>(StringComparer.Ordinal);
        foreach (var typeParam in typeParams)
        {
            if (typeParam.ParameterKind != GenericParameterKind.Type ||
                string.IsNullOrWhiteSpace(typeParam.Name))
            {
                continue;
            }

            if (typeParam.KindAnnotation == null)
            {
                result[typeParam.Name] = FreshKindVariable();
                continue;
            }

            var kindText = typeParam.GetKindText();
            if (!KindParser.TryParse(kindText, out var parsedKind, out var parseError))
            {
                AddError(typeParam.Span, parseError ?? DiagnosticMessages.UnsupportedKindAnnotation(kindText));
                parsedKind = Kind.KStar.Instance;
            }

            result[typeParam.Name] = parsedKind;
        }

        return result;
    }

    private Dictionary<string, Kind> CreateTypeParamKindMapForOwner(SymbolId ownerId, IReadOnlyList<string> typeParamNames)
    {
        var result = new Dictionary<string, Kind>(StringComparer.Ordinal);
        if (!_typeParamKindBindingsBySymbol.TryGetValue(ownerId, out var binding))
        {
            return result;
        }

        var matchCount = Math.Min(typeParamNames.Count, binding.ExpectedKinds.Count);
        for (var i = 0; i < matchCount; i++)
        {
            result[typeParamNames[i]] = ResolveKind(binding.ExpectedKinds[i]);
        }

        return result;
    }

    private Dictionary<string, Kind> CreateTypeParamKindMapForCtorBinding(
        SymbolId adtId,
        IReadOnlyList<string> adtTypeParamNames,
        SymbolId ctorId,
        IReadOnlyList<string> ctorTypeParamNames)
    {
        var result = CreateTypeParamKindMapForOwner(adtId, adtTypeParamNames);
        foreach (var (name, kind) in CreateTypeParamKindMapForOwner(ctorId, ctorTypeParamNames))
        {
            result[name] = kind;
        }

        return result;
    }

    private Dictionary<string, Kind> FinalizeTypeParamKinds(
        IReadOnlyDictionary<string, Kind> kindEnvByName)
    {
        var finalized = new Dictionary<string, Kind>(kindEnvByName.Count, StringComparer.Ordinal);
        foreach (var (name, kind) in kindEnvByName)
        {
            var resolvedKind = ResolveKind(kind);
            if (resolvedKind is Kind.KVar unresolvedKindVar &&
                unresolvedKindVar.Instance == null)
            {
                resolvedKind = Kind.KStar.Instance;
            }

            finalized[name] = resolvedKind;
        }

        return finalized;
    }

}
