namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task CachedReprobeKeepsPendingNoticeAsync()
    {
        var fx = Build();
        using var deadline = new CancellationTokenSource(GateTimeout);
        var ct = deadline.Token;
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var local = fx.A.Seed("held.txt", "retained", mtime: at);
        fx.Store.SaveSyncRetry(fx.Pair.PairId, new(local.Path, "deletion-evidence", 0,
            "history pending", DateTimeOffset.MinValue, Held: new HeldDeletion(local.Path, Side.A), ReprobeEvidence: true));
        fx.Store.AddNotices([EvidencePendingNotice.Create(fx.Pair.PairId, local.Path, "history pending")]);
        var pending = fx.Store.UnresolvedNotices(fx.Pair.PairId).Single();
        var evidence = new DeferredInventory(fx.B)
        {
            Block = true,
            Cached = new Dictionary<string, DateTimeOffset?> { [local.Path] = at.AddDays(1) },
            HistoryHashes = new Dictionary<string, string?> { [local.Path] = local.Hash }
        };
        fx.Provider.AddInventory(fx.Pair.B, evidence);
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), ct);
        Require(plan.HeldDeletions.Any(item => item.Path == local.Path) &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, local.Path) is { ReprobeEvidence: true } &&
            fx.Store.NoticeById(fx.Pair.PairId, pending.NoticeId!) is { Closed: false },
            "newer cached deletion keeps reprobe state and pending notice through prepare");
        var applying = run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), ct, ReviewedHeld(plan));
        try
        {
            await evidence.Started.Task.WaitAsync(ct);
            Require(!applying.IsCompleted && fx.Store.NoticeById(fx.Pair.PairId, pending.NoticeId!) is { Closed: false },
                "held cached evidence preserves its pending notice while the fresh check is blocked");
        }
        finally { evidence.Release.TrySetResult(); }
        await applying;
        var notice = fx.Store.UnresolvedNotices(fx.Pair.PairId).Single(n => n.Path == local.Path);
        Require(notice.ReasonKey == "deletion-review" &&
            notice.Detail.Contains("has been checked", StringComparison.Ordinal) &&
            !notice.Detail.Contains("UNAVAILABLE", StringComparison.Ordinal) &&
            fx.Store.NoticeById(fx.Pair.PairId, pending.NoticeId!) is { Closed: true },
            "freshly answered reprobe produces a checked decision and retires only the old pending notice");
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Puts == 0 && await fx.A.ObserveAsync(local.Path, ct) is not null,
            "a checked restored hold still requires a decision and preserves the survivor");
    }
}
