namespace Massdex.SyncShark.V2;

/// <summary>The named choices a user can apply to a v2 notice; the one home for the vocabulary.</summary>
public static class NoticeChoices
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

    private static IReadOnlyList<SyncNoticeOption> ExportOptions(Notice notice)
    {
        var formats = notice.ExportInfo is { } info ? info.Formats : LegacyExportFormats;
        return formats.Select(format => (Format: format, Choice: ExportChoice(format)))
            .Where(item => item.Choice is not null)
            .Select(item => new SyncNoticeOption(item.Choice!, "Export as " + ExportLabel(item.Format) + "…")).ToArray();
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
        notice.ReasonKey is not null && notice.ReasonKey.StartsWith(SqliteStateStore.IndexFailurePrefix, StringComparison.Ordinal);

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

    public static bool IsRetryNotice(Notice notice) => IsRecoveryRestoreNotice(notice) || notice.RetainedVersion is null && (notice.FreeSpace is not null || notice.Repair is not null || notice.RebuildableRule is not null || notice.ReasonKey is ExecutionReasons.ConflictCopyBlocked or "folder-delete-unrestored" or "held-intents" or "permanent-boundary" or "plan-bound-exceeded" or "deletion-history-unavailable" or "deletion-evidence-pending" or "links-excluded" or "link-item" or "stubs-left-alone" or "online-only-item" or "unreadable" or ItemBlocked or "dropbox-storage-full" or "export-required" or RehashProtection.Reason or PointerObservationSummary || IsPairRetryNotice(notice) || IsIndexFailure(notice) || notice.ReasonKey is SqliteStateStore.DeleteReviewSummary or SqliteStateStore.UncertainDeleteSummary or SqliteStateStore.BlockedDeleteSummary or DeleteSourceGuard.ReasonKey or "sync-retry" or "content-unconfirmed" or "interrupted-delete-unverified" or "deletion-review" or "planning-degraded" or "recovery-unavailable" || IsRemovalAttention(notice) || IsRemovalReview(notice));

    /// <summary>The combined background-verification summary (M3), including the legacy held-intents summary.</summary>
    internal static bool IsVerificationSummary(Notice notice) =>
        notice.ReasonKey == RehashProtection.Reason && notice.Rule == RehashProtection.Rule ||
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
        Options(notice).Any(option => option.Choice == choice);

    public const string ItemBlocked = "item-blocked";

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

    /// <summary>
    /// Recovery restore choices. A frozen intent means bytes may already exist: only Retry and Ignore are offered,
    /// because Ignore pauses retries and keeps both sides protected, and nothing here may abandon a possibly
    /// written effect. An actionless failure wrote nothing, so Retry re-observes, Restore as a copy is offered when
    /// a destination was chosen, and Ignore/Dismiss both close the notice while the durable Recovery record stays.
    /// </summary>
    private static IReadOnlyList<SyncNoticeOption> RecoveryRestoreOptions(Notice notice)
    {
        if (RecoveryRestoreHold.HasFrozenIntent(notice.ConflictOperation))
            return [new(RetryNow, "Retry Recovery restore"),
                    new(Ignore, "Pause Recovery retries (keep both sides protected)")];
        var options = new List<SyncNoticeOption> { new(RetryNow, "Retry Recovery restore") };
        if (!string.IsNullOrEmpty(notice.ConflictOperation?.Destination))
            options.Add(new(RestoreAsCopy, "Restore as a copy"));
        // Ignore stays visible because notice-choice validation requires the rendered set to contain the choice;
        // on an actionless failure both Ignore and Dismiss close (nothing was written, so no hold is retained).
        options.Add(new(Ignore, "Ignore this notice (no file changes)"));
        options.Add(new(Dismiss, "Dismiss (no file changes)"));
        return options;
    }

    private static IReadOnlyList<SyncNoticeOption> FolderRestoreOptions(Notice notice)
    {
        var options = new List<SyncNoticeOption> { new(RetryNow, "Verify again (unknown restores are not replayed)") };
        if (notice.FolderRecoveryAnchor is not null)
            options.Add(new(RestoreAgain, "Restore again (authorise the same revision restore)"));
        options.Add(new(OpenDropbox, "Open Dropbox to review deleted files / version history"));
        options.Add(new(Ignore, "Ignore this notice (keep unresolved recovery protected)"));
        return options;
    }

    /// <summary>Explicit exclusion is separate from Ignore. Only item retry routes with an existing
    /// exclusion capability offer it; summaries and frozen restore authority cannot be abandoned.</summary>
    public static bool CanStopSyncing(Notice notice) =>
        notice.Path.Length != 0 && IsRetryNotice(notice) && !IsPairRetryNotice(notice) &&
        !IsRecoveryRestoreNotice(notice) && notice.RebuildableRule is null && notice.Storage is null &&
        notice.ReasonKey is not ("permanent-boundary" or "folder-delete-unrestored" or "plan-bound-exceeded" or DropboxQuota.NoticeKey) &&
        OptionsCore(notice).Any(option => option.Choice == Ignore);

    /// <summary>The one rendered catalogue for both CLI and UI.</summary>
    public static IReadOnlyList<SyncNoticeOption> Options(Notice notice)
    {
        var options = OptionsCore(notice).Select(option => IsIgnoreChoice(option.Choice)
            ? option with { Label = notice.Quarantine is not null
                ? "Ignore this cause (keep protection)" : "Ignore this cause (keep retrying safely)" } : option).ToList();
        if (CanStopSyncing(notice) && options.All(option => option.Choice != Exclude))
            options.Add(new(Exclude, "Stop syncing this file"));
        return options;
    }

    private static IReadOnlyList<SyncNoticeOption> OptionsCore(Notice notice) =>
        notice.ReasonKey == "effects-ledger-incomplete"
            ? [new(RetryNow, "Retry sync (does not repair the prior export)"), new(Ignore, "Ignore this cause"),
               new(RepairHelp, "Check destination permissions / space; rerun with a new export path")]
            : notice.ReasonKey == "plan-bound-exceeded"
            ? [new(RetryNow, "Retry with the same plan bound"), new(ApprovePlanSize, "Approve this plan size"),
               new(Ignore, "Ignore this cause"), new(RepairHelp, "Review preview / provenance and the plan bound")]
            : (notice.ReasonKey is SqliteStateStore.BlockedDeleteSummary or SqliteStateStore.UncertainDeleteSummary or SqliteStateStore.DeleteReviewSummary)
            ? [new(ShowRemaining, "Show remaining blocked deletions (50 per page)")]
            : IsRecoveryRestoreNotice(notice)
            ? RecoveryRestoreOptions(notice)
            : notice.ReasonKey == "folder-delete-unrestored"
            ? FolderRestoreOptions(notice)
            : notice.ReasonKey == "held-intents"
            ? [new(ShowRemaining, "Show remaining changes (50 per page)")]
            : notice.ReasonKey == RehashProtection.Reason
            ? [new(ShowRemaining, "Show remaining changes (50 per page)")]
            : notice.ReasonKey == "permanent-boundary"
            ? [new(Ignore, "Ignore this information (keep boundary protected)")]
            : notice.Storage is not null
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
            : notice.Repair is not null && notice.ReasonKey is "provider-name-refused" or "windows-name-invalid"
            ? notice.Repair.Destination is null
                ? [new(Rename, "Rename…", RequestsName: true), new(Ignore, "Ignore this cause"), new(RetryNow, "Retry this item")]
                : [new(Rename, "Resume / verify rename"), new(RepairHelp, "Rename awaiting sync"), new(RetryNow, "Retry this item"), new(Ignore, "Ignore this cause")]
            : notice.FreeSpace is not null
            ? [new(OpenStorage, "Open storage"), new(ReviewReserve, "Review the reserve"),
               new(OpenRecoverySettings, "Choose another Recovery location"), new(OpenFolderSettings, "Review destination folder"),
               new(RetryNow, "Check space again"), new(Ignore, "Ignore this cause")]
            : notice.RebuildableRule is not null
            ? [new(RetryNow, "Check again"), new(PreviewMatches, "Preview matches"), new(ChooseFolder, "Choose folder…", RequestsName: true),
               new(RemoveRule, "Remove rule"), new(KeepRule, "Keep rule (ignore this notice)")]
            : notice.ReasonKey == "deletion-evidence-pending"
            ? [new(CheckHistoryAgain, "Check history again"), new(ReviewDeletion, "Review deletion evidence"),
               new(Ignore, "Ignore this cause; keep this copy protected")]
            : notice.ReasonKey == "links-excluded"
            ? [new(ShowRemaining, "Review links (50 per page; optional cursor)", RequestsName: true)]
            : notice.ReasonKey == "link-item"
            ? [new(ShowTarget, "Show target"), new(SyncTargetSeparately, "Sync target separately"),
               new(CopyLink, "Create regular copy"), new(CopyLinkOutsideRoot, "Create regular copy — confirm target access (exact target path)", RequestsName: true),
               new(CopyLinkAgain, "Copy again (exact target path if excluded/outside)", RequestsName: true),
               new(RemoveLinkStaging, "Remove staging (manual cleanup guidance)"),
               new(RetryNow, "Retry copy (rebuild; re-confirm excluded target)"), new(Ignore, "Ignore this cause; leave link protected")]
            : notice.ReasonKey == "stubs-left-alone"
            ? [new(ShowRemaining, "Review online-only files (50 per page)"), new(RetryNow, "Check again"), new(Ignore, "Leave all online-only")]
            : notice.ReasonKey == "online-only-item"
            ? [new(notice.Repair?.Item.Size > OnlineOnlyHydration.ConfirmationBytes && notice.Detail.StartsWith("This downloads ", StringComparison.Ordinal) ? ConfirmLargeDownload : MakeAvailableOffline,
                   notice.Repair?.Item.Size > OnlineOnlyHydration.ConfirmationBytes && notice.Detail.StartsWith("This downloads ", StringComparison.Ordinal) ? "Confirm large download" : "Make available offline"), new(Ignore, "Leave online-only"),
               new(RetryNow, "Check this file again")]
            : notice.ReasonKey == DropboxQuota.NoticeKey
            ? [new(RetryNow, "Retry uploads"), new(Ignore, "Ignore; keep uploads suspended")]
            : notice.ReasonKey == "export-required"
            ? [.. ExportOptions(notice), new(OpenDropbox, "Open in Dropbox"), new(RepairHelp, "Why / repair guidance"),
               new(RetryNow, "Retry this item"), new(Ignore, "Ignore this cause")]
            : notice.FolderInspection
            ? [new(ShowRemaining, "Show remaining items"), new(RetryNow, "Review removal again"), new(Ignore, "Ignore this cause; keep folder protected")]
            : IsRemovalAttention(notice)
            ? [new(RetryNow, "Retry"), new(ReviewRemoval, "Review again"), new(OpenFolderSettings, "Open folder settings"), new(Dismiss, "Dismiss")]
            : notice.ReasonKey == "deletion-review" && notice.Rule == DeletionReviewRestoreOnlyRule
            ? [new(KeepRestore, "Keep / restore"), new(Ignore, "Ignore this cause")]
            : notice.ReasonKey == "deletion-review"
            ? [new(DeleteReviewed, "Delete with recovery"), new(KeepRestore, "Keep / restore"), new(Ignore, "Ignore this cause")]
            : notice.ReasonKey == "recovery-unavailable"
            ? [new(RetryNow, "Retry now"), new(OpenRecoverySettings, "Recovery settings"), new(Ignore, "Ignore this cause")]
            : notice.ReasonKey == ExecutionReasons.ConflictCopyBlocked
            ? [new(RetryNow, "Retry keep-both copy")]
            : notice.ReasonKey == "planning-degraded"
            ? [new(RetryNow, "Retry now"), new(OpenFolderSettings, "Details / folder settings"), new(Ignore, "Ignore this cause")]
            // A pair-level transient summary has no one path to exclude: Retry makes its durable debts due again,
            // Ignore silences the summary while every per-entity retry keeps running.
            : notice.ReasonKey == PointerObservationSummary
            ? [new(RetryNow, "Retry now"), new(Ignore, "Ignore; keep retrying silently")]
            // Retry makes the root's durable deadlines due now; Ignore hides this episode only. Neither pauses
            // retries nor excludes a path, and only verified success clears the failure evidence.
            : IsIndexFailure(notice)
            ? [new(RetryNow, "Retry indexing now"), new(Ignore, "Ignore this episode; indexing keeps retrying silently")]
            : IsRetryNotice(notice)
            ? [new(RepairHelp, "Why / repair guidance"), new(RetryNow, "Retry this item"),
               new(Ignore, "Ignore this cause")]
            : [];

    // Security exception: quarantine Retry is explicit only. Automatically re-reading quarantined
    // bytes may re-trigger antivirus; Ignore suppresses presentation while the file stays held/unsynced.
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
        JournalSizing.Enlarge => "Windows change-history resize completed; sync continues.",
        JournalSizing.NotNow => "Deferred for seven days; sync continues with safe full-scan fallback.",
        JournalSizing.Never => "Automatic resize notices disabled for this volume; sync continues.",
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
