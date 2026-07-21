using Eidosc.Symbols;
using System.Text;
using Eidosc.Borrow;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.CodeGen.Llvm;

// Constant and string operand conversion
public sealed partial class MirToLlvmConverter
{


    private LlvmGlobal CreateRuntimeFunctionGlobal(string name, LlvmType returnType, List<LlvmType> parameterTypes)
    {
        if (_runtimeFunctionGlobalCache.TryGetValue(name, out var cached))
        {
            return IsRuntimeFunctionGlobalSignature(cached, returnType, parameterTypes)
                ? cached
                : CreateRuntimeFunctionGlobalUncached(name, returnType, parameterTypes);
        }

        var global = CreateRuntimeFunctionGlobalUncached(name, returnType, parameterTypes);
        _runtimeFunctionGlobalCache[name] = global;
        return global;
    }

    private static LlvmGlobal CreateRuntimeFunctionGlobalUncached(string name, LlvmType returnType, List<LlvmType> parameterTypes)
    {
        return new LlvmGlobal
        {
            Name = name,
            Type = new LlvmFunctionType
            {
                ReturnType = returnType,
                ParameterTypes = parameterTypes
            }
        };
    }

    private static bool IsRuntimeFunctionGlobalSignature(
        LlvmGlobal global,
        LlvmType returnType,
        IReadOnlyList<LlvmType> parameterTypes)
    {
        if (global.Type is not LlvmFunctionType functionType ||
            !functionType.ReturnType.Equals(returnType) ||
            functionType.ParameterTypes.Count != parameterTypes.Count)
        {
            return false;
        }

        for (var index = 0; index < parameterTypes.Count; index++)
        {
            if (!functionType.ParameterTypes[index].Equals(parameterTypes[index]))
            {
                return false;
            }
        }

        return true;
    }

    private int GetRuntimeElementSize(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return IntPtr.Size;
        }

        if (LowerStorageTypeIdOrReport(typeId, "runtime element size") is LlvmStructType tupleStruct)
        {
            return tupleStruct.Fields.Count * IntPtr.Size;
        }

        return typeId.Value switch
        {
            BaseTypes.BoolId => 1,
            BaseTypes.CharId => 4,
            BaseTypes.UnitId => 1,
            BaseTypes.NeverId => 0,
            BaseTypes.IntId => sizeof(long),
            BaseTypes.FloatId => sizeof(double),
            _ => IntPtr.Size
        };
    }

    private bool IsManagedRcType(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        if (_typeLowering.IsOpenDynamicType(typeId))
        {
            return false;
        }

        if (_typeLowering.TryGetTypeDescriptor(typeId, out var descriptor) &&
            descriptor is TypeDescriptor.Ref or TypeDescriptor.MutRef)
        {
            return false;
        }

        if (IsFfiNonRcPointerType(typeId))
        {
            return false;
        }

        return LowerTypeIdOrReport(typeId, "managed rc type check") is LlvmPointerType;
    }

    private bool IsFfiNonRcPointerType(TypeId typeId)
    {
        if (IsFfiNonRcPointerBaseType(typeId))
        {
            return true;
        }

        if (!_typeLowering.TryGetTyConTypeArguments(typeId, out var constructorDescriptor, out _))
        {
            return false;
        }

        return TypeConstructorKey.TryParse(constructorDescriptor, out var constructorKey) &&
               TryResolveTyConConstructorTypeId(constructorKey, out var constructorTypeId)
            ? IsFfiNonRcPointerBaseType(constructorTypeId)
            : false;
    }

    private bool TryResolveTyConConstructorTypeId(TypeConstructorKey constructor, out TypeId typeId)
    {
        typeId = TypeId.None;

        if (constructor.Kind is TypeConstructorKeyKind.TypeId or TypeConstructorKeyKind.Builtin)
        {
            typeId = new TypeId(constructor.Id);
            return typeId.IsValid;
        }

        if (constructor.Kind == TypeConstructorKeyKind.Symbol &&
            _typeConstructorTypeIdBySymbol.TryGetValue(new SymbolId(constructor.Id), out var symbolTypeId))
        {
            typeId = symbolTypeId;
            return typeId.IsValid;
        }

        return false;
    }

    private static bool IsFfiNonRcPointerBaseType(TypeId typeId) =>
        typeId.Value is BaseTypes.RawPtrId or BaseTypes.CfnId;


    #region 终止指令转换

    private LlvmTerminator ConvertReturn(MirReturn ret)
    {
        var functionReturnType = _currentFunction?.ReturnType ?? LlvmVoidType.Instance;

        if (ret.Value == null)
        {
            return functionReturnType is LlvmVoidType
                ? new LlvmRet()
                : ReportReturnFallback(
                    ret,
                    DiagnosticMessages.MissingReturnValueDuringLlvmLowering,
                    "E5203");
        }

        if (functionReturnType is LlvmVoidType)
        {
            _ = ConvertOperand(ret.Value);
            return new LlvmRet();
        }

        LlvmValue value;
        if (ret.Value is MirPlace { Kind: PlaceKind.Local } localPlace &&
            !IsSlotBackedLocal(localPlace.Local))
        {
            var localValue = _partialCallStates.TryGetValue(localPlace.Local, out var partial)
                ? MaterializePartialClosureValue(localPlace, partial)
                : GetOrCreateLocalById(localPlace.Local, localPlace.TypeId);
            if (functionReturnType is LlvmStructType or LlvmArrayType &&
                localValue.Type is LlvmPointerType)
            {
                var aggregateLoad = new LlvmLoad
                {
                    Pointer = localValue,
                    LoadType = functionReturnType,
                    IsVolatile = false,
                    ResultName = _nameMangler.NewTempName($"l{localPlace.Local.Value}_ret")
                };
                _currentBlock?.Instructions.Add(aggregateLoad);
                value = new LlvmInstructionRef
                {
                    Instruction = aggregateLoad,
                    Type = functionReturnType
                };
            }
            else
            {
                value = localValue;
            }
        }
        else
        {
            value = ConvertOperand(ret.Value);
        }

        if (ret.Value is MirConstant { Value: MirConstantValue.UnitValue })
        {
            return ReportReturnFallback(
                ret,
                DiagnosticMessages.DefaultReturnValueRejectedDuringLlvmLowering,
                "E5204");
        }

        var loweredValue = CoerceDispatchWordForCurrentBlock(value, functionReturnType, "ret");
        if (loweredValue.Type is LlvmVoidType ||
            loweredValue.Type != functionReturnType && IsDefaultReturnSentinel(ret.Value))
        {
            return ReportReturnFallback(
                ret,
                DiagnosticMessages.DefaultReturnValueRejectedDuringLlvmLowering,
                "E5204");
        }

        return new LlvmRet
        {
            Value = loweredValue
        };
    }

    private LlvmTerminator ReportReturnFallback(MirReturn ret, string message, string code)
    {
        var diag = Diagnostic.Diagnostic.Error(message, code);
        if (HasSpan(ret.Span))
        {
            diag.WithLabel(ret.Span, DiagnosticMessages.MissingReturnValueLabel);
        }

        if (_currentFunction != null)
        {
            diag.WithNote(DiagnosticMessages.FunctionNote(_currentFunction.Name));
        }

        diag.WithHelp(DiagnosticMessages.LlvmFallbackLoweredToUnreachableHelp);
        Diagnostics.Add(diag);
        return LlvmUnreachable.Instance;
    }

    private static LlvmRet CreateDefaultReturn(LlvmType returnType)
    {
        if (returnType is LlvmVoidType)
        {
            return new LlvmRet();
        }

        return new LlvmRet
        {
            Value = CreateZeroValue(returnType)
        };
    }

    private static LlvmValue CreateZeroValue(LlvmType type)
    {
        return type switch
        {
            LlvmIntType => new LlvmConstant
            {
                Type = type,
                Value = 0
            },
            LlvmFloatType => new LlvmConstant
            {
                Type = type,
                Value = 0.0
            },
            LlvmPointerType => new LlvmConstant
            {
                Type = type,
                Value = null
            },
            _ => new LlvmZeroInitializer
            {
                Type = type
            }
        };
    }

    private static bool IsDefaultReturnSentinel(MirOperand operand)
    {
        return operand is MirConstant
        {
            Value: MirConstantValue.UnitValue or MirConstantValue.IntValue { Value: 0 }
        };
    }

    private LlvmBr ConvertGoto(MirGoto jump)
    {
        var targetBlock = GetOrCreateBlock(jump.Target);
        return new LlvmBr
        {
            Target = targetBlock
        };
    }

    private LlvmTerminator ConvertSwitch(MirSwitch sw)
    {
        var discriminant = ConvertOperand(sw.Discriminant);

        // 如果只有一个默认分支，转换为无条件跳转
        if (sw.Branches.Count == 0 && sw.DefaultTarget.HasValue)
        {
            return new LlvmBr
            {
                Target = GetOrCreateBlock(sw.DefaultTarget.Value)
            };
        }

        // 如果只有一个条件分支（二元选择），转换为 condbr
        if (sw.Branches.Count == 1 && sw.DefaultTarget.HasValue)
        {
            var branch = sw.Branches[0];
            var condition = CreateComparison(discriminant, branch.Value);
            _currentBlock?.Instructions.Add(condition);

            return new LlvmCondBr
            {
                Condition = new LlvmInstructionRef { Instruction = condition, Type = LlvmIntType.I1 },
                ThenBlock = GetOrCreateBlock(branch.Target),
                ElseBlock = GetOrCreateBlock(sw.DefaultTarget.Value)
            };
        }

        // 多路分支使用 switch
        var defaultBlock = sw.DefaultTarget.HasValue
            ? GetOrCreateBlock(sw.DefaultTarget.Value)
            : new LlvmBasicBlock { Label = "default" };

        var cases = sw.Branches.Select(b => (
            ConvertConstantToLlvm(b.Value, discriminant.Type),
            GetOrCreateBlock(b.Target)
        )).ToList();

        return new LlvmSwitch
        {
            Value = discriminant,
            DefaultBlock = defaultBlock,
            Cases = cases
        };
    }

    private LlvmIcmp CreateComparison(LlvmValue left, MirConstant right)
    {
        LlvmValue rightValue = ConvertConstantToLlvm(right);
        (left, rightValue) = NormalizeComparisonOperands(left, rightValue, BinaryOp.Eq);

        return new LlvmIcmp
        {
            Predicate = "eq",
            Left = left,
            Right = rightValue,
            ResultName = _nameMangler.NewTempName("cmp")
        };
    }

    #endregion

    #region 操作数转换

    /// <summary>
    /// 转换 MIR 操作数为 LLVM 值
    /// </summary>
    private LlvmValue ConvertOperand(MirOperand operand)
    {
        return operand switch
        {
            MirConstant constOp => ConvertConstantOperand(constOp),
            MirFunctionRef funcRef => MaterializeFunctionReference(funcRef),
            MirPlace place => MaterializePlaceValue(place, place.TypeId, "operand"),
            MirTemp temp => new LlvmLocal
            {
                Name = $"t{temp.Id.Value}",
                Type = LowerTypeIdOrReport(temp.TypeId, "temp operand")
            },
            _ => LlvmConstant.Zero
        };
    }

    private LlvmValue ConvertConstantOperand(MirConstant constOp)
    {
        if (constOp.Value is MirConstantValue.StringValue stringValue)
        {
            return ConvertStringConstantToPointer(stringValue.Value);
        }

        if (constOp.Value is MirConstantValue.RawStringValue rawStringValue)
        {
            return ConvertRawStringConstantToPointer(rawStringValue.Value);
        }

        return ConvertConstantToLlvm(constOp);
    }

    private LlvmValue ConvertStringConstantToPointer(string value)
    {
        // ConvertFunction(...) 没有模块上下文时保留旧行为，避免破坏既有单函数测试。
        if (_currentModule == null)
        {
            return new LlvmConstant
            {
                Value = value,
                Type = LlvmPointerType.VoidPtr()
            };
        }

        // Deduplicate: if we already created an EidosString for this value, reuse it.
        if (_stringLiteralPool.TryGetValue(value, out var pooled))
        {
            return pooled;
        }

        var cstrRef = CreateCStringPointer(value, "str");
        var byteLength = Encoding.UTF8.GetByteCount(value);

        var stringCtorCall = new LlvmCall
        {
            Function = CreateRuntimeFunctionGlobal(
                WellKnownStrings.Runtime.StringIntern,
                LlvmPointerType.VoidPtr(),
                [LlvmPointerType.VoidPtr(), LlvmIntType.I64]),
            Arguments =
            [
                cstrRef,
                new LlvmConstant
                {
                    Value = (long)byteLength,
                    Type = LlvmIntType.I64
                }
            ],
            ReturnType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName("strobj")
        };
        // Emit into the entry block so the result dominates all uses across branches.
        // Fall back to _currentBlock when the entry block is not yet materialized.
        var entryBlock = _currentFunction?.BasicBlocks.Count > 0
            ? _currentFunction.BasicBlocks[0]
            : _currentBlock;
        entryBlock?.Instructions.Add(stringCtorCall);

        var result = new LlvmInstructionRef
        {
            Instruction = stringCtorCall,
            Type = LlvmPointerType.VoidPtr()
        };

        _stringLiteralPool[value] = result;
        return result;
    }

    /// <summary>
    /// Convert a string constant to a raw C string pointer (const char*).
    /// Used for runtime-level string parameters like effect dispatch op_name
    /// and handler descriptor name slots. Unlike <see cref="ConvertStringConstantToPointer"/>,
    /// this does NOT wrap the string in <c>eidos_string_from_cstr</c>.
    /// </summary>
    private LlvmValue ConvertRawStringConstantToPointer(string value)
    {
        // No module context: return the string value directly (legacy test compatibility).
        if (_currentModule == null)
        {
            return new LlvmConstant
            {
                Value = value,
                Type = LlvmPointerType.VoidPtr()
            };
        }

        return CreateCStringPointer(value, "rawstr");
    }

    private LlvmInstructionRef CreateCStringPointer(string value, string tempPrefix)
    {
        var global = GetOrCreateStringLiteralGlobal(value);
        var arrayPointer = new LlvmGlobal
        {
            Name = global.Name,
            Type = new LlvmPointerType
            {
                ElementType = global.Type,
                AddressSpace = 0
            }
        };

        var cast = new LlvmCast
        {
            Op = WellKnownStrings.InternalNames.Bitcast,
            Value = arrayPointer,
            TargetType = LlvmPointerType.VoidPtr(),
            ResultName = _nameMangler.NewTempName(tempPrefix)
        };
        // Emit into the entry block so the pointer dominates all uses across branches.
        var cstrEntryBlock = _currentFunction?.BasicBlocks.Count > 0
            ? _currentFunction.BasicBlocks[0]
            : _currentBlock;
        cstrEntryBlock?.Instructions.Add(cast);

        return new LlvmInstructionRef
        {
            Instruction = cast,
            Type = LlvmPointerType.VoidPtr()
        };
    }

    #endregion

}
