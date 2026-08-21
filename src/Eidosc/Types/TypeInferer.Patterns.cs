using System.Numerics;
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
        // 解析期错误（越界/未知转义/非法字符）在此上报，但继续给出可推断类型以避免连锁。
        if (lit.ErrorMessage != null)
        {
            AddError(lit.Span, lit.ErrorMessage, "E4016");
        }

        var resolvedExpected = expectedType == null ? null : _substitution.Apply(expectedType);

        // Char 字面模式对整型主题保留 C `case 'a':` 语义，并对窄型做值域检查。
        if (lit.Type == LiteralType.Char &&
            resolvedExpected is TyCon charExpected &&
            IsIntegerBaseType(charExpected))
        {
            if (!IsCharPatternValueInRange(lit, charExpected))
            {
                AddError(lit.Span, LiteralPatternOutOfRangeMessage(lit, charExpected), "E4016");
            }

            lit.InferredType = charExpected;
            return charExpected;
        }

        var literalType = lit.TypeSuffix != LiteralTypeSuffix.None
            ? GetBaseTypeForLiteralSuffix(lit.TypeSuffix, lit.IntegerSuffixWidth)
            : lit.Type switch
            {
                LiteralType.Integer => (Type)BaseTypes.Int,
                LiteralType.Float => BaseTypes.Float,
                LiteralType.String => BaseTypes.String,
                LiteralType.Char => BaseTypes.Char,
                LiteralType.Boolean => BaseTypes.Bool,
                _ => InferUnsupportedLiteralPattern(lit)
            };

        if (resolvedExpected is TyCon expectedCon &&
            TryAdaptLiteralPatternToExpectedType(lit, expectedCon, out var adapted))
        {
            lit.InferredType = adapted;
            return adapted;
        }

        if (resolvedExpected == null)
        {
            lit.InferredType = literalType;
            return literalType;
        }

        var resultType = TryUnify(
            resolvedExpected!,
            literalType,
            lit.Span,
            DiagnosticMessages.LiteralPatternTypeMismatch);
        var resolved = _substitution.Apply(resultType);
        lit.InferredType = resolved;
        return resolved;
    }

    /// <summary>
    /// 无后缀数值字面模式按主题基元类型解释（后缀 RFC 的上下文适配），
    /// 范围外报 E4016 专用诊断、不静默窄化。
    /// 与表达式侧不同，模式侧负字面量是单个 LiteralPattern（无 UnaryExpr 双层），
    /// 因此 `-128` 可安全适配到 Int8 主题（RFC §4.3 的示例语义优先于 §4.6 的边界陈述）。
    /// </summary>
    private bool TryAdaptLiteralPatternToExpectedType(LiteralPattern lit, TyCon expected, out Type adapted)
    {
        adapted = expected;

        if (lit.Type is not (LiteralType.Integer or LiteralType.Float) ||
            lit.TypeSuffix != LiteralTypeSuffix.None ||
            !TryGetSuffixForBaseType(expected, out var target, out var arbitraryWidth) ||
            target == LiteralTypeSuffix.None)
        {
            return false;
        }

        if (lit.Type == LiteralType.Float)
        {
            if (!LiteralSuffixTable.IsFloat(target))
            {
                return false;
            }

            var floatValue = lit.Value switch
            {
                double d => d,
                float f => f,
                _ => double.NaN
            };
            lit.SetValueAndKind(
                target == LiteralTypeSuffix.Float32 ? (object)(float)floatValue : floatValue,
                LiteralType.Float,
                target,
                arbitraryWidth);
            return true;
        }

        // 整数 → 浮点主题（Rust 语义：`match f: Float { 0 => ... }`）。
        if (LiteralSuffixTable.IsFloat(target))
        {
            if (!TryGetIntegerPatternBigIntegerMagnitude(lit, out var integerMagnitude))
            {
                return false;
            }

            lit.SetValueAndKind(
                target == LiteralTypeSuffix.Float32 ? (object)(float)integerMagnitude : (double)integerMagnitude,
                LiteralType.Float,
                target,
                arbitraryWidth);
            return true;
        }

        // 整数 → 窄整数主题。
        if (TryGetIntegerPatternNegativeValue(lit, out var negativeValue))
        {
            if (!LiteralSuffixTable.IsSigned(target) ||
                !TryGetBigIntegerSignedRange(target, arbitraryWidth, out var signedMin, out _) ||
                negativeValue < signedMin)
            {
                AddError(lit.Span, LiteralPatternOutOfRangeMessage(lit, expected), "E4016");
                return true;
            }

            lit.SetValueAndKind(negativeValue, LiteralType.Integer, target, arbitraryWidth);
            return true;
        }

        if (!TryGetIntegerPatternBigIntegerMagnitude(lit, out var magnitude) ||
            !LiteralSuffixTable.TryGetBigIntegerMagnitudeLimit(target, arbitraryWidth ?? 0, out var magnitudeLimit))
        {
            return false;
        }

        if (magnitude > magnitudeLimit)
        {
            AddError(lit.Span, LiteralPatternOutOfRangeMessage(lit, expected), "E4016");
            return true;
        }

        object value = magnitude <= long.MaxValue
            ? (object)(long)magnitude
            : magnitude <= ulong.MaxValue
                ? (object)(ulong)magnitude
                : magnitude;
        lit.SetValueAndKind(value, LiteralType.Integer, target, arbitraryWidth);
        return true;
    }

    private static bool TryGetIntegerPatternBigIntegerMagnitude(LiteralPattern lit, out BigInteger magnitude)
    {
        switch (lit.Value)
        {
            case BigInteger bigValue when bigValue >= 0:
                magnitude = bigValue;
                return true;
            case long longValue when longValue >= 0:
                magnitude = longValue;
                return true;
            case byte byteValue:
                magnitude = byteValue;
                return true;
            case ushort ushortValue:
                magnitude = ushortValue;
                return true;
            case uint uintValue:
                magnitude = uintValue;
                return true;
            case ulong ulongValue:
                magnitude = ulongValue;
                return true;
            case int intValue when intValue >= 0:
                magnitude = intValue;
                return true;
            default:
                magnitude = BigInteger.Zero;
                return false;
        }
    }

    private static bool TryGetIntegerPatternNegativeValue(LiteralPattern lit, out BigInteger negativeValue)
    {
        switch (lit.Value)
        {
            case BigInteger bigValue when bigValue < 0:
                negativeValue = bigValue;
                return true;
            case long longValue when longValue < 0:
                negativeValue = longValue;
                return true;
            default:
                negativeValue = BigInteger.Zero;
                return false;
        }
    }

    private static bool TryGetBigIntegerSignedRange(
        LiteralTypeSuffix suffix,
        int? arbitraryWidth,
        out BigInteger min,
        out BigInteger max)
    {
        if (suffix == LiteralTypeSuffix.IntArbitrary)
        {
            int bits = arbitraryWidth ?? 0;
            if (bits <= 0 || bits > BaseTypes.MaxIntegerWidth)
            {
                min = BigInteger.Zero;
                max = BigInteger.Zero;
                return false;
            }

            min = -(BigInteger.One << (bits - 1));
            max = (BigInteger.One << (bits - 1)) - 1;
            return true;
        }

        (long fixedMin, long fixedMax) = suffix switch
        {
            LiteralTypeSuffix.Int8 => (sbyte.MinValue, (long)sbyte.MaxValue),
            LiteralTypeSuffix.Int16 => (short.MinValue, (long)short.MaxValue),
            LiteralTypeSuffix.Int32 => (int.MinValue, int.MaxValue),
            LiteralTypeSuffix.Int64 => (long.MinValue, long.MaxValue),
            _ => (0L, 0L)
        };
        min = fixedMin;
        max = fixedMax;
        return fixedMin != 0;
    }

    private static Type GetBaseTypeForLiteralSuffix(LiteralTypeSuffix suffix, int? arbitraryWidth = null) => suffix switch
    {
        LiteralTypeSuffix.Int8 => BaseTypes.Int8,
        LiteralTypeSuffix.Int16 => BaseTypes.Int16,
        LiteralTypeSuffix.Int32 => BaseTypes.Int32,
        LiteralTypeSuffix.Int64 => BaseTypes.Int,
        LiteralTypeSuffix.UInt8 => BaseTypes.UInt8,
        LiteralTypeSuffix.UInt16 => BaseTypes.UInt16,
        LiteralTypeSuffix.UInt32 => BaseTypes.UInt32,
        LiteralTypeSuffix.UInt64 => BaseTypes.UInt64,
        LiteralTypeSuffix.Float32 => BaseTypes.Float32,
        LiteralTypeSuffix.Float64 => BaseTypes.Float,
        LiteralTypeSuffix.IntArbitrary or LiteralTypeSuffix.UIntArbitrary =>
            arbitraryWidth is > 0 and <= BaseTypes.MaxIntegerWidth
                ? BaseTypes.GetIntegerType(suffix == LiteralTypeSuffix.UIntArbitrary, arbitraryWidth.Value)
                : BaseTypes.Int,
        _ => BaseTypes.Int
    };

    private static bool IsCharPatternValueInRange(LiteralPattern lit, TyCon expected)
    {
        if (lit.Value is not char charValue ||
            !TryGetSuffixForBaseType(expected, out var target, out var arbitraryWidth) ||
            target == LiteralTypeSuffix.None)
        {
            return true;
        }

        if (!LiteralSuffixTable.IsInteger(target))
        {
            return true;
        }

        if (LiteralSuffixTable.IsSigned(target))
        {
            return charValue <= GetSignedMax(target, arbitraryWidth);
        }

        return (ulong)charValue <= GetUnsignedMax(target, arbitraryWidth);
    }

    private static long GetSignedMax(LiteralTypeSuffix suffix, int? arbitraryWidth = null) => suffix switch
    {
        LiteralTypeSuffix.Int8 => sbyte.MaxValue,
        LiteralTypeSuffix.Int16 => short.MaxValue,
        LiteralTypeSuffix.Int32 => int.MaxValue,
        LiteralTypeSuffix.Int64 => long.MaxValue,
        LiteralTypeSuffix.IntArbitrary when arbitraryWidth is > 0 and <= 63 => (1L << (arbitraryWidth.Value - 1)) - 1,
        _ => long.MaxValue
    };

    private static ulong GetUnsignedMax(LiteralTypeSuffix suffix, int? arbitraryWidth = null) => suffix switch
    {
        LiteralTypeSuffix.UInt8 => byte.MaxValue,
        LiteralTypeSuffix.UInt16 => ushort.MaxValue,
        LiteralTypeSuffix.UInt32 => uint.MaxValue,
        LiteralTypeSuffix.UInt64 => ulong.MaxValue,
        LiteralTypeSuffix.UIntArbitrary when arbitraryWidth is > 0 and <= 63 => (1UL << arbitraryWidth.Value) - 1,
        _ => ulong.MaxValue
    };

    private string LiteralPatternOutOfRangeMessage(LiteralPattern lit, Type expected)
    {
        var display = string.IsNullOrWhiteSpace(lit.RawText) ? "?" : lit.RawText;
        return $"literal pattern '{display}' is out of range for scrutinee type '{_substitution.Apply(expected)}'";
    }

    private Type InferUnsupportedLiteralPattern(LiteralPattern lit)
    {
        AddError(lit.Span, DiagnosticMessages.UnsupportedLiteralPatternKind(lit.Type));
        var recovered = CreateErrorRecoveryType();
        lit.InferredType = recovered;
        return recovered;
    }

    private static bool IsIntegerBaseType(Type type)
    {
        return type is TyCon { Id: var typeId } && BaseTypes.IsIntegerType(typeId);
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

        if (!TryGetRangeBoundaryValue(rangePattern.Start, out var startValue) ||
            !TryGetRangeBoundaryValue(rangePattern.End, out var endValue))
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

    private static bool TryGetRangeBoundaryValue(LiteralPattern boundary, out BigInteger value)
    {
        switch (boundary.Value)
        {
            case BigInteger bigValue:
                value = bigValue;
                return true;
            case long longValue:
                value = longValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            case short shortValue:
                value = shortValue;
                return true;
            case sbyte sbyteValue:
                value = sbyteValue;
                return true;
            case byte byteValue:
                value = byteValue;
                return true;
            case ushort ushortValue:
                value = ushortValue;
                return true;
            case uint uintValue:
                value = uintValue;
                return true;
            case ulong ulongValue:
                value = ulongValue;
                return true;
            case char charValue:
                value = charValue;
                return true;
            case string text when boundary.Type == LiteralType.Char && text.Length == 1:
                value = text[0];
                return true;
            case string text when BigInteger.TryParse(text, out var parsed):
                value = parsed;
                return true;
            default:
                value = BigInteger.Zero;
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

        return IsIntegerBaseType(typeCon) ||
               typeCon.Id.Value is BaseTypes.CharId ||
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
