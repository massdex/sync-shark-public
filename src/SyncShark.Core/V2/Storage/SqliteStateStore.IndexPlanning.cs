namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    /// <summary>The durable store owns the bounded planning queue, so an index hand-off is supported.</summary>
    public bool SupportsIndexPlanning => true;

    /// <summary>
    /// Transfers verified entity obligations to the existing bounded planning owner atomically. Returns the
    /// number of entities actually transferred, which is the caller's only accepted-success evidence.
    /// </summary>
    public int PublishIndexPlanning(string rootId, IReadOnlyList<ItemState> items) => InIndexingStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return 0;
        using var tx = _connection.BeginTransaction();
        var ready = new List<(string ObjectId, string Path)>();
        foreach (var expected in items)
        {
            var current = _reads.ByObject(root.Value, expected.ObjectId, tx);
            if (current is null || current.Hash is null || (current.Flags & ItemFlags.NeedsRehash) != 0 ||
                current.Hash != expected.Hash || current.Size != expected.Size || current.Mtime != expected.Mtime) continue;
            // The store gate spans revalidation, transfer and retirement. A newer journal write
            // cannot slip between these steps and lose its proof obligation.
            if (StoreSchema.Scalar(_connection, tx,
                "SELECT 1 FROM local_rehash WHERE root_no=$r AND object_id=$o;",
                ("$r", root.Value), ("$o", expected.ObjectId)) is null) continue;
            ready.Add((current.ObjectId, current.Path));
            StoreSchema.Execute(_connection, tx, "DELETE FROM local_rehash WHERE root_no=$r AND object_id=$o;",
                ("$r", root.Value), ("$o", current.ObjectId));
        }
        MarkPlanningDirty(tx, root.Value, ready);
        tx.Commit();
        return ready.Count;
    });
}
