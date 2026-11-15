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
        var oneFolder = shape is "ancestor" or "repair" or "carried";
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
        var reads = new List<string>();
        var localCalls = new CountingSide(fixture.Local);
        var remoteCalls = new CountingSide(new HookedSide(fixture.Dropbox, beforeRead: reads.Add));
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
        fixture.Hooks.BeforeCallWith = (method, _) => queried[method.Name] = queried.GetValueOrDefault(method.Name) + 1;
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
        else await UntilDrainedAsync(new Syncer(context, observed), default);
        fixture.Hooks.BeforeCallWith = null;
        var counts = new long[] { localCalls.Listings, remoteCalls.Listings, localCalls.Reads, remoteCalls.Reads,
            remoteCalls.Moves, remoteCalls.Publishes, remoteCalls.Ensures, remoteCalls.Deletes, batches,
            queried.GetValueOrDefault(nameof(IStateStore.SyncedById)),
            queried.GetValueOrDefault(nameof(IStateStore.UnreadableUnder)),
            queried.GetValueOrDefault(nameof(IStateStore.RequireDiscovery)),
            reads.Count(path => path.StartsWith("Source/", StringComparison.Ordinal)) };
        Console.WriteLine($"Moved-tree shape={shape} real={real} count={count} background={background} values=" +
            string.Join(",", counts));
        Check.Require(remoteCalls.Moves == (oneFolder ? 1 : count),
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
