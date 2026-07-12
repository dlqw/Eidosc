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
    public void Run_TraitDispatch_UsesMirTraitInfoSelfPositionWithoutSymbolTable()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitId = new SymbolId(1737);
        var traitMethodSymbol = new SymbolId(1738);
        var implMethodSymbol = new SymbolId(1739);
        var implId = new SymbolId(1740);

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
                        TraitOwnerId = traitId
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_display",
            symbolId: new SymbolId(1741));

        var module = new MirModule
        {
            Name = "trait_info_self_position_without_symbol_table",
            TraitInfos =
            [
                new MirTraitInfo
                {
                    TraitId = traitId,
                    SelfPosition = SelfPosition.InParameter
                }
            ],
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
        Assert.Equal("display", rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitDispatch_UsesMirTraitMethodInfoWithoutSymbolTable()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitId = new SymbolId(2050);
        var traitMethodSymbol = new SymbolId(2051);
        var implMethodSymbol = new SymbolId(2052);
        var implId = new SymbolId(2053);
        var callerSymbol = new SymbolId(2054);

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
                        TypeId = stringType
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_display_method_info",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "trait_method_info_without_symbol_table",
            TraitInfos =
            [
                new MirTraitInfo
                {
                    TraitId = traitId,
                    TypeParameterCount = 0,
                    HasMethodDispatchMetadata = true,
                    Methods =
                    [
                        new MirTraitMethodInfo
                        {
                            TraitId = traitId,
                            MethodId = traitMethodSymbol,
                            Name = "display",
                            SelfPosition = SelfPosition.InParameter,
                            SelfParameterIndices = [0]
                        }
                    ]
                }
            ],
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
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("display", rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitDispatch_UsesMirMethodSignatureFallbackWithoutSymbolTable()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitId = new SymbolId(1742);
        var traitMethodSymbol = new SymbolId(1743);
        var implMethodSymbol = new SymbolId(1744);
        var implId = new SymbolId(1745);

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
                        TraitOwnerId = traitId
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_display",
            symbolId: new SymbolId(1746));

        var module = new MirModule
        {
            Name = "trait_signature_fallback_without_symbol_table",
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
        Assert.Equal("display", rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitDispatchSignatureCompatibility_PrefersMirFunctionSignatureOverSymbolTable()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var traitMethodSymbol = new SymbolId(1747);
        var implMethodSymbol = new SymbolId(1748);
        var implId = new SymbolId(1749);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Display", SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodSymbol,
            Name = "display_stale",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [boolType],
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
            name: "display_int",
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
                        TraitOwnerId = traitId
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_display",
            symbolId: new SymbolId(1750));

        var module = new MirModule
        {
            Name = "trait_signature_prefers_mir_function",
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
        Assert.Equal("display_int", rewrittenRef.Name);
    }

}
