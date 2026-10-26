namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task EvidenceNoticeLifecycleAsync()
    {
        await CachedReprobeKeepsPendingNoticeAsync();
        foreach (var ignored in new[] { false, true })
        foreach (var cached in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "evidence-notice-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            await EvidenceNoticeLifecycleAsync(store, ignored, cached);
            using var memory = new InMemoryStateStore();
            await EvidenceNoticeLifecycleAsync(memory, ignored, cached);
        }
    }

    private static async Task EvidenceNoticeLifecycleAsync(IStateStore store, bool ignored, bool cached)
    {
        var fx = Build(suppliedStore: store);
        var clock = new CompletionClock();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, clock);
        fx.A.Seed("held.txt", "retained");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B) { LookupFailure = true });
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, ReviewedHeld(plan));
        var pending = store.UnresolvedNotices(fx.Pair.PairId).Single(n => n.Path == "held.txt");
        Require(pending.ReasonKey == "deletion-evidence-pending" && fx.WriterB.Puts == 0,
            "first run retains one pending-evidence notice without an upload");
        if (ignored) store.CloseNotice(pending.NoticeId!, NoticeChoices.Ignore);
        var retry = store.SyncRetryAt(fx.Pair.PairId, "held.txt")!;
        clock.Now = retry.RetryAfter;
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B)
        {
            Cached = cached ? new Dictionary<string, DateTimeOffset?> { ["held.txt"] = null } :
                new Dictionary<string, DateTimeOffset?>()
        });
        fx.WriterB.PutError = new IOException("Upload failed after history answered");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var notices = store.UnresolvedNotices(fx.Pair.PairId).Where(n => n.Path == "held.txt").ToArray();
        Require(report.Failed == 1 && notices.Length == 1 && notices[0].ReasonKey == "sync-retry" &&
            notices[0].Detail.Contains("Upload failed after history answered", StringComparison.Ordinal) &&
            store.OpenNotices(fx.Pair.PairId).Any(n => n.NoticeId == notices[0].NoticeId),
            "answered evidence retires even an ignored old cause and exposes exactly one real effect failure");
        Require(store.NoticeById(fx.Pair.PairId, pending.NoticeId!) is { Closed: true } &&
            store.SyncRetryAt(fx.Pair.PairId, "held.txt") is { ReprobeEvidence: false } &&
            await fx.A.ObserveAsync("held.txt", default) is not null && fx.WriterA.Deletes == 0,
            "evidence retirement retains the survivor and effect retry without stale reprobe state");
    }
}
