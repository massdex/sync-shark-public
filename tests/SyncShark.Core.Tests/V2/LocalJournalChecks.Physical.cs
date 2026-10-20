using System.Diagnostics;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task PhysicalFolderAsync(string folder, int count = 5000)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("SKIP physical lookup regression: the current token is elevated; this check requires unelevated USN name redaction and an elevated run cannot provide that evidence.");
            return;
        }
        var root = Path.Combine(folder, "root");
        Directory.CreateDirectory(root);
        for (var n = 0; n < count; n++)
        {
            using var file = File.Open(Path.Combine(root, $"physical-{n:D6}.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            if (n % 50_000 == 0) Console.WriteLine($"physical fixture {n:N0}/{count:N0}");
        }
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        var gen = store.BeginGeneration("root-local", "seed");
        var seed = await inventory.ChangesForPathsAsync(["physical-000000.txt"], "", gen, _ => true, default);
        store.WriteDelta(seed);
        // Fixture creation must not consume the journal interval under test.
        LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var checkpoint), "physical folder checkpoint after fixture creation");
        File.Move(Path.Combine(root, "physical-000000.txt"), Path.Combine(root, "renamed.txt"));
        LocalChecks.Require(NtfsUsnJournal.TryReadCheckpoint(root, out var through) &&
            NtfsUsnJournal.TryReadChanges(root, checkpoint!, through!, out var records, default) &&
            records.Any(r => r.FileId == LocalItems.ParseObjectId(seed.Changes.Single().ObjectId).FileId && r.Name.Length == 0),
            "real renamed file journal record has redacted name");
        var lookups = 0;
        LocalJournalNames.LookupForTest = () => lookups++;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var baseManaged = GC.GetTotalMemory(false);
        var basePrivate = process.PrivateMemorySize64;
        var baseWorking = process.WorkingSet64;
        long peakManaged = baseManaged, peakPrivate = basePrivate, peakWorking = baseWorking;
        var retained = 0;
        PilotLocalScanner.ParentListingRetainedForTest = count => retained = Math.Max(retained, count);
        using var stop = new CancellationTokenSource();
        var sample = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                try { await Task.Delay(2, stop.Token); } catch (OperationCanceledException) { }
            }
        });
        try
        {
            var delta = await inventory.ChangesSinceAsync(checkpoint!.Cursor, gen, _ => true, default);
            LocalChecks.Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Count == 0 && delta.Changes.Any(c => c.Path == "renamed.txt"),
                $"physical {count:N0} parent rename runs Find plus refresh incrementally");
            LocalChecks.Require(lookups > 0, "nameless journal path actually executes parent name lookup");
            LocalChecks.Require(retained == 1, $"parent cache retains changed names only, not {count:N0} siblings ({retained})");
        }
        finally
        {
            stop.Cancel(); await sample;
            PilotLocalScanner.ParentListingRetainedForTest = null;
            LocalJournalNames.LookupForTest = null;
        }
        Console.WriteLine($"physical {count:N0} incremental peaks: managed={peakManaged:N0} (+{peakManaged-baseManaged:N0}), private={peakPrivate:N0} (+{peakPrivate-basePrivate:N0}), working={peakWorking:N0} (+{peakWorking-baseWorking:N0}), retained={retained}");
        LocalChecks.Require(peakManaged - baseManaged < 64L * 1024 * 1024, $"incremental managed peak stays bounded with {count:N0} physical siblings");
        Console.WriteLine($"Retained test fixture (deletion candidate, not deleted): {folder}");
    }
}
