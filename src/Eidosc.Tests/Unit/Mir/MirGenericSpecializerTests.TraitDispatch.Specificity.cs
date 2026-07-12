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
    public void Run_ValidNonTraitSymbolNamedLikeTraitMethod_DoesNotDispatchByName()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitMethodSymbol = new SymbolId(1730);
        var implMethodSymbol = new SymbolId(1731);
        var nonTraitFunctionSymbol = new SymbolId(1732);

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
            Id = nonTraitFunctionSymbol,
            Name = "display",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [intType],
            ReturnType = stringType
        });
        var implId = symbolTable.DeclareImpl(traitId, intType, SourceSpan.Empty);
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
        symbolTable.AddMethodToImpl(implId, implMethodSymbol, traitMethodSymbol);

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
                        SymbolId = nonTraitFunctionSymbol,
                        TypeId = stringType
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_display",
            symbolId: new SymbolId(1733));

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "non_trait_display",
            Functions = [caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_display");
        var call = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal(nonTraitFunctionSymbol, functionRef.SymbolId);
    }

    [Fact]
    public void Run_GenericTraitHelper_WithSpecializedReceiverImpl_PrefersMoreSpecificImpl()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var tyVarItemType = new TypeId(8101);
        var tyVarOptionType = new TypeId(8102);
        var optionIntType = new TypeId(8103);
        var traitMethodSymbol = new SymbolId(1810);
        var genericImplMethodSymbol = new SymbolId(1811);
        var intImplMethodSymbol = new SymbolId(1812);
        var helperSymbol = new SymbolId(1813);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", SourceSpan.Empty);
        var optionSymbolId = symbolTable.DeclareAdt("Option", SourceSpan.Empty);
        var optionSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(optionSymbolId));
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "show",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [tyVarOptionType],
            ReturnType = stringType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0]
        });

        var genericImplId = symbolTable.DeclareImpl(
            traitId,
            optionSymbol.TypeId,
            SourceSpan.Empty,
            implementingTypeDisplay: "Option[T]",
            canonicalImplementingType: "Option[T]",
            implementingTypeKey: new ImplTypeRefKey(
                optionSymbolId,
                optionSymbol.TypeId,
                "Option",
                [new ImplTypeRefKey(SymbolId.None, TypeId.None, "T", [])]));
        var intImplId = symbolTable.DeclareImpl(
            traitId,
            optionSymbol.TypeId,
            SourceSpan.Empty,
            implementingTypeDisplay: "Option[Int]",
            canonicalImplementingType: "Option[Int]",
            implementingTypeKey: new ImplTypeRefKey(
                optionSymbolId,
                optionSymbol.TypeId,
                "Option",
                [new ImplTypeRefKey(SymbolId.None, intType, "Int", [])]));

        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = genericImplMethodSymbol,
            Name = "show",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [tyVarOptionType],
            ReturnType = stringType
        });
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = intImplMethodSymbol,
            Name = "show",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [optionIntType],
            ReturnType = stringType
        });
        symbolTable.AddMethodToImpl(genericImplId, genericImplMethodSymbol);
        symbolTable.AddMethodToImpl(intImplId, intImplMethodSymbol);

        var helperTemplate = new MirFunc
        {
            Name = "show_option_value",
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
                    TypeId = tyVarOptionType,
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
                                TypeId = stringType,
                                TraitOwnerId = traitId,
                                TraitSelfPosition = SelfPosition.InParameter,
                                TraitSelfParameterIndices = [0]
                            },
                            Arguments = [LocalPlace(1, tyVarOptionType)]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = LocalPlace(2, stringType)
                    }
                }
            ]
        };

        var genericImplMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = tyVarOptionType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("generic")
            },
            name: "show",
            symbolId: genericImplMethodSymbol);

        var intImplMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = optionIntType,
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
            symbolId: intImplMethodSymbol);

        var callerArg = LocalPlace(1, optionIntType);
        var callerResult = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "value", TypeId = optionIntType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "show_option_value",
                        SymbolId = helperSymbol,
                        TypeId = stringType
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_show_option_value",
            symbolId: new SymbolId(1814));

        var module = new MirModule
        {
            Name = "generic_trait_helper_specialized_receiver",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tyVarItemType.Value] = "TyVar_0",
                [tyVarOptionType.Value] = $"TyCon(sym:{optionSymbolId.Value};{tyVarItemType})",
                [optionIntType.Value] = $"TyCon(sym:{optionSymbolId.Value};{intType})"
            },
            TraitImpls = MirTraitImplsFrom(symbolTable),
            Functions = [helperTemplate, genericImplMethod, intImplMethod, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var specializedInstance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("show_option_value__spec_", StringComparison.Ordinal));
        var instanceCall = Assert.Single(specializedInstance.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var instanceRef = Assert.IsType<MirFunctionRef>(instanceCall.Function);
        Assert.Equal(intImplMethodSymbol, instanceRef.SymbolId);
        Assert.Equal("show", instanceRef.Name);
    }

    [Fact]
    public void Run_TraitDispatch_UsesTraitSelfInResultMetadataWithoutSelfPosition()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var traitId = new SymbolId(1921);
        var traitMethodSymbol = new SymbolId(1922);
        var intImplMethodSymbol = new SymbolId(1923);
        var boolImplMethodSymbol = new SymbolId(1924);
        var callerSymbol = new SymbolId(1925);
        var intImplId = new SymbolId(1926);
        var boolImplId = new SymbolId(1927);

        var intImplMethod = BuildFunction(
            returnType: intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "make_int",
            symbolId: intImplMethodSymbol);
        var boolImplMethod = BuildFunction(
            returnType: intType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(0)
            },
            name: "make_bool",
            symbolId: boolImplMethodSymbol);

        var result = LocalPlace(1, intType);
        var caller = BuildFunction(
            returnType: intType,
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
                        Name = "make",
                        SymbolId = traitMethodSymbol,
                        TypeId = intType,
                        TraitOwnerId = traitId,
                        TraitSelfInResult = true
                    },
                    Arguments = []
                }
            ],
            returnValue: result,
            name: "caller_make_int",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "trait_self_in_result_without_self_position",
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = intImplId,
                    Name = "Factory_Int",
                    Trait = traitId,
                    ImplementingType = intType,
                    ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, intType, "Int", []),
                    Methods = [intImplMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = intImplMethodSymbol
                    }
                },
                new ImplSymbol
                {
                    Id = boolImplId,
                    Name = "Factory_Bool",
                    Trait = traitId,
                    ImplementingType = boolType,
                    ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, boolType, "Bool", []),
                    Methods = [boolImplMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = boolImplMethodSymbol
                    }
                }
            ],
            Functions = [intImplMethod, boolImplMethod, caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(intImplMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("make_int", rewrittenRef.Name);
    }

    [Fact]
    public void Run_GenericTraitHelper_WithReturnTypeOnlyTraitDispatch_PrefersMoreSpecificImpl()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tyVarItemType = new TypeId(1820);
        var tyVarOptionType = new TypeId(1821);
        var optionIntType = new TypeId(1822);
        var traitMethodSymbol = new SymbolId(1823);
        var genericImplMethodSymbol = new SymbolId(1824);
        var specializedImplMethodSymbol = new SymbolId(1825);
        var helperSymbol = new SymbolId(1826);
        var callerSymbol = new SymbolId(1827);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Factory", SourceSpan.Empty);
        var optionSymbolId = symbolTable.DeclareAdt("Option", SourceSpan.Empty);
        var optionSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(optionSymbolId));
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "create",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [intType],
            ReturnType = tyVarOptionType,
            TraitSelfPosition = SelfPosition.InResult,
            TraitSelfInResult = true
        });

        var genericImplId = symbolTable.DeclareImpl(
            traitId,
            optionSymbol.TypeId,
            SourceSpan.Empty,
            implementingTypeDisplay: "Option[T]",
            canonicalImplementingType: "Option[T]");
        var specializedImplId = symbolTable.DeclareImpl(
            traitId,
            optionSymbol.TypeId,
            SourceSpan.Empty,
            implementingTypeDisplay: "Option[Int]",
            canonicalImplementingType: "Option[Int]");

        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = genericImplMethodSymbol,
            Name = "create",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [intType],
            ReturnType = tyVarOptionType
        });
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = specializedImplMethodSymbol,
            Name = "create",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [intType],
            ReturnType = optionIntType
        });
        symbolTable.AddMethodToImpl(genericImplId, genericImplMethodSymbol);
        symbolTable.AddMethodToImpl(specializedImplId, specializedImplMethodSymbol);

        var helperTemplate = new MirFunc
        {
            Name = "build_value",
            SymbolId = helperSymbol,
            ReturnType = tyVarOptionType,
            GenericParameterCount = 0,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = intType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "result",
                    TypeId = tyVarOptionType
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
                            Target = LocalPlace(2, tyVarOptionType),
                            Function = new MirFunctionRef
                            {
                                Name = "create",
                                SymbolId = SymbolId.None,
                                TypeId = tyVarOptionType,
                                TraitOwnerId = traitId,
                                TraitSelfPosition = SelfPosition.InResult,
                                TraitSelfInResult = true
                            },
                            Arguments = [LocalPlace(1, intType)]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = LocalPlace(2, tyVarOptionType)
                    }
                }
            ]
        };

        var genericImplMethod = BuildFunction(
            returnType: tyVarOptionType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = intType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "result",
                    TypeId = tyVarOptionType
                }
            ],
            instructions: [],
            returnValue: LocalPlace(2, tyVarOptionType),
            name: "create",
            symbolId: genericImplMethodSymbol);

        var specializedImplMethod = BuildFunction(
            returnType: optionIntType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = intType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "result",
                    TypeId = optionIntType
                }
            ],
            instructions: [],
            returnValue: LocalPlace(2, optionIntType),
            name: "create",
            symbolId: specializedImplMethodSymbol);

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, optionIntType);
        var caller = BuildFunction(
            returnType: optionIntType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = optionIntType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "build_value",
                        SymbolId = helperSymbol,
                        TypeId = optionIntType
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_build_value",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "generic_trait_helper_specialized_return_type",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [tyVarItemType.Value] = "TyVar_0",
                [tyVarOptionType.Value] = $"TyCon(sym:{optionSymbolId.Value};{tyVarItemType})",
                [optionIntType.Value] = $"TyCon(sym:{optionSymbolId.Value};{intType})"
            },
            Functions = [helperTemplate, genericImplMethod, specializedImplMethod, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var specializedInstance = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("build_value__spec_", StringComparison.Ordinal));
        var instanceCall = Assert.Single(specializedInstance.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var instanceRef = Assert.IsType<MirFunctionRef>(instanceCall.Function);
        Assert.StartsWith("create", instanceRef.Name, StringComparison.Ordinal);
        Assert.True(instanceRef.SymbolId.IsValid);
        Assert.NotEqual(SymbolId.None, instanceRef.SymbolId);
    }

    [Fact]
    public void Run_CustomHigherKindedTrait_UsesSelfMetadataForProjection()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxIntType = new TypeId(1830);
        var traitMethodSymbol = new SymbolId(1831);
        var implMethodSymbol = new SymbolId(1832);
        var callerSymbol = new SymbolId(1833);

        var symbolTable = new SymbolTable();
        var traitTypeParam = symbolTable.DeclareTypeParameter("F", SourceSpan.Empty, "kind2");
        var traitId = symbolTable.DeclareTrait("Buildable", SourceSpan.Empty, [traitTypeParam]);
        var boxTypeParam = symbolTable.DeclareTypeParameter("A", SourceSpan.Empty);
        var boxId = symbolTable.DeclareAdt("Box", SourceSpan.Empty, [boxTypeParam]);
        var boxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxId));
        var carrierId = symbolTable.DeclareAdt("BuildableCarrier", SourceSpan.Empty);
        var carrierSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(carrierId));

        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "wrap",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [boxIntType],
            ReturnType = intType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0]
        });
        var trait = Assert.IsType<TraitSymbol>(symbolTable.GetSymbol(traitId));
        symbolTable.UpdateSymbol(trait with
        {
            Methods = [traitMethodSymbol],
            SelfPosition = SelfPosition.InParameter
        });

        var implId = symbolTable.DeclareImpl(
            traitId,
            carrierSymbol.TypeId,
            SourceSpan.Empty,
            traitTypeArgs: ["Box"],
            traitTypeArgKeys:
            [
                new ImplTypeRefKey(boxId, boxSymbol.TypeId, "Box", [])
            ],
            implementingTypeDisplay: "BuildableCarrier",
            canonicalImplementingType: "BuildableCarrier");
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "wrap",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [boxIntType],
            ReturnType = intType
        });
        symbolTable.AddMethodToImpl(implId, implMethodSymbol, traitMethodSymbol);

        var implMethod = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = boxIntType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "wrap",
            symbolId: implMethodSymbol);

        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "box", TypeId = boxIntType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "wrap",
                        SymbolId = traitMethodSymbol,
                        TypeId = intType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [LocalPlace(1, boxIntType)]
                }
            ],
            returnValue: callerResult,
            name: "caller_custom_hkt",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "custom_higher_kinded_trait_projection",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [boxIntType.Value] = new TypeDescriptor.TyCon($"type:{boxSymbol.TypeId.Value}", [intType])
            },
            TraitInfos =
            [
                new MirTraitInfo
                {
                    TraitId = traitId,
                    TypeParameterCount = 1,
                    TypeParameterIds = [traitTypeParam],
                    SelfPosition = SelfPosition.InParameter
                }
            ],
            TraitImpls = MirTraitImplsFrom(symbolTable),
            Functions = [implMethod, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedDispatch_WithModuleCarrierTypeId_UsesModuleTypeMemberIdentity()
    {
        var carrierModuleTypeId = new TypeId(2500);
        var traitMethodSymbol = new SymbolId(2501);
        var implMethodSymbol = new SymbolId(2502);
        var callerSymbol = new SymbolId(2503);

        var symbolTable = new SymbolTable();
        var traitTypeParam = symbolTable.DeclareTypeParameter("F", SourceSpan.Empty, "kind2");
        var traitId = symbolTable.DeclareTrait("Buildable", SourceSpan.Empty, [traitTypeParam]);
        var boxId = symbolTable.DeclareAdt("Box", SourceSpan.Empty);
        var boxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxId));
        var carrierModuleId = new SymbolId(carrierModuleTypeId.Value);
        var carrierModule = new ModuleSymbol
        {
            Id = carrierModuleId,
            Name = "Box",
            Path = ["Std", "Box"],
            Members = [boxId],
            Span = SourceSpan.Empty,
            IsPublic = true
        };
        symbolTable.RegisterSymbol(carrierModule);
        symbolTable.Modules.RegisterModule(carrierModule, carrierModuleId);

        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "build",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [],
            ReturnType = carrierModuleTypeId,
            TraitSelfPosition = SelfPosition.InResult,
            TraitSelfInResult = true
        });
        var trait = Assert.IsType<TraitSymbol>(symbolTable.GetSymbol(traitId));
        symbolTable.UpdateSymbol(trait with
        {
            Methods = [traitMethodSymbol],
            SelfPosition = SelfPosition.InResult
        });

        var boxKey = new ImplTypeRefKey(boxId, boxSymbol.TypeId, "Box", []);
        var implId = symbolTable.DeclareImpl(
            traitId,
            boxSymbol.TypeId,
            SourceSpan.Empty,
            traitTypeArgs: ["Box"],
            traitTypeArgKeys: [boxKey],
            implementingTypeDisplay: "Box",
            canonicalImplementingType: "Box",
            implementingTypeKey: boxKey);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "build",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [],
            ReturnType = carrierModuleTypeId
        });
        symbolTable.AddMethodToImpl(implId, implMethodSymbol, traitMethodSymbol);

        var implMethod = BuildFunction(
            returnType: carrierModuleTypeId,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = carrierModuleTypeId,
                Value = new MirConstantValue.UnitValue()
            },
            name: "build",
            symbolId: implMethodSymbol);

        var callerResult = LocalPlace(1, carrierModuleTypeId);
        var caller = BuildFunction(
            returnType: carrierModuleTypeId,
            locals:
            [
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = carrierModuleTypeId }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "build",
                        SymbolId = traitMethodSymbol,
                        TypeId = carrierModuleTypeId,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InResult
                    },
                    Arguments = []
                }
            ],
            returnValue: callerResult,
            name: "caller_module_carrier",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "module_carrier_trait_dispatch",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [carrierModuleTypeId.Value] = new TypeDescriptor.TyCon($"type:{boxSymbol.TypeId.Value}", [])
            },
            TraitInfos =
            [
                new MirTraitInfo
                {
                    TraitId = traitId,
                    TypeParameterCount = 1,
                    TypeParameterIds = [traitTypeParam],
                    SelfPosition = SelfPosition.InResult
                }
            ],
            TraitImpls = MirTraitImplsFrom(symbolTable),
            Functions = [implMethod, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedTraitProjection_UsesStructuredAliasShape()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var aliasTargetType = new TypeId(1851);
        var boxIntType = new TypeId(1852);
        var traitMethodSymbol = new SymbolId(1853);
        var implMethodSymbol = new SymbolId(1854);
        var callerSymbol = new SymbolId(1855);

        var symbolTable = new SymbolTable();
        var traitTypeParam = symbolTable.DeclareTypeParameter("F", SourceSpan.Empty, "kind2");
        var traitId = symbolTable.DeclareTrait("Buildable", SourceSpan.Empty, [traitTypeParam]);
        var boxTypeParam = symbolTable.DeclareTypeParameter("A", SourceSpan.Empty);
        var boxId = symbolTable.DeclareAdt("Box", SourceSpan.Empty, [boxTypeParam]);
        var boxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxId));
        var aliasTypeParam = symbolTable.DeclareTypeParameter("A", SourceSpan.Empty);
        var aliasTypeParamType = new TypeId(aliasTypeParam.Value);
        var aliasId = symbolTable.DeclareAdt("BoxAlias", SourceSpan.Empty, [aliasTypeParam]);
        var aliasSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(aliasId));
        symbolTable.UpdateSymbol(aliasSymbol with { AliasTarget = aliasTargetType });
        aliasSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(aliasId));
        var carrierId = symbolTable.DeclareAdt("BuildableCarrier", SourceSpan.Empty);
        var carrierSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(carrierId));

        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "wrap",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [boxIntType],
            ReturnType = intType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0]
        });
        var trait = Assert.IsType<TraitSymbol>(symbolTable.GetSymbol(traitId));
        symbolTable.UpdateSymbol(trait with
        {
            Methods = [traitMethodSymbol],
            SelfPosition = SelfPosition.InParameter
        });

        var implId = symbolTable.DeclareImpl(
            traitId,
            carrierSymbol.TypeId,
            SourceSpan.Empty,
            traitTypeArgs: ["BoxAlias"],
            traitTypeArgKeys:
            [
                new ImplTypeRefKey(aliasId, aliasSymbol.TypeId, "BoxAlias", [])
            ],
            implementingTypeDisplay: "BuildableCarrier",
            canonicalImplementingType: "BuildableCarrier");
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "wrap",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [boxIntType],
            ReturnType = intType
        });
        symbolTable.AddMethodToImpl(implId, implMethodSymbol, traitMethodSymbol);

        var implMethod = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = boxIntType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "wrap",
            symbolId: implMethodSymbol);

        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "box", TypeId = boxIntType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "wrap",
                        SymbolId = traitMethodSymbol,
                        TypeId = intType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [LocalPlace(1, boxIntType)]
                }
            ],
            returnValue: callerResult,
            name: "caller_alias_hkt",
            symbolId: callerSymbol);

        var boxConstructorDescriptor = $"type:{boxSymbol.TypeId.Value}";
        var module = new MirModule
        {
            Name = "custom_higher_kinded_trait_alias_projection",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [aliasTypeParamType.Value] = new TypeDescriptor.TypeVar(aliasTypeParam.Value),
                [aliasTargetType.Value] = new TypeDescriptor.TyCon(boxConstructorDescriptor, [aliasTypeParamType]),
                [boxIntType.Value] = new TypeDescriptor.TyCon(boxConstructorDescriptor, [intType])
            },
            TraitInfos =
            [
                new MirTraitInfo
                {
                    TraitId = traitId,
                    TypeParameterCount = 1,
                    TypeParameterIds = [traitTypeParam],
                    SelfPosition = SelfPosition.InParameter
                }
            ],
            TypeAliases = MirTypeAliasesFrom(symbolTable),
            TraitImpls = MirTraitImplsFrom(symbolTable),
            Functions = [implMethod, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedTraitProjection_UsesMirAliasMetadataWithoutSymbolTable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var aliasTargetType = new TypeId(3001);
        var boxIntType = new TypeId(3002);
        var boxTypeId = new TypeId(3003);
        var aliasTypeId = new TypeId(3004);
        var carrierType = new TypeId(3005);
        var traitId = new SymbolId(3006);
        var aliasId = new SymbolId(3007);
        var aliasTypeParamId = new SymbolId(3008);
        var traitMethodSymbol = new SymbolId(3009);
        var implMethodSymbol = new SymbolId(3010);
        var callerSymbol = new SymbolId(3011);
        var implId = new SymbolId(3012);

        var aliasTypeParamType = new TypeId(aliasTypeParamId.Value);
        var boxConstructorDescriptor = $"type:{boxTypeId.Value}";
        var implMethod = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = boxIntType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "wrap",
            symbolId: implMethodSymbol);

        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "box", TypeId = boxIntType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "wrap",
                        SymbolId = traitMethodSymbol,
                        TypeId = intType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [LocalPlace(1, boxIntType)]
                }
            ],
            returnValue: callerResult,
            name: "caller_alias_hkt",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "custom_higher_kinded_trait_alias_projection_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [aliasTypeParamType.Value] = new TypeDescriptor.TypeVar(aliasTypeParamId.Value),
                [aliasTargetType.Value] = new TypeDescriptor.TyCon(boxConstructorDescriptor, [aliasTypeParamType]),
                [boxIntType.Value] = new TypeDescriptor.TyCon(boxConstructorDescriptor, [intType])
            },
            TraitInfos =
            [
                new MirTraitInfo
                {
                    TraitId = traitId,
                    TypeParameterCount = 1,
                    SelfPosition = SelfPosition.InParameter
                }
            ],
            TypeAliases =
            [
                new MirTypeAliasInfo
                {
                    AliasId = aliasId,
                    Name = "BoxAlias",
                    TypeId = aliasTypeId,
                    AliasTarget = aliasTargetType,
                    TypeParameterIds = [aliasTypeParamId]
                }
            ],
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Buildable_BoxAlias",
                    Trait = traitId,
                    ImplementingType = carrierType,
                    TraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(aliasId, aliasTypeId, "BoxAlias", [])
                    ],
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

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedTraitProjection_UsesTyConTypeIdWithoutSymbolTable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxIntType = new TypeId(3021);
        var boxTypeId = new TypeId(3022);
        var carrierType = new TypeId(3023);
        var traitId = new SymbolId(3024);
        var traitMethodSymbol = new SymbolId(3025);
        var implMethodSymbol = new SymbolId(3026);
        var callerSymbol = new SymbolId(3027);
        var implId = new SymbolId(3028);
        var otherTypeId = new TypeId(3029);
        var otherImplMethodSymbol = new SymbolId(3030);
        var otherImplId = new SymbolId(3031);

        var boxConstructorDescriptor = $"type:{boxTypeId.Value}";
        var implMethod = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = boxIntType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "wrap",
            symbolId: implMethodSymbol);
        var otherImplMethod = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = boxIntType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(2)
            },
            name: "wrap",
            symbolId: otherImplMethodSymbol);

        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "box", TypeId = boxIntType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "wrap",
                        SymbolId = traitMethodSymbol,
                        TypeId = intType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [LocalPlace(1, boxIntType)]
                }
            ],
            returnValue: callerResult,
            name: "caller_tycon_typeid_hkt",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "custom_higher_kinded_trait_typeid_projection_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [boxIntType.Value] = new TypeDescriptor.TyCon(boxConstructorDescriptor, [intType])
            },
            TraitInfos =
            [
                new MirTraitInfo
                {
                    TraitId = traitId,
                    TypeParameterCount = 1,
                    SelfPosition = SelfPosition.InParameter
                }
            ],
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Buildable_Box",
                    Trait = traitId,
                    ImplementingType = carrierType,
                    TraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(SymbolId.None, boxTypeId, "Box", [])
                    ],
                    Methods = [implMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = implMethodSymbol
                    }
                },
                new ImplSymbol
                {
                    Id = otherImplId,
                    Name = "Buildable_Other",
                    Trait = traitId,
                    ImplementingType = carrierType,
                    TraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(SymbolId.None, otherTypeId, "Other", [])
                    ],
                    Methods = [otherImplMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = otherImplMethodSymbol
                    }
                }
            ],
            Functions = [implMethod, otherImplMethod, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedDispatch_UsesMirTraitTypeParameterIdsWithoutSymbolTable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxIntType = new TypeId(3071);
        var traitId = new SymbolId(3072);
        var traitTypeParameterId = new SymbolId(3073);
        var traitMethodSymbol = new SymbolId(3074);
        var implMethodSymbol = new SymbolId(3075);
        var callerSymbol = new SymbolId(3076);
        var implId = new SymbolId(3077);

        var implMethod = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = boxIntType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "wrap_generic_trait_arg",
            symbolId: implMethodSymbol);

        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "box", TypeId = boxIntType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = intType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "wrap",
                        SymbolId = traitMethodSymbol,
                        TypeId = intType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [LocalPlace(1, boxIntType)]
                }
            ],
            returnValue: callerResult,
            name: "caller_generic_trait_arg",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "higher_kinded_trait_type_parameter_ids_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [boxIntType.Value] = new TypeDescriptor.TyCon($"type:{boxIntType.Value}", [intType])
            },
            TraitInfos =
            [
                new MirTraitInfo
                {
                    TraitId = traitId,
                    TypeParameterCount = 1,
                    TypeParameterIds = [traitTypeParameterId],
                    SelfPosition = SelfPosition.InParameter
                }
            ],
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Buildable_Generic",
                    Trait = traitId,
                    TraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(traitTypeParameterId, TypeId.None, "MisleadingTypeParamName", [])
                    ],
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

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

}
