namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private static string PreservePlanningCursors(string definition, string prior = "def_json") =>
        $"json_set({definition},'$.PlanningSettleCursor',json_extract({prior},'$.PlanningSettleCursor'))";

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

    private void RetainIncrementalPlanning(Microsoft.Data.Sqlite.SqliteTransaction tx, long root,
        IReadOnlyList<ItemState> items)
    {
        if (items.Count == 0) return;
        foreach (var (number, side) in RootSides(root, tx))
        {
            // Classify fresh observations before admission; never retire existing debt from cached flags.
            var json = StoreSchema.Scalar(_connection, tx,
                "SELECT def_json FROM pair WHERE pair_no=$p AND first_sync_done=1;", ("$p", number)) as string;
            if (json is null) continue;
            var pair = Deserialize(json);
            var eligibility = new ItemEligibility([pair], []);
            var admitted = items.Where(item => PlanningObservationEligible(tx, pair, side, item, eligibility))
                .Select(item => new { root, id = item.ObjectId, path = item.Path }).ToArray();
            if (admitted.Length > 0)
                WritePlanningDirty(tx, number, System.Text.Json.JsonSerializer.Serialize(admitted), ObservationPlanningReason);
        }
    }

    private bool PlanningObservationEligible(Microsoft.Data.Sqlite.SqliteTransaction tx, PairDef pair,
        Side side, ItemState item, ItemEligibility eligibility)
    {
        if (!eligibility.Evaluate(pair, side, item, ItemEligibility.Purpose.ContentProof).Eligible) return false;
        foreach (var (definition, observedSide) in new[] { (pair.A, Side.A), (pair.B, Side.B) })
        {
            if (RootNo(definition.RootId) is not { } root) continue;
            foreach (var path in PairPath.Ancestors(item.Path).Prepend(item.Path))
                foreach (var observed in _reads.AtKey(root, path, tx))
                {
                    // The incoming row supersedes this same identity's old cached classification.
                    if (observedSide == side && observed.Item.ObjectId == item.ObjectId) continue;
                    if (!eligibility.Evaluate(pair, observedSide, observed.Item, ItemEligibility.Purpose.ContentProof).Eligible)
                        return false;
                }
        }
        return true;
    }

    public long IndexPlanningWatermark(string rootId) => InStore(() => Convert.ToInt64(StoreSchema.Scalar(_connection, null,
        "SELECT COALESCE(MAX(version),0) FROM planning_deferred WHERE root_no=(SELECT root_no FROM root WHERE root_id=$id);",
        ("$id", rootId))));
}
