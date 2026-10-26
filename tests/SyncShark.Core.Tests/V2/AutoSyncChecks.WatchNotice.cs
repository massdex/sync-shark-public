namespace Massdex.SyncShark.V2;

internal static partial class AutoSyncChecks
{
    private static async Task WatchNotice_PersistsAndRecoversAsync()
    {
        var root = LocalChecks.FreshRoot("host-watch-notice-" + Guid.NewGuid().ToString("N"));
        var clock = new ManualClock();
        var watches = new FakeChangeWatchSource { WatchError = new IOException("watch channel unavailable") };
        var pair = RigPair("p1", true);
        string id;
        FailureRetryOwner.State retained;
        using (var store = SqliteStateStore.Open(root, Settings, clock, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            store.MarkFirstSyncDone(pair.PairId);
            var auto = new AutoSync(store, new FakeSyncRun(), watches, Settings, _ => false, () => SafetyPoll, clock);
            try
            {
                auto.Start();
                await Until(() => store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "autosync-watch-unavailable") && clock.ActiveTimers == 1,
                    "watch failure persists one notice");
                var notice = store.OpenNotices(pair.PairId).Single(n => n.ReasonKey == "autosync-watch-unavailable");
                id = notice.NoticeId!;
                Require(NoticeOptions.Options(notice).Select(option => option.Choice).SequenceEqual(new[] { NoticeChoices.RetryNow, NoticeChoices.Ignore }),
                    "watch failure offers Retry and Ignore");
                clock.Advance(Settings.AutoRetryDelay);
                await Until(() => store.NoticeById(pair.PairId, id)?.Notice.FailureRetry?.Attempts == 2 && clock.ActiveTimers == 1,
                    "repeat watch failure refreshes metadata on the same notice");
                retained = store.NoticeById(pair.PairId, id)!.Value.Notice.FailureRetry!.Value;
                Require(store.OpenNotices(pair.PairId).Count(n => n.ReasonKey == "autosync-watch-unavailable") == 1, "watch failure deduplicates");

                var provider = new FakeEndpointProvider().Add(pair.A, new FakeEndpoint(Side.A, pair.A.RootId))
                    .Add(pair.B, new FakeEndpoint(Side.B, pair.B.RootId));
                var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), provider, Settings, clock);
                await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
                Require(store.NoticeById(pair.PairId, id) is { Closed: false }, "ordinary run success never closes a dead-watch notice");
                store.CloseNotice(id, NoticeChoices.Ignore);
            }
            finally { await auto.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        }
        using (var store = SqliteStateStore.Open(root, Settings, clock, LocalItems.CaptureOptionalHash))
        {
            var ignored = store.NoticeById(pair.PairId, id);
            Require(ignored is { Closed: false } && ignored.Value.Notice.FailureRetry == retained,
                "cold reopen preserves suppressed active watch retry attempts and deadline");
            Require(!store.OpenNotices(pair.PairId).Any(n => n.NoticeId == id), "Ignore stays suppressed across reopen");
            watches.WatchError = new IOException("watch channel unavailable on another attempt");
            var auto = new AutoSync(store, new FakeSyncRun(), watches, Settings, _ => false, () => SafetyPoll, clock);
            try
            {
                auto.Start();
                await Until(() => clock.ActiveTimers == 1, "restarted degraded loop parks");
                clock.Advance(retained.RetryAfter - clock.GetUtcNow());
                await Until(() => store.NoticeById(pair.PairId, id)?.Notice.FailureRetry?.Attempts == 3 && clock.ActiveTimers == 1,
                    "restarted loop continues persisted watch backoff");
                Require(!store.OpenNotices(pair.PairId).Any(n => n.NoticeId == id), "same outage does not reopen Ignore");
                watches.WatchError = null;
                auto.Kick(pair.PairId); // Same wake invoked by the Retry choice at both host edges.
                await Until(() => watches.Created == 2 && clock.ActiveTimers == 1, "Retry immediately rearms watches");
                clock.Advance(Settings.ChangeQuietWindow);
                await Until(() => store.NoticeById(pair.PairId, id) is { Closed: true }, "watch recovery closes its own episode");
            }
            finally { await auto.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        }
        Pass("WatchNotice_PersistsAndRecovers");
    }
}
