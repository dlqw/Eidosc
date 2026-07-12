using Eidosc.Symbols;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    private LlvmCall? ConvertCall(MirCall call)
    {
        // Reuse 优化：如果此构造器调用有 alloc_reuse 提示，直接内联分配
        if (_currentReuseHints != null && _currentBlockId.HasValue &&
            call.Function is MirFunctionRef { Name: var ctorName } ctorRef &&
            _currentReuseHints.AllocReuseSites.TryGetValue(
                (_currentBlockId.Value, _currentInstructionIndex), out var reuseSlot))
        {
            return ConvertConstructorCallWithReuse(call, ctorRef, reuseSlot);
        }

        // Stack Promotion 优化：如果此构造器调用可以提升到栈，用 alloca 替代 eidos_alloc
        if (_currentStackPromotionHints != null && _currentBlockId.HasValue &&
            _currentStackPromotionHints.StackAllocSites.Contains(
                (_currentBlockId.Value, _currentInstructionIndex)) &&
            call.Function is MirFunctionRef { Name: var stackCtorName })
        {
            return ConvertConstructorCallWithStackPromo(call, stackCtorName);
        }

        if (call.Function is MirFunctionRef { Name: var inlineCtorName } inlineCtorRef &&
            TypeSemantics.IsAdtConstructorCall(inlineCtorRef))
        {
            return ConvertConstructorCallInline(call, inlineCtorRef);
        }

        // Unified Stack Promotion：闭包创建的栈分配（alloca 替代 eidos_closure_new）
        if (_currentUnifiedHints != null
            && call.Function is MirFunctionRef { Name: var closureFuncName }
            && call.Target is MirPlace { Kind: PlaceKind.Local, Local: var closureTargetLocal }
            && _currentUnifiedHints.AllocInfoByLocal.TryGetValue(closureTargetLocal, out var closureAlloc)
            && closureAlloc.Kind == PromotableAllocationKind.Closure)
        {
            return ConvertClosureCallWithStackPromo(call, closureAlloc, closureFuncName);
        }

        if (call.Function is MirFunctionRef arrayNewRef &&
            IsArrayIntrinsicCall(arrayNewRef, WellKnownStrings.InternalNames.ArrayNew))
        {
            return ConvertRuntimeArrayNewCall(call);
        }

        if (call.Function is MirFunctionRef arrayPushRef &&
            IsArrayIntrinsicCall(arrayPushRef, WellKnownStrings.InternalNames.ArrayPush))
        {
            return ConvertRuntimeArrayPushCall(call);
        }

        if (call.Function is MirFunctionRef arrayExtendRef &&
            IsArrayIntrinsicCall(arrayExtendRef, WellKnownStrings.InternalNames.ArrayExtend))
        {
            return ConvertRuntimeArrayExtendCall(call);
        }

        if (call.Function is MirFunctionRef arrayPopRef &&
            IsArrayIntrinsicCall(arrayPopRef, WellKnownStrings.InternalNames.ArrayPop))
        {
            return ConvertRuntimeArrayPopCall(call);
        }

        if (call.Function is MirFunctionRef arraySwapRef &&
            IsArrayIntrinsicCall(arraySwapRef, WellKnownStrings.InternalNames.ArraySwap))
        {
            return ConvertRuntimeArraySwapCall(call);
        }

        // array_set(array, index, value, size_hint) -> Unit : in-place element write.
        if (call.Function is MirFunctionRef arraySetRef &&
            IsArrayIntrinsicCall(arraySetRef, WellKnownStrings.InternalNames.ArraySet))
        {
            return ConvertRuntimeArraySetCall(call);
        }

        if (call.Function is MirFunctionRef { TraitMethodRole: TraitMethodRole.Show } &&
            TryConvertBuiltinShowCall(call, out var builtinShowCall))
        {
            return builtinShowCall;
        }

        // FFI: cfn_from(func) — 将函数引用转为 C 函数指针 (RawPtr)
        if (call.Function is MirFunctionRef cfnFromRef &&
            TryGetBuiltinIntrinsicName(cfnFromRef, "cfn_from", out _))
        {
            return ConvertCfnFromCall(call);
        }

        // FFI: cfn_call(fn_ptr, arg1, arg2, ...) — 通过 C 函数指针调用
        if (call.Function is MirFunctionRef cfnCallRef &&
            TryGetBuiltinIntrinsicName(cfnCallRef, "cfn_call", out _))
        {
            return ConvertCfnCall(call);
        }

        // FFI: ptr_add(ptr, offset) — 指针字节偏移
        if (call.Function is MirFunctionRef ptrAddRef &&
            TryGetBuiltinIntrinsicName(ptrAddRef, "ptr_add", out _))
        {
            return ConvertPtrAdd(call);
        }

        if (call.Function is MirFunctionRef valueBoxRef &&
            TryGetBuiltinIntrinsicName(valueBoxRef, WellKnownStrings.InternalNames.ValueBox, out _))
        {
            return ConvertValueBox(call);
        }

        if (call.Function is MirFunctionRef valueUnboxRef &&
            TryGetBuiltinIntrinsicName(valueUnboxRef, WellKnownStrings.InternalNames.ValueUnbox, out _))
        {
            return ConvertValueUnbox(call);
        }

        if (call.Function is MirFunctionRef valueBoxFreeRef &&
            TryGetBuiltinIntrinsicName(valueBoxFreeRef, WellKnownStrings.InternalNames.ValueBoxFree, out _))
        {
            return ConvertValueBoxFree(call);
        }

        if (call.Function is MirFunctionRef sharedNewRef &&
            TryGetBuiltinIntrinsicName(sharedNewRef, WellKnownStrings.InternalNames.SharedNew, out _))
        {
            return ConvertSharedNew(call);
        }

        if (call.Function is MirFunctionRef sharedBorrowRef &&
            TryGetBuiltinIntrinsicName(sharedBorrowRef, WellKnownStrings.InternalNames.SharedBorrow, out _))
        {
            return ConvertSharedBorrow(call);
        }

        if (call.Function is MirFunctionRef sharedCloneRef &&
            TryGetBuiltinIntrinsicName(sharedCloneRef, WellKnownStrings.InternalNames.SharedClone, out _))
        {
            return ConvertSharedClone(call);
        }

        if (call.Function is MirFunctionRef sharedPtrEqRef &&
            TryGetBuiltinIntrinsicName(sharedPtrEqRef, WellKnownStrings.InternalNames.SharedPtrEq, out _))
        {
            return ConvertSharedPtrEq(call);
        }

        // 浮点数学 LLVM intrinsics
        if (call.Function is MirFunctionRef mathRef &&
            MirBuiltinFunctions.TryGetIntrinsicName(mathRef, out var mathName) &&
            MirBuiltinFunctions.IsMathIntrinsicName(mathName))
        {
            return ConvertMathIntrinsic(call, mathName);
        }

        if (call.Function is MirFunctionRef charConversionRef &&
            (TryGetBuiltinIntrinsicName(charConversionRef, "char_from_code", out _) ||
             TryGetBuiltinIntrinsicName(charConversionRef, "char_to_code", out _)))
        {
            return ConvertRuntimeWordNoOpConversion(call);
        }

        // FFI: ptr_load_int(ptr) — 从指针加载 i64
        if (call.Function is MirFunctionRef ptrLoadIntRef &&
            TryGetBuiltinIntrinsicName(ptrLoadIntRef, WellKnownStrings.InternalNames.PtrLoadInt, out _))
        {
            return ConvertPtrLoadInt(call);
        }

        // FFI: ptr_store_int(ptr, value) — 向指针存储 i64
        if (call.Function is MirFunctionRef ptrStoreIntRef &&
            TryGetBuiltinIntrinsicName(ptrStoreIntRef, WellKnownStrings.InternalNames.PtrStoreInt, out _))
        {
            return ConvertPtrStoreInt(call);
        }

        // FFI: 类型化指针读取（ptr_load_float, ptr_load_ptr, ptr_load_i32, ptr_load_i8, ptr_load_bool）
        if (call.Function is MirFunctionRef ptrLoadRef &&
            MirBuiltinFunctions.TryGetIntrinsicName(ptrLoadRef, out var loadName) &&
            MirBuiltinFunctions.IsPointerLoadIntrinsicName(loadName) &&
            loadName != WellKnownStrings.InternalNames.PtrLoadInt)
        {
            return ConvertTypedPtrLoad(call, loadName);
        }

        // FFI: 类型化指针写入（ptr_store_float, ptr_store_ptr, ptr_store_i32, ptr_store_i8）
        if (call.Function is MirFunctionRef ptrStoreRef &&
            MirBuiltinFunctions.TryGetIntrinsicName(ptrStoreRef, out var storeName) &&
            MirBuiltinFunctions.IsPointerStoreIntrinsicName(storeName) &&
            storeName != WellKnownStrings.InternalNames.PtrStoreInt)
        {
            return ConvertTypedPtrStore(call, storeName);
        }

        // @cstruct 字段访问器（getter/setter）
        if (call.Function is MirFunctionRef { Name: var csName } && _cstructAccessors.TryGetValue(csName, out var csInfo))
        {
            return ConvertCStructAccessor(call, csInfo);
        }

        if (TryConvertCallUsingPartialState(call, out var partialCall))
        {
            if (partialCall == null &&
                call.Arguments.Count == 0 &&
                call.Function is MirPlace { Kind: PlaceKind.Local } localPartial &&
                IsGenericLocal(localPartial.Local))
            {
                ReportUnresolvedGenericCall(call, localPartial.Local);
            }

            return partialCall;
        }

        if (call.Function is MirPlace { Kind: PlaceKind.Local } localIndirect &&
            IsGenericLocal(localIndirect.Local))
        {
            ReportUnresolvedGenericCall(call, localIndirect.Local);
        }

        if (call.Function is not MirFunctionRef &&
            TryResolveIndirectClosureSignature(call, out var indirectSignature))
        {
            var indirectArguments = call.Arguments.Select(ConvertOperand).ToList();
            if (TryRecordPartialCall(call, indirectArguments))
            {
                return null;
            }

            // In handler branch functions (RuntimeWordAbi), indirect calls use
            // direct function-pointer convention (no closure dereference).
            // The function operand is an i64 machine word that must be inttoptr'd.
            if (_currentMirFunction?.IsRuntimeWordAbi == true)
            {
                return EmitDirectCallFromRuntimeWord(call, indirectArguments, indirectSignature);
            }

            var closureValue = ConvertOperand(call.Function);
            if (UsesDirectCallableAbi(closureValue))
            {
                var directCallArguments = CoerceCallArguments(closureValue, indirectArguments);
                return EmitDirectCall(call, closureValue, directCallArguments, indirectSignature.ReturnType);
            }

            // Opaque pointer (e.g., from closure materialization) must go through
            // EmitClosureInvokeCall which properly dereferences the closure object
            // to load the invoke function pointer.  Treating the closure pointer
            // itself as a function pointer would attempt to execute heap memory.
            if (TryResolveClosureValueTypeId(call.Function, out var closureTypeId))
            {
                return EmitClosureValueCall(call, closureValue, indirectArguments, closureTypeId, indirectSignature);
            }

            return EmitClosureInvokeCall(call, closureValue, indirectArguments, indirectSignature);
        }

        var arguments = call.Arguments.Select(ConvertOperand).ToList();
        if (TryRecordPartialCall(call, arguments))
        {
            if (call.Arguments.Count == 0 &&
                call.Function is MirFunctionRef zeroArgPartial &&
                IsGenericFunctionReference(zeroArgPartial))
            {
                ReportUnresolvedGenericCall(call, zeroArgPartial);
            }

            return null;
        }

        if (call.Function is MirFunctionRef functionRef)
        {
            ReportUnresolvedGenericCall(call, functionRef);
        }
        else if (call.Function is MirPlace { Kind: PlaceKind.Local } localFunction &&
                 IsGenericLocal(localFunction.Local))
        {
            ReportUnresolvedGenericCall(call, localFunction.Local);
        }

        var argumentsWithExpectedFunctionTypes = RewriteFunctionValueArgumentsForDirectCall(call, arguments);
        var funcValue = ResolveCallTargetValue(call, argumentsWithExpectedFunctionTypes, out var returnType);
        var coercedArguments = CoerceCallArguments(funcValue, argumentsWithExpectedFunctionTypes);
        return EmitDirectCall(call, funcValue, coercedArguments, returnType);
    }

    private bool TryConvertBuiltinShowCall(MirCall call, out LlvmCall? loweredCall)
    {
        loweredCall = null;

        if (call.Arguments.Count != 1)
        {
            return false;
        }

        var argument = call.Arguments[0];
        var argumentValue = ConvertOperand(argument);
        LlvmCall? builtinCall = argument.TypeId.Value switch
        {
            BaseTypes.IntId => EmitBuiltinShowRuntimeCall(call, argumentValue, WellKnownStrings.Runtime.IntToString, LlvmIntType.I64),
            BaseTypes.CharId => EmitBuiltinShowRuntimeCall(call, argumentValue, WellKnownStrings.Runtime.StringFromChar, LlvmIntType.I64),
            BaseTypes.BoolId => EmitBuiltinShowBoolCall(call, argumentValue),
            BaseTypes.StringId => EmitBuiltinShowStringCall(call, argumentValue),
            _ => null
        };

        if (builtinCall == null)
        {
            return false;
        }

        loweredCall = builtinCall;
        return true;
    }

    private LlvmCall EmitBuiltinShowRuntimeCall(
        MirCall call,
        LlvmValue argument,
        string runtimeName,
        LlvmType parameterType)
    {
        var runtimeCall = new LlvmCall
        {
            Function = new LlvmGlobal
            {
                Name = runtimeName,
                Type = new LlvmFunctionType
                {
                    ReturnType = LlvmPointerType.VoidPtr(),
                    ParameterTypes = [parameterType]
                }
            },
            Arguments = [CoerceValueToType(argument, parameterType, "show_builtin_arg")],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = call.Target is MirPlace target
                ? _nameMangler.NewTempName($"l{target.Local.Value}")
                : _nameMangler.NewTempName(WellKnownStrings.InternalNames.Show)
        };

        if (call.Target is MirPlace { Kind: PlaceKind.Local } targetLocal)
        {
            _partialCallStates.Remove(targetLocal.Local);
            ClearGenericLocal(targetLocal.Local);
            _locals.RuntimeWordLocals.Remove(targetLocal.Local);
            _locals.LocalMap[targetLocal.Local] = new LlvmLocal
            {
                Name = runtimeCall.ResultName!,
                Type = LlvmPointerType.VoidPtr()
            };
        }

        return runtimeCall;
    }

    private LlvmCall EmitBuiltinShowStringCall(MirCall call, LlvmValue argument)
    {
        var lengthCall = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.StringLength,
                LlvmIntType.I64,
                [LlvmPointerType.VoidPtr()]),
            Arguments = [argument],
            ReturnType = LlvmIntType.I64,
            ResultName = _nameMangler.NewTempName("show_string_len")
        };
        _currentBlock?.Instructions.Add(lengthCall);

        var runtimeCall = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.StringSlice,
                LlvmPointerType.VoidPtr(),
                [LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64]),
            Arguments =
            [
                argument,
                new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 },
                new LlvmInstructionRef { Instruction = lengthCall, Type = LlvmIntType.I64 }
            ],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = call.Target is MirPlace target
                ? _nameMangler.NewTempName($"l{target.Local.Value}")
                : _nameMangler.NewTempName("show_string")
        };

        if (call.Target is MirPlace { Kind: PlaceKind.Local } targetLocal)
        {
            _partialCallStates.Remove(targetLocal.Local);
            ClearGenericLocal(targetLocal.Local);
            _locals.RuntimeWordLocals.Remove(targetLocal.Local);
            _locals.LocalMap[targetLocal.Local] = new LlvmLocal
            {
                Name = runtimeCall.ResultName!,
                Type = LlvmPointerType.VoidPtr()
            };
        }

        return runtimeCall;
    }

    private LlvmCall EmitBuiltinShowBoolCall(MirCall call, LlvmValue argument)
    {
        var condition = CoerceValueToType(argument, LlvmIntType.I1, "show_bool_cond");
        var trueCstr = CreateCStringPointer(WellKnownStrings.AdditionalKeywords.True, "show_true");
        var falseCstr = CreateCStringPointer(WellKnownStrings.AdditionalKeywords.False, "show_false");
        var select = new LlvmSelect
        {
            Condition = condition,
            TrueValue = trueCstr,
            FalseValue = falseCstr,
            ResultName = _nameMangler.NewTempName("show_bool_cstr")
        };
        _currentBlock?.Instructions.Add(select);

        var runtimeCall = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.StringFromCstr,
                LlvmPointerType.VoidPtr(),
                [LlvmPointerType.VoidPtr()]),
            Arguments =
            [
                new LlvmInstructionRef
                {
                    Instruction = select,
                    Type = LlvmPointerType.VoidPtr()
                }
            ],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = call.Target is MirPlace target
                ? _nameMangler.NewTempName($"l{target.Local.Value}")
                : _nameMangler.NewTempName("show_bool")
        };

        if (call.Target is MirPlace { Kind: PlaceKind.Local } targetLocal)
        {
            _partialCallStates.Remove(targetLocal.Local);
            ClearGenericLocal(targetLocal.Local);
            _locals.RuntimeWordLocals.Remove(targetLocal.Local);
            _locals.LocalMap[targetLocal.Local] = new LlvmLocal
            {
                Name = runtimeCall.ResultName!,
                Type = LlvmPointerType.VoidPtr()
            };
        }

        return runtimeCall;
    }

    private List<LlvmValue> RewriteFunctionValueArgumentsForDirectCall(MirCall call, IReadOnlyList<LlvmValue> convertedArguments)
    {
        if (call.Function is not MirFunctionRef funcRef ||
            !TryResolveMirFunction(funcRef, out var callee))
        {
            return convertedArguments.ToList();
        }

        var parameterLocals = callee.Locals
            .Where(local => local.IsParameter)
            .ToList();
        if (parameterLocals.Count == 0)
        {
            return convertedArguments.ToList();
        }

        var rewrittenArguments = new List<LlvmValue>(convertedArguments.Count);
        for (var index = 0; index < convertedArguments.Count; index++)
        {
            if (index < parameterLocals.Count &&
                call.Arguments[index] is MirFunctionRef argumentFunctionRef &&
                parameterLocals[index].TypeId.IsValid)
            {
                rewrittenArguments.Add(MaterializeFunctionReference(argumentFunctionRef, parameterLocals[index].TypeId));
                continue;
            }

            rewrittenArguments.Add(convertedArguments[index]);
        }

        return rewrittenArguments;
    }

    private bool TryResolveMirFunction(MirFunctionRef funcRef, out MirFunc function)
    {
        if (funcRef.SymbolId.IsValid &&
            _funcCache.MirFunctionBySymbol.TryGetValue(funcRef.SymbolId, out var bySymbol))
        {
            function = bySymbol;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(funcRef.Name) &&
            _funcCache.MirFunctionByName.TryGetValue(funcRef.Name, out var byName))
        {
            function = byName;
            return true;
        }

        function = null!;
        return false;
    }

    private bool TryConvertCallUsingPartialState(MirCall call, out LlvmCall? loweredCall)
    {
        loweredCall = null;

        if (call.Function is not MirPlace { Kind: PlaceKind.Local } functionPlace ||
            !_partialCallStates.TryGetValue(functionPlace.Local, out var partial))
        {
            return false;
        }

        var newArguments = call.Arguments.Select(ConvertOperand).ToList();
        var newArgumentManagedFlags = newArguments
            .Select((argument, index) => IsManagedRcPayloadValue(call.Arguments[index], argument, argument.Type))
            .ToList();
        var combinedArguments = new List<LlvmValue>(partial.BoundArguments.Count + newArguments.Count);
        combinedArguments.AddRange(partial.BoundArguments);
        combinedArguments.AddRange(newArguments);
        var combinedManagedFlags = new List<bool>(partial.BoundArgumentManagedFlags.Count + newArgumentManagedFlags.Count);
        combinedManagedFlags.AddRange(partial.BoundArgumentManagedFlags);
        combinedManagedFlags.AddRange(newArgumentManagedFlags);

        var coercedCombined = CoerceArgumentsForSignature(partial.Signature, combinedArguments);
        var expectedParameterCount = partial.Signature.ParameterTypes.Count;
        if (coercedCombined.Count < expectedParameterCount)
        {
            if (call.Target is MirPlace { Kind: PlaceKind.Local } targetLocal)
            {
                _partialCallStates[targetLocal.Local] = new PartialCallState(
                    partial.Function,
                    partial.Signature,
                    coercedCombined,
                    combinedManagedFlags,
                    partial.CapturedArgumentCount,
                    partial.VisibleSignature);
                _locals.LocalMap.Remove(targetLocal.Local);
                _locals.RuntimeWordLocals.Remove(targetLocal.Local);
                if (IsGenericLocal(functionPlace.Local))
                {
                    SetGenericLocal(targetLocal.Local, expectedParameterCount - coercedCombined.Count);
                }
                else
                {
                    ClearGenericLocal(targetLocal.Local);
                }
            }

            return true;
        }

        var appliedArguments = coercedCombined.Take(expectedParameterCount).ToList();
        loweredCall = UsesDirectCallableAbi(partial.Function)
            ? EmitDirectCall(
                call,
                partial.Function,
                appliedArguments,
                partial.Signature.ReturnType)
            : EmitClosureInvokeCall(
                call,
                partial.Function,
                appliedArguments,
                partial.Signature);
        return true;
    }

    private static bool UsesDirectCallableAbi(LlvmValue callee)
    {
        return callee switch
        {
            LlvmGlobal { Type: LlvmFunctionType } => true,
            LlvmGlobal { Type: LlvmPointerType { ElementType: LlvmFunctionType } } => true,
            LlvmLocal { Type: LlvmFunctionType } => true,
            LlvmLocal { Type: LlvmPointerType { ElementType: LlvmFunctionType } } => true,
            LlvmInstructionRef { Type: LlvmFunctionType } => true,
            LlvmInstructionRef { Type: LlvmPointerType { ElementType: LlvmFunctionType } } => true,
            _ => false
        };
    }

    private bool TryResolveIndirectClosureSignature(MirCall call, out LlvmFunctionType functionType)
    {
        if (TryResolveCallableSignature(call.Function, out functionType))
        {
            return true;
        }

        var argumentTypes = call.Arguments
            .Select(argument => NormalizeParameterType(
                TypeLowering.NormalizeStorageType(LowerTypeIdOrReport(argument.TypeId, "indirect closure argument"))))
            .ToList();
        if (argumentTypes.Count == 0 && call.Target is not MirPlace { TypeId: { IsValid: true } })
        {
            functionType = default!;
            return false;
        }

        functionType = new LlvmFunctionType
        {
            ReturnType = call.Target is MirPlace target && target.TypeId.IsValid
                ? NormalizeSignatureReturnType(LowerTypeIdOrReport(target.TypeId, "indirect closure result"))
                : LlvmVoidType.Instance,
            ParameterTypes = argumentTypes
        };
        return true;
    }

    private bool TryRecordPartialCall(MirCall call, IReadOnlyList<LlvmValue> arguments)
    {
        if (call.Target is not MirPlace { Kind: PlaceKind.Local } targetLocal)
        {
            return false;
        }

        var visibleSignature = TryResolvePartialResultVisibleSignature(call, targetLocal.TypeId, arguments.Count);
        if (visibleSignature != null &&
            call.Function is MirFunctionRef functionRef &&
            !string.IsNullOrWhiteSpace(functionRef.Name) &&
            !TryGetExternalFfiSymbolName(functionRef.Name, functionRef.SymbolId, out _) &&
            !TryGetRuntimeFunctionType(functionRef, out _, out _))
        {
            var expandedParameterTypes = arguments
                .Select(argument => argument.Type)
                .Concat(visibleSignature.ParameterTypes)
                .ToList();
            var expandedFunctionType = new LlvmFunctionType
            {
                ReturnType = visibleSignature.ReturnType,
                ParameterTypes = expandedParameterTypes
            };

            if (arguments.Count < expandedFunctionType.ParameterTypes.Count &&
                TryResolveFunctionInstanceNameForPartial(functionRef.Name, expandedFunctionType, out var expandedLlvmName))
            {
                var expandedBoundArgumentManagedFlags = arguments
                    .Select((argument, index) => IsManagedRcPayloadValue(call.Arguments[index], argument, argument.Type))
                    .ToList();
                var expandedBoundArguments = CoerceArgumentsForSignature(expandedFunctionType, arguments);
                var expandedCapturedArgumentCount = ResolveCapturedArgumentCount(call.Function, expandedFunctionType);
                _partialCallStates[targetLocal.Local] = new PartialCallState(
                    new LlvmGlobal
                    {
                        Name = expandedLlvmName,
                        Type = expandedFunctionType
                    },
                    expandedFunctionType,
                    expandedBoundArguments,
                    expandedBoundArgumentManagedFlags,
                    expandedCapturedArgumentCount,
                    visibleSignature);
                _locals.LocalMap.Remove(targetLocal.Local);
                _locals.RuntimeWordLocals.Remove(targetLocal.Local);
                SetGenericLocal(targetLocal.Local, expandedFunctionType.ParameterTypes.Count - arguments.Count);
                return true;
            }
        }

        if (!TryResolveCallableSignature(call.Function, out var functionType) ||
            arguments.Count >= functionType.ParameterTypes.Count)
        {
            return false;
        }

        var callee = call.Function switch
        {
            MirFunctionRef directFunctionRef => new LlvmGlobal
            {
                Name = ResolveFunctionLlvmName(directFunctionRef, functionType),
                Type = functionType
            },
            _ => ConvertOperand(call.Function)
        };
        var boundArgumentManagedFlags = arguments
            .Select((argument, index) => IsManagedRcPayloadValue(call.Arguments[index], argument, argument.Type))
            .ToList();
        var boundArguments = CoerceArgumentsForSignature(functionType, arguments);
        var capturedArgumentCount = ResolveCapturedArgumentCount(call.Function, functionType);
        _partialCallStates[targetLocal.Local] = new PartialCallState(
            callee,
            functionType,
            boundArguments,
            boundArgumentManagedFlags,
            capturedArgumentCount,
            VisibleSignature: null);
        _locals.LocalMap.Remove(targetLocal.Local);
        _locals.RuntimeWordLocals.Remove(targetLocal.Local);

        if (call.Function is MirFunctionRef funcRef && IsGenericFunctionReference(funcRef))
        {
            SetGenericLocal(targetLocal.Local, functionType.ParameterTypes.Count - arguments.Count);
        }
        else if (call.Function is MirPlace { Kind: PlaceKind.Local } localFunction &&
                 IsGenericLocal(localFunction.Local))
        {
            SetGenericLocal(targetLocal.Local, functionType.ParameterTypes.Count - arguments.Count);
        }
        else
        {
            ClearGenericLocal(targetLocal.Local);
        }

        return true;
    }

    private bool TryResolveFunctionInstanceNameForPartial(
        string sourceName,
        LlvmFunctionType functionType,
        out string llvmName)
    {
        if (TryResolveFunctionInstanceNameBySignature(sourceName, functionType, out llvmName))
        {
            return true;
        }

        if (TryResolveFunctionInstanceNameByRegisteredType(sourceName, functionType, out llvmName))
        {
            return true;
        }

        return TryResolveFunctionInstanceNameByLlvmName(sourceName, functionType, out llvmName);
    }

    private LlvmFunctionType? TryResolvePartialResultVisibleSignature(
        MirCall call,
        TypeId targetTypeId,
        int boundArgumentCount)
    {
        if (targetTypeId.IsValid &&
            TryResolveClosureValueSignature(targetTypeId, out var targetVisibleSignature))
        {
            return targetVisibleSignature;
        }

        if (call.Function is not MirFunctionRef functionRef ||
            !TryResolveSourceVisibleSignature(GetFunctionReferenceSignatureTypeId(functionRef), out var calleeVisibleSignature) ||
            boundArgumentCount >= calleeVisibleSignature.ParameterTypes.Count)
        {
            return null;
        }

        return new LlvmFunctionType
        {
            ReturnType = calleeVisibleSignature.ReturnType,
            ParameterTypes = calleeVisibleSignature.ParameterTypes
                .Skip(boundArgumentCount)
                .ToList()
        };
    }

    private int ResolveCapturedArgumentCount(MirOperand functionOperand, LlvmFunctionType loweredFunctionType)
    {
        return functionOperand switch
        {
            MirFunctionRef functionRef when TryResolveSourceVisibleSignature(GetFunctionReferenceSignatureTypeId(functionRef), out var sourceVisibleType) =>
                Math.Max(0, loweredFunctionType.ParameterTypes.Count - sourceVisibleType.ParameterTypes.Count),
            MirPlace { Kind: PlaceKind.Local } localPlace when _partialCallStates.TryGetValue(localPlace.Local, out var partial) =>
                partial.CapturedArgumentCount,
            _ => 0
        };
    }

    private bool TryResolveSourceVisibleSignature(TypeId typeId, out LlvmFunctionType functionType)
    {
        if (!typeId.IsValid ||
            !_typeLowering.TryGetFunctionSignature(typeId, out var parameterTypeIds, out var resultTypeId))
        {
            functionType = default!;
            return false;
        }

        functionType = new LlvmFunctionType
        {
            ReturnType = NormalizeSignatureReturnType(LowerTypeIdOrReport(resultTypeId, "closure callable result")),
            ParameterTypes = parameterTypeIds
                .Select(typeId => TypeLowering.NormalizeStorageType(LowerTypeIdOrReport(typeId, "closure callable parameter")))
                .Select(NormalizeParameterType)
                .ToList()
        };
        return true;
    }

    private static TypeId GetFunctionReferenceSignatureTypeId(MirFunctionRef functionRef)
    {
        return functionRef.SignatureTypeId.IsValid
            ? functionRef.SignatureTypeId
            : functionRef.TypeId;
    }

    private bool TryResolveClosureValueSignature(TypeId typeId, out LlvmFunctionType functionType)
    {
        if (!typeId.IsValid ||
            !_typeLowering.TryGetDirectFunctionSignature(typeId, out var parameterTypeIds, out var resultTypeId))
        {
            return TryResolveSourceVisibleSignature(typeId, out functionType);
        }

        functionType = new LlvmFunctionType
        {
            ReturnType = NormalizeSignatureReturnType(LowerTypeIdOrReport(resultTypeId, "source-visible callable result")),
            ParameterTypes = parameterTypeIds
                .Select(typeId => TypeLowering.NormalizeStorageType(LowerTypeIdOrReport(typeId, "source-visible callable parameter")))
                .Select(NormalizeParameterType)
                .ToList()
        };
        return true;
    }

    private bool TryResolveCallableSignature(MirOperand functionOperand, out LlvmFunctionType functionType)
    {
        functionType = default!;

        switch (functionOperand)
        {
            case MirFunctionRef funcRef:
                functionType = ResolveFunctionType(funcRef)!;
                return functionType != null;

            case MirPlace localPlace when TryResolveClosureValueSignature(localPlace.TypeId, out functionType):
                return true;

            case MirPlace { Kind: PlaceKind.Local } localPlace when
                _locals.LocalTypeById.TryGetValue(localPlace.Local, out var localTypeId) &&
                TryResolveClosureValueSignature(localTypeId, out functionType):
                return true;

            case MirPlace { Kind: PlaceKind.Local } localPlace when _locals.LocalMap.TryGetValue(localPlace.Local, out var localValue):
                return TryExtractFunctionSignature(localValue.Type, out functionType);

            case MirPlace place:
                var placeTypeId = ResolvePlaceTypeId(place);
                return placeTypeId.IsValid &&
                       TryResolveClosureValueSignature(placeTypeId, out functionType);

            default:
                return false;
        }
    }

    private bool TryResolveClosureValueTypeId(MirOperand functionOperand, out TypeId typeId)
    {
        typeId = TypeId.None;

        switch (functionOperand)
        {
            case MirPlace place when place.TypeId.IsValid:
                typeId = place.TypeId;
                return true;

            case MirPlace { Kind: PlaceKind.Local } localPlace when
                _locals.LocalTypeById.TryGetValue(localPlace.Local, out var localTypeId) &&
                localTypeId.IsValid:
                typeId = localTypeId;
                return true;

            case MirPlace place:
                var resolvedTypeId = ResolvePlaceTypeId(place);
                if (!resolvedTypeId.IsValid)
                {
                    return false;
                }

                typeId = resolvedTypeId;
                return true;

            default:
                return false;
        }
    }

    private LlvmCall EmitClosureValueCall(
        MirCall call,
        LlvmValue closureValue,
        IReadOnlyList<LlvmValue> arguments,
        TypeId closureTypeId,
        LlvmFunctionType initialSignature)
    {
        if (!TryBuildClosureValueCallPlan(closureTypeId, initialSignature, arguments.Count, out var signatures))
        {
            return EmitClosureInvokeCall(call, closureValue, arguments, initialSignature);
        }

        var currentClosure = closureValue;
        var nextArgumentIndex = 0;

        for (var signatureIndex = 0; signatureIndex < signatures.Count; signatureIndex++)
        {
            var currentSignature = signatures[signatureIndex];
            var parameterCount = currentSignature.ParameterTypes.Count;
            if (parameterCount == 0)
            {
                return EmitClosureInvokeCall(call, currentClosure, arguments.Skip(nextArgumentIndex).ToList(), currentSignature);
            }

            var argumentCount = Math.Min(parameterCount, arguments.Count - nextArgumentIndex);
            var callArguments = arguments
                .Skip(nextArgumentIndex)
                .Take(argumentCount)
                .ToList();
            var isFinalCall = signatureIndex == signatures.Count - 1;
            var invokeCall = EmitClosureInvokeCall(
                isFinalCall ? call : call with { Target = null, Arguments = [] },
                currentClosure,
                callArguments,
                currentSignature);

            if (isFinalCall)
            {
                return invokeCall;
            }

            _currentBlock?.Instructions.Add(invokeCall);

            currentClosure = new LlvmInstructionRef
            {
                Instruction = invokeCall,
                Type = currentSignature.ReturnType
            };
            nextArgumentIndex += argumentCount;
        }

        return EmitClosureInvokeCall(call, currentClosure, [], initialSignature);
    }

    private bool TryBuildClosureValueCallPlan(
        TypeId closureTypeId,
        LlvmFunctionType initialSignature,
        int argumentCount,
        out List<LlvmFunctionType> signatures)
    {
        signatures = [initialSignature];
        var currentTypeId = closureTypeId;
        var currentSignature = initialSignature;
        var consumedArgumentCount = 0;

        while (consumedArgumentCount < argumentCount)
        {
            var parameterCount = currentSignature.ParameterTypes.Count;
            if (parameterCount == 0)
            {
                return false;
            }

            consumedArgumentCount += Math.Min(parameterCount, argumentCount - consumedArgumentCount);
            if (consumedArgumentCount >= argumentCount)
            {
                return true;
            }

            if (!_typeLowering.TryGetDirectFunctionSignature(currentTypeId, out _, out var resultTypeId) ||
                !TryResolveClosureValueSignature(resultTypeId, out var resultSignature))
            {
                return false;
            }

            signatures.Add(resultSignature);
            currentTypeId = resultTypeId;
            currentSignature = resultSignature;
        }

        return true;
    }

    private static bool TryExtractFunctionSignature(LlvmType llvmType, out LlvmFunctionType functionType)
    {
        switch (llvmType)
        {
            case LlvmFunctionType directFunctionType:
                functionType = directFunctionType;
                return true;

            case LlvmPointerType { ElementType: LlvmFunctionType pointedFunctionType }:
                functionType = pointedFunctionType;
                return true;

            default:
                functionType = default!;
                return false;
        }
    }

    private List<LlvmValue> CoerceArgumentsForSignature(LlvmFunctionType functionType, IReadOnlyList<LlvmValue> arguments)
    {
        var coerced = new List<LlvmValue>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (index >= functionType.ParameterTypes.Count)
            {
                coerced.Add(arguments[index]);
                continue;
            }

            coerced.Add(CoerceValueToType(arguments[index], functionType.ParameterTypes[index], $"partial_arg{index}"));
        }

        return coerced;
    }

    private LlvmCall EmitDirectCall(
        MirCall call,
        LlvmValue funcValue,
        IReadOnlyList<LlvmValue> coercedArguments,
        LlvmType returnType)
    {
        var targetPlace = call.Target is MirPlace { Kind: PlaceKind.Local } localTarget
            ? localTarget
            : null;
        var targetUsesSlot = targetPlace != null && IsSlotBackedLocal(targetPlace.Local);
        var resultName = call.Target is MirPlace target
            ? _nameMangler.NewTempName(targetUsesSlot ? $"l{target.Local.Value}_call" : $"l{target.Local.Value}")
            : _nameMangler.NewTempName("call");

        var llvmCall = new LlvmCall
        {
            Function = funcValue,
            Arguments = coercedArguments.ToList(),
            ReturnType = returnType,
            ResultName = resultName,
            TailCallKind = SelectTailCallKind(call, returnType, coercedArguments, targetUsesSlot)
        };

        if (targetPlace != null)
        {
            _partialCallStates.Remove(targetPlace.Local);
            ClearGenericLocal(targetPlace.Local);

            if (targetUsesSlot)
            {
                if (returnType is not LlvmVoidType)
                {
                    var callResult = new LlvmInstructionRef
                    {
                        Instruction = llvmCall,
                        Type = returnType
                    };

                    QueueStoreToLocalSlot(targetPlace.Local, callResult);
                }
            }
            else
            {
                _locals.RuntimeWordLocals.Remove(targetPlace.Local);
                _locals.LocalMap[targetPlace.Local] = new LlvmLocal
                {
                    Name = resultName,
                    Type = returnType
                };
            }
        }

        return llvmCall;
    }

    private LlvmTailCallKind SelectTailCallKind(
        MirCall call,
        LlvmType returnType,
        IReadOnlyList<LlvmValue> arguments,
        bool targetUsesSlot = false)
    {
        if (!call.IsTailCall)
        {
            return LlvmTailCallKind.None;
        }

        if (call.Function is not MirFunctionRef)
        {
            return LlvmTailCallKind.None;
        }

        return CanEmitMustTail(returnType, arguments, targetUsesSlot)
            ? LlvmTailCallKind.MustTail
            : LlvmTailCallKind.Tail;
    }

    private LlvmCall? ConvertRuntimeWordNoOpConversion(MirCall call)
    {
        if (call.Arguments.Count != 1 ||
            call.Target is not MirPlace { Kind: PlaceKind.Local } target)
        {
            return null;
        }

        var source = ConvertOperand(call.Arguments[0]);
        AssignPlaceFromValue(target, source);

        return null;
    }

    private bool CanEmitMustTail(
        LlvmType returnType,
        IReadOnlyList<LlvmValue> arguments,
        bool targetUsesSlot)
    {
        if (targetUsesSlot ||
            _currentFunction == null ||
            !_currentFunction.ReturnType.Equals(returnType) ||
            _currentFunction.Parameters.Count != arguments.Count)
        {
            return false;
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            if (!_currentFunction.Parameters[i].Type.Equals(arguments[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    private List<LlvmValue> CoerceCallArguments(LlvmValue callee, IReadOnlyList<LlvmValue> arguments)
    {
        var functionType = callee.Type switch
        {
            LlvmFunctionType direct => direct,
            LlvmPointerType { ElementType: LlvmFunctionType pointee } => pointee,
            _ => null
        };

        if (functionType == null || functionType.ParameterTypes.Count == 0)
        {
            return arguments.ToList();
        }

        var coerced = new List<LlvmValue>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (index >= functionType.ParameterTypes.Count)
            {
                coerced.Add(argument);
                continue;
            }

            var expectedType = functionType.ParameterTypes[index];
            coerced.Add(CoerceValueToType(argument, expectedType, $"arg{index}"));
        }

        return coerced;
    }

    private LlvmValue CoerceValueToType(LlvmValue value, LlvmType expectedType, string tempPrefix)
    {
        if (value.Type == expectedType)
        {
            return value;
        }

        if (value.Type is LlvmVoidType)
        {
            return expectedType switch
            {
                LlvmPointerType => LlvmNullPointer.Instance,
                LlvmIntType intType => new LlvmConstant { Value = 0L, Type = intType },
                LlvmFloatType floatType => new LlvmConstant { Value = 0.0d, Type = floatType },
                _ => value
            };
        }

        if (value.Type is LlvmPointerType && expectedType is LlvmPointerType)
        {
            var cast = new LlvmCast
            {
                Op = WellKnownStrings.InternalNames.Bitcast,
                Value = value,
                TargetType = expectedType,
                ResultName = _nameMangler.NewTempName($"{tempPrefix}_bitcast")
            };
            _currentBlock?.Instructions.Add(cast);
            return new LlvmInstructionRef
            {
                Instruction = cast,
                Type = expectedType
            };
        }

        if (expectedType is LlvmPointerType)
        {
            return CoerceToPointer(value);
        }

        if (expectedType is LlvmStructType or LlvmArrayType &&
            value.Type is LlvmPointerType)
        {
            var load = new LlvmLoad
            {
                Pointer = CoerceToPointer(value),
                LoadType = expectedType,
                ResultName = _nameMangler.NewTempName($"{tempPrefix}_load")
            };
            _currentBlock?.Instructions.Add(load);
            return new LlvmInstructionRef
            {
                Instruction = load,
                Type = expectedType
            };
        }

        if (expectedType is LlvmIntType intExpected)
        {
            return CoerceIntegerToWidth(value, intExpected.Bits, tempPrefix);
        }

        return value;
    }

    private LlvmValue CoerceIntegerToWidth(LlvmValue value, int bits, string tempPrefix)
    {
        LlvmValue integerValue = value.Type switch
        {
            LlvmIntType => value,
            LlvmPointerType => CoerceToI64(value),
            _ => value
        };

        if (integerValue.Type is not LlvmIntType sourceInt)
        {
            return integerValue;
        }

        if (sourceInt.Bits == bits)
        {
            return integerValue;
        }

        var targetType = bits switch
        {
            1 => LlvmIntType.I1,
            8 => LlvmIntType.I8,
            16 => LlvmIntType.I16,
            32 => LlvmIntType.I32,
            _ => LlvmIntType.I64
        };

        if (sourceInt.Bits < bits)
        {
            var zext = new LlvmZext
            {
                Value = integerValue,
                TargetType = targetType,
                ResultName = _nameMangler.NewTempName($"{tempPrefix}_zext")
            };
            _currentBlock?.Instructions.Add(zext);
            return new LlvmInstructionRef
            {
                Instruction = zext,
                Type = targetType
            };
        }

        var trunc = new LlvmTrunc
        {
            Value = integerValue,
            TargetType = targetType,
            ResultName = _nameMangler.NewTempName($"{tempPrefix}_trunc")
        };
        _currentBlock?.Instructions.Add(trunc);
        return new LlvmInstructionRef
        {
            Instruction = trunc,
            Type = targetType
        };
    }

    private LlvmValue ResolveCallTargetValue(
        MirCall call,
        IReadOnlyList<LlvmValue> arguments,
        out LlvmType returnType)
    {
        if (call.Function is not MirFunctionRef funcRef)
        {
            var loweredFunction = ConvertOperand(call.Function);
            returnType = InferCallReturnType(call, loweredFunction);
            return loweredFunction;
        }

        var functionType = ResolveFunctionType(funcRef);
        if (functionType != null)
        {
            var isExternalFfi = TryGetExternalFfiSymbolName(funcRef.Name, funcRef.SymbolId, out var externalFfiName);
            var functionName = isExternalFfi
                ? externalFfiName
                : ResolveFunctionLlvmName(funcRef, functionType);
            if (isExternalFfi)
            {
                RecordExternalFunctionDeclaration(
                    functionName,
                    functionType,
                    LlvmDeclarationOrigin.ExternalFfi);
            }
            else if (TryGetRuntimeFunctionType(funcRef, out _, out _))
            {
                RecordExternalFunctionDeclaration(
                    functionName,
                    functionType,
                    LlvmDeclarationOrigin.RuntimeIntrinsic);
            }

            returnType = InferCallReturnType(
                call,
                new LlvmGlobal
                {
                    Name = functionName,
                    Type = functionType
                });

            return new LlvmGlobal
            {
                Name = functionName,
                Type = functionType
            };
        }

        if (funcRef.TraitMethodRole == TraitMethodRole.Show)
        {
            var helper = GetOrCreateErasedShowHelper();
            var helperType = new LlvmFunctionType
            {
                ReturnType = LlvmPointerType.VoidPtr(),
                ParameterTypes = [LlvmPointerType.VoidPtr()]
            };
            returnType = LlvmPointerType.VoidPtr();
            return new LlvmGlobal
            {
                Name = helper.Name,
                Type = helperType
            };
        }

        returnType = call.Target is MirPlace target && target.TypeId.IsValid
            ? NormalizeSignatureReturnType(LowerTypeIdOrReport(target.TypeId, "unresolved direct callee result"))
            : TypeSemantics.IsAdtConstructorCall(funcRef)
                ? LlvmPointerType.VoidPtr()
            : LlvmVoidType.Instance;

        var isUnresolvedExternalFfi = TryGetExternalFfiSymbolName(funcRef.Name, funcRef.SymbolId, out var unresolvedExternalFfiName);
        if (!isUnresolvedExternalFfi && !IsPermittedUnresolvedFunctionName(funcRef.Name))
        {
            ReportUnresolvedDirectFunctionReference(call, funcRef);
        }

        var externalType = new LlvmFunctionType
        {
            ReturnType = returnType,
            ParameterTypes = arguments.Select(argument => NormalizeParameterType(argument.Type)).ToList()
        };
        var externalFunctionName = isUnresolvedExternalFfi
            ? unresolvedExternalFfiName
            : ResolveFunctionLlvmName(funcRef);
        RecordExternalFunctionDeclaration(
            externalFunctionName,
            externalType,
            isUnresolvedExternalFfi
                ? LlvmDeclarationOrigin.ExternalFfi
                : LlvmDeclarationOrigin.UnresolvedExternal);

        return new LlvmGlobal
        {
            Name = externalFunctionName,
            Type = externalType
        };
    }

    private static bool IsPermittedUnresolvedFunctionName(string? functionName)
    {
        // ADT constructor names (simple uppercase identifiers like "Some")
        if (TypeSemantics.IsLikelyAdtConstructorByName(functionName))
            return true;

        // Trait-qualified paths (e.g. "Applicative::pure") — trait resolution
        // may not connect the implementation in all cases, so these are
        // permitted to remain unresolved at the LLVM level.
        if (!string.IsNullOrWhiteSpace(functionName) &&
            functionName.Contains(WellKnownStrings.Separators.Path, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsRuntimeFunctionRef(MirFunctionRef functionRef, string name)
    {
        return MirRuntimeFunctions.HasIdentity(functionRef, name);
    }

    // Array intrinsics can reach codegen through two MirFunctionRef shapes:
    //   1. a direct runtime-identity ref (e.g. lowered by the array literal path) —
    //      matched by MirRuntimeFunctions.HasIdentity; OR
    //   2. a specialized @intrinsic("array_*") wrapper ref (e.g. push_raw__spec)
    //      produced by MirGenericSpecializer, whose FunctionId carries the builtin
    //      intrinsic identity — matched by MirBuiltinFunctions.TryGetIntrinsicName.
    // The specialized-wrapper path previously fell through to EmitDirectCall, which
    // coerced scalar element arguments with `inttoptr i64 -> ptr` and dropped the
    // element size, crashing at runtime. Detect both shapes here so every array_*
    // call goes through the dedicated ConvertRuntimeArray* lowering.
    private static bool IsArrayIntrinsicCall(MirFunctionRef functionRef, string intrinsicName)
    {
        return MirRuntimeFunctions.HasIdentity(functionRef, intrinsicName) ||
               (MirBuiltinFunctions.TryGetIntrinsicName(functionRef, out var name) &&
                string.Equals(name, intrinsicName, StringComparison.Ordinal));
    }

    private static bool TryGetBuiltinIntrinsicName(MirFunctionRef functionRef, string expectedName, out string name)
    {
        return MirBuiltinFunctions.TryGetIntrinsicName(functionRef, out name) &&
               string.Equals(name, expectedName, StringComparison.Ordinal);
    }

    private void RecordExternalFunctionDeclaration(string name, LlvmFunctionType functionType, LlvmDeclarationOrigin origin)
    {
        if (!_externalFunctionDeclarations.TryGetValue(name, out var existing))
        {
            _externalFunctionDeclarations[name] = new ExternalFunctionDeclarationInfo(functionType, origin);
            return;
        }

        if (existing.FunctionType == functionType)
        {
            return;
        }

        _externalFunctionDeclarations[name] = new ExternalFunctionDeclarationInfo(
            new LlvmFunctionType
            {
                ReturnType = functionType.ReturnType,
                ParameterTypes = functionType.ParameterTypes,
                IsVarArg = true
            },
            existing.Origin == origin ? origin : LlvmDeclarationOrigin.UnresolvedExternal);
    }

    private LlvmCall ConvertRuntimeArrayNewCall(MirCall call)
    {
        var capacityArg = call.Arguments.Count > 0
            ? CoerceToI64(ConvertOperand(call.Arguments[0]))
            : new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 };
        var originalSizeArg = call.Arguments.Count > 1
            ? CoerceToI64(ConvertOperand(call.Arguments[1]))
            : new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 };
        var sizeArg = ResolveRuntimeArrayElementSizeArgument(call, valueOperand: null, originalSizeArg);
        var policy = TryResolveConcreteRuntimeArrayElementTypeId(call, valueOperand: null, out var elementTypeId)
            ? GetArrayElementPolicy(elementTypeId)
            : new ArrayElementPolicy(LlvmNullPointer.Instance, LlvmNullPointer.Instance);

        return EmitDirectCall(
            call,
            CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.ArrayNewWithPolicy,
                LlvmPointerType.VoidPtr(),
                [LlvmIntType.I64, LlvmIntType.I64, LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr()]),
            [capacityArg, sizeArg, policy.Retain, policy.Release],
            LlvmPointerType.VoidPtr());
    }

    private LlvmCall ConvertRuntimeArrayPushCall(MirCall call)
    {
        var targetPlace = call.Target is MirPlace { Kind: PlaceKind.Local } localTarget
            ? localTarget
            : null;
        var targetUsesSlot = targetPlace != null && IsSlotBackedLocal(targetPlace.Local);
        var resultName = call.Target is MirPlace target
            ? _nameMangler.NewTempName(targetUsesSlot ? $"l{target.Local.Value}_push" : $"l{target.Local.Value}")
            : _nameMangler.NewTempName(WellKnownStrings.InternalNames.ArrayPush);

        var arrayArg = call.Arguments.Count > 0
            ? CoerceToPointer(ConvertOperand(call.Arguments[0]))
            : LlvmNullPointer.Instance;

        var valueArgRaw = call.Arguments.Count > 1
            ? ConvertOperand(call.Arguments[1])
            : LlvmNullPointer.Instance;
        // After specialization the value operand's TypeId may be stripped; resolve the
        // element type robustly (mirrors ConvertIndexedStore) so scalars box into an
        // addressable slot instead of being inttoptr-coerced into a bogus pointer.
        var valueTypeId = TryResolveConcreteRuntimeArrayElementTypeId(
                call, call.Arguments.Count > 1 ? call.Arguments[1] : null, out var pushElementTypeId)
            ? pushElementTypeId
            : (call.Arguments.Count > 1 ? call.Arguments[1].TypeId : TypeId.None);
        var valueArgType = LowerStorageTypeIdOrReport(valueTypeId, "array push value");
        var valueArg = CreateAddressableValuePointer(valueArgRaw, valueArgType);

        var originalSizeArg = call.Arguments.Count > 2
            ? CoerceToI64(ConvertOperand(call.Arguments[2]))
            : new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 };
        var sizeArg = ResolveRuntimeArrayElementSizeArgument(
            call,
            call.Arguments.Count > 1 ? call.Arguments[1] : null,
            originalSizeArg);

        var runtimeCall = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.ArrayPush,
                LlvmPointerType.VoidPtr(),
                [LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64]),
            Arguments = [arrayArg, valueArg, sizeArg],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = resultName
        };

        if (targetPlace != null)
        {
            ClearGenericLocal(targetPlace.Local);
            if (targetUsesSlot)
            {
                var callResult = new LlvmInstructionRef
                {
                    Instruction = runtimeCall,
                    Type = LlvmPointerType.VoidPtr()
                };
                QueueStoreToLocalSlot(targetPlace.Local, callResult);
            }
            else
            {
                _locals.LocalMap[targetPlace.Local] = new LlvmLocal
                {
                    Name = resultName,
                    Type = LlvmPointerType.VoidPtr()
                };
            }
        }

        return runtimeCall;
    }

    private LlvmCall ConvertRuntimeArrayExtendCall(MirCall call)
    {
        var targetPlace = call.Target is MirPlace { Kind: PlaceKind.Local } localTarget
            ? localTarget
            : null;
        var targetUsesSlot = targetPlace != null && IsSlotBackedLocal(targetPlace.Local);
        var resultName = call.Target is MirPlace target
            ? _nameMangler.NewTempName(targetUsesSlot ? $"l{target.Local.Value}_ext" : $"l{target.Local.Value}")
            : _nameMangler.NewTempName(WellKnownStrings.InternalNames.ArrayExtend);

        var dstArg = call.Arguments.Count > 0
            ? CoerceToPointer(ConvertOperand(call.Arguments[0]))
            : LlvmNullPointer.Instance;

        var srcArg = call.Arguments.Count > 1
            ? CoerceToPointer(ConvertOperand(call.Arguments[1]))
            : LlvmNullPointer.Instance;

        var sizeArg = call.Arguments.Count > 2
            ? CoerceToI64(ConvertOperand(call.Arguments[2]))
            : new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 };

        var runtimeCall = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.ArrayExtend,
                LlvmPointerType.VoidPtr(),
                [LlvmPointerType.VoidPtr(), LlvmPointerType.VoidPtr(), LlvmIntType.I64]),
            Arguments = [dstArg, srcArg, sizeArg],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = resultName
        };

        if (targetPlace != null)
        {
            ClearGenericLocal(targetPlace.Local);
            if (targetUsesSlot)
            {
                var callResult = new LlvmInstructionRef
                {
                    Instruction = runtimeCall,
                    Type = LlvmPointerType.VoidPtr()
                };
                QueueStoreToLocalSlot(targetPlace.Local, callResult);
            }
            else
            {
                _locals.LocalMap[targetPlace.Local] = new LlvmLocal
                {
                    Name = resultName,
                    Type = LlvmPointerType.VoidPtr()
                };
            }
        }

        return runtimeCall;
    }

    private LlvmCall ConvertRuntimeArrayPopCall(MirCall call)
    {
        var arrayArg = call.Arguments.Count > 0
            ? CoerceToPointer(ConvertOperand(call.Arguments[0]))
            : LlvmNullPointer.Instance;

        return new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.ArrayPop,
                LlvmVoidType.Instance,
                [LlvmPointerType.VoidPtr()]),
            Arguments = [arrayArg],
            ReturnType = LlvmVoidType.Instance
        };
    }

    private LlvmCall ConvertRuntimeArraySwapCall(MirCall call)
    {
        var arrayArg = call.Arguments.Count > 0
            ? CoerceToPointer(ConvertOperand(call.Arguments[0]))
            : LlvmNullPointer.Instance;

        var leftArg = call.Arguments.Count > 1
            ? CoerceToI64(ConvertOperand(call.Arguments[1]))
            : new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 };

        var rightArg = call.Arguments.Count > 2
            ? CoerceToI64(ConvertOperand(call.Arguments[2]))
            : new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 };

        return new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.ArraySwap,
                LlvmVoidType.Instance,
                [LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmIntType.I64]),
            Arguments = [arrayArg, leftArg, rightArg],
            ReturnType = LlvmVoidType.Instance
        };
    }

    // array_set(array, index, value, size_hint) -> Unit : in-place write at index.
    // The size_hint arg is a placeholder (like push/extend); the real element size
    // is resolved from the value's type so callers do not need to know the ABI width.
    private LlvmCall ConvertRuntimeArraySetCall(MirCall call)
    {
        var arrayArg = call.Arguments.Count > 0
            ? CoerceToPointer(ConvertOperand(call.Arguments[0]))
            : LlvmNullPointer.Instance;

        var indexArg = call.Arguments.Count > 1
            ? CoerceToI64(ConvertOperand(call.Arguments[1]))
            : new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 };

        var valueOperand = call.Arguments.Count > 2 ? call.Arguments[2] : null;
        var valueArgRaw = valueOperand != null
            ? ConvertOperand(valueOperand)
            : LlvmNullPointer.Instance;
        // Resolve the element type robustly (operand TypeId may be stripped after
        // specialization) so scalars box correctly instead of inttoptr-coercing.
        var valueTypeId = TryResolveConcreteRuntimeArrayElementTypeId(call, valueOperand, out var setElementTypeId)
            ? setElementTypeId
            : (valueOperand != null ? valueOperand.TypeId : TypeId.None);
        var valueArgType = LowerStorageTypeIdOrReport(valueTypeId, "array set value");
        var valueArg = CreateAddressableValuePointer(valueArgRaw, valueArgType);

        var originalSizeArg = call.Arguments.Count > 3
            ? CoerceToI64(ConvertOperand(call.Arguments[3]))
            : new LlvmConstant { Value = 0L, Type = LlvmIntType.I64 };
        var sizeArg = ResolveRuntimeArrayElementSizeArgument(call, valueOperand, originalSizeArg);

        return new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.ArraySet,
                LlvmVoidType.Instance,
                [LlvmPointerType.VoidPtr(), LlvmIntType.I64, LlvmPointerType.VoidPtr(), LlvmIntType.I64]),
            Arguments = [arrayArg, indexArg, valueArg, sizeArg],
            ReturnType = LlvmVoidType.Instance
        };
    }

    private LlvmValue ResolveRuntimeArrayElementSizeArgument(
        MirCall call,
        MirOperand? valueOperand,
        LlvmValue fallbackSizeArg)
    {
        return TryResolveConcreteRuntimeArrayElementTypeId(call, valueOperand, out var elementTypeId)
            ? new LlvmConstant
            {
                Value = (long)GetRuntimeElementSize(elementTypeId),
                Type = LlvmIntType.I64
            }
            : fallbackSizeArg;
    }

    private bool TryResolveConcreteRuntimeArrayElementTypeId(
        MirCall call,
        MirOperand? valueOperand,
        out TypeId elementTypeId)
    {
        if (TryUseConcreteRuntimeElementType(valueOperand?.TypeId ?? TypeId.None, out elementTypeId))
        {
            return true;
        }

        if (call.Target is MirPlace targetPlace &&
            TryResolveConcreteListElementType(targetPlace.TypeId, out elementTypeId))
        {
            return true;
        }

        if (call.Arguments.Count > 0 &&
            TryResolveConcreteListElementType(call.Arguments[0].TypeId, out elementTypeId))
        {
            return true;
        }

        // After generic specialization the operand TypeIds may be stripped, but the
        // callee's signature is substituted concretely (e.g. push: (Seq[Int], Int, Int)
        // -> Seq[Int]). Recover the element type from the signature's array parameter
        // or, failing that, the value parameter itself.
        if (TryResolveElementTypeIdFromCalleeSignature(call, out elementTypeId))
        {
            return true;
        }

        elementTypeId = TypeId.None;
        return false;
    }

    private bool TryResolveElementTypeIdFromCalleeSignature(MirCall call, out TypeId elementTypeId)
    {
        elementTypeId = TypeId.None;
        if (call.Function is not MirFunctionRef functionRef ||
            !functionRef.SignatureTypeId.IsValid)
        {
            return false;
        }

        if (!_typeLowering.TryGetFunctionSignature(
                functionRef.SignatureTypeId, out var parameterTypes, out _))
        {
            return false;
        }

        // The first parameter is the array (Seq[T]); peel its element type.
        if (parameterTypes.Count > 0 &&
            TryResolveConcreteListElementType(parameterTypes[0], out elementTypeId))
        {
            return true;
        }

        // Otherwise the value parameter (index 1 for push/set) carries T directly.
        if (parameterTypes.Count > 1 &&
            TryUseConcreteRuntimeElementType(parameterTypes[1], out elementTypeId))
        {
            return true;
        }

        return false;
    }

    private bool TryResolveConcreteListElementType(TypeId listTypeId, out TypeId elementTypeId)
    {
        elementTypeId = TypeId.None;
        return listTypeId.IsValid &&
               !_typeLowering.IsOpenDynamicType(listTypeId) &&
               _typeLowering.TryGetTyConTypeArguments(listTypeId, out _, out var typeArguments) &&
               typeArguments.Count > 0 &&
               TryUseConcreteRuntimeElementType(typeArguments[0], out elementTypeId);
    }

    private bool TryUseConcreteRuntimeElementType(TypeId candidateTypeId, out TypeId elementTypeId)
    {
        elementTypeId = TypeId.None;
        if (!candidateTypeId.IsValid || _typeLowering.IsOpenDynamicType(candidateTypeId))
        {
            return false;
        }

        elementTypeId = candidateTypeId;
        return true;
    }

    /// <summary>
    /// 生成 LLVM intrinsic 调用指令（如 llvm.sin.f64, llvm.cos.f64 等）。
    /// 将 MirCall 的参数转换为 LLVM 值，生成 call 指令并添加到当前基本块。
    /// </summary>
    /// <param name="call">MIR 调用指令</param>
    /// <param name="intrinsicName">LLVM intrinsic 名称（如 "llvm.sin.f64"）</param>
    /// <param name="returnType">返回类型（如 LlvmFloatType.Double）</param>
    /// <param name="parameterTypes">参数类型列表</param>
    /// <returns>生成的 LlvmCall 指令，如果参数不足则返回 null</returns>
    private LlvmCall? ConvertLlvmIntrinsicCall(
        MirCall call,
        string intrinsicName,
        LlvmType returnType,
        IReadOnlyList<LlvmType> parameterTypes)
    {
        // 参数数量校验
        if (call.Arguments.Count < parameterTypes.Count)
        {
            return null;
        }

        var targetPlace = call.Target as MirPlace;
        var resultName = targetPlace != null
            ? _nameMangler.NewTempName($"l{targetPlace.Local.Value}")
            : _nameMangler.NewTempName("intrinsic");

        // 转换所有参数，并进行类型强制转换以匹配 intrinsic 签名
        var arguments = new List<LlvmValue>(call.Arguments.Count);
        for (var i = 0; i < parameterTypes.Count; i++)
        {
            var rawArg = ConvertOperand(call.Arguments[i]);
            arguments.Add(CoerceValueToType(rawArg, parameterTypes[i], $"intrinsic_arg{i}"));
        }

        var intrinsicCall = new LlvmCall
        {
            Function = new LlvmGlobal
            {
                Name = intrinsicName,
                Type = new LlvmFunctionType
                {
                    ReturnType = returnType,
                    ParameterTypes = parameterTypes.ToList()
                }
            },
            Arguments = arguments,
            ReturnType = returnType,
            ResultName = resultName
        };

        if (targetPlace != null)
        {
            ClearGenericLocal(targetPlace.Local);
            _locals.RuntimeWordLocals.Remove(targetPlace.Local);
            _locals.LocalMap[targetPlace.Local] = new LlvmLocal
            {
                Name = resultName,
                Type = returnType
            };
        }

        return intrinsicCall;
    }

    private LlvmCall? ConvertMathIntrinsic(MirCall call, string name)
    {
        var parameterTypes = GetMathFunctionParameterTypes(name);
        if (TryGetLibmFunctionName(name, out var libmName))
        {
            return ConvertExternalMathCall(call, libmName, parameterTypes);
        }

        // Map the remaining Eidos math intrinsics to LLVM intrinsics.
        // Use llvm.minnum/llvm.maxnum instead of llvm.fmin/llvm.fmax because
        // the latter are not lowered by llc on Windows MSVC targets (LLVM 22),
        // leaving undefined symbols for the linker. llvm.minnum/maxnum are the
        // older stable names that lower correctly.
        var llvmName = name switch
        {
            "math_fmin" => "llvm.minnum.f64",
            "math_fmax" => "llvm.maxnum.f64",
            _ => "llvm." + name.Substring(5) + ".f64"
        };

        var intrinsicCall = ConvertLlvmIntrinsicCall(call, llvmName, LlvmFloatType.Double, parameterTypes);
        if (intrinsicCall != null)
        {
            return intrinsicCall;
        }

        // Partial application: not enough arguments yet.
        // Record partial state with the LLVM intrinsic name as callee so the combined
        // call will reference the intrinsic (e.g. @llvm.copysign.f64), not a non-existent
        // MIR function (@eidos_math_copysign).
        RecordMathIntrinsicPartialState(call, llvmName, parameterTypes);
        return null;
    }

    private LlvmCall? ConvertExternalMathCall(
        MirCall call,
        string functionName,
        IReadOnlyList<LlvmType> parameterTypes)
    {
        var functionType = new LlvmFunctionType
        {
            ReturnType = LlvmFloatType.Double,
            ParameterTypes = parameterTypes.ToList()
        };

        if (call.Arguments.Count < parameterTypes.Count)
        {
            RecordExternalMathPartialState(call, functionName, functionType);
            return null;
        }

        var args = new List<LlvmValue>(parameterTypes.Count);
        for (var i = 0; i < parameterTypes.Count; i++)
        {
            args.Add(CoerceValueToType(ConvertOperand(call.Arguments[i]), parameterTypes[i], $"math_arg{i}"));
        }

        RecordExternalFunctionDeclaration(functionName, functionType, LlvmDeclarationOrigin.ExternalFfi);
        AddMathLinkLibrary();
        return EmitDirectCall(
            call,
            new LlvmGlobal
            {
                Name = functionName,
                Type = functionType
            },
            args,
            LlvmFloatType.Double);
    }

    private void RecordExternalMathPartialState(
        MirCall call,
        string functionName,
        LlvmFunctionType functionType)
    {
        if (call.Target is not MirPlace { Kind: PlaceKind.Local } targetLocal)
        {
            return;
        }

        var boundArguments = new List<LlvmValue>(call.Arguments.Count);
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var rawArg = ConvertOperand(call.Arguments[i]);
            boundArguments.Add(CoerceValueToType(rawArg, functionType.ParameterTypes[i], $"math_arg{i}"));
        }

        var boundArgumentManagedFlags = boundArguments
            .Select((argument, index) => IsManagedRcPayloadValue(call.Arguments[index], argument, argument.Type))
            .ToList();

        RecordExternalFunctionDeclaration(functionName, functionType, LlvmDeclarationOrigin.ExternalFfi);
        AddMathLinkLibrary();
        _partialCallStates[targetLocal.Local] = new PartialCallState(
            new LlvmGlobal
            {
                Name = functionName,
                Type = functionType
            },
            functionType,
            boundArguments,
            boundArgumentManagedFlags,
            0,
            null);
        _locals.LocalMap.Remove(targetLocal.Local);
        _locals.RuntimeWordLocals.Remove(targetLocal.Local);
    }

    private static IReadOnlyList<LlvmType> GetMathFunctionParameterTypes(string name)
    {
        return name switch
        {
            "math_fma" => [LlvmFloatType.Double, LlvmFloatType.Double, LlvmFloatType.Double],
            "math_atan2" or "math_pow" or "math_copysign" or "math_fmin" or "math_fmax"
                => [LlvmFloatType.Double, LlvmFloatType.Double],
            _ => [LlvmFloatType.Double]
        };
    }

    private static bool TryGetLibmFunctionName(string name, out string functionName)
    {
        functionName = name switch
        {
            "math_sin" => "sin",
            "math_cos" => "cos",
            "math_tan" => "tan",
            "math_asin" => "asin",
            "math_acos" => "acos",
            "math_atan" => "atan",
            "math_atan2" => "atan2",
            "math_exp" => "exp",
            "math_log" => "log",
            "math_log2" => "log2",
            "math_log10" => "log10",
            "math_pow" => "pow",
            _ => string.Empty
        };
        return functionName.Length > 0;
    }

    private void AddMathLinkLibrary()
    {
        if (_currentModule is null || OperatingSystem.IsWindows())
        {
            return;
        }

        if (!_currentModule.LinkLibraries.Any(library => string.Equals(library, "m", StringComparison.Ordinal)))
        {
            _currentModule.LinkLibraries.Add("m");
        }
    }

    private void RecordMathIntrinsicPartialState(
        MirCall call,
        string llvmIntrinsicName,
        IReadOnlyList<LlvmType> parameterTypes)
    {
        if (call.Target is not MirPlace { Kind: PlaceKind.Local } targetLocal)
        {
            return;
        }

        var boundArguments = new List<LlvmValue>(call.Arguments.Count);
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var rawArg = ConvertOperand(call.Arguments[i]);
            boundArguments.Add(CoerceValueToType(rawArg, parameterTypes[i], $"intrinsic_arg{i}"));
        }

        var boundArgumentManagedFlags = boundArguments
            .Select((argument, index) => IsManagedRcPayloadValue(call.Arguments[index], argument, argument.Type))
            .ToList();

        var functionType = new LlvmFunctionType
        {
            ReturnType = LlvmFloatType.Double,
            ParameterTypes = parameterTypes.ToList()
        };

        var callee = new LlvmGlobal
        {
            Name = llvmIntrinsicName,
            Type = functionType
        };

        _partialCallStates[targetLocal.Local] = new PartialCallState(
            callee,
            functionType,
            boundArguments,
            boundArgumentManagedFlags,
            0,
            null);
        _locals.LocalMap.Remove(targetLocal.Local);
        _locals.RuntimeWordLocals.Remove(targetLocal.Local);
    }

    /// <summary>
    /// 如果需要，对存储的值进行类型转换（trunc, inttoptr, ptrtoint）
    /// </summary>
}
