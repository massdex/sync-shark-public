namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static void LegacyItemInvalidationChecks(string product, RootDef root, ItemState[] rows)
    {
        using var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.WriteSnapshot(new(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), "", rows, []));
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, new NtfsUsnCheckpoint(1, 0, 200, 0).Cursor, 0, false);
        var epoch = gaps.GapEpoch(root.RootId)!;
        gaps.GapDiscoveredBatch(root.RootId, epoch, [(rows[0], true)]);
        LegacyGapFixture.SetDiscovery(product, complete: true);
        store.InvalidateReconciliationPage(root.RootId, epoch.Epoch, _ => true);
        LocalChecks.Require(store.RehashPendingCount(root.RootId) == 1 &&
            store.ByObject(root.RootId, rows[0].ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash) &&
            store.ByObject(root.RootId, rows[1].ObjectId)!.Hash == rows[1].Hash &&
            !store.ByObject(root.RootId, rows[1].ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash),
            "completed legacy discovery invalidates only entities with open gap items");
        gaps.BeginGap(root.RootId, new NtfsUsnCheckpoint(2, 0, 300, 0).Cursor, 0, false);
        store.InvalidateReconciliationPage(root.RootId, gaps.GapEpoch(root.RootId)!.Epoch, _ => true);
        LocalChecks.Require(store.ByObject(root.RootId, rows[1].ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash),
            "a newly begun untrusted epoch invalidates non-item files despite carried legacy items");
    }

    private static void InvalidationRestartChecks(string folder)
    {
        var product = Path.Combine(folder, "invalidation-restart");
        var root = new RootDef("invalidation-root", SyncProviders.Local, null, folder, "id:root");
        var rows = Enumerable.Range(0, V2Settings.PlanningObservationBatchRows + 1).Select(index =>
            new ItemState("file-" + index, ItemKind.File, "object-" + index.ToString("D6"), null,
                new string('a', 64), 3, DateTimeOffset.UnixEpoch, ItemFlags.None)).ToArray();
        LegacyItemInvalidationChecks(Path.Combine(folder, "legacy-invalidation"), root, rows.Take(2).ToArray());
        string epoch;
        long firstWatermark;
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.WriteSnapshot(new(root.RootId, Side.A, store.BeginGeneration(root.RootId, "full"), "", rows, []));
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(root.RootId, new NtfsUsnCheckpoint(1, 0, 200, 0).Cursor, 0, false, protect: false);
            epoch = gaps.GapEpoch(root.RootId)!.Epoch;
            LocalChecks.Require(store.ReconciliationRetainedFileCount(root.RootId) == rows.Length,
                "set-based progress denominator counts retained file identities");
            var examined = 0;
            var invalidated = 0;
            LocalChecks.Require(store.InvalidateReconciliationPage(root.RootId, epoch, _ => true,
                (read, changed) => { examined = read; invalidated = changed; }),
                "first bounded invalidation page leaves a durable frontier");
            LocalChecks.Require(examined == V2Settings.PlanningObservationBatchRows && invalidated == examined,
                "committed page progress reports exact examined and newly invalidated counts");
            firstWatermark = store.RehashWatermark(root.RootId);
            LocalChecks.Require(store.RehashPendingCount(root.RootId) == V2Settings.PlanningObservationBatchRows,
                "invalidation creates one page of actual rehash debt");
        }
        using var reopened = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        LocalChecks.Require(!reopened.InvalidateReconciliationPage(root.RootId, epoch, _ => true) &&
            reopened.RehashPendingCount(root.RootId) == rows.Length,
            "restart resumes after the committed frontier and adds only the remaining debt");
        LocalChecks.Require(reopened.ReadRehashAcknowledgementPage(root.RootId, reopened.RehashWatermark(root.RootId), "", null).Length == 0,
            "ordinary acknowledgement SQL does not page the unproven invalidation queue");
        var proven = reopened.ByObject(root.RootId, rows[0].ObjectId)!;
        LocalChecks.Require(reopened.CompleteRehash(root.RootId, proven, rows[0].Hash!), "fixture publishes real rehash completion");
        var afterProof = reopened.RehashWatermark(root.RootId);
        LocalChecks.Require(reopened.ReadRehashAcknowledgementPage(root.RootId, afterProof, "", null)
            .SequenceEqual(new[] { rows[0].ObjectId }),
            "ordinary acknowledgement SQL returns only the proven unflagged candidate");
        LocalChecks.Require(!reopened.InvalidateReconciliationPage(root.RootId, epoch, _ => true) &&
            reopened.RehashWatermark(root.RootId) == afterProof && afterProof > firstWatermark &&
            reopened.ByObject(root.RootId, rows[0].ObjectId) is { Hash: not null } stable &&
            !stable.Flags.HasFlag(ItemFlags.NeedsRehash),
            "completed invalidation never destroys content proof or rotates debt on restart");
    }
}
