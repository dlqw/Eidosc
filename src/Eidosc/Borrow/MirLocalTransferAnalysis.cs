using Eidosc.Mir;

namespace Eidosc.Borrow;

internal readonly record struct LocalTransferBinding(LocalId Source, LocalId Target);

internal static class MirLocalTransferAnalysis
{
    public static bool TryGetBinding(MirLoad load, out LocalTransferBinding binding)
    {
        if (load.Source is MirPlace { Kind: PlaceKind.Local, Local: var source } &&
            load.Target is { Kind: PlaceKind.Local, Local: var target })
        {
            binding = new LocalTransferBinding(source, target);
            return true;
        }

        binding = default;
        return false;
    }

    public static bool TryGetBinding(MirMove move, out LocalTransferBinding binding)
    {
        if (move.Source is { Kind: PlaceKind.Local, Local: var source } &&
            move.Target is { Kind: PlaceKind.Local, Local: var target })
        {
            binding = new LocalTransferBinding(source, target);
            return true;
        }

        binding = default;
        return false;
    }

    public static bool TryGetBinding(MirCopy copy, out LocalTransferBinding binding)
    {
        if (copy.Source is { Kind: PlaceKind.Local, Local: var source } &&
            copy.Target is { Kind: PlaceKind.Local, Local: var target })
        {
            binding = new LocalTransferBinding(source, target);
            return true;
        }

        binding = default;
        return false;
    }
}
