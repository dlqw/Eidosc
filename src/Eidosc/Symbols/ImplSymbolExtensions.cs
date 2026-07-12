namespace Eidosc.Symbols;

public static class ImplSymbolExtensions
{
    public static bool HasStructuredIdentity(this ImplTypeRefKey key)
    {
        return key.SymbolId.IsValid || key.TypeId.IsValid;
    }

    public static IReadOnlyList<ImplTypeRefKey> GetMatchingTraitTypeArgKeys(this ImplSymbol impl)
    {
        if (impl.CanonicalTraitTypeArgKeys.Any(static key => key.HasStructuredIdentity()) &&
            (impl.TraitTypeArgKeys.Count == 0 ||
             !impl.CanonicalTraitTypeArgKeys.SequenceEqual(impl.TraitTypeArgKeys)))
        {
            return impl.CanonicalTraitTypeArgKeys;
        }

        return impl.TraitTypeArgKeys.Count > 0
            ? impl.TraitTypeArgKeys
            : impl.CanonicalTraitTypeArgKeys;
    }
}
