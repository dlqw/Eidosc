using Eidosc;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public partial class MirToLlvmConverterTests
{
    [Fact]
    public void ConvertFunction_FieldLoad_UsesByteOffsetDerivedFromFieldName()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var adtType = new TypeId(1922);
        var sourcePlace = LocalPlace(1, adtType);
        var targetPlace = LocalPlace(2, intType);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = sourcePlace.Local, Name = "obj", TypeId = adtType, IsParameter = true },
                new MirLocal { Id = targetPlace.Local, Name = "field", TypeId = intType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = targetPlace,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = sourcePlace,
                        FieldName = "_1",
                        TypeId = intType
                    }
                }
            ],
            returnValue: targetPlace,
            name: "load_field");

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        var gep = Assert.Single(entry.Instructions.OfType<LlvmGetElementPtr>());
        var offset = Assert.IsType<LlvmConstant>(gep.Index);
        Assert.Equal(8L, offset.Value);
        Assert.Same(LlvmIntType.I8, gep.ElementType);
        Assert.Contains(", i64 8", gep.ToIrString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertFunction_FieldLoad_WithUnresolvedFieldName_ReportsDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var adtType = new TypeId(1923);
        var sourcePlace = LocalPlace(1, adtType);
        var targetPlace = LocalPlace(2, intType);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = sourcePlace.Local, Name = "obj", TypeId = adtType, IsParameter = true },
                new MirLocal { Id = targetPlace.Local, Name = "field", TypeId = intType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = targetPlace,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = sourcePlace,
                        FieldName = "value",
                        TypeId = intType
                    }
                }
            ],
            returnValue: targetPlace,
            name: "load_unresolved_field");

        var converter = new MirToLlvmConverter();
        var llvmFunc = converter.ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        var gep = Assert.Single(entry.Instructions.OfType<LlvmGetElementPtr>());
        var offset = Assert.IsType<LlvmConstant>(gep.Index);

        Assert.Equal(0L, Convert.ToInt64(offset.Value));
        Assert.Contains(converter.Diagnostics, diagnostic => diagnostic.Code == "E3301");
    }

    [Fact]
    public void ConvertFunction_RuntimeStringSliceBuiltin_UsesRuntimeNameAndPointerReturn()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var stringPlace = LocalPlace(1, stringType);
        var resultPlace = LocalPlace(2, stringType);

        var caller = BuildFunction(
            stringType,
            locals:
            [
                new MirLocal { Id = stringPlace.Local, Name = "src", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "slice", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = resultPlace,
                    Function = new MirFunctionRef
                    {
                        Name = "string_slice",
                        SymbolId = SymbolId.None,
                        TypeId = stringType
                    },
                    Arguments =
                    [
                        stringPlace,
                        new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(1)
                        }
                    ]
                }
            ],
            returnValue: resultPlace,
            name: "caller_string_slice",
            symbolId: new SymbolId(1801));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "string_slice_runtime",
            Functions = [caller]
        });

        var llvmFunc = SingleFunctionBySourceName(llvmModule, "caller_string_slice");
        var call = Assert.Single(llvmFunc.BasicBlocks.Single().Instructions.OfType<LlvmCall>());
        Assert.Contains("@eidos_string_slice", call.ToIrString(), StringComparison.Ordinal);
        Assert.IsType<LlvmPointerType>(call.ReturnType);

        var declaration = Assert.Single(llvmModule.Declarations, item => item.Name == "eidos_string_slice");
        var functionType = Assert.IsType<LlvmFunctionType>(declaration.Type);
        Assert.IsType<LlvmPointerType>(functionType.ReturnType);
        Assert.Equal(3, functionType.ParameterTypes.Count);
    }

    [Fact]
    public void ConvertFunction_IndexStore_UsesRuntimeArraySetCall()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var listType = new TypeId(901);
        var arrayPlace = LocalPlace(1, listType);
        var valuePlace = LocalPlace(2, intType);

        var func = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = arrayPlace.Local, Name = "arr", TypeId = listType, IsParameter = true },
                new MirLocal { Id = valuePlace.Local, Name = "value", TypeId = intType, IsParameter = true }
            ],
            instructions:
            [
                new MirStore
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = arrayPlace,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                        TypeId = intType
                    },
                    Value = valuePlace
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            });

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        var arraySetCall = Assert.Single(
            entry.Instructions.OfType<LlvmCall>(),
            call => call.Function is LlvmGlobal { Name: "eidos_array_set" });

        Assert.IsType<LlvmVoidType>(arraySetCall.ReturnType);
    }

    [Fact]
    public void ConvertFunction_IndexLoad_UsesRuntimeArrayGetCallThenTypedLoad()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var listType = new TypeId(902);
        var arrayPlace = LocalPlace(1, listType);
        var resultPlace = LocalPlace(2, intType);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = arrayPlace.Local, Name = "arr", TypeId = listType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "value", TypeId = intType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = resultPlace,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = arrayPlace,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                        TypeId = intType
                    }
                }
            ],
            returnValue: resultPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);

        var arrayGetCall = Assert.Single(
            entry.Instructions.OfType<LlvmCall>(),
            call => call.Function is LlvmGlobal { Name: "eidos_array_get" });
        Assert.IsType<LlvmPointerType>(arrayGetCall.ReturnType);

        var valueLoad = Assert.Single(entry.Instructions.OfType<LlvmLoad>());
        Assert.IsType<LlvmIntType>(valueLoad.LoadType);
    }

    [Fact]
    public void ConvertFunction_AggregateIndexStore_DoesNotUseRuntimeArraySet()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var tupleType = new TypeId(903);
        var aggregatePlace = LocalPlace(1, tupleType);
        var valuePlace = LocalPlace(2, intType);

        var func = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = aggregatePlace.Local, Name = "agg", TypeId = tupleType, IsParameter = true },
                new MirLocal { Id = valuePlace.Local, Name = "value", TypeId = intType, IsParameter = true }
            ],
            instructions:
            [
                new MirStore
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = aggregatePlace,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.Aggregate,
                        TypeId = intType
                    },
                    Value = valuePlace
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            });

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);

        Assert.DoesNotContain(
            entry.Instructions.OfType<LlvmCall>(),
            call => call.Function is LlvmGlobal { Name: "eidos_array_set" });
        Assert.Contains(entry.Instructions, instr => instr is LlvmStore);
    }

    [Fact]
    public void ConvertFunction_AggregateIndexLoad_DoesNotUseRuntimeArrayGet()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tupleType = new TypeId(904);
        var aggregatePlace = LocalPlace(1, tupleType);
        var resultPlace = LocalPlace(2, intType);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = aggregatePlace.Local, Name = "agg", TypeId = tupleType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "value", TypeId = intType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = resultPlace,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = aggregatePlace,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.Aggregate,
                        TypeId = intType
                    }
                }
            ],
            returnValue: resultPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);

        Assert.DoesNotContain(
            entry.Instructions.OfType<LlvmCall>(),
            call => call.Function is LlvmGlobal { Name: "eidos_array_get" });
        Assert.Contains(entry.Instructions, instr => instr is LlvmLoad);
    }

    [Fact]
    public void ConvertFunction_RuntimeArrayLoad_WithTyVarElement_InfersElementTypeFromListBase()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tyVarType = new TypeId(9045);
        var listType = new TypeId(9046);
        var listPlace = LocalPlace(1, listType);
        var resultPlace = LocalPlace(2, intType);
        var erasedListBase = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = listPlace.Local,
            TypeId = TypeId.None
        };

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = listPlace.Local, Name = "xs", TypeId = listType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "value", TypeId = intType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = resultPlace,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = erasedListBase,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                        TypeId = tyVarType
                    }
                }
            ],
            returnValue: resultPlace);

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "runtime_array_tyvar_load",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tyVarType.Value] = "TyVar_0",
                [listType.Value] = $"TyCon(type:{listType.Value};T{intType.Value})"
            },
            Functions = [func]
        });

        var llvmFunc = Assert.Single(llvmModule.Functions);
        var load = Assert.Single(llvmFunc.BasicBlocks.Single().Instructions.OfType<LlvmLoad>());
        var loadType = Assert.IsType<LlvmIntType>(load.LoadType);
        Assert.Equal(64, loadType.Bits);
    }

    [Fact]
    public void ConvertFunction_RuntimeArrayLoad_WithOpaquePointerElement_InfersElementTypeFromListBase()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var erasedElementType = new TypeId(9047);
        var listType = new TypeId(9048);
        var listPlace = LocalPlace(1, listType);
        var resultPlace = LocalPlace(2, intType);
        var erasedListBase = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = listPlace.Local,
            TypeId = TypeId.None
        };

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = listPlace.Local, Name = "xs", TypeId = listType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "value", TypeId = intType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = resultPlace,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = erasedListBase,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                        TypeId = erasedElementType
                    }
                }
            ],
            returnValue: resultPlace);

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "runtime_array_erased_load",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [listType.Value] = $"TyCon(type:{listType.Value};T{intType.Value})"
            },
            Functions = [func]
        });

        var llvmFunc = Assert.Single(llvmModule.Functions);
        var load = Assert.Single(llvmFunc.BasicBlocks.Single().Instructions.OfType<LlvmLoad>());
        var loadType = Assert.IsType<LlvmIntType>(load.LoadType);
        Assert.Equal(64, loadType.Bits);
    }

    [Fact]
    public void ConvertFunction_RuntimeArrayLoad_WithOpaqueElement_InfersTypeFromSpecializedCallUse()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var listType = new TypeId(9049);
        var predType = new TypeId(9050);
        var erasedElementType = new TypeId(9051);
        var listPlace = LocalPlace(1, listType);
        var predPlace = LocalPlace(2, predType);
        var loadResult = LocalPlace(3, erasedElementType);
        var aliasResult = LocalPlace(4, erasedElementType);
        var callResult = LocalPlace(5, boolType);
        var erasedListBase = new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = listPlace.Local,
            TypeId = TypeId.None
        };

        var func = BuildFunction(
            boolType,
            locals:
            [
                new MirLocal { Id = listPlace.Local, Name = "xs", TypeId = listType, IsParameter = true },
                new MirLocal { Id = predPlace.Local, Name = "pred", TypeId = predType, IsParameter = true },
                new MirLocal { Id = loadResult.Local, Name = "value", TypeId = erasedElementType },
                new MirLocal { Id = aliasResult.Local, Name = "alias", TypeId = erasedElementType },
                new MirLocal { Id = callResult.Local, Name = "matched", TypeId = boolType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = loadResult,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = erasedListBase,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                        TypeId = erasedElementType
                    }
                },
                new MirCopy
                {
                    Target = aliasResult,
                    Source = loadResult
                },
                new MirCall
                {
                    Target = callResult,
                    Function = predPlace,
                    Arguments = [aliasResult]
                }
            ],
            returnValue: callResult);

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "runtime_array_infer_from_use",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [predType.Value] = $"Fun(T{intType.Value})->T{boolType.Value}"
            },
            Functions = [func]
        });

        var llvmFunc = Assert.Single(llvmModule.Functions);
        // The function may have multiple loads (array load + closure invoke pointer load).
        // Find the array load by its i64 load type.
        var arrayLoad = llvmFunc.BasicBlocks.Single().Instructions.OfType<LlvmLoad>()
            .Single(load => load.LoadType is LlvmIntType);
        var loadType = Assert.IsType<LlvmIntType>(arrayLoad.LoadType);
        Assert.Equal(64, loadType.Bits);
    }

    [Fact]
    public void Convert_ModuleAggregateIndexLoad_FromCallResultMaterializesAggregateStorage()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tupleType = new TypeId(9041);
        var calleeSymbol = new SymbolId(9042);
        var tupleArg = LocalPlace(1, tupleType);
        var tupleResult = LocalPlace(2, tupleType);
        var itemResult = LocalPlace(3, intType);

        var callee = BuildFunction(
            tupleType,
            locals:
            [
                new MirLocal { Id = tupleArg.Local, Name = "pair", TypeId = tupleType, IsParameter = true }
            ],
            instructions: [],
            returnValue: tupleArg,
            name: "pair_id",
            symbolId: calleeSymbol);

        var caller = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = tupleArg.Local, Name = "pair", TypeId = tupleType, IsParameter = true },
                new MirLocal { Id = tupleResult.Local, Name = "tmp", TypeId = tupleType },
                new MirLocal { Id = itemResult.Local, Name = "value", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = tupleResult,
                    Function = new MirFunctionRef
                    {
                        Name = "pair_id",
                        SymbolId = calleeSymbol,
                        TypeId = tupleType
                    },
                    Arguments = [tupleArg]
                },
                new MirLoad
                {
                    Target = itemResult,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = tupleResult,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.Aggregate,
                        TypeId = intType
                    }
                }
            ],
            returnValue: itemResult,
            name: "caller_tuple_from_call",
            symbolId: new SymbolId(9043));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "aggregate_call_result_load",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tupleType.Value] = $"Tuple(T{intType.Value},T{intType.Value})"
            },
            Functions = [callee, caller]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller_tuple_from_call");
        var entry = llvmCaller.BasicBlocks.Single();
        var gep = Assert.Single(entry.Instructions.OfType<LlvmGetElementPtr>());
        var pointerRef = Assert.IsType<LlvmInstructionRef>(gep.Pointer);
        Assert.IsType<LlvmAlloca>(pointerRef.Instruction);
    }

    [Fact]
    public void ConvertFunction_AggregateAlloc_UsesByteAddressedTupleStorage()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var tupleType = new TypeId(905);
        var aggregatePlace = LocalPlace(1, tupleType);
        var intValuePlace = LocalPlace(2, intType);
        var boolValuePlace = LocalPlace(3, boolType);

        var func = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = aggregatePlace.Local, Name = "agg", TypeId = tupleType },
                new MirLocal { Id = intValuePlace.Local, Name = "lhs", TypeId = intType, IsParameter = true },
                new MirLocal { Id = boolValuePlace.Local, Name = "rhs", TypeId = boolType, IsParameter = true }
            ],
            instructions:
            [
                new MirAlloc
                {
                    Target = aggregatePlace,
                    TypeId = tupleType
                },
                new MirStore
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = aggregatePlace,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.Aggregate,
                        TypeId = intType
                    },
                    Value = intValuePlace
                },
                new MirStore
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = aggregatePlace,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(1)
                        },
                        IndexAccessKind = MirIndexAccessKind.Aggregate,
                        TypeId = boolType
                    },
                    Value = boolValuePlace
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            });

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);

        var alloc = Assert.Single(entry.Instructions.OfType<LlvmAlloca>());
        var allocType = Assert.IsType<LlvmArrayType>(alloc.AllocatedType);
        Assert.Same(LlvmIntType.I8, allocType.Element);
        Assert.Equal(16, allocType.Size);

        var geps = entry.Instructions.OfType<LlvmGetElementPtr>().ToList();
        Assert.Equal(2, geps.Count);
        Assert.All(geps, gep => Assert.Same(LlvmIntType.I8, gep.ElementType));
        Assert.Equal(0L, Assert.IsType<LlvmConstant>(geps[0].Index).Value);
        Assert.Equal(8L, Assert.IsType<LlvmConstant>(geps[1].Index).Value);
    }

    [Fact]
    public void ConvertFunction_ReturnAggregateLocal_LoadsStructuredValueBeforeRet()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tupleType = new TypeId(9051);
        var aggregatePlace = LocalPlace(1, tupleType);
        var leftValue = LocalPlace(2, intType);
        var rightValue = LocalPlace(3, intType);

        var func = BuildFunction(
            tupleType,
            locals:
            [
                new MirLocal { Id = aggregatePlace.Local, Name = "agg", TypeId = tupleType },
                new MirLocal { Id = leftValue.Local, Name = "lhs", TypeId = intType, IsParameter = true },
                new MirLocal { Id = rightValue.Local, Name = "rhs", TypeId = intType, IsParameter = true }
            ],
            instructions:
            [
                new MirAlloc
                {
                    Target = aggregatePlace,
                    TypeId = tupleType
                },
                new MirStore
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = aggregatePlace,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.Aggregate,
                        TypeId = intType
                    },
                    Value = leftValue
                },
                new MirStore
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = aggregatePlace,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(1)
                        },
                        IndexAccessKind = MirIndexAccessKind.Aggregate,
                        TypeId = intType
                    },
                    Value = rightValue
                }
            ],
            returnValue: aggregatePlace);

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "aggregate_return",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tupleType.Value] = $"Tuple(T{intType.Value},T{intType.Value})"
            },
            Functions = [func]
        });

        var llvmFunc = Assert.Single(llvmModule.Functions);
        var ret = Assert.IsType<LlvmRet>(llvmFunc.BasicBlocks.Single().Terminator);
        var retValue = Assert.IsType<LlvmInstructionRef>(ret.Value);
        var retLoad = Assert.IsType<LlvmLoad>(retValue.Instruction);
        Assert.IsType<LlvmStructType>(retLoad.LoadType);
    }

    [Fact]
    public void ConvertFunction_FieldLoadFromAggregateIndex_MaterializesBaseValueBeforeFieldOffset()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tupleType = new TypeId(906);
        var adtType = new TypeId(907);
        var aggregatePlace = LocalPlace(1, tupleType);
        var resultPlace = LocalPlace(2, intType);

        var indexedAdtPlace = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = aggregatePlace,
            Index = new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = adtType
        };

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = aggregatePlace.Local, Name = "agg", TypeId = tupleType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "value", TypeId = intType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = resultPlace,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = indexedAdtPlace,
                        FieldName = "_0",
                        TypeId = intType
                    }
                }
            ],
            returnValue: resultPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);

        var loads = entry.Instructions.OfType<LlvmLoad>().ToList();
        Assert.Equal(2, loads.Count);
        Assert.IsType<LlvmPointerType>(loads[0].LoadType);
        Assert.IsType<LlvmIntType>(loads[1].LoadType);

        var geps = entry.Instructions.OfType<LlvmGetElementPtr>().ToList();
        Assert.Equal(2, geps.Count);
        Assert.All(geps, gep => Assert.Same(LlvmIntType.I8, gep.ElementType));

        var materializedBase = Assert.IsType<LlvmInstructionRef>(geps[1].Pointer);
        Assert.Same(loads[0], materializedBase.Instruction);
    }

    [Fact]
    public void ConvertFunction_RuntimeCallWithAggregateIndexArgument_LoadsFieldValueBeforePassingToCall()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tupleType = new TypeId(908);
        var listType = new TypeId(909);
        var aggregatePlace = LocalPlace(1, tupleType);
        var resultPlace = LocalPlace(2, intType);

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = aggregatePlace.Local, Name = "agg", TypeId = tupleType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "len", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = resultPlace,
                    Function = new MirFunctionRef
                    {
                        Name = "array_length",
                        TypeId = intType
                    },
                    Arguments =
                    [
                        new MirPlace
                        {
                            Kind = PlaceKind.Index,
                            Base = aggregatePlace,
                            Index = new MirConstant
                            {
                                TypeId = intType,
                                Value = new MirConstantValue.IntValue(0)
                            },
                            IndexAccessKind = MirIndexAccessKind.Aggregate,
                            TypeId = listType
                        }
                    ]
                }
            ],
            returnValue: resultPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);

        var gep = Assert.Single(entry.Instructions.OfType<LlvmGetElementPtr>());
        Assert.Same(LlvmIntType.I8, gep.ElementType);

        var load = Assert.Single(entry.Instructions.OfType<LlvmLoad>());
        Assert.IsType<LlvmPointerType>(load.LoadType);
        var loadPointer = Assert.IsType<LlvmInstructionRef>(load.Pointer);
        Assert.Same(gep, loadPointer.Instruction);

        var call = Assert.Single(entry.Instructions.OfType<LlvmCall>());
        var runtime = Assert.IsType<LlvmGlobal>(call.Function);
        Assert.Equal("eidos_array_length", runtime.Name);
        var argument = Assert.IsType<LlvmInstructionRef>(Assert.Single(call.Arguments));
        Assert.Same(load, argument.Instruction);
    }

    [Fact]
    public void ConvertFunction_RuntimeArrayLoadFromAggregateIndexBase_LoadsBaseArrayPointerBeforeArrayGet()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tupleType = new TypeId(910);
        var listType = new TypeId(911);
        var aggregatePlace = LocalPlace(1, tupleType);
        var resultPlace = LocalPlace(2, intType);

        var listFieldPlace = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = aggregatePlace,
            Index = new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = listType
        };

        var func = BuildFunction(
            intType,
            locals:
            [
                new MirLocal { Id = aggregatePlace.Local, Name = "agg", TypeId = tupleType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "value", TypeId = intType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = resultPlace,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = listFieldPlace,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                        TypeId = intType
                    }
                }
            ],
            returnValue: resultPlace);

        var llvmFunc = new MirToLlvmConverter().ConvertFunction(func);
        var entry = Assert.Single(llvmFunc.BasicBlocks);

        var geps = entry.Instructions.OfType<LlvmGetElementPtr>().ToList();
        Assert.Single(geps);

        var loads = entry.Instructions.OfType<LlvmLoad>().ToList();
        Assert.Equal(2, loads.Count);
        Assert.IsType<LlvmPointerType>(loads[0].LoadType);
        Assert.IsType<LlvmIntType>(loads[1].LoadType);

        var baseArrayLoadPointer = Assert.IsType<LlvmInstructionRef>(loads[0].Pointer);
        Assert.Same(geps[0], baseArrayLoadPointer.Instruction);

        var arrayGet = Assert.Single(entry.Instructions.OfType<LlvmCall>());
        var runtime = Assert.IsType<LlvmGlobal>(arrayGet.Function);
        Assert.Equal("eidos_array_get", runtime.Name);
        var arrayArgument = Assert.IsType<LlvmInstructionRef>(arrayGet.Arguments[0]);
        Assert.Same(loads[0], arrayArgument.Instruction);
    }

    [Fact]
    public void Convert_ModuleRuntimeArrayStore_WithTupleElement_UsesAggregateElementSize()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var listType = new TypeId(912);
        var tupleType = new TypeId(913);
        var listPlace = LocalPlace(1, listType);
        var tuplePlace = LocalPlace(2, tupleType);

        var func = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = listPlace.Local, Name = "xs", TypeId = listType, IsParameter = true },
                new MirLocal { Id = tuplePlace.Local, Name = "pair", TypeId = tupleType, IsParameter = true }
            ],
            instructions:
            [
                new MirStore
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = listPlace,
                        Index = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(0)
                        },
                        IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                        TypeId = tupleType
                    },
                    Value = tuplePlace
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "store_tuple");

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "test",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tupleType.Value] = $"Tuple(T{BaseTypes.IntId},T{BaseTypes.BoolId})"
            },
            Functions = [func]
        });

        var llvmFunc = SingleFunctionBySourceName(llvmModule, "store_tuple");
        var entry = Assert.Single(llvmFunc.BasicBlocks);
        var arraySet = Assert.Single(entry.Instructions.OfType<LlvmCall>());
        var runtime = Assert.IsType<LlvmGlobal>(arraySet.Function);
        Assert.Equal("eidos_array_set", runtime.Name);

        var sizeArg = Assert.IsType<LlvmConstant>(arraySet.Arguments[3]);
        Assert.Equal(16L, Convert.ToInt64(sizeArg.Value));
    }
}
