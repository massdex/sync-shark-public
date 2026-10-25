namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task UrgentFairnessAsync()
    {
        var folder = FixtureFolder("urgent-fairness");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            for (var n = 0; n < 2400; n++)
            { fx.A.Seed($"Held-{n:D4}", "original"); fx.B.Seed($"Held-{n:D4}", "original"); }
            await FirstSyncAsync(fx.Run(), fx);
            SeedPriorityBacklog(store, fx.Pair);
            for (var n = 0; n < 2400; n++) fx.B.Seed($"Held-{n:D4}", "remote edit");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(store.UrgentPending(fx.Pair.PairId) == 4800,
                "remote deltas under a local gap retain both sides of 2400 protected entities");
            var seenBulk = new HashSet<string>();
            for (var pass = 0; pass < 4; pass++)
            {
                var page = fx.Run().AdmitPlanningPage(fx.Pair.PairId);
                var urgent = page.Where(row => row.Reason == SqliteStateStore.UserChangeReason).ToArray();
                var bulk = page.Where(row => row.Reason != SqliteStateStore.UserChangeReason).ToArray();
                Require(page.Count == 2048 && page.Sum(row => row.Paths.Count) <= 8192 &&
                    urgent.Length == 1024 && urgent.Sum(row => row.Paths.Count) <= 4096 && bulk.Length == 1024,
                    "each intake reserves bulk capacity despite persistently protected urgent work");
                Require(bulk.All(row => seenBulk.Add(row.ObjectId)), "bulk FIFO advances every intake");
                Require(store.UrgentPending(fx.Pair.PairId) == 4800, "protected urgent rows survive rotation");
            }
            UrgentAliasBudget(store, fx.Pair);
        }
        finally { DeleteFixture(folder); }
    }

    private static void UrgentAliasBudget(SqliteStateStore store, PairDef pair)
    {
        // Oldest urgent entity alone has more aliases than its half-budget.
        var aliases = Enumerable.Range(0, 4800).Select(n => new ItemState($"Aliases/{n:D4}", ItemKind.File,
            "many-aliases", null, new string('a', 64), 1, DateTimeOffset.UnixEpoch, ItemFlags.None)).ToArray();
        SeedRoot(store, pair.B, Side.B, aliases);
        store.DeferPlanning(pair.PairId, [(pair.B.RootId, aliases[0] with { Path = "Former/Alias" })], urgent: true);
        StoreSchema.Execute(PriorityConnection(store), null,
            "UPDATE planning_deferred SET version=0 WHERE object_id=$id;", ("$id", aliases[0].ObjectId));
        var page = store.AdmitDeferredPlanning(pair.PairId, long.MaxValue, rotate: false);
        var urgent = page.Where(row => row.Reason == SqliteStateStore.UserChangeReason).ToArray();
        Require(urgent.Sum(row => row.Paths.Count) == 4096 && !urgent[0].AllAliases,
            "urgent aliases cannot consume the bulk half of the path budget");
        Require(page.Count(row => row.Reason != SqliteStateStore.UserChangeReason) == 2047 &&
            page.Sum(row => row.Paths.Count) <= 8192, "bulk reuses unused urgent entity capacity");
    }
}
