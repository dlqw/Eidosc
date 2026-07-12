using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

public sealed partial class MirBuilder
{
    private MirOperand EnsureBooleanOperand(MirOperand operand, SourceSpan span)
    {
        return EnsureReadValue(operand, new TypeId(BaseTypes.BoolId), span);
    }

    private static BlockId ResolvePatternMissTarget(
        MirBasicBlock? nextCheckBlock,
        MirBasicBlock fallbackBlock,
        ref bool fallbackNeeded)
    {
        if (nextCheckBlock != null)
        {
            return nextCheckBlock.Id;
        }

        fallbackNeeded = true;
        return fallbackBlock.Id;
    }

    private void EmitBooleanBranch(
        MirOperand condition,
        BlockId trueTarget,
        BlockId falseTarget,
        SourceSpan span)
    {
        _currentBlock!.Terminator = new MirSwitch
        {
            Discriminant = condition,
            Branches =
            [
                new MirSwitchBranch
                {
                    Value = CreateBoolConstant(true, span),
                    Target = trueTarget
                }
            ],
            DefaultTarget = falseTarget,
            Span = span
        };
    }

    private MirOperand ConvertMatch(HirMatch match)
    {
        var scrutinee = ConvertExpr(match.Scrutinee);
        scrutinee = EnsureReadValue(scrutinee, match.Scrutinee.TypeId, match.Span);
        var scrutineePlace = EnsurePlaceOperand(scrutinee, match.Scrutinee.TypeId, match.Span);
        if (TryConvertSingleIrrefutableMatch(match, scrutineePlace, out var directResult))
        {
            return directResult;
        }

        var mergeBlock = NewBlock();
        var fallbackBlock = NewBlock();
        var resultPlace = NewTemp(match.TypeId);
        var checkBlock = _currentBlock;
        var fallbackNeeded = false;
        var matchHasPoisonedPattern = false;

        if (match.Branches.Count == 0)
        {
            EmitInitialization(resultPlace, ReportEmptyMatchValue(match), match.Span);
            _currentBlock!.Terminator = new MirGoto { Target = mergeBlock.Id, Span = match.Span };
        }
        else
        {
            for (var i = 0; i < match.Branches.Count && checkBlock != null; i++)
            {
                var branch = match.Branches[i];
                var isLast = i == match.Branches.Count - 1;

                _currentBlock = checkBlock;

                MirBasicBlock? nextCheckBlock = null;
                if (!isLast)
                {
                    nextCheckBlock = NewBlock();
                    _currentFunc!.BasicBlocks.Add(nextCheckBlock);
                }

                var patternContext = new PatternLoweringContext();
                var condition = EmitPatternCondition(branch.Pattern, scrutineePlace, match.Span, patternContext);
                matchHasPoisonedPattern |= patternContext.HasPoisonedPattern;
                condition = EnsureBooleanOperand(condition, match.Span);
                var conditionBlock = _currentBlock ?? checkBlock;

                if (TryGetBooleanConstant(condition, out var patternMatched) && !patternMatched)
                {
                    var missTarget = ResolvePatternMissTarget(nextCheckBlock, fallbackBlock, ref fallbackNeeded);
                    conditionBlock!.Terminator = new MirGoto
                    {
                        Target = missTarget,
                        Span = match.Span
                    };
                    checkBlock = nextCheckBlock;
                    continue;
                }

                var branchBlock = NewBlock();
                _currentFunc!.BasicBlocks.Add(branchBlock);

                if (TryGetBooleanConstant(condition, out patternMatched) && patternMatched)
                {
                    conditionBlock!.Terminator = new MirGoto
                    {
                        Target = branchBlock.Id,
                        Span = match.Span
                    };
                }
                else
                {
                    var defaultTarget = ResolvePatternMissTarget(nextCheckBlock, fallbackBlock, ref fallbackNeeded);
                    _currentBlock = conditionBlock;
                    EmitBooleanBranch(condition, branchBlock.Id, defaultTarget, match.Span);
                }

                _currentBlock = branchBlock;
                var bindingScope = new LocalBindingScope();

                try
                {
                    BindMatchPatternVariables(branch.Pattern, scrutineePlace, patternContext, bindingScope);

                    if (!EmitMatchBranchGuard(
                        branch.Guard,
                        nextCheckBlock,
                        fallbackBlock,
                        ref fallbackNeeded,
                        bindingScope,
                        ref matchHasPoisonedPattern))
                    {
                        checkBlock = nextCheckBlock;
                        continue;
                    }

                    var bodyResult = ConvertExpr(branch.Body);
                    EmitInitializationAndGoto(resultPlace, bodyResult, match.Span, mergeBlock.Id);
                }
                finally
                {
                    bindingScope.Restore(_variableLocals, _symbolLocals);
                }

                checkBlock = nextCheckBlock;
            }

            if (checkBlock != null)
            {
                fallbackNeeded = true;
                _currentBlock = checkBlock;
                _currentBlock.Terminator = new MirGoto { Target = fallbackBlock.Id, Span = match.Span };
            }

            if (fallbackNeeded)
            {
                _currentFunc!.BasicBlocks.Add(fallbackBlock);
                _currentBlock = fallbackBlock;
                if (matchHasPoisonedPattern)
                {
                    var fallbackValue = CreatePoisonOperand(
                        match.TypeId,
                        match.Span,
                        DiagnosticMessages.MatchContainsPoisonedPatternReason);
                    EmitInitializationAndGoto(resultPlace, fallbackValue, match.Span, mergeBlock.Id);
                }
                else
                {
                    if (!match.IsExhaustive)
                    {
                        ReportNonExhaustiveMatchFallback(match);
                    }

                    _currentBlock.Terminator = new MirUnreachable { Span = match.Span };
                }
            }
        }

        _currentFunc!.BasicBlocks.Add(mergeBlock);
        _currentBlock = mergeBlock;
        return resultPlace;
    }

    private bool TryConvertSingleIrrefutableMatch(
        HirMatch match,
        MirPlace scrutineePlace,
        out MirOperand result)
    {
        if (match.Branches.Count != 1)
        {
            result = null!;
            return false;
        }

        var branch = match.Branches[0];
        if (branch.Guard != null || !IsIrrefutablePattern(branch.Pattern))
        {
            result = null!;
            return false;
        }

        var bindingScope = new LocalBindingScope();
        try
        {
            BindMatchPatternVariables(branch.Pattern, scrutineePlace, new PatternLoweringContext(), bindingScope);
            result = ConvertExpr(branch.Body);
            return true;
        }
        finally
        {
            bindingScope.Restore(_variableLocals, _symbolLocals);
        }
    }

    private void ReportNonExhaustiveMatchFallback(HirMatch match)
    {
        var diagnostic = Diagnostic.Diagnostic.Warning(
            DiagnosticMessages.NonExhaustiveMatchFallbackDuringMirLowering,
            "W5331");
        if (HasSpan(match.Span))
        {
            diagnostic.WithLabel(match.Span, DiagnosticMessages.NonExhaustiveMatchFallbackLabel);
        }

        diagnostic.WithHelp(DiagnosticMessages.NonExhaustiveMatchFallbackHelp);
        Diagnostics.Add(diagnostic);
    }

    private MirOperand ReportEmptyMatchValue(HirMatch match)
    {
        EmitError(
            DiagnosticMessages.EmptyMatchExpressionDuringMirLowering,
            "E5330",
            match.Span,
            DiagnosticMessages.EmptyMatchExpressionLabel);
        return CreatePoisonOperand(match.TypeId, match.Span, DiagnosticMessages.EmptyMatchExpressionLabel);
    }

    private bool EmitMatchBranchGuard(
        HirNode? guard,
        MirBasicBlock? nextCheckBlock,
        MirBasicBlock fallbackBlock,
        ref bool fallbackNeeded,
        LocalBindingScope bindingScope,
        ref bool matchHasPoisonedPattern)
    {
        if (guard == null)
        {
            return true;
        }

        if (guard is HirSequentialGuard sequentialGuard)
        {
            foreach (var item in sequentialGuard.Guards)
            {
                if (!EmitMatchBranchGuard(
                    item,
                    nextCheckBlock,
                    fallbackBlock,
                    ref fallbackNeeded,
                    bindingScope,
                    ref matchHasPoisonedPattern))
                {
                    return false;
                }
            }

            return true;
        }

        var guardMissTarget = ResolvePatternMissTarget(nextCheckBlock, fallbackBlock, ref fallbackNeeded);

        if (guard is HirPatternGuard patternGuard)
        {
            var guardSourceValue = ConvertExpr(patternGuard.SourceExpression);
            guardSourceValue = EnsureReadValue(
                guardSourceValue,
                patternGuard.SourceExpression.TypeId,
                patternGuard.SourceExpression.Span);
            var guardSourcePlace = EnsurePlaceOperand(
                guardSourceValue,
                patternGuard.SourceExpression.TypeId,
                patternGuard.SourceExpression.Span);
            var guardPatternContext = new PatternLoweringContext();
            var guardMatched = EmitPatternCondition(
                patternGuard.Pattern,
                guardSourcePlace,
                patternGuard.Span,
                guardPatternContext);
            matchHasPoisonedPattern |= guardPatternContext.HasPoisonedPattern;
            guardMatched = EnsureBooleanOperand(guardMatched, patternGuard.Span);

            if (TryGetBooleanConstant(guardMatched, out var guardPatternMatched))
            {
                if (!guardPatternMatched)
                {
                    _currentBlock!.Terminator = new MirGoto
                    {
                        Target = guardMissTarget,
                        Span = patternGuard.Span
                    };
                    return false;
                }
            }
            else
            {
                var guardPassBlock = NewBlock();
                _currentFunc!.BasicBlocks.Add(guardPassBlock);
                EmitBooleanBranch(guardMatched, guardPassBlock.Id, guardMissTarget, patternGuard.Span);
                _currentBlock = guardPassBlock;
            }

            BindMatchPatternVariables(
                patternGuard.Pattern,
                guardSourcePlace,
                guardPatternContext,
                bindingScope);
            return true;
        }

        var guardValue = ConvertExpr(guard);
        guardValue = EnsureBooleanOperand(guardValue, guard.Span);
        if (TryGetBooleanConstant(guardValue, out var guardPassed))
        {
            if (!guardPassed)
            {
                _currentBlock!.Terminator = new MirGoto
                {
                    Target = guardMissTarget,
                    Span = guard.Span
                };
                return false;
            }

            return true;
        }

        var boolGuardPassBlock = NewBlock();
        _currentFunc!.BasicBlocks.Add(boolGuardPassBlock);
        EmitBooleanBranch(guardValue, boolGuardPassBlock.Id, guardMissTarget, guard.Span);
        _currentBlock = boolGuardPassBlock;
        return true;
    }

    private sealed class PatternLoweringContext
    {
        private Dictionary<HirViewPattern, MirPlace>? _viewResults;
        private Dictionary<string, PatternBindingLocalState>? _bindingLocals;

        public bool HasPoisonedPattern { get; set; }

        public bool TryGetViewResult(HirViewPattern pattern, out MirPlace result)
        {
            if (_viewResults != null &&
                _viewResults.TryGetValue(pattern, out result!))
            {
                return true;
            }

            result = null!;
            return false;
        }

        public void SetViewResult(HirViewPattern pattern, MirPlace result)
        {
            (_viewResults ??= new Dictionary<HirViewPattern, MirPlace>(
                ReferenceEqualityComparer.Instance))[pattern] = result;
        }

        public bool TryGetBindingLocal(string key, out PatternBindingLocalState state)
        {
            if (_bindingLocals != null &&
                _bindingLocals.TryGetValue(key, out state!))
            {
                return true;
            }

            state = null!;
            return false;
        }

        public void SetBindingLocal(string key, PatternBindingLocalState state)
        {
            (_bindingLocals ??= new Dictionary<string, PatternBindingLocalState>(
                StringComparer.Ordinal))[key] = state;
        }
    }

    private sealed class PatternBindingLocalState
    {
        public required LocalId LocalId { get; init; }
        private BlockId _firstInitializedBlock;
        private HashSet<BlockId>? _additionalInitializedBlocks;

        public bool IsInitializedIn(BlockId blockId)
        {
            return blockId == _firstInitializedBlock ||
                   _additionalInitializedBlocks?.Contains(blockId) == true;
        }

        public void MarkInitializedIn(BlockId blockId)
        {
            if (_firstInitializedBlock == BlockId.None)
            {
                _firstInitializedBlock = blockId;
                return;
            }

            if (blockId == _firstInitializedBlock)
            {
                return;
            }

            (_additionalInitializedBlocks ??= []).Add(blockId);
        }
    }

    private MirOperand EmitPatternCondition(
        HirPattern pattern,
        MirPlace valuePlace,
        SourceSpan span,
        PatternLoweringContext context)
    {
        return pattern switch
        {
            HirErrorPattern errorPattern => ReportHirErrorPattern(errorPattern, span, context),
            HirVarPattern => CreateBoolConstant(true, span),
            HirLiteralPattern literalPattern => EmitLiteralPatternCondition(literalPattern, valuePlace),
            HirCtorPattern ctorPattern => EmitCtorPatternCondition(ctorPattern, valuePlace, span, context),
            HirTuplePattern tuplePattern => EmitTuplePatternCondition(tuplePattern, valuePlace, span, context),
            HirListPattern listPattern => EmitListPatternCondition(listPattern, valuePlace, span, context),
            HirNotPattern notPattern => EmitNotPatternCondition(notPattern, valuePlace, span, context),
            HirOrPattern orPattern => EmitOrPatternCondition(orPattern, valuePlace, span, context),
            HirAndPattern andPattern => EmitAndPatternCondition(andPattern, valuePlace, span, context),
            HirRangePattern rangePattern => EmitRangePatternCondition(rangePattern, valuePlace, span),
            HirViewPattern viewPattern => EmitViewPatternCondition(viewPattern, valuePlace, span, context),
            HirAsPattern asPattern => EmitPatternCondition(asPattern.InnerPattern, valuePlace, span, context),
            _ => ReportUnsupportedHirPattern(pattern, span, context)
        };
    }

    private MirOperand ReportHirErrorPattern(
        HirErrorPattern pattern,
        SourceSpan span,
        PatternLoweringContext context)
    {
        var reason = string.IsNullOrWhiteSpace(pattern.Reason)
            ? DiagnosticMessages.HirErrorPatternReachedMirLowering
            : pattern.Reason;
        context.HasPoisonedPattern = true;
        EmitError(
            DiagnosticMessages.CannotLowerHirErrorPattern(reason),
            "E5332",
            pattern.Span,
            DiagnosticMessages.MirPatternPoisonLabel);
        return CreatePoisonOperand(new TypeId(BaseTypes.BoolId), span, reason);
    }

    private MirOperand ReportUnsupportedHirPattern(
        HirPattern pattern,
        SourceSpan span,
        PatternLoweringContext context)
    {
        var patternType = pattern.GetType().Name;
        context.HasPoisonedPattern = true;
        EmitError(
            DiagnosticMessages.UnsupportedHirPatternDuringMirLowering(patternType),
            "E5332",
            pattern.Span,
            DiagnosticMessages.MirPatternPoisonLabel);
        return CreatePoisonOperand(
            new TypeId(BaseTypes.BoolId),
            span,
            DiagnosticMessages.UnsupportedHirPatternReason(patternType));
    }

    private MirOperand EmitOrPatternCondition(
        HirOrPattern orPattern,
        MirPlace valuePlace,
        SourceSpan span,
        PatternLoweringContext context)
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var left = EmitPatternCondition(orPattern.Left, valuePlace, span, context);
        left = EnsureBooleanOperand(left, span);
        var result = NewTemp(boolType);
        EmitInitialization(result, left, span);

        if (TryGetBooleanConstant(left, out var leftConstant))
        {
            if (leftConstant)
            {
                return result;
            }

            var rightOnly = EmitPatternCondition(orPattern.Right, valuePlace, span, context);
            rightOnly = EnsureReadValue(rightOnly, boolType, span);
            EmitStore(result, rightOnly, span);
            return result;
        }

        var rightBlock = NewBlock();
        var mergeBlock = NewBlock();
        _currentFunc!.BasicBlocks.Add(rightBlock);
        _currentFunc.BasicBlocks.Add(mergeBlock);
        EmitBooleanBranch(left, mergeBlock.Id, rightBlock.Id, span);

        _currentBlock = rightBlock;
        var right = EmitPatternCondition(orPattern.Right, valuePlace, span, context);
        right = EnsureBooleanOperand(right, span);
        EmitStore(result, right, span);
        _currentBlock.Terminator = new MirGoto
        {
            Target = mergeBlock.Id,
            Span = span
        };

        _currentBlock = mergeBlock;
        return result;
    }

    private MirOperand EmitAndPatternCondition(
        HirAndPattern andPattern,
        MirPlace valuePlace,
        SourceSpan span,
        PatternLoweringContext context)
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var left = EmitPatternCondition(andPattern.Left, valuePlace, span, context);
        left = EnsureBooleanOperand(left, span);
        var result = NewTemp(boolType);
        EmitInitialization(result, left, span);

        if (TryGetBooleanConstant(left, out var leftConstant))
        {
            if (!leftConstant)
            {
                return result;
            }

            var rightOnly = EmitPatternCondition(andPattern.Right, valuePlace, span, context);
            rightOnly = EnsureReadValue(rightOnly, boolType, span);
            EmitStore(result, rightOnly, span);
            return result;
        }

        var rightBlock = NewBlock();
        var mergeBlock = NewBlock();
        _currentFunc!.BasicBlocks.Add(rightBlock);
        _currentFunc.BasicBlocks.Add(mergeBlock);
        EmitBooleanBranch(left, rightBlock.Id, mergeBlock.Id, span);

        _currentBlock = rightBlock;
        var right = EmitPatternCondition(andPattern.Right, valuePlace, span, context);
        right = EnsureBooleanOperand(right, span);
        EmitStore(result, right, span);
        _currentBlock.Terminator = new MirGoto
        {
            Target = mergeBlock.Id,
            Span = span
        };

        _currentBlock = mergeBlock;
        return result;
    }

    private MirOperand EmitNotPatternCondition(
        HirNotPattern notPattern,
        MirPlace valuePlace,
        SourceSpan span,
        PatternLoweringContext context)
    {
        var inner = EmitPatternCondition(notPattern.InnerPattern, valuePlace, span, context);
        return EmitBooleanNegation(inner, span);
    }

    private MirOperand EmitLiteralPatternCondition(HirLiteralPattern literalPattern, MirPlace valuePlace)
    {
        var literalConstant = CreateConstantFromLiteral(literalPattern.Value);
        var comparisonType = valuePlace.TypeId.IsValid
            ? valuePlace.TypeId
            : (literalPattern.TypeId.IsValid ? literalPattern.TypeId : literalConstant.TypeId);

        literalConstant = NormalizePatternLiteralConstant(literalConstant, comparisonType, literalPattern.Span);
        literalConstant = literalConstant with { Span = literalPattern.Span };
        return EmitPatternComparison(valuePlace, comparisonType, literalConstant, BinaryOp.Eq, literalPattern.Span);
    }

    private MirOperand EmitRangePatternCondition(HirRangePattern rangePattern, MirPlace valuePlace, SourceSpan span)
    {
        var comparisonType = valuePlace.TypeId.IsValid
            ? valuePlace.TypeId
            : (rangePattern.TypeId.IsValid ? rangePattern.TypeId : new TypeId(BaseTypes.IntId));

        var start = CreateConstantFromLiteral(rangePattern.Start.Value);
        var end = CreateConstantFromLiteral(rangePattern.End.Value);
        start = NormalizePatternLiteralConstant(start, comparisonType, rangePattern.Start.Span);
        end = NormalizePatternLiteralConstant(end, comparisonType, rangePattern.End.Span);

        var ge = EmitPatternComparison(valuePlace, comparisonType, start, BinaryOp.Ge, rangePattern.Start.Span);
        var le = EmitPatternComparison(valuePlace, comparisonType, end, BinaryOp.Le, rangePattern.End.Span);
        return EmitBooleanConjunction(ge, le, span);
    }

    private MirOperand EmitViewPatternCondition(
        HirViewPattern viewPattern,
        MirPlace valuePlace,
        SourceSpan span,
        PatternLoweringContext context)
    {
        var viewedPlace = EnsureViewPatternResult(viewPattern, valuePlace, context);
        return EmitPatternCondition(viewPattern.InnerPattern, viewedPlace, span, context);
    }

    private MirPlace EnsureViewPatternResult(
        HirViewPattern viewPattern,
        MirPlace sourcePlace,
        PatternLoweringContext context)
    {
        if (context.TryGetViewResult(viewPattern, out var cached))
        {
            return cached;
        }

        var viewFunction = ConvertExpr(viewPattern.View);
        var callTargetType = ResolveViewPatternResultType(viewPattern, viewFunction);
        var callResult = NewTemp(callTargetType);
        var call = new MirCall
        {
            Target = callResult,
            Function = viewFunction,
            Arguments = [PrepareCallArgument(sourcePlace, sourcePlace.TypeId, viewPattern.Span, forceCopy: true)],
            Span = viewPattern.Span
        };
        _currentBlock!.Instructions.Add(call);
        context.SetViewResult(viewPattern, callResult);
        return callResult;
    }

    private TypeId ResolveViewPatternResultType(HirViewPattern viewPattern, MirOperand viewFunction)
    {
        if (viewPattern.ViewResultTypeId.IsValid)
        {
            return viewPattern.ViewResultTypeId;
        }

        if (viewPattern.InnerPattern.TypeId.IsValid)
        {
            return viewPattern.InnerPattern.TypeId;
        }

        if (TryResolveFunctionReturnType(viewFunction, out var functionReturnType))
        {
            return functionReturnType;
        }

        return TypeId.None;
    }

    private static MirConstant NormalizePatternLiteralConstant(
        MirConstant literalConstant,
        TypeId comparisonType,
        SourceSpan span)
    {
        if (!comparisonType.IsValid)
        {
            return literalConstant;
        }

        if (comparisonType.Value == BaseTypes.IntId &&
            literalConstant.Value is MirConstantValue.CharValue charValue)
        {
            return CreateIntConstant(charValue.Value, span);
        }

        if (comparisonType.Value == BaseTypes.CharId &&
            literalConstant.Value is MirConstantValue.IntValue intValue)
        {
            return new MirConstant
            {
                Value = new MirConstantValue.CharValue((char)intValue.Value),
                TypeId = new TypeId(BaseTypes.CharId),
                Span = span
            };
        }

        return literalConstant;
    }

    private MirOperand EmitCtorPatternCondition(
        HirCtorPattern ctorPattern,
        MirPlace valuePlace,
        SourceSpan span,
        PatternLoweringContext context)
    {
        var intType = new TypeId(BaseTypes.IntId);
        var scrutineeValue = EnsureReadValue(
            valuePlace,
            valuePlace.TypeId.IsValid ? valuePlace.TypeId : ctorPattern.TypeId,
            ctorPattern.Span);
        var tagPlace = NewTemp(intType);
        _currentBlock!.Instructions.Add(new MirCall
        {
            Target = tagPlace,
            Function = MirRuntimeFunctions.CreateFunctionRef("type_id", intType, ctorPattern.Span),
            Arguments = [scrutineeValue],
            Span = ctorPattern.Span
        });

        var expectedTag = CreateIntConstant(
            ConstructorRuntimeTypeId.Compute(_symbolTable, ctorPattern.ConstructorSymbolId, ctorPattern.ConstructorName),
            ctorPattern.Span);
        var tagMatched = EmitPatternComparison(tagPlace, intType, expectedTag, BinaryOp.Eq, ctorPattern.Span);

        if (ctorPattern.Fields.Count == 0)
        {
            return tagMatched;
        }

        var boolType = new TypeId(BaseTypes.BoolId);
        tagMatched = EnsureBooleanOperand(tagMatched, ctorPattern.Span);
        var result = NewTemp(boolType);
        EmitInitialization(result, tagMatched, ctorPattern.Span);

        if (TryGetBooleanConstant(tagMatched, out var tagConstant))
        {
            if (!tagConstant)
            {
                return result;
            }

            var fieldCondition = EmitCtorPatternFieldConditions(ctorPattern, valuePlace, span, context);
            fieldCondition = EnsureBooleanOperand(fieldCondition, span);
            EmitStore(result, fieldCondition, span);
            return result;
        }

        var fieldCheckBlock = NewBlock();
        var mergeBlock = NewBlock();
        _currentFunc!.BasicBlocks.Add(fieldCheckBlock);
        _currentFunc.BasicBlocks.Add(mergeBlock);
        EmitBooleanBranch(tagMatched, fieldCheckBlock.Id, mergeBlock.Id, ctorPattern.Span);

        _currentBlock = fieldCheckBlock;
        var guardedFieldCondition = EmitCtorPatternFieldConditions(ctorPattern, valuePlace, span, context);
        guardedFieldCondition = EnsureBooleanOperand(guardedFieldCondition, span);
        EmitStore(result, guardedFieldCondition, span);
        _currentBlock.Terminator = new MirGoto
        {
            Target = mergeBlock.Id,
            Span = span
        };

        _currentBlock = mergeBlock;
        return result;
    }

    private MirOperand EmitCtorPatternFieldConditions(
        HirCtorPattern ctorPattern,
        MirPlace valuePlace,
        SourceSpan span,
        PatternLoweringContext context)
    {
        MirOperand condition = CreateBoolConstant(true, span);
        for (var i = 0; i < ctorPattern.Fields.Count; i++)
        {
            var field = ctorPattern.Fields[i];
            var fieldName = NormalizeConstructorFieldName(ctorPattern.ConstructorName, field.FieldName, i);
            var fieldPlace = new MirPlace
            {
                Kind = PlaceKind.Field,
                Base = valuePlace,
                FieldName = fieldName,
                TypeId = field.Pattern.TypeId,
                Span = field.Pattern.Span
            };

            var fieldCondition = EmitPatternCondition(field.Pattern, fieldPlace, field.Pattern.Span, context);
            condition = EmitBooleanConjunction(condition, fieldCondition, field.Pattern.Span);
        }

        return condition;
    }

    private MirOperand EmitTuplePatternCondition(
        HirTuplePattern tuplePattern,
        MirPlace valuePlace,
        SourceSpan span,
        PatternLoweringContext context)
    {
        MirOperand condition = CreateBoolConstant(true, span);
        for (var i = 0; i < tuplePattern.Elements.Count; i++)
        {
            var element = tuplePattern.Elements[i];
            var elementType = ResolveTuplePatternElementType(valuePlace.TypeId, i, element.TypeId);
            var elementPlace = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = valuePlace,
                Index = CreateIntConstant(i, element.Span),
                IndexAccessKind = MirIndexAccessKind.Aggregate,
                TypeId = elementType,
                Span = element.Span
            };

            var elementCondition = EmitPatternCondition(element, elementPlace, element.Span, context);
            condition = EmitBooleanConjunction(condition, elementCondition, element.Span);
        }

        return condition;
    }

    private MirOperand EmitListPatternCondition(
        HirListPattern listPattern,
        MirPlace valuePlace,
        SourceSpan span,
        PatternLoweringContext context)
    {
        var lengthCondition = EmitListPatternLengthCondition(listPattern, valuePlace, span);
        if (listPattern.Elements.Count == 0 && listPattern.SuffixElements.Count == 0)
        {
            return lengthCondition;
        }

        var boolType = new TypeId(BaseTypes.BoolId);
        lengthCondition = EnsureBooleanOperand(lengthCondition, span);
        var result = NewTemp(boolType);
        EmitInitialization(result, lengthCondition, span);

        if (TryGetBooleanConstant(lengthCondition, out var lengthConstant))
        {
            if (!lengthConstant)
            {
                return result;
            }

            var elementCondition = EmitListPatternElementConditions(listPattern, valuePlace, context, span);
            EmitStore(result, elementCondition, span);
            return result;
        }

        var elementCheckBlock = NewBlock();
        var mergeBlock = NewBlock();
        _currentFunc!.BasicBlocks.Add(elementCheckBlock);
        _currentFunc.BasicBlocks.Add(mergeBlock);
        EmitBooleanBranch(lengthCondition, elementCheckBlock.Id, mergeBlock.Id, span);

        _currentBlock = elementCheckBlock;
        var guardedCondition = EmitListPatternElementConditions(listPattern, valuePlace, context, span);
        EmitStore(result, guardedCondition, span);
        _currentBlock.Terminator = new MirGoto
        {
            Target = mergeBlock.Id,
            Span = span
        };

        _currentBlock = mergeBlock;
        return result;
    }

    private MirOperand EmitListPatternLengthCondition(
        HirListPattern listPattern,
        MirPlace valuePlace,
        SourceSpan span)
    {
        var intType = new TypeId(BaseTypes.IntId);
        var expectedLength = listPattern.Elements.Count + listPattern.SuffixElements.Count;
        MirOperand listLength = TryGetKnownListLength(valuePlace, out var knownLength)
            ? CreateIntConstant(knownLength, span)
            : EmitRuntimeArrayLength(valuePlace, span);
        listLength = EnsureReadValue(listLength, intType, span);

        var comparisonOperator = listPattern.HasRest ? BinaryOp.Ge : BinaryOp.Eq;
        return EmitPatternComparison(
            listLength,
            intType,
            CreateIntConstant(expectedLength, span),
            comparisonOperator,
            span);
    }

    private MirOperand EmitListPatternElementConditions(
        HirListPattern listPattern,
        MirPlace valuePlace,
        PatternLoweringContext context,
        SourceSpan span)
    {
        MirOperand condition = CreateBoolConstant(true, span);
        for (var i = 0; i < listPattern.Elements.Count; i++)
        {
            var element = listPattern.Elements[i];
            var elementType = ResolveListPatternElementType(listPattern, valuePlace.TypeId, element.TypeId);
            var elementPlace = CreateListPatternElementPlace(valuePlace, elementType, i, element.Span);
            var elementCondition = EmitPatternCondition(element, elementPlace, element.Span, context);
            condition = EmitBooleanConjunction(condition, elementCondition, element.Span);
        }

        for (var i = 0; i < listPattern.SuffixElements.Count; i++)
        {
            var element = listPattern.SuffixElements[i];
            var elementType = ResolveListPatternElementType(listPattern, valuePlace.TypeId, element.TypeId);
            var elementPlace = CreateListPatternSuffixElementPlace(listPattern, valuePlace, elementType, i, element.Span);
            var elementCondition = EmitPatternCondition(element, elementPlace, element.Span, context);
            condition = EmitBooleanConjunction(condition, elementCondition, element.Span);
        }

        return condition;
    }

    private MirOperand EmitPatternComparison(
        MirOperand left,
        TypeId leftType,
        MirConstant right,
        BinaryOp comparisonOperator,
        SourceSpan span)
    {
        var lhs = EnsureReadValue(left, leftType, span);

        if (comparisonOperator is BinaryOp.Eq or BinaryOp.Ne &&
            IsStringType(leftType, lhs.TypeId) &&
            IsStringType(right.TypeId))
        {
            var equals = EmitRuntimeStringEquals(lhs, right, span);
            return comparisonOperator == BinaryOp.Eq
                ? equals
                : EmitBooleanNegation(equals, span);
        }

        var target = NewTemp(new TypeId(BaseTypes.BoolId));
        _currentBlock!.Instructions.Add(new MirBinOp
        {
            Target = target,
            Operator = comparisonOperator,
            Left = lhs,
            Right = right,
            Span = span
        });

        return target;
    }

    private MirOperand EmitRuntimeStringEquals(MirOperand left, MirOperand right, SourceSpan span)
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var stringType = new TypeId(BaseTypes.StringId);
        var target = NewTemp(boolType);

        _currentBlock!.Instructions.Add(new MirCall
        {
            Target = target,
            Function = MirRuntimeFunctions.CreateFunctionRef("string_equals", boolType, span),
            Arguments =
            [
                PrepareReadonlyStringEqualsArgument(left, stringType, span),
                PrepareReadonlyStringEqualsArgument(right, stringType, span)
            ],
            Span = span
        });

        ClearKnownListLength(target);
        ClearRuntimeArrayLocal(target);
        return target;
    }

    private static bool IsStringType(TypeId primary, TypeId? fallback = null)
    {
        if (primary.IsValid)
        {
            return primary.Value == BaseTypes.StringId;
        }

        return fallback is { IsValid: true } fallbackType &&
               fallbackType.Value == BaseTypes.StringId;
    }

    private MirOperand EmitBooleanConjunction(MirOperand left, MirOperand right, SourceSpan span)
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var lhs = EnsureBooleanOperand(left, span);
        var rhs = EnsureBooleanOperand(right, span);
        var target = NewTemp(boolType);
        _currentBlock!.Instructions.Add(new MirBinOp
        {
            Target = target,
            Operator = BinaryOp.And,
            Left = lhs,
            Right = rhs,
            Span = span
        });

        return target;
    }

    private MirOperand EmitBooleanDisjunction(MirOperand left, MirOperand right, SourceSpan span)
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var lhs = EnsureBooleanOperand(left, span);
        var rhs = EnsureBooleanOperand(right, span);
        var target = NewTemp(boolType);
        _currentBlock!.Instructions.Add(new MirBinOp
        {
            Target = target,
            Operator = BinaryOp.Or,
            Left = lhs,
            Right = rhs,
            Span = span
        });

        return target;
    }

    private MirOperand EmitBooleanNegation(MirOperand operand, SourceSpan span)
    {
        var boolType = new TypeId(BaseTypes.BoolId);
        var source = EnsureBooleanOperand(operand, span);
        var target = NewTemp(boolType);
        _currentBlock!.Instructions.Add(new MirUnaryOp
        {
            Target = target,
            Operator = UnaryOp.Not,
            Operand = source,
            Span = span
        });

        return target;
    }

    private void BindMatchPatternVariables(HirPattern pattern, MirPlace valuePlace, PatternLoweringContext context)
    {
        BindMatchPatternVariables(pattern, valuePlace, context, null);
    }

    private void BindMatchPatternVariables(
        HirPattern pattern,
        MirPlace valuePlace,
        PatternLoweringContext context,
        LocalBindingScope? bindingScope)
    {
        switch (pattern)
        {
            case HirVarPattern varPattern when !varPattern.IsWildcard:
                BindPatternVariable(
                    varPattern.Name,
                    varPattern.SymbolId,
                    valuePlace,
                    varPattern.TypeId,
                    varPattern.BindingMode,
                    varPattern.Span,
                    context,
                    bindingScope);
                return;

            case HirCtorPattern ctorPattern:
                for (var i = 0; i < ctorPattern.Fields.Count; i++)
                {
                    var field = ctorPattern.Fields[i];
                    var fieldName = NormalizeConstructorFieldName(ctorPattern.ConstructorName, field.FieldName, i);
                    var fieldPlace = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = valuePlace,
                        FieldName = fieldName,
                        TypeId = field.Pattern.TypeId,
                        Span = field.Pattern.Span
                    };
                    BindMatchPatternVariables(field.Pattern, fieldPlace, context, bindingScope);
                }
                return;

            case HirTuplePattern tuplePattern:
                for (var i = 0; i < tuplePattern.Elements.Count; i++)
                {
                    var element = tuplePattern.Elements[i];
                    var elementType = ResolveTuplePatternElementType(valuePlace.TypeId, i, element.TypeId);
                    var elementPlace = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = valuePlace,
                        Index = CreateIntConstant(i, element.Span),
                        IndexAccessKind = MirIndexAccessKind.Aggregate,
                        TypeId = elementType,
                        Span = element.Span
                    };
                    BindMatchPatternVariables(element, elementPlace, context, bindingScope);
                }
                return;

            case HirListPattern listPattern:
                BindListPatternVariables(listPattern, valuePlace, context, bindingScope);
                return;

            case HirOrPattern orPattern:
                BindOrPatternVariables(orPattern, valuePlace, context, bindingScope);
                return;

            case HirAndPattern andPattern:
                BindMatchPatternVariables(andPattern.Left, valuePlace, context, bindingScope);
                BindMatchPatternVariables(andPattern.Right, valuePlace, context, bindingScope);
                return;

            case HirNotPattern:
                return;

            case HirViewPattern viewPattern:
            {
                var viewedPlace = EnsureViewPatternResult(viewPattern, valuePlace, context);
                BindMatchPatternVariables(viewPattern.InnerPattern, viewedPlace, context, bindingScope);
                return;
            }

            case HirAsPattern asPattern:
                BindMatchPatternVariables(asPattern.InnerPattern, valuePlace, context, bindingScope);
                BindPatternVariable(
                    asPattern.Name,
                    asPattern.SymbolId,
                    valuePlace,
                    asPattern.TypeId,
                    asPattern.BindingMode,
                    asPattern.Span,
                    context,
                    bindingScope);
                return;

            default:
                return;
        }
    }

    private void BindOrPatternVariables(
        HirOrPattern orPattern,
        MirPlace valuePlace,
        PatternLoweringContext context,
        LocalBindingScope? bindingScope)
    {
        var leftCondition = EmitPatternCondition(orPattern.Left, valuePlace, orPattern.Span, context);
        var discriminant = EnsureBooleanOperand(leftCondition, orPattern.Span);

        var leftBindBlock = NewBlock();
        var rightBindBlock = NewBlock();
        var mergeBlock = NewBlock();

        EmitBooleanBranch(discriminant, leftBindBlock.Id, rightBindBlock.Id, orPattern.Span);

        _currentFunc!.BasicBlocks.Add(leftBindBlock);
        _currentBlock = leftBindBlock;
        BindMatchPatternVariables(orPattern.Left, valuePlace, context, bindingScope);
        _currentBlock.Terminator = new MirGoto { Target = mergeBlock.Id, Span = orPattern.Span };

        _currentFunc.BasicBlocks.Add(rightBindBlock);
        _currentBlock = rightBindBlock;
        BindMatchPatternVariables(orPattern.Right, valuePlace, context, bindingScope);
        _currentBlock.Terminator = new MirGoto { Target = mergeBlock.Id, Span = orPattern.Span };

        _currentFunc.BasicBlocks.Add(mergeBlock);
        _currentBlock = mergeBlock;
    }

    private void BindListPatternVariables(
        HirListPattern listPattern,
        MirPlace valuePlace,
        PatternLoweringContext context,
        LocalBindingScope? bindingScope)
    {
        BindListPatternElementVariables(listPattern, valuePlace, context, bindingScope);

        if (TryResolveListPatternRestPlace(listPattern, valuePlace, out var restPlace))
        {
            BindMatchPatternVariables(listPattern.RestPattern!, restPlace, context, bindingScope);
        }
    }

    private void BindListPatternElementVariables(
        HirListPattern listPattern,
        MirPlace valuePlace,
        PatternLoweringContext context,
        LocalBindingScope? bindingScope)
    {
        for (var i = 0; i < listPattern.Elements.Count; i++)
        {
            var element = listPattern.Elements[i];
            var elementType = ResolveListPatternElementType(listPattern, valuePlace.TypeId, element.TypeId);
            var elementPlace = CreateListPatternElementPlace(valuePlace, elementType, i, element.Span);
            BindMatchPatternVariables(element, elementPlace, context, bindingScope);
        }

        for (var i = 0; i < listPattern.SuffixElements.Count; i++)
        {
            var element = listPattern.SuffixElements[i];
            var elementType = ResolveListPatternElementType(listPattern, valuePlace.TypeId, element.TypeId);
            var elementPlace = CreateListPatternSuffixElementPlace(listPattern, valuePlace, elementType, i, element.Span);
            BindMatchPatternVariables(element, elementPlace, context, bindingScope);
        }
    }

    private bool TryResolveListPatternRestPlace(
        HirListPattern listPattern,
        MirPlace valuePlace,
        out MirPlace restPlace)
    {
        restPlace = default!;
        if (listPattern.RestPattern == null ||
            listPattern.RestPattern is HirVarPattern { IsWildcard: true })
        {
            return false;
        }

        if (listPattern.Elements.Count == 0 && listPattern.SuffixElements.Count == 0)
        {
            restPlace = valuePlace;
            return true;
        }

        var elementType = ResolveListPatternElementType(listPattern, valuePlace.TypeId);
        var restType = listPattern.RestPattern.TypeId.IsValid
            ? listPattern.RestPattern.TypeId
            : valuePlace.TypeId;
        restPlace = MaterializeListTail(
            valuePlace,
            listPattern.Elements.Count,
            listPattern.SuffixElements.Count,
            restType,
            elementType,
            listPattern.RestPattern.Span);
        return true;
    }

    private MirPlace CreateListPatternSuffixElementPlace(
        HirListPattern listPattern,
        MirPlace listPlace,
        TypeId elementType,
        int suffixIndex,
        SourceSpan span)
    {
        var intType = new TypeId(BaseTypes.IntId);
        MirOperand lengthValue = TryGetKnownListLength(listPlace, out var knownLength)
            ? CreateIntConstant(knownLength, span)
            : EmitRuntimeArrayLength(listPlace, span);
        lengthValue = EnsureReadValue(lengthValue, intType, span);

        var offsetFromEnd = listPattern.SuffixElements.Count - suffixIndex;
        var index = NewTemp(intType);
        _currentBlock!.Instructions.Add(new MirBinOp
        {
            Target = index,
            Operator = BinaryOp.Sub,
            Left = lengthValue,
            Right = CreateIntConstant(offsetFromEnd, span),
            Span = span
        });

        return CreateListPatternElementPlace(listPlace, elementType, index, span);
    }

    private MirPlace CreateListPatternElementPlace(
        MirPlace listPlace,
        TypeId elementType,
        int index,
        SourceSpan span)
    {
        return CreateListPatternElementPlace(listPlace, elementType, CreateIntConstant(index, span), span);
    }

    private static MirPlace CreateListPatternElementPlace(
        MirPlace listPlace,
        TypeId elementType,
        MirOperand index,
        SourceSpan span)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = listPlace,
            Index = index,
            IndexAccessKind = MirIndexAccessKind.RuntimeArray,
            TypeId = elementType,
            Span = span
        };
    }

    private TypeId ResolveListPatternElementType(
        HirListPattern listPattern,
        TypeId listTypeId,
        TypeId? fallbackType = null)
    {
        if (fallbackType is { } concreteFallbackType &&
            IsLowerablePatternTypeId(concreteFallbackType))
        {
            return concreteFallbackType;
        }

        foreach (var element in listPattern.Elements)
        {
            if (IsLowerablePatternTypeId(element.TypeId))
            {
                return element.TypeId;
            }
        }

        if (TryResolveListElementTypeFromListType(listTypeId, out var elementTypeId))
        {
            return elementTypeId;
        }

        return TypeId.None;
    }

    private bool TryResolveListElementTypeFromListType(TypeId listTypeId, out TypeId elementTypeId)
    {
        elementTypeId = TypeId.None;
        if (!listTypeId.IsValid)
        {
            return false;
        }

        if (_typeDescriptorsById.TryGetValue(listTypeId.Value, out var descriptor))
        {
            if (TryResolveListElementTypeFromDescriptor(descriptor, out elementTypeId))
            {
                return true;
            }
        }

        if (!_dynamicTypeKeysById.TryGetValue(listTypeId.Value, out var typeKey))
        {
            return false;
        }

        if ((TryParseWrappedTypeId(typeKey, "Ref(", out var referencedTypeId) ||
             TryParseWrappedTypeId(typeKey, "MRef(", out referencedTypeId)) &&
            TryResolveListElementTypeFromListType(referencedTypeId, out elementTypeId))
        {
            return true;
        }

        return TypeKeyParsing.TryParseTyConTypeKey(typeKey, out _, out var typeArguments) &&
               typeArguments.Count > 0 &&
               IsLowerablePatternTypeId(elementTypeId = typeArguments[0]);
    }

    private bool TryResolveListElementTypeFromDescriptor(
        TypeDescriptor descriptor,
        out TypeId elementTypeId)
    {
        elementTypeId = TypeId.None;
        switch (descriptor)
        {
            case TypeDescriptor.TyCon { TypeArgs.Length: > 0 } tyCon:
                elementTypeId = tyCon.TypeArgs[0];
                return IsLowerablePatternTypeId(elementTypeId);

            case TypeDescriptor.Ref reference:
                return TryResolveListElementTypeFromListType(reference.Inner, out elementTypeId);

            case TypeDescriptor.MutRef reference:
                return TryResolveListElementTypeFromListType(reference.Inner, out elementTypeId);

            default:
                return false;
        }
    }

    private static bool IsLowerablePatternTypeId(TypeId typeId)
    {
        return typeId.IsValid && typeId.Value > 0;
    }

    private TypeId ResolveTuplePatternElementType(TypeId tupleTypeId, int elementIndex, TypeId fallbackType)
    {
        if (!tupleTypeId.IsValid ||
            !_dynamicTypeKeysById.TryGetValue(tupleTypeId.Value, out var tupleTypeKey) ||
            !TypeKeyParsing.TryParseTupleTypeKey(tupleTypeKey, out var elementTypes) ||
            elementIndex < 0 ||
            elementIndex >= elementTypes.Count ||
            !elementTypes[elementIndex].IsValid)
        {
            return fallbackType;
        }

        return elementTypes[elementIndex];
    }

    private static bool TryParseTypeIdToken(string token, out TypeId typeId)
    {
        typeId = TypeId.None;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var normalized = token.StartsWith('T') ? token[1..] : token;
        return int.TryParse(normalized, out var typeIdValue) && typeIdValue > 0 && (typeId = new TypeId(typeIdValue)).IsValid;
    }

    private MirPlace MaterializeListTail(
        MirPlace sourcePlace,
        int startIndex,
        int suffixCount,
        TypeId listTypeId,
        TypeId elementTypeId,
        SourceSpan span)
    {
        if (startIndex <= 0 && suffixCount <= 0)
        {
            return sourcePlace;
        }

        if (TryGetKnownListLength(sourcePlace, out var knownLength))
        {
            return MaterializeKnownLengthListSlice(sourcePlace, knownLength, startIndex, suffixCount, listTypeId, elementTypeId, span);
        }

        return MaterializeDynamicListSlice(sourcePlace, startIndex, suffixCount, listTypeId, elementTypeId, span);
    }

    private MirPlace MaterializeKnownLengthListSlice(
        MirPlace sourcePlace,
        int knownLength,
        int startIndex,
        int suffixCount,
        TypeId listTypeId,
        TypeId elementTypeId,
        SourceSpan span)
    {
        var endExclusive = Math.Max(startIndex, knownLength - suffixCount);
        var tailLength = Math.Max(endExclusive - startIndex, 0);
        var elementSize = GetRuntimeElementSize(elementTypeId);
        var tail = EmitRuntimeArrayNew(listTypeId, tailLength, elementSize, span);

        for (var i = startIndex; i < endExclusive; i++)
        {
            var loaded = LoadListPatternElement(sourcePlace, elementTypeId, i, span);
            AppendRuntimeArrayElement(tail, loaded, elementTypeId, elementSize, span);
        }

        RegisterKnownListLength(tail, tailLength);
        return tail;
    }

    private MirPlace MaterializeDynamicListSlice(
        MirPlace sourcePlace,
        int startIndex,
        int suffixCount,
        TypeId listTypeId,
        TypeId elementTypeId,
        SourceSpan span)
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var elementSize = GetRuntimeElementSize(elementTypeId);
        var tail = EmitRuntimeArrayNew(listTypeId, 8, elementSize, span);
        ClearKnownListLength(tail);

        MirOperand lengthValue = EmitRuntimeArrayLength(sourcePlace, span);
        lengthValue = EnsureReadValue(lengthValue, intType, span);
        var endIndex = NewTemp(intType);
        _currentBlock!.Instructions.Add(new MirBinOp
        {
            Target = endIndex,
            Operator = BinaryOp.Sub,
            Left = lengthValue,
            Right = CreateIntConstant(suffixCount, span),
            Span = span
        });

        var indexLocal = NewLocal("$match_tail_i", intType, isMutable: true);
        var indexPlace = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = indexLocal,
            TypeId = intType,
            Span = span
        };
        EmitInitialization(indexPlace, CreateIntConstant(startIndex, span), span);

        var headerBlock = NewBlock();
        var bodyBlock = NewBlock();
        var incrementBlock = NewBlock();
        var exitBlock = NewBlock();
        _currentFunc!.BasicBlocks.Add(headerBlock);
        _currentFunc.BasicBlocks.Add(bodyBlock);
        _currentFunc.BasicBlocks.Add(incrementBlock);
        _currentFunc.BasicBlocks.Add(exitBlock);

        _currentBlock!.Terminator = new MirGoto
        {
            Target = headerBlock.Id,
            Span = span
        };

        _currentBlock = headerBlock;
        var indexValue = EnsureReadValue(indexPlace, intType, span);
        var loopCondition = NewTemp(boolType);
        _currentBlock.Instructions.Add(new MirBinOp
        {
            Target = loopCondition,
            Operator = BinaryOp.Lt,
            Left = indexValue,
            Right = endIndex,
            Span = span
        });
        _currentBlock.Terminator = new MirSwitch
        {
            Discriminant = loopCondition,
            Branches =
            [
                new MirSwitchBranch
                {
                    Value = CreateBoolConstant(true, span),
                    Target = bodyBlock.Id
                }
            ],
            DefaultTarget = exitBlock.Id,
            Span = span
        };

        _currentBlock = bodyBlock;
        var sourceIndex = EnsureReadValue(indexPlace, intType, span);
        var loaded = LoadListPatternElement(sourcePlace, elementTypeId, sourceIndex, span);
        AppendRuntimeArrayElement(tail, loaded, elementTypeId, elementSize, span);
        _currentBlock.Terminator = new MirGoto
        {
            Target = incrementBlock.Id,
            Span = span
        };

        _currentBlock = incrementBlock;
        var currentIndex = EnsureReadValue(indexPlace, intType, span);
        var nextIndex = NewTemp(intType);
        _currentBlock.Instructions.Add(new MirBinOp
        {
            Target = nextIndex,
            Operator = BinaryOp.Add,
            Left = currentIndex,
            Right = CreateIntConstant(1, span),
            Span = span
        });
        EmitStore(indexPlace, nextIndex, span);
        _currentBlock.Terminator = new MirGoto
        {
            Target = headerBlock.Id,
            Span = span
        };

        _currentBlock = exitBlock;
        RegisterRuntimeArrayLocal(tail);
        return tail;
    }

    private MirPlace LoadListPatternElement(
        MirPlace sourcePlace,
        TypeId elementTypeId,
        int index,
        SourceSpan span)
    {
        return LoadListPatternElement(sourcePlace, elementTypeId, CreateIntConstant(index, span), span);
    }

    private MirPlace LoadListPatternElement(
        MirPlace sourcePlace,
        TypeId elementTypeId,
        MirOperand index,
        SourceSpan span)
    {
        var sourceSlot = CreateListPatternElementPlace(sourcePlace, elementTypeId, index, span);
        var loaded = NewTemp(elementTypeId);
        _currentBlock!.Instructions.Add(new MirLoad
        {
            Target = loaded,
            Source = sourceSlot,
            CreatesBorrowAlias = false,
            Span = span
        });

        return loaded;
    }

    private void AppendRuntimeArrayElement(
        MirPlace arrayPlace,
        MirOperand elementValue,
        TypeId elementTypeId,
        int elementSize,
        SourceSpan span)
    {
        _currentBlock!.Instructions.Add(new MirCall
        {
            Target = arrayPlace,
            Function = MirRuntimeFunctions.CreateFunctionRef(
                WellKnownStrings.InternalNames.ArrayPush,
                arrayPlace.TypeId,
                span),
            Arguments =
            [
                arrayPlace,
                PrepareCallArgument(elementValue, elementTypeId, span),
                CreateIntConstant(elementSize, span)
            ],
            Span = span
        });
    }

    private static bool IsIrrefutablePattern(HirPattern pattern)
    {
        return pattern switch
        {
            HirVarPattern => true,
            HirAsPattern asPattern => IsIrrefutablePattern(asPattern.InnerPattern),
            HirTuplePattern tuplePattern => tuplePattern.Elements.All(IsIrrefutablePattern),
            HirListPattern listPattern => listPattern.HasRest &&
                                          listPattern.Elements.Count == 0 &&
                                          listPattern.SuffixElements.Count == 0 &&
                                          (listPattern.RestPattern == null || IsIrrefutablePattern(listPattern.RestPattern)),
            HirOrPattern orPattern => IsIrrefutablePattern(orPattern.Left) || IsIrrefutablePattern(orPattern.Right),
            HirAndPattern andPattern => IsIrrefutablePattern(andPattern.Left) && IsIrrefutablePattern(andPattern.Right),
            _ => false
        };
    }

    private string NormalizeConstructorFieldName(string constructorName, string? fieldName, int fallbackIndex)
    {
        if (!string.IsNullOrWhiteSpace(constructorName) &&
            _constructorFieldOrderByName.TryGetValue(constructorName, out var fields))
        {
            if (!string.IsNullOrWhiteSpace(fieldName) &&
                fields.TryGetValue(fieldName, out var namedOrdinal))
            {
                return $"_{namedOrdinal}";
            }

            var fallbackName = $"_{fallbackIndex}";
            if (fields.TryGetValue(fallbackName, out var positionalOrdinal))
            {
                return $"_{positionalOrdinal}";
            }
        }

        if (TryParseFieldOrdinal(fieldName, out var ordinal))
        {
            return $"_{ordinal}";
        }

        return string.IsNullOrWhiteSpace(fieldName) ? $"_{fallbackIndex}" : fieldName;
    }

    private string NormalizeFieldAccessName(HirFieldAccess fieldAccess)
    {
        var fieldName = fieldAccess.FieldName;
        if (TryParseFieldOrdinal(fieldName, out var ordinal))
        {
            return $"_{ordinal}";
        }

        if (TryResolveAdtNamedFieldOrdinal(fieldAccess.Target.TypeId, fieldName, out var adtFieldName))
        {
            return adtFieldName;
        }

        if (TryCreateAdtFieldAccessDiagnostic(fieldAccess.Target.TypeId, fieldName, fieldAccess.Span, out var adtDiagnostic))
        {
            Diagnostics.Add(adtDiagnostic);
            return fieldName ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(fieldName) &&
            _uniqueNamedFieldOrdinal.TryGetValue(fieldName, out var namedOrdinal))
        {
            return $"_{namedOrdinal}";
        }

        if (!string.IsNullOrWhiteSpace(fieldName) &&
            _ambiguousNamedField.Contains(fieldName))
        {
            var diag = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.AmbiguousFieldAcrossConstructors(fieldName),
                "E3206");
            if (HasSpan(fieldAccess.Span))
            {
                diag.WithLabel(fieldAccess.Span, DiagnosticMessages.AmbiguousFieldAccessLabel);
            }
            Diagnostics.Add(diag);
        }

        return fieldName ?? string.Empty;
    }

    private bool TryResolveAdtNamedFieldOrdinal(TypeId targetType, string? fieldName, out string normalizedFieldName)
    {
        normalizedFieldName = string.Empty;
        if (!targetType.IsValid || string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        if (_uniqueNamedFieldOrdinalByAdtType.TryGetValue(targetType, out var fieldOrdinals) &&
            fieldOrdinals.TryGetValue(fieldName, out var namedOrdinal))
        {
            normalizedFieldName = $"_{namedOrdinal}";
            return true;
        }

        return false;
    }

    private bool TryCreateAdtFieldAccessDiagnostic(
        TypeId targetType,
        string? fieldName,
        SourceSpan span,
        out Diagnostic.Diagnostic diagnostic)
    {
        diagnostic = default!;
        if (!targetType.IsValid || string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        if (!_allNamedFieldByAdtType.TryGetValue(targetType, out var allFields))
        {
            return false;
        }

        var adtDisplayName = _adtDisplayNameByType.TryGetValue(targetType, out var displayName)
            ? displayName
            : targetType.ToString();

        if (_ambiguousNamedFieldByAdtType.TryGetValue(targetType, out var ambiguousFields) &&
            ambiguousFields.Contains(fieldName))
        {
            diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.InconsistentAdtFieldOrdinal(fieldName, adtDisplayName),
                "E3205");
            if (HasSpan(span))
            {
                diagnostic.WithLabel(span, DiagnosticMessages.AmbiguousAdtFieldAccessLabel);
            }
            diagnostic.WithNote(DiagnosticMessages.UseConstructorPatternMatchingFieldHelp);
            return true;
        }

        if (_partialNamedFieldByAdtType.TryGetValue(targetType, out var partialFields) &&
            partialFields.Contains(fieldName))
        {
            diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.NonTotalAdtField(fieldName, adtDisplayName),
                "E3204");
            if (HasSpan(span))
            {
                diagnostic.WithLabel(span, DiagnosticMessages.NonTotalAdtFieldAccessLabel);
            }
            diagnostic.WithNote(DiagnosticMessages.UseMatchBeforeConstructorSpecificFieldHelp);
            return true;
        }

        if (!allFields.Contains(fieldName))
        {
            diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.UnknownAdtField(fieldName, adtDisplayName),
                "E3203");
            if (HasSpan(span))
            {
                diagnostic.WithLabel(span, DiagnosticMessages.UnknownAdtFieldLabel);
            }
            return true;
        }

        return false;
    }

    private static bool TryParseFieldOrdinal(string? fieldName, out int ordinal)
    {
        ordinal = 0;
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        if (fieldName.StartsWith('_') &&
            int.TryParse(fieldName[1..], out var underscoredOrdinal) &&
            underscoredOrdinal >= 0)
        {
            ordinal = underscoredOrdinal;
            return true;
        }

        if (int.TryParse(fieldName, out var rawOrdinal) && rawOrdinal >= 0)
        {
            ordinal = rawOrdinal;
            return true;
        }

        return false;
    }

    private void BindPatternVariable(
        string name,
        SymbolId symbolId,
        MirPlace valuePlace,
        TypeId valueType,
        PatternBindingMode bindingMode,
        SourceSpan span,
        PatternLoweringContext context,
        LocalBindingScope? bindingScope)
    {
        var localId = EnsurePatternBindingLocal(
            valuePlace,
            valueType,
            bindingMode,
            span,
            context,
            name,
            symbolId);

        if (!string.IsNullOrWhiteSpace(name) && name != "_")
        {
            bindingScope?.TrackVariable(name, _variableLocals);
            _variableLocals[name] = localId;
        }

        if (symbolId.IsValid)
        {
            bindingScope?.TrackSymbol(symbolId, _symbolLocals);
            _symbolLocals[symbolId] = localId;
        }
    }

    private LocalId EnsurePatternBindingLocal(
        MirPlace valuePlace,
        TypeId valueType,
        PatternBindingMode bindingMode,
        SourceSpan span,
        PatternLoweringContext context,
        string bindingName,
        SymbolId symbolId)
    {
        if (bindingMode == PatternBindingMode.ByValue && valuePlace.Kind == PlaceKind.Local)
        {
            return valuePlace.Local;
        }

        var effectiveType = valueType.IsValid
            ? (valuePlace.TypeId.IsValid ? valuePlace.TypeId : valueType)
            : valuePlace.TypeId;
        var bindingKey = BuildPatternBindingKey(symbolId, bindingName, span);
        if (!context.TryGetBindingLocal(bindingKey, out var bindingLocal))
        {
            var localName = !string.IsNullOrWhiteSpace(bindingName) && bindingName != "_"
                ? bindingName
                : $"$pbind{_nextLocalId}";
            var localId = NewLocal(
                localName,
                effectiveType,
                isMutable: bindingMode == PatternBindingMode.MutableBorrow,
                bindingMode: bindingMode);
            bindingLocal = new PatternBindingLocalState
            {
                LocalId = localId
            };
            context.SetBindingLocal(bindingKey, bindingLocal);
        }

        var currentBlockId = _currentBlock!.Id;
        if (bindingLocal.IsInitializedIn(currentBlockId))
        {
            return bindingLocal.LocalId;
        }

        var target = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = bindingLocal.LocalId,
            TypeId = effectiveType,
            Span = span
        };

        switch (bindingMode)
        {
            case PatternBindingMode.ByValue:
            {
                var readValue = EnsureReadValue(valuePlace, effectiveType, span);
                EmitInitialization(target, readValue, span);
                break;
            }

            case PatternBindingMode.SharedBorrow:
            case PatternBindingMode.MutableBorrow:
                _currentBlock!.Instructions.Add(new MirLoad
                {
                    Target = target,
                    Source = valuePlace,
                    IsMutableBorrow = bindingMode == PatternBindingMode.MutableBorrow,
                    Span = span
                });
                break;
        }

        bindingLocal.MarkInitializedIn(currentBlockId);
        return bindingLocal.LocalId;
    }

    private static string BuildPatternBindingKey(SymbolId symbolId, string bindingName, SourceSpan span)
    {
        if (symbolId.IsValid)
        {
            return $"sym:{symbolId.Value}";
        }

        if (!string.IsNullOrWhiteSpace(bindingName) && bindingName != "_")
        {
            return $"name:{bindingName}";
        }

        return $"anon:{span.Location.Position}:{span.Location.Line}:{span.Location.Column}";
    }

    private sealed class LocalBindingScope
    {
        private readonly Dictionary<string, LocalId> _trackedVariableLocals = new(StringComparer.Ordinal);
        private readonly Dictionary<SymbolId, LocalId> _trackedSymbolLocals = [];

        public void TrackVariable(string name, Dictionary<string, LocalId> variableLocals)
        {
            if (_trackedVariableLocals.ContainsKey(name))
            {
                return;
            }

            _trackedVariableLocals[name] = variableLocals.TryGetValue(name, out var existingLocal)
                ? existingLocal
                : LocalId.None;
        }

        public void TrackSymbol(SymbolId symbolId, Dictionary<SymbolId, LocalId> symbolLocals)
        {
            if (_trackedSymbolLocals.ContainsKey(symbolId))
            {
                return;
            }

            _trackedSymbolLocals[symbolId] = symbolLocals.TryGetValue(symbolId, out var existingLocal)
                ? existingLocal
                : LocalId.None;
        }

        public void Restore(
            Dictionary<string, LocalId> variableLocals,
            Dictionary<SymbolId, LocalId> symbolLocals)
        {
            foreach (var (name, localId) in _trackedVariableLocals)
            {
                if (localId.IsValid)
                {
                    variableLocals[name] = localId;
                }
                else
                {
                    variableLocals.Remove(name);
                }
            }

            foreach (var (symbolId, localId) in _trackedSymbolLocals)
            {
                if (localId.IsValid)
                {
                    symbolLocals[symbolId] = localId;
                }
                else
                {
                    symbolLocals.Remove(symbolId);
                }
            }
        }
    }
}
