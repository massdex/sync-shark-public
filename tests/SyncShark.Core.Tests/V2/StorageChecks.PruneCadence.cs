namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void PruneCadenceChecks(string root)
    {
        var clock = new TestClock();
        using var store = SqliteStateStore.Open(Fresh(root, "prune-cadence"), Fast, clock, LocalItems.CaptureOptionalHash);
        store.Prune();
        Require(store.PrunePasses == 1, "the first maintenance pass runs immediately");
        for (var run = 0; run < 10; run++) store.Prune();
        clock.UtcNow += TimeSpan.FromMinutes(59);
        store.Prune();
        Require(store.PrunePasses == 1, "repeated runs within the hour do not rescan retention tables");
        clock.UtcNow += TimeSpan.FromMinutes(1);
        store.Prune();
        Require(store.PrunePasses == 2, "the next hour permits one maintenance pass using the injected clock");
    }
}
