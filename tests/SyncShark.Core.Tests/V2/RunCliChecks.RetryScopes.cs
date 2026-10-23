namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    private static async Task Cli_RetryScopesAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            await FirstSyncAsync(fx);
            var source = fx.A.Seed("pair-retry.txt", "pair retry content");
            var summary = new Notice(fx.Pair.PairId, 0, NoticeKind.Info, "", "Run failed; retry.", "run-unavailable");
            store.AddNotices([summary]);
            var notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == summary.ReasonKey);
            await Runtime(fx, root).ResolveConflictAsync(fx.Pair.PairId, "", NoticeChoices.RetryNow,
                notice.NoticeId, 0, null, CancellationToken.None);
            Require((await fx.B.ObserveAsync(source.Path, CancellationToken.None))?.Hash == source.Hash,
                "pair-scoped CLI Retry enters the ordinary admitted run and performs pending work");

            var retry = new SyncRetry("item-retry.txt", "scan", 3, "cannot read", DateTimeOffset.UtcNow.AddHours(1));
            store.SaveSyncRetry(fx.Pair.PairId, retry);
            store.AddNotices([new(fx.Pair.PairId, 40, NoticeKind.Info, retry.Path, "Cannot read; retry.", "sync-retry")]);
            var itemNotice = store.OpenNotices(fx.Pair.PairId).Single(n => n.Path == retry.Path);
            Require(fx.Run().TryApplyImmediateNoticeChoice(fx.Pair.PairId, itemNotice.NoticeId!, NoticeChoices.RetryNow,
                CancellationToken.None), "item Retry is accepted by shared scheduling authority");
            var due = store.SyncRetryAt(fx.Pair.PairId, retry.Path);
            Require(due is not null && due.RetryAfter <= DateTimeOffset.UtcNow && due.Attempts == retry.Attempts &&
                due.LastError == retry.LastError, "item Retry makes existing debt due without erasing failure evidence");
        }
        finally { Cleanup(root); }
    }
}
