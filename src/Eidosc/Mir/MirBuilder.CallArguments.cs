using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Types;
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

            if (IsFirstClassReferenceType(typeId))
            {
                return place with { TypeId = typeId };
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

            if (IsFirstClassReferenceType(typeId))
            {
                return place with { TypeId = typeId };
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

            case HirVar variable when TryResolveModuleVarPlace(variable, out var moduleVarPlace):
                place = moduleVarPlace;
                return true;

            case HirUnaryOp
            {
                Operator: Eidosc.Hir.UnaryOp.Ref or Eidosc.Hir.UnaryOp.MRef
            } unaryOp:
            {
                if (!TryConvertPlaceShapedExprPlace(unaryOp.Operand, out var referencedPlace))
                {
                    place = null!;
                    return false;
                }

                place = referencedPlace with
                {
                    TypeId = unaryOp.TypeId.IsValid ? unaryOp.TypeId : referencedPlace.TypeId,
                    Span = unaryOp.Span
                };
                return true;
            }

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
        if (!referenceTypeId.IsValid)
        {
            return false;
        }

        if (_typeDescriptorsById.TryGetValue(referenceTypeId.Value, out var descriptor))
        {
            switch (descriptor)
            {
                case TypeDescriptor.Ref reference:
                    innerTypeId = reference.Inner;
                    return true;
                case TypeDescriptor.MutRef mutableReference:
                    innerTypeId = mutableReference.Inner;
                    return true;
            }
        }

        if (!_dynamicTypeKeysById.TryGetValue(referenceTypeId.Value, out var typeKey) ||
            !TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor))
        {
            return false;
        }

        return descriptor switch
        {
            TypeDescriptor.Ref reference => (innerTypeId = reference.Inner).IsValid,
            TypeDescriptor.MutRef mutableReference => (innerTypeId = mutableReference.Inner).IsValid,
            _ => false
        };
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

        // A reference argument needs the projected place itself. Loading the
        // indexed value first turns a scalar into an integer-to-pointer cast and
        // loses the address required by Ref[T].
        if (IsFirstClassReferenceType(typeId))
        {
            return place with { TypeId = typeId };
        }

        var temp = NewTemp(typeId);
        _currentBlock!.Instructions.Add(new MirLoad
        {
            Target = temp,
            Source = place,
            CreatesBorrowAlias = ShouldCreateBorrowAliasForProjectedArgument(place, typeId, forceCopy),
            Span = span
        });
        return temp;
    }

    /// <summary>
    /// Seq 索引投影（xs[i]）可靠判别：基位置的类型是内建 Seq TyCon。
    /// 判据链：基 place 的 TypeId → 局部声明类型 → 描述符/动态键里的
    /// TyCon 构造键 == SymbolTable 解析到的 Seq 符号（缓存）。
    /// </summary>
    private TypeConstructorKey? _sequenceConstructorKey;
    private readonly HashSet<int> _sequenceTypeIds = [];

    private bool IsSequenceIndexProjection(MirPlace place)
    {
        if (place.Kind != PlaceKind.Index || place.Base == null)
        {
            return false;
        }

        var baseTypeId = place.Base.TypeId;
        if (!baseTypeId.IsValid && place.Base.Kind == PlaceKind.Local)
        {
            baseTypeId = _currentFunc?.Locals.FirstOrDefault(
                local => local.Id.Equals(place.Base.Local))?.TypeId ?? TypeId.None;
        }

        return IsSequenceTypeId(baseTypeId);
    }

    private bool IsSequenceTypeId(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (_sequenceTypeIds.Contains(typeId.Value))
        {
            return true;
        }

        _sequenceConstructorKey ??= _symbolTable?.LookupType(WellKnownStrings.BuiltinTypes.Seq) is { } sequenceSymbolId
            ? TypeConstructorKey.FromSymbol(sequenceSymbolId)
            : null;
        if (_sequenceConstructorKey is not { } sequenceKey)
        {
            return false;
        }

        if (_typeDescriptorsById.TryGetValue(typeId.Value, out var descriptor) &&
            descriptor is TypeDescriptor.TyCon { Constructor: var constructor } &&
            constructor == sequenceKey)
        {
            _sequenceTypeIds.Add(typeId.Value);
            return true;
        }

        if (_dynamicTypeKeysById.TryGetValue(typeId.Value, out var typeKey) &&
            TypeKeyParsing.TryParseTyConTypeKey(typeKey, out var dynamicConstructor, out _) &&
            dynamicConstructor == sequenceKey)
        {
            _sequenceTypeIds.Add(typeId.Value);
            return true;
        }

        return false;
    }

    private bool ShouldCreateBorrowAliasForProjectedArgument(MirPlace place, TypeId typeId, bool forceCopy)
    {
        _ = forceCopy;

        if (IsFirstClassReferenceType(typeId))
        {
            return false;
        }

        // Seq 索引投影按运行时语义是独立引用计数副本（非别名载入会 IncRef，
        // 见 MirToLlvmConverter 的托管 RC 投影载入路径），不是"从 owning
        // aggregate 移出"——直接作所有权实参是合法的。
        if (IsSequenceIndexProjection(place))
        {
            return false;
        }

        // Copy types never create borrow aliases — the loaded value is an
        // independent copy. Non-Copy projected loads create borrow aliases:
        // moving one element out of an owning aggregate is not an owned value,
        // and the borrow checker must see the alias so partial applications
        // that capture it report E1002 instead of silently moving storage.
        return !IsCopyType(typeId);
    }

    private bool IsFirstClassReferenceType(TypeId typeId)
    {
        return typeId.IsValid &&
               ((_typeDescriptorsById.TryGetValue(typeId.Value, out var descriptor) &&
                 descriptor is TypeDescriptor.Ref or TypeDescriptor.MutRef) ||
                (_dynamicTypeKeysById.TryGetValue(typeId.Value, out var typeKey) &&
                 TypeKeyParsing.TryParseTypeDescriptor(typeKey, out descriptor) &&
                 descriptor is TypeDescriptor.Ref or TypeDescriptor.MutRef));
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

}
