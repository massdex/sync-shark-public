namespace Massdex.SyncShark;

/// <summary>
/// Moves frequently-mutated legacy JSONL state into the existing crash-safe alternating-slot
/// format. The JSONL predecessor is deliberately retained as recovery evidence, but once either
/// successor slot exists it is never consulted again; corrupt successor state therefore cannot
/// silently roll authority back to an old connection or preference.
/// </summary>
internal sealed class MigratingMutableJsonStore<T>
{
    private readonly object _gate;
    private readonly AppendOnlyJsonStore<T> _legacy;
    private readonly DualSlotJsonStore<MutableJsonValue<T>> _current;

    public MigratingMutableJsonStore(PilotStateAuthority authority, string legacyFileName)
    {
        if (!legacyFileName.EndsWith(".jsonl", StringComparison.Ordinal) ||
            Path.GetFileName(legacyFileName) != legacyFileName)
            throw new ArgumentException("A legacy JSONL state file name is required.", nameof(legacyFileName));
        var baseName = Path.GetFileNameWithoutExtension(legacyFileName);
        // The authority's gate, not a per-instance one: a migration on one instance must not overwrite an
        // update committed through another between its selection and its write.
        _gate = authority.StateStoreGate(baseName);
        _legacy = new AppendOnlyJsonStore<T>(authority, legacyFileName);
        _current = new DualSlotJsonStore<MutableJsonValue<T>>(authority, baseName);
    }

    public bool Exists => Load() is not null;

    public T? Load()
    {
        lock (_gate)
        {
            var (value, fromLegacy) = Select();
            if (!fromLegacy) return value;
            _current.SaveAsync(new MutableJsonValue<T>(true, value)).GetAwaiter().GetResult();
            var verified = _current.Load() ??
                           throw new IOException("Mutable state migration did not produce a readable successor.");
            if (!verified.Present || verified.Value is null)
                throw new IOException("Mutable state migration did not preserve the predecessor value.");
            return verified.Value;
        }
    }

    public T? Read()
    {
        lock (_gate) return Select().Value;
    }

    // Selection, transform and the synchronous dual-slot commit share one hold of the gate. The selected
    // predecessor is not migrated first: this commit is itself the one successor write.
    public T Update(Func<T?, T> update, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = update(Select().Value);
            ArgumentNullException.ThrowIfNull(value);
            _current.SaveAsync(new MutableJsonValue<T>(true, value), cancellationToken).GetAwaiter().GetResult();
            return value;
        }
    }

    public Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _ = LoadCurrentOrLegacyForMigration();
            return _current.SaveAsync(new MutableJsonValue<T>(true, value), cancellationToken);
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _ = LoadCurrentOrLegacyForMigration();
            return _current.SaveAsync(new MutableJsonValue<T>(false, default), cancellationToken);
        }
    }

    private T? LoadCurrentOrLegacyForMigration()
    {
        var (value, fromLegacy) = Select();
        if (fromLegacy) _current.SaveAsync(new MutableJsonValue<T>(true, value)).GetAwaiter().GetResult();
        return value;
    }

    // Writes nothing. A successor slot, tombstone included, always wins; a corrupt one throws from Load.
    private (T? Value, bool FromLegacy) Select()
    {
        var current = _current.Load();
        if (current is not null) return (current.Present ? current.Value : default, false);
        var legacy = _legacy.Load();
        return (legacy, legacy is not null);
    }
}
