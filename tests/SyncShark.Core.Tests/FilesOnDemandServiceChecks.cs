using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class FilesOnDemandServiceChecks
{
    internal static async Task RunAsync(string root)
    {
        using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System);
        var a = new RootDef("local", SyncProviders.Local, null, root, SyncLocalEndpointStore.ObjectId(1, 1));
        var b = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root");
        var pair = new PairDef("pair", "Pair A", a, b, [], [], true);
        store.SavePair(pair);
        store.EnableStorage();
        var files = Enumerable.Range(0, 12).Select(i => new ItemState($"file-{i:D2}.txt", ItemKind.File,
            $"object-{i}", null, new string('a', 64), 4, DateTimeOffset.UnixEpoch, ItemFlags.OwnedPointer)).ToArray();
        store.WriteSnapshot(new Snapshot(a.RootId, Side.A, store.BeginGeneration(a.RootId, "snapshot"), "cursor", files, []));
        store.SetStoragePolicy(a.RootId, a.RootObjectId, "offline");
        using var connections = new CloudFilesLifetime((_, _, _) => throw new InvalidOperationException("Native provider called in offline service test"), _ => false, store);
        var calls = 0;
        var running = 0;
        var maximum = 0;
        async Task<StorageObserved> Hydrate(PairDef _, ItemState item, Action validate, CancellationToken ct)
        {
            validate();
            Interlocked.Increment(ref calls);
            var active = Interlocked.Increment(ref running);
            int prior;
            do { prior = maximum; if (prior >= active) break; }
            while (Interlocked.CompareExchange(ref maximum, active, prior) != prior);
            try { await Task.Delay(5, ct); validate(); return new("AvailableOffline", true, true); }
            finally { Interlocked.Decrement(ref running); }
        }
        store.SetPairPaused(pair.PairId, true);
        await using (var paused = new StorageSettlementService(store, connections, hydrate: Hydrate))
            await paused.DrainAsync(CancellationToken.None);
        Require(calls == 0, "paused roots are filtered before hydration or native provider work");
        store.SetPairPaused(pair.PairId, false);
        store.SetStorageRootBackoff(a.RootId, TimeSpan.FromHours(1));
        await using (var blocked = new StorageSettlementService(store, connections, hydrate: Hydrate))
            await blocked.DrainAsync(CancellationToken.None);
        Require(calls == 0, "durable pair backoff survives a new service instance");
        store.ClearStorageRootBackoff(a.RootId);
        await using (var service = new StorageSettlementService(store, connections, hydrate: Hydrate))
            await service.DrainAsync(CancellationToken.None);
        Require(calls == files.Length && maximum <= 4 && maximum > 1, "offline command really hydrates with bounded parallelism");
        Require(store.StorageHydrationPage().Count == 0, "confirmed offline jobs retire durably");

        store.SetStoragePolicy(a.RootId, a.RootObjectId, "offline");
        await using (var failed = new StorageSettlementService(store, connections,
            hydrate: (_, _, _, _) => throw new IOException("provider item failed")))
            await failed.DrainAsync(CancellationToken.None);
        Require(store.StorageHydrationFor(a.RootId, files[0].ObjectId) is not null,
            "failed hydration retains durable intent");
        Require(store.OpenNotices(pair.PairId).Count(n => n.Storage?.Operation == "offline") == files.Length,
            "one current failure notice per failed entity");
        var target = new StorageNotice(a.RootId, files[0].ObjectId, b.RootId, "offline", "offline");
        await using (var retry = new StorageSettlementService(store, connections, target: target, hydrate: Hydrate))
            await retry.DrainAsync(CancellationToken.None);
        Require(store.StorageHydrationFor(a.RootId, files[0].ObjectId) is null && calls == files.Length + 1,
            "explicit Retry targets exactly one deferred hydration entity");
        Require(!store.OpenNotices(pair.PairId).Any(n => n.Storage?.ObjectId == files[0].ObjectId),
            "successful Retry closes the entity failure");
        await CheckOfflinePairRaceAsync(Path.Combine(root, "pair-race"));
        Console.WriteLine("PASS bounded offline hydration, paused/backed-off admission, durable failure, callback correlation and Retry");
    }

    private static async Task CheckOfflinePairRaceAsync(string root)
    {
        var clock = new OfflineClock();
        using var store = SqliteStateStore.Open(root, V2Settings.Default, clock);
        var a = new RootDef("local", SyncProviders.Local, null, root, SyncLocalEndpointStore.ObjectId(1, 1));
        var b = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root");
        var pair = new PairDef("pair", "Pair A", a, b, [], [], true);
        store.SavePair(pair);
        store.EnableStorage();
        var files = Enumerable.Range(0, 3).Select(i => new ItemState($"file-{i}.txt", ItemKind.File,
            $"object-{i}", null, new string('a', 64), 4, DateTimeOffset.UnixEpoch, ItemFlags.OwnedPointer)).ToArray();
        store.WriteSnapshot(new Snapshot(a.RootId, Side.A, store.BeginGeneration(a.RootId, "snapshot"), "cursor", files, []));
        store.SetStoragePolicy(a.RootId, a.RootObjectId, "offline");
        store.EnqueueStoragePolicyPage(128);
        using var connections = new CloudFilesLifetime((_, _, _) => throw new InvalidOperationException("Native provider called in race test"), _ => false, store);
        StorageNotice Target(int i) => new(a.RootId, files[i].ObjectId, b.RootId, "offline", "offline");
        async Task Fail(int i, Exception error)
        {
            await using var service = new StorageSettlementService(store, connections, target: Target(i),
                hydrate: (_, _, _, _) => throw error);
            await service.DrainAsync(CancellationToken.None);
        }
        // Windows wakes the reader before callback notice persistence. Capture the original source
        // synchronously, then feed the correlated generic reader exception through the real service.
        using var attempt = new WindowsCloudFilesProvider.OfflineHydrationAttempt(() => { });
        attempt.Failed(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkDown));
        var diskFailure = new IOException("Disk full", unchecked((int)0x80070070));
        Require(ReferenceEquals(attempt.Correlate(diskFailure), diskFailure),
            "a captured transport error cannot replace a specific disk HRESULT");
        Require(!store.HasOpenStoragePairCause(pair.PairId), "race begins before pair notice persistence");
        await Fail(0, attempt.Correlate(new IOException("Windows read failed")));
        Require(store.StorageHydrationFor(a.RootId, files[0].ObjectId) is not null,
            "same-attempt transport failure retains the durable item retry");
        Require(store.OpenNotices(pair.PairId).Count == 1 && store.HasOpenStoragePairCause(pair.PairId),
            "typed callback source produces the sole pair notice before callback persistence");
        await Fail(1, new IOException("Windows read failed"));
        Require(store.OpenNotices(pair.PairId).Count == 1 && store.StorageHydrationFor(a.RootId, files[1].ObjectId) is not null,
            "generic I/O under an existing transport cause retains retry without an item notice");
        using var itemAttempt = new WindowsCloudFilesProvider.OfflineHydrationAttempt(() => { });
        itemAttempt.Failed(new InvalidDataException("Content hash differs"));
        await Fail(2, itemAttempt.Correlate(new IOException("Windows read failed")));
        Require(store.OpenNotices(pair.PairId).Any(n => n.Storage?.ObjectId == files[2].ObjectId),
            "correlated item-specific failure remains visible during the pair outage");
        Require(!StorageSettlementService.IsGenericOfflineIo(new IOException("Sharing", unchecked((int)0x80070020))) &&
            !StorageSettlementService.IsGenericOfflineIo(new IOException("Disk full", unchecked((int)0x80070070))) &&
            !StorageSettlementService.IsGenericOfflineIo(new InvalidDataException("Policy changed")),
            "sharing, disk and policy failures never qualify for generic suppression");
        var recovered = 0;
        Task<StorageObserved> Success(PairDef _, ItemState item, Action validate, CancellationToken ct)
        {
            validate();
            Interlocked.Increment(ref recovered);
            return Task.FromResult(new StorageObserved("AvailableOffline", true, true));
        }
        await using (var retry = new StorageSettlementService(store, connections, target: Target(0), hydrate: Success))
            await retry.DrainAsync(CancellationToken.None);
        Require(!store.HasOpenStoragePairCause(pair.PairId) && !store.StorageRootBackedOff(a.RootId),
            "successful pair recovery clears pair cause and persistent admission backoff");
        Require(store.OpenNotices(pair.PairId).Any(n => n.Storage?.ObjectId == files[2].ObjectId),
            "pair recovery does not blanket-close unrelated item failures");
        await Fail(1, new IOException("Unclassified item I/O"));
        Require(store.OpenNotices(pair.PairId).Any(n => n.Storage?.ObjectId == files[1].ObjectId),
            "generic I/O without pair evidence is an item failure");
        clock.Advance(TimeSpan.FromMinutes(2));
        await using (var automatic = new StorageSettlementService(store, connections, hydrate: Success))
            await automatic.DrainAsync(CancellationToken.None);
        Require(recovered == files.Length && files.All(f => store.StorageHydrationFor(a.RootId, f.ObjectId) is null),
            "ordinary non-targeted processing automatically retries deferred items after recovery");
        Require(store.OpenNotices(pair.PairId).Count == 0, "successful entity retries close only their own failures");
    }

    private sealed class OfflineClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
