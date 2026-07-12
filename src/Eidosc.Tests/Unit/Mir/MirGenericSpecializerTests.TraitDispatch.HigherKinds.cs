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
    public void Run_HigherKindedDispatch_ImplementingTypeIdIgnoresMisleadingText()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxIntType = new TypeId(3041);
        var carrierType = new TypeId(3042);
        var traitId = new SymbolId(3043);
        var traitMethodSymbol = new SymbolId(3044);
        var implMethodSymbol = new SymbolId(3045);
        var callerSymbol = new SymbolId(3046);
        var implId = new SymbolId(3047);

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
            name: "wrap_misleading_display",
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
            name: "caller_misleading_display",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "higher_kinded_misleading_display",
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
                    SelfPosition = SelfPosition.InParameter
                }
            ],
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Buildable_OtherDisplayedAsBox",
                    Trait = traitId,
                    ImplementingType = carrierType,
                    ImplementingTypeDisplay = "Box",
                    CanonicalImplementingType = "Box",
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
        Assert.Equal(traitMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedDispatch_TextOnlyTraitArgKeyDoesNotMatchReceiverByName()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxIntType = new TypeId(3061);
        var carrierType = new TypeId(3062);
        var traitId = new SymbolId(3063);
        var traitMethodSymbol = new SymbolId(3064);
        var implMethodSymbol = new SymbolId(3065);
        var callerSymbol = new SymbolId(3066);
        var implId = new SymbolId(3067);

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
            name: "wrap_text_only_trait_arg",
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
            name: "caller_text_only_trait_arg",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "higher_kinded_text_only_trait_arg",
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
                    SelfPosition = SelfPosition.InParameter
                }
            ],
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Buildable_TextOnlyBox",
                    Trait = traitId,
                    ImplementingType = carrierType,
                    TraitTypeArgs = ["Box"],
                    CanonicalTraitTypeArgs = ["Box"],
                    TraitTypeArgKeys =
                    [
                        ImplTypeRefKey.FromCanonicalText("Box")
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
        Assert.Equal(traitMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedDispatch_UsesCanonicalTraitArgKeyWhenShapeMissing()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxTypeId = new TypeId(3051);
        var boxIntType = new TypeId(3052);
        var aliasTypeId = new TypeId(3053);
        var traitId = new SymbolId(3054);
        var traitMethodSymbol = new SymbolId(3055);
        var implMethodSymbol = new SymbolId(3056);
        var callerSymbol = new SymbolId(3057);
        var carrierType = new TypeId(3058);
        var implId = new SymbolId(3059);

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
            name: "wrap_canonical_key",
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
            name: "caller_canonical_trait_arg_key",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "higher_kinded_canonical_trait_arg_key",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [boxIntType.Value] = new TypeDescriptor.TyCon($"type:{boxTypeId.Value}", [intType])
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
                    Name = "Buildable_BoxAlias",
                    Trait = traitId,
                    ImplementingType = carrierType,
                    TraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(SymbolId.None, aliasTypeId, "BoxAlias", [])
                    ],
                    CanonicalTraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(SymbolId.None, boxTypeId, "Box", [])
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
    public void Run_HigherKindedDispatch_ResolvesSymConstructorFromMirAliasMetadataWithoutSymbolTable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var aliasTypeId = new TypeId(1941);
        var aliasIntType = new TypeId(1942);
        var aliasId = new SymbolId(1943);
        var traitId = new SymbolId(1944);
        var traitMethodSymbol = new SymbolId(1945);
        var implMethodSymbol = new SymbolId(1946);
        var callerSymbol = new SymbolId(1947);
        var implId = new SymbolId(1948);
        var carrierType = new TypeId(1949);

        var implMethod = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = aliasIntType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "wrap_alias_sym",
            symbolId: implMethodSymbol);

        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "box", TypeId = aliasIntType, IsParameter = true },
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
                    Arguments = [LocalPlace(1, aliasIntType)]
                }
            ],
            returnValue: callerResult,
            name: "caller_alias_sym_constructor",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "higher_kinded_alias_sym_constructor_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [aliasIntType.Value] = new TypeDescriptor.TyCon($"sym:{aliasId.Value}", [intType])
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
                    AliasTarget = TypeId.None,
                    TypeParameterIds = []
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
                        new ImplTypeRefKey(SymbolId.None, aliasTypeId, "BoxAlias", [])
                    ],
                    Methods = [implMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = implMethodSymbol
                    }
                }
            ],
            Functions = [implMethod, caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedDispatch_ResolvesSymbolOnlyTraitArgKeyFromMirAliasMetadataWithoutSymbolTable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var aliasTypeId = new TypeId(2061);
        var aliasIntType = new TypeId(2062);
        var aliasId = new SymbolId(2063);
        var traitId = new SymbolId(2064);
        var traitMethodSymbol = new SymbolId(2065);
        var implMethodSymbol = new SymbolId(2066);
        var callerSymbol = new SymbolId(2067);
        var implId = new SymbolId(2068);
        var carrierType = new TypeId(2069);

        var implMethod = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = aliasIntType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "wrap_symbol_only_alias",
            symbolId: implMethodSymbol);

        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "box", TypeId = aliasIntType, IsParameter = true },
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
                    Arguments = [LocalPlace(1, aliasIntType)]
                }
            ],
            returnValue: callerResult,
            name: "caller_symbol_only_alias_key",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "higher_kinded_symbol_only_alias_key_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [aliasIntType.Value] = new TypeDescriptor.TyCon($"sym:{aliasId.Value}", [intType])
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
                    AliasTarget = TypeId.None,
                    TypeParameterIds = []
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
                        new ImplTypeRefKey(aliasId, TypeId.None, "IgnoredAliasText", [])
                    ],
                    Methods = [implMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = implMethodSymbol
                    }
                }
            ],
            Functions = [implMethod, caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedDispatch_ResolvesSymbolOnlyTraitArgKeyFromMirTypeConstructorMetadataWithoutSymbolTable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxTypeId = new TypeId(2071);
        var boxIntType = new TypeId(2072);
        var boxSymbol = new SymbolId(2073);
        var traitId = new SymbolId(2074);
        var traitMethodSymbol = new SymbolId(2075);
        var implMethodSymbol = new SymbolId(2076);
        var callerSymbol = new SymbolId(2077);
        var implId = new SymbolId(2078);
        var carrierType = new TypeId(2079);

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
            name: "wrap_symbol_only_type_constructor",
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
            name: "caller_symbol_only_type_constructor_key",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "higher_kinded_symbol_only_type_constructor_key_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [boxIntType.Value] = new TypeDescriptor.TyCon($"sym:{boxSymbol.Value}", [intType])
            },
            TypeConstructors =
            [
                new MirTypeConstructorInfo
                {
                    SymbolId = boxSymbol,
                    Name = "Box",
                    TypeId = boxTypeId
                }
            ],
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
                        new ImplTypeRefKey(boxSymbol, TypeId.None, "IgnoredBoxText", [])
                    ],
                    Methods = [implMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = implMethodSymbol
                    }
                }
            ],
            Functions = [implMethod, caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedDispatch_ResolvesSymConstructorFromTypeDescriptorWithoutSymbolTable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxTypeId = new TypeId(1951);
        var boxIntType = new TypeId(1952);
        var traitId = new SymbolId(1953);
        var traitMethodSymbol = new SymbolId(1954);
        var implMethodSymbol = new SymbolId(1955);
        var callerSymbol = new SymbolId(1956);
        var implId = new SymbolId(1957);
        var carrierType = new TypeId(1958);

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
            name: "wrap_box_descriptor",
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
            name: "caller_box_sym_descriptor",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "higher_kinded_sym_constructor_type_descriptor_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [boxTypeId.Value] = new TypeDescriptor.TyCon($"type:{boxTypeId.Value}", []),
                [boxIntType.Value] = new TypeDescriptor.TyCon($"sym:{boxTypeId.Value}", [intType])
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
                }
            ],
            Functions = [implMethod, caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedDispatch_ResolvesSymTypeIdConstructorFromMirTypeConstructorMetadataWithoutSymbolTable()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxTypeId = new TypeId(1961);
        var boxIntType = new TypeId(1962);
        var boxSymbol = new SymbolId(1963);
        var traitId = new SymbolId(1964);
        var traitMethodSymbol = new SymbolId(1965);
        var implMethodSymbol = new SymbolId(1966);
        var callerSymbol = new SymbolId(1967);
        var implId = new SymbolId(1968);
        var carrierType = new TypeId(1969);

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
            name: "wrap_box_typeid_descriptor",
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
            name: "caller_box_sym_typeid_descriptor",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "higher_kinded_sym_typeid_constructor_metadata_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [boxIntType.Value] = new TypeDescriptor.TyCon($"sym:{boxTypeId.Value}", [intType])
            },
            TypeConstructors =
            [
                new MirTypeConstructorInfo
                {
                    SymbolId = boxSymbol,
                    Name = "Box",
                    TypeId = boxTypeId
                }
            ],
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
                        new ImplTypeRefKey(SymbolId.None, boxTypeId, "IgnoredBoxText", [])
                    ],
                    Methods = [implMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = implMethodSymbol
                    }
                }
            ],
            Functions = [implMethod, caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedDispatch_ImplTypeArgKeyWithSymbolOnlyUsesSymbolTypeId()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var firstBoxType = new TypeId(1845);
        var secondBoxType = new TypeId(1846);
        var secondBoxIntType = new TypeId(1847);
        var firstBoxId = new SymbolId(1848);
        var secondBoxId = new SymbolId(1849);
        var traitId = new SymbolId(1850);
        var traitMethodSymbol = new SymbolId(1851);
        var implId = new SymbolId(1852);
        var implMethodSymbol = new SymbolId(1853);
        var callerSymbol = new SymbolId(1854);
        var carrierType = new TypeId(1855);

        var symbolTable = new SymbolTable();
        symbolTable.RegisterSymbol(new AdtSymbol
        {
            Id = firstBoxId,
            Name = "Box",
            TypeId = firstBoxType,
            Span = SourceSpan.Empty,
            IsModuleLevel = true
        });
        symbolTable.RegisterSymbol(new AdtSymbol
        {
            Id = secondBoxId,
            Name = "Box",
            TypeId = secondBoxType,
            Span = SourceSpan.Empty,
            IsModuleLevel = true
        });

        var implMethod = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = secondBoxIntType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = intType,
                Value = new MirConstantValue.IntValue(1)
            },
            name: "wrap_first_box",
            symbolId: implMethodSymbol);

        var callerResult = LocalPlace(2, intType);
        var caller = BuildFunction(
            returnType: intType,
            locals:
            [
                new MirLocal { Id = new LocalId { Value = 1 }, Name = "box", TypeId = secondBoxIntType, IsParameter = true },
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
                    Arguments = [LocalPlace(1, secondBoxIntType)]
                }
            ],
            returnValue: callerResult,
            name: "caller_symbol_only_key_typeid",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "higher_kinded_symbol_only_key_typeid",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [secondBoxIntType.Value] = new TypeDescriptor.TyCon($"type:{secondBoxType.Value}", [intType])
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
                    Name = "Buildable_FirstBox",
                    Trait = traitId,
                    ImplementingType = carrierType,
                    TraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(firstBoxId, TypeId.None, "Box", [])
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

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(traitMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_HigherKindedDispatch_SymbolOnlyKeyWithoutSymbolTable_IgnoresMisleadingText()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxTypeId = new TypeId(1864);
        var boxIntType = new TypeId(1865);
        var staleBoxId = new SymbolId(1866);
        var traitId = new SymbolId(1867);
        var traitMethodSymbol = new SymbolId(1868);
        var implMethodSymbol = new SymbolId(1869);
        var callerSymbol = new SymbolId(1870);
        var carrierType = new TypeId(1871);
        var implId = new SymbolId(1872);
        var misleadingText = boxTypeId.ToString();

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
            name: "wrap_stale_box",
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
            name: "caller_symbol_only_key_misleading_text",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "higher_kinded_symbol_only_key_misleading_text",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [boxIntType.Value] = new TypeDescriptor.TyCon($"type:{boxTypeId.Value}", [intType])
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
                    Name = "Buildable_StaleBox",
                    Trait = traitId,
                    ImplementingType = carrierType,
                    TraitTypeArgKeys =
                    [
                        new ImplTypeRefKey(staleBoxId, TypeId.None, misleadingText, [])
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
        Assert.Equal(traitMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_ResolvedTraitMethodWithUnknownSelfMetadata_DoesNotUseNamePreference()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boxIntType = new TypeId(1840);
        var traitMethodSymbol = new SymbolId(1841);
        var boxImplMethodSymbol = new SymbolId(1842);
        var otherImplMethodSymbol = new SymbolId(1843);
        var callerSymbol = new SymbolId(1844);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Applicative", SourceSpan.Empty);
        var boxId = symbolTable.DeclareAdt("Box", SourceSpan.Empty);
        var boxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxId));
        var otherId = symbolTable.DeclareAdt("Other", SourceSpan.Empty);
        var otherSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(otherId));

        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "pure",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [],
            ReturnType = boxIntType
        });

        var boxImplId = symbolTable.DeclareImpl(
            traitId,
            boxSymbol.TypeId,
            SourceSpan.Empty,
            implementingTypeDisplay: "Box",
            canonicalImplementingType: "Box");
        var otherImplId = symbolTable.DeclareImpl(
            traitId,
            otherSymbol.TypeId,
            SourceSpan.Empty,
            implementingTypeDisplay: "Other",
            canonicalImplementingType: "Other");

        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = boxImplMethodSymbol,
            Name = "pure",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [],
            ReturnType = boxIntType
        });
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = otherImplMethodSymbol,
            Name = "pure",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [],
            ReturnType = boxIntType
        });
        symbolTable.AddMethodToImpl(boxImplId, boxImplMethodSymbol);
        symbolTable.AddMethodToImpl(otherImplId, otherImplMethodSymbol);

        var boxImplMethod = BuildFunction(
            returnType: boxIntType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = boxIntType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "pure",
            symbolId: boxImplMethodSymbol);
        var otherImplMethod = BuildFunction(
            returnType: boxIntType,
            locals: [],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = boxIntType,
                Value = new MirConstantValue.UnitValue()
            },
            name: "pure",
            symbolId: otherImplMethodSymbol);

        var callerResult = LocalPlace(1, boxIntType);
        var caller = BuildFunction(
            returnType: boxIntType,
            locals:
            [
                new MirLocal { Id = callerResult.Local, Name = "result", TypeId = boxIntType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = callerResult,
                    Function = new MirFunctionRef
                    {
                        Name = "pure",
                        SymbolId = traitMethodSymbol,
                        TypeId = boxIntType
                    },
                    Arguments = []
                }
            ],
            returnValue: callerResult,
            name: "caller_unknown_self_metadata",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "resolved_trait_unknown_self_metadata",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [boxIntType.Value] = $"TyCon(type:{boxIntType.Value};{intType})"
            },
            Functions = [boxImplMethod, otherImplMethod, caller]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(traitMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_FunctionNamedLikeTraitInvokeHelper_WithoutHelperMetadata_DoesNotKeepBuiltinShowCall()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var traitMethodSymbol = new SymbolId(1845);
        var implMethodSymbol = new SymbolId(1846);
        var callerSymbol = new SymbolId(1847);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", SourceSpan.Empty);
        var widgetSymbolId = symbolTable.DeclareAdt("Widget", SourceSpan.Empty);
        var widgetSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(widgetSymbolId));
        var widgetType = widgetSymbol.TypeId;

        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodSymbol,
            Name = "show",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [widgetType],
            ReturnType = stringType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0]
        });

        var implId = symbolTable.DeclareImpl(traitId, widgetType, SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "show",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [widgetType],
            ReturnType = stringType
        });
        symbolTable.AddMethodToImpl(implId, implMethodSymbol, traitMethodSymbol);

        var implMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = widgetType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("widget")
            },
            name: "show",
            symbolId: implMethodSymbol);

        var firstParameter = LocalPlace(1, intType);
        var receiver = LocalPlace(2, widgetType);
        var result = LocalPlace(3, stringType);
        var misleadingNameFunction = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = firstParameter.Local, Name = "prefix", TypeId = intType, IsParameter = true },
                new MirLocal { Id = receiver.Local, Name = "value", TypeId = widgetType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "show",
                        SymbolId = traitMethodSymbol,
                        TypeId = stringType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0],
                        TraitMethodRole = TraitMethodRole.Show
                    },
                    Arguments = [receiver]
                }
            ],
            returnValue: result,
            name: "not_trait__show_value",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "function_name_does_not_imply_traitinvoke_helper",
            TraitImpls = MirTraitImplsFrom(symbolTable),
            Functions = [implMethod, misleadingNameFunction]
        };

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(module);

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
    }

    [Fact]
    public void Run_TraitInvokeShowHelper_KeptBuiltinShowCallCarriesStructuredRole()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var helperSymbol = new SymbolId(1848);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", SourceSpan.Empty);

        var value = LocalPlace(1, intType);
        var result = LocalPlace(2, stringType);
        var helper = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = value.Local, Name = "value", TypeId = intType, IsParameter = true },
                new MirLocal { Id = result.Local, Name = "result", TypeId = stringType }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "show",
                        SymbolId = SymbolId.None,
                        TypeId = stringType
                    },
                    Arguments = [value]
                }
            ],
            returnValue: result,
            name: "Std__TraitInvoke__show_value__spec_int",
            symbolId: helperSymbol,
            traitInvokeHelper: TraitInvokeHelperKind.ShowValue,
            traitInvokeHelperTraitId: traitId);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "kept_builtin_show_role",
            Functions = [helper]
        });

        var rewrittenHelper = specialized.Functions.Single(function => function.SymbolId == helperSymbol);
        var rewrittenCall = Assert.Single(rewrittenHelper.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(SymbolId.None, rewrittenRef.SymbolId);
        Assert.Equal(TraitMethodRole.Show, rewrittenRef.TraitMethodRole);
    }

}
