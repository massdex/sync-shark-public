namespace Massdex.SyncShark.V2;

/// <summary>The named choices a user can apply to a v2 notice; the one home for the vocabulary.</summary>
public static class NoticeChoices
{
    /// <summary>Agree with what was done; no file change.</summary>
    public const string Accept = "accept";

    /// <summary>Dismiss the notice without agreeing; no file change.</summary>
    public const string Ignore = "ignore";

    public const string UseOtherVersion = "use-other-version";
    public const string SaveOtherVersionAsCopy = "save-other-version-as-copy";
    public const string Dismiss = "dismiss";
    public const string RetryNow = "retry-now";
    public const string Exclude = "exclude";
    public const string OpenFolderSettings = "open-folder-settings";
    public const string ReviewRemoval = "review-removal";
    public const string ReviewRemovalA = "review-removal-a";
    public const string ReviewRemovalB = "review-removal-b";
    public const string ConfirmRemoval = "confirm-removal";
    public const string RemovalNextPage = "removal-next-page";
    public const string RemovalPreviousPage = "removal-previous-page";
    internal const string RemovalReviewPrefix = "removal-review:";
    public static bool IsRemovalReview(Notice notice) => notice.ReasonKey.StartsWith(RemovalReviewPrefix, StringComparison.Ordinal);
    public static bool IsRemovalAttention(Notice notice) => notice.ReasonKey is "reviewed-folder-grew" or "reviewed-removal-skipped";
    public const string RemovalAttentionHelp = " Choose Review removal to inspect a fresh, frozen listing, then confirm only what you want removed. Ignore-protected children stay. Or open folder settings or dismiss; neither deletes files.";

    public const string DeleteReviewed = "delete";
    public const string KeepRestore = "keep-restore";

    /// <summary>A deletion-review notice whose survivor may be deleted with Recovery, kept/restored, or ignored.</summary>
    public const int DeletionReviewRule = 40;

    /// <summary>
    /// A deletion-review notice whose survivor is a folder or is flagged: neither may be given deletion
    /// authority automatically, so only keep/restore and ignore are offered. The ordinary safety guards
    /// still hold any restore that would write over protected content.
    /// </summary>
    public const int DeletionReviewRestoreOnlyRule = 41;

    public static bool IsRetryNotice(Notice notice) => notice.ReasonKey is "sync-retry" or "deletion-review" || IsRemovalAttention(notice) || IsRemovalReview(notice);

    public static bool IsRetryChoice(string choice) => choice is RetryNow or Exclude or Ignore or DeleteReviewed or KeepRestore or OpenFolderSettings or Dismiss or ReviewRemoval or ReviewRemovalA or ReviewRemovalB or ConfirmRemoval or RemovalNextPage or RemovalPreviousPage;

    public static bool IsRetainedVersionChoice(string choice) =>
        choice is UseOtherVersion or SaveOtherVersionAsCopy or Dismiss;

    public const string ReviewRebind = "review-rebind";
    public const string PausePair = "pause-pair";
    public const string LeaveAsIs = "leave-as-is";
    public const string RebindPending = "pending: rebind arrives with the central registry (B-191)";

    public static IReadOnlyList<SyncNoticeOption> Options(Notice notice) =>
        notice.RetainedVersion is not null
            ? [new(UseOtherVersion, "Use the other version"),
               new(SaveOtherVersionAsCopy, "Save the other version as a copy"), new(Dismiss, "Dismiss")]
            : notice.Rule == 38 && notice.ReasonKey == PlanNotices.CloudFilesBindingNeedsReview.Key
            ? [new(ReviewRebind, "Review / rebind to verified new path"),
               new(PausePair, "Pause the pair"), new(LeaveAsIs, "Leave as is")]
            : IsRemovalReview(notice)
            ? [new(ConfirmRemoval, "Confirm removal with Recovery"), new(RemovalPreviousPage, "Previous page"),
               new(RemovalNextPage, "Next page"), new(ReviewRemoval, "Refresh review"), new(Dismiss, "Cancel review")]
            : IsRemovalAttention(notice) && notice.RemovalSides is null
            ? [new(ReviewRemoval, "Review removal"), new(ReviewRemovalA, "Review removal on A (left)"),
               new(ReviewRemovalB, "Review removal on B (right)"), new(OpenFolderSettings, "Open folder settings"), new(Dismiss, "Dismiss")]
            : IsRemovalAttention(notice)
            ? [new(ReviewRemoval, "Review removal"), new(OpenFolderSettings, "Open folder settings"), new(Dismiss, "Dismiss")]
            : notice.ReasonKey == "deletion-review" && notice.Rule == DeletionReviewRestoreOnlyRule
            ? [new(KeepRestore, "Keep / restore"), new(Ignore, "Ignore; exclude this path from sync")]
            : notice.ReasonKey == "deletion-review"
            ? [new(DeleteReviewed, "Delete with recovery"), new(KeepRestore, "Keep / restore"), new(Ignore, "Ignore; exclude this path from sync")]
            : IsRetryNotice(notice)
            ? [new(RetryNow, "Retry now"), new(Exclude, "Exclude from sync"), new(Ignore, "Ignore; exclude this path from sync")]
            : [];

    public static string Result(string choice) => choice switch
    {
        ReviewRemoval or ReviewRemovalA or ReviewRemovalB => "Frozen removal review saved. Inspect its counts and pages in notices; nothing was deleted.",
        ConfirmRemoval => "Removal confirmation processed. Refresh notices for completion or held changes; protected content stays.",
        RemovalNextPage or RemovalPreviousPage => "Review page shown (at a boundary, the current page stays); nothing was deleted.",
        OpenFolderSettings => "Open this pair's folder settings to inspect selection and ignore rules. The notice remains open; no files changed.",
        RetryNow => "Retry requested. Existing evidence and deletion safeguards remain in force.",
        Exclude => "Excluded from future sync on both sides; no files were deleted.",
        Ignore => "Excluded this path from future sync on both sides; no files were changed or deleted.",
        UseOtherVersion => "Restored the other version; the displaced current version remains recoverable.",
        SaveOtherVersionAsCopy => "Saved the other version as a separate copy at your request.",
        Dismiss => "Dismissed; no files changed.",
        ReviewRebind => RebindPending,
        PausePair => "Pair turned off for future sync. A current run may finish. Enable it in Folder Pairs to resume; files and bindings are unchanged.",
        LeaveAsIs => "Left as is. Files and bindings are unchanged.",
        _ => "Accepted"
    };

    /// <summary>The choices a notice of this kind allows now; a held decision allows none.</summary>
    public static IReadOnlyList<string> For(NoticeKind kind) =>
        kind == NoticeKind.UserDecision ? [] : [Accept, Ignore];
}
