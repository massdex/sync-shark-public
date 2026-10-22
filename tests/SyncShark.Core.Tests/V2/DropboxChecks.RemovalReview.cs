namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task RemovalReviewDropboxAsync()
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "removal-review");
        var pair = ClassificationPair();
        using var store = SqliteStateStore.Open(Path.Combine(product, "state"), StorageChecks.Fast, TimeProvider.System);
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        h.Wire.SeedFolder("folder");
        h.Wire.SeedFolder("folder/nested");
        h.Wire.SeedFile("folder/nested/file.txt", "recoverable bytes");
        h.Wire.SeedFile("unrelated.txt", "untouched");
        var local = new FakeEndpoint(Side.A, pair.A.RootId) { DropboxHashes = true };
        var (_, run) = PrepareCursorRuntime(h, local, pair, store, product);
        store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.UserDecision, "folder", "skipped",
            "reviewed-removal-skipped", RemovalSides: [Side.B])]);
        await run.ApplyRetryNoticeChoiceAsync(pair.PairId, store.OpenNotices(pair.PairId).Single().NoticeId!, NoticeChoices.ReviewRemoval, default);
        var review = store.OpenNotices(pair.PairId).Single(NoticeChoices.IsRemovalReview);
        Require(review.Detail.Contains("folder/nested/file.txt") && !review.Detail.Contains("unrelated.txt"), "Dropbox review lists only requested subtree");
        Require(store.Live(pair.B.RootId).Count == 0 && store.Cursor(pair.B.RootId) is null, "review observation does not overwrite sync inventory or cursors");
        Require(h.Wire.ListRequests.Count > 0 && h.Wire.ListRequests.All(request => request.Path != DropboxWire.RootId && request.Path != "/Pilot"),
            "review uses provider subtree listings, never a filtered whole-root listing");
        await run.ApplyRetryNoticeChoiceAsync(pair.PairId, review.NoticeId!, NoticeChoices.ConfirmRemoval, default);
        Require(await h.Writer().ObserveAsync("folder", default) is null && await h.Writer().ObserveAsync("unrelated.txt", default) is not null,
            "Dropbox side B removes nested folders deepest-first without touching siblings");
        var recovery = store.Recoveries(pair.PairId).Single();
        Require(recovery.Side == Side.B && recovery.VersionId is not null && recovery.Hash.Length > 0,
            "Dropbox removal retains exact version-history Recovery provenance");
    }
}
