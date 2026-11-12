using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private const string Churn = "churn.health.json";
    private const string ChurnTemp = Churn + ".tmp";
    private const string Fresh = "fresh";
    private const string Moved = "moved";
    // Two sizes: one rewrite is the ordinary cost of the path; six show what each further obsolete identity adds.
    private const int Rewrites = 6;
    // Two queue sizes four times apart: linear work grows 4x between them, quadratic work 16x.
    private static readonly int[] GoneQueueSizes = [40, 160];

    public static void RunGoneIdentity() => GoneIdentityAsync().GetAwaiter().GetResult();

    // B-179: the live churn. A saved file is replaced N times by a temporary file renamed over it, each time with a
    // new local id, faster than AutoSync drains. Every obsolete identity is gone by the time its row runs, so it
    // costs one local by-id read and no provider read; the path costs what one rewrite costs, and unrelated work in
    // the same finite pass is served. Acquisition may or may not have seen each file at its temporary name.
    private static async Task GoneIdentityAsync()
    {
        // First: the churn below also holds identity-deleted rows, so a broken eligibility rule must fail here, at (r).
        await IdentityDeletedOccupiedAsync();
        foreach (var tempSeen in new[] { true, false })
        {
            var single = await ChurnAsync(1, tempSeen);
            var churned = await ChurnAsync(Rewrites, tempSeen);
            var label = tempSeen ? "temporary name seen" : "temporary name never seen";
            Console.WriteLine($"B-179 {label}: one rewrite {single.First} then {single.Second}; " +
                $"{Rewrites} rewrites {churned.First} then {churned.Second}; folder after {churned.FreshPasses} passes");
            // R4-1: a gone row reads its own path once (an unowned occupant there keeps its observation).
            var goneRows = (Rewrites - 1) * (tempSeen ? 2 : 1);
            Check.Require(churned.First.Local == single.First.Local + goneRows &&
                churned.First.Dropbox == single.First.Dropbox,
                $"B-179 (a) {label}: run 1 adds one Local path read per gone row and no Dropbox read: " +
                $"{churned.First} vs {single.First}");
            // Each obsolete identity is asked exactly once. The newest is asked at most once: its own probe skips the
            // by-id read when the pass already bound it (the saved file's deletion row, acquired earlier, observes the
            // path through the ordinary route and syncs it first; SelectionBranches.cs:67, :73).
            Check.Require(churned.ObsoleteOnce && single.ObsoleteOnce &&
                churned.First.Identity == Rewrites - 1 + churned.NewestAsks && single.First.Identity == single.NewestAsks,
                $"B-179 (a) {label}: each obsolete identity costs exactly one local by-id read and the newest at most " +
                $"one: {churned.First} (newest {churned.NewestAsks}) vs {single.First} (newest {single.NewestAsks})");
            Check.Require(churned.Second == single.Second,
                $"B-179 (e) {label}: run 2 costs the same whatever run 1 held: {churned.Second} vs {single.Second}");
            Check.Require(churned.FreshPasses == 0 && churned.FreshCreated,
                $"B-179 (a) {label}: the unrelated new folder's last ensure ran in the first finite pass and the " +
                $"folder exists on Dropbox, bound, with no issue: pass {churned.FreshPasses}, created {churned.FreshCreated}");
            Check.Require(churned.Binding == Churn + "-" + Rewrites && churned.Bytes == Rewrites + 1 &&
                churned.Queued == (0, 0),
                $"B-179 (b) {label}: the newest rewrite syncs and nothing stays queued: {churned.Binding}, " +
                $"{churned.Bytes}, {churned.Queued}");
            Check.Require(churned.Replaced == (Churn + "-new", 0xA0),
                $"B-179 (e) {label}: a different file replacing the synced one syncs on the next run: {churned.Replaced}");
        }
        await PresentBudgetAsync();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var occupied in new[] { false, true })
        {
            var gone = new List<GoneRun>();
            foreach (var size in GoneQueueSizes) gone.Add(await GoneOnlyAsync(size, occupied));
            var label = occupied ? "occupied" : "empty";
            Console.WriteLine($"B-179 (f) gone-only queues {string.Join("/", GoneQueueSizes)}, {label} path: " +
                string.Join("/", gone.Select(run => run.Ms.ToString("F1", invariant))) +
                " ms (fake provider and real store; not a native figure); SQL " + string.Join("/", gone.Select(run => run.Sql)) +
                "; store calls " + string.Join("/", gone.Select(run => run.Store)) + "; reads " +
                string.Join("/", gone.Select(run => run.Reads)));
            // Absolute B-179 SQL per gone row: one binding lookup (SelectSyncedByLocalId, StateStore.cs:315-320) and,
            // when the path holds another file with its own row, one separate-work statement (the first exact path
            // probe answers, ChangeTable.cs:618-620). The occupant's own row adds one binding lookup of its own.
            for (var index = 0; index < GoneQueueSizes.Length; index++)
            {
                var (size, sql) = (GoneQueueSizes[index], gone[index].Sql);
                Check.Require(sql.Probe == size + (occupied ? 1 : 0) && sql.Separate == (occupied ? size : 0),
                    $"B-179 (f) {label}, {size} gone rows: exactly one binding-lookup statement per probe and " +
                    $"{(occupied ? "one" : "no")} separate-work statement per gone row: {sql}");
            }
            Check.Require(gone[1].Reads.Dropbox == gone[0].Reads.Dropbox &&
                gone[1].Reads.Local - gone[0].Reads.Local == GoneQueueSizes[1] - GoneQueueSizes[0],
                $"B-179 (f) {label}: each further gone row adds one path read and no Dropbox read: " +
                $"{gone[0].Reads} vs {gone[1].Reads}");
        }
        await RemoteArrivesAsync();
        await MovedIdentityAsync();
        await OccupiedMoveAsync();
        await GoneOccupiedAsync();
        await SavedDeletionAsync();
        await OldPathMemberAsync();
        await OutOfScopeIdentityAsync();
        await LaterRowAsync();
        await ProbeCancelledAsync();
        await InPlaceRewriteAsync();
        await IdentityReadFailureAsync();
        await PathOnlyDirtyKeptAsync();
        await SavedIdentityNotAskedAsync();
        await StaleBeforeProviderAsync();
    }

    private static async Task<(ChurnReads.Counts First, ChurnReads.Counts Second, int? FreshPasses, bool FreshCreated,
        int? Bytes, string? Binding, (long, long) Queued, (string?, int?) Replaced, bool ObsoleteOnce, int NewestAsks)>
        ChurnAsync(int rewrites, bool tempSeen)
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var target = LocalEntry(Churn, Churn + "-0");
        var remote = RemoteEntry(Churn, DropboxId);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(target, remote)] });
        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        var page = 0L;
        WorkSelection? first = null;
        for (var index = 1; index <= rewrites; index++)
        {
            var created = Replace(fixture, Churn + "-" + index, index == 1 ? target.Id : null, tempSeen, ref page);
            first ??= created;
        }
        var newest = LocalEntry(Churn, Churn + "-" + rewrites);
        fixture.Local.Seed(newest, [(byte)(rewrites + 1)]);
        live[newest.Id] = Churn;
        var fresh = FolderEntry(Fresh, Fresh + "-id");
        fixture.Local.Seed(fresh, null);
        live[fresh.Id] = Fresh;
        Selected(fixture, Fresh, fresh, ++page);
        int? passes = null;
        // The last ensure is the one that created the folder when it exists afterwards.
        var reads = Watch(fixture, live, () => passes = fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)));
        await LiveSyncer(fixture, first!, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        var obsoleteOnce = Enumerable.Range(1, rewrites - 1).All(index => reads.AskedOf(Churn + "-" + index) == 1);
        var newestAsks = reads.AskedOf(newest.Id);
        var firstRun = reads.Take();
        var freshCreated = await fixture.Dropbox.ReadOneAsync(Fresh, default) is { IsFolder: true } &&
            Binding(fixture, Fresh) == fresh.Id && fixture.Inner.Issues(PairId).Count == 0;
        var (bytes, binding) = (await DropboxByte(fixture, Churn), Binding(fixture, Churn));
        var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        // The next run: a different file replaces the now saved newest rewrite in the same way.
        var next = Replace(fixture, Churn + "-new", binding, tempSeen, ref page);
        live.Remove(newest.Id);
        live[Churn + "-new"] = Churn;
        fixture.Local.Seed(LocalEntry(Churn, Churn + "-new"), [0xA0]);
        await LiveSyncer(fixture, next, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        return (firstRun, reads.Take(), passes, freshCreated, bytes, binding, queued,
            (Binding(fixture, Churn), await DropboxByte(fixture, Churn)), obsoleteOnce && newestAsks <= 1, newestAsks);
    }

    private readonly record struct GoneRun(double Ms, int Store, SqlCount Sql, ChurnReads.Counts Reads);

    // SQL statements during one run: all of them, those run inside the probe's binding lookup, and those run inside
    // the separate-work probe's returned delegate.
    private readonly record struct SqlCount(int Total, int Probe, int Separate);

    // Absolute budget at one queue size: temporary files written and gone before AutoSync runs, beside an untouched
    // saved file. Each costs one local by-id read and one path read, no provider read and no change on Dropbox.
    // Occupied, the temporary path holds a new file with its own row, so every gone row also asks the separate-work
    // probe, whose SQL runs inside the returned delegate; the trace counts it where it runs.
    private static async Task<GoneRun> GoneOnlyAsync(int count, bool occupied)
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var target = LocalEntry(Churn, Churn + "-0");
        var remote = RemoteEntry(Churn, DropboxId);
        fixture.Local.Seed(target, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(target, remote)] });
        WorkSelection? first = null;
        for (var index = 1; index <= count; index++)
        {
            var created = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, ChurnTemp, null,
                Churn + "-" + index, LocalEntry(ChurnTemp, Churn + "-" + index), true, false), index);
            first ??= created;
        }
        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        if (occupied)
        {
            var occupant = LocalEntry(ChurnTemp, OccupantId);
            fixture.Local.Seed(occupant, [3]);
            live[OccupantId] = ChurnTemp;
            Selected(fixture, ChurnTemp, occupant, count + 1);
        }
        var reads = Watch(fixture, live);
        var store = fixture.Hooks.Total;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sql = await SqlStatementsAsync(fixture, () =>
            LiveSyncer(fixture, first!, new Boundary(_ => new Lease()), []).SyncWorkAsync(default));
        clock.Stop();
        var run = reads.Take();
        var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        store = fixture.Hooks.Total - store;
        Check.Require((occupied ? run.Identity == count + 1 : run == new ChurnReads.Counts(count, 0, count)) &&
            queued == (0, 0) && await DropboxByte(fixture, Churn) == 1 &&
            (occupied ? await DropboxByte(fixture, ChurnTemp) == 3 && Binding(fixture, ChurnTemp) == OccupantId
                : await fixture.Dropbox.ReadOneAsync(ChurnTemp, default) is null),
            $"B-179 (f) {count} gone temporary identities, occupied {occupied}: one by-id read and one path read each, " +
            $"no Dropbox read of their own: {run}, queued {queued}");
        return new(clock.Elapsed.TotalMilliseconds, store, sql, run);
    }

    // The ordinary present case: a new unsaved file at its own path. With its id the probe is one additional identity
    // probe: one by-id read whose resolution observes the entry once (the real adapter's LocalSide.cs:505 lookup),
    // one binding-lookup statement, and no provider, path or separate-work cost; the same file acquired path-only is
    // never probed. The path's own read at SelectionBranches.cs:113 stays, so the observation is a second one.
    private static async Task PresentBudgetAsync()
    {
        var costs = new List<(ChurnReads.Counts Reads, SqlCount Sql)>();
        foreach (var withId in new[] { true, false })
        {
            using var fixture = new Fixture(realStore: true);
            TrustEchoSources(fixture);
            var file = LocalEntry(Churn, LocalId);
            fixture.Local.Seed(file, [5]);
            var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null,
                withId ? LocalId : null, withId ? file : null, true, false));
            var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal) { [LocalId] = Churn });
            var sql = await SqlStatementsAsync(fixture, () =>
                LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default));
            costs.Add((reads.Take(), sql));
            Check.Require(await DropboxByte(fixture, Churn) == 5 && Binding(fixture, Churn) == LocalId,
                $"B-179 (s) the present unsaved file syncs (acquired {(withId ? "with" : "without")} its id)");
        }
        var (probed, plain) = (costs[0], costs[1]);
        Console.WriteLine($"B-179 (s) present unsaved file: with id {probed.Reads}, {probed.Sql} SQL; path-only " +
            $"{plain.Reads}, {plain.Sql} SQL");
        Check.Require(probed.Reads == plain.Reads with { Identity = 1, Observed = 1 } &&
            plain.Reads.Identity == 0 && plain.Reads.Observed == 0 &&
            probed.Sql is { Probe: 1, Separate: 0 } && plain.Sql is { Probe: 0, Separate: 0 },
            $"B-179 (s) the probe adds exactly one by-id read, one observation inside it and one binding-lookup " +
            $"statement, and no path, Dropbox or separate-work cost: {probed} vs {plain}");
    }

    // Every SQL statement the real store runs during the action, counted where SQLite executes it, so statements
    // inside returned delegates (the separate-work probe) count too. Each is attributed by the synchronous caller
    // on the executing thread: the probe (SelectionBranches.ProbeAsync) or the separate-work delegate as the branch
    // enumerator calls it (SelectionBranches.cs:118). The fakes answer synchronously, so no continuation runs inline.
    private static async Task<SqlCount> SqlStatementsAsync(Fixture fixture, Func<Task> action)
    {
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)fixture.Inner.GetType().GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Inner)!;
        int total = 0, probe = 0, separate = 0;
        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) =>
        {
            Interlocked.Increment(ref total);
            // Reflection names, not stack text: a lambda is <Owner>b__N, an async method's frame is <Name>d__N.MoveNext.
            var frames = new System.Diagnostics.StackTrace(false).GetFrames()
                .Select(frame => frame.GetMethod()).Where(method => method is not null).ToArray();
            bool In(string name) => frames.Any(method =>
                method!.Name.Contains(name, StringComparison.Ordinal) ||
                method.DeclaringType?.Name.Contains(name, StringComparison.Ordinal) == true);
            if (In("<CreateSeparateWorkProbe>b__") && In("EnumerateBranchesAsync")) Interlocked.Increment(ref separate);
            else if (In("<ProbeAsync>")) Interlocked.Increment(ref probe);
        }), null);
        try { await action(); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
        return new(total, probe, separate);
    }

    // A remote file at a path whose only Local row is a gone unsaved identity still arrives, through its own Dropbox
    // row: the gone Local row owes nothing, and the Dropbox source owns the remote change.
    private static async Task RemoteArrivesAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var gone = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, LocalId,
            LocalEntry(Churn, LocalId), true, false));
        var remote = RemoteEntry(Churn, DropboxId);
        fixture.Dropbox.Seed(remote, [1]);
        SeedRemote(fixture, remote);
        var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal));
        await LiveSyncer(fixture, gone, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        var run = reads.Take();
        var arrived = await fixture.Local.ReadOneAsync(Churn, default);
        Check.Require(arrived is not null && run.Identity == 1 &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Churn))?.DropboxId == DropboxId,
            $"B-179 (i) a remote file at a gone identity's path arrives through its own Dropbox row: " +
            $"local {arrived?.Path}, {run}");
    }

    // A moved unsaved identity whose old path another item now holds still syncs where it is: the probe's current
    // location is followed, and the occupant, which has no row of its own, is still observed and uploaded (R4-1).
    private static async Task OccupiedMoveAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        fixture.Local.Seed(LocalEntry(Moved, LocalId), [7]);
        fixture.Local.Seed(LocalEntry(Churn, OccupantId), [8]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, LocalId,
            LocalEntry(Churn, LocalId), true, false));
        var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal)
            { [LocalId] = Moved, [OccupantId] = Churn });
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        var run = reads.Take();
        Check.Require(await DropboxByte(fixture, Moved) == 7 && Binding(fixture, Moved) == LocalId &&
            await DropboxByte(fixture, Churn) == 8 && Binding(fixture, Churn) == OccupantId && run.Identity == 1,
            $"B-179 (j) a moved unsaved identity syncs at its new path and the unqueued occupant of its old path " +
            $"syncs too: {run}, bindings {Binding(fixture, Moved)}, {Binding(fixture, Churn)}");
    }

    // NOT CURED, stated: one identity rewritten in place N times is present, so the absence probe does not apply.
    // Its rows are one identity group, so a pass observes the path once whatever N is; this pins that cost
    // (N=1 and N=6 make the same reads) and the newest bytes.
    private static async Task InPlaceRewriteAsync()
    {
        var costs = new List<ChurnReads.Counts>();
        foreach (var rewrites in new[] { 1, Rewrites })
        {
            using var fixture = new Fixture(realStore: true);
            TrustEchoSources(fixture);
            var saved = LocalEntry(Churn, LocalId);
            var remote = RemoteEntry(Churn, DropboxId);
            fixture.Dropbox.Seed(remote, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
            WorkSelection? first = null;
            for (var page = 1; page <= rewrites; page++)
            {
                var created = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, LocalId,
                    saved, true, false), page);
                first ??= created;
            }
            fixture.Local.Seed(saved, [9]);
            var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal) { [LocalId] = Churn });
            await LiveSyncer(fixture, first!, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
            costs.Add(reads.Take());
            Check.Require(await DropboxByte(fixture, Churn) == 9,
                $"B-179 (k) an in-place rewrite still syncs its newest bytes ({rewrites} rewrites)");
        }
        Console.WriteLine($"B-179 (k) in-place rewrite, not cured by the probe: 1 rewrite {costs[0]}, " +
            $"{Rewrites} rewrites {costs[1]}");
        Check.Require(costs[0] == costs[1],
            $"B-179 (k) in-place rewrites of one identity cost one ordinary change per pass: {costs[0]} vs {costs[1]}");
    }

    // A moved unsaved identity is present, so it is never treated as gone: it follows its identity and syncs where
    // it now is, as before.
    private static async Task MovedIdentityAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var moved = LocalEntry(Moved, LocalId);
        fixture.Local.Seed(moved, [7]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, LocalId,
            LocalEntry(Churn, LocalId), true, false));
        var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal) { [LocalId] = Moved });
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        var run = reads.Take();
        Check.Require(await DropboxByte(fixture, Moved) == 7 && Binding(fixture, Moved) == LocalId && run.Identity == 1,
            $"B-179 (g) a moved unsaved identity syncs at its new path with one by-id read: {run}, " +
            $"binding {Binding(fixture, Moved)}");
    }

    // Fail safe: a by-id read that fails (here a locked or unreadable file) is not an absence. The row takes the
    // ordinary path, which reads the path and the identity again and owns the failure.
    private static async Task IdentityReadFailureAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, LocalId,
            LocalEntry(Churn, LocalId), true, false));
        var identity = 0;
        var local = 0;
        fixture.LocalView = new HookedSide(fixture.Local,
            beforeRead: path => { if (PathNames.Same(path, Churn)) Interlocked.Increment(ref local); },
            identity: (_, _) =>
            {
                Interlocked.Increment(ref identity);
                throw new ItemFailedException(Churn, TextKey.ReadLocalFailed, null);
            });
        var sql = await SqlStatementsAsync(fixture, () =>
            LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default));
        var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        var issues = fixture.Inner.Issues(PairId);
        var attempts = fixture.Hooks.BegunSelections.Count;
        Console.WriteLine($"B-179 (h) failed by-id read: {attempts} selection attempts, by-id reads {identity}, " +
            $"path reads {local}, {sql} SQL");
        // Failure budget: per attempt, the probe's read and the relocation's own read, one path read, and exactly one
        // B-179 statement (the probe's binding lookup); the path is empty, so no separate-work statement.
        Check.Require(attempts >= 1 && identity == 2 * attempts && local == attempts &&
            sql.Probe == attempts && sql.Separate == 0,
            $"B-179 (h) a failed by-id read counts as present and the ordinary path runs: attempts {attempts}, " +
            $"by-id reads {identity}, path reads {local}, SQL {sql}");
        Check.Require(await fixture.Dropbox.ReadOneAsync(Churn, default) is null &&
            (queued.Local > 0 || issues.Any(issue => PathNames.Same(issue.Path, Churn))),
            $"B-179 (h) the failure stays owned (row kept or an issue at the path) and nothing reaches Dropbox: " +
            $"queued {queued}, issues {string.Join(" | ", issues.Select(issue => issue.Kind + ":" + issue.Path))}");
        // Recovery: the read works again and proves the identity gone; the kept work then settles, nothing uploads.
        fixture.LocalView = new HookedSide(fixture.Local, identity: (_, _) =>
            Task.FromResult<(IdentityLocation Location, bool InScope)?>(null));
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        issues = fixture.Inner.Issues(PairId);
        Check.Require(queued == (0, 0) && await fixture.Dropbox.ReadOneAsync(Churn, default) is null &&
            !issues.Any(issue => issue.Kind == IssueKind.Failure && PathNames.Same(issue.Path, Churn)),
            $"B-179 (h) after the read recovers the kept work settles with nothing on Dropbox: queued {queued}, " +
            $"issues {string.Join(" | ", issues.Select(issue => issue.Kind + ":" + issue.Path))}");
    }

    // R4-1: an unsaved identity gone from its path while a new file with no row of its own (its acquisition was
    // omitted) holds that path: the gone row keeps observing the path, so the new file reaches Dropbox.
    private static async Task GoneOccupiedAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        fixture.Local.Seed(LocalEntry(Churn, OccupantId), [8]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, LocalId,
            LocalEntry(Churn, LocalId), true, false));
        var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal) { [OccupantId] = Churn });
        var sql = await SqlStatementsAsync(fixture, () =>
            LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default));
        var run = reads.Take();
        var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        // Absolute: one binding lookup, and three separate-work statements (both exact path probes and the identity
        // probe find nothing, ChangeTable.cs:618-621).
        Check.Require(sql is { Probe: 1, Separate: 3 },
            $"B-179 (l) an unowned occupant costs one binding lookup and three separate-work statements: {sql}");
        Check.Require(await DropboxByte(fixture, Churn) == 8 && Binding(fixture, Churn) == OccupantId &&
            queued == (0, 0) && fixture.Inner.Issues(PairId).Count == 0,
            $"B-179 (l) a gone identity's path held by an unqueued new file still syncs that file: {run}, " +
            $"binding {Binding(fixture, Churn)}, queued {queued}");
    }

    // R5-1: an unsaved identity's FILE_DELETE notice never reads its path in the enumerator, so it is never probed:
    // an unqueued new file at that path is still observed by the ordinary route and reaches Dropbox.
    private static async Task IdentityDeletedOccupiedAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        fixture.Local.Seed(LocalEntry(Churn, OccupantId), [8]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, Churn, null, LocalId, null,
            false, true));
        var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal) { [OccupantId] = Churn });
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        var run = reads.Take();
        var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        Check.Require(await DropboxByte(fixture, Churn) == 8 && Binding(fixture, Churn) == OccupantId &&
            queued == (0, 0) && run.Identity == 0,
            $"B-179 (r) an identity-deleted notice is not probed and the unqueued file at its path syncs: {run}, " +
            $"binding {Binding(fixture, Churn)}, queued {queued}");
    }

    // A saved file deleted in the same pass as a gone unsaved identity at its path: the saved deletion still
    // reaches Dropbox and the binding goes; the gone row owes nothing.
    private static async Task SavedDeletionAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var saved = LocalEntry(Churn, OccupantId);
        var remote = RemoteEntry(Churn, DropboxId);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
        var gone = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, LocalId,
            LocalEntry(Churn, LocalId), true, false));
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, Churn, null, OccupantId, null, false, true), 2);
        var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal));
        await LiveSyncer(fixture, gone, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        var run = reads.Take();
        var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        Check.Require(await fixture.Dropbox.ReadOneAsync(Churn, default) is null && Binding(fixture, Churn) is null &&
            queued == (0, 0) && run.Identity == 1,
            $"B-179 (m) a saved deletion beside a gone identity at the same path still deletes on Dropbox: {run}, " +
            $"binding {Binding(fixture, Churn)}, queued {queued}");
    }

    // A mixed identity group: the temporary-name row is probed, the rename row with an old path never is. Gone, the
    // group completes with nothing on Dropbox; present, the file syncs at its final path.
    private static async Task OldPathMemberAsync()
    {
        foreach (var present in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            TrustEchoSources(fixture);
            if (present) fixture.Local.Seed(LocalEntry(Churn, LocalId), [6]);
            var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, ChurnTemp, null, LocalId,
                LocalEntry(ChurnTemp, LocalId), true, false));
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, Churn, ChurnTemp, LocalId,
                LocalEntry(Churn, LocalId), false, false), 2);
            var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal)
                { [present ? LocalId : "none"] = Churn });
            await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
            var run = reads.Take();
            var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
            Check.Require(queued == (0, 0) && fixture.Inner.Issues(PairId).Count == 0 &&
                await fixture.Dropbox.ReadOneAsync(ChurnTemp, default) is null &&
                (present ? await DropboxByte(fixture, Churn) == 6 && Binding(fixture, Churn) == LocalId
                    : await fixture.Dropbox.ReadOneAsync(Churn, default) is null),
                $"B-179 (n) an identity group with an old-path member, {(present ? "present" : "gone")}: {run}, " +
                $"queued {queued}");
        }
    }

    // A present identity outside the pair's scope is not gone: the row takes today's path, nothing reaches Dropbox
    // and no issue is raised; the one by-id answer serves the relocation too.
    private static async Task OutOfScopeIdentityAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, LocalId,
            LocalEntry(Churn, LocalId), true, false));
        var identity = 0;
        fixture.LocalView = new HookedSide(fixture.Local, identity: (_, _) =>
        {
            Interlocked.Increment(ref identity);
            return Task.FromResult<(IdentityLocation Location, bool InScope)?>(
                (new IdentityLocation(VolumeKey, "outside", LocalEntry("outside", LocalId)), false));
        });
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        Check.Require(identity == 1 && queued == (0, 0) && fixture.Inner.Issues(PairId).Count == 0 &&
            await fixture.Dropbox.ReadOneAsync(Churn, default) is null,
            $"B-179 (o) an out-of-scope identity is present, not gone: by-id reads {identity}, queued {queued}");
    }

    // The same identity reappears with a row acquired after its gone selection began (beyond that selection's
    // Through): the later row is not completed with the gone one, and the file syncs in this run or the next.
    private static async Task LaterRowAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, LocalId,
            LocalEntry(Churn, LocalId), true, false));
        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        var added = false;
        fixture.Hooks.BeforeCall = method =>
        {
            if (added || method.Name != nameof(IStateStore.CompleteWork)) return;
            added = true;
            var later = LocalEntry(Fresh, LocalId);
            fixture.Local.Seed(later, [4]);
            live[LocalId] = Fresh;
            Selected(fixture, Fresh, later, 2);
        };
        var reads = Watch(fixture, live);
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        fixture.Hooks.BeforeCall = null;
        if (await fixture.Dropbox.ReadOneAsync(Fresh, default) is null)
            await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        Check.Require(added && await DropboxByte(fixture, Fresh) == 4 && Binding(fixture, Fresh) == LocalId &&
            queued == (0, 0),
            $"B-179 (p) a row beyond the gone selection's Through keeps its own work and syncs: {reads.Take()}, " +
            $"queued {queued}");
    }

    // Cancellation during the by-id read is not an answer: the probe rethrows it, it leaves SyncWorkAsync and the row
    // stays queued.
    private static async Task ProbeCancelledAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, LocalId,
            LocalEntry(Churn, LocalId), true, false));
        fixture.LocalView = new HookedSide(fixture.Local, identity: (_, _) => throw new OperationCanceledException());
        var cancelled = false;
        try { await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default); }
        catch (OperationCanceledException) { cancelled = true; }
        var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        Check.Require(cancelled && queued.Local == 1 && await fixture.Dropbox.ReadOneAsync(Churn, default) is null,
            $"B-179 (q) a cancelled by-id read propagates and the row stays queued: propagated {cancelled}, " +
            $"queued {queued}");
    }

    // R1: path-only Local evidence answers dirty for every identity at its path and is never asked by id. A saved
    // file whose bytes changed with the same id, size and time still reaches Dropbox.
    private static async Task PathOnlyDirtyKeptAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Local.Seed(local, [2]);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        var pathOnly = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, null, null, true, false));
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, local, false, false), page: 2);
        var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal));
        await LiveSyncer(fixture, pathOnly, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        var run = reads.Take();
        Check.Require(await DropboxByte(fixture, Item) == 2 && run.Identity == 0,
            $"B-179 (1) a path-only dirty notice is kept and its changed bytes reach Dropbox: {run}");
    }

    // R2: a saved identity is never asked whether it is gone; its saved binding owns it, and its change syncs.
    private static async Task SavedIdentityNotAskedAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var saved = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Local.Seed(saved, [5]);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, saved,
            true, false));
        var reads = Watch(fixture, new Dictionary<string, string>(StringComparer.Ordinal));
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        var run = reads.Take();
        Check.Require(await DropboxByte(fixture, Item) == 5 && run.Identity == 0,
            $"B-179 (2) a saved identity is never asked by id and its change syncs: {run}");
    }

    // B-179b: a changed saved file already superseded by a later same-path rewrite refuses before its Dropbox read;
    // the work and the binding stay, and the next finite pass syncs the newest bytes.
    private static async Task StaleBeforeProviderAsync()
    {
        // B-202 first, so each control's first failure is its own claim.
        await RenameCandidateAttachesAsync(renamed: true);
        await RenameCandidateAttachesAsync(renamed: false);
        await StaleRewriteSkipsDropboxAsync();
        await UnrelatedArrivalSyncsAsync();
        await LateRewriteRefusesEffectAsync();
        await ExcludedShortcutFirstAsync();
        await FinalRewriteNextOpportunityAsync();
    }

    // B-202: SyncedById calls in the early check's own window, after its held check (the last HeldCovers) and
    // before the first Dropbox read; the rename-candidate lookup is the only one made there.
    private sealed class EarlyLookups
    {
        public int Count;
        public bool Done;
    }

    private static (EarlyLookups Lookups, CountingSide Dropbox) WatchEarlyLookups(Fixture fixture)
    {
        var lookups = new EarlyLookups();
        fixture.Hooks.BeforeCall = method =>
        {
            if (lookups.Done) return;
            if (method.Name == nameof(IStateStore.HeldCovers)) lookups.Count = 0;
            else if (method.Name == nameof(IStateStore.SyncedById)) lookups.Count++;
        };
        var dropbox = new CountingSide(new HookedSide(fixture.Dropbox, beforeRead: _ => lookups.Done = true));
        fixture.DropboxView = dropbox;
        return (lookups, dropbox);
    }

    private static async Task<LiveAdmissionStaleException?> RefusedAsync(SelectionPass pass, WorkSelection selection)
    {
        try { await pass.RunAsync(selection, default); }
        catch (LiveAdmissionStaleException error) { return error; }
        return null;
    }

    private static async Task StaleRewriteSkipsDropboxAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var selection = SavedRewrite(fixture);
        var later = RewriteAgain(fixture);
        Check.Require(later.AnchorWorkId > selection.ThroughWorkId, "B-179b the rewrite is acquired after the selection.");
        var (lookups, dropbox) = WatchEarlyLookups(fixture);
        var boundary = Validating(fixture);
        var pass = ValidatedRunner(fixture, selection, boundary);
        var refused = await RefusedAsync(pass, selection);
        fixture.Hooks.BeforeCall = null;
        Console.WriteLine($"B-179b stale rewrite: dropboxReads={dropbox.Reads}, admissions={boundary.Calls}, " +
            $"stale={refused?.Reason}, early-synced-by-id={lookups.Count}");
        Check.Require(lookups.Count == 1,
            "B-202 a due refusal pays exactly one rename-candidate lookup: " + lookups.Count);
        Check.Require(refused?.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged,
            "B-179b a superseded rewrite refuses with admission's own reason: " + refused?.Reason);
        Check.Require(dropbox.Reads == 0 && boundary.Calls == 0,
            $"B-179b a superseded rewrite makes no Dropbox read: reads={dropbox.Reads}, admissions={boundary.Calls}");
        Check.Require(pass.RefusedRow == new RowStage(Side.Local, selection.AnchorWorkId, Item) &&
            fixture.Inner.Issues(PairId).Count == 0 && fixture.Inner.ReadWork(selection).Any() &&
            Binding(fixture, Item) == LocalId && await DropboxByte(fixture, Item) == 1,
            "B-179b the refusal names its branch and keeps the work, the binding and Dropbox as they were");
        var next = selection with { ThroughWorkId = fixture.Inner.ReadWorkWatermark() };
        var result = await ValidatedRunner(fixture, next, boundary).RunAsync(next, default);
        Check.Require(result.Completed == true && await DropboxByte(fixture, Item) == 3 &&
            Binding(fixture, Item) == LocalId, "B-179b the next pass syncs the newest bytes: " + result.Completed);
    }

    // Another path's arrival after the selection is not evidence about this file.
    private static async Task UnrelatedArrivalSyncsAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var selection = SavedRewrite(fixture);
        var other = LocalEntry("elsewhere", "elsewhere-id");
        fixture.Local.Seed(other, [4]);
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, other.Path, null, other.Id, other, false, false),
            page: 2);
        var (lookups, _) = WatchEarlyLookups(fixture);
        var refused = await RefusedAsync(ValidatedRunner(fixture, selection, Validating(fixture)), selection);
        fixture.Hooks.BeforeCall = null;
        Check.Require(refused is null && await DropboxByte(fixture, Item) == 2,
            "B-179b an unrelated arrival leaves the selected file to sync: " + refused?.Reason);
        // B-202: an eligible file with no later evidence of its own pays no rename-candidate lookup.
        Check.Require(lookups.Count == 0,
            "B-202 an eligible file with no later evidence makes no early SyncedById: " + lookups.Count);
    }

    // A rewrite after the early check and the Dropbox read is still refused at admission, before any effect.
    private static async Task LateRewriteRefusesEffectAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var selection = SavedRewrite(fixture);
        var rewritten = false;
        fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: path =>
        {
            if (rewritten || !PathNames.Same(path, Item)) return;
            rewritten = true;
            RewriteAgain(fixture);
        });
        var boundary = Validating(fixture);
        var refused = await RefusedAsync(ValidatedRunner(fixture, selection, boundary), selection);
        Check.Require(rewritten && refused?.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
            boundary.Calls == 0 && await DropboxByte(fixture, Item) == 1 && Binding(fixture, Item) == LocalId,
            $"B-179b a late rewrite cannot authorize the obsolete upload: stale={refused?.Reason}, admissions={boundary.Calls}");
        var next = selection with { ThroughWorkId = fixture.Inner.ReadWorkWatermark() };
        var result = await ValidatedRunner(fixture, next, boundary).RunAsync(next, default);
        Check.Require(result.Completed == true && await DropboxByte(fixture, Item) == 3,
            "B-179b once the rewrites stop the newest bytes sync: " + result.Completed);
    }

    // The excluded-unbound shortcut settles before the early check, so later evidence costs no refusal or read.
    private static async Task ExcludedShortcutFirstAsync()
    {
        using var fixture = new Fixture(realStore: true);
        fixture.Local.Seed(FolderEntry("Skip", "local-parent"), null);
        var file = LocalEntry("Skip/file.txt", LocalId);
        fixture.Local.Seed(file, [1]);
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, file.Path, null, LocalId, file,
            false, false));
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, file.Path, null, LocalId, file, false, false),
            page: 2);
        LiveAdmissionStaleException? refused = null;
        var reads = -1;
        try { reads = (await RunExcludedAsync(fixture, selected, ExcludedScope(fixture))).Dropbox.Reads; }
        catch (LiveAdmissionStaleException error) { refused = error; }
        Check.Require(refused is null && reads == 0,
            $"B-179b the excluded shortcut runs before the early check: stale={refused?.Reason}, dropboxReads={reads}");
    }

    // Syncer level only: the last rewrite commits during the run, and one further ordinary opportunity that this check
    // supplies itself (standing in for the owed wake) syncs the newest bytes without a further rewrite. It does not
    // prove the wake is delivered; EngineHostChecks.FinalRewriteWakeSettlesAsync does that through the host's AutoSync.
    private static async Task FinalRewriteNextOpportunityAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var selection = SavedRewrite(fixture);
        var rewritten = false;
        fixture.LocalView = new HookedSide(fixture.Local, beforeRead: path =>
        {
            if (rewritten || !PathNames.Same(path, Item)) return;
            rewritten = true;
            RewriteAgain(fixture);
        });
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        var queued = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        var bytes = await DropboxByte(fixture, Item);
        Check.Require(rewritten && bytes == 3 && Binding(fixture, Item) == LocalId && queued == (0, 0) &&
            fixture.Inner.Issues(PairId).Count == 0,
            $"B-179b a further opportunity after the final rewrite syncs its newest bytes: bytes={bytes}, queued={queued}");
    }

    // B-202: a rename candidate (a saved file moved out of a held folder, reported as the journal does with no
    // saved binding on the row) with later evidence at its new path still takes RenameWork and attaches to the holder
    // before admission; the early check never turns it into a stale refusal. renamed: the rename notice itself;
    // otherwise a Changed notice at the new path with no old path (B-202, refused early on 0.3.84). Closing the holder
    // brings back exactly the selected responsibility.
    private static async Task RenameCandidateAttachesAsync(bool renamed)
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var folder = FolderEntry(Folder, LocalId + "-folder");
        fixture.Local.Seed(folder, null);
        var old = LocalEntry(Folder + "/child", LocalId);
        var remote = RemoteEntry(old.Path, DropboxId);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(old, remote)] });
        var moved = old with { Path = Item };
        fixture.Local.Seed(moved, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Folder,
                new IssueSubject([new IssueItem(Side.Local, Folder, folder.Id)], null))]
        });
        var kind = renamed ? ChangeKind.Renamed : ChangeKind.Changed;
        var oldPath = renamed ? old.Path : null;
        var selection = Seed(fixture, new ChangeNotice(Side.Local, kind, Item, oldPath, LocalId, moved, false, false));
        var later = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, moved, true,
            false), page: 2);
        Check.Require(later.AnchorWorkId > selection.ThroughWorkId,
            "B-202 setup: the new path's evidence is acquired after the selection");
        var (lookups, dropbox) = WatchEarlyLookups(fixture);
        var boundary = Validating(fixture);
        var captures = 0;
        LiveAdmissionStaleException? refused = null;
        bool? completed = null;
        try
        {
            completed = (await Runner(fixture, selection, boundary, (selected, _, _) =>
            {
                captures++;
                return Task.FromResult(Coverage(selected));
            }).RunAsync(selection, default)).Completed;
        }
        catch (LiveAdmissionStaleException error) { refused = error; }
        fixture.Hooks.BeforeCall = null;
        var label = renamed ? "renamed" : "changed (B-202)";
        Console.WriteLine($"B-202 rename candidate {label}: completed={completed}, dropboxReads={dropbox.Reads}, " +
            $"captures={captures}, admissions={boundary.Calls}, early-synced-by-id={lookups.Count}, stale={refused?.Reason}");
        Check.Require(refused is null && completed == true,
            $"B-202 {label}: a held rename candidate attaches and its selection completes instead of an early stale " +
            $"refusal: completed={completed}, stale={refused?.Reason}");
        Check.Require(dropbox.Reads > 0 && captures == 0 && boundary.Calls == 0,
            $"B-202 {label}: the rename takes its ordinary observation and no coverage or lease: reads={dropbox.Reads}, " +
            $"captures={captures}, admissions={boundary.Calls}");
        // R2: at most the one lookup a due refusal pays (none when the branch is not eligible at all).
        Check.Require(lookups.Count <= 1, $"B-202 {label}: early SyncedById calls {lookups.Count}");
        Check.Require(fixture.Inner.Issues(PairId).Any(issue => issue.Kind == IssueKind.NameClash && issue.Path == Folder),
            $"B-202 {label}: the holder stays open with the attached rename");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(Folder, new[] { IssueKind.NameClash })] });
        // The exact selected responsibility (its kind, old path and identity), not the separately acquired later row.
        var retained = PendingAt(fixture, Side.Local, Item, old.Path).SelectMany(fixture.Inner.ReadWork)
            .Where(work => work.WorkId != later.AnchorWorkId).ToArray();
        Check.Require(retained.Any(work => work.Notice is { Side: Side.Local } notice && notice.Kind == kind &&
                notice.OldPath == oldPath && notice.Id == LocalId && PathNames.Same(notice.Path, Item)),
            $"B-202 {label}: closing the holder leaves the selected rename's obligation reachable: " +
            string.Join(",", retained.Select(work => work.WorkId + ":" + work.Notice.Kind + ":" + work.Notice.Path)));
    }

    // One temp-and-replace save as acquisition reports it: the new identity at the temporary name (only when it was
    // resolved there), then at the target; a replaced saved identity's deletion follows, and an unsaved one leaves none.
    private static WorkSelection Replace(Fixture fixture, string id, string? replacedSaved, bool tempSeen, ref long page)
    {
        var target = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, tempSeen ? ChurnTemp : Churn, null,
            id, LocalEntry(tempSeen ? ChurnTemp : Churn, id), true, false), ++page);
        if (tempSeen)
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Churn, null, id, LocalEntry(Churn, id),
                false, false), ++page);
        if (replacedSaved is not null)
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, Churn, null, replacedSaved, null, false, true),
                ++page);
        return target;
    }

    // Local path reads and Dropbox reads at the churning keys, and every Local by-id read, per run. The by-id
    // answer is the live map: an id present there is at that path, any other id is proven absent.
    private static ChurnReads Watch(Fixture fixture, Dictionary<string, string> live, Action? freshEnsured = null)
    {
        var reads = new ChurnReads();
        static bool Churning(string path) => PathNames.Same(path, Churn) || PathNames.Same(path, ChurnTemp) ||
            PathNames.Same(path, Item) || PathNames.Same(path, Moved);
        fixture.LocalView = new HookedSide(fixture.Local,
            beforeRead: path => { if (Churning(path)) Interlocked.Increment(ref reads.Local); },
            identity: async (id, ct) =>
            {
                Interlocked.Increment(ref reads.Identity);
                reads.Asked.AddOrUpdate(id, 1, (_, asks) => asks + 1);
                if (!live.TryGetValue(id, out var path)) return null;
                // The resolved entry's observation, as LocalSide.ReadById makes it after resolving the id.
                Interlocked.Increment(ref reads.Observed);
                return await fixture.Local.ReadOneAsync(path, ct) is { } entry && entry.Id == id
                    ? (new IdentityLocation(VolumeKey, path, entry), true) : null;
            });
        fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            OnRead = path => { if (Churning(path)) Interlocked.Increment(ref reads.Dropbox); },
            BlockEnsure = path =>
            {
                if (PathNames.Same(path, Fresh)) freshEnsured?.Invoke();
                return null;
            }
        };
        return reads;
    }

    private sealed class ChurnReads
    {
        public int Local;
        public int Dropbox;
        public int Identity;
        public int Observed;
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Asked = new(StringComparer.Ordinal);

        public int AskedOf(string id) => Asked.TryGetValue(id, out var asks) ? asks : 0;

        public readonly record struct Counts(int Local, int Dropbox, int Identity, int Observed = 0);

        public Counts Take()
        {
            Asked.Clear();
            return TakeCounts();
        }

        private Counts TakeCounts() => new(Interlocked.Exchange(ref Local, 0), Interlocked.Exchange(ref Dropbox, 0),
            Interlocked.Exchange(ref Identity, 0), Interlocked.Exchange(ref Observed, 0));
    }

    private static string? Binding(Fixture fixture, string path) =>
        fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(path))?.LocalId;

    // The one byte the fixtures write, read from the inner fake so it is outside the counted provider reads.
    private static async Task<int?> DropboxByte(Fixture fixture, string path)
    {
        if (await fixture.Dropbox.ReadOneAsync(path, default) is not { } uploaded) return null;
        var bytes = new MemoryStream();
        await using (var stream = await fixture.Dropbox.OpenReadAsync(uploaded, null, default))
            await stream.CopyToAsync(bytes);
        return bytes.Length == 1 ? bytes.ToArray()[0] : -(int)bytes.Length;
    }
}
