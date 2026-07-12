using Eidosc.Diagnostic;
using Eidosc.Mir;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    /// <summary>
    /// Emit a direct function call from a runtime-word (i64) function pointer.
    /// Used inside handler branch functions where indirect calls receive raw function
    /// pointers as i64 machine words, not closure objects.
    /// </summary>
    private LlvmCall EmitDirectCallFromRuntimeWord(
        MirCall call,
        IReadOnlyList<LlvmValue> convertedArguments,
        LlvmFunctionType visibleSignature)
    {
        // Convert the function operand — in RuntimeWordAbi, locals are i64.
        var calleeWord = ConvertOperand(call.Function);

        // inttoptr i64 -> ptr (raw function pointer)
        var calleePtr = CoerceToPointer(calleeWord);

        // Bitcast the pointer to the typed function signature
        var typedFnPtr = new LlvmCast
        {
            Op = WellKnownStrings.InternalNames.Bitcast,
            Value = calleePtr,
            TargetType = new LlvmPointerType { ElementType = visibleSignature },
            ResultName = _nameMangler.NewTempName("dispatch_fn")
        };
        _currentBlock?.Instructions.Add(typedFnPtr);

        var targetPlace = call.Target as MirPlace;
        var resultName = targetPlace != null
            ? _nameMangler.NewTempName($"l{targetPlace.Local.Value}")
            : _nameMangler.NewTempName("dispatch_res");

        // Coerce arguments to match the visible signature (ptr for unresolved types)
        var coercedArgs = CoerceArgumentsForSignature(visibleSignature, convertedArguments);

        var returnType = _currentMirFunction?.IsRuntimeWordAbi == true
            ? (LlvmType)LlvmIntType.I64
            : visibleSignature.ReturnType;

        var directCall = new LlvmCall
        {
            Function = new LlvmInstructionRef
            {
                Instruction = typedFnPtr,
                Type = new LlvmPointerType { ElementType = visibleSignature }
            },
            Arguments = coercedArgs,
            ReturnType = returnType,
            ResultName = resultName,
            TailCallKind = SelectTailCallKind(call, returnType, coercedArgs)
        };

        if (targetPlace != null)
        {
            ClearGenericLocal(targetPlace.Local);
            _locals.RuntimeWordLocals.Add(targetPlace.Local);
            _locals.LocalMap[targetPlace.Local] = new LlvmLocal
            {
                Name = resultName,
                Type = returnType
            };
        }

        return directCall;
    }

    /// <summary>
    /// 处理 cfn_from(func) 调用：将 Eidos 函数引用转为 C 函数指针 (RawPtr)。
    /// 零捕获函数：直接将函数指针 bitcast 为 ptr。
    /// 闭包值：从闭包对象中提取 invoke_fn 字段。
    /// </summary>
    private LlvmCall? ConvertCfnFromCall(MirCall call)
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

        // 情况 1：参数是直接的函数引用（零捕获函数）
        if (call.Arguments[0] is MirFunctionRef funcRef)
        {
            var funcGlobal = ConvertFunctionRef(funcRef);

            // bitcast 函数指针为 ptr (void*)
            var bitcast = new LlvmCast
            {
                Op = WellKnownStrings.InternalNames.Bitcast,
                Value = funcGlobal,
                TargetType = LlvmPointerType.VoidPtr(),
                ResultName = resultName
            };
            _currentBlock?.Instructions.Add(bitcast);

            ClearGenericLocal(targetPlace.Local);
            _locals.LocalMap[targetPlace.Local] = new LlvmLocal
            {
                Name = resultName,
                Type = LlvmPointerType.VoidPtr()
            };

            // 返回 null 表示不生成 LlvmCall（已通过 bitcast 内联处理）
            return null;
        }

        // 情况 2：参数是闭包值（带捕获）— 当前不支持 trampoline 合成，报错
        // 原因：invoke_fn 需要 closure_ptr 作为第一参数，但 C 回调不会传入
        Diagnostics.Add(Diagnostic.Diagnostic.Error(
            DiagnosticMessages.CfnFromCapturedClosureUnsupported,
            "E3053"));

        var closureValue = ConvertOperand(call.Arguments[0]);

        // 加载 invoke_fn：GEP offset 8 (skip header), load
        var invokeFnPtr = new LlvmGetElementPtr
        {
            Pointer = CoerceToPointer(closureValue),
            ElementType = LlvmIntType.I8,
            Index = new LlvmConstant { Value = "8", Type = LlvmIntType.I64 },
            ResultName = _nameMangler.NewTempName("cfn_invoke_ptr")
        };
        _currentBlock?.Instructions.Add(invokeFnPtr);

        var loadInvokeFn = new LlvmLoad
        {
            Pointer = new LlvmInstructionRef
            {
                Instruction = invokeFnPtr,
                Type = LlvmPointerType.VoidPtr()
            },
            LoadType = LlvmPointerType.VoidPtr(),
            ResultName = resultName
        };
        _currentBlock?.Instructions.Add(loadInvokeFn);

        ClearGenericLocal(targetPlace.Local);
        _locals.LocalMap[targetPlace.Local] = new LlvmLocal
        {
            Name = resultName,
            Type = LlvmPointerType.VoidPtr()
        };

        return null;
    }
}
