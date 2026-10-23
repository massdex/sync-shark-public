namespace Massdex.SyncShark.V2;

/// <summary>Missing historical identity is a decision, never automatic removal authority.</summary>
internal static class MissingDeleteIdentityReview
{
    internal static SyncRetry Create(string pairId, SyncAction action)
    {
        const string detail = "No trusted identity is available for the deleted source. Nothing was deleted. " +
            "Choose Delete with Recovery, Keep and sync back, or Ignore.";
        var notice = new Notice(pairId, NoticeChoices.DeletionReviewRule, NoticeKind.UserDecision,
            action.Path, detail, "deletion-review");
        return new SyncRetry(action.Path, "decision", 0, detail, DateTimeOffset.MaxValue,
            ProposedActions: [action with { RecoveryFirst = true }], PendingNotice: notice)
            { MissingDeleteIdentityReview = true };
    }
}

public sealed partial class SyncRun
{
    internal Plan ReviewMissingDeleteIdentities(PairDef pair, Plan plan)
    {
        List<SyncAction>? actions = null;
        List<Notice>? notices = null;
        for (var index = 0; index < plan.Actions.Count; index++)
        {
            var action = plan.Actions[index];
            if (!DeleteSourceGuard.RequiresAbsence(action) ||
                !string.IsNullOrWhiteSpace(StoreCommitter.DeleteSource(_store, pair.PairId, action)?.ObjectId)) continue;
            // Keep an existing reviewed snapshot intact. Only a newly-created folder proposal
            // freezes current descendants; later growth must invalidate, never widen, its authority.
            if (FolderDelete.Expands(action) && action.FrozenVictimSignature is null)
                action = FreezeFolderDeletes(pair, plan with { Actions = [action] }).Actions.Single();
            var retry = MissingDeleteIdentityReview.Create(pair.PairId, action);
            _store.SaveSyncRetry(pair.PairId, retry);
            actions ??= plan.Actions.ToList();
            notices ??= plan.Notices.ToList();
            actions[index] = action with { Kind = ActionKind.Hold };
            notices.Add(retry.PendingNotice!);
        }
        if (actions is null) return plan;
        // Retry input is durable before notice publication, so a failed publication loses no review.
        WriteNotices(notices!);
        return plan with { Actions = actions, Notices = notices! };
    }
}
