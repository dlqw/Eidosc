using System.Text;
using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.Pipeline;

public sealed record MetaQueryStatePayload(
    string SchemaVersion,
    IReadOnlyList<MetaQueryCacheEntryPayload> CacheEntries,
    IReadOnlyList<MetaQueryDependencyPayload> Dependencies,
    string Hash)
{
    public const string CurrentSchemaVersion = "meta-query-state-payload-v2";

    internal static MetaQueryStatePayload Create(SymbolTable? symbolTable)
    {
        if (symbolTable == null)
        {
            return CreateEmpty();
        }

        var state = MetaQueryState.For(symbolTable);
        var cacheEntries = new List<MetaQueryCacheEntryPayload>();
        foreach (var entry in state.SnapshotCacheEntries())
        {
            if (!ComptimeValuePayload.TryCreate(entry.Value, out var result))
            {
                continue;
            }

            cacheEntries.Add(new MetaQueryCacheEntryPayload(
                entry.Key,
                entry.Value.CanonicalHash,
                Encoding.UTF8.GetByteCount(entry.Value.CanonicalText),
                result));
        }

        var dependencies = state.SnapshotDependencies()
            .OrderBy(static dependency => dependency.Sequence)
            .Select(static dependency => new MetaQueryDependencyPayload(
                dependency.Sequence,
                dependency.Key,
                dependency.ResultHash,
                dependency.CacheHit,
                dependency.ResultBytes))
            .ToArray();
        var payload = new MetaQueryStatePayload(
            CurrentSchemaVersion,
            cacheEntries.OrderBy(static entry => entry.Key, StringComparer.Ordinal).ToArray(),
            dependencies,
            "");
        return payload with { Hash = ComputeHash(payload) };
    }

    internal bool TryRestoreCache(
        LiveStateIdRemapper? remapper,
        out IReadOnlyList<MetaQueryCacheEntry> entries,
        out string failure) =>
        TryRestoreState(remapper, out entries, out _, out failure);

    internal bool TryRestoreState(
        LiveStateIdRemapper? remapper,
        out IReadOnlyList<MetaQueryCacheEntry> entries,
        out IReadOnlyList<MetaQueryDependency> dependencies,
        out string failure)
    {
        entries = [];
        dependencies = [];
        if (SchemaVersion != CurrentSchemaVersion || !HasValidHash())
        {
            failure = "invalid meta query state payload";
            return false;
        }

        var restored = new List<MetaQueryCacheEntry>(CacheEntries.Count);
        var cacheByKey = new Dictionary<string, (string ResultHash, int ResultBytes)>(StringComparer.Ordinal);
        foreach (var entry in CacheEntries.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(entry.Key) ||
                !entry.Result.TryRestoreValue(remapper, out var result))
            {
                failure = $"invalid meta query cache entry '{entry.Key}'";
                return false;
            }

            var resultBytes = Encoding.UTF8.GetByteCount(result.CanonicalText);
            if (!string.Equals(entry.ResultHash, result.CanonicalHash, StringComparison.Ordinal) ||
                entry.ResultBytes != resultBytes)
            {
                failure = $"stale meta query cache entry '{entry.Key}'";
                return false;
            }

            if (!cacheByKey.TryAdd(entry.Key, (entry.ResultHash, entry.ResultBytes)))
            {
                failure = $"duplicate meta query cache entry '{entry.Key}'";
                return false;
            }

            restored.Add(new MetaQueryCacheEntry(entry.Key, result));
        }

        long previousSequence = 0;
        foreach (var dependency in Dependencies)
        {
            if (dependency.Sequence <= previousSequence ||
                !cacheByKey.TryGetValue(dependency.Key, out var cachedResult) ||
                !string.Equals(dependency.ResultHash, cachedResult.ResultHash, StringComparison.Ordinal) ||
                dependency.ResultBytes != cachedResult.ResultBytes)
            {
                failure = $"invalid meta query dependency at sequence {dependency.Sequence}";
                return false;
            }

            previousSequence = dependency.Sequence;
        }

        entries = restored;
        dependencies = Dependencies
            .Select(static dependency => new MetaQueryDependency(
                dependency.Sequence,
                dependency.Key,
                dependency.ResultHash,
                dependency.CacheHit,
                dependency.ResultBytes))
            .ToArray();
        failure = string.Empty;
        return true;
    }

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ComputeHash(this), StringComparison.Ordinal);

    private static MetaQueryStatePayload CreateEmpty()
    {
        var payload = new MetaQueryStatePayload(CurrentSchemaVersion, [], [], "");
        return payload with { Hash = ComputeHash(payload) };
    }

    private static string ComputeHash(MetaQueryStatePayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" });
}

public sealed record MetaQueryCacheEntryPayload(
    string Key,
    string ResultHash,
    int ResultBytes,
    ComptimeValuePayload Result);

public sealed record MetaQueryDependencyPayload(
    long Sequence,
    string Key,
    string ResultHash,
    bool CacheHit,
    int ResultBytes);
