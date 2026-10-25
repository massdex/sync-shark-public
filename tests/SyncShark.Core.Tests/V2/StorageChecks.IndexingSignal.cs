namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    public static async Task IndexingSignalChecksAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var sqlite = SqliteStateStore.Open(root, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        using var memory = new InMemoryStateStore();
        foreach (IStateStore store in new IStateStore[] { sqlite, memory })
        {
            var version = store.IndexingWorkVersion;
            _ = store.Pairs();
            Require(store.IndexingWorkVersion == version, "indexing reads do not publish wakeups");
            store.SavePair(Pair("signal"));
            await store.WaitForIndexingWorkAsync(version, default).WaitAsync(TimeSpan.FromSeconds(2));
            Require(store.IndexingWorkVersion != version, "mutation before wait cannot lose wakeup");
            version = store.IndexingWorkVersion;
            using var cancelled = new CancellationTokenSource();
            var abandoned = store.WaitForIndexingWorkAsync(version, cancelled.Token);
            var surviving = store.WaitForIndexingWorkAsync(version, default);
            cancelled.Cancel();
            try { await abandoned; throw new InvalidOperationException("wait cancellation ignored"); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            Require(!surviving.IsCompleted, "one cancelled waiter cannot cancel shared notification");
            store.SaveSyncRetry("signal", new SyncRetry("file", "scan", 0, "retry", DateTimeOffset.UtcNow));
            await surviving.WaitAsync(TimeSpan.FromSeconds(2));
            version = store.IndexingWorkVersion;
            var item = F("signal.txt", "signal-object", "11");
            store.WriteSnapshot(new("ra", Side.A, store.BeginGeneration("ra", "full"), "cursor", [item], []));
            Require(store.IndexingWorkVersion != version, "inventory publication wakes index consumer");
            version = store.IndexingWorkVersion;
            store.RequireRehash("ra", item, "changed");
            Require(store.IndexingWorkVersion != version, "new entity debt wakes index consumer");
            version = store.IndexingWorkVersion;
            if (store is SqliteStateStore sql) sql.SetPairPaused("signal", true);
            else ((InMemoryStateStore)store).SetPairPaused("signal", true);
            Require(store.IndexingWorkVersion != version, "pair pause publishes eligibility change");
            var other = ReferenceEquals(store, sqlite) ? (IStateStore)memory : sqlite;
            version = store.IndexingWorkVersion;
            other.SavePair(Pair("other"));
            Require(store.IndexingWorkVersion == version, "store instances do not share signal state");
        }
        Console.WriteLine("PASS indexing work signal checks");
    }
}
