using Eidosc.Symbols;
using Eidosc;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public class ImplSpecializationComparerTests
{
    [Fact]
    public void CompareNodes_ConcreteAgainstWildcard_IsMoreSpecific()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("Int"),
            ImplWildcardShapeNode.Instance);

        Assert.Equal(ImplSpecializationRelation.MoreSpecific, relation);
    }

    [Fact]
    public void CompareNodes_WildcardAgainstConcrete_IsLessSpecific()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            ImplWildcardShapeNode.Instance,
            Ctor("Int"));

        Assert.Equal(ImplSpecializationRelation.LessSpecific, relation);
    }

    [Fact]
    public void CompareNodes_EquivalentConcreteShapes_AreEquivalent()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("Result", Var("T"), Ctor("String")),
            Ctor("Result", Var("T"), Ctor("String")));

        Assert.Equal(ImplSpecializationRelation.Equivalent, relation);
    }

    [Fact]
    public void CompareNodes_NestedConcreteAgainstOpenArgument_IsMoreSpecific()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("Box", Ctor("Int")),
            Ctor("Box", Var("A")));

        Assert.Equal(ImplSpecializationRelation.MoreSpecific, relation);
    }

    [Fact]
    public void CompareNodes_MixedMoreAndLessSpecificPositions_AreIncomparable()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("Pair", Ctor("Int"), Var("B")),
            Ctor("Pair", Var("A"), Ctor("String")));

        Assert.Equal(ImplSpecializationRelation.Incomparable, relation);
    }

    [Fact]
    public void CompareNodes_DifferentConstructors_AreIncomparable()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("Option", Ctor("Int")),
            Ctor("Result", Ctor("Int"), Ctor("String")));

        Assert.Equal(ImplSpecializationRelation.Incomparable, relation);
    }

    [Fact]
    public void CompareNodes_SameConstructorNameWithDifferentSymbolIdentity_AreIncomparable()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("Box", new SymbolId(10)),
            Ctor("Box", new SymbolId(20)));

        Assert.Equal(ImplSpecializationRelation.Incomparable, relation);
    }

    [Fact]
    public void CompareNodes_SameConstructorNameWithSameSymbolIdentity_AreEquivalent()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("Box", new SymbolId(10)),
            Ctor("Box", new SymbolId(10)));

        Assert.Equal(ImplSpecializationRelation.Equivalent, relation);
    }

    [Fact]
    public void CompareNodes_DifferentSymbolIdentityWithSameTypeIdentity_AreEquivalent()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("BoxAlias", new SymbolId(10), new TypeId(30)),
            Ctor("Box", new SymbolId(20), new TypeId(30)));

        Assert.Equal(ImplSpecializationRelation.Equivalent, relation);
    }

    [Fact]
    public void CompareNodes_StructuredConstructorAgainstLegacyName_FallsBackToName()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("Box", new SymbolId(10)),
            Ctor("Box"));

        Assert.Equal(ImplSpecializationRelation.Equivalent, relation);
    }

    [Fact]
    public void CompareNodes_SameConstructorNameWithMixedStructuredIdentities_AreIncomparable()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("Box", new SymbolId(10)),
            Ctor("Box", new TypeId(20)));

        Assert.Equal(ImplSpecializationRelation.Incomparable, relation);
    }

    [Fact]
    public void CompareHeads_TraitArgsAndImplementingTypeTogether_DetermineSpecialization()
    {
        var trait = new SymbolId(42);
        var specific = new ImplHeadShape(
            trait,
            [Ctor("Box")],
            Ctor("Result", Ctor("Int"), Ctor("String")));
        var generic = new ImplHeadShape(
            trait,
            [Var("F")],
            Ctor("Result", Var("T"), Ctor("String")));

        var relation = ImplSpecializationComparer.CompareHeads(specific, generic);

        Assert.Equal(ImplSpecializationRelation.MoreSpecific, relation);
    }

    [Fact]
    public void CompareHeads_DifferentTraits_AreIncomparable()
    {
        var left = new ImplHeadShape(new SymbolId(1), [Ctor("Box")], Ctor("Int"));
        var right = new ImplHeadShape(new SymbolId(2), [Ctor("Box")], Ctor("Int"));

        var relation = ImplSpecializationComparer.CompareHeads(left, right);

        Assert.Equal(ImplSpecializationRelation.Incomparable, relation);
    }

    [Fact]
    public void CompareHeadsForSelection_RepeatedVariableHead_IsMoreSpecificThanIndependentVariables()
    {
        var trait = new SymbolId(42);
        var repeated = new ImplHeadShape(trait, [], Ctor("Pair", Var("T"), Var("T")));
        var independent = new ImplHeadShape(trait, [], Ctor("Pair", Var("T"), Var("U")));

        var relation = ImplSpecializationComparer.CompareHeadsForSelection(repeated, independent);

        Assert.Equal(ImplSpecializationRelation.MoreSpecific, relation);
    }

    [Fact]
    public void IsApplicableTo_RepeatedCandidateVariable_RequiresSameRequestedShape()
    {
        Assert.True(ImplSpecializationComparer.IsApplicableTo(
            Ctor("Pair", Ctor("Int"), Ctor("Int")),
            Ctor("Pair", Var("T"), Var("T"))));

        Assert.False(ImplSpecializationComparer.IsApplicableTo(
            Ctor("Pair", Ctor("Int"), Ctor("String")),
            Ctor("Pair", Var("T"), Var("T"))));
    }

    [Fact]
    public void IsApplicableTo_RepeatedCandidateVariable_RequiresSameStructuredIdentity()
    {
        Assert.True(ImplSpecializationComparer.IsApplicableTo(
            Ctor("Pair", Ctor("Box", new SymbolId(10)), Ctor("Box", new SymbolId(10))),
            Ctor("Pair", Var("T"), Var("T"))));

        Assert.False(ImplSpecializationComparer.IsApplicableTo(
            Ctor("Pair", Ctor("Box", new SymbolId(10)), Ctor("Box", new SymbolId(20))),
            Ctor("Pair", Var("T"), Var("T"))));
    }

    [Fact]
    public void IsApplicableTo_SameNameWithMixedStructuredIdentities_ReturnsFalse()
    {
        Assert.False(ImplSpecializationComparer.IsApplicableTo(
            Ctor("Box", new SymbolId(10)),
            Ctor("Box", new TypeId(20))));
    }

    [Fact]
    public void MayOverlap_DifferentConcreteTypeArgs_ReturnsFalse()
    {
        Assert.False(ImplSpecializationComparer.MayOverlap(
            Ctor("Option", Ctor("Int")),
            Ctor("Option", Ctor("String"))));
    }

    [Fact]
    public void MayOverlap_IncomparableButIntersectingShapes_ReturnsTrue()
    {
        Assert.True(ImplSpecializationComparer.MayOverlap(
            Ctor("Pair", Ctor("Int"), Var("B")),
            Ctor("Pair", Var("A"), Ctor("String"))));
    }

    [Fact]
    public void MayOverlap_RepeatedVariableWithDifferentConcreteArgs_ReturnsFalse()
    {
        Assert.False(ImplSpecializationComparer.MayOverlap(
            Ctor("Pair", Var("T"), Var("T")),
            Ctor("Pair", Ctor("Int"), Ctor("String"))));
    }

    [Fact]
    public void MayOverlap_DifferentStructuredConstructorIdentity_ReturnsFalse()
    {
        Assert.False(ImplSpecializationComparer.MayOverlap(
            Ctor("Box", new SymbolId(10)),
            Ctor("Box", new SymbolId(20))));
    }

    [Fact]
    public void MayOverlap_SameNameWithMixedStructuredIdentities_ReturnsFalse()
    {
        Assert.False(ImplSpecializationComparer.MayOverlap(
            Ctor("Box", new SymbolId(10)),
            Ctor("Box", new TypeId(20))));
    }

    [Fact]
    public void MayOverlap_HeadBindingsSpanTraitArgsAndImplementingType()
    {
        var trait = new SymbolId(42);
        var generic = new ImplHeadShape(
            trait,
            [Var("T")],
            Ctor("Box", Var("T")));
        var inconsistentConcrete = new ImplHeadShape(
            trait,
            [Ctor("Int")],
            Ctor("Box", Ctor("String")));

        Assert.False(ImplSpecializationComparer.MayOverlap(generic, inconsistentConcrete));
    }

    [Fact]
    public void CompareNodes_ConcreteValue_IsMoreSpecificThanSymbolicValue()
    {
        var relation = ImplSpecializationComparer.CompareNodes(
            Ctor("Buffer", ConstInt(4)),
            Ctor("Buffer", ValueVar("N")));

        Assert.Equal(ImplSpecializationRelation.MoreSpecific, relation);
    }

    [Fact]
    public void MayOverlap_DifferentConcreteValues_ReturnsFalse()
    {
        Assert.False(ImplSpecializationComparer.MayOverlap(
            Ctor("Buffer", ConstInt(4)),
            Ctor("Buffer", ConstInt(5))));
    }

    [Fact]
    public void IsApplicableTo_SymbolicValueCandidate_MatchesConcreteValue()
    {
        Assert.True(ImplSpecializationComparer.IsApplicableTo(
            Ctor("Buffer", ConstInt(4)),
            Ctor("Buffer", ValueVar("N"))));
    }

    [Fact]
    public void IsApplicableTo_RepeatedSymbolicValue_RequiresSameConcreteValue()
    {
        Assert.True(ImplSpecializationComparer.IsApplicableTo(
            Ctor("Pair", Ctor("Buffer", ConstInt(4)), Ctor("Buffer", ConstInt(4))),
            Ctor("Pair", Ctor("Buffer", ValueVar("N")), Ctor("Buffer", ValueVar("N")))));
        Assert.False(ImplSpecializationComparer.IsApplicableTo(
            Ctor("Pair", Ctor("Buffer", ConstInt(4)), Ctor("Buffer", ConstInt(5))),
            Ctor("Pair", Ctor("Buffer", ValueVar("N")), Ctor("Buffer", ValueVar("N")))));
    }

    private static ImplConstructorShapeNode Ctor(string name, params ImplTypeShapeNode[] args)
        => new(name, args);

    private static ImplConstructorShapeNode Ctor(string name, SymbolId symbolId, params ImplTypeShapeNode[] args)
        => new(name, args)
        {
            SymbolId = symbolId
        };

    private static ImplConstructorShapeNode Ctor(string name, SymbolId symbolId, TypeId typeId, params ImplTypeShapeNode[] args)
        => new(name, args)
        {
            SymbolId = symbolId,
            TypeId = typeId
        };

    private static ImplConstructorShapeNode Ctor(string name, TypeId typeId, params ImplTypeShapeNode[] args)
        => new(name, args)
        {
            TypeId = typeId
        };

    private static ImplVariableShapeNode Var(string name)
        => new(name);

    private static ImplValueVariableShapeNode ValueVar(string name)
        => new($"value:{name}", new TypeId(BaseTypes.IntId));

    private static ImplConcreteValueShapeNode ConstInt(int value)
        => new($"int:{value}", new TypeId(BaseTypes.IntId));
}
