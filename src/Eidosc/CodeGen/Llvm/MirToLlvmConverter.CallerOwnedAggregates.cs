using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    private bool TryConvertCallerOwnedOutCall(MirCall call, out LlvmCall? lowered)
    {
        lowered = null;
        if (call.Function is not MirFunctionRef functionRef ||
            call.Target is not MirPlace { Kind: PlaceKind.Local } target ||
            !TryResolveMirFunction(functionRef, out var callee) ||
            !callee.CallerOwnedAggregateAbi.HasOutReturn)
        {
            return false;
        }

        var destination = GetCallerOwnedDestination(target);
        if (destination == null)
        {
            return false;
        }

        var arguments = ConvertArgumentsForKnownDirectCall(call);
        var argumentsWithExpectedFunctionTypes = RewriteFunctionValueArgumentsForDirectCall(call, arguments);
        argumentsWithExpectedFunctionTypes.Add(destination);
        foreach (var storage in callee.CallerOwnedAggregateAbi.OutArrayStorages)
        {
            var arrayStorage = GetCallerOwnedArrayStorage(target, storage.Key);
            if (arrayStorage == null)
            {
                return false;
            }

            argumentsWithExpectedFunctionTypes.Add(arrayStorage);
        }
        var functionValue = ResolveCallTargetValue(call, argumentsWithExpectedFunctionTypes, out _);
        var coercedArguments = CoerceCallArguments(functionValue, argumentsWithExpectedFunctionTypes);
        lowered = EmitDirectCall(
            call with { Target = null, IsTailCall = false },
            functionValue,
            coercedArguments,
            LlvmVoidType.Instance);

        BindCallerOwnedGroupLocals(target.Local, destination);
        return true;
    }

    /// <summary>
    /// Records which aggregate field each caller-owned array storage is stored
    /// into by the callee, so promoted array operations in other variants can
    /// resolve a field projection back to its storage metadata. The mapping is
    /// recovered from the out variant's constructor call arguments, following
    /// move chains from the argument locals back to the allocation locals.
    /// </summary>
    private void RecordCallerOwnedStorageFieldIndexes(MirFunc callee)
    {
        if (!callee.CallerOwnedAggregateAbi.HasOutReturn)
        {
            return;
        }

        var storageByRootLocal = new Dictionary<LocalId, MirCallerOwnedArrayStorage>();
        foreach (var storage in callee.CallerOwnedAggregateAbi.OutArrayStorages)
        {
            storageByRootLocal.TryAdd(storage.ArrayLocal, storage);
        }

        if (storageByRootLocal.Count == 0)
        {
            return;
        }

        var moveSources = new Dictionary<LocalId, LocalId>();
        foreach (var block in callee.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is MirMove
                    {
                        Target: { Kind: PlaceKind.Local, Local: var target },
                        Source: { Kind: PlaceKind.Local, Local: var source }
                    })
                {
                    moveSources.TryAdd(target, source);
                }
            }
        }

        foreach (var block in callee.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is not MirCall
                    {
                        Target: MirPlace { Kind: PlaceKind.Local, Local: var targetLocal },
                        Function: MirFunctionRef constructor
                    } call ||
                    !callee.CallerOwnedAggregateAbi.OutReturnLocals.Contains(targetLocal) ||
                    !TypeSemantics.IsAdtConstructorCall(constructor) ||
                    !_typeLowering.TryGetConstructorLayouts(call.Target.TypeId, out var layouts) ||
                    layouts.Count != 1)
                {
                    continue;
                }

                var fields = layouts[0].FieldTypeIds;
                for (var index = 0; index < call.Arguments.Count && index < fields.Count; index++)
                {
                    if (call.Arguments[index] is not MirPlace { Kind: PlaceKind.Local, Local: var argumentLocal })
                    {
                        continue;
                    }

                    var rootLocal = argumentLocal;
                    while (moveSources.TryGetValue(rootLocal, out var source))
                    {
                        rootLocal = source;
                    }

                    if (storageByRootLocal.TryGetValue(rootLocal, out var storage) &&
                        !_callerOwnedStorageFieldIndexByKey.ContainsKey(storage.Key))
                    {
                        _callerOwnedStorageFieldIndexByKey[storage.Key] = index;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Lowers `array_length` on a promoted caller-owned array to a direct load
    /// of the length header field, eliminating the runtime dispatch. Returns
    /// false when the operand is not a promotable storage, so the generic
    /// runtime call path is used instead.
    /// </summary>
    /// <remarks>
    /// The length is read through the aggregate's live array pointer (like
    /// eidos_array_length) instead of the inline storage slot: the slot header
    /// is stale once the array has outgrown the inline capacity and moved to
    /// the heap, while the payload pointer always names the current array.
    /// </remarks>
    private bool TryConvertPromotedArrayLengthCall(MirCall call)
    {
        if (call.Arguments.Count < 1 ||
            call.Arguments[0] is not MirPlace arrayPlace ||
            !TryResolvePromotedArrayStorage(arrayPlace, out _, out _))
        {
            return false;
        }

        // ConvertPlace yields the array value for a local but only the field
        // address for a field/index projection; load the live pointer there.
        var arrayPointer = ConvertPlace(arrayPlace);
        if (arrayPlace.Kind != PlaceKind.Local)
        {
            var pointerLoad = new LlvmLoad
            {
                Pointer = arrayPointer,
                LoadType = LlvmPointerType.VoidPtr(),
                ResultName = _nameMangler.NewTempName("promoted_array_ptr")
            };
            _currentBlock!.Instructions.Add(pointerLoad);
            arrayPointer = new LlvmInstructionRef { Instruction = pointerLoad, Type = LlvmPointerType.VoidPtr() };
        }

        // Replicate eidos_array_length's NULL -> 0 semantics without a branch:
        // read from a known-valid base when the pointer is null, then discard.
        var isNull = new LlvmIcmp
        {
            Predicate = "eq",
            Left = arrayPointer,
            Right = LlvmNullPointer.Instance,
            ResultName = _nameMangler.NewTempName("promoted_array_null")
        };
        _currentBlock!.Instructions.Add(isNull);
        var isNullRef = new LlvmInstructionRef { Instruction = isNull, Type = LlvmIntType.I1 };
        var fallbackBase = ResolvePromotedFallbackBase(arrayPlace);
        var guardedPointer = new LlvmSelect
        {
            Condition = isNullRef,
            TrueValue = fallbackBase,
            FalseValue = arrayPointer,
            ResultName = _nameMangler.NewTempName("promoted_array_guarded")
        };
        _currentBlock.Instructions.Add(guardedPointer);
        var guardedPointerRef = new LlvmInstructionRef { Instruction = guardedPointer, Type = LlvmPointerType.VoidPtr() };

        var length = EmitRuntimeArrayHeaderLoad(guardedPointerRef, 8, "promoted_length");
        var guardedLength = new LlvmSelect
        {
            Condition = isNullRef,
            TrueValue = new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 },
            FalseValue = length,
            ResultName = _nameMangler.NewTempName("promoted_length_guarded")
        };
        _currentBlock.Instructions.Add(guardedLength);
        if (call.Target is MirPlace target)
        {
            AssignPlaceFromValue(target, new LlvmInstructionRef { Instruction = guardedLength, Type = LlvmIntType.I64 });
        }

        return true;
    }

    /// <summary>
    /// A pointer that is always valid within the current frame, used as the
    /// load base for the NULL-array fallback of the promoted length read.
    /// </summary>
    private LlvmValue ResolvePromotedFallbackBase(MirPlace arrayPlace)
    {
        if (arrayPlace.Kind == PlaceKind.Local &&
            _callerOwnedOutArrayStorageByLocal.TryGetValue(arrayPlace.Local, out var outStorage))
        {
            return outStorage.Pointer;
        }

        if (arrayPlace.Kind != PlaceKind.Local &&
            arrayPlace.Base is { Kind: PlaceKind.Local } basePlace)
        {
            var groupLocal = ResolveGroupAliasLocal(basePlace.Local);
            if (groupLocal.IsValid &&
                _callerOwnedGroupByLocal.TryGetValue(groupLocal, out var group) &&
                group.ArrayStorages.FirstOrDefault() is { } first &&
                GetCallerOwnedArrayStorage(
                    new MirPlace { Kind = PlaceKind.Local, Local = groupLocal },
                    first.Key) is { } slotPointer)
            {
                return slotPointer;
            }
        }

        return new LlvmConstant { Value = 0L, Type = LlvmPointerType.VoidPtr() };
    }

    /// <summary>
    /// Resolves an array operand to a promotable caller-owned storage: either
    /// the out-variant's own allocation local or a field projection of an
    /// aggregate group local whose storage field index was recorded from the
    /// constructing variant.
    /// </summary>
    private bool TryResolvePromotedArrayStorage(
        MirPlace arrayPlace,
        out MirCallerOwnedArrayStorage storage,
        out LlvmValue blobBase)
    {
        storage = null!;
        blobBase = null!;

        if (arrayPlace.Kind == PlaceKind.Local &&
            _callerOwnedOutArrayStorageByLocal.TryGetValue(arrayPlace.Local, out var outStorage))
        {
            if (!outStorage.Storage.PromoteInline)
            {
                return false;
            }

            storage = outStorage.Storage;
            blobBase = outStorage.Pointer;
            return true;
        }

        var fieldOrdinal = arrayPlace.Kind switch
        {
            PlaceKind.Field when TryParseAggregateFieldOrdinal(arrayPlace.FieldName, out var ordinal) => ordinal,
            PlaceKind.Index when arrayPlace.Index is MirConstant { Value: MirConstantValue.IntValue { Value: var indexValue } } &&
                               indexValue >= 0 &&
                               indexValue <= int.MaxValue => (int)indexValue,
            _ => -1
        };
        if (fieldOrdinal < 0 ||
            arrayPlace.Base is not { Kind: PlaceKind.Local } basePlace)
        {
            return false;
        }

        var groupLocal = ResolveGroupAliasLocal(basePlace.Local);
        if (!groupLocal.IsValid ||
            !_callerOwnedGroupByLocal.TryGetValue(groupLocal, out var group))
        {
            return false;
        }

        foreach (var candidate in group.ArrayStorages)
        {
            if (!candidate.PromoteInline ||
                !_callerOwnedStorageFieldIndexByKey.TryGetValue(candidate.Key, out var candidateField) ||
                candidateField != fieldOrdinal)
            {
                continue;
            }

            if (GetCallerOwnedArrayStorage(
                    new MirPlace { Kind = PlaceKind.Local, Local = groupLocal },
                    candidate.Key) is not { } slotPointer)
            {
                return false;
            }

            storage = candidate;
            blobBase = slotPointer;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a local to the caller-owned group root by walking move/copy
    /// chains in the current function.
    /// </summary>
    private LocalId ResolveGroupAliasLocal(LocalId local)
    {
        if (_callerOwnedGroupByLocal.ContainsKey(local))
        {
            return local;
        }

        if (_currentMirFunction == null)
        {
            return LocalId.None;
        }

        var current = local;
        var visited = new HashSet<LocalId>();
        while (visited.Add(current))
        {
            LocalId source = LocalId.None;
            foreach (var block in _currentMirFunction.BasicBlocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is MirMove
                        {
                            Target: { Kind: PlaceKind.Local, Local: var moveTarget },
                            Source: { Kind: PlaceKind.Local, Local: var moveSource }
                        } &&
                        moveTarget == current)
                    {
                        source = moveSource;
                        break;
                    }

                    if (instruction is MirCopy
                        {
                            Target: { Kind: PlaceKind.Local, Local: var copyTarget },
                            Source: { Kind: PlaceKind.Local, Local: var copySource }
                        } &&
                        copyTarget == current)
                    {
                        source = copySource;
                        break;
                    }

                    if (instruction is MirLoad
                        {
                            Target: { Kind: PlaceKind.Local, Local: var loadTarget },
                            Source: MirPlace { Kind: PlaceKind.Local, Local: var loadSource }
                        } &&
                        loadTarget == current)
                    {
                        source = loadSource;
                        break;
                    }
                }

                if (source.IsValid)
                {
                    break;
                }
            }

            if (!source.IsValid)
            {
                break;
            }

            current = source;
            if (_callerOwnedGroupByLocal.ContainsKey(current))
            {
                return current;
            }
        }

        return LocalId.None;
    }

    private bool TryConvertCallerOwnedReturnConstructor(
        MirCall call,
        MirFunctionRef constructor,
        out LlvmCall? lowered)
    {
        lowered = null;
        if (_currentMirFunction?.CallerOwnedAggregateAbi is not { HasOutReturn: true } abi ||
            _callerOwnedOutDestination == null ||
            call.Target is not MirPlace { Kind: PlaceKind.Local } target ||
            !abi.OutReturnLocals.Contains(target.Local) ||
            !TypeSemantics.IsAdtConstructorCall(constructor))
        {
            return false;
        }

        _typeLowering.TryGetStructType(target.TypeId, out var structType);
        var fieldTypeIds = ResolveConstructorFieldTypeIds(target.TypeId, constructor.Name);
        EmitInlineConstructorFieldStores(
            CoerceToPointer(_callerOwnedOutDestination),
            call.Arguments,
            fieldTypeIds,
            structType,
            HasTagFieldForType(target.TypeId),
            retainBorrowedProjectionFields: true);
        AssignPlaceFromValue(target, _callerOwnedOutDestination);
        return true;
    }

    private LlvmValue? GetCallerOwnedDestination(MirPlace target)
    {
        if (_currentMirFunction?.CallerOwnedAggregateAbi is { HasOutReturn: true } abi &&
            abi.OutReturnLocals.Contains(target.Local))
        {
            return _callerOwnedOutDestination;
        }

        if (!_callerOwnedGroupByLocal.TryGetValue(target.Local, out var group))
        {
            return null;
        }

        if (_callerOwnedStorageByCanonicalLocal.TryGetValue(group.CanonicalLocal, out var existing))
        {
            return existing;
        }

        if (!_typeLowering.TryGetStructType(group.TypeId, out var structType))
        {
            return null;
        }

        var allocatedType = BuildCallerOwnedWrapperType(group, structType);
        var alloca = new LlvmAlloca
        {
            AllocatedType = allocatedType,
            ResultName = _nameMangler.NewTempName($"aggregate_l{group.CanonicalLocal.Value}")
        };
        EmitAllocaInEntryBlock(alloca);
        var storage = new LlvmLocal
        {
            Name = alloca.ResultName!,
            Type = LlvmPointerType.VoidPtr()
        };
        _callerOwnedStorageByCanonicalLocal[group.CanonicalLocal] = storage;
        return storage;
    }

    private LlvmValue? GetCallerOwnedArrayStorage(MirPlace target, string key)
    {
        if (!_callerOwnedGroupByLocal.TryGetValue(target.Local, out var group))
        {
            return null;
        }

        var cacheKey = (group.CanonicalLocal, key);
        if (_callerOwnedArrayStorageByGroup.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var orderedStorages = group.ArrayStorages
            .OrderBy(static storage => storage.Key, StringComparer.Ordinal)
            .ToArray();
        var storageIndex = Array.FindIndex(orderedStorages, storage =>
            string.Equals(storage.Key, key, StringComparison.Ordinal));
        if (storageIndex < 0 ||
            GetCallerOwnedDestination(target) is not { } destination ||
            !_typeLowering.TryGetStructType(group.TypeId, out var aggregateType))
        {
            return null;
        }

        if (BuildCallerOwnedWrapperType(group, aggregateType) is not LlvmStructType wrapperType)
        {
            return null;
        }
        var pointer = new LlvmGetElementPtr
        {
            Pointer = CoerceToPointer(destination),
            StructType = wrapperType,
            StructFieldIndex = storageIndex + 1,
            ResultName = _nameMangler.NewTempName($"aggregate_l{group.CanonicalLocal.Value}_array{storageIndex}")
        };
        _currentBlock!.Instructions.Add(pointer);
        var value = new LlvmInstructionRef { Instruction = pointer, Type = LlvmPointerType.VoidPtr() };
        _callerOwnedArrayStorageByGroup[cacheKey] = value;
        return value;
    }

    private LlvmType BuildCallerOwnedWrapperType(
        MirCallerOwnedAggregateGroup group,
        LlvmStructType aggregateType)
    {
        if (group.ArrayStorages.Count == 0)
        {
            return aggregateType;
        }

        if (_callerOwnedWrapperTypeByCanonicalLocal.TryGetValue(group.CanonicalLocal, out var existing))
        {
            return existing;
        }

        var wrapperType = new LlvmStructType
        {
            Fields =
            [
                aggregateType,
                .. group.ArrayStorages
                    .OrderBy(static storage => storage.Key, StringComparer.Ordinal)
                    .Select(storage => (LlvmType)new LlvmArrayType
                    {
                        Element = LlvmIntType.I64,
                        Size = checked((int)Math.Max(
                            1,
                            (ResolveCallerOwnedArrayStorageBytes(storage) + sizeof(long) - 1) / sizeof(long)))
                    })
            ]
        };
        _callerOwnedWrapperTypeByCanonicalLocal[group.CanonicalLocal] = wrapperType;
        return wrapperType;
    }

    private long ResolveCallerOwnedArrayStorageBytes(MirCallerOwnedArrayStorage storage)
    {
        var elementSize = storage.ElementSize;
        if (TryResolveConcreteListElementType(storage.ArrayTypeId, out var elementTypeId))
        {
            elementSize = GetRuntimeElementSize(elementTypeId);
        }

        try
        {
            var storageBytes = checked(
                CallerOwnedArrayStorageOverheadBytes + checked(storage.Capacity * elementSize));
            return storageBytes <= MaxCallerOwnedInlineArrayStorageBytes ? storageBytes : 0;
        }
        catch (OverflowException)
        {
            return 0;
        }
    }

    private void BindCallerOwnedGroupLocals(LocalId target, LlvmValue destination)
    {
        if (!_callerOwnedGroupByLocal.TryGetValue(target, out var group))
        {
            return;
        }

        var pointer = destination as LlvmLocal ?? new LlvmLocal
        {
            Name = destination switch
            {
                LlvmInstructionRef { Instruction.ResultName: { } name } => name,
                _ => $"aggregate_l{group.CanonicalLocal.Value}"
            },
            Type = LlvmPointerType.VoidPtr()
        };
        foreach (var local in group.Locals)
        {
            _locals.LocalMap[local] = pointer;
        }
    }

    private bool TryDropCallerOwnedAggregate(MirDrop drop, LlvmValue value)
    {
        if (drop.Value is not MirPlace { Kind: PlaceKind.Local } local ||
            !_callerOwnedGroupByLocal.TryGetValue(local.Local, out var group) ||
            !_typeLowering.TryGetStructType(group.TypeId, out var structType) ||
            !_typeLowering.TryGetConstructorLayouts(group.TypeId, out var layouts) ||
            layouts.Count != 1)
        {
            return false;
        }

        var layout = layouts[0];
        var hasTag = HasTagFieldForType(group.TypeId);
        for (var fieldIndex = 0; fieldIndex < layout.FieldTypeIds.Count; fieldIndex++)
        {
            var fieldTypeId = layout.FieldTypeIds[fieldIndex];
            var structFieldIndex = ComputeStructFieldIndex(hasTag, fieldIndex);
            if (structFieldIndex < 0 || structFieldIndex >= structType.Fields.Count)
            {
                continue;
            }

            var fieldPointer = new LlvmGetElementPtr
            {
                Pointer = CoerceToPointer(value),
                StructType = structType,
                StructFieldIndex = structFieldIndex,
                ResultName = _nameMangler.NewTempName($"aggregate_drop_field{fieldIndex}_ptr")
            };
            _currentBlock!.Instructions.Add(fieldPointer);
            EmitReleaseManagedPayloadFromPointer(
                _currentBlock,
                new LlvmInstructionRef { Instruction = fieldPointer, Type = LlvmPointerType.VoidPtr() },
                fieldTypeId,
                structType.Fields[structFieldIndex],
                $"{fieldPointer.ResultName!.TrimStart('%')}_payload");
            EmitClearCallerOwnedManagedPayloadFromPointer(
                _currentBlock,
                new LlvmInstructionRef { Instruction = fieldPointer, Type = LlvmPointerType.VoidPtr() },
                fieldTypeId,
                structType.Fields[structFieldIndex],
                $"{fieldPointer.ResultName!.TrimStart('%')}_payload");
        }

        return true;
    }

    private void EmitClearCallerOwnedManagedPayloadFromPointer(
        LlvmBasicBlock block,
        LlvmValue pointer,
        TypeId typeId,
        LlvmType storageType,
        string namePrefix)
    {
        if (!PayloadContainsManagedRc(typeId))
        {
            return;
        }

        if (IsManagedRcType(typeId) && storageType is LlvmPointerType)
        {
            block.Instructions.Add(new LlvmStore
            {
                Value = LlvmNullPointer.Instance,
                Pointer = pointer
            });
            return;
        }

        if (!_typeLowering.TryGetTypeDescriptor(typeId, out var descriptor))
        {
            throw new InvalidOperationException(
                $"Caller-owned managed payload T{typeId.Value} has no lowering descriptor.");
        }

        if (descriptor is TypeDescriptor.Builtin builtin)
        {
            EmitClearCallerOwnedManagedPayloadFromPointer(
                block,
                pointer,
                new TypeId(builtin.TypeIdValue),
                storageType,
                namePrefix);
            return;
        }

        if (descriptor is not TypeDescriptor.Tuple tuple || storageType is not LlvmStructType structType)
        {
            throw new InvalidOperationException(
                $"Caller-owned managed payload T{typeId.Value} cannot be destructively cleared from {storageType.ToIrString()}.");
        }

        var fieldCount = Math.Min(tuple.FieldTypes.Length, structType.Fields.Count);
        for (var index = 0; index < fieldCount; index++)
        {
            var fieldTypeId = tuple.FieldTypes[index];
            if (!PayloadContainsManagedRc(fieldTypeId))
            {
                continue;
            }

            var fieldPointer = new LlvmGetElementPtr
            {
                Pointer = pointer,
                StructType = structType,
                StructFieldIndex = index,
                ResultName = _nameMangler.NewTempName($"{namePrefix}_clear_field{index}_ptr")
            };
            block.Instructions.Add(fieldPointer);
            EmitClearCallerOwnedManagedPayloadFromPointer(
                block,
                new LlvmInstructionRef { Instruction = fieldPointer, Type = LlvmPointerType.VoidPtr() },
                fieldTypeId,
                structType.Fields[index],
                $"{namePrefix}_field{index}");
        }
    }
}
