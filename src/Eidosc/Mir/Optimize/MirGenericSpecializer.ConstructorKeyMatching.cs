using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private bool AreConstructorKeysEquivalent(TypeConstructorKey left, TypeConstructorKey right)
    {
        return CreateConstructorKeyMatcher().AreEquivalent(left, right);
    }

    private bool TryGetConstructorIdentity(TypeConstructorKey constructor, out MirConstructorIdentity identity)
    {
        return CreateConstructorKeyMatcher().TryGetIdentity(constructor, out identity);
    }

    private MirConstructorKeyMatcher CreateConstructorKeyMatcher()
    {
        return new MirConstructorKeyMatcher(symbolId =>
            TryResolveSymbolTypeConstructorId(symbolId, out var typeId)
                ? typeId
                : TypeId.None);
    }
}
