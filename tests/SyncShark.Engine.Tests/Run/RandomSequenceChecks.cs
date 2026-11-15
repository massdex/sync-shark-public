using System.Text;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Replays fixed-seed user mutations through the real store, sources and selection executor.</summary>
public static class RandomSequenceChecks
{
    // The assignment fixes 200 seeds and at most 12 user mutations, not a product retry limit.
    private const int SeedCount = 200;
    private const int OperationKinds = 8;
    private const int MaximumSteps = 12;
    private static readonly DateTimeOffset BaselineTime = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Operations =
        ["create", "edit", "same-metadata edit", "delete", "rename", "folder move", "move in", "move out"];

    public static void Run()
    {
        Check.Require(!Scenario.LiveMode && Scenario.Copies == 1, "random-sequence requires the unscaled offline fixture");
        var failures = new List<Exception>();
        foreach (var seed in Enumerable.Range(0, SeedCount))
        {
            var steps = new List<string>();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Scenario.Cancellation);
            Scenario? scenario = null;
            try
            {
                Trace(steps, $"START seed={seed}; pid={Environment.ProcessId}; construct");
                scenario = Scenario.Begin("random-sequence-" + seed);
                RunSeed(scenario, seed, steps, cancellation.Token);
            }
            catch (Exception error)
            {
                scenario?.Fail(error);
                var replay = $"seed={seed}; steps=[{string.Join("; ", steps)}]";
                Console.WriteLine("FAIL random-sequence " + replay + ": " + error);
                Console.Out.Flush();
                failures.Add(new InvalidOperationException(replay, error));
            }
            finally
            {
                Trace(steps, $"seed={seed}:dispose");
                scenario?.Dispose();
            }
        }
        var cleanup = Scenario.TakeCleanupFailures();
        Check.Require(cleanup.Count == 0, string.Join(Environment.NewLine, cleanup));
        Check.Require(failures.Count == 0, $"random-sequence: {failures.Count}/{SeedCount} seeds failed; see replay lines");
        Console.WriteLine($"PASS random-sequence: {SeedCount} seeds, all eight mutations, split source pages, shuffled sides");
    }

    private static void RunSeed(Scenario scenario, int seed, List<string> steps, CancellationToken ct)
    {
        Trace(steps, "arrange");
        ct.ThrowIfCancellationRequested();
        var random = new Random(seed);
        var sides = Enumerable.Range(0, OperationKinds).Select(_ => (Side)random.Next(2)).ToArray();
        scenario.SetExclusions(new Exclusion(Side.Local, "Outside", true), new Exclusion(Side.Dropbox, "Outside", true));
        scenario.LocalFolder("Outside");
        scenario.DropboxFolder("Outside");
        for (var kind = 1; kind < OperationKinds; kind++)
            if (kind != 6) scenario.LocalFile(PathFor(kind), Content(seed, kind), BaselineTime);
        if (sides[6] == Side.Local) scenario.LocalFile("Outside/In.txt", Content(seed, 6), BaselineTime);
        else scenario.DropboxFile("Outside/In.txt", Content(seed, 6), BaselineTime);
        Trace(steps, "baseline:source-bases");
        scenario.Host.EnsureSourceBasesAsync(scenario.Pair.PairId, ct).GetAwaiter().GetResult();
        Trace(steps, "baseline:repair");
        scenario.InSession("random-baseline", session =>
        {
            var summary = Repair.RunAsync(session, ct).GetAwaiter().GetResult();
            Check.Require(summary.Completed, "baseline Repair completed");
        });
        ct.ThrowIfCancellationRequested();
        var expected = new Dictionary<Side, Dictionary<string, string>>
        {
            [Side.Local] = TextFiles(scenario.LocalFiles()),
            [Side.Dropbox] = TextFiles(scenario.DropboxFiles()),
        };
        var paths = expected.Values.SelectMany(files => files.Keys).ToHashSet(StringComparer.Ordinal);
        var acquired = new HashSet<long>();
        var order = Enumerable.Range(0, OperationKinds)
            .Concat(Enumerable.Repeat(1, random.Next(MaximumSteps - OperationKinds + 1)))
            .OrderBy(_ => random.Next()).ToArray();
        scenario.InSession("random-sequence", session =>
        {
            for (var index = 0; index < order.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var kind = order[index];
                var side = sides[kind];
                var path = kind == 6 ? "Outside/In.txt" : PathFor(kind);
                var target = kind switch
                {
                    4 => "Item4/Renamed.txt",
                    5 => "Moved5/File.txt",
                    6 => PathFor(kind),
                    7 => "Outside/Out.txt",
                    _ => path,
                };
                var content = Content(seed, OperationKinds + index);
                Trace(steps,$"{index}:{side}:{Operations[kind]}:{path}->{target}:{content}");
                paths.Add(path);
                paths.Add(target);
                if (kind is 0 or 1 or 2)
                {
                    if (side == Side.Local)
                        scenario.LocalFile(path, content, kind == 2 ? BaselineTime : BaselineTime.AddMinutes(index + 1));
                    else if (kind == 0) scenario.DropboxFile(path, content, BaselineTime);
                    else scenario.EditRemoteFile(path, content);
                    foreach (var files in expected.Values) files[path] = content;
                }
                else if (kind == 3)
                {
                    if (side == Side.Local) scenario.RemoveLocal(path); else scenario.RemoveDropbox(path);
                    foreach (var files in expected.Values) files.Remove(path);
                }
                else
                {
                    var from = kind == 5 ? "Item5" : path;
                    var to = kind == 5 ? "Moved5" : target;
                    paths.Add(from);
                    paths.Add(to);
                    if (side == Side.Local) scenario.RenameLocal(from, to); else scenario.RenameDropbox(from, to);
                    foreach (var expectedSide in Enum.GetValues<Side>())
                    {
                        var files = expected[expectedSide];
                        files.Remove(path);
                        if (kind != 7 || expectedSide == side) files[target] = Content(seed, kind);
                    }
                }

                // Rename tombstone and destination arrive on separate committed feed pages.
                if (side == Side.Dropbox && kind >= 3)
                {
                    scenario.QueueRemoteMove(kind == 5 ? "Item5" : path);
                    Capture(session, random, steps, paths, acquired, ct);
                    if (random.Next(2) == 0) SelectOne(session, steps, ct);
                    if (kind != 3)
                        scenario.QueueRemoteChanges(kind == 5 ? ["Moved5", target] : [target]);
                }
                else if (side == Side.Dropbox) scenario.QueueRemoteChanges(path);
                Capture(session, random, steps, paths, acquired, ct);
                if (random.Next(2) == 0) SelectOne(session, steps, ct);
            }
            Settle(session, steps, ct);
            Capture(session, random, steps, paths, acquired, ct);
            Settle(session, steps, ct);
            Check.Require(acquired.Count != 0, "I1: the sequence acquired real durable rows");
            var remaining = Pending(session, paths).ToArray();
            var issues = session.Store.Issues(session.Pair.PairId);
            foreach (var selection in remaining)
            {
                var work = session.Store.ReadWork(selection).ToArray();
                Check.Require(work.Length != 0 && work.All(change => issues.Any(issue => !issue.Dismissed &&
                        issue.Subject.Items.Any(item => item.Side == change.Notice.Side &&
                            item.Id == change.Notice.Id && item.Id is not null))),
                    $"I1: acquired row has neither retirement nor an exact-identity visible issue; anchor={selection.AnchorWorkId}; key={selection.Key}");
            }
            foreach (var side in Enum.GetValues<Side>())
            {
                var actual = TextFiles(side == Side.Local ? scenario.LocalFiles() : scenario.DropboxFiles());
                foreach (var path in actual.Keys.Union(expected[side].Keys))
                {
                    var matches = actual.GetValueOrDefault(path) == expected[side].GetValueOrDefault(path);
                    var visible = issues.Any(issue => !issue.Dismissed && issue.Subject.Items.Any(item =>
                        item.Side == side && PathNames.IsSelfOrUnderKey(PathNames.KeyFor(side, path), PathNames.KeyFor(side, item.Path))));
                    Check.Require(matches || visible,
                        $"I2/I3: last content or applied removal/move missing without a visible notice; side={side}; path={path}; expected={expected[side].GetValueOrDefault(path, "<absent>")}; actual={actual.GetValueOrDefault(path, "<absent>")}");
                }
            }
        });
    }

    private static void Capture(PairSession session, Random random, List<string> steps,
        HashSet<string> paths, HashSet<long> acquired, CancellationToken ct)
    {
        var first = (Side)random.Next(2);
        Trace(steps,"capture:" + first + "-first");
        foreach (var side in new[] { first, first == Side.Local ? Side.Dropbox : Side.Local })
        {
            if (side == Side.Local)
                session.Host.CatchUpLocalAsync(session.Pair.PairId, session.ExpectedGeneration, ct).GetAwaiter().GetResult();
            else session.Host.CatchUpDropboxAsync(session.Registration, session.Pair.PairId,
                session.ExpectedGeneration, ct).GetAwaiter().GetResult();
            foreach (var selection in Pending(session, paths))
                foreach (var work in session.Store.ReadWork(selection)) acquired.Add(work.WorkId);
        }
    }

    private static IEnumerable<WorkSelection> Pending(PairSession session, HashSet<string> paths) =>
        Enum.GetValues<Side>().SelectMany(side => session.Store.PendingAncestors(session.Pair.PairId,
            session.ExpectedGeneration, side, paths.SelectMany(path =>
                PathNames.Ancestors(PathNames.KeyFor(side, path)).Prepend(PathNames.KeyFor(side, path)))
                .Distinct(StringComparer.Ordinal).ToArray()));

    private static void SelectOne(PairSession session, List<string> steps, CancellationToken ct)
    {
        var selected = session.Store.SelectWork(session.Pair.PairId, session.ExpectedGeneration, session.Scope);
        if (selected is null) return;
        Trace(steps,$"select:{selected.Side}:{selected.Key}:{selected.AnchorWorkId}");
        Settle(session, steps, ct);
    }

    private static void Settle(PairSession session, List<string> steps, CancellationToken ct)
    {
        session.Store.EndWorkPass(session.Pair.PairId);
        Trace(steps, "settle:start");
        session.Syncer.SyncWorkAsync(ct).GetAwaiter().GetResult();
        Trace(steps,"settle:production-opportunity-completed");
    }

    private static void Trace(List<string> steps, string step)
    {
        steps.Add(step);
        Console.WriteLine(step);
        Console.Out.Flush();
    }

    private static string PathFor(int kind) => $"Item{kind}/File.txt";
    private static string Content(int seed, int version) => $"s{seed:D3}v{version:D3}";
    private static Dictionary<string, string> TextFiles(IReadOnlyDictionary<string, byte[]> files) =>
        files.ToDictionary(item => item.Key, item => Encoding.UTF8.GetString(item.Value), StringComparer.Ordinal);
}
