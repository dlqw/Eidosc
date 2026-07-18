using Eidosc.Types;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class SubstitutionTests
{
    [Fact]
    public void Instantiate_ValueGenericTemplate_FreshensAndSubstitutesEachInstanceIndependently()
    {
        var substitution = new Substitution();
        var typeParameter = new TyVar { Index = 100 };
        var scheme = new TypeScheme
        {
            ForAll = [typeParameter.Index],
            Type = new TyFun
            {
                Params = [CreateOpenValueGenericType("Buffer", typeParameter)],
                Result = CreateOpenValueGenericType("Wrapper", typeParameter)
            }
        };

        var first = Assert.IsType<TyFun>(substitution.Instantiate(scheme));
        var second = Assert.IsType<TyFun>(substitution.Instantiate(scheme));
        var firstResult = Assert.IsType<TyCon>(first.Result);
        var secondResult = Assert.IsType<TyCon>(second.Result);

        Assert.NotEqual(
            firstResult.ValueArgs[0].ValueVariableIndex,
            secondResult.ValueArgs[0].ValueVariableIndex);
        Assert.Equal(
            Assert.IsType<TyCon>(first.Params[0]).ValueArgs[0].ValueVariableIndex,
            firstResult.ValueArgs[0].ValueVariableIndex);

        substitution.Unify(first.Result, CreateConcreteValueGenericType("Wrapper", 4));
        substitution.Unify(second.Result, CreateConcreteValueGenericType("Wrapper", 5));

        var resolvedFirst = Assert.IsType<TyFun>(substitution.Apply(first));
        var resolvedSecond = Assert.IsType<TyFun>(substitution.Apply(second));
        Assert.Equal("Buffer<4, Int> -> Wrapper<4, Int>", resolvedFirst.ToString());
        Assert.Equal("Buffer<5, Int> -> Wrapper<5, Int>", resolvedSecond.ToString());
        Assert.Equal("value-4", Assert.IsType<TyCon>(resolvedFirst.Result).ValueArgs[0].CanonicalHash);
        Assert.Equal("value-5", Assert.IsType<TyCon>(resolvedSecond.Result).ValueArgs[0].CanonicalHash);
    }

    [Fact]
    public void Unify_DistinctConcreteValueGenericArguments_RejectsNominalMatch()
    {
        var substitution = new Substitution();

        Assert.Throws<TypeInferenceException>(() => substitution.Unify(
            CreateConcreteValueGenericType("Vector", 4),
            CreateConcreteValueGenericType("Vector", 5)));
    }

    [Fact]
    public void Unify_DistinctConcreteEffectGenericArguments_RejectsNominalMatch()
    {
        var substitution = new Substitution();
        var io = new TyCon { Name = "io", Symbol = new SymbolId(101) };
        var alloc = new TyCon { Name = "Alloc", Symbol = new SymbolId(102) };
        var left = new TyCon
        {
            Name = "Envelope",
            Symbol = new SymbolId(100),
            EffectArgs = [new GenericEffectArgument(0, io)]
        };
        var right = left with
        {
            EffectArgs = [new GenericEffectArgument(0, alloc)]
        };

        Assert.Throws<TypeInferenceException>(() => substitution.Unify(left, right));
    }

    [Fact]
    public void TypeShapePayload_RoundTripsEffectGenericIdentity()
    {
        var type = new TyCon
        {
            Name = "Envelope",
            Symbol = new SymbolId(100),
            EffectArgs =
            [
                new GenericEffectArgument(
                    0,
                    new TyCon { Name = "io", Symbol = new SymbolId(101), Id = new TypeId(201) })
            ]
        };

        var payload = TypeShapePayload.Create(type);

        Assert.True(payload.TryRestoreType(out var restored));
        var restoredType = Assert.IsType<TyCon>(restored);
        var effectArgument = Assert.Single(restoredType.EffectArgs);
        Assert.Equal(0, effectArgument.ParameterIndex);
        var restoredEffect = Assert.IsType<TyCon>(effectArgument.Argument);
        Assert.Equal(new SymbolId(101), restoredEffect.Symbol);
        Assert.Equal(new TypeId(201), restoredEffect.Id);
    }

    [Fact]
    public void TypeShapePayload_RoundTripsFreshValueVariableIdentity()
    {
        var type = CreateOpenValueGenericType("Buffer", BaseTypes.Int);
        type = type with
        {
            ValueArgs = [type.ValueArgs[0] with { ValueVariableIndex = 7 }]
        };

        var payload = TypeShapePayload.Create(type);

        Assert.True(payload.TryRestoreType(out var restored));
        var restoredType = Assert.IsType<TyCon>(restored);
        Assert.Equal(7, restoredType.ValueArgs[0].ValueVariableIndex);
        Assert.Equal(0, restoredType.ValueArgs[0].ReferencedParameterIndex);
        Assert.Equal(new TypeId(BaseTypes.IntId), restoredType.ValueArgs[0].TypeId);
    }

    [Fact]
    public void TypeSubstitutionPayload_RoundTripsValueBindingsAndAllocator()
    {
        var substitution = new Substitution();
        var scheme = new TypeScheme
        {
            Type = CreateOpenValueGenericType("Buffer", BaseTypes.Int)
        };
        var instance = Assert.IsType<TyCon>(substitution.Instantiate(scheme));
        substitution.Unify(instance, CreateConcreteValueGenericType("Buffer", 4));
        var payload = TypeSubstitutionPayload.Create(substitution);

        Assert.True(payload.TryRestoreSubstitution(out var restored));
        Assert.Equal(substitution.NextFreshValueVarIndex, restored.NextFreshValueVarIndex);
        Assert.Equal(substitution.ValueCount, restored.ValueCount);
        Assert.Equal("Buffer<4, Int>", restored.Apply(instance).ToString());
    }

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

    private static TyCon CreateOpenValueGenericType(string name, Eidosc.Types.Type typeArgument)
    {
        return new TyCon
        {
            Name = name,
            Args = [typeArgument],
            ValueArgs =
            [
                new GenericValueArgument(
                    0,
                    "value-parameter:0:4e",
                    "parameter-n",
                    "N",
                    new TypeId(BaseTypes.IntId),
                    ReferencedParameterIndex: 0)
            ]
        };
    }

    private static TyCon CreateConcreteValueGenericType(string name, int value)
    {
        return new TyCon
        {
            Name = name,
            Args = [BaseTypes.Int],
            ValueArgs =
            [
                new GenericValueArgument(
                    0,
                    $"typed:496e74:int:{value}",
                    $"value-{value}",
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new TypeId(BaseTypes.IntId))
            ]
        };
    }
}
