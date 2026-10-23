using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void IntentHorizonMigrationChecks(string root)
    {
        var folder = Fresh(root, "intent-horizon-migration");
        using (var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System))
        {
            store.SavePair(Pair("p1"));
            foreach (var id in new[] { "first", "second", "third", "last" })
                store.RecordIntent("p1", new(id, 18, ActionKind.CopyNew, Side.B, id, null, null, null, false, null));
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
        connection.Open();
        var indexes = Indexes();
        // Construct the exact populated v23 pending-effect shape, retaining all indexes.
        using (var tx = connection.BeginTransaction())
        {
            StoreSchema.Execute(connection, tx, """
                CREATE TABLE pending_effect_v23(action_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL,
                  action_json TEXT NOT NULL, state TEXT NOT NULL, attempts INTEGER NOT NULL, updated INTEGER NOT NULL);
                INSERT INTO pending_effect_v23(rowid,action_id,pair_no,action_json,state,attempts,updated)
                  SELECT rowid,action_id,pair_no,action_json,state,attempts,updated FROM pending_effect;
                DROP TABLE pending_effect;
                ALTER TABLE pending_effect_v23 RENAME TO pending_effect;
                UPDATE pending_effect SET state=CASE action_id WHEN 'first' THEN 'started' WHEN 'second' THEN 'held'
                  WHEN 'third' THEN 'failed' ELSE 'uncertain' END, attempts=rowid+2;
                PRAGMA user_version=23;
                """);
            foreach (var index in indexes) StoreSchema.Execute(connection, tx, index);
            tx.Commit();
        }
        var before = Rows();
        StoreSchema.Verify(connection);
        Require(Rows().SequenceEqual(before) && Indexes().SequenceEqual(indexes),
            "v24 migration preserves every populated intent field, rowid and explicit index");
        StoreSchema.Verify(connection);
        Require(Rows().SequenceEqual(before), "v24 migration is idempotent");
        var table = new IntentTable(connection);
        var pairNo = Convert.ToInt64(StoreSchema.Scalar(connection, null, "SELECT pair_no FROM pair WHERE pair_id='p1';"));
        var horizon = table.UnresolvedHorizon(pairNo);
        StoreSchema.Execute(connection, null, "DELETE FROM pending_effect;");
        using (var tx = connection.BeginTransaction())
        {
            table.Record(tx, pairNo, new("new", 18, ActionKind.CopyNew, Side.B, "New", null, null, null, false, null), 123);
            tx.Commit();
        }
        Require(table.UnresolvedRowsPage(pairNo, 0, horizon, 32).Items.Count == 0,
            "a post-horizon insert cannot reuse a deleted rowid and enter the old stream");
        Require(table.UnresolvedHorizon(pairNo) > horizon && table.Unresolved(pairNo).Single().ActionId == "new",
            "the new intent remains available to the next stream");

        string[] Indexes()
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name='pending_effect' AND sql IS NOT NULL ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            var result = new List<string>();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result.ToArray();
        }
        string[] Rows()
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT json_array(rowid,action_id,pair_no,action_json,state,attempts,updated) FROM pending_effect ORDER BY rowid;";
            using var reader = cmd.ExecuteReader();
            var result = new List<string>();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result.ToArray();
        }
    }
}
