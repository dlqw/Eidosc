using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class SubstitutionTests
{
    [Fact]
    public void Unify_ConstructorVariableWithConcreteOpenAliasShape_MapsConcreteArgumentIntoMiddleSlot()
    {
        var substitution = new Substitution();
        var g = substitution.FreshTypeVariable();

        var openType = new TyCon
        {
            Name = "G",
            ConstructorVarIndex = ((TyVar)g).Index,
            Args = [BaseTypes.Int]
        };

        var concreteType = new TyCon
        {
            Name = "Triple",
            Args = [BaseTypes.String, BaseTypes.Int, BaseTypes.Bool]
        };

        substitution.Unify(openType, concreteType);

        Assert.Equal(concreteType.ToString(), substitution.Apply(openType).ToString());
    }

    [Fact]
    public void Unify_DeferredOpenAliasShape_DoesNotCommitWrongPlaceholderPosition()
    {
        var substitution = new Substitution();
        var g = substitution.FreshTypeVariable();
        var a = substitution.FreshTypeVariable();
        var b = substitution.FreshTypeVariable();

        var deferredOpenType = new TyCon
        {
            Name = "G",
            ConstructorVarIndex = ((TyVar)g).Index,
            Args = [a]
        };

        var openAliasShape = new TyCon
        {
            Name = "Triple",
            Args = [BaseTypes.String, b, BaseTypes.Bool]
        };

        substitution.Unify(deferredOpenType, openAliasShape);
        substitution.Unify(a, BaseTypes.Int);
        substitution.Unify(b, BaseTypes.Int);

        var concreteType = new TyCon
        {
            Name = "Triple",
            Args = [BaseTypes.String, BaseTypes.Int, BaseTypes.Bool]
        };

        substitution.Unify(
            new TyCon
            {
                Name = "G",
                ConstructorVarIndex = ((TyVar)g).Index,
                Args = [BaseTypes.Int]
            },
            concreteType);

        Assert.Equal(concreteType.ToString(), substitution.Apply(deferredOpenType).ToString());
    }

    [Fact]
    public void Unify_AmbiguousConcreteTripleShape_PrefersMiddlePlaceholderSlotForLaterApplications()
    {
        var substitution = new Substitution();
        var g = substitution.FreshTypeVariable();
        var a = substitution.FreshTypeVariable();

        substitution.Unify(
            new TyCon
            {
                Name = "G",
                ConstructorVarIndex = ((TyVar)g).Index,
                Args = [a]
            },
            new TyCon
            {
                Name = "Triple",
                Args = [BaseTypes.String, BaseTypes.Int, BaseTypes.Bool]
            });

        var resultIntString = new TyCon
        {
            Name = "Result",
            Args = [BaseTypes.Int, BaseTypes.String]
        };

        var applied = new TyCon
        {
            Name = "G",
            ConstructorVarIndex = ((TyVar)g).Index,
            Args = [resultIntString]
        };

        Assert.Equal(
            "Triple<String, Result<Int, String>, Bool>",
            substitution.Apply(applied).ToString());
    }

    [Fact]
    public void Unify_AmbiguousConcreteBinaryShape_PreservesLeftPlaceholderSlotForLaterApplications()
    {
        var substitution = new Substitution();
        var g = substitution.FreshTypeVariable();
        var a = substitution.FreshTypeVariable();

        substitution.Unify(
            new TyCon
            {
                Name = "G",
                ConstructorVarIndex = ((TyVar)g).Index,
                Args = [a]
            },
            new TyCon
            {
                Name = "Result",
                Args = [BaseTypes.Int, BaseTypes.String]
            });

        var applied = new TyCon
        {
            Name = "G",
            ConstructorVarIndex = ((TyVar)g).Index,
            Args = [BaseTypes.Bool]
        };

        Assert.Equal(
            "Result<Bool, String>",
            substitution.Apply(applied).ToString());
    }

    [Fact]
    public void Unify_BinaryShapeWithFreshTrailingTypeVar_PreservesLeftPlaceholderSlotForLaterApplications()
    {
        var substitution = new Substitution();
        var g = substitution.FreshTypeVariable();
        var a = substitution.FreshTypeVariable();
        var e = substitution.FreshTypeVariable();

        substitution.Unify(
            new TyCon
            {
                Name = "G",
                ConstructorVarIndex = ((TyVar)g).Index,
                Args = [a]
            },
            new TyCon
            {
                Name = "Result",
                Args = [BaseTypes.Int, e]
            });

        var applied = new TyCon
        {
            Name = "G",
            ConstructorVarIndex = ((TyVar)g).Index,
            Args = [new TyCon { Name = "Seq", Args = [BaseTypes.Int] }]
        };

        Assert.Equal(
            "Result<Seq<Int>, 't2>",
            substitution.Apply(applied).ToString());
    }
}
