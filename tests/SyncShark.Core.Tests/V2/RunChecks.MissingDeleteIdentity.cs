namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task MissingDeleteIdentityChoicesAsync()
    {
        foreach (var kind in new[] { ItemKind.File, ItemKind.Folder })
        foreach (var choice in new[] { NoticeChoices.DeleteReviewed, NoticeChoices.KeepRestore, NoticeChoices.Ignore })
        {
            var fx = Build();
            var survivor = fx.A.Seed("item", kind == ItemKind.File ? "content" : "", kind);
            fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
            var proposed = new SyncAction("no-identity", kind == ItemKind.Folder ? 23 : 19,
                ActionKind.Delete, Side.A, survivor.Path, null, null, survivor, true, null);
            var plan = new Plan(fx.Pair.PairId, 1, [proposed], []);
            var run = fx.Run();
            var held = run.ReviewMissingDeleteIdentities(fx.Pair, plan);
            Require(held.Actions.Single().Kind == ActionKind.Hold && fx.WriterA.Deletes == 0,
                "missing identity routes to review without automatic deletion");
            run.ReviewMissingDeleteIdentities(fx.Pair, plan);
            var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(item => item.ReasonKey == "deletion-review");
            Require(fx.Store.SyncRetryAt(fx.Pair.PairId, survivor.Path) is
                { Kind: "decision", MissingDeleteIdentityReview: true, ReviewedActions: null },
                "missing identity persists a deduplicated decision, not transient retry authority");
            Require(NoticeOptions.Options(notice).Select(option => option.Choice).ToHashSet().IsSupersetOf(
                [NoticeChoices.DeleteReviewed, NoticeChoices.KeepRestore, NoticeChoices.Ignore]),
                "review offers recovery delete, sync-back, and ignore");
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, choice, default);
            if (choice == NoticeChoices.DeleteReviewed)
                Require(fx.Store.SyncRetryAt(fx.Pair.PairId, survivor.Path)!.ReviewedActions!.Single() is
                    { RemovalReviewChoice: true, RecoveryFirst: true }, "only explicit choice stamps durable removal authority");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(fx.WriterA.Deletes == (choice == NoticeChoices.DeleteReviewed ? 1 : 0),
                "only Delete choice removes the exact victim");
            if (choice == NoticeChoices.DeleteReviewed)
                Require(fx.WriterA.Preserves == (kind == ItemKind.File ? 1 : 0),
                    "file bytes reach Recovery; an empty folder has no bytes to archive");
            if (choice == NoticeChoices.KeepRestore)
                Require(await fx.B.ObserveAsync(survivor.Path, default) is not null, "Keep restores the missing copy");
            if (choice == NoticeChoices.Ignore)
                Require(await fx.A.ObserveAsync(survivor.Path, default) is not null, "Ignore retains the survivor");
        }
        await MissingIdentityFolderArchivesChildrenAsync();
        Console.WriteLine("PASS MissingDeleteIdentityChoices");
    }

    private static async Task MissingIdentityFolderArchivesChildrenAsync()
    {
        var fx = Build();
        var folder = fx.A.Seed("folder", "", ItemKind.Folder);
        var child = fx.A.Seed("folder/child.txt", "unique bytes without a deleted revision");
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        var gen = fx.Store.BeginGeneration(fx.Pair.A.RootId, "fixture");
        fx.Store.WriteSnapshot(new(fx.Pair.A.RootId, Side.A, gen, "fixture-cursor", [folder, child], []));
        fx.Store.CompleteGeneration(gen, "fixture-cursor", true);
        var action = new SyncAction("review-folder", 23, ActionKind.Delete, Side.A, folder.Path,
            null, null, folder, true, null);
        var run = fx.Run();
        run.ReviewMissingDeleteIdentities(fx.Pair, new(fx.Pair.PairId, gen, [action], []));
        var proposal = fx.Store.SyncRetryAt(fx.Pair.PairId, folder.Path)!.ProposedActions!.Single();
        Require(proposal.FrozenVictimSignature == FolderDelete.Signature([child]),
            "missing-identity folder proposal freezes its actual child, not an invented empty snapshot");
        var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(item => item.ReasonKey == "deletion-review");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.DeleteReviewed, default);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.WriterA.Deletes == 2 && fx.WriterA.Preserves == 1 &&
            await fx.A.ObserveAsync(folder.Path, default) is null,
            "explicit reviewed folder archives child bytes once before removing the child and empty parent");
    }
}
