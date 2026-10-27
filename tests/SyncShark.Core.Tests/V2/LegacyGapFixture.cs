namespace Massdex.SyncShark.V2;

internal static class LegacyGapFixture
{
    // Simulate the persisted pre-upgrade provenance, not a new BeginGap made by this version.
    internal static void SetDiscovery(string product, bool complete)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(product, "v2", "state.db")};Pooling=False");
        db.Open();
        StoreSchema.Execute(db, null,
            "UPDATE local_gap SET reason='legacy gap',worker_done=$done,discovery_done=$done;",
            ("$done", complete ? 1 : 0));
    }
}
