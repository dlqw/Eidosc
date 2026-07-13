using Eidosc.Symbols;
using Eidosc;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public class ImplSelectionTests
{
    private static readonly SourceSpan TestSpan = new(new SourceLocation(0, 0, 0), 0);
    private static readonly ImplTypeRefKey IntKey = new(SymbolId.None, new TypeId(BaseTypes.IntId), "Int", []);
    private static readonly ImplTypeRefKey StringKey = new(SymbolId.None, new TypeId(BaseTypes.StringId), "String", []);

    private static ImplTypeRefKey TypeKey(SymbolId symbolId, TypeId typeId, string text, params ImplTypeRefKey[] args) =>
        new(symbolId, typeId, text, [.. args]);

    private static ImplTypeRefKey VarKey(string name) =>
        new(SymbolId.None, TypeId.None, name, []);

    private static ImplTypeRefKey ConstIntKey(int parameterIndex, int value) =>
        ImplTypeRefKey.FromValueArgument(new ImplValueRefKey(
            parameterIndex,
            $"int:{value}",
            new TypeId(BaseTypes.IntId),
            DisplayText: value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static ImplTypeRefKey ConstIntVarKey(int parameterIndex, string name) =>
        ImplTypeRefKey.FromValueArgument(new ImplValueRefKey(
            parameterIndex,
            "",
            new TypeId(BaseTypes.IntId),
            $"param:{parameterIndex}",
            name));

    [Fact]
    public void LookupImplForTrait_OldApiWithMultipleCandidates_RemainsConservative()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var optionId = symbolTable.DeclareAdt("Option", TestSpan);
        var optionSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(optionId));

        symbolTable.DeclareImpl(
            traitId,
            optionSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Option[T]",
            canonicalImplementingType: "Option[T]");
        symbolTable.DeclareImpl(
            traitId,
            optionSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Option[Int]",
            canonicalImplementingType: "Option[Int]");

        var candidates = symbolTable.LookupImplCandidatesForTrait(optionSymbol.TypeId, traitId);
        Assert.Equal(2, candidates.Count);

        var selected = symbolTable.LookupImplForTrait(optionSymbol.TypeId, traitId);

        Assert.Null(selected);
    }

    [Fact]
    public void LookupImplForTrait_WithRequestedCanonicalImplementingType_ReturnsMostSpecificCandidate()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var optionId = symbolTable.DeclareAdt("Option", TestSpan);
        var optionSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(optionId));
        var optionTKey = TypeKey(optionId, optionSymbol.TypeId, "Option", VarKey("T"));
        var optionIntKey = TypeKey(optionId, optionSymbol.TypeId, "Option", IntKey);

        symbolTable.DeclareImpl(
            traitId,
            optionSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Option[T]",
            canonicalImplementingType: "Option[T]",
            implementingTypeKey: optionTKey);
        symbolTable.DeclareImpl(
            traitId,
            optionSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Option[Int]",
            canonicalImplementingType: "Option[Int]",
            implementingTypeKey: optionIntKey);

        var selected = symbolTable.LookupImplForTraitByKeys(
            optionSymbol.TypeId,
            traitId,
            optionIntKey,
            traitTypeArgKeys: null);

        Assert.NotNull(selected);
        Assert.Equal("Option[Int]", selected!.CanonicalImplementingType);
    }

    [Fact]
    public void LookupImplForTrait_IncomparableCandidates_ReturnsNull()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var pairId = symbolTable.DeclareAdt("Pair", TestSpan);
        var pairSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(pairId));

        symbolTable.DeclareImpl(
            traitId,
            pairSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Pair[Int, B]",
            canonicalImplementingType: "Pair[Int,B]");
        symbolTable.DeclareImpl(
            traitId,
            pairSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Pair[A, String]",
            canonicalImplementingType: "Pair[A,String]");

        var candidates = symbolTable.LookupImplCandidatesForTrait(pairSymbol.TypeId, traitId);
        Assert.Equal(2, candidates.Count);

        var selected = symbolTable.LookupImplForTrait(pairSymbol.TypeId, traitId);

        Assert.Null(selected);
    }

    [Fact]
    public void LookupImplForTraitByKeys_WithTraitArgs_ChoosesMostSpecificAcrossTraitArgBuckets()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Applicative", TestSpan);
        var resultId = symbolTable.DeclareAdt("Result", TestSpan);
        var resultSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(resultId));
        var resultGenericTraitKey = TypeKey(resultId, resultSymbol.TypeId, "Result", VarKey("T"), VarKey("E"));
        var resultStringTraitKey = TypeKey(resultId, resultSymbol.TypeId, "Result", VarKey("T"), StringKey);
        var resultGenericImplKey = TypeKey(resultId, resultSymbol.TypeId, "Result", VarKey("A"), VarKey("E"));
        var resultStringImplKey = TypeKey(resultId, resultSymbol.TypeId, "Result", VarKey("A"), StringKey);
        var resultIntStringKey = TypeKey(resultId, resultSymbol.TypeId, "Result", IntKey, StringKey);

        symbolTable.DeclareImpl(
            traitId,
            resultSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["Result[T,E]"],
            implementingTypeDisplay: "ResultWith[E,A]",
            canonicalImplementingType: "Result[A,E]",
            canonicalTraitTypeArgs: ["Result[T,E]"],
            traitTypeArgKeys: [resultGenericTraitKey],
            canonicalTraitTypeArgKeys: [resultGenericTraitKey],
            implementingTypeKey: resultGenericImplKey);
        symbolTable.DeclareImpl(
            traitId,
            resultSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["Result[T,String]"],
            implementingTypeDisplay: "ResultWith[String,A]",
            canonicalImplementingType: "Result[A,String]",
            canonicalTraitTypeArgs: ["Result[T,String]"],
            traitTypeArgKeys: [resultStringTraitKey],
            canonicalTraitTypeArgKeys: [resultStringTraitKey],
            implementingTypeKey: resultStringImplKey);

        var selected = symbolTable.LookupImplForTraitByKeys(
            resultSymbol.TypeId,
            traitId,
            resultIntStringKey,
            [resultStringTraitKey]);

        Assert.NotNull(selected);
        Assert.Equal("Result[A,String]", selected!.CanonicalImplementingType);
        Assert.Equal(["Result[T,String]"], selected.CanonicalTraitTypeArgs);
    }

    [Fact]
    public void LookupImplForTrait_WithRepeatedImplTypeVariable_RequiresConsistentRequestedShape()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var pairId = symbolTable.DeclareAdt("Pair", TestSpan);
        var pairSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(pairId));
        var pairRepeatedKey = TypeKey(pairId, pairSymbol.TypeId, "Pair", VarKey("T"), VarKey("T"));
        var pairIntIntKey = TypeKey(pairId, pairSymbol.TypeId, "Pair", IntKey, IntKey);
        var pairIntStringKey = TypeKey(pairId, pairSymbol.TypeId, "Pair", IntKey, StringKey);

        var implId = symbolTable.DeclareImpl(
            traitId,
            pairSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Pair[T, T]",
            canonicalImplementingType: "Pair[T,T]",
            implementingTypeKey: pairRepeatedKey);

        var sameTypes = symbolTable.LookupImplForTraitByKeys(
            pairSymbol.TypeId,
            traitId,
            pairIntIntKey,
            traitTypeArgKeys: null);
        var differentTypes = symbolTable.LookupImplForTraitByKeys(
            pairSymbol.TypeId,
            traitId,
            pairIntStringKey,
            traitTypeArgKeys: null);

        Assert.NotNull(sameTypes);
        Assert.Equal(implId, sameTypes!.Id);
        Assert.Null(differentTypes);
    }

    [Fact]
    public void LookupImplForTrait_WithRepeatedVariableAndGeneralCandidate_ChoosesRepeatedVariable()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var pairId = symbolTable.DeclareAdt("Pair", TestSpan);
        var pairSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(pairId));
        var pairGeneralKey = TypeKey(pairId, pairSymbol.TypeId, "Pair", VarKey("T"), VarKey("U"));
        var pairRepeatedKey = TypeKey(pairId, pairSymbol.TypeId, "Pair", VarKey("T"), VarKey("T"));
        var pairIntIntKey = TypeKey(pairId, pairSymbol.TypeId, "Pair", IntKey, IntKey);

        var generalImpl = symbolTable.DeclareImpl(
            traitId,
            pairSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Pair[T, U]",
            canonicalImplementingType: "Pair[T,U]",
            implementingTypeKey: pairGeneralKey);
        var repeatedImpl = symbolTable.DeclareImpl(
            traitId,
            pairSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Pair[T, T]",
            canonicalImplementingType: "Pair[T,T]",
            implementingTypeKey: pairRepeatedKey);

        var selected = symbolTable.LookupImplForTraitByKeys(
            pairSymbol.TypeId,
            traitId,
            pairIntIntKey,
            traitTypeArgKeys: null);

        Assert.NotEqual(generalImpl, repeatedImpl);
        Assert.NotNull(selected);
        Assert.Equal(repeatedImpl, selected!.Id);
    }

    [Fact]
    public void LookupImplForTraitByKeys_GenericTraitArgCandidate_AllowsConcreteRequestedKey()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Functor", TestSpan);
        var containerId = symbolTable.DeclareAdt("Container", TestSpan);
        var containerSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(containerId));
        var typeParamId = new SymbolId(10001);
        symbolTable.RegisterSymbol(new TypeParamSymbol
        {
            Id = typeParamId,
            Name = "T",
            Span = TestSpan
        });

        var implId = symbolTable.DeclareImpl(
            traitId,
            containerSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["T"],
            implementingTypeDisplay: "Container",
            canonicalImplementingType: "Container",
            canonicalTraitTypeArgs: ["T"],
            traitTypeArgKeys:
            [
                new ImplTypeRefKey(typeParamId, TypeId.None, "T", [])
            ],
            implementingTypeKey: new ImplTypeRefKey(containerId, containerSymbol.TypeId, "Container", []));

        var selected = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            new ImplTypeRefKey(containerId, containerSymbol.TypeId, "Container", []),
            [new ImplTypeRefKey(SymbolId.None, new TypeId(BaseTypes.IntId), "Int", [])]);

        Assert.NotNull(selected);
        Assert.Equal(implId, selected!.Id);
    }

    [Fact]
    public void DeclareImpl_WithSameTraitArgTextButDifferentTypeIdentity_KeepsDistinctImpls()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var containerId = symbolTable.DeclareAdt("Container", TestSpan);
        var firstBoxId = symbolTable.DeclareAdt("Box", TestSpan);
        var secondBoxId = symbolTable.DeclareAdt("Box", TestSpan);
        var containerSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(containerId));
        var firstBoxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(firstBoxId));
        var secondBoxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(secondBoxId));

        var firstImpl = symbolTable.DeclareImpl(
            traitId,
            containerSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["Box"],
            implementingTypeDisplay: "Container",
            canonicalImplementingType: "Container",
            canonicalTraitTypeArgs: ["Box"],
            traitTypeArgKeys:
            [
                new ImplTypeRefKey(firstBoxId, firstBoxSymbol.TypeId, "Box", [])
            ]);
        var secondImpl = symbolTable.DeclareImpl(
            traitId,
            containerSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["Box"],
            implementingTypeDisplay: "Container",
            canonicalImplementingType: "Container",
            canonicalTraitTypeArgs: ["Box"],
            traitTypeArgKeys:
            [
                new ImplTypeRefKey(secondBoxId, secondBoxSymbol.TypeId, "Box", [])
            ]);

        var impls = symbolTable.LookupImpls(containerSymbol.TypeId)
            .Where(impl => impl.Trait == traitId)
            .ToList();

        Assert.NotEqual(firstImpl, secondImpl);
        Assert.Equal(2, impls.Count);
        Assert.Contains(impls, impl => impl.TraitTypeArgKeys.Single().SymbolId == firstBoxId);
        Assert.Contains(impls, impl => impl.TraitTypeArgKeys.Single().SymbolId == secondBoxId);

        var firstSelected = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            [new ImplTypeRefKey(firstBoxId, firstBoxSymbol.TypeId, "Box", [])]);
        var secondSelected = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            [new ImplTypeRefKey(secondBoxId, secondBoxSymbol.TypeId, "Box", [])]);

        Assert.NotNull(firstSelected);
        Assert.NotNull(secondSelected);
        Assert.Equal(firstImpl, firstSelected!.Id);
        Assert.Equal(secondImpl, secondSelected!.Id);
    }

    [Fact]
    public void LookupImplForTraitByKeys_UsesCanonicalTraitArgShapeWhenRawAliasKeyDiffers()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Functor", TestSpan);
        var containerId = symbolTable.DeclareAdt("Container", TestSpan);
        var targetId = symbolTable.DeclareAdt("Target", TestSpan);
        var aliasId = symbolTable.DeclareAdt("Alias", TestSpan);
        var containerSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(containerId));
        var targetSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(targetId));
        var aliasSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(aliasId));
        var containerKey = new ImplTypeRefKey(containerId, containerSymbol.TypeId, "Container", []);
        var targetKey = new ImplTypeRefKey(targetId, targetSymbol.TypeId, "Target", []);
        var aliasKey = new ImplTypeRefKey(aliasId, aliasSymbol.TypeId, "Alias", []);

        var implId = symbolTable.DeclareImpl(
            traitId,
            containerSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["Alias"],
            implementingTypeDisplay: "Container",
            canonicalImplementingType: "Container",
            canonicalTraitTypeArgs: ["Target"],
            traitTypeArgKeys: [aliasKey],
            implHeadShape: new ImplHeadShape(
                traitId,
                [new ImplConstructorShapeNode("Target", []) { SymbolId = targetId, TypeId = targetSymbol.TypeId }],
                new ImplConstructorShapeNode("Container", []) { SymbolId = containerId, TypeId = containerSymbol.TypeId }),
            implementingTypeKey: containerKey);

        var selected = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            containerKey,
            [targetKey]);

        Assert.NotNull(selected);
        Assert.Equal(implId, selected!.Id);
    }

    [Fact]
    public void DeclareImpl_WithExplicitCanonicalTraitArgKey_UsesCanonicalShapeWithoutImplHeadShape()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Functor", TestSpan);
        var containerId = symbolTable.DeclareAdt("Container", TestSpan);
        var targetId = symbolTable.DeclareAdt("Target", TestSpan);
        var aliasId = symbolTable.DeclareAdt("Alias", TestSpan);
        var containerSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(containerId));
        var targetSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(targetId));
        var aliasSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(aliasId));
        var containerKey = new ImplTypeRefKey(containerId, containerSymbol.TypeId, "Container", []);
        var targetKey = new ImplTypeRefKey(targetId, targetSymbol.TypeId, "Target", []);
        var aliasKey = new ImplTypeRefKey(aliasId, aliasSymbol.TypeId, "Alias", []);

        var implId = symbolTable.DeclareImpl(
            traitId,
            containerSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["Alias"],
            implementingTypeDisplay: "Container",
            canonicalImplementingType: "Container",
            canonicalTraitTypeArgs: ["Target"],
            traitTypeArgKeys: [aliasKey],
            canonicalTraitTypeArgKeys: [targetKey],
            implementingTypeKey: containerKey);

        var selectedByCanonicalTarget = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            containerKey,
            [targetKey]);
        var selectedByRawAlias = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            containerKey,
            [aliasKey]);

        Assert.NotNull(selectedByCanonicalTarget);
        Assert.Equal(implId, selectedByCanonicalTarget!.Id);
        Assert.Null(selectedByRawAlias);
    }

    [Fact]
    public void LookupImplForTraitByKeys_WhenTraitArgShapeIsMissing_UsesCanonicalStructuredTraitArgKey()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Functor", TestSpan);
        var containerId = symbolTable.DeclareAdt("Container", TestSpan);
        var targetId = symbolTable.DeclareAdt("Target", TestSpan);
        var aliasId = symbolTable.DeclareAdt("Alias", TestSpan);
        var containerSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(containerId));
        var targetSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(targetId));
        var aliasSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(aliasId));
        var containerKey = new ImplTypeRefKey(containerId, containerSymbol.TypeId, "Container", []);
        var targetKey = new ImplTypeRefKey(targetId, targetSymbol.TypeId, "Target", []);
        var aliasKey = new ImplTypeRefKey(aliasId, aliasSymbol.TypeId, "Alias", []);

        var implId = symbolTable.DeclareImpl(
            traitId,
            containerSymbol.TypeId,
            TestSpan,
            traitTypeArgs: ["Alias"],
            implementingTypeDisplay: "Container",
            canonicalImplementingType: "Container",
            canonicalTraitTypeArgs: ["Target"],
            traitTypeArgKeys: [aliasKey],
            canonicalTraitTypeArgKeys: [targetKey],
            implementingTypeKey: containerKey);
        var registeredImpl = Assert.Single(symbolTable.LookupImpls(containerSymbol.TypeId), impl => impl.Id == implId);
        registeredImpl.TraitTypeArgShapes.Clear();

        var selectedByCanonicalTarget = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            containerKey,
            [targetKey]);
        var selectedByRawAlias = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            containerKey,
            [aliasKey]);

        Assert.NotNull(selectedByCanonicalTarget);
        Assert.Equal(implId, selectedByCanonicalTarget!.Id);
        Assert.Null(selectedByRawAlias);
    }

    [Fact]
    public void LookupImplForTraitByKeys_WithoutRegisteredShape_UsesImplementingTypeKey()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var boxId = symbolTable.DeclareAdt("Box", TestSpan);
        var boxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxId));
        var intKey = new ImplTypeRefKey(SymbolId.None, new TypeId(BaseTypes.IntId), "Int", []);
        var stringKey = new ImplTypeRefKey(SymbolId.None, new TypeId(BaseTypes.StringId), "String", []);
        var boxIntKey = new ImplTypeRefKey(boxId, boxSymbol.TypeId, "Box", [intKey]);
        var boxStringKey = new ImplTypeRefKey(boxId, boxSymbol.TypeId, "Box", [stringKey]);

        var boxIntImpl = symbolTable.DeclareImpl(
            traitId,
            boxSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Box[Int]",
            canonicalImplementingType: "Box",
            implementingTypeKey: boxIntKey);
        var boxStringImpl = symbolTable.DeclareImpl(
            traitId,
            boxSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Box[String]",
            canonicalImplementingType: "Box",
            implementingTypeKey: boxStringKey);

        var selectedBoxInt = symbolTable.LookupImplForTraitByKeys(
            boxSymbol.TypeId,
            traitId,
            boxIntKey,
            traitTypeArgKeys: null);
        var selectedBoxString = symbolTable.LookupImplForTraitByKeys(
            boxSymbol.TypeId,
            traitId,
            boxStringKey,
            traitTypeArgKeys: null);

        Assert.NotEqual(boxIntImpl, boxStringImpl);
        Assert.NotNull(selectedBoxInt);
        Assert.NotNull(selectedBoxString);
        Assert.Equal(boxIntImpl, selectedBoxInt!.Id);
        Assert.Equal(boxStringImpl, selectedBoxString!.Id);
    }

    [Fact]
    public void LookupImplForTraitByKeys_StructuredImplementingKey_IgnoresMisleadingText()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var containerId = symbolTable.DeclareAdt("Container", TestSpan);
        var containerSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(containerId));
        var staleSymbolId = new SymbolId(980001);
        var misleadingText = containerSymbol.TypeId.ToString();

        symbolTable.DeclareImpl(
            traitId,
            containerSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Container",
            canonicalImplementingType: "Container",
            implementingTypeKey: new ImplTypeRefKey(staleSymbolId, TypeId.None, misleadingText, []));

        var selected = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            new ImplTypeRefKey(SymbolId.None, containerSymbol.TypeId, misleadingText, []),
            traitTypeArgKeys: null);

        Assert.Null(selected);
    }

    [Fact]
    public void LookupImplForTraitByKeys_LegacyCandidateDoesNotMatchStructuredRequestByText()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var containerId = symbolTable.DeclareAdt("Container", TestSpan);
        var containerSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(containerId));

        symbolTable.DeclareImpl(
            traitId,
            containerSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Container",
            canonicalImplementingType: "Container");

        var selected = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            new ImplTypeRefKey(SymbolId.None, new TypeId(980010), "Container", []),
            traitTypeArgKeys: null);

        Assert.Null(selected);
    }

    [Fact]
    public void LookupImplForTraitByKeys_LegacyImplementingShapeDoesNotMatchStructuredRequestByText()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var containerId = symbolTable.DeclareAdt("Container", TestSpan);
        var containerSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(containerId));

        symbolTable.DeclareImpl(
            traitId,
            containerSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "LegacyContainer[Int]",
            canonicalImplementingType: "LegacyContainer[Int]",
            implHeadShape: new ImplHeadShape(
                traitId,
                [],
                new ImplConstructorShapeNode(
                    "LegacyContainer",
                    [new ImplConstructorShapeNode("Int", [])])));

        var selected = symbolTable.LookupImplForTraitByKeys(
            containerSymbol.TypeId,
            traitId,
            new ImplTypeRefKey(
                SymbolId.None,
                containerSymbol.TypeId,
                "LegacyContainer",
                [new ImplTypeRefKey(SymbolId.None, new TypeId(BaseTypes.IntId), "Int", [])]),
            traitTypeArgKeys: null);

        Assert.Null(selected);
    }

    [Fact]
    public void LookupImplForTraitByKeys_LegacyTraitArgCandidateDoesNotMatchStructuredRequestByText()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Mapper", TestSpan);
        var containerId = symbolTable.DeclareAdt("Container", TestSpan);
        var containerSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(containerId));

        Assert.Throws<InvalidOperationException>(() =>
            symbolTable.DeclareImpl(
                traitId,
                containerSymbol.TypeId,
                TestSpan,
                traitTypeArgs: ["Box"],
                canonicalTraitTypeArgs: ["Box"],
                implementingTypeDisplay: "Container",
                canonicalImplementingType: "Container"));
    }

    [Fact]
    public void LookupImplForTraitByKeys_DefaultTraitArgKey_IsIgnoredAsEmpty()
    {
        var defaultKey = default(ImplTypeRefKey);
        Assert.True(defaultKey.IsEmpty);
        Assert.Equal(ImplTypeRefKey.Empty, defaultKey);

        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var boxId = symbolTable.DeclareAdt("Box", TestSpan);
        var boxSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(boxId));

        var implId = symbolTable.DeclareImpl(
            traitId,
            boxSymbol.TypeId,
            TestSpan,
            implementingTypeDisplay: "Box",
            canonicalImplementingType: "Box",
            traitTypeArgKeys: [defaultKey]);

        var selectedByNoArgs = symbolTable.LookupImplForTrait(boxSymbol.TypeId, traitId);
        var selectedByDefaultKey = symbolTable.LookupImplForTraitByKeys(
            boxSymbol.TypeId,
            traitId,
            [defaultKey]);

        Assert.NotNull(selectedByNoArgs);
        Assert.NotNull(selectedByDefaultKey);
        Assert.Equal(implId, selectedByNoArgs!.Id);
        Assert.Equal(implId, selectedByDefaultKey!.Id);
    }

    [Fact]
    public void LookupImplForTraitByKeys_ValueArguments_SelectConcreteBeforeSymbolicImpl()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var bufferId = symbolTable.DeclareAdt("Buffer", TestSpan);
        var buffer = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(bufferId));
        var genericKey = TypeKey(bufferId, buffer.TypeId, "Buffer", ConstIntVarKey(0, "N"), IntKey);
        var fourKey = TypeKey(bufferId, buffer.TypeId, "Buffer", ConstIntKey(0, 4), IntKey);
        var fiveKey = TypeKey(bufferId, buffer.TypeId, "Buffer", ConstIntKey(0, 5), IntKey);

        var genericImpl = symbolTable.DeclareImpl(
            traitId,
            buffer.TypeId,
            TestSpan,
            implementingTypeDisplay: "Buffer[N, Int]",
            canonicalImplementingType: "Buffer",
            implementingTypeKey: genericKey);
        var concreteImpl = symbolTable.DeclareImpl(
            traitId,
            buffer.TypeId,
            TestSpan,
            implementingTypeDisplay: "Buffer[4, Int]",
            canonicalImplementingType: "Buffer",
            implementingTypeKey: fourKey);

        var selectedFour = symbolTable.LookupImplForTraitByKeys(
            buffer.TypeId,
            traitId,
            fourKey,
            traitTypeArgKeys: null);
        var selectedFive = symbolTable.LookupImplForTraitByKeys(
            buffer.TypeId,
            traitId,
            fiveKey,
            traitTypeArgKeys: null);

        Assert.Equal(concreteImpl, selectedFour?.Id);
        Assert.Equal(genericImpl, selectedFive?.Id);
    }

    [Fact]
    public void DeclareImpl_DifferentConcreteValueArguments_KeepDistinctImplementations()
    {
        var symbolTable = new SymbolTable();
        var traitId = symbolTable.DeclareTrait("Show", TestSpan);
        var bufferId = symbolTable.DeclareAdt("Buffer", TestSpan);
        var buffer = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(bufferId));
        var fourKey = TypeKey(bufferId, buffer.TypeId, "Buffer", ConstIntKey(0, 4), IntKey);
        var fiveKey = TypeKey(bufferId, buffer.TypeId, "Buffer", ConstIntKey(0, 5), IntKey);

        var fourImpl = symbolTable.DeclareImpl(
            traitId,
            buffer.TypeId,
            TestSpan,
            implementingTypeDisplay: "Buffer[4, Int]",
            canonicalImplementingType: "Buffer",
            implementingTypeKey: fourKey);
        var fiveImpl = symbolTable.DeclareImpl(
            traitId,
            buffer.TypeId,
            TestSpan,
            implementingTypeDisplay: "Buffer[5, Int]",
            canonicalImplementingType: "Buffer",
            implementingTypeKey: fiveKey);

        Assert.NotEqual(fourImpl, fiveImpl);
        Assert.Equal(
            fourImpl,
            symbolTable.LookupImplForTraitByKeys(buffer.TypeId, traitId, fourKey, null)?.Id);
        Assert.Equal(
            fiveImpl,
            symbolTable.LookupImplForTraitByKeys(buffer.TypeId, traitId, fiveKey, null)?.Id);
    }
}
