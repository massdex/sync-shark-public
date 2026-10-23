namespace Massdex.SyncShark.V2;

/// <summary>Resolves an uncertain or previously interrupted effect by observing the endpoint (executor.md section 6).</summary>
internal static class EffectResolver
{
    internal const string NotApplied = "Not applied; re-planned this run";
    internal const string RestoreAbandoned = "Restore attempt abandoned; any created item stays in place and Recovery remains available.";
    internal const string Superseded = "Superseded by a later change";

    /// <summary>
    /// The resolved result. When the endpoint cannot be observed it is Uncertain (stays unresolved) and its reason
    /// carries the actual failure. A requested cancellation and an endpoint access failure propagate.
    /// </summary>
    internal static async Task<ActionResult> ResolveAsync(SyncAction x, IEndpointWriter target, IEndpointWriter source,
        CancellationToken ct, TimeSpan? unknownCommitGracePeriod = null, TimeProvider? timeProvider = null,
        ProviderJobCheckCache? jobChecks = null, string? materializationBlock = null,
        ItemState? historicalDeleteSource = null, RecoveryRef? preserved = null, bool deleteRestorePending = true)
    {
        // Restore creates only: without the atomic receipt we cannot claim ownership. Abandon the
        // intent, not the Recovery copy or notice, even if observation/provider lookup is unavailable.
        if (x.ConflictRestore)
            return Skipped(x, RestoreAbandoned);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var providerReportsInProgress = false;
        var providerTemporarilyUnavailable = false;
        var deleteJobTerminal = false;
        var deleteJobUnavailable = false;
        if (x.ProviderEffect is { Kind: "delete-job" or "mkdir-job" } checkpoint &&
            target is IProviderEffectReconciler reconciler)
        {
            try
            {
                var applied = await reconciler.ResolveProviderEffectAsync(x, jobChecks, ct).ConfigureAwait(false);
                deleteJobTerminal = checkpoint.Kind == "delete-job" && applied is not null;
                // Proven jobs still fall through to endpoint observation: creates need their new state,
                // and deletes need confirmation that the original source has not survived/reappeared.
                // A completed delete still needs both endpoint observations: its source may have
                // reappeared while the process was down. Never acknowledge absence as a user deletion.
                if (applied == false && x.Kind != ActionKind.Delete) return Skipped(x, NotApplied);
                // Dropbox definitively reports the job still in progress: it may still run, so the effect must
                // never expire by grace while the target survives, or a replan-restored copy could be deleted late.
                if (applied is null) providerReportsInProgress = true;
            }
            catch (Exception e) when (Unverifiable(e, ct) || x.Kind == ActionKind.Delete &&
                FailureRetryOwner.ScopeOf(e, ct) == FailureRetryOwner.FailureScope.Item &&
                !(e is OperationCanceledException && ct.IsCancellationRequested))
            {
                // A transient lookup failure is not proof that the job expired. Positive
                // observation may still prove completion, but do not expire a live job on an outage.
                providerTemporarilyUnavailable = ProviderTransientFailure.IsTransient(e, ct);
                deleteJobUnavailable = checkpoint.Kind == "delete-job" &&
                    e is DropboxEffectUncertainException { JobIdUnavailable: true };
            }
        }
        if (target is IProviderEffectReconciler resumable && resumable.CanResumeProviderEffect(x))
        {
            if (materializationBlock is not null) return Unverified(x, materializationBlock);
            try
            {
                var applied = await resumable.ResolveProviderEffectAsync(x, jobChecks, ct).ConfigureAwait(false);
                if (applied == false) return Skipped(x, Superseded);
                if (applied == true)
                {
                    var moved = await target.ObserveAsync(x.Path, ct).ConfigureAwait(false);
                    if (moved?.ObjectId == x.ExpectedTarget?.ObjectId)
                        return await DoneAsync(x, moved, target, ct, preserved).ConfigureAwait(false);
                }
                return Unverified(x, "Provider recovery remains pending.");
            }
            catch (Exception error) when (Unverifiable(error, ct))
            { return Unverified(x, Describe(error)) with
                { RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds }; }
        }
        // A direct download records one narrow durable checkpoint after verifying its stage and before archiving
        // the original. Until it is finished or rolled back, the action is unresolved; the run gate keeps the path
        // out of planning, so an absent target is never mistaken for a user deletion.
        if (x.ProviderEffect is { Kind: SyncDirectDownloadCheckpoint.Kind } downloadCheckpoint)
        {
            if (materializationBlock is not null)
            {
                // Observation alone may acknowledge publication, but never call resume (which can restore ACLs,
                // clean the stage, publish it, or restore Recovery bytes) while an exclusion guard conflicts.
                SyncDownloadStage? stage;
                try { stage = System.Text.Json.JsonSerializer.Deserialize<SyncDownloadStage>(downloadCheckpoint.Id); }
                catch (System.Text.Json.JsonException) { stage = null; }
                var observed = await ObserveAsync(target, x.Path, ct).ConfigureAwait(false);
                if (stage is not null && observed.State is { Kind: ItemKind.File, Hash: not null } published &&
                    published.ObjectId == stage.ObjectId && x.Source is { Hash: not null } original &&
                    Content.SameContent(published, original) == true)
                {
                    var warning = SyncDirectDownloadCheckpoint.NoticePrefix +
                        "Download publication was observed without touching files. Exclusion guard remains; inspect Recovery at " + stage.RecoveryPath;
                    return x.Target == Side.A
                        ? new(x.ActionId, Outcome.Done, published, x.Source, null, warning)
                        : new(x.ActionId, Outcome.Done, x.Source, published, null, warning);
                }
                return Unverified(x, materializationBlock);
            }
            // Without a resume implementation the state cannot be proven; never fall through to the generic
            // absent-target rule, which could plan a deletion over an interrupted download.
            if (target is not ISyncDirectDownloadTarget direct)
                return Unverified(x, "The local download checkpoint cannot be resumed by this endpoint.");
            try
            {
                return await direct.ResumeDownloadAsync(x, downloadCheckpoint, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (Unverifiable(e, ct))
            {
                return Unverified(x, "download resume: " + Describe(e));
            }
        }

        var path = await ObserveAsync(target, x.Path, ct).ConfigureAwait(false);
        if (path.Failure is { } failure) return Unverified(x, Describe(failure)) with
            { RetryAfterSeconds = x.Kind == ActionKind.Delete || ProviderTransientFailure.IsTransient(failure, ct)
                ? ProviderTransientFailure.VerificationRetrySeconds : null };
        var obs = path.State;
        if (x.Kind == ActionKind.Delete && x.ProviderEffect is { Kind: "delete-submit" } submission)
        {
            // CONSOLIDATED E8: Retry observes only; it never resubmits a deletion. An unchanged target
            // proves no effect only after the existing grace window, while changed content retires old authority.
            if (obs is null)
                return await ResolveAbsentDeleteAsync(x, target, source, historicalDeleteSource, preserved, ct, deleteRestorePending).ConfigureAwait(false);
            if (!SameAsExpected(obs, x.ExpectedTarget)) return Skipped(x, Superseded);
            var grace = unknownCommitGracePeriod ?? V2Settings.Default.UnknownCommitGracePeriod;
            if (submission.RecordedAtUtc is { } submitted && now - submitted >= grace)
                return Skipped(x, NotApplied);
            return Unverified(x, ExecutionReasons.ProviderPending.Detail) with
                { RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds };
        }
        // Unknown/expired job ids become finished-unknown after grace, never a proof of deletion.
        // Both endpoints (including trusted source identity) still decide the outcome below. An active
        // job or a transient lookup outage cannot expire, nor can a submission lacking a job id.
        if (x.Kind == ActionKind.Delete && x.ProviderEffect is { Kind: "delete-job" } deleteJob &&
            deleteJobUnavailable && !providerReportsInProgress && !providerTemporarilyUnavailable &&
            deleteJob.RecordedAtUtc is { } submittedDelete &&
            now - submittedDelete >= (unknownCommitGracePeriod ?? V2Settings.Default.UnknownCommitGracePeriod))
            deleteJobTerminal = true;
        // Even an absent target must not be restored underneath a provider job still running.
        if (x.Kind == ActionKind.Delete && (providerReportsInProgress || providerTemporarilyUnavailable ||
            x.ProviderEffect?.Kind == "delete-job" && !deleteJobTerminal))
            return Unverified(x, ExecutionReasons.ProviderPending.Detail) with
                { RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds };
        // An absent or old path does not prove an asynchronous commit has stopped running.
        // Keep its evidence until a read proves success or the provider reports a terminal rejection.
        var uploadMatches = DefinitelyMatches(obs, x.Source) &&
            (x.ExpectedTarget is null || string.Equals(obs?.ObjectId, x.ExpectedTarget.ObjectId, StringComparison.Ordinal));
        if (!deleteJobTerminal && x.ProviderEffect is { } effect && MayStillRun(effect, obs, uploadMatches))
        {
            // A job Dropbox still reports in progress is never expired by grace: grace applies only when the job is
            // unknown, expired or unavailable, so a possibly-running job can never be replanned over.
            if (providerReportsInProgress || providerTemporarilyUnavailable) return Unverified(x, ExecutionReasons.ProviderPending.Detail) with { RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds };
            var grace = unknownCommitGracePeriod ?? V2Settings.Default.UnknownCommitGracePeriod;
            var expired = effect.RecordedAtUtc is { } submitted &&
                now - submitted >= grace;
            var unchanged = x.Kind is ActionKind.CopyNew or ActionKind.KeepBoth or ActionKind.MkDir ? obs is null :
                SameAsExpected(obs, x.ExpectedTarget);
            if (expired) return Skipped(x, unchanged ? NotApplied : Superseded);
            if (x.ProviderEffect.RecordedAtUtc is not { } recorded)
                return Unverified(x, ExecutionReasons.ProviderUnknown.Detail);
            var remaining = grace - (now - recorded);
            return Unverified(x, ExecutionReasons.ProviderUnknown.Detail) with
                { RetryAfterSeconds = (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds), 1, int.MaxValue) };
        }

        switch (x.Kind)
        {
            case ActionKind.CopyNew or ActionKind.KeepBoth:
                if (DefinitelyMatches(obs, x.Source))
                    return await DoneAsync(x, obs, target, ct, preserved).ConfigureAwait(false);
                if (obs is null && RecoverySafety.HasPreservedFileVictim(x))
                    return await PreservedButAbsentAsync(x, preserved).ConfigureAwait(false);
                return Skipped(x, obs is null ? NotApplied : Superseded);
            case ActionKind.Update:
                if (DefinitelyMatches(obs, x.Source))
                    return await DoneAsync(x, obs, target, ct, preserved).ConfigureAwait(false);
                if (obs is null && RecoverySafety.HasPreservedFileVictim(x))
                    return await PreservedButAbsentAsync(x, preserved).ConfigureAwait(false);
                return Skipped(x, SameAsExpected(obs, x.ExpectedTarget) ? NotApplied : Superseded);
            case ActionKind.Move:
                return await ResolveMoveAsync(x, obs, target, ct, preserved).ConfigureAwait(false);
            case ActionKind.Delete:
                // Typed expired/invalid job + elapsed grace ends verify-only waiting, not deletion
                // authority. Without a trusted original identity retain history and park a user review.
                // This also covers an absent target: never infer the opposite user's deletion from it.
                if (deleteJobUnavailable && deleteJobTerminal &&
                    string.IsNullOrWhiteSpace(historicalDeleteSource?.ObjectId) &&
                    !DeleteSourceGuard.ExplicitRemoval(x) && x.Rule is not (26 or 32))
                    return Skipped(x, "The delete job expired without a trusted source identity. Review the item; nothing further was deleted.") with
                    { DeleteSourceUnconfirmed = true, DeleteSourceIdentityMissing = true };
                if (obs is not null) return Skipped(x, SameAsExpected(obs, x.ExpectedTarget) ? NotApplied : Superseded);
                return await ResolveAbsentDeleteAsync(x, target, source, historicalDeleteSource, preserved, ct, deleteRestorePending)
                    .ConfigureAwait(false);
            case ActionKind.MkDir:
                if (obs?.Kind == ItemKind.Folder)
                    return await DoneAsync(x, obs, target, ct, preserved).ConfigureAwait(false);
                // A create job Dropbox still reports in progress must not be replanned over: its own outcome
                // (present/refused) is definitive, so only an absent path with a live job stays unverified.
                if (providerReportsInProgress) return Unverified(x, ExecutionReasons.ProviderPending.Detail);
                return Skipped(x, obs is null ? NotApplied : Superseded);
            default:
                return Unverified(x, string.Format(ExecutionReasons.NoObservationRule.Detail, x.Kind));
        }
    }

    private static async Task<ActionResult> ResolveAbsentDeleteAsync(SyncAction action, IEndpointWriter target,
        IEndpointWriter source, ItemState? historical, RecoveryRef? preserved, CancellationToken ct, bool restorePending)
    {
        try
        {
            var blocked = await DeleteSourceGuard.CheckReconcileAsync(action, source, historical, restorePending, ct)
                .ConfigureAwait(false);
            return blocked ?? await DoneAsync(action, null, target, ct, preserved).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item)
        {
            return Unverified(action, "Interrupted deletion source check: " + Describe(error)) with
                { RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds };
        }
    }

    // True when a submitted provider effect may still be running, so its intent is retained under the grace policy
    // until a definite read (or the grace's own end) can close it: an upload commit whose bytes have not landed, a
    // delete whose target still shows the pre-effect picture, or a folder create whose path does not already hold
    // the created folder (an occupied path is not a definitive refusal, only a folder proves the create landed).
    private static bool MayStillRun(ProviderEffectCheckpoint effect, ItemState? obs, bool uploadMatches) =>
        effect.Kind switch
        {
            "upload-commit" => !uploadMatches,
            "delete-submit" or "delete-job" => obs is not null,
            "mkdir-submit" or "mkdir-job" => obs?.Kind != ItemKind.Folder,
            _ => false,
        };

    // A move is only Done when the source is gone and the destination holds the moved item, ExpectedTarget (its id,
    // plus its hash when both are known). Source is the opposite side and may differ (a move before an update).
    private static async Task<ActionResult> ResolveMoveAsync(SyncAction x, ItemState? obs, IEndpointWriter target,
        CancellationToken ct, RecoveryRef? preserved)
    {
        if (PairPath.Same(x.FromPath ?? x.Path, x.Path) && MoveLanded(obs, x) &&
            string.Equals(obs!.Path, x.Path, StringComparison.Ordinal))
            return await DoneAsync(x, obs, target, ct, preserved).ConfigureAwait(false);
        var from = await ObserveAsync(target, x.FromPath, ct).ConfigureAwait(false);
        if (from.Failure is { } failure) return Unverified(x, Describe(failure)) with
            { RetryAfterSeconds = ProviderTransientFailure.IsTransient(failure, ct) ? ProviderTransientFailure.VerificationRetrySeconds : null };
        if (from.State is not null) return Skipped(x, NotApplied);
        if (MoveLanded(obs, x)) return await DoneAsync(x, obs, target, ct, preserved).ConfigureAwait(false);
        if (obs is not null && x.ExpectedTarget is not null &&
            string.Equals(obs.ObjectId, x.ExpectedTarget.ObjectId, StringComparison.Ordinal) &&
            Content.SameContent(obs, x.ExpectedTarget) is null)
            return Unverified(x, "Moved file content is unconfirmed; retry verification before committing its baseline.");
        return Skipped(x, Superseded);
    }

    private static bool MoveLanded(ItemState? obs, SyncAction x) =>
        obs is not null && x.ExpectedTarget is not null &&
        string.Equals(obs.ObjectId, x.ExpectedTarget.ObjectId, StringComparison.Ordinal) &&
        HashAgrees(obs, x.ExpectedTarget);

    private static bool HashAgrees(ItemState obs, ItemState moved) =>
        Content.SameContent(obs, moved) == true;

    // The indexed receipt alone decides a RecoveryFirst file outcome. Repeating PreserveAsync here could archive or
    // remove an original that reappeared after the crash; a legacy intent without an indexed ref stays unresolved.
    // Folder victims carry no bytes and never require a receipt .
    private static Task<ActionResult> DoneAsync(SyncAction x, ItemState? newT, IEndpointWriter target,
        CancellationToken ct, RecoveryRef? preserved = null)
    {
        RecoveryRef? recovery = null;
        if (RecoverySafety.HasPreservedFileVictim(x))
        {
            recovery = preserved;
            if (recovery is null)
                return Task.FromResult<ActionResult>(Unverified(x,
                    "The displaced original's retained copy is not indexed; nothing further was changed. " +
                    "Review Recovery before retrying.") with { Recovery = preserved });
        }
        return Task.FromResult<ActionResult>(x.Kind == ActionKind.Delete
            ? new ActionResult(x.ActionId, Outcome.Done, null, null, recovery, null)
            : x.Target == Side.A
                ? new ActionResult(x.ActionId, Outcome.Done, newT, x.Source, recovery, null)
                : new ActionResult(x.ActionId, Outcome.Done, x.Source, newT, recovery, null));
    }

    // The target is absent and this action preserved a file victim before its effect. That absence is ours, not the
    // user's: closing the intent as a later change would leave a baseline whose next plan reads the absence as a
    // deletion and propagates it to the only remaining copy. Stay unresolved (the run protects the footprint) until
    // the retained original is restored or a later observation proves the intended state.
    private static Task<ActionResult> PreservedButAbsentAsync(SyncAction x, RecoveryRef? preserved) =>
        Task.FromResult<ActionResult>(new ActionResult(x.ActionId, Outcome.Uncertain, null, null, preserved,
            preserved is null
                ? "The original was preserved before this effect, but its retained copy is not indexed. Nothing further " +
                  "was changed; review Recovery before retrying."
                : "The displaced original is retained and awaits restoration; the absent target was produced by this " +
                  "action, not by a user deletion. Restore it from Recovery, then retry.")
            { RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds });

    private static async Task<(ItemState? State, Exception? Failure)> ObserveAsync(IEndpointWriter target,
        string? path, CancellationToken ct)
    {
        if (path is null) return (null, null);
        try
        {
            return (await target.ObserveAsync(path, ct).ConfigureAwait(false), null);
        }
        catch (Exception e) when (Unverifiable(e, ct))
        {
            return (null, e);
        }
    }

    // A failure that only leaves the effect unverified. A requested cancellation (the caller's token), a user pause
    // (the run reports Paused and the intent is reconciled next run) and an access failure (the next run observes it)
    // are not swallowed.
    internal static bool Unverifiable(Exception e, CancellationToken ct) =>
        FailureRetryOwner.ScopeOf(e, ct) == FailureRetryOwner.FailureScope.Item;

    private static ActionResult Unverified(SyncAction x, string detail) =>
        new(x.ActionId, Outcome.Uncertain, null, null, null, string.Format(ExecutionReasons.Unverified.Detail, detail));

    private static string Describe(Exception e) => BlockerDiagnostic.Describe(e);

    private static bool DefinitelyMatches(ItemState? obs, ItemState? source) =>
        obs is not null && source is not null && Content.SameContent(obs, source) == true;

    private static bool SameAsExpected(ItemState? obs, ItemState? expected) =>
        obs is not null && expected is not null && Content.SameVersion(obs, expected) == true;

    private static ActionResult Skipped(SyncAction x, string reason) =>
        new(x.ActionId, Outcome.Skipped, null, null, null, reason);
}