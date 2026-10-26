using System.Reflection;

namespace Massdex.SyncShark.V2;

// Opt-in synthetic compare scope, not an endpoint run. No user data or filesystem effects.
internal static class CompareMemoryChecks
{
    internal static async Task RunAsync()
    {
        const int count = 1_000_000;
        using var store = new InMemoryStateStore();
        var pair = new PairDef("pair", "Pair A", new("left", "synthetic", null, "/left", "left"),
            new("right", "synthetic", null, "/right", "right"), [], [], true);
        long peak = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                Interlocked.Exchange(ref peak, Math.Max(peak, GC.GetTotalMemory(false)));
                try { await Task.Delay(10, stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        try
        {
            var a = new ItemState[count];
            var b = new ItemState[count];
            var baselines = new Baseline[count];
            for (var i = 0; i < count; i++)
            {
                ItemState Item(string side) => new($"Parent/{new string('p', 120)}/file-{i:D7}.txt", ItemKind.File,
                    $"{side}-{i:D7}", null, new string('a', 64), 1, DateTimeOffset.UnixEpoch, ItemFlags.None);
                a[i] = Item("a");
                b[i] = Item("b");
                // Match SQLite decoding: the two baseline endpoints share their stored path/hash.
                var basisA = Item("a");
                var basisB = basisA with { ObjectId = $"b-{i:D7}" };
                baselines[i] = new Baseline(pair.PairId, $"key-{i:D7}", basisA, basisB, 1);
            }
            var loader = new RunScope(store, _ => true, _ => true);
            var prepare = typeof(RunScope).GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var scope = (RunScope.Scope)prepare.Invoke(loader,
                [pair, a, b, baselines, Array.Empty<ItemState>(), Array.Empty<ItemState>(), "Full", null])!;
            var rows = Planner.Join(scope.Baselines, scope.A, scope.B,
                new Dictionary<string, DateTimeOffset?>(), new Dictionary<string, DateTimeOffset?>(),
                new Completeness(true, true, new HashSet<string>(), new HashSet<string>()), false);
            if (rows.Count != count || rows.Any(r => r.A is null || r.B is null || r.Base is null))
                throw new InvalidOperationException("The million-item compare lost an established triple.");
            Console.WriteLine($"compare-items={count}; rows={rows.Count}; managed-at-join={GC.GetTotalMemory(false)}");
            var options = new PlanOptions(TimeSpan.Zero, false,
                new LabelRule("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), int.MaxValue);
            var plan = Planner.Decide(pair.PairId, 2, rows, options);
            Console.WriteLine($"compare-actions={plan.Actions.Count}; notices={plan.Notices.Count}");
            GC.KeepAlive(plan);
            GC.KeepAlive(scope);
            GC.KeepAlive(rows);
        }
        finally
        {
            peak = Math.Max(peak, GC.GetTotalMemory(false));
            stop.Cancel();
            await sampler;
            Console.WriteLine($"compare-peak-managed-bytes={peak}");
        }
    }
}
