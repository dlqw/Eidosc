using Eidosc.Symbols;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

// Math intrinsics, C struct interop, libm resolution
public sealed partial class MirToLlvmConverter
{

    private LlvmValue MaybeConvertCStructStoreValue(LlvmValue value, LlvmType storeType)
    {
        // 使用与 ConvertTypedPtrStore 相同的转换逻辑
        if (value.Type is LlvmIntType sourceInt && storeType is LlvmIntType targetInt &&
            sourceInt.Bits > targetInt.Bits)
        {
            var trunc = new LlvmCast
            {
                Op = "trunc",
                Value = value,
                TargetType = storeType,
                ResultName = _nameMangler.NewTempName("cstrunc")
            };
            _currentBlock!.Instructions.Add(trunc);
            return new LlvmInstructionRef { Instruction = trunc, Type = storeType };
        }

        if (value.Type is LlvmIntType && storeType is LlvmPointerType)
        {
            var cast = new LlvmCast
            {
                Op = "inttoptr",
                Value = value,
                TargetType = storeType,
                ResultName = _nameMangler.NewTempName("csi2p")
            };
            _currentBlock!.Instructions.Add(cast);
            return new LlvmInstructionRef { Instruction = cast, Type = storeType };
        }

        if (value.Type is LlvmPointerType && storeType is LlvmIntType)
        {
            var cast = new LlvmCast
            {
                Op = "ptrtoint",
                Value = value,
                TargetType = storeType,
                ResultName = _nameMangler.NewTempName("csp2i")
            };
            _currentBlock!.Instructions.Add(cast);
            return new LlvmInstructionRef { Instruction = cast, Type = storeType };
        }

        return value;
    }

    /// <summary>
    /// 为非逃逸闭包生成栈分配（alloca 替代 eidos_closure_new）。
    /// 在 MirCall 指令点分配闭包 buffer，合成 invoke/release thunk，
    /// 并将捕获值直接存储到栈 buffer 中。
    /// </summary>
    private LlvmCall? ConvertClosureCallWithStackPromo(
        MirCall call,
        UnifiedStackAllocInfo allocInfo,
        string funcName)
    {
        if (!TryGetRequiredLocalCallTargetPlace(call, "closure stack promotion", out var targetPlace))
        {
            return null;
        }

        // 解析被调用函数的完整签名
        if (!TryResolveCallableSignature(call.Function, out var fullSignature))
        {
            // 无法解析签名，回退到标准闭包路径
            return null;
        }

        // 创建被调用函数的 LLVM 全局引用
        var functionName = ResolveFunctionLlvmName(
            (MirFunctionRef)call.Function, fullSignature);
        var directFunction = new LlvmGlobal
        {
            Name = functionName,
            Type = fullSignature
        };

        // 转换捕获的参数为 LLVM 值
        var boundArguments = call.Arguments
            .Select(ConvertOperand)
            .ToList();
        var boundArgumentManagedFlags = boundArguments
            .Select((argument, index) => IsManagedRcPayloadValue(call.Arguments[index], argument, argument.Type))
            .ToList();

        // 将参数强制转换为完整签名类型
        var coercedArguments = CoerceArgumentsForSignature(fullSignature, boundArguments);

        // 构建 payload 条目（带类型 + RC 标记）
        var payload = LayoutClosurePayload(coercedArguments
            .Select((argument, index) => new ClosurePayloadEntry(
                argument,
                fullSignature.ParameterTypes[index],
                boundArgumentManagedFlags.Count > index
                    ? boundArgumentManagedFlags[index]
                    : IsManagedRcPayloadValue(argument, fullSignature.ParameterTypes[index]))
            {
                TypeId = call.Arguments.Count > index
                    ? call.Arguments[index].TypeId
                    : TypeId.None
            })
            .ToList());

        // 计算可见签名（移除已捕获的参数）
        var visibleSignature = BuildRemainingSignature(fullSignature, coercedArguments.Count);

        // 合成 invoke thunk：接收 (closure_ptr, remaining_args...)，
        // 从 payload 加载捕获值，调用 directFunction(captured..., remaining...)
        var invokeThunk = SynthesizeDirectInvokeThunk(
            directFunction,
            fullSignature,
            visibleSignature,
            payload);

        // 合成 release thunk（对 RC payload 执行 decref）
        var releaseThunk = SynthesizeReleaseThunk(payload);

        var basePtr = EmitStackClosureValue(
            invokeThunk,
            releaseThunk,
            payload,
            $"l{allocInfo.TargetLocal.Value}");
        AssignPlaceFromValue(targetPlace, basePtr);

        return null;
    }

    private LlvmValue EmitStackClosureValue(
        LlvmFunction invokeThunk,
        LlvmFunction? releaseThunk,
        IReadOnlyList<ClosurePayloadEntry> payload,
        string namePrefix)
    {
        const int allocationHeaderSize = 8;
        var totalBytes = checked((int)(allocationHeaderSize + ClosurePayloadOffset + ComputeClosurePayloadByteSize(payload)));
        var allocaType = new LlvmArrayType { Element = LlvmIntType.I8, Size = totalBytes };
        var alloca = new LlvmAlloca
        {
            AllocatedType = allocaType,
            ResultName = _nameMangler.NewTempName($"{namePrefix}_closure_stack")
        };
        EmitAllocaInEntryBlock(alloca);

        var storageCast = new LlvmCast
        {
            Op = WellKnownStrings.InternalNames.Bitcast,
            Value = new LlvmInstructionRef
            {
                Instruction = alloca,
                Type = new LlvmPointerType { ElementType = allocaType }
            },
            TargetType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName($"{namePrefix}_closure_storage")
        };
        _currentBlock!.Instructions.Add(storageCast);

        var invokeBitcast = new LlvmCast
        {
            Op = WellKnownStrings.InternalNames.Bitcast,
            Value = new LlvmGlobal
            {
                Name = invokeThunk.Name,
                Type = new LlvmPointerType { ElementType = BuildFunctionTypeFromLlvmFunction(invokeThunk) }
            },
            TargetType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("stack_closure_invoke_ptr")
        };
        _currentBlock.Instructions.Add(invokeBitcast);

        LlvmValue releaseValue = LlvmNullPointer.Instance;
        if (releaseThunk != null)
        {
            var releaseBitcast = new LlvmCast
            {
                Op = WellKnownStrings.InternalNames.Bitcast,
                Value = new LlvmGlobal
                {
                    Name = releaseThunk.Name,
                    Type = new LlvmPointerType
                    {
                        ElementType = new LlvmFunctionType
                        {
                            ReturnType = LlvmVoidType.Instance,
                            ParameterTypes = [LlvmPointerType.VoidPtr()]
                        }
                    }
                },
                TargetType = LlvmPointerType.VoidPtr(),
                ResultName = _nameMangler.NewTempName("stack_closure_release_ptr")
            };
            _currentBlock.Instructions.Add(releaseBitcast);
            releaseValue = new LlvmInstructionRef
            {
                Instruction = releaseBitcast,
                Type = LlvmPointerType.VoidPtr()
            };
        }

        var initialize = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.ClosureInitStack,
                LlvmPointerType.VoidPtr(),
                [LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64]),
            Arguments =
            [
                new LlvmInstructionRef { Instruction = storageCast, Type = LlvmPointerType.VoidPtr() },
                new LlvmConstant { Value = totalBytes, Type = LlvmIntType.I64 },
                new LlvmInstructionRef { Instruction = invokeBitcast, Type = LlvmPointerType.VoidPtr() },
                releaseValue,
                new LlvmConstant { Value = ComputeClosurePayloadWordCount(payload), Type = LlvmIntType.I64 }
            ],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName($"{namePrefix}_closure_ptr")
        };
        _currentBlock.Instructions.Add(initialize);
        var closureValue = new LlvmInstructionRef
        {
            Instruction = initialize,
            Type = LlvmPointerType.VoidPtr()
        };

        for (var index = 0; index < payload.Count; index++)
        {
            var entry = payload[index];
            var slotPtr = EmitClosureFieldPointer(
                closureValue,
                ClosurePayloadOffset + entry.Offset,
                $"stack_closure_slot_{index}");
            _currentBlock.Instructions.Add(new LlvmStore
            {
                Value = CoerceValueToType(entry.Value, entry.Type, $"stack_closure_payload_{index}"),
                Pointer = slotPtr
            });
        }

        return closureValue;
    }
}
