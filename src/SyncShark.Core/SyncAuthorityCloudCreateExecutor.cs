using System.Globalization;
using System.Text.Json;
using static Massdex.SyncShark.SyncAuthorityCloudExecution;

namespace Massdex.SyncShark;

// CC3B-2 and CC3B-3: the one internal cloud create effect executor.
//
// One executor is parameterised by the CC2C typed two-port set and the CC3A private staging context. The
// operation kind selects the logical target; the source and destination are never inferred from a local
// path, a provider name or a display label - they are resolved from the exact retained operation member
// root bindings and compared with the typed ports' own retained authority roots before any effect.
//
// The effect-bearing slice is deliberately narrow: only a create on either logical cloud target runs here.
// Update, delete, move and conflict kinds, a local-containing pair, a native/unsupported destination, a
// foreign identity, a drifted source, a blocked or rejected destination and a mismatched plan all fail
// closed before the target request boundary. Reuse is total: the existing operation lease, allocation state
// machine, dispatched attempt, receipt, version/membership registration, ordered synchronization point and
// completion transitions, plus the CC3A staged-source and cleanup components. No second engine, journal,
// allocation scheme or provider-specific branch is introduced.

/// <summary>
/// One safe refusal of the shared cloud create execution slice. It is never a partial effect. The context,
/// the path matcher, the adapter exception classification and the committed-boundary finalizer are the
/// shared cloud execution surface (SyncAuthorityCloudExecution.cs); this type adds only the create-specific
/// plan refusals that the CC3B proofs assert by name.
/// </summary>
public sealed class SyncAuthorityCloudCreateException(string message)
    : SyncAuthorityCloudExecutionException(message);

internal sealed class SyncAuthorityCloudCreateExecutor(
    SyncAuthorityDatabase authority,
    SyncAuthorityCloudExecutionContext context)
{
    /// <summary>
    /// The bounded crash/cancellation seam used by the focused proofs. It is an ordinary in-process hook with
    /// no production default; it performs no provider or journal effect itself.
    /// </summary>
    internal static Action<string>? PhaseForTest { get; set; }

    /// <summary>
    /// Dispatches one cloud create on its exact logical target and reaches the committed boundary. The whole
    /// source file is staged and verified through CC3A before the target request boundary; a directory needs no
    /// stage. The returned status is <c>Committed</c> only after the effect and its identity are fully verified.
    /// </summary>
    internal async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        argument(operationId, workerId);
        context.RequireHeld();
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            // CC3B-3: repeating a completed cloud create is a no-op; it never recopies and never re-uploads.
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud create is already complete; the repeated call is a no-op and no request was issued.");
        var plan = await RequireCreatePlanAsync(operation, cancellationToken);
        if (operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased))
            throw new SyncAuthorityCloudCreateException(
                "The cloud create is not at a runnable pre-effect boundary; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        var result = plan.IsDirectory
            ? await ExecuteDirectoryAsync(operation, plan, workerId, cancellationToken)
            : await ExecuteFileAsync(operation, plan, workerId, cancellationToken);
        return result.Status == SyncAuthorityOperationStatus.Committed
            ? await SyncAuthorityCloudExecution.FinalizeAsync(authority, operationId, workerId, context,
                "cloud create", cancellationToken)
            : result;
    }

    /// <summary>
    /// Reconciles or finalizes the same cloud create without ever recopying. A completed operation is a
    /// no-op; a committed or cleanup-pending operation resumes only its exact owned cleanup; an unresolved
    /// attempt is completed only when the retained upstream authority itself proves a known-success outcome
    /// and the exact target identity reads back; every other unknown outcome is left explicitly unresolved
    /// with its stage retained and zero target mutation.
    /// </summary>
    internal async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        argument(operationId, workerId);
        context.RequireHeld();
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        var plan = await RequireCreatePlanAsync(operation, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud create is already complete; reconciliation is a no-op and no request was issued.");
        if (operation.Status is SyncAuthorityOperationStatus.Committed or
            SyncAuthorityOperationStatus.CleanupPending)
            return await SyncAuthorityCloudExecution.FinalizeAsync(authority, operationId, workerId, context,
                "cloud create", cancellationToken);
        if (operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
            SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
            SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new SyncAuthorityCloudCreateException(
                "The cloud create has no unresolved target request to reconcile; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        var attempt = operation.Attempts.SingleOrDefault(item => item.Status is
            SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
            SyncAuthorityAttemptStatus.EffectUnknown);
        if (attempt is null)
            throw new SyncAuthorityCloudCreateException(
                "The unresolved cloud create carries no dispatched attempt to reconcile.");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        SyncAuthorityCloudContentStage? stage = null;
        var committed = false;
        try
        {
            stage = plan.IsDirectory
                ? null
                : await SyncAuthorityCloudContentStage.ReopenVerifiedAsync(authority, operationId,
                    context.ControlNamespace, context.ControlRoot, cancellationToken);
            var successor = plan.IsDirectory
                ? await ReadBackDirectoryAsync(plan, cancellationToken)
                : await ReadBackExactFileAsync(plan.TargetPort, plan.Path, stage!, cancellationToken);
            if (successor is null)
                return await UnresolvedAsync(authority, operation, attempt, lease,
                    "The target does not present one exact matching successor; a missing or different target is not proof that this request succeeded or failed.",
                    "cloud create", cancellationToken);
            // The sealed authority requires a durable known-success receipt for this attempt before any
            // successor is committed. Without it the commit fails closed and the stage stays retained.
            await CommitAsync(operation, plan, attempt, lease, successor, cancellationToken);
            committed = true;
        }
        catch (InvalidOperationException error) when (error.Message.Contains(
                   "known-success receipt", StringComparison.Ordinal))
        {
            return await UnresolvedAsync(authority, operation, attempt, lease,
                "The unresolved cloud create has no retained known-success receipt, so no successor may be committed and no target mutation was issued.",
                "cloud create", cancellationToken);
        }
        finally
        {
            if (stage is not null) await stage.DisposeAsync();
            await leaseRenewal.ReleaseAsync();
            PhaseForTest?.Invoke(committed ? "reconcile-committed-before-cleanup" : "reconcile-unresolved");
        }
        return await SyncAuthorityCloudExecution.FinalizeAsync(authority, operationId, workerId, context,
            "cloud create", cancellationToken);
    }

    // ----- create execution ----------------------------------------------------------------------

    private async Task<SyncAuthorityExecutionResult> ExecuteFileAsync(SyncAuthorityOperationSnapshot operation,
        CreatePlan plan, string workerId, CancellationToken cancellationToken)
    {
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        SyncAuthorityCloudContentStage? stage = null;
        try
        {
            // The exact retained cloud source is read, downloaded and verified into one held private stage
            // before any target object could exist. A short, refused or drifted source produces no stage.
            // The one retained stage allocation of this operation - resolved from its already loaded journal -
            // decides which producer path this attempt may take, exactly as the shared cloud update consumer
            // already selects. A fresh Authorized allocation is the only state that may read, download and
            // verify the retained cloud source into a newly created private stage. Any allocation this
            // operation already created is instead reopened by its exact retained identity and bytes, so a
            // retry after a conclusive no-effect refusal never re-reads the current provider source, never
            // creates a second stage and never adopts, re-downloads or destroys retained evidence.
            var stageAllocation = operation.Allocations.Single(item =>
                item.Kind == SyncAuthorityAllocationKind.StageFile);
            stage = stageAllocation.Status == SyncAuthorityAllocationStatus.Authorized
                ? await SyncAuthorityCloudContentStage.PrepareAsync(authority, lease,
                    plan.SourceMember.MemberId, plan.SourcePort, context.ControlNamespace, context.ControlRoot,
                    cancellationToken)
                : await SyncAuthorityCloudContentStage.ReopenVerifiedAsync(authority, operation.OperationId,
                    context.ControlNamespace, context.ControlRoot, cancellationToken);
            var targetDigest = await stage.ComputeNativeDigestAsync(plan.TargetPort.Provider, cancellationToken);
            await RequireTargetAbsentAsync(plan.TargetPort, plan.Path, cancellationToken);
            var uploadSession = await PrepareUploadSessionAsync(authority, operation, plan.TargetPort,
                plan.SourceVersion, context, lease, cancellationToken);
            var fingerprint = Hash("cloud-create", plan.PairId, plan.SourcePort.RootBindingId,
                plan.TargetPort.RootBindingId, plan.Path, plan.SourceVersion.ObjectVersionId,
                stage.NativeHash, stage.Size.ToString(CultureInfo.InvariantCulture), targetDigest.Digest);
            var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease, fingerprint,
                plan.TargetPort.Provider + "-create-only-add", cancellationToken);
            PhaseForTest?.Invoke("request-sent-before-upload");

            PilotUploadResult uploaded;
            try
            {
                stage.ResetPosition();
                await using var uploadProgress = new SyncByteProgressStream(stage.Content, "Reading bytes for upload", stage.Size);
                await uploadProgress.StartedAsync();
                uploaded = uploadSession is null
                    ? await plan.TargetPort.Store.UploadAsync(plan.Path, uploadProgress, targetDigest.Digest,
                        null, cancellationToken)
                    : await ((ISyncResumableRemoteStore)plan.TargetPort.Store).UploadResumableAsync(plan.Path,
                        uploadProgress, targetDigest.Digest, null, uploadSession.Value.Checkpoint,
                        uploadSession.Value.Coordinator.PersistAsync, cancellationToken);
                if (uploadSession is not null)
                    await uploadSession.Value.Coordinator.CompleteAsync(cancellationToken);
            }
            catch (Exception error) when (ConclusiveNonApplication(error))
            {
                await RecordNotAppliedAsync(attempt, lease, plan, stage, fingerprint, error, cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Ready,
                    false,
                    "The destination refused this file without applying it: " + SyncActivityText.Failure(error));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await RecordUnknownAsync(attempt, lease, plan, stage, fingerprint, error, cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The cloud create response is unknown and must be reconciled before any retry: " + SyncActivityText.Failure(error));
            }

            PhaseForTest?.Invoke("after-upload-before-receipt");
            var successor = await VerifyFileSuccessorAsync(plan, stage, uploaded, targetDigest,
                attempt, lease, fingerprint, cancellationToken);
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
                sourceObjectVersionId = plan.SourceVersion.ObjectVersionId,
                sourceNativeHash = plan.SourceVersion.ContentHash,
                sourceSize = plan.SourceVersion.Size
            });
            var destinationJson = JsonSerializer.Serialize(new
            {
                successor.RelativePath,
                successor.Kind,
                successor.Id,
                successor.Revision,
                successor.Size,
                successor.ContentHash
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, SourceJson(stage), destinationJson, requestEvidence,
                Hash("known-success", fingerprint, destinationJson), cancellationToken);
            PhaseForTest?.Invoke("after-receipt-before-commit");
            await CommitAsync(operation, plan, attempt, lease, successor, cancellationToken);
            PhaseForTest?.Invoke("after-commit-before-cleanup");
            return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Committed,
                true, "The one create-only cloud upload, its exact target identity and the ordered synchronization point were verified.");
        }
        finally
        {
            if (stage is not null) await stage.DisposeAsync();
            await leaseRenewal.ReleaseAsync();
        }
    }

    private async Task<SyncAuthorityExecutionResult> ExecuteDirectoryAsync(
        SyncAuthorityOperationSnapshot operation, CreatePlan plan, string workerId,
        CancellationToken cancellationToken)
    {
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            await RequireSourceDirectoryAsync(plan, cancellationToken);
            await RequireTargetAbsentAsync(plan.TargetPort, plan.Path, cancellationToken);
            var fingerprint = Hash("cloud-create-directory", plan.PairId, plan.SourcePort.RootBindingId,
                plan.TargetPort.RootBindingId, plan.Path, plan.SourceVersion.ObjectVersionId);
            var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease, fingerprint,
                plan.TargetPort.Provider + "-create-only-folder", cancellationToken);
            PhaseForTest?.Invoke("request-sent-before-folder-create");
            PilotRemoteEntry created;
            try
            {
                created = await plan.TargetPort.Store.CreateDirectoryAsync(plan.Path, cancellationToken);
            }
            catch (Exception error) when (ConclusiveNonApplication(error))
            {
                await RecordNotAppliedAsync(attempt, lease, plan, null, fingerprint, error, cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Ready,
                    false,
                    "The target adapter conclusively declined the create-folder request before applying it; nothing was created.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await RecordUnknownAsync(attempt, lease, plan, null, fingerprint, error, cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The cloud folder-create response is unknown and must be reconciled before any retry.");
            }
            PhaseForTest?.Invoke("after-folder-create-before-receipt");
            var successor = await VerifyDirectorySuccessorAsync(plan, created, attempt, lease, fingerprint,
                cancellationToken);
            if (successor is null)
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The target folder response or its independent readback did not establish one exact successor; the outcome is unknown and must be reconciled before any retry.");
        var destinationJson = JsonSerializer.Serialize(SuccessorState(successor));
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, "{}", destinationJson,
                JsonSerializer.Serialize(new { path = plan.Path, targetProvider = plan.TargetPort.Provider }),
                Hash("known-success", fingerprint, destinationJson), cancellationToken);
            await CommitAsync(operation, plan, attempt, lease, successor, cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Committed,
                true, "The exact source folder identity was created on the logical cloud target and verified.");
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    // ----- successor verification and commit -------------------------------------------------------

    private async Task<PilotRemoteEntry?> VerifyFileSuccessorAsync(CreatePlan plan,
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
                throw new SyncAuthorityCloudCreateException(
                    "The create-only response does not describe the exact reviewed size and target-native digest.");
            await plan.TargetPort.Store.VerifyRootIdentityAsync(cancellationToken);
            var entry = await plan.TargetPort.Store.ReadEntryAsync(plan.Path, uploaded.FileId, cancellationToken);
            await plan.TargetPort.Store.VerifyRootIdentityAsync(cancellationToken);
            if (entry.Kind != PilotEntryKind.File || entry.TransferBlockedReason is not null ||
                !string.Equals(entry.Id, uploaded.FileId, StringComparison.Ordinal) ||
                !string.Equals(entry.Revision, uploaded.Revision, StringComparison.Ordinal) ||
                entry.Size != stage.Size || entry.ContentHash is null ||
                !string.Equals(entry.ContentHash, digest.Digest, StringComparison.OrdinalIgnoreCase))
                throw new SyncAuthorityCloudCreateException(
                    "The independent target readback differs from the returned successor identity, size or native digest.");
            return entry;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
        {
            await RecordUnknownAsync(attempt, lease, plan, stage, fingerprint, error, cancellationToken);
            return null;
        }
    }

    private async Task<PilotRemoteEntry?> VerifyDirectorySuccessorAsync(CreatePlan plan,
        PilotRemoteEntry created, SyncAuthorityAttempt attempt, SyncAuthorityLease lease, string fingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            if (created is null || created.Kind != PilotEntryKind.Directory ||
                string.IsNullOrWhiteSpace(created.Id) || created.TransferBlockedReason is not null ||
                !MatchesPath(created, plan.Path))
                throw new SyncAuthorityCloudCreateException(
                    "The create-folder response is not the exact requested directory identity.");
            await plan.TargetPort.Store.VerifyRootIdentityAsync(cancellationToken);
            var entries = await plan.TargetPort.Store.ListEntriesAsync(cancellationToken) ??
                          throw new SyncAuthorityCloudCreateException(
                              "The target returned no inventory for its own root; a missing inventory is never a verified successor.");
            await plan.TargetPort.Store.VerifyRootIdentityAsync(cancellationToken);
            var matches = entries.Where(entry => MatchesPath(entry, plan.Path)).ToArray();
            if (matches.Length != 1 || matches[0].Kind != PilotEntryKind.Directory ||
                matches[0].TransferBlockedReason is not null || string.IsNullOrWhiteSpace(matches[0].Id) ||
                !string.Equals(matches[0].Id, created.Id, StringComparison.Ordinal))
                throw new SyncAuthorityCloudCreateException(
                    "The independent target readback does not confirm exactly the created folder identity.");
            return matches[0];
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
        {
            await RecordUnknownAsync(attempt, lease, plan, null, fingerprint, error, cancellationToken);
            return null;
        }
    }

    private async Task CommitAsync(SyncAuthorityOperationSnapshot operation, CreatePlan plan,
        SyncAuthorityAttempt attempt, SyncAuthorityLease lease, PilotRemoteEntry successor,
        CancellationToken cancellationToken)
    {
        var identity = plan.TargetPort.ObservationIdentity(successor.Id!, successor.Revision);
        var identityJson = identity.Serialize();
        var contentHash = successor.Kind == PilotEntryKind.Directory ? null : successor.ContentHash;
        // The successor version identity is exactly the one the observation layer derives for this object,
        // side, serialized identity, hash and size - the same contract the update executor already uses - so
        // the next observation of this target reproduces the committed row instead of losing its INSERT OR
        // IGNORE to the retained uniqueness constraint and failing closed. The uniqueness guard itself is
        // unchanged: a genuinely different identity still collides and is still refused.
        var versionId = Id("version-" + plan.TargetPort.CompatibilitySide, operation.ObjectId!, identityJson,
            contentHash ?? "none", successor.Size.ToString(CultureInfo.InvariantCulture));
        var generation = await authority.NextMembershipGenerationAsync(plan.DestinationMember.RootBindingId,
            plan.Path, cancellationToken);
        var membershipId = Id("membership-cloud-effect", operation.OperationId, plan.Path, versionId);
        await authority.RegisterObjectVersionAsync(versionId, operation.ObjectId!,
            plan.TargetPort.CompatibilitySide, identityJson, contentHash, successor.Size, cancellationToken);
        await authority.RegisterMembershipAsync(membershipId, operation.ObjectId!,
            plan.DestinationMember.RootBindingId, plan.Path, generation, cancellationToken: cancellationToken);
        // The successor occupies its own logical side's slot and the retained source keeps the other, so the
        // left/right version and membership ordering is preserved exactly as the authority stores it.
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
            "{\"result\":\"cloud create successor committed\"}", cancellationToken);
    }

    private async Task<PilotRemoteEntry?> ReadBackDirectoryAsync(CreatePlan plan,
        CancellationToken cancellationToken)
    {
        await plan.TargetPort.Store.VerifyRootIdentityAsync(cancellationToken);
        var entries = await plan.TargetPort.Store.ListEntriesAsync(cancellationToken);
        await plan.TargetPort.Store.VerifyRootIdentityAsync(cancellationToken);
        if (entries is null) return null;
        var matches = entries.Where(entry => MatchesPath(entry, plan.Path)).ToArray();
        return matches.Length == 1 && matches[0].Kind == PilotEntryKind.Directory &&
               matches[0].TransferBlockedReason is null && !string.IsNullOrWhiteSpace(matches[0].Id)
            ? matches[0]
            : null;
    }

    private async Task RecordNotAppliedAsync(SyncAuthorityAttempt attempt, SyncAuthorityLease lease,
        CreatePlan plan, SyncAuthorityCloudContentStage? stage, string fingerprint, Exception error,
        CancellationToken cancellationToken)
    {
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownNotApplied, stage is null ? "{}" : SourceJson(stage), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "adapter_declined_before_application",
                adapter = error.GetType().Name
            }), Hash("known-not-applied", fingerprint), cancellationToken);
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
            SyncAuthorityAttemptStatus.VerifiedNotApplied,
            "{\"reason\":\"The typed target adapter conclusively proves it declined the request before applying it.\"}",
            cancellationToken);
    }

    private async Task RecordUnknownAsync(SyncAuthorityAttempt attempt, SyncAuthorityLease lease,
        CreatePlan plan, SyncAuthorityCloudContentStage? stage, string fingerprint, Exception error,
        CancellationToken cancellationToken)
    {
        await authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
            stage is null ? "{}" : SourceJson(stage), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "response_unknown",
                adapter = error.GetType().Name,
                provider = plan.TargetPort.Provider
            }), Hash("effect-unknown", fingerprint), cancellationToken);
    }

    // ----- target preconditions ----------------------------------------------------------------------

    private static async Task RequireTargetAbsentAsync(SyncAuthorityCloudEndpointPort target, string path,
        CancellationToken cancellationToken)
    {
        await target.Store.VerifyRootIdentityAsync(cancellationToken);
        var existing = await target.Store.FindEntryAtPathAsync(path, cancellationToken);
        if (existing is not null)
        {
            if (existing.TransferBlockedReason is not null)
                throw new SyncAuthorityCloudCreateException(
                    "The destination path already carries a cloud-native or unsupported item; a create-only effect is refused rather than overwriting it.");
            throw new SyncAuthorityCloudCreateException(
                "The cloud destination is no longer absent; the retained create plan was not dispatched.");
        }
        await target.Store.VerifyRootIdentityAsync(cancellationToken);
    }

    private static async Task RequireSourceDirectoryAsync(CreatePlan plan, CancellationToken cancellationToken)
    {
        await plan.SourcePort.Store.VerifyRootIdentityAsync(cancellationToken);
        var entries = await plan.SourcePort.Store.ListEntriesAsync(cancellationToken) ??
                      throw new SyncAuthorityCloudCreateException(
                          "The source returned no inventory for its own root; a missing inventory is never the retained folder.");
        await plan.SourcePort.Store.VerifyRootIdentityAsync(cancellationToken);
        var identity = plan.SourceIdentity!;
        var matches = entries.Where(entry => MatchesPath(entry, plan.Path)).ToArray();
        if (matches.Length != 1 || matches[0].Kind != PilotEntryKind.Directory ||
            matches[0].TransferBlockedReason is not null ||
            !string.Equals(matches[0].Id, identity.FileId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudCreateException(
                "The exact retained source folder is no longer present under the typed source root.");
    }

    // ----- plan validation ---------------------------------------------------------------------------

    private async Task<CreatePlan> RequireCreatePlanAsync(SyncAuthorityOperationSnapshot operation,
        CancellationToken cancellationToken)
    {
        if (operation.Kind is not (SyncAuthorityOperationKind.CreateLocal or
            SyncAuthorityOperationKind.CreateRemote))
            throw new SyncAuthorityCloudCreateException(
                "The shared cloud create executor accepts only a create operation; the retained kind is " +
                SyncAuthorityNames.OperationKind(operation.Kind) +
                " and every other kind is refused before any effect.");
        if (!string.Equals(operation.PairId, context.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudCreateException(
                "The retained operation belongs to another pair than the typed cloud endpoint set.");
        if (string.IsNullOrWhiteSpace(operation.ObjectId))
            throw new SyncAuthorityCloudCreateException(
                "The retained create operation has no exact object identity.");
        if (operation.ConfigurationRevision < 1 || string.IsNullOrWhiteSpace(operation.CapabilityProfileId))
            throw new SyncAuthorityCloudCreateException(
                "The retained create operation has no configuration revision or capability profile authority.");
        var targetSide = SyncAuthorityOperationAllocations.TargetSide(operation.Kind);
        var targetPort = context.Port(targetSide);
        var sourcePort = context.Port(SyncAuthorityCloudExecutionContext.OtherSide(targetSide));
        var sources = operation.Members.Where(item => item.Role == "source").ToArray();
        var destinations = operation.Members.Where(item => item.Role == "destination").ToArray();
        if (sources.Length != 1 || destinations.Length != 1)
            throw new SyncAuthorityCloudCreateException(
                "The retained create operation does not carry exactly one source and one destination member.");
        var source = sources[0];
        var destination = destinations[0];
        if (!string.Equals(source.PairId, operation.PairId, StringComparison.Ordinal) ||
            !string.Equals(destination.PairId, operation.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudCreateException(
                "A retained operation member belongs to another pair; the plan is refused.");
        if (!string.Equals(source.RootBindingId, sourcePort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudCreateException(
                "The retained source member does not bind the exact authority root of its typed cloud port.");
        if (!string.Equals(destination.RootBindingId, targetPort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudCreateException(
                "The retained destination member does not bind the exact authority root of the target typed cloud port.");
        if (destination.MembershipId is not null || destination.ObjectVersionId is not null)
            throw new SyncAuthorityCloudCreateException(
                "The create destination is not a fresh absent destination; the plan is refused.");
        if (string.IsNullOrWhiteSpace(source.ObjectVersionId))
            throw new SyncAuthorityCloudCreateException(
                "The retained create source has no exact object version authority.");
        var path = RequireExactRetainedPath(source);
        RequirePreconditions(operation, path);
        var version = await authority.LoadObjectVersionAsync(source.ObjectVersionId, cancellationToken);
        var identity = RequireRetainedVersionIdentity(version, source, sourcePort, "source");
        var isDirectory = string.Equals(ObjectKind(source), "directory", StringComparison.Ordinal);
        RequireAllocationShape(operation, isDirectory);
        return new CreatePlan(operation.PairId!, operation.ObjectId!, path, sourcePort, targetPort, source,
            destination, version, identity, isDirectory);
    }

    private void RequireAllocationShape(SyncAuthorityOperationSnapshot operation, bool isDirectory)
    {
        var namespaceId = SyncControlNamespace.NormalizeNamespaceId(context.ControlNamespace.NamespaceId);
        var allowed = new[]
        {
            SyncAuthorityAllocationKind.TransactionDirectory,
            SyncAuthorityAllocationKind.StageFile,
            SyncAuthorityAllocationKind.UploadSession
        };
        if (operation.Allocations.Any(item => !allowed.Contains(item.Kind)))
            throw new SyncAuthorityCloudCreateException(
                "The retained create plan carries an allocation kind that a cloud create never uses; it is refused before any effect.");
        var transactions = operation.Allocations.Where(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory).ToArray();
        var stages = operation.Allocations.Where(item => item.Kind == SyncAuthorityAllocationKind.StageFile)
            .ToArray();
        var sessions = operation.Allocations.Where(item => item.Kind == SyncAuthorityAllocationKind.UploadSession)
            .ToArray();
        if (isDirectory)
        {
            if (operation.Allocations.Count != 0)
                throw new SyncAuthorityCloudCreateException(
                    "A cloud directory create must not reserve a file stage, transaction or upload session.");
            return;
        }
        if (transactions.Length != 1 || stages.Length != 1)
            throw new SyncAuthorityCloudCreateException(
                "A cloud file create requires exactly one private transaction and one private stage allocation.");
        if (sessions.Length > 1)
            throw new SyncAuthorityCloudCreateException(
                "A cloud file create may reserve at most one upload session.");
        foreach (var allocation in new[] { transactions[0], stages[0] })
            if (!string.Equals(allocation.ControlNamespaceId, namespaceId, StringComparison.Ordinal))
                throw new SyncAuthorityCloudCreateException(
                    "A private staging allocation does not belong to this exact private control namespace authority.");
        if (!string.Equals(transactions[0].IntendedParentIdentity, namespaceId, StringComparison.Ordinal) ||
            !string.Equals(stages[0].IntendedParentIdentity, transactions[0].AllocationId,
                StringComparison.Ordinal) ||
            !string.Equals(stages[0].DeterministicName, SyncLocalTransactionDirectory.StageName,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudCreateException(
                "The private staging allocations are not nested exactly as the shared cloud allocation policy requires.");
        foreach (var session in sessions)
            if (!string.Equals(session.ControlNamespaceId, namespaceId, StringComparison.Ordinal))
                throw new SyncAuthorityCloudCreateException(
                    "The reserved upload session does not belong to this exact private control namespace authority.");
    }

    private void argument(string operationId, string workerId)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
    }

    private sealed record CreatePlan(
        string PairId,
        string ObjectId,
        string Path,
        SyncAuthorityCloudEndpointPort SourcePort,
        SyncAuthorityCloudEndpointPort TargetPort,
        SyncAuthorityMember SourceMember,
        SyncAuthorityMember DestinationMember,
        SyncAuthorityObjectVersion SourceVersion,
        SyncAuthorityCloudObservationIdentity SourceIdentity,
        bool IsDirectory);
}
