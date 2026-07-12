using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class KindInfererTests
{
    private static readonly SourceSpan TestSpan = new(new SourceLocation(0, 0, 0), 0);

    [Fact]
    public void Infer_TraitTyCon_ReturnsStarKind()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var inferer = new KindInferer(symbolTable);

        var traitType = new TyCon
        {
            Name = "Show",
            Symbol = traitId
        };

        Assert.Equal("kind1", inferer.Infer(traitType).Name);
        Assert.Equal(0, inferer.GetExpectedParamCount(traitType));
    }

    [Fact]
    public void Infer_TraitWithAnnotatedTypeParameter_UsesParamKindVector()
    {
        var symbolTable = new SymbolTable();
        var traitTypeParam = symbolTable.DeclareTypeParameter("F", TestSpan, "kind2");
        var traitId = symbolTable.DeclareTrait("Functor", TestSpan, [traitTypeParam]);
        var inferer = new KindInferer(symbolTable);

        var traitType = new TyCon
        {
            Name = "Functor",
            Symbol = traitId
        };

        Assert.Equal("kind2 -> kind1", inferer.Infer(traitType).Name);
        Assert.Equal(1, inferer.GetExpectedParamCount(traitType));
    }

    [Fact]
    public void Infer_UnboundTypeVariable_ReusesSameKindVariable()
    {
        var symbolTable = new SymbolTable();
        var inferer = new KindInferer(symbolTable);
        var typeVar = new TyVar { Index = 42 };

        var first = inferer.Infer(typeVar);
        var second = inferer.Infer(typeVar);

        var firstVar = Assert.IsType<Kind.KVar>(first);
        var secondVar = Assert.IsType<Kind.KVar>(second);
        Assert.Same(firstVar, secondVar);
    }

    [Fact]
    public void Infer_AdtWithAnnotatedHigherOrderTypeParam_UsesParamKindVector()
    {
        var symbolTable = new SymbolTable();

        var boxTypeParam = symbolTable.DeclareTypeParameter("A", TestSpan, "kind1");
        var boxId = symbolTable.DeclareAdt("Box", TestSpan, [boxTypeParam]);

        var applyFirstParam = symbolTable.DeclareTypeParameter("F", TestSpan, "kind2");
        var applySecondParam = symbolTable.DeclareTypeParameter("X", TestSpan, "kind1");
        var applyId = symbolTable.DeclareAdt("Apply", TestSpan, [applyFirstParam, applySecondParam]);

        var inferer = new KindInferer(symbolTable);

        var unapplied = new TyCon
        {
            Name = "Apply",
            Symbol = applyId
        };

        var partiallyApplied = new TyCon
        {
            Name = "Apply",
            Symbol = applyId,
            Args =
            [
                new TyCon
                {
                    Name = "Box",
                    Symbol = boxId
                }
            ]
        };

        Assert.Equal("kind2 -> kind2", inferer.Infer(unapplied).Name);
        Assert.Equal("kind2", inferer.Infer(partiallyApplied).Name);
        Assert.Equal(2, inferer.GetExpectedParamCount(unapplied));
    }
}
