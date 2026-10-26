namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task UrgentRehashWhileActiveAsync(string folder)
    {
        var root = Path.Combine(folder, "root");
        Directory.CreateDirectory(root);
        LocalChecks.Seed(root, "edited.txt", "before edit");
        LocalChecks.Seed(root, "bulk.txt", "bulk content");
        var clock = new LocalChecks.FixedClock(DateTimeOffset.UtcNow);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, clock, LocalItems.CaptureOptionalHash);
        var def = LocalChecks.RootOf(root, "root-local");
        var pair = new PairDef("urgent-pair", "Pair A", def, new RootDef("remote", "fake", null, "/Remote", "remote-root"), [], [], true);
        store.SavePair(pair);
        var inventory = LocalChecks.Inventory(root, store, clock);
        var snapshot = await inventory.FullAsync(store.BeginGeneration(def.RootId, "full"), _ => true, null!, default);
        store.WriteSnapshot(snapshot);
        LocalChecks.Seed(root, "edited.txt", "genuine edited content");
        var delta = await inventory.ChangesForPathsAsync(["edited.txt"], snapshot.CursorAfter,
            store.BeginGeneration(def.RootId, "delta"), _ => true, default);
        store.WriteDelta(delta);
        var edited = store.Live(def.RootId, ["edited.txt"]).Single();
        var bulk = store.Live(def.RootId, ["bulk.txt"]).Single();
        store.RequireRehash(def.RootId, bulk, "bulk verification");
        store.RequireRehash(def.RootId, edited, "journal edit");
        store.DeferPlanning(pair.PairId, [(def.RootId, edited)], urgent: true);
        var io = new LocalGapIo(store, V2Settings.Default, new HashSet<string>(), () => true, clock);
        inventory.GapIo = io;
        inventory.VerificationElapsedForTest = () => TimeSpan.Zero;
        await using var gaps = new LocalGapWorker(store, io, StorageFailureClassifier.IsStateDamage);
        await using var indexing = new LocalIndexingRuns(store, (_, ct) => inventory.VerifyIndexingPassAsync(ct), gaps, io);
        store.SavePair(pair with { Enabled = false });
        LocalChecks.Require(await indexing.RunAsync(def, default) == "paused" &&
            store.ByObject(def.RootId, edited.ObjectId)!.Hash is null, "pair off blocks urgent indexing");
        store.SavePair(pair);
        store.SetPairPaused(pair.PairId, true);
        LocalChecks.Require(await indexing.RunAsync(def, default) == "paused" &&
            store.ByObject(def.RootId, edited.ObjectId)!.Hash is null, "pair pause blocks urgent indexing");
        store.SetPairPaused(pair.PairId, false);
        var count = await inventory.VerifyIndexingPassAsync(default);
        LocalChecks.Require(io.Paused && count == 1 &&
            store.ByObject(def.RootId, edited.ObjectId)!.Hash == LocalChecks.BlockHash(LocalChecks.Bytes("genuine edited content")) &&
            store.ByObject(def.RootId, bulk.ObjectId)!.Hash is null,
            "urgent edited content verifies while active; older bulk debt stays unverified");
        store.RequireRehash(def.RootId, edited, "foreground proof");
        var proofs = await inventory.HashAsync(["bulk.txt", "edited.txt"], default);
        LocalChecks.Require(proofs.ContainsKey("edited.txt") && !proofs.ContainsKey("bulk.txt"),
            "foreground proof shares urgent classification and retains bulk idle gate");
        var budget = new VerificationBudget(() => TimeSpan.Zero);
        for (var i = 0; i < SqliteStateStore.UrgentHashCap; i++)
            LocalChecks.Require(budget.AdmitUrgent(true), "urgent admission fits existing cap");
        LocalChecks.Require(!budget.AdmitUrgent(true) && !budget.AdmitUrgent(false), "urgent cap cannot be exceeded across proof pages");
    }
}
