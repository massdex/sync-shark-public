using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class FilesOnDemandSettlementChecks
{
    internal static async Task RunAsync(string root)
    {
        var remote = new PilotRemoteEntry("Folder/file.txt", PilotEntryKind.File, "id:file", "rev-1", 4, new string('a', 64));
        Require(StorageSettlementService.SameRemote(remote, remote), "exact remote receipt accepted");
        foreach (var changed in new PilotRemoteEntry?[] { null, remote with { Id = "id:other" },
            remote with { Revision = "rev-2" }, remote with { ContentHash = new string('b', 64) },
            remote with { Size = 5 }, remote with { Kind = PilotEntryKind.Directory } })
            Require(!StorageSettlementService.SameRemote(remote, changed), "changed or missing remote retains local bytes");
        using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System);
        var localRoot = new RootDef("local", SyncProviders.Local, null, root, "root-object");
        var remoteRoot = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root");
        var pair = new PairDef("pair", "Pair A", localRoot, remoteRoot, [], [], true);
        store.SavePair(pair);
        var local = new ItemState(remote.RelativePath, ItemKind.File, "file-object", null,
            remote.ContentHash, remote.Size, DateTimeOffset.UnixEpoch, ItemFlags.None);
        var gen = store.BeginGeneration(localRoot.RootId, "snapshot");
        store.WriteSnapshot(new Snapshot(localRoot.RootId, Side.A, gen, "cursor", [local], []));
        store.SetStoragePolicy(localRoot.RootId, localRoot.RootObjectId, "online-only");
        store.QueueStorageSettlement(localRoot.RootId, local.ObjectId, remoteRoot.RootId, remote);
        var job = store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var errors = 0;
        async Task<StorageObserved> Convert(StorageSettlement _, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new("OnlineOnly", false, true);
        }
        var first = new FilesOnDemandSettlement(store, Convert, (_, _) => Interlocked.Increment(ref errors));
        var second = new FilesOnDemandSettlement(store, Convert, (_, _) => Interlocked.Increment(ref errors));
        var firstRun = first.RunItemAsync(job, CancellationToken.None);
        await entered.Task;
        var secondRun = second.RunItemAsync(job, CancellationToken.None);
        release.TrySetResult();
        await Task.WhenAll(firstRun, secondRun);
        Require(calls == 1 && errors == 0, "Retry and loop serialize, recheck receipt, and complete once");
        Require(store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId) is null, "completed job retired");

        store.QueueStorageSettlement(localRoot.RootId, local.ObjectId, remoteRoot.RootId, remote);
        using (var connections = new CloudFilesLifetime((_, _, _) => throw new InvalidOperationException("paused settlement opened provider"), _ => false, store))
        {
            store.SavePair(pair with { Enabled = false });
            await using (var service = new StorageSettlementService(store, connections))
                await service.DrainAsync(CancellationToken.None);
            Require(store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId) is not null, "disabled pair retains intent without native work");
            store.SavePair(pair);
            store.SetPairPaused(pair.PairId, true);
            await using (var service = new StorageSettlementService(store, connections))
                await service.DrainAsync(CancellationToken.None);
            Require(store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId) is not null, "paused pair retains intent without native work");
            store.SetPairPaused(pair.PairId, false);
        }
        var stale = store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)!;
        var existingRetry = new SyncRetry(local.Path, "decision", 3, "prior decision", DateTimeOffset.MaxValue,
            ReadRoots: ["other-root"]);
        store.SaveSyncRetry(pair.PairId, existingRetry);
        Require(store.RetireStorageForResync(stale, pair, local.Path, "remote changed"), "stale receipt retired for targeted resync");
        var retry = store.SyncRetryAt(pair.PairId, local.Path)!;
        Require(retry.Kind == existingRetry.Kind && retry.Attempts == existingRetry.Attempts && retry.RetryAfter == existingRetry.RetryAfter &&
            retry.ReadRoots!.Contains("other-root") && retry.ReadRoots!.Contains(localRoot.RootId) && retry.ReadRoots!.Contains(remoteRoot.RootId),
            "targeted resync merges roots without losing prior review state");

        var metadata = new StorageNotice(localRoot.RootId, local.ObjectId, remoteRoot.RootId,
            "settlement", StorageNotices.ReceiptScope(remote));
        StorageNotices.Report(store, pair, local.Path, metadata, new IOException("cause one"));
        var notice = store.OpenNotices(pair.PairId).Single(n => n.Storage is not null);
        Require(!PlanNotices.HasPathScope(notice), "storage failures never hold ordinary sync");
        store.CloseNotice(notice.NoticeId!, NoticeChoices.Ignore);
        StorageNotices.Report(store, pair, local.Path, metadata, new IOException("cause one"));
        Require(!store.OpenNotices(pair.PairId).Any(n => n.Storage is not null), "Ignore suppresses exact cause");
        StorageNotices.Report(store, pair, local.Path, metadata, new IOException("cause two"));
        Require(store.OpenNotices(pair.PairId).Count(n => n.Storage is not null) == 1, "different cause remains visible");
        StorageNotices.Complete(store, pair.PairId, metadata);
        StorageNotices.Report(store, pair, local.Path, metadata with { Scope = StorageNotices.ReceiptScope(remote with { Revision = "rev-2" }) }, new IOException("cause one"));
        Require(store.OpenNotices(pair.PairId).Count(n => n.Storage is not null) == 1, "new receipt not suppressed by older Ignore");
        StorageNotices.Complete(store, pair.PairId, metadata);
        StorageNotices.Report(store, pair, local.Path, metadata, new StoragePairException("volume", "Requires fixed NTFS"));
        StorageNotices.Report(store, pair, "other.txt", metadata with { ObjectId = "other" }, new StoragePairException("volume", "Requires fixed NTFS"));
        var pairNotice = store.OpenNotices(pair.PairId).Single(n => n.Storage is not null);
        Require(pairNotice.Path == "" && pairNotice.Storage!.ObjectId == "" && !PlanNotices.HasPathScope(pairNotice),
            "pair cause dedupes across items without root hold");
        StorageNotices.CompletePair(store, pair, "settlement");
        Require(!store.OpenNotices(pair.PairId).Any(n => n.Storage is not null), "pair recovery closes obsolete cause");
        // A remote Move commits its durable acknowledgment alongside the ordinary result.
        var moved = local with { Path = "Moved/file.txt", Flags = ItemFlags.OwnedPointer };
        var cloud = local with { ObjectId = remote.Id!, VersionId = remote.Revision, Flags = ItemFlags.None };
        var movedCloud = cloud with { Path = moved.Path };
        var move = new SyncAction("move-pointer", 9, ActionKind.Move, Side.B, moved.Path, local.Path,
            moved, cloud, false, "pointer-object");
        store.RecordIntent(pair.PairId, move);
        store.CommitResult(pair.PairId, gen, move, new(move.ActionId, Outcome.Done, moved, movedCloud, null, null));
        var acknowledgment = store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId);
        Require(acknowledgment is { Kind: StorageSettlement.AcknowledgeMove } && acknowledgment.Remote.RelativePath == moved.Path,
            "remote Move durably queues typed acknowledgment at the new path");
        Require(store.StorageSettlementPage().Any(item => item.JobId == acknowledgment!.JobId && item.Kind == StorageSettlement.AcknowledgeMove),
            "bounded settlement page preserves acknowledgment job kind");
        var acknowledgments = 0;
        var ackWorker = new FilesOnDemandSettlement(store, (_, _) =>
        {
            acknowledgments++;
            return Task.FromResult(new StorageObserved("OnlineOnly", false, true));
        }, (_, error) => throw error);
        await ackWorker.RunItemAsync(acknowledgment!, CancellationToken.None);
        await ackWorker.RunItemAsync(acknowledgment!, CancellationToken.None);
        Require(acknowledgments == 1, "completed acknowledgment replay is idempotent");
        Require(store.StoragePointerFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)?.Remote.RelativePath == moved.Path,
            "acknowledgment completion publishes relocated receipt");

        store.RequireRehash(localRoot.RootId, moved, "pointer verification");
        store.RequestPointerVerification(pair.PairId, localRoot.RootId, moved.ObjectId, moved.Path, remote);
        store.RequestPointerVerification(pair.PairId, localRoot.RootId, moved.ObjectId, moved.Path, remote);
        var verification = store.OpenNotices(pair.PairId).Single(n => n.Storage?.Operation == "pointer-verify");
        store.SetPairPaused(pair.PairId, true);
        await StorageNotices.ApplyAsync(store, null, verification, NoticeChoices.RetryNow, CancellationToken.None);
        var hydration = store.StorageHydrationFor(localRoot.RootId, moved.ObjectId);
        Require(hydration is not null && store.IsStorageHydrationRequested(hydration),
            "verification Retry persists existing hydration sentinel while paused");
        Require((store.ByObject(localRoot.RootId, moved.ObjectId)!.Flags & ItemFlags.NeedsRehash) != 0,
            "queued hydration never clears content verification debt");
        store.SetPairPaused(pair.PairId, false);
        Console.WriteLine("PASS settlement receipt, move acknowledgment replay, verification hydration Retry and notice dedupe");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
