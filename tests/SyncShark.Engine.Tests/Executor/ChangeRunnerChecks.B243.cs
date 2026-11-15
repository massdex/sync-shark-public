using System.Collections.Concurrent;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void SerialLaneLifetime() => SerialLaneLifetimeAsync().GetAwaiter().GetResult();

    private static async Task SerialLaneLifetimeAsync()
    {
        foreach (var real in new[] { false, true })
        {
            await ReturnsToParallelAsync(real);
            await PassBoundedAtStartAsync(real);
            await SuccessiveAncestorsAsync(real);
            await LaterRenamePrerequisiteAsync(real);
            foreach (var mode in new[] { "recovers", "single", "persists" })
                await CompetitionAfterSwitchAsync(real, mode);
        }
    }

    private static Syncer LaneSyncer(Fixture fixture, WorkSelection selection, Boundary boundary,
        Action<RunEvent> events, bool batch = false)
    {
        var context = fixture.Context(selection, new FakeAdmission { Level = 2 }) with { Events = events };
        var ports = new SyncPorts(new RecoveryBoundary(), boundary, RefusingExecutionBoundary.Instance, 1);
        if (batch) ports = ports with { RunBatchAsync = (_, _, _, _, _, _) => Task.CompletedTask };
        return new Syncer(context, ports);
    }

    private static async Task ReturnsToParallelAsync(bool real)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: real);
        var selected = SeedRetryFile(fixture, "First");
        SeedRetryFile(fixture, "Second");
        ClearObligations(fixture);
        var attempts = new ConcurrentDictionary<string, int>();
        var trace = new ConcurrentQueue<string>();
        var captures = new ConcurrentQueue<(string Key, int Attempt, int Pass, int InFlight)>();
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remainder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialCount = 0;
        var remainderCount = 0;
        var inFlight = 0;
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.EndWorkPass) || fixture.Hooks.Count(method.Name) != 1) return;
            foreach (var path in new[] { "Third", "Fourth", "Fifth" }) SeedRetryFile(fixture, path);
        };
        var boundary = new Boundary(_ => new Lease())
        {
            Capture = async (work, _, token) =>
            {
                var attempt = attempts.AddOrUpdate(work.Key, 1, (_, n) => n + 1);
                var active = Interlocked.Increment(ref inFlight);
                captures.Enqueue((work.Key, attempt, fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) + 1, active));
                trace.Enqueue(work.Key + "#" + attempt);
                try
                {
                    if (work.Key == PathNames.DropboxKey("First") || work.Key == PathNames.DropboxKey("Second"))
                    {
                        if (attempt == 1)
                        {
                            if (Interlocked.Increment(ref initialCount) == 2) initial.TrySetResult();
                            await initial.Task.WaitAsync(token);
                        }
                        if (attempt <= 2) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
                    }
                    if (work.Key == PathNames.DropboxKey("Fourth") || work.Key == PathNames.DropboxKey("Fifth"))
                    {
                        if (Interlocked.Increment(ref remainderCount) == 2) remainder.TrySetResult();
                        await remainder.Task.WaitAsync(token);
                    }
                    return Coverage(work);
                }
                finally { Interlocked.Decrement(ref inFlight); }
            }
        };
        var modes = new List<string>();
        await LaneSyncer(fixture, selected, boundary, item =>
        {
            if (item.What is nameof(TextKey.LanesSerial) or nameof(TextKey.LanesParallel))
            {
                modes.Add(item.What);
                trace.Enqueue(item.What);
            }
        }).SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var order = trace.ToList();
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) == 4 &&
            modes.SequenceEqual([nameof(TextKey.LanesSerial), nameof(TextKey.LanesParallel)]) &&
            order.IndexOf(PathNames.DropboxKey("Third") + "#1") < order.IndexOf(nameof(TextKey.LanesParallel)) &&
            order.IndexOf(nameof(TextKey.LanesParallel)) < order.IndexOf(PathNames.DropboxKey("Fourth") + "#1") &&
            captures.Where(c => c.Pass == 2 && (c.Attempt == 2 || c.Key == PathNames.DropboxKey("Third")))
                .All(c => c.InFlight == 1) && captures.Any(c => c.Key == PathNames.DropboxKey("Fifth") && c.InFlight == 2),
            "serial fallback ends after the first progressing traversal, before the parallel remainder; real=" + real);
        foreach (var path in new[] { "First", "Second", "Third", "Fourth", "Fifth" })
            Check.Require(attempts[PathNames.DropboxKey(path)] == (path is "First" or "Second" ? 3 : 1) &&
                await fixture.Local.ReadOneAsync(path, default) is not null, "the expected attempt retires " + path);
        Check.Require(fixture.Inner.Issues(PairId).Count == 0, "contention raises no issues");
    }

    private static async Task PassBoundedAtStartAsync(bool real)
    {
        using var fixture = new Fixture(realStore: real);
        var selected = SeedRetryFile(fixture, "First");
        ClearObligations(fixture);
        var arrivalPass = 0;
        var seeded = false;
        var boundary = new Boundary(_ => new Lease())
        {
            Capture = (work, _, _) =>
            {
                if (!seeded)
                {
                    seeded = true;
                    SeedRetryFile(fixture, "Arrival");
                }
                else if (work.Key == PathNames.DropboxKey("Arrival"))
                    arrivalPass = fixture.Hooks.Count(nameof(IStateStore.EndWorkPass));
                return Task.FromResult(Coverage(work));
            }
        };
        await LaneSyncer(fixture, selected, boundary, _ => { }).SyncWorkAsync(default);
        Check.Require(arrivalPass == 1 && fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) == 3,
            "a newly arrived top-level row waits for the next finite pass; real=" + real);
    }

    private static async Task SuccessiveAncestorsAsync(bool real)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: real);
        var parent = FolderEntry("Parent", "remote-parent");
        var child = RemoteEntry("Parent/Child", "remote-child");
        fixture.Dropbox.Seed(parent, null);
        fixture.Dropbox.Seed(child, [1]);
        var selected = SeedRemote(fixture, child);
        var ancestors = new List<long> { SeedRemote(fixture, parent).AnchorWorkId };
        ClearObligations(fixture);
        long watermark = 0;
        var begun = new List<(long Anchor, int Pass)>();
        var completed = new List<(long Anchor, int Pass)>();
        var captured = new ConcurrentQueue<(long Anchor, int Pass)>();
        int Pass() => fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) + 1;
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (method.Name != nameof(IStateStore.CompleteWork) || args[0] is not WorkSelection work ||
                work.Key != PathNames.DropboxKey("Parent") || ancestors.Count > 2) return;
            var next = SeedRemote(fixture, parent);
            Check.Require(next.AnchorWorkId > watermark &&
                PendingAt(fixture, Side.Dropbox, "Parent").Any(owner => owner.AnchorWorkId == work.AnchorWorkId),
                "the new ancestor is beyond the pass but the old row owns the path until retirement");
            ancestors.Add(next.AnchorWorkId);
        };
        fixture.Hooks.AfterCall = (method, args, result) =>
        {
            if (method == nameof(IStateStore.SelectWork) && result is WorkSelection selectedWork) watermark = selectedWork.ThroughWorkId;
            if (method == nameof(IStateStore.BeginSelection) && result is WorkSelection work) begun.Add((work.AnchorWorkId, Pass()));
            if (method != nameof(IStateStore.CompleteWork) || result is not true || args[0] is not WorkSelection done) return;
            completed.Add((done.AnchorWorkId, Pass()));
            var index = ancestors.IndexOf(done.AnchorWorkId);
            if (index >= 0 && index + 1 < ancestors.Count)
                Check.Require(PendingAt(fixture, Side.Dropbox, "Parent") is [var owner] &&
                    owner.AnchorWorkId == ancestors[index + 1], "retirement transfers ownership to the next ancestor");
        };
        var boundary = new Boundary(_ => new Lease())
        {
            Capture = (work, _, _) =>
            {
                captured.Enqueue((work.AnchorWorkId, Pass()));
                return Task.FromResult(Coverage(work));
            }
        };
        await LaneSyncer(fixture, selected, boundary, _ => { }, batch: true).SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(ancestors.Count == 3 && ancestors.Select((anchor, i) => begun.Contains((anchor, i + 1)) &&
            completed.Contains((anchor, i + 1))).All(value => value) &&
            begun.Where(item => item.Anchor == selected.AnchorWorkId).All(item => item.Pass == 3) &&
            captured.Contains((ancestors[0], 1)) && captured.Contains((selected.AnchorWorkId, 3)) &&
            completed.IndexOf((ancestors[2], 3)) < completed.IndexOf((selected.AnchorWorkId, 3)) &&
            fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) == 4 &&
            await fixture.Local.ReadOneAsync(child.Path, default) is not null && fixture.Inner.Issues(PairId).Count == 0,
            "successive ancestors each wait for their own pass before the child proceeds; real=" + real);
    }

    private static async Task LaterRenamePrerequisiteAsync(bool real)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: real);
        ClearObligations(fixture);
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        var a = LocalEntry("A", "local-a");
        var b = LocalEntry("B", "local-b");
        var ar = RemoteEntry("A", "remote-a");
        var br = RemoteEntry("B", "remote-b");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(a, ar), SyncedEntries.From(b, br)] });
        fixture.Local.Seed(a, [1]);
        fixture.Local.Seed(b, [1]);
        fixture.Dropbox.Seed(ar with { Path = "B" }, [1]);
        fixture.Dropbox.Seed(br with { Path = "C" }, [1]);
        fixture.DropboxView = new HookedSide(fixture.Dropbox, identity: async (id, ct) =>
        {
            var path = id == br.Id ? "C" : "B";
            var entry = await fixture.Dropbox.ReadOneAsync(path, ct);
            return entry?.Id == id ? (new IdentityLocation(Side.Dropbox.ToString(), path, entry), true) : null;
        });
        var dependent = SeedRemote(fixture, ar with { Path = "B" },
            new ChangeNotice(Side.Dropbox, ChangeKind.Renamed, "B", "A", ar.Id, ar with { Path = "B" }, false, false));
        SeedRetryFile(fixture, "Other.txt");
        WorkSelection? occupant = null;
        Lease? dependentLease = null;
        long watermark = 0;
        var events = new List<RunEvent>();
        var begun = new List<(long Anchor, int Pass)>();
        var completed = new List<(long Anchor, int Pass)>();
        var closed = new List<(long Anchor, int Pass)>();
        int Pass() => fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) + 1;
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            if (occupant is not null || method.Name != nameof(IStateStore.PendingAncestors) ||
                dependentLease is not { Disposed: true } || args[3] is not IReadOnlyList<string> keys ||
                !keys.SequenceEqual([PathNames.DropboxKey("B")])) return;
            occupant = SeedRemote(fixture, br with { Path = "C" },
                new ChangeNotice(Side.Dropbox, ChangeKind.Renamed, "C", "B", br.Id, br with { Path = "C" }, false, false));
            Check.Require(occupant.AnchorWorkId > watermark && Pass() == 1 &&
                events.All(item => item.Error is not LiveAdmissionStaleException),
                "the moving prerequisite arrives after the dependent admission, beyond the pass watermark");
        };
        fixture.Hooks.AfterCall = (method, args, result) =>
        {
            if (method == nameof(IStateStore.SelectWork) && result is WorkSelection selected) watermark = selected.ThroughWorkId;
            if (method == nameof(IStateStore.BeginSelection) && result is WorkSelection work) begun.Add((work.AnchorWorkId, Pass()));
            if (method == nameof(IStateStore.CompleteWork) && result is true && args[0] is WorkSelection done)
                completed.Add((done.AnchorWorkId, Pass()));
            if (method == nameof(IStateStore.EndSelection) && args[0] is WorkSelection ended) closed.Add((ended.AnchorWorkId, Pass()));
        };
        var boundary = new Boundary(request =>
        {
            var lease = new Lease();
            if (request.Selection.Identity == ar.Id) dependentLease = lease;
            return lease;
        });
        await LiveSyncer(fixture, dependent, boundary, events).SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(occupant is not null && begun.Contains((dependent.AnchorWorkId, 1)) &&
            closed.Contains((dependent.AnchorWorkId, 1)) && begun.First(item => item.Anchor == occupant.AnchorWorkId).Pass == 2 &&
            completed.Contains((occupant.AnchorWorkId, 2)) && completed.Contains((dependent.AnchorWorkId, 2)) &&
            completed.IndexOf((occupant.AnchorWorkId, 2)) < completed.IndexOf((dependent.AnchorWorkId, 2)) &&
            (await fixture.Local.ReadOneAsync("B", default))?.Id == a.Id &&
            (await fixture.Local.ReadOneAsync("C", default))?.Id == b.Id && fixture.Inner.Issues(PairId).Count == 0,
            "a newer moving prerequisite waits for pass two, then its dependent follows without a notice; real=" + real);
    }

    private static async Task CompetitionAfterSwitchAsync(bool real, string mode)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: real);
        var selected = SeedRetryFile(fixture, "X1");
        SeedRetryFile(fixture, "X2");
        ClearObligations(fixture);
        var alone = false;
        var inFlight = 0;
        var leadReads = 0;
        var attempts = new ConcurrentDictionary<string, int>();
        var barriers = new ConcurrentDictionary<(string Pair, int Pass), (TaskCompletionSource Ready, HashSet<string> Entered)>();
        var captures = new ConcurrentQueue<(string Key, int Attempt, int Pass, int InFlight, bool Alone)>();
        var trace = new ConcurrentQueue<string>();
        var modes = new List<string>();
        var ends = new List<bool>();
        var complete = new List<(string Key, int Pass)>();
        int Pass() => fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) + 1;
        fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            ReadFault = path => path.StartsWith("Lead", StringComparison.Ordinal) &&
                (mode == "persists" || Interlocked.Increment(ref leadReads) == 1)
                ? new ItemFailedException(path, Text.ReadFailedOn(Side.Dropbox), null) : null
        };
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.EndWorkPass)) return;
            var n = fixture.Hooks.Count(method.Name);
            if (mode == "persists" && n == 8) guard.Cancel();
            if (mode != "persists" && n != 1) return;
            foreach (var path in mode == "persists" ? new[] { "Lead" + n, "C" + n + "a", "C" + n + "b" }
                         : mode == "single" ? new[] { "Lead", "C1" } : new[] { "Lead", "C1", "C2" })
                SeedRetryFile(fixture, path);
        };
        fixture.Hooks.AfterCall = (method, args, result) =>
        {
            if (method == nameof(IStateStore.EndWorkPass)) ends.Add((bool)result!);
            if (method == nameof(IStateStore.CompleteWork) && result is true && args[0] is WorkSelection work)
            {
                complete.Add((work.Key, Pass()));
                trace.Enqueue("complete:" + work.Key + ":" + Pass());
            }
        };
        var boundary = new Boundary(_ => new Lease())
        {
            Capture = async (work, _, token) =>
            {
                var attempt = attempts.AddOrUpdate(work.Key, 1, (_, n) => n + 1);
                var active = Interlocked.Increment(ref inFlight);
                var pass = Pass();
                captures.Enqueue((work.Key, attempt, pass, active, alone));
                trace.Enqueue("capture:" + work.Key + ":" + pass);
                try
                {
                    var x = work.Key.StartsWith(PathNames.DropboxKey("X"), StringComparison.Ordinal);
                    var contender = work.Key.StartsWith(PathNames.DropboxKey("C"), StringComparison.Ordinal);
                    if (!alone && (x || contender && mode != "single"))
                    {
                        var pair = x ? "X" : mode == "persists" ? work.Key[..^1] : "C";
                        var barrier = barriers.GetOrAdd((pair, pass), _ =>
                            (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), []));
                        lock (barrier.Entered)
                            if (barrier.Entered.Add(work.Key) && barrier.Entered.Count == 2) barrier.Ready.TrySetResult();
                        await barrier.Ready.Task.WaitAsync(token);
                    }
                    if ((x || contender) && (mode == "persists" || attempt <= (x ? 2 : mode == "single" ? 3 : 1)))
                        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
                    return Coverage(work);
                }
                finally { Interlocked.Decrement(ref inFlight); }
            }
        };
        await LaneSyncer(fixture, selected, boundary, item =>
        {
            if (item.What is not (nameof(TextKey.LanesSerial) or nameof(TextKey.LanesParallel))) return;
            alone = item.What == nameof(TextKey.LanesSerial);
            modes.Add(item.What);
            trace.Enqueue(item.What);
        }).SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var label = "; real=" + real + "; mode=" + mode;
        Check.Require(ends.SequenceEqual(mode == "persists" ? new[] { false, false, false } : [false, false, true, false]) &&
            modes.SequenceEqual([nameof(TextKey.LanesSerial), nameof(TextKey.LanesParallel),
                nameof(TextKey.LanesSerial), nameof(TextKey.LanesParallel)]) && captures.Where(c => c.Pass == 3).All(c => c.Alone),
            "a competed switched remainder gets one all-serial convergence pass" + label);
        var order = trace.ToList();
        var lead = PathNames.DropboxKey(mode == "persists" ? "Lead1" : "Lead");
        var contenderKey = PathNames.DropboxKey(mode == "persists" ? "C1a" : "C1");
        var switchIndex = order.FindIndex(item => item.StartsWith(nameof(TextKey.LanesParallel), StringComparison.Ordinal));
        Check.Require(complete.Contains((lead, 2)) && order.IndexOf("complete:" + lead + ":2") < switchIndex &&
            switchIndex < order.IndexOf("capture:" + contenderKey + ":2") &&
            captures.Any(c => c.Key == contenderKey && c.Pass == 2 && !c.Alone) &&
            (mode == "single" ? captures.Any(c => c.Key == contenderKey && c.Pass == 2 && c.InFlight == 1)
                : captures.Any(c => c.Pass == 2 && c.InFlight == 2)),
            "selection retirement switches mode even when the whole pass has no net retirement" + label);
        if (mode == "persists")
        {
            Check.Require(!guard.IsCancellationRequested && complete.Contains((PathNames.DropboxKey("Lead2"), 3)) &&
                !complete.Contains((lead, 3)) &&
                order.FindLastIndex(item => item.StartsWith(nameof(TextKey.LanesParallel), StringComparison.Ordinal)) >
                    order.IndexOf("capture:" + PathNames.DropboxKey("C2b") + ":3") &&
                fixture.Inner.Issues(PairId) is [{ Kind: IssueKind.Failure }, { Kind: IssueKind.Failure }],
                "recurring net-zero retirement cannot keep the dry streak running" + label);
            foreach (var path in new[] { "Lead3", "C3a", "C3b" })
                Check.Require(PendingAt(fixture, Side.Dropbox, path).Any() &&
                    fixture.Hooks.BegunSelections.All(work => work.Key != PathNames.DropboxKey(path)),
                    "final-pass arrivals stay pending and unselected: " + path + label);
            foreach (var path in new[] { "X1", "X2", "Lead1", "Lead2", "C1a", "C1b", "C2a", "C2b" })
                Check.Require(await fixture.Local.ReadOneAsync(path, default) is null, "persistent failures do not sync " + path);
        }
        else
        {
            foreach (var path in mode == "single" ? new[] { "X1", "X2", "Lead" } : ["X1", "X2", "Lead", "C1", "C2"])
                Check.Require(await fixture.Local.ReadOneAsync(path, default) is not null, "recovered work syncs: " + path + label);
            Check.Require(attempts[PathNames.DropboxKey("X1")] == 3 && attempts[PathNames.DropboxKey("X2")] == 3 &&
                attempts[PathNames.DropboxKey("C1")] == (mode == "single" ? 3 : 2), "exact refusal attempts" + label);
            if (mode == "single") Check.Require(PendingAt(fixture, Side.Dropbox, "C1").Any(), "lone parallel refusal waits for a real wake");
            else Check.Require(attempts[PathNames.DropboxKey("C2")] == 2 &&
                new[] { "X1", "X2", "Lead", "C1", "C2" }.All(path => !PendingAt(fixture, Side.Dropbox, path).Any()),
                "all recovered rows retire without another wake");
        }
    }
}
