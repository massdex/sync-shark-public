using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

internal static partial class LocalInventoryChecks
{
    private static void AbsenceCallbacksReleaseScan()
    {
        using var store = new InMemoryStateStore();
        var inventory = LocalChecks.Inventory(LocalChecks.FreshRoot("absence-memory"), store, TimeProvider.System);
        var (snapshot, delta, scan) = BuildAbsenceCallbacks(inventory);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        LocalChecks.Require(!scan.IsAlive, "snapshot and delta absence callbacks do not retain the scan graph");
        var missing = new ItemState("missing.txt", ItemKind.File, "missing", null, null, 1,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        LocalChecks.Require(snapshot.VerifyAbsence!(missing) == FileAbsence.Gone &&
            delta.DisplacementEvidence!.VerifyAbsence!(missing) == FileAbsence.Gone,
            "history-only complete listing retains its absence evidence after the scan is released");
        LocalChecks.Require(delta.DisplacementEvidence!.VerifyAbsence!(missing with { Path = "outside.txt" }) == FileAbsence.Unknown,
            "the delta absence callback does not grant evidence outside its explicit refreshed scope");
        GC.KeepAlive(snapshot);
        GC.KeepAlive(delta);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Snapshot, Delta, WeakReference) BuildAbsenceCallbacks(LocalInventory inventory)
    {
        var scan = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(), [], 0);
        var snapshot = inventory.BuildSnapshot(1, "root-local", "cursor", scan, _ => true);
        var delta = inventory.BuildDelta(1, "root-local", "cursor", "next", scan,
            ["missing.txt"], _ => true, DateTimeOffset.UnixEpoch, []);
        return (snapshot, delta, new WeakReference(scan));
    }
}
