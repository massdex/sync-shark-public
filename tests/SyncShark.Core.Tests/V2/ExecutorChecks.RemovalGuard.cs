namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task ReviewedExpansionFallbackNoticeAsync()
    {
        var (store, a, b, executor) = Rig();
        var folder = b.Seed("reviewed", "", ItemKind.Folder);
        var child = b.Seed("reviewed/late.txt", "unreviewed");
        var generation = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, generation, "", [folder, child], []));
        var action = Act(ActionKind.Delete, Side.B, folder.Path, expected: folder) with
            { RemovalReviewChoice = true, FrozenVictimSignature = FolderDelete.Signature([]) };
        var writer = new RecordingWriter(b);
        var results = await Apply(executor, Plan(action), a, writer);
        Require(results.Single().Outcome == Outcome.Skipped && writer.Calls.Count == 0,
            "executor fallback remains fail closed without any fresh-proof bypass");
        var notice = store.UnresolvedNotices("p1").Single(item => item.Path == folder.Path);
        Require(store.SyncRetryAt("p1", folder.Path)?.ReviewedActions?.Single().ActionId == action.ActionId &&
            NoticeChoices.Options(notice).Any(option => option.Choice == NoticeChoices.RetryNow) &&
            NoticeChoices.Options(notice).Any(option => option.Choice == NoticeChoices.ReviewRemoval),
            "conservative stale-store hold retains choice and offers Retry and Review again");
        var otherAction = action with { ActionId = "other-reviewed-remove", Target = Side.A };
        DeleteSourceGuard.HoldReviewedRemoval(store, "p1", otherAction);
        DeleteSourceGuard.HoldReviewedRemoval(store, "p1", otherAction);
        var retained = store.SyncRetryAt("p1", folder.Path)!;
        Require(retained.ReviewedActions?.Count == 2 && retained.PendingNotice?.RemovalSides?.Count == 2,
            "same-path holds retain both side choices without duplicate actions");
        using var signature = new DeleteSourceGuard.ReviewSignature();
        signature.Add("{\"item\":1}");
        var frozen = signature.Finish();
        Require(DeleteSourceGuard.ReviewedRowsUnchanged(frozen, ["{\"item\":1}"]) &&
            !DeleteSourceGuard.ReviewedRowsUnchanged(frozen, ["{\"item\":2}"]),
            "one guard owner compares legacy JSON review rows without changing their saved format");
    }
}
