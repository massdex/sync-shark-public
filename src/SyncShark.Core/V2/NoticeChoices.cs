namespace Massdex.SyncShark.V2;

/// <summary>The named choices a user can apply to a v2 notice; the one home for the vocabulary.</summary>
public static partial class NoticeChoices
{
    /// <summary>Agree with what was done; no file change.</summary>
    public const string Accept = "accept";

    /// <summary>Dismiss the notice without agreeing; no file change.</summary>
    public const string Ignore = "ignore";

    /// <summary>Ignore suppresses this cause only; work, protection and retry remain unchanged.</summary>
    public static bool IsIgnoreChoice(string choice) => choice is Ignore or QuarantineIgnore;

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
    public const string RepairHelp = "repair-help";
    public const string ApprovePlanSize = "approve-plan-size";
    public const string Rename = "rename";
    public const string ShowRemaining = "show-remaining-items";
    public const string OpenDropbox = "open-in-dropbox";
    public const string ShowTarget = "show-target";
    public const string SyncTargetSeparately = "sync-target-separately";
    public const string CopyLinkAgain = "create-regular-copy-again";
    public const string RemoveLinkStaging = "remove-link-staging";
    public const string CopyLink = "create-regular-copy";
    public const string CopyLinkOutsideRoot = "create-regular-copy-confirm-outside-root";
    public const string MakeAvailableOffline = "make-available-offline";
    public const string ConfirmLargeDownload = "confirm-large-download";
    public const string ExportDocx = "export-docx";
    public const string ExportMarkdown = "export-markdown";
    public const string ExportPdf = "export-pdf";
    public const string ExportHtml = "export-html";

    // A notice written before export_info was read carries no ExportInfo; these are its legacy buttons only.
    private static readonly IReadOnlyList<string> LegacyExportFormats = ["docx", "markdown", "pdf"];

    /// <summary>The provider format one export choice names, or null when the choice is not an export action.</summary>
    public static string? ExportFormat(string choice) => choice switch
    {
        ExportDocx => "docx",
        ExportMarkdown => "markdown",
        ExportPdf => "pdf",
        ExportHtml => "html",
        _ => null
    };

    /// <summary>The choice for one provider export format; an unknown format has no safe button.</summary>
    public static string? ExportChoice(string format) => format.ToLowerInvariant() switch
    {
        "docx" => ExportDocx,
        "markdown" => ExportMarkdown,
        "pdf" => ExportPdf,
        "html" => ExportHtml,
        _ => null
    };

    private static string ExportLabel(string format) => format.ToLowerInvariant() switch
    {
        "docx" => "DOCX",
        "markdown" => "Markdown",
        "pdf" => "PDF",
        "html" => "HTML",
        _ => format
    };

    private static IReadOnlyList<(string Choice, string Label, bool RequestsName)> ExportOptions(Notice notice)
    {
        var formats = notice.ExportInfo is { } info ? info.Formats : LegacyExportFormats;
        return formats.Select(format => (Format: format, Choice: ExportChoice(format)))
            .Where(item => item.Choice is not null)
            .Select(item => (item.Choice!, "Export as " + ExportLabel(item.Format) + "…", false)).ToArray();
    }

    public const string CheckHistoryAgain = "check-history-again";
    public const string ReviewDeletion = "review-deletion";
    public const string OpenStorage = "open-storage";
    public const string ReviewReserve = "review-reserve";
    public const string PreviewMatches = "preview-matches";
    public const string ChooseFolder = "choose-folder";
    public const string RemoveRule = "remove-rebuildable-rule";
    public const string KeepRule = "keep-rebuildable-rule";

    /// <summary>One rebuildable rule whose scoped prefix matched no folder during prepare. Identity is the
    /// category and prefix stored on the notice, not its path.</summary>
    public const string RebuildableRuleKey = "rebuildable-prefix-unmatched";
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

    public static bool IsPairRetryNotice(Notice notice) => notice.ReasonKey is "run-history-unavailable" or "run-unavailable" or "store-maintenance" or "autosync-watch-unavailable" or "effects-ledger-incomplete" || IsAutoSyncFailure(notice);

    /// <summary>The one physical root-indexing notice, whichever pair (or projection) is showing it.</summary>
    public static bool IsIndexFailure(Notice notice) =>
        notice.ReasonKey is not null && notice.ReasonKey.StartsWith(NoticeVocabulary.IndexFailurePrefix, StringComparison.Ordinal);

    /// <summary>A root-indexing retry made every durable deadline due; nothing was excluded or paused.</summary>
    public const string IndexRetryResult =
        "Indexing retry requested. Every durable indexing deadline is due now; protected files stay protected " +
        "and no path was excluded.";

    public static bool IsAutoSyncFailure(Notice notice) => notice.ReasonKey?.StartsWith("autosync-failed", StringComparison.Ordinal) == true;

    /// <summary>One pair summary standing for many transient online-only metadata observations. A distinct reason
    /// so it can never intercept an unrelated empty-path root scan failure.</summary>
    public const string PointerObservationSummary = "pointer-observation-summary";

    public static bool IsRecoveryRestoreNotice(Notice notice) =>
        notice.ReasonKey is "recovery-restore-pending" or "recovery-restore-ready";

    public static bool IsRetryNotice(Notice notice) => PlanNotices.NeedsObservation(notice) || IsRecoveryRestoreNotice(notice) || notice.RetainedVersion is null && (notice.FreeSpace is not null || notice.Repair is not null || notice.RebuildableRule is not null || notice.ReasonKey is ExecutionReasons.ConflictCopyBlocked or "folder-delete-unrestored" or "held-intents" or "permanent-boundary" or "plan-bound-exceeded" or "deletion-history-unavailable" or "deletion-evidence-pending" or "links-excluded" or "link-item" or "stubs-left-alone" or "online-only-item" or ItemBlocked or "dropbox-storage-full" or "export-required" or NoticeVocabulary.RehashReason or PointerObservationSummary || IsPairRetryNotice(notice) || IsIndexFailure(notice) || notice.ReasonKey is NoticeVocabulary.DeleteReviewSummary or NoticeVocabulary.UncertainDeleteSummary or NoticeVocabulary.BlockedDeleteSummary or NoticeVocabulary.DeleteSourceReasonKey or "sync-retry" or "content-unconfirmed" or InterruptedDelete.UnverifiedReason or "deletion-review" or "planning-degraded" or "recovery-unavailable" || IsRemovalAttention(notice) || IsRemovalReview(notice) || IsGenericFailure(notice));

    /// <summary>A failure with no structured payload and no more specific owner. A new or unknown reason still
    /// gets the generic why/retry/ignore notice instead of rendering with no choices, and the retry/ignore
    /// dispatchers can act on it. Decision notices keep their own empty catalogue.</summary>
    public static bool IsGenericFailure(Notice notice)
    {
        var reason = notice.ReasonKey;
        // Outcome kinds retain their existing acknowledgement path even when a legacy/custom reason is unknown.
        // Only informational problem notices are eligible for the unknown-failure fallback.
        return notice.Kind == NoticeKind.Info && notice.Storage is null && notice.Quarantine is null &&
            notice.DefenderHistory is null && notice.RetainedVersion is null && notice.ConflictVersions is null &&
            notice.ConflictOperation is null && notice.Repair is null && notice.RebuildableRule is null &&
            notice.FreeSpace is null && reason != "permanent-boundary" &&
            reason != PlanNotices.CloudFilesBindingNeedsReview.Key &&
            reason != PlanNotices.OutsidePairArea.Key && reason != PlanNotices.PairExcluded.Key &&
            reason != PlanNotices.StubWithoutCounterpart.Key && reason != "name-mapping-collision" &&
            !PlanNotices.AwaitsAcknowledgement(notice) &&
            !NoticeVocabulary.IsJournalNotice(notice) && !IsRecoveryRestoreNotice(notice) &&
            !IsRemovalReview(notice) && !IsRemovalAttention(notice) && !IsPairRetryNotice(notice) && !IsIndexFailure(notice);
    }

    /// <summary>The combined background-verification summary (M3), including the legacy held-intents summary.</summary>
    internal static bool IsVerificationSummary(Notice notice) =>
        notice.ReasonKey == NoticeVocabulary.RehashReason && notice.Rule == NoticeVocabulary.RehashRule ||
        notice.ReasonKey == "held-intents";

    // RestoreAsCopy is a retry-family choice: it re-runs the same frozen Recovery selection with a copy destination,
    // so it must route through the notice-choice path rather than the ordinary conflict resolver.
    public static bool IsRetryChoice(string choice) => choice is ApprovePlanSize or RestoreAgain or ShowTarget or SyncTargetSeparately or CopyLink or CopyLinkOutsideRoot or CopyLinkAgain or RemoveLinkStaging or ConfirmLargeDownload or PreviewMatches or ChooseFolder or RemoveRule or KeepRule or ExportHtml or CheckHistoryAgain or ReviewDeletion or OpenStorage or ReviewReserve or ExportDocx or ExportMarkdown or ExportPdf or MakeAvailableOffline or Rename or ShowRemaining or OpenDropbox or RepairHelp or RetryNow or Exclude or Ignore or DeleteReviewed or KeepRestore or OpenFolderSettings or OpenRecoverySettings or Dismiss or RestoreAsCopy or ReviewRemoval or ReviewRemovalA or ReviewRemovalB or ConfirmRemoval or RemovalNextPage or RemovalPreviousPage;
    public const string RestoreAgain = "restore-again";
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
        notice.RetainedVersion is null && choice == Exclude && CanStopSyncing(notice) &&
        !IsPairRetryNotice(notice) && !IsRecoveryRestoreNotice(notice) &&
        notice.ReasonKey is not ("permanent-boundary" or "folder-delete-unrestored") && IsRetryNotice(notice) &&
        !string.IsNullOrEmpty(path) && PairPath.Same(notice.Path, path) &&
        IsAdmissible(notice, choice);

    public const string ItemBlocked = "item-blocked";

    public static bool IsRetainedVersionChoice(string choice) =>
        choice is UseOtherVersion or SaveOtherVersionAsCopy or Dismiss;

    public static bool IsExplicitNoticeChoice(string choice)
    {
        var name = choice.Split('=', 2)[0].ToLowerInvariant();
        return NoticeVocabulary.IsJournalChoice(name) || IsRetainedVersionChoice(name) || IsRetryChoice(name) || IsQuarantineChoice(name) ||
            name is Compare or Merge or KeepA or KeepB or KeepBoth or RenameIncoming or ReplaceExisting or RestoreExisting or RestoreAsCopy;
    }

    public const string ReviewRebind = "review-rebind";
    public const string PausePair = "pause-pair";
    public const string LeaveAsIs = "leave-as-is";
    public const string RebindPending = "pending: rebind arrives with the central registry (B-191)";

    /// <summary>
    /// Recovery restore choices. A frozen intent means bytes may already exist: only Retry and Ignore are offered,
    /// because Ignore pauses retries and keeps both sides protected, and nothing here may abandon a possibly
    /// written effect. An actionless failure wrote nothing, so Retry re-observes, Restore as a copy is offered when
    /// a destination was chosen, and Ignore/Dismiss both close the notice while the durable Recovery record stays.
    /// </summary>
    private static IReadOnlyList<(string Choice, string Label, bool RequestsName)> RecoveryRestoreOptions(Notice notice)
    {
        if (RecoveryRestoreHold.HasFrozenIntent(notice.ConflictOperation))
            return [(RetryNow, "Retry Recovery restore", false),
                    (Ignore, "Pause Recovery retries (keep both sides protected)", false)];
        var options = new List<(string Choice, string Label, bool RequestsName)> { (RetryNow, "Retry Recovery restore", false) };
        if (!string.IsNullOrEmpty(notice.ConflictOperation?.Destination))
            options.Add((RestoreAsCopy, "Restore as a copy", false));
        // Ignore stays visible because notice-choice validation requires the rendered set to contain the choice;
        // on an actionless failure both Ignore and Dismiss close (nothing was written, so no hold is retained).
        options.Add((Ignore, "Ignore this notice (no file changes)", false));
        options.Add((Dismiss, "Dismiss (no file changes)", false));
        return options;
    }

    private static IReadOnlyList<(string Choice, string Label, bool RequestsName)> FolderRestoreOptions(Notice notice)
    {
        var options = new List<(string Choice, string Label, bool RequestsName)> { (RetryNow, "Verify again (unknown restores are not replayed)", false) };
        if (notice.FolderRecoveryAnchor is not null)
            options.Add((RestoreAgain, "Restore again (authorise the same revision restore)", false));
        options.Add((OpenDropbox, "Open Dropbox to review deleted files / version history", false));
        options.Add((Ignore, "Ignore this notice (keep unresolved recovery protected)", false));
        return options;
    }

    /// <summary>Explicit exclusion is separate from Ignore. Only item retry routes with an existing
    /// exclusion capability offer it; summaries and frozen restore authority cannot be abandoned.</summary>
    public static bool CanStopSyncing(Notice notice) =>
        notice.Path.Length != 0 && IsRetryNotice(notice) && !IsPairRetryNotice(notice) &&
        !IsRecoveryRestoreNotice(notice) && notice.RebuildableRule is null && notice.Storage is null &&
        notice.ReasonKey is not ("permanent-boundary" or "folder-delete-unrestored" or "plan-bound-exceeded" or ExecutionReasons.ConflictCopyBlocked or DropboxQuota.NoticeKey) &&
        CatalogueCore(notice).Any(option => option.Choice == Ignore);

    /// <summary>Admission uses the final catalogue with exact, ordinal choice identity.</summary>
    public static bool IsAdmissible(Notice notice, string? choice) =>
        choice is not null && Catalogue(notice).Any(option => string.Equals(option.Choice, choice, StringComparison.Ordinal));

    private static IReadOnlyList<(string Choice, string Label, bool RequestsName)> CatalogueCore(Notice notice) =>
        notice.ReasonKey == "effects-ledger-incomplete"
            ? [(RetryNow, "Retry sync (does not repair the prior export)", false), (Ignore, "Ignore this cause", false),
               (RepairHelp, "Check destination permissions / space; rerun with a new export path", false)]
            : notice.ReasonKey == "plan-bound-exceeded"
            ? [(RetryNow, "Retry with the same plan bound", false), (ApprovePlanSize, "Approve this plan size", false),
               (Ignore, "Ignore this cause", false), (RepairHelp, "Review preview / provenance and the plan bound", false)]
            : (notice.ReasonKey is NoticeVocabulary.BlockedDeleteSummary or NoticeVocabulary.UncertainDeleteSummary or NoticeVocabulary.DeleteReviewSummary)
            ? [(ShowRemaining, "Show remaining blocked deletions (50 per page)", false),
               (RetryNow, "Retry now", false), (Ignore, "Ignore this cause; keep the blocked work protected", false)]
            : IsRecoveryRestoreNotice(notice)
            ? RecoveryRestoreOptions(notice)
            : notice.ReasonKey == "folder-delete-unrestored"
            ? FolderRestoreOptions(notice)
            : notice.ReasonKey == "held-intents"
            ? [(ShowRemaining, "Show remaining changes (50 per page)", false), (RetryNow, "Retry now", false),
               (Ignore, "Ignore this cause", false)]
            : notice.ReasonKey == NoticeVocabulary.RehashReason
            ? [(ShowRemaining, "Show remaining changes (50 per page)", false), (RetryNow, "Retry now", false),
               (Ignore, "Ignore this cause", false)]
            : notice.ReasonKey == "permanent-boundary"
            ? [(Ignore, "Ignore this information (keep boundary protected)", false)]
            : notice.Storage is not null
            ? [(RetryNow, "Retry verification / settlement", false), (Ignore, "Ignore (leave unchanged)", false)]
            : NoticeVocabulary.IsJournalNotice(notice)
            ? [(NoticeVocabulary.JournalEnlarge, "Enlarge (UAC)", false), (NoticeVocabulary.JournalNotNow, "Not now", false), (NoticeVocabulary.JournalNever, "Never", false)]
            : notice.DefenderHistory is { Cause: NoticeVocabulary.DefenderSharedReleaseCause, SharedRelease: { } shared }
            ? [(DefenderReleaseAllPairs, "Release in all pairs (also held by: " + (shared.OtherPairNames.Length == 0 ? "no other pairs" : shared.OtherPairNames) + ")", false),
               (DefenderKeepShared, "Keep held", false)]
            : notice.DefenderHistory is { Cause: NoticeVocabulary.DefenderHeldCause }
            ? [(DefenderReleaseHeld, "Release all (allow these deletions)", false), (DefenderReleaseItem, "Review release of one item…", false),
               (DefenderExcludeHeld, "Stop syncing one item (exclude; keep both copies)…", false)]
            : notice.DefenderHistory is not null
            ? [(DefenderProceed, "Proceed with deletions", false), (DefenderHold, "Keep held", false)]
            : notice.Quarantine is not null
            ? QuarantineOptions(notice.Quarantine)
            : AwaitsArrival(notice)
            ? [(RestoreExisting, "Restore the replaced item", false), (RestoreAsCopy, "Restore as a copy", false), (Dismiss, "Dismiss", false)]
            : notice.ConflictVersions?.Incoming is not null
            ? [(RenameIncoming, "Rename incoming item", false), (ReplaceExisting, "Replace existing item (Recovery first)", false), (Dismiss, "Dismiss", false)]
            : notice.ConflictVersions is not null && notice.Rule == 27
            ? [(Compare, "Compare", false), (Merge, "Merge text (keep originals)", false),
               (KeepA, "Keep this version (A)", false), (KeepB, "Keep that version (B)", false), (KeepBoth, "Keep both", false)]
            : notice.ConflictVersions is not null && notice.Rule is 10 or 11
            ? [(KeepA, "Keep only at A's location", false), (KeepB, "Keep only at B's location", false), (KeepBoth, "Keep both", false)]
            : notice.RetainedVersion is not null
            ? [(UseOtherVersion, "Use the other version", false),
               (SaveOtherVersionAsCopy, "Save the other version as a copy", false), (Dismiss, "Dismiss", false)]
            : notice.Rule == 38 && notice.ReasonKey == PlanNotices.CloudFilesBindingNeedsReview.Key
            ? [(ReviewRebind, "Review / rebind to verified new path", false),
               (PausePair, "Pause the pair", false), (LeaveAsIs, "Leave as is", false)]
            : IsRemovalReview(notice)
            ? [(ConfirmRemoval, "Confirm removal with Recovery", false), (RemovalPreviousPage, "Previous page", false),
               (RemovalNextPage, "Next page", false), (ReviewRemoval, "Refresh review", false), (Dismiss, "Cancel review", false)]
            : IsRemovalAttention(notice) && notice.RemovalSides is null
            ? [(ReviewRemoval, "Review removal", false), (ReviewRemovalA, "Review removal on A (left)", false),
               (ReviewRemovalB, "Review removal on B (right)", false), (OpenFolderSettings, "Open folder settings", false), (Dismiss, "Dismiss", false)]
            : IsPairRetryNotice(notice)
            ? [(RetryNow, "Retry now", false), (Ignore, "Ignore this notice; keep syncing", false)]
            : notice.Repair is not null && notice.ReasonKey is "provider-name-refused" or "windows-name-invalid"
            ? notice.Repair.Destination is null
                ? [(Rename, "Rename…", true), (Ignore, "Ignore this cause", false), (RetryNow, "Retry this item", false)]
                : [(Rename, "Resume / verify rename", false), (RepairHelp, "Rename awaiting sync", false), (RetryNow, "Retry this item", false), (Ignore, "Ignore this cause", false)]
            : notice.FreeSpace is not null
            ? [(OpenStorage, "Open storage", false), (ReviewReserve, "Review the reserve", false),
               (OpenRecoverySettings, "Choose another Recovery location", false), (OpenFolderSettings, "Review destination folder", false),
               (RetryNow, "Check space again", false), (Ignore, "Ignore this cause", false)]
            : notice.RebuildableRule is not null
            ? [(RetryNow, "Check again", false), (PreviewMatches, "Preview matches", false), (ChooseFolder, "Choose folder…", true),
               (RemoveRule, "Remove rule", false), (KeepRule, "Keep rule (ignore this notice)", false)]
            : notice.ReasonKey == "deletion-evidence-pending"
            ? [(CheckHistoryAgain, "Check history again", false), (ReviewDeletion, "Review deletion evidence", false),
               (Ignore, "Ignore this cause; keep this copy protected", false)]
            : notice.ReasonKey == "links-excluded"
            ? [(ShowRemaining, "Review links (50 per page; optional cursor)", true)]
            : notice.ReasonKey == "link-item"
            ? [(ShowTarget, "Show target", false), (SyncTargetSeparately, "Sync target separately", false),
               (CopyLink, "Create regular copy", false), (CopyLinkOutsideRoot, "Create regular copy — confirm target access (exact target path)", true),
               (CopyLinkAgain, "Copy again (exact target path if excluded/outside)", true),
               (RemoveLinkStaging, "Remove staging (manual cleanup guidance)", false),
               (RetryNow, "Retry copy (rebuild; re-confirm excluded target)", false), (Ignore, "Ignore this cause; leave link protected", false)]
            : notice.ReasonKey == "stubs-left-alone"
            ? [(ShowRemaining, "Review online-only files (50 per page)", false), (RetryNow, "Check again", false), (Ignore, "Leave all online-only", false)]
            : notice.ReasonKey == "online-only-item"
            ? [(notice.Repair?.Item.Size > NoticeVocabulary.HydrationConfirmationBytes && notice.Detail.StartsWith("This downloads ", StringComparison.Ordinal) ? ConfirmLargeDownload : MakeAvailableOffline,
                   notice.Repair?.Item.Size > NoticeVocabulary.HydrationConfirmationBytes && notice.Detail.StartsWith("This downloads ", StringComparison.Ordinal) ? "Confirm large download" : "Make available offline", false), (Ignore, "Leave online-only", false),
               (RetryNow, "Check this file again", false)]
            : notice.ReasonKey == DropboxQuota.NoticeKey
            ? [(RetryNow, "Retry uploads", false), (Ignore, "Ignore; keep uploads suspended", false)]
            : notice.ReasonKey == "export-required"
            ? [.. ExportOptions(notice), (OpenDropbox, "Open in Dropbox", false), (RepairHelp, "Why / repair guidance", false),
               (RetryNow, "Retry this item", false), (Ignore, "Ignore this cause", false)]
            : notice.FolderInspection
            ? [(ShowRemaining, "Show remaining items", false), (RetryNow, "Review removal again", false), (Ignore, "Ignore this cause; keep folder protected", false)]
            : IsRemovalAttention(notice)
            ? [(RetryNow, "Retry", false), (ReviewRemoval, "Review again", false), (OpenFolderSettings, "Open folder settings", false), (Dismiss, "Dismiss", false)]
            : notice.ReasonKey == "deletion-review" && notice.Rule == DeletionReviewRestoreOnlyRule
            ? [(KeepRestore, "Keep / restore", false), (Ignore, "Ignore this cause", false)]
            : notice.ReasonKey == "deletion-review"
            ? [(DeleteReviewed, "Delete with recovery", false), (KeepRestore, "Keep / restore", false), (Ignore, "Ignore this cause", false)]
            : notice.ReasonKey == "recovery-unavailable"
            ? [(RetryNow, "Retry now", false), (OpenRecoverySettings, "Recovery settings", false), (Ignore, "Ignore this cause", false)]
            : notice.ReasonKey == ExecutionReasons.ConflictCopyBlocked
            ? [(RetryNow, "Retry keep-both copy", false), (Ignore, string.Empty, false)]
            : notice.ReasonKey == "planning-degraded"
            ? [(RetryNow, "Retry now", false), (OpenFolderSettings, "Details / folder settings", false), (Ignore, "Ignore this cause", false)]
            // A pair-level transient summary has no one path to exclude: Retry makes its durable debts due again,
            // Ignore silences the summary while every per-entity retry keeps running.
            : notice.ReasonKey == PointerObservationSummary
            ? [(RetryNow, "Retry now", false), (Ignore, "Ignore; keep retrying silently", false)]
            // Retry makes the root's durable deadlines due now; Ignore hides this episode only. Neither pauses
            // retries nor excludes a path, and only verified success clears the failure evidence.
            : IsIndexFailure(notice)
            ? [(RetryNow, "Retry indexing now", false), (Ignore, "Ignore this episode; indexing keeps retrying silently", false)]
            : IsRetryNotice(notice)
            ? [(RepairHelp, "Why / repair guidance", false), (RetryNow, "Retry this item", false),
               (Ignore, "Ignore this cause", false)]
            // Unknown failures enter through IsGenericFailure in IsRetryNotice; known information and
            // decisions retain their existing catalogue instead of acquiring undispatchable Retry buttons.
            : [];

    // Security exception: quarantine Retry is explicit only. Automatically re-reading quarantined
    // bytes may re-trigger antivirus; Ignore suppresses presentation while the file stays held/unsynced.
    private static IReadOnlyList<(string Choice, string Label, bool RequestsName)> QuarantineOptions(QuarantineNotice hold)
    {
        var options = new List<(string Choice, string Label, bool RequestsName)> { (QuarantineRetry, "Retry this item once", false),
            (QuarantineIgnore, "Ignore notice; keep held", false), (QuarantineExclude, "Exclude from sync; keep both copies", false) };
        if (hold.ConflictingGrantId is not null)
        {
            if ((hold.ExpectedTarget ?? hold.Source)?.Kind == ItemKind.File)
                options.Add((DefenderKeep, "Keep the Defender exclusion for the new file", false));
            options.Add((DefenderVerify, "Verify exclusion removal with admin (read-only UAC)", false));
            return options;
        }
        if ((hold.ExpectedTarget ?? hold.Source)?.Kind == ItemKind.File)
        {
            options.Add((QuarantineDeleteDropbox, "Delete reviewed Dropbox file (recoverable in Dropbox history)", false));
            options.Add((DefenderExclude, "Exclude exact file AND deterministic sibling download stage from Defender (Windows approval required)", false));
        }
        return options;
    }
    public const string IgnoreResult = "Notice ignored for this cause. The item stays protected and unsynced; retries continue. No exclusions changed.";

    public static string Result(string choice, Notice notice) => choice == Ignore && notice.ReasonKey == "permanent-boundary"
        ? "Information dismissed. The ownership boundary remains protected; no files were excluded, changed or marked synced."
        : choice == RepairHelp
        ? notice.Detail + " The notice stays open. Retry re-observes this item; Ignore suppresses this cause while protected retries continue. Stop syncing this file is a separate explicit choice."
        : Result(choice);

    public static string Result(string choice) => choice switch
    {
        OpenRecoverySettings => "Open Settings and choose an available Recovery location. No files changed; paused operations remain retryable.",
        ReviewRemoval or ReviewRemovalA or ReviewRemovalB => "Frozen removal review saved. Inspect its counts and pages in notices; nothing was deleted.",
        ConfirmRemoval => "Removal confirmation processed. Refresh notices for completion or held changes; protected content stays.",
        RemovalNextPage or RemovalPreviousPage => "Review page shown (at a boundary, the current page stays); nothing was deleted.",
        NoticeVocabulary.JournalEnlarge => "Windows change-history resize completed; sync continues.",
        NoticeVocabulary.JournalNotNow => "Deferred for seven days; sync continues with safe full-scan fallback.",
        NoticeVocabulary.JournalNever => "Automatic resize notices disabled for this volume; sync continues.",
        OpenFolderSettings => "Open this pair's folder settings to inspect selection and ignore rules. The notice remains open; no files changed.",
        RetryNow => "Retry requested. Existing evidence and deletion safeguards remain in force.",
        Exclude => "Excluded from future sync on both sides; no files were deleted.",
        Ignore => IgnoreResult,
        QuarantineIgnore => "Suppressed this cause only. The file remains held and unsynced; Retry is explicit to avoid re-triggering antivirus. No files were changed or deleted.",
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
        PreviewMatches => "Listed candidate folder paths under the current inventory. No files were changed.",
        ChooseFolder => "The rule's prefix was updated. Run prepare again to detect its folders; no files were changed.",
        RemoveRule => "Removed the rebuildable rule. No files were deleted or moved.",
        KeepRule => "Kept. This rule will not be raised again unless it changes; no files were changed.",
        ReviewRebind => RebindPending,
        PausePair => "Pair turned off for future sync. A current run may finish. Enable it in Folder Pairs to resume; files and bindings are unchanged.",
        LeaveAsIs => "Left as is. Files and bindings are unchanged.",
        _ => "Accepted"
    };

    /// <summary>The choices a notice of this kind allows now; a held decision allows none.</summary>
    public static IReadOnlyList<string> For(NoticeKind kind) =>
        kind == NoticeKind.UserDecision ? [] : [Accept, Ignore];
}
