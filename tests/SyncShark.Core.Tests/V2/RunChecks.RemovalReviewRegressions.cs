namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task RemovalReviewRegressionsAsync()
    {
        foreach (var mode in new[] { "ignore", "exclude", "content", "excluded", "unrelated", "side-b", "nested", "failed", "interrupt", "consumed", "stale", "stale-dismiss" })
        {
            var root = Path.Combine(Path.GetTempPath(), "removal-regression-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var target = mode == "side-b" ? fx.B : fx.A;
            var writer = mode == "side-b" ? fx.WriterB : fx.WriterA;
            target.Seed("folder", "", ItemKind.Folder);
            target.Seed("folder/file.txt", "reviewed bytes");
            if (mode == "nested")
            {
                target.Seed("folder/child", "", ItemKind.Folder);
                target.Seed("folder/child/deep", "", ItemKind.Folder);
            }
            if (mode == "excluded") store.SavePair(fx.Pair with { Excluded = ["folder"] });
            store.MarkFirstSyncDone(fx.Pair.PairId);
            store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, "folder", "skipped",
                "reviewed-removal-skipped", RemovalSides: [mode == "side-b" ? Side.B : Side.A])]);
            var run = fx.Run();
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, store.OpenNotices(fx.Pair.PairId).Single().NoticeId!, NoticeChoices.ReviewRemoval, default);
            var review = store.OpenNotices(fx.Pair.PairId).Single(NoticeChoices.IsRemovalReview);
            // Refresh must retain the original reason, rather than turning a skip into growth.
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, review.NoticeId!, NoticeChoices.ReviewRemoval, default);
            review = store.OpenNotices(fx.Pair.PairId).Single(NoticeChoices.IsRemovalReview);
            Require(store.ReadPrepare(fx.Pair.PairId) is null, "removal review leaves first-sync prepare slot free");
            var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), root);
            await runtime.RescanPairAsync(fx.Pair.PairId, null, default);
            if (mode == "ignore") await runtime.SaveIgnoresAsync(fx.Pair.PairId, "FileName:file.txt", default);
            if (mode == "exclude") await runtime.SetSelectionAsync(fx.Pair.PairId, "folder", "exclude", default);
            if (mode == "content") target.Seed("folder/file.txt", "different bytes");
            if (mode == "excluded") Require(review.Detail.StartsWith("This folder is excluded from sync; confirming removes it anyway (Recovery-first)"), "excluded-folder confirmation warns explicitly");
            if (mode == "unrelated")
            {
                store.SavePair(Pair("unrelated", @"C:\unrelated", "/unrelated"));
                fx.A.Seed("elsewhere.txt", "unrelated change");
                await runtime.SetSelectionAsync(fx.Pair.PairId, "elsewhere", "exclude", default);
            }
            if (mode is "stale" or "consumed" or "stale-dismiss")
            {
                var header = store.ReadRemovalReview(fx.Pair, "folder")!;
                if (mode == "consumed") store.ConsumeRemovalReview(header.Generation);
                else store.ClearRemovalReview(header.Generation);
                Require(store.NoticeById(fx.Pair.PairId, review.NoticeId!) is { Closed: false }, "stale/used test reaches real open-notice review validation");
                await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, review.NoticeId!, mode == "stale-dismiss" ? NoticeChoices.Dismiss : NoticeChoices.ConfirmRemoval, default);
                Require(writer.Deletes == 0 && store.OpenNotices(fx.Pair.PairId).Any(NoticeChoices.IsRemovalAttention), "stale and consumed reviews never authorize replay or silently disappear");
                continue;
            }
            if (mode == "interrupt")
            {
                using var canceled = new CancellationTokenSource();
                await ExpectAsync<OperationCanceledException>(() => run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId,
                    review.NoticeId!, NoticeChoices.ConfirmRemoval, canceled.Token, removalResult: (_, _) => canceled.Cancel()));
                Require(store.ReadRemovalReview(fx.Pair, "folder") is null, "interrupted confirmation retires its consumed review");
                Require(store.OpenNotices(fx.Pair.PairId).Count(NoticeChoices.IsRemovalAttention) == 1, "interrupted confirmation leaves exactly one notice");
                await runtime.SaveIgnoresAsync(fx.Pair.PairId, "FileName:keep.txt", default);
                await runtime.SetSelectionAsync(fx.Pair.PairId, "folder", "exclude", default);
                await runtime.RescanPairAsync(fx.Pair.PairId, null, default);
                continue;
            }
            if (mode == "failed")
            {
                await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, review.NoticeId!, NoticeChoices.ConfirmRemoval,
                    default, removalResult: (action, _) => { if (action.ExpectedTarget?.Kind == ItemKind.File) target.Seed("folder/new.txt", "raced"); });
                Require(store.OpenNotices(fx.Pair.PairId).Count == 1 && store.OpenNotices(fx.Pair.PairId).Single().ReasonKey == "reviewed-removal-skipped",
                    "failed confirmation leaves exactly one notice and retains skip reason across refresh");
                Require(await target.ObserveAsync("folder/new.txt", default) is not null, "late child survives failed empty-only removal");
                continue;
            }
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, review.NoticeId!, NoticeChoices.ConfirmRemoval, default);
            if (mode is "ignore" or "exclude" or "content")
                Require(writer.Deletes == 0 && store.OpenNotices(fx.Pair.PairId).Single().ReasonKey == "reviewed-removal-skipped", "changed content or policy holds without losing skip reason");
            else
            {
                Require(await target.ObserveAsync("folder", default) is null && writer.Preserves == 1 && store.Recoveries(fx.Pair.PairId).Count == 1,
                    "confirmed subtree removal is Recovery-first on either side, deepest folders first");
                Require(store.OpenNotices(fx.Pair.PairId).Count == 0, "unrelated pair changes do not hold confirmation");
            }
        }
        await RemovalReviewIndependentFoldersAsync();
    }

    private static async Task RemovalReviewIndependentFoldersAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "removal-independent-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var run = fx.Run();
        foreach (var path in new[] { "x", "y" })
        {
            fx.A.Seed(path, "", ItemKind.Folder);
            fx.A.Seed(path + "/file.txt", "bytes");
            store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, path, "skipped", "reviewed-removal-skipped", RemovalSides: [Side.A])]);
            var notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.Path == path);
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.ReviewRemoval, default);
        }
        Require(store.ReadRemovalReview(fx.Pair, "x") is not null && store.ReadRemovalReview(fx.Pair, "y") is not null, "two folder entities retain independent reviews");
        var x = store.OpenNotices(fx.Pair.PairId).Single(n => n.Path == "x");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, x.NoticeId!, NoticeChoices.ConfirmRemoval, default);
        Require(await fx.A.ObserveAsync("x", default) is null && await fx.A.ObserveAsync("y/file.txt", default) is not null,
            "confirming one review does not overwrite or remove the other folder");
    }
}
