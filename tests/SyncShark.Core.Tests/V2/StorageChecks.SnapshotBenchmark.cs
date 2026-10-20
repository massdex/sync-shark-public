using System.Diagnostics;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    // The real snapshot shape (materialized scanner input), not merge-local memory. Copy this unchanged
    // into the base test build for an apples-to-apples benchmark of the actual base writer.
    public static async Task SnapshotProductionMillionAsync(string root)
    {
        using var store = SqliteStateStore.Open(Fresh(root, "snapshot-production-million"), Fast, new TestClock());
        var gen = store.BeginGeneration("root", "full");
        var items = Enumerable.Range(0, 1_000_000)
            .Select(i => F($"d{i / 10:D6}/f{i:D7}", "id:" + i, "aabb")).ToArray();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        Console.WriteLine($"snapshot-input-gen2-heap={GC.GetGCMemoryInfo().HeapSizeBytes}");
        using var process = Process.GetCurrentProcess();
        long peakManaged = GC.GetTotalMemory(false), peakWorking = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                try { await Task.Delay(10, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });
        try
        {
            var snapshot = new Snapshot("root", Side.A, gen, "c", items, []);
            var watch = Stopwatch.StartNew();
            store.WriteSnapshot(snapshot);
            Console.WriteLine($"snapshot-write-ms={watch.ElapsedMilliseconds}");
            watch.Restart();
            store.WriteSnapshot(snapshot);
            Console.WriteLine($"snapshot-noop-ms={watch.ElapsedMilliseconds}");
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            Console.WriteLine($"snapshot-saved-gen2-heap={GC.GetGCMemoryInfo().HeapSizeBytes}");
            GC.KeepAlive(items);
        }
        finally { stop.Cancel(); await sampler; }
        Console.WriteLine($"snapshot-production-million: peak-managed={peakManaged}; peak-working-set={peakWorking}");
    }
}
