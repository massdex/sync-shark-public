namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task Completion_ApplyPreservesDecisionAsync()
    {
        foreach (var choice in new[] { NoticeChoices.DeleteReviewed, NoticeChoices.KeepRestore })
        {
            var fx = Build();
            var local = fx.A.Seed("held.txt", "reviewed local survivor");
            fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B)
                { Deleted = DateTimeOffset.UtcNow.AddDays(1), ContentHash = local.Hash });
            var run = fx.Run();
            var sink = new RecordingProgress();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, sink, default);
            Require(plan.HeldDeletions.Any(item => item.Path == "held.txt"), "prepare includes the reviewed held survivor");
            fx.Store.SaveSyncRetry(fx.Pair.PairId, new("held.txt", "deletion-evidence", 3, "released history failure",
                DateTimeOffset.MinValue, Held: new HeldDeletion("held.txt", Side.A)));
            await run.ApplyAsync(fx.Pair.PairId, plan, sink, default, ReviewedHeld(plan));
            Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt") is
                { Kind: "decision", ProposedActions: { Count: > 0 }, HeldReviewed: false, ReviewedActions: null },
                "completion parking cannot overwrite a decision with prepare deletion authority");
            var decision = fx.Store.OpenNotices(fx.Pair.PairId).Single(item => item.Path == "held.txt" && item.ReasonKey == "deletion-review");
            // Exercise the duplicate CLI/apply deferral saves against an already-owned decision.
            var unreviewed = plan with { Actions = [], HeldDeletions = [], UnreviewedHeldDeletions = [new("held.txt", Side.A)] };
            run.DeferPostPreviewChanges(fx.Pair, unreviewed, new Dictionary<string, PrepareMatch>());
            Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt") is { Kind: "decision", ProposedActions: { Count: > 0 } },
                "unreviewed handoff cannot erase the usable proposal");
            await run.IncrementalAsync(fx.Pair.PairId, sink, default);
            await run.IncrementalAsync(fx.Pair.PairId, sink, default);
            Require(fx.WriterA.Deletes == 0 && fx.WriterA.Preserves == 0 && fx.WriterB.Puts == 0 &&
                (await fx.A.ObserveAsync("held.txt", default))?.Hash == local.Hash,
                "prepare authority never deletes or uploads while the decision is open");
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, decision.NoticeId!, choice, default);
            await run.IncrementalAsync(fx.Pair.PairId, sink, default);
            if (choice == NoticeChoices.DeleteReviewed)
                Require(fx.WriterA.Deletes == 1 && fx.WriterA.Preserves == 1 && fx.WriterB.Puts == 0 &&
                    await fx.A.ObserveAsync("held.txt", default) is null, "Delete remains usable and Recovery-first after first-sync completion");
            else
                Require(fx.WriterA.Deletes == 0 && fx.WriterB.Puts == 1 &&
                    (await fx.B.ObserveAsync("held.txt", default))?.Hash == local.Hash &&
                    (await fx.A.ObserveAsync("held.txt", default))?.Hash == local.Hash,
                    "Keep remains usable and restores without deleting the survivor");
        }
        Console.WriteLine("PASS Completion_ApplyPreservesDecision");
    }
}
