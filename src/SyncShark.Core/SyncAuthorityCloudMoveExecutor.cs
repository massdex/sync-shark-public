using System.Globalization;
using System.Text.Json;
using static Massdex.SyncShark.SyncAuthorityCloudExecution;

namespace Massdex.SyncShark;

// CC3E-1..CC3E-3: the one internal generic cloud endpoint-native move engine.
//
// One executor is parameterised by the CC2C typed two-port set and the CC3B private staging context. It
// resolves the actual source and destination ports from the retained operation members alone - never from a
// provider name, a connection id or a display label - and it never opens a local root and never uses a
// transfer stage:
//
//   * The operation's 'source' member is the logical endpoint whose own object already occupies the desired
//     path; its retained version and membership establish the desired stable identity and path.
//   * The operation's 'destination' member is the typed target endpoint, whose own object still occupies the
//     prior path and is the object the target adapter is asked to move.
//   * The target adapter's own identity-preserving, no-overwrite MoveAsync is the only effect. It is always a
//     same-endpoint path move: no object ever crosses a provider, account or corpus boundary, and no copy,
//     delete or re-upload fallback exists anywhere in this engine.
//   * An adapter without that capability answers its own contract's NotSupportedException before any effect;
//     that is the only conclusive non-application evidence the engine accepts, and it is recorded as a durable
//     known-not-applied receipt before any fenced retry. Mere presence or absence of the source, the
//     destination or the prior path is never treated as proof that the move was or was not applied.
//   * The engine never advertises a revision-conditional move: the adapter contract carries no revision
//     condition, no revision is ever passed or demanded, and a successor revision that the adapter's own
//     documented metadata-update identity contract produced is accepted while the stable identity, kind, path,
//     size and native content hash must remain exact.
//   * An unknown response is retained with zero repeated move. A retained known-success receipt plus an exact
//     stable identity, content and path readback may resume the durable commit exactly once. A completed or
//     committed path never reissues.
//   * A directory move relies on the adapter preserving the subtree identities, and the shared runner forces a
//     fresh observation and replan before any still-queued descendant operation may execute; no descendant
//     version is ever rewritten here and parent success never pretends the subtree converged.

/// <summary>One safe refusal of the shared cloud move execution slice. It is never a partial effect.</summary>
public sealed class SyncAuthorityCloudMoveException(string message)
    : SyncAuthorityCloudExecutionException(message);

internal sealed class SyncAuthorityCloudMoveExecutor(
    SyncAuthorityDatabase authority,
    SyncAuthorityCloudExecutionContext context)
{
    /// <summary>
    /// The bounded crash/cancellation seam used by the focused proofs. It is an ordinary in-process hook with
    /// no production default; it performs no provider or journal effect itself.
    /// </summary>
    internal static Action<string>? PhaseForTest { get; set; }

    /// <summary>
    /// Dispatches one cloud endpoint-native move on its exact logical target and reaches the committed
    /// boundary. The exact source identity at the desired path and the exact target predecessor at the prior
    /// path are re-established immediately before the request boundary, the adapter's own MoveAsync performs
    /// the only effect, and the returned status is Committed only after the adapter's durable success receipt
    /// and an independent bound readback agree on the exact stable identity, kind, path, size and native
    /// content.
    /// </summary>
    internal async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        argument(operationId, workerId);
        context.RequireHeld();
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        var plan = await RequireMovePlanAsync(operation, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud move is already complete; the repeated call is a no-op and no request was issued.");
        if (operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased))
            throw new SyncAuthorityCloudMoveException(
                "The cloud move is not at a runnable pre-effect boundary; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        if (operation.Attempts.Any(item => item.Status is SyncAuthorityAttemptStatus.RequestSent or
                SyncAuthorityAttemptStatus.ResponseReceived or SyncAuthorityAttemptStatus.EffectUnknown))
            throw new SyncAuthorityCloudMoveException(
                "The cloud move already carries an unresolved move request; it must be reconciled rather than re-executed.");
        var result = await ExecuteMoveAsync(operation, plan, workerId, cancellationToken);
        return result.Status == SyncAuthorityOperationStatus.Committed
            ? await CompleteAsync(operationId, workerId, cancellationToken)
            : result;
    }

    /// <summary>
    /// Reconciles or finalizes the same cloud move without ever reissuing it. A completed operation is a
    /// no-op, a committed or cleanup-pending operation resumes only its own completion, a move that a retained
    /// known-success receipt plus an exact readback already proves is committed exactly once, and every other
    /// outcome stays unresolved with zero repeated move and with its retained evidence.
    /// </summary>
    internal async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        argument(operationId, workerId);
        context.RequireHeld();
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        var plan = await RequireMovePlanAsync(operation, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud move is already complete; reconciliation is a no-op and no request was issued.");
        if (operation.Status is SyncAuthorityOperationStatus.Committed or
            SyncAuthorityOperationStatus.CleanupPending)
            return await CompleteAsync(operationId, workerId, cancellationToken);
        if (operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
            SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
            SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new SyncAuthorityCloudMoveException(
                "The cloud move has no unresolved target request to reconcile; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        var attempt = operation.Attempts.SingleOrDefault(item => item.Status is
            SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
            SyncAuthorityAttemptStatus.EffectUnknown);
        if (attempt is null)
            throw new SyncAuthorityCloudMoveException(
                "The unresolved cloud move carries no dispatched move attempt of any kind in its retained journal, so its retained state is inconsistent: no move request was issued and nothing was committed.");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        SyncAuthorityExecutionResult result;
        try
        {
            result = await ReconcileMoveAsync(operation, plan, attempt, lease, cancellationToken);
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
        return result.Status == SyncAuthorityOperationStatus.Committed
            ? await CompleteAsync(operationId, workerId, cancellationToken)
            : result;
    }

    private void argument(string operationId, string workerId)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
    }

    // ----- move execution --------------------------------------------------------------------------

    private enum MoveConfirmation
    {
        ExactMoved,
        StillAtPrior,
        OccupiedByOther,
        Invisible,
        InventoryUnavailable
    }

    private async Task<SyncAuthorityExecutionResult> ExecuteMoveAsync(
        SyncAuthorityOperationSnapshot operation, MovePlan plan, string workerId,
        CancellationToken cancellationToken)
    {
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            // The desired source identity at the desired path, the exact target predecessor at the prior path
            // and a free destination are re-established immediately before the request boundary. All three are
            // read-only retained-state checks, so a drifted source, a drifted predecessor or an occupied
            // destination refuses before any request rather than moving an unreviewed object.
            await RequireSourceAtDesiredPathAsync(plan, cancellationToken);
            var predecessor = await RequireTargetPredecessorAsync(plan, cancellationToken);
            await RequireDestinationFreeAsync(plan, cancellationToken);
            var fingerprint = Hash("cloud-move", plan.PairId, plan.SourcePort.RootBindingId,
                plan.TargetPort.RootBindingId, plan.PriorPath, plan.DesiredPath,
                plan.SourceVersion.ObjectVersionId, predecessor.Id!, predecessor.Revision ?? "directory",
                plan.ObjectKind);
            var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease, fingerprint,
                plan.TargetPort.Provider + "-path-move", cancellationToken);
            PhaseForTest?.Invoke("request-sent-before-native-move");

            PilotRemoteEntry moved;
            try
            {
                moved = await plan.TargetPort.Store.MoveAsync(predecessor, plan.DesiredPath, cancellationToken);
            }
            catch (Exception error) when (ConclusiveNonApplication(error))
            {
                await RecordNotAppliedAsync(attempt, lease, plan, predecessor, fingerprint, error,
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Ready, false,
                    "The typed target adapter's own contract conclusively declined the identity-preserving move before applying it; nothing was moved, the retained plan may be replanned and no content was copied or deleted.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await RecordUnknownAsync(attempt, lease, plan, predecessor, fingerprint, error,
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The native move response is unknown and must be reconciled by exact stable identity before any retry; no successor was committed and nothing was copied or deleted.");
            }

            PhaseForTest?.Invoke("after-move-before-receipt");
            if (!MovedIdentityMatches(plan, predecessor, moved))
            {
                await RecordUnknownAsync(attempt, lease, plan, predecessor, fingerprint,
                    new SyncAuthorityCloudMoveException(
                        "The adapter returned a different object identity, kind, path or content after the move."),
                    cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The adapter's returned move identity contradicts the exact retained predecessor; the outcome is unknown and nothing was committed.");
            }
            var movedJson = RemoteJson(moved);
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(predecessor), movedJson,
                JsonSerializer.Serialize(new
                {
                    path = plan.DesiredPath,
                    priorPath = plan.PriorPath,
                    targetProvider = plan.TargetPort.Provider,
                    targetSide = plan.TargetSide,
                    movedFileId = moved.Id,
                    returnedRevision = moved.Revision,
                    movedSize = moved.Size,
                    movedContentHash = moved.ContentHash,
                    priorRevision = predecessor.Revision
                }), Hash("move-known-success", fingerprint, moved.Id!, moved.Revision ?? "directory"),
                cancellationToken);
            PhaseForTest?.Invoke("after-receipt-before-readback");
            var confirmation = await ConfirmMovedAsync(plan, predecessor, cancellationToken);
            if (confirmation.Kind != MoveConfirmation.ExactMoved)
                // A matching destination alone is never sufficient, and a partial or unreadable inventory is
                // never evidence: the outcome stays unresolved with its receipt retained and nothing committed.
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The move success receipt is retained, but " + confirmation.Detail +
                    "; the outcome is unresolved, nothing was copied or deleted and nothing was committed or reissued.");
            PhaseForTest?.Invoke("after-readback-before-commit");
            // CC3E-C1: the committed successor is built from the exact independently verified readback entry,
            // never from the adapter's returned shape alone and never from the pre-move predecessor.
            await CommitMoveAsync(operation, plan, attempt, lease, confirmation.Moved!, cancellationToken);
            PhaseForTest?.Invoke("after-commit-before-completion");
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Committed, true,
                "The one endpoint-native move, its exact stable identity and an independent current listing and root readback were verified.");
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileMoveAsync(
        SyncAuthorityOperationSnapshot operation, MovePlan plan, SyncAuthorityAttempt attempt,
        SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        var predecessor = RetainedPredecessor(plan);
        var confirmation = await ConfirmMovedAsync(plan, predecessor, cancellationToken);
        if (confirmation.Kind == MoveConfirmation.ExactMoved)
        {
            if (confirmation.Moved is null)
                return await UnresolvedAsync(authority, operation, attempt, lease,
                    "The exact moved identity was classified without a validated readback entry, so no successor may be committed: nothing was reissued, nothing was copied or deleted and the retained evidence is preserved.",
                    "cloud move", cancellationToken);
            if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
                // No durable receipt was ever recorded for this request, so the exact readback alone can never
                // commit and no journal transition is invented: the retained status is returned unchanged.
                return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                    "The move outcome cannot be resolved from the current listing alone because no durable receipt was ever received for the request; the retained status is preserved, nothing was reissued and the target was not touched again.");
            try
            {
                // The existing commit is the one durable resume point. It fails closed without a retained
                // known-success receipt, so an exact destination can never commit an unreceipted move.
                // CC3E-C1: the resumed successor uses the ACTUAL verified moved identity, revision and content
                // from the readback, which must agree with the approved move identity, path and content rules;
                // the pre-move predecessor's older revision is never published as the new sync-point version.
                await CommitMoveAsync(operation, plan, attempt, lease, confirmation.Moved, cancellationToken);
            }
            catch (InvalidOperationException error) when (error.Message.Contains("known-success receipt",
                       StringComparison.Ordinal) ||
                       error.Message.Contains("unresolved retained tombstone", StringComparison.Ordinal))
            {
                return await UnresolvedAsync(authority, operation, attempt, lease,
                    "The exact moved identity is visible at its desired path, but its move cannot be resumed without a retained known-success receipt: " +
                    error.Message +
                    " Nothing was reissued, nothing was copied or deleted and the retained evidence is preserved.",
                    "cloud move", cancellationToken);
            }
            return new SyncAuthorityExecutionResult(operation.OperationId, SyncAuthorityOperationStatus.Committed,
                false,
                "The retained move receipt plus the exact stable identity, content and path readback resumed the durable commit without another move request.");
        }
        if (attempt.Status == SyncAuthorityAttemptStatus.RequestSent)
            // Presence or absence of the source, the destination or the prior path is never proof that a sent
            // move was or was not applied. Without a durable receipt no transition is available and none is
            // invented: the exact retained status and attempt state are returned unchanged.
            return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                "The move request was sent and no durable receipt was ever received for it, so its outcome remains unresolved exactly as the retained journal records it: " +
                confirmation.Detail +
                ". Nothing was reissued, nothing was copied or deleted and no non-application was inferred.");
        return await UnresolvedAsync(authority, operation, attempt, lease,
            "The move outcome remains unresolved: " + confirmation.Detail +
            " Nothing was reissued, nothing was copied or deleted, the target was not touched again and no non-application was inferred.",
            "cloud move", cancellationToken);
    }

    // ----- completion ------------------------------------------------------------------------------

    /// <summary>
    /// The one completion boundary of a cloud move. A move reserves no transfer stage, transaction, recovery or
    /// upload session, so completion proves only that no such allocation exists and that no private object was
    /// created; nothing is moved again, copied, deleted or force-removed here.
    /// </summary>
    private async Task<SyncAuthorityExecutionResult> CompleteAsync(string operationId, string workerId,
        CancellationToken cancellationToken)
    {
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud move is already complete; completion is a no-op.");
        if (operation.Status is not (SyncAuthorityOperationStatus.Committed or
            SyncAuthorityOperationStatus.CleanupPending))
            throw new SyncAuthorityCloudMoveException(
                "The cloud move has no committed completion boundary; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        if (operation.Allocations.Count != 0)
            throw new SyncAuthorityCloudMoveException(
                "The retained cloud move unexpectedly carries " + operation.Allocations.Count +
                " allocation(s); a move reserves no transfer stage, transaction, recovery or upload session and creates no private object, so completion is refused and nothing is removed.");
        if (operation.Status == SyncAuthorityOperationStatus.Committed)
        {
            var startLeaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId,
                SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
            var startLease = startLeaseRenewal.Lease;
            try
            {
                await authority.MarkCleanupPendingAsync(startLease,
                    "{\"result\":\"cloud move created no private object; nothing is removed by its completion\"}",
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
                "{\"result\":\"cloud endpoint-native move complete; no private object was created or removed\"}",
                cancellationToken);
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
        return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
            "The committed cloud endpoint-native move completed; it reserved no stage or recovery allocation and no request was reissued.");
    }

    // ----- exact reads -----------------------------------------------------------------------------

    /// <summary>
    /// The one fresh complete bound listing of one typed endpoint root: the port's own root is verified, its own
    /// inventory is read, the root is verified again, and a missing or unreadable inventory is reported as
    /// unavailable rather than as an empty or changed root.
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
                    "the typed endpoint adapter returned no inventory for its own root, and a missing inventory is never a complete listing");
            await port.Store.VerifyRootIdentityAsync(cancellationToken);
            return (true, entries, "");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or
            InvalidOperationException or ArgumentException)
        {
            return (false, Array.Empty<PilotRemoteEntry>(),
                "the typed endpoint root could not be read completely (" + error.GetType().Name +
                "), so no move outcome is inferred from it");
        }
    }

    private static async Task RequireSourceAtDesiredPathAsync(MovePlan plan, CancellationToken cancellationToken)
    {
        var (available, entries, detail) = await ListCompleteAsync(plan.SourcePort, cancellationToken);
        if (!available)
            throw new SyncAuthorityCloudMoveException(
                "The desired move source could not be re-established: " + detail +
                ". No move request was issued.");
        var matches = entries.Where(entry => MatchesPath(entry, plan.DesiredPath)).ToArray();
        if (matches.Length != 1)
            throw new SyncAuthorityCloudMoveException(
                "The moved logical endpoint does not present exactly one item at the desired path; found " +
                matches.Length + " item(s). No move request was issued.");
        var entry = matches[0];
        var exact = entry.Kind == plan.Kind && entry.TransferBlockedReason is null &&
                    string.Equals(entry.Id, plan.SourceFileId, StringComparison.Ordinal) &&
                    (plan.Kind == PilotEntryKind.Directory
                        ? entry.Size == 0 && plan.SourceSize == 0
                        : string.Equals(entry.Revision, plan.SourceRevision, StringComparison.Ordinal) &&
                          entry.Size == plan.SourceSize &&
                          string.Equals(entry.ContentHash, plan.SourceContentHash,
                              StringComparison.OrdinalIgnoreCase));
        if (!exact)
            throw new SyncAuthorityCloudMoveException(
                "The desired move source differs from its immutable retained version, membership, kind or path; no move request was issued.");
    }

    private static async Task<PilotRemoteEntry> RequireTargetPredecessorAsync(MovePlan plan,
        CancellationToken cancellationToken)
    {
        var (available, entries, detail) = await ListCompleteAsync(plan.TargetPort, cancellationToken);
        if (!available)
            throw new SyncAuthorityCloudMoveException(
                "The target predecessor could not be re-established: " + detail + ". No move request was issued.");
        var matches = entries.Where(entry => MatchesPath(entry, plan.PriorPath)).ToArray();
        if (matches.Length != 1)
            throw new SyncAuthorityCloudMoveException(
                "The exact retained target predecessor is not present at the prior path; found " + matches.Length +
                " item(s). No move request was issued.");
        var entry = matches[0];
        var exact = entry.Kind == plan.Kind && entry.TransferBlockedReason is null &&
                    string.Equals(entry.Id, plan.PredecessorFileId, StringComparison.Ordinal) &&
                    (plan.Kind == PilotEntryKind.Directory
                        ? entry.Size == 0 && plan.PredecessorSize == 0
                        : string.Equals(entry.Revision, plan.PredecessorRevision, StringComparison.Ordinal) &&
                          entry.Size == plan.PredecessorSize &&
                          string.Equals(entry.ContentHash, plan.PredecessorContentHash,
                              StringComparison.OrdinalIgnoreCase));
        if (!exact)
            throw new SyncAuthorityCloudMoveException(
                "The current target differs from the immutable retained predecessor identity, kind, revision, size or native hash; no move request was issued.");
        return entry;
    }

    /// <summary>
    /// The one destination-collision check, including case-insensitive ambiguity: no other stable identity may
    /// occupy the desired path on the typed target, and the occupying object is never touched.
    /// </summary>
    private static async Task RequireDestinationFreeAsync(MovePlan plan, CancellationToken cancellationToken)
    {
        var (available, entries, detail) = await ListCompleteAsync(plan.TargetPort, cancellationToken);
        if (!available)
            throw new SyncAuthorityCloudMoveException(
                "The move destination could not be checked: " + detail + ". No move request was issued.");
        var canonical = PilotPath.CanonicalComparisonKey(plan.DesiredPath);
        var collisions = entries.Where(entry =>
            string.Equals(PilotPath.CanonicalComparisonKey(entry.RelativePath), canonical,
                StringComparison.Ordinal) &&
            !string.Equals(entry.Id, plan.PredecessorFileId, StringComparison.Ordinal)).ToArray();
        if (collisions.Length != 0)
            throw new SyncAuthorityCloudMoveException(
                "The move destination is occupied on the typed target, including any case-insensitive variant of the desired path; no move request was issued and the occupying object was not touched.");
    }

    /// <summary>
    /// The one classification of a fresh complete bound listing for a move. An exact moved result requires the
    /// exact stable identity, kind, desired path, size and native content with a free prior path; presence or
    /// absence of anything else is never evidence that the move was or was not applied.
    /// </summary>
    private static async Task<(MoveConfirmation Kind, string Detail, PilotRemoteEntry? Moved)> ConfirmMovedAsync(
        MovePlan plan, PilotRemoteEntry predecessor, CancellationToken cancellationToken)
    {
        var (available, entries, detail) = await ListCompleteAsync(plan.TargetPort, cancellationToken);
        if (!available) return (MoveConfirmation.InventoryUnavailable, detail, null);
        var byIdentity = entries.Where(entry =>
            string.Equals(entry.Id, predecessor.Id, StringComparison.Ordinal)).ToArray();
        var atPrior = entries.Where(entry => MatchesPath(entry, plan.PriorPath)).ToArray();
        var atDesired = entries.Where(entry => MatchesPath(entry, plan.DesiredPath)).ToArray();
        // CC3E-C1: an exact moved result now requires EXACTLY ONE entry at the desired path - not merely one
        // entry by stable identity with a free prior path - so a second, different identity at the same
        // destination, or an adapter-marked ambiguous entry, can never falsely complete. The validated entry is
        // returned so a resumed commit can never rebuild a successor from the pre-move predecessor.
        if (byIdentity.Length == 1 && atDesired.Length == 1 &&
            string.Equals(byIdentity[0].Id, atDesired[0].Id, StringComparison.Ordinal) &&
            string.Equals(byIdentity[0].RelativePath, atDesired[0].RelativePath, StringComparison.Ordinal) &&
            atPrior.Length == 0 && ExactMovedEntry(plan, byIdentity[0]))
            return (MoveConfirmation.ExactMoved,
                "exactly one entry at the desired path carries the exact stable identity, kind, size and native content, the prior path is free and no adapter-marked ambiguous entry is present",
                byIdentity[0]);
        if (byIdentity.Length == 0)
            return atDesired.Length == 0
                ? (MoveConfirmation.Invisible,
                    "the exact stable move identity appears nowhere in the complete target listing", null)
                : (MoveConfirmation.OccupiedByOther,
                    "the desired path is occupied by a different stable identity while the exact moved identity is absent from the complete listing", null);
        if (atPrior.Length == 1 && string.Equals(byIdentity[0].Id, atPrior[0].Id, StringComparison.Ordinal))
            return (MoveConfirmation.StillAtPrior,
                "the exact retained object is still at its prior path, and presence alone is never proof that the move was or was not applied", null);
        return (MoveConfirmation.OccupiedByOther,
            "the exact stable identity is not exactly one entry at the desired path with a free prior path", null);
    }

    private static bool ExactMovedEntry(MovePlan plan, PilotRemoteEntry entry) =>
        entry.Kind == plan.Kind && entry.TransferBlockedReason is null &&
        (plan.Kind == PilotEntryKind.Directory
            ? entry.Size == 0
            : entry.Size == plan.PredecessorSize &&
              string.Equals(entry.ContentHash, plan.PredecessorContentHash, StringComparison.OrdinalIgnoreCase));

    private static bool MovedIdentityMatches(MovePlan plan, PilotRemoteEntry predecessor, PilotRemoteEntry moved) =>
        string.Equals(moved.Id, predecessor.Id, StringComparison.Ordinal) &&
        moved.Kind == predecessor.Kind &&
        string.Equals(moved.RelativePath, plan.DesiredPath, StringComparison.Ordinal) &&
        moved.TransferBlockedReason is null &&
        (moved.Kind == PilotEntryKind.Directory
            ? moved.Size == 0
            : moved.Size == predecessor.Size &&
              string.Equals(moved.ContentHash, predecessor.ContentHash, StringComparison.OrdinalIgnoreCase));

    private static PilotRemoteEntry RetainedPredecessor(MovePlan plan) =>
        new(plan.PriorPath, plan.Kind, plan.PredecessorFileId, plan.PredecessorRevision, plan.PredecessorSize,
            plan.PredecessorContentHash);

    // ----- successor registration and the one ordered commit -----------------------------------------

    /// <summary>
    /// The one shared successor commit of a cloud move, identical in shape to the create and update engines:
    /// the moved object's typed CC2C observation identity is registered as its own logical side's successor
    /// version with a new membership at the desired path, and the ordered synchronization point keeps the
    /// moved endpoint's own slot while the target slot receives the successor. The prior membership and the
    /// prior version rows are retained unchanged as history; no descendant version is rewritten here.
    /// </summary>
    private async Task CommitMoveAsync(SyncAuthorityOperationSnapshot operation, MovePlan plan,
        SyncAuthorityAttempt attempt, SyncAuthorityLease lease, PilotRemoteEntry moved,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(moved.Id))
            throw new SyncAuthorityCloudMoveException(
                "The moved object has no stable provider identity, so no successor may be committed.");
        var identity = plan.TargetPort.ObservationIdentity(moved.Id, moved.Revision);
        var identityJson = identity.Serialize();
        var contentHash = moved.Kind == PilotEntryKind.Directory ? null : moved.ContentHash;
        var versionId = Id("version-" + plan.TargetPort.CompatibilitySide, operation.ObjectId!, identityJson,
            contentHash ?? "none", moved.Size.ToString(CultureInfo.InvariantCulture));
        var membershipId = Id("membership-cloud-effect", operation.OperationId, plan.DesiredPath, versionId);
        // The successor membership generation is loaded when this exact successor membership already exists, so
        // a resumed commit registers the identical membership instead of minting a second generation.
        var generation = await authority.LoadMembershipGenerationAsync(membershipId, cancellationToken) ??
                         await authority.NextMembershipGenerationAsync(plan.DestinationMember.RootBindingId,
                             plan.DesiredPath, cancellationToken);
        await authority.RegisterObjectVersionAsync(versionId, operation.ObjectId!,
            plan.TargetPort.CompatibilitySide, identityJson, contentHash, moved.Size, cancellationToken);
        await authority.RegisterMembershipAsync(membershipId, operation.ObjectId!,
            plan.DestinationMember.RootBindingId, plan.DesiredPath, generation,
            cancellationToken: cancellationToken);
        var sourceIsLeft = string.Equals(plan.SourcePort.CompatibilitySide,
            SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal);
        var localVersionId = sourceIsLeft ? plan.SourceVersion.ObjectVersionId : versionId;
        var dropboxVersionId = sourceIsLeft ? versionId : plan.SourceVersion.ObjectVersionId;
        var localMembershipId = sourceIsLeft ? plan.SourceMember.MembershipId : membershipId;
        var dropboxMembershipId = sourceIsLeft ? membershipId : plan.SourceMember.MembershipId;
        var pointId = Id("syncpoint-cloud-effect", operation.OperationId, plan.SourceVersion.ObjectVersionId,
            versionId);
        await authority.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, pointId, plan.PairId,
            operation.ObjectId!, localVersionId, dropboxVersionId, localMembershipId, dropboxMembershipId,
            JsonSerializer.Serialize(new
            {
                result = "cloud endpoint-native move committed",
                priorPath = plan.PriorPath,
                desiredPath = plan.DesiredPath,
                targetProvider = plan.TargetPort.Provider,
                targetSide = plan.TargetSide,
                movedFileId = moved.Id,
                movedRevision = moved.Revision,
                movedKind = moved.Kind.ToString(),
                movedSize = moved.Size,
                objectKind = plan.ObjectKind
            }), cancellationToken);
    }

    private async Task RecordNotAppliedAsync(SyncAuthorityAttempt attempt, SyncAuthorityLease lease, MovePlan plan,
        PilotRemoteEntry predecessor, string fingerprint, Exception error, CancellationToken cancellationToken)
    {
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownNotApplied, RemoteJson(predecessor), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "adapter_declined_before_application",
                adapter = error.GetType().Name,
                provider = plan.TargetPort.Provider
            }), Hash("move-known-not-applied", fingerprint), cancellationToken);
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
            SyncAuthorityAttemptStatus.VerifiedNotApplied,
            "{\"reason\":\"The typed target adapter's own contract conclusively proves it declined the identity-preserving move before applying it.\"}",
            cancellationToken);
    }

    private async Task RecordUnknownAsync(SyncAuthorityAttempt attempt, SyncAuthorityLease lease, MovePlan plan,
        PilotRemoteEntry predecessor, string fingerprint, Exception error, CancellationToken cancellationToken) =>
        await authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
            RemoteJson(predecessor), "{}",
            JsonSerializer.Serialize(new
            {
                classification = "move_response_unknown",
                adapter = error.GetType().Name,
                provider = plan.TargetPort.Provider
            }), Hash("move-effect-unknown", fingerprint), cancellationToken);

    private static string RemoteJson(PilotRemoteEntry entry) => JsonSerializer.Serialize(new
    {
        entry.RelativePath, entry.Kind, entry.Id, entry.Revision, entry.Size, entry.ContentHash
    });

    // ----- plan validation ----------------------------------------------------------------------------

    private async Task<MovePlan> RequireMovePlanAsync(SyncAuthorityOperationSnapshot operation,
        CancellationToken cancellationToken)
    {
        if (operation.Kind is not (SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote))
            throw new SyncAuthorityCloudMoveException(
                "The shared cloud move executor accepts only a move operation; the retained kind is " +
                SyncAuthorityNames.OperationKind(operation.Kind) +
                " and every other kind is refused before any effect.");
        if (!string.Equals(operation.PairId, context.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudMoveException(
                "The retained operation belongs to another pair than the typed cloud endpoint set.");
        if (string.IsNullOrWhiteSpace(operation.ObjectId))
            throw new SyncAuthorityCloudMoveException("The retained move operation has no exact object identity.");
        if (operation.ConfigurationRevision < 1 || string.IsNullOrWhiteSpace(operation.CapabilityProfileId))
            throw new SyncAuthorityCloudMoveException(
                "The retained move operation has no configuration revision or capability profile authority.");
        var targetSide = SyncAuthorityOperationAllocations.TargetSide(operation.Kind);
        var targetPort = context.Port(targetSide);
        var sourcePort = context.Port(SyncAuthorityCloudExecutionContext.OtherSide(targetSide));
        var sources = operation.Members.Where(item => item.Role == "source").ToArray();
        var destinations = operation.Members.Where(item => item.Role == "destination").ToArray();
        var locks = operation.Members.Where(item => item.Role == "lock").ToArray();
        if (sources.Length != 1 || destinations.Length != 1 || locks.Length != 1)
            throw new SyncAuthorityCloudMoveException(
                "The retained move operation does not carry exactly one source, one destination and one destination lock member.");
        var source = sources[0];
        var destination = destinations[0];
        var destinationLock = locks[0];
        if (!string.Equals(source.PairId, operation.PairId, StringComparison.Ordinal) ||
            !string.Equals(destination.PairId, operation.PairId, StringComparison.Ordinal) ||
            !string.Equals(destinationLock.PairId, operation.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudMoveException(
                "A retained operation member belongs to another pair; the plan is refused.");
        // CC3E-1: the actual ports come from the retained member roots. The moved endpoint is the source
        // member; the endpoint that performs the native move is the destination member's own typed port.
        if (!string.Equals(source.RootBindingId, sourcePort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudMoveException(
                "The retained move source member does not bind the exact authority root of its own typed cloud port; no move was issued.");
        if (!string.Equals(destination.RootBindingId, targetPort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudMoveException(
                "The retained move destination member does not bind the exact authority root of the typed target cloud port that would perform the move; no move was issued.");
        if (string.IsNullOrWhiteSpace(source.ObjectVersionId) || string.IsNullOrWhiteSpace(source.MembershipId))
            throw new SyncAuthorityCloudMoveException(
                "The retained move source has no exact object version and membership authority.");
        if (string.IsNullOrWhiteSpace(destination.ObjectVersionId))
            throw new SyncAuthorityCloudMoveException(
                "The retained move destination has no exact predecessor object version authority.");
        var desiredPath = RequireExactRetainedPath(source);
        var priorPath = RequireExactRetainedPath(destination);
        if (string.Equals(desiredPath, priorPath, StringComparison.Ordinal))
            throw new SyncAuthorityCloudMoveException(
                "The retained move does not change membership; the plan is refused.");
        if (!string.Equals(RequireExactRetainedPath(destinationLock), desiredPath, StringComparison.Ordinal) ||
            !string.Equals(destinationLock.RootBindingId, targetPort.RootBindingId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudMoveException(
                "The retained destination lock does not lock the exact desired path on the typed target root; the plan is refused.");
        RequirePreconditions(operation, desiredPath);
        RequireMovePreconditionPaths(operation.PreconditionsJson, desiredPath, priorPath);
        if (operation.Allocations.Count != 0)
            throw new SyncAuthorityCloudMoveException(
                "A cloud move reserves no transfer stage, transaction, recovery or upload session and has no copy or delete fallback; the retained allocation shape is refused before any effect.");
        var sourceVersion = await authority.LoadObjectVersionAsync(source.ObjectVersionId, cancellationToken);
        var sourceIdentity = RequireRetainedVersionIdentity(sourceVersion, source, sourcePort, "move source");
        var predecessorVersion = await authority.LoadObjectVersionAsync(destination.ObjectVersionId,
            cancellationToken);
        var predecessorIdentity = RequireRetainedVersionIdentity(predecessorVersion, destination, targetPort,
            "move predecessor");
        var objectKind = ObjectKind(destination);
        if (!string.Equals(objectKind, ObjectKind(source), StringComparison.Ordinal))
            throw new SyncAuthorityCloudMoveException(
                "The retained move members do not describe one object kind; the plan is refused.");
        var kind = string.Equals(objectKind, "directory", StringComparison.Ordinal)
            ? PilotEntryKind.Directory
            : PilotEntryKind.File;
        if (kind == PilotEntryKind.File &&
            (string.IsNullOrWhiteSpace(sourceIdentity.Revision) ||
             string.IsNullOrWhiteSpace(predecessorIdentity.Revision) ||
             string.IsNullOrWhiteSpace(sourceVersion.ContentHash) || sourceVersion.Size is null ||
             string.IsNullOrWhiteSpace(predecessorVersion.ContentHash) || predecessorVersion.Size is null))
            throw new SyncAuthorityCloudMoveException(
                "The retained file move has no exact stable identity, revision, size and native content hash on both endpoints; the plan is refused.");
        return new MovePlan(operation.PairId!, targetSide, sourcePort, targetPort, source, destination,
            sourceVersion, predecessorVersion, desiredPath, priorPath, kind, objectKind, sourceIdentity.FileId,
            sourceIdentity.Revision, sourceVersion.Size ?? 0, sourceVersion.ContentHash,
            predecessorIdentity.FileId, predecessorIdentity.Revision, predecessorVersion.Size ?? 0,
            predecessorVersion.ContentHash);
    }

    private static void RequireMovePreconditionPaths(string preconditionsJson, string desiredPath,
        string priorPath)
    {
        try
        {
            using var document = JsonDocument.Parse(preconditionsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("sourcePath", out var source) &&
                source.ValueKind == JsonValueKind.String &&
                string.Equals(source.GetString(), desiredPath, StringComparison.Ordinal) &&
                document.RootElement.TryGetProperty("priorPath", out var prior) &&
                prior.ValueKind == JsonValueKind.String &&
                string.Equals(prior.GetString(), priorPath, StringComparison.Ordinal))
                return;
        }
        catch (JsonException)
        {
            // The retained preconditions are refused below rather than guessed.
        }
        throw new SyncAuthorityCloudMoveException(
            "The retained move preconditions do not name this exact desired and prior path; the plan is refused.");
    }

    private sealed record MovePlan(
        string PairId,
        string TargetSide,
        SyncAuthorityCloudEndpointPort SourcePort,
        SyncAuthorityCloudEndpointPort TargetPort,
        SyncAuthorityMember SourceMember,
        SyncAuthorityMember DestinationMember,
        SyncAuthorityObjectVersion SourceVersion,
        SyncAuthorityObjectVersion PredecessorVersion,
        string DesiredPath,
        string PriorPath,
        PilotEntryKind Kind,
        string ObjectKind,
        string SourceFileId,
        string? SourceRevision,
        long SourceSize,
        string? SourceContentHash,
        string PredecessorFileId,
        string? PredecessorRevision,
        long PredecessorSize,
        string? PredecessorContentHash);



}
