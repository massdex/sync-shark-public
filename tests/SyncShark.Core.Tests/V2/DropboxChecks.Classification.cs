namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task CursorClassificationChecksAsync()
    {
        await CursorNeverExistedAsync();
        await CursorClassificationScaleAsync();
        await CursorClassificationCacheAsync();
        await CursorReleasedTransientAsync();
        await CursorListingClosureAsync();
        await CursorClassificationInterruptedAsync();
        CursorClassificationMigration();
    }

    private static PairDef ClassificationPair() => new("p1", "classification", new RootDef("ra", SyncProviders.Local,
        null, "C:\\offline-classification", "id:ra"), Root(), [], [], true);

    private static async Task CursorNeverExistedAsync()
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "never-existed");
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("brand-new.txt", "new content");
        var pair = ClassificationPair();
        h.Wire.LatestCursor = "new-start";
        h.Wire.SeedChangePage("new-start", [], false, "new-start");
        using var store = SqliteStateStore.Open(Path.Combine(product, "state"), StorageChecks.Fast, TimeProvider.System);
        store.SavePair(pair);
        var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, product);
        await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        var lists = h.Wire.ListRequests.Count;
        h.Wire.SeedChangePage("new-start", [], false, "new-end");
        var result = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { }, CancellationToken.None);
        Require(result.Failed == 0 && store.FirstSyncDone(pair.PairId) && h.Wire.ListRequests.Count == lists &&
            await h.Client.FindEntryAtPathAsync("brand-new.txt") is not null &&
            !h.Wire.Calls.Any(call => call.Contains("list_revisions", StringComparison.Ordinal)),
            "never-existed local file remains an ordinary upload after prepared cursor resume, without revision probes");
    }

    private static async Task CursorClassificationScaleAsync()
    {
        using var h = new Harness();
        var state = Path.Combine(_root, "classification-scale");
        h.Wire.LatestCursor = "scale-start";
        h.Wire.SeedChangePage("scale-start", [], false, "scale-start");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(ClassificationPair());
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, "p1");
            await inventory.FullAsync(1, _ => true, new Progress<string>(), CancellationToken.None);
        }
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, "p1");
            var lists = h.Wire.ListRequests.Count;
            h.Wire.SeedChangePage("scale-start", [], false, "scale-end");
            var delta = await inventory.ChangesSinceAsync("scale-start", 2, _ => true, CancellationToken.None, true);
            var paths = Enumerable.Range(0, 100_000).Select(i => "new/" + i + ".txt").ToArray();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var prepared = await inventory.PrepareDeletionEvidenceAsync(paths, CancellationToken.None);
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            Console.WriteLine($"classification 100k components: cursor-invalid={delta.CursorInvalid}; " +
                $"pending={prepared.Pending.Count}; resolved={prepared.Resolved.Count}; requested={paths.Length}; " +
                $"revision-calls={h.Wire.Calls.Count(call => call.Contains("list_revisions", StringComparison.Ordinal))}; " +
                $"lists-before={lists}; lists-after={h.Wire.ListRequests.Count}; allocated-bytes={allocated}; limit-bytes={96L * 1024 * 1024}");
            Require(!delta.CursorInvalid && prepared.Pending.Count == 0 && prepared.Resolved.Count == paths.Length &&
                !h.Wire.Calls.Any(call => call.Contains("list_revisions", StringComparison.Ordinal)) &&
                h.Wire.ListRequests.Count == lists && allocated < 96L * 1024 * 1024,
                "100k never-existed candidates resume with zero revision calls, zero held tasks, and under 96MiB allocations");
            Console.WriteLine($"classification 100k allocation bytes: {allocated}");
        }
    }

    private static async Task CursorClassificationCacheAsync()
    {
        using var h = new Harness();
        h.Wire.SeedDeletedEntry("gone.txt");
        h.Wire.RevisionContentHash = new string('a', 64);
        h.Wire.LatestCursor = "cache-start";
        h.Wire.SeedChangePage("cache-start", [], false, "cache-start");
        var state = Path.Combine(_root, "classification-cache");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(ClassificationPair());
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, "p1");
            await inventory.FullAsync(1, _ => true, new Progress<string>(), CancellationToken.None);
            await inventory.DeletionTimesAsync(["gone.txt"], CancellationToken.None);
        }
        var revisions = h.Wire.Calls.Count(call => call.Contains("list_revisions", StringComparison.Ordinal));
        using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        var resumed = new DropboxInventory(Root(), Side.B, h.Client, reopened, "p1");
        h.Wire.SeedChangePage("cache-start", [], false, "cache-next");
        await resumed.ChangesSinceAsync("cache-start", 2, _ => true, CancellationToken.None, true);
        var evidence = await resumed.PrepareDeletionEvidenceAsync(["gone.txt"], CancellationToken.None);
        Require(evidence.Pending.Count == 0 && evidence.Resolved["gone.txt"].ContentHash == new string('a', 64) &&
            h.Wire.Calls.Count(call => call.Contains("list_revisions", StringComparison.Ordinal)) == revisions &&
            reopened.DeletionEvidenceRows("p1", RootId).Single().BasisCursor == "cache-next",
            "unchanged hash-bearing held evidence reuses the already-read cursor feed and advances its basis");
    }

    private static async Task CursorReleasedTransientAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFile("held.txt", "keep");
        h.Wire.LatestCursor = "transient-start";
        h.Wire.SeedChangePage("transient-start", [], false, "transient-start");
        using var store = SqliteStateStore.Open(Path.Combine(_root, "released-transient"), StorageChecks.Fast, TimeProvider.System);
        store.SavePair(ClassificationPair());
        var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, "p1");
        var gen = store.BeginGeneration(RootId, "full");
        var full = await inventory.FullAsync(gen, _ => true, new Progress<string>(), CancellationToken.None);
        store.WriteSnapshot(full); store.CompleteGeneration(gen, full.CursorAfter, true);
        store.StoreDeletionEvidence("p1", RootId, [new("held.txt", null, Failure: new(3, DateTimeOffset.UtcNow, gen, true))]);
        h.Client.ReadTransientBudget = TimeSpan.FromMilliseconds(100);
        h.Wire.MetadataFailurePath = DropboxWire.RootId + "/held.txt";
        h.Wire.SeedChangePage("transient-start", [h.Wire.ChangeFile("other.txt", "independent")], false, "transient-end");
        var deltaGen = store.BeginGeneration(RootId, "delta");
        var delta = await inventory.ChangesSinceAsync("transient-start", deltaGen, _ => true, CancellationToken.None, true);
        Require(!delta.CursorInvalid && delta.IncompleteSubtrees.Count == 0 &&
            delta.Changes.Any(change => change.Path == "other.txt" && change.Kind == ChangeKind.Upsert) &&
            delta.Changes.Single(change => change.Path == "held.txt").State!.Flags.HasFlag(ItemFlags.Unreadable),
            "one transient released metadata failure holds only that row and preserves unrelated cursor coverage");
        store.WriteDelta(delta); store.CompleteGeneration(deltaGen, delta.CursorAfter, true);
        h.Wire.MetadataFailurePath = null;
        // The 100ms budget above bounds only the injected outage. Restore a realistic
        // successful-read budget so scheduler/GC delays cannot masquerade as a second outage.
        h.Client.ReadTransientBudget = TimeSpan.FromSeconds(5);
        h.Wire.SeedChangePage("transient-end", [], false, "transient-retried");
        var lists = h.Wire.ListRequests.Count;
        var retry = await inventory.ChangesSinceAsync("transient-end", deltaGen + 1, _ => true, CancellationToken.None, true);
        Require(!retry.CursorInvalid && h.Wire.ListRequests.Count == lists &&
            !retry.Changes.Single(change => change.Path == "held.txt").State!.Flags.HasFlag(ItemFlags.Unreadable),
            $"retry repairs the held row and clears unreadable state without a full listing (invalid={retry.CursorInvalid}, lists={lists}->{h.Wire.ListRequests.Count}, rows={string.Join(';', retry.Changes.Select(change => change.Path + ':' + change.State?.Flags))})");
    }

    private static void CursorClassificationMigration()
    {
        var state = Path.Combine(_root, "classification-migration");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(ClassificationPair());
            var gen = store.BeginGeneration(RootId, "full");
            store.CompleteGeneration(gen, "preserved-v8-cursor", true);
        }
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(state)))
        {
            connection.Open();
            StoreSchema.Execute(connection, null, "DROP TABLE deletion_classification_path; DROP TABLE deletion_classification; PRAGMA user_version=8;");
        }
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
            Require(store.Pairs().Count == 1 && store.Cursor(RootId) == "preserved-v8-cursor" &&
                store.ReadDeletionClassification(Root()) is null,
                "v8 migration preserves pair/cursor rows but never invents empty tombstone coverage");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(state)))
        {
            connection.Open();
            Require((long)StoreSchema.Scalar(connection, null, "PRAGMA user_version;")! == StoreSchema.Version,
                "classification, observation, rehash and item retries publish coordinated current schema");
        }
    }

    private static async Task CursorClassificationInterruptedAsync()
    {
        using var h = new Harness();
        using var store = SqliteStateStore.Open(Path.Combine(_root, "classification-interrupted"), StorageChecks.Fast, TimeProvider.System);
        store.SavePair(ClassificationPair());
        var empty = new HashSet<string>();
        store.ReplaceDeletionClassification(Root(), "old-complete", empty, empty);
        try
        {
            store.ReplayDeletionClassification(Root(), "never-published", InterruptedRows());
            throw new InvalidOperationException("Expected injected batch interruption.");
        }
        catch (IOException) { }
        Require(store.ReadDeletionClassification(Root()) is null,
            "interrupted multi-batch classification cannot retain the old complete header or publish partial absence");
        var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, "p1");
        var delta = await inventory.ChangesSinceAsync("old-complete", 1, _ => true, CancellationToken.None, true);
        Require(delta.CursorInvalid, "leftover staging rows without a published header require bootstrap");
        static IEnumerable<(string Path, bool Deleted)> InterruptedRows()
        {
            for (var i = 0; i < 300; i++) yield return ("old-" + i, true);
            throw new IOException("Injected interruption after a committed 256-row batch.");
        }
    }

    private static async Task CursorListingClosureAsync()
    {
        using var h = new Harness();
        h.Wire.LatestCursor = "race-before";
        h.Wire.SeedChangePage("race-before", [h.Wire.ChangeFile("during.txt", "arrived during listing")], true, "race-middle");
        h.Wire.SeedChangePage("race-middle", [], false, "race-final");
        var full = await h.Inventory.FullAsync(1, _ => true, new Progress<string>(), CancellationToken.None);
        Require(full.Items.Any(item => item.Path == "during.txt") && full.CursorAfter == "race-final" &&
            h.Known.ReadDeletionClassification(Root())?.Cursor == full.CursorAfter,
            "full snapshot includes listing-race changes and publishes the last fully drained classification cursor");
    }
}
