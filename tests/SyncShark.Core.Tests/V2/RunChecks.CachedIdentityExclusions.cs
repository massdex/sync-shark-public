namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task CachedIdentityExclusionsAsync(string folder)
    {
        await ExactCreationObservationAsync(Path.Combine(folder, "creation-observation"));
        await PlanningObservationHandoffAsync(Path.Combine(folder, "handoff"));
        await ItemEligibilityContentAsync(Path.Combine(folder, "eligibility"));
        await ScanBoundaryRetirementAsync(Path.Combine(folder, "boundaries"));
        await LocalCatchUpNoticeLifecycleAsync(Path.Combine(folder, "notice"));
        foreach (var ignored in new[] { false, true })
        {
            using var store = SqliteStateStore.Open(Path.Combine(folder, ignored ? "ignored" : "excluded"),
                StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            await FirstSyncAsync(fx.Run(), fx);
            const string path = "Parent/Child/item.txt";
            fx.A.Seed(path, "content");
            store.WriteSnapshot(await fx.A.FullAsync(store.BeginGeneration(fx.Pair.A.RootId, "full"),
                _ => true, SilentProgress, default));
            var item = store.Live(fx.Pair.A.RootId, [path]).Single();
            var pair = ignored ? fx.Pair with { IgnoreRules = ["FolderName:Child"] } :
                fx.Pair with { Excluded = ["Parent/Child"] };
            store.SavePair(pair);

            var result = CachedIdentityRefresh.OpenFailure(store, pair.A, item, 87);
            Require(result.Outcome == LocalGapHashOutcome.IdentityChanged && store.CountSyncRetries(pair.PairId) == 0 &&
                store.OpenNotices(pair.PairId).Count == 0,
                "excluded or ignored cached-ID 87 is handled without scan debt or notice");
            // Model rows persisted by the previous version, including a sleeping failure.
            store.SaveSyncRetry(pair.PairId, new(path, "scan", 0, CachedIdentityRefresh.PendingReason,
                DateTimeOffset.MinValue, ReadRoots: [pair.A.RootId]));
            store.SaveSyncRetry(pair.PairId, new("Parent/Child/old.txt", "scan", 2, CachedIdentityRefresh.OpenReason,
                DateTimeOffset.MaxValue, ReadRoots: [pair.A.RootId]));
            var report = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(store.CountSyncRetries(pair.PairId) == 0 && report.Deferred == 0 &&
                report.Outcome == RunOutcomes.Complete && store.OpenNotices(pair.PairId).Count == 0,
                "next run retires excluded or ignored backlog and reports complete, not partial");
            Require(fx.A.WriteCalls == 0 && fx.B.WriteCalls == 0,
                "retiring filtered scan debt never changes endpoint content");

            // A second pair may still need the same root's file; skipping one must not suppress it.
            var included = fx.Pair with { PairId = "included-pair" };
            store.SavePair(included);
            Require(CachedIdentityRefresh.TryQueue(store, pair.A, item, 87) &&
                store.SyncRetryAt(pair.PairId, path) is null &&
                store.SyncRetryAt(included.PairId, path) is { } retry && CachedIdentityRefresh.Pending(retry),
                "shared-root included control still queues independently");
            CachedIdentityRefresh.Observed(store, included.PairId, path, pair.A.RootId);
            Require(store.SyncRetryAt(included.PairId, path) is null,
                "included control still retires after guarded observation");
            store.SavePair(fx.Pair);
            Require(CachedIdentityRefresh.TryQueue(store, pair.A, item, 87) &&
                store.SyncRetryAt(pair.PairId, path) is { } restored && CachedIdentityRefresh.Pending(restored),
                "re-inclusion or removal of ignore rule restores refresh admission");
        }
    }
}
