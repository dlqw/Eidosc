using Eidosc.Symbols;
using Eidosc;
using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirBuilderTests
{
    [Fact]
    public void Build_TraitMethodFunctionRef_CarriesDispatchMetadata()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var traitMethodSymbol = new SymbolId(3091);
        var callerSymbol = new SymbolId(3092);

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
            TraitSelfParameterIndices = [0],
            TraitSelfInResult = false,
            TraitMethodRole = TraitMethodRole.None
        });

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    ReturnType = stringType,
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "display",
                            SymbolId = traitMethodSymbol,
                            TypeId = stringType
                        },
                        Arguments =
                        [
                            new HirLiteral
                            {
                                LiteralKind = LiteralKind.Int,
                                Value = 1L,
                                TypeId = intType
                            }
                        ],
                        TypeId = stringType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder(null, symbolTable: symbolTable).Build(module);

        var traitInfo = Assert.Single(mirModule.TraitInfos);
        Assert.Equal(traitId, traitInfo.TraitId);
        Assert.Equal(0, traitInfo.TypeParameterCount);
        Assert.True(traitInfo.HasMethodDispatchMetadata);
        var methodInfo = Assert.Single(traitInfo.Methods);
        Assert.Equal(traitId, methodInfo.TraitId);
        Assert.Equal(traitMethodSymbol, methodInfo.MethodId);
        Assert.Equal("display", methodInfo.Name);
        Assert.Equal(SelfPosition.InParameter, methodInfo.SelfPosition);
        Assert.Equal([0], methodInfo.SelfParameterIndices);

        var caller = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var call = Assert.Single(caller.BasicBlocks.Single(block => block.IsEntry).Instructions.OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal(traitId, functionRef.TraitOwnerId);
        Assert.Equal(SelfPosition.InParameter, functionRef.TraitSelfPosition);
        Assert.Equal([0], functionRef.TraitSelfParameterIndices);
    }

    [Fact]
    public void Build_GenericTraitInfo_CarriesTypeParameterIds()
    {
        var symbolTable = new SymbolTable();
        var traitTypeParameter = symbolTable.DeclareTypeParameter("F", SourceSpan.Empty);
        var traitId = symbolTable.DeclareTrait("Functor", SourceSpan.Empty, [traitTypeParameter]);

        var module = new HirModule
        {
            Name = "Main",
            Declarations = []
        };

        var mirModule = new MirBuilder(null, symbolTable: symbolTable).Build(module);

        var traitInfo = Assert.Single(mirModule.TraitInfos);
        Assert.Equal(traitId, traitInfo.TraitId);
        Assert.Equal(1, traitInfo.TypeParameterCount);
        Assert.Equal([traitTypeParameter], traitInfo.TypeParameterIds);
    }

    [Fact]
    public void Build_HirImpl_CarriesStructuredImplMetadataIntoMirModule()
    {
        var traitId = new SymbolId(3101);
        var implId = new SymbolId(3102);
        var methodId = new SymbolId(3103);
        var intType = new TypeId(BaseTypes.IntId);

        var implMetadata = new ImplSymbol
        {
            Id = implId,
            Name = "Display_Int",
            Trait = traitId,
            ImplementingType = intType,
            ImplementingTypeKey = new ImplTypeRefKey(SymbolId.None, intType, "Int", []),
            Methods = [methodId],
            TraitTypeArgKeys = [new ImplTypeRefKey(SymbolId.None, intType, "Int", [])],
            TraitTypeArgShapes = [new ImplConstructorShapeNode("Int", []) { TypeId = intType }],
            ImplementingTypeShape = new ImplConstructorShapeNode("Int", []) { TypeId = intType }
        };

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirImpl
                {
                    Name = implMetadata.Name,
                    TraitId = traitId,
                    ImplementingType = intType,
                    ImplMetadata = implMetadata,
                    IsModuleLevel = true
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);

        var mirImpl = Assert.Single(mirModule.TraitImpls);
        Assert.Equal(implId, mirImpl.Id);
        Assert.Equal(traitId, mirImpl.Trait);
        Assert.Equal(intType, mirImpl.ImplementingType);
        Assert.Equal(methodId, Assert.Single(mirImpl.Methods));
        Assert.False(mirImpl.ImplementingTypeKey.IsEmpty);
        Assert.NotNull(mirImpl.ImplementingTypeShape);
        Assert.Single(mirImpl.TraitTypeArgKeys);
        Assert.Single(mirImpl.TraitTypeArgShapes);
    }

    [Fact]
    public void Build_TypeConstructorMetadata_CarriesTypeParameterIds()
    {
        var symbolTable = new SymbolTable();
        var typeParameterId = symbolTable.DeclareTypeParameter("T", SourceSpan.Empty);
        var boxId = symbolTable.DeclareAdt("Box", SourceSpan.Empty, [typeParameterId]);
        var boxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxId));

        var module = new HirModule
        {
            Name = "Main",
            Declarations = []
        };

        var mirModule = new MirBuilder(null, symbolTable: symbolTable).Build(module);

        var boxConstructor = Assert.Single(
            mirModule.TypeConstructors,
            constructor => constructor.SymbolId == boxId);
        Assert.Equal("Box", boxConstructor.Name);
        Assert.Equal(boxSymbol.TypeId, boxConstructor.TypeId);
        Assert.Equal([typeParameterId], boxConstructor.TypeParameterIds);
    }
}
