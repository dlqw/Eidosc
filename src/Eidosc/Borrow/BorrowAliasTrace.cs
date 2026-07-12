using Eidosc.Diagnostic;
using Eidosc.Mir;

namespace Eidosc.Borrow;

internal static class BorrowAliasTrace
{
    public readonly record struct BorrowStateKey(
        LocalId Borrower,
        LocalId Borrowee,
        BorrowTarget BorrowTarget,
        bool IsMutable,
        (BlockId Block, int Index) Location,
        (BlockId Block, int Index) OriginLocation,
        string OriginSummary,
        string TraceId);

    public static string? BuildConflictHint(
        IReadOnlyList<string> aliasTrace,
        string? traceId,
        string? fallback)
    {
        if (aliasTrace.Count == 0)
        {
            return fallback;
        }

        var normalizedTraceId = string.IsNullOrEmpty(traceId)
            ? "unknown"
            : traceId;
        return DiagnosticMessages.MatchedAliasTraceHint(
            normalizedTraceId,
            string.Join(" => ", aliasTrace));
    }

    public static string BuildTraceId(
        (BlockId Block, int Index) originLocation,
        IReadOnlyList<string> aliasTrace,
        LocalId borrower,
        LocalId borrowee,
        bool isMutable)
    {
        return BuildTraceId(
            originLocation,
            aliasTrace,
            borrower,
            borrowee,
            BorrowTarget.ForLocal(borrowee),
            isMutable);
    }

    public static string BuildTraceId(
        (BlockId Block, int Index) originLocation,
        IReadOnlyList<string> aliasTrace,
        LocalId borrower,
        LocalId borrowee,
        BorrowTarget borrowTarget,
        bool isMutable)
    {
        unchecked
        {
            uint hash = AdtConstructorTypeId.FnvOffset;

            hash = UpdateHash(hash, originLocation.Block.Value.ToString());
            hash = UpdateHash(hash, originLocation.Index.ToString());
            hash = UpdateHash(hash, borrower.Value.ToString());
            hash = UpdateHash(hash, borrowee.Value.ToString());
            hash = UpdateHash(hash, borrowTarget.PathKey ?? string.Empty);
            hash = UpdateHash(hash, isMutable ? WellKnownStrings.Keywords.Mut : "shared");

            foreach (var trace in aliasTrace)
            {
                hash = UpdateHash(hash, trace);
            }

            return $"T{hash:X8}";
        }
    }

    public static BorrowStateKey BuildBorrowStateKey(
        LocalId borrower,
        LocalId borrowee,
        bool isMutable,
        (BlockId Block, int Index) location,
        (BlockId Block, int Index) originLocation,
        string originSummary,
        string traceId,
        IReadOnlyList<string> aliasTrace)
    {
        return BuildBorrowStateKey(
            borrower,
            borrowee,
            BorrowTarget.ForLocal(borrowee),
            isMutable,
            location,
            originLocation,
            originSummary,
            traceId,
            aliasTrace);
    }

    public static BorrowStateKey BuildBorrowStateKey(
        LocalId borrower,
        LocalId borrowee,
        BorrowTarget borrowTarget,
        bool isMutable,
        (BlockId Block, int Index) location,
        (BlockId Block, int Index) originLocation,
        string originSummary,
        string traceId,
        IReadOnlyList<string> aliasTrace)
    {
        return new BorrowStateKey(
            borrower,
            borrowee,
            borrowTarget,
            isMutable,
            location,
            originLocation,
            originSummary,
            traceId);
    }

    public static string BuildDiagnosticDedupKey(BorrowDiagnostic diagnostic)
    {
        return $"{diagnostic.Kind}:{diagnostic.Location.Block.Value}:{diagnostic.Location.Index}:{diagnostic.Message}";
    }

    private static uint UpdateHash(uint seed, string text)
    {
        var hash = seed;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= AdtConstructorTypeId.FnvPrime;
        }

        return hash;
    }
}
