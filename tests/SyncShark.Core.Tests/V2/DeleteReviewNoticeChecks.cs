namespace Massdex.SyncShark.V2;

internal static class DeleteReviewNoticeChecks
{
    internal static void Run(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        var pair = new PairDef("pair", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
            new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
        store.SavePair(pair);
        var actions = Enumerable.Range(0, 53).Select(n => new SyncAction("uncertain-" + n.ToString("D3"), 6,
            ActionKind.Delete, Side.B, "Parent/" + n.ToString("D3"), null, null,
            new ItemState("Parent/" + n.ToString("D3"), ItemKind.File, "object-" + n, null, null, 0,
                DateTimeOffset.UnixEpoch, ItemFlags.None), false, null)).ToArray();
        foreach (var action in actions)
            store.CommitResult(pair.PairId, 1, action, new(action.ActionId, Outcome.Uncertain, null, null, null, "probe unavailable") { RetryAfterSeconds = 30 });
        store.RefreshBlockedDeleteNotice(pair.PairId);
        LocalChecks.Require(store.OpenNotices(pair.PairId).Single().Detail.StartsWith("53 ", StringComparison.Ordinal), "uncertain backlog has one exact summary");
        var first = store.ReadDeleteNoticeItems(pair.PairId, false);
        var second = store.ReadDeleteNoticeItems(pair.PairId, false, first.NextToken);
        LocalChecks.Require(first.Items.Count == 50 && second.Items.Count == 3, "uncertain keyset pages cover backlog");
        foreach (var action in actions)
            store.CommitResult(pair.PairId, 1, action, new(action.ActionId, Outcome.Skipped, null, null, null, "resolved"));
        store.RefreshBlockedDeleteNotice(pair.PairId);
        LocalChecks.Require(store.OpenNotices(pair.PairId).Count == 0, "uncertain summary closes at zero");
        foreach (var action in actions)
            store.CommitResult(pair.PairId, 1, action, new(action.ActionId, Outcome.Skipped, null, null, null, "missing identity")
                { DeleteSourceUnconfirmed = true, DeleteSourceIdentityMissing = true });
        store.RefreshBlockedDeleteNotice(pair.PairId);
        var summary = store.OpenNotices(pair.PairId).Single();
        LocalChecks.Require(summary.ReasonKey == SqliteStateStore.DeleteReviewSummary, "missing identity has one decision summary");
        var reviewPage = store.ReadDeleteNoticeItems(pair.PairId, true);
        var item = reviewPage.Items[0];
        LocalChecks.Require(store.NoticeById(pair.PairId, item.NoticeId!) is { Closed: false }, "aggregated item remains actionable by id");
        var run = new SyncRun(store, new Executor(store, V2Settings.Default, ExecutorChecks.Admission()),
            new FakeEndpointProvider(), V2Settings.Default, TimeProvider.System);
        var shown = run.ApplyRetryNoticeChoiceAsync(pair.PairId, summary.NoticeId!, NoticeChoices.ShowRemaining, default).GetAwaiter().GetResult();
        LocalChecks.Require(shown.Contains(item.NoticeId!, StringComparison.Ordinal) && shown.Contains(NoticeChoices.DeleteReviewed, StringComparison.Ordinal),
            "actual summary route supplies actionable per-item ids and choices");
        run.ApplyRetryNoticeChoiceAsync(pair.PairId, item.NoticeId!, NoticeChoices.KeepRestore, default).GetAwaiter().GetResult();
        LocalChecks.Require(store.SyncRetryAt(pair.PairId, item.Path) is { MissingDeleteIdentityReview: false, Kind: "reviewed-choice" },
            "Keep and sync back leaves review summary and queues safe copy action");
        Console.WriteLine("PASS uncertain and missing-identity summary paging and actual decision route");
    }
}
