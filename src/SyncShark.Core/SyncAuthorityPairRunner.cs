using System.Globalization;
using System.Text.Json;

namespace Massdex.SyncShark;

internal sealed record SyncAuthorityPairPreparationResult(
    bool CanPlan,
    SyncAuthorityMonitorWatermark MonitorWatermark,
    IReadOnlyList<SyncDecision> Decisions);

internal sealed record SyncAuthorityPlannedPairOperation(
    string RelativePath,
    SyncAuthorityOperationSnapshot Operation);

internal sealed record SyncAuthorityPairPlanningResult(
    string PairId,
    IReadOnlyList<SyncDecision> Decisions,
    IReadOnlyList<SyncAuthorityPlannedPairOperation> Operations)
{
    internal SyncAuthorityPairExecutionResult? Streamed { get; init; }
    internal bool WorkWindowFull { get; init; }
}

internal sealed record SyncAuthorityPairExecutionResult(
    string PairId,
    IReadOnlyList<SyncDecision> Decisions,
    int CompletedEffects,
    bool PlanningInvalidated);

/// <summary>
/// CC4B-1: the ONE internal cloud/cloud runner context. It carries the exact CC4A prepared context of one
/// explicit cloud/cloud pair - its typed two-port set, its private staging binding, both retained side
/// authorities and its planner allocation options - plus the ONE way to open the pair's private staging
/// control root for the duration of one effect. It is internal, so no public constructor signature carries
/// it and no caller boolean can select a cloud engine; it never names a local pair root, so a cloud effect
/// can never be bound to an NTFS selected root.
/// </summary>
internal sealed record SyncAuthorityCloudRunnerContext(
    SyncAuthorityPreparedCloudPair Prepared,
    Func<PilotDirectoryLease> OpenControlRoot);

public sealed class SyncAuthorityPairRunner(
    SyncPair pair,
    ISyncRemoteStore remoteStore,
    SyncAuthorityDatabase authority,
    string localRootBindingId,
    string dropboxRootBindingId,
    SyncAuthorityPlannerOptions options,
    PilotRunControl runControl,
    SyncAuthorityCommandAdmission admission,
    WindowsCloudFilesProvider? cloudFiles = null,
    IReadOnlyDictionary<string, string>? membershipObjectFences = null,
    IReadOnlyDictionary<string, string>? membershipPathFences = null,
    IReadOnlyDictionary<string, SyncAuthorityOperationKind>? expectedOperationKinds = null,
    Func<SyncComparisonProgress, Task>? onInventory = null,
    Func<SyncAuthorityLiveAction, Task>? onExecution = null,
    IReadOnlyDictionary<string, SyncBootstrapMoveReview>? expectedMoveReviews = null)
{
    private const int MaximumPlanningPasses = 8;
    private const int RecoveryPageSize = 100;
    private readonly SemaphoreSlim _liveActionGate = new(1, 1);
    private readonly SyncTransferBatchTracker _transferBatch = new();
    // Only the host's ordered, durably retained sink opts in. Arbitrary caller
    // callbacks retain the original fully serialized contract.
    internal bool ConcurrentProgressRetention { get; init; }
    internal SyncTransferSchedulingScope? TransferScheduling { get; init; }
    internal SyncTransferResourceController? InventoryResources { get; init; }
    internal static int? RecoveryPageSizeForTest { get; set; }
    internal static Func<string, Task>? OnlineOnlyAdoptionPhaseForTest { get; set; }

    /// <summary>
    /// CC4B-1: the typed cloud context of this runner, present only for one explicit cloud/cloud pair. It is
    /// never settable from outside this class and is attached exactly once by <see cref="AttachCloudContext"/>
    /// from the Host's single runner factory; the public constructor signature is unchanged, so every existing
    /// legacy caller stays source-compatible.
    /// </summary>
    internal SyncAuthorityCloudRunnerContext? Cloud { get; private set; }

    /// <summary>
    /// CC4B-1: the ONE one-way attach of the typed cloud context. A second attach, or an attach on a runner
    /// that already carries a context, is refused rather than silently replacing the prepared authority.
    /// </summary>
    internal SyncAuthorityPairRunner AttachCloudContext(SyncAuthorityCloudRunnerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Cloud is not null)
            throw new InvalidOperationException(
                "This pair runner already carries a typed cloud context; the prepared authority is never replaced.");
        Cloud = context;
        return this;
    }

    /// <summary>
    /// CC4B-1: the ONE provider-aware comparer with the CC2F revision-bound common-digest resolver over this
    /// pair's two typed ports, or null for a legacy pair. Both the initial planner and the reconciliation
    /// planner take exactly this instance, so no planning call of a cloud pair omits it.
    /// </summary>
    private SyncAuthorityProviderAwareContentComparison? _cloudComparison;

    /// <summary>
    /// CC4B-1 correction: exactly ONE provider-aware comparer with ONE CC2F revision-bound resolver is created
    /// for this attached prepared context and then cached, so both planner calls receive the very same
    /// instance and its revision-bound digest cache is retained across them. It is per runner, not a global
    /// cache, and a legacy runner still resolves to null.
    /// </summary>
    // Visibility-only change, approved for the CC4BC3 shared-comparer identity proof: this member was private and
    // is now internal so the test project (which the assembly already grants InternalsVisibleTo) can assert that
    // one comparer object and its revision-bound digest cache are shared. No behaviour, signature, semantics,
    // caching or lifetime change; no other member touched.
    internal SyncAuthorityProviderAwareContentComparison? CloudComparison
    {
        get
        {
            if (Cloud is null) return null;
            return _cloudComparison ??= new SyncAuthorityProviderAwareContentComparison(
                Cloud.Prepared.Endpoints.Left.Provider, Cloud.Prepared.Endpoints.Right.Provider,
                new SyncAuthorityCloudCommonDigestResolver(authority, Cloud.Prepared.Endpoints));
        }
    }

    public async Task<SyncRunResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            runControl.StopToken);
        var decisions = new Dictionary<string, SyncDecision>(StringComparer.OrdinalIgnoreCase);
        await DrainRunnableOperationsAsync("pair-recovery-" + Guid.NewGuid().ToString("N"), decisions,
            linked.Token);
        return new SyncRunResult(pair.Id, decisions.Values
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal async Task<SyncAuthorityPairPreparationResult> PrepareCoordinatedAsync(string triggerKind,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            runControl.StopToken);
        var token = linked.Token;
        var monitorWatermark = await authority.CaptureMonitorWatermarkAsync(pair.Id, token);
        var decisions = new Dictionary<string, SyncDecision>(StringComparer.OrdinalIgnoreCase);
        var recoveryDrained = await DrainRunnableOperationsAsync(
            "pair-coordinated-recovery-" + Guid.NewGuid().ToString("N"), decisions, token);
        if (!recoveryDrained)
            return new SyncAuthorityPairPreparationResult(false, monitorWatermark,
                decisions.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());

        if (await authority.LoadPairStateAsync(pair.Id, token) == "discovery_required")
        {
            await Observer().ObserveAsync(NormalizeTrigger(triggerKind, initial: true), token);
            var adopted = await new SyncAuthorityInitialPlanner(authority, CloudComparison)
                .AdoptAsync(pair.Id, token);
            foreach (var decision in adopted) RetainMostMaterial(decisions, decision);
            if (adopted.Any(item => item.Kind == SyncDecisionKind.Blocked))
                return new SyncAuthorityPairPreparationResult(false, monitorWatermark,
                    decisions.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
            await authority.SetPairStateAsync(pair.Id, "active", token);
            if (pair.InitialReviewApproved == false)
            {
                RetainMostMaterial(decisions, new SyncDecision("<first-sync>", SyncDecisionKind.Blocked,
                    "Initial inventory is complete. Review the proposed uploads, downloads and conflicts, then start the first sync. No file has been copied yet."));
                return new SyncAuthorityPairPreparationResult(false, monitorWatermark,
                    decisions.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
            }
        }
        else if (pair.InitialReviewApproved == false)
        {
            RetainMostMaterial(decisions, new SyncDecision("<first-sync>", SyncDecisionKind.Blocked,
                "First-sync review is waiting. Review the proposed actions, then start the first sync. No file has been copied yet."));
            return new SyncAuthorityPairPreparationResult(false, monitorWatermark, decisions.Values.ToArray());
        }

        return new SyncAuthorityPairPreparationResult(true, monitorWatermark,
            decisions.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal async Task<SyncAuthorityPairPlanningResult> PlanBoundAsync(
        SyncAuthorityPairObservationSet observationSet, string observationSetId,
        IReadOnlySet<string>? coordinatedObjectReservations = null,
        IReadOnlyList<SyncAuthorityPairObservationSet>? participantObservationSets = null,
        CancellationToken cancellationToken = default,
        Func<SyncComparisonProgress, Task>? onComparison = null,
        IReadOnlySet<string>? pathsAttemptedThisRun = null,
        bool streamIndependentFiles = false,
        Func<SyncAuthorityLiveAction, Task>? onAction = null,
        Func<Task>? beforeIndependentWindow = null,
        Func<SyncAuthorityPairExecutionResult,Task>? onIndependentResult = null)
    {
        var participants = participantObservationSets ?? [observationSet];
        var streamedDecisions = new Dictionary<string,SyncDecision>(StringComparer.OrdinalIgnoreCase);
        var streamedCompleted = 0;
        var streamedInvalidated = false;
        async Task<SyncAuthorityPairPlanningResult> BindWindow(IReadOnlyList<SyncAuthorityPlannerOutcome> window)
        {
            var selected=window.Where(item=>item.OperationId is not null &&
                pathsAttemptedThisRun?.Contains(item.RelativePath)!=true).ToArray();
            var ids=DistinctOperationIds(selected);
            var loaded=ids.Count==0?Array.Empty<SyncAuthorityOperationSnapshot>():
                await authority.LoadOperationsAsync(ids,cancellationToken,PlannedOperationBatchReadForTest);
            return new(pair.Id,window.Where(item=>item.OperationId is null).Select(Decision).ToArray(),
                BindPlannedOperations(selected,loaded));
        }
        async Task<bool> ExecuteWindow(IReadOnlyList<SyncAuthorityPlannerOutcome> window)
        {
            await runControl.WaitBeforeNextEffectAsync(cancellationToken);
            var bound=await BindWindow(window);
            if(beforeIndependentWindow is not null)await beforeIndependentWindow();
            foreach(var decision in bound.Decisions)RetainMostMaterial(streamedDecisions,decision);
            var result=await ExecuteBoundAsync(bound,cancellationToken,onAction);
            foreach(var decision in result.Decisions)RetainMostMaterial(streamedDecisions,decision);
            streamedCompleted+=result.CompletedEffects;
            streamedInvalidated|=result.PlanningInvalidated;
            // Publish drained outcomes before the next planning step can fail or cancel.
            // The owner retains exact successes even if no final plan is returned.
            if(onIndependentResult is not null)await onIndependentResult(result);
            return !runControl.IsStopRequested && !result.PlanningInvalidated &&
                !result.Decisions.Any(item=>item.Kind is SyncDecisionKind.ResponseUnknown or
                    SyncDecisionKind.CleanupPending or SyncDecisionKind.Stopped);
        }
        var planner=new SyncAuthorityReconciliationPlanner(authority,options,admission,
            membershipObjectFences:membershipObjectFences,membershipPathFences:membershipPathFences,
            onComparison:onComparison,contentComparison:CloudComparison,endpointContext:SyncEndpointContext.Capture(pair),
            planningConcurrency:streamIndependentFiles?16:1)
        {
            OnPlanWindow=streamIndependentFiles && participants.Count==1?ExecuteWindow:null
        };
        var outcomes=await planner.PlanAsync(observationSet,observationSetId,coordinatedObjectReservations,participants,
            cancellationToken);
        streamedInvalidated |= planner.RequiresFreshObservation;
        var remainder=await BindWindow(outcomes);
        return remainder with { WorkWindowFull=planner.WorkWindowFull, Streamed=streamIndependentFiles
            ?new SyncAuthorityPairExecutionResult(pair.Id,streamedDecisions.Values.ToArray(),streamedCompleted,streamedInvalidated)
            :null };
    }

    /// <summary>
    /// Test-only observation of the real reviewed batch read: the index and identity of each selected
    /// operation the batch loader materializes, in the order it was asked for them. Production installs
    /// no callback, so the default null leaves the loader's own behaviour unchanged.
    /// </summary>
    internal static Func<int, string, Task>? PlannedOperationBatchReadForTest { get; set; }

    /// <summary>
    /// The distinct retained identities the filtered outcomes selected, in their original order. The
    /// reviewed batch API is called once for exactly this list, so an identity two outcomes share is still
    /// read once while both outcomes keep their own position and relative path.
    /// </summary>
    internal static IReadOnlyList<string> DistinctOperationIds(
        IReadOnlyList<SyncAuthorityPlannerOutcome> selectedOutcomes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>(selectedOutcomes.Count);
        foreach (var outcome in selectedOutcomes)
        {
            if (outcome.OperationId is null)
                throw new InvalidDataException(
                    "A planned operation outcome carries no retained operation identity.");
            if (seen.Add(outcome.OperationId)) ids.Add(outcome.OperationId);
        }
        return ids;
    }

    /// <summary>
    /// Binds one ordered retained batch back to the ordered outcomes that selected it. Every outcome keeps
    /// its original position and relative path, and each identity is bound to the snapshot the batch
    /// returned for exactly that identity: a repeated identity reuses the one loaded snapshot, and a
    /// missing, foreign or ambiguous identity is refused instead of being bound positionally.
    /// </summary>
    internal static IReadOnlyList<SyncAuthorityPlannedPairOperation> BindPlannedOperations(
        IReadOnlyList<SyncAuthorityPlannerOutcome> selectedOutcomes,
        IReadOnlyList<SyncAuthorityOperationSnapshot> loaded)
    {
        var byId = new Dictionary<string, SyncAuthorityOperationSnapshot>(loaded.Count, StringComparer.Ordinal);
        foreach (var snapshot in loaded)
            if (!byId.TryAdd(snapshot.OperationId, snapshot))
                throw new InvalidDataException("The retained operation batch returned " + snapshot.OperationId +
                    " more than once, so a planned identity is ambiguous.");
        var operations = new List<SyncAuthorityPlannedPairOperation>(selectedOutcomes.Count);
        foreach (var outcome in selectedOutcomes)
        {
            if (outcome.OperationId is null)
                throw new InvalidDataException(
                    "A planned operation outcome carries no retained operation identity.");
            if (!byId.TryGetValue(outcome.OperationId, out var operation))
                throw new InvalidDataException("The retained operation " + outcome.OperationId +
                    " is not part of the batch read for this plan; it is never bound positionally.");
            operations.Add(new SyncAuthorityPlannedPairOperation(outcome.RelativePath, operation));
        }
        return operations;
    }

    internal async Task<SyncAuthorityPairExecutionResult> ExecuteBoundAsync(
        SyncAuthorityPairPlanningResult plan, CancellationToken cancellationToken = default,
        Func<SyncAuthorityLiveAction, Task>? onAction = null)
    {
        if (!string.Equals(plan.PairId, pair.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("The coordinated pair plan belongs to a different pair.");
        var decisions = new Dictionary<string, SyncDecision>(StringComparer.OrdinalIgnoreCase);
        if (plan.Streamed is { } prior)
            foreach (var decision in prior.Decisions) RetainMostMaterial(decisions, decision);
        var completed = plan.Streamed?.CompletedEffects ?? 0;
        var invalidated = plan.Streamed?.PlanningInvalidated ?? false;
        var workerId = "pair-coordinated-runner-" + Guid.NewGuid().ToString("N");
        if (plan.Streamed is { } streamed && (streamed.PlanningInvalidated ||
            streamed.Decisions.Any(item => item.Kind is SyncDecisionKind.ResponseUnknown or
                SyncDecisionKind.CleanupPending or SyncDecisionKind.Stopped)))
        {
            foreach (var pending in plan.Operations)
                RetainMostMaterial(decisions,new SyncDecision(pending.RelativePath,SyncDecisionKind.Blocked,
                    "Not attempted: an earlier planning window stopped or needs a fresh observation.") { NotAttempted=true });
            return new(pair.Id,decisions.Values.ToArray(),completed,invalidated);
        }
        foreach (var planned in plan.Operations)
            await NotifyLiveActionAsync(onAction, planned, "Queued", "Fresh plan retained; no file effect has started for this item.", SyncActivityPhase.Planned);
        var ordered = plan.Operations
                     .OrderBy(value => ExecutionClass(value.Operation.Kind))
                     .ThenBy(value => ExecutionDepth(value.Operation.Kind, value.RelativePath))
                     .ThenBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var cancelledAfterDrain = false;
        try
        {
        await ScheduleAsync(ordered, async item =>
        {
            await runControl.WaitBeforeNextEffectAsync(cancellationToken);
            if (await authority.SupersedeReadyOperationIfPlanningAuthorityChangedAsync(
                    item.Operation.OperationId, item.Operation.PlanningAuthorities, cancellationToken))
            {
                await NotifyLiveActionAsync(onAction, item, "Superseded",
                    "A newer observation replaced this queued action before any effect was issued.",
                    SyncActivityPhase.Superseded);
                return new SyncAuthorityExecutionResult(item.Operation.OperationId,
                    SyncAuthorityOperationStatus.Superseded, false,
                    "A newer observation replaced this queued action before any effect was issued.")
                    { NotAttempted = true };
            }
            await NotifyLiveActionAsync(onAction, item, "Starting",
                "The exact source, destination and retained preconditions are being checked. Byte and rate totals are unavailable until this provider operation completes.",
                SyncActivityPhase.Running);
            return await DispatchAsync(item.Operation, workerId, cancellationToken, onAction);
        }, async (item, result) =>
        {
            invalidated |= result.Status == SyncAuthorityOperationStatus.Superseded ||
                           (result.SourceChangedBeforeEffect || result.DestinationChangedBeforeEffect) &&
                           result.NotAttempted;
            RetainMostMaterial(decisions, Decision(item.RelativePath, item.Operation.Kind, result));
            await NotifyLiveActionAsync(onAction, item,
                result.NotAttempted ? "Not attempted" : result.Status == SyncAuthorityOperationStatus.Completed ? "Completed" : "Needs attention",
                result.Reason, SyncEndpointActivityPresentation.PhaseFor(result.Status));
            if (result.Status == SyncAuthorityOperationStatus.Completed) completed++;
            if (result.Status != SyncAuthorityOperationStatus.Completed &&
                result.Status != SyncAuthorityOperationStatus.Superseded &&
                !result.NotAttempted &&
                !await authority.IsKnownNoEffectRefusalAsync(item.Operation.OperationId, cancellationToken))
                return false; // Drain admitted disjoint effects; never start more against unknown authority.
            if (result.Status == SyncAuthorityOperationStatus.Completed && IsDirectoryMove(item.Operation))
            {
                // CC3E-2: this coordinated plan was built before the directory move, so its remaining
                // descendant operations are stale and none of them may execute. The caller is told the plan
                // needs a fresh coordinated pass, and the next real observation reconciles child memberships
                // through the shared planner instead of pretending the subtree converged from parent success.
                invalidated = true;
                return false;
            }
            return true;
        }, cancellationToken, allowDirectoryCreates: Cloud is null && cloudFiles is null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The shared scheduler has drained every admitted effect before throwing.
            // Keep its completed reductions and project unresolved dispatched work
            // from durable authority; cancellation is not evidence of no effect.
            cancelledAfterDrain = true;
        }
        foreach (var pending in plan.Operations.Where(item => !decisions.ContainsKey(item.RelativePath)))
        {
            if (cancelledAfterDrain)
            {
                var retained = await authority.LoadOperationAsync(pending.Operation.OperationId,
                    CancellationToken.None);
                if (retained.Status == SyncAuthorityOperationStatus.Completed)
                {
                    const string detail = "Completed before cancellation; the exact successful outcome is retained.";
                    RetainMostMaterial(decisions, Decision(pending.RelativePath, retained.Kind,
                        new(retained.OperationId, retained.Status, false, detail)));
                    completed++;
                    await NotifyLiveActionAsync(onAction, pending, "Completed", detail,
                        SyncActivityPhase.Completed);
                    continue;
                }
                if (retained.Status == SyncAuthorityOperationStatus.CleanupPending)
                {
                    const string detail = "The file effect is retained, but its owned cleanup still needs verification after cancellation.";
                    RetainMostMaterial(decisions, new(pending.RelativePath, SyncDecisionKind.CleanupPending,
                        detail));
                    await NotifyLiveActionAsync(onAction, pending, "Needs attention", detail,
                        SyncActivityPhase.Blocked);
                    continue;
                }
                if (!await authority.IsUnissuedOperationAsync(retained.OperationId, CancellationToken.None) &&
                    !await authority.IsKnownNoEffectRefusalAsync(retained.OperationId, CancellationToken.None))
                {
                    const string detail = "Cancellation drained the dispatched request, but its exact outcome is not verified. " +
                        "The retained operation and attempt must be reconciled; this file was not retried.";
                    RetainMostMaterial(decisions, new(pending.RelativePath, SyncDecisionKind.ResponseUnknown, detail));
                    await NotifyLiveActionAsync(onAction, pending, "Needs attention", detail,
                        SyncActivityPhase.EffectUnknown);
                    continue;
                }
            }
            var reason = "Not attempted: this batch stopped or its plan became stale before this file was dispatched. Completed files are retained; recheck this remaining file before retry.";
            decisions[pending.RelativePath] = new(pending.RelativePath, SyncDecisionKind.Blocked, reason) { NotAttempted = true };
            await NotifyLiveActionAsync(onAction, pending, "Not attempted", reason, SyncActivityPhase.Blocked);
        }
        // Cancellation of the run and certainty of each file outcome are independent. Keep the exact
        // completed/unknown/not-attempted decisions above, and also retain the existing run-level Stop
        // marker so the host, CLI and GUI do not report an interrupted run as normally completed.
        if (cancelledAfterDrain || cancellationToken.IsCancellationRequested || runControl.IsStopRequested)
            decisions["<run>"] = new SyncDecision("<run>", SyncDecisionKind.Stopped,
                "The run stopped after admitted work drained. Completed outcomes and unresolved recovery authority are retained.");
        if (!cancellationToken.IsCancellationRequested)
            await authority.CompleteEligibleConflictResolutionsAsync(pair.Id, cancellationToken);
        return new SyncAuthorityPairExecutionResult(pair.Id,
            decisions.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            completed, invalidated);
    }

    private async Task<bool> ScheduleAsync<TResult>(IReadOnlyList<SyncAuthorityPlannedPairOperation> items,
        Func<SyncAuthorityPlannedPairOperation, Task<TResult>> execute,
        Func<SyncAuthorityPlannedPairOperation, TResult, Task<bool>> accept, CancellationToken token,
        bool allowDirectoryCreates = false)
    {
        await _liveActionGate.WaitAsync(token);
        try { _transferBatch.Register(items.Select(item => item.Operation.OperationId)); }
        finally { _liveActionGate.Release(); }
        // The pump drains all admitted work, including on cancellation/errors,
        // before pooled database handles and their identity anchors are released.
        await using var connections = authority.BeginExecutionConnectionScope();
        await using var accountVerification = SyncProviderAccountVerificationScope.Begin(token);
        return await SyncAuthorityOperationScheduler.RunAsync(items, item => item.Operation,
            execute, accept, token, adaptive: TransferScheduling,
            successful: result => result is SyncAuthorityExecutionResult effect &&
                                  effect.Status == SyncAuthorityOperationStatus.Completed,
            allowDirectoryCreates: allowDirectoryCreates);
    }

    private async Task NotifyLiveActionAsync(Func<SyncAuthorityLiveAction, Task>? onAction,
        SyncAuthorityPlannedPairOperation item, string stage, string detail, SyncActivityPhase? phase = null,
        SyncActivityProgress? progress = null)
    {
        if (onAction is null) return;
        Task notification;
        await _liveActionGate.WaitAsync();
        try
        {
            var (itemKind, fileType) = LiveActionType(item);
            var action = new SyncAuthorityLiveAction(item.Operation.OperationId, item.RelativePath,
                LiveActionName(item.Operation.Kind), stage, detail, DateTimeOffset.UtcNow, itemKind, fileType,
                item.Operation.Kind, phase, progress);
            notification = onAction(action with { Batch = _transferBatch.Observe(action) });
            if (!ConcurrentProgressRetention) await notification;
        }
        finally { _liveActionGate.Release(); }
        // Submission and batch counters remain ordered. Host retention can group
        // these awaited requests; presentation is separately delivered in order.
        if (ConcurrentProgressRetention) await notification;
    }

    private static string LiveActionName(SyncAuthorityOperationKind kind) => kind switch
    {
        SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote => "Upload",
        SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal => "Download",
        SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote => "Recoverable deletion",
        SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote or
            SyncAuthorityOperationKind.CrossPairMove => "Move",
        SyncAuthorityOperationKind.ResolveConflict => "Resolve conflict",
        _ => kind.ToString()
    };

    private static (string ItemKind, string FileType) LiveActionType(
        SyncAuthorityPlannedPairOperation item)
    {
        var kinds = item.Operation.Members.Select(MemberObjectKind)
            .Where(kind => kind is "file" or "directory")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (kinds.Length != 1) return ("Unknown", "Unknown");
        if (kinds[0] == "directory") return ("Folder", "Folder");
        var extension = Path.GetExtension(item.RelativePath);
        return ("File", string.IsNullOrWhiteSpace(extension)
            ? "File"
            : extension.TrimStart('.').ToUpperInvariant());
    }

    private static string? MemberObjectKind(SyncAuthorityMember member)
    {
        try
        {
            using var state = JsonDocument.Parse(member.ExpectedStateJson);
            if (state.RootElement.TryGetProperty("ObjectKind", out var pascal))
                return pascal.GetString()?.ToLowerInvariant();
            if (state.RootElement.TryGetProperty("objectKind", out var camel))
                return camel.GetString()?.ToLowerInvariant();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Activity metadata must not turn a retained sync operation into a failed effect.
        }
        return null;
    }

    public async Task<SyncRunResult> RecoverReviewedOperationGroupAsync(string operationGroupId,
        IReadOnlySet<string> reviewedPaths,
        Func<SyncAuthorityReviewedPathCheckpoint, Task> onCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationGroupId);
        ArgumentNullException.ThrowIfNull(reviewedPaths);
        ArgumentNullException.ThrowIfNull(onCheckpoint);
        var normalizedPaths = reviewedPaths.Select(PilotPath.NormalizeRelative)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            runControl.StopToken);
        var token = linked.Token;
        var decisions = new Dictionary<string, SyncDecision>(StringComparer.OrdinalIgnoreCase);

        for (var pass = 0; pass < MaximumPlanningPasses; pass++)
        {
            var operations = await authority.ListOperationsForGroupAsync(operationGroupId, token);
            foreach (var completed in operations.Where(item => item.Status == SyncAuthorityOperationStatus.Completed))
                await CheckpointOperationAsync(completed,
                    Decision(OperationDisplayPath(completed), completed.Kind,
                        new SyncAuthorityExecutionResult(completed.OperationId,
                            SyncAuthorityOperationStatus.Completed, false,
                            "The reviewed operation is durably complete.")),
                    normalizedPaths, onCheckpoint);

            var incomplete = operations.Where(item => item.Status != SyncAuthorityOperationStatus.Completed)
                .OrderBy(item => ExecutionClass(item.Kind))
                .ThenBy(item => ExecutionDepth(item.Kind, OperationDisplayPath(item)))
                .ThenBy(item => item.OperationId, StringComparer.Ordinal)
                .ToArray();
            if (incomplete.Length == 0)
                return new SyncRunResult(pair.Id, decisions.Values
                    .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());

            var progressed = false;
            var completedBatch = await ScheduleAsync(incomplete.Select(operation =>
                new SyncAuthorityPlannedPairOperation(OperationDisplayPath(operation), operation)).ToArray(), async item =>
            {
                var operation = item.Operation;
                if (!string.Equals(operation.PairId, pair.Id, StringComparison.Ordinal))
                    throw new InvalidDataException("A Rewind operation group crossed its retained pair boundary.");
                if (operation.Kind == SyncAuthorityOperationKind.CrossPairMove)
                    throw new InvalidDataException("A Rewind operation group contains a cross-pair transaction.");
                await runControl.WaitBeforeNextEffectAsync(token);
                return await DispatchAsync(operation,
                    "rewind-recovery-" + Guid.NewGuid().ToString("N"), token);
            }, async (item, result) =>
            {
                var operation = item.Operation;
                var decision = Decision(OperationDisplayPath(operation), operation.Kind, result);
                RetainMostMaterial(decisions, decision);
                await CheckpointOperationAsync(operation, decision, normalizedPaths, onCheckpoint);
                progressed |= result.Status != operation.Status;
                return result.Status == SyncAuthorityOperationStatus.Completed && !IsDirectoryMove(operation);
            }, token);
            if (!completedBatch)
                return new SyncRunResult(pair.Id, decisions.Values
                    .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
            if (!progressed) break;
        }

        var blocked = new SyncDecision("<rewind-recovery>", SyncDecisionKind.Blocked,
            "The retained Rewind operation group did not reach a known terminal state within its bounded recovery passes.");
        RetainMostMaterial(decisions, blocked);
        return new SyncRunResult(pair.Id, decisions.Values.ToArray());
    }

    public async Task<SyncRunResult> RunReviewedAsync(string triggerKind,
        IReadOnlySet<string> reviewedPaths, string operationGroupId,
        Func<SyncAuthorityReviewedPathCheckpoint, Task> onCheckpoint,
        CancellationToken cancellationToken = default,
        Func<SyncAuthorityLiveAction, Task>? onAction = null,
        bool continueAfterBlockedPaths = false,
        string? reviewedLocalScanId = null, string? reviewedRemoteScanId = null,
        bool allowIndependentDirectoryCreates = false)
    {
        ArgumentNullException.ThrowIfNull(reviewedPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationGroupId);
        ArgumentNullException.ThrowIfNull(onCheckpoint);
        if ((reviewedLocalScanId is null) != (reviewedRemoteScanId is null))
            throw new InvalidDataException("Both retained reviewed inventory identities are required together.");
        if (reviewedLocalScanId is not null && expectedOperationKinds is null)
            throw new InvalidDataException("Retained inventory reuse requires an exact approved operation kind for each reviewed path.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            runControl.StopToken);
        var token = linked.Token;
        var remaining = reviewedPaths.Select(PilotPath.NormalizeRelative)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decisions = new Dictionary<string, SyncDecision>(StringComparer.OrdinalIgnoreCase);
        var workerId = "reviewed-runner-" + Guid.NewGuid().ToString("N");
        var sourceRefreshAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceRefreshOperations = new List<SyncAuthorityOperationSnapshot>();
        var workWindowWaiting = false;
        SyncRunResult ReviewedResult()
        {
            foreach (var path in remaining.Where(path => !decisions.ContainsKey(path)))
                decisions[path] = new(path, SyncDecisionKind.Blocked,
                    workWindowWaiting
                        ? "Waiting for retained work to drain. This original approved file has not been attempted."
                        : "Not attempted: the reviewed run stopped before this approved file was dispatched. Its original approval and completed peers are retained.") { NotAttempted = true };
            return new(pair.Id, decisions.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        var capacityContinuations = 0;
        for (var pass = 0; pass < MaximumPlanningPasses + capacityContinuations && remaining.Count > 0; pass++)
        {
            token.ThrowIfCancellationRequested();
            await authority.SuspendOperationGroupForPlanningAsync(operationGroupId, token);
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var executed = 0;
            var blockedPaths = 0;
            var sourceRefreshPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directoryMoved = false;
            var streamStopped = false;
            async Task<bool> ExecuteIndependentBatchAsync(IReadOnlyList<SyncAuthorityPlannerOutcome> batch)
            {
                // The planner has drained/disposed its write scope. Seal this
                // dependency-closed batch, finish every issued effect, then reopen
                // planning only through the existing no-active/uncertain guard.
                if (!await ExecuteOutcomesAsync(batch) || directoryMoved)
                {
                    streamStopped = true;
                    return false;
                }
                await authority.SuspendOperationGroupForPlanningAsync(operationGroupId, token);
                return true;
            }
            var planner = new SyncAuthorityReconciliationPlanner(authority, options, admission, remaining,
                    operationGroupId, membershipObjectFences, membershipPathFences,
                    expectedOperationKinds, onComparison: onInventory,
                    contentComparison: CloudComparison, endpointContext: SyncEndpointContext.Capture(pair),
                    expectedMoveReviews: expectedMoveReviews,
                    planningConcurrency: ConcurrentProgressRetention ? 16 : 1)
            {
                OnPlanWindow = ExecuteIndependentBatchAsync
            };
            IReadOnlyList<SyncAuthorityPlannerOutcome> outcomes;
            if (pass == 0 && reviewedLocalScanId is not null)
            {
                if (onInventory is not null)
                    await onInventory(new SyncComparisonProgress("Loading Saved Inventories", pair.Name,
                        0, remaining.Count, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                        $"Loading current complete saved inventories for {remaining.Count:N0} unfinished approved paths. Only the original approved paths and directions are eligible. Exact per-file dispatch checks the actual source revision, destination and root; no whole-folder scan or transfer is running.", "approved paths"));
                // The original two-sided review binds consent, not a requirement
                // that no later scan has completed. Use the latest complete saved
                // inventories through the existing current-authority boundary,
                // keeping this runner's captured roots and policy. The planner's
                // reviewed-path, object and direction fences remain unchanged;
                // new unrelated arrivals do not become approved work.
                var planningLocalScanId = await authority.LoadLatestCompleteScanIdAsync(
                    pair.Id, "local", token);
                var planningRemoteScanId = await authority.LoadLatestCompleteScanIdAsync(
                    pair.Id, "dropbox", token);
                var observation = await authority.LoadPlanningObservationSetAsync(pair.Id,
                    options.ConfigurationRevision, localRootBindingId, dropboxRootBindingId,
                    planningLocalScanId, planningRemoteScanId, token);
                // A completed effect can publish its synchronization point after these
                // saved inventories were captured. If that point's membership is absent
                // from a complete saved side, it is not safe to infer a later deletion:
                // the scan may simply predate our own successful create. Refresh through
                // the existing whole-pair observer, then retain the original reviewed-path
                // and direction fences. Real subsequent deletion/uncertainty still fails
                // those fences; completed history alone is never current-file proof.
                var observedLocalMemberships = observation.Local.Select(item => item.MembershipId)
                    .ToHashSet(StringComparer.Ordinal);
                var observedRemoteMemberships = observation.Dropbox.Select(item => item.MembershipId)
                    .ToHashSet(StringComparer.Ordinal);
                if (observation.SynchronizationPoints.Any(point =>
                        (point.LocalRelativePath is { } left && remaining.Contains(left) ||
                         point.DropboxRelativePath is { } right && remaining.Contains(right)) &&
                        (point.LocalMembershipId is { } localMembership &&
                         !observedLocalMemberships.Contains(localMembership) ||
                         point.DropboxMembershipId is { } remoteMembership &&
                         !observedRemoteMemberships.Contains(remoteMembership))))
                {
                    if (onInventory is not null)
                        await onInventory(new SyncComparisonProgress("Refreshing Reviewed Retry Inventories", pair.Name,
                            0, remaining.Count, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                            "A saved folder inventory omits a membership recorded by a completed action. Reading both current endpoints before retry planning; only the original approved paths and directions remain eligible, and no file effect is running.", "approved paths"));
                    await Observer().ObserveAsync(NormalizeTrigger(triggerKind, initial: false), token);
                    observation = await authority.LoadPlanningObservationSetAsync(pair.Id,
                        options.ConfigurationRevision, localRootBindingId, dropboxRootBindingId,
                        await authority.LoadLatestCompleteScanIdAsync(pair.Id, "local", token),
                        await authority.LoadLatestCompleteScanIdAsync(pair.Id, "dropbox", token), token);
                }
                outcomes = await planner.PlanAsync(observation,
                    SyncAuthorityReconciliationPlanner.ObservationSetIdentity(observation),
                    cancellationToken: token);
            }
            else if (sourceRefreshOperations.Count > 0 && Cloud is null)
            {
                // These are proven no-effect source-version refusals, not directory
                // movement or unknown absence. Refresh only their exact dependency
                // boundary; never publish that subset as a complete pair inventory.
                outcomes = await SyncAuthorityReviewedSourceRefresh.PlanAsync(pair, remoteStore, authority,
                    options, admission, localRootBindingId, dropboxRootBindingId, operationGroupId,
                    sourceRefreshOperations, expectedOperationKinds!, membershipObjectFences,
                    membershipPathFences, onInventory, token);
                sourceRefreshOperations.Clear();
            }
            else
            {
                // A directory move invalidates descendant memberships and needs a fresh
                // complete observation. Rewind retains its existing fresh-observation path.
                await Observer().ObserveAsync(NormalizeTrigger(triggerKind, initial: false), token);
                outcomes = await planner.PlanAsync(pair.Id, token);
            }
            async Task<bool> ExecuteOutcomesAsync(IReadOnlyList<SyncAuthorityPlannerOutcome> batchOutcomes)
            {
            await authority.MarkOperationGroupActiveAsync(operationGroupId, token);
            foreach (var outcome in batchOutcomes.Where(item => item.OperationId is null &&
                                                            remaining.Contains(item.RelativePath)))
            {
                var decision = Decision(outcome);
                if (sourceRefreshAttempted.Contains(outcome.RelativePath))
                    decisions[outcome.RelativePath] = decision;
                else
                    RetainMostMaterial(decisions, decision);
                var state = decision.Kind switch
                {
                    SyncDecisionKind.Conflict => SyncRewindPathStatus.Conflict,
                    SyncDecisionKind.Blocked => SyncRewindPathStatus.Blocked,
                    _ => SyncRewindPathStatus.Completed
                };
                await onCheckpoint(new SyncAuthorityReviewedPathCheckpoint(outcome.RelativePath, state,
                    null, decision));
                touched.Add(outcome.RelativePath);
                if (state == SyncRewindPathStatus.Blocked)
                {
                    // ENG2 opt-in (the reviewed first sync asks for it; Rewind keeps the original stop-on-blocked
                    // behaviour): this exact path's blocked decision and checkpoint are already retained above, so
                    // the path leaves the reviewed set and the independent reviewed paths continue. An
                    // effect-unknown, partial, stopped or ambiguous outcome still ends the run below instead.
                    if (!continueAfterBlockedPaths)
                        return false;
                    blockedPaths++;
                }
                remaining.Remove(outcome.RelativePath);
            }

            var executable = new List<SyncAuthorityPlannedPairOperation>();
            var pathsByOperation = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var operationOutcomes = batchOutcomes.Where(item => item.OperationId is not null).ToArray();
            var loadingStarted = DateTimeOffset.UtcNow;
            if (onInventory is not null && operationOutcomes.Length > 0)
                await onInventory(new SyncComparisonProgress("Loading Planned File Operations", pair.Name,
                    0, operationOutcomes.Length, loadingStarted, DateTimeOffset.UtcNow,
                    $"Reading retained members and allocations for {operationOutcomes.Length:N0} planned operations in one batch. No file transfer has started.",
                    "operations loaded"));
            async Task ReportOperationLoad(int index, string operationId)
            {
                if (onInventory is not null)
                    await onInventory(new SyncComparisonProgress("Loading Planned File Operations",
                        operationOutcomes[index].RelativePath, index, operationOutcomes.Length,
                        loadingStarted, DateTimeOffset.UtcNow,
                        $"Reading retained operation {operationId}: its status, members, dependencies, allocations and attempts. All file effects still require their own current precondition checks; no transfer has started.",
                        "operations loaded"));
            }
            var operationSnapshots = await authority.LoadOperationsAsync(
                operationOutcomes.Select(item => item.OperationId!).ToArray(), token, ReportOperationLoad);
            if (onInventory is not null)
                await onInventory(new SyncComparisonProgress("Preparing File Transfers", pair.Name,
                    operationSnapshots.Count, operationOutcomes.Length, loadingStarted, DateTimeOffset.UtcNow,
                    $"Loaded {operationSnapshots.Count:N0} retained operations in {(DateTimeOffset.UtcNow - loadingStarted).TotalSeconds:F3}s. Building the ordered transfer batch; no file transfer has started.",
                    "operations loaded"));
            for (var operationIndex = 0; operationIndex < operationOutcomes.Length; operationIndex++)
            {
                var outcome = operationOutcomes[operationIndex];
                var operation = operationSnapshots[operationIndex];
                var operationPaths = ReviewedOperationPaths(operation, remaining).ToArray();
                if (operationPaths.Length == 0)
                    throw new InvalidDataException("A reviewed operation does not belong to an unfinished preview path.");
                var planned = new SyncDecision(outcome.RelativePath, SyncDecisionKind.Baseline,
                    $"Reviewed operation {operation.OperationId} is durably planned and awaiting execution.");
                foreach (var path in operationPaths)
                    await onCheckpoint(new SyncAuthorityReviewedPathCheckpoint(path,
                        SyncRewindPathStatus.Planned, operation.OperationId, planned));
                executable.Add(new(outcome.RelativePath, operation));
                pathsByOperation.Add(operation.OperationId, operationPaths);
            }
            var completedBatch = await ScheduleAsync(executable
                .OrderBy(item => ExecutionClass(item.Operation.Kind))
                .ThenBy(item => ExecutionDepth(item.Operation.Kind, item.RelativePath))
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), async plannedAction =>
            {
                await runControl.WaitBeforeNextEffectAsync(token);
                await NotifyLiveActionAsync(onAction, plannedAction, "Starting",
                    "The reviewed source, destination and retained preconditions are being checked.",
                    SyncActivityPhase.Running);
                return await DispatchAsync(plannedAction.Operation, workerId, token, onAction);
            }, async (plannedAction, result) =>
            {
                var operation = plannedAction.Operation;
                var operationPaths = pathsByOperation[operation.OperationId];
                var decision = Decision(plannedAction.RelativePath, operation.Kind, result);
                if (sourceRefreshAttempted.Contains(plannedAction.RelativePath))
                    decisions[plannedAction.RelativePath] = decision;
                else
                    RetainMostMaterial(decisions, decision);
                await CheckpointOperationAsync(operation, decision, remaining, onCheckpoint);
                await NotifyLiveActionAsync(onAction, plannedAction,
                    result.NotAttempted ? "Not attempted" : result.Status == SyncAuthorityOperationStatus.Completed ? "Completed" : "Needs attention",
                    result.Reason, SyncEndpointActivityPresentation.PhaseFor(result.Status));
                foreach (var path in operationPaths) touched.Add(path);
                if (result.Status == SyncAuthorityOperationStatus.Completed)
                {
                    executed++;
                    foreach (var path in operationPaths) remaining.Remove(path);
                }
                else
                {
                    // A same-identity source edited since review gets one fresh
                    // observation/planning pass, retaining the original path and
                    // direction fence. Never relax executor checks or retry an
                    // identity replacement, changed destination or issued effect.
                    if (continueAfterBlockedPaths && reviewedLocalScanId is not null &&
                        result.SourceChangedBeforeEffect &&
                        operationPaths.All(path => !sourceRefreshAttempted.Contains(path)) &&
                        await authority.IsKnownNoEffectRefusalAsync(operation.OperationId, token))
                    {
                        foreach (var path in operationPaths)
                        {
                            sourceRefreshAttempted.Add(path);
                            sourceRefreshPaths.Add(path);
                        }
                        sourceRefreshOperations.Add(operation);
                        await NotifyLiveActionAsync(onAction, plannedAction, "Refreshing approved source",
                            "This source file changed after review. No user file effect was issued. " +
                            "Re-observing it once and re-planning only unfinished approved paths in their original direction; independent files continue.",
                            SyncActivityPhase.Running);
                        return true;
                    }
                    if (!continueAfterBlockedPaths ||
                        !result.NotAttempted && !await authority.IsKnownNoEffectRefusalAsync(operation.OperationId, token))
                        return false;
                    blockedPaths++;
                    // Retain the proven no-effect refusal and its checkpoint.
                    // Continue disjoint members of this sealed batch only; do
                    // not automatically reissue it in another planning pass.
                    foreach (var path in operationPaths) remaining.Remove(path);
                }
                directoryMoved |= result.Status == SyncAuthorityOperationStatus.Completed && IsDirectoryMove(operation);
                return !directoryMoved;
            }, token, allowDirectoryCreates: allowIndependentDirectoryCreates && Cloud is null && cloudFiles is null);
            return completedBatch;
            }

            if (streamStopped)
            {
                if(directoryMoved || sourceRefreshPaths.Count>0)continue;
                return ReviewedResult();
            }
            var completedBatch = await ExecuteOutcomesAsync(outcomes);
            if (!completedBatch && !directoryMoved) return ReviewedResult();

            if (planner.WorkWindowFull)
            {
                if (executed > 0)
                {
                    if (!directoryMoved && sourceRefreshPaths.Count == 0) capacityContinuations++;
                    continue;
                }
                workWindowWaiting = true;
                return ReviewedResult();
            }
            if (sourceRefreshPaths.Count > 0 || planner.RequiresFreshObservation && executed>0) continue;
            if (blockedPaths > 0 && continueAfterBlockedPaths) break;
            if (executed == 0)
            {
                foreach (var path in remaining.Where(path => !touched.Contains(path)).ToArray())
                {
                    var decision = new SyncDecision(path, SyncDecisionKind.Blocked,
                        "The exact reviewed path produced no terminal reconciliation decision.");
                    RetainMostMaterial(decisions, decision);
                    await onCheckpoint(new SyncAuthorityReviewedPathCheckpoint(path,
                        SyncRewindPathStatus.Blocked, null, decision));
                }
                break;
            }
        }

        if (runControl.IsStopRequested)
        {
            var stopped = new SyncDecision("<rewind>", SyncDecisionKind.Stopped,
                "Stop was requested; completed Rewind paths and every uncertain effect remain durably checkpointed.");
            RetainMostMaterial(decisions, stopped);
        }
        return ReviewedResult();
    }

    private static async Task CheckpointOperationAsync(SyncAuthorityOperationSnapshot operation,
        SyncDecision decision, IReadOnlySet<string> reviewedPaths,
        Func<SyncAuthorityReviewedPathCheckpoint, Task> onCheckpoint)
    {
        var status = decision.Kind switch
        {
            SyncDecisionKind.ResponseUnknown => SyncRewindPathStatus.EffectUnknown,
            SyncDecisionKind.Conflict => SyncRewindPathStatus.Conflict,
            SyncDecisionKind.Blocked or SyncDecisionKind.CleanupPending => SyncRewindPathStatus.Blocked,
            SyncDecisionKind.Stopped => SyncRewindPathStatus.Stopped,
            _ => SyncRewindPathStatus.Completed
        };
        var paths = ReviewedOperationPaths(operation, reviewedPaths).ToArray();
        if (paths.Length == 0)
            throw new InvalidDataException("A Rewind operation has no member inside its exact reviewed path set.");
        foreach (var path in paths)
            await onCheckpoint(new SyncAuthorityReviewedPathCheckpoint(path, status,
                operation.OperationId, decision with { RelativePath = path }));
    }

    private static IEnumerable<string> ReviewedOperationPaths(SyncAuthorityOperationSnapshot operation,
        IReadOnlySet<string> reviewedPaths) => operation.Members
        .Select(item => item.ExpectedPathKey)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => PilotPath.NormalizeRelative(path!))
        .Where(reviewedPaths.Contains)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string OperationDisplayPath(SyncAuthorityOperationSnapshot operation) =>
        operation.Members.Select(item => item.ExpectedPathKey)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? "<rewind>";

    public async Task<SyncRunResult> RunAsync(string triggerKind,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            runControl.StopToken);
        var token = linked.Token;
        var monitorWatermark = await authority.CaptureMonitorWatermarkAsync(pair.Id, token);
        var finalByPath = new Dictionary<string, SyncDecision>(StringComparer.OrdinalIgnoreCase);
        var refusedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workerId = "pair-runner-" + Guid.NewGuid().ToString("N");

        var recoveryDrained = await DrainRunnableOperationsAsync(workerId, finalByPath, token);
        if (!recoveryDrained)
            return new SyncRunResult(pair.Id, finalByPath.Values
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
        if (await authority.LoadPairStateAsync(pair.Id, token) == "discovery_required")
        {
            var observer = Observer();
            await observer.ObserveAsync(NormalizeTrigger(triggerKind, initial: true), token);
            var adopted = await new SyncAuthorityInitialPlanner(authority, CloudComparison).AdoptAsync(pair.Id, token);
            foreach (var decision in adopted) RetainMostMaterial(finalByPath, decision);
            if (adopted.Any(item => item.Kind == SyncDecisionKind.Blocked))
                return new SyncRunResult(pair.Id, finalByPath.Values
                    .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
            await authority.SetPairStateAsync(pair.Id, "active", token);
            if (pair.InitialReviewApproved == false)
            {
                RetainMostMaterial(finalByPath, new SyncDecision("<first-sync>", SyncDecisionKind.Blocked,
                    "Initial inventory is complete. Review the proposed uploads, downloads and conflicts, then start the first sync. No file has been copied yet."));
                return new SyncRunResult(pair.Id, finalByPath.Values
                    .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
            }
        }

        else if (pair.InitialReviewApproved == false)
        {
            RetainMostMaterial(finalByPath, new SyncDecision("<first-sync>", SyncDecisionKind.Blocked,
                "First-sync review is waiting. Review the proposed actions, then start the first sync. No file has been copied yet."));
            return new SyncRunResult(pair.Id, finalByPath.Values.ToArray());
        }

        var capacityContinuations = 0;
        for (var pass = 0; pass < MaximumPlanningPasses + capacityContinuations; pass++)
        {
            token.ThrowIfCancellationRequested();
            await Observer().ObserveAsync(NormalizeTrigger(triggerKind, initial: false), token);

            var localScan = await authority.LoadLatestCompleteScanIdAsync(pair.Id, "local", token);
            var remoteScan = await authority.LoadLatestCompleteScanIdAsync(pair.Id, "dropbox", token);
            var cut = await authority.LoadPlanningObservationSetAsync(pair.Id, options.ConfigurationRevision,
                localRootBindingId, dropboxRootBindingId, localScan, remoteScan, token);
            Task RetainWindow(SyncAuthorityPairExecutionResult result)
            {
                foreach (var decision in result.Decisions)
                {
                    RetainMostMaterial(finalByPath, decision);
                    if (decision.Kind == SyncDecisionKind.Blocked &&
                        !(result.PlanningInvalidated && decision.NotAttempted))
                        refusedPaths.Add(decision.RelativePath);
                }
                return Task.CompletedTask;
            }
            var plan = await PlanBoundAsync(cut, SyncAuthorityReconciliationPlanner.ObservationSetIdentity(cut),
                cancellationToken: token, onComparison: onInventory, pathsAttemptedThisRun: refusedPaths,
                streamIndependentFiles: true, onIndependentResult: RetainWindow);
            foreach (var decision in plan.Decisions) RetainMostMaterial(finalByPath, decision);
            var result = await ExecuteBoundAsync(plan, token);
            await RetainWindow(result);
            if (result.Decisions.Any(item => item.Kind is SyncDecisionKind.ResponseUnknown or
                    SyncDecisionKind.CleanupPending or SyncDecisionKind.Stopped))
                break;
            if (plan.WorkWindowFull)
            {
                if (result.CompletedEffects > 0)
                {
                    if (!result.PlanningInvalidated) capacityContinuations++;
                    continue;
                }
                RetainMostMaterial(finalByPath,new SyncDecision("<work-window>",SyncDecisionKind.Blocked,
                    "Waiting for retained work to drain before planning more files.") { NotAttempted=true });
                break;
            }
            if (result.CompletedEffects == 0 && !result.PlanningInvalidated) break;
        }

        if (runControl.IsStopRequested)
            finalByPath["<run>"] = new SyncDecision("<run>", SyncDecisionKind.Stopped,
                "Stop was requested; durable operation and recovery authority was retained.");
        else if (!finalByPath.Values.Any(item => item.NotAttempted || item.Kind is
                     SyncDecisionKind.Blocked or SyncDecisionKind.Conflict or
                     SyncDecisionKind.ResponseUnknown or SyncDecisionKind.CleanupPending))
            await authority.CloseMonitorHintsThroughAsync(pair.Id, monitorWatermark, token);
        return new SyncRunResult(pair.Id, finalByPath.Values
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static int ExecutionClass(SyncAuthorityOperationKind kind) => kind switch
    {
        SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote => 0,
        SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote => 1,
        SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote => 2,
        _ => 3
    };

    private static int ExecutionDepth(SyncAuthorityOperationKind kind, string path)
    {
        var depth = PilotPath.NormalizeRelative(path).Count(character => character == '/') + 1;
        return kind is SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote
            ? -depth
            : depth;
    }

    /// <summary>
    /// CC3E-2: exactly one directory-move detector, used to stop a stale plan before any descendant of a moved
    /// parent executes. A file move never needs it because no descendant can exist beneath it.
    /// </summary>
    private static bool IsDirectoryMove(SyncAuthorityOperationSnapshot operation) =>
        operation.Kind is SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote &&
        operation.Members.Any(member => string.Equals(member.Role, "source", StringComparison.Ordinal) &&
            string.Equals(MemberObjectKind(member), "directory", StringComparison.Ordinal));

    public async Task<SyncRunResult> ResolveConflictAsync(string relativePath,
        SyncAuthorityConflictResolutionKind resolutionKind, string? expectedConflictId = null,
        long? expectedConflictGeneration = null, CancellationToken cancellationToken = default)
    {
        using var work = await PrepareConflictResolutionAsync(relativePath, resolutionKind,
            expectedConflictId, expectedConflictGeneration, cancellationToken);
        return await work.ExecuteAsync(cancellationToken);
    }

    internal sealed record PreparedConflictResolution(SyncAuthorityOperationSnapshot SchedulingOperation,
        Func<CancellationToken, Task<SyncRunResult>> ExecuteAsync, IDisposable? ControlRoot) : IDisposable
    {
        public void Dispose() => ControlRoot?.Dispose();
    }

    internal async Task<PreparedConflictResolution> PrepareConflictResolutionAsync(string relativePath,
        SyncAuthorityConflictResolutionKind resolutionKind, string? expectedConflictId,
        long? expectedConflictGeneration, CancellationToken cancellationToken)
    {
        if ((expectedConflictId is null) != (expectedConflictGeneration is null))
            throw new ArgumentException("Expected conflict identity and generation must be supplied together.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            runControl.StopToken);
        var token = linked.Token;
        var path = PilotPath.NormalizeRelative(relativePath);
        var conflict = await authority.LoadCurrentConflictForPathAsync(pair.Id, path, token)
                       ?? throw new InvalidOperationException("The selected path has no current conflict generation.");
        if (expectedConflictId is not null &&
            (!string.Equals(conflict.ConflictId, expectedConflictId, StringComparison.Ordinal) ||
             conflict.ConflictGeneration != expectedConflictGeneration))
            throw new InvalidOperationException(
                "The conflict changed after it was selected; no resolution effect was issued. Refresh and review the new generation.");
        await runControl.WaitBeforeNextEffectAsync(token);
        var controlRoot = Cloud?.OpenControlRoot();
        try
        {
        var coordinator = Cloud is null
            ? new SyncAuthorityConflictResolutionCoordinator(pair, remoteStore, authority, options, admission)
            : new SyncAuthorityConflictResolutionCoordinator(pair, remoteStore, authority, options, admission,
                new SyncAuthorityCloudConflictContext(new SyncAuthorityCloudExecutionContext(
                    Cloud.Prepared.Endpoints, Cloud.Prepared.PrivateStaging, controlRoot!),
                    Cloud.Prepared.SideBindings));
        var plan = await coordinator.PlanAsync(conflict.ConflictId, conflict.ConflictGeneration, resolutionKind, token);
        var operation = await authority.LoadOperationAsync(plan.OperationId, token);
        // Scheduling-only projection. Never persist changed member JSON or change an operation's
        // deterministic identity. The coordinator has proved both retained versions are files.
        var members = new List<SyncAuthorityMember>();
        var footprints = new List<SyncAuthorityOperationSnapshot> { operation };
        if (plan.PreserveOperationId is { } prerequisite)
            footprints.Add(await authority.LoadOperationAsync(prerequisite, token));
        foreach (var footprint in footprints)
        foreach (var member in footprint.Members)
        {
            if (member.ObjectVersionId is not { } versionId)
            {
                // Missing type evidence stays a serial scheduling barrier.
                members.Add(member);
                continue;
            }
            var version = await authority.LoadObjectVersionAsync(versionId, token);
            members.Add(version.ContentHash is not null && version.Size is not null
                ? member with { ExpectedStateJson = JsonSerializer.Serialize(new {
                    objectKind = "file", Size = version.Size }) }
                : member);
        }
        return new PreparedConflictResolution(operation with { Members = members },
            async cancellation =>
            {
                using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellation, runControl.StopToken);
                await runControl.WaitBeforeNextEffectAsync(executionCancellation.Token);
                return await ExecutePreparedConflictAsync(path, resolutionKind, conflict.ConflictId,
                    conflict.ConflictGeneration, coordinator, plan, operation, executionCancellation.Token);
            }, controlRoot);
        }
        catch
        {
            controlRoot?.Dispose();
            throw;
        }
    }

    private async Task<SyncRunResult> ExecutePreparedConflictAsync(string path,
        SyncAuthorityConflictResolutionKind resolutionKind, string conflictId, long conflictGeneration,
        SyncAuthorityConflictResolutionCoordinator coordinator, SyncAuthorityConflictResolutionPlan plan,
        SyncAuthorityOperationSnapshot operation, CancellationToken token)
    {
        var worker = "conflict-runner-" + Guid.NewGuid().ToString("N");
        var display = new SyncAuthorityPlannedPairOperation(path, operation);
        await NotifyLiveActionAsync(onExecution, display, "Starting",
            $"Applying {resolutionKind} to conflict {conflictId}, generation {conflictGeneration}; " +
            "the losing version must be retained before replacement.", SyncActivityPhase.Running);
        using var byteProgress = SyncByteProgressStream.Begin(path, progress =>
            NotifyLiveActionAsync(onExecution, new(progress.Path, operation), progress.Phase,
                progress.Detail, SyncActivityPhase.Running,
                progress.Unit is null ? null : new SyncActivityProgress(progress.Bytes, progress.Total,
                    progress.Unit, progress.ElapsedMilliseconds)));
        SyncAuthorityExecutionResult result;
        var sourceChangedBeforeEffect = false;
        try
        {
            result = await coordinator.ExecuteAsync(plan, worker, token);
        }
        catch (SyncAuthoritySourceChangedException error) when (!token.IsCancellationRequested)
        {
            // Conflict execution uses the same fenced failure journal as ordinary file execution.
            // A version check before dispatch is not an unknown file effect. Retire this exact
            // resolution only when the whole group proves no attempt or preparation was issued;
            // immutable versions/choices remain history and a fresh observation supplies new authority.
            result = await authority.RetainExecutionFailureAsync(plan.OperationId, worker, error, token);
            sourceChangedBeforeEffect = true;
        }
        // Older callers/startup may already have journalled the same zero-effect refusal as
        // superseded while leaving its conflict choice executing. Such a primary cannot throw
        // another source-version exception: the coordinator correctly refuses to run it.
        // Apply the identical whole-group guard to that retained terminal result, not a reset.
        if (!result.EffectDispatched &&
            (sourceChangedBeforeEffect || result.Status == SyncAuthorityOperationStatus.Superseded) &&
            await authority.MarkStaleUnissuedConflictResolutionAsync(
                plan.ConflictId, plan.ConflictGeneration, plan.OperationGroupId, token))
            result = result with
            {
                NotAttempted = true,
                SourceChangedBeforeEffect = true,
                Reason = result.Reason + " The exact old operation group has no attempts or prepared resources. " +
                    "Its conflict choice is retained as stale, not applied. " +
                    "Fresh observations must pass through the ordinary baseline-aware planner; " +
                    "only a current two-sided conflict needs a choice. Completed files are not replayed."
            };
        await NotifyLiveActionAsync(onExecution, display,
            result.NotAttempted ? "Not attempted" : result.Status == SyncAuthorityOperationStatus.Completed
                ? "Completed" : "Needs attention", result.Reason,
            SyncEndpointActivityPresentation.PhaseFor(result.Status));
        var rightLabel = Cloud?.Prepared.Endpoints.Right.Definition.DisplayName ?? "Dropbox";
        var decision = result.Status switch
        {
            SyncAuthorityOperationStatus.Completed => new SyncDecision(path, SyncDecisionKind.Recovered,
                resolutionKind == SyncAuthorityConflictResolutionKind.PreserveBoth
                    ? $"Both versions were retained; the {rightLabel} version is visible as {plan.ConflictCopyPath}."
                    : "The selected conflict generation was resolved after retaining its losing version."),
            SyncAuthorityOperationStatus.EffectUnknown => new SyncDecision(path, SyncDecisionKind.ResponseUnknown,
                result.Reason),
            _ => new SyncDecision(path, SyncDecisionKind.Blocked, result.Reason) { NotAttempted = result.NotAttempted }
        };
        return new SyncRunResult(pair.Id, [decision]);
    }

    private SyncAuthorityObservationCoordinator Observer() =>
        // CC4B: a runner carrying the typed cloud context observes through that context's exact prepared
        // endpoint set, so a cloud-only pair never resolves an observation against its absent local root.
        // An unattached legacy runner keeps the identical physical observer it has always built.
        new(pair, remoteStore, authority, localRootBindingId, dropboxRootBindingId, onInventory,
            Cloud?.Prepared.Endpoints) { InventoryResources = InventoryResources,
                ConcurrentProgressRetention = ConcurrentProgressRetention };

    // One bounded pass over the invocation's exact retained set. Never drain the
    // pair queue, retire unrelated plans, or revisit a reconciled Ready operation.
    internal async Task<SyncRunResult> VerifyAsync(IReadOnlyCollection<string> operationIds,
        CancellationToken cancellationToken = default)
    {
        var decisions = new List<SyncDecision>();
        var workerId = "pair-verification-" + Guid.NewGuid().ToString("N");
        foreach (var id in operationIds.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = await authority.LoadOperationAsync(id, cancellationToken);
            if (operation.PairId != pair.Id)
                throw new InvalidDataException("Verification operation does not belong to the selected pair.");
            if (operation.Status == SyncAuthorityOperationStatus.Leased &&
                !SyncAuthorityCreateLocalReconciler.HasUnissuedPreparation(operation))
            {
                // The same no-attempt/no-allocation/no-active-holder predicate as recovery,
                // restricted to this selected operation. Never recover the whole pair here.
                await authority.ReconcileUnissuedOperationLeasesAsync(pair.Id, cancellationToken,
                    operationIds: [id]);
                operation = await authority.LoadOperationAsync(id, cancellationToken);
                if (operation.Status == SyncAuthorityOperationStatus.Superseded)
                {
                    var path = RecoveryDecisionPath(operation);
                    const string reason = "Verified that this interrupted lease issued no file effect. The old plan was retired; this file is eligible for a fresh retained retry.";
                    await NotifyLiveActionAsync(onExecution, new(path, operation), "Verified not applied",
                        reason, SyncActivityPhase.Completed);
                    decisions.Add(new(path, SyncDecisionKind.Recovered, reason));
                    continue;
                }
            }
            // Ready is pending consent to retry, not consent conveyed by Verify.
            // Terminal records need no reconciliation and must never be replayed.
            if (operation.Status is SyncAuthorityOperationStatus.Ready or
                SyncAuthorityOperationStatus.Completed or SyncAuthorityOperationStatus.Cancelled or
                SyncAuthorityOperationStatus.Superseded) continue;
            var uncertain = operation.Status is SyncAuthorityOperationStatus.EffectIssuing or
                SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
                SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying;
            // Cancellation can leave an owned private directory before the first attempt.
            // The ordinary recovery path already verifies and retires that exact preparation.
            // Verify must reach that same path, otherwise its own retry preview remains stuck
            // at "Verify first" forever. Never dispatch other Leased/Ready work as a new effect.
            var unissuedPreparation = Cloud is null && cloudFiles is null &&
                SyncAuthorityCreateLocalReconciler.HasUnissuedPreparation(operation);
            var result = (uncertain || unissuedPreparation) && operation.Kind != SyncAuthorityOperationKind.CrossPairMove
                ? await DispatchAsync(operation, workerId, cancellationToken, verificationOnly: true)
                : new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                    "This retained state requires its explicit recovery action; verification issued no new effect.");
            decisions.Add(result.Status is SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Completed or
                SyncAuthorityOperationStatus.Superseded
                ? new SyncDecision(RecoveryDecisionPath(operation), SyncDecisionKind.Recovered,
                    "Verified retained outcome without a new transfer. " + result.Reason)
                : Decision(RecoveryDecisionPath(operation), operation.Kind, result));
        }
        return new SyncRunResult(pair.Id, decisions);
    }

    private async Task<bool> DrainRunnableOperationsAsync(string workerId,
        IDictionary<string, SyncDecision> decisions, CancellationToken cancellationToken)
    {
        await authority.ReconcileUnissuedOperationLeasesAsync(pair.Id, cancellationToken);
        while (true)
        {
            var pageSize = RecoveryPageSizeForTest ?? RecoveryPageSize;
            if (pageSize is < 1 or > 500)
                throw new InvalidOperationException("The recovery page size is outside the bounded authority range.");
            var queue = await authority.ListRecoveryOperationsForPairAsync(pair.Id, pageSize,
                cancellationToken, includePreparedLocalCreates: Cloud is null && cloudFiles is null);
            if (queue.Count == 0)
            {
                await authority.CompleteEligibleConflictResolutionsAsync(pair.Id, cancellationToken);
                return true;
            }

            var progressed = false;
            var executable = new List<SyncAuthorityPlannedPairOperation>();
            foreach (var item in queue)
            {
                var operation = await authority.LoadOperationAsync(item.OperationId, cancellationToken);
                executable.Add(new(RecoveryDecisionPath(operation), operation));
            }
            var completedBatch = await ScheduleAsync(executable, async item =>
            {
                await runControl.WaitBeforeNextEffectAsync(cancellationToken);
                var operation = item.Operation;
                if (operation.Status == SyncAuthorityOperationStatus.Ready &&
                    operation.PlanningAuthorities.Count > 0)
                {
                    var current = true;
                    foreach (var planningAuthority in operation.PlanningAuthorities)
                        current &= await authority.IsPlanningAuthorityCurrentAsync(planningAuthority,
                            cancellationToken);
                    if (!current)
                    {
                        if (await authority.SupersedeReadyOperationIfPlanningAuthorityChangedAsync(
                                operation.OperationId, operation.PlanningAuthorities, cancellationToken))
                        {
                            return (Result: new SyncAuthorityExecutionResult(operation.OperationId,
                                SyncAuthorityOperationStatus.Superseded, false,
                                "A newer observation replaced this queued recovery action before any effect was issued."), Stop: false);
                        }
                        return (Result: new SyncAuthorityExecutionResult(operation.OperationId,
                            SyncAuthorityOperationStatus.Blocked, false,
                            "A stale unissued operation could not be safely superseded; no effect was issued.") { NotAttempted = true }, Stop: true);
                    }
                }
                if (operation.Kind == SyncAuthorityOperationKind.CrossPairMove)
                {
                    return (Result: new SyncAuthorityExecutionResult(operation.OperationId,
                        SyncAuthorityOperationStatus.Blocked, false,
                        $"Cross-pair transaction {operation.OperationId} is retained and must be reconciled by the global coordinator with both exact pair roots available.") { NotAttempted = true }, Stop: false);
                }
                return (Result: await DispatchAsync(operation, workerId, cancellationToken), Stop: false);
            }, async (item, dispatched) =>
            {
                var result = dispatched.Result;
                var operation = item.Operation;
                var path = RecoveryDecisionPath(operation);
                RetainMostMaterial(decisions, Decision(path, operation.Kind, result));
                // CC3D correction: scheduling progress is taken from PERSISTED state, never from the
                // explanatory result projection. A cloud engine may legitimately return an unresolved
                // disposition (for example a cloud deletion whose target is absent but whose durable journal
                // carries no known-success receipt) while the operation's persisted status deliberately stays
                // exactly as it was, and an already-resolved operation may equally report a projection that
                // does not match the retained queue row. Comparing that projection with a potentially stale
                // queue item re-dispatched the same unchanged operation forever, so the exact operation is
                // re-loaded here and its durable status compared with the status loaded immediately before
                // dispatch. The decision projection, the receipts, the unresolved state, the no-reissue rules
                // and the cross-pair handling above are all unchanged.
                var persisted = await authority.LoadOperationAsync(operation.OperationId, cancellationToken);
                progressed |= persisted.Status != operation.Status;
                // CC3E-2: a completed directory move invalidates every descendant this page still carries,
                // because each of those operations was planned against the pre-move paths. Verified by
                // execution: the supersede-on-stale-planning-authority check above does NOT catch them (their
                // authority is still current until the next observation), so without this stop the drain
                // dispatches a stale descendant whose exact retained predecessor is already gone. The pass
                // therefore stops here and returns, and the next real observation and plan reconcile the moved
                // subtree instead of dispatching anything against the pre-move plan. No descendant version is
                // rewritten and nothing was copied or deleted.
                if (persisted.Status == SyncAuthorityOperationStatus.Completed && IsDirectoryMove(operation))
                    return false;
                // A child's unmet dependency is a per-item deferral, not a
                // reason to skip unrelated recoverable files in the same page.
                return !dispatched.Stop;
            }, cancellationToken);
            if (!completedBatch) return false;
            await authority.CompleteEligibleConflictResolutionsAsync(pair.Id, cancellationToken);
            if (queue.All(item => item.Kind == SyncAuthorityOperationKind.CrossPairMove)) return true;
            if (!progressed) return false;
        }
    }

    internal static string RecoveryDecisionPath(SyncAuthorityOperationSnapshot operation)
        => RecoveryDecisionPath(operation.Kind, operation.Members);

    internal static string RecoveryDecisionPath(SyncAuthorityOperationKind kind,
        IEnumerable<SyncAuthorityMember> members)
    {
        // A move's source is the peer already observed at the desired path; its destination is
        // the predecessor still at the old path. Member IDs/order are not presentation authority.
        var move = kind is SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote;
        return members
            .OrderBy(member => move ? member.Role == "source" ? 0 : member.Role == "lock" ? 1 : 2
                : member.Role == "affected" ? 0 : member.Role == "destination" ? 1 : member.Role == "source" ? 2 : 3)
            .ThenBy(member => member.ExpectedPathKey, StringComparer.Ordinal)
            .Select(member => member.ExpectedPathKey)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? "<run>";
    }

    private async Task<SyncAuthorityExecutionResult> DispatchAsync(SyncAuthorityOperationSnapshot operation,
        string workerId, CancellationToken cancellationToken, Func<SyncAuthorityLiveAction, Task>? onAction = null,
        bool verificationOnly = false)
    {
        // The caller selected this exact operation under this runner's explicit command. A later run
        // must adopt retained work before asking for an ordinary lease; immutable plan replay does not
        // transfer its owner. The shared transaction still refuses another live command, a live lease,
        // stale admission or a pair/kind outside this command. Verification adopts too: the original run's
        // command is terminal by the time Verify can run, so without adoption no reconciliation lease could
        // ever be issued. Verification only reaches the read-first reconcilers, which never re-send an effect.
        if ((await authority.LoadCurrentOperationCommandAdmissionAsync(operation.OperationId, cancellationToken))
                ?.CommandRequestId != admission.CommandRequestId)
            await authority.AuthorizeRetainedOperationsAsync(admission, [operation.OperationId], cancellationToken);
        var path = OperationDisplayPath(operation);
        // Reviewed/coordinated callers already publish start/outcome events. Recovery,
        // conflict and legacy callers use the same host sink here, without duplicates.
        var publishLifecycle = onAction is null;
        onAction ??= onExecution;
        var display = new SyncAuthorityPlannedPairOperation(path, operation);
        if (publishLifecycle)
            await NotifyLiveActionAsync(onAction, display, "Starting",
                $"Checking retained {operation.Kind} operation {operation.OperationId} ({operation.Status}) before any effect or recovery action.",
                SyncActivityPhase.Running);
        using var byteProgress = SyncByteProgressStream.Begin(path, async progress =>
        {
            if (onAction is not null)
                await NotifyLiveActionAsync(onAction, new(progress.Path, operation), progress.Phase,
                    progress.Path == path ? progress.Detail :
                        progress.Detail + "; prerequisite check for " + path, SyncActivityPhase.Running,
                    progress.Unit is null ? null : new SyncActivityProgress(progress.Bytes, progress.Total,
                        progress.Unit, progress.ElapsedMilliseconds));
            else if (onInventory is not null)
                await onInventory(new(progress.Phase, progress.Path, 0, 0,
                    progress.StartedUtc, progress.ReportedUtc, progress.Detail));
        });
        // Corrupt authority, lease loss, cancellation and invariant failures are
        // deliberately not converted into ordinary file failures.
        SyncAuthorityExecutionResult result;
        try
        {
            // A capability refusal cannot issue an effect and does not depend
            // on a child finishing. Surface that exact reason for every unsupported
            // directory, rather than hiding its cause behind an unmet dependency.
            if (Cloud is null && operation.Kind == SyncAuthorityOperationKind.DeleteRemote &&
                !remoteStore.SupportsConditionalDirectoryDelete &&
                operation.Members.Any(member => member.Role == "source" && MemberObjectKind(member) == "directory"))
                result = await DispatchCoreAsync(operation, workerId, cancellationToken, verificationOnly);
            else if (operation.Status == SyncAuthorityOperationStatus.Ready &&
                await authority.LoadUnfinishedDependencyAsync(operation.OperationId, cancellationToken) is { } dependency)
                // This child remains unissued. Do not acquire an execution lease
                // around an unsatisfied dependency, or mutate its immutable plan.
                // The caller retains this exact deferral in the run and log.
                result = new(operation.OperationId, SyncAuthorityOperationStatus.Blocked, false,
                    "Not attempted because required parent/dependency did not complete: " + dependency) { NotAttempted = true };
            else
                result = await DispatchCoreAsync(operation, workerId, cancellationToken, verificationOnly);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            error is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            result = await ReconcileFailedLocalDownloadAsync(operation, workerId, error, cancellationToken)
                ?? await authority.RetainExecutionFailureAsync(operation.OperationId,
                    workerId, error, cancellationToken);
            if (error is SyncAuthoritySourceChangedException && !result.EffectDispatched &&
                result.Status == SyncAuthorityOperationStatus.Blocked &&
                await authority.IsKnownNoEffectRefusalAsync(operation.OperationId, cancellationToken))
                result = result with { SourceChangedBeforeEffect = true, NotAttempted = true };
            else if (error is SyncAuthorityDestinationChangedException && !result.EffectDispatched &&
                result.Status == SyncAuthorityOperationStatus.Blocked &&
                await authority.IsKnownNoEffectRefusalAsync(operation.OperationId, cancellationToken))
                result = result with { DestinationChangedBeforeEffect = true, NotAttempted = true };
        }
        if (publishLifecycle)
            await NotifyLiveActionAsync(onAction, display,
                verificationOnly && result.Status is (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Superseded) ? "Verified not applied" :
                    result.NotAttempted ? "Not attempted" : result.Status == SyncAuthorityOperationStatus.Completed ? "Completed" : "Needs attention",
                result.Reason, verificationOnly && result.Status is (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Superseded)
                    ? SyncActivityPhase.Completed : SyncEndpointActivityPresentation.PhaseFor(result.Status));
        return result;
    }

    private async Task<SyncAuthorityExecutionResult?> ReconcileFailedLocalDownloadAsync(
        SyncAuthorityOperationSnapshot original, string workerId, Exception error, CancellationToken token)
    {
        // A failed READ into a private stage is not an uncertain cloud write. The
        // existing identity-fenced reconciler proves whether the local promotion
        // happened and removes only this operation's owned partial stage. Never
        // generalize this to remote writes or blindly reissue the download.
        if (Cloud is not null || cloudFiles is not null ||
            original.Kind != SyncAuthorityOperationKind.CreateLocal)
            return null;
        var current = await authority.LoadOperationAsync(original.OperationId, token);
        if (current.Status != SyncAuthorityOperationStatus.EffectIssuing ||
            current.Attempts.LastOrDefault() is not { Status: SyncAuthorityAttemptStatus.RequestSent,
                EffectKind: "local-create-from-dropbox-revision" }) return null;
        SyncAuthorityExecutionResult reconciled;
        try
        {
            reconciled = await new SyncAuthorityCreateLocalReconciler(pair, authority)
                .ReconcileAsync(original.OperationId, workerId, token);
        }
        catch (Exception verificationError) when (!token.IsCancellationRequested &&
            verificationError is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return await authority.RetainExecutionFailureAsync(original.OperationId, workerId,
                new IOException(SyncActivityText.Failure(error) + " Local outcome verification also failed: " +
                    SyncActivityText.Failure(verificationError), error), token);
        }
        var safe = reconciled.Status == SyncAuthorityOperationStatus.Completed ||
            await authority.IsKnownNoEffectRefusalAsync(original.OperationId, token);
        return reconciled with
        {
            Status = safe ? reconciled.Status : SyncAuthorityOperationStatus.EffectUnknown,
            Reason = SyncActivityText.Failure(error) + " " + reconciled.Reason +
                (safe ? " Independent files can continue; this file was not blindly retried."
                    : " The retained local outcome needs review before any further effects.")
        };
    }

    private async Task<SyncAuthorityExecutionResult> DispatchCoreAsync(SyncAuthorityOperationSnapshot operation,
        string workerId, CancellationToken cancellationToken, bool verificationOnly = false)
    {
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                "The durable operation is already complete.");

        var recovering = operation.Status is SyncAuthorityOperationStatus.EffectIssuing or
            SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
            SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying;
        // CC4B-2: an explicit cloud/cloud pair branches on its ACTUAL typed context BEFORE the legacy
        // committed/cleanup finalizer and before any local-root effect engine, so a cloud operation is never
        // finalized as a local one and never interpreted as pair.LocalRoot. The new-effect direction fence is
        // applied inside that branch, where it fences only a genuinely new cloud effect.
        if (Cloud is not null)
            return await DispatchCloudAsync(operation, recovering, workerId, cancellationToken);
        // The legacy committed/cleanup finalizer keeps its ORIGINAL position: BEFORE the new-effect direction
        // fence. A pair whose direction changed must still finish its already-committed cleanup rather than
        // being stranded by a fence that only governs NEW effects.
        if (operation.Status is SyncAuthorityOperationStatus.Committed or SyncAuthorityOperationStatus.CleanupPending)
            return await new SyncAuthorityCommittedOperationFinalizer(pair, authority)
                .FinalizeAsync(operation.OperationId, workerId, cancellationToken);
        // Retiring owned pre-attempt preparation is cleanup, not a new download.
        // It remains allowed after a direction change, but never on cloud/cloud
        // or placeholder contexts and never with an issued attempt.
        if (cloudFiles is null && SyncAuthorityCreateLocalReconciler.HasUnissuedPreparation(operation))
            return await new SyncAuthorityCreateLocalReconciler(pair, authority)
                .ReconcileUnissuedPreparationAsync(operation.OperationId, workerId, cancellationToken);
        // Direction fences automatic synchronization effects, not a user's retained explicit
        // conflict-resolution/recovery transaction. Those retain their existing authority checks.
        if (!recovering && operation.Kind is (SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote or
            SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote or
            SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote or
            SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote) &&
            !SyncModePolicy.Allows(options.Direction, operation.Kind))
            return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                "No new effect was issued because this pair now uses " + SyncModePolicy.Label(options.Direction) +
                ". The retained operation is preserved for reconciliation or an explicit policy change.");
        SyncAuthorityExecutionResult result;
        try
        {
            result = (operation.Kind, recovering) switch
            {
            (SyncAuthorityOperationKind.CreateRemote, false) =>
                await new SyncAuthorityCreateRemoteExecutor(pair, remoteStore, authority)
                    .ExecuteAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.CreateRemote, true) =>
                await new SyncAuthorityCreateRemoteReconciler(pair, remoteStore, authority)
                    .ReconcileAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.CreateLocal, false) =>
                await new SyncAuthorityCreateLocalExecutor(pair, remoteStore, authority, cloudFiles)
                    .ExecuteAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.CreateLocal, true) =>
                await new SyncAuthorityCreateLocalReconciler(pair, authority, cloudFiles)
                    .ReconcileAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.UpdateRemote, false) =>
                await new SyncAuthorityUpdateRemoteExecutor(pair, remoteStore, authority)
                    .ExecuteAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.UpdateRemote, true) =>
                await new SyncAuthorityUpdateRemoteReconciler(pair, remoteStore, authority)
                    .ReconcileAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.UpdateLocal, false) =>
                await new SyncAuthorityUpdateLocalExecutor(pair, remoteStore, authority, cloudFiles)
                    .ExecuteAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.UpdateLocal, true) =>
                await new SyncAuthorityUpdateLocalReconciler(pair, remoteStore, authority, cloudFiles)
                    .ReconcileAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.DeleteRemote, false) =>
                await new SyncAuthorityDeleteRemoteExecutor(pair, remoteStore, authority)
                    .ExecuteAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.DeleteRemote, true) =>
                await new SyncAuthorityDeleteRemoteReconciler(pair, remoteStore, authority)
                    .ReconcileAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.DeleteLocal, false) =>
                await new SyncAuthorityDeleteLocalExecutor(pair, authority, remoteStore)
                    .ExecuteAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.DeleteLocal, true) =>
                await new SyncAuthorityDeleteLocalReconciler(pair, authority)
                    .ReconcileAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.MoveRemote, false) =>
                await new SyncAuthorityMoveRemoteExecutor(pair, remoteStore, authority)
                    .ExecuteAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.MoveRemote, true) =>
                await new SyncAuthorityMoveRemoteReconciler(pair, remoteStore, authority)
                    .ReconcileAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.MoveLocal, false) =>
                await new SyncAuthorityMoveLocalExecutor(pair, remoteStore, authority)
                    .ExecuteAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.MoveLocal, true) =>
                await new SyncAuthorityMoveLocalReconciler(pair, remoteStore, authority)
                    .ReconcileAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.ResolveConflict, false) =>
                await new SyncAuthorityPreserveConflictCopyExecutor(pair, remoteStore, authority)
                    .ExecuteAsync(operation.OperationId, workerId, cancellationToken),
            (SyncAuthorityOperationKind.ResolveConflict, true) when
                operation.Status == SyncAuthorityOperationStatus.EffectUnknown =>
                await new SyncAuthorityPreserveConflictCopyReconciler(pair, remoteStore, authority)
                    .ReconcileAsync(operation.OperationId, workerId, cancellationToken),
                _ => new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Blocked,
                    false, $"The retained {operation.Kind}/{operation.Status} boundary has no safe dispatcher.")
            };
        }
        catch (SyncAuthorityPlanningInvalidatedException)
        {
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Superseded, false,
                "A fresher coordinated observation superseded this operation at final effect admission.");
        }

        // Only a reconciler which actually advanced a non-Ready operation back
        // to Ready may issue its newly authorised retry here. An unchanged Ready
        // capability/direction refusal is an outcome, not another dispatch request.
        if (result.Status == SyncAuthorityOperationStatus.Ready &&
            operation.Status != SyncAuthorityOperationStatus.Ready && !result.NotAttempted)
        {
            // Verification-only: the reconciler proved the retained outcome (for example the destination is
            // absent => known_not_applied / verified_not_applied retained as Ready). Retain that Ready outcome
            // and do NOT re-enter effect dispatch; issuing the newly authorised retry is the explicit retry
            // command's job, not the verify command's.
            if (verificationOnly) return result;
            var ready = await authority.LoadOperationAsync(operation.OperationId, cancellationToken);
            return await DispatchAsync(ready, workerId, cancellationToken);
        }
        if (!verificationOnly && result.Status == SyncAuthorityOperationStatus.Completed && cloudFiles is not null &&
            operation.Kind is SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote)
            result = await ApplyOnlineOnlyDefaultAsync(operation, result, cancellationToken);
        return result;
    }

    /// <summary>
    /// CC4B-2 and CC3D-2: the ONE cloud/cloud dispatch. Exactly one create engine serves both create kinds,
    /// exactly one update engine serves both update kinds and exactly one deletion engine serves both delete
    /// kinds, chosen only from the retained operation kind through the prepared typed context. A runnable
    /// pre-effect boundary executes; a completed, committed, cleanup-pending or unresolved operation is handled
    /// by the SAME engine's reconciliation, which resumes only its exact owned cleanup or completes only a
    /// proven known-success outcome, so an uncertain effect is never reissued and the outer planning loop
    /// cannot spin on it. No Move or conflict operation has a cloud engine at this intermediate packet, so it
    /// returns an explicit no-effect refusal rather than a legacy fallback or a false completion.
    /// </summary>
    private async Task<SyncAuthorityExecutionResult> DispatchCloudAsync(SyncAuthorityOperationSnapshot operation,
        bool recovering, string workerId, CancellationToken cancellationToken)
    {
        var cloud = Cloud!;
        // CC3D-RES-1 correction: a Validating operation that carries no unresolved target attempt is the exact
        // state the update and deletion engines themselves leave after a verified predecessor recovery, and
        // both engines accept that state as their own resumable pre-effect boundary. It is therefore treated as
        // runnable for exactly those four kinds. Create keeps its previous behaviour because the create engine
        // accepts no such state, and nothing else about this computation changes. A Validating operation is
        // still an as-yet-unissued new target effect, so the direction fence and every other admission check
        // below apply to it exactly as they do to a ready boundary; it is deliberately NOT added to the
        // recovering set, which would classify it as an already-issued request and bypass the direction fence.
        var runnable = operation.Status is SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased ||
            (operation.Status == SyncAuthorityOperationStatus.Validating && operation.Kind is
                SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote or
                SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote);
        var resumable = recovering || operation.Status is SyncAuthorityOperationStatus.Completed or
            SyncAuthorityOperationStatus.Committed or SyncAuthorityOperationStatus.CleanupPending;
        // The direction fences only a NEW cloud effect. An already committed or cleanup-pending cloud
        // operation must still reconcile its exact owned cleanup, and an unresolved one must still reconcile
        // its retained attempt, so neither is refused as though it were a new forbidden-direction effect and
        // neither is ever reissued.
        if (runnable && !recovering &&
            operation.Kind is (SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote or
                SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote or
                SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote or
                SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote) &&
            !SyncModePolicy.Allows(options.Direction, operation.Kind))
            return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                "No new effect was issued because this pair now uses " + SyncModePolicy.Label(options.Direction) +
                ". The retained cloud operation is preserved for reconciliation or an explicit policy change.");
        if (!runnable && !resumable)
            return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Blocked,
                false, "The retained cloud " + operation.Kind + "/" + operation.Status +
                " boundary carries no runnable or resumable cloud step, so no request was issued and nothing is " +
                "reported complete.");
        using var controlRoot = cloud.OpenControlRoot();
        var context = new SyncAuthorityCloudExecutionContext(cloud.Prepared.Endpoints,
            cloud.Prepared.PrivateStaging, controlRoot);
        switch (operation.Kind)
        {
            case SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote:
            {
                var engine = new SyncAuthorityCloudCreateExecutor(authority, context);
                return runnable
                    ? await engine.ExecuteAsync(operation.OperationId, workerId, cancellationToken)
                    : await engine.ReconcileAsync(operation.OperationId, workerId, cancellationToken);
            }
            case SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote:
            {
                var engine = new SyncAuthorityCloudUpdateExecutor(authority, context, cloud.Prepared.SideBindings);
                return runnable
                    ? await engine.ExecuteAsync(operation.OperationId, workerId, cancellationToken)
                    : await engine.ReconcileAsync(operation.OperationId, workerId, cancellationToken);
            }
            case SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote:
            {
                // CC3D: the one generic cloud deletion engine serves both delete kinds. It is selected only
                // from the retained operation kind through this prepared typed context, and it keeps its own
                // refusal for a cloud folder deletion because no current adapter can remove a folder
                // atomically only when it is empty.
                var engine = new SyncAuthorityCloudDeleteExecutor(authority, context, cloud.Prepared.SideBindings);
                return runnable
                    ? await engine.ExecuteAsync(operation.OperationId, workerId, cancellationToken)
                    : await engine.ReconcileAsync(operation.OperationId, workerId, cancellationToken);
            }
            case SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote:
            {
                // CC3E: the one generic endpoint-native move engine serves both move kinds. It is selected
                // only from the retained operation kind through this prepared typed context, and it never
                // opens a local root, never uses a transfer stage and has no copy or delete fallback.
                var engine = new SyncAuthorityCloudMoveExecutor(authority, context);
                return runnable
                    ? await engine.ExecuteAsync(operation.OperationId, workerId, cancellationToken)
                    : await engine.ReconcileAsync(operation.OperationId, workerId, cancellationToken);
            }
            case SyncAuthorityOperationKind.ResolveConflict:
            {
                var engine = new SyncAuthorityCloudConflictCopyExecutor(authority,
                    new SyncAuthorityCloudConflictContext(context, cloud.Prepared.SideBindings));
                return runnable
                    ? await engine.ExecuteAsync(operation.OperationId, workerId, cancellationToken)
                    : await engine.ReconcileAsync(operation.OperationId, workerId, cancellationToken);
            }
            default:
                return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Blocked,
                    false, "A cloud/cloud pair has no effect engine for " + operation.Kind +
                    " at this packet; no local or provider effect was issued and nothing is reported complete.");
        }
    }

    private async Task<SyncAuthorityExecutionResult> ApplyOnlineOnlyDefaultAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityExecutionResult result,
        CancellationToken cancellationToken)
    {
        var path = operation.Members.Select(member => member.ExpectedPathKey)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (path is null) return result;
        try
        {
            await remoteStore.VerifyRootIdentityAsync(cancellationToken);
            var remote = (await remoteStore.ListEntriesAsync(cancellationToken)).SingleOrDefault(item =>
                item.Kind == PilotEntryKind.File &&
                string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase));
            if (remote is null)
                return result with
                {
                    Reason = result.Reason +
                             " The uploaded item was not returned by Dropbox for online-only conversion, so the local file remains available offline."
                };
            if (remote.Kind != PilotEntryKind.File) return result;
            var source = operation.Members.Single(member => member.Role == "source");
            var localVersion = await authority.LoadObjectVersionAsync(source.ObjectVersionId ??
                throw new InvalidDataException("The completed upload has no exact local source version."),
                cancellationToken);
            var adoptedIdentity = AdoptedLocalIdentity(localVersion, remote);
            await authority.AdoptLocalCloudRepresentationAsync(pair.Id, path, localVersion.IdentityJson,
                adoptedIdentity, remote.Id!, remote.Revision!, remote.ContentHash!, remote.Size,
                cancellationToken);
            if (OnlineOnlyAdoptionPhaseForTest is not null)
                await OnlineOnlyAdoptionPhaseForTest(path);
            var actual = await cloudFiles!.ConvertVerifiedSyncedFileAsync(remote, cancellationToken);
            return result with { Reason = result.Reason + (actual.State.Availability == SyncCloudFileAvailability.OnlineOnly
                ? " The verified upload is now online-only on this computer."
                : " The verified upload is available offline on this computer.") };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return result with
            {
                Reason = result.Reason + " The file remains available offline because online-only conversion stopped safely: " +
                         exception.Message
            };
        }
    }

    private static string AdoptedLocalIdentity(SyncAuthorityObjectVersion localVersion,
        PilotRemoteEntry remote)
    {
        if (localVersion.Side != "local" || localVersion.ContentHash is null || localVersion.Size is null ||
            remote.Id is null || remote.Revision is null || remote.ContentHash is null ||
            localVersion.Size != remote.Size ||
            !string.Equals(localVersion.ContentHash, remote.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Online-only conversion requires exact matching local and Dropbox file versions.");
        using var identity = JsonDocument.Parse(localVersion.IdentityJson);
        var root = identity.RootElement;
        var volumeSerial = uint.Parse(root.GetProperty("volumeSerial").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var fileId = ulong.Parse(root.GetProperty("fileId").GetString()!,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var lastWriteUtc = DateTime.Parse(root.GetProperty("lastWriteUtc").GetString()!,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, volumeSerial, fileId,
            lastWriteUtc, remote.Id, remote.Revision,
            root.TryGetProperty("contentAlgorithm", out var algorithm) ? algorithm.GetString() : null,
            localVersion.ContentHash, localVersion.Size);
    }

    private static SyncDecision Decision(SyncAuthorityPlannerOutcome outcome) => outcome.Kind switch
    {
        SyncAuthorityPlannerOutcomeKind.Skipped =>
            new SyncDecision(outcome.RelativePath, SyncDecisionKind.Skipped, outcome.Reason),
        SyncAuthorityPlannerOutcomeKind.Unchanged =>
            new SyncDecision(outcome.RelativePath, SyncDecisionKind.Identical, outcome.Reason),
        SyncAuthorityPlannerOutcomeKind.Adopted =>
            new SyncDecision(outcome.RelativePath, SyncDecisionKind.Baseline, outcome.Reason),
        SyncAuthorityPlannerOutcomeKind.Conflict =>
            new SyncDecision(outcome.RelativePath, SyncDecisionKind.Conflict, outcome.Reason),
        SyncAuthorityPlannerOutcomeKind.Tombstone =>
            new SyncDecision(outcome.RelativePath, SyncDecisionKind.Blocked, outcome.Reason),
        SyncAuthorityPlannerOutcomeKind.Blocked =>
            new SyncDecision(outcome.RelativePath, SyncDecisionKind.Blocked, outcome.Reason),
        _ => new SyncDecision(outcome.RelativePath, SyncDecisionKind.Blocked, outcome.Reason)
    };

    private static SyncDecision Decision(string path, SyncAuthorityOperationKind kind,
        SyncAuthorityExecutionResult result)
    {
        if (result.Status == SyncAuthorityOperationStatus.EffectUnknown)
            return new SyncDecision(path, SyncDecisionKind.ResponseUnknown, result.Reason);
        if (result.Status == SyncAuthorityOperationStatus.CleanupPending)
            return new SyncDecision(path, SyncDecisionKind.CleanupPending, result.Reason);
        if (result.Status != SyncAuthorityOperationStatus.Completed)
            return new SyncDecision(path, SyncDecisionKind.Blocked, result.Reason) { NotAttempted = result.NotAttempted };
        var decisionKind = kind switch
        {
            SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote =>
                SyncDecisionKind.Uploaded,
            SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal =>
                SyncDecisionKind.Downloaded,
            SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote =>
                SyncDecisionKind.Archived,
            _ => SyncDecisionKind.Recovered
        };
        return new SyncDecision(path, decisionKind, result.Reason);
    }

    private static string NormalizeTrigger(string trigger, bool initial)
    {
        if (initial) return "initial";
        return trigger.Trim().ToLowerInvariant() switch
        {
            "manual" => "manual",
            "scheduled" or "startup" => "periodic",
            "monitor" => "monitor",
            "recovery" => "recovery",
            _ => "manual"
        };
    }

    internal static void RetainMostMaterial(IDictionary<string, SyncDecision> decisions, SyncDecision candidate)
    {
        if (decisions.TryGetValue(candidate.RelativePath, out var earlier))
        {
            if (candidate.NotAttempted && !earlier.NotAttempted && DecisionPriority(earlier.Kind) >= 3) return;
            if (earlier.NotAttempted && !candidate.NotAttempted)
            { decisions[candidate.RelativePath] = candidate; return; }
        }
        if (!decisions.TryGetValue(candidate.RelativePath, out var current) ||
            DecisionPriority(candidate.Kind) >= DecisionPriority(current.Kind))
            decisions[candidate.RelativePath] = candidate;
    }

    internal static void RetainPlannerOutcome(IDictionary<string, SyncDecision> decisions,
        SyncAuthorityPlannerOutcome outcome)
    {
        var isTerminalArchiveConfirmation = outcome.IsBothAbsentConfirmation &&
                                            outcome.Kind == SyncAuthorityPlannerOutcomeKind.Tombstone &&
                                            outcome.OperationId is null && outcome.ConflictId is null &&
                                            !string.IsNullOrWhiteSpace(outcome.TombstoneId);
        if (isTerminalArchiveConfirmation &&
            decisions.TryGetValue(outcome.RelativePath, out var current) &&
            (current.Kind == SyncDecisionKind.Archived || DecisionPriority(current.Kind) >= 4))
            return;
        RetainMostMaterial(decisions, Decision(outcome));
    }

    private static int DecisionPriority(SyncDecisionKind kind) => kind switch
    {
        SyncDecisionKind.ResponseUnknown or SyncDecisionKind.CleanupPending => 5,
        SyncDecisionKind.Conflict or SyncDecisionKind.Blocked => 4,
        SyncDecisionKind.Uploaded or SyncDecisionKind.Downloaded or SyncDecisionKind.Archived or
            SyncDecisionKind.Recovered => 3,
        SyncDecisionKind.Baseline => 2,
        _ => 1
    };
}
