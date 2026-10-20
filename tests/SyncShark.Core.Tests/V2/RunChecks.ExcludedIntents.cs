namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task Reconcile_ExcludedUncertainHonorsGraceAsync()
    {
        foreach (var folder in new[] { false, true })
        {
            var fx = Build();
            fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
            fx.Store.SavePair(fx.Pair with { Excluded = ["private"] });
            var target = fx.B.Seed("private/item", "same", folder ? ItemKind.Folder : ItemKind.File);
            var intent = new SyncAction("pending-excluded", 23, ActionKind.Delete, Side.B, "private/item",
                null, null, target, false, "pending-key")
            {
                ProviderEffect = new ProviderEffectCheckpoint("delete-submit", "pending",
                    DateTimeOffset.UtcNow - (folder ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1)))
            };
            fx.Store.RecordIntent(fx.Pair.PairId, intent);
            var run = fx.Run();
            var pending = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 1,
                "excluded uncertain effect retains provider evidence within grace (folder minimum two hours)");
            Require(pending.Failures.Single().Reason!.Contains("excluded, awaiting provider confirmation"),
                "pending excluded effect is explicitly reported rather than silently stalled");
            Require(!fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == SyncRun.ExcludedIntentNoticeKey),
                "uncertain effect is not described as not applied");
            fx.Store.RecordIntent(fx.Pair.PairId, intent with
            {
                ProviderEffect = intent.ProviderEffect! with { RecordedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(1) }
            });
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0,
                "unchanged target settles after resolver grace expiry");
            Require(fx.WriterB.Deletes == 0, "pending or expired excluded effect is never replayed");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == SyncRun.ExcludedIntentNoticeKey) == 1,
                "definitive expired effect gets the settlement notice");
        }
    }

    private static async Task Reconcile_ExcludedMoveCompletesFirstSyncAsync()
    {
        var fx = Build();
        var source = fx.A.Seed("old.txt", "same");
        var target = fx.B.Seed("old.txt", "same");
        fx.Store.SavePair(fx.Pair with { Excluded = ["private"] });
        fx.Store.RecordIntent(fx.Pair.PairId, new SyncAction("excluded-move", 9, ActionKind.Move,
            Side.B, "private/new.txt", "old.txt", source, target, false, "move-key"));
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0, "excluded move settles");
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId), "settled move does not block first-sync completion");
        Require(fx.WriterB.Moves == 0, "excluded move is never performed");
        var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == SyncRun.ExcludedIntentNoticeKey);
        Require(notice.Detail.Contains(SyncRun.ExcludedIntentReason) && notice.Detail.Contains("private/new.txt") &&
            notice.Detail.Contains("old.txt") && notice.Detail.Contains("include the folder and resync") &&
            notice.Detail.Contains("leave excluded"), "one notice explains paths and decision-74 options");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == SyncRun.ExcludedIntentNoticeKey) == 1,
            "notice persists without duplicates on resume");
    }

    private static async Task Reconcile_ExcludedFolderDeleteNeverExpandsAsync()
    {
        var fx = Build();
        fx.A.Seed("folder", "", ItemKind.Folder);
        var folder = fx.B.Seed("folder", "", ItemKind.Folder);
        fx.A.Seed("folder/private/keep.txt", "same");
        fx.B.Seed("folder/private/keep.txt", "same");
        fx.Store.SavePair(fx.Pair with { Excluded = ["folder/private"] });
        fx.Store.RecordIntent(fx.Pair.PairId, new SyncAction("excluded-delete", 23, ActionKind.Delete,
            Side.B, "folder", null, null, folder, true, "folder-key"));
        await FirstSyncAsync(fx.Run(), fx);
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0, "containing delete settles");
        Require(fx.WriterB.Deletes == 0, "folder delete is never expanded or applied");
        Require((await MapAsync(fx.B)).ContainsKey("folder/private/keep.txt"), "excluded descendant survives");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == SyncRun.ExcludedIntentNoticeKey)
            .Detail.Contains("folder/private"), "notice names excluded descendant boundary");
    }

    private static async Task Reconcile_ExcludedAppliedIntentIsDoneAsync()
    {
        var fx = Build();
        var source = fx.A.Seed("private/done.txt", "same");
        fx.B.Seed("private/done.txt", "same");
        fx.Store.SavePair(fx.Pair with { Excluded = ["private"] });
        fx.Store.RecordIntent(fx.Pair.PairId, new SyncAction("excluded-applied", 1, ActionKind.CopyNew,
            Side.B, "private/done.txt", null, source, null, false, "applied-key"));
        await FirstSyncAsync(fx.Run(), fx);
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0, "applied excluded intent resolves");
        Require(fx.Store.Baselines(fx.Pair.PairId).Any(b => b.ObjectKey == "applied-key"),
            "positive observation commits Done and its baseline, not Skipped");
        Require(!fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == SyncRun.ExcludedIntentNoticeKey),
            "proven applied intent needs no not-applied warning");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "settlement performs no endpoint effect");
    }
}
