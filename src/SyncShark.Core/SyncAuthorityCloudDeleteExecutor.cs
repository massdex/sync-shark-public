using System.Text.Json;
using static Massdex.SyncShark.SyncAuthorityCloudExecution;

namespace Massdex.SyncShark;

// CC3D-1..CC3D-3: the one internal generic cloud file deletion executor.
//
// One executor is parameterised by the CC2C typed two-port set, the CC3B private staging context and the
// CC4A retained per-side account/recovery authorities, and it selects the retired logical target from the
// retained operation kind alone: DeleteLocal retires the logical left cloud object and DeleteRemote the
// logical right one. The surviving peer that must be retired is the operation's own retained 'source'
// member - never the logical left by assumption - and its root binding must be the exact authority root of
// that logical side's typed cloud port, exactly as the create and update engines already require.
//
// Everything bearing an effect reuses the existing journal: the planner's tombstone observation, grace and
// evidence; the operation lease and fencing token; the allocation rows the planner already produced; the
// dispatched attempt and its durable receipt; the CC3C bound recovery helper with its bound-root readback
// and its typed recovery descriptor; the existing deletion commit with its exact unresolved-tombstone
// requirement; and the operation completion transitions. No second journal, allocation scheme, provider
// branch or deletion engine is introduced.
//
// The effect-bearing slice is deliberately narrow, and a deletion never infers success from absence:
//
//   * A cloud FOLDER deletion is refused before any provider request. Both current cloud adapters declare
//     SupportsConditionalDirectoryDelete=false: neither can remove a folder atomically only when it is
//     empty, and a descendant pre-list is never an atomic emptiness guard. Cloud folder deletion therefore
//     remains explicitly unavailable; this engine does not inherit its file safety, never runs an unguarded
//     subtree deletion and never reports a refusal as complete.
//   * A cloud FILE deletion first retains the exact target predecessor in the deleted side's own bound
//     recovery root - re-established from the retained rows, never from a caller assertion - and refuses
//     every disagreement before the request boundary.
//   * After the request boundary an exception is never guessed as applied or not applied. An unresolved
//     recovery copy means zero deletion. A sent deletion without exact success evidence stays unresolved
//     even when the path is absent, and a same-path object with a different stable identity is never
//     touched, deleted or committed over.
//   * A retained known-success deletion receipt plus a fresh complete bound listing and a verified recovery
//     resume only the durable commit; the deletion request itself is never reissued.
//   * A completed or committed path never reissues anything.
//   * CC3D-R1: a presence observation is never proof of non-application. A sent revision-conditioned request
//     may still be in flight, so only the adapter's own explicit conclusive non-application records that
//     proof; every other unresolved outcome keeps its exact retained status with zero reissue.
//   * CC3D-DEV-1: an explicit no-effect unresolved result is truthful only when an actual unresolved
//     predecessor-recovery attempt is retained. A retained journal with no unresolved attempt of any kind is
//     refused truthfully rather than explained by an invented cause.

/// <summary>One safe refusal of the shared cloud deletion execution slice. It is never a partial effect.</summary>
public sealed class SyncAuthorityCloudDeleteException(string message)
    : SyncAuthorityCloudExecutionException(message);

internal sealed class SyncAuthorityCloudDeleteExecutor(
    SyncAuthorityDatabase authority,
    SyncAuthorityCloudExecutionContext context,
    IReadOnlyList<SyncAuthorityCloudSideAuthority> sideAuthorities)
{
    /// <summary>
    /// The bounded crash/cancellation seam used by the focused proofs. It is an ordinary in-process hook with
    /// no production default; it performs no provider or journal effect itself.
    /// </summary>
    internal static Action<string>? PhaseForTest { get; set; }

    private enum Confirmation
    {
        ExactIdentityAbsent,
        ExactPredecessorPresent,
        ForeignOrChangedPresent,
        InventoryUnavailable
    }

    /// <summary>
    /// Dispatches one cloud deletion on its exact logical target and reaches the committed boundary: the exact
    /// retained predecessor is retained in its own bound recovery root first, the revision-conditioned
    /// deletion is issued against that exact predecessor revision, and the returned status is Committed only
    /// after the adapter's durable success receipt and a fresh complete bound listing agree that the exact
    /// stable identity is gone.
    /// </summary>
    internal async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        argument(operationId, workerId);
        context.RequireHeld();
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            // Repeating a completed cloud deletion is a no-op; it never recopies and never deletes again.
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud deletion is already complete; the repeated call is a no-op and no request was issued.");
        var plan = await RequireDeletePlanAsync(operation, cancellationToken);
        if (plan.IsDirectory) return RefuseDirectory(operation, plan);
        // A validating operation that carries no unresolved deletion attempt is the exact state this engine
        // itself leaves after a verified predecessor recovery, so a restart resumes the deletion from that
        // retained verified recovery and never copies the predecessor twice. Any unresolved target request
        // must be reconciled instead of re-executed.
        if (operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased or
            SyncAuthorityOperationStatus.Validating))
            throw new SyncAuthorityCloudDeleteException(
                "The cloud deletion is not at a runnable pre-effect boundary; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        if (operation.Attempts.Any(item => !IsRecoveryEffect(item) && item.Status is
                SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
                SyncAuthorityAttemptStatus.EffectUnknown))
            throw new SyncAuthorityCloudDeleteException(
                "The cloud deletion already carries an unresolved target deletion request; it must be reconciled rather than re-executed.");
        var result = await ExecuteDeletionAsync(operation, plan, workerId, cancellationToken);
        return result.Status == SyncAuthorityOperationStatus.Committed
            ? await CompleteAsync(operationId, workerId, cancellationToken)
            : result;
    }

    /// <summary>
    /// Reconciles or finalizes the same cloud deletion without ever reissuing the deletion. A completed
    /// operation is a no-op; a committed or cleanup-pending operation resumes only its exact owned completion;
    /// an unresolved deletion is committed only when a retained known-success receipt and a fresh complete
    /// bound listing both prove it, and every other outcome - including an unresolved recovery copy - is left
    /// explicitly unresolved with its recovery retained and zero deletion requests.
    /// </summary>
    internal async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        argument(operationId, workerId);
        context.RequireHeld();
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        var plan = await RequireDeletePlanAsync(operation, cancellationToken);
        if (plan.IsDirectory) return RefuseDirectory(operation, plan);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud deletion is already complete; reconciliation is a no-op and no request was issued.");
        if (operation.Status is SyncAuthorityOperationStatus.Committed or
            SyncAuthorityOperationStatus.CleanupPending)
            return await CompleteAsync(operationId, workerId, cancellationToken);
        if (operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
            SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
            SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new SyncAuthorityCloudDeleteException(
                "The cloud deletion has no unresolved target request to reconcile; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        var attempt = operation.Attempts.SingleOrDefault(item => !IsRecoveryEffect(item) && item.Status is
            SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
            SyncAuthorityAttemptStatus.EffectUnknown);
        if (attempt is null)
        {
            // CC3D-DEV-1: an explicit no-effect unresolved result is truthful ONLY when an actual unresolved
            // predecessor-recovery attempt is retained. Its durable operation status and evidence are then
            // preserved exactly: no status transition is written, nothing is committed and no request is
            // issued, so a recovery restart keeps its exact retained state instead of a fabricated one. A
            // retained journal with no unresolved attempt of any kind is an inconsistent state and is refused
            // truthfully rather than explained away by an invented cause.
            var unresolvedRecovery = operation.Attempts.Any(item => IsRecoveryEffect(item) && item.Status is
                SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
                SyncAuthorityAttemptStatus.EffectUnknown);
            return new SyncAuthorityExecutionResult(operationId, operation.Status, false,
                unresolvedRecovery
                    ? "The unresolved cloud deletion carries no dispatched deletion attempt: only its predecessor recovery copy is unresolved, so no deletion request was issued and nothing was committed. The retained recovery allocation and its evidence are preserved exactly as the journal records them."
                    : "The unresolved cloud deletion carries no unresolved attempt of any kind in its retained journal, so its retained state is inconsistent: no deletion request was issued, nothing was committed and no cause is inferred.");
        }
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        SyncAuthorityExecutionResult result;
        try
        {
            result = await ReconcileDeletionAsync(operation, plan, attempt, lease, cancellationToken);
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
        return result.Status == SyncAuthorityOperationStatus.Committed
            ? await CompleteAsync(operationId, workerId, cancellationToken)
            : result;
    }

    // ----- deletion execution ----------------------------------------------------------------------

    private async Task<SyncAuthorityExecutionResult> ExecuteDeletionAsync(
        SyncAuthorityOperationSnapshot operation, DeletePlan plan, string workerId,
        CancellationToken cancellationToken)
    {
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            // The exact retained predecessor and the deleted side's own retained recovery authority are
            // established first: both are retained-state checks that issue no effect, so a refused
            // predecessor, account, corpus or recovery scope refuses before anything is copied or deleted.
            var predecessor = await RequireExactPredecessorAsync(plan, cancellationToken);
            var recovery = await SyncAuthorityCloudRecovery.RequireAuthorityAsync(authority, plan.TargetPort,
                plan.TargetSideAuthority, plan.RecoveryAllocation!,
                SyncControlNamespace.NormalizeNamespaceId(context.ControlNamespace.NamespaceId),
                cancellationToken);
            var recovered = await SyncAuthorityCloudRecovery.EnsureRetainedAsync(authority, plan.TargetPort,
                recovery, plan.RecoveryAllocation!, predecessor, operation, plan.Path,
                plan.TargetPort.Definition.ContentHashAlgorithm, lease, cancellationToken);
            if (recovered.Refusal is not null) return recovered.Refusal;
            PhaseForTest?.Invoke("after-recovery-verified");
            // The exact predecessor and its root are re-established immediately before the request boundary.
            // A target that moved since the recovery was retained is refused before any request rather than
            // deleted unconditionally.
            var confirmed = await RequireExactPredecessorAsync(plan, cancellationToken);
            if (!string.Equals(confirmed.Id, predecessor.Id, StringComparison.Ordinal) ||
                !string.Equals(confirmed.Revision, predecessor.Revision, StringComparison.Ordinal))
                throw new SyncAuthorityCloudDeleteException(
                    "The target predecessor changed after its predecessor was retained; no conditional deletion was issued and the retained recovery is preserved.");
            var fingerprint = Hash("cloud-delete", plan.PairId, plan.TargetPort.RootBindingId, plan.Path,
                plan.Version.ObjectVersionId, confirmed.Id!, confirmed.Revision!, plan.TombstoneId,
                recovered.Descriptor!.RecoveryRootBindingId, recovered.Descriptor.RecoveryCorpusId);
            var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease, fingerprint,
                plan.TargetPort.Provider + "-conditional-delete", cancellationToken);
            PhaseForTest?.Invoke("request-sent-before-conditional-delete");

            try
            {
                await plan.TargetPort.Store.DeleteAsync(confirmed, cancellationToken);
            }
            catch (Exception error) when (ConclusiveNonApplication(error))
            {
                await RecordNotAppliedAsync(attempt, lease, plan, confirmed, fingerprint, error,
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Ready, false,
                    "The typed target adapter conclusively declined the revision-conditioned deletion before applying it; nothing was deleted, the retained predecessor recovery is preserved and the plan may be replanned.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await RecordUnknownAsync(attempt, lease, plan, confirmed, fingerprint, error,
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The conditional deletion response is unknown and must be reconciled before any retry; the predecessor recovery and its evidence are retained and nothing was committed.");
            }

            PhaseForTest?.Invoke("after-delete-before-receipt");
            // The adapter's own success is the exact evidence of the applied deletion. It is persisted before
            // the independent readback, so a crash between the two resumes the durable commit instead of ever
            // issuing a second deletion.
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(confirmed), "{\"present\":false}",
                JsonSerializer.Serialize(new
                {
                    path = plan.Path,
                    targetProvider = plan.TargetPort.Provider,
                    targetSide = plan.TargetSide,
                    deletedFileId = confirmed.Id,
                    deletedRevision = confirmed.Revision,
                    predecessorNativeHash = confirmed.ContentHash,
                    recoveryRootBindingId = recovered.Descriptor.RecoveryRootBindingId,
                    recoveryCorpusId = recovered.Descriptor.RecoveryCorpusId,
                    recoveryFileId = recovered.Descriptor.RetainedFileId,
                    tombstoneId = plan.TombstoneId
                }), Hash("delete-known-success", fingerprint, confirmed.Id!, confirmed.Revision!),
                cancellationToken);
            PhaseForTest?.Invoke("after-receipt-before-readback");
            var confirmation = await ConfirmAsync(plan, confirmed, cancellationToken);
            if (confirmation.Kind != Confirmation.ExactIdentityAbsent)
                // A partial or unavailable inventory is never absence, and a receipt contradicted by the
                // exact retained identity is never a verified success: the outcome stays unresolved with its
                // receipt and recovery retained and zero further requests.
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The deletion success receipt is retained, but " + confirmation.Detail +
                    "; the outcome is unresolved, the retained predecessor recovery is preserved and nothing was committed or reissued.");
            PhaseForTest?.Invoke("after-readback-before-commit");
            await authority.CommitSuccessfulDeletionAttemptAsync(attempt.AttemptId, lease, plan.TombstoneId,
                plan.PairId, plan.ObjectId!, CommitDetail(plan, confirmed, recovered), cancellationToken);
            PhaseForTest?.Invoke("after-commit-before-completion");
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Committed, true,
                "The one revision-conditioned cloud file deletion, its exact absent target identity and its retained predecessor recovery were verified.");
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileDeletionAsync(
        SyncAuthorityOperationSnapshot operation, DeletePlan plan, SyncAuthorityAttempt attempt,
        SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        var predecessor = RetainedPredecessor(plan);
        var recovery = await SyncAuthorityCloudRecovery.RequireAuthorityAsync(authority, plan.TargetPort,
            plan.TargetSideAuthority, plan.RecoveryAllocation!,
            SyncControlNamespace.NormalizeNamespaceId(context.ControlNamespace.NamespaceId), cancellationToken);
        // The CC3C helper re-reads a verified recovery by its exact retained identity and refuses an
        // unresolved recovery copy: a copy whose response was lost never admits a deletion, and a verified
        // recovery is never copied a second time.
        var recovered = await SyncAuthorityCloudRecovery.EnsureRetainedAsync(authority, plan.TargetPort,
            recovery, plan.RecoveryAllocation!, predecessor, operation, plan.Path,
            plan.TargetPort.Definition.ContentHashAlgorithm, lease, cancellationToken);
        if (recovered.Refusal is not null) return recovered.Refusal;
        var confirmation = await ConfirmAsync(plan, predecessor, cancellationToken);
        if (confirmation.Kind == Confirmation.ExactIdentityAbsent)
        {
            try
            {
                // The existing deletion commit is the one durable resume point. It fails closed without a
                // retained known-success receipt or an unresolved retained tombstone, so mere absence can
                // never commit a deletion.
                await authority.CommitSuccessfulDeletionAttemptAsync(attempt.AttemptId, lease, plan.TombstoneId,
                    plan.PairId, plan.ObjectId!, CommitDetail(plan, predecessor, recovered), cancellationToken);
            }
            catch (InvalidOperationException error) when (error.Message.Contains("known-success receipt",
                       StringComparison.Ordinal) ||
                       error.Message.Contains("unresolved retained tombstone", StringComparison.Ordinal))
            {
                return await UnresolvedAsync(authority, operation, attempt, lease,
                    "The exact stable target identity is absent, but its deletion cannot be resumed from absence alone: " +
                    error.Message +
                    " The retained predecessor recovery and evidence are preserved and no second deletion request was issued.",
                    "cloud delete", cancellationToken);
            }
            return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Committed,
                false,
                "The retained deletion receipt plus the fresh complete bound listing resumed the durable deletion commit without another deletion request.");
        }
        // CC3D-R1: a presence observation is never proof of non-application. A sent revision-conditioned
        // request may still be in flight and may still apply later, so no KnownNotApplied receipt and no
        // VerifiedNotApplied transition is ever synthesised from a listing, and no retry is unlocked. Only the
        // adapter's own explicit conclusive non-application, classified at the request boundary in
        // ExecuteDeletionAsync, records that proof. Whether the target is present, absent, changed or the
        // listing is unavailable, every other outcome stays unresolved with zero reissue.
        if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
            // No durable receipt was ever recorded for this request, so no journal transition is available and
            // none is invented: the exact retained operation status and the exact attempt state are returned
            // unchanged, which also leaves the runner's drain with no progress to misread.
            return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                "The deletion request was sent and no durable receipt was ever received for it, so its outcome remains unresolved exactly as the retained journal records it: " +
                confirmation.Detail +
                ". Nothing was reissued, nothing was committed and the retained predecessor recovery is preserved.");
        return await UnresolvedAsync(authority, operation, attempt, lease,
            "The deletion outcome remains unresolved: " + confirmation.Detail +
            " The retained predecessor recovery and evidence are preserved, the target was not touched and no second deletion request was issued.",
            "cloud delete", cancellationToken);
    }
    /// <summary>
    /// The one completion boundary of a cloud deletion. A cloud file deletion creates no private object: its
    /// private transaction allocation stays exactly as fresh and Authorized as the planner left it, its
    /// recovery allocation stays Verified and its retained recovery artifact is preserved. Nothing is deleted
    /// or force-removed here, and a private transaction object that unexpectedly exists is retained as
    /// evidence rather than adopted or removed.
    /// </summary>
    private async Task<SyncAuthorityExecutionResult> CompleteAsync(string operationId, string workerId,
        CancellationToken cancellationToken)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud deletion is already complete; completion is a no-op.");
        if (operation.Status is not (SyncAuthorityOperationStatus.Committed or
            SyncAuthorityOperationStatus.CleanupPending))
            throw new SyncAuthorityCloudDeleteException(
                "The cloud deletion has no committed completion boundary; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        foreach (var allocation in operation.Allocations.Where(item =>
                     item.Kind == SyncAuthorityAllocationKind.TransactionDirectory))
        {
            var path = Path.Combine(context.ControlRoot.Path, allocation.DeterministicName);
            if (Directory.Exists(path) || File.Exists(path))
                throw new SyncAuthorityCloudDeleteException(
                    "The retained private transaction object of this cloud deletion unexpectedly exists; it is retained exactly as it was found and is never adopted or removed by deletion completion.");
        }
        if (operation.Status == SyncAuthorityOperationStatus.Committed)
        {
            var startLeaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId,
                SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
            var startLease = startLeaseRenewal.Lease;
            try
            {
                await authority.MarkCleanupPendingAsync(startLease,
                    "{\"result\":\"cloud deletion created no private object; nothing is deleted by its completion\"}",
                    cancellationToken);
            }
            finally
            {
                await startLeaseRenewal.ReleaseAsync();
            }
        }
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"cloud file deletion complete; its predecessor remains retained in the target's own recovery root\"}",
                cancellationToken);
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
        return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
            "The committed cloud file deletion completed; no private object existed to clean up and no request was reissued.");
    }

    // ----- target reads and classification -----------------------------------------------------------

    /// <summary>
    /// The one fresh complete bound listing of the deleted side's typed target root: the port's own root is
    /// verified, its own inventory is read, the root is verified again, and a missing or unreadable inventory
    /// is reported as unavailable rather than as an empty root.
    /// </summary>
    private static async Task<(bool Available, IReadOnlyList<PilotRemoteEntry> Entries, string Detail)>
        ListCompleteAsync(SyncAuthorityCloudEndpointPort port, CancellationToken cancellationToken)
    {
        try
        {
            await port.Store.VerifyRootIdentityAsync(cancellationToken);
            var entries = await port.Store.ListEntriesAsync(cancellationToken);
            if (entries is null)
                return (false, Array.Empty<PilotRemoteEntry>(),
                    "the typed target adapter returned no inventory for its own root, and a missing inventory is never a complete listing or proof of absence");
            await port.Store.VerifyRootIdentityAsync(cancellationToken);
            return (true, entries, "");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or
            InvalidOperationException or ArgumentException)
        {
            return (false, Array.Empty<PilotRemoteEntry>(),
                "the typed target root could not be read completely (" + error.GetType().Name +
                "), so no deletion outcome is inferred from it");
        }
    }

    private static async Task<PilotRemoteEntry> RequireExactPredecessorAsync(DeletePlan plan,
        CancellationToken cancellationToken)
    {
        var (available, entries, detail) = await ListCompleteAsync(plan.TargetPort, cancellationToken);
        if (!available)
            throw new SyncAuthorityCloudDeleteException(
                "The exact retained deletion predecessor could not be re-established: " + detail +
                ". No deletion request was issued.");
        var matches = entries.Where(entry => MatchesPath(entry, plan.Path)).ToArray();
        if (matches.Length != 1)
            throw new SyncAuthorityCloudDeleteException(
                "The exact retained deletion predecessor is not present under the typed target root; found " +
                matches.Length + " item(s) at the retained path. No deletion request was issued.");
        var entry = matches[0];
        if (entry.Kind != PilotEntryKind.File || entry.TransferBlockedReason is not null)
            throw new SyncAuthorityCloudDeleteException(
                "The retained path does not carry the exact ordinary file the deletion names; it is refused rather than deleted.");
        if (!string.Equals(entry.Id, plan.FileId, StringComparison.Ordinal) ||
            !string.Equals(entry.Revision, plan.Revision, StringComparison.Ordinal) ||
            entry.Size != plan.Size ||
            !string.Equals(entry.ContentHash, plan.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudDeleteException(
                "The current target differs from the immutable retained predecessor identity, revision, size or native hash; no conditional deletion was issued.");
        return entry;
    }

    /// <summary>
    /// The one classification of a fresh complete bound listing. Absence is only reported when the exact
    /// stable identity is absent from a complete listing AND its retained path is free; a same-path object
    /// with another stable identity is a foreign or changed presence and is never touched.
    /// </summary>
    private static async Task<(Confirmation Kind, string Detail)> ConfirmAsync(DeletePlan plan,
        PilotRemoteEntry predecessor, CancellationToken cancellationToken)
    {
        var (available, entries, detail) = await ListCompleteAsync(plan.TargetPort, cancellationToken);
        if (!available) return (Confirmation.InventoryUnavailable, detail);
        var byIdentity = entries.Where(entry =>
            string.Equals(entry.Id, predecessor.Id, StringComparison.Ordinal)).ToArray();
        if (byIdentity.Length == 0)
        {
            var atPath = entries.Where(entry => MatchesPath(entry, plan.Path)).ToArray();
            return atPath.Length == 0
                ? (Confirmation.ExactIdentityAbsent,
                    "the typed target's complete listing no longer carries the exact stable predecessor identity anywhere and its retained path is free")
                : (Confirmation.ForeignOrChangedPresent,
                    "the exact stable predecessor identity is absent, but a different item now occupies the retained deletion path");
        }
        var exact = byIdentity.Length == 1 && MatchesPath(byIdentity[0], plan.Path) &&
                    byIdentity[0].Kind == PilotEntryKind.File &&
                    string.Equals(byIdentity[0].Revision, predecessor.Revision, StringComparison.Ordinal) &&
                    byIdentity[0].Size == predecessor.Size &&
                    string.Equals(byIdentity[0].ContentHash, predecessor.ContentHash,
                        StringComparison.OrdinalIgnoreCase);
        return exact
            ? (Confirmation.ExactPredecessorPresent,
                "the exact retained predecessor identity is still present, unchanged, at its exact retained path")
            : (Confirmation.ForeignOrChangedPresent,
                "the exact stable predecessor identity is still present but its path, kind, revision, size or native content no longer matches the reviewed predecessor");
    }

    /// <summary>The exact retained predecessor entry reconstructed from the immutable retained rows alone.</summary>
    private static PilotRemoteEntry RetainedPredecessor(DeletePlan plan) =>
        new(plan.Path, PilotEntryKind.File, plan.FileId, plan.Revision, plan.Size, plan.ContentHash);

    // ----- refusals -----------------------------------------------------------------------------------

    /// <summary>
    /// The one capability-based folder refusal. Both current cloud adapters declare no atomic
    /// empty-directory guard, so no folder deletion request is ever issued; the operation keeps its real
    /// retained status so the runner projects a blocked refusal instead of progress or a false completion.
    /// </summary>
    private static SyncAuthorityExecutionResult RefuseDirectory(SyncAuthorityOperationSnapshot operation,
        DeletePlan plan) =>
        new(operation.OperationId, operation.Status, false,
            plan.TargetPort.Store.SupportsConditionalDirectoryDelete
                ? "The typed " + plan.TargetPort.Provider +
                  " cloud adapter declares an atomic empty-directory guard, but this packet retains no cloud directory deletion evidence contract, so no folder deletion was issued and nothing is reported complete. Cloud folder deletion remains explicitly unavailable."
                : "The typed " + plan.TargetPort.Provider +
                  " cloud adapter declares SupportsConditionalDirectoryDelete=false: it cannot remove a folder atomically only when it is empty. The cloud folder deletion was refused before any provider request, so the folder and every descendant remain unchanged and no recovery or deletion effect was attempted, and nothing is reported complete. Cloud folder deletion remains explicitly unavailable until a safe directory capability is added to the adapter contract.");

    private static bool IsRecoveryEffect(SyncAuthorityAttempt attempt) =>
        string.Equals(attempt.EffectKind, SyncAuthorityCloudRecovery.RecoveryEffectKind, StringComparison.Ordinal);

    private static string CommitDetail(DeletePlan plan, PilotRemoteEntry predecessor,
        SyncAuthorityCloudRecoveryOutcome recovered) => JsonSerializer.Serialize(new
    {
        result = "cloud file deletion verified; its predecessor stays retained in the target's own bound recovery root",
        path = plan.Path,
        targetProvider = plan.TargetPort.Provider,
        targetSide = plan.TargetSide,
        deletedFileId = predecessor.Id,
        deletedRevision = predecessor.Revision,
        deletedSize = predecessor.Size,
        recoveryRootBindingId = recovered.Descriptor!.RecoveryRootBindingId,
        recoveryCorpusId = recovered.Descriptor.RecoveryCorpusId,
        recoveryFileId = recovered.Descriptor.RetainedFileId,
        recoveryRevision = recovered.Descriptor.RetainedRevision,
        tombstoneId = plan.TombstoneId
    });

    private async Task RecordNotAppliedAsync(SyncAuthorityAttempt attempt, SyncAuthorityLease lease,
        DeletePlan plan, PilotRemoteEntry predecessor, string fingerprint, Exception error,
        CancellationToken cancellationToken)
    {
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownNotApplied, RemoteJson(predecessor), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "adapter_declined_before_application",
                adapter = error.GetType().Name,
                provider = plan.TargetPort.Provider
            }), Hash("delete-known-not-applied", fingerprint), cancellationToken);
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
            SyncAuthorityAttemptStatus.VerifiedNotApplied,
            "{\"reason\":\"The typed target adapter conclusively proves it declined the revision-conditioned deletion before applying it.\"}",
            cancellationToken);
    }

    private async Task RecordUnknownAsync(SyncAuthorityAttempt attempt, SyncAuthorityLease lease,
        DeletePlan plan, PilotRemoteEntry predecessor, string fingerprint, Exception error,
        CancellationToken cancellationToken) =>
        await authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
            RemoteJson(predecessor), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "delete_response_unknown",
                adapter = error.GetType().Name,
                provider = plan.TargetPort.Provider
            }), Hash("delete-effect-unknown", fingerprint), cancellationToken);

    // ----- plan validation ------------------------------------------------------------------------------

    private async Task<DeletePlan> RequireDeletePlanAsync(SyncAuthorityOperationSnapshot operation,
        CancellationToken cancellationToken)
    {
        if (operation.Kind is not (SyncAuthorityOperationKind.DeleteLocal or
            SyncAuthorityOperationKind.DeleteRemote))
            throw new SyncAuthorityCloudDeleteException(
                "The shared cloud deletion executor accepts only a deletion operation; the retained kind is " +
                SyncAuthorityNames.OperationKind(operation.Kind) +
                " and every other kind is refused before any effect.");
        if (!string.Equals(operation.PairId, context.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudDeleteException(
                "The retained operation belongs to another pair than the typed cloud endpoint set.");
        if (string.IsNullOrWhiteSpace(operation.ObjectId))
            throw new SyncAuthorityCloudDeleteException(
                "The retained deletion operation has no exact object identity.");
        if (operation.ConfigurationRevision < 1 || string.IsNullOrWhiteSpace(operation.CapabilityProfileId))
            throw new SyncAuthorityCloudDeleteException(
                "The retained deletion operation has no configuration revision or capability profile authority.");
        var targetSide = SyncAuthorityOperationAllocations.TargetSide(operation.Kind);
        var targetPort = context.Port(targetSide);
        var peerPort = context.Port(SyncAuthorityCloudExecutionContext.OtherSide(targetSide));
        var sources = operation.Members.Where(item => item.Role == "source").ToArray();
        var destinations = operation.Members.Where(item => item.Role == "destination").ToArray();
        if (sources.Length != 1 || destinations.Length != 1)
            throw new SyncAuthorityCloudDeleteException(
                "The retained deletion operation does not carry exactly one source and one destination member.");
        var source = sources[0];
        var destination = destinations[0];
        if (!string.Equals(source.PairId, operation.PairId, StringComparison.Ordinal) ||
            !string.Equals(destination.PairId, operation.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudDeleteException(
                "A retained operation member belongs to another pair; the plan is refused.");
        // CC3D-1: the operation's own source member names the deleted-side survivor to retire. It is never
        // assumed to be the logical left, and a pair provider, connection or display label is never the
        // target: the member must bind the exact authority root of the logical target's typed port.
        if (!string.Equals(source.RootBindingId, targetPort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudDeleteException(
                "The retained deletion source member does not bind the exact authority root of the typed cloud port whose object it retires; no deletion was issued.");
        if (!string.Equals(destination.RootBindingId, peerPort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudDeleteException(
                "The retained deletion destination member does not bind the exact authority root of this pair's other typed cloud port; no deletion was issued.");
        if (destination.ObjectVersionId is not null || destination.MembershipId is not null)
            throw new SyncAuthorityCloudDeleteException(
                "The retained deletion destination is not the absent peer of this deletion; a present destination is never deleted by this engine.");
        if (string.IsNullOrWhiteSpace(source.ObjectVersionId) || string.IsNullOrWhiteSpace(source.MembershipId))
            throw new SyncAuthorityCloudDeleteException(
                "The retained deletion source has no exact object version and membership authority.");
        var path = RequireExactRetainedPath(source);
        RequirePreconditions(operation, path);
        var tombstoneId = RequireTombstoneId(operation.PreconditionsJson);
        var version = await authority.LoadObjectVersionAsync(source.ObjectVersionId, cancellationToken);
        var identity = RequireRetainedVersionIdentity(version, source, targetPort, "deletion source");
        var isDirectory = string.Equals(ObjectKind(source), "directory", StringComparison.Ordinal);
        if (isDirectory && (version.ContentHash is not null || version.Size is not (null or 0)))
            throw new SyncAuthorityCloudDeleteException(
                "The retained directory deletion authority carries file content; it is refused rather than reinterpreted.");
        if (!isDirectory && (version.ContentHash is null || version.Size is null || version.Size.Value < 0))
            throw new SyncAuthorityCloudDeleteException(
                "The retained file deletion authority has no exact native content hash and size; it is refused rather than treated as a directory.");
        if (!isDirectory)
        {
            if (string.IsNullOrWhiteSpace(identity.Revision))
                throw new SyncAuthorityCloudDeleteException(
                    "The retained target predecessor has no exact provider revision; a revision-conditioned cloud deletion is never issued without it.");
            if (!string.Equals(identity.ContentAlgorithm, targetPort.Definition.ContentHashAlgorithm,
                    StringComparison.Ordinal))
                throw new SyncAuthorityCloudDeleteException(
                    "The retained target predecessor identity was not produced with its port provider's registered content algorithm.");
        }
        var matching = sideAuthorities.Where(item =>
            string.Equals(item.CompatibilitySide, targetSide, StringComparison.Ordinal)).ToArray();
        if (matching.Length > 1)
            throw new SyncAuthorityCloudDeleteException(
                "More than one retained side recovery authority names the deleted logical side; the plan is refused.");
        var namespaceId = SyncControlNamespace.NormalizeNamespaceId(context.ControlNamespace.NamespaceId);
        if (isDirectory)
        {
            if (operation.Allocations.Count != 0)
                throw new SyncAuthorityCloudDeleteException(
                    "A cloud directory deletion must not reserve a private transaction, stage, recovery or upload session; the retained allocation shape is refused.");
            return new DeletePlan(operation.PairId!, operation.ObjectId!, path, targetSide, targetPort, version,
                identity, identity.FileId, "", 0, "", tombstoneId, true, null,
                matching.Length == 1 ? matching[0] : null);
        }
        var allowed = new[]
        {
            SyncAuthorityAllocationKind.TransactionDirectory,
            SyncAuthorityAllocationKind.RemoteRecoveryFile
        };
        if (operation.Allocations.Any(item => !allowed.Contains(item.Kind)))
            throw new SyncAuthorityCloudDeleteException(
                "The retained deletion plan carries an allocation kind that a cloud file deletion never uses; it is refused before any effect.");
        var transactions = operation.Allocations.Where(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory).ToArray();
        var recoveries = operation.Allocations.Where(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).ToArray();
        if (transactions.Length != 1 || recoveries.Length != 1)
            throw new SyncAuthorityCloudDeleteException(
                "A cloud file deletion requires exactly one private transaction and exactly one retained remote recovery allocation; found " +
                transactions.Length + " and " + recoveries.Length + ".");
        foreach (var allocation in new[] { transactions[0], recoveries[0] })
            if (!string.Equals(allocation.ControlNamespaceId, namespaceId, StringComparison.Ordinal))
                throw new SyncAuthorityCloudDeleteException(
                    "A retained deletion allocation does not belong to this exact private control namespace authority.");
        if (!string.Equals(transactions[0].IntendedParentIdentity, namespaceId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudDeleteException(
                "The retained private transaction is not parented by this exact private control namespace authority.");
        if (matching.Length == 1 &&
            !string.Equals(recoveries[0].IntendedParentIdentity, matching[0].RecoveryRootBindingId,
                StringComparison.Ordinal))
            throw new SyncAuthorityCloudDeleteException(
                "The retained recovery allocation belongs to another recovery authority than the deleted side's own retained recovery root; the plan is refused.");
        return new DeletePlan(operation.PairId!, operation.ObjectId!, path, targetSide, targetPort, version,
            identity, identity.FileId, identity.Revision!, version.Size!.Value, version.ContentHash!, tombstoneId,
            false, recoveries[0], matching.Length == 1 ? matching[0] : null);
    }

    private static string RequireTombstoneId(string preconditionsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(preconditionsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("tombstoneId", out var value) &&
                value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!;
        }
        catch (JsonException)
        {
            // The retained preconditions are refused below rather than guessed.
        }
        throw new SyncAuthorityCloudDeleteException(
            "The retained deletion operation carries no retained tombstone authority; the deletion is refused before any effect.");
    }

    private void argument(string operationId, string workerId)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sideAuthorities);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
    }

    private static string RemoteJson(PilotRemoteEntry entry) => JsonSerializer.Serialize(new
    {
        entry.RelativePath, entry.Id, entry.Revision, entry.Size, entry.ContentHash
    });

    private sealed record DeletePlan(
        string PairId,
        string ObjectId,
        string Path,
        string TargetSide,
        SyncAuthorityCloudEndpointPort TargetPort,
        SyncAuthorityObjectVersion Version,
        SyncAuthorityCloudObservationIdentity Identity,
        string FileId,
        string Revision,
        long Size,
        string ContentHash,
        string TombstoneId,
        bool IsDirectory,
        SyncAuthorityAllocation? RecoveryAllocation,
        SyncAuthorityCloudSideAuthority? TargetSideAuthority);
}
