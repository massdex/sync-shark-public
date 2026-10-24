namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public bool SupportsJournalWindows(string rootId) => InStore(() =>
        RootNo(rootId) is { } root && Cursor(rootId) is { Length: > 0 } &&
        RootSides(root, null).All(binding => StoreSchema.Scalar(_connection, null,
            "SELECT first_sync_done FROM pair WHERE pair_no=$p;", ("$p", binding.PairNo)) is long done && done == 1));

    public long? JournalWindowSize(long serial, string rootId) => InStore(() =>
        ReadJournal(serial)?.ReplayWindows?.GetValueOrDefault(rootId));

    public void SaveJournalWindowSize(long serial, string volume, string rootId, long bytes) => InIndexingStore(() =>
    {
        var state = ReadJournal(serial) ?? new JournalSizingState(0, 0, _clock.GetUtcNow());
        var windows = new Dictionary<string, long>(state.ReplayWindows ?? new());
        if (windows.GetValueOrDefault(rootId) == bytes) return;
        windows[rootId] = bytes;
        state = state with { ReplayWindows = windows };
        StoreSchema.Execute(_connection, null,
            "INSERT INTO journal_volume(serial,volume,state_json) VALUES($s,$v,$j) " +
            "ON CONFLICT(serial) DO UPDATE SET state_json=excluded.state_json;",
            ("$s", serial), ("$v", volume), ("$j", System.Text.Json.JsonSerializer.Serialize(state)));
    });

    // Keep the caller's generation open. A crash before the final transaction replays this
    // window; a crash after it finds both the new cursor and the durable planning handoff.
    // No Local dependency: this accepts the existing inventory delta contract.
    public void CheckpointJournalWindow(Delta delta) => InIndexingStore(() =>
    {
        if (!SupportsJournalWindows(delta.RootId) || delta.CursorInvalid || delta.IncompleteSubtrees.Count != 0 || delta.PendingDeletionEvidence.Count != 0)
            throw new InvalidOperationException("Only complete journal windows on committed, first-sync-complete roots can be checkpointed.");
        WriteDelta(delta);
        {
            var root = RootNo(delta.RootId) ?? throw new InvalidOperationException("Unknown journal root.");
            RequireOpenGen(null, delta.Gen, root);
            using var tx = _connection.BeginTransaction();
            var rows = System.Text.Json.JsonSerializer.Serialize(delta.Changes
                .Where(change => change.ObjectId.Length > 0 && (delta.IncludesPath?.Invoke(change.Path) ?? true))
                .Select(change => new { root, id = change.ObjectId, path = change.Path }));
            foreach (var pair in RootSides(root, tx).Select(binding => binding.PairNo).Distinct())
                WritePlanningDirty(tx, pair, rows, UserChangeReason);
            StoreSchema.Execute(_connection, tx, "UPDATE root SET cursor=$cursor WHERE root_no=$r;",
                ("$cursor", delta.CursorAfter), ("$r", root));
            tx.Commit();
        }
    });
}
