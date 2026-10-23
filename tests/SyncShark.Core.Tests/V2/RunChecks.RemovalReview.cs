namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task RemovalReviewPagesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "removal-pages-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("folder", "", ItemKind.Folder);
        for (var i = 0; i < 260; i++) fx.A.Seed($"folder/{i:D3}.txt", "content");
        store.MarkFirstSyncDone(fx.Pair.PairId);
        store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, "folder", "legacy", "reviewed-folder-grew")]);
        var original = store.OpenNotices(fx.Pair.PairId).Single();
        var run = fx.Run();
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, original.NoticeId!, NoticeChoices.ReviewRemovalA, default);
        var first = store.OpenNotices(fx.Pair.PairId).Single(NoticeChoices.IsRemovalReview);
        Require(first.Detail.Contains("Remove 260 files") && first.Detail.Contains("Page 1/6"), "bounded listing reports whole snapshot counts");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, first.NoticeId!, NoticeChoices.RemovalNextPage, default);
        var second = store.OpenNotices(fx.Pair.PairId).Single(NoticeChoices.IsRemovalReview);
        Require(second.Detail.Contains("Page 2/6") && second.NoticeId != first.NoticeId, "next page is bound to the same snapshot with a current exact key");
        await ExpectAsync<SyncRunRefusedException>(() => run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, first.NoticeId!, NoticeChoices.ConfirmRemoval, default));
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, second.NoticeId!, NoticeChoices.ReviewRemoval, default);
        await ExpectAsync<SyncRunRefusedException>(() => run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, second.NoticeId!, NoticeChoices.ConfirmRemoval, default));
        Require(fx.WriterA.Deletes == 0 && fx.WriterA.Preserves == 0, "paging and replacing a review never authorize removal");
    }

    internal static async Task RemovalReviewChecksAsync()
    {
        foreach (var mode in new[] { "confirm", "changed-target", "changed-other", "ignored", "cancel" })
        {
            var root = Path.Combine(Path.GetTempPath(), "removal-review-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("folder", "", ItemKind.Folder);
            fx.A.Seed("folder/file.txt", "reviewed bytes");
            fx.A.InventoryWithoutHashes = true;
            fx.A.RequireHashes = true;
            if (mode == "ignored")
            {
                store.SavePair(fx.Pair with { IgnoreRules = ["FileName:keep.txt"] });
                fx.A.Seed("folder/keep.txt", "protected bytes");
            }
            store.MarkFirstSyncDone(fx.Pair.PairId);
            store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, "folder", "grew",
                "reviewed-folder-grew", RemovalSides: [Side.A])]);
            var original = store.OpenNotices(fx.Pair.PairId).Single();
            var run = fx.Run();
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, original.NoticeId!, NoticeChoices.ReviewRemoval, default);
            var review = store.OpenNotices(fx.Pair.PairId).Single(NoticeChoices.IsRemovalReview);
            if (mode == "confirm")
            {
                // Exercise the shared CLI edge against the same SQLite-backed frozen snapshot.
                var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), root);
                await runtime.ResolveConflictAsync(fx.Pair.PairId, review.Path, NoticeChoices.RemovalNextPage,
                    review.NoticeId, 0, null, default);
                review = store.OpenNotices(fx.Pair.PairId).Single(NoticeChoices.IsRemovalReview);
            }
            Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 && fx.WriterA.Preserves == 0,
                "review is observation only, never deletion authority");
            Require(review.Detail.Contains("folder/file.txt") && review.Detail.Contains("Signature"),
                "review exposes frozen counts, signature and exact listing");
            if (mode == "changed-target") fx.A.Seed("folder/new.txt", "new child");
            if (mode == "changed-other") fx.B.Seed("folder/other.txt", "other side changed");
            if (mode == "confirm")
            {
                var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), root);
                var receipt = await runtime.ResolveConflictAsync(fx.Pair.PairId, review.Path, NoticeChoices.ConfirmRemoval,
                    review.NoticeId, 0, null, default);
                Require(receipt.Deleted == 1 && receipt.Failed == 0, "CLI reports the confirmed file removal, not a zero-effect receipt");
            }
            else await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, review.NoticeId!,
                mode == "cancel" ? NoticeChoices.Dismiss : NoticeChoices.ConfirmRemoval, default);
            if (mode is "changed-target" or "changed-other")
            {
                Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
                    store.OpenNotices(fx.Pair.PairId).Any(NoticeChoices.IsRemovalAttention),
                    "either-side growth holds the whole removal and re-raises review notice");
            }
            else if (mode == "cancel")
                Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0, "cancel never deletes");
            else
            {
                Require(await fx.A.ObserveAsync("folder/file.txt", default) is null &&
                    fx.WriterA.Preserves == 1 && store.Recoveries(fx.Pair.PairId).Count == 1,
                    "confirmed file removal has a retained Recovery copy");
                Require((await fx.A.ObserveAsync("folder", default) is not null) == (mode == "ignored"),
                    "empty reviewed folders are removed; protected-child parents survive");
                if (mode == "ignored")
                    Require(await fx.A.ObserveAsync("folder/keep.txt", default) is not null && review.Detail.Contains("KEEP: ignore rule"),
                        "ignored child is listed and survives enclosing review");
            }
            await ExpectAsync<SyncRunRefusedException>(() => run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId,
                review.NoticeId!, NoticeChoices.ConfirmRemoval, default));
        }
        await RemovalReviewPagesAsync();
        // The focused --v2-removal-review-checks entry point includes all follow-up regressions.
        await RemovalReviewRegressionsAsync();
        Console.WriteLine("PASS RemovalReview confirm/recovery, both-side growth, ignore protection and no-confirm safety");
    }
}
