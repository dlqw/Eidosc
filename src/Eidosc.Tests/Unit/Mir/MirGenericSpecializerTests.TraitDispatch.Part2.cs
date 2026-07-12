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
    public void Run_TraitDispatch_GenericImplHeadUsesTypeVarDescriptorWithoutSymbolTable()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var typeVariable = new TypeId(1911);
        var traitId = new SymbolId(1912);
        var traitMethodSymbol = new SymbolId(1913);
        var implMethodSymbol = new SymbolId(1914);
        var implId = new SymbolId(1915);

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
                Value = new MirConstantValue.StringValue("value")
            },
            name: "display_generic",
            symbolId: implMethodSymbol);

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
            name: "caller_display_generic_impl",
            symbolId: new SymbolId(1916));

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "generic_impl_head_typevar_descriptor_without_symbol_table",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [typeVariable.Value] = new TypeDescriptor.TypeVar(0)
            },
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Display_T",
                    Trait = traitId,
                    ImplementingType = typeVariable,
                    ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, typeVariable, "T", []),
                    Methods = [implMethodSymbol],
                    TraitMethodImplementations = new Dictionary<SymbolId, SymbolId>
                    {
                        [traitMethodSymbol] = implMethodSymbol
                    }
                }
            ],
            Functions = [implMethod, caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.Name == "caller_display_generic_impl");
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("display_generic", rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitDispatch_StructuredImplHeadDoesNotMatchOnlyByBaseTypeId()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitId = new SymbolId(19400);
        var traitMethodSymbol = new SymbolId(19401);
        var implMethodSymbol = new SymbolId(19402);
        var implId = new SymbolId(19403);
        var callerSymbol = new SymbolId(19404);
        var boxSymbolId = new SymbolId(19405);
        var boxTypeId = new TypeId(19406);
        var boxIntType = new TypeId(19407);
        var boxStringType = new TypeId(19408);
        var boxConstructorDescriptor = $"type:{boxTypeId.Value}";

        var implMethod = BuildFunction(
            returnType: stringType,
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
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("box-int")
            },
            name: "display_box_int",
            symbolId: implMethodSymbol);

        var callerArg = LocalPlace(1, boxStringType);
        var callerResult = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = callerArg.Local, Name = "value", TypeId = boxStringType, IsParameter = true },
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
            name: "caller_display_box_string",
            symbolId: callerSymbol);

        var intShape = new ImplConstructorShapeNode("Int", []) { TypeId = intType };
        var boxIntKey = new ImplTypeRefKey(
            boxSymbolId,
            boxTypeId,
            "Box",
            [new ImplTypeRefKey(SymbolId.None, intType, "Int", [])]);
        var module = new MirModule
        {
            Name = "trait_dispatch_structured_impl_head_base_type_mismatch",
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [boxIntType.Value] = new TypeDescriptor.TyCon(boxConstructorDescriptor, [intType]),
                [boxStringType.Value] = new TypeDescriptor.TyCon(boxConstructorDescriptor, [stringType])
            },
            TraitImpls =
            [
                new ImplSymbol
                {
                    Id = implId,
                    Name = "Display_Box_Int",
                    Trait = traitId,
                    ImplementingType = boxTypeId,
                    ImplementingTypeKey = boxIntKey,
                    ImplementingTypeShape = new ImplConstructorShapeNode("Box", [intShape])
                    {
                        SymbolId = boxSymbolId,
                        TypeId = boxTypeId
                    },
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
        var call = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal(traitMethodSymbol, functionRef.SymbolId);
        Assert.Equal("display", functionRef.Name);
    }

    [Fact]
    public void Run_TraitDispatch_UsesStructuredImplMethodMappingWithoutSymbolTable()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitId = new SymbolId(19300);
        var traitMethodSymbol = new SymbolId(19301);
        var implMethodSymbol = new SymbolId(19302);
        var implId = new SymbolId(19303);
        var callerSymbol = new SymbolId(19304);

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
            name: "display_int_impl",
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
            name: "caller_structured_mapping",
            symbolId: callerSymbol);

        var module = new MirModule
        {
            Name = "trait_method_mapping_without_symbol_table",
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

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(implMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("display_int_impl", rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitMethodWithKnownId_MissingImplMappingDoesNotFallbackToMethodName()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitMethodSymbol = new SymbolId(19320);
        var implMethodSymbol = new SymbolId(19321);
        var callerSymbol = new SymbolId(19322);

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
        symbolTable.AddMethodToImpl(implId, implMethodSymbol);

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
            name: "caller_known_trait_missing_mapping",
            symbolId: callerSymbol);

        var implMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal { Id = argument.Local, Name = "value", TypeId = intType, IsParameter = true }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue("int")
            },
            name: "display",
            symbolId: implMethodSymbol);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "known_trait_method_missing_impl_mapping",
            Functions = [caller, implMethod]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(traitMethodSymbol, rewrittenRef.SymbolId);
        Assert.Equal("display", rewrittenRef.Name);
    }

    [Fact]
    public void Run_TraitDispatch_WithPartialMetadataWithoutSymbolTable_DoesNotThrow()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var callerSymbol = new SymbolId(19305);

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
                        SymbolId = SymbolId.None,
                        TypeId = stringType,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [callerArg]
                }
            ],
            returnValue: callerResult,
            name: "caller_partial_trait_metadata",
            symbolId: callerSymbol);

        var specialized = new MirGenericSpecializer().Run(new MirModule
        {
            Name = "partial_trait_metadata_without_symbol_table",
            Functions = [caller]
        });

        var rewrittenCaller = specialized.Functions.Single(function => function.SymbolId == callerSymbol);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(SymbolId.None, functionRef.SymbolId);
        Assert.Equal("display", functionRef.Name);
    }

}
