using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class RefTypeTests
{
    [Fact]
    public void TyRef_CreatesImmutableReference()
    {
        var innerType = BaseTypes.Int;
        var refType = new TyRef { Inner = innerType };

        Assert.Equal("Ref[Int]", refType.ToString());
        Assert.False(refType.IsMutable);
    }

    [Fact]
    public void TyMutRef_CreatesMutableReference()
    {
        var innerType = BaseTypes.String;
        var refType = new TyMutRef { Inner = innerType };

        Assert.Equal("MRef[String]", refType.ToString());
        Assert.True(refType.IsMutable);
    }

    [Fact]
    public void TyRef_IsConcrete_WhenInnerIsConcrete()
    {
        var innerType = BaseTypes.Int;
        var refType = new TyRef { Inner = innerType };

        Assert.True(refType.IsConcrete);
    }

    [Fact]
    public void TyMutRef_IsConcrete_WhenInnerIsConcrete()
    {
        var innerType = BaseTypes.Bool;
        var refType = new TyMutRef { Inner = innerType };

        Assert.True(refType.IsConcrete);
    }

    [Fact]
    public void TyRef_IsNotConcrete_WhenInnerHasTypeVariable()
    {
        var innerType = new TyVar { Index = 0 };
        var refType = new TyRef { Inner = innerType };

        Assert.False(refType.IsConcrete);
    }

    [Fact]
    public void TyRef_FreeTypeVariables_ReturnsInnerVariables()
    {
        var innerType = new TyVar { Index = 5 };
        var refType = new TyRef { Inner = innerType };

        var freeVars = refType.FreeTypeVariables().ToList();

        Assert.Single(freeVars);
        Assert.Equal(5, freeVars[0]);
    }

    [Fact]
    public void TyMutRef_FreeTypeVariables_ReturnsInnerVariables()
    {
        var innerType = new TyVar { Index = 3 };
        var refType = new TyMutRef { Inner = innerType };

        var freeVars = refType.FreeTypeVariables().ToList();

        Assert.Single(freeVars);
        Assert.Equal(3, freeVars[0]);
    }
}
