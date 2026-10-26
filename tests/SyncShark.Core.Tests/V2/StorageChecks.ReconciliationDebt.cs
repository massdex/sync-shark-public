using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void ScopedRehashAcknowledgement(string root)
    {
        var folder = Path.Combine(root, "scoped-rehash");
        using var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = Pair("reconciliation-debt");
        store.SavePair(pair);
        ItemState[] rows = [NoHash("selected.txt", "selected-object", 1), NoHash("other.txt", "other-object", 1)];
        store.WriteSnapshot(new(pair.A.RootId, Side.A, store.BeginGeneration(pair.A.RootId, "full"), "", rows, []));
        store.RequireRehashBatch(pair.A.RootId, rows, "fixture proof");
        foreach (var row in rows) Require(store.CompleteRehash(pair.A.RootId, row, new string('a', 64)), "scoped rehash fixture verified");
        var boundary = store.RehashWatermark(pair.A.RootId);
        store.AcknowledgeRehash(pair.A.RootId, boundary, _ => true, objectIds: [rows[0].ObjectId]);
        Require(store.RehashCandidates(pair.A.RootId, 10).Single().ObjectId == rows[1].ObjectId,
            "scoped rehash acknowledgement retires only supplied identities");
        store.AcknowledgeRehash(pair.A.RootId, boundary, _ => true, objectIds: []);
        Require(store.RehashCandidates(pair.A.RootId, 10).Count == 1, "empty scoped acknowledgement never becomes whole-root acknowledgement");
        store.DeferPlanning(pair.PairId, rows.Select(row => (pair.A.RootId, row)));
        store.DeferPlanning(pair.PairId, [(pair.A.RootId, rows[1])], urgent: true);
        var selected = store.DeferredPlanningFor(pair.PairId, pair.A.RootId, [rows[0].ObjectId]);
        Require(selected.Count == 1 && selected[0].ObjectId == rows[0].ObjectId && selected[0].AllAliases &&
            selected[0].Paths.Contains(rows[0].Path), "scoped planning debt materializes aliases only for selected identities");
        Require(store.PlanningBacklogPage(pair.PairId).Single().ObjectId == rows[0].ObjectId,
            "backlog selection excludes urgent user-change debt");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath(folder), Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + SqliteStateStore.RehashAcknowledgementAliasesSql;
        command.Parameters.AddWithValue("$r", 1);
        command.Parameters.AddWithValue("$o", rows[0].ObjectId);
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read()) plan.Add(reader.GetString(3));
        Require(plan.Any(line => line.Contains("fs_obj", StringComparison.Ordinal)), "rehash acknowledgement uses the fs_obj identity index");
    }
}
