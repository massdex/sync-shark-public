using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void UserMovesMirrorOneMoveEach() => UserMovesMirrorOneMoveEachAsync().GetAwaiter().GetResult();

    private static async Task UserMovesMirrorOneMoveEachAsync()
    {
        var failures = new List<Exception>();
        foreach (var real in new[] { false, true })
        foreach (var shape in new[] { "files", "folders", "ancestor", "repair" })
        foreach (var count in new[] { 2, 8 })
        {
            long[]? previous = null;
            foreach (var background in new[] { 8, 16 })
            {
                try
                {
                    var counts = await MovedTreeAsync(real, shape, count, background);
                    if (previous is not null) Check.Require(previous.SequenceEqual(counts),
                        "An unchanged background does not add point reads, effects or batch work to the involved move.");
                    previous = counts;
                }
                catch (Exception error) { failures.Add(new InvalidOperationException(
                    $"Move shape={shape} real={real} count={count} background={background}", error)); }
            }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    public static void CarriedFolders() => CarriedFoldersAsync().GetAwaiter().GetResult();

    private static async Task CarriedFoldersAsync()
    {
        var failures = new List<Exception>();
        foreach (var real in new[] { false, true })
        foreach (var shape in new[] { "carried", "woken" })
        foreach (var count in new[] { 2, 8 })
        {
            long[]? previous = null;
            foreach (var background in new[] { 8, 16 })
            {
                try
                {
                    var counts = await MovedTreeAsync(real, shape, count, background);
                    if (previous is not null) Check.Require(counts.SequenceEqual(previous),
                        "A carried subtree's complete count vector is independent of unrelated pair size.");
                    previous = counts;
                }
                catch (Exception error) { failures.Add(new InvalidOperationException(
                    $"Carried shape={shape} real={real} count={count} background={background}", error)); }
            }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    public static void CarriedFolderBoundaries() => CarriedFolderBoundariesAsync().GetAwaiter().GetResult();

    private static async Task CarriedFolderBoundariesAsync()
    {
        var failures = new List<Exception>();
        foreach (var real in new[] { false, true })
        foreach (var shape in new[] { "replacement", "unreadable", "residual-unreadable", "discovery", "case" })
        foreach (var count in new[] { 2, 8 })
        {
            long[]? previous = null;
            foreach (var background in new[] { 8, 16 })
                try
                {
                    var counts = await CarriedFolderBoundaryAsync(real, shape, count, background);
                    if (previous is not null) Check.Require(counts.SequenceEqual(previous),
                        "A carried boundary's whole opportunity does not grow with unrelated pair size.");
                    previous = counts;
                }
                catch (Exception error) { failures.Add(new InvalidOperationException(
                    $"Carried boundary={shape} real={real} count={count} background={background}", error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task<long[]> CarriedFolderBoundaryAsync(bool real, string shape, int count, int background)
    {
        using var fixture = new Fixture(realStore: real);
        RepairDone(fixture);
        var local = FolderEntry("Folder", "local-folder");
        var remote = FolderEntry("Folder", "remote-folder");
        var before = SyncedEntries.From(local, remote);
        fixture.Local.Seed(local, null);
        fixture.Dropbox.Seed(remote, null);
        var bindings = new List<SyncedEntry> { before };
        foreach (var path in Enumerable.Range(0, count).Select(index => "Folder/Child" + index)
            .Concat(Enumerable.Range(0, background).Select(index => "Outside" + index)))
        {
            var left = LocalEntry(path, "local-" + path);
            var right = RemoteEntry(path, "remote-" + path);
            fixture.Local.Seed(left, [1]);
            fixture.Dropbox.Seed(right, [1]);
            bindings.Add(SyncedEntries.From(left, right));
        }
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = bindings });
        if (shape == "replacement")
        {
            await fixture.Local.DeleteAsync(local, null, default);
            fixture.Local.Seed(local with { Id = "replacement-folder" }, null);
        }
        if (shape == "case")
        {
            Check.Require(!Massdex.SyncShark.Engine.Planner.Decide.CarriedHere(before, Side.Local, local.Id, "FOLDER"),
                "A case-only spelling change is not the ordinal saved path.");
            await fixture.Local.MoveAsync(local, "FOLDER", default);
        }
        var notice = new ChangeNotice(Side.Local, ChangeKind.Renamed, shape == "case" ? "FOLDER" : local.Path,
            shape == "case" ? local.Path : null, local.Id, local, false, false);
        var selection = Seed(fixture, notice);
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        ClearObligations(fixture);
        if (shape == "residual-unreadable")
        {
            fixture.Inner.Commit(PairId, StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.DropboxRefused,
                local.Path, new IssueSubject([new IssueItem(Side.Local, local.Path, local.Id)], Side.Local))] });
            var issue = fixture.Inner.Issues(PairId).Single();
            Check.Require(fixture.Inner.AttachIssueWork(selection, issue, [(notice, true, before)]) &&
                fixture.Inner.CompleteWork(selection, null), "The unreadable folder starts as retained residual work.");
            fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(issue.Path, new[] { issue.Kind })] });
            selection = PendingAt(fixture, Side.Local, local.Path).Single();
            Check.Require(fixture.Inner.ReadWork(selection).All(work => work.IsResidual), "Closure keeps the residual boundary.");
        }
        if (shape.Contains("unreadable", StringComparison.Ordinal))
            UnreadableMember.Record(fixture.Inner, PairId, local.Path, local.Id);
        if (shape == "discovery")
            fixture.Inner.RequireDiscovery(selection, fixture.Inner.ReadWork(selection).Single().WorkId);
        var context = fixture.Context(selection);
        if (shape == "replacement")
        {
            var begun = fixture.Inner.BeginSelection(selection)!;
            var probe = Counted(fixture, begun, () => { });
            await using (var branches = new SelectionBranches(probe, context).ReadBranchesAsync(begun, default).GetAsyncEnumerator())
                Check.Require(await branches.MoveNextAsync() && branches.Current.FreshPrimary?.Entry?.Id == "replacement-folder" &&
                    !branches.Current.Gone, "The selected identity's unowned replacement is the fresh primary, not a gone branch.");
            fixture.Inner.EndSelection(begun);
            fixture.Inner.EndWorkPass(PairId);
        }
        var queries = new Dictionary<string, int>();
        var replacementLookups = 0;
        int? replacementAtObservation = null;
        var guardReads = 0;
        var inBatch = false;
        var localCalls = new CountingSide(fixture.Local);
        var remoteCalls = new CountingSide(new HookedSide(fixture.Dropbox, beforeRead: _ =>
            replacementAtObservation ??= replacementLookups));
        context = context with { Local = localCalls, Dropbox = remoteCalls };
        var boundary = new Boundary(_ => new Lease()) { Capture = (work, _, _) => Task.FromResult(Coverage(work)) };
        var ports = new SyncPorts(new RecoveryBoundary(), boundary, new AdmittedSelection(), 0);
        var batches = 0;
        var observed = ports with { RunBatchAsync = async (work, side, scope, retries, settles, ct) =>
        {
            batches++;
            inBatch = true;
            try { await SyncJobs.RunBatchAsync(context, ports, work, scope, ct, side, retries, settles); }
            finally { inBatch = false; }
        } };
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            queries[method.Name] = queries.GetValueOrDefault(method.Name) + 1;
            if (method.Name == nameof(IStateStore.UnreadableUnder) && !inBatch) guardReads++;
            if (method.Name == nameof(IStateStore.SyncedById) && args?.Contains("replacement-folder") == true) replacementLookups++;
        };
        await UntilDrainedAsync(new Syncer(context, observed), default);
        fixture.Hooks.BeforeCallWith = null;
        var counts = new long[] { localCalls.Listings, remoteCalls.Listings, localCalls.Reads, remoteCalls.Reads,
            remoteCalls.Moves, remoteCalls.Publishes, remoteCalls.Deletes, batches,
            queries.GetValueOrDefault(nameof(IStateStore.SyncedById)), queries.GetValueOrDefault(nameof(IStateStore.UnreadableUnder)),
            queries.GetValueOrDefault(nameof(IStateStore.RequireDiscovery)), guardReads,
            queries.GetValueOrDefault(nameof(IStateStore.UnknownMoveCovering)) };
        Console.WriteLine($"Carried-boundary shape={shape} real={real} count={count} background={background} values=" + string.Join(",", counts));
        if (shape != "case")
        {
            Check.Require(batches == 1 && localCalls.Listings == 1 && remoteCalls.Listings == 1,
                "Replacement, unreadability and owed discovery each retain one bilateral folder batch.");
            Check.Require(guardReads == (shape.Contains("unreadable", StringComparison.Ordinal) ? 1 : 0),
                "Only a carried unreadable row adds its one guarding query, including a residual row.");
            Check.Require(queries.GetValueOrDefault(nameof(IStateStore.RequireDiscovery)) == (shape == "discovery" ? 0 : 1),
                "Discovery is written once before a new batch, never rewritten when already owed.");
        }
        else Check.Require(batches == 0 && remoteCalls.Moves == 1 && remoteCalls.Publishes == 0 && remoteCalls.Deletes == 0 &&
            fixture.Inner.SyncedById(PairId, Side.Local, local.Id)?.LocalPath == "FOLDER",
            "An ordinal case-only folder move keeps one counterpart move and no batch or content transfer.");
        if (shape == "replacement") Check.Require(replacementAtObservation == 1,
            "The fresh replacement pays exactly one identity lookup in batch admission, not the carried guard.");
        return counts;
    }

    private static async Task<long[]> MovedTreeAsync(bool real, string shape, int count, int background)
    {
        using var fixture = new Fixture(realStore: real);
        RepairDone(fixture);
        var localEntries = new List<Entry>();
        var bindings = new List<SyncedEntry>();
        void Bind(string path, bool folder)
        {
            var local = folder ? FolderEntry(path, "local-" + path) : LocalEntry(path, "local-" + path);
            var remote = folder ? FolderEntry(path, "remote-" + path) : RemoteEntry(path, "remote-" + path);
            fixture.Local.Seed(local, folder ? null : [1]);
            fixture.Dropbox.Seed(remote, folder ? null : [1]);
            localEntries.Add(local);
            bindings.Add(SyncedEntries.From(local, remote));
        }
        Bind("Source", true);
        for (var index = 0; index < count; index++)
        {
            var path = "Source/Child" + index;
            Bind(path, shape != "files");
            if (shape != "files")
                for (var child = 0; child < 3; child++) Bind(path + "/File" + child, false);
        }
        var involved = localEntries.ToArray();
        for (var index = 0; index < background; index++) Bind("Outside" + index, false);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = bindings });
        var oneFolder = shape is "ancestor" or "repair" or "carried" or "woken";
        if (oneFolder) await fixture.Local.MoveAsync(involved[0], "Destination", default);
        else
        {
            fixture.Local.Seed(FolderEntry("Destination", "local-destination"), null);
            foreach (var entry in involved.Where(entry => entry.Path.Count(character => character == '/') == 1))
                await fixture.Local.MoveAsync(entry, "Destination" + entry.Path["Source".Length..], default);
        }
        var notices = involved.Where(entry => oneFolder || entry.Path != "Source")
            .Select(entry => new ChangeNotice(Side.Local, ChangeKind.Renamed,
                "Destination" + entry.Path["Source".Length..], entry.Path, entry.Id,
                fixture.Local.FindById(entry.Id), false, false)).ToArray();
        WorkSelection? selection = null;
        long page = 0;
        if (shape != "repair")
        {
            if (!oneFolder)
            {
                var destination = fixture.Local.FindById("local-destination")!;
                selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, destination.Path,
                    null, destination.Id, destination, false, false), ++page);
            }
            foreach (var notice in shape == "ancestor" ? notices.Reverse() : notices)
                selection = Seed(fixture, notice, ++page);
        }
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        ClearObligations(fixture);
        if (shape == "woken")
        {
            var remoteRoot = fixture.Dropbox.FindById("remote-Source")!;
            await fixture.Dropbox.MoveAsync(remoteRoot, "Destination", default);
            var move = new IssueSubject.UnknownMove(remoteRoot.Id, "Source", "Destination", null, true);
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.DropboxRefused, "Source",
                    new IssueSubject([new IssueItem(Side.Local, "Destination", involved[0].Id),
                        new IssueItem(Side.Dropbox, "Source", remoteRoot.Id)], Side.Dropbox) { Move = move })]
            });
            var issue = fixture.Inner.Issues(PairId).Single();
            foreach (var notice in notices)
            {
                var pending = PendingAt(fixture, Side.Local, notice.Path).Single();
                var before = bindings.Single(saved => saved.LocalId == notice.Id);
                Check.Require(fixture.Inner.AttachIssueWork(pending, issue, [(notice, before.IsFolder, before)]) &&
                    fixture.Inner.CompleteWork(pending, null), "Every Local rename is parked under the unknown move.");
                Check.Require(fixture.Inner.ReadWork(PendingAt(fixture, Side.Local, notice.Path).Single())
                    .All(work => work.IsResidual), "The parked Local work is residual before settlement.");
            }
        }
        var reads = new List<string>();
        var readSites = new Dictionary<string, int>();
        var guardSites = new Dictionary<string, int>();
        static string Caller() => string.Join(" <- ", new System.Diagnostics.StackTrace().GetFrames()
            .Select(frame => frame.GetMethod()?.DeclaringType?.FullName)
            .Where(name => name is not null && (name.Contains(".Executor.", StringComparison.Ordinal) ||
                name.Contains(".Run.", StringComparison.Ordinal))));
        var localCalls = new CountingSide(fixture.Local);
        var remoteCalls = new CountingSide(new HookedSide(fixture.Dropbox, beforeRead: path =>
        {
            reads.Add(path);
            var site = path + " @ " + Caller();
            readSites[site] = readSites.GetValueOrDefault(site) + 1;
        }));
        fixture.LocalView = IdentitySide.Truthful(localCalls, fixture.Local);
        fixture.DropboxView = IdentitySide.Truthful(remoteCalls, fixture.Dropbox);
        var context = fixture.Context(selection ?? new WorkSelection(PairId, Generation, 0, Side.Local,
            VolumeKey, null, "", 0, 0));
        var boundary = new Boundary(_ => new Lease())
            { Capture = (work, _, _) => Task.FromResult(Coverage(work)) };
        var ports = new SyncPorts(new RecoveryBoundary(), boundary, new AdmittedSelection(), 0);
        var batches = 0;
        var observed = ports with { RunBatchAsync = (work, side, scope, retries, settles, ct) =>
        {
            batches++;
            return SyncJobs.RunBatchAsync(context, ports, work, scope, ct, side, retries, settles);
        } };
        var queried = new Dictionary<string, int>();
        fixture.Hooks.BeforeCallWith = (method, _) =>
        {
            queried[method.Name] = queried.GetValueOrDefault(method.Name) + 1;
            if (method.Name == nameof(IStateStore.UnknownMoveCovering))
            {
                var site = Caller();
                guardSites[site] = guardSites.GetValueOrDefault(site) + 1;
            }
        };
        if (shape == "repair")
        {
            var token = fixture.Store.BeginScan(PairId, Generation, false);
            foreach (var side in new ISide[] { localCalls, remoteCalls })
            {
                var sink = fixture.Store.OpenScan(PairId, side.Side, ScanTestSettings.BatchRows);
                await foreach (var entry in side.ReadAllAsync("", new ListOptions(true, false, false), default))
                    await sink.AddAsync(entry, default);
                await sink.CompleteAsync();
            }
            await new Syncer(context with { Live = false, ScanId = token }, observed)
                .SyncScannedAsync(token, new ListedSkips([], [], [], []), null, default);
        }
        else
        {
            var syncer = new Syncer(context, observed);
            if (shape == "woken")
            {
                var issue = fixture.Inner.Issues(PairId).Single();
                var resolution = await syncer.ResolveAsync(issue, ResolutionChoice.Retry, null, null, default);
                Check.Require(resolution.Resolved, "Retry settles the exact unknown move before its Local residuals drain.");
            }
            await UntilDrainedAsync(syncer, default);
        }
        fixture.Hooks.BeforeCallWith = null;
        var counts = new long[] { localCalls.Listings, remoteCalls.Listings, localCalls.Reads, remoteCalls.Reads,
            remoteCalls.Moves, remoteCalls.Publishes, remoteCalls.Ensures, remoteCalls.Deletes, batches,
            queried.GetValueOrDefault(nameof(IStateStore.SyncedById)),
            queried.GetValueOrDefault(nameof(IStateStore.UnreadableUnder)),
            queried.GetValueOrDefault(nameof(IStateStore.RequireDiscovery)),
            reads.Count(path => path.StartsWith("Source/", StringComparison.Ordinal)),
            queried.GetValueOrDefault(nameof(IStateStore.UnknownMoveCovering)) };
        Console.WriteLine($"Moved-tree shape={shape} real={real} count={count} background={background} values=" +
            string.Join(",", counts));
        foreach (var (site, calls) in readSites)
            Console.WriteLine($"Moved-tree read shape={shape} real={real} count={count} background={background} calls={calls} {site}");
        foreach (var (site, calls) in guardSites)
            Console.WriteLine($"Moved-tree guard shape={shape} real={real} count={count} background={background} calls={calls} {site}");
        if (shape is "carried" or "woken")
        {
            Check.Require(batches == 0 && localCalls.Listings == 0 && remoteCalls.Listings == 0,
                "Carried folders owe no batch or bilateral listing after the move.");
            Check.Require(reads.All(path => !path.StartsWith("Source/", StringComparison.Ordinal)),
                "Carried descendants owe zero old-path Dropbox reads.");
            Check.Require(queried.GetValueOrDefault(nameof(IStateStore.RequireDiscovery)) == 0 &&
                queried.GetValueOrDefault(nameof(IStateStore.UnreadableUnder)) == count + (shape == "woken" ? 1 : 0),
                "Each carried folder pays only its unreadable guard, with no new discovery write.");
            Check.Require(reads.Count(path => path.StartsWith("Destination/", StringComparison.Ordinal)) <= 4 * count &&
                reads.Count(path => !path.Contains('/')) <= 3,
                "Carried work has at most one new-path read per descendant and three constant endpoint reads.");
        }
        Check.Require(remoteCalls.Moves == (shape == "woken" ? 0 : oneFolder ? 1 : count),
            "Each user move has exactly one counterpart move, never a descendant decomposition.");
        Check.Require(remoteCalls.Publishes == 0 && remoteCalls.Deletes == 0 && localCalls.Deletes == 0,
            "Moves neither upload nor delete content.");
        if (!oneFolder) Check.Require(batches == 1 && remoteCalls.Ensures == 1,
            "The genuinely new destination gets exactly one add-only batch and one folder creation.");
        foreach (var entry in involved.Where(entry => oneFolder || entry.Path != "Source"))
        {
            var expected = "Destination" + entry.Path["Source".Length..];
            var saved = fixture.Inner.SyncedById(PairId, Side.Local, entry.Id);
            Check.Require(saved?.LocalPath == expected && saved.DropboxPath == expected,
                "Every involved identity keeps both rebased bindings.");
        }
        Check.Require(fixture.Inner.Issues(PairId).Count == 0 && fixture.Events.All(item => item.Kind != RunEventKind.Failed),
            "The whole drained move opportunity completes without a hidden failure.");
        return counts;
    }
}
