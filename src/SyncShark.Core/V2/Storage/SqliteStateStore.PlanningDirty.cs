namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Run observation handoff uses the same entity-owned queue as indexing. Publish before
    // advancing the scan cursor; each bounded batch is durable even if a later batch fails.
    internal void RetainPlanningObservations(string rootId, IEnumerable<ItemState> items,
        CancellationToken ct)
    {
        foreach (var batch in items.Chunk(256))
        {
            ct.ThrowIfCancellationRequested();
            InStore(() =>
            {
                var root = RootNo(rootId);
                if (root is null) return;
                using var tx = _connection.BeginTransaction();
                MarkPlanningDirty(tx, root.Value, batch.Select(item => (item.ObjectId, item.Path)).ToArray());
                tx.Commit();
            });
        }
    }

    public long IndexPlanningWatermark(string rootId) => InStore(() => Convert.ToInt64(StoreSchema.Scalar(_connection, null,
        "SELECT COALESCE(MAX(version),0) FROM planning_deferred WHERE root_no=(SELECT root_no FROM root WHERE root_id=$id);",
        ("$id", rootId))));
}
