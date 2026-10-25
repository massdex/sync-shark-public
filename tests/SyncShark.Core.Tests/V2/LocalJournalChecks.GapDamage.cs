namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task GapDamageChecksAsync(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var root = new RootDef("damage-root", SyncProviders.Local, null, "/local", "id:root");
        var remote = new RootDef("damage-remote", SyncProviders.Dropbox, "account", "/remote", "id:remote");
        var pair = new PairDef("damage-pair", "Pair A", root, remote, [], [], true);
        store.SavePair(pair);
        var item = new ItemState("file.txt", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 20), null,
            new string('a', 64), 3, DateTimeOffset.UtcNow, ItemFlags.None);
        var start = new NtfsUsnCheckpoint(1, 0, 200, 0);
        var generation = store.BeginGeneration(root.RootId, "full");
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, generation, start.Cursor, [item], []));
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, start.Cursor, 100, true);
        var epoch = gaps.GapEpoch(root.RootId)!;
        var damage = new IOException("injected classified damage");
        bool IsDamage(Exception error) => ReferenceEquals(error, damage);
        var io = new GapCheckIo(item.Mtime, start.Cursor) { OnProbe = (_, _) => throw damage };
        await using var worker = new LocalGapWorker(gaps, io, IsDamage);
        try { worker.Discover(root, epoch, default); throw new InvalidOperationException("damage swallowed"); }
        catch (IOException error) when (ReferenceEquals(error, damage)) { }
        LocalChecks.Require(gaps.GapEpoch(root.RootId)!.AfterObject == "" && store.NextIndexRetry(root.RootId) is null,
            "classified discovery damage propagates without writing a frontier or retry");
        gaps.GapDiscovered(root.RootId, epoch, item, true);
        io.OnProbe = null;
        io.OnHash = _ => throw damage;
        try { await worker.HashPage(root, epoch, default); throw new InvalidOperationException("damage swallowed"); }
        catch (IOException error) when (ReferenceEquals(error, damage)) { }
        LocalChecks.Require(store.NextIndexRetry(root.RootId) is null && gaps.GapHashPage(root.RootId, epoch, 1).Count == 1,
            "classified hash damage does not write a failed-item retry or retire protection");
        gaps.StageGapHash(root.RootId, epoch, item, new string('b', 64), io.Proof(item, 100));
        var watermark = gaps.GapResultWatermark(root.RootId);
        io.OnProbe = (_, _) => throw damage;
        try
        {
            LocalGapWorker.Promote(gaps, store, io, root, watermark, start, _ => true, _ => { }, default, IsDamage);
            throw new InvalidOperationException("damage swallowed");
        }
        catch (IOException error) when (ReferenceEquals(error, damage)) { }
        LocalChecks.Require(gaps.GapResultWatermark(root.RootId) == watermark && store.NextIndexRetry(root.RootId) is null,
            "classified promotion damage propagates without deferring or recording failure");
        io.OnProbe = (_, _) => throw new IOException("ordinary proof outage");
        LocalGapWorker.Promote(gaps, store, io, root, watermark, start, _ => true, _ => { }, default, IsDamage);
        LocalChecks.Require(gaps.GapResultWatermark(root.RootId) > watermark && store.NextIndexRetry(root.RootId) is not null,
            "ordinary promotion failure retains proof behind the shared retry deadline");
        // Fail the root-failure persistence operation, not the ordinary notice owner fallback.
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(folder, "v2", "state.db"), Pooling = false }.ToString());
        db.Open();
        using (var command = db.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_index_record BEFORE UPDATE OF index_failure_count ON root " +
                "BEGIN SELECT RAISE(ABORT, 'injected record failure'); END;";
            command.ExecuteNonQuery();
        }
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OnOpen = () => { opened.TrySetResult(); throw new IOException("root unavailable"); };
        worker.Start(root);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(root.RootId);
        LocalChecks.Require(store.OpenNotices(pair.PairId).Count(notice => notice.ReasonKey == "run-unavailable") == 1 &&
            gaps.HasGapProtection(root.RootId),
            "RecordIndexFailure outage does not fault the worker and uses one shared failure-owner notice");
    }
}
