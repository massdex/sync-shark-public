namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static void DeferredIntakeMillion()
    {
        var folder = FixtureFolder("deferred-intake-million");
        try
        {
            var pair = IntakePair();
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            store.SavePair(pair);
            // SQLite generates the backlog: the test never allocates a million managed rows.
            StoreSchema.Execute(PriorityConnection(store), null, """
                WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<1000000)
                INSERT INTO planning_deferred(root_no,object_id,pair_no,path,version,reason)
                SELECT (SELECT root_no FROM root WHERE root_id=$root),'item-'||i,
                    (SELECT pair_no FROM pair WHERE pair_id=$pair),'Pending/item-'||i,i,'bulk' FROM n;
                """, ("$root", pair.B.RootId), ("$pair", pair.PairId));
            var allocated = GC.GetTotalAllocatedBytes(true);
            var retained = GC.GetTotalMemory(true);
            var page = store.AdmitDeferredPlanning(pair.PairId, long.MaxValue, rotate: false);
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            retained = GC.GetTotalMemory(true) - retained;
            Require(page.Count == DeferredIntakeEntities && page.Sum(row => row.Paths.Count) == DeferredIntakeEntities,
                "one million queued rows still admit exactly 2048 single-path entities");
            Require(retained < 16L * 1024 * 1024, "retained intake memory stays below 16 MiB at one million rows");
            Console.WriteLine($"million-row intake: entities={page.Count} allocated={allocated} retained={retained} bytes");
            GC.KeepAlive(page);
        }
        finally { DeleteFixture(folder); }
    }
}
