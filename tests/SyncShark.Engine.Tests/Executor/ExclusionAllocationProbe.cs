using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Measurement only (unmerged probe branch): bytes allocated per in-sync item by one exclusion apply.</summary>
public static class ExclusionAllocationProbe
{
    public static async Task RunAsync()
    {
        await MeasureAsync(200);
        var small = await MeasureAsync(1000);
        var large = await MeasureAsync(2000);
        throw new InvalidOperationException(
            $"PROBE thread n1000={small.Thread} n2000={large.Thread} perItem={(large.Thread - small.Thread) / 1000.0:F1} | " +
            $"total n1000={small.Total} n2000={large.Total} perItem={(large.Total - small.Total) / 1000.0:F1} | " +
            $"outcome={large.Outcome} hashes={large.Hashes}");
    }

    private static async Task<(long Thread, long Total, string Outcome, int Hashes)> MeasureAsync(int count)
    {
        var pair = Scenarios.ItemChecks.Pair();
        var exclusion = new Exclusion(Side.Local, "dir", false);
        var configured = pair with { Exclusions = [exclusion] };
        using var store = new TestDoubles.InMemoryStateStore();
        var local = new TestDoubles.FakeSide(Side.Local);
        var remote = new TestDoubles.FakeSide(Side.Dropbox);
        var pc = Scenarios.ItemChecks.File(Side.Local, "dir", 0, "pc-dir", true);
        var db = Scenarios.ItemChecks.File(Side.Dropbox, "dir", 0, "db-dir", true);
        local.Seed(pc, []); remote.Seed(db, []);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(pc, db), null, null, default);
        for (var i = 0; i < count; i++)
        {
            var file = Scenarios.ItemChecks.File(Side.Local, $"dir/f{i:D5}", 1, $"pc{i}");
            var peer = Scenarios.ItemChecks.File(Side.Dropbox, $"dir/f{i:D5}", 1, $"db{i}");
            local.Seed(file, [1]); remote.Seed(peer, [1]);
            await store.CommitAsync(pair.PairId, null, SyncedEntries.From(file, peer), null, null, default);
        }
        var scope = new PathScope(configured, [configured], DriveType.Fixed);
        var exclusions = new Exclusions();
        var outcome = "none";
        var thread = GC.GetAllocatedBytesForCurrentThread();
        var total = GC.GetTotalAllocatedBytes(true);
        try
        {
            outcome = (await exclusions.ApplyAsync(configured, Scenarios.ItemChecks.Settings, _ => { }, scope, exclusion,
                local, remote, store, null!, null!, default)).Outcome.ToString();
        }
        catch (Exception error) { outcome = error.GetType().Name; }
        thread = GC.GetAllocatedBytesForCurrentThread() - thread;
        total = GC.GetTotalAllocatedBytes(true) - total;
        return (thread, total, outcome, local.Requests[nameof(ISide.ContentHashAsync)]);
    }
}
