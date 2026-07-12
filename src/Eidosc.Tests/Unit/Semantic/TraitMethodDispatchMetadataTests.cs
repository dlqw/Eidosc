using Eidosc.Symbols;
using System.Linq;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class TraitMethodDispatchMetadataTests
{
    [Fact]
    public void Namer_TraitWithMixedMethodSelfPositions_RecordsMethodSpecificMetadata()
    {
        const string source = """
Factory[F: kind2] :: trait {
    makebox[A] :: A -> F[A]
    unbox[A] :: F[A] -> A
    consume[A] :: A -> F[A] -> A
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "trait_method_dispatch_metadata.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        var symbolTable = result.SymbolTable;
        Assert.NotNull(symbolTable);

        var trait = Assert.Single(symbolTable!.Symbols.Values.OfType<TraitSymbol>(), symbol => symbol.Name == "Factory");
        Assert.Equal(SelfPosition.Both, trait.SelfPosition);

        var makebox = Assert.Single(symbolTable.Symbols.Values.OfType<FuncSymbol>(), symbol => symbol.Name == "makebox");
        var unbox = Assert.Single(symbolTable.Symbols.Values.OfType<FuncSymbol>(), symbol => symbol.Name == "unbox");
        var consume = Assert.Single(symbolTable.Symbols.Values.OfType<FuncSymbol>(), symbol => symbol.Name == "consume");

        Assert.Equal(trait.Id, makebox.OwnerTrait);
        Assert.Equal(trait.Id, unbox.OwnerTrait);
        Assert.Equal(trait.Id, consume.OwnerTrait);
        Assert.Equal(SelfPosition.InResult, makebox.TraitSelfPosition);
        Assert.Equal(SelfPosition.InParameter, unbox.TraitSelfPosition);
        Assert.Equal(SelfPosition.InParameter, consume.TraitSelfPosition);
        Assert.Equal([0], unbox.TraitSelfParameterIndices);
        Assert.Equal([1], consume.TraitSelfParameterIndices);
        Assert.False(consume.TraitSelfInResult);
    }

    [Fact]
    public void Namer_ShowTraitMethod_RecordsStructuredShowRole()
    {
        const string source = """
Show :: trait {
    show :: Self -> String
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "show_trait_method_role.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        var symbolTable = result.SymbolTable;
        Assert.NotNull(symbolTable);

        var show = Assert.Single(symbolTable!.Symbols.Values.OfType<FuncSymbol>(), symbol => symbol.Name == "show");
        Assert.Equal(TraitMethodRole.Show, show.TraitMethodRole);
    }
}
