using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static async Task WalkRetirementChecks(string root)
    {
        var folder = Fresh(root, "walk-retirement");
        using var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        var definition = new RootDef("walk-root", SyncProviders.Local, null, folder, "root-identity");
        var start = new NtfsUsnCheckpoint(1, 0, 100, 0);
        var later = new NtfsUsnCheckpoint(1, 0, 200, 0);
        var count = Fast.StoreWalkReclaimRows * 2 + 1;
        store.BeginLocalWalk(definition, "options", start);
        store.SaveLocalWalkPages(definition.RootId, Enumerable.Range(0, count).Select(n => (n, "[]")).ToArray());
        store.SaveLocalWalkPages("other-root", [(0, "[]")]);
        var gate = store.LocalWalkGate(definition.RootId);
        await gate.WaitAsync();
        Task drain;
        try
        {
            drain = store.RetireLocalWalk(definition.RootId);
            Require(!drain.IsCompleted && store.LocalWalkPageCount(definition.RootId) == count,
                "retirement returns without waiting for any payload deletion");
            var connection = (SqliteConnection)typeof(SqliteStateStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            var epoch = (string)StoreSchema.Scalar(connection, null,
                "SELECT cursor FROM local_walk WHERE root_id=$r;", ("$r", definition.RootId))!;
            Require(store.ReclaimRetiredWalkPage(definition.RootId, epoch), "matching retired epoch accepts a page");
            var remaining = Convert.ToInt64(StoreSchema.Scalar(connection, null,
                "SELECT COUNT(*) FROM local_walk_page WHERE root_id=$r AND ordinal>=0;", ("$r", definition.RootId)));
            Require(remaining < count && count - remaining <= Fast.StoreWalkReclaimRows,
                "background delete makes progress within its row bound and may yield early at the time budget");
            Require(store.BeginLocalWalk(definition, "options", later) == later.Cursor,
                "new walk drains pending retirement before installing its boundary");
            store.SaveLocalWalkPages(definition.RootId, [(0, "new-payload")]);
            Require(!store.ReclaimRetiredWalkPage(definition.RootId, epoch) &&
                store.LocalWalkPage(definition.RootId, 0) == "new-payload" && store.LocalWalkPageCount("other-root") == 1,
                "stale epoch cannot delete newer checkpoint payloads or another root");
        }
        finally { gate.Release(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Require(store.LocalWalkPage(definition.RootId, 0) == "new-payload", "queued old worker also respects the new generation");
        await WalkRetirementShutdownChecks(root);
        await WalkRetirementCompletionChecks(root);
    }

    private static async Task WalkRetirementCompletionChecks(string root)
    {
        var folder = Fresh(root, "walk-retirement-completion");
        var settings = Fast with { StoreWalkReclaimSlice = TimeSpan.Zero };
        using var store = SqliteStateStore.Open(folder, settings, new TestClock(), LocalItems.CaptureOptionalHash);
        var definition = new RootDef("walk-root", SyncProviders.Local, null, folder, "root-identity");
        store.BeginLocalWalk(definition, "options", new NtfsUsnCheckpoint(1, 0, 100, 0));
        store.SaveLocalWalkPages(definition.RootId, [(0, "[]"), (1, "[]"), (2, "[]")]);
        var connection = (SqliteConnection)typeof(SqliteStateStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        string Epoch() => (string)StoreSchema.Scalar(connection, null,
            "SELECT cursor FROM local_walk WHERE root_id=$r;", ("$r", definition.RootId))!;
        var gate = store.LocalWalkGate(definition.RootId);
        await gate.WaitAsync();
        Task drain;
        try
        {
            drain = store.RetireLocalWalk(definition.RootId);
            var oldEpoch = Epoch();
            var coalesced = store.RetireLocalWalk(definition.RootId);
            Require(ReferenceEquals(drain, coalesced) && Epoch() != oldEpoch,
                "second pending retirement coalesces into one worker with the latest epoch");
            Require(!store.ReclaimRetiredWalkPage(definition.RootId, oldEpoch), "superseded retirement cannot delete payloads");
            Require(store.ReclaimRetiredWalkPage(definition.RootId, Epoch()) &&
                store.LocalWalkPage(definition.RootId, 0) is null && store.LocalWalkPage(definition.RootId, 1) is not null,
                "expired slice yields after one indivisible payload instead of consuming the row allowance");
        }
        finally { gate.Release(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Require(store.LocalWalkPageCount(definition.RootId) == 0 && store.LocalWalkPage(definition.RootId, -1) is null &&
            Convert.ToInt64(StoreSchema.Scalar(connection, null,
                "SELECT COUNT(*) FROM local_walk WHERE root_id=$r;", ("$r", definition.RootId))) == 0,
            "completed worker removes every payload, count marker and retired header");
    }

    private static async Task WalkRetirementShutdownChecks(string root)
    {
        var folder = Fresh(root, "walk-retirement-shutdown");
        var definition = new RootDef("walk-root", SyncProviders.Local, null, folder, "root-identity");
        var start = new NtfsUsnCheckpoint(1, 0, 100, 0);
        var later = new NtfsUsnCheckpoint(1, 0, 200, 0);
        var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        store.BeginLocalWalk(definition, "options", start);
        store.SaveLocalWalkPages(definition.RootId, [(0, "[]")]);
        var gate = store.LocalWalkGate(definition.RootId);
        await gate.WaitAsync();
        try
        {
            var drain = store.RetireLocalWalk(definition.RootId);
            store.Dispose(); // cancellation must release a root-gate waiter without touching the closed connection
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { gate.Release(); store.Dispose(); }
        using var reopened = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        Require(reopened.LocalWalkPageCount(definition.RootId) == 1 && !reopened.HasLocalWalk(definition.RootId),
            "shutdown retains retired payloads without presenting a resumable walk");
        Require(reopened.BeginLocalWalk(definition, "options", later) == later.Cursor &&
            reopened.LocalWalkPageCount(definition.RootId) == 0 && reopened.Cursor(definition.RootId) is null,
            "reopen drains retirement before a fresh checkpoint, never advances the completed inventory cursor");
    }
}
