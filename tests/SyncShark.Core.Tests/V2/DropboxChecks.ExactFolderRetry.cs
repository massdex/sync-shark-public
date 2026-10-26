namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    internal static async Task ReconciliationExactFolderRetryAsync()
    {
        using var h = new Harness();
        var directory = LocalChecks.FreshRoot("exact-folder-retry");
        using var store = SqliteStateStore.Open(Path.Combine(directory, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("p1", "Pair", LocalChecks.RootOf(directory, "ra"), Root(), [], [], true);
        store.SavePair(pair);
        var a = new FakeEndpoint(Side.A, pair.A.RootId) { DropboxHashes = true };
        var b = new FakeEndpoint(Side.B, pair.B.RootId) { DropboxHashes = true };
        foreach (var endpoint in new[] { a, b })
        {
            endpoint.Seed("Missing", "", ItemKind.Folder);
            endpoint.Seed("Missing/child.txt", "retained");
        }
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b);
        var run = new SyncRun(store, new Executor(store, StorageChecks.Fast, ExecutorChecks.Admission(),
            isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException),
            provider, StorageChecks.Fast, TimeProvider.System);
        var progress = new RecordingProgress();
        var preview = await run.PreviewFirstSyncAsync(pair.PairId, progress, default);
        var initial = await run.ApplyAsync(pair.PairId, preview, progress, default);
        Require(initial.Failed == 0 && store.FirstSyncDone(pair.PairId), "folder retry fixture establishes baselines");
        var selected = store.Live(pair.B.RootId, ["Missing"]).Single();
        // The actual metadata/listing endpoint now has neither folder nor child; cached rows remain.
        provider.AddInventory(pair.B, new DropboxInventory(pair.B, Side.B, h.Client, store, pair.PairId))
            .AddWriter(pair.B, h.Writer());
        a.AppliedActions.Clear();
        var batch = new ReconcileBatch("batch", ["Missing"], new HashSet<string>(),
            new HashSet<string> { selected.ObjectId }, (_, _) => false);
        await run.IncrementalAsync(pair.PairId, progress, default, new RunOptions { Batch = batch });
        Require(store.SyncRetryAt(pair.PairId, "Missing") is { Kind: "scan" } &&
            store.Live(pair.B.RootId, ["Missing/child.txt"]).Count == 1,
            "an exact missing cloud folder retains descendants and hands observation to scan retry");
        Require(a.AppliedActions.All(action => action.Kind != ActionKind.MkDir && action.Kind != ActionKind.Delete) &&
            store.OpenNotices(pair.PairId).All(notice => notice.ReasonKey == "sync-retry"),
            "unresolved cloud descendants produce neither KeepFolder nor false edit-beats-delete notices");
        var retry = store.SyncRetryAt(pair.PairId, "Missing")!;
        store.SaveSyncRetry(pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
        var retried = await run.IncrementalAsync(pair.PairId, progress, default);
        Require(retried.Failed == 0 && store.SyncRetryAt(pair.PairId, "Missing") is null &&
            store.LiveUnder(pair.B.RootId, ["Missing"]).Count == 0 &&
            await a.ObserveAsync("Missing", default) is null &&
            await a.ObserveAsync("Missing/child.txt", default) is null,
            "the existing recursive scan retry resolves the subtree and propagates proven deletion");
        Require(a.AppliedActions.All(action => action.Kind != ActionKind.MkDir) &&
            store.OpenNotices(pair.PairId).All(notice => notice.ReasonKey == "sync-retry"),
            "settled folder deletion never recreates the folder or reports a false conflict");
    }
}
