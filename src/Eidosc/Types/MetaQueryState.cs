using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Eidosc.Ast.Declarations;
using Eidosc.Symbols;

namespace Eidosc.Types;

[Flags]
internal enum MetaQueryCapability
{
    None = 0,
    CurrentPackagePrivateShapes = 1 << 0,
    CurrentPackageBodies = 1 << 1,
    DependencyBodies = 1 << 2,
    DependencyPrivateShapes = 1 << 3,
    Workspace = 1 << 4,
    Layout = 1 << 5
}

internal sealed record MetaQueryAccessContext(
    SymbolId CurrentModuleId,
    ClauseStage AvailableStage,
    MetaQueryCapability Capabilities,
    string TargetIdentity = "",
    string TargetTriple = "",
    SymbolId? TargetSymbolId = null,
    string RequesterIdentity = "")
{
    public static MetaQueryAccessContext Default { get; } = new(
        SymbolId.None,
        ClauseStage.Body,
        MetaQueryCapability.CurrentPackagePrivateShapes | MetaQueryCapability.CurrentPackageBodies);

    public string Fingerprint => Hash(string.Join(
        "|",
        RequesterIdentity,
        AvailableStage,
        (int)Capabilities,
        TargetIdentity,
        TargetTriple,
        WellKnownStrings.Meta.SchemaVersion));

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed record MetaQueryDependency(
    long Sequence,
    string Key,
    string ResultHash,
    bool CacheHit,
    int ResultBytes);

internal sealed record MetaQueryCacheEntry(
    string Key,
    ComptimeValue Value);

internal sealed class MetaQueryState
{
    private static readonly ConditionalWeakTable<SymbolTable, MetaQueryState> States = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, ComptimeValue> _cache = new(StringComparer.Ordinal);
    private readonly List<MetaQueryDependency> _dependencies = [];
    private long _nextDependencySequence;

    public static MetaQueryState For(SymbolTable symbolTable) => States.GetValue(symbolTable, static _ => new());

    public long CreateDependencyCursor()
    {
        lock (_gate)
        {
            return _nextDependencySequence;
        }
    }

    public bool HasDependenciesAfter(long cursor)
    {
        lock (_gate)
        {
            return _dependencies.Any(dependency => dependency.Sequence > cursor);
        }
    }

    public string CreateDependencyFingerprintAfter(long cursor)
    {
        lock (_gate)
        {
            var canonical = string.Join(
                "|",
                _dependencies
                    .Where(dependency => dependency.Sequence > cursor)
                    .OrderBy(static dependency => dependency.Sequence)
                    .Select(static dependency => $"{dependency.Key}:{dependency.ResultHash}:{dependency.ResultBytes}"));
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
    }

    public bool TryGet(string key, out ComptimeValue value)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(key, out value!);
        }
    }

    public void Store(string key, ComptimeValue value)
    {
        lock (_gate)
        {
            _cache[key] = value;
        }
    }

    public void Record(string key, ComptimeValue value, bool cacheHit)
    {
        var bytes = Encoding.UTF8.GetByteCount(value.CanonicalText);
        lock (_gate)
        {
            _dependencies.Add(new MetaQueryDependency(
                ++_nextDependencySequence,
                key,
                value.CanonicalHash,
                cacheHit,
                bytes));
        }
    }

    public IReadOnlyList<MetaQueryCacheEntry> SnapshotCacheEntries()
    {
        lock (_gate)
        {
            return _cache
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => new MetaQueryCacheEntry(entry.Key, entry.Value))
                .ToArray();
        }
    }

    public bool TryRestoreCache(
        IEnumerable<MetaQueryCacheEntry> entries,
        out string failure) =>
        TryRestoreState(entries, [], out failure);

    public bool TryRestoreState(
        IEnumerable<MetaQueryCacheEntry> entries,
        IEnumerable<MetaQueryDependency> dependencies,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(dependencies);

        var incomingCache = new Dictionary<string, ComptimeValue>(StringComparer.Ordinal);
        foreach (var entry in entries.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            if (incomingCache.TryGetValue(entry.Key, out var duplicate) &&
                !duplicate.StructuralEquals(entry.Value))
            {
                failure = $"conflicting restored meta query cache result for key '{entry.Key}'";
                return false;
            }

            incomingCache[entry.Key] = entry.Value;
        }

        var incomingDependencies = dependencies
            .Select(static dependency => new MetaQueryDependency(
                0,
                dependency.Key,
                dependency.ResultHash,
                dependency.CacheHit,
                dependency.ResultBytes))
            .DistinctBy(static dependency => (
                dependency.Key,
                dependency.ResultHash,
                dependency.CacheHit,
                dependency.ResultBytes))
            .OrderBy(static dependency => dependency.Key, StringComparer.Ordinal)
            .ThenBy(static dependency => dependency.ResultHash, StringComparer.Ordinal)
            .ThenBy(static dependency => dependency.CacheHit)
            .ThenBy(static dependency => dependency.ResultBytes)
            .ToArray();

        lock (_gate)
        {
            foreach (var entry in incomingCache)
            {
                if (_cache.TryGetValue(entry.Key, out var existing) &&
                    !existing.StructuralEquals(entry.Value))
                {
                    failure = $"conflicting meta query cache result for key '{entry.Key}'";
                    return false;
                }
            }

            foreach (var dependency in incomingDependencies)
            {
                var hasIncomingValue = incomingCache.TryGetValue(dependency.Key, out var value);
                if (!hasIncomingValue && !_cache.TryGetValue(dependency.Key, out value))
                {
                    failure = $"restored meta query dependency has no cache result for key '{dependency.Key}'";
                    return false;
                }

                var bytes = Encoding.UTF8.GetByteCount(value!.CanonicalText);
                if (!string.Equals(value.CanonicalHash, dependency.ResultHash, StringComparison.Ordinal) ||
                    bytes != dependency.ResultBytes)
                {
                    failure = $"restored meta query dependency result does not match key '{dependency.Key}'";
                    return false;
                }
            }

            foreach (var entry in incomingCache)
            {
                _cache[entry.Key] = entry.Value;
            }

            var existingDependencies = _dependencies
                .Select(static dependency => (
                    dependency.Key,
                    dependency.ResultHash,
                    dependency.CacheHit,
                    dependency.ResultBytes))
                .ToHashSet();
            foreach (var dependency in incomingDependencies)
            {
                if (!existingDependencies.Add((
                        dependency.Key,
                        dependency.ResultHash,
                        dependency.CacheHit,
                        dependency.ResultBytes)))
                {
                    continue;
                }

                _dependencies.Add(dependency with { Sequence = ++_nextDependencySequence });
            }
        }

        failure = string.Empty;
        return true;
    }

    public IReadOnlyList<MetaQueryDependency> SnapshotDependencies()
    {
        lock (_gate)
        {
            return _dependencies.ToArray();
        }
    }
}
