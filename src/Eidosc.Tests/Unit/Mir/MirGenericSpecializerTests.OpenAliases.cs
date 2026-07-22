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
    public void Run_OpenAliasLikeReturnShape_RebuildsConstructorArgumentsBySlotInsteadOfSuffix()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var itemType = new TypeId(1910);
        var openResultType = new TypeId(1911);
        var concreteTripleType = new TypeId(1912);
        var tripleConstructorType = new TypeId(1915);
        var helperSymbol = new SymbolId(1913);
        var callerSymbol = new SymbolId(1914);

        var helper = new MirFunc
        {
            Name = "lift_like",
            SymbolId = helperSymbol,
            ReturnType = openResultType,
            GenericParameterCount = 0,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = itemType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "result",
                    TypeId = openResultType
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
                        Value = LocalPlace(2, openResultType)
                    }
                }
            ]
        };

        var callerArg = LocalPlace(1, intType);
        var callerResult = LocalPlace(2, concreteTripleType);
        var caller = BuildFunction(
            returnType: concreteTripleType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = concreteTripleType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "lift_like",
                        SymbolId = helperSymbol,
                        TypeId = concreteTripleType
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_lift_like",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "open_alias_slot_binding",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [itemType.Value] = "TyVar_0",
                [openResultType.Value] = $"TyCon(var:900;{itemType})",
                [concreteTripleType.Value] = $"TyCon(type:{tripleConstructorType.Value};{stringType},{intType},{boolType})"
            },
            Functions = [helper, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var specializedHelper = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("lift_like__spec_", StringComparison.Ordinal));
        Assert.Equal(concreteTripleType, specializedHelper.ReturnType);

        var specializedParameter = Assert.Single(specializedHelper.Locals, local => local.IsParameter);
        Assert.Equal(intType, specializedParameter.TypeId);

        var specializedResultLocal = Assert.Single(specializedHelper.Locals, local => local.Name == "result");
        Assert.Equal(concreteTripleType, specializedResultLocal.TypeId);

        var rewrittenCaller = Assert.Single(specialized.Functions, function => function.Name == "caller_lift_like");
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(specializedHelper.SymbolId, rewrittenRef.SymbolId);
        Assert.Equal(specializedHelper.Name, rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitAliasConstructorBinding_UsesStructuredTraitTypeArgKeys()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var itemType = new TypeId(1920);
        var openResultType = new TypeId(1921);
        var concreteTripleType = new TypeId(1922);
        var aliasTargetType = new TypeId(1923);
        var helperSymbol = new SymbolId(1924);
        var callerSymbol = new SymbolId(1925);

        var symbolTable = new SymbolTable();
        var traitTypeParam = symbolTable.DeclareTypeParameter("F", SourceSpan.Empty, "kind2");
        var traitId = symbolTable.DeclareTrait("Applicative", SourceSpan.Empty, [traitTypeParam]);
        var leftParam = symbolTable.DeclareTypeParameter("L", SourceSpan.Empty);
        var rightParam = symbolTable.DeclareTypeParameter("R", SourceSpan.Empty);
        var itemParam = symbolTable.DeclareTypeParameter("X", SourceSpan.Empty);
        var aliasId = symbolTable.DeclareAdt("KeepEdges", SourceSpan.Empty, [leftParam, rightParam, itemParam]);
        var aliasSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(aliasId));
        symbolTable.UpdateSymbol(aliasSymbol with { AliasTarget = aliasTargetType });
        aliasSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(aliasId));
        var carrierId = symbolTable.DeclareAdt("ApplicativeCarrier", SourceSpan.Empty);
        var carrierSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(carrierId));

        symbolTable.DeclareImpl(
            traitId,
            carrierSymbol.TypeId,
            SourceSpan.Empty,
            traitTypeArgs: ["LegacyTextMustNotDriveBinding[String,Bool]"],
            traitTypeArgKeys:
            [
                new ImplTypeRefKey(
                    aliasId,
                    aliasSymbol.TypeId,
                    "KeepEdges",
                    [
                        new ImplTypeRefKey(SymbolId.None, stringType, "String", []),
                        new ImplTypeRefKey(SymbolId.None, boolType, "Bool", [])
                    ])
            ],
            implementingTypeDisplay: "ApplicativeCarrier",
            canonicalImplementingType: "ApplicativeCarrier");

        var helper = new MirFunc
        {
            Name = "pure_like",
            SymbolId = helperSymbol,
            ReturnType = openResultType,
            GenericParameterCount = 0,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "item",
                    TypeId = itemType
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "result",
                    TypeId = openResultType
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
                        Value = LocalPlace(2, openResultType)
                    }
                }
            ]
        };

        var callerResult = LocalPlace(1, concreteTripleType);
        var caller = BuildFunction(
            returnType: concreteTripleType,
            locals:
            [
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = concreteTripleType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "pure_like",
                        SymbolId = helperSymbol,
                        TypeId = concreteTripleType
                    },
                    Arguments = []
                }
            ],
            returnValue: callerResult,
            name: "caller_pure_like",
            symbolId: callerSymbol);

        var tripleConstructorType = new TypeId(1926);
        var tripleConstructorDescriptor = $"type:{tripleConstructorType.Value}";
        var module = new MirModule
        {
            Name = "trait_alias_structured_slot_binding",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [itemType.Value] = new TypeDescriptor.TypeVar(0),
                [new TypeId(leftParam.Value).Value] = new TypeDescriptor.TypeVar(leftParam.Value),
                [new TypeId(rightParam.Value).Value] = new TypeDescriptor.TypeVar(rightParam.Value),
                [new TypeId(itemParam.Value).Value] = new TypeDescriptor.TypeVar(itemParam.Value),
                [openResultType.Value] = new TypeDescriptor.TyCon("var:900", [itemType]),
                [aliasTargetType.Value] = new TypeDescriptor.TyCon(
                    tripleConstructorDescriptor,
                    [new TypeId(leftParam.Value), new TypeId(itemParam.Value), new TypeId(rightParam.Value)]),
                [concreteTripleType.Value] = new TypeDescriptor.TyCon(
                    tripleConstructorDescriptor,
                    [stringType, intType, boolType])
            },
            TypeAliases = MirTypeAliasesFrom(symbolTable),
            TraitImpls = MirTraitImplsFrom(symbolTable),
            Functions = [helper, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var specializedHelper = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("pure_like__spec_", StringComparison.Ordinal));
        var itemLocal = Assert.Single(specializedHelper.Locals, local => local.Name == "item");
        Assert.Equal(intType, itemLocal.TypeId);
        Assert.Equal(concreteTripleType, specializedHelper.ReturnType);
    }

    [Fact]
    public void Run_TraitAliasConstructorBinding_UsesMirAliasMetadataWithoutSymbolTable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var itemType = new TypeId(1930);
        var openResultType = new TypeId(1931);
        var concreteTripleType = new TypeId(1932);
        var aliasTargetType = new TypeId(1933);
        var aliasType = new TypeId(1934);
        var helperSymbol = new SymbolId(1935);
        var callerSymbol = new SymbolId(1936);
        var traitId = new SymbolId(1937);
        var aliasId = new SymbolId(1938);
        var leftParam = new SymbolId(1939);
        var rightParam = new SymbolId(1940);
        var itemParam = new SymbolId(1941);
        var implId = new SymbolId(1942);
        var carrierType = new TypeId(1943);

        var helper = new MirFunc
        {
            Name = "pure_like",
            SymbolId = helperSymbol,
            ReturnType = openResultType,
            GenericParameterCount = 0,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "item",
                    TypeId = itemType
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "result",
                    TypeId = openResultType
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
                        Value = LocalPlace(2, openResultType)
                    }
                }
            ]
        };

        var callerResult = LocalPlace(1, concreteTripleType);
        var caller = BuildFunction(
            returnType: concreteTripleType,
            locals:
            [
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = concreteTripleType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "pure_like",
                        SymbolId = helperSymbol,
                        TypeId = concreteTripleType
                    },
                    Arguments = []
                }
            ],
            returnValue: callerResult,
            name: "caller_pure_like",
            symbolId: callerSymbol);

        var tripleConstructorType = new TypeId(1944);
        var tripleConstructorDescriptor = $"type:{tripleConstructorType.Value}";
        var module = new MirModule
        {
            Name = "trait_alias_structured_slot_binding_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [itemType.Value] = new TypeDescriptor.TypeVar(0),
                [openResultType.Value] = new TypeDescriptor.TyCon("var:900", [itemType]),
                [aliasTargetType.Value] = new TypeDescriptor.TyCon(
                    tripleConstructorDescriptor,
                    [new TypeId(leftParam.Value), new TypeId(itemParam.Value), new TypeId(rightParam.Value)]),
                [concreteTripleType.Value] = new TypeDescriptor.TyCon(
                    tripleConstructorDescriptor,
                    [intType, intType, boolType])
            },
            TypeAliases =
            [
                new MirTypeAliasInfo
                {
                    AliasId = aliasId,
                    Name = "KeepEdges",
                    TypeId = aliasType,
                    AliasTarget = aliasTargetType,
                    TypeParameterIds = [leftParam, rightParam, itemParam]
                }
            ],
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Applicative_KeepEdges",
                    Trait = traitId,
                    ImplementingType = carrierType,
                    TraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(
                            aliasId,
                            aliasType,
                            "KeepEdges",
                            [
                                new ImplTypeRefKey(SymbolId.None, intType, "Int", []),
                                new ImplTypeRefKey(SymbolId.None, boolType, "Bool", [])
                            ])
                    ]
                }
            ],
            Functions = [helper, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var specializedHelper = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("pure_like__spec_", StringComparison.Ordinal));
        var itemLocal = Assert.Single(specializedHelper.Locals, local => local.Name == "item");
        Assert.Equal(intType, itemLocal.TypeId);
        Assert.Equal(concreteTripleType, specializedHelper.ReturnType);
    }

    [Fact]
    public void Run_TraitAliasConstructorBinding_UsesCanonicalAliasKeyWhenRawKeyIsTarget()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var itemType = new TypeId(1960);
        var openResultType = new TypeId(1961);
        var concreteTripleType = new TypeId(1962);
        var aliasTargetType = new TypeId(1963);
        var aliasType = new TypeId(1964);
        var helperSymbol = new SymbolId(1965);
        var callerSymbol = new SymbolId(1966);
        var traitId = new SymbolId(1967);
        var aliasId = new SymbolId(1968);
        var leftParam = new SymbolId(1969);
        var rightParam = new SymbolId(1970);
        var itemParam = new SymbolId(1971);
        var implId = new SymbolId(1972);
        var carrierType = new TypeId(1973);

        var helper = new MirFunc
        {
            Name = "pure_like",
            SymbolId = helperSymbol,
            ReturnType = openResultType,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "item",
                    TypeId = itemType
                },
                new MirLocal
                {
                    Id = new LocalId { Value = 2 },
                    Name = "result",
                    TypeId = openResultType
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
                        Value = LocalPlace(2, openResultType)
                    }
                }
            ]
        };

        var callerResult = LocalPlace(1, concreteTripleType);
        var caller = BuildFunction(
            returnType: concreteTripleType,
            locals:
            [
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = concreteTripleType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "pure_like",
                        SymbolId = helperSymbol,
                        TypeId = concreteTripleType
                    },
                    Arguments = []
                }
            ],
            returnValue: callerResult,
            name: "caller_pure_like",
            symbolId: callerSymbol);

        var tripleConstructorType = new TypeId(1974);
        var tripleConstructorDescriptor = $"type:{tripleConstructorType.Value}";
        var module = new MirModule
        {
            Name = "trait_alias_canonical_key_slot_binding_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [itemType.Value] = new TypeDescriptor.TypeVar(0),
                [new TypeId(leftParam.Value).Value] = new TypeDescriptor.TypeVar(leftParam.Value),
                [new TypeId(rightParam.Value).Value] = new TypeDescriptor.TypeVar(rightParam.Value),
                [new TypeId(itemParam.Value).Value] = new TypeDescriptor.TypeVar(itemParam.Value),
                [openResultType.Value] = new TypeDescriptor.TyCon("var:900", [itemType]),
                [aliasTargetType.Value] = new TypeDescriptor.TyCon(
                    tripleConstructorDescriptor,
                    [new TypeId(leftParam.Value), new TypeId(itemParam.Value), new TypeId(rightParam.Value)]),
                [concreteTripleType.Value] = new TypeDescriptor.TyCon(
                    tripleConstructorDescriptor,
                    [stringType, intType, boolType])
            },
            TypeAliases =
            [
                new MirTypeAliasInfo
                {
                    AliasId = aliasId,
                    Name = "KeepEdges",
                    TypeId = aliasType,
                    AliasTarget = aliasTargetType,
                    TypeParameterIds = [leftParam, rightParam, itemParam]
                }
            ],
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Applicative_KeepEdges",
                    Trait = traitId,
                    ImplementingType = carrierType,
                    TraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(
                            SymbolId.None,
                            TypeId.None,
                            "Triple",
                            [
                                new ImplTypeRefKey(SymbolId.None, stringType, "String", []),
                                new ImplTypeRefKey(SymbolId.None, itemType, "X", []),
                                new ImplTypeRefKey(SymbolId.None, boolType, "Bool", [])
                            ])
                    ],
                    CanonicalTraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(
                            aliasId,
                            aliasType,
                            "KeepEdges",
                            [
                                new ImplTypeRefKey(SymbolId.None, stringType, "String", []),
                                new ImplTypeRefKey(SymbolId.None, boolType, "Bool", [])
                            ])
                    ]
                }
            ],
            Functions = [helper, caller]
        };

        var specialized = new MirGenericSpecializer().Run(module);

        var specializedHelper = Assert.Single(
            specialized.Functions,
            function => function.Name.StartsWith("pure_like__spec_", StringComparison.Ordinal));
        var itemLocal = Assert.Single(specializedHelper.Locals, local => local.Name == "item");
        Assert.Equal(intType, itemLocal.TypeId);
        Assert.Equal(concreteTripleType, specializedHelper.ReturnType);
    }

}
