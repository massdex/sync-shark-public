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
        List<Func<Task>> cases =
        [
            () => DropboxEchoFifoAsync(1, stagedEdit: true), () => DropboxEchoFifoAsync(10_001, stagedEdit: false),
            DropboxEchoRollbackAsync, DropboxEchoHostRollbackAsync, DropboxEchoResponsibilityAsync, DropboxEchoRenameRemovalAsync
        ];
        foreach (var shape in new[] { "exact", "metadata", "case", "replacement", "kind", "skip", "hash", "new" })
            cases.Add(() => DropboxEchoEntryAsync(shape));
        foreach (var rename in new[] { false, true }) cases.Add(() => DropboxEchoLaterEvidenceAsync(rename));
        foreach (var failedSave in new[] { false, true }) cases.Add(() => DropboxEchoRestartAsync(failedSave));
        foreach (var explicitKey in new[] { false, true })
            foreach (var boundParent in new[] { false, true }) cases.Add(() => DropboxEchoBoundDeletionAsync(explicitKey, boundParent));
        foreach (var overrides in new[] { 10, 100, 1_000, 100_000 })
            foreach (var inherited in new[] { false, true }) cases.Add(() => DropboxEchoOverrideAsync(overrides, inherited));
        var failures = new List<Exception>();
        foreach (var run in cases)
        {
            try { await run(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static void TrustEchoBaseline(Fixture fixture)
    {
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
            [(PairId, Generation, source.Epoch, source.Revision, false)], []);
        TrustEchoSources(fixture);
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
        TrustEchoBaseline(fixture);
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
        const string persisted = "SELECT json_array(rowid,quote(pair_id),quote(local_path),quote(dropbox_key_delta)," +
            "quote(dropbox_path_delta),quote(is_folder),quote(local_id),quote(local_size),quote(local_mtime)," +
            "quote(dropbox_id),quote(dropbox_hash)) FROM synced_entry;";
        var before = ExecuteScalarOnStore(fixture, persisted);
        var changed = shape switch
        {
            "metadata" => remote with { Revision = "different", Modified = Stamp.AddDays(1) },
            "case" => remote with { Path = "ITEM" },
            "replacement" => remote with { Id = "replacement", Modified = Stamp.AddMinutes(1) },
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
        TrustEchoBaseline(fixture);
        var reads = new List<string>();
        var remoteCounter = new CountingSide(new HookedSide(fixture.Dropbox, beforeRead: path => reads.Add(path)));
        fixture.DropboxView = remoteCounter;
        var counters = new StoreCounters(fixture.Store);
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), events, configure: context =>
        {
            ((FakeTimeProvider)context.Time).Advance(Stamp.AddDays(1) - context.Time.GetUtcNow());
            return context with { Store = counters };
        }).SyncWorkAsync(default);
        Console.WriteLine($"B214 entry={shape} settled={counters.EchoesSettled} reads={string.Join(',', reads)} opens={remoteCounter.Opens} " +
            $"issues={string.Join(';', fixture.Inner.Issues(PairId))} events={string.Join(';', events)}");
        var eligible = shape is "exact" or "metadata";
        // Downloads observe in SelectionBranches, then reread in ItemFreshness before the effect; B-214 adds neither read.
        var expectedReads = eligible ? 0 : shape is "replacement" or "new" or "hash" ? 2 : 1;
        Check.Require(counters.EchoesSettled == (eligible ? 1 : 0) &&
            reads.Count(path => PathNames.DropboxKey(path) == PathNames.DropboxKey(changed.Path)) == expectedReads,
            "B214-T12/T13: only exact baseline echoes avoid their fresh metadata read: " + shape);
        if (eligible)
            Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved &&
                Equals(before, ExecuteScalarOnStore(fixture, persisted)),
                "B214-T13: Revision/Modified-only evidence leaves every persisted value and encoded byte unchanged");
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
        Console.WriteLine($"B214 later rename={rename} first={first.AnchorWorkId} later={later.AnchorWorkId} " +
            $"settled={counters.EchoesSettled} begins={string.Join(',', fixture.Hooks.BegunSelections.Select(row => row.AnchorWorkId))} " +
            $"opens={counter.Opens} issues={string.Join(';', fixture.Inner.Issues(PairId))}");
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
        return Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, "Y", "OldY", moved.Id, moved, false, false), page: 2);
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
        var rolledBack = false;
        var observed = new EchoRollbackStore(fixture.Store)
        {
            RolledBack = error => rolledBack = error.Selection.AnchorWorkId == faulted &&
                Enumerable.Range(0, 100).All(index => WorkRows(fixture, first.AnchorWorkId + index) == 1)
        };
        var order = observed.Order;
        var counters = new StoreCounters(observed);
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
                    observed.Record("Failed", faulted);
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

    private static async Task DropboxEchoHostRollbackAsync()
    {
        var folder = Directory.CreateTempSubdirectory("SyncSharkEchoHost-");
        try
        {
            EchoRollbackStore observed = null!;
            var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rolledBack = false;
            var armed = false;
            long first = 0;
            await using var rig = await Massdex.SyncShark.Engine.Tests.Run.RepairChecks.ComposeRigAsync(folder.FullName,
                level: 1, decorate: inner =>
                {
                    observed = new EchoRollbackStore(inner)
                    {
                        RolledBack = error =>
                        {
                            var queue = inner.CountWork("pair-a", error.Selection.PairGeneration, inner.ReadWorkWatermark());
                            Console.WriteLine($"B214 host rollback fault={error.Selection.AnchorWorkId} expected={first + 49} queue={queue}");
                            rolledBack = error.Selection.AnchorWorkId == first + 49 && queue.Dropbox == 201;
                        }
                    };
                    return new HookStore(observed) { OnFolderJobProgress = () => { if (armed) returned.TrySetResult(); } };
                });
            for (var index = 0; index < 201; index++)
            {
                var path = $"X{index:D3}";
                await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, path), "content");
                rig.Wire.SeedFile(path, "content");
            }
            Directory.CreateDirectory(Path.Combine(rig.Pair.LocalRoot, "OldY"));
            rig.Wire.SeedFolder("OldY");
            using (var baseline = rig.Log(folder.FullName, "baseline"))
                Check.Require((await rig.RunAsync(baseline, default)).Completed, "B214-T6c host baseline completes");
            var pairId = rig.Pair.PairId;
            var generation = rig.Store.ReadPairExecution(pairId)!.Value.Generation;
            var remote = rig.Store.ReadSource(pairId, Side.Dropbox)!.Value;
            rig.Wire.SeedChangePage(remote.Cursor!, Enumerable.Range(0, 201)
                .Select(index => rig.Wire.ChangeExistingFile($"X{index:D3}")).ToArray(), false, "echo-host-terminal");
            using var log = rig.Log(folder.FullName, "rollback");
            await rig.Host.DispatchSessionAsync(pairId, () => log, async (session, ct) =>
            {
                Check.Require(session.Settings.StoragePageSize == 100, "B214-T6c host uses the specified 100-row page");
                await rig.Host.CatchUpDropboxAsync(session.Registration, pairId, generation, ct);
                first = rig.Store.ReadWorkWatermark() - 200;
                var old = (await session.Local.ReadOneAsync("OldY", ct))!;
                var moved = await session.Local.MoveAsync(old, "Y", ct);
                var local = rig.Store.ReadSource(pairId, Side.Local)!.Value;
                var domain = rig.Pair.Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var position = rig.Store.ReadJournal(domain)!;
                Check.Require(rig.Store.AcquireJournalPage(domain, position, position,
                    [(pairId, generation, local.Epoch, local.Revision, false)],
                    [(pairId, new ChangeNotice(Side.Local, ChangeKind.Renamed, "Y", "OldY", moved.Id, moved, false, false))]) is not null,
                    "B214-T6c host acquires Y behind the echo");
                return true;
            }, default);
            Console.WriteLine($"B214 host setup queue={rig.Store.CountWork(pairId, generation, rig.Store.ReadWorkWatermark())} " +
                $"local={rig.Store.ReadSource(pairId, Side.Local)} remote={rig.Store.ReadSource(pairId, Side.Dropbox)}");
            var database = Path.Combine(folder.FullName, "composed.db");
            ExecuteDatabase(database, "CREATE TRIGGER echo_host_abort AFTER DELETE ON change_work WHEN OLD.work_id=" +
                (first + 49) + " BEGIN SELECT RAISE(ABORT,'echo-host-fixture'); END;");
            armed = true;
            using var stopped = rig.Host.Stopping.Register(() => failed.TrySetResult());
            var auto = new Massdex.SyncShark.Engine.Run.AutoSync(rig.Store, _ => log, rig.Host);
            auto.Start();
            try
            {
                await Task.WhenAny(returned.Task, failed.Task).WaitAsync(TimeSpan.FromSeconds(30));
                Console.WriteLine($"B214 host returned={returned.Task.IsCompleted} stopping={failed.Task.IsCompleted} " +
                    $"status={rig.Host.Status()} rolledBack={rolledBack} paths={string.Join(',', rig.Wire.Snapshot().Select(item => item.Path))} " +
                    $"issues={string.Join(';', rig.Store.Issues(pairId))}");
                // shortcut: not-Held observes containment indirectly; add a direct hook only if the host exposes one publicly.
                Check.Require(returned.Task.IsCompleted && rig.Host.Status().Status != AutoStatus.Held && rolledBack &&
                    rig.Wire.Snapshot().Any(item => item.Path == "Y"),
                    "B214-T6c: real AutoSync returns normally, host is not Held, atomic rollback preserves work and Y moves");
            }
            finally { await auto.StopAsync(); }
            ExecuteDatabase(database, "DROP TRIGGER echo_host_abort;");
            await rig.Host.DispatchSessionAsync(pairId, () => log, async (session, ct) =>
            {
                await session.Syncer.SyncWorkAsync(ct);
                return true;
            }, default);
            Check.Require(rig.Store.CountWork(pairId, generation, rig.Store.ReadWorkWatermark()) == (0L, 0L),
                "B214-T6c: the real host drains the preserved work after the fault is removed");
        }
        finally { Directory.Delete(folder.FullName, true); }
    }

    private static async Task DropboxEchoRenameRemovalAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var selected = SeedEchoBindings(fixture, 1);
        var saved = fixture.Inner.SyncedById(PairId, Side.Dropbox, selected.Identity!)!;
        var destination = SyncedEntries.From(FolderEntry("Q", "local-q"), FolderEntry("Q", "remote-q"));
        fixture.Local.Seed(FolderEntry("Q", "local-q"), null);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [destination] });
        fixture.Inner.RegisterRenameContinuation(selected, saved, destination, null);
        using var plan = await new FolderPlanBuilder(fixture.Context(selected)).BuildAsync(
            Row.ForOneSide(Side.Local, destination.LocalKey, FolderEntry("Q", "local-q"), destination, [], null,
                RowContext.Neutral), Side.Local, new HashSet<string>(), live: true, default);
        var operation = fixture.Inner.BeginFolderRemoval(PairId, plan, fixture.Settings.StoragePageSize, default, selected);
        fixture.Inner.Commit(PairId, StateChange.Empty with
            { Renames = [new RenameApplied(saved, saved, null, null, null)] }, selected, null);
        Check.Require(fixture.Inner.ReadRenameContinuation(selected) is null &&
            fixture.Inner.RenameRemoval(selected) == operation,
            "B214-V1 setup: completed rename evidence leaves an incomplete removal's work link");
        var move = SeedEchoUnrelatedMove(fixture);
        var counters = new StoreCounters(fixture.Store);
        await LiveSyncer(fixture, selected, new Boundary(_ => new Lease()), [], configure: context => context with
            { Store = counters }).SyncWorkAsync(default);
        Check.Require(counters.EchoesSettled == 0 &&
            fixture.Hooks.BegunSelections.Any(row => row.AnchorWorkId == selected.AnchorWorkId) &&
            fixture.Hooks.BegunSelections.Any(row => row.AnchorWorkId == move.AnchorWorkId) &&
            await fixture.Dropbox.ReadOneAsync("Y", default) is not null,
            "B214-V1: linked removal remains ordinary and cannot stall unrelated pair work at echo settlement");
    }

    private static async Task DropboxEchoResponsibilityAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var selected = SeedEchoBindings(fixture, 1);
        var remote = fixture.Dropbox.FindById(selected.Identity!)!;
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, remote.Path, remote.Id)], Side.Dropbox);
        fixture.Inner.Commit(PairId, StateChange.Empty with
            { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, remote.Path, subject)] });
        var issue = fixture.Inner.Issues(PairId).Single();
        var reads = new List<string>();
        fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: reads.Add);
        var counters = new StoreCounters(fixture.Store);
        await LiveSyncer(fixture, selected, new Boundary(_ => new Lease()), [], configure: context => context with
            { Store = counters }).SyncWorkAsync(default);
        Console.WriteLine($"B214 responsibility settled={counters.EchoesSettled} reads={string.Join(',', reads)} " +
            $"issues={string.Join(';', fixture.Inner.Issues(PairId))}");
        Check.Require(counters.EchoesSettled == 0 && reads.Count(path => path == remote.Path) == 1 &&
            fixture.Inner.Issues(PairId).Any(current => current.Id == issue.Id),
            "B214-T7: responsibility retains the ordinary read and the open NameClash");
    }

    private static async Task DropboxEchoRestartAsync(bool failedSave)
    {
        using var fixture = new Fixture(realStore: true);
        var local = FolderEntry("Old", "local-root");
        var remote = FolderEntry("Old", "remote-root");
        fixture.Local.Seed(local, null);
        fixture.Dropbox.Seed(remote, null);
        var child = LocalEntry("Old/c", "local-c");
        var remoteChild = RemoteEntry("Old/c", "remote-c");
        fixture.Local.Seed(child, [1]);
        fixture.Dropbox.Seed(remoteChild, [1]);
        var saved = SyncedEntries.From(local, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved, SyncedEntries.From(child, remoteChild)] });
        local = await fixture.Local.MoveAsync(local, "New", default);
        remote = await fixture.Dropbox.MoveAsync(remote, "New", default);
        if (failedSave)
            ExecuteDatabase(fixture.DatabasePath!, "CREATE TRIGGER echo_rename_fault BEFORE UPDATE ON synced_entry " +
                "BEGIN SELECT RAISE(ABORT,'rename-fixture'); END;");
        var failed = false;
        try
        {
            fixture.Inner.Commit(PairId, StateChange.Empty with
                { Renames = [new RenameApplied(saved, SyncedEntries.From(local, remote), null, null, null)] });
        }
        catch (Microsoft.Data.Sqlite.SqliteException) when (failedSave) { failed = true; }
        Check.Require(failed == failedSave, "B214-T6 setup: the intended RenameApplied transaction commits or aborts");
        if (failedSave) ExecuteDatabase(fixture.DatabasePath!, "DROP TRIGGER echo_rename_fault;");
        var selected = SeedRemote(fixture, remote,
            new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, "Old", null, null, null, false, false));
        AcquireRemotePage(fixture,
            new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, "Old/c", null, null, null, false, false),
            new ChangeNotice(Side.Dropbox, ChangeKind.Changed, "New", null, remote.Id, remote, false, false),
            new ChangeNotice(Side.Dropbox, ChangeKind.Changed, "New/c", null, remoteChild.Id,
                remoteChild with { Path = "New/c" }, false, false));
        TrustEchoBaseline(fixture);
        fixture.RestartStore();
        var reads = new List<string>();
        var localCounter = new CountingSide(fixture.Local);
        fixture.LocalView = new HookedSide(localCounter);
        fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: reads.Add);
        await LiveSyncer(fixture, selected, new Boundary(_ => new Lease()), [], configure: context => context with
            { Settings = context.Settings with { StoragePageSize = 100 } }).SyncWorkAsync(default);
        Check.Require(localCounter.Deletes == 0 && await fixture.Local.ReadOneAsync("New/c", default) is not null,
            "B214-T6: restart never deletes the moved child, including after an aborted binding save");
        if (failedSave)
            Check.Require(reads.Contains("Old"), "B214-T6b: an old bound tombstone takes the fresh ordinary path");
        else
            Check.Require(reads.Count == 0 &&
                fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0L, 0L),
                "B214-T6a: committed RenameApplied permits read-free echo settlement after reopening");
    }

    private static async Task DropboxEchoBoundDeletionAsync(bool explicitKey, bool boundParent)
    {
        using var fixture = new Fixture(realStore: true);
        var parent = FolderEntry("P", "local-p");
        fixture.Local.Seed(parent, null);
        var local = LocalEntry("P/c", "local-c");
        fixture.Local.Seed(local, [1]);
        var remote = RemoteEntry(explicitKey ? "Remote/c" : "P/c", "remote-c");
        var path = explicitKey ? "Remote" : "P";
        var remoteParent = FolderEntry(path, "remote-p");
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = boundParent ? [SyncedEntries.From(parent, remoteParent), SyncedEntries.From(local, remote)]
                : [SyncedEntries.From(local, remote)]
        });
        var selected = SeedRemote(fixture, remoteParent,
            new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, path, null, null, null, false, false));
        TrustEchoBaseline(fixture);
        var readBeforeDelete = false;
        var reads = new List<string>();
        fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: read =>
        {
            reads.Add(read);
            if (read == path) readBeforeDelete = fixture.Local.FindById(local.Id) is not null;
        });
        var counters = new StoreCounters(fixture.Store);
        await LiveSyncer(fixture, selected, new Boundary(_ => new Lease()), [], configure: context => context with
            { Store = counters, Settings = context.Settings with { StoragePageSize = 100 } }).SyncWorkAsync(default);
        Console.WriteLine($"B214 bound explicit={explicitKey} parent={boundParent} settled={counters.EchoesSettled} reads={string.Join(',', reads)} " +
            $"before={readBeforeDelete} remains={fixture.Local.FindById(local.Id) is not null} issues={string.Join(';', fixture.Inner.Issues(PairId))}");
        if (!boundParent)
        {
            Check.Require(counters.EchoesSettled == 0 && readBeforeDelete &&
                fixture.Hooks.BegunSelections.Any(row => row.AnchorWorkId == selected.AnchorWorkId) &&
                await fixture.Local.ReadOneAsync(local.Path, default) is not null,
                "B214-T5: an unbound parent still takes ordinary work when a descendant is bound: " + explicitKey);
            return;
        }
        Check.Require(counters.EchoesSettled == 0 && reads.Count(read => read == path) == 1 && readBeforeDelete &&
            await fixture.Local.ReadOneAsync(local.Path, default) is null,
            "B214-T5: a bound descendant needs fresh Dropbox absence before Local removal: " + explicitKey);
    }

    private static async Task DropboxEchoOverrideAsync(int overrides, bool inherited)
    {
        using var fixture = new Fixture(realStore: true);
        var saved = Enumerable.Range(0, overrides).Select(index => SyncedEntries.From(
            LocalEntry($"P/{index:D6}", "local-" + index), RemoteEntry($"Q/{index:D6}", "remote-" + index)))
            .Concat(inherited ? [SyncedEntries.From(LocalEntry("P/Z", "local-z"), RemoteEntry("P/Z", "remote-z"))] : []).ToArray();
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = saved });
        var selection = SeedRemote(fixture, FolderEntry("P", "deleted"),
            new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, "P", null, null, null, false, false));
        TrustEchoBaseline(fixture);
        var counter = new CountingSide(fixture.Dropbox);
        fixture.DropboxView = counter;
        var counters = new StoreCounters(fixture.Store);
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [], configure: context => context with
            { Store = counters, Settings = context.Settings with { StoragePageSize = 100 } }).SyncWorkAsync(default);
        var settles = overrides < 100 && !inherited;
        Check.Require(counters.EchoesSettled == (settles ? 1 : 0) && counter.Reads == (settles ? 0 : 1) &&
            (settles || fixture.Hooks.BegunSelections.Any(row => row.AnchorWorkId == selection.AnchorWorkId)),
            $"B214-QP2: only a short exhausted probe settles; bound/undecided gets one ordinary read: {overrides}/{inherited}");
    }
}
