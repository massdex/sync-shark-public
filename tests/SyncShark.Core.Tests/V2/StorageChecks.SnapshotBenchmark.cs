using System.Diagnostics;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    // The real snapshot shape (materialized scanner input), not merge-local memory. Copy this unchanged
    // into the base test build for an apples-to-apples benchmark of the actual base writer.
    public static async Task SnapshotProductionMillionAsync(string root)
    {
        using var store = SqliteStateStore.Open(Fresh(root, "snapshot-production-million"), Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        var gen = store.BeginGeneration("root", "full");
        var items = Enumerable.Range(0, 1_000_000)
            .Select(i => F($"d{i / 10:D6}/f{i:D7}", "id:" + i, "aabb")).ToArray();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        Console.WriteLine($"snapshot-input-gen2-heap={GC.GetGCMemoryInfo().HeapSizeBytes}");
        using var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(false);
        long peakManaged = managedBefore, peakWorking = 0, peakPrivate = 0;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var allChanged = false;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                try { await Task.Delay(10, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });
        try
        {
            var snapshot = new Snapshot("root", Side.A, gen, "c", items, [])
            { ChangedObject = id => changed.Add(id), AllItemsChanged = () => allChanged = true };
            var watch = Stopwatch.StartNew();
            store.WriteSnapshot(snapshot);
            Console.WriteLine($"snapshot-write-ms={watch.ElapsedMilliseconds}");
            watch.Restart();
            store.WriteSnapshot(snapshot);
            Console.WriteLine($"snapshot-noop-ms={watch.ElapsedMilliseconds}");
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            Console.WriteLine($"snapshot-saved-gen2-heap={GC.GetGCMemoryInfo().HeapSizeBytes}");
            Console.WriteLine($"snapshot-change-ids={changed.Count}; all-changed={allChanged}; retained-managed-delta={GC.GetTotalMemory(false)-managedBefore}");
            Require(allChanged ? changed.Count == 0 : changed.Count == items.Length, "initial snapshot reports all changes without losing identities");
            GC.KeepAlive(changed);
            GC.KeepAlive(items);
        }
        finally { stop.Cancel(); await sampler; }
        Console.WriteLine($"snapshot-production-million: peak-managed={peakManaged}; peak-managed-delta={peakManaged-managedBefore}; peak-private={peakPrivate}; peak-working-set={peakWorking}");
    }
}
