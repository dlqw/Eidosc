using Eidosc.Mir;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    /// <summary>
    /// 处理 cfn_call(fn_ptr, arg1, arg2, ...) 调用：通过 C 函数指针间接调用。
    /// 第一个参数是函数指针 (RawPtr/ptr)，后续参数直接传递给 C 函数。
    /// </summary>
    private LlvmCall? ConvertCfnCall(MirCall call)
    {
        if (call.Arguments.Count < 1)
        {
            return null;
        }

        var targetPlace = call.Target as MirPlace;
        var resultName = targetPlace != null
            ? _nameMangler.NewTempName($"l{targetPlace.Local.Value}")
            : _nameMangler.NewTempName("cfn_call_res");

        // 第一个参数：函数指针
        var fnPtrValue = ConvertOperand(call.Arguments[0]);
        var fnPtr = CoerceToPointer(fnPtrValue);

        if (!TryBuildCfnSignature(call.Arguments[0].TypeId, out var functionType))
        {
            var fallbackReturnType = (LlvmType)LlvmIntType.I64;
            if (targetPlace != null && targetPlace.TypeId.IsValid)
            {
                fallbackReturnType = NormalizeSignatureReturnType(
                    LowerTypeIdOrReport(targetPlace.TypeId, "cfn call result"));
            }

            functionType = new LlvmFunctionType
            {
                ReturnType = fallbackReturnType,
                ParameterTypes = call.Arguments.Skip(1)
                    .Select(argument => NormalizeParameterType(LowerStorageTypeIdOrReport(argument.TypeId, "cfn call argument")))
                    .ToList()
            };
        }

        var typedFnPtr = new LlvmCast
        {
            Op = WellKnownStrings.InternalNames.Bitcast,
            Value = fnPtr,
            TargetType = new LlvmPointerType { ElementType = functionType },
            ResultName = _nameMangler.NewTempName("cfn_typed")
        };
        _currentBlock?.Instructions.Add(typedFnPtr);

        var callArgs = new List<LlvmValue>(call.Arguments.Count - 1);
        for (var i = 1; i < call.Arguments.Count; i++)
        {
            var rawArgument = ConvertOperand(call.Arguments[i]);
            var parameterIndex = i - 1;
            callArgs.Add(parameterIndex < functionType.ParameterTypes.Count
                ? CoerceValueToType(rawArgument, functionType.ParameterTypes[parameterIndex], $"cfn_arg{parameterIndex}")
                : rawArgument);
        }

        var indirectCall = new LlvmCall
        {
            Function = new LlvmInstructionRef
            {
                Instruction = typedFnPtr,
                Type = new LlvmPointerType { ElementType = functionType }
            },
            Arguments = callArgs,
            ReturnType = functionType.ReturnType,
            ResultName = resultName
        };
        _currentBlock?.Instructions.Add(indirectCall);

        if (targetPlace != null)
        {
            ClearGenericLocal(targetPlace.Local);
            _locals.LocalMap[targetPlace.Local] = new LlvmLocal
            {
                Name = resultName,
                Type = functionType.ReturnType
            };
        }

        // 返回 null 表示已自行处理 LlvmCall
        return null;
    }

    private bool TryBuildCfnSignature(TypeId cfnTypeId, out LlvmFunctionType functionType)
    {
        functionType = default!;
        if (!cfnTypeId.IsValid ||
            !_typeLowering.TryGetTyConTypeArguments(cfnTypeId, out var constructorDescriptor, out var typeArguments) ||
            constructorDescriptor != WellKnownStrings.BuiltinTypes.Cfn ||
            typeArguments.Count == 0)
        {
            return false;
        }

        var returnTypeId = typeArguments[^1];
        functionType = new LlvmFunctionType
        {
            ReturnType = NormalizeSignatureReturnType(LowerTypeIdOrReport(returnTypeId, "cfn call result")),
            ParameterTypes = typeArguments
                .Take(typeArguments.Count - 1)
                .Select(typeId => NormalizeParameterType(LowerStorageTypeIdOrReport(typeId, "cfn call parameter")))
                .ToList()
        };
        return true;
    }

    /// <summary>
    /// 处理 ptr_add(ptr, byte_offset) 调用：指针字节偏移。
    /// 生成 LLVM: getelementptr i8, ptr %ptr, i64 %offset
    /// </summary>
    private LlvmCall? ConvertPtrAdd(MirCall call)
    {
        if (call.Arguments.Count != 2)
        {
            return null;
        }

        var targetPlace = call.Target as MirPlace;
        if (targetPlace == null)
        {
            return null;
        }

        var resultName = _nameMangler.NewTempName($"l{targetPlace.Local.Value}");

        var ptrValue = CoerceToPointer(ConvertOperand(call.Arguments[0]));
        var offsetValue = ConvertOperand(call.Arguments[1]);

        var gep = new LlvmGetElementPtr
        {
            Pointer = ptrValue,
            ElementType = LlvmIntType.I8,
            Index = offsetValue,
            ResultName = resultName
        };
        _currentBlock?.Instructions.Add(gep);

        ClearGenericLocal(targetPlace.Local);
        _locals.LocalMap[targetPlace.Local] = new LlvmLocal
        {
            Name = resultName,
            Type = LlvmPointerType.VoidPtr()
        };

        return null;
    }

    /// <summary>
    /// 处理 ptr_load_int(ptr) 调用：从指针加载 i64 值。
    /// 生成 LLVM: load i64, ptr %ptr
    /// </summary>
    private LlvmCall? ConvertPtrLoadInt(MirCall call)
    {
        if (call.Arguments.Count != 1)
        {
            return null;
        }

        var targetPlace = call.Target as MirPlace;
        if (targetPlace == null)
        {
            return null;
        }

        var resultName = _nameMangler.NewTempName($"l{targetPlace.Local.Value}");

        var ptrValue = CoerceToPointer(ConvertOperand(call.Arguments[0]));

        var load = new LlvmLoad
        {
            Pointer = ptrValue,
            LoadType = LlvmIntType.I64,
            ResultName = resultName
        };
        _currentBlock?.Instructions.Add(load);

        ClearGenericLocal(targetPlace.Local);
        _locals.LocalMap[targetPlace.Local] = new LlvmLocal
        {
            Name = resultName,
            Type = LlvmIntType.I64
        };

        return null;
    }

    /// <summary>
    /// 处理 ptr_store_int(ptr, value) 调用：向指针存储 i64 值。
    /// 生成 LLVM: store i64 %value, ptr %ptr
    /// </summary>
    private LlvmCall? ConvertPtrStoreInt(MirCall call)
    {
        if (call.Arguments.Count != 2)
        {
            return null;
        }

        var ptrValue = CoerceToPointer(ConvertOperand(call.Arguments[0]));
        var value = ConvertOperand(call.Arguments[1]);

        var store = new LlvmStore
        {
            Value = value,
            Pointer = ptrValue
        };
        _currentBlock?.Instructions.Add(store);

        // Unit 返回值，不需要存入 _locals.LocalMap
        return null;
    }

    /// <summary>
    /// 类型化指针读取分发表：函数名 → (LLVM 加载类型, LLVM 本地类型)
    /// </summary>
    private static (LlvmType LoadType, LlvmType LocalType) GetTypedLoadInfo(string name) => name switch
    {
        WellKnownStrings.InternalNames.PtrLoadFloat => (LlvmFloatType.Double, LlvmFloatType.Double),
        WellKnownStrings.InternalNames.PtrLoadPtr   => (LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr()),
        WellKnownStrings.InternalNames.PtrLoadI32   => (LlvmIntType.I32, LlvmIntType.I64),
        WellKnownStrings.InternalNames.PtrLoadI8    => (LlvmIntType.I8, LlvmIntType.I64),
        WellKnownStrings.InternalNames.PtrLoadBool  => (LlvmIntType.I1, LlvmIntType.I1),
        _ => (LlvmIntType.I64, LlvmIntType.I64)
    };

    /// <summary>
    /// 类型化指针写入分发表：函数名 → LLVM 值类型
    /// </summary>
    private static LlvmType GetTypedStoreValueType(string name) => name switch
    {
        WellKnownStrings.InternalNames.PtrStoreFloat => LlvmFloatType.Double,
        WellKnownStrings.InternalNames.PtrStorePtr   => LlvmPointerType.VoidPtr(),
        WellKnownStrings.InternalNames.PtrStoreI32   => LlvmIntType.I32,
        WellKnownStrings.InternalNames.PtrStoreI8    => LlvmIntType.I8,
        WellKnownStrings.InternalNames.PtrStoreBool  => LlvmIntType.I1,
        _ => LlvmIntType.I64
    };

    /// <summary>
    /// 处理类型化指针读取（ptr_load_float, ptr_load_ptr, ptr_load_i32, ptr_load_i8, ptr_load_bool）。
    /// 根据函数名查表确定 LLVM 加载类型。
    /// </summary>
    private LlvmCall? ConvertTypedPtrLoad(MirCall call, string name)
    {
        if (call.Arguments.Count != 1)
        {
            return null;
        }

        var targetPlace = call.Target as MirPlace;
        if (targetPlace == null)
        {
            return null;
        }

        var (loadType, localType) = GetTypedLoadInfo(name);
        var resultName = _nameMangler.NewTempName($"l{targetPlace.Local.Value}");
        var ptrValue = CoerceToPointer(ConvertOperand(call.Arguments[0]));

        var load = new LlvmLoad
        {
            Pointer = ptrValue,
            LoadType = loadType,
            ResultName = resultName
        };
        _currentBlock?.Instructions.Add(load);

        // 窄整数类型需要 zext 到 i64 以匹配 Eidos 的 Int 表示
        LlvmValue resultRef;
        var needsExt = localType is LlvmIntType { Bits: > 0 } localInt
            && loadType is LlvmIntType { Bits: > 0 } loadInt
            && localInt.Bits > loadInt.Bits;
        if (needsExt)
        {
            var ext = new LlvmCast
            {
                Op = "zext",
                Value = new LlvmInstructionRef { Instruction = load, Type = loadType },
                TargetType = localType,
                ResultName = _nameMangler.NewTempName("zext")
            };
            _currentBlock?.Instructions.Add(ext);
            resultRef = new LlvmInstructionRef { Instruction = ext, Type = localType };
            resultName = ext.ResultName!;
        }
        else
        {
            resultRef = new LlvmInstructionRef { Instruction = load, Type = loadType };
        }

        ClearGenericLocal(targetPlace.Local);
        _locals.LocalMap[targetPlace.Local] = new LlvmLocal
        {
            Name = resultName,
            Type = localType
        };

        return null;
    }

    /// <summary>
    /// 处理类型化指针写入（ptr_store_float, ptr_store_ptr, ptr_store_i32, ptr_store_i8）。
    /// 根据函数名查表确定 LLVM 存储类型，并截断/转换值。
    /// </summary>
    private LlvmCall? ConvertTypedPtrStore(MirCall call, string name)
    {
        if (call.Arguments.Count != 2)
        {
            return null;
        }

        var valueType = GetTypedStoreValueType(name);
        var ptrValue = CoerceToPointer(ConvertOperand(call.Arguments[0]));
        var rawValue = ConvertOperand(call.Arguments[1]);

        // 如果值的 LLVM 类型与目标类型不同，需要截断/转换
        LlvmValue storeValue;
        if (rawValue.Type is LlvmIntType sourceInt && valueType is LlvmIntType targetInt &&
            sourceInt.Bits > targetInt.Bits)
        {
            var trunc = new LlvmCast
            {
                Op = "trunc",
                Value = rawValue,
                TargetType = valueType,
                ResultName = _nameMangler.NewTempName("trunc")
            };
            _currentBlock?.Instructions.Add(trunc);
            storeValue = new LlvmInstructionRef { Instruction = trunc, Type = valueType };
        }
        else if (rawValue.Type is LlvmIntType && valueType is LlvmPointerType)
        {
            var cast = new LlvmCast
            {
                Op = "inttoptr",
                Value = rawValue,
                TargetType = valueType,
                ResultName = _nameMangler.NewTempName("i2p")
            };
            _currentBlock?.Instructions.Add(cast);
            storeValue = new LlvmInstructionRef { Instruction = cast, Type = valueType };
        }
        else if (rawValue.Type is LlvmPointerType && valueType is LlvmIntType)
        {
            var cast = new LlvmCast
            {
                Op = "ptrtoint",
                Value = rawValue,
                TargetType = valueType,
                ResultName = _nameMangler.NewTempName("p2i")
            };
            _currentBlock?.Instructions.Add(cast);
            storeValue = new LlvmInstructionRef { Instruction = cast, Type = valueType };
        }
        else
        {
            storeValue = rawValue;
        }

        var store = new LlvmStore
        {
            Value = storeValue,
            Pointer = ptrValue
        };
        _currentBlock?.Instructions.Add(store);

        return null;
    }

    /// <summary>
    /// Lowers ordinary ADT constructor calls at the call site. Constructor symbols are
    /// polymorphic, so a single synthesized LLVM function cannot safely cover both
    /// word-sized payloads and aggregate payloads such as Option[(Int, Int)].
    /// </summary>
    private LlvmCall? ConvertConstructorCallInline(
        MirCall call,
        MirFunctionRef ctorRef)
    {
        if (!TryGetRequiredLocalCallTargetPlace(call, "constructor inline allocation", out var targetPlace))
        {
            return null;
        }

        var payloadSize = ComputeConstructorPayloadSize(call.Arguments);
        var typeId = ComputeRuntimeConstructorTypeId(ctorRef);

        var allocCall = new LlvmCall
        {
            Function = new LlvmGlobal
            {
                Name = WellKnownStrings.Runtime.Alloc,
                Type = new LlvmFunctionType
                {
                    ReturnType = LlvmPointerType.VoidPtr(),
                    ParameterTypes = [LlvmIntType.I64, LlvmIntType.I32]
                }
            },
            Arguments =
            [
                new LlvmConstant { Value = payloadSize, Type = LlvmIntType.I64 },
                new LlvmConstant { Value = typeId, Type = LlvmIntType.I32 }
            ],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("ctor_alloc")
        };

        _currentBlock!.Instructions.Add(allocCall);
        var allocResult = new LlvmInstructionRef
        {
            Instruction = allocCall,
            Type = LlvmPointerType.VoidPtr()
        };

        EmitConstructorFieldStoresByType(
            allocResult,
            call.Arguments,
            retainBorrowedProjectionFields: true);
        AssignPlaceFromValue(targetPlace, allocResult);

        return null;
    }
}
