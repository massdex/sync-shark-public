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
    public const string RepairHelp = "repair-help";
    public const string Rename = "rename";
    public const string ShowRemaining = "show-remaining-items";
    public const string OpenDropbox = "open-in-dropbox";
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

    /// <summary>Fallback reason key for item blockers without a specialized repair.</summary>
    public const string ItemBlocked = "item-blocked";

    public static bool IsRetryNotice(Notice notice) => notice.RetainedVersion is null &&
        (notice.FreeSpace is not null || notice.Repair is not null || notice.RebuildableRule is not null || notice.ReasonKey is "deletion-evidence-pending" or "stubs-left-alone" or "online-only-item" or "unreadable" or ItemBlocked or "dropbox-storage-full" or "export-required" or "sync-retry" or "deletion-review" or "planning-degraded" or "recovery-unavailable" || IsRemovalAttention(notice));

    public static bool IsRetryChoice(string choice) => choice is ConfirmLargeDownload or CheckHistoryAgain or ReviewDeletion or OpenStorage or ReviewReserve or PreviewMatches or ChooseFolder or RemoveRule or KeepRule or ExportDocx or ExportMarkdown or ExportPdf or ExportHtml or MakeAvailableOffline or Rename or ShowRemaining or OpenDropbox or RepairHelp or RetryNow or Exclude or Ignore or DeleteReviewed or KeepRestore or OpenFolderSettings or OpenRecoverySettings or Dismiss;

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
            : notice.Repair is not null && notice.ReasonKey is "provider-name-refused" or "windows-name-invalid"
            ? notice.Repair.Destination is null
                ? [new(Rename, "Rename…", RequestsName: true), new(Ignore, "Keep excluded"), new(RetryNow, "Retry this item")]
                : [new(Rename, "Resume / verify rename"), new(RepairHelp, "Rename awaiting sync"), new(RetryNow, "Retry this item"), new(Ignore, "Keep excluded")]
            : notice.FreeSpace is not null
            ? [new(OpenStorage, "Open storage"), new(ReviewReserve, "Review the reserve"),
               new(OpenRecoverySettings, "Choose another Recovery location"), new(OpenFolderSettings, "Review destination folder"),
               new(RetryNow, "Check space again"), new(Ignore, "Ignore; exclude this path")]
            : notice.RebuildableRule is not null
            ? [new(PreviewMatches, "Preview matches"), new(ChooseFolder, "Choose folder…", RequestsName: true),
               new(RemoveRule, "Remove rule"), new(KeepRule, "Keep")]
            : notice.ReasonKey == "deletion-evidence-pending"
            ? [new(CheckHistoryAgain, "Check history again"), new(ReviewDeletion, "Review deletion evidence"),
               new(Ignore, "Keep this copy; stop syncing this path")]
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
               new(RetryNow, "Retry this item"), new(Ignore, "Ignore; exclude this path from sync")]
            : notice.FolderInspection
            ? [new(ShowRemaining, "Show remaining items"), new(RetryNow, "Review removal again"), new(Ignore, "Keep folder; exclude from sync")]
            : IsRemovalAttention(notice)
            ? [new(OpenFolderSettings, "Open folder settings"), new(Dismiss, "Dismiss")]
            : notice.ReasonKey == "deletion-review" && notice.Rule == DeletionReviewRestoreOnlyRule
            ? [new(KeepRestore, "Keep / restore"), new(Ignore, "Ignore; exclude this path from sync")]
            : notice.ReasonKey == "deletion-review"
            ? [new(DeleteReviewed, "Delete with recovery"), new(KeepRestore, "Keep / restore"), new(Ignore, "Ignore; exclude this path from sync")]
            : notice.ReasonKey == "recovery-unavailable"
            ? [new(RetryNow, "Retry now"), new(OpenRecoverySettings, "Recovery settings")]
            : notice.ReasonKey == "planning-degraded"
            ? [new(RetryNow, "Retry now"), new(OpenFolderSettings, "Details / folder settings")]
            : IsRetryNotice(notice)
            ? [new(RepairHelp, "Why / repair guidance"), new(RetryNow, "Retry this item"),
               new(Ignore, "Ignore; exclude this path from sync")]
            : [];

    public static string Result(string choice, Notice notice) => choice == RepairHelp
        ? notice.Detail + " The notice stays open. Retry re-observes this item; Ignore excludes it without deleting files."
        : Result(choice);

    public static string Result(string choice) => choice switch
    {
        OpenRecoverySettings => "Open Settings and choose an available Recovery location. No files changed; paused operations remain retryable.",
        OpenFolderSettings => "Open this pair's folder settings to inspect selection and ignore rules. The notice remains open; no files changed.",
        RetryNow => "Retry requested. Existing evidence and deletion safeguards remain in force.",
        Exclude => "Excluded from future sync on both sides; no files were deleted.",
        Ignore => "Excluded this path from future sync on both sides; no files were changed or deleted.",
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
