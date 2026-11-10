using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public enum SyncAuthorityCrossPairMoveDirection
{
    LocalToDropbox,
    DropboxToLocal
}

public sealed record SyncAuthorityCrossPairMovePlan(
    string OperationGroupId,
    string OperationId,
    string SourcePairId,
    string DestinationPairId,
    string SourcePath,
    string DestinationPath,
    SyncAuthorityCrossPairMoveDirection Direction,
    PilotEntryKind Kind);

/// <summary>
/// Correlates a stable NTFS or provider identity across two pair roots before either pair's
/// ordinary planner can interpret the move as an unrelated create/delete. One immutable
/// operation owns both roots and is reconciled by identity after interruption.
/// </summary>
public sealed class SyncAuthorityCrossPairMoveCoordinator(
    SyncPair sourcePair,
    ISyncRemoteStore sourceRemoteStore,
    SyncAuthorityPlannerOptions sourceOptions,
    SyncPair destinationPair,
    ISyncRemoteStore destinationRemoteStore,
    SyncAuthorityPlannerOptions destinationOptions,
    SyncAuthorityDatabase authority,
    SyncAuthorityCommandAdmission admission)
{
    internal bool WorkWindowFull { get; private set; }
    internal static Action<string>? PhaseForTest { get; set; }
    internal static Func<string, Task>? PlanningPhaseForTest { get; set; }
    internal static Func<string, Task>? EffectAdmissionPhaseForTest { get; set; }
    internal static TimeSpan? LeaseDurationForTest { get; set; }
    internal static bool ForceCrossVolumeForTest { get; set; }

    public async Task<IReadOnlyList<SyncAuthorityCrossPairMovePlan>> PlanAsync(
        CancellationToken cancellationToken = default)
    {
        if (sourcePair.Id == destinationPair.Id)
            throw new InvalidOperationException("Cross-pair move planning requires two different pairs.");
        if (!sourceOptions.EnableMoveEffects || !destinationOptions.EnableMoveEffects)
            throw new InvalidOperationException("Both retained pair profiles must enable identity-preserving moves.");
        if (!string.Equals(sourcePair.ConnectionId, destinationPair.ConnectionId, StringComparison.Ordinal))
            return [];
        // Different content-identity algorithms can never be compared, so a cross-provider cross-pair
        // move is refused before any local or remote effect is prepared.
        SyncContentIdentity.RequireSameAlgorithm(sourcePair.Provider, destinationPair.Provider,
            "cross-pair move");

        var sourceLocalRoot = await authority.LoadActiveRootBindingIdAsync(sourcePair.Id, "local", cancellationToken);
        var sourceDropboxRoot = await authority.LoadActiveRootBindingIdAsync(sourcePair.Id, "dropbox", cancellationToken);
        var destinationLocalRoot = await authority.LoadActiveRootBindingIdAsync(destinationPair.Id, "local",
            cancellationToken);
        var destinationDropboxRoot = await authority.LoadActiveRootBindingIdAsync(destinationPair.Id, "dropbox",
            cancellationToken);
        var localScanId = await authority.LoadLatestCompleteScanIdAsync(sourcePair.Id, "local", cancellationToken);
        var dropboxScanId = await authority.LoadLatestCompleteScanIdAsync(sourcePair.Id, "dropbox", cancellationToken);
        var destinationLocalScanId = await authority.LoadLatestCompleteScanIdAsync(destinationPair.Id, "local",
            cancellationToken);
        var destinationDropboxScanId = await authority.LoadLatestCompleteScanIdAsync(destinationPair.Id, "dropbox",
            cancellationToken);
        var sourceObservationSet = await authority.LoadPlanningObservationSetAsync(sourcePair.Id,
            sourceOptions.ConfigurationRevision, sourceLocalRoot, sourceDropboxRoot, localScanId, dropboxScanId,
            cancellationToken);
        var destinationObservationSet = await authority.LoadPlanningObservationSetAsync(destinationPair.Id,
            destinationOptions.ConfigurationRevision, destinationLocalRoot, destinationDropboxRoot,
            destinationLocalScanId, destinationDropboxScanId, cancellationToken);
        var observationSetId = Id("observation-set-cross-pair", sourcePair.ConnectionId,
            sourceObservationSet.LocalScanGenerationId, sourceObservationSet.DropboxScanGenerationId,
            destinationObservationSet.LocalScanGenerationId, destinationObservationSet.DropboxScanGenerationId);
        return await PlanAsync(sourceObservationSet, destinationObservationSet, observationSetId,
            [sourceObservationSet, destinationObservationSet], cancellationToken);
    }

    public async Task<IReadOnlyList<SyncAuthorityCrossPairMovePlan>> PlanAsync(
        SyncAuthorityPairObservationSet sourceObservationSet,
        SyncAuthorityPairObservationSet destinationObservationSet,
        string observationSetId,
        IReadOnlyList<SyncAuthorityPairObservationSet>? participantObservationSets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceObservationSet);
        ArgumentNullException.ThrowIfNull(destinationObservationSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(observationSetId);
        if (sourcePair.Id == destinationPair.Id)
            throw new InvalidOperationException("Cross-pair move planning requires two different pairs.");
        if (!sourceOptions.EnableMoveEffects || !destinationOptions.EnableMoveEffects)
            throw new InvalidOperationException("Both retained pair profiles must enable identity-preserving moves.");
        if (!string.Equals(sourcePair.ConnectionId, destinationPair.ConnectionId, StringComparison.Ordinal))
            return [];
        // Different content-identity algorithms can never be compared, so a cross-provider cross-pair
        // move is refused before any local or remote effect is prepared.
        SyncContentIdentity.RequireSameAlgorithm(sourcePair.Provider, destinationPair.Provider,
            "cross-pair move");
        if (!string.Equals(sourceObservationSet.PairId, sourcePair.Id, StringComparison.Ordinal) ||
            !string.Equals(destinationObservationSet.PairId, destinationPair.Id, StringComparison.Ordinal) ||
            sourceObservationSet.ConfigurationRevision != sourceOptions.ConfigurationRevision ||
            destinationObservationSet.ConfigurationRevision != destinationOptions.ConfigurationRevision)
            throw new InvalidOperationException("The cross-pair observation set is bound to different pair authority.");
        var participants = participantObservationSets ?? [sourceObservationSet, destinationObservationSet];
        if (participants.Select(item => item.PairId).Distinct(StringComparer.Ordinal).Count() != participants.Count ||
            participants.All(item => !string.Equals(item.PairId, sourcePair.Id, StringComparison.Ordinal)) ||
            participants.All(item => !string.Equals(item.PairId, destinationPair.Id, StringComparison.Ordinal)) ||
            participants.Any(item => item.ConnectionId is not null &&
                                     !string.Equals(item.ConnectionId, sourcePair.ConnectionId,
                                         StringComparison.Ordinal)))
            throw new InvalidOperationException("The coordinated cross-pair participant set is incomplete or mixed.");
        foreach (var participant in participants)
            if (!await authority.IsPlanningAuthorityCurrentAsync(participant.Bind(observationSetId),
                    cancellationToken))
                throw new InvalidOperationException(
                    "The cross-pair observation set changed before correlation began.");

        var sourceLocal = sourceObservationSet.Local;
        var sourceDropbox = sourceObservationSet.Dropbox;
        var destinationLocal = destinationObservationSet.Local;
        var destinationDropbox = destinationObservationSet.Dropbox;
        var baselines = sourceObservationSet.SynchronizationPoints;
        var sourceLocalRoot = sourceObservationSet.LocalRootBindingId;
        var sourceDropboxRoot = sourceObservationSet.DropboxRootBindingId;
        var destinationLocalRoot = destinationObservationSet.LocalRootBindingId;
        var destinationDropboxRoot = destinationObservationSet.DropboxRootBindingId;
        var localScanId = sourceObservationSet.LocalScanGenerationId;
        var dropboxScanId = sourceObservationSet.DropboxScanGenerationId;
        var destinationLocalScanId = destinationObservationSet.LocalScanGenerationId;
        var destinationDropboxScanId = destinationObservationSet.DropboxScanGenerationId;

        var destinationLocalIdentities = await LoadIdentitiesAsync(destinationLocal, "local", cancellationToken);
        var destinationDropboxIdentities = await LoadIdentitiesAsync(destinationDropbox, "dropbox", cancellationToken);
        var candidates = new List<Candidate>();
        foreach (var baseline in baselines)
        {
            if (baseline.LocalObjectVersionId is null || baseline.DropboxObjectVersionId is null ||
                baseline.LocalMembershipId is null || baseline.DropboxMembershipId is null ||
                baseline.LocalRelativePath is null || baseline.DropboxRelativePath is null)
                continue;
            var baselineLocal = await authority.LoadObjectVersionAsync(baseline.LocalObjectVersionId,
                cancellationToken);
            var baselineDropbox = await authority.LoadObjectVersionAsync(baseline.DropboxObjectVersionId,
                cancellationToken);
            var kind = baselineLocal.ContentHash is null ? PilotEntryKind.Directory : PilotEntryKind.File;
            var currentSourceLocal = sourceLocal.Where(item => item.ObjectId == baseline.ObjectId).ToArray();
            var currentSourceDropbox = sourceDropbox.Where(item => item.ObjectId == baseline.ObjectId).ToArray();

            if (currentSourceLocal.Length == 0 && currentSourceDropbox.Length == 1 &&
                SyncModePolicy.Allows(sourceOptions.Direction, SyncAuthorityOperationKind.MoveRemote) &&
                SyncModePolicy.Allows(destinationOptions.Direction, SyncAuthorityOperationKind.MoveRemote) &&
                MatchesBaseline(currentSourceDropbox[0], baselineDropbox, baseline.DropboxRelativePath))
            {
                var identity = StableLocalIdentity(baselineLocal);
                var moved = destinationLocalIdentities.Where(item => item.Identity == identity).ToArray();
                if (moved.Length == 1 && moved[0].Entry.ObjectKind == ObjectKind(kind) &&
                    SyncSelectionPolicy.IsIncluded(moved[0].Entry.RelativePath, destinationPair,
                        moved[0].Entry.ObjectKind == "directory"))
                    candidates.Add(new Candidate(baseline, moved[0].Entry, baselineLocal, baselineDropbox,
                        SyncAuthorityCrossPairMoveDirection.LocalToDropbox, kind,
                        destinationDropbox.SingleOrDefault(item =>
                            SamePath(item.RelativePath, moved[0].Entry.RelativePath))));
            }

            if (currentSourceDropbox.Length == 0 && currentSourceLocal.Length == 1 &&
                SyncModePolicy.Allows(sourceOptions.Direction, SyncAuthorityOperationKind.MoveLocal) &&
                SyncModePolicy.Allows(destinationOptions.Direction, SyncAuthorityOperationKind.MoveLocal) &&
                MatchesBaseline(currentSourceLocal[0], baselineLocal, baseline.LocalRelativePath))
            {
                var identity = StableDropboxIdentity(baselineDropbox);
                var moved = destinationDropboxIdentities.Where(item => item.Identity == identity).ToArray();
                if (moved.Length == 1 && moved[0].Entry.ObjectKind == ObjectKind(kind) &&
                    SyncSelectionPolicy.IsIncluded(moved[0].Entry.RelativePath, destinationPair,
                        moved[0].Entry.ObjectKind == "directory"))
                    candidates.Add(new Candidate(baseline, moved[0].Entry, baselineLocal, baselineDropbox,
                        SyncAuthorityCrossPairMoveDirection.DropboxToLocal, kind,
                        destinationLocal.SingleOrDefault(item =>
                            SamePath(item.RelativePath, moved[0].Entry.RelativePath))));
            }
        }

        // A directory move carries its descendants; child operations would become stale when
        // the parent succeeds.
        var selected = new List<Candidate>();
        foreach (var candidate in candidates.OrderBy(item => Depth(item.SourcePath))
                     .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            if (selected.Any(parent => parent.Kind == PilotEntryKind.Directory &&
                                       parent.Direction == candidate.Direction &&
                                       IsDescendant(candidate.SourcePath, parent.SourcePath) &&
                                       IsDescendant(candidate.DestinationPath, parent.DestinationPath)))
                continue;
            selected.Add(candidate);
        }

        var plans = new List<SyncAuthorityCrossPairMovePlan>();
        if (selected.Count > 0 && PlanningPhaseForTest is not null)
            await PlanningPhaseForTest("cross-pair-correlated-before-reservation");
        WorkWindowFull=false;
        var availableSlots=await authority.AvailableWorkWindowAsync(cancellationToken);
        try
        {
        foreach (var candidate in selected)
        {
            if(plans.Count>=availableSlots){WorkWindowFull=true;break;}
            var groupId = Id("operation-group-cross-pair", sourcePair.Id, destinationPair.Id,
                candidate.Direction.ToString(), candidate.Baseline.ObjectId, candidate.SourcePath,
                candidate.DestinationPath, localScanId, dropboxScanId, destinationLocalScanId,
                destinationDropboxScanId);
            var idempotencyKey = Id("idempotency", Id("operation-cross-pair", groupId));
            var registration = await authority.ReserveActionAsync(idempotencyKey, admission, cancellationToken);
            var operationId = registration.OperationId;
            var detail = JsonSerializer.Serialize(new
            {
                observationSetId,
                sourcePairId = sourcePair.Id,
                destinationPairId = destinationPair.Id,
                sourcePath = candidate.SourcePath,
                destinationPath = candidate.DestinationPath,
                direction = candidate.Direction.ToString(),
                kind = candidate.Kind.ToString(),
                sourceConnectionId = sourcePair.ConnectionId,
                destinationConnectionId = destinationPair.ConnectionId
            });
            await authority.RegisterOperationGroupAsync(groupId, "cross_pair_move", detail, cancellationToken);

            var members = new List<SyncAuthorityMember>();
            var memberIds = new HashSet<string>(StringComparer.Ordinal);
            void AddMember(SyncAuthorityMember member)
            {
                if (memberIds.Add(member.MemberId)) members.Add(member);
            }
            AddMember(BaselineMember(operationId, "source", sourcePair.Id, candidate.Baseline.ObjectId,
                sourceLocalRoot, candidate.Baseline.LocalMembershipId!, candidate.Baseline.LocalObjectVersionId!,
                candidate.Baseline.LocalRelativePath!));
            AddMember(BaselineMember(operationId, "source", sourcePair.Id, candidate.Baseline.ObjectId,
                sourceDropboxRoot, candidate.Baseline.DropboxMembershipId!,
                candidate.Baseline.DropboxObjectVersionId!, candidate.Baseline.DropboxRelativePath!));
            AddMember(BaselineMember(operationId, "lock", sourcePair.Id, candidate.Baseline.ObjectId,
                sourceLocalRoot, candidate.Baseline.LocalMembershipId!, candidate.Baseline.LocalObjectVersionId!,
                candidate.Baseline.LocalRelativePath!));
            AddMember(BaselineMember(operationId, "lock", sourcePair.Id, candidate.Baseline.ObjectId,
                sourceDropboxRoot, candidate.Baseline.DropboxMembershipId!,
                candidate.Baseline.DropboxObjectVersionId!, candidate.Baseline.DropboxRelativePath!));
            AddMember(ObservedMember(operationId, "destination", destinationPair.Id,
                candidate.Destination, candidate.Direction == SyncAuthorityCrossPairMoveDirection.LocalToDropbox
                    ? destinationLocalRoot : destinationDropboxRoot));
            if (candidate.DestinationOccupant is not null)
            {
                AddMember(ObservedMember(operationId, "lock", destinationPair.Id,
                    candidate.DestinationOccupant,
                    candidate.Direction == SyncAuthorityCrossPairMoveDirection.LocalToDropbox
                        ? destinationDropboxRoot : destinationLocalRoot));
            }
            else
            {
                AddMember(AbsentMember(operationId, destinationPair.Id, candidate.Destination.ObjectId, "affected",
                    candidate.Direction == SyncAuthorityCrossPairMoveDirection.LocalToDropbox
                        ? destinationDropboxRoot : destinationLocalRoot,
                    candidate.DestinationPath));
            }
            if (candidate.Kind == PilotEntryKind.Directory)
                AddDirectorySubtreeMembers(operationId, candidate, baselines, sourceLocal, sourceDropbox,
                    destinationLocal, destinationDropbox, sourceLocalRoot, sourceDropboxRoot,
                    destinationLocalRoot, destinationDropboxRoot, members, memberIds);
            IReadOnlyList<SyncAuthorityAllocation> allocations =
                !SameLocalVolume() &&
                candidate.Direction == SyncAuthorityCrossPairMoveDirection.DropboxToLocal
                    ?
                    [
                        new SyncAuthorityAllocation(Id("allocation", operationId, "source-recovery"),
                            sourceOptions.LocalControlNamespaceId, SyncAuthorityAllocationKind.LocalRecoveryFile,
                            "cross-pair-" + Hash(operationId, "source-recovery")[..24] + ".recovery",
                            sourceOptions.LocalControlNamespaceId,
                            Hash("owner-nonce", operationId, "source-recovery"))
                    ]
                    : [];
            var preconditions = JsonSerializer.Serialize(new
            {
                observationSetId,
                kind = "identity_preserving_cross_pair_move",
                operationGroupId = groupId,
                sourcePairId = sourcePair.Id,
                destinationPairId = destinationPair.Id,
                sourcePath = candidate.SourcePath,
                destinationPath = candidate.DestinationPath,
                direction = candidate.Direction.ToString(),
                entryKind = candidate.Kind.ToString(),
                sourceLocalScanId = localScanId,
                sourceDropboxScanId = dropboxScanId,
                destinationLocalScanId,
                destinationDropboxScanId,
                sourceConfigurationRevision = sourceOptions.ConfigurationRevision,
                destinationConfigurationRevision = destinationOptions.ConfigurationRevision,
                sourcePhysicalIdentity = StableLocalIdentity(candidate.BaselineLocal),
                dropboxPhysicalIdentity = StableDropboxIdentity(candidate.BaselineDropbox)
            });
            var planningAuthorities = participants.Select(item =>
                    string.Equals(item.PairId, sourcePair.Id, StringComparison.Ordinal)
                        ? item.Bind(observationSetId, candidate.Baseline.ObjectId)
                        : item.Bind(observationSetId))
                .OrderBy(item => item.PairId, StringComparer.Ordinal)
                .ToArray();
            IReadOnlyList<SyncAuthoritySubtreeReservation> subtreeReservations =
                candidate.Kind == PilotEntryKind.Directory
                    ?
                    [
                        new(sourcePair.Id, sourceLocalRoot, candidate.SourcePath),
                        new(sourcePair.Id, sourceDropboxRoot, candidate.SourcePath),
                        new(destinationPair.Id, destinationLocalRoot, candidate.DestinationPath),
                        new(destinationPair.Id, destinationDropboxRoot, candidate.DestinationPath)
                    ]
                    : [];
            await authority.CreateOperationAsync(new SyncAuthorityOperationPlan(operationId,
                idempotencyKey, SyncAuthorityOperationKind.CrossPairMove, sourcePair.Id,
                candidate.Baseline.ObjectId, sourceOptions.ConfigurationRevision,
                sourceOptions.CapabilityProfileId, preconditions, 98, members, [], allocations, groupId,
                planningAuthorities, subtreeReservations),
                admission, cancellationToken, reservation: registration.Reservation);
            plans.Add(new SyncAuthorityCrossPairMovePlan(groupId, operationId, sourcePair.Id,
                destinationPair.Id, candidate.SourcePath, candidate.DestinationPath, candidate.Direction,
                candidate.Kind));
        }
        }
        catch(SyncAuthorityWorkWindowFullException)
        {
            // Another writer used the remaining capacity. Preserve the exact admitted
            // prefix and its reservations; the caller drains it before any continuation.
            WorkWindowFull=true;
        }
        return plans;
    }

    public async Task<SyncAuthorityExecutionResult> ExecuteAsync(SyncAuthorityCrossPairMovePlan plan,
        string workerId, CancellationToken cancellationToken = default)
    {
        if (plan.SourcePairId != sourcePair.Id || plan.DestinationPairId != destinationPair.Id)
            throw new InvalidOperationException("The cross-pair plan is bound to different pair identities.");
        var operation = await authority.LoadOperationAsync(plan.OperationId, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                "The identity-preserving cross-pair move is already complete.");
        if (operation.Status == SyncAuthorityOperationStatus.Blocked)
            return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                "The retained cross-pair move is blocked and requires review.");
        await authority.MarkOperationGroupActiveAsync(plan.OperationGroupId, cancellationToken);
        if (operation.Status is SyncAuthorityOperationStatus.EffectIssuing or
            SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
            SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying)
            return await ReconcileAsync(plan, operation, workerId, cancellationToken);
        if (operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased or
            SyncAuthorityOperationStatus.Validating))
            throw new InvalidOperationException("The cross-pair operation is not runnable.");

        var baseline = await LoadBaselineAsync(plan, operation, cancellationToken);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            LeaseDurationForTest ?? SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            var fingerprint = Hash("cross-pair-move", operation.OperationId, plan.Direction.ToString(),
                plan.SourcePath, plan.DestinationPath, baseline.Local.ObjectVersionId,
                baseline.Dropbox.ObjectVersionId);
            SyncAuthorityAttempt attempt;
            if (plan.Direction == SyncAuthorityCrossPairMoveDirection.LocalToDropbox)
            {
                PilotRemoteEntry predecessor;
                try
                {
                    predecessor = await ValidateLocalOriginAsync(plan, operation, baseline, cancellationToken);
                }
                catch (IOException exception)
                {
                    return await BlockBeforeEffectAsync(operation, lease, fingerprint, exception.Message,
                        cancellationToken);
                }
                if (sourceRemoteStore is not ISyncCrossRootRemoteMove mover)
                    return await BlockBeforeEffectAsync(operation, lease, fingerprint,
                        "The connected provider does not support identity-preserving moves between pair roots.",
                        cancellationToken);
                attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease,
                    fingerprint, "dropbox-cross-root-native-move", cancellationToken);
                dispatched = true;
                if (EffectAdmissionPhaseForTest is not null)
                    await EffectAdmissionPhaseForTest("cross-pair-effect-admitted-before-physical-move");
                PilotRemoteEntry movedRoot;
                try
                {
                    movedRoot = await mover.MoveToAsync(predecessor, destinationRemoteStore,
                        plan.DestinationPath, cancellationToken);
                    RequireRemoteSuccessor(predecessor, movedRoot, plan.DestinationPath);
                }
                catch (DropboxApiException exception)
                {
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownNotApplied, JsonSerializer.Serialize(predecessor), "{}",
                        JsonSerializer.Serialize(new { classification = "provider_rejected", exception.StatusCode }),
                        Hash("cross-pair-rejected", fingerprint), cancellationToken);
                    await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                        SyncAuthorityAttemptStatus.Blocked,
                        "{\"reason\":\"Dropbox rejected the native cross-root move.\"}", cancellationToken);
                    return new SyncAuthorityExecutionResult(operation.OperationId,
                        SyncAuthorityOperationStatus.Blocked, true,
                        "Dropbox rejected the native cross-root move without applying it.");
                }
                catch (DropboxEffectUncertainException)
                {
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.Unknown, JsonSerializer.Serialize(predecessor), "{}",
                        "{\"classification\":\"cross-root move response unknown\"}",
                        Hash("cross-pair-unknown", fingerprint), cancellationToken);
                    return new SyncAuthorityExecutionResult(operation.OperationId,
                        SyncAuthorityOperationStatus.EffectUnknown, true,
                        "The Dropbox cross-root move response is unknown and will be reconciled by stable identity.");
                }
                PhaseForTest?.Invoke("dropbox-cross-root-moved-before-receipt");
                // The provider's own acknowledgement of the applied cross-root move is persisted before any
                // destination observation, so an interruption during convergence leaves a durable known-success
                // root-move receipt that binds the exact returned identity, path, kind, revision, size and content
                // hash rather than an unproven request.
                var rootMoveJson = JsonSerializer.Serialize(new
                {
                    classification = "root_move_applied",
                    fileId = movedRoot.Id,
                    path = movedRoot.RelativePath,
                    kind = movedRoot.Kind.ToString(),
                    revision = movedRoot.Revision,
                    size = movedRoot.Size,
                    contentHash = movedRoot.ContentHash
                });
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownSuccess,
                    JsonSerializer.Serialize(new { plan.SourcePairId, plan.SourcePath }),
                    JsonSerializer.Serialize(new { plan.DestinationPairId, plan.DestinationPath }),
                    rootMoveJson, Hash("cross-pair-root-move-applied", fingerprint, rootMoveJson),
                    cancellationToken);
            }
            else
            {
                try
                {
                    await ValidateDropboxOriginAsync(plan, operation, baseline, cancellationToken);
                }
                catch (IOException exception)
                {
                    return await BlockBeforeEffectAsync(operation, lease, fingerprint, exception.Message,
                        cancellationToken);
                }
                attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease,
                    fingerprint, "local-cross-root-move", cancellationToken);
                dispatched = true;
                if (EffectAdmissionPhaseForTest is not null)
                    await EffectAdmissionPhaseForTest("cross-pair-effect-admitted-before-physical-move");
                await MoveLocalAsync(plan, operation, baseline.Local, lease, cancellationToken);
                PhaseForTest?.Invoke("local-cross-root-moved-before-receipt");
            }

            return await CompleteKnownEffectAsync(plan, operation, lease, attempt, fingerprint,
                effectWasDispatched: true, cancellationToken);
        }
        finally
        {
            try { await leaseRenewal.ReleaseAsync(); }
            catch when (dispatched) { }
        }
    }

    private async Task<SyncAuthorityExecutionResult> BlockBeforeEffectAsync(
        SyncAuthorityOperationSnapshot operation, SyncAuthorityLease lease, string fingerprint,
        string reason, CancellationToken cancellationToken)
    {
        var attempt = await authority.StartDispatchedAttemptAsync(operation.OperationId, lease,
            fingerprint, "cross-pair-precondition-rejected-no-effect", cancellationToken);
        var detail = JsonSerializer.Serialize(new
        {
            classification = "precondition_rejected_before_effect",
            reason,
            contentEffect = false
        });
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}", detail,
            Hash("cross-pair-precondition-rejected", fingerprint, reason), cancellationToken);
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
            SyncAuthorityAttemptStatus.Blocked, detail, cancellationToken);
        return new SyncAuthorityExecutionResult(operation.OperationId,
            SyncAuthorityOperationStatus.Blocked, false,
            reason + " The durable two-pair transaction retains both path reservations and made no content change.");
    }

    private async Task<SyncAuthorityExecutionResult> ReconcileAsync(SyncAuthorityCrossPairMovePlan plan,
        SyncAuthorityOperationSnapshot operation, string workerId, CancellationToken cancellationToken)
    {
        var attempt = operation.Attempts.OrderBy(item => item.AttemptNumber).LastOrDefault(item =>
                          item.Status is SyncAuthorityAttemptStatus.RequestSent or
                              SyncAuthorityAttemptStatus.ResponseReceived or SyncAuthorityAttemptStatus.EffectUnknown)
                      ?? throw new InvalidOperationException("The interrupted cross-pair move has no attempt.");
        var baseline = await LoadBaselineAsync(plan, operation, cancellationToken);
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operation.OperationId, workerId,
            LeaseDurationForTest ?? SyncAuthorityLeasePolicy.LeaseDuration, cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            var localToDropbox = plan.Direction == SyncAuthorityCrossPairMoveDirection.LocalToDropbox;
            // SS-D18: a dispatched cross-root network request that retained no durable terminal receipt has no
            // proof of its outcome. The retained receipt is therefore loaded before any inventory or path
            // observation, and the current source and destination are not evidence about that sent request: the
            // operation is returned explicitly unresolved with zero inventory, effect and authority transition.
            // This gate belongs to the outbound local-origin direction only; the native local and cross-volume
            // directions keep their own fully observable reconciliation semantics.
            var retainedReceipt = localToDropbox
                ? await authority.LoadLatestEffectReceiptAsync(operation.OperationId, attempt.AttemptId,
                    cancellationToken)
                : null;
            var durableTerminalReceipt = retainedReceipt is not null &&
                (string.Equals(retainedReceipt.Certainty,
                     SyncAuthorityNames.ReceiptCertainty(SyncAuthorityReceiptCertainty.KnownSuccess),
                     StringComparison.Ordinal) ||
                 string.Equals(retainedReceipt.Certainty,
                     SyncAuthorityNames.ReceiptCertainty(SyncAuthorityReceiptCertainty.KnownNotApplied),
                     StringComparison.Ordinal));
            var positiveNotAppliedReceipt = retainedReceipt is not null && string.Equals(
                retainedReceipt.Certainty,
                SyncAuthorityNames.ReceiptCertainty(SyncAuthorityReceiptCertainty.KnownNotApplied),
                StringComparison.Ordinal);
            if (localToDropbox && attempt.Status == SyncAuthorityAttemptStatus.RequestSent &&
                !durableTerminalReceipt)
                return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                    "The cross-root Dropbox move request was sent without a durable receipt, so its outcome is " +
                    "unknown; the operation stays explicitly unresolved and no outcome, inventory observation or " +
                    "retry is derived from the current source or destination.");
            var state = plan.Direction == SyncAuthorityCrossPairMoveDirection.LocalToDropbox
                ? await InspectRemoteMoveStateAsync(plan, baseline.Dropbox, cancellationToken)
                : await InspectLocalMoveStateAsync(plan, baseline.Local, cancellationToken);
            if (state == ReconcileState.Applied)
                return await CompleteKnownEffectAsync(plan, operation, lease, attempt,
                    attempt.RequestFingerprint, effectWasDispatched: false, cancellationToken);
            if (state == ReconcileState.CrossVolumeCopyNeedsRetirement)
            {
                await RetireCrossVolumeSourceAsync(plan, operation, baseline.Local, lease,
                    cancellationToken);
                return await CompleteKnownEffectAsync(plan, operation, lease, attempt,
                    attempt.RequestFingerprint, effectWasDispatched: false, cancellationToken);
            }
            // A source that is still present with a free destination is an observation, never proof that the
            // sent network request cannot still finish. Without the provider's own retained known-not-applied
            // receipt that observation cannot clear the reservation or open a retry.
            if (state == ReconcileState.NotApplied && localToDropbox && !positiveNotAppliedReceipt)
                return new SyncAuthorityExecutionResult(operation.OperationId, operation.Status, false,
                    "The interrupted cross-root Dropbox move has no durable terminal receipt, so the current " +
                    "source and destination cannot prove non-application; the operation stays explicitly " +
                    "unresolved and no retry is opened.");
            if (state == ReconcileState.NotApplied)
            {
                // The outbound direction records no new non-application receipt here: the retained positive
                // known-not-applied receipt is the evidence that authorises the terminal disposition. The native
                // local and cross-volume directions keep their existing recorded observation.
                if (!localToDropbox)
                    await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                        SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}",
                        "{\"reconciliation\":\"stable identity remains at source\"}",
                        Hash("cross-pair-not-applied", attempt.RequestFingerprint,
                            attempt.Status.ToString()), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.VerifiedNotApplied,
                    "{\"result\":\"cross-pair move verified not applied\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operation.OperationId,
                    SyncAuthorityOperationStatus.Ready, false,
                    "The exact source remains unchanged and one fenced retry is allowed.");
            }
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}",
                "{\"reconciliation\":\"cross-pair state is ambiguous\"}",
                Hash("cross-pair-ambiguous", attempt.RequestFingerprint,
                    attempt.Status.ToString()), cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.Blocked,
                "{\"result\":\"cross-pair identity or destination is ambiguous; all content retained\"}",
                cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Blocked, false,
                "The interrupted cross-pair move is ambiguous; all observed content was retained.");
        }
        finally { await leaseRenewal.ReleaseAsync(); }
    }

    private async Task<SyncAuthorityExecutionResult> CompleteKnownEffectAsync(
        SyncAuthorityCrossPairMovePlan plan, SyncAuthorityOperationSnapshot operation,
        SyncAuthorityLease lease, SyncAuthorityAttempt attempt, string fingerprint,
        bool effectWasDispatched, CancellationToken cancellationToken)
    {
        string successorDetail;
        try
        {
            successorDetail = await ObserveAndAcceptDestinationAsync(plan, operation, lease, cancellationToken);
        }
        catch (IOException exception)
        {
            var detail = JsonSerializer.Serialize(new
            {
                classification = "root_effect_applied_destination_subtree_diverged",
                reason = exception.Message,
                contentRetained = true
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess,
                JsonSerializer.Serialize(new { plan.SourcePairId, plan.SourcePath }),
                JsonSerializer.Serialize(new { plan.DestinationPairId, plan.DestinationPath }),
                detail, Hash("cross-pair-applied-subtree-diverged", fingerprint, detail), cancellationToken);
            await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                SyncAuthorityAttemptStatus.Blocked, detail, cancellationToken);
            return new SyncAuthorityExecutionResult(operation.OperationId,
                SyncAuthorityOperationStatus.Blocked, effectWasDispatched,
                "The provider applied the root move, but the destination subtree did not converge on one exact " +
                "object; the moved contents still need a fresh comparison and the complete subtree remains " +
                "fenced for review.");
        }
        await authority.RecordReceiptAsync(attempt.AttemptId, lease,
            SyncAuthorityReceiptCertainty.KnownSuccess,
            JsonSerializer.Serialize(new { plan.SourcePairId, plan.SourcePath }),
            JsonSerializer.Serialize(new { plan.DestinationPairId, plan.DestinationPath }),
            successorDetail, Hash("cross-pair-success", fingerprint, successorDetail), cancellationToken);
        await authority.CommitSuccessfulAuxiliaryAttemptAsync(attempt.AttemptId, lease,
            JsonSerializer.Serialize(new
            {
                result = "identity-preserving cross-pair move committed",
                plan.SourcePairId,
                plan.DestinationPairId,
                plan.SourcePath,
                plan.DestinationPath,
                direction = plan.Direction.ToString()
            }), cancellationToken);
        await authority.CompleteOperationAsync(lease,
            "{\"result\":\"identity-preserving cross-pair move complete\"}", cancellationToken);
        await authority.CompleteOperationGroupAsync(plan.OperationGroupId, cancellationToken);
        return new SyncAuthorityExecutionResult(operation.OperationId,
            SyncAuthorityOperationStatus.Completed, effectWasDispatched,
            plan.Direction == SyncAuthorityCrossPairMoveDirection.LocalToDropbox
                ? "Dropbox moved the existing object between pair roots without uploading or deleting its content."
                : SameLocalVolume()
                    ? "Windows atomically moved the existing local object between pair roots."
                    : "The local object was verified on the destination volume before its source identity was retained in Recovery.");
    }

    private async Task<PilotRemoteEntry> ValidateLocalOriginAsync(SyncAuthorityCrossPairMovePlan plan,
        SyncAuthorityOperationSnapshot operation, Baseline baseline, CancellationToken cancellationToken)
    {
        var sourceEntries = await sourceRemoteStore.ListEntriesAsync(cancellationToken);
        // SS-D17 (local-origin outbound direction): this source-side Dropbox object is the object that is about
        // to move between pair roots, so its current remote contents are accepted while the exact path, the
        // scoped stable identity and the kind stay mandatory. An object that changed identity or kind, or that
        // is absent from its retained path, is still refused.
        var predecessor = ExactRemoteIdentity(sourceEntries, plan.SourcePath, baseline.Dropbox);
        var destinationEntries = await destinationRemoteStore.ListEntriesAsync(cancellationToken);
        if (destinationEntries.Any(item => SamePath(item.RelativePath, plan.DestinationPath)))
            throw new IOException("The Dropbox cross-pair destination is occupied.");
        using var destinationRoot = PilotDirectoryLease.Open(destinationPair.LocalRoot,
            new PilotDirectoryIdentity(destinationPair.LocalVolumeSerialNumber, destinationPair.LocalRootFileId));
        var destinationScan = await new PilotLocalScanner().ScanAsync(destinationRoot,
            cancellationToken: cancellationToken);
        if (!destinationScan.Entries.TryGetValue(plan.DestinationPath, out var movedLocal))
            throw new IOException("The exact moved local object is absent from the destination pair.");
        SyncAuthorityMoveRemoteExecutor.RequireExactLocal(movedLocal, baseline.Local);
        using var sourceRoot = PilotDirectoryLease.Open(sourcePair.LocalRoot,
            new PilotDirectoryIdentity(sourcePair.LocalVolumeSerialNumber, sourcePair.LocalRootFileId));
        var sourceScan = await new PilotLocalScanner().ScanAsync(sourceRoot, cancellationToken: cancellationToken);
        if (sourceScan.Entries.Values.Any(item => LocalIdentity(item) == StableLocalIdentity(baseline.Local)))
            throw new IOException("The moved NTFS identity is still visible in the source pair.");
        if (plan.Kind == PilotEntryKind.Directory)
        {
            var retained = await LoadRetainedSourceBindingsAsync(plan, operation, cancellationToken);
            RequireExactRemoteSubtree(sourceEntries, plan.SourcePath, plan.SourcePath, retained,
                item => item.DropboxMember, item => item.Dropbox, allowChangedContent: true);
            RequireExactLocalSubtree(destinationScan.Entries.Values, plan.DestinationPath, plan.SourcePath,
                retained, item => item.LocalMember, item => item.Local);
        }
        return predecessor;
    }

    private async Task ValidateDropboxOriginAsync(SyncAuthorityCrossPairMovePlan plan,
        SyncAuthorityOperationSnapshot operation, Baseline baseline, CancellationToken cancellationToken)
    {
        var sourceRemote = await sourceRemoteStore.ListEntriesAsync(cancellationToken);
        var dropboxId = StableDropboxIdentity(baseline.Dropbox);
        if (sourceRemote.Any(item => string.Equals(item.Id, dropboxId, StringComparison.Ordinal)))
            throw new IOException("The Dropbox identity is still visible in the source pair.");
        var destinationRemote = await destinationRemoteStore.ListEntriesAsync(cancellationToken);
        _ = ExactRemote(destinationRemote, plan.DestinationPath, baseline.Dropbox);
        using var sourceRoot = PilotDirectoryLease.Open(sourcePair.LocalRoot,
            new PilotDirectoryIdentity(sourcePair.LocalVolumeSerialNumber, sourcePair.LocalRootFileId));
        var sourceScan = await new PilotLocalScanner().ScanAsync(sourceRoot, cancellationToken: cancellationToken);
        if (!sourceScan.Entries.TryGetValue(plan.SourcePath, out var local))
            throw new IOException("The exact local source is absent.");
        SyncAuthorityMoveRemoteExecutor.RequireExactLocal(local, baseline.Local);
        using var destinationRoot = PilotDirectoryLease.Open(destinationPair.LocalRoot,
            new PilotDirectoryIdentity(destinationPair.LocalVolumeSerialNumber, destinationPair.LocalRootFileId));
        var destinationPath = PilotPath.ResolveContainedPath(destinationRoot.Path, plan.DestinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException("The local cross-pair destination is occupied.");
        if (plan.Kind == PilotEntryKind.Directory)
        {
            var retained = await LoadRetainedSourceBindingsAsync(plan, operation, cancellationToken);
            RequireExactLocalSubtree(sourceScan.Entries.Values, plan.SourcePath, plan.SourcePath, retained,
                item => item.LocalMember, item => item.Local);
            RequireExactRemoteSubtree(destinationRemote, plan.DestinationPath, plan.SourcePath, retained,
                item => item.DropboxMember, item => item.Dropbox);
        }
    }

    private async Task MoveLocalAsync(SyncAuthorityCrossPairMovePlan plan,
        SyncAuthorityOperationSnapshot operation, SyncAuthorityObjectVersion baselineLocal,
        SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        using var sourceRoot = PilotDirectoryLease.Open(sourcePair.LocalRoot,
            new PilotDirectoryIdentity(sourcePair.LocalVolumeSerialNumber, sourcePair.LocalRootFileId));
        using var destinationRoot = PilotDirectoryLease.Open(destinationPair.LocalRoot,
            new PilotDirectoryIdentity(destinationPair.LocalVolumeSerialNumber, destinationPair.LocalRootFileId));
        var sourcePath = PilotPath.ResolveContainedPath(sourceRoot.Path, plan.SourcePath);
        var destinationPath = PilotPath.ResolveContainedPath(destinationRoot.Path, plan.DestinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException("The local cross-pair destination became occupied.");
        if (SameLocalVolume())
        {
            if (plan.Kind == PilotEntryKind.File)
            {
                await using var source = SyncLocalFileAuthority.OpenExistingExclusive(sourcePath);
                await VerifyExactFileAsync(sourcePair.Provider, source, sourcePath, baselineLocal, sourceRoot, cancellationToken);
                source.RenameTo(destinationRoot, plan.DestinationPath);
            }
            else
            {
                var identity = SyncAuthorityDeleteLocalExecutor.DirectoryIdentity(baselineLocal);
                using var source = PilotDirectoryLease.OpenOwnedDirectory(sourcePath, identity);
                source.RenameTo(destinationRoot, plan.DestinationPath);
            }
            return;
        }

        await CopyAcrossVolumesAsync(plan, operation, baselineLocal, sourceRoot, destinationRoot,
            lease, cancellationToken);
    }

    private async Task CopyAcrossVolumesAsync(SyncAuthorityCrossPairMovePlan plan,
        SyncAuthorityOperationSnapshot operation, SyncAuthorityObjectVersion baselineLocal,
        PilotDirectoryLease sourceRoot, PilotDirectoryLease destinationRoot, SyncAuthorityLease lease,
        CancellationToken cancellationToken)
    {
        if (destinationPair.ControlNamespace is null || sourcePair.ControlNamespace is null)
            throw new InvalidOperationException("Cross-volume movement requires both protected control namespaces.");
        using var destinationControl = SyncControlNamespace.OpenLocal(destinationPair.ControlNamespace,
            destinationRoot);
        var token = Hash("cross-volume-stage", operation.OperationId)[..24];
        var stageName = "cross-pair-stage-" + token;
        var stagePath = Path.Combine(destinationControl.Path, stageName);
        if (File.Exists(stagePath) || Directory.Exists(stagePath))
            throw new IOException("An unbound cross-volume staging object already exists.");
        var sourcePath = PilotPath.ResolveContainedPath(sourceRoot.Path, plan.SourcePath);
        if (plan.Kind == PilotEntryKind.File)
        {
            await using var source = SyncLocalFileAuthority.OpenExistingExclusive(sourcePath);
            await VerifyExactFileAsync(sourcePair.Provider, source, sourcePath, baselineLocal, sourceRoot, cancellationToken);
            await using var stage = SyncLocalFileAuthority.CreateNewMovableFile(stagePath);
            source.Stream.Position = 0;
            await source.Stream.CopyToAsync(stage.CreateGuardedWriteStream(), cancellationToken);
            await stage.Stream.FlushAsync(cancellationToken);
            stage.Stream.Flush(true);
            stage.Stream.Position = 0;
            var hash = await SyncContentIdentity.ComputeAsync(sourcePair.Provider, stage.Stream, cancellationToken);
            if (stage.Stream.Length != baselineLocal.Size ||
                !string.Equals(hash, baselineLocal.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The cross-volume file copy failed exact readback.");
            stage.RenameTo(destinationRoot, plan.DestinationPath);
        }
        else
        {
            var sourceManifest = await BuildDirectoryManifestAsync(sourcePair.Provider, sourcePath, cancellationToken);
            using var stage = PilotDirectoryLease.CreateChild(destinationControl, stageName);
            await CopyDirectoryTreeAsync(sourcePath, stage.Path, cancellationToken);
            var copiedManifest = await BuildDirectoryManifestAsync(sourcePair.Provider, stage.Path, cancellationToken);
            var sourceAgain = await BuildDirectoryManifestAsync(sourcePair.Provider, sourcePath, cancellationToken);
            if (sourceManifest != copiedManifest || sourceManifest != sourceAgain)
                throw new IOException("The cross-volume directory changed or failed exact copy verification.");
            stage.RenameTo(destinationRoot, plan.DestinationPath);
        }
        PhaseForTest?.Invoke("cross-volume-destination-verified-before-source-retirement");
        await RetireCrossVolumeSourceAsync(plan, operation, baselineLocal, lease, cancellationToken);
    }

    private async Task RetireCrossVolumeSourceAsync(SyncAuthorityCrossPairMovePlan plan,
        SyncAuthorityOperationSnapshot operation, SyncAuthorityObjectVersion baselineLocal,
        SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        if (sourcePair.ControlNamespace is null)
            throw new InvalidOperationException("The source pair has no protected local Recovery namespace.");
        using var sourceRoot = PilotDirectoryLease.Open(sourcePair.LocalRoot,
            new PilotDirectoryIdentity(sourcePair.LocalVolumeSerialNumber, sourcePair.LocalRootFileId));
        using var sourceControl = SyncControlNamespace.OpenLocal(sourcePair.ControlNamespace, sourceRoot);
        var allocation = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.LocalRecoveryFile);
        var recoveryFolder = SyncLocalRecovery.RecoveryFolderPrefix + sourcePair.Id;
        using var recoveryRoot = PilotDirectoryLease.OpenOrCreateDescendant(sourceControl, recoveryFolder);
        var recoveryPath = Path.Combine(recoveryRoot.Path, allocation.DeterministicName);
        var sourcePath = PilotPath.ResolveContainedPath(sourceRoot.Path, plan.SourcePath);
        if (File.Exists(recoveryPath) || Directory.Exists(recoveryPath))
        {
            if (File.Exists(sourcePath) || Directory.Exists(sourcePath))
                throw new IOException("Both source and bound Recovery memberships exist after interruption.");
            return;
        }
        if (allocation.Status == SyncAuthorityAllocationStatus.Authorized)
            await authority.TransitionAllocationAsync(allocation.AllocationId, lease,
                SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
        string identity;
        string contentHash;
        long size;
        if (plan.Kind == PilotEntryKind.File)
        {
            await using var source = SyncLocalFileAuthority.OpenExistingExclusive(sourcePath);
            await VerifyExactFileAsync(sourcePair.Provider, source, sourcePath, baselineLocal, sourceRoot, cancellationToken);
            var localIdentity = source.VerifySingleLink();
            source.RenameTo(recoveryRoot, allocation.DeterministicName);
            identity = Format(localIdentity);
            contentHash = baselineLocal.ContentHash!;
            size = baselineLocal.Size!.Value;
        }
        else
        {
            var directoryIdentity = SyncAuthorityDeleteLocalExecutor.DirectoryIdentity(baselineLocal);
            var manifest = await BuildDirectoryManifestAsync(sourcePair.Provider, sourcePath, cancellationToken);
            using var source = PilotDirectoryLease.OpenOwnedDirectory(sourcePath, directoryIdentity);
            source.RenameTo(recoveryRoot, allocation.DeterministicName);
            identity = Format(directoryIdentity);
            contentHash = manifest.Hash;
            size = manifest.Size;
        }
        await AdvanceAllocationAsync(allocation.AllocationId, lease, identity, contentHash, size,
            cancellationToken);
        var recoveryJson = JsonSerializer.Serialize(new
        {
            path = recoveryPath,
            identity,
            kind = plan.Kind.ToString().ToLowerInvariant(),
            size,
            contentHash,
            sourcePairId = sourcePair.Id,
            destinationPairId = destinationPair.Id
        });
        await authority.RegisterRecoveryArtifactAsync(Id("recovery-artifact", operation.OperationId),
            operation.OperationId, operation.ObjectId!, allocation.AllocationId, "local", recoveryJson,
            contentHash, size, cancellationToken);
    }

    private async Task<ReconcileState> InspectRemoteMoveStateAsync(SyncAuthorityCrossPairMovePlan plan,
        SyncAuthorityObjectVersion baselineDropbox, CancellationToken cancellationToken)
    {
        var id = StableDropboxIdentity(baselineDropbox);
        var sourceEntries = await sourceRemoteStore.ListEntriesAsync(cancellationToken);
        var destinationEntries = await destinationRemoteStore.ListEntriesAsync(cancellationToken);
        var source = sourceEntries.Where(item => string.Equals(item.Id, id, StringComparison.Ordinal)).ToArray();
        var destination = destinationEntries.Where(item => string.Equals(item.Id, id, StringComparison.Ordinal))
            .ToArray();
        if (source.Length == 0 && destination.Length == 1 &&
            SamePath(destination[0].RelativePath, plan.DestinationPath) &&
            RemoteVersionMatches(destination[0], baselineDropbox))
            return ReconcileState.Applied;
        if (source.Length == 1 && destination.Length == 0 &&
            SamePath(source[0].RelativePath, plan.SourcePath) && RemoteVersionMatches(source[0], baselineDropbox) &&
            !destinationEntries.Any(item => SamePath(item.RelativePath, plan.DestinationPath)))
            return ReconcileState.NotApplied;
        return ReconcileState.Ambiguous;
    }

    private async Task<ReconcileState> InspectLocalMoveStateAsync(SyncAuthorityCrossPairMovePlan plan,
        SyncAuthorityObjectVersion baselineLocal, CancellationToken cancellationToken)
    {
        using var sourceRoot = PilotDirectoryLease.Open(sourcePair.LocalRoot,
            new PilotDirectoryIdentity(sourcePair.LocalVolumeSerialNumber, sourcePair.LocalRootFileId));
        using var destinationRoot = PilotDirectoryLease.Open(destinationPair.LocalRoot,
            new PilotDirectoryIdentity(destinationPair.LocalVolumeSerialNumber, destinationPair.LocalRootFileId));
        var sourceScan = await new PilotLocalScanner().ScanAsync(sourceRoot, cancellationToken: cancellationToken);
        var destinationScan = await new PilotLocalScanner().ScanAsync(destinationRoot,
            cancellationToken: cancellationToken);
        var identity = StableLocalIdentity(baselineLocal);
        var sourceIdentity = sourceScan.Entries.Values.Where(item => LocalIdentity(item) == identity).ToArray();
        var destinationIdentity = destinationScan.Entries.Values.Where(item => LocalIdentity(item) == identity).ToArray();
        if (SameLocalVolume())
        {
            if (sourceIdentity.Length == 0 && destinationIdentity.Length == 1 &&
                SamePath(destinationIdentity[0].RelativePath, plan.DestinationPath))
                return ReconcileState.Applied;
            if (sourceIdentity.Length == 1 && destinationIdentity.Length == 0 &&
                SamePath(sourceIdentity[0].RelativePath, plan.SourcePath) &&
                !destinationScan.Entries.ContainsKey(plan.DestinationPath))
                return ReconcileState.NotApplied;
            return ReconcileState.Ambiguous;
        }
        var destinationMatches = destinationScan.Entries.TryGetValue(plan.DestinationPath, out var copied) &&
                                 EntryMatchesVersion(copied, baselineLocal);
        var sourceMatches = sourceScan.Entries.TryGetValue(plan.SourcePath, out var source) &&
                            EntryMatchesVersion(source, baselineLocal);
        if (destinationMatches && sourceMatches) return ReconcileState.CrossVolumeCopyNeedsRetirement;
        if (destinationMatches && !sourceMatches) return ReconcileState.Applied;
        if (!destinationMatches && sourceMatches) return ReconcileState.NotApplied;
        return ReconcileState.Ambiguous;
    }

    private async Task<string> ObserveAndAcceptDestinationAsync(SyncAuthorityCrossPairMovePlan plan,
        SyncAuthorityOperationSnapshot operation, SyncAuthorityLease lease,
        CancellationToken cancellationToken)
    {
        var bindings = await BuildDestinationBindingsAsync(plan, operation, cancellationToken);
        var bindingAuthority = new SyncAuthorityObservationBindingAuthority(operation.OperationId, lease, bindings);
        var sourceLocalRoot = await authority.LoadActiveRootBindingIdAsync(sourcePair.Id, "local", cancellationToken);
        var sourceDropboxRoot = await authority.LoadActiveRootBindingIdAsync(sourcePair.Id, "dropbox", cancellationToken);
        var destinationLocalRoot = await authority.LoadActiveRootBindingIdAsync(destinationPair.Id, "local",
            cancellationToken);
        var destinationDropboxRoot = await authority.LoadActiveRootBindingIdAsync(destinationPair.Id, "dropbox",
            cancellationToken);
        await new SyncAuthorityObservationCoordinator(sourcePair, sourceRemoteStore, authority,
            sourceLocalRoot, sourceDropboxRoot).ObserveAsync("monitor", cancellationToken);
        await new SyncAuthorityObservationCoordinator(destinationPair, destinationRemoteStore, authority,
            destinationLocalRoot, destinationDropboxRoot).ObserveBoundAsync("monitor", bindingAuthority,
            cancellationToken);
        var local = (await authority.LoadLatestCompleteObservationAsync(destinationPair.Id, "local",
            cancellationToken)).Where(item => IsSameOrDescendant(item.RelativePath, plan.DestinationPath)).ToArray();
        var remote = (await authority.LoadLatestCompleteObservationAsync(destinationPair.Id, "dropbox",
            cancellationToken)).Where(item => IsSameOrDescendant(item.RelativePath, plan.DestinationPath))
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var expectedLocalPaths = bindings.Where(item => item.Side == "local")
            .Select(item => PilotPath.NormalizeRelative(item.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedRemotePaths = bindings.Where(item => item.Side == "dropbox")
            .Select(item => PilotPath.NormalizeRelative(item.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedLocalPaths.SetEquals(local.Select(item => item.RelativePath)) ||
            !expectedRemotePaths.SetEquals(remote.Keys))
            throw new CrossPairDestinationDivergenceException(
                "The complete cross-pair destination subtree differs from its immutable binding inventory.");
        if (local.Length == 0)
            throw new CrossPairDestinationDivergenceException(
                "The moved destination is absent after the cross-pair effect.");
        var accepted = new List<(SyncAuthorityObservedEntry Local, SyncAuthorityObservedEntry Remote)>();
        foreach (var localEntry in local.OrderBy(item => Depth(item.RelativePath)))
        {
            if (!remote.TryGetValue(localEntry.RelativePath, out var remoteEntry) ||
                !ObservedContentMatches(localEntry, remoteEntry) || localEntry.ObjectId != remoteEntry.ObjectId)
                throw new CrossPairDestinationDivergenceException(
                    "The cross-pair destination sides did not converge on one exact object.");
            accepted.Add((localEntry, remoteEntry));
        }
        if (accepted.Count != remote.Count)
            throw new CrossPairDestinationDivergenceException(
                "The cross-pair destination sides retained different subtree membership.");
        foreach (var (localEntry, remoteEntry) in accepted)
        {
            await authority.AcceptSynchronizationPointAsync(Id("syncpoint-cross-pair", plan.OperationId,
                    localEntry.RelativePath, localEntry.ObjectVersionId, remoteEntry.ObjectVersionId),
                destinationPair.Id, localEntry.ObjectId, localEntry.ObjectVersionId,
                remoteEntry.ObjectVersionId, localEntry.MembershipId, remoteEntry.MembershipId,
                cancellationToken);
        }
        return JsonSerializer.Serialize(new
        {
            acceptedObjects = accepted.Count,
            destinationPairId = destinationPair.Id,
            destinationPath = plan.DestinationPath,
            providerIdentityPreserved = plan.Direction == SyncAuthorityCrossPairMoveDirection.LocalToDropbox,
            localIdentityPreserved = plan.Direction == SyncAuthorityCrossPairMoveDirection.DropboxToLocal &&
                                     SameLocalVolume()
        });
    }

    private async Task<IReadOnlyList<SyncAuthorityObservationObjectBinding>> BuildDestinationBindingsAsync(
        SyncAuthorityCrossPairMovePlan plan, SyncAuthorityOperationSnapshot operation,
        CancellationToken cancellationToken)
    {
        var points = await LoadRetainedSourceBindingsAsync(plan, operation, cancellationToken);

        var bindings = new List<SyncAuthorityObservationObjectBinding>(points.Length * 2);
        foreach (var point in points)
        {
            var localPath = point.LocalMember.ExpectedPathKey!;
            var dropboxPath = point.DropboxMember.ExpectedPathKey!;
            if (!string.Equals(point.Local.ObjectId, point.ObjectId, StringComparison.Ordinal) ||
                !string.Equals(point.Dropbox.ObjectId, point.ObjectId, StringComparison.Ordinal) ||
                !string.Equals(point.Local.ContentHash, point.Dropbox.ContentHash,
                    StringComparison.OrdinalIgnoreCase) || point.Local.Size != point.Dropbox.Size)
                throw new InvalidDataException("The retained moved subtree contains a divergent source baseline.");
            var objectKind = point.Local.ContentHash is null ? "directory" : "file";
            bindings.Add(new SyncAuthorityObservationObjectBinding("local", localPath,
                RebaseDestinationPath(localPath, plan.SourcePath, plan.DestinationPath), point.ObjectId,
                objectKind, point.Local.ObjectVersionId, point.Local.IdentityJson, point.Local.ContentHash,
                point.Local.Size, SameLocalVolume()));
            bindings.Add(new SyncAuthorityObservationObjectBinding("dropbox", dropboxPath,
                RebaseDestinationPath(dropboxPath, plan.SourcePath, plan.DestinationPath), point.ObjectId,
                objectKind, point.Dropbox.ObjectVersionId, point.Dropbox.IdentityJson, point.Dropbox.ContentHash,
                point.Dropbox.Size, true));
        }
        return bindings;
    }

    private async Task<RetainedSourceBinding[]> LoadRetainedSourceBindingsAsync(
        SyncAuthorityCrossPairMovePlan plan, SyncAuthorityOperationSnapshot operation,
        CancellationToken cancellationToken)
    {
        var sourceMembers = operation.Members.Where(item =>
                string.Equals(item.PairId, sourcePair.Id, StringComparison.Ordinal) &&
                string.Equals(item.Role, "source", StringComparison.Ordinal) &&
                item.ObjectVersionId is not null && item.ExpectedPathKey is not null &&
                IsSameOrDescendant(item.ExpectedPathKey, plan.SourcePath))
            .ToArray();
        var retained = new List<RetainedSourceBinding>();
        foreach (var group in sourceMembers.GroupBy(item => item.ObjectId, StringComparer.Ordinal))
        {
            var versions = new List<(SyncAuthorityMember Member, SyncAuthorityObjectVersion Version)>();
            foreach (var member in group)
                versions.Add((member, await authority.LoadObjectVersionAsync(member.ObjectVersionId!,
                    cancellationToken)));
            var local = versions.Where(item => string.Equals(item.Version.Side, "local",
                StringComparison.Ordinal)).ToArray();
            var dropbox = versions.Where(item => string.Equals(item.Version.Side, "dropbox",
                StringComparison.Ordinal)).ToArray();
            if (versions.Count != 2 || local.Length != 1 || dropbox.Length != 1)
                throw new InvalidDataException("The retained moved subtree lacks one exact two-sided source version.");
            var localSuffix = SubtreeSuffix(local[0].Member.ExpectedPathKey!, plan.SourcePath);
            var dropboxSuffix = SubtreeSuffix(dropbox[0].Member.ExpectedPathKey!, plan.SourcePath);
            if (!string.Equals(localSuffix, dropboxSuffix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The retained moved subtree has mismatched source-side membership.");
            retained.Add(new RetainedSourceBinding(group.Key, local[0].Member, local[0].Version,
                dropbox[0].Member, dropbox[0].Version));
        }
        var points = retained.OrderBy(item => Depth(item.LocalMember.ExpectedPathKey!))
            .ThenBy(item => item.LocalMember.ExpectedPathKey, StringComparer.OrdinalIgnoreCase).ToArray();
        if (sourceMembers.Length != points.Length * 2 || points.Length == 0 || operation.ObjectId is null ||
            points.Count(item => string.Equals(item.ObjectId, operation.ObjectId, StringComparison.Ordinal) &&
                                 SamePath(item.LocalMember.ExpectedPathKey!, plan.SourcePath) &&
                                 SamePath(item.DropboxMember.ExpectedPathKey!, plan.SourcePath)) != 1)
            throw new InvalidDataException("The retained moved subtree has no exact source synchronization point inventory.");
        return points;
    }

    private static void RequireExactLocalSubtree(
        IEnumerable<PilotLocalEntry> actualEntries, string actualRoot, string retainedRoot,
        IReadOnlyList<RetainedSourceBinding> retained,
        Func<RetainedSourceBinding, SyncAuthorityMember> memberSelector,
        Func<RetainedSourceBinding, SyncAuthorityObjectVersion> versionSelector)
    {
        var actual = actualEntries.Where(item => IsSameOrDescendant(item.RelativePath, actualRoot))
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (actual.Count != retained.Count)
            throw new IOException("The moved local directory membership differs from its immutable synchronized subtree.");
        foreach (var point in retained)
        {
            var member = memberSelector(point);
            var version = versionSelector(point);
            var expectedPath = RebaseDestinationPath(member.ExpectedPathKey!, retainedRoot, actualRoot);
            if (!actual.TryGetValue(expectedPath, out var entry) ||
                !string.Equals(LocalIdentity(entry), StableLocalIdentity(version), StringComparison.Ordinal) ||
                !EntryMatchesVersion(entry, version))
                throw new IOException("The moved local directory contains a descendant that differs from its immutable synchronized subtree.");
        }
    }

    private static void RequireExactRemoteSubtree(
        IEnumerable<PilotRemoteEntry> actualEntries, string actualRoot, string retainedRoot,
        IReadOnlyList<RetainedSourceBinding> retained,
        Func<RetainedSourceBinding, SyncAuthorityMember> memberSelector,
        Func<RetainedSourceBinding, SyncAuthorityObjectVersion> versionSelector,
        bool allowChangedContent = false)
    {
        var actual = actualEntries.Where(item => IsSameOrDescendant(item.RelativePath, actualRoot))
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (actual.Count != retained.Count)
            throw new IOException("The moved Dropbox directory membership differs from its immutable synchronized subtree.");
        foreach (var point in retained)
        {
            var member = memberSelector(point);
            var version = versionSelector(point);
            var expectedPath = RebaseDestinationPath(member.ExpectedPathKey!, retainedRoot, actualRoot);
            // The move-identity variant retains the exact path set, the scoped stable identities and the kinds
            // and permits only the remote side's content version/hash/size to differ, which is the accepted
            // outbound native-move semantics for the direction that carries the provider's latest contents.
            if (!actual.TryGetValue(expectedPath, out var entry) ||
                !(allowChangedContent
                    ? RemoteIdentityKindMatches(entry, version)
                    : RemoteVersionMatches(entry, version)))
                throw new IOException("The moved Dropbox directory contains a descendant that differs from its immutable synchronized subtree.");
        }
    }

    private static string RebaseDestinationPath(string path, string sourcePath, string destinationPath)
    {
        var normalized = PilotPath.NormalizeRelative(path);
        var source = PilotPath.NormalizeRelative(sourcePath);
        var destination = PilotPath.NormalizeRelative(destinationPath);
        if (SamePath(normalized, source)) return destination;
        var prefix = source + "/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A moved source object is outside the retained subtree.");
        return destination + "/" + normalized[prefix.Length..];
    }

    private bool SameLocalVolume() => !ForceCrossVolumeForTest &&
                                      sourcePair.LocalVolumeSerialNumber ==
                                      destinationPair.LocalVolumeSerialNumber;

    private async Task<Baseline> LoadBaselineAsync(SyncAuthorityCrossPairMovePlan plan,
        SyncAuthorityOperationSnapshot operation,
        CancellationToken cancellationToken)
    {
        var members = operation.Members.Where(item =>
                string.Equals(item.PairId, sourcePair.Id, StringComparison.Ordinal) &&
                string.Equals(item.Role, "source", StringComparison.Ordinal) &&
                item.ObjectVersionId is not null && item.ExpectedPathKey is not null &&
                SamePath(item.ExpectedPathKey, plan.SourcePath))
            .ToArray();
        var versions = new List<SyncAuthorityObjectVersion>(members.Length);
        foreach (var member in members)
            versions.Add(await authority.LoadObjectVersionAsync(member.ObjectVersionId!, cancellationToken));
        var local = versions.SingleOrDefault(item => string.Equals(item.Side, "local", StringComparison.Ordinal));
        var dropbox = versions.SingleOrDefault(item => string.Equals(item.Side, "dropbox", StringComparison.Ordinal));
        if (local is null || dropbox is null || operation.ObjectId is null ||
            !string.Equals(local.ObjectId, operation.ObjectId, StringComparison.Ordinal) ||
            !string.Equals(dropbox.ObjectId, operation.ObjectId, StringComparison.Ordinal))
            throw new InvalidDataException("The cross-pair operation lacks its exact immutable two-sided baseline.");
        return new Baseline(local, dropbox);
    }

    private async Task<IReadOnlyList<IdentityEntry>> LoadIdentitiesAsync(
        IReadOnlyList<SyncAuthorityObservedEntry> entries, string side, CancellationToken cancellationToken)
    {
        var result = new List<IdentityEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var version = await authority.LoadObjectVersionAsync(entry.ObjectVersionId, cancellationToken);
            result.Add(new IdentityEntry(entry, side == "local"
                ? StableLocalIdentity(version)
                : StableDropboxIdentity(version)));
        }
        return result;
    }

    private static async Task VerifyExactFileAsync(string? contentProvider, SyncLocalFileAuthority file, string path,
        SyncAuthorityObjectVersion expected, PilotDirectoryLease root, CancellationToken cancellationToken)
    {
        if (expected.ContentHash is null || expected.Size is null)
            throw new InvalidDataException("The retained cross-pair file version is incomplete.");
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        var expectedIdentity = new PilotDirectoryIdentity(
            uint.Parse(identity.RootElement.GetProperty("volumeSerial").GetString()!,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ulong.Parse(identity.RootElement.GetProperty("fileId").GetString()!,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        if (file.VerifySingleLink() != expectedIdentity || file.Stream.Length != expected.Size ||
            !string.Equals(file.FinalPath, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local cross-pair source differs from its immutable identity.");
        file.Stream.Position = 0;
        var hash = await SyncContentIdentity.ComputeAsync(contentProvider,  file.Stream, cancellationToken);
        if (!string.Equals(hash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The local cross-pair source bytes changed.");
        root.VerifyBinding();
    }

    private async Task AdvanceAllocationAsync(string allocationId, SyncAuthorityLease lease,
        string identity, string contentHash, long size, CancellationToken cancellationToken)
    {
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Created,
            boundIdentity: identity, contentHash: contentHash, size: size, cancellationToken: cancellationToken);
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Bound,
            boundIdentity: identity, cancellationToken: cancellationToken);
        await authority.TransitionAllocationAsync(allocationId, lease, SyncAuthorityAllocationStatus.Verified,
            boundIdentity: identity, contentHash: contentHash, size: size, cancellationToken: cancellationToken);
    }

    internal static async Task CopyDirectoryTreeAsync(string source, string destination,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PilotPath.IsUnsafeAttributes(File.GetAttributes(directory)))
                throw new IOException("A reparse point cannot participate in a cross-volume directory move.");
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PilotPath.IsUnsafeAttributes(File.GetAttributes(file)))
                throw new IOException("A reparse point cannot participate in a cross-volume directory move.");
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
    }

    internal static async Task<DirectoryManifest> BuildDirectoryManifestAsync(string? contentProvider, string root,
        CancellationToken cancellationToken)
    {
        var rows = new List<string>();
        long size = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                     .OrderBy(item => Path.GetRelativePath(root, item), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if (PilotPath.IsUnsafeAttributes(attributes))
                throw new IOException("A reparse point cannot participate in a cross-volume directory move.");
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (attributes.HasFlag(FileAttributes.Directory))
                rows.Add("d\0" + relative);
            else
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await SyncContentIdentity.ComputeAsync(contentProvider,  stream, cancellationToken);
                size = checked(size + stream.Length);
                rows.Add("f\0" + relative + "\0" + stream.Length.ToString(CultureInfo.InvariantCulture) +
                         "\0" + hash);
            }
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))))
            .ToLowerInvariant();
        return new DirectoryManifest(digest, size, rows.Count);
    }

    private static PilotRemoteEntry ExactRemote(IEnumerable<PilotRemoteEntry> entries, string path,
        SyncAuthorityObjectVersion expected)
    {
        var matches = entries.Where(item => SamePath(item.RelativePath, path)).ToArray();
        if (matches.Length != 1 || !RemoteVersionMatches(matches[0], expected))
            throw new IOException("The Dropbox cross-pair object differs from its immutable baseline.");
        return matches[0];
    }

    // The identity-only form for the direction whose native move carries the provider's latest contents: the
    // exact path, the scoped stable identity and the kind are still mandatory and a different object is still
    // refused; only the current revision, size and content hash are accepted.
    private static PilotRemoteEntry ExactRemoteIdentity(IEnumerable<PilotRemoteEntry> entries, string path,
        SyncAuthorityObjectVersion expected)
    {
        var matches = entries.Where(item => SamePath(item.RelativePath, path)).ToArray();
        if (matches.Length != 1 || !RemoteIdentityKindMatches(matches[0], expected))
            throw new IOException("The Dropbox cross-pair object differs from its immutable baseline.");
        return matches[0];
    }

    private static bool RemoteVersionMatches(PilotRemoteEntry entry, SyncAuthorityObjectVersion expected)
    {
        if (!string.Equals(entry.Id, StableDropboxIdentity(expected), StringComparison.Ordinal)) return false;
        if (expected.ContentHash is null)
            return entry.Kind == PilotEntryKind.Directory && expected.Size is 0;
        using var identity = JsonDocument.Parse(expected.IdentityJson);
        return entry.Kind == PilotEntryKind.File && entry.Size == expected.Size &&
               string.Equals(entry.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(entry.Revision,
                   identity.RootElement.GetProperty("revision").GetString(), StringComparison.Ordinal);
    }

    // The immutable part of a cross-pair Dropbox membership: the scoped stable identity and the kind. Content
    // is deliberately excluded so the outbound native move can be recognised after remote contents changed.
    private static bool RemoteIdentityKindMatches(PilotRemoteEntry entry,
        SyncAuthorityObjectVersion expected)
    {
        if (!string.Equals(entry.Id, StableDropboxIdentity(expected), StringComparison.Ordinal)) return false;
        return expected.ContentHash is null
            ? entry.Kind == PilotEntryKind.Directory
            : entry.Kind == PilotEntryKind.File;
    }

    private static void RequireRemoteSuccessor(PilotRemoteEntry predecessor, PilotRemoteEntry moved,
        string destinationPath)
    {
        if (!string.Equals(predecessor.Id, moved.Id, StringComparison.Ordinal) ||
            !SamePath(moved.RelativePath, destinationPath) || predecessor.Kind != moved.Kind)
            throw new DropboxEffectUncertainException(
                "The provider did not return the same object identity, destination and kind after the cross-root move.");
    }

    private static bool EntryMatchesVersion(PilotLocalEntry entry, SyncAuthorityObjectVersion version) =>
        entry.Kind == (version.ContentHash is null ? PilotEntryKind.Directory : PilotEntryKind.File) &&
        (entry.Kind == PilotEntryKind.Directory || entry.Size == version.Size &&
         string.Equals(entry.ContentHash, version.ContentHash, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesBaseline(SyncAuthorityObservedEntry entry,
        SyncAuthorityObjectVersion version, string path) => SamePath(entry.RelativePath, path) &&
                                                           entry.ObjectVersionId == version.ObjectVersionId;

    private static bool ObservedContentMatches(SyncAuthorityObservedEntry left,
        SyncAuthorityObservedEntry right) => left.ObjectKind == right.ObjectKind && left.Size == right.Size &&
                                             string.Equals(left.ContentHash, right.ContentHash,
                                                 StringComparison.OrdinalIgnoreCase);

    private static string StableLocalIdentity(SyncAuthorityObjectVersion version)
    {
        if (version.Side != "local") throw new InvalidDataException("The cross-pair identity is not local.");
        using var identity = JsonDocument.Parse(version.IdentityJson);
        return identity.RootElement.GetProperty("volumeSerial").GetString() + ":" +
               identity.RootElement.GetProperty("fileId").GetString();
    }

    private static string StableDropboxIdentity(SyncAuthorityObjectVersion version)
    {
        if (version.Side != "dropbox") throw new InvalidDataException("The cross-pair identity is not Dropbox.");
        using var identity = JsonDocument.Parse(version.IdentityJson);
        return identity.RootElement.GetProperty("fileId").GetString()
               ?? throw new InvalidDataException("The Dropbox object ID is absent.");
    }

    private static string? LocalIdentity(PilotLocalEntry entry) =>
        entry.VolumeSerialNumber is null || entry.FileId is null
            ? null
            : $"{entry.VolumeSerialNumber.Value:x8}:{entry.FileId.Value:x16}";

    private static string ObjectKind(PilotEntryKind kind) =>
        kind == PilotEntryKind.Directory ? "directory" : "file";

    private static bool SamePath(string left, string right) =>
        string.Equals(PilotPath.NormalizeRelative(left), PilotPath.NormalizeRelative(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsDescendant(string path, string parent) =>
        PilotPath.NormalizeRelative(path).StartsWith(PilotPath.NormalizeRelative(parent) + "/",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrDescendant(string path, string parent) =>
        SamePath(path, parent) || IsDescendant(path, parent);

    private static int Depth(string path) => PilotPath.NormalizeRelative(path).Count(item => item == '/');

    private void AddDirectorySubtreeMembers(string operationId, Candidate candidate,
        IReadOnlyList<SyncAuthoritySynchronizationPoint> baselines,
        IReadOnlyList<SyncAuthorityObservedEntry> sourceLocal,
        IReadOnlyList<SyncAuthorityObservedEntry> sourceDropbox,
        IReadOnlyList<SyncAuthorityObservedEntry> destinationLocal,
        IReadOnlyList<SyncAuthorityObservedEntry> destinationDropbox,
        string sourceLocalRoot, string sourceDropboxRoot,
        string destinationLocalRoot, string destinationDropboxRoot,
        List<SyncAuthorityMember> members, HashSet<string> memberIds)
    {
        var claims = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void Claim(string path, string rootPath, string objectId)
        {
            var suffix = SubtreeSuffix(path, rootPath);
            if (!claims.TryGetValue(suffix, out var objects))
            {
                objects = new HashSet<string>(StringComparer.Ordinal);
                claims[suffix] = objects;
            }
            objects.Add(objectId);
        }

        foreach (var point in baselines.Where(item =>
                     item.LocalMembershipId is not null && item.DropboxMembershipId is not null &&
                     item.LocalObjectVersionId is not null && item.DropboxObjectVersionId is not null &&
                     item.LocalRelativePath is not null && item.DropboxRelativePath is not null &&
                     IsSameOrDescendant(item.LocalRelativePath, candidate.SourcePath) &&
                     IsSameOrDescendant(item.DropboxRelativePath, candidate.SourcePath)))
        {
            AddUnique(members, memberIds, BaselineMember(operationId, "source", sourcePair.Id,
                point.ObjectId, sourceLocalRoot, point.LocalMembershipId!, point.LocalObjectVersionId!,
                point.LocalRelativePath!));
            AddUnique(members, memberIds, BaselineMember(operationId, "source", sourcePair.Id,
                point.ObjectId, sourceDropboxRoot, point.DropboxMembershipId!, point.DropboxObjectVersionId!,
                point.DropboxRelativePath!));
            AddUnique(members, memberIds, BaselineMember(operationId, "lock", sourcePair.Id,
                point.ObjectId, sourceLocalRoot, point.LocalMembershipId!, point.LocalObjectVersionId!,
                point.LocalRelativePath!));
            AddUnique(members, memberIds, BaselineMember(operationId, "lock", sourcePair.Id,
                point.ObjectId, sourceDropboxRoot, point.DropboxMembershipId!, point.DropboxObjectVersionId!,
                point.DropboxRelativePath!));
            Claim(point.LocalRelativePath!, candidate.SourcePath, point.ObjectId);
            Claim(point.DropboxRelativePath!, candidate.SourcePath, point.ObjectId);
        }

        void AddObserved(IReadOnlyList<SyncAuthorityObservedEntry> entries, string pairId,
            string rootBindingId, string subtreeRoot)
        {
            foreach (var entry in entries.Where(item => IsSameOrDescendant(item.RelativePath, subtreeRoot)))
            {
                AddUnique(members, memberIds, ObservedMember(operationId, "lock", pairId, entry,
                    rootBindingId));
                Claim(entry.RelativePath, subtreeRoot, entry.ObjectId);
            }
        }

        AddObserved(sourceLocal, sourcePair.Id, sourceLocalRoot, candidate.SourcePath);
        AddObserved(sourceDropbox, sourcePair.Id, sourceDropboxRoot, candidate.SourcePath);
        AddObserved(destinationLocal, destinationPair.Id, destinationLocalRoot, candidate.DestinationPath);
        AddObserved(destinationDropbox, destinationPair.Id, destinationDropboxRoot, candidate.DestinationPath);

        foreach (var claim in claims.OrderBy(item => Depth(JoinSubtreePath(candidate.SourcePath, item.Key)))
                     .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var objectId = claim.Value.OrderBy(item => item, StringComparer.Ordinal).FirstOrDefault() ??
                           candidate.Baseline.ObjectId;
            var sourcePath = JoinSubtreePath(candidate.SourcePath, claim.Key);
            var destinationPath = JoinSubtreePath(candidate.DestinationPath, claim.Key);
            EnsurePathReservation(sourcePair.Id, sourceLocalRoot, sourcePath, objectId);
            EnsurePathReservation(sourcePair.Id, sourceDropboxRoot, sourcePath, objectId);
            EnsurePathReservation(destinationPair.Id, destinationLocalRoot, destinationPath, objectId);
            EnsurePathReservation(destinationPair.Id, destinationDropboxRoot, destinationPath, objectId);
        }

        void EnsurePathReservation(string pairId, string rootBindingId, string path, string objectId)
        {
            if (members.Any(item => string.Equals(item.RootBindingId, rootBindingId, StringComparison.Ordinal) &&
                                    item.ExpectedPathKey is not null && SamePath(item.ExpectedPathKey, path)))
                return;
            AddUnique(members, memberIds,
                AbsentMember(operationId, pairId, objectId, "affected", rootBindingId, path));
        }
    }

    private static string SubtreeSuffix(string path, string rootPath)
    {
        var normalized = PilotPath.NormalizeRelative(path);
        var root = PilotPath.NormalizeRelative(rootPath);
        if (SamePath(normalized, root)) return string.Empty;
        var prefix = root + "/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A retained cross-pair member is outside its directory subtree.");
        return normalized[prefix.Length..];
    }

    private static string JoinSubtreePath(string rootPath, string suffix) =>
        string.IsNullOrWhiteSpace(suffix)
            ? PilotPath.NormalizeRelative(rootPath)
            : PilotPath.NormalizeRelative(rootPath) + "/" + PilotPath.NormalizeRelative(suffix);

    private static void AddUnique(List<SyncAuthorityMember> members, HashSet<string> memberIds,
        SyncAuthorityMember member)
    {
        if (memberIds.Add(member.MemberId)) members.Add(member);
    }

    private static SyncAuthorityMember BaselineMember(string operationId, string role, string pairId,
        string objectId, string rootId, string membershipId, string versionId, string path) =>
        new(Id("member", operationId, "baseline", role, rootId, objectId, path), pairId, objectId, role,
            rootId, membershipId, versionId,
            null, path, JsonSerializer.Serialize(new { present = true, path, versionId, membershipId }));

    private static SyncAuthorityMember ObservedMember(string operationId, string role, string pairId,
        SyncAuthorityObservedEntry entry, string rootId) =>
        new(Id("member", operationId, "observed", role, rootId, entry.ObjectId, entry.RelativePath), pairId,
            entry.ObjectId, role, rootId, entry.MembershipId,
            entry.ObjectVersionId, entry.MembershipGeneration, entry.RelativePath,
            JsonSerializer.Serialize(new
            {
                present = true,
                entry.RelativePath,
                entry.ObjectVersionId,
                entry.MembershipId,
                entry.MembershipGeneration,
                entry.ContentHash,
                entry.Size
            }));

    private static SyncAuthorityMember AbsentMember(string operationId, string pairId, string objectId,
        string role, string rootId, string path) =>
        new(Id("member", operationId, "absent", role, rootId, objectId, path), pairId, objectId, role,
            rootId, null, null, null, path, JsonSerializer.Serialize(new { present = false, path }));

    private static string Format(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];
    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();

    private sealed record IdentityEntry(SyncAuthorityObservedEntry Entry, string Identity);
    private sealed record Baseline(SyncAuthorityObjectVersion Local, SyncAuthorityObjectVersion Dropbox);
    private sealed record RetainedSourceBinding(string ObjectId, SyncAuthorityMember LocalMember,
        SyncAuthorityObjectVersion Local, SyncAuthorityMember DropboxMember,
        SyncAuthorityObjectVersion Dropbox);
    private sealed class CrossPairDestinationDivergenceException(string message) : IOException(message);
    internal sealed record DirectoryManifest(string Hash, long Size, int Entries);
    private sealed record Candidate(SyncAuthoritySynchronizationPoint Baseline,
        SyncAuthorityObservedEntry Destination, SyncAuthorityObjectVersion BaselineLocal,
        SyncAuthorityObjectVersion BaselineDropbox, SyncAuthorityCrossPairMoveDirection Direction,
        PilotEntryKind Kind, SyncAuthorityObservedEntry? DestinationOccupant)
    {
        public string SourcePath => Baseline.LocalRelativePath!;
        public string DestinationPath => Destination.RelativePath;
    }

    private enum ReconcileState
    {
        NotApplied,
        Applied,
        CrossVolumeCopyNeedsRetirement,
        Ambiguous
    }
}
