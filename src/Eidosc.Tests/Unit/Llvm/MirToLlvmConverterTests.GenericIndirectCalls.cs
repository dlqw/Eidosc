using Eidosc.Symbols;
using Eidosc;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Llvm;

public partial class MirToLlvmConverterTests
{
    [Fact]
    public void Convert_ModuleWithUnspecializedGenericCall_ReportsDiagnosticE5301()
    {
        var genericSymbol = new SymbolId(2201);
        var unitType = new TypeId(BaseTypes.UnitId);

        var genericId = BuildFunction(
            TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "id",
            symbolId: genericSymbol);

        var partial = LocalPlace(1, TypeId.None);
        var caller = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = partial.Local, Name = "p", TypeId = TypeId.None }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partial,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = []
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller",
            symbolId: new SymbolId(2202));

        var converter = new MirToLlvmConverter();
        _ = converter.Convert(new MirModule
        {
            Name = "generic_partial_call",
            Functions = [genericId, caller]
        });

        var diagnostic = Assert.Single(
            converter.Diagnostics,
            entry => entry.Code == "E5301" &&
                     entry.Message.Contains("escaped MIR specialization", StringComparison.Ordinal));
        Assert.Contains("remaining generic arity at call site: 1", diagnostic.Notes, StringComparer.Ordinal);
    }

    [Fact]
    public void Convert_ModuleWithSpecializationFailureInfo_AttachesFailureMetadataToE5301()
    {
        var genericSymbol = new SymbolId(2299);
        var unitType = new TypeId(BaseTypes.UnitId);

        var genericId = BuildFunction(
            TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "id",
            symbolId: genericSymbol);

        var partial = LocalPlace(1, TypeId.None);
        var caller = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = partial.Local, Name = "p", TypeId = TypeId.None }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partial,
                    Function = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = []
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_with_failure",
            symbolId: new SymbolId(2300));

        var converter = new MirToLlvmConverter();
        _ = converter.Convert(new MirModule
        {
            Name = "generic_partial_call_with_failure",
            Functions = [genericId, caller],
            SpecializationFailures =
            [
                new MirSpecializationFailureInfo
                {
                    Reason = "unresolved-types",
                    TemplateKey = $"sym:{genericSymbol.Value}",
                    TemplateName = "id",
                    SignatureKey = "0|",
                    SignatureDisplay = "0|",
                    PreviewName = "id__spec_deadbeef"
                }
            ]
        });

        var diagnostic = Assert.Single(
            converter.Diagnostics,
            entry => entry.Code == "E5301" &&
                     entry.Message.Contains("escaped MIR specialization", StringComparison.Ordinal));
        Assert.Equal("unresolved-types", diagnostic.Metadata["specialization.reason"]);
        Assert.Equal($"sym:{genericSymbol.Value}", diagnostic.Metadata["specialization.templateKey"]);
        Assert.Equal("0|", diagnostic.Metadata["specialization.signatureKey"]);
        Assert.Contains("MIR specialization failure reason: unresolved-types", diagnostic.Notes, StringComparer.Ordinal);
        Assert.Contains(
            diagnostic.Notes,
            note => note.Contains("provide concrete type arguments or add annotations", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_ModuleWithTraitSpecializationFailureInfo_AttachesFailureMetadataToE5301()
    {
        var genericSymbol = new SymbolId(2301);
        var traitId = new SymbolId(2302);
        var unitType = new TypeId(BaseTypes.UnitId);

        var traitMethod = BuildFunction(
            TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "display",
            symbolId: genericSymbol);

        var partial = LocalPlace(1, TypeId.None);
        var caller = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = partial.Local, Name = "p", TypeId = TypeId.None }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = partial,
                    Function = new MirFunctionRef
                    {
                        Name = "display",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = []
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_with_trait_failure",
            symbolId: new SymbolId(2303));

        var converter = new MirToLlvmConverter();
        _ = converter.Convert(new MirModule
        {
            Name = "generic_trait_call_with_failure",
            Functions = [traitMethod, caller],
            SpecializationFailures =
            [
                new MirSpecializationFailureInfo
                {
                    Reason = "no-concrete-dispatch-type",
                    TemplateKey = $"trait:{traitId.Value}:display",
                    TemplateName = "display",
                    SignatureKey = "trait-dispatch:0|",
                    SignatureDisplay = "trait-dispatch return:0 args:[] typeArgs:[]",
                    PreviewName = "display__spec_deadbeef"
                }
            ]
        });

        var diagnostic = Assert.Single(
            converter.Diagnostics,
            entry => entry.Code == "E5301" &&
                     entry.Message.Contains("escaped MIR specialization", StringComparison.Ordinal));
        Assert.Equal("no-concrete-dispatch-type", diagnostic.Metadata["specialization.reason"]);
        Assert.Equal($"trait:{traitId.Value}:display", diagnostic.Metadata["specialization.templateKey"]);
        Assert.Contains(
            diagnostic.Notes,
            note => note.Contains("trait Self metadata", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_ModuleWithUnspecializedIndirectGenericCall_ReportsDiagnosticE5301()
    {
        var genericSymbol = new SymbolId(2203);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);

        var genericId = BuildFunction(
            TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "id",
            symbolId: genericSymbol);

        var fnLocal = LocalPlace(1, TypeId.None);
        var argLocal = LocalPlace(2, intType);
        var resultLocal = LocalPlace(3, intType);
        var caller = BuildFunction(
            unitType,
            locals:
            [
                new MirLocal { Id = fnLocal.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argLocal.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = resultLocal.Local, Name = "res", TypeId = intType }
            ],
            instructions:
            [
                new MirAssign
                {
                    Target = fnLocal,
                    Source = new MirFunctionRef
                    {
                        Name = "id",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    }
                },
                new MirCall
                {
                    Target = resultLocal,
                    Function = fnLocal,
                    Arguments = [argLocal]
                }
            ],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "caller_indirect",
            symbolId: new SymbolId(2204));

        var converter = new MirToLlvmConverter();
        _ = converter.Convert(new MirModule
        {
            Name = "generic_indirect_call",
            Functions = [genericId, caller]
        });

        var diagnostic = Assert.Single(
            converter.Diagnostics,
            entry => entry.Code == "E5301" &&
                     entry.Message.Contains("Indirect call through local", StringComparison.Ordinal));
        Assert.Contains("remaining generic arity at call site: 1", diagnostic.Notes, StringComparer.Ordinal);
    }

    [Fact]
    public void Convert_ModuleWithCfgJoinConflictingGenericAliases_DoesNotReportE5301()
    {
        var genericSymbol = new SymbolId(2205);
        var concreteSymbol = new SymbolId(2206);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var boolType = new TypeId(BaseTypes.BoolId);

        var genericId = BuildFunction(
            TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "id",
            symbolId: genericSymbol);

        var concreteId = BuildFunction(
            intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, intType),
            name: "id_int",
            symbolId: concreteSymbol);

        var fPlace = LocalPlace(1, TypeId.None);
        var argPlace = LocalPlace(2, intType);
        var resultPlace = LocalPlace(3, intType);

        var caller = new MirFunc
        {
            Name = "caller_cfg_conflict",
            SymbolId = new SymbolId(2207),
            ReturnType = unitType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = fPlace.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argPlace.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "res", TypeId = intType },
                new MirLocal { Id = new LocalId { Value = 4 }, Name = "cond", TypeId = boolType, IsParameter = true }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirSwitch
                    {
                        Discriminant = LocalPlace(4, boolType),
                        Branches =
                        [
                            new MirSwitchBranch
                            {
                                Value = new MirConstant
                                {
                                    TypeId = boolType,
                                    Value = new MirConstantValue.BoolValue(true)
                                },
                                Target = new BlockId { Value = 2 }
                            }
                        ],
                        DefaultTarget = new BlockId { Value = 3 }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    Instructions =
                    [
                        new MirAssign
                        {
                            Target = fPlace,
                            Source = new MirFunctionRef
                            {
                                Name = "id",
                                SymbolId = genericSymbol,
                                TypeId = TypeId.None
                            }
                        }
                    ],
                    Terminator = new MirGoto
                    {
                        Target = new BlockId { Value = 4 }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 3 },
                    Instructions =
                    [
                        new MirAssign
                        {
                            Target = fPlace,
                            Source = new MirFunctionRef
                            {
                                Name = "id_int",
                                SymbolId = concreteSymbol,
                                TypeId = intType
                            }
                        }
                    ],
                    Terminator = new MirGoto
                    {
                        Target = new BlockId { Value = 4 }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 4 },
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = resultPlace,
                            Function = fPlace,
                            Arguments = [argPlace]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirConstant
                        {
                            TypeId = unitType,
                            Value = new MirConstantValue.UnitValue()
                        }
                    }
                }
            ]
        };

        var converter = new MirToLlvmConverter();
        _ = converter.Convert(new MirModule
        {
            Name = "cfg_conflict",
            Functions = [genericId, concreteId, caller]
        });

        Assert.DoesNotContain(converter.Diagnostics, diagnostic => diagnostic.Code == "E5301");
    }

    [Fact]
    public void Convert_ModuleWithCfgJoinSameGenericAlias_ReportsE5301()
    {
        var genericSymbol = new SymbolId(2208);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var boolType = new TypeId(BaseTypes.BoolId);

        var genericId = BuildFunction(
            TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "id",
            symbolId: genericSymbol);

        var fPlace = LocalPlace(1, TypeId.None);
        var argPlace = LocalPlace(2, intType);
        var resultPlace = LocalPlace(3, intType);

        var caller = new MirFunc
        {
            Name = "caller_cfg_same",
            SymbolId = new SymbolId(2209),
            ReturnType = unitType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = fPlace.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argPlace.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "res", TypeId = intType },
                new MirLocal { Id = new LocalId { Value = 4 }, Name = "cond", TypeId = boolType, IsParameter = true }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirSwitch
                    {
                        Discriminant = LocalPlace(4, boolType),
                        Branches =
                        [
                            new MirSwitchBranch
                            {
                                Value = new MirConstant
                                {
                                    TypeId = boolType,
                                    Value = new MirConstantValue.BoolValue(true)
                                },
                                Target = new BlockId { Value = 2 }
                            }
                        ],
                        DefaultTarget = new BlockId { Value = 3 }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    Instructions =
                    [
                        new MirAssign
                        {
                            Target = fPlace,
                            Source = new MirFunctionRef
                            {
                                Name = "id",
                                SymbolId = genericSymbol,
                                TypeId = TypeId.None
                            }
                        }
                    ],
                    Terminator = new MirGoto
                    {
                        Target = new BlockId { Value = 4 }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 3 },
                    Instructions =
                    [
                        new MirAssign
                        {
                            Target = fPlace,
                            Source = new MirFunctionRef
                            {
                                Name = "id",
                                SymbolId = genericSymbol,
                                TypeId = TypeId.None
                            }
                        }
                    ],
                    Terminator = new MirGoto
                    {
                        Target = new BlockId { Value = 4 }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 4 },
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = resultPlace,
                            Function = fPlace,
                            Arguments = [argPlace]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirConstant
                        {
                            TypeId = unitType,
                            Value = new MirConstantValue.UnitValue()
                        }
                    }
                }
            ]
        };

        var converter = new MirToLlvmConverter();
        _ = converter.Convert(new MirModule
        {
            Name = "cfg_same",
            Functions = [genericId, caller]
        });

        var diagnostic = Assert.Single(
            converter.Diagnostics,
            entry => entry.Code == "E5301" &&
                     entry.Message.Contains("Indirect call through local", StringComparison.Ordinal));
        Assert.Contains("remaining generic arity at call site: 1", diagnostic.Notes, StringComparer.Ordinal);
    }

    [Fact]
    public void Convert_ModuleWithCfgJoinDifferentGenericArityAliases_DoesNotReportE5301()
    {
        var unaryGenericSymbol = new SymbolId(2210);
        var binaryGenericSymbol = new SymbolId(2211);
        var intType = new TypeId(BaseTypes.IntId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var boolType = new TypeId(BaseTypes.BoolId);

        var unaryGeneric = BuildFunction(
            TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "id_unary",
            symbolId: unaryGenericSymbol);

        var binaryGeneric = BuildFunction(
            TypeId.None,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = TypeId.None,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "y",
                    TypeId = TypeId.None,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, TypeId.None),
            name: "id_binary",
            symbolId: binaryGenericSymbol);

        var fPlace = LocalPlace(1, TypeId.None);
        var argPlace = LocalPlace(2, intType);
        var resultPlace = LocalPlace(3, TypeId.None);

        var caller = new MirFunc
        {
            Name = "caller_cfg_arity_conflict",
            SymbolId = new SymbolId(2212),
            ReturnType = unitType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = fPlace.Local, Name = "f", TypeId = TypeId.None },
                new MirLocal { Id = argPlace.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "res", TypeId = TypeId.None },
                new MirLocal { Id = new LocalId { Value = 4 }, Name = "cond", TypeId = boolType, IsParameter = true }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirSwitch
                    {
                        Discriminant = LocalPlace(4, boolType),
                        Branches =
                        [
                            new MirSwitchBranch
                            {
                                Value = new MirConstant
                                {
                                    TypeId = boolType,
                                    Value = new MirConstantValue.BoolValue(true)
                                },
                                Target = new BlockId { Value = 2 }
                            }
                        ],
                        DefaultTarget = new BlockId { Value = 3 }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    Instructions =
                    [
                        new MirAssign
                        {
                            Target = fPlace,
                            Source = new MirFunctionRef
                            {
                                Name = "id_unary",
                                SymbolId = unaryGenericSymbol,
                                TypeId = TypeId.None
                            }
                        }
                    ],
                    Terminator = new MirGoto
                    {
                        Target = new BlockId { Value = 4 }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 3 },
                    Instructions =
                    [
                        new MirAssign
                        {
                            Target = fPlace,
                            Source = new MirFunctionRef
                            {
                                Name = "id_binary",
                                SymbolId = binaryGenericSymbol,
                                TypeId = TypeId.None
                            }
                        }
                    ],
                    Terminator = new MirGoto
                    {
                        Target = new BlockId { Value = 4 }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 4 },
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = resultPlace,
                            Function = fPlace,
                            Arguments = [argPlace]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirConstant
                        {
                            TypeId = unitType,
                            Value = new MirConstantValue.UnitValue()
                        }
                    }
                }
            ]
        };

        var converter = new MirToLlvmConverter();
        _ = converter.Convert(new MirModule
        {
            Name = "cfg_arity_conflict",
            Functions = [unaryGeneric, binaryGeneric, caller]
        });

        Assert.DoesNotContain(converter.Diagnostics, diagnostic => diagnostic.Code == "E5301");
    }

    [Fact]
    public void Convert_ModuleWithConcreteIndirectFunctionLocal_UsesConcreteArgumentType()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var functionTypeId = new TypeId(9001);
        var calleeSymbol = new SymbolId(3001);

        var callee = BuildFunction(
            boolType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = boolType,
                Value = new MirConstantValue.BoolValue(true)
            },
            name: "is_small",
            symbolId: calleeSymbol);

        var functionLocal = LocalPlace(1, functionTypeId);
        var argumentLocal = LocalPlace(2, intType);
        var resultLocal = LocalPlace(3, boolType);
        var caller = BuildFunction(
            boolType,
            locals:
            [
                new MirLocal { Id = functionLocal.Local, Name = "pred", TypeId = functionTypeId },
                new MirLocal { Id = argumentLocal.Local, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = resultLocal.Local, Name = "ok", TypeId = boolType }
            ],
            instructions:
            [
                new MirAssign
                {
                    Target = functionLocal,
                    Source = new MirFunctionRef
                    {
                        Name = "is_small",
                        SymbolId = calleeSymbol,
                        TypeId = functionTypeId
                    }
                },
                new MirCall
                {
                    Target = resultLocal,
                    Function = functionLocal,
                    Arguments = [argumentLocal]
                }
            ],
            returnValue: resultLocal,
            name: "caller_indirect_concrete",
            symbolId: new SymbolId(3002));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "indirect_concrete_call",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [functionTypeId.Value] = $"Fun({intType})-" + $">{boolType}"
            },
            Functions = [callee, caller]
        });

        Assert.DoesNotContain(
            llvmModule.Functions
                .SelectMany(func => func.BasicBlocks)
                .SelectMany(block => block.Instructions)
                .OfType<LlvmCast>(),
            cast => cast.Op == "inttoptr" || cast.Op == "ptrtoint");

        var callerLlvm = Assert.Single(
            llvmModule.Functions,
            func => func.Name.Contains("caller_indirect_concrete", StringComparison.Ordinal));
        var entry = Assert.Single(callerLlvm.BasicBlocks);
        var call = Assert.Single(entry.Instructions.OfType<LlvmCall>());
        Assert.Equal("i1", call.ReturnType.ToIrString());
        Assert.Single(call.Arguments);
        Assert.Equal("i64", call.Arguments[0].Type.ToIrString());
        Assert.Contains("call i1", call.ToIrString(), StringComparison.Ordinal);
        Assert.Contains("(i64 ", call.ToIrString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_ModuleAggregateFunctionLoad_UsesTargetFunctionTypeForIndirectCall()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var concreteFunctionTypeId = new TypeId(9010);
        var erasedTypeVarId = new TypeId(9011);
        var erasedFunctionTypeId = new TypeId(9012);
        var tupleTypeId = new TypeId(9013);
        var calleeSymbol = new SymbolId(3010);

        var callee = BuildFunction(
            boolType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = boolType,
                Value = new MirConstantValue.BoolValue(true)
            },
            name: "is_small",
            symbolId: calleeSymbol);

        var aggregatePlace = LocalPlace(1, tupleTypeId);
        var argumentPlace = LocalPlace(2, intType);
        var predicatePlace = LocalPlace(3, concreteFunctionTypeId);
        var resultPlace = LocalPlace(4, boolType);
        var caller = BuildFunction(
            boolType,
            locals:
            [
                new MirLocal { Id = aggregatePlace.Local, Name = "agg", TypeId = tupleTypeId, IsParameter = true },
                new MirLocal { Id = argumentPlace.Local, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = predicatePlace.Local, Name = "pred", TypeId = concreteFunctionTypeId },
                new MirLocal { Id = resultPlace.Local, Name = "ok", TypeId = boolType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = predicatePlace,
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
                        TypeId = erasedFunctionTypeId
                    }
                },
                new MirCall
                {
                    Target = resultPlace,
                    Function = predicatePlace,
                    Arguments = [argumentPlace]
                }
            ],
            returnValue: resultPlace,
            name: "caller_aggregate_indirect",
            symbolId: new SymbolId(3011));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "aggregate_indirect_call",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [erasedTypeVarId.Value] = "TyVar_9011",
                [concreteFunctionTypeId.Value] = $"Fun({intType})-" + $">{boolType}",
                [erasedFunctionTypeId.Value] = $"Fun({erasedTypeVarId})-" + $">{boolType}",
                [tupleTypeId.Value] = $"Tuple({concreteFunctionTypeId})"
            },
            Functions = [callee, caller]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller_aggregate_indirect");
        var entry = Assert.Single(llvmCaller.BasicBlocks);
        // There are multiple loads: the aggregate field load (ptr) and closure invoke loads.
        var loads = entry.Instructions.OfType<LlvmLoad>().ToList();
        Assert.True(loads.Count >= 1, "Expected at least one load instruction.");
        // The aggregate field load produces an opaque closure pointer.
        Assert.Contains(loads, load => load.LoadType.ToIrString() == "ptr");

        // The indirect call goes through closure invoke — the final call produces i1 (bool).
        var calls = entry.Instructions.OfType<LlvmCall>().ToList();
        Assert.Contains(calls, call => call.ToIrString().Contains("call i1", StringComparison.Ordinal));
    }

    [Fact]
    public void Convert_ModuleTailClosureInvokeFromAggregate_DoesNotEmitTailCall()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var concreteFunctionTypeId = new TypeId(9014);
        var erasedTypeVarId = new TypeId(9015);
        var erasedFunctionTypeId = new TypeId(9016);
        var tupleTypeId = new TypeId(9017);

        var aggregatePlace = LocalPlace(1, tupleTypeId);
        var argumentPlace = LocalPlace(2, intType);
        var predicatePlace = LocalPlace(3, concreteFunctionTypeId);
        var resultPlace = LocalPlace(4, boolType);
        var caller = BuildFunction(
            boolType,
            locals:
            [
                new MirLocal { Id = aggregatePlace.Local, Name = "agg", TypeId = tupleTypeId, IsParameter = true },
                new MirLocal { Id = argumentPlace.Local, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = predicatePlace.Local, Name = "pred", TypeId = concreteFunctionTypeId },
                new MirLocal { Id = resultPlace.Local, Name = "ok", TypeId = boolType }
            ],
            instructions:
            [
                new MirLoad
                {
                    Target = predicatePlace,
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
                        TypeId = erasedFunctionTypeId
                    }
                },
                new MirCall
                {
                    Target = resultPlace,
                    Function = predicatePlace,
                    Arguments = [argumentPlace],
                    IsTailCall = true
                }
            ],
            returnValue: resultPlace,
            name: "caller_tail_closure_indirect",
            symbolId: new SymbolId(3014));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "tail_closure_indirect_call",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [erasedTypeVarId.Value] = "TyVar_9015",
                [concreteFunctionTypeId.Value] = $"Fun({intType})-" + $">{boolType}",
                [erasedFunctionTypeId.Value] = $"Fun({erasedTypeVarId})-" + $">{boolType}",
                [tupleTypeId.Value] = $"Tuple({concreteFunctionTypeId})"
            },
            Functions = [caller]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller_tail_closure_indirect");
        var entry = Assert.Single(llvmCaller.BasicBlocks);
        var invokeCall = entry.Instructions
            .OfType<LlvmCall>()
            .Single(call => call.ReturnType.ToIrString() == "i1");

        Assert.Equal(LlvmTailCallKind.None, invokeCall.TailCallKind);
        Assert.DoesNotContain("tail call i1", invokeCall.ToIrString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_ModuleTailClosureParameterWithMatchingSignature_DoesNotEmitTailHint()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var functionTypeId = new TypeId(9018);
        var functionPlace = LocalPlace(1, functionTypeId);
        var argumentPlace = LocalPlace(2, intType);
        var resultPlace = LocalPlace(3, boolType);
        var caller = BuildFunction(
            boolType,
            locals:
            [
                new MirLocal { Id = functionPlace.Local, Name = "pred", TypeId = functionTypeId, IsParameter = true },
                new MirLocal { Id = argumentPlace.Local, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = resultPlace.Local, Name = "ok", TypeId = boolType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = resultPlace,
                    Function = functionPlace,
                    Arguments = [argumentPlace],
                    IsTailCall = true
                }
            ],
            returnValue: resultPlace,
            name: "caller_tail_closure_parameter",
            symbolId: new SymbolId(3018));

        var llvmModule = new MirToLlvmConverter().Convert(new MirModule
        {
            Name = "tail_closure_parameter_call",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [functionTypeId.Value] = $"Fun({intType})-" + $">{boolType}"
            },
            Functions = [caller]
        });

        var llvmCaller = SingleFunctionBySourceName(llvmModule, "caller_tail_closure_parameter");
        var entry = Assert.Single(llvmCaller.BasicBlocks);
        var invokeCall = entry.Instructions
            .OfType<LlvmCall>()
            .Single(call => call.ReturnType.ToIrString() == "i1");

        Assert.Equal(LlvmTailCallKind.None, invokeCall.TailCallKind);
        Assert.DoesNotContain("tail call i1", invokeCall.ToIrString(), StringComparison.Ordinal);
    }

}
