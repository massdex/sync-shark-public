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
}
