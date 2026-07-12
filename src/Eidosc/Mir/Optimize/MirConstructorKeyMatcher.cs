using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

internal sealed class MirConstructorKeyMatcher(Func<SymbolId, TypeId> resolveSymbolTypeId)
{
    public bool AreEquivalent(TypeConstructorKey left, TypeConstructorKey right)
    {
        if (left == right)
        {
            return true;
        }

        if (TryGetIdentity(left, out var leftIdentity) &&
            TryGetIdentity(right, out var rightIdentity))
        {
            return leftIdentity.IsCompatibleWith(rightIdentity);
        }

        return false;
    }

    public bool TryGetIdentity(TypeConstructorKey constructor, out MirConstructorIdentity identity)
    {
        identity = default;
        switch (constructor.Kind)
        {
            case TypeConstructorKeyKind.TypeId:
            case TypeConstructorKeyKind.Builtin:
                var typeId = new TypeId(constructor.Id);
                if (!typeId.IsValid)
                {
                    return false;
                }

                identity = new MirConstructorIdentity(SymbolId.None, typeId);
                return true;
            case TypeConstructorKeyKind.Symbol:
                var symbolId = new SymbolId(constructor.Id);
                if (!symbolId.IsValid)
                {
                    return false;
                }

                identity = new MirConstructorIdentity(symbolId, resolveSymbolTypeId(symbolId));
                return true;
            default:
                return false;
        }
    }
}

internal readonly record struct MirConstructorIdentity(SymbolId SymbolId, TypeId TypeId)
{
    public bool HasIdentity => SymbolId.IsValid || TypeId.IsValid;

    public bool IsCompatibleWith(MirConstructorIdentity other)
    {
        if (TypeId.IsValid && other.TypeId.IsValid)
        {
            return TypeId == other.TypeId;
        }

        if (SymbolId.IsValid && other.SymbolId.IsValid)
        {
            return SymbolId == other.SymbolId;
        }

        return !HasIdentity && !other.HasIdentity;
    }
}
