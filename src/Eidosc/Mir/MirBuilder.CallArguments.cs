using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Mir;

public sealed partial class MirBuilder
{
    private MirOperand EnsureReadValue(MirOperand operand, TypeId fallbackType, SourceSpan span)
    {
        if (operand is MirPlace place)
        {
            if (!TryResolveOperandTypeId(operand, fallbackType, span, "read value", out var typeId))
            {
                return CreatePoisonOperand(TypeId.None, span, DiagnosticMessages.MissingMirTypeForReadValueReason);
            }

            var temp = NewTemp(typeId);

            if (place.Kind == PlaceKind.Local && ShouldCopyLocalValue(place.Local, typeId))
            {
                _currentBlock!.Instructions.Add(new MirCopy
                {
                    Target = temp,
                    Source = place,
                    Span = span
                });
            }
            else
            {
                _currentBlock!.Instructions.Add(new MirLoad
                {
                    Target = temp,
                    Source = place,
                    CreatesBorrowAlias = false,
                    Span = span
                });
            }

            return temp;
        }

        return operand;
    }

    private MirOperand PrepareCallArgument(MirOperand operand, TypeId fallbackType, SourceSpan span)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local } place)
        {
            if (!TryResolveOperandTypeId(operand, fallbackType, span, "call argument", out var typeId))
            {
                return CreatePoisonOperand(TypeId.None, span, DiagnosticMessages.MissingMirTypeForCallArgumentReason);
            }

            var temp = NewTemp(typeId);

            if (ShouldCopyLocalValue(place.Local, typeId))
            {
                _currentBlock!.Instructions.Add(new MirCopy
                {
                    Target = temp,
                    Source = place,
                    Span = span
                });
            }
            else
            {
                _currentBlock!.Instructions.Add(new MirMove
                {
                    Target = temp,
                    Source = place,
                    Span = span
                });
            }

            return temp;
        }

        return operand;
    }

    private MirOperand PrepareCallArgument(
        MirOperand operand,
        TypeId fallbackType,
        SourceSpan span,
        bool forceCopy)
    {
        if (!forceCopy)
        {
            return PrepareCallArgument(operand, fallbackType, span);
        }

        if (operand is MirPlace { Kind: PlaceKind.Local } place)
        {
            if (!TryResolveOperandTypeId(operand, fallbackType, span, "forced-copy call argument", out var typeId))
            {
                return CreatePoisonOperand(
                    TypeId.None,
                    span,
                    DiagnosticMessages.MissingMirTypeForForcedCopyCallArgumentReason);
            }

            var temp = NewTemp(typeId);
            _currentBlock!.Instructions.Add(new MirCopy
            {
                Target = temp,
                Source = place,
                Span = span
            });
            return temp;
        }

        return operand;
    }

    private MirOperand PrepareCallArgumentForNode(HirNode argument, SourceSpan span, bool forceCopy)
    {
        if (TryConvertPlaceShapedExprPlace(argument, out var place))
        {
            return PrepareProjectedCallArgument(place, argument.TypeId, span, forceCopy);
        }

        return PrepareCallArgument(ConvertExpr(argument), argument.TypeId, span, forceCopy);
    }

    private bool TryConvertPlaceShapedExprPlace(HirNode argument, out MirPlace place)
    {
        if (!HirPlaceExpressionClassifier.IsPlaceShaped(argument))
        {
            place = null!;
            return false;
        }

        switch (argument)
        {
            case HirVar variable when TryGetLocalForVariable(variable, out var localId):
                var localTypeId = variable.TypeId.IsValid ? variable.TypeId : ResolveLocalType(localId);
                place = new MirPlace
                {
                    Kind = PlaceKind.Local,
                    Local = localId,
                    TypeId = localTypeId,
                    Span = variable.Span
                };
                return true;

            case HirUnaryOp { Operator: Eidosc.Hir.UnaryOp.Deref } unaryOp:
            {
                var derefOperand = TryConvertPlaceShapedExprPlace(unaryOp.Operand, out var derefBasePlace)
                    ? derefBasePlace
                    : ConvertExpr(unaryOp.Operand);
                var basePlace = EnsurePlaceOperand(derefOperand, unaryOp.Operand.TypeId, unaryOp.Span);
                place = new MirPlace
                {
                    Kind = PlaceKind.Deref,
                    Base = basePlace,
                    TypeId = unaryOp.TypeId.IsValid ? unaryOp.TypeId : unaryOp.Operand.TypeId,
                    Span = unaryOp.Span
                };
                return true;
            }

            case HirFieldAccess fieldAccess:
            {
                var baseOperand = TryConvertPlaceShapedExprPlace(fieldAccess.Target, out var fieldBasePlace)
                    ? fieldBasePlace
                    : ConvertExpr(fieldAccess.Target);
                var basePlace = EnsureReadableProjectionBasePlace(baseOperand, fieldAccess.Target.TypeId, fieldAccess.Span);
                place = new MirPlace
                {
                    Kind = PlaceKind.Field,
                    Base = basePlace,
                    FieldName = NormalizeFieldAccessName(fieldAccess),
                    TypeId = fieldAccess.TypeId,
                    Span = fieldAccess.Span
                };
                return true;
            }

            case HirIndexAccess indexAccess:
            {
                var baseOperand = TryConvertPlaceShapedExprPlace(indexAccess.Target, out var indexBasePlace)
                    ? indexBasePlace
                    : ConvertExpr(indexAccess.Target);
                var basePlace = EnsureReadableProjectionBasePlace(baseOperand, indexAccess.Target.TypeId, indexAccess.Span);
                var indexOperand = ConvertExpr(indexAccess.Index);
                indexOperand = EnsureReadValue(indexOperand, indexAccess.Index.TypeId, indexAccess.Span);
                place = new MirPlace
                {
                    Kind = PlaceKind.Index,
                    Base = basePlace,
                    Index = indexOperand,
                    IndexAccessKind = ResolveIndexAccessKind(basePlace, indexAccess.TargetKind),
                    TypeId = indexAccess.TypeId,
                    Span = indexAccess.Span
                };
                return true;
            }
        }

        place = null!;
        return false;
    }

    private MirPlace EnsureReadableProjectionBasePlace(MirOperand operand, TypeId operandTypeId, SourceSpan span)
    {
        var basePlace = EnsurePlaceOperand(operand, operandTypeId, span);
        if (!TryResolveReferenceInnerTypeId(operandTypeId, out var innerTypeId))
        {
            return basePlace;
        }

        return new MirPlace
        {
            Kind = PlaceKind.Deref,
            Base = basePlace,
            TypeId = innerTypeId,
            Span = span
        };
    }

    private bool TryResolveReferenceInnerTypeId(TypeId referenceTypeId, out TypeId innerTypeId)
    {
        innerTypeId = TypeId.None;
        if (!referenceTypeId.IsValid ||
            !_dynamicTypeKeysById.TryGetValue(referenceTypeId.Value, out var typeKey))
        {
            return false;
        }

        return TryParseWrappedTypeId(typeKey, "Ref(", out innerTypeId) ||
               TryParseWrappedTypeId(typeKey, "MRef(", out innerTypeId);
    }

    private static bool TryParseWrappedTypeId(string typeKey, string prefix, out TypeId wrappedTypeId)
    {
        wrappedTypeId = TypeId.None;
        if (!typeKey.StartsWith(prefix, StringComparison.Ordinal) ||
            !typeKey.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        var innerText = typeKey[prefix.Length..^1];
        if (!int.TryParse(innerText, out var parsedTypeId))
        {
            return false;
        }

        wrappedTypeId = new TypeId(parsedTypeId);
        return wrappedTypeId.IsValid;
    }

    private MirOperand PrepareProjectedCallArgument(
        MirPlace place,
        TypeId fallbackType,
        SourceSpan span,
        bool forceCopy)
    {
        if (place.Kind == PlaceKind.Local)
        {
            return PrepareCallArgument(place, fallbackType, span, forceCopy);
        }

        if (!TryResolveOperandTypeId(place, fallbackType, span, "projected call argument", out var typeId))
        {
            return CreatePoisonOperand(TypeId.None, span, DiagnosticMessages.MissingMirTypeForProjectedCallArgumentReason);
        }

        var temp = NewTemp(typeId);
        _currentBlock!.Instructions.Add(new MirLoad
        {
            Target = temp,
            Source = place,
            CreatesBorrowAlias = ShouldCreateBorrowAliasForProjectedArgument(typeId, forceCopy),
            Span = span
        });
        return temp;
    }

    private bool ShouldCreateBorrowAliasForProjectedArgument(TypeId typeId, bool forceCopy)
    {
        if (IsFirstClassReferenceType(typeId))
        {
            return false;
        }

        // Copy types never create borrow aliases — the loaded value is an independent copy.
        if (IsCopyType(typeId))
        {
            return false;
        }

        // Non-Copy projected loads create borrow aliases (shared ownership semantics).
        return true;
    }

    private bool IsFirstClassReferenceType(TypeId typeId)
    {
        return typeId.IsValid &&
               _dynamicTypeKeysById.TryGetValue(typeId.Value, out var typeKey) &&
               (typeKey.StartsWith("Ref(", StringComparison.Ordinal) ||
                typeKey.StartsWith("MRef(", StringComparison.Ordinal));
    }

    private MirOperand PrepareReadonlyStringEqualsArgument(
        MirOperand operand,
        TypeId fallbackType,
        SourceSpan span)
    {
        if (operand is not MirPlace place)
        {
            return PrepareCallArgument(operand, fallbackType, span, forceCopy: true);
        }

        if (!TryResolveOperandTypeId(operand, fallbackType, span, "readonly string argument", out var typeId))
        {
            return CreatePoisonOperand(TypeId.None, span, DiagnosticMessages.MissingMirTypeForReadonlyStringArgumentReason);
        }

        var temp = NewTemp(typeId);
        if (place.Kind == PlaceKind.Local && ShouldCopyLocalValue(place.Local, typeId))
        {
            _currentBlock!.Instructions.Add(new MirCopy
            {
                Target = temp,
                Source = place,
                Span = span
            });
        }
        else
        {
            _currentBlock!.Instructions.Add(new MirLoad
            {
                Target = temp,
                Source = place,
                CreatesBorrowAlias = true,
                Span = span
            });
        }

        return temp;
    }

    private bool ShouldPassCallArgumentByCopy(MirOperand functionOperand, int argumentIndex)
    {
        if (functionOperand is MirFunctionRef { Name: var funcName, SymbolId: { IsValid: true } symbolId })
        {
            if (_parameterEffects != null &&
                _parameterEffects.TryGetEffects(funcName, symbolId.Value, out var effects) &&
                effects != null)
            {
                return argumentIndex < effects.Count && effects[argumentIndex] == ParameterEffect.Read;
            }

            if (_copyFirstArgumentFunctionSymbols.Contains(symbolId.Value) && argumentIndex == 0)
            {
                return true;
            }

            // No known effects for this function — default to Read (MirCopy).
        }

        // Unknown or untracked functions default to Read (MirCopy).
        // Extra copy/incref is always safe; only wasteful.
        // Functions that consume have effects tracked in _parameterEffects.
        return true;
    }
}
