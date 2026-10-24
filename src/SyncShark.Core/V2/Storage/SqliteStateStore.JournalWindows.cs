namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public bool SupportsJournalWindows => true;

    // Keep the caller's generation open. A crash before the final transaction replays this
    // window; a crash after it finds both the new cursor and the durable planning handoff.
    // No Local dependency: this accepts the existing inventory delta contract.
    public void CheckpointJournalWindow(Delta delta)
    {
        if (delta.CursorInvalid || delta.IncompleteSubtrees.Count != 0 || delta.PendingDeletionEvidence.Count != 0)
            throw new InvalidOperationException("Only a complete journal window can be checkpointed.");
        WriteDelta(delta);
        InIndexingStore(() =>
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
        });
    }
}
