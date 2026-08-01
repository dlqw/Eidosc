using Eidosc.Diagnostic;
using Eidosc.Mir;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    private LlvmCall? ConvertRecordUpdateCall(MirCall call, MirFunctionRef constructor)
    {
        if (!TryGetRequiredLocalCallTargetPlace(call, "record update", out var targetPlace) ||
            call.RecordUpdate is not { } update ||
            call.Arguments.Count != update.UpdatedFieldIndices.Count + 1)
        {
            return null;
        }

        var fieldTypeIds = ResolveConstructorFieldTypeIds(targetPlace.TypeId, constructor.Name);
        if (fieldTypeIds.Count == 0 ||
            update.UpdatedFieldIndices.Any(index => index < 0 || index >= fieldTypeIds.Count))
        {
            return null;
        }

        if (_typeLowering.IsInlineValueRecordType(targetPlace.TypeId) &&
            _typeLowering.TryGetStructType(targetPlace.TypeId, out var inlineRecordType))
        {
            LlvmValue aggregate = ConvertOperand(update.Source);
            for (var updateIndex = 0; updateIndex < update.UpdatedFieldIndices.Count; updateIndex++)
            {
                var fieldIndex = update.UpdatedFieldIndices[updateIndex];
                var argument = call.Arguments[updateIndex + 1];
                var fieldValue = ConvertConstructorFieldValue(
                    argument,
                    fieldTypeIds[fieldIndex],
                    inlineRecordType.Fields[fieldIndex],
                    fieldIndex);
                var insert = new LlvmInsertValue
                {
                    Aggregate = aggregate,
                    Element = fieldValue,
                    Indices = [fieldIndex],
                    ResultName = _nameMangler.NewTempName($"record_update_field{fieldIndex}")
                };
                _currentBlock!.Instructions.Add(insert);
                aggregate = new LlvmInstructionRef
                {
                    Instruction = insert,
                    Type = inlineRecordType
                };
            }

            AssignPlaceFromValue(targetPlace, aggregate);
            return null;
        }

        _typeLowering.TryGetStructType(targetPlace.TypeId, out var structType);
        var payloadSize = structType != null
            ? structType.Fields.Sum(field => AlignConstructorPayloadSize(GetLlvmStorageSize(field)))
            : fieldTypeIds.Sum(fieldTypeId => AlignConstructorPayloadSize(
                GetLlvmStorageSize(LowerStorageTypeIdOrReport(fieldTypeId, "record update payload"))));
        payloadSize = Math.Max(8L, payloadSize);

        var source = CoerceToPointer(ConvertOperand(update.Source));
        LlvmValue recordPointer = source;
        if (!update.IsKnownUnique)
        {
            var cowCall = new LlvmCall
            {
                Function = CreateRuntimeFunctionGlobal(
                    WellKnownStrings.Runtime.RecordUpdateCow,
                    LlvmPointerType.VoidPtr(),
                    [LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I32]),
                Arguments =
                [
                    source,
                    new LlvmConstant { Value = payloadSize, Type = LlvmIntType.I64 },
                    new LlvmConstant
                    {
                        Value = ComputeRuntimeConstructorTypeId(constructor),
                        Type = LlvmIntType.I32
                    }
                ],
                ReturnType = LlvmPointerType.VoidPtr(),
                ResultName = _nameMangler.NewTempName("record_update")
            };
            _currentBlock!.Instructions.Add(cowCall);
            recordPointer = new LlvmInstructionRef
            {
                Instruction = cowCall,
                Type = LlvmPointerType.VoidPtr()
            };
        }
        var hasTag = HasTagFieldForType(targetPlace.TypeId);

        for (var updateIndex = 0; updateIndex < update.UpdatedFieldIndices.Count; updateIndex++)
        {
            var fieldIndex = update.UpdatedFieldIndices[updateIndex];
            var argument = call.Arguments[updateIndex + 1];
            var fieldTypeId = fieldTypeIds[fieldIndex];
            var storageType = GetConstructorFieldStorageType(argument, fieldTypeId);
            LlvmGetElementPtr fieldPointer;
            if (structType != null)
            {
                fieldPointer = new LlvmGetElementPtr
                {
                    Pointer = recordPointer,
                    StructType = structType,
                    StructFieldIndex = ComputeStructFieldIndex(hasTag, fieldIndex),
                    ResultName = _nameMangler.NewTempName($"record_field{fieldIndex}_ptr")
                };
            }
            else
            {
                long offset = 0;
                for (var index = 0; index < fieldIndex; index++)
                {
                    offset += AlignConstructorPayloadSize(GetLlvmStorageSize(
                        LowerStorageTypeIdOrReport(fieldTypeIds[index], "record update field offset")));
                }

                fieldPointer = new LlvmGetElementPtr
                {
                    Pointer = recordPointer,
                    ElementType = LlvmIntType.I8,
                    Index = new LlvmConstant { Value = offset, Type = LlvmIntType.I64 },
                    ResultName = _nameMangler.NewTempName($"record_field{fieldIndex}_ptr")
                };
            }

            _currentBlock!.Instructions.Add(fieldPointer);
            var fieldPointerRef = new LlvmInstructionRef
            {
                Instruction = fieldPointer,
                Type = LlvmPointerType.VoidPtr()
            };
            EmitReleaseManagedPayloadFromPointer(
                _currentBlock,
                fieldPointerRef,
                fieldTypeId,
                storageType,
                $"record_field{fieldIndex}_old");

            var fieldValue = ConvertConstructorFieldValue(
                argument,
                fieldTypeId,
                storageType,
                fieldIndex);
            RetainBorrowedProjectionConstructorField(argument, fieldValue, retainBorrowedProjectionFields: true);
            _currentBlock.Instructions.Add(new LlvmStore
            {
                Value = fieldValue,
                Pointer = fieldPointerRef
            });
        }

        AssignPlaceFromValue(targetPlace, recordPointer);
        return null;
    }

    /// <summary>
    /// 处理带 reuse 提示的构造器调用：绕过构造器 stub，
    /// 直接在调用点内联 eidos_alloc_reuse + 字段存储。
    /// </summary>
    private LlvmCall? ConvertConstructorCallWithReuse(
        MirCall call,
        MirFunctionRef ctorRef,
        int reuseSlot)
    {
        if (!TryGetRequiredLocalCallTargetPlace(call, "constructor reuse allocation", out var targetPlace))
        {
            return null;
        }

        var reuseSlotPtr = GetOrCreateReuseSlotAlloca(reuseSlot);
        var fieldTypeIds = ResolveConstructorFieldTypeIds(targetPlace.TypeId, ctorRef.Name);
        var payloadSize = ComputeConstructorPayloadSize(call.Arguments, fieldTypeIds);
        var typeId = ComputeRuntimeConstructorTypeId(ctorRef);

        var targetTypeId = targetPlace.TypeId;

        // Try to get struct type for structured GEP
        _typeLowering.TryGetStructType(targetTypeId, out var structType);

        // call i8* eidos_alloc_reuse(%reuse_slot, payload_size, type_id)
        var allocCall = new LlvmCall
        {
            Function = new LlvmGlobal
            {
                Name = WellKnownStrings.Runtime.AllocReuse,
                Type = new LlvmFunctionType
                {
                    ReturnType = LlvmPointerType.VoidPtr(),
                    ParameterTypes =
                    [
                        LlvmPointerType.VoidPtr(),
                        LlvmIntType.I64,
                        LlvmIntType.I32
                    ]
                }
            },
            Arguments =
            [
                reuseSlotPtr,
                new LlvmConstant { Value = payloadSize, Type = LlvmIntType.I64 },
                new LlvmConstant { Value = typeId, Type = LlvmIntType.I32 }
            ],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("reuse_alloc")
        };

        _currentBlock!.Instructions.Add(allocCall);
        var allocResult = new LlvmInstructionRef
        {
            Instruction = allocCall,
            Type = LlvmPointerType.VoidPtr()
        };

        // 存储字段
        var reuseHasTag = HasTagFieldForType(targetTypeId);
        EmitInlineConstructorFieldStores(
            allocResult,
            call.Arguments,
            fieldTypeIds,
            structType,
            reuseHasTag,
            retainBorrowedProjectionFields: true);

        // 将结果赋值给目标 local
        AssignPlaceFromValue(targetPlace, allocResult);

        // 返回 null 因为指令已直接添加到 currentBlock
        return null;
    }

    /// <summary>
    /// 处理带 StackPromotion 提示的构造器调用：用 alloca 替代 eidos_alloc，
    /// 跳过 EidosHeader，直接在栈上分配 payload 空间。
    /// </summary>
    private LlvmCall? ConvertConstructorCallWithStackPromo(
        MirCall call,
        string ctorName)
    {
        if (!TryGetRequiredLocalCallTargetPlace(call, "constructor stack promotion", out var targetPlace))
        {
            return null;
        }

        var targetLocal = targetPlace.Local;
        if (!_currentStackPromotionHints!.StackAllocInfoByLocal.TryGetValue(
                targetLocal, out var allocInfo))
        {
            return null;
        }

        // Try to get struct type for structured GEP
        _typeLowering.TryGetStructType(targetPlace.TypeId, out var structType);
        var fieldTypeIds = ResolveConstructorFieldTypeIds(targetPlace.TypeId, ctorName);

        // alloca — use struct type when available, otherwise [i8 x payloadSize]
        LlvmInstructionRef rawPtrValue;
        if (structType != null)
        {
            var alloca = new LlvmAlloca
            {
                AllocatedType = structType,
                ResultName = _nameMangler.NewTempName($"l{targetLocal.Value}_stack")
            };
            EmitAllocaInEntryBlock(alloca);

            var allocaResult = new LlvmInstructionRef
            {
                Instruction = alloca,
                Type = new LlvmPointerType { ElementType = structType }
            };

            // bitcast %struct.* to ptr for uniform field storage
            var rawPtr = new LlvmCast
            {
                Op = WellKnownStrings.InternalNames.Bitcast,
                Value = allocaResult,
                TargetType = LlvmPointerType.VoidPtr(),
                ResultName = _nameMangler.NewTempName($"l{targetLocal.Value}_stack_ptr")
            };
            _currentBlock!.Instructions.Add(rawPtr);
            rawPtrValue = new LlvmInstructionRef
            {
                Instruction = rawPtr,
                Type = LlvmPointerType.VoidPtr()
            };
        }
        else
        {
            var payloadArray = new LlvmArrayType
            {
                Element = LlvmIntType.I8,
                Size = (int)allocInfo.PayloadSize
            };
            var alloca = new LlvmAlloca
            {
                AllocatedType = payloadArray,
                ResultName = _nameMangler.NewTempName($"l{targetLocal.Value}_stack")
            };
            EmitAllocaInEntryBlock(alloca);

            var allocaResult = new LlvmInstructionRef
            {
                Instruction = alloca,
                Type = new LlvmPointerType { ElementType = payloadArray }
            };

            var rawPtr = new LlvmCast
            {
                Op = WellKnownStrings.InternalNames.Bitcast,
                Value = allocaResult,
                TargetType = LlvmPointerType.VoidPtr(),
                ResultName = _nameMangler.NewTempName($"l{targetLocal.Value}_stack_ptr")
            };
            _currentBlock!.Instructions.Add(rawPtr);
            rawPtrValue = new LlvmInstructionRef
            {
                Instruction = rawPtr,
                Type = LlvmPointerType.VoidPtr()
            };
        }

        // 存储字段
        var promoHasTag = HasTagFieldForType(targetPlace.TypeId);
        EmitInlineConstructorFieldStores(
            rawPtrValue,
            call.Arguments,
            fieldTypeIds,
            structType,
            promoHasTag,
            retainBorrowedProjectionFields: false);

        // 将结果赋值给目标 local
        AssignPlaceFromValue(targetPlace, rawPtrValue);

        return null;
    }

    /// <summary>
    /// 在构造器分配后内联存储各字段。
    /// 支持 struct-typed GEP（当 structType 非 null 时）和 byte-offset GEP（回退）。
    /// </summary>
    private void EmitInlineConstructorFieldStores(
        LlvmValue basePointer,
        IReadOnlyList<MirOperand> arguments,
        IReadOnlyList<TypeId> fieldTypeIds,
        LlvmStructType? structType,
        bool hasTagField,
        bool retainBorrowedProjectionFields)
    {
        if (structType == null)
        {
            EmitConstructorFieldStoresByType(
                basePointer,
                arguments,
                fieldTypeIds,
                retainBorrowedProjectionFields);
            return;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            var expectedTypeId = GetExpectedConstructorFieldTypeId(arguments[index], fieldTypeIds, index);
            var storageType = GetConstructorFieldStorageType(arguments[index], expectedTypeId);
            var argValue = ConvertConstructorFieldValue(arguments[index], expectedTypeId, storageType, index);
            RetainBorrowedProjectionConstructorField(arguments[index], argValue, retainBorrowedProjectionFields);
            var fieldIndex = structType != null ? ComputeStructFieldIndex(hasTagField, index) : index;

            LlvmGetElementPtr fieldPtr;
            if (structType != null)
            {
                fieldPtr = new LlvmGetElementPtr
                {
                    Pointer = basePointer,
                    StructType = structType,
                    StructFieldIndex = fieldIndex,
                    ResultName = _nameMangler.NewTempName($"field{index}_ptr")
                };
            }
            else
            {
                fieldPtr = new LlvmGetElementPtr
                {
                    Pointer = basePointer,
                    ElementType = LlvmIntType.I8,
                    Index = new LlvmConstant
                    {
                        Value = (long)index * 8L,
                        Type = LlvmIntType.I64
                    },
                    ResultName = _nameMangler.NewTempName($"field{index}_ptr")
                };
            }

            _currentBlock!.Instructions.Add(fieldPtr);
            _currentBlock!.Instructions.Add(new LlvmStore
            {
                Value = argValue,
                Pointer = new LlvmInstructionRef
                {
                    Instruction = fieldPtr,
                    Type = LlvmPointerType.VoidPtr()
                }
            });
        }
    }

    private void EmitConstructorFieldStoresByType(
        LlvmValue basePointer,
        IReadOnlyList<MirOperand> arguments,
        IReadOnlyList<TypeId> fieldTypeIds,
        bool retainBorrowedProjectionFields)
    {
        long offset = 0;
        for (var index = 0; index < arguments.Count; index++)
        {
            var expectedTypeId = GetExpectedConstructorFieldTypeId(arguments[index], fieldTypeIds, index);
            var storageType = GetConstructorFieldStorageType(arguments[index], expectedTypeId);
            var fieldPtr = new LlvmGetElementPtr
            {
                Pointer = basePointer,
                ElementType = LlvmIntType.I8,
                Index = new LlvmConstant
                {
                    Value = offset,
                    Type = LlvmIntType.I64
                },
                ResultName = _nameMangler.NewTempName($"field{index}_ptr")
            };
            _currentBlock!.Instructions.Add(fieldPtr);

            var fieldValue = ConvertConstructorFieldValue(arguments[index], expectedTypeId, storageType, index);
            RetainBorrowedProjectionConstructorField(arguments[index], fieldValue, retainBorrowedProjectionFields);
            _currentBlock!.Instructions.Add(new LlvmStore
            {
                Value = fieldValue,
                Pointer = new LlvmInstructionRef
                {
                    Instruction = fieldPtr,
                    Type = LlvmPointerType.VoidPtr()
                }
            });

            offset += AlignConstructorPayloadSize(GetLlvmStorageSize(storageType));
        }
    }

    private long ComputeConstructorPayloadSize(
        IReadOnlyList<MirOperand> arguments,
        IReadOnlyList<TypeId> fieldTypeIds)
    {
        long payloadSize = 0;
        for (var index = 0; index < arguments.Count; index++)
        {
            var expectedTypeId = GetExpectedConstructorFieldTypeId(arguments[index], fieldTypeIds, index);
            payloadSize += AlignConstructorPayloadSize(
                GetLlvmStorageSize(GetConstructorFieldStorageType(arguments[index], expectedTypeId)));
        }

        return Math.Max(8L, payloadSize);
    }

    private LlvmType GetConstructorFieldStorageType(MirOperand argument, TypeId expectedTypeId = default)
    {
        var storageTypeId = expectedTypeId.IsValid ? expectedTypeId : argument.TypeId;
        return storageTypeId.IsValid
            ? LowerStorageTypeIdOrReport(storageTypeId, "constructor field storage")
            : LlvmPointerType.VoidPtr();
    }

    private IReadOnlyList<TypeId> ResolveConstructorFieldTypeIds(TypeId targetTypeId, string constructorName)
    {
        if (_typeLowering.TryGetConstructorLayouts(targetTypeId, out var layouts) &&
            layouts.FirstOrDefault(layout =>
                string.Equals(layout.ConstructorName, constructorName, StringComparison.Ordinal)) is { } layout)
        {
            return layout.FieldTypeIds;
        }

        return [];
    }

    private static TypeId GetExpectedConstructorFieldTypeId(
        MirOperand argument,
        IReadOnlyList<TypeId> fieldTypeIds,
        int index) =>
        index < fieldTypeIds.Count && fieldTypeIds[index].IsValid
            ? fieldTypeIds[index]
            : argument.TypeId;

    private LlvmValue ConvertConstructorFieldValue(
        MirOperand argument,
        TypeId expectedTypeId,
        LlvmType storageType,
        int index)
    {
        var rawValue = ConvertOperand(argument);
        var injectedValue = MaterializeScalarTagForNonScalarTarget(
            rawValue,
            argument.TypeId,
            expectedTypeId,
            $"ctor_field{index}_inject");
        return CoerceValueToType(injectedValue, storageType, $"ctor_field{index}");
    }

    private static long AlignConstructorPayloadSize(long size)
    {
        if (size <= 0)
        {
            return 0;
        }

        const long alignment = 8L;
        return ((size + alignment - 1) / alignment) * alignment;
    }

    private static long GetLlvmStorageSize(LlvmType type)
    {
        return type switch
        {
            LlvmVoidType => 0,
            LlvmIntType intType => Math.Max(1L, (intType.Bits + 7L) / 8L),
            LlvmFloatType floatType => Math.Max(1L, (floatType.Bits + 7L) / 8L),
            LlvmPointerType => IntPtr.Size,
            LlvmArrayType arrayType => checked(GetLlvmStorageSize(arrayType.Element) * arrayType.Size),
            LlvmStructType structType => structType.Fields.Sum(field => AlignConstructorPayloadSize(GetLlvmStorageSize(field))),
            _ => IntPtr.Size
        };
    }

    /// <summary>
    /// 转换 @cstruct 字段访问器调用。
    /// Getter: GEP(offset) + load(field_type)
    /// Setter: GEP(offset) + store(field_type)
    /// </summary>
    private LlvmCall? ConvertCStructAccessor(MirCall call, CStructAccessorInfo info)
    {
        if (!TryGetRequiredCallTargetPlace(
                call,
                info.IsGetter ? "cstruct field getter" : "cstruct field setter",
                out var targetPlace))
        {
            return null;
        }

        var basePtr = CoerceToPointer(ConvertOperand(call.Arguments[0]));
        var fieldTypeId = new TypeId(info.FieldTypeId);
        var fieldStorageType = LowerStorageTypeIdOrReport(fieldTypeId, "cstruct field");
        var offset = info.FieldOffset;

        // 计算字段地址: getelementptr i8, ptr %base, i64 offset
        var fieldPtr = new LlvmGetElementPtr
        {
            ResultName = _nameMangler.NewTempName("csfield"),
            Pointer = basePtr,
            Index = new LlvmConstant { Value = offset, Type = LlvmIntType.I64 },
            ElementType = LlvmIntType.I8
        };
        _currentBlock!.Instructions.Add(fieldPtr);

        var fieldPtrRef = new LlvmInstructionRef
        {
            Instruction = fieldPtr,
            Type = LlvmPointerType.VoidPtr()
        };

        if (info.IsGetter)
        {
            // Getter: 从字段地址加载值
            var load = new LlvmLoad
            {
                ResultName = targetPlace.Kind == PlaceKind.Local
                    ? _nameMangler.NewTempName($"l{targetPlace.Local.Value}")
                    : _nameMangler.NewTempName("csload"),
                LoadType = fieldStorageType,
                Pointer = fieldPtrRef
            };
            _currentBlock!.Instructions.Add(load);

            var loadedValue = new LlvmInstructionRef
            {
                Instruction = load,
                Type = fieldStorageType
            };

            AssignPlaceFromValue(targetPlace, loadedValue);
        }
        else
        {
            // Setter: 将值写入字段地址
            var argValue = ConvertOperand(call.Arguments[1]);
            var convertedValue = CoerceValueToType(argValue, fieldStorageType, "cstruct_store");

            _currentBlock!.Instructions.Add(new LlvmStore
            {
                Value = convertedValue,
                Pointer = fieldPtrRef
            });

            AssignPlaceFromValue(targetPlace, LlvmVoid.Instance);
        }

        return null;
    }

    private bool TryGetRequiredCallTargetPlace(MirCall call, string context, out MirPlace target)
    {
        if (call.Target is MirPlace place)
        {
            target = place;
            return true;
        }

        ReportMissingCallTargetPlace(call, context);
        target = null!;
        return false;
    }

    private bool TryGetRequiredLocalCallTargetPlace(MirCall call, string context, out MirPlace target)
    {
        if (!TryGetRequiredCallTargetPlace(call, context, out target))
        {
            return false;
        }

        if (target.Kind == PlaceKind.Local)
        {
            return true;
        }

        ReportUnsupportedCallTargetPlaceKind(target, context);
        target = null!;
        return false;
    }

    private void ReportMissingCallTargetPlace(MirCall call, string context)
    {
        var functionName = _currentFunction?.Name ?? _currentMirFunction?.Name ?? "<module>";
        var dedupeKey = $"{functionName}:target:missing:{context}:{call.Span.Location.Position}";
        if (!_reportedUnresolvedTypeSites.Add(dedupeKey))
        {
            return;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.MissingMirTargetPlaceForContext(context),
                "E5306")
            .WithNote(DiagnosticMessages.ExpectedMirPlaceTargetBeforeLlvm)
            .WithNote(DiagnosticMessages.FunctionNote(functionName));

        if (HasSpan(call.Span))
        {
            diagnostic.WithLabel(call.Span, DiagnosticMessages.MissingTargetPlaceLabel);
        }
        else if (_currentMirFunction != null && HasSpan(_currentMirFunction.Span))
        {
            diagnostic.WithLabel(_currentMirFunction.Span, DiagnosticMessages.MissingTargetPlaceLabel);
        }

        Diagnostics.Add(diagnostic);
    }

    private void ReportUnsupportedCallTargetPlaceKind(MirPlace target, string context)
    {
        var functionName = _currentFunction?.Name ?? _currentMirFunction?.Name ?? "<module>";
        var kindValue = (int)target.Kind;
        var dedupeKey = $"{functionName}:target-place-kind:{kindValue}:{context}:{target.Span.Location.Position}";
        if (!_reportedUnresolvedTypeSites.Add(dedupeKey))
        {
            return;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(
                DiagnosticMessages.UnsupportedMirTargetPlaceKind(kindValue, context),
                "E5306")
            .WithNote(DiagnosticMessages.ExpectedLocalMirPlaceTargetBeforeTargetLowering)
            .WithNote(DiagnosticMessages.FunctionNote(functionName));

        if (HasSpan(target.Span))
        {
            diagnostic.WithLabel(target.Span, DiagnosticMessages.UnsupportedTargetPlaceKindLabel);
        }
        else if (_currentMirFunction != null && HasSpan(_currentMirFunction.Span))
        {
            diagnostic.WithLabel(_currentMirFunction.Span, DiagnosticMessages.UnsupportedTargetPlaceKindLabel);
        }

        Diagnostics.Add(diagnostic);
    }
}
