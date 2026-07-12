using System.Collections.Concurrent;

namespace Eidosc.Query;

public interface IQueryCache<TKey, TResult> where TKey : notnull
{
    (TResult Result, DepNodeIndex DepIndex)? Lookup(TKey key);
    void Insert(TKey key, TResult result, DepNodeIndex depIndex);
    void Remove(TKey key);
    void Clear();
    int Count { get; }
}

public sealed class DefaultQueryCache<TKey, TResult> : IQueryCache<TKey, TResult> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, (TResult Result, DepNodeIndex DepIndex)> _entries = new();

    public (TResult Result, DepNodeIndex DepIndex)? Lookup(TKey key)
    {
        return _entries.TryGetValue(key, out var entry) ? entry : null;
    }

    public void Insert(TKey key, TResult result, DepNodeIndex depIndex)
    {
        _entries[key] = (result, depIndex);
    }

    public void Remove(TKey key) => _entries.TryRemove(key, out _);
    public void Clear() => _entries.Clear();
    public int Count => _entries.Count;
}

public sealed class SingleQueryCache<TResult> : IQueryCache<UnitKey, TResult>
{
    private (TResult Result, DepNodeIndex DepIndex)? _entry;

    public (TResult Result, DepNodeIndex DepIndex)? Lookup(UnitKey key) => _entry;

    public void Insert(UnitKey key, TResult result, DepNodeIndex depIndex)
    {
        _entry = (result, depIndex);
    }

    public void Remove(UnitKey key) => _entry = null;
    public void Clear() => _entry = null;
    public int Count => _entry.HasValue ? 1 : 0;
}
