using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

internal sealed class SyncAuthorityLocalControlNamespaceAllocator(SyncAuthorityDatabase authority)
{
    internal static Action<string>? BoundaryForTest { get; set; }

    /// <summary>
    /// The one guarded private control-namespace allocation. <paramref name="selectedRoot"/> is the REAL
    /// physical allocation boundary: the held validated local directory lease the control namespace is created
    /// beside - the product state anchor for a cloud/cloud pair, and each existing local-pair caller's own real
    /// local root. <paramref name="localRootBindingId"/> is NOT that boundary and is not the pair identity; it is
    /// the exact retained authority or lock reference of the operation member, journalled unchanged into
    /// operation_member.root_binding_id so the allocation is attributed to a real retained root binding rather
    /// than to a caller label. A caller must therefore pass the retained binding id of the pair and logical
    /// side it is allocating for, never a pair id, a remote recovery authority or a synthesized placeholder.
    /// </summary>
    /// <summary>root723: a prepared-but-unpublished control allocation plus the LIVE fenced lease that prepared
    /// it. The caller must complete the operation under this lease and then release it.</summary>
    public sealed record SyncPreparedControlPublication(SyncLocalControlNamespaceBinding Binding,
        SyncAuthorityLeaseRenewal Renewal, SyncAuthorityCommandAdmission Admission);

    [SupportedOSPlatform("windows")]
    public async Task<SyncLocalControlNamespaceBinding> BindAsync(PilotDirectoryLease selectedRoot,
        string installationId, string pairId, string localRootBindingId, long configurationRevision,
        string capabilityProfileId, CancellationToken cancellationToken = default, string? namespaceId = null,
        string side = "local", bool deferActivePublication = false, SyncPair? partitionCandidate = null,
        SyncAuthorityCommandAdmission? admission = null)
        => (await BindCoreAsync(selectedRoot, installationId, pairId, localRootBindingId, configurationRevision,
            capabilityProfileId, cancellationToken, namespaceId, side, deferActivePublication,
            keepRenewal: false, partitionCandidate, admission)).Binding;

    /// <summary>root723: prepare the control ownership WITHOUT publishing it and WITHOUT releasing the fence, so a
    /// published same-root edit can publish that ownership and complete the prepared operation under the same live
    /// lease inside its own transaction.</summary>
    [SupportedOSPlatform("windows")]
    public async Task<SyncPreparedControlPublication> PrepareForPublicationAsync(PilotDirectoryLease selectedRoot,
        string installationId, string pairId, string localRootBindingId, long configurationRevision,
        string capabilityProfileId, CancellationToken cancellationToken = default, string? namespaceId = null,
        string side = "local", SyncAuthorityCommandAdmission? admission = null)
    {
        var prepared = await BindCoreAsync(selectedRoot, installationId, pairId, localRootBindingId,
            configurationRevision, capabilityProfileId, cancellationToken, namespaceId, side,
            deferActivePublication: true, keepRenewal: true, partitionCandidate: null, admission);
        return new SyncPreparedControlPublication(prepared.Binding,
            prepared.Renewal ?? throw new InvalidOperationException(
                "A deferred control preparation must retain its live lease."),
            prepared.Admission ?? throw new InvalidOperationException("Prepared publication has no accepted command."));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task<(SyncLocalControlNamespaceBinding Binding, SyncAuthorityLeaseRenewal? Renewal, SyncAuthorityCommandAdmission? Admission)> BindCoreAsync(
        PilotDirectoryLease selectedRoot, string installationId, string pairId, string localRootBindingId,
        long configurationRevision, string capabilityProfileId, CancellationToken cancellationToken,
        string? namespaceId, string side, bool deferActivePublication, bool keepRenewal,
        SyncPair? partitionCandidate, SyncAuthorityCommandAdmission? admission)
    {
        ArgumentNullException.ThrowIfNull(selectedRoot);
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        if (partitionCandidate is not null && (partitionCandidate.Id != pairId ||
            partitionCandidate.PolicyRevision != configurationRevision))
            throw new InvalidDataException("Control partition candidate must be this exact pair and policy revision.");
        if (partitionCandidate is not null)
        {
            var endpoints = SyncCloudPairConfiguration.SavedEndpoints(partitionCandidate)
                ?? throw new InvalidDataException("Control partition candidate has no verified endpoint projection.");
            var endpoint = SyncEndpointContext.CompatibilityPort(endpoints.Left.Kind, endpoints.Right.Kind,
                SyncPairSide.Left) == side ? endpoints.Left : endpoints.Right;
            if (!endpoint.IsLocal || endpoint.LocalIdentity is null ||
                endpoint.LocalIdentity.VolumeSerialNumber != selectedRoot.Identity.VolumeSerialNumber ||
                endpoint.LocalIdentity.RootFileId != selectedRoot.Identity.FileId ||
                !string.Equals(PilotPath.NormalizeLocalRoot(endpoint.LocalPath!),
                    PilotPath.NormalizeLocalRoot(selectedRoot.Path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Control partition candidate does not represent the held native folder and side.");
        }
        var exactNamespace = SyncControlNamespace.NormalizeNamespaceId(namespaceId ?? pairId);
        var plan = SyncControlNamespace.PlanLocalAllocation(selectedRoot, installationId, exactNamespace);
        var objectId = Id("object-control", pairId, plan.ParentIdentityText, plan.DeterministicName);
        var idempotencyKey = Id("idempotency-control",
            Id("operation-control", pairId, plan.ParentIdentityText, plan.DeterministicName));
        var registration = await authority.PrepareControlActionAsync(idempotencyKey, cancellationToken);
        var operationId = registration.OperationId;
        var allocationId = Id("namespace-allocation", operationId);
        var memberId = Id("member-control", operationId, localRootBindingId);
        var preconditions = JsonSerializer.Serialize(new
        {
            plan.SelectedRootPath,
            selectedRootIdentity = IdentityJson(plan.SelectedRootIdentity),
            plan.ParentPath,
            plan.ParentIdentityText,
            plan.DeterministicName,
            plan.OwnerMarkerHash
        });

        // R683: run the authenticated retained-control reopen BEFORE any new allocation registration, so a
        // collided retained operation never fires CreateOperationAsync first. A proof that belongs to a
        // different caller authority refuses; no proof at all continues the unchanged ordinary path below.
        var guardedReopen = await authority.TryReopenRecoveredControlLocalAsync(pairId, side, selectedRoot,
            cancellationToken, installationId, localRootBindingId, exactNamespace);
        if (guardedReopen is not null) return (guardedReopen, null, null);

        var member = new SyncAuthorityMember(memberId, pairId, objectId, "lock", localRootBindingId,
            null, null, null, null, preconditions);
        // A rebuild must not turn publication of an already-created, verified control folder into a
        // second creation. Preserve its original capability authority only after proving the exact
        // retained effect and member. CreateOperationAsync below checks the immutable operation header.
        capabilityProfileId = await VerifiedPublicationProfileAsync(selectedRoot, plan, operationId,
            allocationId, side, member, capabilityProfileId, cancellationToken);
        SyncAuthorityOperationSnapshot? retainedOperation = null;
        try { retainedOperation = await authority.LoadOperationAsync(operationId, cancellationToken); }
        catch (InvalidDataException error) when (error.Message == "The operation is not retained.") { }
        if (retainedOperation is not null && (retainedOperation.Kind != SyncAuthorityOperationKind.AllocateControl ||
            retainedOperation.PairId != pairId || retainedOperation.ObjectId != objectId ||
            retainedOperation.ConfigurationRevision != configurationRevision ||
            retainedOperation.IdempotencyKey != idempotencyKey ||
            retainedOperation.PreconditionsJson != preconditions))
            throw new InvalidDataException("Control allocation replay differs from its exact retained authority.");
        if (retainedOperation?.Status == SyncAuthorityOperationStatus.Completed)
            return (await RequireCompletedBindingAsync(selectedRoot, plan, cancellationToken), null, null);
        if (retainedOperation?.Status is SyncAuthorityOperationStatus.Blocked or SyncAuthorityOperationStatus.Cancelled or
            SyncAuthorityOperationStatus.Superseded)
            throw new InvalidOperationException("The retained local control-namespace allocation is not runnable.");
        await authority.RegisterObjectAsync(objectId, "directory", cancellationToken);
        var retainedAdmission = await authority.LoadCurrentOperationCommandAdmissionAsync(operationId, cancellationToken);
        // ADM02-A4 setup exception: after the exact root, side, pair and configuration validation above and the
        // authenticated recovered and completed-binding reopen checks, this genuine setup allocation owns exactly ONE
        // internal accepted reconcile command whose immutable payload binds the installation, pair, side, retained root
        // binding, held native identity, configuration revision, namespace and the single allowed operation kind. It
        // cannot authorize any other operation kind or scope, and it never invents an ordinary sync run.
        var setupAdmission = admission ?? retainedAdmission ?? await authority.CreateAcceptedCommandAdmissionAsync(null, "reconcile", pairId,
            localRootBindingId, null,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                installationId,
                pairId,
                side,
                operationId,
                localRootBindingId,
                nativeIdentity = plan.ParentIdentityText,
                configurationRevision,
                namespaceId = exactNamespace,
                allowedOperationKind = "AllocateControl"
            }), SyncAuthorityCommandPurpose.ControlAllocation, cancellationToken);
        var ownsSetupCommand = setupAdmission.Purpose == SyncAuthorityCommandPurpose.ControlAllocation;
        if (ownsSetupCommand)
            await authority.TransitionCommandRequestAsync(setupAdmission.CommandRequestId, "executing",
                cancellationToken: cancellationToken);
        await authority.CreateOperationAsync(new SyncAuthorityOperationPlan(operationId,
            idempotencyKey, SyncAuthorityOperationKind.AllocateControl, pairId, objectId,
            configurationRevision, capabilityProfileId, preconditions, 1000,
            [
                // The member's root reference is the retained root binding this allocation is attributed to.
                member
            ], [], []), setupAdmission, cancellationToken, reservation: registration.Reservation);

        if (retainedOperation is not null && retainedAdmission?.CommandRequestId != setupAdmission.CommandRequestId)
            await authority.AuthorizeRetainedOperationsAsync(setupAdmission, [operationId], cancellationToken);
        var snapshot = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (snapshot.Status == SyncAuthorityOperationStatus.Completed)
            return (await RequireCompletedBindingAsync(selectedRoot, plan, cancellationToken), null, null);
        if (snapshot.Status is SyncAuthorityOperationStatus.Blocked or SyncAuthorityOperationStatus.Cancelled or
            SyncAuthorityOperationStatus.Superseded)
        {
            // LC653-3/RC639-4: the ordinary local authoring consumer reopens the AUTHENTICATED recovered
            // ownership BEFORE the allocator fallback, so a candidate whose only unresolved work is the exact
            // retained-control recovery keeps its owned namespace reopenable instead of being refused. The
            // recovered binding is the existing scoped reader; nothing is created, adopted or reallocated here.
            var recovered = await authority.TryReopenRecoveredControlLocalAsync(pairId, side, selectedRoot,
                cancellationToken, installationId, localRootBindingId, exactNamespace);
            if (recovered is not null) return (recovered, null, null);
            throw new InvalidOperationException("The retained local control-namespace allocation is not runnable.");
        }

        var callerToken = cancellationToken;
        // root723 diagnostic: names the preparation step that failed, so an FK or guard refusal identifies itself.
        var prepareStep = "enter";
        var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId,
            Id("worker-control", installationId), TimeSpan.FromSeconds(30), cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var handedOff = false;
        var completed = false;
        try
        {
            prepareStep = "allocation";
            var allocation = await TryLoadAllocationAsync(allocationId, cancellationToken);
            if (allocation is null)
            {
                await authority.AuthorizeNamespaceAllocationAsync(new SyncAuthorityNamespaceAllocation(allocationId,
                    operationId, side, plan.ParentIdentityText, plan.DeterministicName, plan.OwnerMarkerHash),
                    lease, cancellationToken);
                BoundaryForTest?.Invoke("allocation-authorized-before-filesystem-effect");
                allocation = await authority.LoadNamespaceAllocationAsync(allocationId, cancellationToken);
            }

            snapshot = await authority.LoadOperationAsync(operationId, cancellationToken);
            var pending = RequireAtMostOnePendingAttempt(snapshot);
            SyncLocalControlNamespaceBinding? existing = null;
            Exception? inspectionFailure = null;
            try
            {
                existing = await SyncControlNamespace.InspectAuthorizedLocalAsync(selectedRoot, plan);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                inspectionFailure = exception;
            }

            if (inspectionFailure is not null &&
                allocation.Status == SyncAuthorityAllocationStatus.Created &&
                !string.IsNullOrWhiteSpace(allocation.BoundIdentity) && pending is not null)
            {
                try
                {
                    existing = await SyncControlNamespace.ResumeCreatedLocalAsync(selectedRoot, plan,
                        ParseIdentity(allocation.BoundIdentity));
                    if (existing is not null) inspectionFailure = null;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    inspectionFailure = exception;
                }
            }

            if (inspectionFailure is not null)
            {
                await BlockCollisionAsync(snapshot, pending, allocation, lease, inspectionFailure.Message,
                    cancellationToken);
                throw new IOException("The local control-namespace allocation collided with an unowned object.",
                    inspectionFailure);
            }

            if (existing is not null)
            {
                if (allocation.Status == SyncAuthorityAllocationStatus.Creating &&
                    string.IsNullOrWhiteSpace(allocation.BoundIdentity) || pending is null &&
                    snapshot.Status != SyncAuthorityOperationStatus.Committed)
                {
                    await BlockCollisionAsync(snapshot, pending, allocation, lease,
                        "A pre-existing owner marker has no retained current-candidate creation identity.",
                        cancellationToken);
                    throw new IOException(
                        "A pre-existing private staging root cannot be adopted without exact retained creation authority.");
                }
                await FinalizeAllocationAsync(pairId, exactNamespace, side, allocation, existing, lease, pending,
                    cancellationToken);
                if (!deferActivePublication)
                    await PublishActiveBindingAsync(installationId, pairId, exactNamespace, side, allocation, existing, lease,
                        cancellationToken, partitionCandidate);
                completed = !deferActivePublication;
                handedOff = keepRenewal;
                return (existing, keepRenewal ? leaseRenewal : null, setupAdmission);
            }

            if (snapshot.Status == SyncAuthorityOperationStatus.Committed ||
                allocation.Status is SyncAuthorityAllocationStatus.Created or SyncAuthorityAllocationStatus.Bound or
                    SyncAuthorityAllocationStatus.Verified)
            {
                await BlockCollisionAsync(snapshot, pending, allocation, lease,
                    "The retained control-namespace identity is no longer present at its authorized path.",
                    cancellationToken);
                throw new IOException("The retained private staging identity disappeared before completion.");
            }

            if (pending is not null)
            {
                if (pending.Status == SyncAuthorityAttemptStatus.ResponseReceived)
                {
                    await BlockCollisionAsync(snapshot, pending, allocation, lease,
                        "A definite control-namespace response no longer has a verifiable destination object.",
                        cancellationToken);
                    throw new IOException("The definite private staging result is no longer verifiable.");
                }
                await RecordKnownNotAppliedAsync(pending, plan, lease, cancellationToken);
                snapshot = await authority.LoadOperationAsync(operationId, cancellationToken);
                pending = RequireAtMostOnePendingAttempt(snapshot);
                if (pending is not null)
                    throw new InvalidOperationException("The prior control allocation attempt did not reconcile terminally.");
                await leaseRenewal.ReleaseAsync();
                cancellationToken.ThrowIfCancellationRequested();
                await leaseRenewal.DisposeAsync();
                leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId,
                    Id("worker-control", installationId), TimeSpan.FromSeconds(30), callerToken);
                lease = leaseRenewal.Lease;
                cancellationToken = leaseRenewal.WorkToken;
                snapshot = await authority.LoadOperationAsync(operationId, cancellationToken);
                if (snapshot.Status != SyncAuthorityOperationStatus.Leased)
                    throw new InvalidOperationException(
                        "A verified-not-applied control allocation did not return through a fresh leased boundary.");
            }

            allocation = await authority.LoadNamespaceAllocationAsync(allocationId, cancellationToken);
            if (allocation.Status == SyncAuthorityAllocationStatus.Authorized)
            {
                await authority.TransitionNamespaceAllocationAsync(allocationId, lease,
                    SyncAuthorityAllocationStatus.Creating, cancellationToken: cancellationToken);
                allocation = await authority.LoadNamespaceAllocationAsync(allocationId, cancellationToken);
            }
            if (allocation.Status != SyncAuthorityAllocationStatus.Creating)
                throw new InvalidOperationException("The local control allocation is not at a create-safe boundary.");

            var requestFingerprint = EvidenceDigest("fingerprint-control", operationId, plan.OwnerMarkerHash,
                plan.ParentIdentityText);
            prepareStep = "attempt";
            pending = await authority.StartDispatchedAttemptAsync(operationId, lease, requestFingerprint,
                "local-control-directory-create", cancellationToken);
            BoundaryForTest?.Invoke("attempt-retained-before-filesystem-effect");
            prepareStep = "bind";
            var binding = await SyncControlNamespace.BindAuthorizedLocalAsync(selectedRoot, plan,
                expectedExistingUnmarkedIdentity: null,
                async identity =>
                {
                    await authority.TransitionNamespaceAllocationAsync(allocationId, lease,
                        SyncAuthorityAllocationStatus.Created, IdentityJson(identity), cancellationToken);
                    BoundaryForTest?.Invoke("directory-identity-retained-before-owner-marker");
                });
            allocation = await authority.LoadNamespaceAllocationAsync(allocationId, cancellationToken);
            prepareStep = "finalize";
            await FinalizeAllocationAsync(pairId, exactNamespace, side, allocation, binding, lease, pending,
                cancellationToken);
            if (!deferActivePublication)
                await PublishActiveBindingAsync(installationId, pairId, exactNamespace, side, allocation, binding, lease,
                    cancellationToken, partitionCandidate);
            completed = !deferActivePublication;
            handedOff = keepRenewal;
            return (binding, keepRenewal ? leaseRenewal : null, setupAdmission);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException("control-preparation-failed at " + prepareStep + ": " +
                error.Message, error);
        }
        finally
        {
            // root723: a deferred preparation keeps its live fence and hands the renewal to the caller, which
            // completes the prepared operation under it and then releases it.
            if (!handedOff)
            {
                try
                {
                    await leaseRenewal.ReleaseAsync();
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    // A cancelled caller leaves the short lease to expire; retained work is recovery-first.
                }
                finally
                {
                    await leaseRenewal.DisposeAsync();
                    if (ownsSetupCommand)
                        await authority.TransitionCommandRequestAsync(setupAdmission.CommandRequestId,
                            completed ? "completed" : "blocked", cancellationToken: CancellationToken.None);
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<SyncLocalControlNamespaceBinding> RequireCompletedBindingAsync(
        PilotDirectoryLease selectedRoot, SyncLocalControlNamespaceAllocationPlan plan,
        CancellationToken cancellationToken)
    {
        var binding = await SyncControlNamespace.InspectAuthorizedLocalAsync(selectedRoot, plan);
        cancellationToken.ThrowIfCancellationRequested();
        return binding ?? throw new IOException(
            "The completed control-namespace allocation no longer resolves to its exact owned directory.");
    }

    /// <summary>
    /// CC4A-3: the one READ-ONLY reopen of an already completed private control-namespace allocation, for
    /// ordinary preparation of an explicit cloud/cloud pair. It requires the exact completed retained
    /// allocation and verifies its identity against the exact anchor, pair and namespace; it never creates,
    /// adopts, reallocates, repairs or replaces anything. A missing, moved, replaced, foreign or unbound
    /// allocation refuses here and leaves the ordinary prepare with no staging identity at all. Only the
    /// separate binding action may create the instance, through the existing BindAsync once.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<SyncLocalControlNamespaceBinding> InspectBoundAsync(PilotDirectoryLease anchor,
        string pairId, CancellationToken cancellationToken = default, string? namespaceId = null,
        string side = "local")
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairId);
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        var installationId = await authority.LoadInstallationIdAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The private staging anchor carries no retained installation identity, so no completed " +
                "control-namespace allocation can be reopened.");
        var exactNamespace = SyncControlNamespace.NormalizeNamespaceId(namespaceId ?? pairId);
        var plan = SyncControlNamespace.PlanLocalAllocation(anchor, installationId, exactNamespace);
        if (!Directory.Exists(plan.ControlPath))
            throw new InvalidOperationException(
                "This pair has no private staging allocation at its exact anchored path. Ordinary " +
                "preparation never creates, adopts or reallocates one.");
        var idempotencyKey = Id("idempotency-control",
            Id("operation-control", pairId, plan.ParentIdentityText, plan.DeterministicName));
        var operationId = await authority.FindRetainedActionIdAsync(idempotencyKey, cancellationToken)
            ?? throw new InvalidOperationException("This exact private staging object has no retained allocation authority.");
        SyncAuthorityOperationSnapshot snapshot;
        try
        {
            snapshot = await authority.LoadOperationAsync(operationId, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException(
                "The private staging object at this pair's anchored path has no retained allocation " +
                "operation, so it is a foreign object rather than this pair's completed binding.", exception);
        }
        if (snapshot.Status != SyncAuthorityOperationStatus.Completed)
        {
            // RC639-5: an unpublished candidate whose only unresolved work is the exact authenticated
            // retained-control recovery keeps its owned namespace reopenable for ordinary preparation.
            var recovered = await authority.TryReopenRecoveredControlLocalAsync(pairId, side, anchor,
                cancellationToken);
            if (recovered is not null) return recovered;
            throw new InvalidOperationException(
                "The retained private staging allocation is at " + SyncAuthorityNames.OperationStatus(snapshot.Status) +
                " rather than a completed binding. Ordinary preparation neither resumes nor recreates it.");
        }
        return await RequireCompletedBindingAsync(anchor, plan, cancellationToken);
    }

    private async Task FinalizeAllocationAsync(string pairId, string namespaceId, string side, SyncAuthorityNamespaceAllocation allocation,
        SyncLocalControlNamespaceBinding binding, SyncAuthorityLease lease, SyncAuthorityAttempt? pending,
        CancellationToken cancellationToken)
    {
        var identityJson = IdentityJson(new PilotDirectoryIdentity(binding.RootVolumeSerialNumber,
            binding.RootFileId));
        if (allocation.Status == SyncAuthorityAllocationStatus.Created)
        {
            await authority.TransitionNamespaceAllocationAsync(allocation.NamespaceAllocationId, lease,
                SyncAuthorityAllocationStatus.Bound, identityJson, cancellationToken);
            allocation = await authority.LoadNamespaceAllocationAsync(allocation.NamespaceAllocationId,
                cancellationToken);
        }
        if (allocation.Status == SyncAuthorityAllocationStatus.Bound)
        {
            await authority.TransitionNamespaceAllocationAsync(allocation.NamespaceAllocationId, lease,
                SyncAuthorityAllocationStatus.Verified, cancellationToken: cancellationToken);
            allocation = await authority.LoadNamespaceAllocationAsync(allocation.NamespaceAllocationId,
                cancellationToken);
        }
        if (allocation.Status != SyncAuthorityAllocationStatus.Verified ||
            !string.Equals(allocation.BoundIdentity, identityJson, StringComparison.Ordinal))
            throw new InvalidOperationException("The local control directory lacks a verified exact allocation binding.");

        if (pending is not null)
        {
            if (pending.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.EffectUnknown)
                await authority.RecordReceiptAsync(pending.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownSuccess,
                    JsonSerializer.Serialize(new { allocation.ParentIdentity }), identityJson,
                    JsonSerializer.Serialize(new { effect = "local-control-directory-create", binding.MarkerFileId }),
                EvidenceDigest("known-success", identityJson, binding.MarkerFileId, binding.MarkerContentHash),
                    cancellationToken);
            await authority.ReconcileAttemptAsync(pending.AttemptId, lease,
                SyncAuthorityAttemptStatus.VerifiedSuccess,
                JsonSerializer.Serialize(new { result = "verified-control-namespace", identityJson }),
                cancellationToken);
        }
    }

    /// <summary>root723: the ACTIVE publication of a prepared allocation - control_namespace, the pair binding,
    /// the local_control root binding and the operation completion. Split out so the published same-root edit can
    /// publish this ownership (and terminalise the operation) inside its own atomic transaction.</summary>
    private async Task PublishActiveBindingAsync(string installationId, string pairId, string namespaceId, string side,
        SyncAuthorityNamespaceAllocation allocation, SyncLocalControlNamespaceBinding binding,
        SyncAuthorityLease lease, CancellationToken cancellationToken, SyncPair? partitionCandidate)
    {
        var identityJson = IdentityJson(new PilotDirectoryIdentity(binding.RootVolumeSerialNumber,
            binding.RootFileId));
        await authority.RegisterControlNamespaceAsync(new SyncAuthorityControlNamespace(namespaceId,
            allocation.NamespaceAllocationId, side, identityJson,
            JsonSerializer.Serialize(new { fileIdentity = binding.MarkerFileId }), binding.MarkerContentHash,
            "active"), cancellationToken);
        await authority.BindPairControlNamespaceAsync(pairId, side, namespaceId, 1, cancellationToken);
        // Only the allocator's native-verified binding is represented as candidate ownership.
        // The shared row guard still requires an exact enclosing-pair exclusion.
        var owned = new SyncControlNamespaceBinding(installationId, namespaceId, binding);
        var candidate = partitionCandidate is null ? null : side == "local"
            ? partitionCandidate with { ControlNamespace = owned }
            : partitionCandidate with { RightControlNamespace = owned };
        await authority.RegisterRootBindingAsync(Id("root-local-control", pairId, identityJson), pairId,
            side + "_control", 1, identityJson, binding.RootPath, cancellationToken,
            partitionCandidate: candidate);

        var operation = await authority.LoadOperationAsync(lease.OperationId, cancellationToken);
        if (operation.Status == SyncAuthorityOperationStatus.Committed)
            await authority.CompleteOperationAsync(lease,
                JsonSerializer.Serialize(new { result = "local-control-namespace-active", pairId }),
                cancellationToken);
        else if (operation.Status != SyncAuthorityOperationStatus.Completed)
            throw new InvalidOperationException("The verified control allocation did not reach a committable state.");
    }

    private async Task RecordKnownNotAppliedAsync(SyncAuthorityAttempt pending,
        SyncLocalControlNamespaceAllocationPlan plan, SyncAuthorityLease lease,
        CancellationToken cancellationToken)
    {
        if (pending.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.EffectUnknown)
            await authority.RecordReceiptAsync(pending.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownNotApplied,
                JsonSerializer.Serialize(new { plan.ParentIdentityText }), "{}",
                JsonSerializer.Serialize(new { effect = "local-control-directory-create", observed = "absent" }),
                EvidenceDigest("known-not-applied", plan.ParentIdentityText, plan.ControlPath), cancellationToken);
        await authority.ReconcileAttemptAsync(pending.AttemptId, lease,
            SyncAuthorityAttemptStatus.VerifiedNotApplied,
            JsonSerializer.Serialize(new { result = "verified-not-applied", plan.ControlPath }), cancellationToken);
    }

    private async Task BlockCollisionAsync(SyncAuthorityOperationSnapshot snapshot,
        SyncAuthorityAttempt? pending, SyncAuthorityNamespaceAllocation allocation, SyncAuthorityLease lease,
        string reason, CancellationToken cancellationToken)
    {
        if (pending is null && snapshot.Status is SyncAuthorityOperationStatus.Ready or
            SyncAuthorityOperationStatus.Leased)
            pending = await authority.StartDispatchedAttemptAsync(snapshot.OperationId, lease,
                EvidenceDigest("fingerprint-control-blocked", snapshot.OperationId, reason),
                "local-control-directory-precondition", cancellationToken);
        if (allocation.Status is SyncAuthorityAllocationStatus.Authorized or
            SyncAuthorityAllocationStatus.Creating or SyncAuthorityAllocationStatus.Created or
            SyncAuthorityAllocationStatus.Bound)
            await authority.TransitionNamespaceAllocationAsync(allocation.NamespaceAllocationId, lease,
                SyncAuthorityAllocationStatus.Blocked, cancellationToken: cancellationToken);
        if (pending is null) return;
        if (pending.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.EffectUnknown)
            await authority.RecordReceiptAsync(pending.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownConflict, "{}", "{}",
                JsonSerializer.Serialize(new { effect = "local-control-directory-create", reason }),
                EvidenceDigest("blocked", reason), cancellationToken);
        await authority.ReconcileAttemptAsync(pending.AttemptId, lease, SyncAuthorityAttemptStatus.Blocked,
            JsonSerializer.Serialize(new { result = "blocked", reason }), cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private async Task<string> VerifiedPublicationProfileAsync(PilotDirectoryLease selectedRoot,
        SyncLocalControlNamespaceAllocationPlan plan, string operationId, string allocationId,
        string side, SyncAuthorityMember expectedMember, string requestedProfile, CancellationToken cancellationToken)
    {
        SyncAuthorityOperationSnapshot retained;
        try { retained = await authority.LoadOperationAsync(operationId, cancellationToken); }
        catch (InvalidDataException error) when (error.Message == "The operation is not retained.")
        { return requestedProfile; }
        // Generic operation replay validates its header, not its children. The allocator must bind
        // its ONE retained lock member explicitly, including the exact root reference and state.
        if (retained.Members.Count != 1 || retained.Members[0] != expectedMember ||
            retained.Dependencies.Count != 0 || retained.Allocations.Count != 0 ||
            retained.PlanningAuthorities.Count != 0 || retained.SubtreeReservations.Count != 0)
            throw new InvalidDataException("Control publication resume has a different retained member authority.");
        if (retained.CapabilityProfileId == requestedProfile) return requestedProfile;
        if (retained.Kind != SyncAuthorityOperationKind.AllocateControl ||
            retained.Status is not (SyncAuthorityOperationStatus.Committed or SyncAuthorityOperationStatus.Completed))
            return requestedProfile; // The ordinary immutable-plan collision must remain a refusal.
        var allocation = await authority.LoadNamespaceAllocationAsync(allocationId, cancellationToken);
        if (allocation.OperationId != operationId || allocation.Side != side ||
            allocation.Status != SyncAuthorityAllocationStatus.Verified ||
            allocation.ParentIdentity != plan.ParentIdentityText ||
            allocation.DeterministicName != plan.DeterministicName ||
            allocation.OwnerNonceHash != plan.OwnerMarkerHash ||
            retained.Attempts.Count(attempt => attempt.Status == SyncAuthorityAttemptStatus.VerifiedSuccess) != 1 ||
            retained.Attempts.Any(attempt => attempt.Status is not
                (SyncAuthorityAttemptStatus.VerifiedSuccess or SyncAuthorityAttemptStatus.VerifiedNotApplied)))
            throw new InvalidDataException("Control publication resume lacks exact verified creation authority.");
        var success = retained.Attempts.Single(attempt => attempt.Status == SyncAuthorityAttemptStatus.VerifiedSuccess);
        var receipt = await authority.LoadLatestEffectReceiptAsync(operationId, success.AttemptId, cancellationToken);
        var binding = await SyncControlNamespace.InspectAuthorizedLocalAsync(selectedRoot, plan)
            ?? throw new InvalidDataException("The verified control folder is absent; publication cannot recreate it.");
        var identity = IdentityJson(new PilotDirectoryIdentity(binding.RootVolumeSerialNumber, binding.RootFileId));
        if (success.EffectKind != "local-control-directory-create" || receipt?.Certainty != "known_success" ||
            receipt.DestinationIdentityJson != identity || allocation.BoundIdentity != identity)
            throw new InvalidDataException("Control publication resume does not match its retained successful effect.");
        return retained.CapabilityProfileId;
    }

    private async Task<SyncAuthorityNamespaceAllocation?> TryLoadAllocationAsync(string allocationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await authority.LoadNamespaceAllocationAsync(allocationId, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static SyncAuthorityAttempt? RequireAtMostOnePendingAttempt(SyncAuthorityOperationSnapshot snapshot)
    {
        var pending = snapshot.Attempts.Where(attempt => attempt.Status is SyncAuthorityAttemptStatus.Started or
                SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
                SyncAuthorityAttemptStatus.EffectUnknown)
            .ToArray();
        if (pending.Length > 1)
            throw new InvalidDataException("The control allocation retains more than one unresolved attempt.");
        if (pending.Any(attempt => attempt.Status == SyncAuthorityAttemptStatus.Started))
            throw new InvalidDataException("The control allocation retains an unsafe pre-dispatch attempt gap.");
        return pending.SingleOrDefault();
    }

    private static string IdentityJson(PilotDirectoryIdentity identity) => JsonSerializer.Serialize(new
    {
        volumeSerial = identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
        fileId = identity.FileId.ToString("x16", CultureInfo.InvariantCulture)
    });

    private static PilotDirectoryIdentity ParseIdentity(string identityJson)
    {
        using var document = JsonDocument.Parse(identityJson);
        var root = document.RootElement;
        var volume = root.GetProperty("volumeSerial").GetString();
        var fileId = root.GetProperty("fileId").GetString();
        if (!uint.TryParse(volume, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedVolume) ||
            parsedVolume == 0 ||
            !ulong.TryParse(fileId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedFileId) ||
            parsedFileId == 0)
            throw new InvalidDataException("The retained control-directory identity is malformed.");
        return new PilotDirectoryIdentity(parsedVolume, parsedFileId);
    }

    private static string EvidenceDigest(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))))
            .ToLowerInvariant();

    private static string Id(string role, params string[] parts) => role + "-" +
        EvidenceDigest(new[] { role }.Concat(parts).ToArray())[..32];
}
