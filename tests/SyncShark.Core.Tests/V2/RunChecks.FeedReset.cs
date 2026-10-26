namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task DropboxResetRoutesToReconciliation()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "reset-route-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("file.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        var saved = store.Cursor(fx.Pair.B.RootId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inventory = new ResetFeedInventory(fx.B, entered, release);
        fx.Provider.AddInventory(fx.Pair.B, inventory);
        var run = fx.Run();
        try
        {
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(report.Failed == 0 && inventory.FullReads == 1 && store.Cursor(fx.Pair.B.RootId) == saved,
                "AutoSync completes without joining the blocked reset listing or advancing the invalid cursor");
            Require(!await run.MonitorAsync(fx.Pair.PairId, new RecordingProgress(), default),
                "in-run reset synchronously registers the reconciliation scope");
        }
        finally
        {
            var stop = run.StopAsync();
            release.TrySetResult();
            await stop;
        }
    }

    private sealed class ResetFeedInventory(IInventorySource inner, TaskCompletionSource entered,
        TaskCompletionSource release) : IInventorySource
    {
        internal int FullReads { get; private set; }
        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
        {
            FullReads++;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return await inner.FullAsync(gen, include, p, ct);
        }
        public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
            (await inner.ChangesSinceAsync(cursor, gen, include, ct)) with { CursorInvalid = true, Changes = [] };
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.DeletionTimesAsync(paths, ct);
    }

    private static async Task GapRoutesToReconciliation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gap-route-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        LocalChecks.Seed(directory, "file.txt", "original");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        store.SavePair(fx.Pair with { A = root });
        var checkpoint = new NtfsUsnCheckpoint(7, 0, 100, 0);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings,
            TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage, pairId: fx.Pair.PairId)
        { WalkCheckpointForTest = () => (checkpoint, false) };
        inventory.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
        { changes = []; failure = null; kind = default; return true; };
        fx.Provider.AddInventory(root, inventory);
        await FirstSyncAsync(fx.Run(), fx);
        checkpoint = new NtfsUsnCheckpoint(7, 150, 200, 150);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inventory.DiscoveryReadForTest = async (_, ct) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(ct); return null; };
        var fullReads = 0;
        inventory.FullInventoryForTest = () => fullReads++;
        var run = fx.Run();
        try
        {
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default).WaitAsync(TimeSpan.FromSeconds(10));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(fullReads == 0 && store.GapInventoryPending(root.RootId),
                "journal gap replays once and leaves metadata selection to reconciliation without an AutoSync full walk");
        }
        finally
        {
            var stop = run.StopAsync();
            release.TrySetResult();
            await stop;
        }
    }
}
