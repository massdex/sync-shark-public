using System.Collections.Concurrent;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Runs one planned action through the ordered, conditional steps of executor.md section 4.</summary>
internal sealed class ActionRunner
{
    private const string ParentNotReady = "Parent folder not ready: ";
    private const string FolderNotEmptied = "Folder not emptied: ";
    private const string RecoveryFailed = "Recovery copy failed; nothing changed: ";
    private const string UncertainReason = "Could not verify: effect outcome unknown";
    private const string UnexpectedKind = "Unexpected effect kind ";

    private readonly StoreCommitter _committer;
    private readonly AdmissionAbsenceBatch _absenceBatch = new();
    private readonly DropboxQuota _quota;
    private readonly SyncTransferResourceController _admission;
    private readonly string _pairId;
    private readonly TimeSpan _admissionRetryDelay;
    private readonly TimeSpan _unknownCommitGracePeriod;
    private readonly IEndpointWriter _a;
    private readonly IEndpointWriter _b;
    private readonly IReadOnlyList<SyncAction> _dependencies;
    private readonly IReadOnlyDictionary<(Side, int, string), ItemState> _replacementActions;
    private readonly ConcurrentDictionary<string, Outcome> _outcomes;
    private readonly ConcurrentDictionary<string, Outcome> _actionOutcomes = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<(Side, string), List<SyncAction>> _pairedEffects;
    private readonly HashSet<string> _pairedActionIds;
    private readonly IReadOnlyDictionary<string, SyncAction[]> _conflictSuccessors;
    private readonly HashSet<string> _conflictForgets;
    private readonly ConcurrentDictionary<string, byte> _durableConflicts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _refusedParents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _grantHeldParents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _sourceBlockedDeletes = new(StringComparer.Ordinal);

    internal ActionRunner(StoreCommitter committer, V2Settings settings, SyncTransferResourceController admission,
        string pairId, IEndpointWriter a, IEndpointWriter b, IReadOnlyList<SyncAction> dependencies,
        ConcurrentDictionary<string, Outcome> outcomes, DropboxQuota? quota = null,
        IReadOnlyList<SyncAction>? replacementActions = null)
    {
        _committer = committer;
        _quota = quota ?? new DropboxQuota(null, false);
        _admission = admission;
        _pairId = pairId;
        _admissionRetryDelay = settings.AdmissionRetryDelay;
        _unknownCommitGracePeriod = settings.UnknownCommitGracePeriod;
        _a = a;
        _b = b;
        _dependencies = dependencies;
        var planActions = replacementActions ?? dependencies;
        _conflictForgets = planActions.Where(x => x.Kind == ActionKind.Forget && x.Rule == 27 && x.ObjectKey is not null)
            .Select(x => x.ObjectKey!).ToHashSet(StringComparer.Ordinal);
        _conflictSuccessors = planActions.Where(x => x.ConflictTransitionKey is not null)
            .GroupBy(x => x.ConflictTransitionKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        _replacementActions = DeleteSourceGuard.Replacements(planActions);
        _pairedEffects = IndexPairedEffects(planActions);
        _pairedActionIds = _pairedEffects.Values.SelectMany(items => items).Select(item => item.ActionId)
            .ToHashSet(StringComparer.Ordinal);
        _outcomes = outcomes;
    }

    /// <summary>The committed result of the action, or null when a cancelled action records nothing.</summary>
    internal async Task<ActionResult?> RunAsync(SyncAction action, CancellationToken ct)
    {
        var result = await RunStepsAsync(action, ct).ConfigureAwait(false);
        if (result is not null && _pairedActionIds.Contains(action.ActionId)) _actionOutcomes[action.ActionId] = result.Outcome;
        if (result is not null && action.Kind is ActionKind.MkDir or ActionKind.Move or ActionKind.Delete)
        {
            if ((result.ProviderNameRefused || result.ParentNameRefused) &&
                (action.Kind == ActionKind.MkDir || action.Source?.Kind == ItemKind.Folder))
                _refusedParents[OutcomeKey(action)] = 0;
            if (result.DefenderGrantHeld && (action.Kind == ActionKind.MkDir || action.Source?.Kind == ItemKind.Folder))
                _grantHeldParents[OutcomeKey(action)] = 0;
            if (result.DeleteSourceUnconfirmed) _sourceBlockedDeletes[OutcomeKey(action)] = 0;
            _outcomes[OutcomeKey(action)] = result.Outcome;
        }
        return result;
    }

    private async Task<ActionResult?> RunStepsAsync(SyncAction action, CancellationToken ct)
    {
        if (await CommitOnlyOrSkipAsync(action, ct).ConfigureAwait(false) is { } early) return early;
        if (_quota.Blocks(action))
            return await CommitAsync(action, Failed(action, DropboxQuota.Detail) with { UploadQuotaBlocked = true });
        if (RecoverySafety.RequiresRecovery(action) && RecoverySafety.Unavailable(_a, _b) is { } recovery)
            return Failed(action, "Recovery location unavailable: " + recovery);
        var target = action.Target == Side.A ? _a : _b;
        var source = action.Target == Side.A ? _b : _a;
        return OwnedPointerUpdates.IsMetadataUpdate(action)
            ? await _committer.RunPointerUpdateAsync(action,
                () => WithLeaseAsync(action, target, source, ct), ct).ConfigureAwait(false)
            : await WithLeaseAsync(action, target, source, ct).ConfigureAwait(false);
    }

    // Steps 1 and 2; null means "run the effect".
    private async Task<ActionResult?> CommitOnlyOrSkipAsync(SyncAction action, CancellationToken ct)
    {
        if (action.ConflictTransitionKey is { } transition && _conflictForgets.Contains(transition) &&
            !_durableConflicts.ContainsKey(transition))
            return Failed(action, "Conflict transition was not committed; original correspondence retained.");
        if (action.Kind == ActionKind.Adopt && await GrantHoldAsync(action).ConfigureAwait(false) is { } held) return held;
        if (action.Kind == ActionKind.Forget && action.Rule == 27)
        {
            ct.ThrowIfCancellationRequested();
            if (action.ObjectKey is null || !_conflictSuccessors.TryGetValue(action.ObjectKey, out var successors))
                throw new InvalidOperationException("Conflict correspondence cannot be forgotten without its successors.");
            var result = CommitOnlyResult(action);
            await _committer.CommitConflictTransitionAsync(action, result, successors).ConfigureAwait(false);
            _durableConflicts[action.ObjectKey] = 0;
            return result;
        }
        if (action.Kind is ActionKind.Adopt or ActionKind.Forget)
            return await CommitAsync(action, CommitOnlyResult(action)).ConfigureAwait(false);
        if (BlockingParent(action) is { } parent)
            return await CommitAsync(action, Skipped(action, ParentNotReady + parent.Path) with
            {
                ParentNameRefused = _refusedParents.ContainsKey(OutcomeKey(parent)),
                DefenderGrantHeld = _grantHeldParents.ContainsKey(OutcomeKey(parent))
            }).ConfigureAwait(false);
        return UndeletedDescendant(action) is { } child
            ? await CommitAsync(action, Skipped(action, FolderNotEmptied + child) with
            {
                DeleteSourceUnconfirmed = _sourceBlockedDeletes.ContainsKey("delete|" + action.Target + "|" + child.ToLowerInvariant())
            }).ConfigureAwait(false)
            : null;
    }

    // The transfer lease is acquired first; free-space admission then runs immediately before the durable intent, so
    // a reservation is never held while a long admission queue waits. A refusal releases the transfer lease and
    // leaves the item pending: never a failed or replayed effect.
    private async Task<ActionResult?> WithLeaseAsync(SyncAction action, IEndpointWriter target,
        IEndpointWriter source, CancellationToken ct)
    {
        SyncTransferResourceController.Lease lease;
        try
        {
            lease = await AcquireAsync(action, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            return await CommitAsync(action, Failed(action, Describe(e)) with { FailureHResult = e.HResult }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var freeSpace = FreeSpaceAdmission.Granted;
        var success = false;
        SyncTransferResourceController.Lease.Current.Value = lease;
        var previousContext = ProviderEffectContext.Current.Value;
        try
        {
            if (await GrantHoldAsync(action).ConfigureAwait(false) is { } held) return held;
            // Rule 12 checks its surviving bytes once, after the intent/admission waits below.
            if ((action.Kind != ActionKind.Delete || action.Rule != 12) &&
                await DeleteSourceGuard.CheckAsync(action, source, _committer, _pairId, ct,
                DeleteSourceGuard.Replacement(action, _replacementActions),
                pairedAdmitted: await PairedDeleteAdmittedAsync(action, ct).ConfigureAwait(false),
                admissionBatch: _absenceBatch).ConfigureAwait(false) is { } blocked)
                return await CommitAsync(action, blocked).ConfigureAwait(false);
            freeSpace = target is ILocalFreeSpaceAdmission admission
                ? await admission.AdmitFreeSpaceAsync(action, ct).ConfigureAwait(false)
                : FreeSpaceAdmission.Granted;
            if (freeSpace.Refusal is { } refusal)
                // Refusal is retryable, not a conditional mismatch ("changed, kept").
                return action.CallerOwnsNotice
                    ? await CommitAsync(action, Refused(action, refusal.Detail)).ConfigureAwait(false)
                    : await CommitAsync(action,
                        await PendingForFreeSpaceAsync(action, refusal).ConfigureAwait(false)).ConfigureAwait(false);

            if (_quota.Blocks(action))
                return await CommitAsync(action, Failed(action, DropboxQuota.Detail) with { UploadQuotaBlocked = true });
            var providerContext = _committer.CreateProviderContext(action);
            if (DeleteSourceGuard.RequiresAbsence(action))
            {
                // Store evidence is captured before the provider queue takes any namespace gate.
                var historical = _committer.DeleteSource(action);
                providerContext.RevalidateDelete = async () =>
                {
                    if (historical is null || source is not ISourceAbsenceProbe probe) return new(null, null);
                    try { return await _absenceBatch.ProbeAsync(probe, historical, action.Path, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not (OutOfMemoryException or PairPausedException))
                    { return new(null, AbsenceObservation.Cause(error)); }
                };
                // Only refused members need the full guard result; the provider invokes this after releasing its gate.
                providerContext.RefuseDelete = observation => DeleteSourceGuard.CheckAsync(action, source,
                    _committer, _pairId, ct, preprobed: true, absence: observation.Absent, absenceReason: observation.Reason);
            }
            ProviderEffectContext.Current.Value = providerContext;
            // Step 4: the intent is durable before any effect is attempted (group-committed with its neighbours').
            await _committer.RecordIntentAsync(action, ct).ConfigureAwait(false);

            // Rule 12's survivor proof must follow admission and intent-write waits: local
            // preservation can itself remove the old copy. Planned Adopt success is not proof.
            if (action.Kind == ActionKind.Delete && action.Rule == 12 &&
                await DeleteSourceGuard.CheckAsync(action, source, _committer, _pairId, ct,
                    pairedAdmitted: await PairedDeleteAdmittedAsync(action, ct).ConfigureAwait(false))
                    .ConfigureAwait(false) is { } staleSurvivor)
                return await CommitAsync(action, staleSurvivor).ConfigureAwait(false);

            // Step 5: RecoveryFirst never overwrites or deletes without an archive; a failure stops the action.
            // A direct download defers the archive until its bytes are verified, so a failed or cancelled download
            // leaves the original in place; its RecoveryRef is carried out through the holder.
            var deferPreserve = action.RecoveryFirst && action.ExpectedTarget is not null &&
                IsDirectDownload(action, target, source);
            var recoveryHolder = new RecoveryHolder();
            if (!deferPreserve)
            {
                var (recovery, failure) = await PreserveAsync(action, target, ct).ConfigureAwait(false);
                if (failure is not null) return await CommitAsync(action, failure).ConfigureAwait(false);
                recoveryHolder.Reference = recovery;
                // A remote archive is metadata-only and the local owner already persisted its file archive through
                // the same recovery store before removal. Persist here too, idempotently, so the indexed receipt
                // exists even if the effect's result is never committed.
                if (recovery is not null && action.ExpectedTarget is { Kind: ItemKind.File } victim)
                    await _committer.RecordRecoveryAsync(action.ActionId, victim.Path, recovery, ct)
                        .ConfigureAwait(false);
            }

            var result = await ExecuteAsync(action, target, source, recoveryHolder, deferPreserve, ct)
                .ConfigureAwait(false);
            success = result?.Outcome == Outcome.Done;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            freeSpace.Reservation?.Dispose();
            ProviderEffectContext.Current.Value = previousContext;
            lease.Complete(success);
            SyncTransferResourceController.Lease.Current.Value = null;
        }
    }

    private static IReadOnlyDictionary<(Side, string), List<SyncAction>> IndexPairedEffects(IReadOnlyList<SyncAction> actions)
    {
        var needed = actions.Where(action => action.Kind == ActionKind.Delete && action.Rule is 4 or 12 && action.Source is not null)
            .Select(action => (action.Target == Side.A ? Side.B : Side.A, action.Source!.ObjectId)).ToHashSet();
        var index = new Dictionary<(Side, string), List<SyncAction>>();
        if (needed.Count == 0) return index;
        foreach (var action in actions)
        {
            if (action.Kind is not (ActionKind.Move or ActionKind.Adopt or ActionKind.Update)) continue;
            Add(action.Target == Side.A ? Side.B : Side.A, action.Source, action);
            Add(action.Target, action.ExpectedTarget, action);
        }
        return index;

        void Add(Side side, ItemState? item, SyncAction action)
        {
            if (item is null || !needed.Contains((side, item.ObjectId))) return;
            if (!index.TryGetValue((side, item.ObjectId), out var matches))
                index[(side, item.ObjectId)] = matches = new List<SyncAction>();
            matches.Add(action);
        }
    }

    // Collision deletes are authorized by a specific same-plan effect, never by a rule number alone.
    // A vacating delete precedes its Move, so admit that Move read-only without writing a speculative intent.
    private async Task<bool> PairedDeleteAdmittedAsync(SyncAction action, CancellationToken ct)
    {
        if (action.Kind != ActionKind.Delete || action.Rule is not (4 or 12) ||
            action.Source is not { } incoming || !action.RecoveryFirst) return false;
        var sourceSide = action.Target == Side.A ? Side.B : Side.A;
        if (!_pairedEffects.TryGetValue((sourceSide, incoming.ObjectId), out var candidates)) return false;
        foreach (var paired in candidates)
        {
            var matches = action.Rule == 12
                ? paired.Kind == ActionKind.Adopt && PairPath.Same(paired.Path, incoming.Path) &&
                    !PairPath.Same(action.Path, paired.Path)
                : (paired.Kind == ActionKind.Move && paired.Target == action.Target &&
                    PairPath.Same(paired.Path, action.Path) && paired.Source?.ObjectId == incoming.ObjectId) ||
                  (paired.Kind == ActionKind.Update && paired.Target == sourceSide &&
                    PairPath.Same(paired.Path, incoming.Path) && paired.ExpectedTarget?.ObjectId == incoming.ObjectId);
            if (!matches) continue;
            if (_actionOutcomes.TryGetValue(paired.ActionId, out var outcome))
            {
                if (outcome != Outcome.Done) continue;
                if (paired.Kind != ActionKind.Adopt) return true;
                // Adopt records planned metadata without observing bytes. Only its destructive
                // dependant needs fresh proof of the copy that will survive on the delete side.
                var survivor = paired.Target == action.Target ? paired.ExpectedTarget : paired.Source;
                if (survivor is null) continue;
                try
                {
                    var endpoint = action.Target == Side.A ? _a : _b;
                    var observed = await endpoint.ObserveAsync(survivor.Path, ct).ConfigureAwait(false);
                    if (observed is not null && observed.ObjectId == survivor.ObjectId &&
                        observed.Kind == survivor.Kind && Content.SameContent(observed, survivor) == true)
                        return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not (OutOfMemoryException or PairPausedException)) { /* Unknown is not admission. */ }
                continue;
            }
            // Adopt runs in the earlier commit-only phase and Update before removal. A still-pending
            // one is not completion evidence. Only a Move may depend on this vacating Delete.
            if (paired.Kind != ActionKind.Move || paired.FromPath is null || paired.Source is null ||
                paired.ExpectedTarget is null || _quota.Blocks(paired) ||
                _committer.DefenderGrantConflict(paired) is not null || BlockingParent(paired) is not null ||
                (RecoverySafety.RequiresRecovery(paired) && RecoverySafety.Unavailable(_a, _b) is not null)) continue;
            try
            {
                var source = paired.Target == Side.A ? _b : _a;
                var target = paired.Target == Side.A ? _a : _b;
                if (source is not ISourceAbsenceProbe sourceProbe || target is not ISourceAbsenceProbe targetProbe) continue;
                var currentSource = await sourceProbe.ObserveSourceMetadataAsync(paired.Source.Path, ct).ConfigureAwait(false);
                var currentTarget = await targetProbe.ObserveSourceMetadataAsync(paired.FromPath, ct).ConfigureAwait(false);
                if (currentSource is not null && currentTarget is not null &&
                    currentSource.ObjectId == paired.Source.ObjectId && currentSource.Kind == paired.Source.Kind &&
                    currentTarget.ObjectId == paired.ExpectedTarget.ObjectId && currentTarget.Kind == paired.ExpectedTarget.Kind)
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            // A pause is not inconclusive evidence: propagate it instead of treating the pair as not admitted.
            catch (Exception error) when (error is not (OutOfMemoryException or PairPausedException)) { /* Unknown is not admission. */ }
        }
        return false;
    }

    private async Task<ActionResult?> GrantHoldAsync(SyncAction action)
    {
        if (_committer.DefenderGrantConflict(action) is not { } grant) return null;
        var warning = QuarantineFailure.GrantWarning(grant.Path);
        var notice = QuarantineFailure.Notice(_pairId, action, warning);
        await _committer.AddNoticeAsync(notice with { Quarantine = notice.Quarantine! with
            { ConflictingGrantId = grant.GrantId, GrantedExclusionPath = grant.Path, GrantedStagePath = grant.StagePath } }).ConfigureAwait(false);
        return await CommitAsync(action, Skipped(action, warning) with { DefenderGrantHeld = true }).ConfigureAwait(false);
    }

    // A proven shortage (or unprovable free space) leaves the item pending: a stored notice and a Skipped result.
    // The caller commits it, so a preserved destructive replace can still stay protective (Uncertain).
    private async Task<ActionResult> PendingForFreeSpaceAsync(SyncAction action, FreeSpaceRefusal refusal)
    {
        await _committer.AddNoticeAsync(new Notice(_pairId, action.Rule, NoticeKind.Info, action.Path,
            refusal.Detail, refusal.ReasonKey, FreeSpace: new FreeSpaceNoticeMetadata(refusal.VolumeRoot,
                refusal.RequiredBytes, refusal.FreeBytes, refusal.ReserveBytes))).ConfigureAwait(false);
        return Skipped(action, refusal.Detail);
    }

    // Steps 6 and 7: run the conditional effect once, map its exceptions, and commit the outcome.
    private async Task<ActionResult?> ExecuteAsync(SyncAction action, IEndpointWriter target, IEndpointWriter source,
        RecoveryHolder recoveryHolder, bool deferPreserve, CancellationToken ct)
    {
        // Once an archive exists, every settled outcome must carry it, and a non-Done destructive replace must not
        // close its intent as Skipped/Refused: the target original was already removed, so a later plan would read
        // the absence as a user deletion. Keep such an outcome protective (Uncertain) until reconciliation observes.
        async Task<ActionResult?> SettleAsync(ActionResult result)
        {
            if (result.Recovery is null && recoveryHolder.Reference is { } retained)
                result = result with { Recovery = retained };
            var preservedDestructive = RecoverySafety.HasPreservedFileVictim(action) &&
                (recoveryHolder.Reference is not null || recoveryHolder.CheckpointRecorded);
            if (preservedDestructive && result.Outcome is Outcome.Skipped or Outcome.Refused)
                result = result with
                {
                    Outcome = Outcome.Uncertain,
                    Recovery = result.Recovery ?? recoveryHolder.Reference,
                    Reason = "The displaced original is retained; this effect did not complete. " +
                        "Reconcile will prove the target state; restore from Recovery if needed. " + result.Reason,
                    RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds,
                    ProviderNameRefused = false
                };
            return await CommitAsync(action, result).ConfigureAwait(false);
        }

        ItemState? newT;
        try
        {
            newT = await EffectAsync(action, target, source, recoveryHolder, deferPreserve, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (QuarantineFailure.IsQuarantine(error))
        {
            NativeIoDiagnostic.LogOnce($"pair={_pairId}; relative={action.Path}", error);
            return await SettleAsync(new ActionResult(action.ActionId, Outcome.Skipped, null, null,
                recoveryHolder.Reference, QuarantineFailure.Describe(action.Path, error)) { Quarantined = true })
                .ConfigureAwait(false);
        }
        catch (ProviderNameRefusedException) when (action.ResolvedNoticeId is null && action.Source is { } item &&
            string.Equals(item.Path, action.Path, StringComparison.Ordinal) &&
            action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.Move or ActionKind.MkDir or ActionKind.KeepBoth)
        {
            // Settled but not performed: never satisfy a child dependency or commit a sync baseline.
            return await SettleAsync(new ActionResult(action.ActionId, Outcome.Skipped, null, null,
                recoveryHolder.Reference, ProviderNameRefusedException.Detail) { ProviderNameRefused = true })
                .ConfigureAwait(false);
        }
        catch (ProviderWriteBlockedException e) when (e.Code == "insufficient_space")
        {
            if (_quota.Suspend())
                await _committer.AddNoticeAsync(DropboxQuota.Notice(_pairId, action.Target, action.Source?.Size));
            return await SettleAsync(
                Failed(action, DropboxQuota.Detail) with { UploadQuotaBlocked = true });
        }
        catch (FreeSpaceRefusedException refused) when (!recoveryHolder.CheckpointRecorded)
        {
            // The up-front admission reserved metadata only (an online-only new file), so the writer re-admitted its
            // real bytes and refused. Report the same pending free-space notice as an up-front refusal: the reason
            // key and volume facts aggregate per volume, and the Skipped result leaves the item unsynced and retryable.
            // A notice-owned action has its own receipt: its caller raises the single notice for this
            // failure, so adding a path notice here would duplicate it (and that path notice could close on a later
            // sync without the restore ever running). Commit the pending outcome alone and let the restore report it.
            // The refusal precedes any effect, so a checkpoint-bearing action must never be retired as Refused or Skipped.
            return action.CallerOwnsNotice
                ? await SettleAsync(Refused(action, refused.Refusal.Detail)).ConfigureAwait(false)
                : await SettleAsync(await PendingForFreeSpaceAsync(action, refused.Refusal).ConfigureAwait(false))
                    .ConfigureAwait(false);
        }
        catch (OwnedPointerDeferredException deferred) when (OwnedPointerUpdates.IsMetadataUpdate(action))
        {
            await _committer.DeferPointerUpdateAsync(action, deferred.Cause).ConfigureAwait(false);
            return await SettleAsync(Skipped(action, deferred.Message)).ConfigureAwait(false);
        }
        catch (StoragePairException error) when (OwnedPointerUpdates.IsMetadataUpdate(action))
        {
            await _committer.DeferPointerUpdateAsync(action, error).ConfigureAwait(false);
            return await SettleAsync(Skipped(action, error.Message)).ConfigureAwait(false);
        }
        catch (DeleteSourceUnconfirmedException blocked)
        {
            return await CommitAsync(action, blocked.Result).ConfigureAwait(false);
        }
        catch (ProviderWriteThrottledException e)
        {
            return await SettleAsync(Skipped(action, e.Message)).ConfigureAwait(false);
        }
        catch (ExpectedTargetMismatchException e)
        {
            return await SettleAsync(Skipped(action, e.Message)).ConfigureAwait(false);
        }
        catch (UncertainEffectException)
        {
            return await ResolveUncertainAsync(action, target, source, recoveryHolder.Reference, ct).ConfigureAwait(false);
        }
        catch (DeferredFailureException deferred)
        {
            // The deferred Recovery archive refused or failed after the download was verified. Once the checkpoint
            // is durable, keep the action unresolved so reconcile can prove the original's state instead of
            // clearing it from a single observation.
            return await SettleAsync(recoveryHolder.CheckpointRecorded
                ? new ActionResult(action.ActionId, Outcome.Uncertain, null, null, null, deferred.Result.Reason)
                : deferred.Result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (EndpointAccessException e)
        {
            NativeIoDiagnostic.LogOnce($"pair={_pairId}; relative={action.Path}", e);
            throw;
        }
        // A user pause is not an effect failure: propagate it so the run reports Paused and the durable intent is
        // reconciled (never replayed blind) on the next run, instead of committing a false Failed/Uncertain result.
        catch (Exception e) when (e is not (EndpointAccessException or PairPausedException))
        {
            NativeIoDiagnostic.LogOnce($"pair={_pairId}; relative={action.Path}", e);
            // An access failure is not committed: the intent stays unresolved and the next run observes it. After a
            // durable download checkpoint the same holds for any other failure, so resume keeps the provenance.
            return await SettleAsync(recoveryHolder.CheckpointRecorded
                ? Uncertain(action, e)
                : Failed(action, Describe(e)) with { FailureHResult = e.HResult }).ConfigureAwait(false);
        }

        var done = DoneResult(action, newT, recoveryHolder.Reference);
        await SettleAsync(done).ConfigureAwait(false);
        return done;
    }

    private async Task<ItemState?> EffectAsync(SyncAction action, IEndpointWriter target, IEndpointWriter source,
        RecoveryHolder recoveryHolder, bool deferPreserve, CancellationToken ct) => action.Kind switch
        {
            ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth =>
                await CopyAsync(action, target, source, recoveryHolder, deferPreserve, ct).ConfigureAwait(false),
            ActionKind.Move => await target.MoveAsync(action, ct).ConfigureAwait(false),
            ActionKind.Delete => await DeleteAsync(action, target, ct).ConfigureAwait(false),
            ActionKind.MkDir => await target.MkDirAsync(action.Path, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException(UnexpectedKind + action.Kind),
        };

    private static bool IsDirectDownload(SyncAction action, IEndpointWriter target, IEndpointWriter source) =>
        action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth &&
        target is ISyncDirectDownloadTarget && source is ISyncDirectDownloadSource;

    private async Task<ItemState?> CopyAsync(SyncAction action, IEndpointWriter target, IEndpointWriter source,
        RecoveryHolder recoveryHolder, bool deferPreserve, CancellationToken ct)
    {
        if (action.Source is null) throw new InvalidOperationException("Source missing for " + action.ActionId);
        if (OwnedPointerUpdates.IsMetadataUpdate(action))
        {
            if (target is not IOwnedPointerUpdateTarget pointers)
                throw new IOException("The endpoint cannot update online-only metadata. Reconnect the owned root and retry.");
            return await pointers.UpdateOwnedPointerAsync(action, source, ct).ConfigureAwait(false);
        }
        // A remote source and a local target download straight into the target's own temporary file: no product
        // spool, so the target volume holds one copy. Any other pair keeps the streaming handoff.
        if (target is ISyncDirectDownloadTarget direct && source is ISyncDirectDownloadSource remote)
        {
            Func<SyncDownloadStage, CancellationToken, Task> preservedAfterVerify =
                (stage, token) => PreserveAfterVerifyAsync(action, target, recoveryHolder, stage, token);
            return await direct.PutDownloadedAsync(action, remote, preservedAfterVerify, ct).ConfigureAwait(false);
        }
        var content = await source.OpenReadAsync(action.Source, ct).ConfigureAwait(false);
        await using var owner = new SyncUploadSource(content, () => _quota.CheckUploadStart(action));
        var previous = SyncUploadSource.Current.Value;
        SyncUploadSource.Current.Value = owner;
        try { return await target.PutAsync(action, content, ct).ConfigureAwait(false); }
        finally { SyncUploadSource.Current.Value = previous; }
    }

    private static async Task<ItemState?> DeleteAsync(SyncAction action, IEndpointWriter target, CancellationToken ct)
    {
        await target.DeleteAsync(action, ct).ConfigureAwait(false);
        return null;
    }

    // Owner decision: the overwritten original is archived only after the downloaded bytes are verified. A
    // successful archive removes the planned original, so the verified partial is published create-only. One
    // narrow durable checkpoint (existing provider-effect evidence, bound to this action) records the verified
    // stage before the archive, so a crash can finish the publish or restore the original.
    private async Task PreserveAfterVerifyAsync(SyncAction action, IEndpointWriter target,
        RecoveryHolder holder, SyncDownloadStage stage, CancellationToken ct)
    {
        if (action.RecoveryFirst && action.ExpectedTarget is null)
            throw new InvalidOperationException("A deferred Recovery step needs its planned victim.");
        var context = ProviderEffectContext.Current.Value ??
            throw new InvalidOperationException("A deferred download needs its durable checkpoint sink.");
        await context.RecordAsync(SyncDirectDownloadCheckpoint.Kind, JsonSerializer.Serialize(stage))
            .ConfigureAwait(false);
        holder.CheckpointRecorded = stage.Verified && action.RecoveryFirst;
        if (!stage.Verified || !action.RecoveryFirst) return;
        var (reference, failure) = await PreserveAsync(action, target, ct).ConfigureAwait(false);
        if (failure is not null) throw new DeferredFailureException(failure);
        holder.Reference = reference;
    }

    private async Task<(RecoveryRef? Recovery, ActionResult? Failure)> PreserveAsync(SyncAction action,
        IEndpointWriter target, CancellationToken ct)
    {
        if (!action.RecoveryFirst) return (null, null);
        // Planner bug: RecoveryFirst without a victim must fail loudly, not delete without a recovery copy.
        if (action.ExpectedTarget is null)
            throw new InvalidOperationException("RecoveryFirst action without an ExpectedTarget: " + action.ActionId);
        try
        {
            return (await target.PreserveAsync(action.ExpectedTarget, action.ActionId, ct).ConfigureAwait(false), null);
        }
        catch (ExpectedTargetMismatchException e)
        {
            // The victim changed since planning: nothing was changed, so re-plan rather than record a failure.
            return (null, Skipped(action, e.Message));
        }
        catch (Exception e) when (QuarantineFailure.IsQuarantine(e))
        {
            NativeIoDiagnostic.LogOnce($"pair={_pairId}; relative={action.Path}", e);
            return (null, new ActionResult(action.ActionId, Outcome.Skipped, null, null, null,
                QuarantineFailure.Describe(action.Path, e)) { Quarantined = true });
        }
        catch (Exception e) when (e is not (OperationCanceledException or EndpointAccessException))
        {
            NativeIoDiagnostic.LogOnce($"pair={_pairId}; relative={action.Path}", e);
            return (null, Failed(action, RecoveryFailed + Describe(e)) with { FailureHResult = e.HResult });
        }
    }

    private async Task<ActionResult> ResolveUncertainAsync(SyncAction action, IEndpointWriter target,
        IEndpointWriter source, RecoveryRef? preserved, CancellationToken ct)
    {
        var uncertain = new ActionResult(action.ActionId, Outcome.Uncertain, null, null, preserved, UncertainReason);
        await CommitAsync(action, uncertain).ConfigureAwait(false);
        var resolved = await EffectResolver.ResolveAsync(_committer.LatestAction(action), target, source, ct,
            _unknownCommitGracePeriod, historicalDeleteSource: action.Kind == ActionKind.Delete
                ? _committer.DeleteSource(action) : null, preserved: preserved).ConfigureAwait(false);
        // Still unverifiable: the committed Uncertain stands; report the actual failure in its reason.
        if (resolved.Outcome == Outcome.Uncertain) return resolved;
        if (resolved.Recovery is null && preserved is not null) resolved = resolved with { Recovery = preserved };
        return await CommitAsync(action, resolved).ConfigureAwait(false);
    }

    private async Task<SyncTransferResourceController.Lease> AcquireAsync(SyncAction action, CancellationToken ct)
    {
        using var timing = RunInstrumentation.Current?.Measure(RunTimingStage.AdmissionWait);
        // The admission retry is a tracked wait, announced once with its reason and delay (RQ-090).
        using var wait = RunOperations.Begin(RunOperationKind.TransferSlot, "resource admission");
        var metadata = OwnedPointerUpdates.IsMetadataUpdate(action);
        var bytes = metadata ? 0 : action.Source?.Size ?? 0;
        var file = !metadata && action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth;
        var target = action.Target == Side.A ? _a : _b;
        var source = action.Target == Side.A ? _b : _a;
        var keys = new List<SyncProviderBudgetKey>(2);
        if (target is ISyncTransferResourceEndpoint write)
            keys.Add(new(write.TransferResourceKey, SyncProviderOperation.Write));
        if ((file || metadata) && source is ISyncTransferResourceEndpoint read)
            keys.Add(new(read.TransferResourceKey, SyncProviderOperation.Read));
        var scope = _admission.ForOperations(keys);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (scope.TryAcquire(bytes, file) is { } lease) return lease;
            await Task.Delay(_admissionRetryDelay, ct).ConfigureAwait(false);
        }
    }

    private SyncAction? BlockingParent(SyncAction action)
    {
        foreach (var move in _dependencies)
        {
            if (move.Kind == ActionKind.Delete || move.Target != action.Target) continue;
            if (!_outcomes.TryGetValue(OutcomeKey(move), out var outcome) || outcome == Outcome.Done) continue;
            if (PairPath.IsSelfOrUnder(action.Path, move.Path) ||
                (action.FromPath is not null && PairPath.IsSelfOrUnder(action.FromPath, move.Path)))
                return move;
        }
        return null;
    }

    // A folder's own delete runs after its descendants' deletes (deepest first); unless every one of them is Done,
    // the folder still holds content, so it is kept (a failed or skipped child is never removed with its folder).
    private string? UndeletedDescendant(SyncAction action)
    {
        if (action.Kind != ActionKind.Delete || action.ExpectedTarget?.Kind != ItemKind.Folder) return null;
        foreach (var child in _dependencies)
        {
            if (child.Kind != ActionKind.Delete || child.Target != action.Target) continue;
            if (PairPath.Same(child.Path, action.Path) || !PairPath.IsSelfOrUnder(child.Path, action.Path)) continue;
            if (!_outcomes.TryGetValue(OutcomeKey(child), out var outcome) || outcome != Outcome.Done) return child.Path;
        }
        return null;
    }

    private static ActionResult CommitOnlyResult(SyncAction action) => action.Kind == ActionKind.Adopt
        ? new ActionResult(action.ActionId, Outcome.Done, action.Source, action.ExpectedTarget, null, null)
        : new ActionResult(action.ActionId, Outcome.Done, null, null, null, null);

    private static ActionResult DoneResult(SyncAction action, ItemState? newT, RecoveryRef? recovery)
    {
        if (action.Kind == ActionKind.Delete) return new ActionResult(action.ActionId, Outcome.Done, null, null, recovery, null);
        return action.Target == Side.A
            ? new ActionResult(action.ActionId, Outcome.Done, newT, action.Source, recovery, null)
            : new ActionResult(action.ActionId, Outcome.Done, action.Source, newT, recovery, null);
    }

    private static ActionResult Skipped(SyncAction action, string reason) =>
        new(action.ActionId, Outcome.Skipped, null, null, null, reason);

    private static ActionResult Refused(SyncAction action, string reason) =>
        new(action.ActionId, Outcome.Refused, null, null, null, reason);

    private static ActionResult Failed(SyncAction action, string reason) =>
        new(action.ActionId, Outcome.Failed, null, null, null, reason);

    // A post-checkpoint failure is not evidence that nothing happened; it stays unresolved for resume.
    private static ActionResult Uncertain(SyncAction action, Exception error) =>
        new(action.ActionId, Outcome.Uncertain, null, null, null, UncertainReason + ": " + Describe(error));

    // The failure reason carries the exception type, message and the deepest stack frame, so an unexpected
    // exception (for example a malformed provider body) names its source instead of vanishing into a bare type name.
    private static string Describe(Exception e)
    {
        var reason = BlockerDiagnostic.Describe(e);
        var frame = e.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(frame)) return reason;
        if (frame.Length > 240) frame = frame[..240];
        return reason + " [" + frame + "]";
    }

    /// <summary>The key under which a structure or delete action's outcome is recorded for the dependency checks.</summary>
    internal static string OutcomeKey(SyncAction action) =>
        (action.Kind == ActionKind.Delete ? "delete|" : "") + action.Target + "|" + action.Path.ToLowerInvariant();

    /// <summary>Carries the deferred Recovery archive out of the download callback to the final Done result.</summary>
    private sealed class RecoveryHolder
    {
        internal RecoveryRef? Reference { get; set; }

        /// <summary>
        /// True once the verified-stage checkpoint is durable. From then on a later failure must stay Unresolved
        /// (Uncertain) rather than be committed as Skipped/Failed, or the recovery bookkeeping would be lost.
        /// </summary>
        internal bool CheckpointRecorded { get; set; }
    }

    /// <summary>A deferred step's already-mapped result (Skipped or Failed) raised from inside the download.</summary>
    private sealed class DeferredFailureException(ActionResult result) : Exception(result.Reason)
    {
        internal ActionResult Result { get; } = result;
    }

    // The result is durable before it is reported; cancellation never abandons it (the effect may have happened).
    private async Task<ActionResult> CommitAsync(SyncAction action, ActionResult result)
    {
        using var timing = RunInstrumentation.Current?.Measure(RunTimingStage.ResultCommit);
        if (action.ConflictTransitionKey is not null && !action.SuppressReconciledBaseline &&
            result.Outcome is Outcome.Skipped or Outcome.Refused)
            result = result with { Outcome = Outcome.Failed };
        await _committer.CommitAsync(action, result).ConfigureAwait(false);
        return result;
    }
}
