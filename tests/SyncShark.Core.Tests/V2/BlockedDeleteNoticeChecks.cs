namespace Massdex.SyncShark.V2;

internal static class BlockedDeleteNoticeChecks
{
    internal static void Run(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("pair", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
            new("cloud", SyncProviders.Dropbox, null, "/cloud", "cloud"), [], [], true);
        store.SavePair(pair);
        for (var n = 0; n < 20; n++) Block(n);
        LocalChecks.Require(store.OpenNotices(pair.PairId).Count(x => x.ReasonKey == DeleteSourceGuard.ReasonKey) == 20,
            "twenty blocked paths retain individual explanations");
        for (var n = 20; n < 57; n++) Block(n);
        store.RefreshBlockedDeleteNotice(pair.PairId);
        var summary = store.OpenNotices(pair.PairId).Single(x => x.ReasonKey == SqliteStateStore.BlockedDeleteSummary);
        LocalChecks.Require(summary.Detail.Contains("57 deletions", StringComparison.Ordinal) &&
            !store.OpenNotices(pair.PairId).Any(x => x.ReasonKey == DeleteSourceGuard.ReasonKey), "one exact pair summary above threshold");
        LocalChecks.Require(NoticeOptions.Options(summary).Any(x => x.Choice == NoticeChoices.ShowRemaining), "summary routes to remaining items");
        var run = new SyncRun(store, new Executor(store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException),
            new FakeEndpointProvider(), V2Settings.Default, TimeProvider.System);
        var shown = run.ApplyRetryNoticeChoiceAsync(pair.PairId, summary.NoticeId!, NoticeChoices.ShowRemaining,
            default).GetAwaiter().GetResult();
        LocalChecks.Require(shown.Contains("Parent/000", StringComparison.Ordinal) && shown.Contains("Next page token:", StringComparison.Ordinal),
            "actual Show remaining action reads the storage page without endpoint access");
        var first = store.ReadBlockedDeletes(pair.PairId);
        var second = store.ReadBlockedDeletes(pair.PairId, first.NextToken);
        LocalChecks.Require(first.Items.Count == 50 && first.NextToken is not null && second.Items.Count == 7 &&
            second.NextToken is null && first.Items.Concat(second.Items).Select(x => x.Path).Distinct().Count() == 57,
            "bounded keyset pages contain every path once, including baseline-less held folder");
        for (var n = 0; n < 57; n++) Block(n);
        store.RefreshBlockedDeleteNotice(pair.PairId);
        LocalChecks.Require(store.OpenNotices(pair.PairId).Count(x => x.ReasonKey == SqliteStateStore.BlockedDeleteSummary) == 1 &&
            store.ReadBlockedDeletes(pair.PairId).Items.Count == 50, "repeat run deduplicates durable paths and summary");
        store.AddNotices([new Notice(pair.PairId, 40, NoticeKind.Info, "Parent/000", "generic retry", "sync-retry")]);
        LocalChecks.Require(!store.OpenNotices(pair.PairId).Any(x => x.ReasonKey == "sync-retry"), "generic failure cannot recreate flood");
        for (var n = 0; n < 57; n++) store.ClearSyncRetry(pair.PairId, $"Parent/{n:D3}");
        store.RefreshBlockedDeleteNotice(pair.PairId);
        LocalChecks.Require(!store.OpenNotices(pair.PairId).Any(x => x.ReasonKey is DeleteSourceGuard.ReasonKey or SqliteStateStore.BlockedDeleteSummary),
            "settled retries close both detail and summary notices");
        store.SaveSyncRetry(pair.PairId, new("Parent/000", "decision", 2, "original evidence owner", DateTimeOffset.MaxValue));
        Block(0, deferred: true);
        var delayed = store.SyncRetryAt(pair.PairId, "Parent/000")!;
        LocalChecks.Require(delayed.Kind == "decision" && delayed.Attempts == 2 && delayed.DeleteBlockedDeferred &&
            delayed.RetryAfter > DateTimeOffset.UtcNow && delayed.DeleteBlockedDetail is not null,
            "deferred source check preserves evidence ownership and provider delay");
        LocalChecks.Require(store.OpenNotices(pair.PairId).Single().ReasonKey == SqliteStateStore.BlockedDeleteSummary,
            "provider-deferred checks never emit per-item notices, even below threshold");
        // A later, unrelated write failure must replace the guard's explanation, not inherit suppression.
        store.ClearSyncRetry(pair.PairId, "Parent/000");
        Block(0, deferred: true);
        var blocked = store.SyncRetryAt(pair.PairId, "Parent/000")!;
        var saveFailure = typeof(SyncRun).GetMethod("SaveFailedRetry",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        // Reflection requires every argument, including the optional typed failure payload.
        saveFailure.Invoke(run, [pair, "Parent/000", "Upload permission denied", blocked, null, null]);
        store.RefreshBlockedDeleteNotice(pair.PairId);
        var failed = store.SyncRetryAt(pair.PairId, "Parent/000")!;
        LocalChecks.Require(failed.DeleteBlockedDetail is null && !failed.DeleteBlockedDeferred &&
            failed.LastError == "Upload permission denied", "new upload failure clears stale deletion-block fields");
        LocalChecks.Require(store.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == "sync-retry" &&
            notice.Detail.Contains("Upload permission denied", StringComparison.Ordinal)),
            "new upload failure publishes its own accurate retry notice");
        LocalChecks.Require(!store.OpenNotices(pair.PairId).Any(notice =>
            notice.ReasonKey is DeleteSourceGuard.ReasonKey or SqliteStateStore.BlockedDeleteSummary),
            "new non-guard failure leaves no stale blocked-delete notice or summary");
        Console.WriteLine("PASS blocked deletion threshold, deduplication, paging and settlement");

        void Block(int n, bool deferred = false)
        {
            var path = $"Parent/{n:D3}";
            var action = new SyncAction("blocked-" + n, 6, ActionKind.Delete, Side.B, path, null, null,
                new ItemState(path, n == 56 ? ItemKind.Folder : ItemKind.File, "object-" + n, null, null, 0,
                    DateTimeOffset.UnixEpoch, ItemFlags.None), false, null);
            store.CommitResult(pair.PairId, 1, action, new(action.ActionId, Outcome.Skipped, null, null, null,
                n == 56 ? "Folder held: child has no baseline." : "Source still present; nothing deleted.") { DeleteSourceUnconfirmed = true, RetryAfterSeconds = deferred ? 30 : null });
        }
    }
}
