using System.Collections.Immutable;
using Eidosc.Symbols;
using Eidosc.Types;

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

    [Fact]
    public void BuildFromKey_BuildsConcreteAndSymbolicValueShapes()
    {
        var concrete = ImplTypeShapeFactory.BuildFromKey(
            ImplTypeRefKey.FromValueArgument(new ImplValueRefKey(
                0,
                "int:4",
                new TypeId(BaseTypes.IntId),
                DisplayText: "4")));
        var symbolic = ImplTypeShapeFactory.BuildFromKey(
            ImplTypeRefKey.FromValueArgument(new ImplValueRefKey(
                0,
                "",
                new TypeId(BaseTypes.IntId),
                "param:0",
                "N")));

        var concreteValue = Assert.IsType<ImplConcreteValueShapeNode>(concrete);
        Assert.Equal("int:4", concreteValue.CanonicalPayload);
        var variable = Assert.IsType<ImplValueVariableShapeNode>(symbolic);
        Assert.Equal("value:N", variable.Name);
    }
}
