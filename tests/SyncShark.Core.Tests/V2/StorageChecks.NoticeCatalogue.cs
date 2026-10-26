using static Massdex.SyncShark.V2.NoticeChoices;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void NoticeCatalogueChecks()
    {
        CatalogueEquivalenceChecks();
        FailureSummaryOptionsChecks();
        // All non-payload retry reasons, plus dynamic reason families and payload branches below.
        // Ignore on tagged successors suppresses the cause without abandoning protected work.
        string[] reasons = ["run-history-unavailable", "run-unavailable", "store-maintenance", "autosync-watch-unavailable",
            "autosync-failed:test", "effects-ledger-incomplete", "recovery-restore-pending", "recovery-restore-ready", "folder-delete-unrestored",
            "unreadable", "item-blocked", "dropbox-storage-full", "online-only-item", "export-required",
            "sync-retry", "deletion-history-unavailable", "plan-bound-exceeded", "content-unconfirmed", "interrupted-delete-unverified", "planning-degraded", "recovery-unavailable",
            NoticeChoices.PointerObservationSummary, "link-item", "stubs-left-alone", "deletion-evidence-pending", "deletion-review"];
        foreach (var reason in reasons)
        {
            var notice = new Notice("p1", 40, NoticeKind.Info, "Parent/file.txt", "Reason; repair then retry.", reason);
            var options = NoticeOptions.Options(notice);
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
        Require(NoticeOptions.Options(quarantine).Any(option => option.Choice == NoticeChoices.QuarantineIgnore &&
            option.Label == "Ignore this cause (keep protection)"), "security hold does not promise automatic byte retries");
        Require(NoticeOptions.Options(quarantine).Any(option => option.Choice == NoticeChoices.QuarantineRetry),
            "security hold retains explicit user retry");
        var successor = new Notice("p1", 40, NoticeKind.Info, "Parent/file.txt", "Copy blocked; retry.", ExecutionReasons.ConflictCopyBlocked);
        Require(NoticeOptions.Options(successor).Select(option => option.Choice).SequenceEqual([NoticeChoices.RetryNow, NoticeChoices.Ignore]),
            "tagged keep-both successor offers Retry and cause-only Ignore");
        Require(!NoticeChoices.CanQueueExclusion(new("p1", 40, NoticeKind.Info, "file.txt", "failed", "sync-retry"),
            "file.txt", NoticeChoices.Ignore), "Ignore never enters deferred permanent exclusion queue");
    }

    /// <summary>A failure summary and an unknown failure keep Retry and Ignore beside their existing page
    /// action, so a failure never renders with no choices and never ends sync. A decision notice keeps its own
    /// empty catalogue: it waits for the user's decision, never a retry.</summary>
    private static void FailureSummaryOptionsChecks()
    {
        (string Reason, int Rule)[] summaries =
        [
            (SqliteStateStore.BlockedDeleteSummary, 0), (SqliteStateStore.UncertainDeleteSummary, 0),
            (SqliteStateStore.DeleteReviewSummary, 0), ("held-intents", 0),
            (RehashProtection.Reason, RehashProtection.Rule)
        ];
        foreach (var (reason, rule) in summaries)
        {
            var summary = new Notice("pair", rule, NoticeKind.Info, "", "summary", reason);
            var options = NoticeOptions.Options(summary);
            Require(options.Any(option => option.Choice == ShowRemaining), reason + " keeps its bounded Show remaining action");
            Require(options.Any(option => option.Choice == RetryNow), reason + " offers Retry");
            Require(options.Any(option => option.Choice == Ignore), reason + " offers Ignore without ending sync");
            Require(options.Select(option => option.Choice).Distinct().Count() == options.Count, reason + " has no duplicate options");
        }
        var unknown = new Notice("pair", 40, NoticeKind.Info, "Parent/file.txt", "A new failure.", "unrecognised-failure");
        Require(NoticeOptions.Options(unknown).Select(option => option.Choice)
                .SequenceEqual([RepairHelp, RetryNow, Ignore, Exclude]),
            "an unknown non-decision failure gets the generic why/retry/ignore notice with explicit exclusion");
        var decision = new Notice("pair", 40, NoticeKind.UserDecision, "Parent/file.txt", "A decision.", "unrecognised-decision");
        Require(NoticeOptions.Options(decision).Count == 0, "a decision notice never offers Retry or Ignore");
        var automatic = new Notice("pair", 4, NoticeKind.AutoConflict, "Parent/file.txt", "Decided automatically.",
            PlanNotices.AutoConflict.Key);
        Require(NoticeOptions.Options(automatic).Count == 0, "an automatic decision notice never offers Retry or Ignore");
        foreach (var kind in new[] { NoticeKind.KeepBoth, NoticeKind.EditBeatsDelete, NoticeKind.AutoConflict })
        {
            var outcome = new Notice("pair", 7, kind, "Parent/file.txt", "Completed outcome.", "test-reason");
            Require(NoticeOptions.Options(outcome).Count == 0 && !NoticeChoices.IsRetryNotice(outcome),
                kind + " with an unknown legacy reason retains acknowledgement instead of acquiring failure actions");
            Require(NoticeChoices.For(kind).Contains(Accept), kind + " retains its existing Accept acknowledgement");
        }
        var review = new Notice("pair", 40, NoticeKind.UserDecision, "Parent/file.txt", "Review the deletion.", "deletion-review");
        Require(NoticeOptions.Options(review).All(option => option.Choice != RetryNow),
            "a deletion-review decision notice never offers Retry");
        foreach (var reason in new[] { PlanNotices.OutsidePairArea.Key, PlanNotices.PairExcluded.Key,
            PlanNotices.StubWithoutCounterpart.Key, "name-mapping-collision" })
            Require(NoticeOptions.Options(new Notice("pair", 40, NoticeKind.Info, "Parent/file.txt", "known information", reason)).Count == 0,
                reason + " remains a known informational notice, not a generic retry");
    }

    private static void CatalogueEquivalenceChecks()
    {
        Notice N(string reason, int rule = 40) => new("pair", rule, NoticeKind.Info, "Parent/file.txt", "detail", reason);
        var file = new ItemState("Parent/file.txt", ItemKind.File, "object", "revision", "hash", 1,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        var folder = file with { Kind = ItemKind.Folder };
        var delete = new SyncAction("delete", 40, ActionKind.Delete, Side.A, file.Path, null, null, file, true, null);
        var frozen = delete with { ActionId = "rrs-frozen", SuppressReconciledBaseline = true };
        string[] reasons = [
            "effects-ledger-incomplete", "plan-bound-exceeded", "blocked-delete-summary", "uncertain-delete-summary",
            "deletion-review-summary", "recovery-restore-pending", "recovery-restore-ready", "folder-delete-unrestored",
            "held-intents", "local-rehash-pending", "permanent-boundary", "journal-capacity:1", "removal-review:one",
            "reviewed-folder-grew", "reviewed-removal-skipped", "run-unavailable", "run-history-unavailable", "store-maintenance",
            "autosync-watch-unavailable", "autosync-failed:one", "deletion-evidence-pending", "links-excluded", "link-item",
            "stubs-left-alone", "online-only-item", "dropbox-storage-full", "export-required", "deletion-review",
            "recovery-unavailable", "keep-both-copy-blocked", "planning-degraded", "pointer-observation-summary",
            "indexing-root-failure:one", "sync-retry", "unreadable", "item-blocked", "content-unconfirmed",
            "deletion-history-unavailable", "interrupted-delete-unverified", "delete-source-unconfirmed", "unknown"
        ];
        var notices = reasons.Select(reason => N(reason)).ToList();
        notices.AddRange([
            N("storage") with { Storage = new("root", "object", "remote", "settlement") },
            N("defender") with { DefenderHistory = new("root", "batch", 1, "shared-release", SharedRelease: new("object", "bindings", "")) },
            N("defender") with { DefenderHistory = new("root", "batch", 1, "shared-release", SharedRelease: new("object", "bindings", "Pair B")) },
            N("defender") with { DefenderHistory = new("root", "batch", 1, "held") },
            N("defender") with { DefenderHistory = new("root", "batch", 1, "review") },
            N("quarantine") with { Quarantine = new(Side.A, null, null) },
            N("quarantine") with { Quarantine = new(Side.A, file, null) },
            N("quarantine") with { Quarantine = new(Side.A, folder, null) },
            N("quarantine") with { Quarantine = new(Side.A, file, folder) },
            N("quarantine") with { Quarantine = new(Side.A, file, null, ConflictingGrantId: "grant") },
            N("quarantine") with { Quarantine = new(Side.A, folder, null, ConflictingGrantId: "grant") },
            N("arrival") with { ConflictOperation = new(ReplaceExisting, null, [delete], [delete.ActionId]) },
            N("arrival") with { ConflictOperation = new(ReplaceExisting, null, [delete], []) },
            N("conflict") with { ConflictVersions = new(file, file, Incoming: delete) },
            N("conflict", 27) with { ConflictVersions = new(file, file) },
            N("conflict", 10) with { ConflictVersions = new(file, file) },
            N("conflict", 11) with { ConflictVersions = new(file, file) },
            N("retained") with { RetainedVersion = new("action", Side.A, file, file) },
            N("cloud-files-binding-needs-review", 38),
            N("reviewed-folder-grew") with { RemovalSides = [Side.A] },
            N("provider-name-refused") with { Repair = new(Side.A, file) },
            N("windows-name-invalid") with { Repair = new(Side.A, file, "renamed.txt") },
            N("space") with { FreeSpace = new("volume", 1) },
            N("rebuildable") with { RebuildableRule = new("category", "prefix") },
            N("folder") with { FolderInspection = true },
            N("deletion-review", 41),
            N("online-only-item") with { Repair = new(Side.A, file with { Size = OnlineOnlyHydration.ConfirmationBytes }), Detail = "This downloads bytes" },
            N("online-only-item") with { Repair = new(Side.A, file with { Size = OnlineOnlyHydration.ConfirmationBytes + 1 }), Detail = "This downloads bytes" },
            N("online-only-item") with { Repair = new(Side.A, file with { Size = OnlineOnlyHydration.ConfirmationBytes + 1 }) },
            N("export-required") with { ExportInfo = new(null, []) },
            N("export-required") with { ExportInfo = new("html", ["HTML", "pdf", "unknown", "markdown", "docx"]) },
            N("folder-delete-unrestored") with { FolderRecoveryAnchor = "anchor" },
            N("recovery-restore-pending") with { ConflictOperation = new(RetryNow, "copy.txt", [], []) },
            N("recovery-restore-pending") with { ConflictOperation = new(RetryNow, "copy.txt", [frozen], []) },
            N("sync-retry") with { RetainedVersion = new("action", Side.A, file, file) }
        ]);
        // Both item/root paths exercise the conditional exclusion tail and summary behavior.
        notices.AddRange(notices.Select(notice => notice with { Path = "" }).ToArray());
        var allChoices = notices.SelectMany(LegacyOptions).Select(option => option.Choice)
            .Append("unknown-choice").Append("").Distinct(StringComparer.Ordinal).ToArray();
        foreach (var notice in notices)
        {
            var expected = LegacyOptions(notice).Select(option => (option.Choice, option.Label, option.RequestsName)).ToArray();
            Require(NoticeChoices.Catalogue(notice).SequenceEqual(expected), "catalogue preserves every ordered label and name request: " + notice.ReasonKey);
            Require(NoticeOptions.Options(notice).Select(option => (option.Choice, option.Label, option.RequestsName)).SequenceEqual(expected),
                "display projects exactly the admitted catalogue: " + notice.ReasonKey);
            foreach (var choice in allChoices.Cast<string?>().Append(null).Concat(allChoices.Select(choice => choice.ToUpperInvariant())))
            {
                var oldAdmission = LegacyOptions(notice).Any(option => option.Choice == choice);
                var newAdmission = NoticeChoices.IsAdmissible(notice, choice);
                // Seven migrated expressions, including every independent surrounding gate truth value.
                Require(oldAdmission == newAdmission, "ConflictChoices admission truth table");
                Require(!oldAdmission == !newAdmission, "PlanAdmission refusal truth table");
                foreach (var exists in new[] { false, true })
                foreach (var closed in new[] { false, true })
                foreach (var retry in new[] { false, true })
                {
                    Require((exists && retry && oldAdmission) == (exists && retry && newAdmission), "RetryNotices queued retry truth table");
                    Require((exists && !closed && retry && oldAdmission) == (exists && !closed && retry && newAdmission), "RetryNotices pair retry truth table");
                    Require((exists && !closed && oldAdmission) == (exists && !closed && newAdmission), "RetryNotices gated review truth table");
                    Require((exists && retry && oldAdmission && !closed) == (exists && retry && newAdmission && !closed), "RetryNotices accepted exclusion truth table");
                    Require((exists && oldAdmission) == (exists && newAdmission), "NoticeVersions admission truth table");
                }
            }
        }
    }

    // Frozen pre-split display builders: a test oracle only, never consulted by production admission.
    private static string ExportLabel(string format) => format.ToLowerInvariant() switch
    {
        "docx" => "DOCX",
        "markdown" => "Markdown",
        "pdf" => "PDF",
        "html" => "HTML",
        _ => format
    };

    private static IReadOnlyList<SyncNoticeOption> LegacyExportOptions(Notice notice)
    {
        var formats = notice.ExportInfo is { } info ? info.Formats : new[] { "docx", "markdown", "pdf" };
        return formats.Select(format => (Format: format, Choice: ExportChoice(format)))
            .Where(item => item.Choice is not null)
            .Select(item => new SyncNoticeOption(item.Choice!, "Export as " + ExportLabel(item.Format) + "…")).ToArray();
    }

    private static IReadOnlyList<SyncNoticeOption> LegacyRecoveryRestoreOptions(Notice notice)
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

    private static IReadOnlyList<SyncNoticeOption> LegacyFolderRestoreOptions(Notice notice)
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
    private static bool LegacyCanStopSyncing(Notice notice) =>
        notice.Path.Length != 0 && IsRetryNotice(notice) && !IsPairRetryNotice(notice) &&
        !IsRecoveryRestoreNotice(notice) && notice.RebuildableRule is null && notice.Storage is null &&
        notice.ReasonKey is not ("permanent-boundary" or "folder-delete-unrestored" or "plan-bound-exceeded" or ExecutionReasons.ConflictCopyBlocked or DropboxQuota.NoticeKey) &&
        LegacyOptionsCore(notice).Any(option => option.Choice == Ignore);

    /// <summary>The one rendered catalogue for both CLI and UI.</summary>
    private static IReadOnlyList<SyncNoticeOption> LegacyOptions(Notice notice)
    {
        var options = LegacyOptionsCore(notice).Select(option => IsIgnoreChoice(option.Choice)
            ? option with { Label = notice.Quarantine is not null
                ? "Ignore this cause (keep protection)" : "Ignore this cause (keep retrying safely)" } : option).ToList();
        if (LegacyCanStopSyncing(notice) && options.All(option => option.Choice != Exclude))
            options.Add(new(Exclude, "Stop syncing this file"));
        return options;
    }

    private static IReadOnlyList<SyncNoticeOption> LegacyOptionsCore(Notice notice) =>
        notice.ReasonKey == "effects-ledger-incomplete"
            ? [new(RetryNow, "Retry sync (does not repair the prior export)"), new(Ignore, "Ignore this cause"),
               new(RepairHelp, "Check destination permissions / space; rerun with a new export path")]
            : notice.ReasonKey == "plan-bound-exceeded"
            ? [new(RetryNow, "Retry with the same plan bound"), new(ApprovePlanSize, "Approve this plan size"),
               new(Ignore, "Ignore this cause"), new(RepairHelp, "Review preview / provenance and the plan bound")]
            : (notice.ReasonKey is SqliteStateStore.BlockedDeleteSummary or SqliteStateStore.UncertainDeleteSummary or SqliteStateStore.DeleteReviewSummary)
            ? [new(ShowRemaining, "Show remaining blocked deletions (50 per page)"),
               new(RetryNow, "Retry now"), new(Ignore, "Ignore this cause; keep the blocked work protected")]
            : IsRecoveryRestoreNotice(notice)
            ? LegacyRecoveryRestoreOptions(notice)
            : notice.ReasonKey == "folder-delete-unrestored"
            ? LegacyFolderRestoreOptions(notice)
            : notice.ReasonKey == "held-intents"
            ? [new(ShowRemaining, "Show remaining changes (50 per page)"), new(RetryNow, "Retry now"),
               new(Ignore, "Ignore this cause")]
            : notice.ReasonKey == RehashProtection.Reason
            ? [new(ShowRemaining, "Show remaining changes (50 per page)"), new(RetryNow, "Retry now"),
               new(Ignore, "Ignore this cause")]
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
            ? LegacyQuarantineOptions(notice.Quarantine)
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
            ? [.. LegacyExportOptions(notice), new(OpenDropbox, "Open in Dropbox"), new(RepairHelp, "Why / repair guidance"),
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
            ? [new(RetryNow, "Retry keep-both copy"), new(Ignore, "Ignore this cause")]
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
    private static IReadOnlyList<SyncNoticeOption> LegacyQuarantineOptions(QuarantineNotice hold)
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
}
