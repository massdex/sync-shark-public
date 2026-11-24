using System.Reflection;
using System.Runtime.ExceptionServices;
using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Measurement only (unmerged probe branch): bytes per item by one exclusion apply whose items reach the locked effect.</summary>
public static class ExclusionEffectAllocationProbe
{
    public static async Task RunAsync()
    {
        var lines = new List<string>();
        foreach (var workload in new[] { "applied", "refused", "failed" })
            foreach (var suspended in new[] { false, true })
            {
                await MeasureAsync(workload, suspended, 200);
                var small = await MeasureAsync(workload, suspended, 1000);
                var large = await MeasureAsync(workload, suspended, 2000);
                lines.Add($"{workload}/{(suspended ? "suspended" : "sync")} thread={(large.Thread - small.Thread) / 1000.0:F1} " +
                    $"total={(large.Total - small.Total) / 1000.0:F1} n1000={small.Total} n2000={large.Total} {large.Result}");
            }
        throw new InvalidOperationException("PROBE " + string.Join(" | ", lines));
    }

    private static async Task<(long Thread, long Total, string Result)> MeasureAsync(string workload, bool suspended, int count)
    {
        var exclusion = new Exclusion(Side.Local, "dir", false);
        var pair = Scenarios.ItemChecks.Pair() with { Exclusions = [exclusion] };
        // Owed makes a file facing a peer folder a planned clash notice: the effect refuses with the action's cause.
        var configured = workload == "refused" ? pair with { OwedScopes = [new("dir", "dir")] } : pair;
        using var store = new TestDoubles.InMemoryStateStore();
        var local = new TestDoubles.FakeSide(Side.Local);
        var remote = new TestDoubles.FakeSide(Side.Dropbox);
        var pc = Scenarios.ItemChecks.File(Side.Local, "dir", 0, "pc-dir", true);
        var db = Scenarios.ItemChecks.File(Side.Dropbox, "dir", 0, "db-dir", true);
        local.Seed(pc, []); remote.Seed(db, []);
        await store.CommitAsync(pair.PairId, null, SyncedEntries.From(pc, db), null, null, default);
        for (var i = 0; i < count; i++)
        {
            var path = $"dir/f{i:D5}";
            local.Seed(Scenarios.ItemChecks.File(Side.Local, path, 1, $"pc{i}"), [1]);
            if (workload == "refused") remote.Seed(Scenarios.ItemChecks.File(Side.Dropbox, path, 0, $"db{i}", true), []);
            if (workload == "failed") remote.PublishFailures[path] = new ItemFailedException(path, TextKey.PublishFailed, null);
        }
        var source = suspended ? YieldingSide.Over(local) : local;
        var scope = new PathScope(configured, [configured], DriveType.Fixed);
        var exclusions = new Exclusions();
        string outcome;
        var thread = GC.GetAllocatedBytesForCurrentThread();
        var total = GC.GetTotalAllocatedBytes(true);
        try
        {
            var result = await exclusions.ApplyAsync(configured, Scenarios.ItemChecks.Settings, _ => { }, scope, exclusion,
                source, remote, store, null!, null!, default);
            outcome = $"{result.Outcome}/{result.Cause}";
        }
        catch (Exception error) { outcome = error.GetType().Name; }
        thread = GC.GetAllocatedBytesForCurrentThread() - thread;
        total = GC.GetTotalAllocatedBytes(true) - total;
        return (thread, total, $"outcome={outcome} reads={local.Requests[nameof(ISide.ReadOneAsync)]} " +
            $"publishes={remote.Requests[nameof(ISide.PublishAsync)]} notices={store.OpenNotices.Count()}");
    }

    /// <summary>Forwards to a fake side but completes every single-item read asynchronously, so locked effects suspend.</summary>
    public class YieldingSide : DispatchProxy
    {
        private ISide inner = null!;

        public static ISide Over(ISide inner)
        {
            var proxy = Create<ISide, YieldingSide>();
            ((YieldingSide)(object)proxy).inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result;
            try { result = method!.Invoke(inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Throw(error.InnerException!); throw; }
            return method!.Name == nameof(ISide.ReadOneAsync) ? YieldAsync((Task<Entry?>)result!) : result;
        }

        private static async Task<Entry?> YieldAsync(Task<Entry?> read)
        {
            await Task.Yield();
            return await read;
        }
    }
}
