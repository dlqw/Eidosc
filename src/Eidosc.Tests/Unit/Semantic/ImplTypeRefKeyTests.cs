using Eidosc.Symbols;
using Eidosc;
using Eidosc.Semantic;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class ImplTypeRefKeyTests
{
    [Fact]
    public void Equals_DifferentSymbolsWithSameTypeId_TreatsKeysAsSameType()
    {
        var left = new ImplTypeRefKey(new SymbolId(10), new TypeId(30), "Alias", []);
        var right = new ImplTypeRefKey(new SymbolId(20), new TypeId(30), "Target", []);

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Equals_SameSymbolWithoutCommonTypeId_UsesSymbolIdentity()
    {
        var left = new ImplTypeRefKey(new SymbolId(10), TypeId.None, "Left", []);
        var right = new ImplTypeRefKey(new SymbolId(10), TypeId.None, "Right", []);

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Equals_SameSymbolWithOnlyOneTypeId_DoesNotMixIdentityKinds()
    {
        var symbolOnly = new ImplTypeRefKey(new SymbolId(10), TypeId.None, "Box", []);
        var symbolAndType = new ImplTypeRefKey(new SymbolId(10), new TypeId(30), "Box", []);
        var sameTypeDifferentSymbol = new ImplTypeRefKey(new SymbolId(20), new TypeId(30), "BoxAlias", []);

        Assert.NotEqual(symbolOnly, symbolAndType);
        Assert.Equal(symbolAndType, sameTypeDifferentSymbol);
        Assert.NotEqual(symbolOnly, sameTypeDifferentSymbol);
    }

    [Fact]
    public void Equals_DifferentTypeArguments_RemainsDistinct()
    {
        var intArg = new ImplTypeRefKey(SymbolId.None, new TypeId(BaseTypes.IntId), "Int", []);
        var stringArg = new ImplTypeRefKey(SymbolId.None, new TypeId(BaseTypes.StringId), "String", []);
        var left = new ImplTypeRefKey(new SymbolId(10), new TypeId(30), "Box", [intArg]);
        var right = new ImplTypeRefKey(new SymbolId(20), new TypeId(30), "BoxAlias", [stringArg]);

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void ToString_WhenTypeIdIsAvailable_PrefersTypeIdentityOverSymbolIdentity()
    {
        var key = new ImplTypeRefKey(new SymbolId(10), new TypeId(30), "Alias", []);

        Assert.Equal("T30", key.ToString());
    }

    [Fact]
    public void FromCanonicalText_NestedTypeArguments_PreservesStructuredShape()
    {
        var key = ImplTypeRefKey.FromCanonicalText("Map[String, Box[Int]]");

        Assert.Equal("Map", key.Text);
        Assert.Equal(2, key.TypeArguments.Length);
        Assert.Equal("String", key.TypeArguments[0].Text);

        var boxArg = key.TypeArguments[1];
        Assert.Equal("Box", boxArg.Text);
        var intArg = Assert.Single(boxArg.TypeArguments);
        Assert.Equal("Int", intArg.Text);
    }
}
