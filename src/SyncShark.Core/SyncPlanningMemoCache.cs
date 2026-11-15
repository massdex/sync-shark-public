namespace Massdex.SyncShark;

/// <summary>
/// The one bounded accounting budget every planning memo cache shares. These are recomputable cache
/// limits over pure derived values, never retention of user state: an evicted entry is recomputed
/// identically from its exact key inputs on the next read, and no authority decision ever reads a memo.
/// </summary>
internal static class SyncPlanningMemoBudget
{
    /// <summary>The fixed per-entry bookkeeping overhead charged to every retained entry.</summary>
    internal const int PerEntryOverheadBytes = 128;

    /// <summary>The maximum number of entries one planning memo cache retains.</summary>
    internal const int MaximumEntries = 128;

    /// <summary>The maximum estimated retained bytes one planning memo cache holds.</summary>
    internal const long MaximumBytes = 8L * 1024 * 1024;
}

/// <summary>
/// One small internal FIFO memo cache for a pure planning calculation. It exists so the three planning
/// memos share one bounded policy instead of each carrying its own ad-hoc "clear everything once the
/// count reaches a limit" insertion rule, which discarded the whole memo and could retain an unbounded
/// number of bytes in the meantime.
///
/// Retention is bounded twice over: at most <see cref="SyncPlanningMemoBudget.MaximumEntries"/> entries
/// and at most <see cref="SyncPlanningMemoBudget.MaximumBytes"/> estimated retained bytes. An item whose
/// own weight exceeds the byte budget is never retained at all. Admission, replacement, eviction and
/// clearing are one atomic critical section, so a concurrent reader never observes a half-updated entry
/// or an accounting total that disagrees with the retained entries.
///
/// This type owns no schema, no persistence and no authority. Every caller still reads the current pair
/// state, roots, scan identities and synchronization points inside its own transaction; a memo miss or
/// eviction only recomputes the identical pure value.
/// </summary>
internal sealed class SyncPlanningMemoCache<TKey, TValue> where TKey : notnull
{
    private sealed class Entry(TValue value, int weight)
    {
        internal TValue Value { get; set; } = value;
        internal int Weight { get; set; } = weight;
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries = [];
    private readonly LinkedList<TKey> _admissionOrder = [];
    private readonly Func<TKey, TValue, int> _weight;
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private long _retainedBytes;

    internal SyncPlanningMemoCache(Func<TKey, TValue, int> weight, int maximumEntries =
        SyncPlanningMemoBudget.MaximumEntries, long maximumBytes = SyncPlanningMemoBudget.MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(weight);
        if (maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _weight = weight;
        _maximumEntries = maximumEntries;
        _maximumBytes = maximumBytes;
    }

    /// <summary>The number of entries currently retained.</summary>
    internal int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>The currently charged estimated retained bytes, for the bounded-growth proof.</summary>
    internal long RetainedBytes
    {
        get { lock (_gate) return _retainedBytes; }
    }

    internal int MaximumEntries => _maximumEntries;

    internal long MaximumBytes => _maximumBytes;

    internal bool TryGetValue(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                value = entry.Value;
                return true;
            }
            value = default!;
            return false;
        }
    }

    /// <summary>Adds only when the exact key is absent; an existing key keeps its retained value.</summary>
    internal bool TryAdd(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(key)) return false;
            Retain(key, value);
            return _entries.ContainsKey(key);
        }
    }

    /// <summary>Adds, or replaces the value retained for an existing key, keeping its admission order.</summary>
    internal TValue this[TKey key]
    {
        set
        {
            lock (_gate)
            {
                var weight = _weight(key, value);
                if (weight > _maximumBytes)
                {
                    // An item heavier than the whole byte budget is never retained, and never displaces a
                    // retained entry it cannot legitimately replace.
                    Discard(key);
                    return;
                }
                if (_entries.TryGetValue(key, out var existing))
                {
                    _retainedBytes += weight - existing.Weight;
                    existing.Value = value;
                    existing.Weight = weight;
                }
                else
                {
                    Retain(key, value);
                    return;
                }
                Evict();
            }
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _admissionOrder.Clear();
            _retainedBytes = 0;
        }
    }

    private void Retain(TKey key, TValue value)
    {
        var weight = _weight(key, value);
        if (weight > _maximumBytes) return;
        _entries[key] = new Entry(value, weight);
        _admissionOrder.AddLast(key);
        _retainedBytes += weight;
        Evict();
    }

    private void Discard(TKey key)
    {
        if (!_entries.Remove(key, out var removed)) return;
        _retainedBytes -= removed.Weight;
        _admissionOrder.Remove(key);
    }

    // FIFO: the entry retained longest is evicted first, until both bounds hold. Eviction drops only the
    // memo, never authority, so the next read recomputes the identical pure value from its exact inputs.
    private void Evict()
    {
        while ((_entries.Count > _maximumEntries || _retainedBytes > _maximumBytes) &&
               _admissionOrder.First is { } oldest)
        {
            _admissionOrder.RemoveFirst();
            if (_entries.Remove(oldest.Value, out var evicted)) _retainedBytes -= evicted.Weight;
        }
    }
}

/// <summary>
/// The three explicit weight functions of the planning memos. Every string is charged as UTF-16
/// (two bytes per character), a set member adds its own fixed overhead, and each entry carries the
/// shared per-entry overhead, so the byte bound is a conservative estimate of retained memory rather
/// than a count of only the largest member.
/// </summary>
internal static class SyncPlanningMemoWeights
{
    /// <summary>One retained string value: UTF-16 characters at two bytes each.</summary>
    internal static int RetainedString(string value) => value.Length * 2;

    /// <summary>The content-fingerprint memo: one exact cache key string to one hex fingerprint string.</summary>
    internal static int ContentFingerprint(string key, string value) =>
        SyncPlanningMemoBudget.PerEntryOverheadBytes + RetainedString(key) + RetainedString(value);

    /// <summary>The excluded-path memo: two generation keys to the immutable excluded path set.</summary>
    internal static int ExcludedPaths((string Local, string Remote) key, IReadOnlySet<string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var weight = SyncPlanningMemoBudget.PerEntryOverheadBytes +
                     RetainedString(key.Local) + RetainedString(key.Remote);
        foreach (var path in value) weight += RetainedString(path) + 64;
        return weight;
    }

    /// <summary>
    /// The planning-state memo: the existing state tuple to one hex fingerprint string. The single
    /// numeric tuple member is charged as one eight-byte value.
    /// </summary>
    internal static int PlanningState((string LocalScan, string RemoteScan, string PairId, long Revision,
        string LocalRoot, string RemoteRoot, string PairState) key, string value) =>
        SyncPlanningMemoBudget.PerEntryOverheadBytes + sizeof(long) +
        RetainedString(key.LocalScan) + RetainedString(key.RemoteScan) + RetainedString(key.PairId) +
        RetainedString(key.LocalRoot) + RetainedString(key.RemoteRoot) + RetainedString(key.PairState) +
        RetainedString(value);
}
