namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static Task ReconciliationClearsGapDebt() => ReconcileGapDebt(false);
    private static Task ReconciliationClearsLegacyEpoch() => ReconcileGapDebt(true);

    private static async Task ReconcileGapDebt(bool legacy)
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "reconcile-gap-debt-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var items = Enumerable.Range(0, 50).Select(i => fx.A.Seed($"Parent/file-{i:D2}.txt", "original")).ToArray();
        await FirstSyncAsync(fx.Run(), fx);
        var gaps = (ILocalGapStore)store;
        var root = fx.Pair.A.RootId;
        gaps.BeginGap(root, new NtfsUsnCheckpoint(2, 0, 200, 0).Cursor, 100, false, protect: legacy);
        var epoch = gaps.GapEpoch(root)!;
        if (legacy)
        {
            gaps.GapDiscoveredBatch(root, epoch, items.Select(item => (item, true)).ToArray());
            Require(gaps.HasGapProtection(root) && gaps.GapObligations(root, items.Select(item => item.ObjectId).ToArray()).Count > 0,
                "legacy fixture retains both discovery protection and concrete gap debt");
        }
        for (var i = 0; i < 3; i++) fx.A.Seed(items[i].Path, "changed content");
        var writes = fx.WriterB.Calls;
        fx.A.HashedPaths.Clear(); fx.B.HashedPaths.Clear();
        var report = await fx.Run().ReconcileScopeAsync(fx.Pair.PairId,
            StateMonitor.FromGap(epoch) with { RootId = root }, new RecordingProgress(), default);
        Require(!report.CompletionFailed && report.Failed == 0 && fx.WriterB.Calls - writes == 3,
            "one gap reconciliation uploads exactly the three changed files");
        Require(fx.A.HashedPaths.Count == 0 && fx.B.HashedPaths.Count == 0,
            "available verified content hashes require no extra hashing");
        Require(!store.PendingGapInventory(root) && !gaps.HasGapProtection(root) &&
            gaps.GapObligations(root, items.Select(item => item.ObjectId).ToArray()).Count == 0 &&
            store.PlanningBacklogPage(fx.Pair.PairId).Count == 0,
            "reconciliation clears inventory, legacy frontier, concrete gap items and observation planning debt");
        await MirrorAsync(fx);
    }
}
