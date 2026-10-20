namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // One shared authority for app and CLI. These choices change scheduling or exclusion policy,
    // never turn an uncertain effect into deletion permission or mark an effect successful.
    internal Task<string> ApplyRetryNoticeChoiceAsync(string pairId, string noticeId, string choice, CancellationToken ct,
        Action<PairDef, PairDef>? saveNarrowerPair = null, NoticeActionInput? input = null,
        Func<PairDef, RebuildableChoice, RebuildableChoice?, string>? updateRebuildableRule = null) =>
        RunExclusiveAsync(null, async (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            var saved = _store.NoticeById(pairId, noticeId);
            if (saved is null || saved.Value.Closed || !NoticeChoices.IsRetryNotice(saved.Value.Notice) ||
                !NoticeChoices.Options(saved.Value.Notice).Any(option => option.Choice == choice))
                throw new SyncRunRefusedException("That retry notice or option is no longer available. Refresh notices.");
            if (input?.Name is not null && choice is not (NoticeChoices.Rename or NoticeChoices.ChooseFolder) ||
                input?.Offset is > 0 && choice is not (NoticeChoices.ShowRemaining or NoticeChoices.PreviewMatches))
                throw new SyncRunRefusedException("Name is only for Rename or Choose folder; offset is only for paged previews.");
            if (input?.PageToken is not null && choice != NoticeChoices.ShowRemaining)
                throw new SyncRunRefusedException("Page token is only for Show remaining items.");
            var notice = saved.Value.Notice;
            if (notice.ReasonKey == "stubs-left-alone" && input?.Offset is > 0)
                throw new SyncRunRefusedException("Use the returned page token, not an offset, for online-only files.");
            noticeId = notice.NoticeId!; // A legacy id resolves to the canonical notice before any effect or closure.
            var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is no longer available.");
            if (notice.RebuildableRule is not null)
                return HandleRebuildableRuleChoice(pair, notice, choice, input, updateRebuildableRule);
            if (notice.ReasonKey == DropboxQuota.NoticeKey)
            {
                if (choice == NoticeChoices.RetryNow)
                {
                    _store.AddNotices([notice with { UploadSuspended = false }]);
                    _store.CloseNotice(noticeId, choice);
                    foreach (var retry in _store.ReadSyncRetries(pairId))
                        if (retry.LastError == DropboxQuota.Detail)
                            _store.SaveSyncRetry(pairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                    return "Upload retry requested. Downloads and other work continue.";
                }
                // Dismiss the message, not the upload hold; do not exclude the pair's root.
                _store.CloseNotice(noticeId, choice);
                return "Notice ignored. Uploads remain suspended until a quota check finds free space. No files were excluded or deleted.";
            }
            if (choice == NoticeChoices.Rename) return await RenameNoticeItemAsync(pair, notice, input?.Name, token);
            if (choice is NoticeChoices.ExportDocx or NoticeChoices.ExportMarkdown or NoticeChoices.ExportPdf or NoticeChoices.ExportHtml)
                return await ExportNoticeDocumentAsync(pair, notice, choice, token);
            if (choice is NoticeChoices.MakeAvailableOffline or NoticeChoices.ConfirmLargeDownload)
                return await MakeNoticeAvailableOfflineAsync(pair, notice, token, choice == NoticeChoices.ConfirmLargeDownload);
            if (choice == NoticeChoices.ShowRemaining) return notice.ReasonKey == "stubs-left-alone"
                ? ShowOnlineOnlyItems(pair, input?.PageToken) : RemainingItems(pair, notice, input?.Offset ?? 0);
            if (notice.ReasonKey == "stubs-left-alone")
            {
                if (choice == NoticeChoices.Ignore)
                {
                    _store.CloseNotice(noticeId, choice);
                    return "Summary ignored. Online-only files remain protected; no paths were excluded or deleted.";
                }
                RaiseLinksSummary(pair, LeftAlone(pair));
                return "Availability checked. Review online-only files to download individual items; scanning never downloads them.";
            }
            if (choice == NoticeChoices.OpenDropbox)
            {
                var cloud = new[] { pair.A, pair.B }.FirstOrDefault(root => root.Provider == SyncProviders.Dropbox)
                    ?? throw new SyncRunRefusedException("This pair has no Dropbox endpoint.");
                return "https://www.dropbox.com/home/" + string.Join("/", (cloud.RootPath.Trim('/') + "/" + notice.Path).TrimStart('/').Split('/').Select(Uri.EscapeDataString));
            }
            if (choice == NoticeChoices.OpenStorage) return "ms-settings:storagesense";
            if (choice == NoticeChoices.ReviewReserve) return ReviewNoticeReserve(notice);
            if (choice == NoticeChoices.ReviewDeletion) return ReviewNoticeDeletion(pair, notice);
            if (choice is NoticeChoices.OpenRecoverySettings or NoticeChoices.OpenFolderSettings)
                return NoticeChoices.Result(choice, notice);
            if (choice == NoticeChoices.CheckHistoryAgain)
            {
                if (_store.SyncRetryAt(pairId, notice.Path) is { } pending)
                    _store.SaveSyncRetry(pairId, pending with { RetryAfter = DateTimeOffset.MinValue });
                else _store.SaveSyncRetry(pairId, new(notice.Path, "sync", 0, notice.Detail, DateTimeOffset.MinValue));
                return "History check requested for the next run. Existing deletion holds, identity checks and Recovery safeguards are unchanged; nothing was deleted.";
            }
            if (choice == NoticeChoices.RepairHelp) return NoticeChoices.Result(choice, notice);
            if (notice.ReasonKey is "planning-degraded" or "recovery-unavailable")
            {
                if (choice == NoticeChoices.RetryNow)
                    foreach (var retry in _store.ReadSyncRetries(pairId).Where(retry => retry.Kind == (notice.ReasonKey == "planning-degraded" ? "planning" : "recovery")))
                        _store.SaveSyncRetry(pairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                return NoticeChoices.Result(choice, notice);
            }
            if (NoticeChoices.IsRemovalAttention(notice))
            {
                if (choice == NoticeChoices.Dismiss) _store.CloseNotice(noticeId, choice);
                // Opening settings is navigation only; neither choice authorizes an effect or retries a removal.
                return NoticeChoices.Result(choice, notice);
            }
            if (notice.ReasonKey == "deletion-review" && choice is NoticeChoices.DeleteReviewed or NoticeChoices.KeepRestore)
            {
                // A folder or flagged survivor never carries deletion authority, even if a fabricated
                // request bypasses the option list.
                if (choice == NoticeChoices.DeleteReviewed && notice.Rule == NoticeChoices.DeletionReviewRestoreOnlyRule)
                    throw new SyncRunRefusedException(
                        "This item cannot be deleted automatically. Choose Keep / restore or Ignore; nothing was deleted.");
                var retry = _store.SyncRetryAt(pairId, notice.Path);
                if (retry?.ProposedActions is not { Count: > 0 } proposed)
                    throw new SyncRunRefusedException("The deletion proposal is no longer available. Refresh notices.");
                // Explicit notice deletion is Recovery-first, even if cached history would let
                // automatic reviewed propagation skip Recovery for identical content.
                var actions = choice == NoticeChoices.DeleteReviewed
                    ? proposed.Select(action => action.Kind == ActionKind.Delete ? action with { RecoveryFirst = true } : action).ToArray()
                    : proposed
                    .Where(action => action.Kind == ActionKind.Delete && action.ExpectedTarget is not null)
                    .Select(action => action with
                    {
                        Kind = action.ExpectedTarget!.Kind == ItemKind.Folder ? ActionKind.MkDir : ActionKind.CopyNew,
                        Target = action.Target == Side.A ? Side.B : Side.A,
                        Source = action.ExpectedTarget, ExpectedTarget = null, RecoveryFirst = false,
                        OppositeAbsent = false, FrozenVictimSignature = null, ProviderEffect = null
                    }).ToArray();
                _store.SaveSyncRetry(pairId, retry with
                {
                    Kind = "reviewed-choice", ReviewedActions = actions, ProposedActions = null,
                    Held = null, HeldReviewed = false, RetryAfter = DateTimeOffset.MinValue
                });
                _store.CloseNotice(noticeId, choice);
                return NoticeChoices.Result(choice, notice);
            }
            if (choice == NoticeChoices.RetryNow)
            {
                if (_store.SyncRetryAt(pairId, notice.Path) is { } retry)
                    _store.SaveSyncRetry(pairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                else
                    _store.SaveSyncRetry(pairId, new SyncRetry(notice.Repair?.Destination ?? notice.Path, "sync", 0, notice.Detail, DateTimeOffset.MinValue));
                // Keep the notice visible until verified success. A retry request is not success.
                return NoticeChoices.Result(choice, notice);
            }
            if (choice is NoticeChoices.Exclude or NoticeChoices.Ignore)
            {
                var paths = CreateExclusions.Validate(pair.Excluded.Concat([notice.Path, notice.Repair?.Destination ?? notice.Path]).ToArray());
                if (saveNarrowerPair is not null) saveNarrowerPair(pair, pair with { Excluded = paths });
                else _store.SavePair(pair with { Excluded = paths });
                var held = _store.SyncRetryAt(pairId, notice.Path)?.Held;
                _store.ClearSyncRetry(pairId, notice.Path);
                if (held is not null)
                    _store.SaveSyncRetry(pairId, new(notice.Path, "deletion-evidence", 0, "",
                        DateTimeOffset.MaxValue, Held: held, ReprobeEvidence: true));
                // Retain pending effects for verify-only reconciliation under the normal exclusion policy.
            }
            _store.CloseNotice(noticeId, choice);
            return NoticeChoices.Result(choice, notice);
        }, ct);
}
