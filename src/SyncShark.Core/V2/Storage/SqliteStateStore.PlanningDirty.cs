namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private static string PreservePlanningCursors(string definition, string prior = "def_json") =>
        $"json_set({definition},'$.PlanningSettleCursor',json_extract({prior},'$.PlanningSettleCursor'))";

    // Run observation handoff uses the same entity-owned queue as indexing. Publish before
    // advancing the scan cursor; each bounded batch is durable even if a later batch fails.
    internal void RetainPlanningObservations(string rootId, IEnumerable<ItemState> items,
        CancellationToken ct, PairDef? observationPair = null)
    {
        foreach (var batch in items.Chunk(256))
        {
            ct.ThrowIfCancellationRequested();
            InStore(() =>
            {
                var root = RootNo(rootId);
                if (root is null) return;
                using var tx = _connection.BeginTransaction();
                RetainPlanningObservations(tx, root.Value, batch, incremental: false, observationPair);
                tx.Commit();
            });
        }
    }

    private void RetainIncrementalPlanning(Microsoft.Data.Sqlite.SqliteTransaction tx, long root,
        IReadOnlyList<ItemState> items) => RetainPlanningObservations(tx, root, items, incremental: true);

    // Single admission owner; explicit run observations also retain work before first-sync completion.
    private void RetainPlanningObservations(Microsoft.Data.Sqlite.SqliteTransaction tx, long root,
        IReadOnlyList<ItemState> items, bool incremental, PairDef? observationPair = null)
    {
        if (items.Count == 0) return;
        foreach (var (number, side) in RootSides(root, tx))
        {
            // Classify fresh observations before admission; never retire existing debt from cached flags.
            var json = StoreSchema.Scalar(_connection, tx,
                "SELECT def_json FROM pair WHERE pair_no=$p AND ($incremental=0 OR first_sync_done=1);",
                ("$p", number), ("$incremental", incremental ? 1 : 0)) as string;
            if (json is null) continue;
            var pair = Deserialize(json);
            // Retain precisely the same reviewed-removal observation scope that the scan used, not effect authority.
            if (observationPair?.PairId == pair.PairId) pair = observationPair;
            var eligibility = new ItemEligibility([pair], []);
            var admitted = items.Where(item => PlanningObservationEligible(pair, side, item, eligibility))
                .Select(item => new { root, id = item.ObjectId, path = item.Path }).ToArray();
            if (admitted.Length > 0)
                WritePlanningDirty(tx, number, System.Text.Json.JsonSerializer.Serialize(admitted), ObservationPlanningReason);
        }
    }

    private static bool PlanningObservationEligible(PairDef pair, Side side, ItemState item,
        ItemEligibility eligibility) => eligibility.Evaluate(pair, side, item, ItemEligibility.Purpose.Plan).Eligible;

    public long IndexPlanningWatermark(string rootId) => InStore(() => Convert.ToInt64(StoreSchema.Scalar(_connection, null,
        "SELECT COALESCE(MAX(version),0) FROM planning_deferred WHERE root_no=(SELECT root_no FROM root WHERE root_id=$id);",
        ("$id", rootId))));
}
