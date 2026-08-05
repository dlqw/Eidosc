using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed partial class MirGenericSpecializerTests
{
    [Theory]
    [InlineData("Point", "Point", true)]
    [InlineData("Anim", "Dog", false)]
    public void Run_ClosedCaseTraitDispatch_ProjectsOnlySameNamedProductCase(
        string rootName,
        string caseName,
        bool expectsRootDispatch)
    {
        var symbolTable = new SymbolTable();
        var rootId = symbolTable.DeclareAdt(rootName, SourceSpan.Empty);
        var caseId = symbolTable.DeclareCaseType(caseName, SourceSpan.Empty, rootId);
        var rootType = symbolTable.GetSymbol<AdtSymbol>(rootId)!.TypeId;
        var caseType = symbolTable.GetSymbol<AdtSymbol>(caseId)!.TypeId;
        var stringType = new TypeId(BaseTypes.StringId);
        var traitMethodId = new SymbolId(9201);
        var implMethodId = new SymbolId(9202);
        var callerId = new SymbolId(9203);

        var traitId = symbolTable.DeclareTrait("Display", SourceSpan.Empty);
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = traitMethodId,
            Name = "display",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = false,
            OwnerTrait = traitId,
            ParamTypes = [rootType],
            ReturnType = stringType,
            TraitSelfPosition = SelfPosition.InParameter,
            TraitSelfParameterIndices = [0]
        });
        symbolTable.RegisterSymbol(new FuncSymbol
        {
            Id = implMethodId,
            Name = "display",
            Span = SourceSpan.Empty,
            IsModuleLevel = true,
            HasBody = true,
            ParamTypes = [rootType],
            ReturnType = stringType
        });

        var rootKey = new ImplTypeRefKey(rootId, rootType, rootName, []);
        var implId = symbolTable.DeclareImpl(
            traitId,
            rootType,
            SourceSpan.Empty,
            implementingTypeDisplay: rootName,
            canonicalImplementingType: rootName,
            implementingTypeKey: rootKey);
        symbolTable.AddMethodToImpl(implId, implMethodId, traitMethodId);
        var implementation = symbolTable.GetSymbol<ImplSymbol>(implId)! with
        {
            ImplementingTypeShape = new ImplConstructorShapeNode(
                TypeConstructorKey.FromSymbol(rootId).ToDescriptorString(),
                [])
            {
                SymbolId = rootId,
                TypeId = rootType
            }
        };

        var implMethod = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = new LocalId { Value = 1 },
                    Name = "value",
                    TypeId = rootType,
                    IsParameter = true
                }
            ],
            instructions: [],
            returnValue: new MirConstant
            {
                TypeId = stringType,
                Value = new MirConstantValue.StringValue(rootName)
            },
            name: "display_impl",
            symbolId: implMethodId,
            sourceName: "display");

        var argument = LocalPlace(1, caseType);
        var result = LocalPlace(2, stringType);
        var caller = BuildFunction(
            returnType: stringType,
            locals:
            [
                new MirLocal
                {
                    Id = argument.Local,
                    Name = "value",
                    TypeId = caseType,
                    IsParameter = true
                },
                new MirLocal
                {
                    Id = result.Local,
                    Name = "result",
                    TypeId = stringType
                }
            ],
            instructions:
            [
                new MirCall
                {
                    Target = result,
                    Function = new MirFunctionRef
                    {
                        Name = "display",
                        SymbolId = traitMethodId,
                        TypeId = stringType,
                        TraitOwnerId = traitId,
                        TraitSelfPosition = SelfPosition.InParameter,
                        TraitSelfParameterIndices = [0]
                    },
                    Arguments = [argument]
                }
            ],
            returnValue: result,
            name: "caller",
            symbolId: callerId);

        var specialized = new MirGenericSpecializer(null, null, symbolTable).Run(new MirModule
        {
            Name = "closed_case_trait_dispatch",
            TraitImpls = [implementation],
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [caseType.Value] = new TypeDescriptor.TyCon(
                    TypeConstructorKey.FromSymbol(caseId),
                    [])
            },
            Functions = [implMethod, caller]
        });

        var rewrittenCaller = Assert.Single(specialized.Functions, function => function.SymbolId == callerId);
        var rewrittenCall = Assert.Single(rewrittenCaller.BasicBlocks.Single().Instructions.OfType<MirCall>());
        var rewrittenReference = Assert.IsType<MirFunctionRef>(rewrittenCall.Function);
        Assert.Equal(expectsRootDispatch ? implMethodId : traitMethodId, rewrittenReference.SymbolId);
    }
}
