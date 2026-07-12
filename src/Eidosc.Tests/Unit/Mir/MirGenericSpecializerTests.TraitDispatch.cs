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
    public void Run_GenericHigherOrderCall_ResolvesConcreteFunctionTypeWithEffectSuffix()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var tyVarType = new TypeId(7001);
        var predicateTemplateType = new TypeId(7002);
        var predicateConcreteType = new TypeId(7003);
        var genericSymbol = new SymbolId(1701);
        var concretePredicateSymbol = new SymbolId(1702);

        var applyPred = new MirFunc
        {
            Name = "apply_pred",
            SymbolId = genericSymbol,
            ReturnType = boolType,
            GenericParameterCount = 1,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "x",
                    TypeId = tyVarType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "pred",
                    TypeId = predicateTemplateType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 3 },
                    Name = "result",
                    TypeId = boolType
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(3, boolType),
                            Function = LocalPlace(2, predicateTemplateType),
                            Arguments = [LocalPlace(1, tyVarType)]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = LocalPlace(3, boolType)
                    }
                }
            ]
        };

        var isSmall = BuildFunction(
            returnType: boolType,
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
            symbolId: concretePredicateSymbol);

        var callerResult = LocalPlace(2, boolType);
        var caller = BuildFunction(
            returnType: boolType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = boolType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "apply_pred",
                        SymbolId = genericSymbol,
                        TypeId = TypeId.None
                    },
                    Arguments =
                    [
                        LocalPlace(1, intType),
                        new MirFunctionRef
                        {
                            Name = "is_small",
                            SymbolId = concretePredicateSymbol,
                            TypeId = TypeId.None
                        }
                    ]
                }
            ],
            returnValue: callerResult,
            name: "caller_apply_pred",
            symbolId: new SymbolId(1703));

        var module = new MirModule
        {
            Name = "generic_higher_order",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tyVarType.Value] = "TyVar_0",
                [predicateTemplateType.Value] = $"Fun({tyVarType})-" + $">{boolType}{{}}",
                [predicateConcreteType.Value] = $"Fun({intType})-" + $">{boolType}{{}}"
            },
            Functions = [applyPred, isSmall, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);
        var instance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("apply_pred__spec_", StringComparison.Ordinal));
        var predicateParameter = instance.Locals.Single(local => local.IsParameter && local.Name == "pred");
        Assert.Equal(predicateConcreteType, predicateParameter.TypeId);

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_apply_pred");
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(instance.SymbolId, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_AfterDeadCodeEliminationRemovesUnreachablePartialBinding_RerunPrunesOrphanedGenericTemplate()
    {
        var tyVarType = new TypeId(7101);
        var stringType = new TypeId(BaseTypes.StringId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var helperSymbol = new SymbolId(1708);

        var helperTemplate = new MirFunc
        {
            Name = "show_value",
            TraitInvokeHelper = TraitInvokeHelperKind.ShowValue,
            SymbolId = helperSymbol,
            ReturnType = stringType,
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
                        Value = new MirConstant
                        {
                            TypeId = stringType,
                            Value = new MirConstantValue.StringValue("unused")
                        }
                    }
                }
            ]
        };

        var partialSlot = LocalPlace(1, TypeId.None);
        var deadPartialKeeper = new MirFunc
        {
            Name = "dead_partial_keeper",
            SymbolId = new SymbolId(1709),
            ReturnType = unitType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = partialSlot.Local,
                    Name = "partial",
                    TypeId = TypeId.None
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
                        Value = new MirConstant
                        {
                            TypeId = unitType,
                            Value = new MirConstantValue.UnitValue()
                        }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = partialSlot,
                            Function = new MirFunctionRef
                            {
                                Name = "show_value",
                                SymbolId = helperSymbol,
                                TypeId = TypeId.None
                            },
                            Arguments = []
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

        var module = new MirModule
        {
            Name = "generic_template_prune_after_dce",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tyVarType.Value] = "TyVar_0"
            },
            Functions = [helperTemplate, deadPartialKeeper]
        };

        var specializer = new MirGenericSpecializer();
        var specialized = specializer.Run(module);

        Assert.Contains(specialized.Functions, function => function.SymbolId == helperSymbol);

        var optimized = MirOptimizer.CreateDefault().Optimize(specialized);
        var pruned = specializer.Run(optimized);

        Assert.DoesNotContain(pruned.Functions, function => function.SymbolId == helperSymbol);
    }

    [Fact]
    public void Run_GenericTemplateClusterReferencedOnlyByGenericTemplates_DropsEntireOrphanedCluster()
    {
        var tyVarItemType = new TypeId(7111);
        var tyVarListType = new TypeId(7112);
        var listConstructorType = new TypeId(7113);
        var stringType = new TypeId(BaseTypes.StringId);
        var showValueSymbol = new SymbolId(1715);
        var showItemsSymbol = new SymbolId(1716);

        var showValue = new MirFunc
        {
            Name = "show_value",
            TraitInvokeHelper = TraitInvokeHelperKind.ShowValue,
            SymbolId = showValueSymbol,
            ReturnType = stringType,
            GenericParameterCount = 1,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = tyVarItemType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "result",
                    TypeId = stringType
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(2, stringType),
                            Function = new MirFunctionRef
                            {
                                Name = "show",
                                SymbolId = SymbolId.None,
                                TypeId = stringType
                            },
                            Arguments = [LocalPlace(1, tyVarItemType)]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = LocalPlace(2, stringType)
                    }
                }
            ]
        };

        var showItems = new MirFunc
        {
            Name = "show_items",
            SymbolId = showItemsSymbol,
            ReturnType = stringType,
            GenericParameterCount = 1,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "items",
                    TypeId = tyVarListType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "head",
                    TypeId = tyVarItemType
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 3 },
                    Name = "tail",
                    TypeId = tyVarListType
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 4 },
                    Name = "head_text",
                    TypeId = stringType
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 5 },
                    Name = "tail_text",
                    TypeId = stringType
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(4, stringType),
                            Function = new MirFunctionRef
                            {
                                Name = "show_value",
                                SymbolId = showValueSymbol,
                                TypeId = stringType
                            },
                            Arguments = [LocalPlace(2, tyVarItemType)]
                        },
                        new MirCall
                        {
                            Target = LocalPlace(5, stringType),
                            Function = new MirFunctionRef
                            {
                                Name = "show_items",
                                SymbolId = showItemsSymbol,
                                TypeId = stringType
                            },
                            Arguments = [LocalPlace(3, tyVarListType)]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = LocalPlace(5, stringType)
                    }
                }
            ]
        };

        var module = new MirModule
        {
            Name = "generic_orphan_cluster",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tyVarItemType.Value] = "TyVar_0",
                [tyVarListType.Value] = $"TyCon(type:{listConstructorType.Value};{tyVarItemType})"
            },
            Functions = [showValue, showItems]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        Assert.DoesNotContain(specialized.Functions, function => function.SymbolId == showValueSymbol);
        Assert.DoesNotContain(specialized.Functions, function => function.SymbolId == showItemsSymbol);
    }

    [Fact]
    public void Run_GenericTraitHelper_RewritesOnlySpecializedInstanceToConcreteImpl()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var tyVarType = new TypeId(7101);
        var traitMethodSymbol = new SymbolId(1710);
        var implMethodSymbol = new SymbolId(1711);
        var helperSymbol = new SymbolId(1712);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "show",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [tyVarType],
            ReturnType = stringType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0],
            TraitMethodRole = TraitMethodRole.Show
        });

        var implId = symbolTable.DeclareImpl(traitId, intType, SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "show",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [intType],
            ReturnType = stringType
        });
        symbolTable.AddMethodToImpl(implId, implMethodSymbol, traitMethodSymbol);

        var helperTemplate = new MirFunc
        {
            Name = "show_value",
            TraitInvokeHelper = TraitInvokeHelperKind.ShowValue,
            TraitInvokeHelperTraitId = traitId,
            SymbolId = helperSymbol,
            ReturnType = stringType,
            GenericParameterCount = 0,
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
                    Name = "result",
                    TypeId = stringType
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(2, stringType),
                            Function = new MirFunctionRef
                            {
                                Name = "show",
                                SymbolId = SymbolId.None,
                                TypeId = stringType
                            },
                            Arguments = [LocalPlace(1, tyVarType)]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = LocalPlace(2, stringType)
                    }
                }
            ]
        };

        var implMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("int")
            },
            name: "show",
            symbolId: implMethodSymbol);

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "show_value",
                        SymbolId = helperSymbol,
                        TypeId = stringType
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_show_value",
            symbolId: new SymbolId(1713));

        var module = new MirModule
        {
            Name = "generic_trait_helper",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tyVarType.Value] = "TyVar_0"
            },
            TraitImpls =
            [
                .. MirTraitImplsFrom(symbolTable),
                new ImplSymbol
                {
                    Id = new SymbolId(1714),
                    Name = "impl Unrelated for Int",
                    Trait = new SymbolId(1715),
                    ImplementingType = intType,
                    ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, intType, "Int", [])
                }
            ],
            Functions = [helperTemplate, implMethod, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        Assert.DoesNotContain(specialized.Functions, function => function.SymbolId == helperSymbol);

        var specializedInstance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("show_value__spec_", StringComparison.Ordinal));
        var instanceCall = Assert.Single(specializedInstance.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var instanceRef = Assert.IsType<MirFunctionRef>(instanceCall.Function);
        Assert.Equal(implMethodSymbol, instanceRef.SymbolId);
        Assert.Equal("show", instanceRef.Name);

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_show_value");
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(specializedInstance.SymbolId, rewrittenRef.SymbolId);
        Assert.Equal(
            """
            func <spec:show_value:1> symbol=<spec:show_value:1> fid=<spec:show_value:1>
              call %2:T4 -> show fid=sym:1711 args=[%1:T1]
            func caller_show_value symbol=sym:1713 fid=sym:1713
              call %2:T4 -> <spec:show_value:1> fid=<spec:show_value:1> args=[%1:T1]
            func show symbol=sym:1711 fid=sym:1711
            """.ReplaceLineEndings("\n"),
            BuildIdentityContract(specialized).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Run_TraitDispatch_UsesMirModuleImplMetadataWhenSymbolTableHasNoImplSymbol()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitMethodSymbol = new SymbolId(1720);
        var implMethodSymbol = new SymbolId(1721);
        var callerSymbol = new SymbolId(1722);
        var implId = new SymbolId(1723);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Display", SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "display",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [intType],
            ReturnType = stringType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0]
        });
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "display",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [intType],
            ReturnType = stringType
        });

        var implMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("int")
            },
            name: "display",
            symbolId: implMethodSymbol);

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "display",
                        SymbolId = traitMethodSymbol,
                        TypeId = stringType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_display",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "trait_impl_metadata",
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Display_Int",
                    Trait = traitId,
                    ImplementingType = intType,
                    ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, intType, "Int", []),
                    ImplementingTypeShape = new ImplConstructorShapeNode("Int", []) { TypeId = intType },
                    Methods = [implMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = implMethodSymbol
                    }
                }
            ],
            Functions = [implMethod, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_display");
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("display", rewrittenRef.Name);
        Assert.Single(specialized.TraitImpls);
    }

    [Fact]
    public void Run_TraitDispatch_WithMirImplMetadata_IgnoresStaleSymbolTableImpls()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var traitMethodSymbol = new SymbolId(2020);
        var staleImplMethodSymbol = new SymbolId(2021);
        var mirImplMethodSymbol = new SymbolId(2022);
        var callerSymbol = new SymbolId(2023);
        var mirImplId = new SymbolId(2024);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Display", SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "display",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [intType],
            ReturnType = stringType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0]
        });
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = staleImplMethodSymbol,
            Name = "display_int_stale",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [intType],
            ReturnType = stringType
        });
        var staleImplId = symbolTable.DeclareImpl(
            traitId,
            intType,
            SourceSpan.Empty,
            implementingTypeDisplay: "Int",
            canonicalImplementingType: "Int");
        symbolTable.AddMethodToImpl(staleImplId, staleImplMethodSymbol, traitMethodSymbol);

        var argument = LocalPlace(1, intType);
        var result = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = argument.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "display",
                        SymbolId = traitMethodSymbol,
                        TypeId = stringType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [argument]
                }
            ],
            returnValue: result,
            name: "caller_display_int",
            symbolId: callerSymbol);

        var mirImplMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "value", TypeId = boolType, IsParameter = true }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("bool")
            },
            name: "display_bool",
            symbolId: mirImplMethodSymbol);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "trait_dispatch_ignores_stale_symbol_table_impls",
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = mirImplId,
                    Name = "Display_Bool",
                    Trait = traitId,
                    ImplementingType = boolType,
                    ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, boolType, "Bool", []),
                    ImplementingTypeShape = new ImplConstructorShapeNode("Bool", []) { TypeId = boolType },
                    Methods = [mirImplMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = mirImplMethodSymbol
                    }
                }
            ],
            Functions = [caller, mirImplMethod]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_display_int");
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(traitMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("display", rewrittenRef.Name);
        Assert.Single(specialized.TraitImpls);
    }

    [Fact]
    public void Run_TraitDispatch_PrefersMirImplMethodNameOverStaleSymbolTableName()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitMethodSymbol = new SymbolId(2080);
        var implMethodSymbol = new SymbolId(2081);
        var callerSymbol = new SymbolId(2082);
        var implId = new SymbolId(2083);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Display", SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "stale_display_name",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [intType],
            ReturnType = stringType
        });

        var implMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("int")
            },
            name: "Display__display",
            symbolId: implMethodSymbol,
            sourceName: "display");

        var argument = LocalPlace(1, intType);
        var result = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = argument.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "display",
                        SymbolId = SymbolId.None,
                        TypeId = stringType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [argument]
                }
            ],
            returnValue: result,
            name: "caller_display_mir_name",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "trait_dispatch_prefers_mir_impl_method_name",
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Display_Int",
                    Trait = traitId,
                    ImplementingType = intType,
                    ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, intType, "Int", []),
                    ImplementingTypeShape = new ImplConstructorShapeNode("Int", []) { TypeId = intType },
                    Methods = [implMethodSymbol]
                }
            ],
            Functions = [implMethod, caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("Display__display", rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitDispatch_UsesFunctionRefTraitMetadataWhenTraitMethodSymbolIsNotInSymbolTable()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitMethodSymbol = new SymbolId(1724);
        var implMethodSymbol = new SymbolId(1725);
        var implId = new SymbolId(1726);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Display", SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "display",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [intType],
            ReturnType = stringType
        });

        var implMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("int")
            },
            name: "display",
            symbolId: implMethodSymbol);

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "display",
                        SymbolId = traitMethodSymbol,
                        TypeId = stringType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_display",
            symbolId: new SymbolId(1727));

        var module = new MirModule
        {
            Name = "trait_method_metadata",
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Display_Int",
                    Trait = traitId,
                    ImplementingType = intType,
                    ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, intType, "Int", []),
                    ImplementingTypeShape = new ImplConstructorShapeNode("Int", []) { TypeId = intType },
                    Methods = [implMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = implMethodSymbol
                    }
                }
            ],
            Functions = [implMethod, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_display");
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("display", rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitDispatch_WithMirMetadata_DoesNotRequireSymbolTable()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitId = new SymbolId(1728);
        var traitMethodSymbol = new SymbolId(1729);
        var implMethodSymbol = new SymbolId(1734);
        var implId = new SymbolId(1735);

        var implMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = intType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("int")
            },
            name: "display",
            symbolId: implMethodSymbol);

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "display",
                        SymbolId = traitMethodSymbol,
                        SymbolKind = SymbolKind.Constructor,
                        TypeId = stringType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_display",
            symbolId: new SymbolId(1736));

        var module = new MirModule
        {
            Name = "trait_metadata_without_symbol_table",
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Display_Int",
                    Trait = traitId,
                    ImplementingType = intType,
                    ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, intType, "Int", []),
                    ImplementingTypeShape = new ImplConstructorShapeNode("Int", []) { TypeId = intType },
                    Methods = [implMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = implMethodSymbol
                    }
                }
            ],
            Functions = [implMethod, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_display");
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal(SymbolKind.Function, rewrittenRef.SymbolKind);
        Assert.Equal("display", rewrittenRef.Name);
    }

}
