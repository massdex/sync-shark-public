namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    // The background index is a host-lifetime lane over the SAME SQLite state as a sync run. These checks
    // drive the real LocalIndexingRuns host and its RunAsync entry point; only the per-root native
    // verification pass is supplied through the composition seam, so the fixture never hashes bytes here.
    // The durable handoff itself (local_rehash) is the real store table.
    private static async Task IndexingRunsAsync(string folder)
    {
        await IndexIndependentOfCompletedSyncAsync(Path.Combine(folder, "index-sync-independent"));
        await IndexPerRootStopAndResumeAsync(Path.Combine(folder, "index-per-root"));
        await IndexDirectRunCancellationAsync(Path.Combine(folder, "index-direct"));
        await IndexPausedOutcomeAsync(Path.Combine(folder, "index-paused"));
        await IndexFailureDoesNotWakeAsync(Path.Combine(folder, "index-failure"));
        await IndexUnsupportedStoreAsync(Path.Combine(folder, "index-unsupported"));
        Console.WriteLine("PASS background index lifecycle: blocked debt independent of sync, per-root stop, durable resume, positive outcomes");
    }

    // Invariant: a blocked background hash is durable state that a COMPLETED sync neither waits for nor
    // clears; when the block is released the index finishes the local_rehash handoff and wakes the pair.
    private static async Task IndexIndependentOfCompletedSyncAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var local = new RootDef("index-sync-root", SyncProviders.Local, null, Path.Combine(folder, "root"),
            "id:index-sync-root");
        var remote = new RootDef("index-sync-remote", SyncProviders.Dropbox, "acc", "/remote",
            "id:index-sync-remote");
        var pair = new PairDef("index-sync-pair", "index-sync-pair", local, remote, [], [], true);
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var a = new FakeEndpoint(Side.A, local.RootId);
        var b = new FakeEndpoint(Side.B, remote.RootId);
        var synced = a.Seed("folder/keep.txt", "keep");
        b.Seed("folder/keep.txt", "keep");
        var blocked = a.Seed("pending/held.txt", "held");
        b.Seed("pending/held.txt", "held");
        var gen = store.BeginGeneration(local.RootId, "full");
        store.WriteSnapshot(new Snapshot(local.RootId, Side.A, gen, "0000000000000001:200", [synced, blocked], []));
        store.CompleteGeneration(gen, "0000000000000001:200", true);
        var held = store.Live(local.RootId, [blocked.Path]).Single();
        store.RequireRehash(local.RootId, held,
            "content verification blocked while another process holds the file");
        var protectedPaths = store.ProtectionBatch(local.RootId, ItemFlags.NeedsRehash, [held.Path, "pending"]);
        LocalChecks.Require(store.RehashPendingCount(local.RootId) == 1 &&
            store.HasPendingRehashVerification(local.RootId) &&
            store.RehashTouches(local.RootId, held.Path, false) &&
            store.RehashTouches(local.RootId, "pending", true) &&
            protectedPaths[0].Covered && protectedPaths[0].Contains && protectedPaths[1].Contains,
            "blocked background hash is durable and protects its path and containing folder");

        var provider = new FakeEndpointProvider()
            .AddInventory(local, new ScriptedInventory(a))
            .AddInventory(remote, b)
            .AddWriter(local, new CountingWriter(a))
            .AddWriter(remote, new CountingWriter(b));
        var settings = StorageChecks.Fast with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), provider,
            settings, TimeProvider.System);

        var io = new GapCheckIo(DateTimeOffset.UtcNow, "0000000000000001:200");
        await using var worker = new LocalGapWorker((ILocalGapStore)store, io, StorageFailureClassifier.IsStateDamage);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passes = 0;
        var wakes = new List<string>();
        var indexing = new LocalIndexingRuns(store, async (root, ct) =>
        {
            Interlocked.Increment(ref passes);
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return CompleteIndexWork(store, root.RootId);
        }, worker, io, _ => new NtfsUsnCheckpoint(1, 0, 200, 0));
        await using (indexing)
        {
            indexing.Start(_ => false, id => { lock (wakes) wakes.Add(id); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            LocalChecks.Require(Volatile.Read(ref passes) == 1 && store.RehashPendingCount(local.RootId) == 1,
                "the background index waits inside its blocked per-root pass");
            // One bounded manual pass never joins a root the continuous host is already running.
            var pending = await indexing.RunAsync(local, default).WaitAsync(TimeSpan.FromSeconds(2));
            LocalChecks.Require(pending == "pending" && Volatile.Read(ref passes) == 1,
                "a manual pass on an already-running root reports pending immediately without releasing the blocker");

            // A real incremental sync over the same pair and store must complete without joining that task.
            var report = await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default)
                .WaitAsync(TimeSpan.FromSeconds(15));
            LocalChecks.Require(report.Failed == 0 && report.Outcome != RunOutcomes.Stopped,
                "a completed sync does not join or stall on the blocked background index");
            LocalChecks.Require(Volatile.Read(ref passes) == 1 && store.RehashPendingCount(local.RootId) == 1 &&
                (store.Live(local.RootId, [held.Path]).Single().Flags & ItemFlags.NeedsRehash) != 0 &&
                store.HasPendingRehashVerification(local.RootId) &&
                (store.Live(local.RootId, [held.Path]).Single().Hash is null),
                "sync completion leaves the blocked hash and its protection untouched");

            gate.SetResult();
            // The completed sync can already have queued the opposite root's held counterpart.
            // Wait for this root's committed handoff, not a pair-wide row count that predates release.
            await WaitForIndexAsync(() => store.IndexPlanningWatermark(local.RootId) > 0 &&
                store.RehashPendingCount(local.RootId) == 0,
                "the released background pass to publish the blocked entity");
            var verified = store.Live(local.RootId, [held.Path]).Single();
            var intake = store.AdmitDeferredPlanning(pair.PairId, long.MaxValue, rotate: false)
                .Where(row => row.RootId == local.RootId).ToArray();
            LocalChecks.Require((verified.Flags & ItemFlags.NeedsRehash) == 0 && verified.Hash == new string('b', 64) &&
                store.RehashPendingCount(local.RootId) == 0 && intake.Length == 1 &&
                intake[0].Paths.Contains(held.Path, StringComparer.OrdinalIgnoreCase) && intake[0].AllAliases &&
                store.IndexPlanningWatermark(local.RootId) > 0 &&
                !store.RehashTouches(local.RootId, held.Path, false) &&
                !store.RehashTouches(local.RootId, "pending", true),
                "the bounded index pass verifies and publishes the entity to the durable planning queue");
            await WaitForIndexAsync(() => { lock (wakes) { return wakes.Contains(pair.PairId); } },
                "successful publication to wake the pair for planning");
            LocalChecks.Require(Volatile.Read(ref passes) == 1,
                "one completed bounded pass clears the debt, so the index stops retrying");
            await indexing.StopAsync();
        }
    }

    // Invariant: removing one root from the active set cancels only that root's pass; cancellation never
    // drops durable work, and a fresh host resumes each root from the local_rehash frontier.
    private static async Task IndexPerRootStopAndResumeAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var (rootA, pairA) = SaveIndexRoot(store, folder, "index-stop-a", "index-pair-a",
            ["a/one.txt", "a/two.txt", "a/three.txt"]);
        var (rootB, pairB) = SaveIndexRoot(store, folder, "index-stop-b", "index-pair-b",
            ["b/one.txt", "b/two.txt", "b/three.txt"]);
        LocalChecks.Require(store.RehashPendingCount(rootA.RootId) == 3 &&
            store.RehashPendingCount(rootB.RootId) == 3,
            "both roots start with a durable background backlog");

        var io = new GapCheckIo(DateTimeOffset.UtcNow, "0000000000000001:200");
        await using var worker = new LocalGapWorker((ILocalGapStore)store, io, StorageFailureClassifier.IsStateDamage);
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new int[2];
        var indexing = new LocalIndexingRuns(store, async (root, ct) =>
        {
            var index = root.RootId == rootA.RootId ? 0 : 1;
            (index == 0 ? enteredA : enteredB).TrySetResult();
            try { await (index == 0 ? gateA : gateB).Task.WaitAsync(ct); }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled[index]);
                throw;
            }
            return CompleteIndexWork(store, root.RootId);
        }, worker, io, _ => new NtfsUsnCheckpoint(1, 0, 200, 0));
        await using (indexing)
        {
            indexing.Start(_ => false, _ => { });
            await enteredA.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await enteredB.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Disabling one pair removes only its root from the active set: the other root's pass keeps running.
            store.SavePair(pairA with { Enabled = false });
            // A persistent writer may not signal on its own; publish the indexing work change explicitly.
            IndexingWorkSignal.Publish(store);
            await WaitForIndexAsync(() => Volatile.Read(ref cancelled[0]) == 1,
                "the disabled root's pass to observe cancellation");
            LocalChecks.Require(Volatile.Read(ref cancelled[1]) == 0 &&
                store.RehashPendingCount(rootA.RootId) == 3 && store.RehashPendingCount(rootB.RootId) == 3,
                "per-root cancellation stops only the removed root and both durable frontiers survive");
            await indexing.StopAsync();
            LocalChecks.Require(Volatile.Read(ref cancelled[1]) == 1 &&
                store.RehashPendingCount(rootA.RootId) == 3 && store.RehashPendingCount(rootB.RootId) == 3,
                "host shutdown cancels the remaining root without losing durable work");
        }

        // A fresh host over the same store resumes each root from its durable frontier. Root B records the
        // partial progress of the first resumed pass, proving the second pass continues instead of restarting.
        store.SavePair(pairA);
        IndexingWorkSignal.Publish(store);
        var callSizes = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var resumed = new LocalIndexingRuns(store, (root, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var candidates = store.RehashCandidates(root.RootId, 128);
            var take = root.RootId == rootB.RootId ? Math.Min(2, candidates.Count) : candidates.Count;
            var done = 0;
            foreach (var item in candidates.Take(take))
            {
                var revision = store.RehashRevision(root.RootId, item.ObjectId);
                if (store.CompleteIndexingRehash(root.RootId, item, new string('b', 64), revision)) done++;
            }
            lock (callSizes)
            {
                if (!callSizes.TryGetValue(root.RootId, out var sizes))
                {
                    sizes = new List<int>();
                    callSizes[root.RootId] = sizes;
                }
                sizes.Add(done);
            }
            return Task.FromResult(done);
        }, worker, io, _ => new NtfsUsnCheckpoint(1, 0, 200, 0));
        await using (resumed)
        {
            resumed.Start(_ => false, _ => { });
            await WaitForIndexAsync(() => store.CountDeferredPlanning(pairA.PairId) == 3 &&
                store.CountDeferredPlanning(pairB.PairId) == 3,
                "both resumed roots to verify and publish their backlog");
            await resumed.StopAsync();
        }
        lock (callSizes)
        {
            LocalChecks.Require(callSizes[rootB.RootId].SequenceEqual(new[] { 2, 1 }),
                "a resumed root continues from its durable local_rehash frontier after a partial pass");
            LocalChecks.Require(callSizes[rootA.RootId].SequenceEqual(new[] { 3 }),
                "the re-enabled root indexes its whole durable backlog on resume");
        }
        LocalChecks.Require(!store.HasPendingRehashVerification(rootA.RootId) &&
            !store.HasPendingRehashVerification(rootB.RootId) &&
            store.RehashPendingCount(rootA.RootId) == 0 && store.RehashPendingCount(rootB.RootId) == 0 &&
            store.AdmitDeferredPlanning(pairA.PairId, long.MaxValue, rotate: false).All(row => row.AllAliases),
            "each resumed pass verifies and publishes into its own pair's durable planning queue");
    }

    // Invariant: the direct RunAsync entry point cancels on its caller token, retains the durable frontier,
    // and a later RunAsync finishes exactly that frontier.
    private static async Task IndexDirectRunCancellationAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var (root, pair) = SaveIndexRoot(store, folder, "index-direct-root", "index-direct-pair",
            ["direct/one.txt", "direct/two.txt", "direct/three.txt"]);
        LocalChecks.Require(store.RehashCandidates(root.RootId, 128).Count == 3 &&
            store.HasPendingRehashVerification(root.RootId),
            "the direct-run fixture starts behind its durable frontier");

        var io = new GapCheckIo(DateTimeOffset.UtcNow, "0000000000000001:200");
        await using var worker = new LocalGapWorker((ILocalGapStore)store, io, StorageFailureClassifier.IsStateDamage);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = 0;
        var blocked = new LocalIndexingRuns(store, async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
            return 0;
        }, worker, io, _ => new NtfsUsnCheckpoint(1, 0, 200, 0));
        await using (blocked)
        {
            using var stop = new CancellationTokenSource();
            var running = blocked.RunAsync(root, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            var outcome = RunOutcomes.Stopped;
            try { outcome = await running; }
            catch (OperationCanceledException) { }
            await WaitForIndexAsync(() => Volatile.Read(ref cancelled) == 1,
                "the cancelled pass to unwind before the store is inspected");
            LocalChecks.Require(outcome == RunOutcomes.Stopped && Volatile.Read(ref cancelled) == 1,
                "one bounded RunAsync reports stopped and forwards cancellation into the per-root pass");
            await blocked.StopAsync();
            LocalChecks.Require(store.RehashPendingCount(root.RootId) == 3 &&
                store.HasPendingRehashVerification(root.RootId),
                "cancelled RunAsync keeps every durable frontier row for the next host");
        }

        var resumed = new LocalIndexingRuns(store, (r, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(CompleteIndexWork(store, r.RootId));
        }, worker, io, _ => new NtfsUsnCheckpoint(1, 0, 200, 0));
        await using (resumed)
        {
            // One bounded pass per call; repeat until the service reports the frontier complete.
            var outcome = string.Empty;
            for (var pass = 0; pass < 4 && outcome != RunOutcomes.Complete; pass++)
                outcome = await resumed.RunAsync(root, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            LocalChecks.Require(outcome == RunOutcomes.Complete &&
                !store.HasPendingRehashVerification(root.RootId) &&
                store.RehashPendingCount(root.RootId) == 0 &&
                store.Live(root.RootId).All(item => (item.Flags & ItemFlags.NeedsRehash) == 0 &&
                    item.Hash == new string('b', 64)) &&
                store.CountDeferredPlanning(pair.PairId) == 3 &&
                store.AdmitDeferredPlanning(pair.PairId, long.MaxValue, rotate: false).All(row => row.AllAliases),
                "bounded RunAsync calls complete the cancelled frontier and publish it to the durable planning queue");
            await resumed.StopAsync();
        }
    }

    // F6 positive outcome: a paused pair is refused before any inventory work, using the same
    // persistent pause authority an ordinary run obeys (its writer publishes the work signal).
    private static async Task IndexPausedOutcomeAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var (root, pair) = SaveIndexRoot(store, folder, "index-paused-root", "index-paused-pair",
            ["paused/one.txt"]);
        store.SetPairPaused(pair.PairId, true);
        var io = new GapCheckIo(DateTimeOffset.UtcNow, "0000000000000001:200");
        await using var worker = new LocalGapWorker((ILocalGapStore)store, io, StorageFailureClassifier.IsStateDamage);
        var passes = 0;
        await using var indexing = new LocalIndexingRuns(store, (_, _) =>
        {
            Interlocked.Increment(ref passes);
            return Task.FromResult(0);
        }, worker, io, _ => new NtfsUsnCheckpoint(1, 0, 200, 0));
        var outcome = await indexing.RunAsync(root, default).WaitAsync(TimeSpan.FromSeconds(5));
        LocalChecks.Require(outcome == "paused" && Volatile.Read(ref passes) == 0 &&
            store.RehashPendingCount(root.RootId) == 1,
            "an explicit pass on a prepaused pair reports paused without touching inventory");
        await indexing.StopAsync();
    }

    // F6 positive outcome: a failed read records durable retry debt but publishes nothing, so the
    // pair wake callback is never invoked and the entity stays protected for the next bounded pass.
    private static async Task IndexFailureDoesNotWakeAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var (root, _) = SaveIndexRoot(store, folder, "index-failure-root", "index-failure-pair",
            ["failure/one.txt"]);
        var io = new GapCheckIo(DateTimeOffset.UtcNow, "0000000000000001:200");
        await using var worker = new LocalGapWorker((ILocalGapStore)store, io, StorageFailureClassifier.IsStateDamage);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = 0;
        var wakes = new List<string>();
        await using var indexing = new LocalIndexingRuns(store, (r, ct) =>
        {
            entered.TrySetResult();
            var failed = 0;
            foreach (var item in store.RehashCandidates(r.RootId, 128,
                store.RehashWatermark(r.RootId), needsVerification: true))
            {
                var revision = store.RehashRevision(r.RootId, item.ObjectId);
                if (store.FailIndexingRehash(r.RootId, item, revision, "injected read failure")) failed++;
            }
            Interlocked.Add(ref attempted, failed);
            return Task.FromResult(failed);
        }, worker, io, _ => new NtfsUsnCheckpoint(1, 0, 200, 0));
        indexing.Start(_ => false, id => { lock (wakes) wakes.Add(id); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForIndexAsync(() => store.NextIndexRetry(root.RootId) is { } due &&
            due > DateTimeOffset.UtcNow, "the failed read to record a durable retry deadline");
        await Task.Delay(200);
        int wakeCount;
        lock (wakes) wakeCount = wakes.Count;
        LocalChecks.Require(Volatile.Read(ref attempted) >= 1 &&
            store.HasPendingRehashVerification(root.RootId) &&
            store.RehashPendingCount(root.RootId) == 1 &&
            wakeCount == 0,
            "a failed read keeps debt and records a durable retry without waking planning");
        await indexing.StopAsync();
    }

    // F6 capability: a store without the bounded planning owner must refuse the hand-off, keep the
    // verified debt, start no inventory work and report unsupported rather than "complete".
    private static async Task IndexUnsupportedStoreAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var store = new InMemoryStateStore();
        var root = new RootDef("index-unsupported-root", SyncProviders.Local, null,
            Path.Combine(folder, "root"), "id:index-unsupported-root");
        var remote = new RootDef("index-unsupported-remote", SyncProviders.Dropbox, "acc", "/remote",
            "id:index-unsupported-remote");
        var pair = new PairDef("index-unsupported-pair", "unsupported", root, remote, [], [], true);
        store.SavePair(pair);
        var stamp = DateTimeOffset.UtcNow;
        var item = new ItemState("unsupported/one.txt", ItemKind.File,
            SyncLocalEndpointStore.ObjectId(1, 10), null, new string('a', 64), 3, stamp, ItemFlags.None);
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"),
            "0000000000000001:200", [item], []));
        var held = store.Live(root.RootId, [item.Path]).Single();
        store.RequireRehash(root.RootId, held, "verified debt at an unsupported store");
        LocalChecks.Require(store.CompleteRehash(root.RootId, held, new string('b', 64)) &&
            store.RehashPendingCount(root.RootId) == 1 &&
            !store.HasPendingRehashVerification(root.RootId),
            "the in-memory fixture holds verified debt with no planning owner");
        var passes = 0;
        // The unsupported paths never touch the gap worker or checkpoint; a native-free fake suffices.
        var nativeFree = new GapCheckIo(stamp, "0000000000000001:200");
        await using var indexing = new LocalIndexingRuns(store, (_, _) =>
        {
            Interlocked.Increment(ref passes);
            return Task.FromResult(0);
        }, null!, nativeFree, _ => new NtfsUsnCheckpoint(1, 0, 200, 0));
        indexing.Start(_ => false, _ => { });
        await Task.Delay(100);
        LocalChecks.Require(Volatile.Read(ref passes) == 0,
            "the unsupported service start performs no inventory work");
        var outcome = await indexing.RunAsync(root, default).WaitAsync(TimeSpan.FromSeconds(5));
        LocalChecks.Require(outcome == "unsupported" && Volatile.Read(ref passes) == 0 &&
            store.RehashPendingCount(root.RootId) == 1,
            "an unsupported store reports unsupported and retains verified rehash debt");
        var refused = false;
        try { store.PublishIndexPlanning(root.RootId, store.RehashCandidates(root.RootId, 8)); }
        catch (NotSupportedException) { refused = true; }
        LocalChecks.Require(refused && store.RehashPendingCount(root.RootId) == 1,
            "the unsupported planning hand-off refuses without dropping the retained debt");
        await indexing.StopAsync();
    }

    private static (RootDef Root, PairDef Pair) SaveIndexRoot(SqliteStateStore store, string folder,
        string rootId, string pairId, string[] paths)
    {
        var root = new RootDef(rootId, SyncProviders.Local, null, Path.Combine(folder, rootId), "id:" + rootId);
        var remote = new RootDef(rootId + "-remote", SyncProviders.Dropbox, "acc", "/" + rootId,
            "id:" + rootId + "-remote");
        var pair = new PairDef(pairId, pairId, root, remote, [], [], true);
        store.SavePair(pair);
        store.MarkFirstSyncDone(pairId);
        var stamp = DateTimeOffset.UtcNow;
        var rows = paths.Select((path, n) => new ItemState(path, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(1, (ulong)(n + 10)), null, new string('a', 64), 3, stamp,
            ItemFlags.None)).ToArray();
        var gen = store.BeginGeneration(rootId, "full");
        store.WriteSnapshot(new Snapshot(rootId, Side.A, gen, "0000000000000001:200", rows, []));
        store.CompleteGeneration(gen, "0000000000000001:200", true);
        foreach (var item in store.Live(rootId))
            store.RequireRehash(rootId, item, "background content verification pending");
        return (root, pair);
    }

    // The composition seam stands in for LocalInventory.VerifyIndexingPassAsync, which ONLY verifies.
    // Publication belongs to the index owner after the delegate returns (LocalIndexingRuns.PassAsync
    // selects the ready page and calls PublishIndexPlanning), so the fake must not publish either.
    private static int CompleteIndexWork(SqliteStateStore store, string rootId)
    {
        var attempted = 0;
        foreach (var item in store.RehashCandidates(rootId, 128, store.RehashWatermark(rootId), needsVerification: true))
        {
            var revision = store.RehashRevision(rootId, item.ObjectId);
            if (store.CompleteIndexingRehash(rootId, item, new string('b', 64), revision)) attempted++;
        }
        return attempted;
    }

    private static async Task WaitForIndexAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException("Timed out waiting for " + what + ".");
            await Task.Delay(20).ConfigureAwait(false);
        }
    }
}
