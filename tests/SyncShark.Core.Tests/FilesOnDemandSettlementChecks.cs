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
        var localRoot = new RootDef("local", SyncProviders.Local, null, root, "id:local:00000001:0000000000000001");
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
        var movedCloud = cloud with { Path = moved.Path, VersionId = "rev-after-move" };
        var move = new SyncAction("move-pointer", 9, ActionKind.Move, Side.B, moved.Path, local.Path,
            moved, cloud, false, "pointer-object");
        store.RecordIntent(pair.PairId, move);
        store.CommitResult(pair.PairId, gen, move, new(move.ActionId, Outcome.Done, moved, movedCloud, null, null));
        // The Move renamed the local entity. Production publishes that indexed location on the next scan
        // before the acknowledgment may run; the service revalidation requires the two paths to agree.
        var movedGeneration = store.BeginGeneration(localRoot.RootId, "snapshot");
        store.WriteSnapshot(new Snapshot(localRoot.RootId, Side.A, movedGeneration, "moved-local", [moved], []));
        var acknowledgment = store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId);
        Require(acknowledgment is { Kind: StorageSettlement.AcknowledgeMove } && acknowledgment.Remote.RelativePath == moved.Path,
            "remote Move durably queues typed acknowledgment at the new path");
        Require(store.StorageSettlementPage().Any(item => item.JobId == acknowledgment!.JobId && item.Kind == StorageSettlement.AcknowledgeMove),
            "bounded settlement page preserves acknowledgment job kind");
        var movedReceipt = acknowledgment!.Remote;
        Require(movedReceipt.Revision == movedCloud.VersionId &&
            store.StoragePointerFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)?.Remote == movedReceipt,
            "Move publishes the new revision and path receipt before the native acknowledgment");
        Require(!store.QueueVerifiedPointerAcknowledgment(localRoot.RootId, local.ObjectId, remoteRoot.RootId, movedReceipt),
            "scan does not replace a pending acknowledgment");
        store.QueueStorageSettlement(localRoot.RootId, local.ObjectId, remoteRoot.RootId, movedReceipt);
        Require(store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)?.JobId == acknowledgment!.JobId,
            "ordinary conversion enqueue preserves typed acknowledgment identity");
        var acknowledgments = 0;
        var ackTarget = metadata with { Scope = StorageNotices.ReceiptScope(movedReceipt) };
        using (var connections = new CloudFilesLifetime((_, _, _) => throw new InvalidOperationException("managed ack opened native provider"), _ => false, store))
        {
            StorageObserved Acknowledge(StorageSettlement pending, Action revalidate)
            {
                Require(pending.Kind == StorageSettlement.AcknowledgeMove && pending.Remote == movedReceipt,
                    "real service passes the complete post-move receipt to the native boundary");
                revalidate();
                acknowledgments++;
                return new("OnlineOnly", false, true);
            }
            await using (var service = new StorageSettlementService(store, connections, target: ackTarget, acknowledge: Acknowledge))
            {
                await service.DrainAsync(CancellationToken.None);
                await service.DrainAsync(CancellationToken.None);
            }
            Require(acknowledgments == 1 && store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId) is null,
                "real service completes and replays acknowledgment idempotently");
            // A parent rename publishes child inventories but returns no per-child Move result.
            moved = moved with { Path = "RenamedFolder/file.txt" };
            var localGeneration = store.BeginGeneration(localRoot.RootId, "snapshot");
            store.WriteSnapshot(new Snapshot(localRoot.RootId, Side.A, localGeneration, "folder-local", [moved], []));
            var childReceipt = movedReceipt with { RelativePath = moved.Path };
            Require(!store.QueueVerifiedPointerAcknowledgment(localRoot.RootId, local.ObjectId, remoteRoot.RootId, childReceipt),
                "child receipt cannot move before the indexed remote location agrees");
            movedCloud = movedCloud with { Path = moved.Path, VersionId = "rev-after-folder-move" };
            var remoteGeneration = store.BeginGeneration(remoteRoot.RootId, "snapshot");
            store.WriteSnapshot(new Snapshot(remoteRoot.RootId, Side.B, remoteGeneration, "folder-remote", [movedCloud], []));
            Require(store.QueueVerifiedPointerAcknowledgment(localRoot.RootId, local.ObjectId, remoteRoot.RootId, childReceipt),
                "verified dirty folder child gets a bounded entity acknowledgment after both sides converge");
            movedReceipt = childReceipt with { Revision = movedCloud.VersionId };
            Require(store.StoragePointerFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)?.Remote == movedReceipt &&
                store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)?.Remote == movedReceipt,
                "folder child receipt and acknowledgment carry the indexed postmove revision atomically");
            var healedJob = store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)!;
            Require(!store.QueueVerifiedPointerAcknowledgment(localRoot.RootId, local.ObjectId, remoteRoot.RootId, movedReceipt) &&
                store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)?.JobId == healedJob.JobId,
                "repeated dirty scan preserves job identity");
            Require(!store.QueueVerifiedPointerAcknowledgment(localRoot.RootId, local.ObjectId, remoteRoot.RootId,
                movedReceipt with { Revision = "uncommitted" }), "scan cannot invent a remote receipt");
            await using (var service = new StorageSettlementService(store, connections, target: ackTarget,
                acknowledge: (_, _) => throw new IOException("Pointer metadata temporarily unreadable")))
                await service.DrainAsync(CancellationToken.None);
            Require(store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)?.JobId == healedJob.JobId &&
                (store.ByObject(localRoot.RootId, local.ObjectId)!.Flags & ItemFlags.NeedsRehash) == 0 &&
                !store.OpenNotices(pair.PairId).Any(n => n.Storage?.Operation == "pointer-verify"),
                "transient native metadata read keeps the same ack retryable without hydration debt");
            await using (var service = new StorageSettlementService(store, connections, target: ackTarget, acknowledge: (_, revalidate) =>
            {
                // Simulate a scanner publishing a subsequent rename between admission and native effect.
                var changed = moved with { Path = "Again/file.txt" };
                var next = store.BeginGeneration(localRoot.RootId, "snapshot");
                store.WriteSnapshot(new Snapshot(localRoot.RootId, Side.A, next, "changed", [changed], []));
                revalidate();
                throw new InvalidOperationException("changed path reached native effect");
            }))
                await service.DrainAsync(CancellationToken.None);
            Require(store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId) is null &&
                (store.ByObject(localRoot.RootId, local.ObjectId)!.Flags & ItemFlags.NeedsRehash) == 0,
                "real service retires a later rename without inventing content verification debt");
            Require(!store.OpenNotices(pair.PairId).Any(n => n.Storage?.Operation is "pointer-verify" or "settlement"),
                "metadata-only rename retirement creates no verification or stale settlement failure");
            movedCloud = movedCloud with { Path = "Again/file.txt" };
            remoteGeneration = store.BeginGeneration(remoteRoot.RootId, "snapshot");
            store.WriteSnapshot(new Snapshot(remoteRoot.RootId, Side.B, remoteGeneration, "again-remote", [movedCloud], []));
            movedReceipt = movedReceipt with { RelativePath = movedCloud.Path };
            Require(store.QueueVerifiedPointerAcknowledgment(localRoot.RootId, local.ObjectId, remoteRoot.RootId, movedReceipt),
                "later committed location can enqueue acknowledgment after stale-job retirement");
            await using (var service = new StorageSettlementService(store, connections, target: ackTarget,
                acknowledge: (_, revalidate) =>
                {
                    revalidate();
                    throw new WindowsCloudFilesProvider.PointerAcknowledgmentChangedException();
                }))
                await service.DrainAsync(CancellationToken.None);
            Require(store.StorageSettlementFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId) is null &&
                (store.ByObject(localRoot.RootId, local.ObjectId)!.Flags & ItemFlags.NeedsRehash) != 0 &&
                store.OpenNotices(pair.PairId).Count(n => n.Storage?.Operation == "pointer-verify") == 1,
                "native byte/identity proof failure retires acknowledgment into one content verification notice");
        }
        Require(store.StoragePointerFor(localRoot.RootId, local.ObjectId, remoteRoot.RootId)?.Remote == movedReceipt,
            "changed-path acknowledgment does not alter the last durable receipt");

        store.RequireRehash(localRoot.RootId, moved, "pointer verification");
        store.RequestPointerVerification(pair.PairId, localRoot.RootId, moved.ObjectId, "Again/file.txt", movedReceipt);
        store.RequestPointerVerification(pair.PairId, localRoot.RootId, moved.ObjectId, "Again/file.txt", movedReceipt);
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
