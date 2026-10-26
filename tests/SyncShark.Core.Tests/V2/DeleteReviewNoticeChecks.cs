namespace Massdex.SyncShark.V2;

internal static class DeleteReviewNoticeChecks
{
    private sealed class RetentionClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void AggregatedRetention(string folder)
    {
        var clock = new RetentionClock();
        string activeId, closedId;
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, clock, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(new PairDef("retention", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
                new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true));
            for (var i = 0; i < 22; i++)
            {
                var action = new SyncAction("review-" + i, 6, ActionKind.Delete, Side.B, "Parent/" + i, null, null,
                    new ItemState("Parent/" + i, ItemKind.File, "object-" + i, null, null, 0,
                        DateTimeOffset.UnixEpoch, ItemFlags.None), false, null);
                store.CommitResult("retention", 1, action, new(action.ActionId, Outcome.Skipped, null, null, null,
                    "missing identity") { DeleteSourceUnconfirmed = true, DeleteSourceIdentityMissing = true });
            }
            store.RefreshBlockedDeleteNotice("retention");
            var page = store.ReadDeleteNoticeItems("retention", true);
            activeId = page.Items[0].NoticeId!;
            closedId = page.Items[1].NoticeId!;
            LocalChecks.Require(store.NoticeById("retention", activeId) is { Closed: false } &&
                store.OpenNotices("retention").All(n => n.NoticeId != activeId),
                "review fixture is actionable but aggregated out of inbox");
            store.CloseNotice(closedId, "resolved");
            clock.Now += StorageChecks.Fast.HistoryRetention + TimeSpan.FromDays(1);
            store.Prune();
        }
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, clock, LocalItems.CaptureOptionalHash);
        LocalChecks.Require(reopened.NoticeById("retention", activeId) is { Closed: false },
            "aged active aggregated review survives prune and cold reopen");
        LocalChecks.Require(reopened.NoticeById("retention", closedId) is null,
            "same-age terminal notice retains existing ageing and is pruned");
    }

    internal static void Run(string folder)
    {
        AggregatedRetention(Path.Combine(folder, "retention"));
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("pair", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
            new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
        store.SavePair(pair);
        var actions = Enumerable.Range(0, 53).Select(n => new SyncAction("uncertain-" + n.ToString("D3"), 6,
            ActionKind.Delete, Side.B, "Parent/" + n.ToString("D3"), null, null,
            new ItemState("Parent/" + n.ToString("D3"), ItemKind.File, "object-" + n, null, null, 0,
                DateTimeOffset.UnixEpoch, ItemFlags.None), false, null)
            { ProviderEffect = new("delete-submit", "pending", DateTimeOffset.UnixEpoch) }).ToArray();
        store.CommitResult(pair.PairId, 1, actions[0], new(actions[0].ActionId, Outcome.Uncertain,
            null, null, null, "outcome unknown") { RetryAfterSeconds = 30 });
        LocalChecks.Require(store.OpenNotices(pair.PairId).Single().Detail.Contains("waiting for confirmation from Dropbox", StringComparison.Ordinal),
            "no-job-id submission notice describes waiting, not an automatic retry");
        foreach (var action in actions)
            store.CommitResult(pair.PairId, 1, action, new(action.ActionId, Outcome.Uncertain, null, null, null, "probe unavailable") { RetryAfterSeconds = 30 });
        store.RefreshBlockedDeleteNotice(pair.PairId);
        LocalChecks.Require(store.OpenNotices(pair.PairId).Single().Detail.StartsWith("53 ", StringComparison.Ordinal), "uncertain backlog has one exact summary");
        var first = store.ReadDeleteNoticeItems(pair.PairId, false);
        var second = store.ReadDeleteNoticeItems(pair.PairId, false, first.NextToken);
        LocalChecks.Require(first.Items.Count == 50 && second.Items.Count == 3, "uncertain keyset pages cover backlog");
        LocalChecks.Require(first.Items.All(item => item.Detail.Contains("waiting for confirmation from Dropbox", StringComparison.Ordinal)) &&
            !store.OpenNotices(pair.PairId).Single().Detail.Contains("retries automatically", StringComparison.OrdinalIgnoreCase),
            "pending submission pages and summary do not promise automatic retry");
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
        var run = new SyncRun(store, new Executor(store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException),
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
