using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    private LlvmCall? ConvertValueBox(MirCall call)
    {
        if (call.Arguments.Count != 1 ||
            !TryGetRequiredLocalCallTargetPlace(call, WellKnownStrings.InternalNames.ValueBox, out var targetPlace))
        {
            return null;
        }

        var payload = call.Arguments[0];
        var payloadTypeId = payload.TypeId;
        if (!CanLowerValueBoxPayload(payloadTypeId, call, "box"))
        {
            return null;
        }

        var storageType = LowerStorageTypeIdOrReport(payloadTypeId, "value_box payload");
        var storageSize = Math.Max(1L, GetLlvmStorageSize(storageType));
        var boxRuntimeTypeId = ComputeValueBoxRuntimeTypeId(payloadTypeId);
        _valueBoxPayloadTypeByRuntimeTypeId[boxRuntimeTypeId] = payloadTypeId;
        var alloc = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.Alloc,
                LlvmPointerType.VoidPtr(),
                [LlvmIntType.I64, LlvmIntType.I32]),
            Arguments =
            [
                new LlvmConstant { Value = storageSize, Type = LlvmIntType.I64 },
                new LlvmConstant { Value = boxRuntimeTypeId, Type = LlvmIntType.I32 }
            ],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("value_box")
        };
        _currentBlock?.Instructions.Add(alloc);

        var boxedPointer = new LlvmInstructionRef
        {
            Instruction = alloc,
            Type = LlvmPointerType.VoidPtr()
        };
        var rawPayload = ConvertOperand(payload);
        var storedPayload = CoerceValueToType(rawPayload, storageType, "value_box payload");
        EmitRetainManagedPayloadValue(payloadTypeId, storedPayload, storageType);
        _currentBlock?.Instructions.Add(new LlvmStore
        {
            Value = storedPayload,
            Pointer = boxedPointer
        });

        ClearGenericLocal(targetPlace.Local);
        _locals.LocalMap[targetPlace.Local] = new LlvmLocal
        {
            Name = alloc.ResultName!,
            Type = LlvmPointerType.VoidPtr()
        };

        return null;
    }

    private LlvmCall? ConvertValueUnbox(MirCall call)
    {
        if (call.Arguments.Count != 1 ||
            !TryGetRequiredLocalCallTargetPlace(call, WellKnownStrings.InternalNames.ValueUnbox, out var targetPlace))
        {
            return null;
        }

        var payloadTypeId = targetPlace.TypeId;
        if (!CanLowerValueBoxPayload(payloadTypeId, call, "unbox"))
        {
            return null;
        }

        var storageType = LowerStorageTypeIdOrReport(payloadTypeId, "value_unbox payload");
        var resultName = _nameMangler.NewTempName($"l{targetPlace.Local.Value}");
        var load = new LlvmLoad
        {
            Pointer = CoerceToPointer(ConvertOperand(call.Arguments[0])),
            LoadType = storageType,
            ResultName = resultName
        };
        _currentBlock?.Instructions.Add(load);
        EmitRetainManagedPayloadValue(
            payloadTypeId,
            new LlvmInstructionRef { Instruction = load, Type = storageType },
            storageType);

        ClearGenericLocal(targetPlace.Local);
        _locals.LocalMap[targetPlace.Local] = new LlvmLocal
        {
            Name = resultName,
            Type = storageType
        };

        return null;
    }

    private LlvmCall? ConvertValueBoxFree(MirCall call)
    {
        if (call.Arguments.Count != 1)
        {
            return null;
        }

        _currentBlock?.Instructions.Add(new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.DecRefShared,
                LlvmVoidType.Instance,
                [LlvmPointerType.VoidPtr()]),
            Arguments = [CoerceToPointer(ConvertOperand(call.Arguments[0]))]
        });
        return null;
    }

    private LlvmCall? ConvertSharedNew(MirCall call)
    {
        if (call.Arguments.Count != 1 ||
            !TryGetRequiredLocalCallTargetPlace(call, WellKnownStrings.InternalNames.SharedNew, out var targetPlace))
        {
            return null;
        }

        var payload = call.Arguments[0];
        var payloadTypeId = payload.TypeId;
        if (!CanLowerValueBoxPayload(payloadTypeId, call, "shared new"))
        {
            return null;
        }

        var storageType = LowerStorageTypeIdOrReport(payloadTypeId, "shared_payload");
        var storageSize = Math.Max(1L, GetLlvmStorageSize(storageType));
        var boxRuntimeTypeId = ComputeValueBoxRuntimeTypeId(payloadTypeId);
        _valueBoxPayloadTypeByRuntimeTypeId[boxRuntimeTypeId] = payloadTypeId;
        var alloc = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.Alloc,
                LlvmPointerType.VoidPtr(),
                [LlvmIntType.I64, LlvmIntType.I32]),
            Arguments =
            [
                new LlvmConstant { Value = storageSize, Type = LlvmIntType.I64 },
                new LlvmConstant { Value = boxRuntimeTypeId, Type = LlvmIntType.I32 }
            ],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("shared_new")
        };
        _currentBlock?.Instructions.Add(alloc);

        var sharedPointer = new LlvmInstructionRef
        {
            Instruction = alloc,
            Type = LlvmPointerType.VoidPtr()
        };
        var rawPayload = ConvertOperand(payload);
        var storedPayload = CoerceValueToType(rawPayload, storageType, "shared_payload");
        EmitRetainManagedPayloadValue(payloadTypeId, storedPayload, storageType);
        _currentBlock?.Instructions.Add(new LlvmStore
        {
            Value = storedPayload,
            Pointer = sharedPointer
        });

        ClearGenericLocal(targetPlace.Local);
        _locals.LocalMap[targetPlace.Local] = new LlvmLocal
        {
            Name = alloc.ResultName!,
            Type = LlvmPointerType.VoidPtr()
        };

        return null;
    }

    private LlvmCall? ConvertSharedBorrow(MirCall call)
    {
        if (call.Arguments.Count != 1 ||
            !TryGetRequiredLocalCallTargetPlace(call, WellKnownStrings.InternalNames.SharedBorrow, out var targetPlace))
        {
            return null;
        }

        var sourceValue = CoerceToPointer(ConvertOperand(call.Arguments[0]));
        var borrowedValue = ResolveSharedBorrowPayload(sourceValue, call.Arguments[0].TypeId);
        var alias = EmitSharedAlias(borrowedValue, "shared_borrow");
        ClearGenericLocal(targetPlace.Local);
        _locals.LocalMap[targetPlace.Local] = new LlvmLocal
        {
            Name = alias.ResultName!,
            Type = LlvmPointerType.VoidPtr()
        };
        return null;
    }

    private LlvmValue ResolveSharedBorrowPayload(LlvmValue sharedValue, TypeId argumentTypeId)
    {
        if (!TryGetSharedPayloadTypeId(argumentTypeId, out var payloadTypeId) ||
            LowerStorageTypeIdOrReport(payloadTypeId, "shared borrow payload") is not LlvmPointerType)
        {
            return sharedValue;
        }

        var load = new LlvmLoad
        {
            Pointer = sharedValue,
            LoadType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("shared_borrow_payload")
        };
        _currentBlock?.Instructions.Add(load);
        return new LlvmInstructionRef
        {
            Instruction = load,
            Type = LlvmPointerType.VoidPtr()
        };
    }

    private bool TryGetSharedPayloadTypeId(TypeId argumentTypeId, out TypeId payloadTypeId)
    {
        payloadTypeId = TypeId.None;
        if (!_typeLowering.TryGetTypeDescriptor(argumentTypeId, out var descriptor))
        {
            return false;
        }

        if (descriptor is TypeDescriptor.Ref or TypeDescriptor.MutRef)
        {
            var inner = descriptor switch
            {
                TypeDescriptor.Ref reference => reference.Inner,
                TypeDescriptor.MutRef mutableReference => mutableReference.Inner,
                _ => TypeId.None
            };
            if (!_typeLowering.TryGetTypeDescriptor(inner, out descriptor))
            {
                return false;
            }
        }

        if (descriptor is not TypeDescriptor.Shared shared)
        {
            return false;
        }

        payloadTypeId = shared.Inner;
        return payloadTypeId.IsValid;
    }

    private LlvmCall? ConvertSharedClone(MirCall call)
    {
        if (call.Arguments.Count != 1 ||
            !TryGetRequiredLocalCallTargetPlace(call, WellKnownStrings.InternalNames.SharedClone, out var targetPlace))
        {
            return null;
        }

        var sourceValue = CoerceToPointer(ConvertOperand(call.Arguments[0]));
        var retain = CreateRuntimeRcCall(WellKnownStrings.Runtime.IncRefLocal, sourceValue);
        _currentBlock?.Instructions.Add(retain);
        var alias = EmitSharedAlias(sourceValue, "shared_clone");

        ClearGenericLocal(targetPlace.Local);
        _locals.LocalMap[targetPlace.Local] = new LlvmLocal
        {
            Name = alias.ResultName!,
            Type = LlvmPointerType.VoidPtr()
        };
        return null;
    }

    private LlvmCast EmitSharedAlias(LlvmValue sourceValue, string tempPrefix)
    {
        var alias = new LlvmCast
        {
            Op = WellKnownStrings.InternalNames.Bitcast,
            Value = sourceValue,
            TargetType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName(tempPrefix)
        };
        _currentBlock?.Instructions.Add(alias);
        return alias;
    }

    private LlvmCall? ConvertSharedPtrEq(MirCall call)
    {
        var functionType = new LlvmFunctionType
        {
            ReturnType = LlvmIntType.I1,
            ParameterTypes = [LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr()]
        };

        if (call.Arguments.Count < functionType.ParameterTypes.Count)
        {
            RecordSharedPtrEqPartialState(call, functionType);
            return null;
        }

        if (call.Arguments.Count != functionType.ParameterTypes.Count)
        {
            return null;
        }

        var resultName = call.Target is MirPlace target
            ? _nameMangler.NewTempName($"l{target.Local.Value}")
            : _nameMangler.NewTempName("shared_ptr_eq");
        var ptrEqCall = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.PtrEquals,
                LlvmIntType.I1,
                functionType.ParameterTypes),
            Arguments =
            [
                CoerceToPointer(ConvertOperand(call.Arguments[0])),
                CoerceToPointer(ConvertOperand(call.Arguments[1]))
            ],
            ReturnType = LlvmIntType.I1,
            ResultName = resultName
        };

        if (call.Target is MirPlace { Kind: PlaceKind.Local } targetPlace)
        {
            ClearGenericLocal(targetPlace.Local);
            _locals.LocalMap[targetPlace.Local] = new LlvmLocal
            {
                Name = resultName,
                Type = LlvmIntType.I1
            };
        }

        return ptrEqCall;
    }

    private void RecordSharedPtrEqPartialState(MirCall call, LlvmFunctionType functionType)
    {
        if (call.Target is not MirPlace { Kind: PlaceKind.Local } targetLocal)
        {
            return;
        }

        var boundArguments = new List<LlvmValue>(call.Arguments.Count);
        for (var index = 0; index < call.Arguments.Count; index++)
        {
            var rawArgument = ConvertOperand(call.Arguments[index]);
            boundArguments.Add(CoerceValueToType(rawArgument, functionType.ParameterTypes[index], $"shared_ptr_eq_arg{index}"));
        }

        var boundArgumentManagedFlags = boundArguments
            .Select((argument, index) => IsManagedRcPayloadValue(call.Arguments[index], argument, argument.Type))
            .ToList();

        _partialCallStates[targetLocal.Local] = new PartialCallState(
            CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.PtrEquals,
                LlvmIntType.I1,
                functionType.ParameterTypes),
            functionType,
            boundArguments,
            boundArgumentManagedFlags,
            0,
            null);
        _locals.LocalMap.Remove(targetLocal.Local);
        _locals.RuntimeWordLocals.Remove(targetLocal.Local);
        ClearGenericLocal(targetLocal.Local);
    }

    private bool CanLowerValueBoxPayload(TypeId typeId, MirCall call, string operation)
    {
        if (!typeId.IsValid || _typeLowering.IsOpenDynamicType(typeId))
        {
            ReportValueBoxLoweringError(call, $"{operation} requires a concrete payload type before LLVM lowering");
            return false;
        }

        return true;
    }

    private static int ComputeValueBoxRuntimeTypeId(TypeId payloadTypeId)
    {
        var hash = AdtConstructorTypeId.Compute($"value-box:{payloadTypeId.Value}");
        return (hash & 0x1fffffff) | 0x60000000;
    }

    private bool PayloadContainsManagedRc(TypeId typeId)
    {
        if (!typeId.IsValid || _typeLowering.IsOpenDynamicType(typeId))
        {
            return false;
        }

        if (IsManagedRcType(typeId))
        {
            return true;
        }

        if (!_typeLowering.TryGetTypeDescriptor(typeId, out var descriptor))
        {
            return false;
        }

        return descriptor switch
        {
            TypeDescriptor.Tuple tuple => tuple.FieldTypes.Any(PayloadContainsManagedRc),
            TypeDescriptor.Shared => true,
            TypeDescriptor.Builtin builtin => PayloadContainsManagedRc(new TypeId(builtin.TypeIdValue)),
            _ => false
        };
    }

    private void EmitRetainManagedPayloadValue(TypeId typeId, LlvmValue value, LlvmType storageType)
    {
        if (!PayloadContainsManagedRc(typeId))
        {
            return;
        }

        if (IsManagedRcType(typeId) && storageType is LlvmPointerType)
        {
            _currentBlock?.Instructions.Add(new LlvmCall
            {
                Function = CreateRuntimeFunctionGlobal(
                    WellKnownStrings.Runtime.IncRefShared,
                    LlvmVoidType.Instance,
                    [LlvmPointerType.VoidPtr()]),
                Arguments = [CoerceToPointer(value)]
            });
            return;
        }

        if (storageType is not LlvmStructType structType ||
            !_typeLowering.TryGetTypeDescriptor(typeId, out var descriptor) ||
            descriptor is not TypeDescriptor.Tuple tuple)
        {
            return;
        }

        for (var index = 0; index < tuple.FieldTypes.Length && index < structType.Fields.Count; index++)
        {
            var fieldTypeId = tuple.FieldTypes[index];
            if (!PayloadContainsManagedRc(fieldTypeId))
            {
                continue;
            }

            var fieldType = structType.Fields[index];
            var extract = new LlvmExtractValue
            {
                Aggregate = value,
                Indices = [index],
                ResultName = _nameMangler.NewTempName("value_box_field")
            };
            _currentBlock?.Instructions.Add(extract);
            EmitRetainManagedPayloadValue(
                fieldTypeId,
                new LlvmInstructionRef { Instruction = extract, Type = fieldType },
                fieldType);
        }
    }

    private void ReportValueBoxLoweringError(MirCall call, string message)
    {
        var functionName = _currentFunction?.Name ?? _currentMirFunction?.Name ?? "<module>";
        var dedupeKey = $"{functionName}:value-box:{message}:{call.Span.Location.Position}";
        if (!_reportedUnresolvedTypeSites.Add(dedupeKey))
        {
            return;
        }

        var diagnostic = Diagnostic.Diagnostic.Error(message, "E5306")
            .WithNote(DiagnosticMessages.FunctionNote(functionName));

        if (HasSpan(call.Span))
        {
            diagnostic.WithLabel(call.Span, "value boxing call");
        }
        else if (_currentMirFunction != null && HasSpan(_currentMirFunction.Span))
        {
            diagnostic.WithLabel(_currentMirFunction.Span, "value boxing call");
        }

        Diagnostics.Add(diagnostic);
    }
}
