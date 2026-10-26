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
