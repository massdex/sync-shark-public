using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.V2;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void RunDropboxEchoes() => DropboxEchoesAsync().GetAwaiter().GetResult();

    private static async Task DropboxEchoesAsync()
    {
        foreach (var shape in new[] { "exact", "metadata", "case", "replacement", "kind", "skip", "hash", "new" })
            await DropboxEchoEntryAsync(shape);
        foreach (var rename in new[] { false, true }) await DropboxEchoLaterEvidenceAsync(rename);
        await DropboxEchoFifoAsync(1, stagedEdit: true);
        await DropboxEchoFifoAsync(10_001, stagedEdit: false);
        await DropboxEchoRollbackAsync();
        foreach (var overrides in new[] { 10, 100, 1_000, 100_000 })
            await DropboxEchoOverrideAsync(overrides);
    }

    private static WorkSelection SeedEchoBindings(Fixture fixture, int count)
    {
        fixture.Local.Seed(FolderEntry("X", "local-x"), null);
        fixture.Dropbox.Seed(FolderEntry("X", "remote-x"), null);
        var bindings = new List<SyncedEntry>();
        var notices = new List<ChangeNotice>();
        for (var index = 0; index < count; index++)
        {
            var path = $"X/{index:D5}";
            var local = LocalEntry(path, "local-" + index);
            var remote = RemoteEntry(path, "remote-" + index);
            fixture.Local.Seed(local, [1]);
            fixture.Dropbox.Seed(remote, [1]);
            bindings.Add(SyncedEntries.From(local, remote));
            notices.Add(new(Side.Dropbox, ChangeKind.Changed, path, null, remote.Id, remote, false, false));
        }
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = bindings });
        var (through, epoch, domain) = AcquireRemotePage(fixture, [.. notices]);
        TrustEchoSources(fixture);
        return new(PairId, Generation, epoch, Side.Dropbox, domain, notices[0].Id,
            PathNames.DropboxKey(notices[0].Path), through - count + 1, through);
    }

    private static async Task DropboxEchoEntryAsync(string shape)
    {
        using var fixture = new Fixture(realStore: true);
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Local.Seed(local, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        var saved = SyncedEntries.From(local, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var changed = shape switch
        {
            "metadata" => remote with { Revision = "different", Modified = Stamp.AddDays(1) },
            "case" => remote with { Path = "ITEM" },
            "replacement" => remote with { Id = "replacement" },
            "kind" => remote with { IsFolder = true },
            "skip" => remote with { Skip = "synthetic-skip" },
            "hash" => remote with { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }) },
            "new" => remote with { Id = "new-id", Path = "new-item" },
            _ => remote
        };
        if (shape is "replacement" or "new" or "hash")
        {
            changed = changed with { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }) };
            fixture.Dropbox.Seed(changed, [2]);
        }
        var selection = SeedRemote(fixture, changed);
        TrustEchoSources(fixture);
        var reads = new List<string>();
        var remoteCounter = new CountingSide(new HookedSide(fixture.Dropbox, beforeRead: path => reads.Add(path)));
        fixture.DropboxView = remoteCounter;
        var counters = new StoreCounters(fixture.Store);
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [], configure: context => context with
            { Store = counters }).SyncWorkAsync(default);
        var eligible = shape is "exact" or "metadata";
        Check.Require(counters.EchoesSettled == (eligible ? 1 : 0) &&
            reads.Count(path => PathNames.DropboxKey(path) == PathNames.DropboxKey(changed.Path)) == (eligible ? 0 : 1),
            "B214-T12/T13: only exact baseline echoes avoid their fresh metadata read: " + shape);
        if (eligible)
            Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved,
                "B214-T13: Revision/Modified-only evidence leaves the complete saved record unchanged");
        if (shape is "replacement" or "new" or "hash")
            Check.Require(remoteCounter.Opens == 1 &&
                await fixture.Local.ContentHashAsync((await fixture.Local.ReadOneAsync(changed.Path, default))!, default) ==
                    DropboxContentHash.Compute(new byte[] { 2 }),
                "B214-T4/T12: an unbound or edited entry downloads normally without escaping the opportunity: " + shape);
    }

    private static async Task DropboxEchoLaterEvidenceAsync(bool rename)
    {
        using var fixture = new Fixture(realStore: true);
        var first = SeedEchoBindings(fixture, 1);
        var old = fixture.Dropbox.FindById(first.Identity!)!;
        var next = rename ? await fixture.Dropbox.MoveAsync(old, "Moved", default) :
            old with { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }), Revision = "edit" };
        if (!rename) fixture.Dropbox.Seed(next, [2]);
        var later = SeedRemote(fixture, next);
        var counter = new CountingSide(fixture.Dropbox);
        fixture.DropboxView = counter;
        var counters = new StoreCounters(fixture.Store);
        await LiveSyncer(fixture, first, new Boundary(_ => new Lease()), [], configure: context => context with
            { Store = counters, Settings = context.Settings with { StoragePageSize = 100 } }).SyncWorkAsync(default);
        Check.Require(counters.EchoesSettled == 1 &&
            fixture.Hooks.BegunSelections.Count(row => row.AnchorWorkId == later.AnchorWorkId) == 1,
            "B214-T11: exact-row settlement cannot retire later evidence of the same identity");
        if (rename)
            Check.Require(await fixture.Local.ReadOneAsync("Moved", default) is not null &&
                await fixture.Local.ReadOneAsync(old.Path, default) is null,
                "B214-T11: the later Dropbox rename moves the Local item");
        else
            Check.Require(counter.Opens == 1 &&
                await fixture.Local.ContentHashAsync((await fixture.Local.ReadOneAsync(old.Path, default))!, default) == next.ContentHash,
                "B214-T11: the later Dropbox edit is downloaded exactly once");
    }

    private static WorkSelection SeedEchoUnrelatedMove(Fixture fixture)
    {
        var old = FolderEntry("OldY", "local-y");
        var remote = FolderEntry("OldY", "remote-y");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(old, remote)] });
        fixture.Dropbox.Seed(remote, null);
        var moved = old with { Path = "Y" };
        fixture.Local.Seed(moved, null);
        return Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, "Y", "OldY", moved.Id, moved, false, false));
    }

    private static async Task DropboxEchoFifoAsync(int echoes, bool stagedEdit)
    {
        using var fixture = new Fixture(realStore: true);
        var first = SeedEchoBindings(fixture, echoes);
        WorkSelection? edit = null;
        if (stagedEdit)
        {
            var changed = fixture.Dropbox.FindById(first.Identity!)! with
                { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }), Revision = "edit" };
            fixture.Dropbox.Seed(changed, [2]);
            edit = SeedRemote(fixture, changed);
            var local = LocalEntry("J", "local-j");
            var remote = RemoteEntry("J", "remote-j");
            fixture.Local.Seed(local, [1]);
            fixture.Dropbox.Seed(remote, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            SeedRemote(fixture, remote);
        }
        var move = SeedEchoUnrelatedMove(fixture);
        var order = new List<(string Call, long Work)>();
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            lock (order) order.Add((method.Name, args.FirstOrDefault() is WorkSelection selected ? selected.AnchorWorkId : 0));
        };
        var reads = new List<string>();
        var remoteCounter = new CountingSide(new HookedSide(fixture.Dropbox, beforeRead: path => reads.Add(path)));
        fixture.DropboxView = remoteCounter;
        var counters = new StoreCounters(fixture.Store);
        await LiveSyncer(fixture, first, new Boundary(_ => new Lease()), [], configure: context => context with
            { Store = counters, Settings = context.Settings with { StoragePageSize = 100 } }).SyncWorkAsync(default);
        var y = order.FindIndex(item => item == (nameof(IStateStore.BeginSelection), move.AnchorWorkId));
        var end = order.FindIndex(item => item.Call == nameof(IStateStore.EndWorkPass));
        Check.Require(y >= 0 && y < end && remoteCounter.Moves == 1,
            "B214-T3/W1: unrelated Y moves before the first EndWorkPass");
        if (edit is not null)
        {
            var realEdit = order.FindIndex(item => item == (nameof(IStateStore.BeginSelection), edit.AnchorWorkId));
            Check.Require(realEdit >= 0 && realEdit < y && counters.EchoesSettled == 2 && remoteCounter.Opens == 1,
                "B214-W1: staged-earliest flush preserves the real edit before Y and settles only w1/w3");
        }
        else
            Check.Require(order.Take(y).Count(item => item.Call == nameof(IStateStore.SettleEchoes)) == (echoes + 99) / 100 &&
                reads.All(path => !PathNames.Contains("X", path)) && counters.EchoesSettled == echoes,
                "B214-T3: 10001 echoes settle in exactly 101 calls with no X metadata before Y begins");
        Check.Require(fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0L, 0L),
            "B214-T3/W1: the single opportunity empties the queue");
    }

    private static async Task DropboxEchoRollbackAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var first = SeedEchoBindings(fixture, 201);
        var move = SeedEchoUnrelatedMove(fixture);
        var faulted = first.AnchorWorkId + 49;
        ExecuteDatabase(fixture.DatabasePath!, "CREATE TRIGGER echo_abort AFTER DELETE ON change_work WHEN OLD.work_id=" +
            faulted + " BEGIN SELECT RAISE(ABORT,'echo-fixture'); END;");
        var order = new List<(string Call, long Work)>();
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            lock (order) order.Add((method.Name, args.FirstOrDefault() is WorkSelection selected ? selected.AnchorWorkId : 0));
        };
        var failures = new List<RunEvent>();
        var rolledBack = false;
        var counters = new StoreCounters(fixture.Store);
        var events = new List<RunEvent>();
        ExecutorContext Configure(ExecutorContext context) => context with
        {
            Store = counters,
            Settings = context.Settings with { StoragePageSize = 100 },
            Events = item =>
            {
                lock (order)
                {
                    events.Add(item);
                    if (item.Kind != RunEventKind.Failed || item.WorkId != faulted) return;
                    order.Add(("Failed", faulted));
                    failures.Add(item);
                    if (failures.Count == 1)
                        rolledBack = Enumerable.Range(0, 100).All(index => WorkRows(fixture, first.AnchorWorkId + index) == 1);
                }
            }
        };
        await LiveSyncer(fixture, first, new Boundary(_ => new Lease()), events, configure: Configure).SyncWorkAsync(default);
        var begin = order.FindIndex(item => item == (nameof(IStateStore.BeginSelection), faulted));
        var failed = order.Select((item, index) => (item, index)).Where(row => row.item == ("Failed", faulted))
            .Select(row => row.index).ToArray();
        Check.Require(rolledBack && failed.Length == 2 && failed[0] < begin && failed[1] > begin,
            "B214-T6c: one rollback diagnostic precedes Begin; the separate ordinary failure follows it");
        Check.Require(order.Skip(failed[0] + 1).All(item => item.Call != nameof(IStateStore.SettleEchoes)) &&
            Enumerable.Range(0, 100).All(index => order.Contains((nameof(IStateStore.BeginSelection), first.AnchorWorkId + index))),
            "B214-T6c: no settlement retry in this opportunity, and every rolled-back row begins ordinarily");
        Check.Require(fixture.Inner.Issues(PairId, [IssueKind.Failure]).Count(issue => issue.Cause == TextKey.FailStoreMutation) == 1 &&
            order.Any(item => item == (nameof(IStateStore.BeginSelection), move.AnchorWorkId)) &&
            await fixture.Dropbox.ReadOneAsync("Y", default) is not null,
            "B214-T6c: exactly one durable Failure and unrelated Y completes in the same opportunity");
        ExecuteDatabase(fixture.DatabasePath!, "DROP TRIGGER echo_abort;");
        await LiveSyncer(fixture, first, new Boundary(_ => new Lease()), [], configure: context => context with
            { Settings = context.Settings with { StoragePageSize = 100 } }).SyncWorkAsync(default);
        Check.Require(fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0L, 0L),
            "B214-T6c: after removing the fault the next opportunity empties the queue");
    }

    private static async Task DropboxEchoOverrideAsync(int overrides)
    {
        using var fixture = new Fixture(realStore: true);
        var saved = Enumerable.Range(0, overrides).Select(index => SyncedEntries.From(
            LocalEntry($"P/{index:D6}", "local-" + index), RemoteEntry($"Q/{index:D6}", "remote-" + index))).ToArray();
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = saved });
        var selection = SeedRemote(fixture, FolderEntry("P", "deleted"),
            new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, "P", null, null, null, false, false));
        TrustEchoSources(fixture);
        var counter = new CountingSide(fixture.Dropbox);
        fixture.DropboxView = counter;
        var counters = new StoreCounters(fixture.Store);
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [], configure: context => context with
            { Store = counters, Settings = context.Settings with { StoragePageSize = 100 } }).SyncWorkAsync(default);
        Check.Require(counters.EchoesSettled == (overrides < 100 ? 1 : 0) && counter.Reads == (overrides < 100 ? 0 : 1),
            "B214-QP2: a short exhausted probe settles; a full probe falls back to one ordinary metadata read: " + overrides);
    }
}
