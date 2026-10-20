using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class FilesOnDemandServiceChecks
{
    internal static async Task RunAsync(string root)
    {
        var clock = new OfflineClock();
        using var store = SqliteStateStore.Open(root, V2Settings.Default, clock);
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
        Require(files.All(file => store.StorageHydrationFor(a.RootId, file.ObjectId) is not null),
            "every failed hydration retains its durable intent");
        Require(!store.HasStorageWork(),
            "deferred offline jobs are not reported as due background work");
        Require(store.StorageHydrationPage().Count == 0,
            "deferred offline jobs never appear on an untargeted background page");
        Require(store.OpenNotices(pair.PairId).Count(n => n.Storage?.Operation == "offline") == files.Length,
            "one current failure notice per failed entity");
        var target = new StorageNotice(a.RootId, files[0].ObjectId, b.RootId, "offline", "offline");
        await using (var retry = new StorageSettlementService(store, connections, target: target, hydrate: Hydrate))
            await retry.DrainAsync(CancellationToken.None);
        Require(store.StorageHydrationFor(a.RootId, files[0].ObjectId) is null && calls == files.Length + 1,
            "explicit Retry targets exactly one deferred hydration entity");
        Require(files.Skip(1).All(file => store.StorageHydrationFor(a.RootId, file.ObjectId) is not null),
            "explicit Retry leaves every other deferred entity untouched");
        Require(store.StorageHydrationPage().Count == 0,
            "targeted Retry never makes the other deferred entities due");
        Require(store.OpenNotices(pair.PairId).Count(n => n.Storage?.Operation == "offline") == files.Length - 1,
            "targeted Retry closes only its own entity failure");
        Require(!store.OpenNotices(pair.PairId).Any(n => n.Storage?.ObjectId == files[0].ObjectId),
            "successful Retry closes the entity failure");
        await CheckOfflinePairRaceAsync(Path.Combine(root, "pair-race"));
        await CheckSettlementSchedulingAsync(Path.Combine(root, "settlement-failures"));
        await CheckRefreshClassification(Path.Combine(root, "refresh-classification"));
        Console.WriteLine("PASS bounded offline hydration, paused/backed-off admission, durable failure, callback correlation and Retry");
    }

    private static async Task CheckRefreshClassification(string root)
    {
        using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System);
        var a = new RootDef("local", SyncProviders.Local, null, root, SyncLocalEndpointStore.ObjectId(1, 1));
        var b = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root");
        var pair = new PairDef("pair", "Pair A", a, b, [], [], true);
        store.SavePair(pair);
        store.EnableStorage();
        using var connections = new CloudFilesLifetime(
            (_, _, _) => throw new InvalidOperationException("native provider must not open"), _ => false, store);
        var receipt = new PilotRemoteEntry("Folder/file.bin", PilotEntryKind.File, "id:remote-file", "rev-1", 4, new string('a', 64));
        var item = new StorageNotice(a.RootId, "id:local:00000001:00000000000000ae", b.RootId,
            "remote-update", StorageNotices.ReceiptScope(receipt));
        async Task ReportAsync(Exception error)
        {
            await using var service = new StorageSettlementService(store, connections);
            service.ReportFailure(pair, "Folder/file.bin", item, error);
        }
        await ReportAsync(new HttpRequestException("offline", null, System.Net.HttpStatusCode.ServiceUnavailable));
        var network = store.OpenNotices(pair.PairId).Single(n => n.Storage is not null);
        Require(network.Path.Length == 0 && network.Storage is
                { Operation: "availability", ObjectId: "", Scope: "network" },
            "a refresh network failure is the same ONE pair notice as settlement");
        Require(store.StorageRootBackedOff(a.RootId), "a refresh network failure durably backs off the root");
        await ReportAsync(new HttpRequestException("denied", null, System.Net.HttpStatusCode.Unauthorized));
        var auth = store.OpenNotices(pair.PairId).Single(n => n.Storage is not null);
        Require(auth.NoticeId == network.NoticeId && auth.Storage!.Scope == "auth",
            "a refresh auth failure updates that pair notice in place");
        await ReportAsync(new SyncRunRefusedException("The Dropbox connection is not connected."));
        var provider = store.OpenNotices(pair.PairId).Single(n => n.Storage is not null);
        Require(provider.NoticeId == network.NoticeId && provider.Storage!.Scope == "provider",
            "a refresh provider failure updates that pair notice in place");
        store.ClearStorageRootBackoff(a.RootId);
        await ReportAsync(new IOException("Edited local item", unchecked((int)0x80070020)));
        Require(store.OpenNotices(pair.PairId).Any(n => n.Storage is { Operation: "remote-update" } stored &&
                stored.ObjectId == item.ObjectId),
            "a refresh item cause remains its own notice");
        Require(!store.StorageRootBackedOff(a.RootId), "a refresh item cause never backs off the root");
    }

    private static async Task CheckSettlementSchedulingAsync(string root)
    {
        var clock = new OfflineClock();
        using var store = SqliteStateStore.Open(root, V2Settings.Default, clock);
        var a = new RootDef("local", SyncProviders.Local, null, root, SyncLocalEndpointStore.ObjectId(1, 1));
        var b = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root");
        var pair = new PairDef("pair", "Pair A", a, b, [], [], true);
        store.SavePair(pair);
        store.EnableStorage();
        var hash = new string('a', 64);
        var item = new ItemState("Folder/file.bin", ItemKind.File, "id:local:00000001:0000000000000002", null,
            hash, 4, DateTimeOffset.UnixEpoch, ItemFlags.None);
        store.WriteSnapshot(new Snapshot(a.RootId, Side.A, store.BeginGeneration(a.RootId, "snapshot"), "cursor", [item], []));
        store.SetStoragePolicy(a.RootId, a.RootObjectId, "online-only");
        var receipt = new PilotRemoteEntry(item.Path, PilotEntryKind.File, "id:remote-file", "rev-1", 4, hash);
        store.QueueStorageSettlement(a.RootId, item.ObjectId, b.RootId, receipt);
        using var connections = new CloudFilesLifetime(
            (_, _, _) => throw new InvalidOperationException("native provider must not open"), _ => false, store);
        var calls = 0;
        var network = new HttpRequestException("offline", null, System.Net.HttpStatusCode.ServiceUnavailable);
        Task<StorageObserved> FailNetwork(StorageSettlement job, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<StorageObserved>(network);
        }
        async Task RunTurnAsync(Func<StorageSettlement, CancellationToken, Task<StorageObserved>> convert)
        {
            await using var service = new StorageSettlementService(store, connections, convert: convert);
            await service.DrainAsync(CancellationToken.None);
        }
        await RunTurnAsync(FailNetwork);
        Require(calls == 1, "one failed conversion, never one per file across the pair");
        var pairNotice = store.OpenNotices(pair.PairId).Single(n => n.Storage is not null);
        Require(pairNotice.Path.Length == 0 && pairNotice.Storage is
                { Operation: "availability", ObjectId: "", Scope: "network" },
            "a network settlement failure is ONE pair notice");
        Require(store.StorageRootBackedOff(a.RootId), "a network settlement failure durably backs off the root");
        Require(store.StorageSettlementFor(a.RootId, item.ObjectId, b.RootId) is not null,
            "a failed settlement retains the item intent");
        Require(store.StorageSettlementPage().Count == 0, "a failed settlement item is not due on the next pass");
        await RunTurnAsync(FailNetwork);
        Require(calls == 1, "no rehash or hold while the root is backed off");
        clock.Advance(TimeSpan.FromMinutes(2));
        await RunTurnAsync(FailNetwork);
        Require(calls == 2 && store.OpenNotices(pair.PairId).Count(n => n.Storage is not null) == 1,
            "an expired item due retries once and updates the same pair notice");

        store.ClearStorageRootBackoff(a.RootId);
        clock.Advance(TimeSpan.FromMinutes(2));
        var itemError = new IOException("Sharing violation", unchecked((int)0x80070020));
        Task<StorageObserved> FailItem(StorageSettlement job, CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<StorageObserved>(itemError);
        }
        await RunTurnAsync(FailItem);
        Require(calls == 3, "the item cause is attempted after its due");
        var itemNotice = store.OpenNotices(pair.PairId).Single(n => n.Storage?.ObjectId == item.ObjectId);
        Require(itemNotice.Storage!.Operation == "settlement" && !PlanNotices.HasPathScope(itemNotice),
            "an item-specific cause remains its own non-holding settlement notice");
        Require(!store.StorageRootBackedOff(a.RootId), "an item-specific cause never backs off the whole root");
        Require(store.StorageSettlementPage().Count == 0, "the item cause schedules only the item");
        await RunTurnAsync(FailItem);
        Require(calls == 3, "item backoff prevents an immediate rehash");
        store.RetryStorageSettlement(a.RootId, item.ObjectId, b.RootId);
        Require(store.StorageSettlementPage().Count == 1, "explicit Retry resets the item due");
        clock.Advance(TimeSpan.FromMinutes(2));
        await RunTurnAsync((_, _) => Task.FromResult(new StorageObserved("OnlineOnly", false, true)));
        Require(calls == 3 && store.StorageSettlementFor(a.RootId, item.ObjectId, b.RootId) is null,
            "an expired item due converts without another failure");
        Require(store.OpenNotices(pair.PairId).All(n => n.Storage?.ObjectId != item.ObjectId),
            "successful settlement closes its own item notice");
        StorageNotices.CompletePair(store, pair, "settlement");
        Require(store.OpenNotices(pair.PairId).Count(n => n.Storage is not null) == 0,
            "pair recovery closes the availability notice");
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
        Require(store.OpenNotices(pair.PairId).Count == 1 && store.HasOpenStoragePairCause(pair.PairId) &&
            store.StorageRootBackedOff(a.RootId) &&
            store.OpenNotices(pair.PairId).All(n => n.Storage?.ObjectId == ""),
            "typed callback source alone produces the pair notice and durable root backoff");
        await Fail(1, new IOException("Windows read failed"));
        Require(store.OpenNotices(pair.PairId).Any(n => n.Storage?.ObjectId == files[1].ObjectId) &&
            store.StorageHydrationFor(a.RootId, files[1].ObjectId) is not null,
            "generic I/O without same-attempt evidence is an item failure even while a pair notice is open");
        using var itemAttempt = new WindowsCloudFilesProvider.OfflineHydrationAttempt(() => { });
        itemAttempt.Failed(new InvalidDataException("Content hash differs"));
        await Fail(2, itemAttempt.Correlate(new IOException("Windows read failed")));
        Require(store.OpenNotices(pair.PairId).Any(n => n.Storage?.ObjectId == files[2].ObjectId),
            "correlated item-specific failure remains visible during the pair outage");
        Require(StorageNotices.ClassifyFailure(new IOException("Sharing", unchecked((int)0x80070020))) is null &&
            StorageNotices.ClassifyFailure(new IOException("Disk full", unchecked((int)0x80070070))) is null &&
            StorageNotices.ClassifyFailure(new InvalidDataException("Policy changed")) is null,
            "specific item HRESULTs and policy faults never become pair causes");
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
