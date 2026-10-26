namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task LegacyGapProvenanceAsync()
    {
        foreach (var rebegin in new[] { false, true })
            foreach (var remoteEdit in new[] { false, true })
                await LegacyGapNonItemEditAsync(rebegin, remoteEdit);
    }

    private static async Task LegacyGapNonItemEditAsync(bool rebegin, bool remoteEdit)
    {
        var folder = FixtureFolder("legacy-gap-provenance");
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.ContentProofStore = store;
            var legacy = fx.A.Seed("Parent/item.txt", "old");
            var other = fx.A.Seed("Parent/other.txt", "old");
            await FirstSyncAsync(fx.Run(), fx);
            var gaps = (ILocalGapStore)store;
            var root = fx.Pair.A.RootId;
            gaps.BeginGap(root, new NtfsUsnCheckpoint(1, 0, 200, 0).Cursor, 0, false);
            gaps.GapDiscoveredBatch(root, gaps.GapEpoch(root)!, [(legacy, true)]);
            LegacyGapFixture.SetDiscovery(folder, complete: rebegin);
            if (rebegin) gaps.BeginGap(root, new NtfsUsnCheckpoint(2, 0, 300, 0).Cursor, 0, false);
            fx.A.Seed(other.Path, "new", versionId: other.VersionId, mtime: other.Mtime);
            if (remoteEdit) fx.B.Seed(other.Path, "far");
            fx.A.HashedPaths.Clear();
            var report = await fx.Run().ReconcileScopeAsync(fx.Pair.PairId,
                StateMonitor.FromGap(gaps.GapEpoch(root)!) with { RootId = root }, new RecordingProgress(), default);
            Require(report.Failed == 0 && fx.A.HashedPaths.Contains(other.Path),
                "fresh or discovery-incomplete legacy epochs prove the metadata-identical non-item edit");
            if (remoteEdit)
                Require(report.KeptBoth == 2 && store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Kind == NoticeKind.KeepBoth),
                    "a concurrent remote change conflicts instead of overwriting the non-item local edit");
            else
                Require((await fx.B.ObserveAsync(other.Path, default))?.Hash ==
                    DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("new")),
                    "the metadata-identical non-item local edit propagates");
        }
        finally { DeleteFixture(folder); }
    }
}
