namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task JournalWindowPrepareAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        LocalChecks.Seed(directory, "retained.txt", "observed but not applied");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        var start = new NtfsUsnCheckpoint(1, 0, 128, 0);
        var checkpoint = start;
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings,
            TimeProvider.System, StorageFailureClassifier.IsStateDamage, pairId: pair.PairId)
        { WalkCheckpointForTest = () => (checkpoint, false),
            GapIo = new LocalGapIo(store, Settings, new HashSet<string>(), () => false) };
        inventory.JournalReadForTest = (string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint through,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct) =>
        { changes = []; failure = null; kind = NtfsUsnReadFailure.Transient; return true; };
        fx.Provider.AddInventory(root, inventory);
        fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default,
            isStateDamage: StorageFailureClassifier.IsStateDamage));
        // Verification must be available during the first prepare. A deliberately paused
        // prepare creates sleeping ContentProof retry debt, which tests retry backoff instead.
        var first = await fx.Run().PreviewFirstSyncAsync(pair.PairId, new RecordingProgress(), default);
        Require(first.Actions.Any(action => action.Path == "retained.txt") &&
            store.Live(root.RootId, ["retained.txt"]).Single().Hash is not null,
            "first prepare observes and verifies the retained file without applying its plan");
        Require(fx.B.WriteCalls == 0 && store.Baselines(pair.PairId).Count == 0 &&
            store.SyncRetryAt(pair.PairId, "retained.txt") is null,
            "lost-publication fixture has no effects, baseline or sleeping content-proof failure");
        var debt = store.CountDeferredPlanning(pair.PairId);
        checkpoint = start with { NextUsn = start.NextUsn + JournalReplayWindow.ReplayWindowBytes * 7 / 2 };
        // Discard both prepares, as if publication failed. No pending/effect cursor is supplied.
        await fx.Run().PreviewFirstSyncAsync(pair.PairId, new RecordingProgress(), default);
        Require(store.Cursor(root.RootId) is null && store.CountDeferredPlanning(pair.PairId) == debt,
            "prepare across 3.5 windows cannot commit a cursor or intermediate planning debt");
        var recovered = await fx.Run().PreviewFirstSyncAsync(pair.PairId, new RecordingProgress(), default);
        Console.WriteLine($"Journal prepare: effects={fx.B.WriteCalls}; baselines={store.Baselines(pair.PairId).Count}; cursor={store.Cursor(root.RootId) ?? "null"}; " +
            $"debt={store.CountDeferredPlanning(pair.PairId)}; unavailable={recovered.RunUnavailable}; " +
            $"actions={string.Join(",", recovered.Actions.Select(a => a.Path + ":" + a.Kind))}");
        Console.WriteLine($"Journal live={string.Join(";", store.Live(root.RootId, ["retained.txt"]).Select(i => i.Path + ":" + i.Flags + ":hash=" + (i.Hash is not null)))}; skipped={string.Join(";", recovered.SkippedActions.Select(a => a.Path + ":" + a.Kind))}; notices={string.Join(";", recovered.Notices.Select(n => n.Path + ":" + n.ReasonKey))}; unavailable={recovered.RunUnavailable}");
        Require(store.Cursor(root.RootId) is null && recovered.Actions.Any(action => action.Path == "retained.txt"),
            "prepare after lost publication still plans retained observed-but-unapplied files");
        Console.WriteLine("PASS first-sync prepare retains unexecuted observations across window boundaries");
    }
}
