using Eidosc.Symbols;
using Eidosc;
using Eidosc.Borrow;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public partial class MirToLlvmConverterTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ConvertFunction_ManagedDerefLoad_RetainsOnlyOwnedResult(
        bool createsBorrowAlias,
        bool expectsRetain)
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var referenceType = new TypeId(9701);
        var reference = LocalPlace(1, referenceType);
        var loaded = LocalPlace(2, stringType);
        var function = BuildFunction(
            new TypeId(BaseTypes.IntId),
            locals:
            [
                new MirLocal
                {
                    Id = reference.Local,
                    Name = "value_ref",
                    TypeId = referenceType,
                    IsParameter = true
                },
                new MirLocal { Id = loaded.Local, Name = "loaded", TypeId = stringType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = loaded,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Deref,
                        Base = reference,
                        TypeId = stringType
                    },
                    CreatesBorrowAlias = createsBorrowAlias
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = new TypeId(BaseTypes.IntId),
                Value = new MirConstantValue.IntValue(0)
            },
            name: createsBorrowAlias ? "borrowed_deref" : "owned_deref");
        var module = new MirModule
        {
            Name = "managed_deref",
            TypeDescriptors =
            {
                [referenceType.Value] = new TypeDescriptor.Ref(stringType)
            },
            Functions = [function]
        };

        var converted = new MirToLlvmConverter().Convert(module);
        var convertedFunction = Assert.Single(converted.Functions);
        var hasRetain = convertedFunction.BasicBlocks
            .SelectMany(static block => block.Instructions)
            .OfType<LlvmCall>()
            .Any(static call => call.Function is LlvmGlobal { Name: WellKnownStrings.Runtime.IncRefLocal });

        Assert.Equal(expectsRetain, hasRetain);
    }

    [Fact]
    public void TryInferFunctionReferenceValueType_ShowNameWithoutRole_DoesNotInferBuiltinShow()
    {
        var converter = new MirToLlvmConverter();
        var showRef = new MirFunctionRef
        {
            Name = "show",
            SymbolId = SymbolId.None,
            TypeId = TypeId.None
        };

        Assert.False(InvokeTryInferFunctionReferenceValueType(converter, showRef, out _));
    }

    [Fact]
    public void TryInferFunctionReferenceValueType_ShowRole_InfersBuiltinShowPointer()
    {
        var converter = new MirToLlvmConverter();
        var showRef = new MirFunctionRef
        {
            Name = "anything",
            SymbolId = SymbolId.None,
            TypeId = TypeId.None,
            CompilerSemanticRole = CompilerSemanticRole.Show
        };

        Assert.True(InvokeTryInferFunctionReferenceValueType(converter, showRef, out var inferredType));
        Assert.IsType<LlvmPointerType>(inferredType);
    }

    [Fact]
    public void ConvertFunction_UnitParameter_LowersToI1InFunctionSignature()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var intType = new TypeId(BaseTypes.IntId);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "u", TypeId = unitType, IsParameter = true }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            name: "unit_param");

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var param = Assert.Single(llvmFunc.Parameters);
        var paramType = Assert.IsType<LlvmIntType>(param.Type);
        Assert.Equal(1, paramType.Bits);
    }

    [Fact]
    public void Convert_ModuleCallWithUnitArgument_CoercesArgumentToI1()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(900);

        var callee = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "u", TypeId = unitType, IsParameter = true }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "callee_unit",
            symbolId: calleeSymbol);

        var caller = BuildFunction(
            intType,
            locals: [],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "callee_unit",
                        SymbolId = calleeSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments =
                    [
                        new MirConstant
                        {
                            TypeId = unitType,
                            Value = new MirConstantValue.UnitValue()
                        }
                    ]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            name: "caller_unit");

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "test",
            Functions = [callee, caller]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller_unit");
        var call = Assert.Single(llvmCaller.BasicBlocks.Single().Instructions.OfType<LlvmCall>());
        var argument = Assert.Single(call.Arguments);
        var argumentType = Assert.IsType<LlvmIntType>(argument.Type);
        Assert.Equal(1, argumentType.Bits);

        var ir = new LlvmEmitter().Emit(llvmModule);
        var calleeName = SingleFunctionNameBySourceName(llvmModule, "callee_unit");
        Assert.Contains($"call i64 @{calleeName}(i1 0)", ir);
        Assert.DoesNotContain($"define external i64 @{llvmCaller.Name}(void", ir);
    }

    [Fact]
    public void Convert_SpecializedExternalDeclaredAfterCaller_UsesOriginalExternalSymbol()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var externalSymbol = new SymbolId(925);
        var result = LocalPlace(1, intType);
        var caller = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = result.Local, Name = "result", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "native_callback__spec_EFFECT",
                        SymbolId = externalSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments =
                    [
                        new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(7)
                        }
                    ]
                }
            ],
            returnValue: result,
            name: "caller_before_external");
        var external = new MirFunc
        {
            Name = "native_callback__spec_EFFECT",
            SourceName = "native_callback",
            ReturnType = intType,
            SymbolId = externalSymbol,
            FunctionId = new FunctionId
            {
                SymbolId = externalSymbol,
                Name = "native_callback__spec_EFFECT",
                QualifiedName = "native_callback__spec_EFFECT"
            },
            IsExternal = true,
            ExternalSymbolName = "eidos_native_callback",
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = intType,
                    IsParameter = true
                }
            ]
        };

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "external_specialization",
            Functions = [caller, external]
        });

        Assert.Contains(
            llvmModule.Declarations,
            declaration => declaration is
            {
                Name: "eidos_native_callback",
                Origin: LlvmDeclarationOrigin.ExternalFfi
            });
        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller_before_external");
        var call = Assert.Single(llvmCaller.BasicBlocks.Single().Instructions.OfType<LlvmCall>());
        Assert.Equal("eidos_native_callback", Assert.IsType<LlvmGlobal>(call.Function).Name);

        var ir = new LlvmEmitter().Emit(llvmModule);
        Assert.Contains("call i64 @eidos_native_callback(i64 7)", ir);
        Assert.DoesNotContain("@eidos_native_callback__spec_EFFECT", ir);
    }

    [Fact]
    public void Convert_ModuleCurriedCall_CombinesIntoDirectFullArityCall()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var partialType = TypeId.None;
        var calleeSymbol = new SymbolId(950);

        var arg0 = LocalPlace(1, intType);
        var arg1 = LocalPlace(2, intType);
        var partial = LocalPlace(3, partialType);
        var result = LocalPlace(4, intType);

        var callee = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = arg0.Local, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = arg1.Local, Name = "y", TypeId = intType, IsParameter = true }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            name: "sum2",
            symbolId: calleeSymbol);

        var caller = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = arg0.Local, Name = "a", TypeId = intType, IsParameter = true },
                new MirLocal { Id = arg1.Local, Name = "b", TypeId = intType, IsParameter = true },
                new MirLocal { Id = partial.Local, Name = "f", TypeId = partialType },
                new MirLocal { Id = result.Local, Name = "ret", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partial,
                    Function = new MirFunctionRef
                    {
                        Name = "sum2",
                        SymbolId = calleeSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [arg0]
                },
                new MirCall
                {
                    Target = result,
                    Function = partial,
                    Arguments = [arg1]
                }
            ],
            returnValue: result,
            name: "caller_curried",
            symbolId: new SymbolId(951));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "test_curried_call",
            Functions = [callee, caller]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller_curried");
        var calls = llvmCaller.BasicBlocks.Single().Instructions.OfType<LlvmCall>().ToList();
        var call = Assert.Single(calls);
        var functionGlobal = Assert.IsType<LlvmGlobal>(call.Function);
        var sumName = SingleFunctionNameBySourceName(llvmModule, "sum2");
        Assert.Equal(sumName, functionGlobal.Name);
        Assert.Equal(2, call.Arguments.Count);

        var ir = new LlvmEmitter().Emit(llvmModule);
        Assert.Contains($"call i64 @{sumName}(i64 %a, i64 %b)", ir);
        Assert.DoesNotContain("call i64 %", ir);
    }

    [Fact]
    public void Convert_FunctionParameterCurriedCall_InvokesReturnedClosure()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var outerFunctionType = new TypeId(9601);
        var innerFunctionType = new TypeId(9602);
        var reducer = LocalPlace(1, outerFunctionType);
        var arg0 = LocalPlace(2, intType);
        var arg1 = LocalPlace(3, intType);
        var partial = LocalPlace(4, innerFunctionType);
        var result = LocalPlace(5, intType);

        var caller = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = reducer.Local, Name = "reducer", TypeId = outerFunctionType, IsParameter = true },
                new MirLocal { Id = arg0.Local, Name = "a", TypeId = intType, IsParameter = true },
                new MirLocal { Id = arg1.Local, Name = "b", TypeId = intType, IsParameter = true },
                new MirLocal { Id = partial.Local, Name = "partial", TypeId = innerFunctionType },
                new MirLocal { Id = result.Local, Name = "ret", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partial,
                    Function = reducer,
                    Arguments = [arg0]
                },
                new MirCall
                {
                    Target = result,
                    Function = partial,
                    Arguments = [arg1]
                }
            ],
            returnValue: result,
            name: "caller_curried_param",
            symbolId: new SymbolId(9603));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "test_curried_param_call",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [outerFunctionType.Value] = $"Fun(T{intType.Value})->T{innerFunctionType.Value}",
                [innerFunctionType.Value] = $"Fun(T{intType.Value})->T{intType.Value}"
            },
            Functions = [caller]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller_curried_param");
        var calls = llvmCaller.BasicBlocks.Single().Instructions.OfType<LlvmCall>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(LlvmPointerType.VoidPtr(), calls[0].ReturnType);
        Assert.Equal(2, calls[0].Arguments.Count); // closure_ptr + a
        Assert.Equal(LlvmIntType.I64, calls[1].ReturnType);
        Assert.Equal(2, calls[1].Arguments.Count); // returned closure_ptr + b

        var ir = new LlvmEmitter().Emit(llvmModule);
        // The outer closure returns another closure, which is then invoked with b.
        Assert.Contains("i64 %a", ir);
        Assert.Contains("i64 %b", ir);
        Assert.DoesNotContain("call ptr %reducer(i64 %a)", ir);
    }

    [Fact]
    public void ConvertFunction_CallWithoutKnownCalleeSignature_FallsBackToVoid()
    {
        var unitType = new TypeId(BaseTypes.UnitId);

        var func = BuildFunction(
            unitType,
            locals: [],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "unknown_callee",
                        SymbolId = SymbolId.None,
                        TypeId = TypeId.None
                    },
                    Arguments = []
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            });

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var call = Assert.Single(llvmFunc.BasicBlocks.Single().Instructions.OfType<LlvmCall>());

        Assert.IsType<LlvmVoidType>(call.ReturnType);
        Assert.StartsWith("call void @eidos_unknown_callee", call.ToIrString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertFunction_ReturnPointerValueInIntFunction_CoercesRetToI64()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var valuePlace = LocalPlace(1, stringType);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = valuePlace.Local, Name = "p", TypeId = stringType, IsParameter = true }
            ],
            instructions: [],
            returnValue: valuePlace,
            name: "ret_ptr_in_int");

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        var cast = Assert.Single(entry.Instructions.OfType<LlvmCast>());
        Assert.Equal("ptrtoint", cast.Op);
        Assert.IsType<LlvmIntType>(cast.TargetType);

        var ret = Assert.IsType<LlvmRet>(entry.Terminator);
        var retValue = Assert.IsType<LlvmInstructionRef>(ret.Value);
        Assert.Same(cast, retValue.Instruction);
    }

    [Fact]
    public void ConvertFunction_RuntimeArrayLengthCall_UsesRuntimeNameAndI64Return()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var listType = new TypeId(900);
        var sourcePlace = LocalPlace(1, listType);
        var targetPlace = LocalPlace(2, intType);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = sourcePlace.Local, Name = "src", TypeId = listType, IsParameter = true },
                new MirLocal { Id = targetPlace.Local, Name = "len", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = targetPlace,
                    Function = new MirFunctionRef
                    {
                        Name = "array_length",
                        SymbolId = SymbolId.None,
                        TypeId = intType
                    },
                    Arguments = [sourcePlace]
                }
            ],
            returnValue: targetPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var call = Assert.Single(llvmFunc.BasicBlocks.Single().Instructions.OfType<LlvmCall>());

        Assert.IsType<LlvmIntType>(call.ReturnType);
        Assert.Contains("@eidos_array_length", call.ToIrString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_RuntimeArrayRangeAndTailShift_DeclareNativeRuntimeSignatures()
    {
        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "runtime_array_range_declarations"
        });

        var tailShift = Assert.Single(
            llvmModule.Declarations,
            declaration => declaration.Name == WellKnownStrings.Runtime.ArrayTailShiftPrepend);
        var rangeLength = Assert.Single(
            llvmModule.Declarations,
            declaration => declaration.Name == WellKnownStrings.Runtime.ArrayRangeLength);
        var rangeGet = Assert.Single(
            llvmModule.Declarations,
            declaration => declaration.Name == WellKnownStrings.Runtime.ArrayRangeGet);
        var uniqueUnmanaged16 = Assert.Single(
            llvmModule.Declarations,
            declaration => declaration.Name == WellKnownStrings.Runtime.ArrayTailShiftPrependUniqueUnmanaged16);

        var tailShiftType = Assert.IsType<LlvmFunctionType>(tailShift.Type);
        Assert.IsType<LlvmPointerType>(tailShiftType.ReturnType);
        Assert.Equal(4, tailShiftType.ParameterTypes.Count);

        var rangeLengthType = Assert.IsType<LlvmFunctionType>(rangeLength.Type);
        Assert.IsType<LlvmIntType>(rangeLengthType.ReturnType);
        Assert.Equal(3, rangeLengthType.ParameterTypes.Count);

        var rangeGetType = Assert.IsType<LlvmFunctionType>(rangeGet.Type);
        Assert.IsType<LlvmPointerType>(rangeGetType.ReturnType);
        Assert.Equal(4, rangeGetType.ParameterTypes.Count);

        var uniqueUnmanaged16Type = Assert.IsType<LlvmFunctionType>(uniqueUnmanaged16.Type);
        Assert.IsType<LlvmPointerType>(uniqueUnmanaged16Type.ReturnType);
        Assert.Equal(3, uniqueUnmanaged16Type.ParameterTypes.Count);
    }

    [Fact]
    public void Convert_OptimizationVariantCaller_IsAlwaysInlineWithoutPrivateLinkage()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var unit = new MirConstant
        {
            TypeId = unitType,
            Value = new MirConstantValue.UnitValue()
        };
        var variantId = new FunctionId
        {
            Name = "variant",
            QualifiedName = "variant",
            StableIdentityKey = "test:variant|unique:0"
        };
        var variant = BuildFunction(
            unitType,
            locals: [],
            instructions: [],
            returnValue: unit,
            name: "variant",
            functionId: variantId);
        var caller = BuildFunction(
            unitType,
            locals: [],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "variant",
                        FunctionId = variantId,
                        TypeId = unitType
                    },
                    Arguments = []
                }
            ],
            returnValue: unit,
            name: "caller");

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "variant_caller_linkage",
            Functions = [caller, variant]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller");
        Assert.Equal(LlvmLinkage.External, llvmCaller.Linkage);
        Assert.Contains(0, llvmCaller.AttributeIds);
        var llvmVariant = SingleFunctionBySourceName(llvmModule, "variant");
        Assert.Equal(LlvmLinkage.Private, llvmVariant.Linkage);
        Assert.Contains(0, llvmVariant.AttributeIds);
        var attributes = Assert.Single(llvmModule.AttributeGroups);
        Assert.Contains("alwaysinline", attributes.Attributes);
    }

    [Fact]
    public void ConvertFunction_RuntimeStringBuiltinCalls_UseRuntimeNamesAndI64Return()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var stringPlace = LocalPlace(1, stringType);
        var lengthPlace = LocalPlace(2, intType);
        var charPlace = LocalPlace(3, intType);

        var caller = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = stringPlace.Local, Name = "src", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = lengthPlace.Local, Name = "len", TypeId = intType },
                new MirLocal { Id = charPlace.Local, Name = "ch", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = lengthPlace,
                    Function = new MirFunctionRef
                    {
                        Name = "string_length",
                        SymbolId = SymbolId.None,
                        TypeId = intType
                    },
                    Arguments = [stringPlace]
                },
                new MirCall
                {
                    Target = charPlace,
                    Function = new MirFunctionRef
                    {
                        Name = "string_char_at",
                        SymbolId = SymbolId.None,
                        TypeId = intType
                    },
                    Arguments =
                    [
                        stringPlace,
                        new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        }
                    ]
                }
            ],
            returnValue: charPlace,
            name: "caller_string_builtins",
            symbolId: new SymbolId(1800));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "string_builtin_runtime",
            Functions = [caller]
        });

        var llvmFunc = SingleFunctionBySourceName(llvmModule, "caller_string_builtins");
        var calls = llvmFunc.BasicBlocks.Single().Instructions.OfType<LlvmCall>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, call => call.ToIrString().Contains("@eidos_string_length", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.ToIrString().Contains("@eidos_string_char_at", StringComparison.Ordinal));
        Assert.All(calls, call => Assert.IsType<LlvmIntType>(call.ReturnType));

        Assert.Contains(llvmModule.Declarations, declaration => declaration.Name == "eidos_string_length");
        Assert.Contains(llvmModule.Declarations, declaration => declaration.Name == "eidos_string_char_at");
    }

    [Fact]
    public void ConvertFunction_RuntimeOutputBridges_UseSemanticRuntimeNamesAndVoidReturn()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var stringPlace = LocalPlace(1, stringType);

        var caller = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = stringPlace.Local, Name = "src", TypeId = stringType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "write_text_raw",
                        SymbolId = new SymbolId(1803),
                        FunctionId = MirBuiltinFunctions.CreateIntrinsicFunctionId(
                            new SymbolId(1803),
                            "write_text_raw"),
                        TypeId = unitType
                    },
                    Arguments = [stringPlace]
                },
                new MirCall
                {
                    Function = new MirFunctionRef
                    {
                        Name = "write_char_code_raw",
                        SymbolId = new SymbolId(1804),
                        FunctionId = MirBuiltinFunctions.CreateIntrinsicFunctionId(
                            new SymbolId(1804),
                            "write_char_code_raw"),
                        TypeId = unitType
                    },
                    Arguments =
                    [
                        new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(33)
                        }
                    ]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_print_runtime",
            symbolId: new SymbolId(1802));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "print_runtime",
            Functions = [caller]
        });

        var llvmFunc = SingleFunctionBySourceName(llvmModule, "caller_print_runtime");
        var calls = llvmFunc.BasicBlocks.Single().Instructions.OfType<LlvmCall>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, call => call.ToIrString().Contains("@eidos_write_text_raw", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.ToIrString().Contains("@eidos_write_char_code_raw", StringComparison.Ordinal));
        Assert.All(calls, call => Assert.IsType<LlvmVoidType>(call.ReturnType));

        Assert.Contains(llvmModule.Declarations, declaration => declaration.Name == "eidos_write_text_raw");
        Assert.Contains(llvmModule.Declarations, declaration => declaration.Name == "eidos_write_char_code_raw");
    }

    [Fact]
    public void ConvertFunction_RuntimeTypeIdBuiltin_UsesRuntimeNameAndI64Return()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var adtType = new TypeId(1910);
        var adtParam = LocalPlace(1, adtType);
        var resultPlace = LocalPlace(2, intType);

        var caller = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = adtParam.Local, Name = "obj", TypeId = adtType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "tag", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = resultPlace,
                    Function = new MirFunctionRef
                    {
                        Name = "type_id",
                        SymbolId = SymbolId.None,
                        TypeId = intType
                    },
                    Arguments = [adtParam]
                }
            ],
            returnValue: resultPlace,
            name: "caller_type_id",
            symbolId: new SymbolId(1911));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "type_id_runtime",
            Functions = [caller]
        });

        var llvmFunc = SingleFunctionBySourceName(llvmModule, "caller_type_id");
        var call = Assert.Single(llvmFunc.BasicBlocks.Single().Instructions.OfType<LlvmCall>());
        Assert.Contains("@eidos_type_id", call.ToIrString(), StringComparison.Ordinal);
        Assert.IsType<LlvmIntType>(call.ReturnType);

        var declaration = Assert.Single(llvmModule.Declarations, item => item.Name == "eidos_type_id");
        var functionType = Assert.IsType<LlvmFunctionType>(declaration.Type);
        Assert.IsType<LlvmIntType>(functionType.ReturnType);
        Assert.Single(functionType.ParameterTypes);
    }

    [Fact]
    public void ConvertFunction_RuntimeTypeIdBuiltin_ExactSingleConstructorTypeUsesStaticTag()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var recordType = new TypeId(1912);
        var recordParam = LocalPlace(1, recordType);
        var resultPlace = LocalPlace(2, intType);
        const int runtimeTypeId = 0x12345678;
        var caller = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = recordParam.Local, Name = "value", TypeId = recordType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "tag", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = resultPlace,
                    Function = new MirFunctionRef
                    {
                        Name = WellKnownStrings.InternalNames.TypeId,
                        SymbolId = SymbolId.None,
                        FunctionId = MirRuntimeFunctions.CreateFunctionId(WellKnownStrings.InternalNames.TypeId),
                        TypeId = intType
                    },
                    Arguments = [recordParam]
                }
            ],
            returnValue: resultPlace,
            name: "caller_inline_record_type_id",
            symbolId: new SymbolId(1913));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "inline_record_type_id",
            Functions = [caller],
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [recordType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Range",
                        ConstructorName = "Range",
                        RuntimeTypeId = runtimeTypeId,
                        FieldTypeIds = [intType, intType]
                    }
                ]
            }
        });

        var llvmFunction = SingleFunctionBySourceName(llvmModule, "caller_inline_record_type_id");
        Assert.DoesNotContain(
            llvmFunction.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmCall>(),
            call => call.Function is LlvmGlobal { Name: "eidos_type_id" });
        var materializedTag = Assert.Single(
            llvmFunction.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmBinOp>());
        var tag = Assert.IsType<LlvmConstant>(materializedTag.Right);
        Assert.Equal((long)runtimeTypeId, tag.Value);
        Assert.Equal(64, Assert.IsType<LlvmIntType>(tag.Type).Bits);
    }

    [Fact]
    public void Convert_ModuleUnknownAdtConstructor_InlinesAllocationWithFieldStores()
    {
        var adtType = new TypeId(1920);
        var arg0 = LocalPlace(1, adtType);
        var arg1 = LocalPlace(2, adtType);
        var result = LocalPlace(3, adtType);

        var caller = BuildFunction(
            adtType,
            locals:
            [
                new MirLocal { Id = arg0.Local, Name = "head", TypeId = adtType, IsParameter = true },
                new MirLocal { Id = arg1.Local, Name = "tail", TypeId = adtType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "node", TypeId = adtType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "TokCons",
                        SymbolId = SymbolId.None,
                        SymbolKind = SymbolKind.Constructor,
                        TypeId = adtType
                    },
                    Arguments = [arg0, arg1]
                }
            ],
            returnValue: result,
            name: "mk_tok_cons",
            symbolId: new SymbolId(1921));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "adt_ctor_stub",
            Functions = [caller]
        });

        var llvmFunc = Assert.Single(llvmModule.Functions);
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        var allocCall = Assert.Single(
            entry.Instructions.OfType<LlvmCall>(),
            call => call.Function is LlvmGlobal { Name: "eidos_alloc" });

        Assert.Equal(2, entry.Instructions.OfType<LlvmStore>().Count());
        Assert.Equal(2, entry.Instructions.OfType<LlvmGetElementPtr>().Count());

        var payloadSize = Assert.IsType<LlvmConstant>(allocCall.Arguments[0]);
        Assert.Equal(16L, payloadSize.Value);

        var typeId = Assert.IsType<LlvmConstant>(allocCall.Arguments[1]);
        Assert.Equal(AdtConstructorTypeId.ComputeFromSymbol("TokCons"), typeId.Value);

        var ir = new LlvmEmitter().Emit(llvmModule);
        Assert.Contains("getelementptr i8, ptr %ctor_alloc", ir, StringComparison.Ordinal);
        Assert.Contains("i64 0", ir, StringComparison.Ordinal);
        Assert.Contains("i64 8", ir, StringComparison.Ordinal);

        Assert.DoesNotContain(llvmModule.Functions, function => function.Name == "eidos_TokCons");
        Assert.DoesNotContain(llvmModule.Declarations, declaration => declaration.Name == "eidos_TokCons");
    }

    [Fact]
    public void Convert_PayloadlessAdtConstructor_UsesScalarTagWithoutAllocation()
    {
        var directionType = new TypeId(1925);
        var result = LocalPlace(1, directionType);
        var constructor = new MirFunctionRef
        {
            Name = "West",
            SymbolId = new SymbolId(1926),
            SymbolKind = SymbolKind.Constructor,
            TypeId = directionType
        };
        var caller = BuildFunction(
            directionType,
            locals: [new MirLocal { Id = result.Local, Name = "direction", TypeId = directionType }],
            instructions: [new MirCall { Target = result, Function = constructor }],
            returnValue: result,
            name: "mk_west",
            symbolId: new SymbolId(1927));
        var runtimeTag = AdtConstructorTypeId.ComputeFromSymbol("West");

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "scalar_adt_ctor",
            Functions = [caller],
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [directionType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Direction",
                        ConstructorName = "West",
                        RuntimeTypeId = runtimeTag,
                        FieldTypeIds = []
                    },
                    new ConstructorTypeLayout
                    {
                        TypeName = "Direction",
                        ConstructorName = "East",
                        RuntimeTypeId = AdtConstructorTypeId.ComputeFromSymbol("East"),
                        FieldTypeIds = []
                    }
                ]
            }
        });

        var llvmFunction = Assert.Single(llvmModule.Functions);
        Assert.DoesNotContain(
            llvmFunction.BasicBlocks.Single().Instructions.OfType<LlvmCall>(),
            call => call.Function is LlvmGlobal { Name: "eidos_alloc" });
        var materializedTag = Assert.Single(llvmFunction.BasicBlocks.Single().Instructions.OfType<LlvmBinOp>());
        var tag = Assert.IsType<LlvmConstant>(materializedTag.Right);
        Assert.Equal(runtimeTag, tag.Value);
        Assert.Equal(32, Assert.IsType<LlvmIntType>(tag.Type).Bits);
        var ret = Assert.IsType<LlvmRet>(llvmFunction.BasicBlocks.Single().Terminator);
        Assert.Equal(32, Assert.IsType<LlvmIntType>(Assert.IsType<LlvmLocal>(ret.Value).Type).Bits);
    }

    [Fact]
    public void Convert_ReuseSlot_ZeroInitializesBeforeDropAndAllocation()
    {
        var recordType = new TypeId(1928);
        var intType = new TypeId(BaseTypes.IntId);
        var functionSymbol = new SymbolId(1929);
        var oldValue = LocalPlace(1, recordType);
        var replacement = LocalPlace(2, recordType);
        var blockId = new BlockId { Value = 1 };
        var caller = BuildFunction(
            recordType,
            locals:
            [
                new MirLocal { Id = oldValue.Local, Name = "old", TypeId = recordType, IsParameter = true },
                new MirLocal { Id = replacement.Local, Name = "replacement", TypeId = recordType }
            ],
            instructions:
            [
                new MirDrop { Value = oldValue },
                new MirCall
                {
                    Target = replacement,
                    Function = new MirFunctionRef
                    {
                        Name = "Record",
                        SymbolId = new SymbolId(1930),
                        SymbolKind = SymbolKind.Constructor,
                        TypeId = recordType
                    },
                    Arguments =
                    [
                        new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(7)
                        }
                    ]
                }
            ],
            returnValue: replacement,
            name: "reuse_record",
            symbolId: functionSymbol);
        var hints = new ReuseHints { SlotCount = 1 };
        hints.DropReuseSites[(blockId, 0)] = 0;
        hints.AllocReuseSites[(blockId, 1)] = 0;
        var borrowResult = new ModuleBorrowCheckResult();
        borrowResult.AddResult(new BorrowCheckResult
        {
            FunctionName = caller.Name,
            FunctionSymbolId = functionSymbol,
            ReuseHints = hints
        });
        var converter = new MirToLlvmConverter();
        converter.SetReuseHints(borrowResult);

        var llvmModule = converter.Convert(new MirModule { Name = "reuse_slot", Functions = [caller] });

        var instructions = Assert.Single(llvmModule.Functions).BasicBlocks.Single().Instructions;
        var allocaIndex = instructions.FindIndex(static instruction => instruction is LlvmAlloca
        {
            AllocatedType: LlvmStructType { Fields.Count: 3 }
        });
        var initializeIndex = instructions.FindIndex(static instruction => instruction is LlvmStore
        {
            Value: LlvmZeroInitializer
        });
        var dropIndex = instructions.FindIndex(static instruction => instruction is LlvmCall
        {
            Function: LlvmGlobal { Name: "eidos_drop_reuse" }
        });
        var allocIndex = instructions.FindIndex(static instruction => instruction is LlvmCall
        {
            Function: LlvmGlobal { Name: "eidos_alloc_reuse" }
        });
        Assert.True(allocaIndex >= 0);
        Assert.True(initializeIndex > allocaIndex);
        Assert.True(dropIndex > initializeIndex);
        Assert.True(allocIndex > dropIndex);
    }

    [Fact]
    public void ConvertFunction_InlineAdtConstructorMissingTarget_ReportsDiagnostic()
    {
        var adtType = new TypeId(1930);
        var arg0 = LocalPlace(1, adtType);
        var span = new SourceSpan(new SourceLocation(31, 1, 12), 7);
        var func = BuildFunction(
            adtType,
            locals:
            [
                new MirLocal { Id = arg0.Local, Name = "head", TypeId = adtType, IsParameter = true }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = null,
                    Function = new MirFunctionRef
                    {
                        Name = "TokBox",
                        SymbolId = SymbolId.None,
                        SymbolKind = SymbolKind.Constructor,
                        TypeId = adtType,
                        Span = span
                    },
                    Arguments = [arg0],
                    Span = span
                }
            ],
            returnValue: arg0,
            name: "ctor_missing_target");

        var converter = new MirToLlvmConverter();
        var llvmFunc = converter.ConvertFunction(func);

        Assert.Contains(
            converter.Diagnostics,
            diagnostic => diagnostic.Code == "E5306" &&
                          diagnostic.Message.Contains("Missing MIR target place", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("constructor inline allocation", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Assert.Single(llvmFunc.BasicBlocks).Instructions.OfType<LlvmCall>(),
            call => call.Function is LlvmGlobal { Name: "eidos_alloc" });
    }

    private static bool InvokeTryInferFunctionReferenceValueType(
        MirToLlvmConverter converter,
        MirFunctionRef functionRef,
        out LlvmType inferredType)
    {
        var method = typeof(MirToLlvmConverter).GetMethod(
            "TryInferFunctionReferenceValueType",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] arguments = [functionRef, null];
        var resolved = Assert.IsType<bool>(method.Invoke(converter, arguments));
        inferredType = arguments[1] as LlvmType ?? null!;
        return resolved;
    }

}
