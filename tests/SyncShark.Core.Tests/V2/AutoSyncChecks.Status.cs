using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class AutoSyncChecks
{
    // d4: status is composed from memory first; while another owner holds the store gate it answers from its cache
    // with storeStale instead of waiting behind the run.
    private static async Task StatusAnswersWithoutStoreGateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "autosync-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var authority = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using (var seed = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            // A local-to-local pair keeps cloud-file hydration out of a status-only check.
            seed.SavePair(RigPair("p1", false) with
            {
                B = new RootDef("b-p1", SyncProviders.Local, null, @"C:\b-p1", "id:b-p1")
            });
            seed.MarkFirstSyncDone("p1");
        }
        await using var engine = new ProductionAutoSyncEngine(authority, root);
        JsonElement first = default;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            first = JsonSerializer.SerializeToElement(await engine.ExecuteAsync(AutoSyncHostClient.Request("status"), default));
            if (!first.GetProperty("storeStale").GetBoolean() && first.GetProperty("pairs").GetArrayLength() == 1) break;
            await Task.Delay(25);
        }
        Require(!first.GetProperty("storeStale").GetBoolean() && first.GetProperty("pairs").GetArrayLength() == 1,
            "an uncontended status read fills the store fields and caches the pair list");
        var auto = (AutoSync)Field(engine, "_auto");
        auto.ReportProgress("p1", new LiveProgress(LiveProgress.AutoSyncKind, RunPhases.Applying, 5, 10,
            "x.txt", null, DateTimeOffset.UtcNow));
        var store = ((EngineComposition)Field(engine, "_composition")).Store;
        var gate = (FairStoreGate)store.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var lease = gate.Enter(CancellationToken.None);
            entered.Set();
            release.Wait();
        });
        holder.Start();
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)), "the status check can hold the store gate");
            var watch = Stopwatch.StartNew();
            var stale = JsonSerializer.SerializeToElement(await engine.ExecuteAsync(AutoSyncHostClient.Request("status"), default));
            watch.Stop();
            Require(watch.Elapsed < TimeSpan.FromSeconds(1), "status answers in under a second while the gate is held");
            Require(stale.GetProperty("storeStale").GetBoolean(), "status marks store-sourced fields stale while the gate is held");
            var pair = stale.GetProperty("pairs")[0];
            Require(pair.GetProperty("progress").GetProperty("kind").GetString() == LiveProgress.AutoSyncKind &&
                pair.GetProperty("progress").GetProperty("done").GetInt64() == 5,
                "status keeps live in-memory progress while the store gate is held");
        }
        finally
        {
            release.Set();
            holder.Join();
        }
        await engine.StopAsync();
        Pass("StatusAnswersWithoutStoreGate");
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    // H2: the first status request must already name the pairs read at startup, not wait for a warm cache.
    private static async Task StatusFirstRequestSeededPairsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "autosync-status-first-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var authority = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using (var seed = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            seed.SavePair(RigPair("p1", false) with
            {
                B = new RootDef("b-p1", SyncProviders.Local, null, @"C:\b-p1", "id:b-p1")
            });
            seed.MarkFirstSyncDone("p1");
        }
        await using var engine = new ProductionAutoSyncEngine(authority, root);
        var store = ((EngineComposition)Field(engine, "_composition")).Store;
        var gate = (FairStoreGate)store.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var lease = gate.Enter(CancellationToken.None);
            entered.Set();
            release.Wait();
        });
        holder.Start();
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)), "the first status check can hold the store gate");
            // No warm-up request: this is the very first status read and the cache holds only seeded identities.
            var first = JsonSerializer.SerializeToElement(await engine.ExecuteAsync(AutoSyncHostClient.Request("status"), default));
            Require(first.GetProperty("storeStale").GetBoolean(), "the first contended read is marked stale");
            Require(first.GetProperty("openConflicts").ValueKind == JsonValueKind.Null,
                "an unread conflict count is unknown rather than falsely zero");
            Require(first.GetProperty("pairs").GetArrayLength() == 1,
                "the first contended read still names the pair read at startup");
            var pair = first.GetProperty("pairs")[0];
            Require(pair.GetProperty("PairId").GetString() == "p1" && pair.GetProperty("Name").GetString() == "p1",
                "the seeded identity keeps the name and exact id read at startup");
            Require(pair.GetProperty("storeStale").GetBoolean() &&
                pair.GetProperty("urgentPending").ValueKind == JsonValueKind.Null &&
                pair.GetProperty("planningBacklog").ValueKind == JsonValueKind.Null,
                "fields the projection did not supply are marked stale, not reported as zero");
        }
        finally
        {
            release.Set();
            holder.Join();
        }
        await engine.StopAsync();
        Pass("StatusFirstRequestSeededPairs");
    }

    // H3: the deadline bounds the read itself, not only gate entry, and an expired projection never becomes cache.
    private static void StatusDeadlineBoundsTheRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "autosync-status-slow-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
        foreach (var id in new[] { "p1", "p2", "p3" }) store.SavePair(StatusPair(id));
        var seeded = ProductionAutoSyncEngine.StatusCache.Seeded(store.Pairs(), DateTimeOffset.UnixEpoch)
            with { OpenConflicts = 7 };
        var slow = SlowStatusStore.Wrap(store, out var control);
        control.HistoryDelay = TimeSpan.FromMilliseconds(80);
        var settings = V2Settings.Default with { StatusStoreTimeout = TimeSpan.FromMilliseconds(120) };
        var watch = Stopwatch.StartNew();
        var (status, cache) = ProductionAutoSyncEngine.ReadStatus([], new Dictionary<string, LiveProgress>(), true, 5,
            slow, settings, _ => null, TimeProvider.System, seeded);
        watch.Stop();
        var element = JsonSerializer.SerializeToElement(status);
        Require(element.GetProperty("storeStale").GetBoolean(),
            "a read that runs past its deadline answers stale instead of publishing fresh values");
        Require(watch.Elapsed < TimeSpan.FromSeconds(1), "the bounded read returns promptly");
        Require(control.HistoryReads < 3,
            "the projection stops at the deadline instead of reading every pair: " + control.HistoryReads);
        Require(cache.AsOf == seeded.AsOf, "an expired projection is never published as the fresh cache");
        Require(element.GetProperty("openConflicts").GetInt32() == 7,
            "an expired projection retains the last known conflict count");
        Require(element.GetProperty("pairs").GetArrayLength() == 3,
            "the cached projection still names every pair while the read is stale");
        Pass("StatusDeadlineBoundsTheRead");
    }

    // H2: memory-known pairs absent from a stale projection stay visible, with their store fields marked stale.
    private static void StatusKeepsMemoryPairsWhenStale()
    {
        var root = Path.Combine(Path.GetTempPath(), "autosync-status-memory-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var slow = SlowStatusStore.Wrap(store, out var control);
        control.PairsDelay = TimeSpan.FromMilliseconds(200);
        var settings = V2Settings.Default with { StatusStoreTimeout = TimeSpan.FromMilliseconds(50) };
        var states = new[] { new PairAutoState("p9", AutoStatus.Running, null, null, null) };
        var progress = new Dictionary<string, LiveProgress>
        {
            ["p8"] = new(LiveProgress.AutoSyncKind, RunPhases.Applying, 1, 2, "x.txt", null, DateTimeOffset.UtcNow)
        };
        var (status, cache) = ProductionAutoSyncEngine.ReadStatus(states, progress, true, 5, slow, settings,
            _ => null, TimeProvider.System, ProductionAutoSyncEngine.StatusCache.Empty);
        var element = JsonSerializer.SerializeToElement(status);
        Require(element.GetProperty("storeStale").GetBoolean() && cache.AsOf == DateTimeOffset.MinValue,
            "the blocked projection answers stale");
        var pairs = element.GetProperty("pairs");
        Require(pairs.GetArrayLength() == 2, "memory-known pairs are not dropped by an empty stale projection");
        Require(pairs.EnumerateArray().All(pair => pair.GetProperty("storeStale").GetBoolean() &&
            pair.GetProperty("urgentPending").ValueKind == JsonValueKind.Null),
            "memory-only pairs mark unavailable store fields stale");
        Require(pairs[0].GetProperty("PairId").GetString() == "p9" &&
            pairs[0].GetProperty("progress").ValueKind == JsonValueKind.Null &&
            pairs[1].GetProperty("PairId").GetString() == "p8" &&
            pairs[1].GetProperty("progress").GetProperty("done").GetInt64() == 1,
            "the pair's live state and progress still come from memory");
        Pass("StatusKeepsMemoryPairsWhenStale");
    }

    private static PairDef StatusPair(string id) => new(id, "Pair " + id,
        new RootDef("a-" + id, SyncProviders.Local, null, @"C:\a-" + id, "id:a-" + id),
        new RootDef("b-" + id, SyncProviders.Dropbox, "acc", "/" + id, "id:b-" + id), [], [], true);

    /// <summary>Forwards to a real store and makes one read slow, so the deadline is exercised after admission.</summary>
    private class SlowStatusStore : DispatchProxy
    {
        private IStateStore _inner = null!;
        internal TimeSpan PairsDelay { get; set; }
        internal TimeSpan HistoryDelay { get; set; }
        internal int HistoryReads { get; private set; }

        internal static IStateStore Wrap(IStateStore inner, out SlowStatusStore control)
        {
            var proxy = DispatchProxy.Create<IStateStore, SlowStatusStore>();
            control = (SlowStatusStore)(object)proxy;
            control._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateStore.Pairs) && PairsDelay > TimeSpan.Zero)
                Thread.Sleep(PairsDelay);
            if (targetMethod?.Name == nameof(IStateStore.ReadRunHistory))
            {
                HistoryReads++;
                if (HistoryDelay > TimeSpan.Zero) Thread.Sleep(HistoryDelay);
            }
            try
            {
                return targetMethod!.Invoke(_inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                // A proxied call must surface the store's own exception, not reflection's wrapper;
                // the production path has no reflection layer.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
