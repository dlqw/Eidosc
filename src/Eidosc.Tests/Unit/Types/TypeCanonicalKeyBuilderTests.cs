using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.Tests.Unit.Types;

public sealed class TypeCanonicalKeyBuilderTests
{
    [Fact]
    public void Build_UsesResolvedTypeIdForTyConHead()
    {
        var key = TypeCanonicalKeyBuilder.Build(
            new TyCon { Name = "Box", Args = [BaseTypes.Int] },
            con => con.Name == "Box" ? new TypeId(100) : BaseTypes.GetBuiltInTypeId(con.Name));

        Assert.Equal($"type:100[type:{BaseTypes.IntId}]", key);
    }

    [Fact]
    public void Build_UsesConstructorVariableBeforeTypeIdentity()
    {
        var key = TypeCanonicalKeyBuilder.Build(
            new TyCon
            {
                Name = "F",
                Id = new TypeId(20),
                ConstructorVarIndex = 3,
                Args = [new TyVar { Index = 1 }]
            },
            con => con.Id);

        Assert.Equal("ctorvar:3[var:1]", key);
    }

    [Fact]
    public void Build_PreservesCompositeTypeFormat()
    {
        var type = new TyFun
        {
            Params =
            [
                new TyRef { Inner = BaseTypes.Int },
                new TyTuple { Elements = [BaseTypes.Bool, new TyMutRef { Inner = BaseTypes.String }] }
            ],
            Result = BaseTypes.Unit
        };

        var key = TypeCanonicalKeyBuilder.Build(type, con => BaseTypes.GetBuiltInTypeId(con.Name));

        Assert.Equal(
            $"fun(ref(type:{BaseTypes.IntId}),tuple(type:{BaseTypes.BoolId},mref(type:{BaseTypes.StringId})))->type:{BaseTypes.UnitId}",
            key);
    }
}
