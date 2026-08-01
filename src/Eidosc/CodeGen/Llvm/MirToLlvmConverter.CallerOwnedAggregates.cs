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
