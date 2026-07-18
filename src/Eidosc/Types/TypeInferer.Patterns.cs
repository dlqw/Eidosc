using Eidosc.Symbols;
using Eidosc.Ast.Patterns;
using Eidosc.Diagnostic;
using Eidosc.Utils;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    /// <summary>
    /// 推断模式的类型（并绑定变量）
    /// </summary>
    private Type InferPattern(Pattern pattern, Type? expectedType = null)
    {
        var previousAllowRigidRefinement = _substitution.AllowRigidExistentialRefinement;
        _substitution.AllowRigidExistentialRefinement = true;
        try
        {
            return pattern switch
            {
                ExpandPattern { ExpandedPattern: not null } expansion =>
                    InferExpandedPattern(expansion, expectedType),
                VarPattern varPattern => InferVarPattern(varPattern, expectedType),
                WildcardPattern wildcardPattern => InferWildcardPattern(wildcardPattern, expectedType),
                LiteralPattern lit => InferLiteralPattern(lit, expectedType),
                CtorPattern ctor => InferCtorPattern(ctor, expectedType),
                TuplePattern tuple => InferTuplePattern(tuple, expectedType),
                ListPattern listPattern => InferListPattern(listPattern, expectedType),
                NotPattern notPattern => InferNotPattern(notPattern, expectedType),
                OrPattern orPattern => InferOrPattern(orPattern, expectedType),
                AndPattern andPattern => InferAndPattern(andPattern, expectedType),
                RangePattern rangePattern => InferRangePattern(rangePattern, expectedType),
                ViewPattern viewPattern => InferViewPattern(viewPattern, expectedType),
                AsPattern asPattern => InferAsPattern(asPattern, expectedType),
                _ => InferUnsupportedPattern(pattern)
            };
        }
        finally
        {
            _substitution.AllowRigidExistentialRefinement = previousAllowRigidRefinement;
        }
    }

    private Type InferExpandedPattern(ExpandPattern expansion, Type? expectedType)
    {
        var inferred = InferPattern(expansion.ExpandedPattern!, expectedType);
        expansion.InferredType = inferred;
        return inferred;
    }

    private Type InferUnsupportedPattern(Pattern pattern)
    {
        AddError(pattern.Span, DiagnosticMessages.UnsupportedPatternKind(pattern.GetType().Name));
        var recovered = CreateErrorRecoveryType();
        pattern.InferredType = recovered;
        return recovered;
    }

    private Type InferWildcardPattern(WildcardPattern wildcardPattern, Type? expectedType = null)
    {
        var matchedType = expectedType ?? _substitution.FreshTypeVariable();
        wildcardPattern.InferredType = matchedType;
        return matchedType;
    }

    private Type InferVarPattern(VarPattern varPattern, Type? expectedType = null)
    {
        var matchedType = expectedType ?? _substitution.FreshTypeVariable();
        var bindingType = WrapPatternBindingType(varPattern.BindingMode, matchedType);
        varPattern.InferredType = bindingType;

        if (varPattern.SymbolId.IsValid)
        {
            _env = _env.ExtendMono(varPattern.SymbolId, bindingType);
        }

        return matchedType;
    }

    private Type InferLiteralPattern(LiteralPattern lit, Type? expectedType = null)
    {
        var literalType = lit.Type switch
        {
            LiteralType.Integer => (Type)BaseTypes.Int,
            LiteralType.Float => BaseTypes.Float,
            LiteralType.String => BaseTypes.String,
            LiteralType.Char => BaseTypes.Char,
            LiteralType.Boolean => BaseTypes.Bool,
            _ => InferUnsupportedLiteralPattern(lit)
        };

        if (expectedType == null)
        {
            return literalType;
        }

        if (lit.Type == LiteralType.Char && IsIntType(expectedType))
        {
            lit.InferredType = expectedType;
            return expectedType;
        }

        var resultType = TryUnify(expectedType, literalType, lit.Span, DiagnosticMessages.LiteralPatternTypeMismatch);
        var resolved = _substitution.Apply(resultType);
        lit.InferredType = resolved;
        return resolved;
    }

    private Type InferUnsupportedLiteralPattern(LiteralPattern lit)
    {
        AddError(lit.Span, DiagnosticMessages.UnsupportedLiteralPatternKind(lit.Type));
        var recovered = CreateErrorRecoveryType();
        lit.InferredType = recovered;
        return recovered;
    }

    private static bool IsIntType(Type type)
    {
        return type is TyCon { Id.Value: BaseTypes.IntId };
    }

    private Type InferCtorPattern(CtorPattern ctor, Type? expectedType = null)
    {
        if (TryGetCtorTypeBinding(ctor.SymbolId, ctor.ConstructorName, out var binding))
        {
            if (expectedType != null && ContainsErrorRecoveryType(expectedType))
            {
                var recovered = CreateErrorRecoveryType();
                foreach (var subPattern in ctor.PositionalPatterns)
                {
                    InferPattern(subPattern, recovered);
                }

                foreach (var fieldPattern in ctor.NamedPatterns)
                {
                    if (fieldPattern.Pattern != null)
                    {
                        InferPattern(fieldPattern.Pattern, recovered);
                    }
                }

                ctor.InferredType = recovered;
                return recovered;
            }

            var typeVarEnv = CreateCtorTypeVarEnv(binding, rigidExistentialCtorParams: true);
            var ctorType = CreateAdtTypeFromBinding(binding, typeVarEnv, ctor.Span);
            Type resultType;
            if (expectedType == null)
            {
                resultType = ctorType;
            }
            else if (TryJoinClosedCaseTypes(
                         _substitution.Apply(expectedType),
                         _substitution.Apply(ctorType),
                         out var patternAncestor))
            {
                resultType = patternAncestor;
            }
            else
            {
                resultType = TryUnify(
                    expectedType,
                    ctorType,
                    ctor.Span,
                    DiagnosticMessages.ConstructorPatternTypeMismatch);
            }
            var kindEnvByName = CreateTypeParamKindMapForCtorBinding(
                binding.AdtId,
                binding.AdtTypeParamNames,
                binding.CtorId,
                binding.CtorTypeParamNames);

            var matchedPositionalCount = Math.Min(ctor.PositionalPatterns.Count, binding.PositionalArgTypes.Count);
            for (var i = 0; i < matchedPositionalCount; i++)
            {
                var expectedSubPatternType = ConvertTypeWithAdditionalKindContext(
                    binding.PositionalArgTypes[i],
                    typeVarEnv,
                    kindEnvByName);
                InferPattern(ctor.PositionalPatterns[i], expectedSubPatternType);
            }

            for (var i = matchedPositionalCount; i < ctor.PositionalPatterns.Count; i++)
            {
                InferPattern(ctor.PositionalPatterns[i]);
            }

            foreach (var fieldPattern in ctor.NamedPatterns)
            {
                if (fieldPattern.Pattern == null)
                {
                    continue;
                }

                if (binding.NamedArgTypes.TryGetValue(fieldPattern.FieldName, out var fieldType))
                {
                    var expectedFieldType = ConvertTypeWithAdditionalKindContext(
                        fieldType,
                        typeVarEnv,
                        kindEnvByName);
                    InferPattern(fieldPattern.Pattern, expectedFieldType);
                }
                else
                {
                    InferPattern(fieldPattern.Pattern);
                }
            }

            ApplyAdtTypeParamConstraints(binding.AdtId, typeVarEnv, ctor.Span);
            ApplyConstructorTypeParamConstraints(binding, typeVarEnv, ctor.Span);

            var resolvedResultType = _substitution.Apply(resultType);
            ctor.InferredType = resolvedResultType;
            return resolvedResultType;
        }

        var fallbackCtorType = TryInferAdtTypeFromConstructor(ctor.SymbolId, ctor.ConstructorName);
        var fallbackResultType = expectedType ?? fallbackCtorType ?? CreateErrorRecoveryType();

        if (expectedType != null && fallbackCtorType != null)
        {
            fallbackResultType = TryUnify(expectedType, fallbackCtorType, ctor.Span, DiagnosticMessages.ConstructorPatternTypeMismatch);
        }

        foreach (var subPattern in ctor.PositionalPatterns)
        {
            InferPattern(subPattern, CreateErrorRecoveryType());
        }

        foreach (var fieldPattern in ctor.NamedPatterns)
        {
            if (fieldPattern.Pattern != null)
            {
                InferPattern(fieldPattern.Pattern, CreateErrorRecoveryType());
            }
        }

        ctor.InferredType = fallbackResultType;
        return fallbackResultType;
    }

    private Type InferTuplePattern(TuplePattern tuple, Type? expectedType = null)
    {
        var elementTypes = new List<Type>();
        var expectedElements = new List<Type?>();
        var expectedCannotMatchTuple = false;
        Type? shapeMismatchResult = null;
        if (expectedType != null)
        {
            var resolvedExpectedType = _substitution.Apply(expectedType);
            if (resolvedExpectedType is TyTuple expectedTuple &&
                expectedTuple.Elements.Count == tuple.Elements.Count)
            {
                expectedElements = expectedTuple.Elements.Cast<Type?>().ToList();
            }
            else if (!ContainsErrorRecoveryType(resolvedExpectedType) &&
                     resolvedExpectedType is not TyVar)
            {
                expectedCannotMatchTuple = true;
                var diagnosticTupleType = new TyTuple
                {
                    Elements = tuple.Elements.Select(_ => (Type)_substitution.FreshTypeVariable()).ToList()
                };
                shapeMismatchResult = TryUnify(
                    expectedType,
                    diagnosticTupleType,
                    tuple.Span,
                    DiagnosticMessages.TuplePatternTypeMismatch);
            }
        }

        if (expectedCannotMatchTuple)
        {
            expectedElements = tuple.Elements.Select(_ => (Type?)CreateErrorRecoveryType()).ToList();
        }

        var i = 0;
        var hasRecovery = false;
        foreach (var elem in tuple.Elements)
        {
            var elemExpected = i < expectedElements.Count ? expectedElements[i] : null;
            var elementType = InferPattern(elem, elemExpected);
            elementTypes.Add(elementType);
            hasRecovery |= ContainsErrorRecoveryType(elementType);
            i++;
        }

        Type resultType = new TyTuple { Elements = elementTypes };
        if (shapeMismatchResult != null)
        {
            resultType = shapeMismatchResult;
        }
        else if (hasRecovery)
        {
            resultType = CreateErrorRecoveryType();
        }
        else if (expectedType != null)
        {
            resultType = TryUnify(expectedType, resultType, tuple.Span, DiagnosticMessages.TuplePatternTypeMismatch);
        }

        var resolved = _substitution.Apply(resultType);
        tuple.InferredType = resolved;
        return resolved;
    }

    private Type InferAsPattern(AsPattern asPattern, Type? expectedType = null)
    {
        var type = expectedType ?? _substitution.FreshTypeVariable();
        var hasRecovery = false;

        if (string.IsNullOrWhiteSpace(asPattern.BindingName))
        {
            AddError(asPattern.Span, DiagnosticMessages.AsPatternRequiresBindingName);
            hasRecovery = true;
        }

        if (asPattern.InnerPattern != null)
        {
            Type innerType;
            var innerInferenceFailed = false;
            try
            {
                innerType = InferPattern(asPattern.InnerPattern, type);
            }
            catch (TypeInferenceException ex)
            {
                AddAsPatternTypeMismatchError(
                    asPattern,
                    type,
                    null,
                    ex.Message,
                    asPattern.InnerPattern.Span);
                innerType = CreateErrorRecoveryType();
                type = innerType;
                innerInferenceFailed = true;
                hasRecovery = true;
            }

            if (!innerInferenceFailed)
            {
                if (ContainsErrorRecoveryType(innerType))
                {
                    AddAsPatternTypeMismatchError(
                        asPattern,
                        type,
                        innerType,
                        DiagnosticMessages.InnerPatternRecoveredAfterEarlierMismatch,
                        asPattern.InnerPattern.Span);
                    type = CreateErrorRecoveryType();
                    hasRecovery = true;
                }
                else
                {
                    type = TryUnifyAsPattern(type, innerType, asPattern, asPattern.InnerPattern.Span);
                    hasRecovery |= ContainsErrorRecoveryType(type);
                }
            }
        }

        var resolved = hasRecovery
            ? CreateErrorRecoveryType()
            : _substitution.Apply(type);
        var bindingType = WrapPatternBindingType(asPattern.BindingMode, resolved);
        asPattern.InferredType = bindingType;
        if (!hasRecovery && asPattern.SymbolId.IsValid)
        {
            _env = _env.ExtendMono(asPattern.SymbolId, bindingType);
        }

        return resolved;
    }

    private Type WrapPatternBindingType(PatternBindingMode bindingMode, Type matchedType)
    {
        var resolvedMatchedType = _substitution.Apply(matchedType);
        return bindingMode switch
        {
            PatternBindingMode.SharedBorrow => new TyRef { Inner = resolvedMatchedType },
            PatternBindingMode.MutableBorrow => new TyMutRef { Inner = resolvedMatchedType },
            _ => resolvedMatchedType
        };
    }

    private Type InferNotPattern(NotPattern notPattern, Type? expectedType = null)
    {
        var resultType = expectedType ?? _substitution.FreshTypeVariable();

        if (notPattern.InnerPattern == null)
        {
            AddError(notPattern.Span, DiagnosticMessages.NotPatternMissingInnerPattern);
            var recovered = CreateErrorRecoveryType();
            notPattern.InferredType = recovered;
            return recovered;
        }

        var innerType = InferPattern(notPattern.InnerPattern, resultType);
        resultType = TryUnify(resultType, innerType, notPattern.InnerPattern.Span, DiagnosticMessages.NotPatternInnerTypeMismatch);

        var resolved = _substitution.Apply(resultType);
        notPattern.InferredType = resolved;
        return resolved;
    }

    private Type InferOrPattern(OrPattern orPattern, Type? expectedType = null)
    {
        if (orPattern.Alternatives.Count < 2)
        {
            AddError(orPattern.Span, DiagnosticMessages.OrPatternRequiresAtLeastTwoAlternatives);
            var recovered = CreateErrorRecoveryType();
            orPattern.InferredType = recovered;
            return recovered;
        }

        var resultType = InferPattern(orPattern.Alternatives[0], expectedType);
        for (var i = 1; i < orPattern.Alternatives.Count; i++)
        {
            var alternativeType = InferPattern(orPattern.Alternatives[i], expectedType ?? resultType);
            resultType = TryUnify(resultType, alternativeType, orPattern.Alternatives[i].Span, DiagnosticMessages.OrPatternAlternativeTypeMismatch);
        }

        if (expectedType != null)
        {
            resultType = TryUnify(expectedType, resultType, orPattern.Span, DiagnosticMessages.OrPatternExpectedTypeMismatch);
        }

        var resolved = _substitution.Apply(resultType);
        orPattern.InferredType = resolved;
        return resolved;
    }

    private Type InferAndPattern(AndPattern andPattern, Type? expectedType = null)
    {
        if (andPattern.Conjuncts.Count < 2)
        {
            AddError(andPattern.Span, DiagnosticMessages.AndPatternRequiresAtLeastTwoConjuncts);
            var recovered = CreateErrorRecoveryType();
            andPattern.InferredType = recovered;
            return recovered;
        }

        var resultType = expectedType ?? _substitution.FreshTypeVariable();
        foreach (var conjunct in andPattern.Conjuncts)
        {
            var conjunctType = InferPattern(conjunct, resultType);
            resultType = TryUnify(
                resultType,
                conjunctType,
                conjunct.Span,
                DiagnosticMessages.AndPatternConjunctTypeMismatch);
        }

        var resolved = _substitution.Apply(resultType);
        andPattern.InferredType = resolved;
        return resolved;
    }

    private Type InferRangePattern(RangePattern rangePattern, Type? expectedType = null)
    {
        var resultType = expectedType ?? _substitution.FreshTypeVariable();
        var resolvedExpected = _substitution.Apply(resultType);

        if (IsKnownNonRangeComparableType(resolvedExpected))
        {
            AddRangeComparableTypeError(rangePattern, resolvedExpected);
            rangePattern.InferredType = resolvedExpected;
            return resolvedExpected;
        }

        if (rangePattern.Start == null || rangePattern.End == null)
        {
            AddRangeBoundaryError(rangePattern);
            var recovered = CreateErrorRecoveryType();
            rangePattern.InferredType = recovered;
            return recovered;
        }

        var startType = InferLiteralPattern(rangePattern.Start, resultType);
        var endType = InferLiteralPattern(rangePattern.End, resultType);

        resultType = TryUnify(resultType, startType, rangePattern.Span, DiagnosticMessages.RangePatternStartTypeMismatch);
        resultType = TryUnify(resultType, endType, rangePattern.Span, DiagnosticMessages.RangePatternEndTypeMismatch);

        var resolved = _substitution.Apply(resultType);
        if (ContainsErrorRecoveryType(resolved))
        {
            var recovered = CreateErrorRecoveryType();
            rangePattern.InferredType = recovered;
            return recovered;
        }

        if (!IsRangeComparableType(resolved))
        {
            AddRangeComparableTypeError(rangePattern, resolved);
        }
        else
        {
            ValidateRangePatternOrder(rangePattern);
        }

        rangePattern.InferredType = resolved;
        return resolved;
    }

    private void ValidateRangePatternOrder(RangePattern rangePattern)
    {
        if (rangePattern.Start == null || rangePattern.End == null)
        {
            return;
        }

        if (!TryConvertRangeBoundaryToComparable(rangePattern.Start, out var startValue) ||
            !TryConvertRangeBoundaryToComparable(rangePattern.End, out var endValue))
        {
            return;
        }

        if (startValue > endValue)
        {
            AddRangeOrderError(rangePattern);
        }
    }

    private void AddRangeBoundaryError(RangePattern rangePattern)
    {
        var diagnostic = new EidoscDiagnostic(
            EidoscDiagnosticLevel.Error,
            DiagnosticMessages.RangePatternRequiresStartAndEndLiterals,
            RangeMissingBoundaryCode);

        diagnostic.WithLabel(rangePattern.Span, DiagnosticMessages.InvalidRangePatternLabel);

        if (rangePattern.Start != null)
        {
            diagnostic.WithLabel(rangePattern.Start.Span, DiagnosticMessages.ParsedStartBoundaryLabel);
        }
        else
        {
            diagnostic.WithNote(DiagnosticMessages.MissingRangeStartBoundaryLiteralNote);
        }

        if (rangePattern.End != null)
        {
            diagnostic.WithLabel(rangePattern.End.Span, DiagnosticMessages.ParsedEndBoundaryLabel);
        }
        else
        {
            diagnostic.WithNote(DiagnosticMessages.MissingRangeEndBoundaryLiteralNote);
        }

        AddStructuredErrorDiagnostic(diagnostic, rangePattern.Span);
    }

    private void AddRangeOrderError(RangePattern rangePattern)
    {
        var diagnostic = new EidoscDiagnostic(
            EidoscDiagnosticLevel.Error,
            DiagnosticMessages.RangeStartMustBeLessThanOrEqualToEnd,
            RangeInvalidOrderCode);

        diagnostic.WithLabel(rangePattern.Span, DiagnosticMessages.InvalidRangeOrderingLabel);
        diagnostic.WithLabel(rangePattern.Start!.Span, DiagnosticMessages.RangeStartBoundaryLabel);
        diagnostic.WithLabel(rangePattern.End!.Span, DiagnosticMessages.RangeEndBoundaryLabel);
        diagnostic.WithNote(DiagnosticMessages.RangeOrderingCheckNote);

        AddStructuredErrorDiagnostic(diagnostic, rangePattern.Span);
    }

    private void AddRangeComparableTypeError(RangePattern rangePattern, Type scrutineeType)
    {
        var diagnostic = new EidoscDiagnostic(
            EidoscDiagnosticLevel.Error,
            DiagnosticMessages.RangePatternExpectsIntOrCharScrutinee(scrutineeType),
            RangeInvalidScrutineeCode);

        diagnostic.WithLabel(rangePattern.Span, DiagnosticMessages.RangePatternTypeMismatchLabel);

        if (rangePattern.Start != null)
        {
            diagnostic.WithLabel(rangePattern.Start.Span, DiagnosticMessages.RangeStartBoundaryLabel);
        }

        if (rangePattern.End != null)
        {
            diagnostic.WithLabel(rangePattern.End.Span, DiagnosticMessages.RangeEndBoundaryLabel);
        }

        diagnostic.WithNote(DiagnosticMessages.ScrutineeTypeInferredAs(scrutineeType));
        diagnostic.WithNote(DiagnosticMessages.RangePatternSupportsOnlyIntAndCharNote);

        AddStructuredErrorDiagnostic(diagnostic, rangePattern.Span);
    }

    private Type TryUnifyAsPattern(Type expected, Type actual, AsPattern asPattern, SourceSpan span)
    {
        try
        {
            _substitution.Unify(expected, actual);
            _recoveryContext.RecordSuccess();
            return _substitution.Apply(expected);
        }
        catch (TypeInferenceException ex)
        {
            if (IsCascadingError(expected, actual))
            {
                return CreateErrorRecoveryType();
            }

            AddAsPatternTypeMismatchError(asPattern, expected, actual, ex.Message, span);
            return CreateErrorRecoveryType();
        }
    }

    private void AddAsPatternTypeMismatchError(AsPattern asPattern, Type expectedType, Type? innerType, string reason, SourceSpan span)
    {
        var resolvedExpected = _substitution.Apply(expectedType);
        var resolvedInner = innerType != null ? _substitution.Apply(innerType) : null;
        var diagnostic = new EidoscDiagnostic(
            EidoscDiagnosticLevel.Error,
            DiagnosticMessages.AsPatternInnerTypeMismatch(reason),
            AsPatternTypeMismatchCode);

        diagnostic.WithLabel(asPattern.Span, DiagnosticMessages.AsPatternBindingLabel);
        diagnostic.WithLabel(span, DiagnosticMessages.AsPatternInnerPatternLabel);
        diagnostic.WithNote(DiagnosticMessages.ScrutineeTypeInferredAs(resolvedExpected));
        if (resolvedInner != null)
        {
            diagnostic.WithNote(DiagnosticMessages.InnerPatternInferredAs(resolvedInner));
        }

        diagnostic.WithNote(DiagnosticMessages.AsPatternRequiresInnerTypeMatchNote);
        _diagnostics.Add(diagnostic);
        _recoveryContext.RecordError();
    }

    private static bool TryConvertRangeBoundaryToComparable(LiteralPattern boundary, out long value)
    {
        value = 0;

        switch (boundary.Value)
        {
            case long intValue:
                value = intValue;
                return true;
            case int int32Value:
                value = int32Value;
                return true;
            case char charValue:
                value = charValue;
                return true;
            default:
                return false;
        }
    }

    private Type InferViewPattern(ViewPattern viewPattern, Type? expectedType = null)
    {
        var scrutineeType = expectedType ?? _substitution.FreshTypeVariable();
        Type viewedType = _substitution.FreshTypeVariable();
        var hasRecovery = ContainsErrorRecoveryType(scrutineeType);

        if (viewPattern.ViewExpression == null)
        {
            AddError(viewPattern.Span, DiagnosticMessages.ViewPatternMissingViewExpression);
            hasRecovery = true;
        }
        else
        {
            var viewExprType = SafeInferExpression(viewPattern.ViewExpression);
            hasRecovery |= ContainsErrorRecoveryType(viewExprType);
            if (!TryUnifyViewPatternExpression(viewPattern, scrutineeType, viewedType, viewExprType))
            {
                viewedType = CreateErrorRecoveryType();
                hasRecovery = true;
            }
        }

        if (viewPattern.InnerPattern == null)
        {
            AddError(viewPattern.Span, DiagnosticMessages.ViewPatternMissingInnerPattern);
            hasRecovery = true;
        }
        else
        {
            var innerType = InferPattern(viewPattern.InnerPattern, viewedType);
            var innerResult = TryUnify(viewedType, innerType, viewPattern.InnerPattern.Span, DiagnosticMessages.ViewPatternInnerTypeMismatch);
            hasRecovery |= ContainsErrorRecoveryType(innerType) || ContainsErrorRecoveryType(innerResult);
        }

        var resolved = hasRecovery
            ? CreateErrorRecoveryType()
            : _substitution.Apply(scrutineeType);
        viewPattern.InferredType = resolved;
        return resolved;
    }

    private bool TryUnifyViewPatternExpression(
        ViewPattern viewPattern,
        Type scrutineeType,
        Type viewedType,
        Type viewExpressionType)
    {
        var resolvedViewExpressionType = _substitution.Apply(viewExpressionType);
        var resolvedScrutineeType = _substitution.Apply(scrutineeType);

        if (resolvedViewExpressionType is TyFun functionType &&
            functionType.Params.Count != 1)
        {
            AddViewPatternViewExpressionError(
                viewPattern,
                resolvedScrutineeType,
                resolvedViewExpressionType,
                DiagnosticMessages.ViewExpressionMustAcceptOneArgument(functionType.Params.Count));
            return false;
        }

        if (resolvedViewExpressionType is not TyFun and not TyVar)
        {
            AddViewPatternViewExpressionError(
                viewPattern,
                resolvedScrutineeType,
                resolvedViewExpressionType,
                DiagnosticMessages.ViewExpressionIsNotCallable);
            return false;
        }

        var expectedViewType = new TyFun
        {
            Params = [scrutineeType],
            Result = viewedType,
            Effects = resolvedViewExpressionType is TyFun resolvedFunction
                ? resolvedFunction.Effects
                : EffectRow.Pure
        };

        try
        {
            _substitution.Unify(expectedViewType, viewExpressionType);
            _recoveryContext.RecordSuccess();
            return true;
        }
        catch (TypeInferenceException ex)
        {
            if (IsCascadingError(expectedViewType, viewExpressionType))
            {
                return false;
            }

            AddViewPatternViewExpressionError(
                viewPattern,
                resolvedScrutineeType,
                resolvedViewExpressionType,
                DiagnosticMessages.ViewExpressionTypeMismatch(ex.Message));
            return false;
        }
    }

    private void AddViewPatternViewExpressionError(
        ViewPattern viewPattern,
        Type scrutineeType,
        Type inferredViewExpressionType,
        string reason)
    {
        var diagnostic = new EidoscDiagnostic(
            EidoscDiagnosticLevel.Error,
            DiagnosticMessages.ViewPatternExpressionInvalid(reason),
            ViewPatternInvalidViewExpressionCode);

        var viewExpressionSpan = viewPattern.ViewExpression?.Span ?? viewPattern.Span;
        diagnostic.WithLabel(viewExpressionSpan, DiagnosticMessages.ViewExpressionLabel);
        diagnostic.WithLabel(viewPattern.Span, DiagnosticMessages.ViewPatternLabel);
        diagnostic.WithNote(DiagnosticMessages.ScrutineeTypeInferredAs(scrutineeType));
        diagnostic.WithNote(DiagnosticMessages.ViewExpressionInferredAs(inferredViewExpressionType));
        diagnostic.WithNote(DiagnosticMessages.ViewPatternCallableNote);

        _diagnostics.Add(diagnostic);
        _recoveryContext.RecordError();
    }

    private static bool IsRangeComparableType(Type type)
    {
        type = NormalizeForRange(type);
        if (type is not TyCon typeCon)
        {
            return false;
        }

        return typeCon.Id.Value is BaseTypes.IntId or BaseTypes.CharId ||
               string.Equals(typeCon.Name, WellKnownStrings.BuiltinTypes.Int, StringComparison.Ordinal) ||
               string.Equals(typeCon.Name, WellKnownStrings.BuiltinTypes.Char, StringComparison.Ordinal);
    }

    private static bool IsKnownNonRangeComparableType(Type type)
    {
        type = NormalizeForRange(type);

        return type switch
        {
            TyVar => false,
            TyCon typeCon when !typeCon.Id.IsValid && string.IsNullOrWhiteSpace(typeCon.Name) => false,
            _ => !IsRangeComparableType(type)
        };
    }

    private static Type NormalizeForRange(Type type)
    {
        while (type is TyVar { Instance: not null } typeVar)
        {
            type = typeVar.Instance!;
        }

        return type;
    }
}
