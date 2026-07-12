using System.Collections.Immutable;
using Eidosc.Symbols;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class ImplTypeShapeFactoryTests
{
    [Fact]
    public void BuildFromKey_BuildsVariableForTypeParameterSymbol()
    {
        var shape = ImplTypeShapeFactory.BuildFromKey(
            new ImplTypeRefKey(
                new SymbolId(7),
                TypeId.None,
                "T",
                ImmutableArray<ImplTypeRefKey>.Empty),
            symbolId => symbolId.Value == 7 ? "T" : null);

        var variable = Assert.IsType<ImplVariableShapeNode>(shape);
        Assert.Equal("T", variable.Name);
    }

    [Fact]
    public void BuildFromKey_AttachesResolvedTypeIdToConstructor()
    {
        var key = new ImplTypeRefKey(
            new SymbolId(8),
            TypeId.None,
            "Option",
            [ImplTypeRefKey.FromCanonicalText("T")]);

        var shape = ImplTypeShapeFactory.BuildFromKey(
            key,
            typeIdResolver: _ => new TypeId(80));

        var constructor = Assert.IsType<ImplConstructorShapeNode>(shape);
        Assert.Equal(new SymbolId(8), constructor.SymbolId);
        Assert.Equal(new TypeId(80), constructor.TypeId);
        Assert.Single(constructor.Args);
    }

    [Fact]
    public void BuildFromKey_BuildsVariableForTextOnlyVariableName()
    {
        var shape = ImplTypeShapeFactory.BuildFromKey(ImplTypeRefKey.FromCanonicalText("t"));

        var variable = Assert.IsType<ImplVariableShapeNode>(shape);
        Assert.Equal("t", variable.Name);
    }

    [Fact]
    public void BuildFromKey_RejectsTextOnlyConstructorWithoutStructuredIdentity()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ImplTypeShapeFactory.BuildFromKey(ImplTypeRefKey.FromCanonicalText("Option")));

        Assert.Contains("no structured SymbolId or TypeId", exception.Message, StringComparison.Ordinal);
    }
}
