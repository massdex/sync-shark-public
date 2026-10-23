namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private readonly object _noticeChoiceGate = new();

    internal void UpdatePairSettings(Action update)
    {
        lock (_noticeChoiceGate) update();
    }

    internal Task UpdatePairSettingsAsync(Action update, CancellationToken ct) =>
        RunExclusiveAsync(null, (_, _) => { update(); return Task.FromResult(true); }, ct);

    internal bool TryApplyImmediateNoticeChoice(string pairId, string noticeId, string choice, CancellationToken ct)
    {
        if (choice is not (NoticeChoices.RetryNow or NoticeChoices.Ignore)) return false;
        lock (_noticeChoiceGate)
        {
            ct.ThrowIfCancellationRequested();
            // A root-indexing notice is one physical row shown through several pairs; resolve the projection.
            var notice = NoticeForChoice(pairId, noticeId);
            if (notice is null || !NoticeChoices.IsRetryNotice(notice) ||
                !NoticeChoices.Options(notice).Any(option => option.Choice == choice)) return false;
            // Admission notices own their explicit cap; never schedule an unbounded generic Retry.
            if (notice.ReasonKey == PlanNotices.PlanBoundExceededKey) return false;
            if (notice.ReasonKey == "folder-delete-unrestored")
            {
                if (choice == NoticeChoices.Ignore) _store.CloseNotice(noticeId, choice);
                return true; // Wake verification only; the anchor, not a path retry, owns recovery.
            }
            if (notice.ReasonKey is RecoveryRestorePending or RecoveryRestoreReady) return false;
            if (NoticeChoices.IsIndexFailure(notice))
            {
                // Retry makes the root's durable deadlines due now; Ignore suppresses this episode only.
                // Neither pauses retries nor excludes a path, and only verified success clears the evidence.
                if (choice == NoticeChoices.RetryNow && _store is SqliteStateStore indexStore &&
                    SqliteStateStore.IndexFailureRoot(notice.ReasonKey) is { } indexRoot)
                    indexStore.RequestIndexRetry(indexRoot);
                else if (choice == NoticeChoices.Ignore) _store.CloseNotice(notice.NoticeId ?? noticeId, choice);
                return true;
            }
            if (NoticeChoices.IsPairRetryNotice(notice)) return ApplyPairNoticeChoice(pairId, noticeId, choice, ct);
            // Specialized blockers need their durable action path, not only a retry timestamp.
            if (notice.ReasonKey == DropboxQuota.NoticeKey || notice.Repair is not null || notice.RebuildableRule is not null) return false;
            if (choice != NoticeChoices.RetryNow) return false;
            if (notice.ReasonKey == NoticeChoices.PointerObservationSummary)
            {
                // The pair summary stands for per-entity retries; make exactly those due, touching no other kind.
                if (_store is SqliteStateStore storage)
                    storage.RequestOwnedPointerObservationRetriesNow(pairId, OwnedPointerProof.ObservationMessage);
                return true;
            }
            if (notice.ReasonKey is "planning-degraded" or "recovery-unavailable")
            {
                var kind = notice.ReasonKey == "planning-degraded" ? "planning" : "recovery";
                foreach (var retry in _store.ReadSyncRetries(pairId).Where(retry => retry.Kind == kind))
                    _store.RequestSyncRetryNow(pairId, retry.Path);
            }
            else _store.RequestSyncRetryNow(pairId, notice.Path);
            return true; // scheduler will pick up retries; no synchronous run or effect here
        }
    }

    internal bool ApplyPairNoticeChoice(string pairId, string noticeId, string choice, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var saved = _store.NoticeById(pairId, noticeId);
        if (saved is null || saved.Value.Closed || !NoticeChoices.IsPairRetryNotice(saved.Value.Notice) ||
            !NoticeChoices.Options(saved.Value.Notice).Any(option => option.Choice == choice))
            throw new SyncRunRefusedException("That pair notice or option is no longer available. Refresh notices.");
        if (choice == NoticeChoices.Ignore) _store.CloseNotice(noticeId, choice);
        return true;
    }

    // One shared authority for app and CLI. These choices change scheduling or exclusion policy,
    // never turn an uncertain effect into deletion permission or mark an effect successful.
    internal async Task<string> ApplyRetryNoticeChoiceAsync(string pairId, string noticeId, string choice, CancellationToken ct,
        Action<PairDef, PairDef>? saveNarrowerPair = null, Action<SyncAction, ActionResult>? removalResult = null, IProgress<RunProgress>? progress = null, Notice? acceptedExclusion = null, NoticeActionInput? input = null,
        Func<PairDef, RebuildableChoice, RebuildableChoice?, string>? updateRebuildableRule = null)
    {
        if (_store.NoticeById(pairId, noticeId) is { Closed: false } boundNotice &&
            boundNotice.Notice.ReasonKey == PlanNotices.PlanBoundExceededKey)
            return await ApplyPlanBoundNoticeChoiceAsync(boundNotice.Notice, choice, progress, ct);

        // A bounded remaining-items read must not wait behind a long verification/sync run.
        // The page is a current snapshot only; it neither changes intent authority nor claims fresh proof.
        if (choice == NoticeChoices.ShowRemaining &&
            _store.NoticeById(pairId, noticeId) is { Closed: false } savedHeld &&
            NoticeChoices.IsVerificationSummary(savedHeld.Notice))
        {
            ct.ThrowIfCancellationRequested();
            var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is no longer available.");
            return ShowHeldIntentItems(pair, input);
        }
        if (choice == NoticeChoices.ShowRemaining &&
            _store.NoticeById(pairId, noticeId) is { Closed: false } savedBlocked &&
            savedBlocked.Notice.ReasonKey is SqliteStateStore.BlockedDeleteSummary or SqliteStateStore.UncertainDeleteSummary or SqliteStateStore.DeleteReviewSummary)
        {
            ct.ThrowIfCancellationRequested();
            return savedBlocked.Notice.ReasonKey == SqliteStateStore.BlockedDeleteSummary ? ShowBlockedDeletes(pairId, input) :
                ShowDeleteNoticeItems(pairId, savedBlocked.Notice.ReasonKey == SqliteStateStore.DeleteReviewSummary, input);
        }
        string? observationSession = null;
        PairDef? observedPair = null;
        Exception? primaryError = null;
        try
        {
            if ((choice is NoticeChoices.ReviewRemoval or NoticeChoices.ReviewRemovalA or NoticeChoices.ReviewRemovalB or NoticeChoices.ConfirmRemoval) &&
                _store is SqliteStateStore db)
            {
                // Observation is read-only and must start even while another pair owns the run gate.
                // Revalidate the notice and root bindings under the gate before publication or effects.
                var saved = _store.NoticeById(pairId, noticeId);
                if (saved is null || saved.Value.Closed || !NoticeChoices.Options(saved.Value.Notice).Any(o => o.Choice == choice))
                    throw new SyncRunRefusedException("That review is no longer available. Refresh notices.");
                var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is no longer available.");
                RefuseLayout(pair);
                (IInventorySource A, IInventorySource B) readers;
                try { readers = await _endpoints.OpenReviewAsync(pair, ct); }
                catch (Exception error) when (error is not (OperationCanceledException or StateDamagedException))
                { throw new SyncRunRefusedException($"Folder review could not open: {error.Message}. Retry review, open settings or dismiss (Ignore); nothing was removed."); }
                var observation = (Pair: pair, Path: saved.Value.Notice.Path, readers.A, readers.B);
                using var activity = RunActivity.Begin(progress, _settings, _time);
                observedPair = observation.Pair;
                observationSession = Guid.NewGuid().ToString("N");
                var session = observationSession;
                try { await Task.WhenAll(Read(observation.A, Side.A), Read(observation.B, Side.B)); }
                catch (Exception error) when (error is not (OperationCanceledException or StateDamagedException))
                {
                    var detail = $"Folder review could not finish: {error.Message}. Retry review, open settings or dismiss (Ignore); nothing was removed.";
                    var notice = saved.Value.Notice;
                    if (NoticeChoices.IsRemovalReview(notice))
                    {
                        if (db.ReadRemovalReview(pair, notice.Path) is { } old &&
                            notice.ReasonKey.StartsWith(NoticeChoices.RemovalReviewPrefix + old.Generation + ":", StringComparison.Ordinal))
                            db.ClearRemovalReview(old.Generation);
                        _store.CloseNotice(noticeId, "listing-incomplete");
                    }
                    _store.AddNotices([notice with { ReasonKey = notice.RemovalOriginReason ??
                        (NoticeChoices.IsRemovalAttention(notice) ? notice.ReasonKey : "reviewed-folder-grew"), Detail = detail }]);
                    throw new SyncRunRefusedException(detail);
                }
                async Task Read(IInventorySource inventory, Side side) =>
                    await inventory.ReadSubtreeAsync(observation.Path, rows =>
                    {
                        if (rows.Any(item => !PairPath.IsSelfOrUnder(item.Path, observation.Path)))
                            throw new IOException("Folder listing escaped its requested subtree.");
                        db.StageRemovalItems(session, side, rows);
                        return Task.CompletedTask;
                    }, ct, new RemovalProgress(activity.Progress, observation.Path, side));
                // A root replacement cannot bind an observation from the old endpoint to the new one.
                if (_store.Pair(pairId) is not { } current || current.A != observation.Pair.A || current.B != observation.Pair.B)
                    throw new SyncRunRefusedException("The folder roots changed during review. Refresh the review; nothing was removed.");
            }
            return await ApplyRetryNoticeChoiceCoreAsync(pairId, noticeId, choice, ct, saveNarrowerPair, removalResult, observationSession, observedPair, acceptedExclusion, input, updateRebuildableRule);
        }
        catch (Exception error) { primaryError = error; throw; }
        finally
        {
            if (observationSession is not null && _store is SqliteStateStore db)
                FairStoreGate.Cleanup(() => db.ClearRemovalObservation(observationSession), primaryError: primaryError);
        }
    }

    private sealed class RemovalProgress(IProgress<RunProgress> sink, string path, Side side) : IProgress<string>
    {
        public void Report(string message) => sink.Report(new RunProgress("Removal review", path, 0, 0)
            { Message = $"{side}: {message}" });
    }

    /// <summary>
    /// The open notice a choice names. One root-indexing notice is physical on its lowest pair and projected to
    /// the other pairs sharing the root, so a choice made on a projection resolves to that same row.
    /// </summary>
    private Notice? NoticeForChoice(string pairId, string noticeId)
    {
        var saved = _store.NoticeById(pairId, noticeId);
        if (saved is { Closed: false }) return saved.Value.Notice;
        if (saved is null && _store is SqliteStateStore storage &&
            storage.IndexNoticeById(pairId, noticeId) is { } physical) return physical.Notice;
        return null;
    }

    private Task<string> ApplyRetryNoticeChoiceCoreAsync(string pairId, string noticeId, string choice, CancellationToken ct,
        Action<PairDef, PairDef>? saveNarrowerPair, Action<SyncAction, ActionResult>? removalResult, string? observationSession, PairDef? observedPair, Notice? acceptedExclusion, NoticeActionInput? input,
        Func<PairDef, RebuildableChoice, RebuildableChoice?, string>? updateRebuildableRule) =>
        RunExclusiveAsync(null, async (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            // A validated exclusion accepted before waiting remains user intent even if the run
            // resolves its notice. Read the pair only AFTER admission, never save a run-start snapshot.
            var notice = acceptedExclusion ?? NoticeForChoice(pairId, noticeId);
            if (notice is null || !NoticeChoices.IsRetryNotice(notice) ||
                !NoticeChoices.Options(notice).Any(option => option.Choice == choice) ||
                (acceptedExclusion is not null && (NoticeChoices.IsPairRetryNotice(notice) ||
                    choice is not (NoticeChoices.Ignore or NoticeChoices.Exclude) || notice.NoticeId != noticeId)))
                throw new SyncRunRefusedException("That retry notice or option is no longer available. Refresh notices.");
            if (input?.Name is not null && choice is not (NoticeChoices.Rename or NoticeChoices.ChooseFolder or NoticeChoices.CopyLinkOutsideRoot or NoticeChoices.CopyLinkAgain or NoticeChoices.ShowRemaining) ||
                input?.Offset is > 0 && choice is not (NoticeChoices.ShowRemaining or NoticeChoices.PreviewMatches))
                throw new SyncRunRefusedException("Name is for Rename, Choose folder, exact link target confirmation, or a link page cursor; offset is only for Show remaining items or paged previews.");
            if (input?.PageToken is not null && choice != NoticeChoices.ShowRemaining)
                throw new SyncRunRefusedException("Page token is only for Show remaining items.");
            if (notice.ReasonKey == "stubs-left-alone" && input?.Offset is > 0)
                throw new SyncRunRefusedException("Use the returned page token, not an offset, for online-only files.");
            noticeId = notice.NoticeId!; // A legacy id resolves to the canonical notice before any effect or closure.
            var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is no longer available.");
            if (choice == NoticeChoices.Ignore)
            {
                // E9: suppression belongs to notice lifecycle, never to an exclusion or retry pause.
                _store.CloseNotice(noticeId, choice);
                return NoticeChoices.Result(choice, notice);
            }
            if (notice.ReasonKey is RecoveryRestorePending or RecoveryRestoreReady)
            {
                if (choice is NoticeChoices.RetryNow or NoticeChoices.RestoreAsCopy) await _endpoints.PrepareAsync(pair, token);
                return await RetryRecoveryRestoreAsync(pair, notice, choice, token);
            }
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
            if (NoticeChoices.IsVerificationSummary(notice) && choice == NoticeChoices.ShowRemaining)
                return ShowHeldIntentItems(pair, input);
            if (choice == NoticeChoices.Rename) return await RenameNoticeItemAsync(pair, notice, input?.Name, token);
            if (choice is NoticeChoices.ExportDocx or NoticeChoices.ExportMarkdown or NoticeChoices.ExportPdf or NoticeChoices.ExportHtml)
                return await ExportNoticeDocumentAsync(pair, notice, choice, token);
            if (choice is NoticeChoices.ShowTarget or NoticeChoices.SyncTargetSeparately or NoticeChoices.CopyLink or NoticeChoices.CopyLinkOutsideRoot or NoticeChoices.CopyLinkAgain or NoticeChoices.RemoveLinkStaging || notice.ReasonKey == "link-item" && choice == NoticeChoices.RetryNow)
                return await LinkNoticeActionAsync(pair, notice, choice, token, input);
            if (choice is NoticeChoices.MakeAvailableOffline or NoticeChoices.ConfirmLargeDownload)
                return await MakeNoticeAvailableOfflineAsync(pair, notice, token, choice == NoticeChoices.ConfirmLargeDownload);
            if (choice == NoticeChoices.ShowRemaining) return notice.ReasonKey == "stubs-left-alone"
                ? ShowOnlineOnlyItems(pair, input?.PageToken) : notice.ReasonKey == "links-excluded"
                ? ShowLinkItems(pair, input) : RemainingItems(pair, notice, input?.Offset ?? 0);
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
            if (notice.ReasonKey == "folder-delete-unrestored")
            {
                if (choice == NoticeChoices.RestoreAgain)
                {
                    Executor.RequestFolderRestoreAgain(_store, pairId, notice);
                    return "Restore again requested for the same submitted revisions. The notice stays until recovery is verified; occupied paths remain protected.";
                }
                if (choice == NoticeChoices.Ignore) _store.CloseNotice(noticeId, choice);
                // The anchor owns recovery work. Never create a replacement action or exclude the folder.
                return "Recovery remains protected. The next run verifies unknown submissions without replaying them; known no-effect failures retain their bounded retry.";
            }
            if (notice.ReasonKey is "planning-degraded" or "recovery-unavailable")
            {
                // A summary's Ignore silences information, never excludes the root or discards debt.
                if (choice == NoticeChoices.Ignore) _store.CloseNotice(noticeId, choice);
                if (choice == NoticeChoices.RetryNow)
                    foreach (var retry in _store.ReadSyncRetries(pairId).Where(retry => retry.Kind == (notice.ReasonKey == "planning-degraded" ? "planning" : "recovery")))
                        _store.SaveSyncRetry(pairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                return NoticeChoices.Result(choice, notice);
            }
            if (observedPair is not null && (pair.A != observedPair.A || pair.B != observedPair.B))
                throw new SyncRunRefusedException("The folder roots changed during review. Refresh the review; nothing was removed.");
            if (NoticeChoices.IsRemovalReview(notice) || choice is NoticeChoices.ReviewRemoval or NoticeChoices.ReviewRemovalA or NoticeChoices.ReviewRemovalB)
            {
                await RemovalChoiceAsync(pair, notice, choice, token, removalResult, observationSession);
                return NoticeChoices.Result(choice, notice);
            }
            if (NoticeChoices.IsPairRetryNotice(notice))
            {
                if (choice == NoticeChoices.RetryNow && notice.FailureRetry is { } failure)
                    _store.AddNotices([notice with { FailureRetry = failure with { RetryAfter = DateTimeOffset.MinValue } }]);
                if (choice == NoticeChoices.Ignore) _store.CloseNotice(noticeId, choice);
                // A pair failure is not a path exclusion or item retry. The host wakes the pair.
                return NoticeChoices.Result(choice, notice);
            }
            if (NoticeChoices.IsIndexFailure(notice))
            {
                // Retry makes the root's durable deadlines due now; Ignore suppresses this episode only.
                // Neither pauses retries nor excludes a path, and only verified success clears the evidence.
                if (choice == NoticeChoices.RetryNow && _store is SqliteStateStore indexStore &&
                    SqliteStateStore.IndexFailureRoot(notice.ReasonKey) is { } indexRoot)
                    indexStore.RequestIndexRetry(indexRoot);
                else if (choice == NoticeChoices.Ignore) _store.CloseNotice(notice.NoticeId ?? noticeId, choice);
                return choice == NoticeChoices.RetryNow ? NoticeChoices.IndexRetryResult : NoticeChoices.IgnoreResult;
            }
            if (notice.ReasonKey == NoticeChoices.PointerObservationSummary)
            {
                // Retry makes the durable per-entity debts due; Ignore only silences this summary. Neither may
                // exclude the empty path or discard a per-entity retry.
                if (choice == NoticeChoices.RetryNow && _store is SqliteStateStore storage)
                    storage.RequestOwnedPointerObservationRetriesNow(pairId, OwnedPointerProof.ObservationMessage);
                else if (choice == NoticeChoices.Ignore) _store.CloseNotice(noticeId, choice);
                return NoticeChoices.Result(choice, notice);
            }
            if (NoticeChoices.IsRemovalAttention(notice))
            {
                if (choice == NoticeChoices.RetryNow) _store.RequestSyncRetryNow(pairId, notice.Path);
                // Dismiss is this catalogue's Ignore label: suppress the cause through the shared owner.
                if (choice == NoticeChoices.Dismiss) _store.CloseNotice(noticeId, NoticeChoices.Ignore);
                // Retry only schedules the retained choice; fresh proof is still mandatory next run.
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
                    ? proposed.Select(action => action.Kind == ActionKind.Delete
                        ? action with { RecoveryFirst = true } : action).ToArray()
                    : proposed
                    .Where(action => action.Kind == ActionKind.Delete && action.ExpectedTarget is not null)
                    .Select(action => action with
                    {
                        Kind = action.ExpectedTarget!.Kind == ItemKind.Folder ? ActionKind.MkDir : ActionKind.CopyNew,
                        Target = action.Target == Side.A ? Side.B : Side.A,
                        Source = action.ExpectedTarget, ExpectedTarget = null, RecoveryFirst = false,
                        OppositeAbsent = false, FrozenVictimSignature = null, ProviderEffect = null
                    }).ToArray();
                if (choice == NoticeChoices.DeleteReviewed)
                    for (var i = 0; i < actions.Length; i++)
                        actions[i] = await RecordRemovalConsentAsync(pair, actions[i], token).ConfigureAwait(false);
                _store.SaveSyncRetry(pairId, retry with
                {
                    Kind = "reviewed-choice", ReviewedActions = actions, ProposedActions = null,
                    Held = null, HeldReviewed = false, RetryAfter = DateTimeOffset.MinValue,
                    MissingDeleteIdentityReview = false, PendingNotice = null
                });
                _store.CloseNotice(noticeId, choice);
                _store.RefreshBlockedDeleteNotice(pairId);
                return NoticeChoices.Result(choice, notice);
            }
            if (choice == NoticeChoices.RetryNow)
            {
                if (notice.ReasonKey == PlanNotices.CannotRead.Key)
                {
                    var unreadable = _store.SyncRetryAt(pairId, notice.Path) ??
                        new SyncRetry(notice.Path, "scan", 0, notice.Detail, DateTimeOffset.MinValue);
                    _store.SaveSyncRetry(pairId, unreadable with { RetryAfter = DateTimeOffset.MinValue,
                        ReadRoots = new[] { pair.A.RootId, pair.B.RootId } });
                    return "Exact-path read retry requested. Existing content and deletion protection remain unchanged.";
                }
                if (_store.SyncRetryAt(pairId, notice.Path) is { } retry)
                    _store.SaveSyncRetry(pairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                else
                    _store.SaveSyncRetry(pairId, new SyncRetry(notice.Repair?.Destination ?? notice.Path, "sync", 0, notice.Detail, DateTimeOffset.MinValue));
                // Keep the notice visible until verified success. A retry request is not success.
                return NoticeChoices.Result(choice, notice);
            }
            if (choice == NoticeChoices.Exclude)
            {
                lock (_noticeChoiceGate)
                {
                    pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is no longer available.");
                    var paths = CreateExclusions.Validate(pair.Excluded.Concat([notice.Path, notice.Repair?.Destination ?? notice.Path]).ToArray());
                    if (saveNarrowerPair is not null) saveNarrowerPair(pair, pair with { Excluded = paths });
                    else _store.SavePair(pair with { Excluded = paths });
                }
                var retry = _store.SyncRetryAt(pairId, notice.Path);
                if (retry?.Held is not null) _store.SuspendHeldRetry(pairId, retry);
                else _store.ClearSyncRetry(pairId, notice.Path);
                // Retain pending effects for verify-only reconciliation under the normal exclusion policy.
            }
            _store.CloseNotice(noticeId, choice);
            if (notice.ReasonKey == "deletion-review") _store.RefreshBlockedDeleteNotice(pairId);
            return NoticeChoices.Result(choice, notice);
        }, ct, admissionPairId: pairId, preserveAcceptedChoice: acceptedExclusion is not null);
}
