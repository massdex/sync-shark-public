using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void WalkCleanupRestartChecks(string root)
    {
        var folder = Fresh(root, "walk-cleanup-restart");
        var definition = new RootDef("walk-root", SyncProviders.Local, null, folder, "root-identity");
        var start = new NtfsUsnCheckpoint(1, 0, 100, 0);
        var later = new NtfsUsnCheckpoint(1, 0, 200, 0);
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
        {
            store.BeginLocalWalk(definition, "options", start);
            store.SaveLocalWalkPages(definition.RootId, Enumerable.Range(0, Fast.StoreWalkCleanupRows * 2)
                .Select(n => (n, "[]")).ToArray());
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath(folder) }.ToString()))
        {
            connection.Open();
            using var tx = connection.BeginTransaction();
            // Persist the exact first-page state, then reopen without finishing reclamation.
            StoreSchema.Execute(connection, tx, "UPDATE local_walk SET binding='' WHERE root_id=$r;", ("$r", definition.RootId));
            StoreSchema.Execute(connection, tx, SqliteStateStore.LocalWalkCleanupPageSql,
                ("$r", definition.RootId), ("$n", Fast.StoreWalkCleanupRows));
            tx.Commit();
        }
        using var reopened = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        Require(reopened.HasLocalWalk(definition.RootId), "interrupted cleanup remains an unfinished full walk");
        Require(reopened.BeginLocalWalk(definition, "options", later) == later.Cursor &&
            reopened.LocalWalkPageCount(definition.RootId) == 0 && reopened.Cursor(definition.RootId) is null,
            "restart reclaims the remainder before publishing a fresh walk boundary, never a completed cursor");
        reopened.SaveLocalWalkPages(definition.RootId, [(0, "[]")]);
        Require(reopened.BeginLocalWalk(definition, "options", later) == later.Cursor &&
            reopened.LocalWalkPageCount(definition.RootId) == 1, "valid checkpoints still resume unchanged");
    }

    private static async Task WalkCleanupChecks(string root)
    {
        WalkCleanupRestartChecks(root);
        var folder = Fresh(root, "walk-cleanup");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        var count = Fast.StoreWalkCleanupRows * 3;
        var definition = new RootDef("walk-root", SyncProviders.Local, null, folder, "root-identity");
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 100, 0);
        store.BeginLocalWalk(definition, "options", checkpoint);
        store.SaveLocalWalkPages(definition.RootId, Enumerable.Range(0, count).Select(n => (n, "[]")).ToArray());
        store.SaveLocalWalkPages("other-root", [(0, "[]")]);
        var gate = (FairStoreGate)typeof(SqliteStateStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var connection = (SqliteConnection)typeof(SqliteStateStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        Task cleanup;
        Task observer;
        var limit = TimeSpan.FromSeconds(5);
        using (gate.Enter())
        {
            cleanup = Task.Factory.StartNew(() => store.ForgetLocalWalk(definition.RootId),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Require(SpinWait.SpinUntil(() => gate.Waiting == 1, limit), "cleanup queues first");
            observer = Task.Factory.StartNew(() =>
            {
                using (gate.Enter())
                {
                    var remaining = Convert.ToInt64(StoreSchema.Scalar(connection, null,
                        "SELECT COUNT(*) FROM local_walk_page WHERE root_id=$r AND ordinal>=0;", ("$r", definition.RootId)));
                    Require(remaining == count - Fast.StoreWalkCleanupRows,
                        "FIFO admits another store user after exactly one cleanup page");
                    Require(store.HasLocalWalk(definition.RootId), "partial cleanup retains unfinished-walk protection");
                    Require((string?)StoreSchema.Scalar(connection, null,
                        "SELECT binding FROM local_walk WHERE root_id=$r;", ("$r", definition.RootId)) == string.Empty,
                        "partial cleanup invalidates legacy checkpoint binding atomically");
                    try { store.LocalWalkPageCount(definition.RootId); throw new InvalidOperationException("truncated replay accepted"); }
                    catch (LocalWalkReplayInvalidException) { }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Require(SpinWait.SpinUntil(() => gate.Waiting == 2, limit), "observer queues behind one cleanup page");
        }
        await Task.WhenAll(cleanup, observer).WaitAsync(TimeSpan.FromSeconds(30));
        Require(!store.HasLocalWalk(definition.RootId) && store.LocalWalkPageCount(definition.RootId) == 0 &&
            store.LocalWalkPageCount("other-root") == 1, "cleanup removes only its checkpoint and payloads");
        store.ForgetLocalWalk(definition.RootId);
        using var plan = StoreSchema.Command(connection, null, "EXPLAIN QUERY PLAN " + SqliteStateStore.LocalWalkCleanupPageSql);
        plan.Parameters.AddWithValue("$r", definition.RootId);
        plan.Parameters.AddWithValue("$n", Fast.StoreWalkCleanupRows);
        using var rows = plan.ExecuteReader();
        while (rows.Read())
            Require(!rows.GetString(3).Contains("SCAN ") && !rows.GetString(3).Contains("TEMP B-TREE"),
                "cleanup seeks the existing root ordinal primary key without scans or sorting");
    }
}
