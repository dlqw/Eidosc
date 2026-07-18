using Eidosc.Symbols;
using System.Reflection;
using Eidosc;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public partial class TraitImplResolutionTests
{
    [Fact]
    public void CompilationPipeline_NameFirstConstructorBridgeInstance_AllowsTypeAndTraitSameName()
    {
        const string source = """
Direction :: type
{
    North :: type {} ,
    South :: type {}
}

Direction :: trait
{
    opposite :: Self -> Self
}

DirectionDirection :: instance Direction for Direction
{
    North => { opposite = South() } |
    South => { opposite = North() }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_type_trait_same_name_bridge.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var typeId = symbolTable.LookupType("Direction");
        var traitId = symbolTable.LookupTrait("Direction");
        Assert.True(typeId.HasValue);
        Assert.True(traitId.HasValue);
        Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(typeId.Value));
        Assert.IsType<TraitSymbol>(symbolTable.GetSymbol(traitId.Value));
    }

    [Fact]
    public void CompilationPipeline_NameFirstConstructorBridgeInstance_ConsumesAssociatedConstants()
    {
        const string source = """
DirectionInfo :: trait
{
    opposite :: Self -> Self
}

Direction :: type
{
    North :: type {} ,
    South :: type {}
}

DirectionInfoDirection :: instance DirectionInfo for Direction
{
    North => { opposite = South() } |
    South => { opposite = North() }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_constructor_bridge_consumes_constants.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("constructor associated constant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompilationPipeline_NameFirstConstructorBridgeInstance_GeneratesExtraParameterMethod()
    {
        const string source = """
DirectionInfo :: trait
{
    is_opposite :: Self -> Self -> Bool
}

Direction :: type
{
    North :: type {} ,
    South :: type {}
}

DirectionInfoDirection :: instance DirectionInfo for Direction
{
    North => { is_opposite = north_is_opposite } |
    South => { is_opposite = south_is_opposite }
}

north_is_opposite :: Direction -> Bool
{
    South() => true,
    _ => false
}

south_is_opposite :: Direction -> Bool
{
    North() => true,
    _ => false
}

check :: Direction -> Direction -> Bool
{
    left => right => is_opposite(left)(right)
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_constructor_bridge_extra_parameter.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void CompilationPipeline_NameFirstConstructorBridgeInstance_MissingConstant_ReportsDiagnostic()
    {
        const string source = """
DirectionInfo :: trait
{
    opposite :: Self -> Self
}

Direction :: type
{
    North :: type {} ,
    South :: type {}
}

DirectionInfoDirection :: instance DirectionInfo for Direction
{
    North => { opposite = South() }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_constructor_bridge_missing_constant.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Constructor 'South' must provide associated constant 'opposite'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_NameFirstGivenExpression_SelectsNamedInstanceMethod()
    {
        const string source = """
Show :: trait
{
    show :: Self -> String;
}

Person :: type
{
    Person:: type(String)
}

ShowPerson :: instance Show
{
    show :: Person -> String
    {
        p => "person"
    }
}

main :: Unit -> String
{
    _ => show(Person("Ada")) given ShowPerson
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_given_ok.eidos",
            StopAtPhase = CompilationPhase.Hir,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));

        var given = Assert.Single(EnumerateAst(result.Ast).OfType<GivenExpr>());
        Assert.True(given.EvidenceSymbolId.IsValid);
        var call = Assert.IsType<CallExpr>(given.Target);
        var callee = Assert.IsType<IdentifierExpr>(call.Function);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var impl = Assert.IsType<ImplSymbol>(symbolTable.GetSymbol(given.EvidenceSymbolId));
        var implementationMethodId = Assert.Single(impl.TraitMethodImplementations.Values);
        Assert.Equal(implementationMethodId, callee.SymbolId);
    }

    [Fact]
    public void CompilationPipeline_NameFirstGivenExpression_RejectsNonInstanceEvidence()
    {
        const string source = """
id :: Int -> Int
{
    x => x
}

notEvidence :: Unit -> Unit
{
    _ => ()
}

main :: Unit -> Int
{
    _ => id(1) given notEvidence
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_given_non_instance.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Given evidence 'notEvidence' must resolve to a named instance.", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_NameFirstGivenExpression_RejectsCallableNotImplementedByEvidence()
    {
        const string source = """
Show :: trait
{
    show :: Self -> String;
}

Person :: type
{
    Person:: type(String)
}

ShowPerson :: instance Show
{
    show :: Person -> String
    {
        p => "person"
    }
}

id :: Int -> Int
{
    x => x
}

main :: Unit -> Int
{
    _ => id(1) given ShowPerson
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_given_wrong_callable.eidos",
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Given evidence 'ShowPerson' does not implement callable 'id'.", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_NameFirstInstance_AssociatedItems_RegisterEvidence()
    {
        const string source = """
Bounded[T] :: trait
{
    Min :: T
    Max :: T
}

BoundedInt :: instance Bounded[Int]
{
    Min :: Int = 0
    Max :: Int = 100
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_associated_items_ok.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var traitId = symbolTable.LookupType("Bounded");
        var intId = symbolTable.LookupType("Int");
        Assert.True(traitId.HasValue);
        Assert.True(intId.HasValue);

        var intSymbol = Assert.IsAssignableFrom<Symbol>(symbolTable.GetSymbol(intId.Value));
        var impl = symbolTable.LookupImplForTrait(intSymbol.TypeId, traitId.Value, ["Int"]);
        Assert.NotNull(impl);
        var trait = Assert.IsType<TraitSymbol>(symbolTable.GetSymbol(traitId.Value));
        Assert.Equal(2, trait.AssociatedConsts.Count);
        Assert.Equal(2, impl.AssociatedConsts.Count);
        Assert.All(trait.AssociatedConsts, associatedId =>
        {
            var associated = Assert.IsType<AssociatedConstSymbol>(symbolTable.GetSymbol(associatedId));
            Assert.Equal(trait.Id, associated.OwnerTrait);
            Assert.False(associated.OwnerImpl.IsValid);
        });
        Assert.All(impl.AssociatedConsts, associatedId =>
        {
            var associated = Assert.IsType<AssociatedConstSymbol>(symbolTable.GetSymbol(associatedId));
            Assert.Equal(trait.Id, associated.OwnerTrait);
            Assert.Equal(impl.Id, associated.OwnerImpl);
        });
    }

    [Fact]
    public void CompilationPipeline_NameFirstInstance_MissingAssociatedItem_ReportsDiagnostic()
    {
        const string source = """
Bounded[T] :: trait
{
    Min :: T
    Max :: T
}

BoundedInt :: instance Bounded[Int]
{
    Min :: Int = 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "name_first_associated_items_missing.eidos",
            StopAtPhase = CompilationPhase.Namer,
            LanguageVersion = EidosLanguageVersions.Current,
            UseColors = false
        }).Run();

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("must implement associated const 'Max'", StringComparison.Ordinal));
    }

}
