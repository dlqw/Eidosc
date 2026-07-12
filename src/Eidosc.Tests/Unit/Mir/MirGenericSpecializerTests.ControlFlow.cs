using Eidosc.Symbols;
using Eidosc;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed partial class MirGenericSpecializerTests
{
    [Fact]
    public void Run_UnreferencedGenericTemplate_DropsTemplateFunction()
    {
        var genericSymbol = new SymbolId(1301);
        var unitType = new TypeId(BaseTypes.UnitId);

        var genericId = BuildFunction(
            returnType: TypeId.None,
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

        var main = BuildFunction(
            returnType: unitType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = unitType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "main",
            symbolId: new SymbolId(1302));

        var module = new MirModule
        {
            Name = "generic_unused",
            Functions = [genericId, main]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        Assert.DoesNotContain(specialized.Functions, function => function.SymbolId == genericSymbol);
        Assert.Contains(specialized.Functions, function => function.Name == "main");
    }

    [Fact]
    public void Run_IndirectLocalCallToGenericFunction_RewritesToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1401);
        var intType = new TypeId(BaseTypes.IntId);

        var genericId = BuildFunction(
            returnType: TypeId.None,
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

        var fnLocal = LocalPlace(2, TypeId.None);
        var argLocal = LocalPlace(1, intType);
        var resultLocal = LocalPlace(3, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = argLocal.Local, Name = "arg", TypeId = intType, IsParameter = true },
                new MirLocal { Id = fnLocal.Local, Name = "f", TypeId = TypeId.None },
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
            returnValue: resultLocal,
            name: "caller",
            symbolId: new SymbolId(1402));

        var module = new MirModule
        {
            Name = "generic_indirect_call",
            Functions = [genericId, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller");
        var rewrittenCall = Assert.IsType<MirCall>(rewrittenCaller.BasicBlocks.Single().Instructions[0]);
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);

        Assert.NotEqual(genericSymbol, rewrittenFunctionRef.SymbolId);
        Assert.StartsWith("id__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(intType, rewrittenFunctionRef.TypeId);
    }

    [Fact]
    public void Run_CfgJoinWithSameGenericAlias_RewritesJoinCall()
    {
        var genericSymbol = new SymbolId(1501);
        var intType = new TypeId(BaseTypes.IntId);

        var genericId = BuildFunction(
            returnType: TypeId.None,
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

        var argLocal = new MirLocal { Id = new LocalId { Value = 1 }, Name = "arg", TypeId = intType, IsParameter = true };
        var fnLocal = new MirLocal { Id = new LocalId { Value = 2 }, Name = "f", TypeId = TypeId.None };
        var resultLocal = new MirLocal { Id = new LocalId { Value = 3 }, Name = "res", TypeId = intType };
        var condLocal = new MirLocal { Id = new LocalId { Value = 4 }, Name = "cond", TypeId = new TypeId(BaseTypes.BoolId), IsParameter = true };

        var fnPlace = LocalPlace(fnLocal.Id.Value, TypeId.None);
        var argPlace = LocalPlace(argLocal.Id.Value, intType);
        var resultPlace = LocalPlace(resultLocal.Id.Value, intType);

        var caller = new MirFunc
        {
            Name = "caller_cfg_join",
            SymbolId = new SymbolId(1502),
            ReturnType = intType,
            Locals = [argLocal, fnLocal, resultLocal, condLocal],
            EntryBlockId = new BlockId { Value = 1 },
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirSwitch
                    {
                        Discriminant = LocalPlace(condLocal.Id.Value, new TypeId(BaseTypes.BoolId)),
                        Branches =
                        [
                            new MirSwitchBranch
                            {
                                Value = new MirConstant
                                {
                                    TypeId = new TypeId(BaseTypes.BoolId),
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
                            Target = fnPlace,
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
                            Target = fnPlace,
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
                            Function = fnPlace,
                            Arguments = [argPlace]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = resultPlace
                    }
                }
            ]
        };

        var module = new MirModule
        {
            Name = "generic_cfg_join",
            Functions = [genericId, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_cfg_join");
        var joinBlock = rewrittenCaller.BasicBlocks.Single(block => block.Id.Value == 4);
        var rewrittenCall = Assert.Single(joinBlock.Instructions.OfType<MirCall>());
        var rewrittenFunctionRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.StartsWith("id__spec_", rewrittenFunctionRef.Name, StringComparison.Ordinal);
        Assert.Equal(intType, rewrittenFunctionRef.TypeId);
    }

    [Fact]
    public void Run_CfgJoinWithConflictingAliases_DoesNotRewriteJoinCall()
    {
        var genericSymbol = new SymbolId(1601);
        var concreteSymbol = new SymbolId(1602);
        var intType = new TypeId(BaseTypes.IntId);

        var genericId = BuildFunction(
            returnType: TypeId.None,
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
            returnType: intType,
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

        var argLocal = new MirLocal { Id = new LocalId { Value = 1 }, Name = "arg", TypeId = intType, IsParameter = true };
        var fnLocal = new MirLocal { Id = new LocalId { Value = 2 }, Name = "f", TypeId = TypeId.None };
        var resultLocal = new MirLocal { Id = new LocalId { Value = 3 }, Name = "res", TypeId = intType };
        var condLocal = new MirLocal { Id = new LocalId { Value = 4 }, Name = "cond", TypeId = new TypeId(BaseTypes.BoolId), IsParameter = true };

        var fnPlace = LocalPlace(fnLocal.Id.Value, TypeId.None);
        var argPlace = LocalPlace(argLocal.Id.Value, intType);
        var resultPlace = LocalPlace(resultLocal.Id.Value, intType);

        var caller = new MirFunc
        {
            Name = "caller_cfg_conflict",
            SymbolId = new SymbolId(1603),
            ReturnType = intType,
            Locals = [argLocal, fnLocal, resultLocal, condLocal],
            EntryBlockId = new BlockId { Value = 1 },
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirSwitch
                    {
                        Discriminant = LocalPlace(condLocal.Id.Value, new TypeId(BaseTypes.BoolId)),
                        Branches =
                        [
                            new MirSwitchBranch
                            {
                                Value = new MirConstant
                                {
                                    TypeId = new TypeId(BaseTypes.BoolId),
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
                            Target = fnPlace,
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
                            Target = fnPlace,
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
                            Function = fnPlace,
                            Arguments = [argPlace]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = resultPlace
                    }
                }
            ]
        };

        var module = new MirModule
        {
            Name = "generic_cfg_conflict",
            Functions = [genericId, concreteId, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_cfg_conflict");
        var joinBlock = rewrittenCaller.BasicBlocks.Single(block => block.Id.Value == 4);
        var joinCall = Assert.Single(joinBlock.Instructions.OfType<MirCall>());
        Assert.IsType<MirPlace>(joinCall.Function);
    }

    [Fact]
    public void Run_CfgJoinWithDifferentExplicitTypeArgs_DoesNotMergeFunctionAlias()
    {
        var genericSymbol = new SymbolId(1604);
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var typeVariable = new TypeId(1605);

        var genericId = BuildFunction(
            returnType: typeVariable,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = typeVariable,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: LocalPlace(1, typeVariable),
            name: "id",
            symbolId: genericSymbol,
            genericParameterCount: 1);

        var argLocal = new MirLocal { Id = new LocalId { Value = 1 }, Name = "arg", TypeId = intType, IsParameter = true };
        var fnLocal = new MirLocal { Id = new LocalId { Value = 2 }, Name = "f", TypeId = TypeId.None };
        var resultLocal = new MirLocal { Id = new LocalId { Value = 3 }, Name = "res", TypeId = intType };
        var condLocal = new MirLocal { Id = new LocalId { Value = 4 }, Name = "cond", TypeId = boolType, IsParameter = true };

        var fnPlace = LocalPlace(fnLocal.Id.Value, TypeId.None);
        var argPlace = LocalPlace(argLocal.Id.Value, intType);
        var resultPlace = LocalPlace(resultLocal.Id.Value, intType);

        var caller = new MirFunc
        {
            Name = "caller_cfg_type_arg_conflict",
            SymbolId = new SymbolId(1606),
            ReturnType = intType,
            Locals = [argLocal, fnLocal, resultLocal, condLocal],
            EntryBlockId = new BlockId { Value = 1 },
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirSwitch
                    {
                        Discriminant = LocalPlace(condLocal.Id.Value, boolType),
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
                            Target = fnPlace,
                            Source = new MirFunctionRef
                            {
                                Name = "id",
                                SymbolId = genericSymbol,
                                TypeId = typeVariable,
                                TypeArgumentIds = [intType]
                            }
                        }
                    ],
                    Terminator = new MirGoto { Target = new BlockId { Value = 4 } }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 3 },
                    Instructions =
                    [
                        new MirAssign
                        {
                            Target = fnPlace,
                            Source = new MirFunctionRef
                            {
                                Name = "id",
                                SymbolId = genericSymbol,
                                TypeId = typeVariable,
                                TypeArgumentIds = [stringType]
                            }
                        }
                    ],
                    Terminator = new MirGoto { Target = new BlockId { Value = 4 } }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 4 },
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = resultPlace,
                            Function = fnPlace,
                            Arguments = [argPlace]
                        }
                    ],
                    Terminator = new MirReturn { Value = resultPlace }
                }
            ]
        };

        var module = new MirModule
        {
            Name = "generic_cfg_type_arg_conflict",
            Functions = [genericId, caller],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0)
            }
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_cfg_type_arg_conflict");
        var joinBlock = rewrittenCaller.BasicBlocks.Single(block => block.Id.Value == 4);
        var joinCall = Assert.Single(joinBlock.Instructions.OfType<MirCall>());
        Assert.IsType<MirPlace>(joinCall.Function);
    }

    [Fact]
    public void Run_GenericContainerOnlyBinding_SpecializesTyConArguments()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tyVarType = new TypeId(6101);
        var listTemplateType = new TypeId(6102);
        var listConcreteType = new TypeId(6103);
        var genericSymbol = new SymbolId(1610);

        var reverseTemplate = new MirFunc
        {
            Name = "reverse_like",
            SymbolId = genericSymbol,
            ReturnType = listTemplateType,
            GenericParameterCount = 1,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "xs",
                    TypeId = listTemplateType,
                    IsParameter = true
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn
                    {
                        Value = LocalPlace(1, listTemplateType)
                    }
                }
            ]
        };

        var callerArg = LocalPlace(1, listConcreteType);
        var callerResult = LocalPlace(2, listConcreteType);
        var caller = BuildFunction(
            returnType: listConcreteType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "xs", TypeId = listConcreteType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "res", TypeId = listConcreteType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "reverse_like",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_container_only_binding",
            symbolId: new SymbolId(1611));

        var module = new MirModule
        {
            Name = "generic_container_only_binding",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tyVarType.Value] = "TyVar_0",
                [listTemplateType.Value] = $"TyCon(sym:1;{tyVarType})",
                [listConcreteType.Value] = $"TyCon(sym:1;{intType})"
            },
            Functions = [reverseTemplate, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var instance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("reverse_like__spec_", StringComparison.Ordinal));

        Assert.Equal(listConcreteType, instance.ReturnType);
        Assert.Equal(listConcreteType, Assert.Single(instance.Locals, local => local.IsParameter).TypeId);
        Assert.DoesNotContain(specialized.Functions, function => function.SymbolId == genericSymbol);

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_container_only_binding");
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(instance.SymbolId, rewrittenRef.SymbolId);
        Assert.Equal(instance.Name, rewrittenRef.Name);
        Assert.Equal(listConcreteType, rewrittenRef.TypeId);
    }

    [Fact]
    public void Run_MultiStepPartialChainWithNonCopyBoundLocal_RewritesFinalCallToSpecializedDirectCall()
    {
        var genericSymbol = new SymbolId(1612);
        var tyVarType = new TypeId(6110);
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);

        var chooseFirst = new MirFunc
        {
            Name = "choose_first",
            SymbolId = genericSymbol,
            ReturnType = tyVarType,
            GenericParameterCount = 1,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = tyVarType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "count",
                    TypeId = intType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 3 },
                    Name = "flag",
                    TypeId = boolType,
                    IsParameter = true
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn
                    {
                        Value = LocalPlace(1, tyVarType)
                    }
                }
            ]
        };

        var sourceArg = LocalPlace(1, stringType);
        var copiedArg = LocalPlace(2, stringType);
        var partial1 = LocalPlace(3, TypeId.None);
        var partial2 = LocalPlace(4, TypeId.None);
        var resultSlot = LocalPlace(5, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = sourceArg.Local, Name = "value", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = copiedArg.Local, Name = "value_copy", TypeId = stringType },
                new MirLocal { Id = partial1.Local, Name = "p1", TypeId = TypeId.None },
                new MirLocal { Id = partial2.Local, Name = "p2", TypeId = TypeId.None },
                new MirLocal { Id = resultSlot.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCopy
                {
                    Target = copiedArg,
                    Source = sourceArg
                },
                new MirCall
                {
                    Target = partial1,
                    Function = new MirFunctionRef
                    {
                        Name = "choose_first",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments = [copiedArg]
                },
                new MirCall
                {
                    Target = partial2,
                    Function = partial1,
                    Arguments =
                    [
                        new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(7)
                        }
                    ]
                },
                new MirCall
                {
                    Target = resultSlot,
                    Function = partial2,
                    Arguments =
                    [
                        new MirConstant
                        {
                            TypeId = boolType,
                            Value = new MirConstantValue.BoolValue(true)
                        }
                    ]
                }
            ],
            returnValue: resultSlot,
            name: "caller_multi_step_partial_chain",
            symbolId: new SymbolId(1613));

        var module = new MirModule
        {
            Name = "generic_multi_step_partial_chain",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tyVarType.Value] = "TyVar_0"
            },
            Functions = [chooseFirst, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var instance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("choose_first__spec_", StringComparison.Ordinal));

        Assert.Equal(stringType, instance.ReturnType);
        Assert.Equal(3, instance.Locals.Count(local => local.IsParameter));
        Assert.Equal(stringType, Assert.Single(instance.Locals, local => local.IsParameter && local.Name == "value").TypeId);
        Assert.Equal(intType, Assert.Single(instance.Locals, local => local.IsParameter && local.Name == "count").TypeId);
        Assert.Equal(boolType, Assert.Single(instance.Locals, local => local.IsParameter && local.Name == "flag").TypeId);

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_multi_step_partial_chain");
        var entryBlock = rewrittenCaller.BasicBlocks.Single();

        var loweredFirstPartial = Assert.IsType<MirAssign>(entryBlock.Instructions[1]);
        Assert.Equal(partial1.Local, loweredFirstPartial.Target.Local);

        var loweredSecondPartial = Assert.IsType<MirAssign>(entryBlock.Instructions[2]);
        Assert.Equal(partial2.Local, loweredSecondPartial.Target.Local);

        var rewrittenCall = Assert.IsType<MirCall>(entryBlock.Instructions[3]);
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(instance.SymbolId, rewrittenRef.SymbolId);
        Assert.Equal(instance.Name, rewrittenRef.Name);
        Assert.Equal(stringType, rewrittenRef.TypeId);
        Assert.Equal(3, rewrittenCall.Arguments.Count);
    }
}
