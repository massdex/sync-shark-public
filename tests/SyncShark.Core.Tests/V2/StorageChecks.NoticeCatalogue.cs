namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void NoticeCatalogueChecks()
    {
        // All non-payload retry reasons, plus dynamic reason families and payload branches below.
        // No Ignore is allowed on tagged keep-both successors: abandoning that successor would
        // silently undo an established keep-both decision (ExecutionReasons.ConflictCopyBlocked).
        string[] reasons = ["run-history-unavailable", "run-unavailable", "store-maintenance", "autosync-watch-unavailable",
            "autosync-failed:test", "effects-ledger-incomplete", "recovery-restore-pending", "recovery-restore-ready", "folder-delete-unrestored",
            "unreadable", "item-blocked", "dropbox-storage-full", "online-only-item", "export-required",
            "sync-retry", "deletion-history-unavailable", "plan-bound-exceeded", "content-unconfirmed", "interrupted-delete-unverified", "planning-degraded", "recovery-unavailable",
            NoticeChoices.PointerObservationSummary, "link-item", "stubs-left-alone", "deletion-evidence-pending", "deletion-review"];
        foreach (var reason in reasons)
        {
            var notice = new Notice("p1", 40, NoticeKind.Info, "Parent/file.txt", "Reason; repair then retry.", reason);
            var options = NoticeChoices.Options(notice);
            Require(options.Any(option => option.Choice is NoticeChoices.RetryNow or NoticeChoices.CheckHistoryAgain or NoticeChoices.KeepRestore),
                reason + " offers a retry/resolution action");
            Require(options.Any(option => option.Choice == NoticeChoices.Ignore), reason + " offers cause suppression");
            Require(options.Select(option => option.Choice).Distinct().Count() == options.Count, reason + " has no duplicate options");
            if (NoticeChoices.CanStopSyncing(notice))
                Require(options.Any(option => option.Choice == NoticeChoices.Exclude && option.Label == "Stop syncing this file"),
                    reason + " has explicit exclusion separate from Ignore");
        }
        const string rawScanCause = "ArgumentException: targeted re-read root could not be opened";
        var scan = FailureRetryOwner.RetryNotice("p1", "folder", rawScanCause, 2,
            explanation: ScanFailureText.Describe(rawScanCause));
        Require(scan.CauseKey == rawScanCause && !scan.Detail.Contains("ArgumentException", StringComparison.Ordinal) &&
            scan.Detail.Contains("restore read access", StringComparison.OrdinalIgnoreCase),
            "scan notice keeps raw cause identity separately from existing plain-language explanation");
        var quarantine = new Notice("p1", 40, NoticeKind.Info, "file.txt", "Antivirus hold.", QuarantineFailure.NoticeKey,
            Quarantine: new(Side.A, null, null));
        Require(NoticeChoices.Options(quarantine).Any(option => option.Choice == NoticeChoices.QuarantineIgnore &&
            option.Label == "Ignore this cause (keep protection)"), "security hold does not promise automatic byte retries");
        Require(NoticeChoices.Options(quarantine).Any(option => option.Choice == NoticeChoices.QuarantineRetry),
            "security hold retains explicit user retry");
        var successor = new Notice("p1", 40, NoticeKind.Info, "Parent/file.txt", "Copy blocked; retry.", ExecutionReasons.ConflictCopyBlocked);
        Require(NoticeChoices.Options(successor).Select(option => option.Choice).SequenceEqual([NoticeChoices.RetryNow]),
            "tagged keep-both successor is Retry-only, never silently abandoned");
        Require(!NoticeChoices.CanQueueExclusion(new("p1", 40, NoticeKind.Info, "file.txt", "failed", "sync-retry"),
            "file.txt", NoticeChoices.Ignore), "Ignore never enters deferred permanent exclusion queue");
    }
}
