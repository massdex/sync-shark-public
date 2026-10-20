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
    private readonly SyncTransferResourceController _admission;
    private readonly string _pairId;
    private readonly TimeSpan _admissionRetryDelay;
    private readonly TimeSpan _unknownCommitGracePeriod;
    private readonly IEndpointWriter _a;
    private readonly IEndpointWriter _b;
    private readonly IReadOnlyList<SyncAction> _dependencies;
    private readonly ConcurrentDictionary<string, Outcome> _outcomes;
    private readonly ConcurrentDictionary<string, byte> _refusedParents = new(StringComparer.Ordinal);

    internal ActionRunner(StoreCommitter committer, V2Settings settings, SyncTransferResourceController admission,
        string pairId, IEndpointWriter a, IEndpointWriter b, IReadOnlyList<SyncAction> dependencies,
        ConcurrentDictionary<string, Outcome> outcomes)
    {
        _committer = committer;
        _admission = admission;
        _pairId = pairId;
        _admissionRetryDelay = settings.AdmissionRetryDelay;
        _unknownCommitGracePeriod = settings.UnknownCommitGracePeriod;
        _a = a;
        _b = b;
        _dependencies = dependencies;
        _outcomes = outcomes;
    }

    /// <summary>The committed result of the action, or null when a cancelled action records nothing.</summary>
    internal async Task<ActionResult?> RunAsync(SyncAction action, CancellationToken ct)
    {
        var result = await RunStepsAsync(action, ct).ConfigureAwait(false);
        if (result is not null && action.Kind is ActionKind.MkDir or ActionKind.Move or ActionKind.Delete)
        {
            if ((result.ProviderNameRefused || result.ParentNameRefused) &&
                (action.Kind == ActionKind.MkDir || action.Source?.Kind == ItemKind.Folder))
                _refusedParents[OutcomeKey(action)] = 0;
            _outcomes[OutcomeKey(action)] = result.Outcome;
        }
        return result;
    }

    private async Task<ActionResult?> RunStepsAsync(SyncAction action, CancellationToken ct)
    {
        if (await CommitOnlyOrSkipAsync(action, ct).ConfigureAwait(false) is { } early) return early;
        if (RecoverySafety.RequiresRecovery(action) && RecoverySafety.Unavailable(_a, _b) is { } recovery)
            return Failed(action, "Recovery location unavailable: " + recovery);
        var target = action.Target == Side.A ? _a : _b;
        var source = action.Target == Side.A ? _b : _a;
        return await WithLeaseAsync(action, target, source, ct).ConfigureAwait(false);
    }

    // Steps 1 and 2; null means "run the effect".
    private async Task<ActionResult?> CommitOnlyOrSkipAsync(SyncAction action, CancellationToken ct)
    {
        if (action.Kind is ActionKind.Adopt or ActionKind.Forget)
            return await CommitAsync(action, CommitOnlyResult(action)).ConfigureAwait(false);
        if (BlockingParent(action) is { } parent)
            return await CommitAsync(action, Skipped(action, ParentNotReady + parent.Path) with
            {
                ParentNameRefused = _refusedParents.ContainsKey(OutcomeKey(parent))
            }).ConfigureAwait(false);
        return UndeletedDescendant(action) is { } child
            ? await CommitAsync(action, Skipped(action, FolderNotEmptied + child)).ConfigureAwait(false)
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
            return await CommitAsync(action, Failed(action, Describe(e))).ConfigureAwait(false);
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
            freeSpace = target is ILocalFreeSpaceAdmission admission
                ? await admission.AdmitFreeSpaceAsync(action, ct).ConfigureAwait(false)
                : FreeSpaceAdmission.Granted;
            if (freeSpace.Refusal is { } refusal)
                return await PendingForFreeSpaceAsync(action, refusal).ConfigureAwait(false);

            ProviderEffectContext.Current.Value = _committer.CreateProviderContext(action);
            // Step 4: the intent is durable before any effect is attempted (group-committed with its neighbours').
            await _committer.RecordIntentAsync(action, ct).ConfigureAwait(false);

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

    // A proven shortage (or unprovable free space) leaves the item pending: a stored notice and a Skipped result
    // (not Failed, and no intent to replay). The plan's other items keep running.
    private async Task<ActionResult> PendingForFreeSpaceAsync(SyncAction action, FreeSpaceRefusal refusal)
    {
        await _committer.AddNoticeAsync(new Notice(_pairId, action.Rule, NoticeKind.Info, action.Path,
            refusal.Detail, refusal.ReasonKey, FreeSpace: new FreeSpaceNoticeMetadata(refusal.VolumeRoot,
                refusal.RequiredBytes, refusal.FreeBytes, refusal.ReserveBytes))).ConfigureAwait(false);
        return await CommitAsync(action, Skipped(action, refusal.Detail)).ConfigureAwait(false);
    }

    // Steps 6 and 7: run the conditional effect once, map its exceptions, and commit the outcome.
    private async Task<ActionResult?> ExecuteAsync(SyncAction action, IEndpointWriter target, IEndpointWriter source,
        RecoveryHolder recoveryHolder, bool deferPreserve, CancellationToken ct)
    {
        ItemState? newT;
        try
        {
            newT = await EffectAsync(action, target, source, recoveryHolder, deferPreserve, ct).ConfigureAwait(false);
        }
        catch (ProviderNameRefusedException) when (action.ResolvedNoticeId is null && action.Source is { } item &&
            string.Equals(item.Path, action.Path, StringComparison.Ordinal) &&
            action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.Move or ActionKind.MkDir or ActionKind.KeepBoth)
        {
            // Settled but not performed: never satisfy a child dependency or commit a sync baseline.
            return await CommitAsync(action, new ActionResult(action.ActionId, Outcome.Skipped, null, null,
                recoveryHolder.Reference, ProviderNameRefusedException.Detail) { ProviderNameRefused = true })
                .ConfigureAwait(false);
        }
        catch (ProviderWriteThrottledException e)
        {
            return await CommitAsync(action, Skipped(action, e.Message)).ConfigureAwait(false);
        }
        catch (ExpectedTargetMismatchException e)
        {
            return await CommitAsync(action, Skipped(action, e.Message)).ConfigureAwait(false);
        }
        catch (UncertainEffectException)
        {
            return await ResolveUncertainAsync(action, target, source, ct).ConfigureAwait(false);
        }
        catch (DeferredFailureException deferred)
        {
            // The deferred Recovery archive refused or failed after the download was verified. Once the checkpoint
            // is durable, keep the action unresolved so reconcile can prove the original's state instead of
            // clearing it from a single observation.
            return await CommitAsync(action, recoveryHolder.CheckpointRecorded
                ? new ActionResult(action.ActionId, Outcome.Uncertain, null, null, null, deferred.Result.Reason)
                : deferred.Result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is not EndpointAccessException)
        {
            // An access failure is not committed: the intent stays unresolved and the next run observes it. After a
            // durable download checkpoint the same holds for any other failure, so resume keeps the provenance.
            return await CommitAsync(action, recoveryHolder.CheckpointRecorded
                ? Uncertain(action, e)
                : Failed(action, Describe(e))).ConfigureAwait(false);
        }

        var done = DoneResult(action, newT, recoveryHolder.Reference);
        await CommitAsync(action, done).ConfigureAwait(false);
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
        // A remote source and a local target download straight into the target's own temporary file: no product
        // spool, so the target volume holds one copy. Any other pair keeps the streaming handoff.
        if (target is ISyncDirectDownloadTarget direct && source is ISyncDirectDownloadSource remote)
        {
            Func<SyncDownloadStage, CancellationToken, Task> preservedAfterVerify =
                (stage, token) => PreserveAfterVerifyAsync(action, target, recoveryHolder, stage, token);
            return await direct.PutDownloadedAsync(action, remote, preservedAfterVerify, ct).ConfigureAwait(false);
        }
        var content = await source.OpenReadAsync(action.Source, ct).ConfigureAwait(false);
        await using var owner = new SyncUploadSource(content);
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
        catch (Exception e) when (e is not (OperationCanceledException or EndpointAccessException))
        {
            return (null, Failed(action, RecoveryFailed + Describe(e)));
        }
    }

    private async Task<ActionResult> ResolveUncertainAsync(SyncAction action, IEndpointWriter target,
        IEndpointWriter source, CancellationToken ct)
    {
        var uncertain = new ActionResult(action.ActionId, Outcome.Uncertain, null, null, null, UncertainReason);
        await CommitAsync(action, uncertain).ConfigureAwait(false);
        var resolved = await EffectResolver.ResolveAsync(_committer.LatestAction(action), target, source, ct,
            _unknownCommitGracePeriod).ConfigureAwait(false);
        // Still unverifiable: the committed Uncertain stands; report the actual failure in its reason.
        if (resolved.Outcome == Outcome.Uncertain) return resolved;
        await CommitAsync(action, resolved).ConfigureAwait(false);
        return resolved;
    }

    private async Task<SyncTransferResourceController.Lease> AcquireAsync(SyncAction action, CancellationToken ct)
    {
        using var timing = RunInstrumentation.Current?.Measure(RunTimingStage.AdmissionWait);
        // The admission retry is a tracked wait, announced once with its reason and delay (RQ-090).
        using var wait = RunOperations.Begin(RunOperationKind.TransferSlot, "resource admission");
        var bytes = action.Source?.Size ?? 0;
        var file = action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth;
        var target = action.Target == Side.A ? _a : _b;
        var source = action.Target == Side.A ? _b : _a;
        var keys = new List<SyncProviderBudgetKey>(2);
        if (target is ISyncTransferResourceEndpoint write)
            keys.Add(new(write.TransferResourceKey, SyncProviderOperation.Write));
        if (file && source is ISyncTransferResourceEndpoint read)
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

    private static ActionResult Failed(SyncAction action, string reason) =>
        new(action.ActionId, Outcome.Failed, null, null, null, reason);

    // A post-checkpoint failure is not evidence that nothing happened; it stays unresolved for resume.
    private static ActionResult Uncertain(SyncAction action, Exception error) =>
        new(action.ActionId, Outcome.Uncertain, null, null, null, UncertainReason + ": " + Describe(error));

    // The failure reason carries the exception type, message and the deepest stack frame, so an unexpected
    // exception (for example a malformed provider body) names its source instead of vanishing into a bare type name.
    private static string Describe(Exception e)
    {
        var reason = e.GetType().Name + ": " + e.Message;
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
        await _committer.CommitAsync(action, result).ConfigureAwait(false);
        return result;
    }
}
