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
        ProviderJobCheckCache? jobChecks = null, string? materializationBlock = null)
    {
        // Restore creates only: without the atomic receipt we cannot claim ownership. Abandon the
        // intent, not the Recovery copy or notice, even if observation/provider lookup is unavailable.
        if (x.ConflictRestore)
            return Skipped(x, RestoreAbandoned);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var providerReportsInProgress = false;
        var providerTemporarilyUnavailable = false;
        if (x.ProviderEffect is { Kind: "delete-job" or "mkdir-job" } checkpoint &&
            target is IProviderEffectReconciler reconciler)
        {
            try
            {
                var applied = await reconciler.ResolveProviderEffectAsync(x, jobChecks, ct).ConfigureAwait(false);
                // A delete carries no post-effect state, so a proven job is Done as-is. A proven folder create
                // must instead fall through to the MkDir observation rule, which reads the created folder's state
                // back and returns Done with it (leaving the effect unverified when that folder cannot be read).
                if (applied == true && checkpoint.Kind == "delete-job")
                    return await DoneAsync(x, null, target, ct).ConfigureAwait(false);
                if (applied == false) return Skipped(x, NotApplied);
                // Dropbox definitively reports the job still in progress: it may still run, so the effect must
                // never expire by grace while the target survives, or a replan-restored copy could be deleted late.
                if (applied is null) providerReportsInProgress = true;
            }
            catch (Exception e) when (Unverifiable(e, ct))
            {
                // A transient lookup failure is not proof that the job expired. Positive
                // observation may still prove completion, but do not expire a live job on an outage.
                providerTemporarilyUnavailable = ProviderTransientFailure.IsTransient(e, ct);
            }
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
                    published.Size == original.Size && string.Equals(published.Hash, original.Hash, StringComparison.OrdinalIgnoreCase))
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
            { RetryAfterSeconds = ProviderTransientFailure.IsTransient(failure, ct) ? ProviderTransientFailure.VerificationRetrySeconds : null };
        var obs = path.State;
        // An absent or old path does not prove an asynchronous commit has stopped running.
        // Keep its evidence until a read proves success or the provider reports a terminal rejection.
        var uploadMatches = DefinitelyMatches(obs, x.Source) &&
            (x.ExpectedTarget is null || string.Equals(obs?.ObjectId, x.ExpectedTarget.ObjectId, StringComparison.Ordinal));
        if (x.ProviderEffect is { } effect && MayStillRun(effect, obs, uploadMatches))
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
                if (DefinitelyMatches(obs, x.Source)) return await DoneAsync(x, obs, target, ct).ConfigureAwait(false);
                return Skipped(x, obs is null ? NotApplied : Superseded);
            case ActionKind.Update:
                if (DefinitelyMatches(obs, x.Source)) return await DoneAsync(x, obs, target, ct).ConfigureAwait(false);
                return Skipped(x, SameAsExpected(obs, x.ExpectedTarget) ? NotApplied : Superseded);
            case ActionKind.Move:
                return await ResolveMoveAsync(x, obs, target, ct).ConfigureAwait(false);
            case ActionKind.Delete:
                if (obs is null) return await DoneAsync(x, null, target, ct).ConfigureAwait(false);
                return Skipped(x, SameAsExpected(obs, x.ExpectedTarget) ? NotApplied : Superseded);
            case ActionKind.MkDir:
                if (obs?.Kind == ItemKind.Folder) return await DoneAsync(x, obs, target, ct).ConfigureAwait(false);
                // A create job Dropbox still reports in progress must not be replanned over: its own outcome
                // (present/refused) is definitive, so only an absent path with a live job stays unverified.
                if (providerReportsInProgress) return Unverified(x, ExecutionReasons.ProviderPending.Detail);
                return Skipped(x, obs is null ? NotApplied : Superseded);
            default:
                return Unverified(x, string.Format(ExecutionReasons.NoObservationRule.Detail, x.Kind));
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
        CancellationToken ct)
    {
        var from = await ObserveAsync(target, x.FromPath, ct).ConfigureAwait(false);
        if (from.Failure is { } failure) return Unverified(x, Describe(failure)) with
            { RetryAfterSeconds = ProviderTransientFailure.IsTransient(failure, ct) ? ProviderTransientFailure.VerificationRetrySeconds : null };
        if (from.State is not null) return Skipped(x, NotApplied);
        if (MoveLanded(obs, x)) return await DoneAsync(x, obs, target, ct).ConfigureAwait(false);
        return Skipped(x, Superseded);
    }

    private static bool MoveLanded(ItemState? obs, SyncAction x) =>
        obs is not null && x.ExpectedTarget is not null &&
        string.Equals(obs.ObjectId, x.ExpectedTarget.ObjectId, StringComparison.Ordinal) &&
        HashAgrees(obs, x.ExpectedTarget);

    private static bool HashAgrees(ItemState obs, ItemState moved) =>
        obs.Hash is null || moved.Hash is null ||
        string.Equals(obs.Hash, moved.Hash, StringComparison.OrdinalIgnoreCase);

    // A failed recovery lookup keeps the intent unresolved (Uncertain, with the failure) for a later run.
    private static async Task<ActionResult> DoneAsync(SyncAction x, ItemState? newT, IEndpointWriter target,
        CancellationToken ct)
    {
        RecoveryRef? recovery = null;
        if (x.RecoveryFirst && x.ExpectedTarget is not null)
        {
            try
            {
                recovery = await target.PreserveAsync(x.ExpectedTarget, x.ActionId, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (Unverifiable(e, ct))
            {
                return Unverified(x, string.Format(ExecutionReasons.RecoveryUnverified.Detail, Describe(e)));
            }
        }
        if (x.Kind == ActionKind.Delete) return new ActionResult(x.ActionId, Outcome.Done, null, null, recovery, null);
        return x.Target == Side.A
            ? new ActionResult(x.ActionId, Outcome.Done, newT, x.Source, recovery, null)
            : new ActionResult(x.ActionId, Outcome.Done, x.Source, newT, recovery, null);
    }

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

    // A failure that only leaves the effect unverified. A requested cancellation (the caller's token) and an access
    // failure (the next run observes it) are not swallowed.
    private static bool Unverifiable(Exception e, CancellationToken ct) =>
        e is not EndpointAccessException && !(e is OperationCanceledException && ct.IsCancellationRequested);

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