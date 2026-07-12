using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// Control flow lowering: if/loop/return/break/continue.
/// </summary>
public sealed partial class MirBuilder
{
    private MirOperand ConvertIf(HirIf ifExpr)
    {
        var condition = ConvertExpr(ifExpr.Condition);
        condition = EnsureReadValue(condition, ifExpr.Condition.TypeId, ifExpr.Span);

        // 创建基本块
        var thenBlock = NewBlock();
        var elseBlock = NewBlock();
        var mergeBlock = NewBlock();
        var resultPlace = NewTemp(ifExpr.TypeId);

        // 添加条件跳转
        _currentBlock!.Terminator = new MirSwitch
        {
            Discriminant = condition,
            Branches =
            [
                new MirSwitchBranch
                {
                    Value = new MirConstant
                    {
                        Value = new MirConstantValue.BoolValue(true),
                        TypeId = new TypeId(BaseTypes.BoolId),
                        Span = ifExpr.Span
                    },
                    Target = thenBlock.Id
                }
            ],
            DefaultTarget = elseBlock.Id,
            Span = ifExpr.Span
        };

        // Then 分支
        _currentFunc!.BasicBlocks.Add(thenBlock);
        _currentBlock = thenBlock;
        var thenResult = ConvertExpr(ifExpr.ThenBranch);
        EmitInitializationAndGoto(resultPlace, thenResult, ifExpr.Span, mergeBlock.Id);

        // Else 分支
        _currentFunc.BasicBlocks.Add(elseBlock);
        _currentBlock = elseBlock;
        if (ifExpr.ElseBranch != null)
        {
            var elseResult = ConvertExpr(ifExpr.ElseBranch);
            EmitInitializationAndGoto(resultPlace, elseResult, ifExpr.Span, mergeBlock.Id);
        }
        else
        {
            var missingElseValue = IsUnitLikeResultType(ifExpr.TypeId)
                ? CreateUnitConstant(ifExpr.Span, new TypeId(BaseTypes.UnitId))
                : ReportMissingIfElseValue(ifExpr);
            EmitInitializationAndGoto(resultPlace, missingElseValue, ifExpr.Span, mergeBlock.Id);
        }

        // Merge 块
        _currentFunc.BasicBlocks.Add(mergeBlock);
        _currentBlock = mergeBlock;

        return resultPlace;
    }

    private MirOperand ConvertLoop(HirLoop loop)
    {
        var headerBlock = NewBlock();
        var bodyBlock = NewBlock();
        var exitBlock = NewBlock();

        _currentFunc!.BasicBlocks.Add(headerBlock);
        _currentFunc.BasicBlocks.Add(bodyBlock);
        _currentFunc.BasicBlocks.Add(exitBlock);

        _currentBlock!.Terminator = new MirGoto
        {
            Target = headerBlock.Id,
            Span = loop.Span
        };

        headerBlock.Terminator = new MirGoto
        {
            Target = bodyBlock.Id,
            Span = loop.Span
        };

        _loopContextStack.Push(new LoopLoweringContext(headerBlock.Id, exitBlock.Id));
        _currentBlock = bodyBlock;
        try
        {
            _ = ConvertExpr(loop.Body);

            if (_currentBlock != null && _currentBlock.Terminator == null)
            {
                _currentBlock.Terminator = new MirGoto
                {
                    Target = headerBlock.Id,
                    Span = loop.Span
                };
            }
        }
        finally
        {
            _loopContextStack.Pop();
        }

        _currentBlock = exitBlock;
        return CreateUnitConstant(loop.Span, loop.TypeId);
    }

    private MirOperand ConvertReturn(HirReturn returnExpr)
    {
        var functionReturnType = ResolveCurrentFunctionReturnType();
        MirOperand? returnValue = null;

        if (returnExpr.Value != null)
        {
            var loweredValue = ConvertExpr(returnExpr.Value);
            var expectedValueType = functionReturnType.IsValid
                ? functionReturnType
                : returnExpr.Value.TypeId;

            returnValue = expectedValueType.IsValid
                ? EnsureReadValue(loweredValue, expectedValueType, returnExpr.Span)
                : loweredValue;
        }
        else if (functionReturnType.IsValid && functionReturnType.Value != BaseTypes.UnitId)
        {
            returnValue = ReportMissingReturnValue(returnExpr, functionReturnType);
        }

        _currentBlock!.Terminator = new MirReturn
        {
            Value = returnValue,
            Span = returnExpr.Span
        };

        MoveToSyntheticUnreachableBlock(returnExpr.Span);

        var expressionType = returnExpr.TypeId.IsValid
            ? returnExpr.TypeId
            : (functionReturnType.IsValid ? functionReturnType : new TypeId(BaseTypes.UnitId));
        return CreatePoisonOperand(expressionType, returnExpr.Span, DiagnosticMessages.UnreachableAfterReturnReason);
    }

    private MirOperand ReportMissingIfElseValue(HirIf ifExpr)
    {
        EmitError(
            DiagnosticMessages.IfExpressionMissingElseNonUnitDuringMir,
            "E5330",
            ifExpr.Span,
            DiagnosticMessages.MissingElseBranchValueLabel);
        return CreatePoisonOperand(ifExpr.TypeId, ifExpr.Span, DiagnosticMessages.MissingNonUnitElseBranchReason);
    }

    private MirOperand ReportMissingReturnValue(HirReturn returnExpr, TypeId returnType)
    {
        EmitError(
            DiagnosticMessages.ReturnExpressionMissingValueNonUnitDuringMir,
            "E5330",
            returnExpr.Span,
            DiagnosticMessages.MissingReturnValueLabel);
        return CreatePoisonOperand(returnType, returnExpr.Span, DiagnosticMessages.MissingNonUnitReturnValueReason);
    }

    private MirOperand ConvertBreak(HirBreak breakExpr)
    {
        if (breakExpr.Value != null)
        {
            _ = ConvertExpr(breakExpr.Value);
        }

        if (_loopContextStack.Count == 0)
        {
            var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.BreakExpressionOnlyInsideLoop,
                "E5310");
            if (HasSpan(breakExpr.Span))
            {
                diagnostic.WithLabel(breakExpr.Span, DiagnosticMessages.BreakOutsideLoopLabel);
            }
            Diagnostics.Add(diagnostic);
            return CreatePoisonOperand(breakExpr.TypeId, breakExpr.Span, DiagnosticMessages.BreakOutsideLoopLabel);
        }

        var context = _loopContextStack.Peek();
        _currentBlock!.Terminator = new MirGoto
        {
            Target = context.Exit,
            Span = breakExpr.Span
        };

        MoveToSyntheticUnreachableBlock(breakExpr.Span);
        var expressionType = breakExpr.TypeId.IsValid
            ? breakExpr.TypeId
            : new TypeId(BaseTypes.NeverId);
        return CreatePoisonOperand(expressionType, breakExpr.Span, WellKnownStrings.AdditionalKeywords.Break);
    }

    private MirOperand ConvertContinue(HirContinue continueExpr)
    {
        if (_loopContextStack.Count == 0)
        {
            var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.ContinueExpressionOnlyInsideLoop,
                "E5310");
            if (HasSpan(continueExpr.Span))
            {
                diagnostic.WithLabel(continueExpr.Span, DiagnosticMessages.ContinueOutsideLoopLabel);
            }
            Diagnostics.Add(diagnostic);
            return CreatePoisonOperand(continueExpr.TypeId, continueExpr.Span, DiagnosticMessages.ContinueOutsideLoopLabel);
        }

        var context = _loopContextStack.Peek();
        _currentBlock!.Terminator = new MirGoto
        {
            Target = context.Header,
            Span = continueExpr.Span
        };

        MoveToSyntheticUnreachableBlock(continueExpr.Span);
        var expressionType = continueExpr.TypeId.IsValid
            ? continueExpr.TypeId
            : new TypeId(BaseTypes.NeverId);
        return CreatePoisonOperand(expressionType, continueExpr.Span, WellKnownStrings.AdditionalKeywords.Continue);
    }

    private MirOperand ConvertUnreachable(HirUnreachable unreachable)
    {
        _currentBlock!.Terminator = new MirUnreachable
        {
            Span = unreachable.Span
        };

        MoveToSyntheticUnreachableBlock(unreachable.Span);

        var expressionType = unreachable.TypeId.IsValid
            ? unreachable.TypeId
            : new TypeId(BaseTypes.NeverId);
        return CreatePoisonOperand(expressionType, unreachable.Span, WellKnownStrings.Keywords.Unreachable);
    }

    private void MoveToSyntheticUnreachableBlock(SourceSpan span)
    {
        var unreachable = NewBlock();
        unreachable.Terminator = new MirUnreachable { Span = span };
        _currentFunc!.BasicBlocks.Add(unreachable);
        _currentBlock = unreachable;
    }

    private static MirConstant CreateUnitConstant(SourceSpan span, TypeId typeId)
    {
        var resolvedType = typeId.IsValid ? typeId : new TypeId(BaseTypes.UnitId);
        return new MirConstant
        {
            Value = new MirConstantValue.UnitValue(),
            TypeId = resolvedType,
            Span = span
        };
    }

    private bool IsUnitLikeResultType(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (typeId.Value == BaseTypes.UnitId)
        {
            return true;
        }

        return false;
    }


    /// <summary>
    /// 从字面量值创建 MirConstant
    /// </summary>
    private static MirConstant CreateConstantFromLiteral(object? value)
    {
        return value switch
        {
            int intValue => new MirConstant
            {
                Value = new MirConstantValue.IntValue(intValue),
                TypeId = new TypeId(BaseTypes.IntId)
            },
            long longValue => new MirConstant
            {
                Value = new MirConstantValue.IntValue(longValue),
                TypeId = new TypeId(BaseTypes.IntId)
            },
            float floatValue => new MirConstant
            {
                Value = new MirConstantValue.FloatValue(floatValue),
                TypeId = new TypeId(BaseTypes.FloatId)
            },
            double doubleValue => new MirConstant
            {
                Value = new MirConstantValue.FloatValue(doubleValue),
                TypeId = new TypeId(BaseTypes.FloatId)
            },
            char charValue => new MirConstant
            {
                Value = new MirConstantValue.CharValue(charValue),
                TypeId = new TypeId(BaseTypes.CharId)
            },
            bool boolValue => new MirConstant
            {
                Value = new MirConstantValue.BoolValue(boolValue),
                TypeId = new TypeId(BaseTypes.BoolId)
            },
            string stringValue => new MirConstant
            {
                Value = new MirConstantValue.StringValue(stringValue),
                TypeId = new TypeId(BaseTypes.StringId)
            },
            null => new MirConstant
            {
                Value = new MirConstantValue.UnitValue(),
                TypeId = new TypeId(BaseTypes.UnitId)
            },
            _ => new MirConstant
            {
                Value = new MirConstantValue.IntValue(0),
                TypeId = new TypeId(BaseTypes.IntId)
            }
        };
    }
}
