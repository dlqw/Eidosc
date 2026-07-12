using Eidosc.Symbols;
using Eidosc.Mir;
using Eidosc.Semantic;

namespace Eidosc.Borrow;

internal static class BorrowDiagnosticFormatter
{
    public static string FormatBorrowTarget(BorrowTarget target)
    {
        if (!target.IsValid)
        {
            return "%?/root";
        }

        return $"{FormatLocal(target.BaseLocal)}/{target.PathKey}";
    }

    public static string FormatBorrowTarget(BorrowTarget target, IReadOnlyDictionary<LocalId, MirLocal> localsById)
    {
        if (!target.IsValid)
        {
            return "%?/root";
        }

        return $"{FormatLocal(target.BaseLocal, localsById)}/{target.PathKey}";
    }

    public static string FormatLocal(LocalId localId)
    {
        return localId.IsValid ? $"%{localId.Value}" : "%?";
    }

    public static string FormatLocal(LocalId localId, IReadOnlyDictionary<LocalId, MirLocal> localsById)
    {
        if (!localId.IsValid)
        {
            return "%?";
        }

        return localsById.TryGetValue(localId, out var local) &&
               !string.IsNullOrWhiteSpace(local.Name)
            ? $"%{localId.Value}({local.Name})"
            : $"%{localId.Value}";
    }

    public static string BuildLoadTrace(LocalId source, LocalId target, BlockId blockId, int index)
    {
        return $"load %{source.Value} -> %{target.Value} @ bb{blockId.Value}:{index}";
    }

    public static string BuildMoveTrace(LocalId source, LocalId target, BlockId blockId, int index)
    {
        return $"move %{source.Value} -> %{target.Value} @ bb{blockId.Value}:{index}";
    }

    public static string BuildCopyTrace(LocalId source, LocalId target, BlockId blockId, int index)
    {
        return $"copy %{source.Value} -> %{target.Value} @ bb{blockId.Value}:{index}";
    }

    public static string BuildCallTrace(
        MirCall call,
        int argumentIndex,
        LocalId target,
        LocalId borrowee,
        BlockId blockId,
        int index)
    {
        return $"call {call.Function} arg[{argumentIndex}] -> %{target.Value} aliases %{borrowee.Value} @ bb{blockId.Value}:{index}";
    }
}
