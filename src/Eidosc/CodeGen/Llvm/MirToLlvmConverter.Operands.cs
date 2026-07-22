using Eidosc.Symbols;
using System.Text;
using Eidosc.Borrow;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    private LlvmGlobal GetOrCreateStringLiteralGlobal(string value)
    {
        if (_stringLiteralGlobals.TryGetValue(value, out var existing))
        {
            return existing;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var payload = new byte[bytes.Length + 1];
        Array.Copy(bytes, payload, bytes.Length);
        payload[^1] = 0;

        var arrayType = new LlvmArrayType
        {
            Element = LlvmIntType.I8,
            Size = payload.Length
        };

        var globalName = CreateUniqueStringGlobalName();
        var global = new LlvmGlobal
        {
            Name = globalName,
            Type = arrayType,
            Initializer = new LlvmByteArrayConstant
            {
                Type = arrayType,
                Bytes = payload
            },
            Linkage = LlvmLinkage.Private,
            IsConstant = true
        };

        _currentModule!.Globals.Add(global);
        _currentModule.NamedGlobals[globalName] = global;
        _stringLiteralGlobals[value] = global;
        return global;
    }

    private string CreateUniqueStringGlobalName()
    {
        var candidate = $".str.{_stringLiteralCounter++}";
        if (_currentModule == null)
        {
            return candidate;
        }

        while (_currentModule.NamedGlobals.ContainsKey(candidate))
        {
            candidate = $".str.{_stringLiteralCounter++}";
        }

        return candidate;
    }

    private LlvmValue ConvertFunctionRef(MirFunctionRef funcRef)
    {
        var mangledName = ResolveFunctionLlvmName(funcRef);
        var functionType = ResolveFunctionType(funcRef);

        return new LlvmGlobal
        {
            Name = mangledName,
            Type = functionType is not null
                ? new LlvmPointerType
                {
                    ElementType = functionType,
                    AddressSpace = 0
                }
                : LlvmPointerType.VoidPtr()
        };
    }

    private LlvmValue ConvertPlace(MirPlace place)
    {
        switch (place.Kind)
        {
            case PlaceKind.Local:
                if (_partialCallStates.TryGetValue(place.Local, out var partial))
                {
                    return MaterializePartialClosureValue(place, partial);
                }

                if (IsSlotBackedLocal(place.Local))
                {
                    return LoadFromLocalSlot(place.Local, place.TypeId);
                }

                var localValue = GetOrCreateLocalById(place.Local, place.TypeId);
                if (_locals.RuntimeWordLocals.Contains(place.Local))
                {
                    var expectedType = ResolveLoweredPlaceTypeOrFallback(place, TypeId.None, "runtime-word local");
                    if (!IsOpaqueRuntimeWordType(expectedType))
                    {
                        return CoerceDispatchWordForCurrentBlock(localValue, expectedType, $"l{place.Local.Value}_coerce");
                    }
                }

                return localValue;

            case PlaceKind.Deref:
                var derefPointer = ConvertPlace(place.Base!);
                var derefType = ResolveDerefValueType(place);
                // Heap-backed Eidos values (Seq, String, ADTs, and other pointer-represented
                // types) are already passed as their runtime pointer for Ref[T]. Scalar
                // references remain address-backed and require an LLVM load.
                if (derefType is LlvmPointerType)
                {
                    return derefPointer;
                }

                var loadInstr = new LlvmLoad
                {
                    Pointer = derefPointer,
                    LoadType = derefType,
                    ResultName = _nameMangler.NewTempName("deref")
                };
                _currentBlock?.Instructions.Add(loadInstr);
                return new LlvmInstructionRef { Instruction = loadInstr, Type = loadInstr.LoadType };

            case PlaceKind.Field:
                var fieldBase = place.Base!;
                if (TryParseAggregateFieldOrdinal(place.FieldName, out var fieldOrdinal) &&
                    _typeLowering.TryGetStructType(fieldBase.TypeId, out var fieldStructType))
                {
                    var hasTag = HasTagFieldForType(fieldBase.TypeId);
                    var structFieldIndex = ComputeStructFieldIndex(hasTag, fieldOrdinal);
                    var structFieldGEP = new LlvmGetElementPtr
                    {
                        Pointer = ResolveFieldBasePointer(fieldBase),
                        StructType = fieldStructType,
                        StructFieldIndex = structFieldIndex,
                        ResultName = _nameMangler.NewTempName("field")
                    };
                    _currentBlock?.Instructions.Add(structFieldGEP);
                    var fieldLoadType = GetStructFieldLoadType(fieldStructType, structFieldIndex);
                    return new LlvmInstructionRef { Instruction = structFieldGEP, Type = new LlvmPointerType { ElementType = fieldLoadType } };
                }

                var fieldPtr = new LlvmGetElementPtr
                {
                    Pointer = ResolveFieldBasePointer(fieldBase),
                    ElementType = LlvmIntType.I8,
                    Index = BuildAggregateFieldOffset(place.FieldName, place.Span),
                    ResultName = _nameMangler.NewTempName("field")
                };
                _currentBlock?.Instructions.Add(fieldPtr);
                return new LlvmInstructionRef { Instruction = fieldPtr, Type = LlvmPointerType.VoidPtr() };

            case PlaceKind.Index:
                if (place.IndexAccessKind == MirIndexAccessKind.Aggregate)
                {
                    if (place.Index is MirConstant { Value: MirConstantValue.IntValue indexConst } &&
                        TryResolveAggregateStructType(place.Base?.TypeId ?? TypeId.None, out var aggStructType))
                    {
                        var aggHasTag = HasTagFieldForType(place.Base?.TypeId ?? TypeId.None);
                        var aggFieldIndex = ComputeStructFieldIndex(aggHasTag, (int)indexConst.Value);
                        var structIndexGEP = new LlvmGetElementPtr
                        {
                            Pointer = ResolveAggregateBasePointer(place.Base!, "aggregate_base"),
                            StructType = aggStructType,
                            StructFieldIndex = aggFieldIndex,
                            ResultName = _nameMangler.NewTempName("index")
                        };
                        _currentBlock?.Instructions.Add(structIndexGEP);
                        var indexLoadType = GetStructFieldLoadType(aggStructType, aggFieldIndex);
                        return new LlvmInstructionRef { Instruction = structIndexGEP, Type = new LlvmPointerType { ElementType = indexLoadType } };
                    }

                    var aggregateIndexPtr = new LlvmGetElementPtr
                    {
                        Pointer = ResolveAggregateBasePointer(place.Base!, "aggregate_base"),
                        ElementType = LlvmIntType.I8,
                        Index = BuildAggregateIndexByteOffset(place.Index!),
                        ResultName = _nameMangler.NewTempName("index")
                    };
                    _currentBlock?.Instructions.Add(aggregateIndexPtr);
                    return new LlvmInstructionRef { Instruction = aggregateIndexPtr, Type = LlvmPointerType.VoidPtr() };
                }

                var indexPtr = new LlvmGetElementPtr
                {
                    Pointer = ConvertPlace(place.Base!),
                    ElementType = ResolveLoweredPlaceTypeOrFallback(place, TypeId.None, "index pointer"),
                    Index = ConvertOperand(place.Index!),
                    ResultName = _nameMangler.NewTempName("index")
                };
                _currentBlock?.Instructions.Add(indexPtr);
                return new LlvmInstructionRef { Instruction = indexPtr, Type = LlvmPointerType.VoidPtr() };

            default:
                return ReportUnsupportedPlaceKindFallback(place, "place conversion");
        }
    }

    private LlvmValue ResolveFieldBasePointer(MirPlace basePlace)
    {
        return ResolveAggregateBasePointer(basePlace, "field_base");
    }

    private LlvmValue ResolveRuntimeArrayBasePointer(MirPlace basePlace)
    {
        return basePlace.Kind switch
        {
            PlaceKind.Local or PlaceKind.Deref => CoerceToPointer(ConvertPlace(basePlace)),
            _ => CoerceToPointer(MaterializePlaceValue(basePlace, basePlace.TypeId, "array_base"))
        };
    }

    private LlvmValue ResolveAggregateBasePointer(MirPlace basePlace, string tempPrefix)
    {
        var baseValue = basePlace.Kind switch
        {
            PlaceKind.Local or PlaceKind.Deref => ConvertPlace(basePlace),
            _ => MaterializePlaceValue(basePlace, basePlace.TypeId, tempPrefix)
        };

        var baseType = LowerStorageTypeIdOrReport(basePlace.TypeId, "resolve aggregate base pointer");
        return baseType is LlvmStructType or LlvmArrayType
            ? CreateAddressableValuePointer(baseValue, baseType)
            : CoerceToPointer(baseValue);
    }

    private LlvmValue MaterializePlaceValue(MirPlace place, TypeId fallbackType, string tempPrefix)
    {
        if (place.Kind is PlaceKind.Local or PlaceKind.Deref)
        {
            return ConvertPlace(place);
        }

        if (place.Kind == PlaceKind.Index &&
            place.IndexAccessKind == MirIndexAccessKind.RuntimeArray &&
            place.Base != null &&
            place.Index != null)
        {
            var arrayValue = ResolveRuntimeArrayBasePointer(place.Base);
            var indexValue = CoerceToI64(ConvertOperand(place.Index));

            var getCall = new LlvmCall
            {
                Function = CreateRuntimeFunctionGlobal(
                    WellKnownStrings.Runtime.ArrayGet,
                    LlvmPointerType.VoidPtr(),
                    [LlvmPointerType.VoidPtr(), LlvmIntType.I64]),
                Arguments = [arrayValue, indexValue],
                ReturnType = LlvmPointerType.VoidPtr(),
                ResultName = _nameMangler.NewTempName("array_get")
            };
            _currentBlock?.Instructions.Add(getCall);

            var loadType = ResolveRuntimeArrayElementType(place, fallbackType);
            if (loadType is LlvmVoidType)
            {
                return LlvmConstant.Zero;
            }

            var typedLoad = new LlvmLoad
            {
                Pointer = new LlvmInstructionRef
                {
                    Instruction = getCall,
                    Type = LlvmPointerType.VoidPtr()
                },
                LoadType = loadType,
                ResultName = _nameMangler.NewTempName(tempPrefix)
            };
            _currentBlock?.Instructions.Add(typedLoad);
            return new LlvmInstructionRef
            {
                Instruction = typedLoad,
                Type = loadType
            };
        }

        var resolvedType = ResolvePlaceTypeId(place, fallbackType);
        var loadInstr = new LlvmLoad
        {
            Pointer = ConvertPlace(place),
            LoadType = LowerStorageTypeIdOrReport(resolvedType, $"materialize {place.Kind} place"),
            ResultName = _nameMangler.NewTempName(tempPrefix)
        };
        _currentBlock?.Instructions.Add(loadInstr);
        return new LlvmInstructionRef
        {
            Instruction = loadInstr,
            Type = loadInstr.LoadType
        };
    }

    private LlvmType GetAggregateStorageType(LocalId localId, TypeId fallbackType)
    {
        if (TryResolveAggregateStorageType(fallbackType, out var aggregateType))
        {
            return aggregateType;
        }

        if (!TryGetAggregateStorageWordCount(localId, out var wordCount) &&
            _locals.AggregateStorageWordCountByLocal.TryGetValue(localId, out var cachedWordCount))
        {
            wordCount = cachedWordCount;
        }

        if (wordCount > 0)
        {
            return new LlvmArrayType
            {
                Element = LlvmIntType.I8,
                Size = checked(wordCount * 8)
            };
        }

        return LowerStorageTypeIdOrReport(fallbackType, "aggregate storage fallback");
    }

    private bool TryResolveAggregateStorageType(TypeId typeId, out LlvmType aggregateType)
    {
        aggregateType = LlvmVoidType.Instance;
        if (!typeId.IsValid || _typeLowering.IsOpenDynamicType(typeId))
        {
            return false;
        }

        var loweredType = LowerStorageTypeIdOrReport(typeId, "aggregate storage type");
        if (loweredType is LlvmStructType or LlvmArrayType)
        {
            aggregateType = loweredType;
            return true;
        }

        return false;
    }

    private bool TryResolveAggregateStructType(TypeId typeId, out LlvmStructType structType)
    {
        if (_typeLowering.TryGetStructType(typeId, out var namedStructType))
        {
            structType = namedStructType;
            return true;
        }

        if (typeId.IsValid &&
            !_typeLowering.IsOpenDynamicType(typeId) &&
            LowerStorageTypeIdOrReport(typeId, "aggregate struct type") is LlvmStructType loweredStructType)
        {
            structType = loweredStructType;
            return true;
        }

        structType = null!;
        return false;
    }

    private LlvmType ResolveRuntimeArrayElementType(MirPlace indexPlace, TypeId? fallbackTypeId = null)
    {
        var candidateTypeId = ResolvePlaceTypeId(indexPlace, fallbackTypeId);
        var baseTypeId = indexPlace.Base is null
            ? TypeId.None
            : ResolvePlaceTypeId(indexPlace.Base);

        if (ShouldInferRuntimeArrayElementType(candidateTypeId) &&
            _typeLowering.TryGetTyConTypeArguments(baseTypeId, out _, out var inferredTypeArguments) &&
            inferredTypeArguments.Count > 0)
        {
            return LowerStorageTypeIdOrReport(inferredTypeArguments[0], "runtime array inferred element");
        }

        var candidateType = LowerStorageTypeIdOrReport(candidateTypeId, "runtime array element");
        if (IsOpaqueRuntimeWordType(candidateType) &&
            _typeLowering.TryGetTyConTypeArguments(baseTypeId, out _, out var typeArguments) &&
            typeArguments.Count > 0)
        {
            return LowerStorageTypeIdOrReport(typeArguments[0], "runtime array type argument element");
        }

        return candidateType;
    }

    private LlvmType ResolveDerefValueType(MirPlace place)
    {
        var valueTypeId = ResolvePlaceTypeId(place);
        if (valueTypeId.IsValid &&
            _typeLowering.TypeDescriptors.TryGetValue(valueTypeId.Value, out var descriptor))
        {
            valueTypeId = descriptor switch
            {
                TypeDescriptor.Ref reference => reference.Inner,
                TypeDescriptor.MutRef reference => reference.Inner,
                _ => valueTypeId
            };
        }

        return valueTypeId.IsValid
            ? LowerStorageTypeIdOrReport(valueTypeId, "deref load")
            : ResolveLoweredPlaceTypeOrFallback(place, TypeId.None, "deref load");
    }

    private TypeId ResolvePlaceTypeId(MirPlace place, TypeId? fallbackTypeId = null)
    {
        if (place.TypeId.IsValid)
        {
            return place.TypeId;
        }

        if (place.Kind == PlaceKind.Local &&
            _locals.LocalTypeById.TryGetValue(place.Local, out var localTypeId) &&
            localTypeId.IsValid)
        {
            return localTypeId;
        }

        return fallbackTypeId ?? TypeId.None;
    }

    private bool TryInferRuntimeArrayElementTypeFromLocalUses(LocalId sourceLocalId, out LlvmType inferredType)
    {
        inferredType = LlvmPointerType.VoidPtr();
        if (_currentMirFunction == null)
        {
            return false;
        }

        var pendingLocals = new Queue<LocalId>();
        var visitedLocals = new HashSet<LocalId>();
        pendingLocals.Enqueue(sourceLocalId);
        visitedLocals.Add(sourceLocalId);

        while (pendingLocals.Count > 0)
        {
            var currentLocalId = pendingLocals.Dequeue();
            foreach (var block in _currentMirFunction.BasicBlocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    switch (instruction)
                    {
                        case MirCopy
                        {
                            Source: { Kind: PlaceKind.Local } copySource,
                            Target: { Kind: PlaceKind.Local } copyTarget
                        } when copySource.Local == currentLocalId:
                            if (visitedLocals.Add(copyTarget.Local))
                            {
                                pendingLocals.Enqueue(copyTarget.Local);
                            }

                            break;

                        case MirMove
                        {
                            Source: { Kind: PlaceKind.Local } moveSource,
                            Target: { Kind: PlaceKind.Local } moveTarget
                        } when moveSource.Local == currentLocalId:
                            if (visitedLocals.Add(moveTarget.Local))
                            {
                                pendingLocals.Enqueue(moveTarget.Local);
                            }

                            break;

                        case MirAssign
                        {
                            Source: MirPlace { Kind: PlaceKind.Local } assignSource,
                            Target: { Kind: PlaceKind.Local } assignTarget
                        } when assignSource.Local == currentLocalId:
                            if (visitedLocals.Add(assignTarget.Local))
                            {
                                pendingLocals.Enqueue(assignTarget.Local);
                            }

                            break;

                        case MirLoad
                        {
                            Source: MirPlace { Kind: PlaceKind.Local } loadSource,
                            Target: { Kind: PlaceKind.Local } loadTarget
                        } when loadSource.Local == currentLocalId:
                            if (visitedLocals.Add(loadTarget.Local))
                            {
                                pendingLocals.Enqueue(loadTarget.Local);
                            }

                            break;

                        case MirStore
                        {
                            Value: MirPlace { Kind: PlaceKind.Local } storeValue,
                            Target: { Kind: PlaceKind.Local } storeTarget
                        } when storeValue.Local == currentLocalId:
                            if (visitedLocals.Add(storeTarget.Local))
                            {
                                pendingLocals.Enqueue(storeTarget.Local);
                            }

                            break;

                        case MirCall call:
                            for (var argumentIndex = 0; argumentIndex < call.Arguments.Count; argumentIndex++)
                            {
                                if (call.Arguments[argumentIndex] is not MirPlace { Kind: PlaceKind.Local } argumentPlace ||
                                    argumentPlace.Local != currentLocalId ||
                                    !TryResolveCallableSignature(call.Function, out var functionType) ||
                                    argumentIndex >= functionType.ParameterTypes.Count)
                                {
                                    continue;
                                }

                                var parameterType = functionType.ParameterTypes[argumentIndex];
                                if (!IsOpaqueRuntimeWordType(parameterType))
                                {
                                    inferredType = parameterType;
                                    return true;
                                }
                            }

                            break;
                    }
                }
            }
        }

        return false;
    }

    private bool ShouldInferRuntimeArrayElementType(TypeId elementTypeId)
    {
        if (!elementTypeId.IsValid)
        {
            return true;
        }

        return _typeLowering.TryGetDynamicTypeKey(elementTypeId, out var typeKey) &&
               typeKey.StartsWith("TyVar_", StringComparison.Ordinal);
    }

    private static bool IsOpaqueRuntimeWordType(LlvmType type)
    {
        return type is LlvmPointerType { ElementType: null };
    }

    private bool TryGetAggregateStorageWordCount(LocalId localId, out int wordCount)
    {
        if (_locals.AggregateStorageWordCountByLocal.TryGetValue(localId, out wordCount) && wordCount > 0)
        {
            return true;
        }

        wordCount = 0;
        if (_currentMirFunction == null)
        {
            return false;
        }

        foreach (var block in _currentMirFunction.BasicBlocks)
        {
            foreach (var store in block.Instructions.OfType<MirStore>())
            {
                if (store.Target is not MirPlace
                    {
                        Kind: PlaceKind.Index,
                        IndexAccessKind: MirIndexAccessKind.Aggregate,
                        Base: { Kind: PlaceKind.Local } baseLocal,
                        Index: MirConstant { Value: MirConstantValue.IntValue intValue }
                    } ||
                    baseLocal.Local != localId ||
                    intValue.Value < 0 ||
                    intValue.Value > int.MaxValue - 1)
                {
                    continue;
                }

                wordCount = Math.Max(wordCount, (int)intValue.Value + 1);
            }
        }

        if (wordCount > 0)
        {
            _locals.AggregateStorageWordCountByLocal[localId] = wordCount;
            return true;
        }

        return false;
    }

    private LlvmValue BuildAggregateIndexByteOffset(MirOperand indexOperand)
    {
        var indexValue = CoerceToI64(ConvertOperand(indexOperand));
        if (TryGetIntegerConstantValue(indexValue, out var constantIndex))
        {
            return new LlvmConstant
            {
                Value = checked(constantIndex * 8L),
                Type = LlvmIntType.I64
            };
        }

        var scaledIndex = new LlvmBinOp
        {
            Op = "mul",
            Left = indexValue,
            Right = new LlvmConstant
            {
                Value = 8L,
                Type = LlvmIntType.I64
            },
            ResultType = LlvmIntType.I64,
            ResultName = _nameMangler.NewTempName("agg_index")
        };
        _currentBlock?.Instructions.Add(scaledIndex);
        return new LlvmInstructionRef
        {
            Instruction = scaledIndex,
            Type = LlvmIntType.I64
        };
    }

    private static bool TryGetIntegerConstantValue(LlvmValue value, out long constantValue)
    {
        if (value is LlvmConstant { Value: long longValue })
        {
            constantValue = longValue;
            return true;
        }

        if (value is LlvmConstant { Value: int intValue })
        {
            constantValue = intValue;
            return true;
        }

        constantValue = 0;
        return false;
    }

    private LlvmConstant BuildAggregateFieldOffset(string? fieldName, SourceSpan span)
    {
        if (TryParseAggregateFieldOrdinal(fieldName, out var ordinal))
        {
            return new LlvmConstant
            {
                Value = ordinal * 8L,
                Type = LlvmIntType.I64
            };
        }

        var normalizedFieldName = string.IsNullOrWhiteSpace(fieldName) ? "<empty>" : fieldName;
        var diag = Diagnostic.Diagnostic.Error(
            DiagnosticMessages.CannotLowerAggregateFieldToByteOffset(normalizedFieldName),
            "E3301");
        if (HasSpan(span))
        {
            diag.WithLabel(span, DiagnosticMessages.UnresolvedAggregateFieldLabel);
        }
        Diagnostics.Add(diag);

        return LlvmConstant.Zero;
    }

    /// <summary>
    /// Computes the struct field GEP index for a logical field ordinal.
    /// The tag is stored in EidosHeader (outside payload), so payload fields
    /// are always at their natural index with no offset.
    /// </summary>
    private static int ComputeStructFieldIndex(bool hasTagField, int fieldOrdinal)
    {
        return fieldOrdinal;
    }

    /// <summary>
    /// Gets the load type for a struct field at the given GEP index.
    /// Falls back to i64 if the index is out of range.
    /// </summary>
    private static LlvmType GetStructFieldLoadType(LlvmStructType structType, int structFieldIndex)
    {
        return structFieldIndex >= 0 && structFieldIndex < structType.Fields.Count
            ? structType.Fields[structFieldIndex]
            : LlvmIntType.I64;
    }

    /// <summary>
    /// Returns true if the struct type has a tag field (multi-ctor ADT).
    /// Uses TypeLowering to check constructor count.
    /// </summary>
    private bool HasTagFieldForType(TypeId typeId)
    {
        return _typeLowering.TryGetConstructorLayouts(typeId, out var layouts) && layouts.Count > 1;
    }

    private static bool TryParseAggregateFieldOrdinal(string? fieldName, out int ordinal)
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

    private LlvmConstant ConvertConstantToLlvm(MirConstant constOp)
    {
        var type = InferConstantLlvmType(constOp);
        var value = constOp.Value switch
        {
            MirConstantValue.IntValue intVal => (object?)intVal.Value,
            MirConstantValue.FloatValue floatVal => floatVal.Value,
            MirConstantValue.StringValue strVal => strVal.Value,
            MirConstantValue.CharValue charVal => (object?)(long)charVal.Value,
            MirConstantValue.BoolValue boolVal => boolVal.Value,
            MirConstantValue.UnitValue => type is LlvmVoidType ? null : 0L,
            _ => null
        };

        return new LlvmConstant
        {
            Value = value,
            Type = type
        };
    }

    private LlvmConstant ConvertConstantToLlvm(MirConstant constOp, LlvmType expectedType)
    {
        var converted = ConvertConstantToLlvm(constOp);
        if (converted.Type == expectedType)
        {
            return converted;
        }

        if (expectedType is LlvmIntType intType &&
            TryGetIntegerLikeConstantValue(converted.Value, out var intValue))
        {
            return new LlvmConstant
            {
                Value = intValue,
                Type = intType
            };
        }

        if (expectedType is LlvmPointerType && converted.Value == null)
        {
            return new LlvmConstant
            {
                Value = null,
                Type = expectedType
            };
        }

        return converted;
    }

    private LlvmType InferConstantLlvmType(MirConstant constOp)
    {
        LlvmType loweredType = constOp.TypeId.IsValid
            ? LowerStorageTypeIdOrReport(constOp.TypeId, "constant operand")
            : LlvmVoidType.Instance;

        return constOp.Value switch
        {
            MirConstantValue.IntValue => loweredType is LlvmIntType intType ? intType : LlvmIntType.I64,
            MirConstantValue.FloatValue => loweredType is LlvmFloatType floatType ? floatType : LlvmFloatType.Double,
            MirConstantValue.StringValue => LlvmPointerType.VoidPtr(),
            MirConstantValue.CharValue => loweredType is LlvmIntType charType ? charType : LlvmIntType.I32,
            MirConstantValue.BoolValue => loweredType is LlvmIntType boolType ? boolType : LlvmIntType.I1,
            MirConstantValue.UnitValue => loweredType is LlvmVoidType ? LlvmIntType.I1 : loweredType,
            _ => LlvmVoidType.Instance
        };
    }

    private static bool TryGetIntegerLikeConstantValue(object? value, out long intValue)
    {
        switch (value)
        {
            case int int32:
                intValue = int32;
                return true;
            case long int64:
                intValue = int64;
                return true;
            case bool boolValue:
                intValue = boolValue ? 1L : 0L;
                return true;
            default:
                intValue = 0L;
                return false;
        }
    }

    private void EmitSlotLocalsForEntryBlock()
    {
        if (_locals.SlotBackedLocals.Count == 0 || _currentBlock == null)
        {
            return;
        }

        foreach (var localId in _locals.SlotBackedLocals.OrderBy(local => local.Value))
        {
            if (!_locals.LocalTypeById.TryGetValue(localId, out var typeId))
            {
                continue;
            }

            var loweredType = LowerStorageTypeIdOrReport(
                typeId,
                "entry slot local",
                _currentFunctionAllowsOpenLocalTypes);
            if (loweredType is LlvmVoidType)
            {
                continue;
            }

            var slot = new LlvmAlloca
            {
                AllocatedType = loweredType,
                Alignment = 8,
                ResultName = _nameMangler.NewTempName($"l{localId.Value}_slot")
            };

            _currentBlock.Instructions.Add(slot);
            _locals.LocalSlots[localId] = slot;
        }

        foreach (var localId in _locals.SlotBackedLocals.OrderBy(local => local.Value))
        {
            if (!_locals.LocalMap.TryGetValue(localId, out var parameterValue))
            {
                continue;
            }

            var store = CreateStoreToLocalSlot(localId, parameterValue);
            if (store != null)
            {
                _currentBlock.Instructions.Add(store);
            }
        }
    }

    private bool IsSlotBackedLocal(LocalId localId) => _locals.SlotBackedLocals.Contains(localId);

    private LlvmStore? CreateStoreToLocalSlot(LocalId localId, LlvmValue value)
    {
        if (value.Type is LlvmVoidType || !_locals.LocalSlots.TryGetValue(localId, out var slot))
        {
            return null;
        }

        var storeValue = value;
        if (_locals.LocalTypeById.TryGetValue(localId, out var localTypeId))
        {
            var allowOpenTypes = _currentFunctionAllowsOpenLocalTypes;
            var localStorageType = LowerStorageTypeIdOrReport(localTypeId, "local slot storage", allowOpenTypes);
            if (localStorageType is LlvmStructType or LlvmArrayType &&
                value.Type is LlvmPointerType)
            {
                var load = new LlvmLoad
                {
                    Pointer = CoerceToPointer(value),
                    LoadType = localStorageType,
                    IsVolatile = false,
                    ResultName = _nameMangler.NewTempName($"l{localId.Value}_slot_value")
                };
                _currentBlock?.Instructions.Add(load);
                storeValue = new LlvmInstructionRef
                {
                    Instruction = load,
                    Type = localStorageType
                };
            }
        }

        return new LlvmStore
        {
            Value = storeValue,
            Pointer = new LlvmInstructionRef
            {
                Instruction = slot,
                Type = LlvmPointerType.VoidPtr()
            },
            IsVolatile = false
        };
    }

    private void QueueStoreToLocalSlot(LocalId localId, LlvmValue value)
    {
        var store = CreateStoreToLocalSlot(localId, value);
        if (store != null)
        {
            _postInstructionBuffer.Add(store);
        }
    }

    private LlvmValue LoadFromLocalSlot(LocalId localId, TypeId typeId)
    {
        if (!_locals.LocalSlots.TryGetValue(localId, out var slot))
        {
            return GetOrCreateLocalById(localId, typeId);
        }

        var loadType = LowerLocalTypeOrReport(localId, typeId, "load local slot");
        if (loadType is LlvmVoidType)
        {
            return LlvmConstant.Zero;
        }

        var load = new LlvmLoad
        {
            Pointer = new LlvmInstructionRef
            {
                Instruction = slot,
                Type = LlvmPointerType.VoidPtr()
            },
            LoadType = loadType,
            IsVolatile = false,
            ResultName = _nameMangler.NewTempName($"l{localId.Value}_ld")
        };

        _currentBlock?.Instructions.Add(load);
        return new LlvmInstructionRef
        {
            Instruction = load,
            Type = loadType
        };
    }

    private static LocalId? TryGetTargetLocal(MirOperand? target)
    {
        return target is MirPlace { Kind: PlaceKind.Local } place ? place.Local : null;
    }

    private void BuildLocalDefinitionIndex(MirFunc func)
    {
        foreach (var block in func.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (!TryGetDefinedLocal(instruction, out var local))
                {
                    continue;
                }

                if (!_definitionInstructionsByLocal.TryGetValue(local, out var definitions))
                {
                    definitions = [];
                    _definitionInstructionsByLocal[local] = definitions;
                }

                definitions.Add(instruction);

                if (_definitionStatsByLocal.TryGetValue(local, out var stats))
                {
                    stats.Count++;
                    stats.HasMultipleBlocks |= !stats.FirstBlock.Equals(block.Id);
                    _definitionStatsByLocal[local] = stats;
                }
                else
                {
                    _definitionStatsByLocal[local] = new LocalDefinitionStats(block.Id, 1, false);
                }
            }
        }
    }

    private static HashSet<LocalId> ComputeSlotBackedLocals(
        MirFunc func,
        IReadOnlyDictionary<LocalId, LocalDefinitionStats> definitionStatsByLocal)
    {
        var slotBacked = new HashSet<LocalId>();
        foreach (var local in func.Locals)
        {
            if (local.IsMutable)
            {
                slotBacked.Add(local.Id);
            }
        }

        foreach (var (local, stats) in definitionStatsByLocal)
        {
            if (stats.HasMultipleBlocks || stats.Count > 1)
            {
                slotBacked.Add(local);
            }
        }

        return slotBacked;
    }

    private struct LocalDefinitionStats(BlockId firstBlock, int count, bool hasMultipleBlocks)
    {
        public BlockId FirstBlock { get; } = firstBlock;
        public int Count { get; set; } = count;
        public bool HasMultipleBlocks { get; set; } = hasMultipleBlocks;
    }

    private static bool TryGetDefinedLocal(MirInstruction instruction, out LocalId local)
    {
        switch (instruction)
        {
            case MirAssign { Target: { Kind: PlaceKind.Local } assignTarget }:
                local = assignTarget.Local;
                return true;

            case MirStore { Target: { Kind: PlaceKind.Local } storeTarget }:
                local = storeTarget.Local;
                return true;

            case MirCall { Target: { Kind: PlaceKind.Local } callTarget }:
                local = callTarget.Local;
                return true;

            case MirLoad { Target: { Kind: PlaceKind.Local } loadTarget }:
                local = loadTarget.Local;
                return true;

            case MirCopy { Target: { Kind: PlaceKind.Local } copyTarget }:
                local = copyTarget.Local;
                return true;

            case MirMove { Target: { Kind: PlaceKind.Local } moveTarget }:
                local = moveTarget.Local;
                return true;

            case MirAlloc { Target: { Kind: PlaceKind.Local } allocTarget }:
                local = allocTarget.Local;
                return true;

            case MirBinOp { Target: MirPlace { Kind: PlaceKind.Local } binOpTarget }:
                local = binOpTarget.Local;
                return true;

            case MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local } unaryTarget }:
                local = unaryTarget.Local;
                return true;

            default:
                local = default;
                return false;
        }
    }

    private LlvmLocal GetOrCreateLocalFromOperand(MirOperand operand, string context)
    {
        return operand switch
        {
            MirPlace place => GetOrCreateLocal(place),
            MirTemp temp => GetOrCreateLocalById(new LocalId { Value = temp.Id.Value }, temp.TypeId),
            _ => CreateUnknownLocalForUnsupportedTargetOperand(operand, context)
        };
    }

    private LlvmLocal CreateUnknownLocalForUnsupportedTargetOperand(MirOperand operand, string context)
    {
        ReportUnsupportedTargetOperandFallback(operand, context);
        return new LlvmLocal
        {
            Name = _nameMangler.NewTempName("unknown"),
            Type = LlvmPointerType.VoidPtr()
        };
    }

    private LlvmLocal GetOrCreateLocal(MirPlace place)
    {
        if (place.Kind == PlaceKind.Local)
        {
            return GetOrCreateLocalById(place.Local, place.TypeId);
        }

        return new LlvmLocal
        {
            Name = _nameMangler.NewTempName("place"),
            Type = ResolveLoweredPlaceTypeOrFallback(place, TypeId.None, "materialize non-local place")
        };
    }

    private LlvmLocal GetOrCreateLocalById(LocalId id, TypeId typeId)
    {
        if (_locals.LocalMap.TryGetValue(id, out var llvmLocal))
        {
            return llvmLocal;
        }

        llvmLocal = new LlvmLocal
        {
            Name = $"l{id.Value}",
            Type = LowerLocalTypeOrReport(id, typeId, "materialize local")
        };
        _locals.LocalMap[id] = llvmLocal;
        return llvmLocal;
    }

    private LlvmType ResolveLoweredPlaceTypeOrFallback(MirPlace place, TypeId fallbackTypeId, string context)
    {
        var resolvedTypeId = ResolvePlaceTypeId(place, fallbackTypeId);
        if (resolvedTypeId.IsValid)
        {
            return LowerStorageTypeIdOrReport(resolvedTypeId, context);
        }

        if (place.Kind == PlaceKind.Local &&
            _locals.LocalMap.TryGetValue(place.Local, out var loweredLocal))
        {
            return loweredLocal.Type;
        }

        if (place.Kind == PlaceKind.Local &&
            _locals.RuntimeWordLocals.Contains(place.Local))
        {
            return LlvmIntType.I64;
        }

        if (place.Kind == PlaceKind.Local &&
            TryInferLocalTypeFromDefinitions(place.Local, out var inferredLocalType))
        {
            return inferredLocalType;
        }

        if (place.Kind == PlaceKind.Local &&
            IsToleratedUnresolvedLocal(place.Local))
        {
            return LlvmPointerType.VoidPtr();
        }

        ReportUnresolvedValueTypeLeakage($"place:{place.Kind}:{place.Local.Value}", context);
        return LlvmPointerType.VoidPtr();
    }

    private LlvmType LowerLocalTypeOrReport(LocalId localId, TypeId typeId, string context)
    {
        if (typeId.IsValid)
        {
            return LowerStorageTypeIdOrReport(typeId, context);
        }

        if (_locals.LocalTypeById.TryGetValue(localId, out var localTypeId) &&
            localTypeId.IsValid)
        {
            return LowerStorageTypeIdOrReport(localTypeId, context);
        }

        if (_locals.LocalMap.TryGetValue(localId, out var loweredLocal))
        {
            return loweredLocal.Type;
        }

        if (_locals.RuntimeWordLocals.Contains(localId))
        {
            return LlvmIntType.I64;
        }

        if (TryInferLocalTypeFromDefinitions(localId, out var inferredLocalType))
        {
            return inferredLocalType;
        }

        if (IsToleratedUnresolvedLocal(localId))
        {
            return LlvmPointerType.VoidPtr();
        }

        ReportUnresolvedValueTypeLeakage($"local:{localId.Value}", context);
        return LlvmPointerType.VoidPtr();
    }

    private LlvmType LowerTypeIdOrReport(
        TypeId typeId,
        string context,
        bool allowOpenDynamicTypes = true)
    {
        if (!typeId.IsValid)
        {
            ReportUnresolvedValueTypeLeakage($"type:{typeId.Value}", context);
            return LlvmPointerType.VoidPtr();
        }

        if (!_typeLowering.HasKnownLoweringMetadata(typeId))
        {
            ReportUnknownTypeIdLoweringFallback(typeId, context);
            return LlvmPointerType.VoidPtr();
        }

        return _typeLowering.Lower(typeId, allowOpenDynamicTypes);
    }

    private LlvmType LowerStorageTypeIdOrReport(
        TypeId typeId,
        string context,
        bool allowOpenDynamicTypes = true)
    {
        if (!typeId.IsValid)
        {
            ReportUnresolvedValueTypeLeakage($"type:{typeId.Value}", context);
            return LlvmPointerType.VoidPtr();
        }

        if (!_typeLowering.HasKnownLoweringMetadata(typeId))
        {
            ReportUnknownTypeIdLoweringFallback(typeId, context);
            return LlvmPointerType.VoidPtr();
        }

        return _typeLowering.LowerStorage(typeId, allowOpenDynamicTypes);
    }

    private bool TryInferLocalTypeFromDefinitions(LocalId localId, out LlvmType inferredType)
    {
        if (_inferredLocalTypeCache.TryGetValue(localId, out var cachedType))
        {
            inferredType = cachedType;
            return true;
        }

        if (_failedLocalTypeInferenceCache.Contains(localId))
        {
            inferredType = default!;
            return false;
        }

        var visiting = new HashSet<LocalId>();
        if (!TryInferLocalTypeFromDefinitionsCore(localId, visiting, out inferredType))
        {
            _failedLocalTypeInferenceCache.Add(localId);
            inferredType = default!;
            return false;
        }

        _inferredLocalTypeCache[localId] = inferredType;
        return true;
    }

    private bool TryInferLocalTypeFromDefinitionsCore(LocalId localId, HashSet<LocalId> visiting, out LlvmType inferredType)
    {
        inferredType = default!;
        if (_inferredLocalTypeCache.TryGetValue(localId, out var cachedType))
        {
            inferredType = cachedType;
            return true;
        }

        if (_failedLocalTypeInferenceCache.Contains(localId) ||
            !visiting.Add(localId) ||
            !_definitionInstructionsByLocal.TryGetValue(localId, out var definitions))
        {
            return false;
        }

        try
        {
            LlvmType? candidate = null;
            foreach (var instruction in definitions)
            {
                if (!TryInferTypeFromDefinitionInstruction(localId, instruction, visiting, out var instructionType))
                {
                    continue;
                }

                if (candidate == null)
                {
                    candidate = instructionType;
                    continue;
                }

                if (candidate != instructionType)
                {
                    return false;
                }
            }

            if (candidate == null || candidate is LlvmVoidType)
            {
                return false;
            }

            inferredType = candidate;
            _inferredLocalTypeCache[localId] = inferredType;
            return true;
        }
        finally
        {
            visiting.Remove(localId);
        }
    }

    private bool TryInferTypeFromDefinitionInstruction(
        LocalId localId,
        MirInstruction instruction,
        HashSet<LocalId> visiting,
        out LlvmType inferredType)
    {
        switch (instruction)
        {
            case MirAssign { Target: { Kind: PlaceKind.Local } target, Source: var source } when target.Local.Equals(localId):
                return TryInferOperandType(source, visiting, out inferredType);

            case MirStore { Target: { Kind: PlaceKind.Local } target, Value: var value } when target.Local.Equals(localId):
                return TryInferOperandType(value, visiting, out inferredType);

            case MirCopy { Target: var target, Source: var source } when target.Local.Equals(localId):
                return TryInferPlaceType(source, visiting, out inferredType);

            case MirMove { Target: var target, Source: var source } when target.Local.Equals(localId):
                return TryInferPlaceType(source, visiting, out inferredType);

            case MirLoad { Target: { Kind: PlaceKind.Local } target, Source: var source } when target.Local.Equals(localId):
                if (target.TypeId.IsValid)
                {
                    inferredType = LowerStorageTypeIdOrReport(target.TypeId, "infer local load result");
                    return inferredType is not LlvmVoidType;
                }

                return source switch
                {
                    MirPlace sourcePlace => TryInferPlaceType(sourcePlace, visiting, out inferredType),
                    _ => TryInferOperandType(source, visiting, out inferredType)
                };

            case MirCall { Target: { Kind: PlaceKind.Local } target } call when target.Local.Equals(localId):
                return TryInferCallResultType(call, visiting, out inferredType);

            case MirBinOp { Target: MirPlace { Kind: PlaceKind.Local } target } binOp when target.Local.Equals(localId):
                return TryInferBinOpType(binOp, visiting, out inferredType);

            case MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local } target } unaryOp when target.Local.Equals(localId):
                return TryInferUnaryOpType(unaryOp, visiting, out inferredType);

            case MirAlloc { Target: { Kind: PlaceKind.Local } target } when target.Local.Equals(localId):
                inferredType = LlvmPointerType.VoidPtr();
                return true;

            default:
                inferredType = default!;
                return false;
        }
    }

    private bool TryInferOperandType(MirOperand operand, HashSet<LocalId> visiting, out LlvmType inferredType)
    {
        if (operand.TypeId.IsValid)
        {
            inferredType = LowerStorageTypeIdOrReport(operand.TypeId, "infer operand type");
            return inferredType is not LlvmVoidType;
        }

        switch (operand)
        {
            case MirPlace place:
                return TryInferPlaceType(place, visiting, out inferredType);

            case MirFunctionRef functionRef:
                if (TryResolveSourceVisibleSignature(GetFunctionReferenceSignatureTypeId(functionRef), out _))
                {
                    inferredType = LlvmPointerType.VoidPtr();
                    return true;
                }

                if (TryInferFunctionReferenceValueType(functionRef, out inferredType))
                {
                    return true;
                }

                inferredType = default!;
                return false;

            case MirConstant constant:
                return TryInferConstantType(constant, out inferredType);

            case MirTemp temp when temp.TypeId.IsValid:
                inferredType = LowerStorageTypeIdOrReport(temp.TypeId, "infer temp type");
                return inferredType is not LlvmVoidType;

            default:
                inferredType = default!;
                return false;
        }
    }

    private bool TryInferPlaceType(MirPlace place, HashSet<LocalId> visiting, out LlvmType inferredType)
    {
        var resolvedTypeId = ResolvePlaceTypeId(place);
        if (resolvedTypeId.IsValid)
        {
            inferredType = LowerStorageTypeIdOrReport(resolvedTypeId, "infer place type");
            return inferredType is not LlvmVoidType;
        }

        switch (place.Kind)
        {
            case PlaceKind.Local:
                if (_locals.LocalMap.TryGetValue(place.Local, out var localValue))
                {
                    inferredType = localValue.Type;
                    return inferredType is not LlvmVoidType;
                }

                if (_locals.RuntimeWordLocals.Contains(place.Local))
                {
                    inferredType = LlvmIntType.I64;
                    return true;
                }

                if (_inferredLocalTypeCache.TryGetValue(place.Local, out var cachedType))
                {
                    inferredType = cachedType;
                    return true;
                }

                return TryInferLocalTypeFromDefinitionsCore(place.Local, visiting, out inferredType);

            case PlaceKind.Index when place.IndexAccessKind == MirIndexAccessKind.RuntimeArray:
                inferredType = ResolveRuntimeArrayElementType(place);
                return inferredType is not LlvmVoidType;

            default:
                inferredType = LlvmPointerType.VoidPtr();
                return true;
        }
    }

    private bool TryInferCallResultType(MirCall call, HashSet<LocalId> visiting, out LlvmType inferredType)
    {
        if (call.Target is MirPlace target && target.TypeId.IsValid)
        {
            inferredType = LowerStorageTypeIdOrReport(target.TypeId, "infer call result");
            return inferredType is not LlvmVoidType;
        }

        if (call.Function is MirFunctionRef arrayPushRef &&
            IsRuntimeFunctionRef(arrayPushRef, WellKnownStrings.InternalNames.ArrayPush))
        {
            inferredType = LlvmPointerType.VoidPtr();
            return true;
        }

        if (call.Function is MirFunctionRef showRef &&
            call.Arguments.Count == 1 &&
            TryInferBuiltinShowResultType(showRef, call.Arguments[0], out inferredType))
        {
            return true;
        }

        if (call.Function is MirFunctionRef functionRef)
        {
            var functionType = ResolveFunctionType(functionRef);
            if (functionType != null)
            {
                inferredType = functionType.ReturnType;
                return inferredType is not LlvmVoidType;
            }

            if (TypeSemantics.IsAdtConstructorCall(functionRef))
            {
                inferredType = LlvmPointerType.VoidPtr();
                return true;
            }
        }

        if (TryResolveCallableSignature(call.Function, out var callableSignature))
        {
            inferredType = callableSignature.ReturnType;
            return inferredType is not LlvmVoidType;
        }

        if (call.Function is MirPlace functionPlace &&
            TryInferPlaceType(functionPlace, visiting, out var functionValueType) &&
            TryExtractFunctionSignature(functionValueType, out var functionSignature))
        {
            inferredType = functionSignature.ReturnType;
            return inferredType is not LlvmVoidType;
        }

        inferredType = default!;
        return false;
    }

    private bool TryInferBuiltinShowResultType(MirFunctionRef showRef, MirOperand argument, out LlvmType inferredType)
    {
        if (showRef.TraitMethodRole != TraitMethodRole.Show)
        {
            inferredType = default!;
            return false;
        }

        var argumentTypeId = argument.TypeId;
        if (!argumentTypeId.IsValid)
        {
            inferredType = default!;
            return false;
        }

        switch (argumentTypeId.Value)
        {
            case BaseTypes.IntId:
            case BaseTypes.CharId:
            case BaseTypes.BoolId:
                inferredType = LlvmPointerType.VoidPtr();
                return true;
            default:
                inferredType = default!;
                return false;
        }
    }

    private bool TryInferFunctionReferenceValueType(MirFunctionRef functionRef, out LlvmType inferredType)
    {
        if (functionRef.TraitMethodRole == TraitMethodRole.Show)
        {
            inferredType = LlvmPointerType.VoidPtr();
            return true;
        }

        if (TryResolveSourceVisibleSignature(GetFunctionReferenceSignatureTypeId(functionRef), out _))
        {
            inferredType = LlvmPointerType.VoidPtr();
            return true;
        }

        inferredType = default!;
        return false;
    }

    private bool TryInferConstantType(MirConstant constant, out LlvmType inferredType)
    {
        switch (constant.Value)
        {
            case MirConstantValue.BoolValue:
                inferredType = LlvmIntType.I1;
                return true;
            case MirConstantValue.IntValue:
            case MirConstantValue.CharValue:
                inferredType = LlvmIntType.I64;
                return true;
            case MirConstantValue.FloatValue:
                inferredType = LlvmFloatType.Double;
                return true;
            case MirConstantValue.StringValue:
                inferredType = LlvmPointerType.VoidPtr();
                return true;
            case MirConstantValue.UnitValue:
                inferredType = LlvmIntType.I1;
                return true;
            default:
                inferredType = default!;
                return false;
        }
    }

    private bool TryInferBinOpType(MirBinOp binOp, HashSet<LocalId> visiting, out LlvmType inferredType)
    {
        if (binOp.Target.TypeId.IsValid)
        {
            inferredType = LowerStorageTypeIdOrReport(binOp.Target.TypeId, "infer binop result");
            return inferredType is not LlvmVoidType;
        }

        if (binOp.Operator is BinaryOp.Eq or BinaryOp.Ne or BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge or BinaryOp.And or BinaryOp.Or)
        {
            inferredType = LlvmIntType.I1;
            return true;
        }

        if (TryInferOperandType(binOp.Left, visiting, out inferredType))
        {
            return inferredType is not LlvmVoidType;
        }

        inferredType = default!;
        return false;
    }

    private bool TryInferUnaryOpType(MirUnaryOp unaryOp, HashSet<LocalId> visiting, out LlvmType inferredType)
    {
        if (unaryOp.Target.TypeId.IsValid)
        {
            inferredType = LowerStorageTypeIdOrReport(unaryOp.Target.TypeId, "infer unary result");
            return inferredType is not LlvmVoidType;
        }

        return TryInferOperandType(unaryOp.Operand, visiting, out inferredType);
    }

    private bool IsToleratedUnresolvedLocal(LocalId localId)
    {
        return IsGenericLocal(localId) || _partialCallStates.ContainsKey(localId);
    }

    private void ReportUnresolvedValueTypeLeakage(string siteKey, string context)
    {
        var functionName = _currentFunction?.Name ?? _currentMirFunction?.Name ?? "<module>";
        var dedupeKey = $"{functionName}:{siteKey}:{context}";
        if (!_reportedUnresolvedTypeSites.Add(dedupeKey))
        {
            return;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.UnresolvedValueTypeDuringLlvmOperandLowering(context),
                "E5303")
            .WithNote(DiagnosticMessages.OnlyGenericPartialPlaceholdersMayRemainTypeErased)
            .WithNote(DiagnosticMessages.SiteNote(siteKey))
            .WithNote(DiagnosticMessages.FunctionNote(functionName));

        if (_currentMirFunction != null && HasSpan(_currentMirFunction.Span))
        {
            diagnostic.WithLabel(_currentMirFunction.Span, DiagnosticMessages.UnresolvedOperandValueTypeLabel);
        }

        Diagnostics.Add(diagnostic);
    }

    private void ReportUnknownTypeIdLoweringFallback(TypeId typeId, string context)
    {
        var functionName = _currentFunction?.Name ?? _currentMirFunction?.Name ?? "<module>";
        var dedupeKey = $"{functionName}:type:{typeId.Value}:{context}";
        if (!_reportedUnresolvedTypeSites.Add(dedupeKey))
        {
            return;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.UnknownTypeIdOpaquePointerFallback(typeId),
                "E5304")
            .WithNote(DiagnosticMessages.MirExpectedKnownTypeIdNote)
            .WithNote(DiagnosticMessages.ContextNote(context))
            .WithNote(DiagnosticMessages.FunctionNote(functionName));

        if (_currentMirFunction != null && HasSpan(_currentMirFunction.Span))
        {
            diagnostic.WithLabel(_currentMirFunction.Span, DiagnosticMessages.UnknownTypeIdFallbackLabel);
        }

        Diagnostics.Add(diagnostic);
    }

    private void ReportUnsupportedTargetOperandFallback(MirOperand operand, string context)
    {
        var functionName = _currentFunction?.Name ?? _currentMirFunction?.Name ?? "<module>";
        var operandKind = operand.GetType().Name;
        var dedupeKey = $"{functionName}:target:{operandKind}:{context}:{operand.Span.Location.Position}";
        if (!_reportedUnresolvedTypeSites.Add(dedupeKey))
        {
            return;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.UnsupportedTargetOperandFallback(operandKind, context),
                "E5306")
            .WithNote(DiagnosticMessages.ExpectedMirPlaceOrTempTargetBeforeLlvm)
            .WithNote(DiagnosticMessages.FunctionNote(functionName));

        if (HasSpan(operand.Span))
        {
            diagnostic.WithLabel(operand.Span, DiagnosticMessages.UnsupportedTargetOperandLabel);
        }
        else if (_currentMirFunction != null && HasSpan(_currentMirFunction.Span))
        {
            diagnostic.WithLabel(_currentMirFunction.Span, DiagnosticMessages.UnsupportedTargetOperandFallbackLabel);
        }

        Diagnostics.Add(diagnostic);
    }

    private LlvmValue ReportUnsupportedPlaceKindFallback(MirPlace place, string context)
    {
        var functionName = _currentFunction?.Name ?? _currentMirFunction?.Name ?? "<module>";
        var kindValue = (int)place.Kind;
        var dedupeKey = $"{functionName}:place-kind:{kindValue}:{context}:{place.Span.Location.Position}";
        if (_reportedUnresolvedTypeSites.Add(dedupeKey))
        {
            var diagnostic = Diagnostic.Diagnostic.Error(
                    DiagnosticMessages.UnsupportedPlaceKindFallback(kindValue, context),
                    "E5307")
                .WithNote(DiagnosticMessages.ExpectedLocalDerefFieldOrIndexPlaceBeforeLlvm)
                .WithNote(DiagnosticMessages.FunctionNote(functionName));

            if (HasSpan(place.Span))
            {
                diagnostic.WithLabel(place.Span, DiagnosticMessages.UnsupportedPlaceKindLabel);
            }
            else if (_currentMirFunction != null && HasSpan(_currentMirFunction.Span))
            {
                diagnostic.WithLabel(_currentMirFunction.Span, DiagnosticMessages.UnsupportedPlaceKindFallbackLabel);
            }

            Diagnostics.Add(diagnostic);
        }

        return LlvmConstant.Zero;
    }

    private static IReadOnlyList<MirBasicBlock> GetBlockLoweringOrder(MirFunc func)
    {
        if (func.BasicBlocks.Count <= 1)
        {
            return func.BasicBlocks;
        }

        var byId = func.BasicBlocks.ToDictionary(block => block.Id);
        var order = new List<MirBasicBlock>(func.BasicBlocks.Count);
        var visited = new HashSet<BlockId>();

        void Visit(BlockId blockId)
        {
            if (!visited.Add(blockId))
            {
                return;
            }

            if (!byId.TryGetValue(blockId, out var block))
            {
                return;
            }

            order.Add(block);
            foreach (var successor in GetSuccessors(block.Terminator))
            {
                Visit(successor);
            }
        }

        Visit(func.EntryBlockId);

        foreach (var block in func.BasicBlocks)
        {
            if (visited.Add(block.Id))
            {
                order.Add(block);
            }
        }

        return order;
    }

    private static IEnumerable<BlockId> GetSuccessors(MirTerminator? terminator)
    {
        switch (terminator)
        {
            case MirGoto jump:
                yield return jump.Target;
                yield break;

            case MirSwitch sw:
                foreach (var branch in sw.Branches)
                {
                    yield return branch.Target;
                }

                if (sw.DefaultTarget.HasValue)
                {
                    yield return sw.DefaultTarget.Value;
                }

                yield break;

            default:
                yield break;
        }
    }
}
