namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    internal static async Task HotfixChecksAsync(string folder)
    {
        try
        {
        await ScopeOverflowAsync(Path.Combine(folder, "overflow"));
        await DeletedAliasAsync(Path.Combine(folder, "deleted-alias"));
        await AncestorRetryAsync(Path.Combine(folder, "ancestor"));
        await HardLinkReplaySliceAsync(Path.Combine(folder, "hardlink"));
        await VerificationSliceAsync(Path.Combine(folder, "verification"));
        await FairAdmissionAsync();
        await WorkerForegroundAsync(Path.Combine(folder, "worker"));
        await ReplayBudgetAsync(Path.Combine(folder, "replay"));
        Console.WriteLine("PASS bounded journal replay, hard-link scopes, verification slice/progress and fair store admission");
        }
        finally { RemoveFixture(folder); }
    }

    private static async Task HardLinkReplaySliceAsync(string folder)
    {
        var root = Path.Combine(folder, "root");
        LocalChecks.Seed(root, "one.txt", "old");
        LocalChecks.Seed(root, "healthy.txt", "old");
        LocalChecks.Require(CreateHardLink(Path.Combine(root, "alias.txt"), Path.Combine(root, "one.txt"), IntPtr.Zero), "hard-link slice fixture");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.WalkCheckpointForTest = () => (new(1, 0, 100, 0), false);
        var snapshot = await SaveFull(inventory, store);
        var one = store.Live("root-local", ["one.txt"]).Single();
        var good = store.Live("root-local", ["healthy.txt"]).Single();
        var def = LocalChecks.RootOf(root, "root-local");
        var parent = LocalItems.ParseObjectId(def.RootObjectId).FileId;
        LocalChecks.Seed(root, "healthy.txt", "new healthy content");
        LocalChecks.Require(CreateHardLink(Path.Combine(root, "new-alias.txt"), Path.Combine(root, "one.txt"), IntPtr.Zero), "new unnamed alias fixture");
        inventory.WalkCheckpointForTest = () => (new(1, 0, 200, 0), false);
        inventory.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
        {
            changes = [new(LocalItems.ParseObjectId(one.ObjectId).FileId, parent, 110, 0x10000, FileAttributes.Normal, ""),
                new(LocalItems.ParseObjectId(good.ObjectId).FileId, parent, 120, 0x100, FileAttributes.Normal, "")];
            failure = null; kind = NtfsUsnReadFailure.Transient; return true;
        };
        var record = new NtfsUsnChange(LocalItems.ParseObjectId(one.ObjectId).FileId, parent, 110, 0x10000, FileAttributes.Normal, "");
        var names = new LocalJournalNames(def, store, [record], 100);
        var cached = names.FindAmbiguous(record, "", default);
        LocalChecks.Require(cached is { Count: 3 } && ReferenceEquals(cached, names.FindAmbiguous(record, "", default)),
            "ambiguous names reuse one parent batch instead of relisting per record");
        var delta = await inventory.ChangesSinceAsync(snapshot.CursorAfter, store.BeginGeneration(def.RootId, "delta"), _ => true, default);
        LocalChecks.Require(!delta.CursorInvalid && delta.CursorAfter == "0000000000000001:200" &&
            delta.Changes.Any(change => change.Path == "healthy.txt") &&
            delta.Changes.All(change => change.ObjectId != one.ObjectId) &&
            delta.IncompleteSubtrees.Contains("one.txt") && delta.IncompleteSubtrees.Contains("alias.txt") &&
            delta.IncompleteSubtrees.Contains("new-alias.txt") && !delta.IncompleteSubtrees.Contains(""),
            "one hard-link record protects every known alias and retains unrelated journal changes");
        store.WriteDelta(delta);
        LocalChecks.Require(store.Live(def.RootId, ["one.txt", "alias.txt"]).All(item => item.Flags.HasFlag(ItemFlags.NeedsRehash)),
            "ambiguous entity retains durable rehash debt after replay publication");
    }

    private static async Task VerificationSliceAsync(string folder)
    {
        var root = Path.Combine(folder, "root");
        for (var n = 0; n < 600; n++) LocalChecks.Seed(root, $"file-{n:D4}.txt", "content");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.WalkCheckpointForTest = () => (new(1, 0, 100, 0), false);
        await SaveFull(inventory, store);
        store.RequireRehashBatch("root-local", store.Live("root-local"), "slice fixture");
        var progress = new List<(long Done, long Backlog)>();
        inventory.VerificationProgress = (done, backlog) => progress.Add((done, backlog));
        await inventory.VerifyPendingAsync(_ => true, _ => { }, default);
        LocalChecks.Require(store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == 88 &&
            progress.First() == (0L, 600L) && progress.Max(item => item.Done) == 512,
            "foreground verifies at most 512 candidates and reports the durable backlog");
        await inventory.VerifyPendingAsync(_ => true, _ => { }, default);
        LocalChecks.Require(store.LiveFlaggedSummary("root-local", ItemFlags.NeedsRehash, 0).Count == 0,
            "later verification slice resumes the protected remainder");
    }

    private static async Task FairAdmissionAsync()
    {
        var gate = new FairStoreGate();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new List<string>();
        var owner = Task.Run(() =>
        {
            using (gate.Enter())
            using (gate.Enter())
            { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); }
        });
        entered.Wait(TimeSpan.FromSeconds(15));
        var foreground = Task.Run(() => { using (gate.Enter()) order.Add("foreground"); });
        try
        {
            LocalChecks.Require(SpinWait.SpinUntil(() => gate.Waiting == 1, TimeSpan.FromSeconds(15)), "foreground queued behind held store gate");
            var background = Task.Run(() => { using (gate.Enter()) order.Add("background"); });
            LocalChecks.Require(SpinWait.SpinUntil(() => gate.Waiting == 2, TimeSpan.FromSeconds(15)), "later background page queued");
            release.Set();
            await Task.WhenAll(owner, foreground, background).WaitAsync(TimeSpan.FromSeconds(15));
            LocalChecks.Require(order.SequenceEqual(["foreground", "background"]), "queued foreground acquires before a later background page; reentrancy is preserved");
        }
        finally { release.Set(); await Task.WhenAll(owner, foreground); }
    }

    private static async Task WorkerForegroundAsync(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        var stamp = DateTimeOffset.UtcNow;
        var root = new RootDef("worker-root", SyncProviders.Local, null, folder, SyncLocalEndpointStore.ObjectId(1, 1));
        var rows = Enumerable.Range(1, 1024).Select(n => new ItemState("file-" + n, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(1, (ulong)n + 10), null, new string('a', 64), 3, stamp, ItemFlags.None)).ToArray();
        store.WriteSnapshot(new(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), "0000000000000001:200", rows, []));
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, "0000000000000001:200", 100, true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new GapCheckIo(stamp, "0000000000000001:200") { OnProbe = (_, _) => started.TrySetResult() };
        await using var worker = new LocalGapWorker(gaps, io);
        worker.Start(root);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var foreground = Task.Run(() =>
        {
            for (var n = 0; n < 32; n++) store.LiveFlaggedSummary(root.RootId, ItemFlags.NeedsRehash, 0);
        });
        await foreground.WaitAsync(TimeSpan.FromSeconds(15));
        LocalChecks.Require(io.Probes > 0 && foreground.IsCompletedSuccessfully, "foreground store calls complete while gap worker runs");
    }

    private static async Task ReplayBudgetAsync(string folder)
    {
        var root = Path.Combine(folder, "root");
        LocalChecks.Seed(root, "file.txt", "content");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.WalkCheckpointForTest = () => (new(1, 0, 100, 0), false);
        await SaveFull(inventory, store);
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap("root-local", "0000000000000001:100", 50, true);
        var oldEpoch = gaps.GapEpoch("root-local")!;
        var now = new NtfsUsnCheckpoint(1, 0, 100 + LocalInventory.MaximumReplayBytes + 1, 0);
        inventory.WalkCheckpointForTest = () => (now, false);
        inventory.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
        { throw new InvalidOperationException("oversized replay must be rejected before journal IO"); };
        var delta = await inventory.ChangesSinceAsync("0000000000000001:100", store.BeginGeneration("root-local", "delta"), _ => true, default);
        LocalChecks.Require(delta.CursorInvalid && store.Cursor("root-local") == now.Cursor && store.GapInventoryPending("root-local"),
            "oversized replay is preflighted without journal IO and publishes a protected checkpoint");
        var fresh = await inventory.FullAsync(store.BeginGeneration("root-local", "full"), _ => true, null!, default);
        LocalChecks.Require(fresh.Items.Count() == 1 && gaps.GapEpoch("root-local")!.Boundary == oldEpoch.Boundary,
            "one fresh walk preserves the oldest content-proof boundary");
    }
}
