using Massdex.SyncShark;
using Massdex.SyncShark.V2;

/// <summary>
/// Real partial-residency integration proof for one owned Cloud Files pointer, owned by the settlement
/// side and called from the native partial window. The caller guarantees all of the following:
/// <list type="bullet">
/// <item>the pointer is natively partially hydrated: 0 &lt; resident bytes &lt; logical size, availability
/// PartiallyHydrated, and its identity still matches the supplied receipt;</item>
/// <item>no exclusive native file handle is held across this call;</item>
/// <item>the fixture range source is unrestricted, and any increase of <paramref name="downloads"/> fails
/// the caller's outer assertion.</item>
/// </list>
/// The check uses the real production inventory scan (<see cref="LocalInventory.FullAsync"/>), the real
/// pair/registration/receipt index and the real planner. It proves that a partial prefix is never hashed,
/// uploaded, marked synced, inferred absent, or published as ordinary local content; instead the entity
/// gains one durable content-verification retry and exactly one item-scoped, deduplicated notice across
/// repeated real scans. It never registers, connects, unregisters or deletes the fixture root, and it opens
/// the file only for metadata (non-exclusive) reads.
/// </summary>
internal static class FilesOnDemandNativePartialChecks
{
    /// <summary>
    /// Runs two real metadata-only inventory scans while a genuine native partial prefix exists and asserts
    /// the Sync Shark obligations around it. Call after the caller's metadata scan, before any ordinary read
    /// restores full residency. The helper begins and completes its own local generations, so the caller must
    /// not still be writing a local generation at that moment.
    /// </summary>
    internal static async Task VerifyAsync(string root, PairDef pair, SqliteStateStore store,
        ItemState pointer, PilotRemoteEntry remote, Func<int> downloads, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(downloads);
        var fullRoot = Path.GetFullPath(root);
        Require(Directory.Exists(fullRoot), "The partial native check needs the existing disposable fixture root.");
        Require(pair.A.Provider == SyncProviders.Local && pair.B.Provider == SyncProviders.Dropbox &&
                string.Equals(Path.GetFullPath(pair.A.RootPath), fullRoot, StringComparison.OrdinalIgnoreCase),
            "The supplied pair does not bind this fixture root as its local side.");
        Require(remote.Kind == PilotEntryKind.File && !string.IsNullOrWhiteSpace(remote.Id) &&
                !string.IsNullOrWhiteSpace(remote.Revision) && !string.IsNullOrWhiteSpace(remote.ContentHash),
            "The supplied receipt is not a complete file receipt.");
        Require(pointer.Kind == ItemKind.File && pointer.Path == remote.RelativePath &&
                pointer.Flags.HasFlag(ItemFlags.OwnedPointer) && pointer.Size == remote.Size &&
                pointer.Hash == remote.ContentHash,
            "The supplied pointer is not the pre-hydration, receipt-identical owned pointer.");
        Require(store.IsStorageEnabled, "Files On-Demand storage must already be enabled for the fixture store.");
        var savedPair = store.Pair(pair.PairId);
        Require(savedPair is not null && savedPair.A.RootId == pair.A.RootId && savedPair.B.RootId == pair.B.RootId,
            "The fixture pair is not saved in the store, so the owned-pointer classifier cannot run.");

        // The owned-pointer classifier reads the durable registration token, the entity row and the receipt.
        // Seed exactly those through the production APIs; do not touch the native object.
        var registration = WindowsCloudFilesProvider.ReadRootRegistration(fullRoot);
        var token = registration?.OwnershipToken;
        Require(token is { Length: > 0 },
            "The fixture root has no v2 ownership token, so owned-pointer proof cannot be established.");
        store.RecordStorageRegistration(pair.A.RootId, fullRoot, token!);
        var readsBefore = downloads();

        var cursor = store.Cursor(pair.A.RootId) ?? "";
        var seed = store.BeginGeneration(pair.A.RootId, "native-partial");
        store.WriteDelta(new Delta(pair.A.RootId, Side.A, seed, cursor, cursor,
            [new Change(ChangeKind.Upsert, pointer.ObjectId, pointer.Path, pointer, null)], false, []));
        store.CompleteGeneration(seed, cursor, true);
        store.QueueStorageSettlement(pair.A.RootId, pointer.ObjectId, pair.B.RootId, remote);
        var receiptJob = store.StorageSettlementFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId);
        Require(receiptJob is not null && store.CompleteStorageSettlement(receiptJob, new("OnlineOnly", false, true)),
            "The fixture could not publish the pointer's exact durable receipt.");

        var fullPath = Path.Combine(fullRoot, pointer.Path);
        Require(File.Exists(fullPath), "The partial native pointer disappeared before the check.");
        Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(fullPath, out var before) && before is not null,
            "The partial native pointer identity is unreadable.");
        Require(before!.State.Availability == SyncCloudFileAvailability.PartiallyHydrated &&
                before.State.OnDiskBytes > 0 && before.State.OnDiskBytes < remote.Size &&
                before.State.LogicalSize == remote.Size,
            "This check needs genuine partial native residency (0 < resident < size). Native resident=" +
            before.State.OnDiskBytes + " of " + remote.Size + ", availability=" + before.State.Availability + ".");
        Require(before.Identity.DropboxId == remote.Id && before.Identity.DropboxRevision == remote.Revision &&
                before.Identity.ContentHash == remote.ContentHash && before.Identity.LogicalSize == remote.Size &&
                before.Identity.RelativePath == pointer.Path,
            "The partial native identity does not match the supplied pointer receipt.");

        // First real production scan: must record the owned entity as unreadable verification debt without
        // reading the resident prefix, and must own exactly one notice and one durable retry.
        var first = await ScanAsync(pair, store, ct).ConfigureAwait(false);
        Require(downloads() == readsBefore,
            "A metadata-only scan of a partially hydrated pointer fetched bytes or hashed its prefix.");
        var firstItem = ScannedPointer(first, pointer, remote);
        store.WriteSnapshot(first);
        store.CompleteGeneration(first.Gen, first.CursorAfter, true);
        var firstNotices = VerificationNotices(store, pair, pointer);
        Require(firstNotices.Length == 1,
            "A partial owned pointer must produce exactly one item-scoped verification notice, not " +
            firstNotices.Length + ".");
        Require(RehashObligations(store, pair, pointer) == 1,
            "A partial owned pointer must own exactly one durable content-verification retry.");

        // A second real scan must update the same notice and retry in place, never duplicate them.
        var second = await ScanAsync(pair, store, ct).ConfigureAwait(false);
        Require(downloads() == readsBefore,
            "The second metadata-only scan of the partial pointer fetched bytes.");
        var secondItem = ScannedPointer(second, pointer, remote);
        store.WriteSnapshot(second);
        store.CompleteGeneration(second.Gen, second.CursorAfter, true);
        var secondNotices = VerificationNotices(store, pair, pointer);
        Require(secondNotices.Length == 1 && secondNotices[0].NoticeId == firstNotices[0].NoticeId,
            "Repeated scans must update one verification notice in place, not create duplicates.");
        Require(RehashObligations(store, pair, pointer) == 1,
            "Repeated scans must keep exactly one content-verification retry, not duplicate the obligation.");
        Require(secondItem.Flags == firstItem.Flags && secondItem.Hash is null,
            "A repeated scan changed the partial pointer's verification debt or published a prefix hash.");

        // The index must retain the entity and its exact receipt: no mark-synced, no conversion, no absence.
        var stored = store.ByObject(pair.A.RootId, pointer.ObjectId);
        Require(stored is not null && stored.ObjectId == pointer.ObjectId && stored.Path == pointer.Path,
            "A partially hydrated owned pointer was omitted from the index; absence must never be inferred.");
        Require(stored!.Kind == ItemKind.File && stored.Size == remote.Size &&
                stored.Flags.HasFlag(ItemFlags.OwnedPointer) && stored.Flags.HasFlag(ItemFlags.NeedsRehash) &&
                stored.Flags.HasFlag(ItemFlags.Unreadable),
            "A partial owned pointer must stay a file with owned, unreadable, verification-debt flags.");
        Require(stored.Hash is null,
            "A partial pointer published a content hash derived from its resident prefix.");
        Require(store.StoragePointerFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId)?.Remote == remote,
            "A metadata-only scan replaced the exact receipt of a partially hydrated pointer.");
        Require(store.StorageSettlementFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId) is null,
            "A metadata-only scan queued conversion or marked a partially hydrated pointer synced.");

        // No upload source and no inferred absence in the real planner for the scanned partial item.
        Require(store.UnresolvedIntents(pair.PairId).All(intent => intent.Source?.ObjectId != pointer.ObjectId),
            "A partial owned pointer became a durable upload source.");
        var plan = Planner.Decide(pair.PairId, 1,
            [new PlanRow(null, secondItem, null, new Evidence(false, false, true, true))],
            new PlanOptions(TimeSpan.Zero, true,
                new LabelRule("{name}", "yyyyMMdd", TimeSpan.Zero, "A", "B"), 10));
        Require(plan.Actions.All(action => action.Kind != ActionKind.Delete &&
                                           action.Source?.ObjectId != pointer.ObjectId),
            "The planner inferred absence or an upload from a partially hydrated owned pointer.");

        // Native state and download count are untouched: nothing was hashed, hydrated or acknowledged.
        Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(fullPath, out var after) && after is not null,
            "The partial native pointer identity became unreadable after the metadata scans.");
        Require(after!.State.Availability == before.State.Availability &&
                after.State.OnDiskBytes == before.State.OnDiskBytes &&
                after.State.IsInSync == before.State.IsInSync &&
                after.State.LogicalSize == before.State.LogicalSize &&
                after.Identity.DropboxId == before.Identity.DropboxId &&
                after.Identity.DropboxRevision == before.Identity.DropboxRevision &&
                after.Identity.ContentHash == before.Identity.ContentHash &&
                after.Identity.LogicalSize == before.Identity.LogicalSize &&
                after.Identity.RelativePath == before.Identity.RelativePath,
            "Metadata-only scans changed the partial native residency, in-sync state or identity. Before: " +
            Describe(before) + " After: " + Describe(after));
        Require(downloads() == readsBefore,
            "The partial native check downloaded or re-verified bytes; no hash, hydration or conversion is allowed.");
    }

    private static async Task<Snapshot> ScanAsync(PairDef pair, SqliteStateStore store, CancellationToken ct)
    {
        // The production metadata scan with the real owned-pointer classifier and store index. The caller
        // already guarantees the native partial state; this only observes it.
        var inventory = new LocalInventory(pair.A, Side.A, store,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), V2Settings.Default, TimeProvider.System,
            pairId: pair.PairId);
        var generation = store.BeginGeneration(pair.A.RootId, "native-partial-scan");
        return await inventory.FullAsync(generation, _ => true, new Progress<string>(), ct).ConfigureAwait(false);
    }

    private static ItemState ScannedPointer(Snapshot snapshot, ItemState pointer, PilotRemoteEntry remote)
    {
        var matches = snapshot.Items.Where(item => item.ObjectId == pointer.ObjectId).ToArray();
        Require(matches.Length == 1,
            "The production scan did not report exactly one partially hydrated owned pointer; absence must never be inferred.");
        var item = matches[0];
        Require(item.Path == pointer.Path && item.Kind == ItemKind.File && item.Size == remote.Size &&
                item.Flags.HasFlag(ItemFlags.OwnedPointer) && item.Flags.HasFlag(ItemFlags.NeedsRehash) &&
                item.Flags.HasFlag(ItemFlags.Unreadable) && item.Hash is null,
            "The production scan did not preserve the partial pointer as owned, unreadable, hash-free debt: " + item);
        return item;
    }

    private static Notice[] VerificationNotices(SqliteStateStore store, PairDef pair, ItemState pointer) =>
        store.OpenNotices(pair.PairId)
            .Where(notice => notice.Storage is { Operation: StorageNotices.PointerVerificationOperation } storage &&
                             storage.ObjectId == pointer.ObjectId)
            .ToArray();

    private static int RehashObligations(SqliteStateStore store, PairDef pair, ItemState pointer) =>
        store.RehashReasons(pair.A.RootId, 50)
            .Count(reason => reason.StartsWith(pointer.Path + ":", StringComparison.Ordinal));

    private static string Describe(WindowsCloudPlaceholderInfo? state) => state is null
        ? "none"
        : "availability=" + state.State.Availability + " resident=" + state.State.OnDiskBytes +
          " inSync=" + state.State.IsInSync + " id=" + state.Identity.DropboxId +
          " revision=" + state.Identity.DropboxRevision;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }
}
