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
    public static bool IsRemovalAttention(Notice notice) => notice.ReasonKey is "reviewed-folder-grew" or "reviewed-removal-skipped";
    public const string RemovalAttentionHelp = " Open folder settings to inspect selection and ignore rules, or dismiss this notice. You can remove the unwanted copy yourself or adjust the ignore rules. Ordinary sync carries deletions with its usual Recovery safeguards only when the path is included and no protection holds it; excluded paths remain untouched.";

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

    public static bool IsRetryNotice(Notice notice) => notice.ReasonKey is "sync-retry" or "deletion-review" or "planning-degraded" or "recovery-unavailable" || IsRemovalAttention(notice);

    public static bool IsRetryChoice(string choice) => choice is RetryNow or Exclude or Ignore or DeleteReviewed or KeepRestore or OpenFolderSettings or Dismiss;

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
            : IsRemovalAttention(notice)
            ? [new(OpenFolderSettings, "Open folder settings"), new(Dismiss, "Dismiss")]
            : notice.ReasonKey == "deletion-review" && notice.Rule == DeletionReviewRestoreOnlyRule
            ? [new(KeepRestore, "Keep / restore"), new(Ignore, "Ignore; exclude this path from sync")]
            : notice.ReasonKey == "deletion-review"
            ? [new(DeleteReviewed, "Delete with recovery"), new(KeepRestore, "Keep / restore"), new(Ignore, "Ignore; exclude this path from sync")]
            : notice.ReasonKey is "planning-degraded" or "recovery-unavailable"
            ? [new(RetryNow, "Retry now"), new(OpenFolderSettings, "Details / folder settings")]
            : IsRetryNotice(notice)
            ? [new(RetryNow, "Retry now"), new(Exclude, "Exclude from sync"), new(Ignore, "Ignore; exclude this path from sync")]
            : [];

    public static string Result(string choice) => choice switch
    {
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
