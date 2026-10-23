namespace Massdex.SyncShark.V2;

internal static class SettlementInventoryChecks
{
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var replace in new[] { false, true })
            await FlowAsync(Path.Combine(root, replace ? "replace" : "default"), replace);
    }

    private static async Task FlowAsync(string root, bool replace)
    {
        var localPath = Path.Combine(root, "local");
        Directory.CreateDirectory(localPath);
        var file = Path.Combine(localPath, "file.txt");
        File.WriteAllText(file, "data");
        var attributes = File.GetAttributes(file);
        var settings = V2Settings.Default with { ReplaceOnlineOnlyStubs = replace };
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("pair", "Pair A", RootOf(localPath),
            new("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root"), [], [], true);
        store.SavePair(pair);
        LocalInventory Inventory() => new(pair.A, Side.A, store,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), settings, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage, pairId: pair.PairId)
            { WalkCheckpointForTest = () => (new NtfsUsnCheckpoint(1, 0, 100, 0), false) };
        var initial = await Inventory().FullAsync(1, _ => true, null!, CancellationToken.None);
        var local = initial.Items.Single() with { Hash = DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("data")) };
        var remote = local with { ObjectId = "id:file", VersionId = "revision-1" };
        var localGen = store.BeginGeneration(pair.A.RootId, "snapshot");
        store.WriteSnapshot(initial with { Gen = localGen, Items = [local] });
        var remoteGen = store.BeginGeneration(pair.B.RootId, "snapshot");
        store.WriteSnapshot(new(pair.B.RootId, Side.B, remoteGen, "remote", [remote], []));
        var seed = new SyncAction("seed", 1, ActionKind.CopyNew, Side.B, local.Path, null, local, null, false, "object");
        store.RecordIntent(pair.PairId, seed);
        store.CommitResult(pair.PairId, localGen, seed, new(seed.ActionId, Outcome.Done, local, remote, null, null));
        Require(!store.HasOwnedPointers(pair.A.RootId), "existing synced file starts ordinary");
        store.SetStoragePolicy(pair.A.RootId, pair.A.RootObjectId, "online-only");
        Require(store.EnqueueStoragePolicyPage() > 0, "online-only sweeps the existing synced file");
        var job = store.StorageSettlementPage().Single();
        var stale = job with { Remote = job.Remote with { Revision = "obsolete" } };
        Require(!store.CompleteStorageSettlement(stale, new("OnlineOnly", false, true)) &&
            !store.HasOwnedPointers(pair.A.RootId) && store.StoragePointerFor(pair.A.RootId, local.ObjectId, pair.B.RootId) is null,
            "stale settlement publishes neither ownership nor receipt");
        var conversions = 0;
        var worker = new FilesOnDemandSettlement(store, (_, _) =>
        {
            conversions++;
            // Synthetic native conversion boundary: unsafe attributes plus the exact owned identity below.
            // No Cloud Files registration, connection, dehydration or content download occurs in this test.
            File.SetAttributes(file, attributes | FileAttributes.Offline);
            return Task.FromResult(new StorageObserved("OnlineOnly", false, true));
        }, (_, error) => throw new InvalidOperationException("Settlement failed.", error));
        try
        {
            await worker.RunPageAsync(0, CancellationToken.None);
            Require(conversions == 1 && store.StorageSettlementPage().Count == 0 &&
                store.ByObject(pair.A.RootId, local.ObjectId)?.Flags == ItemFlags.OwnedPointer &&
                store.StoragePointerFor(pair.A.RootId, local.ObjectId, pair.B.RootId) is not null,
                "conversion atomically publishes the entity ownership mark, receipt and queue acknowledgement");
            using (var reopened = SqliteStateStore.Open(Path.Combine(root, "state"), settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
                Require(reopened.HasOwnedPointers(pair.A.RootId) && reopened.StorageSettlementPage().Count == 0,
                    "ownership and acknowledgement survive reopening without registration");
            // Model the durable/native registration handshake without registering a real root.
            const string registrationId = "11111111-1111-4111-8111-111111111111";
            store.RecordStorageRegistration(pair.A.RootId, localPath, registrationId);
            var probes = 0;
            var inventory = Inventory();
            inventory.ReadRootRegistrationForTest = _ => new CloudFilesRootRegistration(
                LocalItems.ParseObjectId(pair.A.RootObjectId).FileId,
                WindowsCloudFileIdentity.Root(pair.PairId, pair.B.Provider) with { V2OwnershipToken = registrationId });
            inventory.OwnedPlaceholderProbeForTest = _ => probes++;
            inventory.ReadOwnedPlaceholderForTest = _ => new WindowsCloudPlaceholderInfo(
                WindowsCloudFileIdentity.FromRemote(pair.PairId, job.Remote, pair.B.Provider),
                new(local.Path, SyncCloudFileAvailability.OnlineOnly, false, true, local.Size, 0,
                    remote.ObjectId, remote.VersionId, remote.Hash),
                LocalItems.ParseObjectId(local.ObjectId).FileId, LocalItems.ParseObjectId(pair.A.RootObjectId).FileId,
                false, 0x31);
            var scanned = await inventory.FullAsync(2, _ => true, null!, CancellationToken.None);
            var pointer = scanned.Items.Single();
            Require(probes == 1 && pointer.Flags == ItemFlags.OwnedPointer && pointer.Hash == local.Hash &&
                scanned.IncompleteSubtrees.Count == 0, "next scan recognises the converted entity without reading bytes");
            var reviewRoot = new ItemState("", ItemKind.Folder, pair.A.RootObjectId, null, null, 0,
                DateTimeOffset.UnixEpoch, ItemFlags.None);
            var removal = new SyncAction("pointer-review", 40, ActionKind.Delete, Side.A, "", null,
                null, reviewRoot, false, null) { RemovalReviewChoice = true };
            var proof = await DeleteSourceGuard.CaptureReviewedRemovalAsync(removal, pair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage);
            Require(proof is not null && proof == await DeleteSourceGuard.CaptureReviewedRemovalAsync(
                removal, pair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage),
                "real local subtree reader proves owned pointers from native identity and version metadata without hydration");
            Require(!await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(
                removal with { FrozenRemovalSignature = proof }, pair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage),
                "whole-root observation never authorises removal of the pair root");
            store.WriteSnapshot(scanned with { Gen = store.BeginGeneration(pair.A.RootId, "snapshot") });
            // An unavailable NTFS journal can independently protect this disposable root.
            // Assert the foreign-stub classification, not the absence of unrelated gap protection.
            Require(!store.LiveFlagged(pair.A.RootId, ItemFlags.Placeholder).Any(item => item.Path == local.Path) &&
                !LinkExclusions.Of(store, pair, !replace).Summaries(pair.PairId)
                    .Any(notice => notice.Rule == LinkExclusions.StubRule),
                "converted pointer is not excluded as a foreign stub");
            var edited = remote with { Hash = new string('b', 64), VersionId = "revision-2", Size = 8,
                Mtime = remote.Mtime.AddHours(1) };
            store.WriteDelta(new(pair.B.RootId, Side.B, remoteGen, "remote", "edited",
                [new(ChangeKind.Upsert, edited.ObjectId, edited.Path, edited, null)], false, []));
            var baseline = store.BaselinesAt(pair.PairId, Side.A, [local.Path]).Single();
            var row = new PlanRow(baseline, pointer, edited, new Evidence(false, false, false, false));
            var options = new PlanOptions(TimeSpan.FromSeconds(2), false,
                new("{name} ({side}){ext}", "yyyyMMdd", TimeSpan.Zero, "A", "B"), 10)
                { ProviderA = pair.A.Provider, ProviderB = pair.B.Provider };
            var plan = Planner.Decide(pair.PairId, 2, [row], options);
            var action = plan.Actions.Single();
            Require(OwnedPointerUpdates.IsMetadataUpdate(action) && Planner.HashRequests([row]).PathsA.Count == 0,
                "remote edit plans a metadata update, never a byte download or hash");
            var target = new MetadataWriter(pointer);
            var source = new MetadataWriter(edited);
            using var gate = new SemaphoreSlim(1, 1);
            var runner = new ActionRunner(new StoreCommitter(store, gate, pair.PairId, 2), settings,
                new SyncTransferResourceController(new SyncTransferSettings(ResourceMode: "Performance"),
                    TimeProvider.System, () => new SyncResourcePressure(0, 0.1, 8L << 30)), pair.PairId, target, source, [], new(), isStateDamage: StorageFailureClassifier.IsStateDamage);
            var result = await runner.RunAsync(action, CancellationToken.None);
            Require(result?.Outcome == Outcome.Done && target.Updates == 1 && target.ByteCalls == 0 && source.ByteCalls == 0,
                "remote update executes through metadata only with either ReplaceOnlineOnlyStubs setting");
            store.SetStoragePolicy(pair.A.RootId, pair.A.RootObjectId, "online-only");
            store.EnqueueStoragePolicyPage();
            await worker.RunPageAsync(0, CancellationToken.None);
            Require(conversions == 1 && store.StorageSettlementPage().Count == 0,
                "updated pointer is never queued into a byte-download/conversion loop");
        }
        finally { File.SetAttributes(file, attributes); }
    }

    private sealed class MetadataWriter(ItemState current) : IEndpointWriter, IOwnedPointerUpdateTarget
    {
        internal int Updates, ByteCalls;
        public Task<ItemState> UpdateOwnedPointerAsync(SyncAction action, IEndpointWriter source, CancellationToken ct)
        {
            Updates++;
            var remote = action.Source!;
            return Task.FromResult(current = current with { Hash = remote.Hash, Size = remote.Size, Mtime = remote.Mtime });
        }
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => Task.FromResult<ItemState?>(current);
        public Task<Stream> OpenReadAsync(ItemState item, CancellationToken ct)
        { ByteCalls++; throw new InvalidOperationException("No byte read permitted."); }
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct)
        { ByteCalls++; throw new InvalidOperationException("No byte replacement permitted."); }
        public Task<RecoveryRef> PreserveAsync(ItemState item, string actionId, CancellationToken ct)
        { ByteCalls++; throw new InvalidOperationException("No byte archive permitted."); }
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }

    private static RootDef RootOf(string path)
    {
        using var lease = PilotDirectoryLease.Open(path);
        return new("local", SyncProviders.Local, null, path,
            SyncLocalEndpointStore.ObjectId(lease.Identity.VolumeSerialNumber, lease.Identity.FileId));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
