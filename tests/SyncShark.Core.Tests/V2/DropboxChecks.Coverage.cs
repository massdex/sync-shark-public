namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    public static async Task RunCoverageChecksAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh test directory.");
        Directory.CreateDirectory(root); _root = root;
        await CoverageUnrelatedAndFailedPrepareAsync();
        await PrepareCursorWidenedScopeAsync("excluded");
        await PrepareCursorWidenedScopeAsync("nested-root");
        await PrepareCursorWidenedScopeAsync("ignore");
        await CoverageStreamRaceAsync();
        await CoverageInterruptedHydrationAsync();
        CoverageRollback();
        Console.WriteLine("--v2-coverage-checks passed (scope, widening/race, failed-prepare/legacy schema groups).");
    }

    private static async Task CoverageUnrelatedAndFailedPrepareAsync()
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "coverage-prepare");
        var pair = ClassificationPair();
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        h.Wire.LatestCursor = "coverage-start";
        h.Wire.SeedChangePage("coverage-start", [], false, "coverage-start");
        using var store = SqliteStateStore.Open(Path.Combine(product, "state"), StorageChecks.Fast, TimeProvider.System);
        store.SavePair(pair);
        var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, product);
        await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        var saved = FirstSyncChoicesState.Load(product, pair.PairId, store)!;
        var lists = h.Wire.ListRequests.Count;
        store.SavePair(new PairDef("elsewhere", "unrelated", pair.A with { RootId = "else-a", RootPath = "C:\\elsewhere", RootObjectId = "id:else-local" },
            pair.B with { RootId = "else-b", RootPath = "/Elsewhere", RootObjectId = "id:else" }, [], [], true));
        await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        Require(h.Wire.ListRequests.Count == lists && FirstSyncChoicesState.Load(product, pair.PairId, store)!.InventoryScope == saved.InventoryScope,
            "unrelated pair changes neither invalidate relevant scope nor relist");
        try
        {
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None, "{");
            throw new InvalidOperationException("Malformed choices should fail.");
        }
        catch (Exception error) when (error is not InvalidOperationException) { }
        Require(FirstSyncChoicesState.Load(product, pair.PairId, store) is null && store.ReadInventoryObservation(pair.B) is not null,
            "failed reprepare retains observation, never old authorization");
        await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        Require(h.Wire.ListRequests.Count == lists, "retry after failed prepare reuses durable observation");
    }

    private static async Task CoverageStreamRaceAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFolder("wide");
        for (var i = 0; i < 10_005; i++) h.Wire.SeedFile("wide/f" + i, "x");
        h.Wire.DeletedPageSize = 701;
        var count = 0; var max = 0;
        await h.Client.HydratePrefixAsync("wide", rows =>
        {
            count += rows.Count; max = Math.Max(max, rows.Count);
            if (count > 700) h.Wire.SeedChangePage("original-root", [h.Wire.ChangeFile("wide/raced.txt", "race")], false, "after-race");
            return Task.CompletedTask;
        }, CancellationToken.None);
        var page = await h.Client.ReadChangePageAsync("original-root", CancellationToken.None);
        Require(count == 10_006 && max <= 256 && h.Wire.ListRequests.All(row => row.Path != DropboxWire.RootId) &&
            page.Cursor == "after-race" && page.Changes.Any(row => row.Path == "wide/raced.txt"),
            "uncapped exact subtree stream stays bounded and original root feed captures in-flight changes");
    }

    private static async Task CoverageInterruptedHydrationAsync()
    {
        using var h = new Harness();
        var pair = ClassificationPair() with { Excluded = ["wide"] };
        var state = Path.Combine(_root, "coverage-interrupted");
        h.Wire.SeedFolder("wide");
        for (var i = 0; i < 300; i++) h.Wire.SeedFile("wide/f" + i, "x");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(pair);
            store.ReplaceDeletionClassification(pair.B, "original", new HashSet<string> { "wide/old" }, new HashSet<string>());
            store.SaveInventoryObservation(InventoryCoverage.For(pair, Side.B, [pair], []), "original");
            store.BeginPrefixHydration(RootId);
            var count = 0;
            try
            {
                await h.Client.HydratePrefixAsync("wide", rows =>
                {
                    store.StagePrefixHydration(RootId, rows, _ => true);
                    count += rows.Count;
                    if (count > 256) throw new IOException("Injected partial hydration.");
                    return Task.CompletedTask;
                }, CancellationToken.None);
                throw new InvalidOperationException("Expected hydration interruption.");
            }
            catch (IOException) { }
            Require(store.ReadDeletionClassification(pair.B)?.Cursor == "original" && store.Live(RootId).Count == 0,
                "partial subtree staging cannot alter published classification or live inventory");
        }
        using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        var observation = reopened.ReadInventoryObservation(pair.B)!;
        Require(observation.Cursor == "original" && observation.Coverage.Prefixes.Contains("wide") &&
            reopened.ReadPrepare(pair.PairId) is null,
            "interrupted hydration preserves only narrow observation, never expanded coverage or authorization");
    }

    private static void CoverageRollback()
    {
        var state = Path.Combine(_root, "coverage-rollback");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
            store.SavePair(ClassificationPair());
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(state)))
        {
            connection.Open();
            StoreSchema.Execute(connection, null, "DROP TABLE observation_checkpoint; PRAGMA user_version=9;");
            Require((long)StoreSchema.Scalar(connection, null, "SELECT COUNT(*) FROM pair;")! == 1 &&
                (string)StoreSchema.Scalar(connection, null, "PRAGMA integrity_check;")! == "ok", "v10 rollback preserves legacy pair and database integrity");
        }
        using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        Require(reopened.Pairs().Count == 1 && reopened.ReadInventoryObservation(Root()) is null,
            "v9 upgrade preserves data and does not invent coverage");
    }
}
