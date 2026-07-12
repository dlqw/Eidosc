using System.Collections.Immutable;
using Eidosc.Symbols;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class ImplSymbolExtensionsTests
{
    [Fact]
    public void GetMatchingTraitTypeArgKeys_PrefersStructuredCanonicalKeysWhenTheyDiffer()
    {
        var declaredKey = ImplTypeRefKey.FromCanonicalText("Alias[T]");
        var canonicalKey = new ImplTypeRefKey(
            SymbolId.None,
            new TypeId(42),
            "Canonical",
            ImmutableArray<ImplTypeRefKey>.Empty);
        var impl = new ImplSymbol
        {
            Name = "impl",
            TraitTypeArgKeys = [declaredKey],
            CanonicalTraitTypeArgKeys = [canonicalKey]
        };

        var keys = impl.GetMatchingTraitTypeArgKeys();

        Assert.Same(impl.CanonicalTraitTypeArgKeys, keys);
    }

    [Fact]
    public void GetMatchingTraitTypeArgKeys_UsesDeclaredKeysWhenCanonicalKeysAreTextOnly()
    {
        var declaredKey = new ImplTypeRefKey(
            new SymbolId(7),
            TypeId.None,
            "Declared",
            ImmutableArray<ImplTypeRefKey>.Empty);
        var impl = new ImplSymbol
        {
            Name = "impl",
            TraitTypeArgKeys = [declaredKey],
            CanonicalTraitTypeArgKeys = [ImplTypeRefKey.FromCanonicalText("Canonical")]
        };

        var keys = impl.GetMatchingTraitTypeArgKeys();

        Assert.Same(impl.TraitTypeArgKeys, keys);
    }

    [Fact]
    public void HasStructuredIdentity_RequiresSymbolOrTypeIdentity()
    {
        Assert.False(ImplTypeRefKey.FromCanonicalText("TextOnly").HasStructuredIdentity());
        Assert.True(new ImplTypeRefKey(
            new SymbolId(1),
            TypeId.None,
            "Text",
            ImmutableArray<ImplTypeRefKey>.Empty).HasStructuredIdentity());
        Assert.True(new ImplTypeRefKey(
            SymbolId.None,
            new TypeId(2),
            "Text",
            ImmutableArray<ImplTypeRefKey>.Empty).HasStructuredIdentity());
    }
}
