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
        var payload = coercedArguments
            .Select((argument, index) => new ClosurePayloadEntry(
                argument,
                fullSignature.ParameterTypes[index],
                boundArgumentManagedFlags.Count > index
                    ? boundArgumentManagedFlags[index]
                    : IsManagedRcPayloadValue(argument, fullSignature.ParameterTypes[index])))
            .ToList();

        // 计算可见签名（移除已捕获的参数）
        var visibleSignature = BuildRemainingSignature(fullSignature, coercedArguments.Count);

        // 合成 invoke thunk：接收 (closure_ptr, remaining_args...)，
        // 从 payload 加载捕获值，调用 directFunction(captured..., remaining...)
        var invokeThunk = SynthesizeDirectInvokeThunk(
            directFunction,
            fullSignature,
            visibleSignature,
            payload.Select(entry => entry.Type).ToList());

        // 合成 release thunk（对 RC payload 执行 decref）
        var releaseThunk = SynthesizeReleaseThunk(payload);

        // ── 栈分配闭包 buffer ──
        // Layout: [header(8)] [invoke_fn(8)] [release_fn(8)] [payload_word_count(8)] [payload...]
        // Total: 32 + payload_count * 8
        var totalBytes = (int)(ClosurePayloadOffset + payload.Count * 8L);

        var allocaType = new LlvmArrayType { Element = LlvmIntType.I8, Size = totalBytes };
        var alloca = new LlvmAlloca
        {
            AllocatedType = allocaType,
            ResultName = _nameMangler.NewTempName($"l{allocInfo.TargetLocal.Value}_closure_stack")
        };
        EmitAllocaInEntryBlock(alloca);

        var allocaRef = new LlvmInstructionRef
        {
            Instruction = alloca,
            Type = new LlvmPointerType { ElementType = allocaType }
        };

        // Bitcast to ptr (i8*) 以便统一字段访问
        var rawPtrCast = new LlvmCast
        {
            Op = WellKnownStrings.InternalNames.Bitcast,
            Value = allocaRef,
            TargetType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName($"l{allocInfo.TargetLocal.Value}_closure_ptr")
        };
        _currentBlock!.Instructions.Add(rawPtrCast);
        var basePtr = new LlvmInstructionRef
        {
            Instruction = rawPtrCast,
            Type = LlvmPointerType.VoidPtr()
        };

        // 存储 invoke_fn at offset 8（跳过 header）
        var invokeBitcast = new LlvmCast
        {
            Op = WellKnownStrings.InternalNames.Bitcast,
            Value = new LlvmGlobal
            {
                Name = invokeThunk.Name,
                Type = new LlvmPointerType
                {
                    ElementType = BuildFunctionTypeFromLlvmFunction(invokeThunk)
                }
            },
            TargetType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("stack_closure_invoke_ptr")
        };
        _currentBlock!.Instructions.Add(invokeBitcast);
        var invokePtrValue = new LlvmInstructionRef
        {
            Instruction = invokeBitcast,
            Type = LlvmPointerType.VoidPtr()
        };
        var invokeFieldPtr = EmitClosureFieldPointer(basePtr, ClosureInvokeOffset, "stack_invoke_field");
        _currentBlock!.Instructions.Add(new LlvmStore
        {
            Value = invokePtrValue,
            Pointer = invokeFieldPtr
        });

        // 存储 release_fn at offset 16（null 或 thunk bitcast）
        var releaseFieldPtr = EmitClosureFieldPointer(basePtr, ClosureReleaseOffset, "stack_release_field");
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
            _currentBlock!.Instructions.Add(releaseBitcast);
            var releasePtrValue = new LlvmInstructionRef
            {
                Instruction = releaseBitcast,
                Type = LlvmPointerType.VoidPtr()
            };
            _currentBlock!.Instructions.Add(new LlvmStore
            {
                Value = releasePtrValue,
                Pointer = releaseFieldPtr
            });
        }
        else
        {
            _currentBlock!.Instructions.Add(new LlvmStore
            {
                Value = LlvmNullPointer.Instance,
                Pointer = releaseFieldPtr
            });
        }

        // 存储 payload_word_count at offset 24
        var countFieldPtr = EmitClosureFieldPointer(basePtr, ClosurePayloadWordCountOffset, "stack_count_field");
        _currentBlock!.Instructions.Add(new LlvmStore
        {
            Value = new LlvmConstant { Value = (long)payload.Count, Type = LlvmIntType.I64 },
            Pointer = countFieldPtr
        });

        // 存储捕获值到 payload 区域（offset 32 + i*8）
        // 栈提升闭包跳过 incref：闭包不逃逸，其捕获值的生命周期由包含函数管理。
        // ConvertDrop 也跳过栈提升值的 decref，保持 incref/decref 对称。
        for (var index = 0; index < payload.Count; index++)
        {
            var entry = payload[index];

            var slotPtr = EmitClosureFieldPointer(
                basePtr,
                ClosurePayloadOffset + (index * 8L),
                $"stack_closure_slot_{index}");
            _currentBlock!.Instructions.Add(new LlvmStore
            {
                Value = CoerceValueToType(entry.Value, entry.Type, $"stack_closure_payload_{index}"),
                Pointer = slotPtr
            });
        }

        // 将 alloca 指针赋值给目标 local（作为 void ptr，与标准闭包路径一致）
        AssignPlaceFromValue(targetPlace, basePtr);

        return null;
    }
}
