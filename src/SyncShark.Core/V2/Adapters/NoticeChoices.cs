namespace Massdex.SyncShark.V2;

/// <summary>The named choices a user can apply to a v2 notice; the one home for the vocabulary.</summary>
public static class NoticeChoices
{
    /// <summary>Agree with what was done; no file change.</summary>
    public const string Accept = "accept";

    /// <summary>Dismiss the notice without agreeing; no file change.</summary>
    public const string Ignore = "ignore";

    public const string Compare = "compare";
    public const string Merge = "merge";
    public const string KeepA = "keep-a";
    public const string KeepB = "keep-b";
    public const string KeepBoth = "keep-both";
    public const string RenameIncoming = "rename-incoming";
    public const string ReplaceExisting = "replace-existing";
    public const string RestoreExisting = "restore-existing";
    public const string RestoreAsCopy = "restore-as-copy";

    /// <summary>A finished replace that removed the occupant and still waits for the incoming item to arrive.</summary>
    public static bool AwaitsArrival(Notice notice) =>
        notice.ConflictOperation is { Choice: ReplaceExisting, Finished: true } operation &&
        operation.Actions.Any(action => action.Kind == ActionKind.Delete);

    public const string UseOtherVersion = "use-other-version";
    public const string SaveOtherVersionAsCopy = "save-other-version-as-copy";
    public const string Dismiss = "dismiss";
    public const string RetryNow = "retry-now";
    public const string Exclude = "exclude";
    public const string OpenFolderSettings = "open-folder-settings";
    public const string OpenRecoverySettings = "open-recovery-settings";
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

    public static bool IsPairRetryNotice(Notice notice) => notice.ReasonKey == "run-history-unavailable" || IsAutoSyncFailure(notice);

    public static bool IsAutoSyncFailure(Notice notice) => notice.ReasonKey?.StartsWith("autosync-failed", StringComparison.Ordinal) == true;

    public static bool IsRetryNotice(Notice notice) => IsPairRetryNotice(notice) || notice.ReasonKey is "sync-retry" or "deletion-review" or "planning-degraded" or "recovery-unavailable" || IsRemovalAttention(notice) || IsRemovalReview(notice);

    public static bool IsRetryChoice(string choice) => choice is RetryNow or Exclude or Ignore or DeleteReviewed or KeepRestore or OpenFolderSettings or OpenRecoverySettings or Dismiss or ReviewRemoval or ReviewRemovalA or ReviewRemovalB or ConfirmRemoval or RemovalNextPage or RemovalPreviousPage;
    public const string QuarantineRetry = "quarantine-retry";
    public const string QuarantineIgnore = "quarantine-ignore";
    public const string QuarantineExclude = "quarantine-exclude";
    public const string QuarantineDeleteDropbox = "quarantine-delete-dropbox";
    public const string DefenderExclude = "defender-exclude";
    public const string DefenderVerify = "defender-verify";
    public const string DefenderKeep = "defender-keep";
    public const string DefenderProceed = "defender-proceed-deletions";
    public const string DefenderHold = "defender-keep-held";
    public const string DefenderReleaseHeld = "defender-release-held";
    public const string DefenderReleaseItem = "defender-release-item";
    public const string DefenderExcludeHeld = "defender-exclude-held";
    public const string DefenderReleaseAllPairs = "defender-release-all-pairs";
    public const string DefenderKeepShared = "defender-keep-shared-held";
    public static bool IsQuarantineChoice(string choice) => choice is QuarantineRetry or QuarantineIgnore or
        QuarantineExclude or QuarantineDeleteDropbox or DefenderExclude or DefenderVerify or DefenderKeep or DefenderProceed or DefenderHold ||
        IsHeldChoice(choice) || choice is DefenderReleaseAllPairs or DefenderKeepShared;

    /// <summary>The standing "Held by your choice" notice's choices.</summary>
    public static bool IsHeldChoice(string choice) => choice is DefenderReleaseHeld or DefenderReleaseItem or DefenderExcludeHeld;

    /// <summary>Held-notice choices that act on one named item; the request's path names that item, not the notice.</summary>
    public static bool IsHeldItemChoice(string choice) => choice is DefenderReleaseItem or DefenderExcludeHeld;
    /// <summary>Only ordinary item exclusions may outlive the requesting client while waiting for a run.</summary>
    public static bool CanQueueExclusion(Notice notice, string? path, string? choice) =>
        notice.RetainedVersion is null && choice is Ignore or Exclude &&
        !IsPairRetryNotice(notice) && IsRetryNotice(notice) &&
        !string.IsNullOrEmpty(path) && PairPath.Same(notice.Path, path) &&
        Options(notice).Any(option => option.Choice == choice);


    public static bool IsRetainedVersionChoice(string choice) =>
        choice is UseOtherVersion or SaveOtherVersionAsCopy or Dismiss;

    public static bool IsExplicitNoticeChoice(string choice)
    {
        var name = choice.Split('=', 2)[0].ToLowerInvariant();
        return JournalSizing.IsChoice(name) || IsRetainedVersionChoice(name) || IsRetryChoice(name) || IsQuarantineChoice(name) ||
            name is Compare or Merge or KeepA or KeepB or KeepBoth or RenameIncoming or ReplaceExisting or RestoreExisting or RestoreAsCopy;
    }

    public const string ReviewRebind = "review-rebind";
    public const string PausePair = "pause-pair";
    public const string LeaveAsIs = "leave-as-is";
    public const string RebindPending = "pending: rebind arrives with the central registry (B-191)";

    public static IReadOnlyList<SyncNoticeOption> Options(Notice notice) =>
        notice.Storage is not null
            ? [new(RetryNow, "Retry verification / settlement"), new(Ignore, "Ignore (leave unchanged)")]
            : JournalSizing.IsNotice(notice)
            ? [new(JournalSizing.Enlarge, "Enlarge (UAC)"), new(JournalSizing.NotNow, "Not now"), new(JournalSizing.Never, "Never")]
            : notice.DefenderHistory is { Cause: DefenderReview.SharedReleaseCause, SharedRelease: { } shared }
            ? [new(DefenderReleaseAllPairs, "Release in all pairs (also held by: " + (shared.OtherPairNames.Length == 0 ? "no other pairs" : shared.OtherPairNames) + ")"),
               new(DefenderKeepShared, "Keep held")]
            : notice.DefenderHistory is { Cause: DefenderReview.HeldCause }
            ? [new(DefenderReleaseHeld, "Release all (allow these deletions)"), new(DefenderReleaseItem, "Review release of one item…"),
               new(DefenderExcludeHeld, "Stop syncing one item (exclude; keep both copies)…")]
            : notice.DefenderHistory is not null
            ? [new(DefenderProceed, "Proceed with deletions"), new(DefenderHold, "Keep held")]
            : notice.Quarantine is not null
            ? QuarantineOptions(notice.Quarantine)
            : AwaitsArrival(notice)
            ? [new(RestoreExisting, "Restore the replaced item"), new(RestoreAsCopy, "Restore as a copy"), new(Dismiss, "Dismiss")]
            : notice.ConflictVersions?.Incoming is not null
            ? [new(RenameIncoming, "Rename incoming item"), new(ReplaceExisting, "Replace existing item (Recovery first)"), new(Dismiss, "Dismiss")]
            : notice.ConflictVersions is not null && notice.Rule == 27
            ? [new(Compare, "Compare"), new(Merge, "Merge text (keep originals)"),
               new(KeepA, "Keep this version (A)"), new(KeepB, "Keep that version (B)"), new(KeepBoth, "Keep both")]
            : notice.ConflictVersions is not null && notice.Rule is 10 or 11
            ? [new(KeepA, "Keep only at A's location"), new(KeepB, "Keep only at B's location"), new(KeepBoth, "Keep both")]
            : notice.RetainedVersion is not null
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
            : IsPairRetryNotice(notice)
            ? [new(RetryNow, "Retry now"), new(Ignore, "Ignore this notice; keep syncing")]
            : IsRemovalAttention(notice)
            ? [new(ReviewRemoval, "Review removal"), new(OpenFolderSettings, "Open folder settings"), new(Dismiss, "Dismiss")]
            : notice.ReasonKey == "deletion-review" && notice.Rule == DeletionReviewRestoreOnlyRule
            ? [new(KeepRestore, "Keep / restore"), new(Ignore, "Ignore; exclude this path from sync")]
            : notice.ReasonKey == "deletion-review"
            ? [new(DeleteReviewed, "Delete with recovery"), new(KeepRestore, "Keep / restore"), new(Ignore, "Ignore; exclude this path from sync")]
            : notice.ReasonKey == "recovery-unavailable"
            ? [new(RetryNow, "Retry now"), new(OpenRecoverySettings, "Recovery settings")]
            : notice.ReasonKey == "planning-degraded"
            ? [new(RetryNow, "Retry now"), new(OpenFolderSettings, "Details / folder settings")]
            : IsRetryNotice(notice)
            ? [new(RetryNow, "Retry now"), new(Exclude, "Exclude from sync"), new(Ignore, "Ignore; exclude this path from sync")]
            : [];

    private static IReadOnlyList<SyncNoticeOption> QuarantineOptions(QuarantineNotice hold)
    {
        var options = new List<SyncNoticeOption> { new(QuarantineRetry, "Retry this item once"),
            new(QuarantineIgnore, "Ignore notice; keep held"), new(QuarantineExclude, "Exclude from sync; keep both copies") };
        if (hold.ConflictingGrantId is not null)
        {
            if ((hold.ExpectedTarget ?? hold.Source)?.Kind == ItemKind.File)
                options.Add(new(DefenderKeep, "Keep the Defender exclusion for the new file"));
            options.Add(new(DefenderVerify, "Verify exclusion removal with admin (read-only UAC)"));
            return options;
        }
        if ((hold.ExpectedTarget ?? hold.Source)?.Kind == ItemKind.File)
        {
            options.Add(new(QuarantineDeleteDropbox, "Delete reviewed Dropbox file (recoverable in Dropbox history)"));
            options.Add(new(DefenderExclude, "Exclude exact file AND deterministic sibling download stage from Defender (Windows approval required)"));
        }
        return options;
    }

    public static string Result(string choice) => choice switch
    {
        OpenRecoverySettings => "Open Settings and choose an available Recovery location. No files changed; paused operations remain retryable.",
        ReviewRemoval or ReviewRemovalA or ReviewRemovalB => "Frozen removal review saved. Inspect its counts and pages in notices; nothing was deleted.",
        ConfirmRemoval => "Removal confirmation processed. Refresh notices for completion or held changes; protected content stays.",
        RemovalNextPage or RemovalPreviousPage => "Review page shown (at a boundary, the current page stays); nothing was deleted.",
        JournalSizing.Enlarge => "Windows change-history resize completed; sync continues.",
        JournalSizing.NotNow => "Deferred for seven days; sync continues with safe full-scan fallback.",
        JournalSizing.Never => "Automatic resize notices disabled for this volume; sync continues.",
        OpenFolderSettings => "Open this pair's folder settings to inspect selection and ignore rules. The notice remains open; no files changed.",
        RetryNow => "Retry requested. Existing evidence and deletion safeguards remain in force.",
        Exclude => "Excluded from future sync on both sides; no files were deleted.",
        Ignore => "Excluded this path from future sync on both sides; no files were changed or deleted.",
        Merge => "Saved a clean three-way merged file; all original versions remain unchanged.",
        KeepA or KeepB => "Kept the chosen version/location; removed files remain recoverable.",
        KeepBoth => "Kept both; no files changed.",
        RenameIncoming => "Renamed the incoming item; the existing destination is unchanged.",
        ReplaceExisting => "Replaced the existing item; its previous content remains recoverable.",
        RestoreAsCopy => "Restored a separate copy; the current item is unchanged.",
        RestoreExisting => "Restored the replaced item from Recovery.",
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
