using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Operation ownership, real SQLite isolation, live facts and the stale issue evidence gate.</summary>
public static class ScanPartitionChecks
{
    private const string PairId = "a";
    private const string OtherPair = "b";
    private const string Child = "Parent/Child";
    private static readonly PairConfig Pair = CompactStorageChecks.Pair(PairId);
    private static readonly RowContext Context = RowContext.Neutral;

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await OwnershipAsync();
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var wholeFirst in new[] { false, true })
                await PartitionsAsync(Path.Combine(folder, wholeFirst ? "first.db" : "last.db"), wholeFirst);
            await IssuesAsync(Path.Combine(folder, "issues.db"));
            await AbsentEpisodeAsync(folder);
            await LiveFactsAsync(Path.Combine(folder, "facts.db"));
            await WalAsync(Path.Combine(folder, "wal.db"));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task OwnershipAsync()
    {
        var owner = new ScanOwnership();
        var whole = await owner.EnterAsync([PairId], () => true, default);
        var waiting = owner.EnterAsync([PairId], () => true, default).AsTask();
        Check.Require(!waiting.IsCompleted, "another whole Repair waits for this exact pair");
        await using var scoped = owner.GrantScoped(PairId, () => true, default);
        await using var overlapping = owner.GrantScoped(PairId, () => true, default);
        await using var other = await owner.EnterAsync([OtherPair], () => true, default);
        scoped.Require(owner, PairId);
        Throws<InvalidOperationException>(() => scoped.Require(new ScanOwnership(), PairId));
        Throws<InvalidOperationException>(() => scoped.Require(owner, OtherPair));
        Check.Require(new[] { whole.ScanId, scoped.ScanId, overlapping.ScanId, other.ScanId }.Distinct().Count() == 4,
            "whole and overlapping scoped grants have independent operation tokens");
        await whole.DisposeAsync();
        Throws<InvalidOperationException>(() => whole.Require(owner, PairId));
        await using var next = await waiting;
        Throws<LiveAdmissionStaleException>(() => owner.GrantScoped(PairId, () => false, default));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Throws<OperationCanceledException>(() => owner.GrantScoped(PairId, () => true, cancel.Token));
    }

    private static Entry Entry(string path, string id, bool folder = false) =>
        new(path, folder, 1, DateTimeOffset.UnixEpoch, id, "revision", "", null);

    private static async Task Put(StateStore store, Guid token, Side side, params Entry[] entries)
    {
        var sink = store.OpenScan(PairId, token, side, ScanTestSettings.BatchRows);
        foreach (var entry in entries) await sink.AddAsync(new Listed(entry.Path, entry), default);
        await sink.CompleteAsync();
    }

    private static async Task PartitionsAsync(string path, bool wholeFirst)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair);
        var whole = Guid.NewGuid();
        var scoped = Guid.NewGuid();
        var overlap = Guid.NewGuid();
        if (wholeFirst) store.BeginScan(PairId, whole, 0, true);
        store.BeginScan(PairId, scoped, 0, false, "Parent");
        if (!wholeFirst) store.BeginScan(PairId, whole, 0, true);
        store.BeginScan(PairId, overlap, 0, false, "Parent");
        var saved = CompactStorageChecks.Saved("Parent/Before", "whole");
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        await Task.WhenAll(Put(store, whole, Side.Local, Entry(Child, "whole")),
            Put(store, scoped, Side.Local, Entry(Child, "scoped")),
            Put(store, overlap, Side.Local, Entry(Child, "overlap")));
        foreach (var (token, id) in new[] { (whole, "whole"), (scoped, "scoped"), (overlap, "overlap") })
        {
            Check.Require(store.ScanEntry(PairId, token, Side.Local, PathNames.Key(Child))?.Id == id &&
                store.InScan(PairId, token, Side.Local, PathNames.Key(Child)) &&
                store.ScanRows(PairId, token, _ => Context).Single(row => row.Local is not null).Local!.Id == id,
                "same-key lookups and row unions remain operation-specific");
            Check.Require(store.ScanRenames(PairId, token).Count == (token == whole ? 1 : 0),
                "rename candidates cannot borrow another partition's observation");
        }
        var deleted = store.OpenScan(PairId, whole, Side.Dropbox, ScanTestSettings.BatchRows);
        await deleted.AddAsync(new Listed("Parent/Gone", null), default);
        await deleted.CompleteAsync();
        Check.Require(store.DeletedNameExact(PairId, whole, "PARENT/GONE") &&
            !store.HasDeletedNames(PairId, scoped) && !store.DeletedNameExact(PairId, scoped, "PARENT/GONE"),
            "only discovery Repair owns deleted-name membership in either begin order");
        Throws<InvalidOperationException>(() => store.BeginScan(PairId, Guid.NewGuid(), 0, true, "Parent"));
        Throws<InvalidOperationException>(() => store.ReplaceSkipped(PairId, scoped, []));
        using (var iterator = store.ScanRows(PairId, scoped, _ => Context).GetEnumerator())
        {
            Check.Require(iterator.MoveNext(), "reader starts");
            await ThrowsAsync<InvalidOperationException>(() => store.EndScanAsync(PairId, scoped, 1));
            Check.Require(store.ReadScan(PairId, scoped).Token == scoped, "failed early end preserves borrower authority");
        }
        var aborted = store.OpenScan(PairId, overlap, Side.Local, ScanTestSettings.BatchRows);
        await aborted.AbortAsync();
        Throws<LiveAdmissionStaleException>(() => store.ReadScan(PairId, overlap));
        await store.EndScanAsync(PairId, overlap, 1);
        await store.EndScanAsync(PairId, scoped, 1);
        Check.Require(store.ScanEntry(PairId, whole, Side.Local, PathNames.Key(Child))?.Id == "whole" &&
            store.DeletedNameExact(PairId, whole, "PARENT/GONE"), "nonlast cleanup preserves sibling rows and history");
        var parent = Entry("Parent", "parent", true);
        var row = Row.ForOneSide(Side.Local, "PARENT", parent, null, [], null, Context);
        using var plan1 = store.OpenFolderPlan(PairId, row, Side.Local);
        using var plan2 = store.OpenFolderPlan(PairId, row, Side.Local);
        await plan1.CaptureAsync(Listed(Entry(Child, "plan-1")), default);
        await plan2.CaptureAsync(Listed(Entry(Child, "plan-2")), default);
        await store.EndScanAsync(PairId, whole, 1);
        Check.Require(plan1.Entries(default).Single().Id == "plan-1" &&
            plan2.Entries(default).Single().Id == "plan-2", "last scan cleanup cannot delete either independent plan");
        Throws<LiveAdmissionStaleException>(() => store.ScanEntry(PairId, whole, Side.Local, PathNames.Key(Child)));
        Check.Require(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + StateSchema.ScanFileSuffix + "*").Any(),
            "last owner removes only its exact pair scan and WAL/SHM files");
        var stale = store.BeginScan(PairId, Guid.NewGuid(), 0, false);
        store.SavePairDecision(Pair, 0, Pair with { Enabled = false }, null, false, null);
        Throws<LiveAdmissionStaleException>(() => store.OpenScan(PairId, stale, Side.Local, ScanTestSettings.BatchRows));
        await store.EndScanAsync(PairId, stale, 1);
    }

    private static async IAsyncEnumerable<Listed> Listed(Entry entry)
    {
        yield return new Listed(entry.Path, entry);
        await Task.CompletedTask;
    }

    private static StateChange Raise(string path, string id, bool firstRun = false) => StateChange.Empty with
    {
        Raise = [(IssueKind.Failure, TextKey.FailChanged, path, new IssueSubject(
            [new IssueItem(Side.Local, path, id)], null, FirstRunDeletion: firstRun
                ? IssueSubject.FirstRunDeletionKind.Item : IssueSubject.FirstRunDeletionKind.None))]
    };

    private static async Task IssuesAsync(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair);
        store.Commit(PairId, Raise("Old", "old"));
        store.Commit(PairId, Raise("Updated", "old", true));
        var token = store.BeginScan(PairId, Guid.NewGuid(), 0, false);
        var old = store.Issues(PairId).Single(issue => issue.Path == "Old");
        var updated = store.Issues(PairId).Single(issue => issue.Path == "Updated");
        store.Commit(PairId, Raise("Updated", "new", true));
        store.Commit(PairId, Raise("New", "new", true));
        Check.Require(store.CloseScanIssue(PairId, token, old), "S-G2 unchanged pre-list issue may close");
        Check.Require(!store.CloseScanIssue(PairId, token, updated), "S-G2 stale expected issue cannot close its replacement");
        foreach (var issue in store.Issues(PairId))
            Check.Require(!store.CloseScanIssue(PairId, token, issue), "S-G2 older listing cannot close newer issue evidence");
        Check.Require(store.RetainedFirstRun(PairId).Count == 2, "S-G2 newer first-run responsibility remains retained");
        await store.EndScanAsync(PairId, token, 1);
        token = store.BeginScan(PairId, Guid.NewGuid(), 0, false);
        foreach (var issue in store.Issues(PairId))
            Check.Require(store.CloseScanIssue(PairId, token, issue), "a later owned listing can settle its own unchanged first-run subject");
        Check.Require(store.RetainedFirstRun(PairId).Count == 0, "acknowledgment and exact closure commit together");
        await store.EndScanAsync(PairId, token, 1);
    }

    private static async Task AbsentEpisodeAsync(string folder)
    {
        foreach (var memory in new[] { false, true })
        foreach (var firstRun in new[] { false, true })
        foreach (var active in new[] { false, true })
        {
            var attempt = Guid.NewGuid();
            using IStateStore store = memory ? new InMemoryStateStore() : StateStore.Open(
                Path.Combine(folder, Path.GetRandomFileName()), (id, _, _, _, _) => id == attempt || active);
            var pair = SourceFixture.Bound(Pair);
            store.SavePair(pair);
            var subject = new IssueSubject([new IssueItem(Side.Local, Child, "old-id")], null);
            if (firstRun) store.Commit(PairId, Raise(Child, "old-id", true));
            var episode = store.RecordInUseFailure(PairId, 0, 0, Child, subject, TextKey.FailLocalInUse,
                null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
            var expected = store.Issues(PairId).Single();
            var scan = store.BeginScan(PairId, Guid.NewGuid(), 0, false);
            var closed = store.CloseScanIssue(PairId, scan, expected);
            var retained = memory || firstRun || active;
            Check.Require(closed == !retained, "B222 scan closes only an unretained real-store episode");
            Check.Require((store.ReadItemRetryState(PairId, [episode.Token.IssueId])[episode.Token.IssueId] is not null) == retained,
                "B222 scan episode and notice settle together");
            Check.Require(store.Issues(PairId).Count == (retained ? 1 : 0), "B222 scan retained notice survives");
            Check.Require(store.RetainedFirstRun(PairId).Count == (firstRun ? 1 : 0),
                "B222 S3 refused scan close preserves first-run provenance");
            if (!retained) Check.Require(store.CountWork(PairId, 0, store.ReadWorkWatermark()) == (0, 0), "B222 scan source-less work settles atomically");
            await store.EndScanAsync(PairId, scan, 1);
        }
    }

    private static async Task LiveFactsAsync(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair);
        var whole = store.BeginScan(PairId, Guid.NewGuid(), 0, false);
        var scoped = store.BeginScan(PairId, Guid.NewGuid(), 0, false, "Parent");
        var outside = store.BeginScan(PairId, Guid.NewGuid(), 0, false, "Sibling");
        var subject = new IssueSubject([new IssueItem(Side.Local, "Parent", "parent")], null);
        store.ObserveSkipped(PairId, 0, Side.Local, "Parent", (TextKey.SkipLink, subject));
        using var inspect = CompactStorageChecks.Inspect(path);
        var scanPath = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + StateSchema.ScanFileSuffix + "*" + StateSchema.DatabaseExtension).Single();
        using var scan = CompactStorageChecks.Inspect(scanPath);
        Check.Require(Number(scan, "SELECT count(*) FROM skip_touched;") == 2,
            "shared skip observations fan out only to covering partitions, including the exact scope root");
        Execute(inspect, "CREATE TRIGGER reject_issue BEFORE INSERT ON issue BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        Throws<SqliteException>(() => store.ObserveSkipped(PairId, 0, Side.Local, Child,
            (TextKey.SkipLink, subject with { Items = [new IssueItem(Side.Local, Child, "child")] })));
        Check.Require(Number(scan, "SELECT count(*) FROM skip_touched;") == 2,
            "skip touch and issue mutation roll back together");
        var before = CompactStorageChecks.Saved("Parent", "parent") with { IsFolder = true };
        var after = before with { DropboxKey = "MOVED", DropboxPath = "Moved" };
        store.Commit(PairId, StateChange.Empty with { Record = [before] });
        await Put(store, whole, Side.Dropbox, Entry(Child, "whole"));
        await Put(store, scoped, Side.Dropbox, Entry(Child, "scope"));
        var change = StateChange.Empty with { Renames = [new RenameApplied(before, after, Side.Dropbox, "Parent", "Moved")] };
        Throws<SqliteException>(() => store.Commit(PairId, change with { Raise = Raise("Failure", "id").Raise }));
        Check.Require(store.InScan(PairId, whole, Side.Dropbox, PathNames.DropboxKey(Child)) &&
            store.InScan(PairId, scoped, Side.Dropbox, PathNames.DropboxKey(Child)), "rename fanout rolls back with its main-state mutation");
        Execute(inspect, "DROP TRIGGER reject_issue;");
        store.Commit(PairId, change);
        Check.Require(store.InScan(PairId, whole, Side.Dropbox, "MOVED/CHILD") &&
            store.InScan(PairId, scoped, Side.Dropbox, "MOVED/CHILD"), "committed counterpart rename updates every active partition");
        scan.Dispose();
        foreach (var token in new[] { whole, scoped, outside }) await store.EndScanAsync(PairId, token, 1);
    }

    private static async Task WalAsync(string path)
    {
        using var store = StateStore.Open(path);
        store.SavePair(Pair);
        store.SavePair(CompactStorageChecks.Pair(OtherPair));
        var first = store.BeginScan(PairId, Guid.NewGuid(), 0, false);
        var second = store.BeginScan(PairId, Guid.NewGuid(), 0, false, "Parent");
        await Put(store, first, Side.Local, Entry(Child, "first"));
        var scanPath = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + StateSchema.ScanFileSuffix + "*" + StateSchema.DatabaseExtension).Single();
        using (var scan = CompactStorageChecks.Inspect(scanPath))
        {
            using var mode = StorageCommand.Create(scan, null, "PRAGMA journal_mode;");
            Check.Require((string)mode.ExecuteScalar()! == "wal", "real pair scratch uses WAL");
            using var command = StorageCommand.Create(scan, null, "SELECT * FROM scan;");
            using var reader = command.ExecuteReader();
            Check.Require(reader.Read(), "external snapshot pins a scan reader");
            // Watchdog is test-only: deadlock must fail rather than hang the CI runner.
            await Put(store, second, Side.Local, Entry(Child, "second")).WaitAsync(TimeSpan.FromSeconds(10));
            var other = store.BeginScan(OtherPair, Guid.NewGuid(), 0, false);
            var writer = store.OpenScan(OtherPair, other, Side.Local, ScanTestSettings.BatchRows);
            await writer.AddAsync(new Listed(Child, Entry(Child, "other")), default);
            await writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await store.EndScanAsync(OtherPair, other, 1);
            Check.Require(store.ScanEntry(PairId, second, Side.Local, PathNames.Key(Child))?.Id == "second",
                "same-pair writer and another pair progress while a scan reader holds its snapshot");
        }
        await store.EndScanAsync(PairId, first, 1);
        await store.EndScanAsync(PairId, second, 1);
    }

    private static long Number(SqliteConnection connection, string sql)
    {
        using var command = StorageCommand.Create(connection, null, sql);
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = StorageCommand.Create(connection, null, sql);
        command.ExecuteNonQuery();
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
