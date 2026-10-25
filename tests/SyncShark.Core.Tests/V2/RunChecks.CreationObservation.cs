namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ExactCreationObservationAsync(string folder)
    {
        const string path = "Documents/Repeat/Repeat";
        foreach (var sourceSide in new[] { Side.A, Side.B })
        foreach (var failure in new[] { "none", "exact", "ancestor", "read" })
        {
            using var store = SqliteStateStore.Open(Path.Combine(folder, sourceSide + "-" + failure), StorageChecks.Fast,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Documents", "", ItemKind.Folder);
            fx.A.Seed("Documents/Repeat", "", ItemKind.Folder);
            fx.A.Seed("keep.txt", "keep");
            await FirstSyncAsync(fx.Run(), fx);
            var baselineKeys = store.Baselines(fx.Pair.PairId).Select(basis => basis.ObjectKey).ToHashSet();
            var source = sourceSide == Side.A ? fx.A : fx.B;
            var target = sourceSide == Side.A ? fx.B : fx.A;
            var targetRoot = sourceSide == Side.A ? fx.Pair.B : fx.Pair.A;
            var targetWriter = sourceSide == Side.A ? fx.WriterB : fx.WriterA;
            source.Seed(path, "", ItemKind.Folder);
            var failedScope = failure == "exact" ? path : failure == "ancestor" ? "Documents/Repeat" : "Unrelated";
            store.SaveSyncRetry(fx.Pair.PairId, new(failedScope, "scan", 1, "fixture incomplete scope", DateTimeOffset.MaxValue,
                ReadRoots: [targetRoot.RootId]));
            targetWriter.ObserveFails = failure == "read";
            var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            targetWriter.ObserveFails = false;
            var created = await target.ObserveAsync(path, default);
            Require((created is { Kind: ItemKind.Folder }) == (failure == "none"),
                "exact destination observation creates the repeated-name folder in this run only when reads and intersecting scopes permit it: " + sourceSide + "/" + failure);
            Require(report.Deleted == 0 && await fx.A.ObserveAsync("keep.txt", default) is not null &&
                await fx.B.ObserveAsync("keep.txt", default) is not null &&
                baselineKeys.IsSubsetOf(store.Baselines(fx.Pair.PairId).Select(basis => basis.ObjectKey)),
                "exact creation observation grants no new Delete or Forget authority");
            if (failure == "read")
                Require(store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == path &&
                    notice.ReasonKey == PlanNotices.DestinationUnobserved.Key),
                    "a failed exact read has the destination-not-observed reason, not cannot-read");
        }
        Console.WriteLine("PASS ExactCreationObservation");
    }
}
