using System.Globalization;
using System.Text.Json;
using static Massdex.SyncShark.SyncAuthorityCloudExecution;

namespace Massdex.SyncShark;

// CC3C-1 and CC3C-3: the one internal generic cloud Update executor.
//
// One executor is parameterised by the CC2C typed two-port set and the CC3B private staging context, and it
// selects the logical target from the retained operation kind alone - UpdateLocal targets the logical left
// cloud endpoint and UpdateRemote the logical right one. There is no per-provider and no per-direction
// engine: the source and destination are the exact retained operation member root bindings, the predecessor
// must match the retained destination version exactly, the replacement is a real conditional write carrying
// the retained predecessor revision, and the predecessor is retained in the target's own bound recovery root
// before anything is replaced (CC3C-2). The whole source file is staged and verified through CC3A before the
// target request boundary, and the target-native expected hash is computed from those same held bytes.
//
// Everything after the request boundary is either a verified known success or an unknown outcome: an
// exception after the request was sent is never guessed as applied or not applied, an unknown update retains
// its stage and recovery and is never retried from merely matching bytes or absence, a retained known-success
// receipt plus an exact target readback may resume the commit without another upload, and the committed
// boundary resumes only the exact owned CC3A cleanup. The existing operation lease, fencing, allocation
// state machine, dispatched attempt, receipt, version/membership registration, ordered synchronization point
// and completion transitions are reused; no second journal, allocation scheme or finalizer is introduced.

/// <summary>One safe refusal of the shared cloud update execution slice. It is never a partial effect.</summary>
public sealed class SyncAuthorityCloudUpdateException(string message)
    : SyncAuthorityCloudExecutionException(message);

internal sealed class SyncAuthorityCloudUpdateExecutor(
    SyncAuthorityDatabase authority,
    SyncAuthorityCloudExecutionContext context,
    IReadOnlyList<SyncAuthorityCloudSideAuthority> sideAuthorities)
{
    /// <summary>
    /// The bounded crash/cancellation seam used by the focused proofs. It is an ordinary in-process hook with
    /// no production default; it performs no provider or journal effect itself.
    /// </summary>
    internal static Action<string>? PhaseForTest { get; set; }

    /// <summary>
    /// Dispatches one cloud update on its exact logical target and reaches the committed boundary: the exact
    /// source revision is staged and verified, the exact retained predecessor is retained in its bound
    /// recovery root, the conditional in-place replacement is issued against that predecessor revision, and
    /// the returned status is <c>Committed</c> only after the successor identity and content are verified.
    /// </summary>
    internal async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        argument(operationId, workerId);
        context.RequireHeld();
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            // Repeating a completed cloud update is a no-op; it never recopies and never re-uploads.
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud update is already complete; the repeated call is a no-op and no request was issued.");
        var plan = await RequireUpdatePlanAsync(operation, cancellationToken);
        // A validating operation that carries no unresolved replacement attempt is the exact state this
        // engine itself leaves after a verified predecessor recovery, so a restart resumes the replacement
        // from the retained verified stage and never copies the predecessor twice. Any unresolved target
        // request must be reconciled instead of re-executed.
        if (operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased or
            SyncAuthorityOperationStatus.Validating))
            throw new SyncAuthorityCloudUpdateException(
                "The cloud update is not at a runnable pre-effect boundary; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        if (operation.Attempts.Any(item =>
                !string.Equals(item.EffectKind, SyncAuthorityCloudRecovery.RecoveryEffectKind,
                    StringComparison.Ordinal) &&
                item.Status is SyncAuthorityAttemptStatus.RequestSent or
                    SyncAuthorityAttemptStatus.ResponseReceived or SyncAuthorityAttemptStatus.EffectUnknown))
            throw new SyncAuthorityCloudUpdateException(
                "The cloud update already carries an unresolved target request; it must be reconciled rather than re-executed.");
        var result = await ExecuteReplacementAsync(operation, plan, workerId, cancellationToken);
        return result.Status == SyncAuthorityOperationStatus.Committed
            ? await FinalizeAsync(authority, operationId, workerId, context, "cloud update", cancellationToken)
            : result;
    }

    /// <summary>
    /// Reconciles or finalizes the same cloud update without ever reissuing the replacement. A completed
    /// operation is a no-op; a committed or cleanup-pending operation resumes only its exact owned cleanup; an
    /// unresolved replacement is committed only when the retained upstream authority already proves a
    /// known-success outcome and the exact target readback agrees; every other unknown outcome - including an
    /// unresolved predecessor recovery - is left explicitly unresolved with its stage and recovery retained
    /// and zero target mutation.
    /// </summary>
    internal async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        argument(operationId, workerId);
        context.RequireHeld();
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        var plan = await RequireUpdatePlanAsync(operation, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud update is already complete; reconciliation is a no-op and no request was issued.");
        if (operation.Status is SyncAuthorityOperationStatus.Committed or
            SyncAuthorityOperationStatus.CleanupPending)
            return await FinalizeAsync(authority, operationId, workerId, context, "cloud update",
                cancellationToken);
        if (operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
            SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
            SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new SyncAuthorityCloudUpdateException(
                "The cloud update has no unresolved target request to reconcile; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        var attempt = operation.Attempts.SingleOrDefault(item =>
            (item.Status is SyncAuthorityAttemptStatus.RequestSent or
                SyncAuthorityAttemptStatus.ResponseReceived or SyncAuthorityAttemptStatus.EffectUnknown) &&
            !string.Equals(item.EffectKind, SyncAuthorityCloudRecovery.RecoveryEffectKind,
                StringComparison.Ordinal));
        if (attempt is null)
        {
            // CC3D-DEV-1: an explicit no-effect unresolved result is truthful only when an actual unresolved
            // predecessor-recovery attempt is retained. Its durable operation status and evidence are
            // preserved exactly: no status transition is written, no replacement is issued and nothing is
            // committed. A retained journal with no unresolved attempt of any kind is refused truthfully
            // rather than explained by an invented cause.
            var unresolvedRecovery = operation.Attempts.Any(item =>
                string.Equals(item.EffectKind, SyncAuthorityCloudRecovery.RecoveryEffectKind,
                    StringComparison.Ordinal) && item.Status is SyncAuthorityAttemptStatus.RequestSent or
                    SyncAuthorityAttemptStatus.ResponseReceived or SyncAuthorityAttemptStatus.EffectUnknown);
            return new SyncAuthorityExecutionResult(operationId, operation.Status, false,
                unresolvedRecovery
                    ? "The unresolved cloud update carries no dispatched replacement attempt: only its predecessor recovery copy is unresolved, so no replacement request was issued and nothing was committed. The retained recovery allocation and its evidence are preserved exactly as the journal records it."
                    : "The unresolved cloud update carries no unresolved attempt of any kind in its retained journal, so its retained state is inconsistent: no replacement request was issued, nothing was committed and no cause is inferred.");
        }
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        SyncAuthorityCloudContentStage? stage = null;
        var committed = false;
        try
        {
            stage = await SyncAuthorityCloudContentStage.ReopenVerifiedAsync(authority, operationId,
                context.ControlNamespace, context.ControlRoot, cancellationToken);
            var successor = await ReadBackExactFileAsync(plan.TargetPort, plan.Path, stage, cancellationToken);
            if (successor is null)
                return await UnresolvedAsync(authority, operation, attempt, lease,
                    "The target does not present one exact matching successor; a missing or different target is not proof that this replacement succeeded or failed.",
                    "cloud update", cancellationToken);
            // The sealed authority requires a durable known-success receipt for this attempt before any
            // successor is committed. Without it the commit fails closed and the stage stays retained.
            await CommitAsync(operation, plan, attempt, lease, successor, cancellationToken);
            committed = true;
        }
        catch (InvalidOperationException error) when (error.Message.Contains(
                   "known-success receipt", StringComparison.Ordinal))
        {
            return await UnresolvedAsync(authority, operation, attempt, lease,
                "The unresolved cloud update has no retained known-success receipt, so no successor may be committed and no target mutation was issued.",
                "cloud update", cancellationToken);
        }
        finally
        {
            if (stage is not null) await stage.DisposeAsync();
            await leaseRenewal.ReleaseAsync();
            PhaseForTest?.Invoke(committed ? "reconcile-committed-before-cleanup" : "reconcile-unresolved");
        }
        return await FinalizeAsync(authority, operationId, workerId, context, "cloud update", cancellationToken);
    }

    // ----- replacement execution ----------------------------------------------------------------------

    private async Task<SyncAuthorityExecutionResult> ExecuteReplacementAsync(
        SyncAuthorityOperationSnapshot operation, UpdatePlan plan, string workerId,
        CancellationToken cancellationToken)
    {
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        SyncAuthorityCloudContentStage? stage = null;
        try
        {
            // The exact retained predecessor and the target's own retained recovery authority are established
            // first: both are retained-state checks that issue no effect and create no private object, so a
            // refused predecessor, account, corpus or recovery scope refuses before anything is staged.
            var predecessor = await RequireExactPredecessorAsync(plan, cancellationToken);
            var recovery = await SyncAuthorityCloudRecovery.RequireAuthorityAsync(authority, plan.TargetPort,
                plan.TargetSideAuthority, plan.RecoveryAllocation,
                SyncControlNamespace.NormalizeNamespaceId(context.ControlNamespace.NamespaceId),
                cancellationToken);
            // The exact retained source revision is then read, downloaded and verified into one held private
            // stage before any target object could change. A short, refused or drifted source produces no
            // verified stage. A stage this operation already created is reopened by its exact retained
            // identity instead of being recreated, so a restart never adopts or destroys retained evidence.
            var stageAllocation = operation.Allocations.Single(item =>
                item.Kind == SyncAuthorityAllocationKind.StageFile);
            stage = stageAllocation.Status == SyncAuthorityAllocationStatus.Authorized
                ? await SyncAuthorityCloudContentStage.PrepareAsync(authority, lease,
                    plan.SourceMember.MemberId, plan.SourcePort, context.ControlNamespace, context.ControlRoot,
                    cancellationToken)
                : await SyncAuthorityCloudContentStage.ReopenVerifiedAsync(authority, operation.OperationId,
                    context.ControlNamespace, context.ControlRoot, cancellationToken);
            var targetDigest = await stage.ComputeNativeDigestAsync(plan.TargetPort.Provider, cancellationToken);
            var recovered = await SyncAuthorityCloudRecovery.EnsureRetainedAsync(authority, plan.TargetPort,
                recovery, plan.RecoveryAllocation, predecessor, operation, plan.Path,
                plan.TargetPort.Definition.ContentHashAlgorithm, lease, cancellationToken);
            if (recovered.Refusal is not null) return recovered.Refusal;
            PhaseForTest?.Invoke("after-recovery-verified");
            // CC3C-3: the exact predecessor, its root and the held source stage are re-established
            // immediately before the conditional request boundary. A target that moved since the recovery was
            // retained is refused before any request rather than replaced unconditionally.
            var confirmed = await RequireExactPredecessorAsync(plan, cancellationToken);
            if (!string.Equals(confirmed.Revision, predecessor.Revision, StringComparison.Ordinal))
                throw new SyncAuthorityCloudUpdateException(
                    "The target predecessor revision changed after its predecessor was retained; no conditional replacement was issued and the retained recovery is preserved.");
            stage.VerifyHeld();
            PhaseForTest?.Invoke("predecessor-confirmed-before-request");
            var uploadSession = await PrepareUploadSessionAsync(authority, operation, plan.TargetPort,
                plan.SourceVersion, context, lease, cancellationToken);
            var fingerprint = Hash("cloud-update", plan.PairId, plan.SourcePort.RootBindingId,
                plan.TargetPort.RootBindingId, plan.Path, plan.SourceVersion.ObjectVersionId,
                plan.PredecessorVersion.ObjectVersionId, confirmed.Revision!, stage.NativeHash,
                stage.Size.ToString(CultureInfo.InvariantCulture), targetDigest.Digest,
                recovered.Descriptor!.RecoveryRootBindingId, recovered.Descriptor.RecoveryCorpusId);
            var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease, fingerprint,
                plan.TargetPort.Provider + "-conditional-update", cancellationToken);
            PhaseForTest?.Invoke("request-sent-before-conditional-upload");

            PilotUploadResult uploaded;
            try
            {
                stage.ResetPosition();
                await using var uploadProgress = new SyncByteProgressStream(stage.Content, "Reading bytes for upload", stage.Size);
                await uploadProgress.StartedAsync();
                uploaded = uploadSession is null
                    ? await plan.TargetPort.Store.UploadAsync(plan.Path, uploadProgress, targetDigest.Digest,
                        confirmed.Revision, cancellationToken)
                    : await ((ISyncResumableRemoteStore)plan.TargetPort.Store).UploadResumableAsync(plan.Path,
                        uploadProgress, targetDigest.Digest, confirmed.Revision,
                        uploadSession.Value.Checkpoint, uploadSession.Value.Coordinator.PersistAsync,
                        cancellationToken);
                if (uploadSession is not null)
                    await uploadSession.Value.Coordinator.CompleteAsync(cancellationToken);
            }
            catch (Exception error) when (ConclusiveNonApplication(error))
            {
                await RecordNotAppliedAsync(authority, attempt, lease, plan, stage, fingerprint, error,
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Ready, false,
                    "The destination refused this replacement without applying it; the predecessor and recovery remain: " + SyncActivityText.Failure(error));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await RecordUnknownAsync(authority, attempt, lease, plan, stage, fingerprint, error,
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The conditional replacement response is unknown; verify before retry. The predecessor recovery and private stage remain: " + SyncActivityText.Failure(error));
            }

            PhaseForTest?.Invoke("after-upload-before-receipt");
            var successor = await VerifySuccessorAsync(plan, stage, uploaded, targetDigest, attempt, lease,
                fingerprint, cancellationToken);
            if (successor is null)
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The target response or its independent readback did not establish one exact successor; the outcome is unknown and must be reconciled before any retry.");
            var requestEvidence = JsonSerializer.Serialize(new
            {
                path = plan.Path,
                targetProvider = plan.TargetPort.Provider,
                targetAlgorithm = targetDigest.Algorithm,
                targetDigest = targetDigest.Digest,
                predecessorObjectVersionId = plan.PredecessorVersion.ObjectVersionId,
                predecessorRevision = confirmed.Revision,
                predecessorNativeHash = plan.PredecessorVersion.ContentHash,
                sourceObjectVersionId = plan.SourceVersion.ObjectVersionId,
                sourceNativeHash = plan.SourceVersion.ContentHash,
                sourceSize = plan.SourceVersion.Size,
                recoveryRootBindingId = recovered.Descriptor.RecoveryRootBindingId,
                recoveryCorpusId = recovered.Descriptor.RecoveryCorpusId,
                recoveryFileId = recovered.Descriptor.RetainedFileId
            });
            var destinationJson = JsonSerializer.Serialize(SuccessorState(successor));
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, SourceJson(stage), destinationJson, requestEvidence,
                Hash("known-success", fingerprint, destinationJson), cancellationToken);
            PhaseForTest?.Invoke("after-receipt-before-commit");
            await CommitAsync(operation, plan, attempt, lease, successor, cancellationToken);
            PhaseForTest?.Invoke("after-commit-before-cleanup");
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Committed, true,
                "The one conditional in-place cloud replacement, its retained predecessor recovery and the ordered synchronization point were verified.");
        }
        finally
        {
            if (stage is not null) await stage.DisposeAsync();
            await leaseRenewal.ReleaseAsync();
        }
    }

    // ----- successor verification and commit ------------------------------------------------------------

    private async Task<PilotRemoteEntry?> VerifySuccessorAsync(UpdatePlan plan,
        SyncAuthorityCloudContentStage stage, PilotUploadResult uploaded, SyncAuthorityCloudStageDigest digest,
        SyncAuthorityAttempt attempt, SyncAuthorityLease lease, string fingerprint,
        CancellationToken cancellationToken)
    {
        // A response or readback that does not establish one exact successor is an unknown application
        // outcome, never a confirmed failure: the request may have been applied and must be reconciled.
        try
        {
            if (uploaded is null || string.IsNullOrWhiteSpace(uploaded.FileId) ||
                string.IsNullOrWhiteSpace(uploaded.Revision) || uploaded.Size != stage.Size ||
                string.IsNullOrWhiteSpace(uploaded.ContentHash) ||
                !string.Equals(uploaded.ContentHash, digest.Digest, StringComparison.OrdinalIgnoreCase))
                throw new SyncAuthorityCloudUpdateException(
                    "The conditional replacement response does not describe the exact reviewed size and target-native digest.");
            await plan.TargetPort.Store.VerifyRootIdentityAsync(cancellationToken);
            var entry = await plan.TargetPort.Store.ReadEntryAsync(plan.Path, uploaded.FileId, cancellationToken);
            await plan.TargetPort.Store.VerifyRootIdentityAsync(cancellationToken);
            if (entry.Kind != PilotEntryKind.File || entry.TransferBlockedReason is not null ||
                !string.Equals(entry.Id, uploaded.FileId, StringComparison.Ordinal) ||
                !string.Equals(entry.Revision, uploaded.Revision, StringComparison.Ordinal) ||
                entry.Size != stage.Size || entry.ContentHash is null ||
                !string.Equals(entry.ContentHash, digest.Digest, StringComparison.OrdinalIgnoreCase))
                throw new SyncAuthorityCloudUpdateException(
                    "The independent target readback differs from the returned successor identity, size or native digest.");
            return entry;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
        {
            await RecordUnknownAsync(authority, attempt, lease, plan, stage, fingerprint, error,
                cancellationToken);
            return null;
        }
    }

    /// <summary>
    /// Commits one verified replacement successor: the new version and membership are registered for the
    /// target's own logical slot, the retained source keeps the other slot, and the ordered synchronization
    /// point preserves the left/right version and membership ordering exactly as the authority stores it. The
    /// predecessor version and membership rows are retained unchanged.
    /// </summary>
    private async Task CommitAsync(SyncAuthorityOperationSnapshot operation, UpdatePlan plan,
        SyncAuthorityAttempt attempt, SyncAuthorityLease lease, PilotRemoteEntry successor,
        CancellationToken cancellationToken)
    {
        var identity = plan.TargetPort.ObservationIdentity(successor.Id!, successor.Revision);
        var identityJson = identity.Serialize();
        var contentHash = successor.ContentHash;
        // The successor version identity is exactly the one the observation layer derives for this object,
        // side, serialized identity, hash and size - the same scheme the existing replacement executors use -
        // so the next observation of this target reproduces the committed row instead of colliding with it.
        var versionId = Id("version-" + plan.TargetPort.CompatibilitySide, operation.ObjectId!, identityJson,
            contentHash ?? "none", successor.Size.ToString(CultureInfo.InvariantCulture));
        var generation = await authority.NextMembershipGenerationAsync(plan.DestinationMember.RootBindingId,
            plan.Path, cancellationToken);
        var membershipId = Id("membership-cloud-effect", operation.OperationId, plan.Path, versionId);
        await authority.RegisterObjectVersionAsync(versionId, operation.ObjectId!,
            plan.TargetPort.CompatibilitySide, identityJson, contentHash, successor.Size, cancellationToken);
        await authority.RegisterMembershipAsync(membershipId, operation.ObjectId!,
            plan.DestinationMember.RootBindingId, plan.Path, generation, cancellationToken: cancellationToken);
        var sourceIsLeft = string.Equals(plan.SourcePort.CompatibilitySide,
            SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal);
        var localVersionId = sourceIsLeft ? plan.SourceVersion.ObjectVersionId : versionId;
        var dropboxVersionId = sourceIsLeft ? versionId : plan.SourceVersion.ObjectVersionId;
        var localMembershipId = sourceIsLeft ? plan.SourceMember.MembershipId : membershipId;
        var dropboxMembershipId = sourceIsLeft ? membershipId : plan.SourceMember.MembershipId;
        var pointId = Id("syncpoint-cloud-effect", operation.OperationId,
            plan.SourceVersion.ObjectVersionId, versionId);
        await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, plan.PairId,
            operation.ObjectId!, localVersionId, dropboxVersionId, localMembershipId, dropboxMembershipId,
            "{\"result\":\"cloud update successor committed over its retained predecessor\"}",
            cancellationToken);
    }

    private static async Task RecordNotAppliedAsync(SyncAuthorityDatabase authority, SyncAuthorityAttempt attempt,
        SyncAuthorityLease lease, UpdatePlan plan, SyncAuthorityCloudContentStage? stage, string fingerprint,
        Exception error, CancellationToken cancellationToken)
    {
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownNotApplied, stage is null ? "{}" : SourceJson(stage), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "adapter_declined_before_application",
                adapter = error.GetType().Name,
                provider = plan.TargetPort.Provider
            }), Hash("known-not-applied", fingerprint), cancellationToken);
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
            SyncAuthorityAttemptStatus.VerifiedNotApplied,
            "{\"reason\":\"The typed target adapter conclusively proves it declined the conditional replacement before applying it.\"}",
            cancellationToken);
    }

    private static async Task RecordUnknownAsync(SyncAuthorityDatabase authority, SyncAuthorityAttempt attempt,
        SyncAuthorityLease lease, UpdatePlan plan, SyncAuthorityCloudContentStage? stage, string fingerprint,
        Exception error, CancellationToken cancellationToken)
    {
        await authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
            stage is null ? "{}" : SourceJson(stage), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "response_unknown",
                adapter = error.GetType().Name,
                provider = plan.TargetPort.Provider,
                predecessorObjectVersionId = plan.PredecessorVersion.ObjectVersionId
            }), Hash("effect-unknown", fingerprint), cancellationToken);
    }

    // ----- target preconditions -------------------------------------------------------------------------

    private static async Task<PilotRemoteEntry> RequireExactPredecessorAsync(UpdatePlan plan,
        CancellationToken cancellationToken)
    {
        await plan.TargetPort.Store.VerifyRootIdentityAsync(cancellationToken);
        var entry = await plan.TargetPort.Store.FindEntryAtPathAsync(plan.Path, cancellationToken) ??
            throw new SyncAuthorityCloudUpdateException("The exact retained target predecessor is absent under the typed target root.");
        if (entry.Kind != PilotEntryKind.File || entry.TransferBlockedReason is not null)
            throw new SyncAuthorityCloudUpdateException(
                "The target path does not carry the exact ordinary file the retained predecessor names; it is refused rather than replaced.");
        if (!string.Equals(entry.Id, plan.PredecessorIdentity.FileId, StringComparison.Ordinal) ||
            !string.Equals(entry.Revision, plan.PredecessorIdentity.Revision, StringComparison.Ordinal) ||
            entry.Size != plan.PredecessorVersion.Size ||
            !string.Equals(entry.ContentHash, plan.PredecessorVersion.ContentHash,
                StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudUpdateException(
                "The current target differs from the immutable retained predecessor identity, revision, size or native hash; no conditional replacement was issued.");
        await plan.TargetPort.Store.VerifyRootIdentityAsync(cancellationToken);
        return entry;
    }

    // ----- plan validation ------------------------------------------------------------------------------

    private async Task<UpdatePlan> RequireUpdatePlanAsync(SyncAuthorityOperationSnapshot operation,
        CancellationToken cancellationToken)
    {
        if (operation.Kind is not (SyncAuthorityOperationKind.UpdateLocal or
            SyncAuthorityOperationKind.UpdateRemote))
            throw new SyncAuthorityCloudUpdateException(
                "The shared cloud update executor accepts only an update operation; the retained kind is " +
                SyncAuthorityNames.OperationKind(operation.Kind) +
                " and every other kind is refused before any effect.");
        if (!string.Equals(operation.PairId, context.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudUpdateException(
                "The retained operation belongs to another pair than the typed cloud endpoint set.");
        if (string.IsNullOrWhiteSpace(operation.ObjectId))
            throw new SyncAuthorityCloudUpdateException(
                "The retained update operation has no exact object identity.");
        if (operation.ConfigurationRevision < 1 || string.IsNullOrWhiteSpace(operation.CapabilityProfileId))
            throw new SyncAuthorityCloudUpdateException(
                "The retained update operation has no configuration revision or capability profile authority.");
        var targetSide = SyncAuthorityOperationAllocations.TargetSide(operation.Kind);
        var targetPort = context.Port(targetSide);
        var sourcePort = context.Port(SyncAuthorityCloudExecutionContext.OtherSide(targetSide));
        var sources = operation.Members.Where(item => item.Role == "source").ToArray();
        var destinations = operation.Members.Where(item => item.Role == "destination").ToArray();
        if (sources.Length != 1 || destinations.Length != 1)
            throw new SyncAuthorityCloudUpdateException(
                "The retained update operation does not carry exactly one source and one destination member.");
        var source = sources[0];
        var destination = destinations[0];
        if (!string.Equals(source.PairId, operation.PairId, StringComparison.Ordinal) ||
            !string.Equals(destination.PairId, operation.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudUpdateException(
                "A retained operation member belongs to another pair; the plan is refused.");
        if (!string.Equals(source.RootBindingId, sourcePort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudUpdateException(
                "The retained source member does not bind the exact authority root of its typed cloud port.");
        if (!string.Equals(destination.RootBindingId, targetPort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudUpdateException(
                "The retained destination member does not bind the exact authority root of the target typed cloud port.");
        if (string.IsNullOrWhiteSpace(destination.ObjectVersionId) ||
            string.IsNullOrWhiteSpace(destination.MembershipId))
            throw new SyncAuthorityCloudUpdateException(
                "The update destination is not a retained predecessor; a fresh or membership-less destination is never replaced.");
        if (string.IsNullOrWhiteSpace(source.ObjectVersionId) ||
            string.IsNullOrWhiteSpace(source.MembershipId))
            throw new SyncAuthorityCloudUpdateException(
                "The retained update source has no exact object version and membership authority.");
        var path = RequireExactRetainedPath(source);
        if (!string.Equals(path, RequireExactRetainedPath(destination), StringComparison.Ordinal))
            throw new SyncAuthorityCloudUpdateException(
                "The retained update source and destination do not name the same exact relative path.");
        RequirePreconditions(operation, path);
        var sourceVersion = await authority.LoadObjectVersionAsync(source.ObjectVersionId, cancellationToken);
        RequireRetainedVersionIdentity(sourceVersion, source, sourcePort, "source");
        var predecessorVersion = await authority.LoadObjectVersionAsync(destination.ObjectVersionId,
            cancellationToken);
        var predecessorIdentity =
            RequireRetainedVersionIdentity(predecessorVersion, destination, targetPort, "predecessor");
        if (predecessorVersion.ContentHash is null || predecessorVersion.Size is null ||
            predecessorVersion.Size.Value < 0)
            throw new SyncAuthorityCloudUpdateException(
                "The retained target predecessor has no exact file content identity; a predecessor without an exact native hash and size is never conditionally replaced.");
        RequireAllocationShape(operation);
        var recoveries = operation.Allocations.Where(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).ToArray();
        if (recoveries.Length != 1)
            throw new SyncAuthorityCloudUpdateException(
                "A cloud update requires exactly one retained remote recovery allocation for its target; found " +
                recoveries.Length + ".");
        var matching = sideAuthorities.Where(item =>
            string.Equals(item.CompatibilitySide, targetSide, StringComparison.Ordinal)).ToArray();
        if (matching.Length > 1)
            throw new SyncAuthorityCloudUpdateException(
                "More than one retained side recovery authority names the target logical side; the plan is refused.");
        return new UpdatePlan(operation.PairId!, operation.ObjectId!, path, sourcePort, targetPort, source,
            destination, sourceVersion, predecessorVersion, predecessorIdentity, recoveries[0],
            matching.Length == 1 ? matching[0] : null);
    }

    private void RequireAllocationShape(SyncAuthorityOperationSnapshot operation)
    {
        var namespaceId = SyncControlNamespace.NormalizeNamespaceId(context.ControlNamespace.NamespaceId);
        var allowed = new[]
        {
            SyncAuthorityAllocationKind.TransactionDirectory,
            SyncAuthorityAllocationKind.StageFile,
            SyncAuthorityAllocationKind.RemoteRecoveryFile,
            SyncAuthorityAllocationKind.UploadSession
        };
        if (operation.Allocations.Any(item => !allowed.Contains(item.Kind)))
            throw new SyncAuthorityCloudUpdateException(
                "The retained update plan carries an allocation kind that a cloud update never uses; it is refused before any effect.");
        var transactions = operation.Allocations.Where(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory).ToArray();
        var stages = operation.Allocations.Where(item => item.Kind == SyncAuthorityAllocationKind.StageFile)
            .ToArray();
        var sessions = operation.Allocations.Where(item => item.Kind == SyncAuthorityAllocationKind.UploadSession)
            .ToArray();
        if (transactions.Length != 1 || stages.Length != 1)
            throw new SyncAuthorityCloudUpdateException(
                "A cloud file update requires exactly one private transaction and one private stage allocation.");
        if (sessions.Length > 1)
            throw new SyncAuthorityCloudUpdateException(
                "A cloud file update may reserve at most one upload session.");
        foreach (var allocation in new[] { transactions[0], stages[0] })
            if (!string.Equals(allocation.ControlNamespaceId, namespaceId, StringComparison.Ordinal))
                throw new SyncAuthorityCloudUpdateException(
                    "A private staging allocation does not belong to this exact private control namespace authority.");
        if (!string.Equals(transactions[0].IntendedParentIdentity, namespaceId, StringComparison.Ordinal) ||
            !string.Equals(stages[0].IntendedParentIdentity, transactions[0].AllocationId,
                StringComparison.Ordinal) ||
            !string.Equals(stages[0].DeterministicName, SyncLocalTransactionDirectory.StageName,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudUpdateException(
                "The private staging allocations are not nested exactly as the shared cloud allocation policy requires.");
        foreach (var session in sessions)
            if (!string.Equals(session.ControlNamespaceId, namespaceId, StringComparison.Ordinal))
                throw new SyncAuthorityCloudUpdateException(
                    "The reserved upload session does not belong to this exact private control namespace authority.");
    }

    private void argument(string operationId, string workerId)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sideAuthorities);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
    }

    private sealed record UpdatePlan(
        string PairId,
        string ObjectId,
        string Path,
        SyncAuthorityCloudEndpointPort SourcePort,
        SyncAuthorityCloudEndpointPort TargetPort,
        SyncAuthorityMember SourceMember,
        SyncAuthorityMember DestinationMember,
        SyncAuthorityObjectVersion SourceVersion,
        SyncAuthorityObjectVersion PredecessorVersion,
        SyncAuthorityCloudObservationIdentity PredecessorIdentity,
        SyncAuthorityAllocation RecoveryAllocation,
        SyncAuthorityCloudSideAuthority? TargetSideAuthority);
}
