namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task StoreCapabilityChecksAsync()
    {
        foreach (var capability in new[] { typeof(IStoreRemovalObservation), typeof(IStoragePointerCommit) })
            Require(capability.GetMethods().All(method => method.IsAbstract), "store capabilities have no default members");
        var timedOut = false;
        try
        {
            FairStoreGate.Cleanup(() =>
            {
                var token = StoreAdmission.CurrentCancellation;
                Require(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)), "Storage cleanup owns a bounded deadline");
                token.ThrowIfCancellationRequested();
            }, admissionTimeout: TimeSpan.FromMilliseconds(1));
        }
        catch (TimeoutException) { timedOut = true; }
        Require(timedOut, "Storage cleanup deadline retains its timeout failure classification");
        var pair = new PairDef("pair", "Pair A",
            new("a", SyncProviders.Local, null, "/A", "root-a"),
            new("b", SyncProviders.Local, null, "/B", "root-b"), [], [], true);
        var folder = new ItemState("folder", ItemKind.Folder, "folder-id", null, null, 0,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        var child = folder with { Path = "folder/child", ObjectId = "child-id", Kind = ItemKind.File };
        var action = new SyncAction("remove", 1, ActionKind.Delete, Side.A, folder.Path, null,
            null, folder, false, "object");
        using var memory = new InMemoryStateStore();
        var expected = await DeleteSourceGuard.CaptureReviewedRemovalAsync(action, pair, memory,
            new RemovalCapabilityInventory(folder, child, null), default, StorageFailureClassifier.IsStateDamage);
        Require(expected is not null, "missing observation capability preserves in-memory proof");
        using var gate = new SemaphoreSlim(0, 1);
        var fallbackCalls = 0;
        var fallback = new StoreCommitter(memory, gate, pair.PairId, 1);
        var fallbackResult = await fallback.RunPointerUpdateAsync(action, () =>
        {
            fallbackCalls++;
            return Task.FromResult<ActionResult?>(null);
        }, default);
        Require(fallbackCalls == 1 && fallbackResult is null && gate.CurrentCount == 0,
            "missing pointer capability runs the original callback without acquiring commit gate");

        foreach (var code in new[] { 0, 5, 11, 26 })
        {
            var root = LocalChecks.FreshRoot("store-capability-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
            store.SavePair(pair);
            Exception? failure = code == 0 ? null : new IOException("wrapped",
                new Microsoft.Data.Sqlite.SqliteException("synthetic", code));
            Exception? caught = null;
            string? proof = null;
            try
            {
                proof = await DeleteSourceGuard.CaptureReviewedRemovalAsync(action, pair, store,
                    new RemovalCapabilityInventory(folder, child, failure), default, StorageFailureClassifier.IsStateDamage);
            }
            catch (Exception error) { caught = error; }
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = SqliteStateStore.DatabasePath(root) }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM removal_observation;";
            var retained = (long)command.ExecuteScalar()!;
            Require(code is 11 or 26 ? ReferenceEquals(caught, failure) && retained == 1 : caught is null && retained == 0,
                "damage skips finally clear; successful and item-failed observation clears staged rows");
            if (code == 0) Require(proof == expected, "capability and memory observation produce identical proof");
            if (code == 5) Require(proof is null, "item failure cannot authorize removal");

            var observation = (IStoreRemovalObservation)store;
            observation.StageRemovalItems("cleanup", Side.A, [child], ordinalPaths: true);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            using (StoreAdmission.WithCancellation(cancelled.Token)) observation.ClearRemovalObservation("cleanup");
            Require(!observation.RemovalObservation("cleanup", Side.A).Any(),
                "Storage cleanup overrides caller cancellation rather than abandoning staged observations");
            if (code == 0)
            {
                using var commitGate = new SemaphoreSlim(1, 1);
                var generation = store.BeginGeneration(pair.A.RootId, "snapshot");
                var committer = new StoreCommitter(store, commitGate, pair.PairId, generation);
                store.RecordIntent(pair.PairId, action);
                using (StoreAdmission.WithCancellation(cancelled.Token))
                    await committer.CommitAsync(action, new(action.ActionId, Outcome.Skipped, null, null, null, "unchanged"));
                Require(store.UnresolvedIntents(pair.PairId).Count == 0,
                    "completed-effect commit remains durable under cancelled ambient admission");
                store.Dispose();
                var fatal = new Microsoft.Data.Sqlite.SqliteException("synthetic", 11);
                observation.ClearRemovalObservation("cleanup", primaryError: fatal);
                Require(fatal.Data["StoreCleanupFailure"] is Exception,
                    "capability forwards primary damage so Storage retains fatal authority on cleanup failure");
            }
        }
    }

    private sealed class RemovalCapabilityInventory(ItemState folder, ItemState child, Exception? failure) : IInventorySource
    {
        public async Task ReadSubtreeAsync(string path, Func<IReadOnlyList<ItemState>, Task> accept,
            CancellationToken ct, IProgress<string>? progress = null)
        {
            await accept([folder, child]);
            if (failure is not null) throw failure;
        }
        public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct) => throw new NotSupportedException();
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) => throw new NotSupportedException();
    }
}
