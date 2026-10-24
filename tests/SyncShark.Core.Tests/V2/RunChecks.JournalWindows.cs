namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task JournalWindowRunAsync(string folder)
    {
        await JournalWindowPrepareAsync(Path.Combine(folder, "prepare"));
        await JournalWindowRunCaseAsync(Path.Combine(folder, "four"), 8);
        await JournalWindowRunCaseAsync(Path.Combine(folder, "split"), 2);
        await JournalWindowRunCaseAsync(Path.Combine(folder, "pending-inventory"), 8, true);
    }

    private static async Task JournalWindowRunCaseAsync(string folder, int windowBudget, bool pendingInventory = false)
    {
        var settings = Settings with { LocalReplayWindowsPerRun = windowBudget };
        var directory = Path.Combine(folder, "root");
        for (var n = 0; n < 4; n++) LocalChecks.Seed(directory, $"edit-{n}.txt", "journal edit " + n);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var start = new NtfsUsnCheckpoint(1, 0, 128, 0);
        var target = start with { NextUsn = start.NextUsn + JournalReplayWindow.ReplayWindowBytes * 7 / 2 };
        var checkpoint = start;
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), settings,
            TimeProvider.System, StorageFailureClassifier.IsStateDamage, pairId: pair.PairId)
        { WalkCheckpointForTest = () => (checkpoint, false),
            GapIo = new LocalGapIo(store, Settings, new HashSet<string>(), () => true) };
        var snapshot = await inventory.FullAsync(store.BeginGeneration(root.RootId, "full"), _ => true, null!, default);
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(snapshot.Gen, start.Cursor, true);
        await inventory.VerifyIndexingPassAsync(default);
        LocalChecks.Seed(directory, "edit-3.txt", "user edit near the end of a large retained journal");
        checkpoint = target;
        var reads = new List<long>();
        inventory.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
        {
            failure = null; kind = NtfsUsnReadFailure.Transient;
            if (from.NextUsn == through.NextUsn) { changes = []; return true; }
            reads.Add(from.NextUsn);
            var index = (from.NextUsn - start.NextUsn) / JournalReplayWindow.ReplayWindowBytes;
            var item = store.Live(root.RootId, [$"edit-{index}.txt"]).Single();
            changes = [new(LocalItems.ParseObjectId(item.ObjectId).FileId, LocalItems.ParseObjectId(root.RootObjectId).FileId,
                from.NextUsn, index == 3 ? 0x1U : 0x100U, FileAttributes.Normal, item.Path)];
            return true;
        };
        var walks = 0;
        string? epoch = null;
        if (pendingInventory)
        {
            ((ILocalGapStore)store).BeginGap(root.RootId, start.Cursor, start.NextUsn - 1, true,
                "previous bounded journal overflow");
            epoch = ((ILocalGapStore)store).GapEpoch(root.RootId)!.Epoch;
            Require(store.GapInventoryPending(root.RootId), "prior overflow starts with inventory pending");
        }
        inventory.FullInventoryForTest = () =>
        {
            walks++;
            if (!pendingInventory || walks > 1) throw new InvalidOperationException("unexpected repeated full walk");
        };
        fx.Provider.AddInventory(root, inventory);
        fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default,
            isStateDamage: StorageFailureClassifier.IsStateDamage));
        var firstReport = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        if (pendingInventory) Require(walks == 1 && !store.GapInventoryPending(root.RootId) &&
            ((ILocalGapStore)store).GapEpoch(root.RootId)!.Epoch == epoch,
            "one pending-inventory walk settles without replacing its gap epoch");
        Require(firstReport.JournalBacklog == (windowBudget == 2), "run report carries unread journal suffix");
        Require(reads.Count == Math.Min(4, windowBudget), "real run respects its replay-window budget");
        if (windowBudget == 2) Require(store.Cursor(root.RootId) != target.Cursor, "budget split retains the unread suffix");
        await inventory.VerifyIndexingPassAsync(default);
        var secondReport = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(!secondReport.JournalBacklog, "caught-up run clears backlog flag");
        Require(reads.Count == 4 && reads.Distinct().Count() == 4, "real run replays 3.5 windows exactly once");
        Require(store.Live(root.RootId, ["edit-3.txt"]).Single().Size == new FileInfo(Path.Combine(directory, "edit-3.txt")).Length,
            "late user edit is inventoried and handed to planning no later than the next run");
        if (pendingInventory)
        {
            Require(walks == 1 && !store.GapInventoryPending(root.RootId), "second run uses pure delta after settling inventory pending");
            Console.WriteLine("PASS existing inventory-pending epoch: one walk, catch-up, then delta");
            return; // Fake journal identity cannot supply real native gap hash proofs.
        }
        // Content verification remains its existing background owner, not gap hashing.
        await inventory.VerifyIndexingPassAsync(default);
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        for (var n = 0; n < 4; n++)
            Require(await fx.B.ObserveSourceMetadataAsync($"edit-{n}.txt", default) is not null,
                "every window, including the late user edit, reaches planning and transfer");
        Require((pendingInventory || ((ILocalGapStore)store).GapEpoch(root.RootId) is null) && !store.GapInventoryPending(root.RootId),
            "incident-shaped replay never creates gap hashing work");
        Console.WriteLine("PASS real run plans changes from all journal windows without a gap");
    }
}
