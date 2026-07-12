using System.Collections.Concurrent;

namespace Eidosc.Query;

public sealed class QueryEngine
{
    private readonly record struct QueryJobId(Type QueryType, object Key);

    private readonly DependencyGraph _depGraph = new();
    private readonly ConcurrentDictionary<QueryJobId, object?> _activeJobs = new();
    private readonly ConcurrentDictionary<QueryJobId, TaskCompletionSource<object?>> _latches = new();
    private readonly ConcurrentDictionary<QueryJobId, int> _jobVersions = new();
    private readonly ConcurrentDictionary<int, QueryJobId> _depQueryJobs = new();
    private readonly Dictionary<Type, object> _descriptors = new();
    private readonly Dictionary<Type, object> _caches = new();
    private readonly Dictionary<Type, DepKind> _depKinds = new();

    public DependencyGraph DepGraph => _depGraph;

    public void Register<TKey, TResult>(QueryDescriptor<TKey, TResult> descriptor, DepKind depKind)
        where TKey : notnull
    {
        var queryType = typeof(QueryDescriptor<TKey, TResult>);
        _descriptors[queryType] = descriptor;
        _caches[queryType] = descriptor.CreateCache();
        _depKinds[queryType] = depKind;
    }

    public void Register<TKey, TResult>(QueryDescriptor<TKey, TResult> descriptor)
        where TKey : notnull
    {
        Register(descriptor, DepKindFor<TResult>());
    }

    public TResult Execute<TKey, TResult>(TKey key, Func<TKey, TResult> provider)
        where TKey : notnull
    {
        return Execute(key, provider, CancellationToken.None);
    }

    public TResult Execute<TKey, TResult>(
        TKey key,
        Func<TKey, TResult> provider,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        var queryType = typeof(QueryDescriptor<TKey, TResult>);
        var cache = (IQueryCache<TKey, TResult>)_caches[queryType];

        cancellationToken.ThrowIfCancellationRequested();

        var cached = cache.Lookup(key);
        if (cached.HasValue)
        {
            RecordCurrentDependency(cached.Value.DepIndex);
            return cached.Value.Result!;
        }

        var jobId = new QueryJobId(queryType, key);

        if (_activeJobs.TryAdd(jobId, null))
        {
            var jobVersion = GetJobVersion(jobId);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var depKind = _depKinds.TryGetValue(queryType, out var dk) ? dk : DepKindFor<TResult>();
                var depIndex = _depGraph.Record(DepNode.Create(depKind, key));
                RecordDepQueryJob(depIndex, jobId);
                var result = RunWithTracking(depIndex, () => provider(key));
                cancellationToken.ThrowIfCancellationRequested();

                if (IsJobVersionCurrent(jobId, jobVersion))
                {
                    cache.Insert(key, result, depIndex);
                }

                _activeJobs.TryRemove(jobId, out _);

                if (_latches.TryRemove(jobId, out var latch))
                    latch.SetResult(null);

                RecordCurrentDependency(depIndex);
                return result;
            }
            catch (OperationCanceledException)
            {
                _activeJobs.TryRemove(jobId, out _);
                if (_latches.TryRemove(jobId, out var latch))
                    latch.TrySetCanceled(cancellationToken);
                throw;
            }
            catch
            {
                _activeJobs.TryRemove(jobId, out _);
                if (_latches.TryRemove(jobId, out var latch))
                    latch.TrySetCanceled();
                throw;
            }
        }

        var latch2 = _latches.GetOrAdd(jobId, _ => new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_activeJobs.ContainsKey(jobId) &&
            _latches.TryRemove(jobId, out var orphanedLatch))
        {
            orphanedLatch.TrySetResult(null);
        }

        latch2.Task.Wait(cancellationToken);

        var afterCache = cache.Lookup(key);
        if (afterCache.HasValue)
        {
            RecordCurrentDependency(afterCache.Value.DepIndex);
            return afterCache.Value.Result!;
        }

        return Execute(key, provider, cancellationToken);
    }

    public async Task<TResult> ExecuteAsync<TKey, TResult>(TKey key, Func<TKey, Task<TResult>> provider)
        where TKey : notnull
    {
        return await ExecuteAsync(
            key,
            (currentKey, _) => provider(currentKey),
            CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<TResult> ExecuteAsync<TKey, TResult>(
        TKey key,
        Func<TKey, CancellationToken, Task<TResult>> provider,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        var queryType = typeof(QueryDescriptor<TKey, TResult>);
        var cache = (IQueryCache<TKey, TResult>)_caches[queryType];

        cancellationToken.ThrowIfCancellationRequested();

        var cached = cache.Lookup(key);
        if (cached.HasValue)
        {
            RecordCurrentDependency(cached.Value.DepIndex);
            return cached.Value.Result!;
        }

        var jobId = new QueryJobId(queryType, key);

        if (_activeJobs.TryAdd(jobId, null))
        {
            var jobVersion = GetJobVersion(jobId);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var depKind = _depKinds.TryGetValue(queryType, out var dk) ? dk : DepKindFor<TResult>();
                var depIndex = _depGraph.Record(DepNode.Create(depKind, key));
                RecordDepQueryJob(depIndex, jobId);
                var result = await RunWithTrackingAsync(depIndex, () => provider(key, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();

                if (IsJobVersionCurrent(jobId, jobVersion))
                {
                    cache.Insert(key, result, depIndex);
                }

                _activeJobs.TryRemove(jobId, out _);

                if (_latches.TryRemove(jobId, out var latch))
                    latch.SetResult(null);

                RecordCurrentDependency(depIndex);
                return result;
            }
            catch (OperationCanceledException)
            {
                _activeJobs.TryRemove(jobId, out _);
                if (_latches.TryRemove(jobId, out var latch))
                    latch.TrySetCanceled(cancellationToken);
                throw;
            }
            catch
            {
                _activeJobs.TryRemove(jobId, out _);
                if (_latches.TryRemove(jobId, out var latch))
                    latch.TrySetCanceled();
                throw;
            }
        }

        var latch2 = _latches.GetOrAdd(jobId, _ => new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_activeJobs.ContainsKey(jobId) &&
            _latches.TryRemove(jobId, out var orphanedLatch))
        {
            orphanedLatch.TrySetResult(null);
        }

        await latch2.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        var afterCache = cache.Lookup(key);
        if (afterCache.HasValue)
        {
            RecordCurrentDependency(afterCache.Value.DepIndex);
            return afterCache.Value.Result!;
        }

        return await ExecuteAsync(key, provider, cancellationToken).ConfigureAwait(false);
    }

    private int GetJobVersion(QueryJobId jobId)
    {
        return _jobVersions.GetOrAdd(jobId, 0);
    }

    private bool IsJobVersionCurrent(QueryJobId jobId, int version)
    {
        return _jobVersions.TryGetValue(jobId, out var current) && current == version;
    }

    private void BumpJobVersion(Type queryType, object key)
    {
        var jobId = new QueryJobId(queryType, key);
        _jobVersions.AddOrUpdate(jobId, 1, static (_, current) => current + 1);
    }

    private void BumpJobVersionsForQuery(Type queryType)
    {
        foreach (var jobId in _jobVersions.Keys)
        {
            if (jobId.QueryType == queryType)
            {
                BumpJobVersion(queryType, jobId.Key);
            }
        }
    }

    private void RecordCurrentDependency(DepNodeIndex depIndex)
    {
        if (_currentJob.Value is { } current && depIndex.IsValid)
            _depGraph.AddEdge(current, depIndex);
    }

    private void RecordDepQueryJob(DepNodeIndex depIndex, QueryJobId jobId)
    {
        if (depIndex.IsValid)
        {
            _depQueryJobs[depIndex.Value] = jobId;
        }
    }

    private readonly AsyncLocal<DepNodeIndex?> _currentJob = new();

    private TResult RunWithTracking<TResult>(DepNodeIndex depIndex, Func<TResult> fn)
    {
        var prev = _currentJob.Value;
        _currentJob.Value = depIndex;
        try { return fn(); }
        finally { _currentJob.Value = prev; }
    }

    private async Task<TResult> RunWithTrackingAsync<TResult>(DepNodeIndex depIndex, Func<Task<TResult>> fn)
    {
        var prev = _currentJob.Value;
        _currentJob.Value = depIndex;
        try { return await fn(); }
        finally { _currentJob.Value = prev; }
    }

    public void Invalidate(DepNode node)
    {
        var index = _depGraph.GetIndex(node);
        if (!index.IsValid) return;
        InvalidateDependents(index, [], null);
    }

    public void InvalidateKey<TKey>(TKey key, DepKind kind) where TKey : notnull
    {
        var node = DepNode.Create(kind, key);
        var index = _depGraph.GetIndex(node);
        if (!index.IsValid) return;
        InvalidateDependents(index, [], key);
    }

    private void InvalidateDependents(DepNodeIndex index, HashSet<int> visited, object? key)
    {
        if (!visited.Add(index.Value)) return;

        var node = _depGraph.GetNode(index);
        if (node != null)
        {
            if (key != null && _depQueryJobs.TryGetValue(index.Value, out var jobId))
            {
                if (_caches.TryGetValue(jobId.QueryType, out var cacheObj))
                {
                    var descriptor = (IClearableQuery)_descriptors[jobId.QueryType];
                    BumpJobVersion(jobId.QueryType, jobId.Key);
                    descriptor.RemoveCacheKey(cacheObj, jobId.Key);
                }
            }
            else
            {
                foreach (var (queryType, cacheObj) in _caches)
                {
                    if (_depKinds.TryGetValue(queryType, out var kind) && kind == node.Value.Kind)
                    {
                        var descriptor = (IClearableQuery)_descriptors[queryType];
                        BumpJobVersionsForQuery(queryType);
                        descriptor.ClearCache(cacheObj);
                        break;
                    }
                }
            }
        }

        foreach (var dependent in _depGraph.GetDependents(index))
            InvalidateDependents(dependent, visited, key);
    }

    public TResult? TryGetCached<TKey, TResult>(TKey key)
        where TKey : notnull
        where TResult : class
    {
        var queryType = typeof(QueryDescriptor<TKey, TResult>);
        if (!_caches.TryGetValue(queryType, out var cacheObj))
            return null;
        var cache = (IQueryCache<TKey, TResult>)cacheObj;
        var cached = cache.Lookup(key);
        return cached.HasValue ? cached.Value.Result : null;
    }

    public void ClearAllCaches()
    {
        foreach (var (queryType, cacheObj) in _caches)
        {
            var descriptor = (IClearableQuery)_descriptors[queryType];
            BumpJobVersionsForQuery(queryType);
            descriptor.ClearCache(cacheObj);
        }
        _depGraph.Clear();
        _depQueryJobs.Clear();
    }

    private static DepKind DepKindFor<TResult>()
    {
        var name = typeof(TResult).Name;
        if (name.Contains("Parse")) return DepKind.ParseModule;
        if (name.Contains("SymbolTable") || name.Contains("Name")) return DepKind.ResolveNames;
        if (name.Contains("Type")) return DepKind.InferTypes;
        if (name.Contains("Effect")) return DepKind.InferAbilities;
        if (name.Contains("Hir")) return DepKind.BuildHir;
        if (name.Contains("Mir")) return DepKind.BuildMir;
        if (name.Contains("Borrow")) return DepKind.CheckBorrow;
        if (name.Contains("Optim")) return DepKind.Optimize;
        if (name.Contains("Send")) return DepKind.CheckSend;
        return DepKind.CodeGen;
    }
}

internal interface IClearable { void Clear(); }

public interface IClearableQuery
{
    void ClearCache(object cache);
    void RemoveCacheKey(object cache, object key);
}

public abstract class QueryDescriptor<TKey, TResult> : IClearableQuery where TKey : notnull
{
    public abstract IQueryCache<TKey, TResult> CreateCache();

    public void ClearCache(object cache)
    {
        ((IQueryCache<TKey, TResult>)cache).Clear();
    }

    public void RemoveCacheKey(object cache, object key)
    {
        if (key is TKey typedKey)
            ((IQueryCache<TKey, TResult>)cache).Remove(typedKey);
    }
}
