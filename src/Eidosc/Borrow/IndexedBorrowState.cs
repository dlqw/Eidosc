using Eidosc.Mir;

namespace Eidosc.Borrow;

internal sealed class IndexedBorrowState<TBorrow, TKey>
    where TBorrow : class
    where TKey : notnull
{
    private readonly Func<TBorrow, TKey> _borrowKeySelector;
    private readonly Func<TBorrow, LocalId> _borrowerSelector;
    private readonly Func<TBorrow, LocalId> _borroweeSelector;
    private readonly Func<TBorrow, BorrowTarget> _borrowTargetSelector;
    private readonly Func<TBorrow, TBorrow> _cloneBorrow;
    private readonly Func<TBorrow, int>? _borrowIdSelector;

    private readonly Dictionary<TKey, TBorrow> _borrowsByKey = [];
    private readonly Dictionary<LocalId, HashSet<TKey>> _borrowKeysByBorrower = [];
    private readonly Dictionary<LocalId, HashSet<TKey>> _borrowKeysByBorrowee = [];
    private readonly HashSet<int>? _borrowIds;

    /// <summary>
    /// Set whenever borrows are added or removed; consumed by BorrowChecker
    /// to skip unnecessary snapshot cloning.
    /// </summary>
    private bool _isDirty;

    public List<TBorrow> Borrows { get; } = [];

    public IndexedBorrowState(
        IEnumerable<TBorrow> borrows,
        Func<TBorrow, TKey> borrowKeySelector,
        Func<TBorrow, LocalId> borrowerSelector,
        Func<TBorrow, LocalId> borroweeSelector,
        Func<TBorrow, TBorrow> cloneBorrow,
        bool cloneInputs = true,
        Func<TBorrow, int>? borrowIdSelector = null,
        Func<TBorrow, BorrowTarget>? borrowTargetSelector = null)
    {
        _borrowKeySelector = borrowKeySelector;
        _borrowerSelector = borrowerSelector;
        _borroweeSelector = borroweeSelector;
        _cloneBorrow = cloneBorrow;
        _borrowIdSelector = borrowIdSelector;
        _borrowTargetSelector = borrowTargetSelector ?? (borrow => BorrowTarget.ForLocal(_borroweeSelector(borrow)));
        _borrowIds = borrowIdSelector == null ? null : [];

        foreach (var borrow in borrows)
        {
            TryAddBorrow(cloneInputs ? _cloneBorrow(borrow) : borrow);
        }
    }

    public bool SemanticallyEquals(IndexedBorrowState<TBorrow, TKey> other)
    {
        if (_borrowsByKey.Count != other._borrowsByKey.Count)
        {
            return false;
        }

        foreach (var key in _borrowsByKey.Keys)
        {
            if (!other._borrowsByKey.ContainsKey(key))
            {
                return false;
            }
        }

        return true;
    }

    public bool ContainsBorrowId(int borrowId)
    {
        return _borrowIds != null && _borrowIds.Contains(borrowId);
    }

    public bool TryAddBorrow(TBorrow borrow)
    {
        var key = _borrowKeySelector(borrow);
        if (_borrowsByKey.ContainsKey(key))
        {
            return false;
        }

        _borrowsByKey[key] = borrow;
        Borrows.Add(borrow);
        _isDirty = true;

        if (_borrowIds != null && _borrowIdSelector != null)
        {
            _borrowIds.Add(_borrowIdSelector(borrow));
        }

        AddBorrowIndex(_borrowKeysByBorrower, _borrowerSelector(borrow), key);
        AddBorrowIndex(_borrowKeysByBorrowee, _borrowTargetSelector(borrow).BaseLocal, key);
        return true;
    }

    public bool IsBorrower(LocalId localId)
    {
        return _borrowKeysByBorrower.TryGetValue(localId, out var keys) && keys.Count > 0;
    }

    public List<TBorrow> GetBorrowsByBorrower(LocalId localId)
    {
        return GetBorrows(_borrowKeysByBorrower, localId);
    }

    public List<TBorrow> GetBorrowsByBorrowee(LocalId localId)
    {
        return GetBorrows(_borrowKeysByBorrowee, localId);
    }

    public List<TBorrow> GetBorrowsByBorrowTarget(BorrowTarget target)
    {
        if (!target.IsValid)
        {
            return [];
        }

        if (!_borrowKeysByBorrowee.TryGetValue(target.BaseLocal, out var keys) || keys.Count == 0)
        {
            return [];
        }

        var result = new List<TBorrow>(keys.Count);
        foreach (var key in keys)
        {
            if (!_borrowsByKey.TryGetValue(key, out var borrow) ||
                !_borrowTargetSelector(borrow).OverlapsWith(target))
            {
                continue;
            }

            result.Add(borrow);
        }

        return result;
    }

    public List<LocalId> ResolveBorrowTargets(LocalId localId)
    {
        var targetPaths = ResolveBorrowTargetPaths(localId);
        var result = new List<LocalId>(targetPaths.Count);
        var seen = new HashSet<LocalId>();
        foreach (var target in targetPaths)
        {
            if (seen.Add(target.BaseLocal))
            {
                result.Add(target.BaseLocal);
            }
        }

        return result;
    }

    public List<BorrowTarget> ResolveBorrowTargetPaths(LocalId localId)
    {
        if (!_borrowKeysByBorrower.TryGetValue(localId, out var keys) || keys.Count == 0)
        {
            return [BorrowTarget.ForLocal(localId)];
        }

        var result = new List<BorrowTarget>(keys.Count);
        var seen = new HashSet<BorrowTarget>();
        foreach (var key in keys)
        {
            if (!_borrowsByKey.TryGetValue(key, out var borrow))
            {
                continue;
            }

            var target = _borrowTargetSelector(borrow);
            if (!seen.Add(target))
            {
                continue;
            }

            result.Add(target);
        }

        return result;
    }

    public void EndBorrowsByBorrower(LocalId localId, Action<TBorrow> onBorrowEnded)
    {
        EndBorrows(_borrowKeysByBorrower, localId, onBorrowEnded);
    }

    public void EndBorrowsByBorrowee(LocalId localId, Action<TBorrow> onBorrowEnded)
    {
        EndBorrows(_borrowKeysByBorrowee, localId, onBorrowEnded);
    }

    public void EndBorrowsByBorrowTarget(BorrowTarget target, Action<TBorrow> onBorrowEnded)
    {
        if (!target.IsValid || !_borrowKeysByBorrowee.TryGetValue(target.BaseLocal, out var keys) || keys.Count == 0)
        {
            return;
        }

        foreach (var key in keys.ToList())
        {
            if (!_borrowsByKey.TryGetValue(key, out var borrow) ||
                !_borrowTargetSelector(borrow).OverlapsWith(target))
            {
                continue;
            }

            onBorrowEnded(borrow);
            RemoveBorrow(key, borrow);
        }
    }

    private void EndBorrows(
        Dictionary<LocalId, HashSet<TKey>> index,
        LocalId localId,
        Action<TBorrow> onBorrowEnded)
    {
        if (!index.TryGetValue(localId, out var keys) || keys.Count == 0)
        {
            return;
        }

        foreach (var key in keys.ToList())
        {
            if (!_borrowsByKey.TryGetValue(key, out var borrow))
            {
                continue;
            }

            onBorrowEnded(borrow);
            RemoveBorrow(key, borrow);
        }
    }

    private List<TBorrow> GetBorrows(
        Dictionary<LocalId, HashSet<TKey>> index,
        LocalId localId)
    {
        if (!index.TryGetValue(localId, out var keys) || keys.Count == 0)
        {
            return [];
        }

        var result = new List<TBorrow>(keys.Count);
        foreach (var key in keys)
        {
            if (_borrowsByKey.TryGetValue(key, out var borrow))
            {
                result.Add(borrow);
            }
        }

        return result;
    }

    private void RemoveBorrow(TKey key, TBorrow borrow)
    {
        _borrowsByKey.Remove(key);
        Borrows.Remove(borrow);
        _isDirty = true;

        if (_borrowIds != null && _borrowIdSelector != null)
        {
            _borrowIds.Remove(_borrowIdSelector(borrow));
        }

        RemoveBorrowIndex(_borrowKeysByBorrower, _borrowerSelector(borrow), key);
        RemoveBorrowIndex(_borrowKeysByBorrowee, _borrowTargetSelector(borrow).BaseLocal, key);
    }

    private static void AddBorrowIndex(
        Dictionary<LocalId, HashSet<TKey>> index,
        LocalId localId,
        TKey key)
    {
        if (!index.TryGetValue(localId, out var keys))
        {
            keys = [];
            index[localId] = keys;
        }

        keys.Add(key);
    }

    private static void RemoveBorrowIndex(
        Dictionary<LocalId, HashSet<TKey>> index,
        LocalId localId,
        TKey key)
    {
        if (!index.TryGetValue(localId, out var keys))
        {
            return;
        }

        keys.Remove(key);
        if (keys.Count == 0)
        {
            index.Remove(localId);
        }
    }

    /// <summary>
    /// Returns whether borrows were added/removed since the last call,
    /// and resets the flag. Used by BorrowChecker to skip redundant snapshots.
    /// </summary>
    public bool ConsumeDirty()
    {
        var dirty = _isDirty;
        _isDirty = false;
        return dirty;
    }
}
