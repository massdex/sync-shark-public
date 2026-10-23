using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

// CC2C: the provider-neutral cloud root registration and cloud identity-validation helpers.
//
// This partial reuses the existing private write/insertion helpers, so the legacy physical root guard, the
// legacy Dropbox cloud guard and the new typed cloud guard all share one insertion path and no caller can
// bypass an overlap guard through a public switch. The legacy methods keep their exact behaviour; the guard
// extraction only makes the guarded insert reusable.
public sealed partial class SyncAuthorityDatabase
{
    // Read-only, exact retained join for cloud Recovery consumers. A descriptor or a UI item is
    // never sufficient authority: its original artifact, operation, allocation, control namespace
    // and active endpoint root must still agree. This creates no registration or provider request.
    internal async Task<(SyncAuthorityAllocation Allocation, SyncAuthorityCloudRootIdentity Endpoint)>
        LoadCloudRecoveryConsumerBindingAsync(SyncRecoveryArtifactProjection artifact,
            SyncAuthorityCloudRecoveryDescriptor descriptor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.allocation_id,a.control_namespace_id,a.resource_kind,a.deterministic_name,
                   a.intended_parent_identity,a.owner_nonce_hash,a.status,a.bound_identity,a.content_hash,
                   a.size_bytes,r.identity_json,r.display_path
            FROM recovery_artifact ra
            JOIN operation o ON o.operation_id=ra.operation_id AND o.object_id=ra.object_id
            JOIN resource_allocation a ON a.allocation_id=ra.allocation_id AND a.operation_id=o.operation_id
            JOIN pair_control_namespace b ON b.pair_id=o.pair_id AND b.control_namespace_id=a.control_namespace_id
                AND b.status='active'
            JOIN control_namespace c ON c.control_namespace_id=b.control_namespace_id AND c.status='active'
            JOIN root_binding r ON r.root_binding_id=$root AND r.pair_id=o.pair_id
                AND r.side=$side AND r.state='active'
            WHERE ra.recovery_artifact_id=$id AND o.pair_id=$pair AND o.operation_id=$operation
                AND ra.identity_json=$identity AND ra.content_hash=$hash AND ra.size_bytes=$size
                AND ra.side='dropbox' AND ra.status=$status;
            """;
        foreach (var (key, value) in new (string, object)[]
        {
            ("$root", descriptor.EndpointRootBindingId), ("$side", descriptor.LogicalSide),
            ("$id", artifact.RecoveryArtifactId), ("$pair", artifact.PairId),
            ("$operation", artifact.OperationId), ("$identity", artifact.IdentityJson),
            ("$hash", artifact.ContentHash), ("$size", artifact.Size), ("$status", artifact.Status)
        }) command.Parameters.AddWithValue(key, value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("The retained Recovery copy no longer has its exact operation, allocation and endpoint binding.");
        var allocation = new SyncAuthorityAllocation(reader.GetString(0), reader.GetString(1),
            SyncAuthorityNames.AllocationKind(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), AllocationStatus(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetInt64(9));
        if (!SyncAuthorityCloudRootPersistence.TryRead(reader.GetString(10), out var provider,
                out var accountId, out var namespaceId, out var folderId))
            throw new InvalidDataException("The retained Recovery endpoint is not an explicit cloud root.");
        var endpoint = new SyncAuthorityCloudRootIdentity(provider!, accountId, namespaceId!, folderId!,
            reader.GetString(11), descriptor.EndpointRootBindingId,
            SyncAuthorityCloudRootIdentity.PairRootAuthority, descriptor.LogicalSide);
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("The retained Recovery binding is ambiguous.");
        if (allocation.Kind != SyncAuthorityAllocationKind.RemoteRecoveryFile ||
            allocation.Status != SyncAuthorityAllocationStatus.Verified ||
            allocation.IntendedParentIdentity != descriptor.RecoveryRootBindingId ||
            allocation.BoundIdentity != descriptor.RetainedFileId ||
            allocation.Size != descriptor.Size || artifact.Size != descriptor.Size ||
            !string.Equals(allocation.ContentHash, descriptor.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifact.ContentHash, descriptor.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            endpoint.Provider != descriptor.Provider || endpoint.ProviderAccountId != descriptor.ProviderAccountId ||
            descriptor.RecoveryProviderAccountId != descriptor.ProviderAccountId ||
            !SyncEndpointProviders.TryGet(descriptor.Provider, out var definition) ||
            definition!.ContentHashAlgorithm != descriptor.NativeAlgorithm ||
            string.IsNullOrWhiteSpace(descriptor.RetainedRevision) ||
            string.IsNullOrWhiteSpace(descriptor.ContentHash))
            throw new InvalidDataException("The Recovery descriptor disagrees with its original allocation or provider identity.");
        var expectedPath = definition.PathsDescribeAncestry
            ? SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(descriptor.RecoveryPath).TrimEnd('/')
              + "/" + allocation.DeterministicName
            : allocation.DeterministicName;
        if (!string.Equals(expectedPath, descriptor.RetainedPath, StringComparison.Ordinal))
            throw new InvalidDataException("The Recovery member is not the exact allocated child of its retained root.");
        return (allocation, endpoint);
    }

    /// <summary>
    /// CC4E-2: the ONE narrow manual cloud-restore commit. It admits exactly one fenced Reconcile operation
    /// that is the executing accepted restore_recovery command's own operation, whose immutable preconditions
    /// and affected member bind the exact recovery artifact and destination path, whose dispatched attempt
    /// holds a durable known-success receipt, and whose recorded destination readback is exactly the caller's
    /// independently verified successor identity. It then transitions that one attempt and that one operation
    /// to their committed boundary and appends the history event. It fabricates no synchronization point, no
    /// membership and no version row, claims no counterpart convergence, and the existing auxiliary commit
    /// admission is deliberately NOT widened to every Reconcile operation. No schema change is involved.
    /// </summary>
    /// <summary>
    /// CC4E correction: the ONE narrow abandonment of a manual cloud restore that provably dispatched NO
    /// attempt at all. A pre-dispatch failure (an interrupted retained read, a cancellation before the request,
    /// or a refusal proven before any effect) must not leave a leased operation that permanently blocks the
    /// pair's further preparation. This takes the EXISTING leased-to-superseded terminal transition the schema
    /// already allows, and only when every precondition below holds: the exact fenced Reconcile restore
    /// operation bound to its accepted command, artifact and destination; ZERO operation attempts of any kind
    /// (so nothing external was ever dispatched and no recovery copy was ever issued); no remote recovery
    /// allocation advanced past its fresh Authorized state; no private staging allocation left mid-flight; and
    /// the caller's exact private cleanup outcome with zero residuals. An operation with ANY attempt row is
    /// refused here and stays unresolved. It fabricates no attempt, changes no schema or trigger, and appends
    /// exactly one no-effect history event.
    /// </summary>
    public async Task SupersedeNoEffectManualRestoreAsync(string operationId, SyncAuthorityLease lease,
        string commandRequestId, string recoveryArtifactId, string destinationPath, bool cleaned,
        bool stageRemoved, bool transactionRemoved, int residualCount, string detailJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        foreach (var value in new[] { operationId, commandRequestId, recoveryArtifactId })
            RequireIdentifier(value, nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (residualCount < 0) throw new ArgumentOutOfRangeException(nameof(residualCount));
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var bound = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM operation o
                WHERE o.operation_id=$operation AND o.kind='reconcile' AND o.status='leased'
                  AND o.object_id IS NOT NULL
                  AND json_extract(o.preconditions_json,'$.action')='restore_recovery'
                  AND json_extract(o.preconditions_json,'$.restoreKind')='cloud-recovery-restore'
                  AND json_extract(o.preconditions_json,'$.restoreCommandId')=$command
                  AND json_extract(o.preconditions_json,'$.recoveryArtifactId')=$artifact
                  AND json_extract(o.preconditions_json,'$.path')=$path;
                """, cancellationToken, ("$operation", operationId), ("$command", commandRequestId),
                ("$artifact", recoveryArtifactId), ("$path", destinationPath));
            if (bound != 1)
                throw new InvalidOperationException(
                    "Only the exact fenced leased manual restore operation bound to its accepted command, artifact and destination can be abandoned as a no-effect refusal.");
            var attempts = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation_attempt WHERE operation_id=$id;", cancellationToken,
                ("$id", operationId));
            if (attempts != 0)
                throw new InvalidOperationException(
                    "This manual restore dispatched attempt(s); an unresolved or externally dispatched request is never abandoned as a no-effect refusal and stays unresolved.");
            if (!cleaned || residualCount != 0)
                throw new InvalidOperationException(
                    "The exact private cleanup of this manual restore is not proven (cleaned and zero residuals are required), so it is retained rather than abandoned.");
            if (stageRemoved != transactionRemoved)
                throw new InvalidOperationException(
                    "The private cleanup outcome of this manual restore is not exact: its owned stage and its own transaction are removed together or not at all.");
            var advancedRecovery = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM resource_allocation WHERE operation_id=$id AND resource_kind='remote_recovery_file' AND status<>'authorized';",
                cancellationToken, ("$id", operationId));
            if (advancedRecovery != 0)
                throw new InvalidOperationException(
                    "This manual restore advanced a remote recovery allocation past its fresh Authorized state, so the destination predecessor may already be retained; it is never abandoned as a no-effect refusal.");
            var midFlight = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM resource_allocation WHERE operation_id=$id AND resource_kind IN ('transaction_directory','stage_file') AND status IN ('creating','created','bound');",
                cancellationToken, ("$id", operationId));
            if (midFlight != 0)
                throw new InvalidOperationException(
                    "A private staging allocation of this manual restore is still mid-flight; its exact cleanup is not proven, so it is retained rather than abandoned.");
            var command = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM command_request c
                JOIN operation o ON o.operation_id=$operation
                WHERE c.command_request_id=$command AND c.command_kind='reconcile'
                  AND c.status IN ('executing','blocked')
                  AND c.pair_id=o.pair_id AND c.target_id=$artifact
                  AND json_extract(c.payload_json,'$.action')='restore_recovery';
                """, cancellationToken, ("$operation", operationId), ("$command", commandRequestId),
                ("$artifact", recoveryArtifactId));
            if (command != 1)
                throw new InvalidOperationException(
                    "The manual restore no-effect abandonment requires the exact accepted restore_recovery command of this pair and artifact, still failed or executing and never completed.");
            var now = Utc(DateTimeOffset.UtcNow);
            var superseded = await ExecuteCountAsync(connection, transaction,
                "UPDATE operation SET status='superseded',terminal_utc=$utc,last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status='leased';",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            if (superseded != 1)
                throw new InvalidOperationException(
                    "The manual restore no-effect abandonment did not reach exactly one terminal transition.");
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "manual_restore_no_effect_superseded", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task CommitSuccessfulManualRestoreAttemptAsync(string attemptId, SyncAuthorityLease lease,
        string commandRequestId, string recoveryArtifactId, string destinationPath, string successorFileId,
        string successorRevision, long successorSize, string successorContentHash, string detailJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        foreach (var value in new[] { attemptId, commandRequestId, recoveryArtifactId })
            RequireIdentifier(value, nameof(attemptId));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        foreach (var value in new[] { successorFileId, successorRevision, successorContentHash })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (successorSize < 0) throw new ArgumentOutOfRangeException(nameof(successorSize));
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var admissible = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM operation o
                WHERE o.operation_id=$operation AND o.kind='reconcile' AND o.object_id IS NOT NULL
                  AND o.status IN ('effect_issuing','effect_unknown','effect_observed','reconciling','verifying')
                  AND json_extract(o.preconditions_json,'$.action')='restore_recovery'
                  AND json_extract(o.preconditions_json,'$.restoreKind')='cloud-recovery-restore'
                  AND json_extract(o.preconditions_json,'$.restoreCommandId')=$command
                  AND json_extract(o.preconditions_json,'$.recoveryArtifactId')=$artifact
                  AND json_extract(o.preconditions_json,'$.path')=$path;
                """, cancellationToken, ("$operation", operationId), ("$command", commandRequestId),
                ("$artifact", recoveryArtifactId), ("$path", destinationPath));
            if (admissible != 1)
                throw new InvalidOperationException(
                    "The manual restore commit requires this exact fenced Reconcile operation bound to its executing restore command, artifact and destination.");
            // The command must be the accepted restore_recovery command of this pair and this exact recovery
            // artifact while it is still executing. Its own recorded preview target is deliberately NOT compared
            // here: a restore-as-copy re-derives its create-only destination at effect time, so the immutable
            // target binding is the operation's own preconditions and affected member, checked above and below.
            var command = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM command_request c
                JOIN operation o ON o.operation_id=$operation
                WHERE c.command_request_id=$command AND c.command_kind='reconcile' AND c.status='executing'
                  AND c.pair_id=o.pair_id AND c.target_id=$artifact
                  AND json_extract(c.payload_json,'$.action')='restore_recovery';
                """, cancellationToken, ("$operation", operationId), ("$command", commandRequestId),
                ("$artifact", recoveryArtifactId));
            if (command != 1)
                throw new InvalidOperationException(
                    "The manual restore commit requires the exact executing accepted restore_recovery command of this operation's pair and recovery artifact.");
            var targetBound = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM operation_member m
                JOIN operation o ON o.operation_id=m.operation_id
                WHERE m.operation_id=$operation AND m.role='affected' AND m.expected_path_key=$path
                  AND m.object_id=o.object_id
                  AND m.root_binding_id=json_extract(o.preconditions_json,'$.endpointRootBindingId');
                """, cancellationToken, ("$operation", operationId), ("$path", destinationPath));
            if (targetBound != 1)
                throw new InvalidOperationException(
                    "The manual restore commit requires the operation's own affected member to bind this exact destination path under the named endpoint root.");
            var receipt = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM effect_receipt r
                WHERE r.attempt_id=$attempt AND r.certainty='known_success'
                  AND json_extract(r.destination_identity_json,'$.path')=$path
                  AND json_extract(r.destination_identity_json,'$.Id')=$file
                  AND json_extract(r.destination_identity_json,'$.Revision')=$revision
                  AND json_extract(r.destination_identity_json,'$.Size')=$size
                  AND lower(json_extract(r.destination_identity_json,'$.ContentHash'))=lower($hash);
                """, cancellationToken, ("$attempt", attemptId), ("$path", destinationPath),
                ("$file", successorFileId), ("$revision", successorRevision), ("$size", successorSize),
                ("$hash", successorContentHash));
            if (receipt != 1)
                throw new InvalidOperationException(
                    "The manual restore commit requires a durable known-success receipt whose recorded destination readback is exactly the caller's independently verified successor.");
            var now = Utc(DateTimeOffset.UtcNow);
            var attempts = await ExecuteCountAsync(connection, transaction,
                "UPDATE operation_attempt SET status='verified_success',ended_utc=$utc,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status IN ('response_received','effect_unknown');",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", attemptId));
            if (attempts != 1)
                throw new InvalidOperationException(
                    "The manual restore attempt did not reach its exact verified-success transition.");
            var operations = await ExecuteCountAsync(connection, transaction,
                "UPDATE operation SET status='committed',last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status IN ('effect_observed','effect_unknown','reconciling','verifying');",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            if (operations != 1)
                throw new InvalidOperationException(
                    "The manual restore operation did not reach its exact committed transition.");
            await BindCommittedManualRestoreCoreAsync(connection, transaction, operationId, attemptId,
                commandRequestId, recoveryArtifactId, destinationPath, successorFileId,
                successorRevision, successorSize, successorContentHash, cancellationToken);
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "manual_restore_committed", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    /// <summary>The retained provider-account facts one cloud root or cloud observation is validated against.</summary>
    private sealed record CloudProviderAccountRow(string Provider, string ProviderAccountId, string AccountId,
        string RootNamespaceId, string HomeNamespaceId);

    /// <summary>
    /// B1-C5: the internal typed input of one unpublished cloud-pair candidate preparation. It binds the
    /// candidate pair, the FULL draft pair definition, both exact logical endpoint identities and the retained
    /// installation. There is no caller boolean, no hierarchy snapshot and no selection here.
    /// </summary>
    internal sealed record SyncAuthorityCloudPairCandidate(
        SyncPair Definition,
        long PolicyRevision,
        string InstallationId,
        string LeftRootBindingId,
        string LeftProvider,
        string LeftProviderAccountId,
        string LeftNamespaceId,
        string LeftFolderId,
        string LeftDisplayPath,
        string RightRootBindingId,
        string RightProvider,
        string RightProviderAccountId,
        string RightNamespaceId,
        string RightFolderId,
        string RightDisplayPath);

    /// <summary>B1-C5: the typed state of one prepared candidate, including the retained installation.</summary>
    internal sealed record SyncAuthorityCloudPairCandidateState(
        string PairId,
        long PolicyRevision,
        bool Replayed,
        string InstallationId,
        string LeftRootBindingId,
        string LeftIdentityJson,
        string LeftDisplayPath,
        string RightRootBindingId,
        string RightIdentityJson,
        string RightDisplayPath);

    /// <summary>
    /// B1-C3/B1-C4/B1-C5: prepares, or exactly replays, ONE unpublished cloud-pair candidate. The draft
    /// definition is validated with the shared cloud configuration and NormalizePair and must agree with every
    /// candidate field; an enabled or approved definition is refused; an existing candidate is checked for its
    /// exact blocked preimage BEFORE any write and a mismatch refuses without a write; only an absent candidate
    /// is retained, through the extracted existing configuration core and the one guarded insertion path. No old
    /// pair, active root, Recovery authority or filesystem object is touched.
    /// </summary>
    internal async Task<SyncAuthorityCloudPairCandidateState> PrepareCloudPairCandidateAsync(
        SyncAuthorityCloudPairCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(candidate.Definition);
        foreach (var value in new[]
                 {
                     candidate.InstallationId, candidate.LeftRootBindingId, candidate.LeftProvider,
                     candidate.LeftProviderAccountId, candidate.LeftNamespaceId, candidate.LeftFolderId,
                     candidate.RightRootBindingId, candidate.RightProvider, candidate.RightProviderAccountId,
                     candidate.RightNamespaceId, candidate.RightFolderId
                 })
            RequireIdentifier(value, nameof(candidate));
        if (candidate.PolicyRevision < 1) throw new ArgumentOutOfRangeException(nameof(candidate));
        var definition = SyncPairStore.NormalizePair(candidate.Definition);
        RequireDisabledUnapprovedCandidate(definition);
        if (!string.Equals(definition.Id, candidate.Definition.Id, StringComparison.Ordinal) ||
            definition.PolicyRevision != candidate.PolicyRevision)
            throw new InvalidDataException(
                "The prepared candidate revision does not match its own normalized definition revision.");
        if (string.Equals(candidate.LeftRootBindingId, candidate.RightRootBindingId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "A cloud-pair candidate needs two distinct root bindings; one root binding is never both sides.");
        var projection = SyncCloudPairConfiguration.RequireProjection(definition);
        RequireCandidateSideAgreement(candidate, projection.Left, SyncAuthorityCloudSides.LogicalLeft);
        RequireCandidateSideAgreement(candidate, projection.Right, SyncAuthorityCloudSides.LogicalRight);
        var left = ResolveCandidateEndpoint(candidate, SyncAuthorityCloudSides.LogicalLeft);
        var right = ResolveCandidateEndpoint(candidate, SyncAuthorityCloudSides.LogicalRight);
        if (string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.ProviderAccountId, right.ProviderAccountId, StringComparison.Ordinal) &&
            string.Equals(left.NamespaceId, right.NamespaceId, StringComparison.Ordinal) &&
            string.Equals(left.FolderId, right.FolderId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Both candidate sides name the same provider object; one stable identity is never both endpoints.");
        var leftProvider = RequiredCloudDefinition(left.Provider);
        var rightProvider = RequiredCloudDefinition(right.Provider);
        var leftIdentityJson = SyncAuthorityCloudRootPersistence.Serialize(leftProvider.ProviderId!,
            left.ProviderAccountId, left.NamespaceId, left.FolderId);
        var rightIdentityJson = SyncAuthorityCloudRootPersistence.Serialize(rightProvider.ProviderId!,
            right.ProviderAccountId, right.NamespaceId, right.FolderId);
        var leftPath = SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(left.DisplayPath);
        var rightPath = SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(right.DisplayPath);
        var pairId = definition.Id;
        var replayed = false;
        await WriteAsync(async (connection, transaction) =>
        {
            var foreign = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND (state='retired' OR state<>'retired' AND (pair_json IS NOT NULL OR selected<>0 OR configuration_revision<>$revision));",
                cancellationToken, ("$id", pairId), ("$revision", candidate.PolicyRevision));
            if (foreign != 0)
                throw new InvalidOperationException(
                    "The candidate pair is retired, published, selected or retained at another revision; it is refused rather than downgraded, resurrected or overwritten.");
            var retainedRows = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state<>'retired' AND pair_json IS NULL AND selected=0 AND configuration_revision=$revision;",
                cancellationToken, ("$id", pairId), ("$revision", candidate.PolicyRevision));
            var existingRoots = await CountCandidateRootsAsync(connection, transaction, pairId, cancellationToken);
            if (retainedRows == 1 || existingRoots != 0)
            {
                // B1-C3: an existing candidate is checked for its exact blocked preimage BEFORE any write, and a
                // mismatch refuses here with the row untouched. No active or out_of_scope root is ever downgraded.
                if (retainedRows != 1 || existingRoots != 2)
                    throw new InvalidOperationException(
                        "The candidate pair is partially retained; it is refused rather than completed, adopted or rewritten.");
                replayed = true;
                await RequireExactBlockedCandidateRootAsync(connection, transaction, candidate.LeftRootBindingId,
                    pairId, SyncAuthorityCloudSides.LogicalLeft, candidate.PolicyRevision, leftIdentityJson,
                    leftPath, cancellationToken);
                await RequireExactBlockedCandidateRootAsync(connection, transaction, candidate.RightRootBindingId,
                    pairId, SyncAuthorityCloudSides.LogicalRight, candidate.PolicyRevision, rightIdentityJson,
                    rightPath, cancellationToken);
            }
            else
            {
                await RegisterPairConfigurationCoreAsync(connection, transaction, pairId,
                    candidate.PolicyRevision, SyncAuthorityPairConfiguration.FromPair(definition), cancellationToken);
                await UpsertBlockedCandidateRootCoreAsync(connection, transaction, candidate.LeftRootBindingId,
                    pairId, SyncAuthorityCloudSides.LogicalLeft, candidate.PolicyRevision, leftIdentityJson,
                    leftPath, cancellationToken);
                await UpsertBlockedCandidateRootCoreAsync(connection, transaction, candidate.RightRootBindingId,
                    pairId, SyncAuthorityCloudSides.LogicalRight, candidate.PolicyRevision, rightIdentityJson,
                    rightPath, cancellationToken);
                await RequireExactBlockedCandidateRootAsync(connection, transaction, candidate.LeftRootBindingId,
                    pairId, SyncAuthorityCloudSides.LogicalLeft, candidate.PolicyRevision, leftIdentityJson,
                    leftPath, cancellationToken);
                await RequireExactBlockedCandidateRootAsync(connection, transaction, candidate.RightRootBindingId,
                    pairId, SyncAuthorityCloudSides.LogicalRight, candidate.PolicyRevision, rightIdentityJson,
                    rightPath, cancellationToken);
            }
            // Exact replay also binds the policy, not only the root identities and revision.
            await RegisterPairConfigurationCoreAsync(connection, transaction, pairId,
                candidate.PolicyRevision, SyncAuthorityPairConfiguration.FromPair(definition), cancellationToken);
            await RequireCandidateOperationsAsync(connection, transaction, pairId, cancellationToken);
            await RequireActiveCandidateAccountAsync(connection, transaction, candidate.InstallationId,
                leftProvider.ProviderId!, left.ProviderAccountId, left.NamespaceId, cancellationToken);
            await RequireActiveCandidateAccountAsync(connection, transaction, candidate.InstallationId,
                rightProvider.ProviderId!, right.ProviderAccountId, right.NamespaceId, cancellationToken);
        }, cancellationToken);
        return new SyncAuthorityCloudPairCandidateState(pairId, candidate.PolicyRevision, replayed,
            candidate.InstallationId, candidate.LeftRootBindingId, leftIdentityJson, leftPath,
            candidate.RightRootBindingId, rightIdentityJson, rightPath);
    }

    /// <summary>
    /// B1-C5: a new or identity-changed cloud pair is published disabled and unapproved for first-sync review,
    /// so an enabled or already approved definition is refused rather than silently published.
    /// </summary>
    private static void RequireDisabledUnapprovedCandidate(SyncPair definition)
    {
        if (definition.Enabled)
            throw new InvalidDataException(
                "The candidate pair definition is enabled; a new or identity-changed cloud pair is published disabled and is refused otherwise.");
        if (definition.InitialReviewApproved == true)
            throw new InvalidDataException(
                "The candidate pair definition is already approved for first-sync review; it is refused rather than silently published.");
    }

    private sealed record CandidateEndpoint(string Provider, string ProviderAccountId, string NamespaceId,
        string FolderId, string DisplayPath);

    private static CandidateEndpoint ResolveCandidateEndpoint(SyncAuthorityCloudPairCandidate candidate,
        string side)
    {
        var leftSide = string.Equals(side, SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal);
        return leftSide
            ? new CandidateEndpoint(candidate.LeftProvider, candidate.LeftProviderAccountId,
                candidate.LeftNamespaceId, candidate.LeftFolderId, candidate.LeftDisplayPath)
            : new CandidateEndpoint(candidate.RightProvider, candidate.RightProviderAccountId,
                candidate.RightNamespaceId, candidate.RightFolderId, candidate.RightDisplayPath);
    }

    /// <summary>
    /// B1-C5: the shared cloud projection of the draft definition must agree with the candidate's own fields on
    /// provider, namespace, folder and display path. Nothing is inferred from a connection id and no default
    /// account is invented.
    /// </summary>
    private static void RequireCandidateSideAgreement(SyncAuthorityCloudPairCandidate candidate,
        SyncEndpoint endpoint, string side)
    {
        var expected = ResolveCandidateEndpoint(candidate, side);
        var label = string.Equals(side, SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal)
            ? "left"
            : "right";
        var definition = SyncEndpointProviders.TryGet(expected.Provider, out var provider) &&
                         provider!.ProviderId is not null && provider.Kind != SyncEndpointKind.Local
            ? provider
            : throw new InvalidDataException(
                "The " + label + " candidate endpoint does not name a registered cloud provider.");
        if (endpoint.Kind != definition.Kind)
            throw new InvalidDataException(
                "The draft definition's " + label + " endpoint kind does not match the candidate endpoint.");
        if (!string.Equals(endpoint.NamespaceId, expected.NamespaceId, StringComparison.Ordinal) ||
            !string.Equals(endpoint.FolderId, expected.FolderId, StringComparison.Ordinal) ||
            !string.Equals(SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(endpoint.FolderPath ?? ""),
                SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(expected.DisplayPath),
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The draft definition's " + label +
                " endpoint does not agree with the candidate namespace, folder and display path.");
    }

    private static SyncEndpointProviderDefinition RequiredCloudDefinition(string provider)
    {
        if (!SyncEndpointProviders.TryGet(provider, out var definition) || definition!.ProviderId is null ||
            definition.Kind == SyncEndpointKind.Local)
            throw new ArgumentException(
                "This provider is not a registered cloud provider, so no candidate root may be retained.",
                nameof(provider));
        return definition;
    }

    private static async Task RequireActiveCandidateAccountAsync(SqliteConnection connection,
        SqliteTransaction transaction, string installationId, string provider, string providerAccountId,
        string namespaceId, CancellationToken cancellationToken)
    {
        var account = await LoadCloudProviderAccountAsync(connection, transaction, providerAccountId,
            installationId, cancellationToken) ?? throw new InvalidOperationException(
            "The candidate endpoint is not bound to an active provider account of this installation.");
        if (!string.Equals(account.Provider, provider, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained provider account does not belong to the candidate endpoint's provider.");
        if (!SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(provider, account.AccountId,
                account.RootNamespaceId, account.HomeNamespaceId, namespaceId))
            throw new InvalidOperationException(
                "The candidate endpoint namespace is not the retained namespace of its provider account.");
    }

    private static async Task<long> CountCandidateRootsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, CancellationToken cancellationToken) =>
        await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM root_binding WHERE pair_id=$pair;", cancellationToken, ("$pair", pairId));

    /// <summary>
    /// B1-C3: a candidate may carry only completed private-stage control allocations of the existing allocator;
    /// any other kind, any unresolved status and any unexpected operation refuses preparation.
    /// </summary>
    private static async Task RequireCandidateOperationsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, CancellationToken cancellationToken)
    {
        // RC639-4.INTEGRATION-1: the exact authenticated retained-control recovery non-creating collision
        // is the ONLY record excused here; every other unresolved or unexpected candidate operation refuses.
        var recoveryInstallation = await ScalarStringAsync(connection, transaction,
            "SELECT installation_id FROM installation ORDER BY installation_id LIMIT 1;", cancellationToken);
        var recoveredCollision = recoveryInstallation is null ? null
            : await ResolveRecoveredNonCreatingCollisionOperationIdAsync(connection, transaction, pairId,
                recoveryInstallation, cancellationToken);
        var unexpected = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM operation WHERE pair_id=$pair AND (status NOT IN ('completed','cancelled','superseded') OR kind<>'allocate_control') AND ($recovered IS NULL OR operation_id<>$recovered);",
            cancellationToken, ("$pair", pairId), ("$recovered", (object?)recoveredCollision ?? DBNull.Value));
        if (unexpected != 0)
            throw new InvalidOperationException(
                "The candidate pair carries an unresolved or unexpected operation; cloud-pair authoring is refused.");
    }

    /// <summary>The retained cloud root facts of one scan root, or null for a legacy physical/cloud root.</summary>
    private sealed record CloudScanRoot(string Provider, string ProviderAccountId, string NamespaceId,
        string FolderId);

    /// <summary>
    /// Registers one cloud root binding under a retained compatibility side tag. Before any write it
    /// validates the actual registered provider, the active provider account of this installation and the
    /// native endpoint namespace that account retains, and then compares the candidate's real cloud identity
    /// against every other active pair root and recovery boundary. Caller-supplied hierarchy snapshots are
    /// accepted only through this internal typed API, each bound to one exact retained root, provider,
    /// provider account, namespace and folder; a missing or unbound chain is refused rather than treated as
    /// disjoint. The Host/provider hierarchy producer remains a later packet and is not claimed here.
    /// </summary>
    public Task RegisterCloudEndpointRootBindingAsync(string rootBindingId, string pairId,
        string compatibilitySide, long generation, string installationId, string provider,
        string providerAccountId, string namespaceId, string folderId, string displayPath,
        CancellationToken cancellationToken = default,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? hierarchies = null) =>
        // The raw public API is strict: it never carries a candidate and therefore never waives an overlap.
        RegisterCloudEndpointRootBindingCoreAsync(rootBindingId, pairId, compatibilitySide, generation,
            installationId, provider, providerAccountId, namespaceId, folderId, displayPath, cancellationToken,
            hierarchies, null);

    /// <summary>
    /// The narrow internal candidate-bearing registration used only by the normalized Host/publication path.
    /// It is not a caller allow-overlap option: the candidate is bound inside the transaction to the exact
    /// pair id, configuration revision, policy and endpoint identity before any nested allowance is even
    /// considered.
    /// </summary>
    internal Task RegisterCloudEndpointRootBindingForCandidateAsync(string rootBindingId, string pairId,
        string compatibilitySide, long generation, string installationId, string provider,
        string providerAccountId, string namespaceId, string folderId, string displayPath,
        SyncPair partitionCandidate, CancellationToken cancellationToken = default,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? hierarchies = null) =>
        RegisterCloudEndpointRootBindingCoreAsync(rootBindingId, pairId, compatibilitySide, generation,
            installationId, provider, providerAccountId, namespaceId, folderId, displayPath, cancellationToken,
            hierarchies, partitionCandidate);

    /// <summary>
    /// R671: the ONE narrow atomic recovered publication for a local/cloud pair whose cloud endpoint is a typed
    /// cloud root. The pair configuration, the activated exact own blocked LOCAL root preimage and the typed
    /// cloud candidate root are written inside ONE transaction, using the same typed guard sequence the ordinary
    /// candidate registration uses, so a refused cloud side (missing pair, inactive/foreign account, mismatched
    /// namespace, missing or wrong-own hierarchy, or the typed overlap comparator) can never leave a committed
    /// local root or policy change. The cloud root keeps its typed identity; the Dropbox registrar is not used.
    /// </summary>
    internal async Task ActivateRecoveredLocalAndTypedCloudRootAsync(SyncPair pair,
        long expectedUnpublishedRevision, string installationId, string localRootBindingId, string volumeId,
        string volumeSerialHex, string localFileIdHex, string provider, string providerAccountId,
        string cloudRootBindingId, string cloudCompatibilitySide, string cloudNamespaceId, string cloudFolderId,
        string cloudDisplayPath, IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchies,
        SyncAuthorityPairConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hierarchies);
        foreach (var value in new[]
                 {
                     installationId, localRootBindingId, volumeId, volumeSerialHex, localFileIdHex, provider,
                     providerAccountId, cloudRootBindingId, cloudNamespaceId, cloudFolderId
                 })
            RequireIdentifier(value, nameof(localRootBindingId));
        SyncAuthorityCloudSides.RequireCompatibilityTag(cloudCompatibilitySide);
        if (expectedUnpublishedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedUnpublishedRevision));
        if (expectedUnpublishedRevision != NormalAuthoringRootGeneration)
            throw new InvalidDataException(
                "A fresh unpublished local/account candidate is retained at configuration revision 1 with root " +
                "generation 1 by normal authoring; the recovered activation refuses another revision rather than " +
                "assuming its root generation.");
        if (!string.Equals(localRootBindingId, SyncAuthorityRuntimeHost.LocalRecoveryRootId(pair.Id, volumeId, localFileIdHex), StringComparison.Ordinal))
            throw new InvalidDataException("The recovered local root id is not the pair's own normal-authoring local root identity.");
        if (!SyncEndpointProviders.TryGet(provider, out var definition) || definition!.ProviderId is null)
            throw new ArgumentException("This provider is not a registered cloud provider, so no cloud root is written.", nameof(provider));
        var providerLiteral = definition.ProviderId;
        var localPath = NormalizeAuthorityPath("local", pair.LocalRoot);
        var localIdentity = JsonSerializer.Serialize(new
        {
            volumeId,
            volumeSerial = volumeSerialHex,
            fileId = localFileIdHex
        });
        var cloudPath = SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(cloudDisplayPath);
        var cloudIdentityJson = SyncAuthorityCloudRootPersistence.Serialize(providerLiteral, providerAccountId,
            cloudNamespaceId, cloudFolderId);
        var cloudIdentity = new SyncAuthorityCloudRootIdentity(providerLiteral, providerAccountId,
            cloudNamespaceId, cloudFolderId, cloudPath, cloudRootBindingId,
            SyncAuthorityCloudRootIdentity.PairRootAuthority, cloudCompatibilitySide);
        var supplied = hierarchies.Select(item => item ?? throw new ArgumentException(
            "A supplied cloud hierarchy snapshot is null.", nameof(hierarchies))).ToArray();
        await WriteAsync(async (connection, transaction) =>
        {
            var candidate = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND state<>'retired' AND pair_json IS NULL AND selected=0 AND configuration_revision=$revision;",
                cancellationToken, ("$pair", pair.Id), ("$revision", expectedUnpublishedRevision));
            if (candidate != 1)
                throw new InvalidOperationException(
                    "The unpublished candidate pair is not retained at its exact current revision; recovered activation refuses.");
            await RequireExactOwnRootPreimageAsync(connection, transaction, localRootBindingId, pair.Id, "local",
                NormalAuthoringRootGeneration, localIdentity, localPath, cancellationToken);
            await RequireExactOwnRootPreimageAsync(connection, transaction, cloudRootBindingId, pair.Id,
                cloudCompatibilitySide, NormalAuthoringRootGeneration, cloudIdentityJson, cloudPath, cancellationToken);
            await RequireCompletedOrRecoveredCandidateControlAsync(connection, transaction, pair.Id,
                localRootBindingId, cancellationToken);
            await RegisterPairConfigurationCoreAsync(connection, transaction, pair.Id, expectedUnpublishedRevision,
                configuration, cancellationToken);
            await RegisterLocalRootBindingCoreAsync(connection, transaction, localRootBindingId, pair.Id,
                NormalAuthoringRootGeneration, installationId, volumeId, volumeSerialHex, localFileIdHex,
                localIdentity, localPath, cancellationToken, allowBlockedCandidatePreimage: true,
                partitionCandidate: pair);
            // The typed cloud guard sequence below is the SAME one the ordinary candidate registration uses. Any
            // refusal here rolls back the local activation and the policy registration above.
            var pairRetained = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND state<>'retired';",
                cancellationToken, ("$pair", pair.Id));
            if (pairRetained != 1)
                throw new InvalidDataException("The cloud root is not bound to a retained pair.");
            var account = await LoadCloudProviderAccountAsync(connection, transaction, providerAccountId,
                installationId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The cloud root is not bound to an active provider account of this installation.");
            if (!string.Equals(account.Provider, providerLiteral, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The retained provider account does not belong to this provider; the cloud root is refused.");
            if (!SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(providerLiteral, account.AccountId,
                    account.RootNamespaceId, account.HomeNamespaceId, cloudNamespaceId))
                throw new InvalidOperationException(
                    "The endpoint namespace is not the retained namespace of this provider account; the cloud root is refused.");
            await RequireCloudRootIdentityAvailableAsync(connection, transaction, cloudIdentity, supplied,
                cancellationToken, pair.Id, pair);
            await UpsertRootBindingCoreAsync(connection, transaction, cloudRootBindingId, pair.Id,
                cloudCompatibilitySide, NormalAuthoringRootGeneration, cloudIdentityJson, cloudPath,
                cancellationToken, allowBlockedCandidatePreimage: true);
        }, cancellationToken);
    }

    /// <summary>
    /// OV637-6 (): the ONE narrow internal, guarded, ATOMIC two-sided cloud-root
    /// registration for an unpublished cloud/cloud candidate. Both own exact root rows are provisioned inside
    /// this single WriteAsync transaction BEFORE the two unchanged typed guards run, so the unchanged
    /// ReadPairOpaqueProofsAsync can resolve both genuine DB-held identities for an opaque corresponding pair.
    /// Both guards must pass before commit; any validation, cancellation, ambiguity or second-side failure rolls
    /// BOTH rows back and nothing is partially durable. The public raw registration and the ordinary single-side
    /// candidate registration are unchanged and stay strict. No provider call happens inside this transaction,
    /// no proof trust is exported, and the typed comparator still owns Same/Nested/Disjoint/Unverified.
    /// </summary>
    internal sealed record SyncAuthorityCloudTwoSidedRoot(string RootBindingId, string CompatibilitySide,
        string Provider, string ProviderAccountId, string NamespaceId, string FolderId, string DisplayPath);

    internal async Task RegisterTwoSidedCandidateCloudRootsAsync(string pairId, long configurationRevision,
        string installationId, long generation, SyncAuthorityCloudTwoSidedRoot left,
        SyncAuthorityCloudTwoSidedRoot right, SyncPair partitionCandidate,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(partitionCandidate);
        ArgumentNullException.ThrowIfNull(hierarchies);
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(installationId, nameof(installationId));
        if (configurationRevision < 1) throw new ArgumentOutOfRangeException(nameof(configurationRevision));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (string.Equals(left.RootBindingId, right.RootBindingId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The two logical sides require two distinct retained authority root binding ids.");
        if (string.Equals(left.CompatibilitySide, right.CompatibilitySide, StringComparison.Ordinal))
            throw new InvalidDataException("The two logical sides require two distinct compatibility sides.");
        if (!string.Equals(left.CompatibilitySide, SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal) ||
            !string.Equals(right.CompatibilitySide, SyncAuthorityCloudSides.LogicalRight, StringComparison.Ordinal))
            throw new InvalidDataException(
                "This two-cloud entry point requires the logical-left identity as its left argument and the " +
                "logical-right identity as its right argument; a swapped or mislabelled side is refused before any write.");
        var leftIdentity = TwoSidedCandidateIdentity(left, out var leftIdentityJson);
        var rightIdentity = TwoSidedCandidateIdentity(right, out var rightIdentityJson);
        var supplied = hierarchies.Select(item => item ?? throw new ArgumentException(
            "A supplied cloud hierarchy snapshot is null.", nameof(hierarchies))).ToArray();
        await WriteAsync(async (connection, transaction) =>
        {
            var pairRetained = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND state<>'retired' " +
                "AND configuration_revision=$revision;",
                cancellationToken, ("$pair", pairId), ("$revision", configurationRevision));
            if (pairRetained != 1)
                throw new InvalidDataException(
                    "The cloud roots are not bound to this retained pair at this exact configuration revision.");
            await RequireTwoSidedAccountAsync(connection, transaction, installationId, leftIdentity,
                cancellationToken);
            await RequireTwoSidedAccountAsync(connection, transaction, installationId, rightIdentity,
                cancellationToken);
            await RequireTwoSidedPreimageAsync(connection, transaction, pairId, left.RootBindingId, cancellationToken);
            await RequireTwoSidedPreimageAsync(connection, transaction, pairId, right.RootBindingId, cancellationToken);
            // the two-sided operation must UNCONDITIONALLY bind the carried candidate to this
            // registration BEFORE it writes either row, regardless of any comparator relation. A row-level pair
            // id/revision match is not that binding: the carried definition, its immutable policy and BOTH own
            // typed endpoint identities must each be exactly bound here, or a foreign/stale carried definition
            // (an A/B saved pair with an incoming A/C registration) would otherwise commit unbound roots.
            if (!string.Equals(partitionCandidate.Id, pairId, StringComparison.Ordinal) ||
                partitionCandidate.PolicyRevision != configurationRevision)
                throw new InvalidDataException(
                    "The carried candidate pair is not this registration's exact pair at this exact configuration " +
                    "revision; the two-sided registration is refused rather than committing unbound roots.");
            if (!await CandidateExactlyBoundAsync(connection, transaction, partitionCandidate, pairId, leftIdentity,
                    cancellationToken) ||
                !await CandidateExactlyBoundAsync(connection, transaction, partitionCandidate, pairId, rightIdentity,
                    cancellationToken))
                throw new InvalidOperationException(
                    "The carried candidate pair is not exactly bound to BOTH of this registration's own logical " +
                    "cloud root identities at their exact ports; the two-sided registration is refused rather than " +
                    "committing unbound roots.");
            // each own logical side must carry exactly ONE complete provider-produced hierarchy bound to
            // its own exact identity before commit, even when no comparator runs. Missing, duplicate or wrong-own
            // evidence is refused rather than assumed disjoint.
            RequireTwoSidedOwnHierarchy(leftIdentity, supplied, "left");
            RequireTwoSidedOwnHierarchy(rightIdentity, supplied, "right");
            await UpsertRootBindingCoreAsync(connection, transaction, left.RootBindingId, pairId,
                left.CompatibilitySide, generation, leftIdentityJson,
                SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(left.DisplayPath), cancellationToken);
            await UpsertRootBindingCoreAsync(connection, transaction, right.RootBindingId, pairId,
                right.CompatibilitySide, generation, rightIdentityJson,
                SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(right.DisplayPath), cancellationToken);
            await RequireCloudRootIdentityAvailableAsync(connection, transaction, leftIdentity, supplied,
                cancellationToken, pairId, partitionCandidate);
            await RequireCloudRootIdentityAvailableAsync(connection, transaction, rightIdentity, supplied,
                cancellationToken, pairId, partitionCandidate);
        }, cancellationToken);
    }

    private static SyncAuthorityCloudRootIdentity TwoSidedCandidateIdentity(
        SyncAuthorityCloudTwoSidedRoot root, out string identityJson)
    {
        foreach (var value in new[]
                 {
                     root.RootBindingId, root.Provider, root.ProviderAccountId, root.NamespaceId, root.FolderId
                 })
            RequireIdentifier(value, nameof(root));
        SyncAuthorityCloudSides.RequireCompatibilityTag(root.CompatibilitySide);
        if (!SyncEndpointProviders.TryGet(root.Provider, out var definition) || definition!.ProviderId is null)
            throw new ArgumentException(
                "This provider is not a registered cloud provider, so no cloud root is written.", nameof(root));
        identityJson = SyncAuthorityCloudRootPersistence.Serialize(definition.ProviderId, root.ProviderAccountId,
            root.NamespaceId, root.FolderId);
        return new SyncAuthorityCloudRootIdentity(definition.ProviderId, root.ProviderAccountId, root.NamespaceId,
            root.FolderId, SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(root.DisplayPath),
            root.RootBindingId, SyncAuthorityCloudRootIdentity.PairRootAuthority, root.CompatibilitySide);
    }

    private static void RequireTwoSidedOwnHierarchy(SyncAuthorityCloudRootIdentity own,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchies, string sideLabel)
    {
        var bound = ExactCloudHierarchy(own, hierarchies);
        if (bound is null || !bound.IsCompleteFor(own))
            throw new InvalidDataException(
                "The " + sideLabel + " own cloud root of the two-sided registration requires exactly one complete " +
                "provider hierarchy bound to its exact identity before it commits; a missing, duplicate or unbound " +
                "own-side hierarchy is refused rather than assumed disjoint.");
    }

    private static async Task RequireTwoSidedAccountAsync(SqliteConnection connection,
        SqliteTransaction transaction, string installationId, SyncAuthorityCloudRootIdentity candidate,
        CancellationToken cancellationToken)
    {
        var account = await LoadCloudProviderAccountAsync(connection, transaction, candidate.ProviderAccountId!,
            installationId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The cloud root is not bound to an active provider account of this installation.");
        if (!string.Equals(account.Provider, candidate.Provider, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained provider account does not belong to this provider; the cloud root is refused.");
        if (!SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(candidate.Provider!,
                account.AccountId, account.RootNamespaceId, account.HomeNamespaceId, candidate.NamespaceId!))
            throw new InvalidOperationException(
                "The endpoint namespace is not the retained namespace of this provider account; the cloud root " +
                "is refused.");
    }

    private static async Task RequireTwoSidedPreimageAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, string rootBindingId, CancellationToken cancellationToken)
    {
        var owner = await ScalarStringAsync(connection, transaction,
            "SELECT pair_id FROM root_binding WHERE root_binding_id=$id;", cancellationToken,
            ("$id", rootBindingId));
        if (owner is not null && !string.Equals(owner, pairId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "That root binding id already belongs to another pair; the root is refused rather than adopted.");
    }
    private async Task RegisterCloudEndpointRootBindingCoreAsync(string rootBindingId, string pairId,
        string compatibilitySide, long generation, string installationId, string provider,
        string providerAccountId, string namespaceId, string folderId, string displayPath,
        CancellationToken cancellationToken = default,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? hierarchies = null,
        SyncPair? partitionCandidate = null)
    {
        foreach (var value in new[]
                 {
                     rootBindingId, pairId, installationId, provider, providerAccountId, namespaceId, folderId
                 })
            RequireIdentifier(value, nameof(rootBindingId));
        SyncAuthorityCloudSides.RequireCompatibilityTag(compatibilitySide);
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!SyncEndpointProviders.TryGet(provider, out var definition) || definition!.ProviderId is null)
            throw new ArgumentException(
                "This provider is not a registered cloud provider, so no cloud root is written.", nameof(provider));
        var providerLiteral = definition.ProviderId;
        var normalizedDisplayPath = SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(displayPath);
        var identityJson = SyncAuthorityCloudRootPersistence.Serialize(providerLiteral, providerAccountId,
            namespaceId, folderId);
        var candidate = new SyncAuthorityCloudRootIdentity(providerLiteral, providerAccountId, namespaceId,
            folderId, normalizedDisplayPath, rootBindingId, SyncAuthorityCloudRootIdentity.PairRootAuthority,
            compatibilitySide);
        var supplied = (hierarchies ?? Array.Empty<SyncAuthorityCloudHierarchySnapshot>())
            .Select(item => item ?? throw new ArgumentException(
                "A supplied cloud hierarchy snapshot is null.", nameof(hierarchies))).ToArray();
        await WriteAsync(async (connection, transaction) =>
        {
            var pairRetained = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND state<>'retired';",
                cancellationToken, ("$pair", pairId));
            if (pairRetained != 1)
                throw new InvalidDataException("The cloud root is not bound to a retained pair.");
            var account = await LoadCloudProviderAccountAsync(connection, transaction, providerAccountId,
                installationId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The cloud root is not bound to an active provider account of this installation.");
            if (!string.Equals(account.Provider, providerLiteral, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The retained provider account does not belong to this provider; the cloud root is refused.");
            if (!SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(providerLiteral, account.AccountId,
                    account.RootNamespaceId, account.HomeNamespaceId, namespaceId))
                throw new InvalidOperationException(
                    "The endpoint namespace is not the retained namespace of this provider account; the cloud root is refused.");
            await RequireCloudRootIdentityAvailableAsync(connection, transaction, candidate, supplied,
                cancellationToken, pairId, partitionCandidate);
            await UpsertRootBindingCoreAsync(connection, transaction, rootBindingId, pairId, compatibilitySide,
                generation, identityJson, normalizedDisplayPath, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// CC4A: the narrow internal read-only typed snapshot of every OTHER retained cloud authority of this
    /// installation - every active pair root and every active recovery boundary - taken over exactly the
    /// existing <see cref="LoadActiveCloudAuthoritiesAsync"/> logic that the transactional cloud-root
    /// registration already uses to recheck the full retained set. It returns only typed cloud root
    /// identities: no raw rows, no connection handle, no write, no adoption and no way to omit a retained
    /// root. A retained row whose provider account no longer agrees fails closed rather than being silently
    /// dropped from the comparison set.
    /// </summary>
    internal async Task<IReadOnlyList<SyncAuthorityCloudRootIdentity>> LoadRetainedCloudAuthoritiesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // The read runs outside any write transaction, so the existing retained-set query is reused exactly
        // as the registration calls it, with no transaction attached and no authority excluded.
        return await LoadActiveCloudAuthoritiesAsync(connection, null!, "-", cancellationToken);
    }

    // The one typed cloud guard. It compares the candidate's real cloud identity against every other active
    // pair root and every active recovery boundary, using native verified hierarchy input for a path-opaque
    // provider and same provider+namespace paths for a path-descriptive provider.
    private static async Task RequireCloudRootIdentityAvailableAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityCloudRootIdentity candidate,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchies, CancellationToken cancellationToken,
        string? registeringPairId = null, SyncPair? partitionCandidate = null,
        IReadOnlyList<SyncAuthorityCloudRootIdentity>? preparedAuthorities = null)
    {
        var retained = await LoadActiveCloudAuthoritiesAsync(connection, transaction, candidate.AuthorityId,
            cancellationToken);
        foreach (var snapshot in hierarchies)
        {
            if (snapshot.BindsTo(candidate)) continue;
            if (retained.Any(item => snapshot.BindsTo(item))) continue;
            if (preparedAuthorities?.Any(snapshot.BindsTo) == true) continue;
            throw new InvalidDataException(
                "A supplied cloud hierarchy snapshot is not bound to an exact retained cloud root or recovery boundary; it is refused rather than used as authority.");
        }
        var candidateHierarchy = ExactCloudHierarchy(candidate, hierarchies);
        var label = candidate.CompatibilitySide == SyncAuthorityCloudSides.LogicalLeft ? "left" : "right";
        foreach (var item in retained)
        {
            var itemHierarchy = ExactCloudHierarchy(item, hierarchies);
            // The typed comparison still owns Same/Unverified/provider-global alias evidence. Only a relation
            // it has already proven Nested may consult the shared partition policy, and only for the exact
            // bound candidate pair whose own endpoint agrees with this registration; recovery boundaries and
            // any unresolved, stale or foreign relation stay strict.
            var relation = SyncAuthorityCloudRootComparison.Compare(candidate, candidateHierarchy, item, itemHierarchy);
            if (relation == SyncAuthorityCloudRootRelation.Nested && partitionCandidate is not null &&
                registeringPairId is not null &&
                await CandidateExactlyBoundAsync(connection, transaction, partitionCandidate, registeringPairId,
                    candidate, cancellationToken) &&
                await NestedCloudPartitionAllowedAsync(connection, transaction, partitionCandidate, candidate,
                    item, hierarchies, cancellationToken, preparedAuthorities))
                continue;
            var validation = SyncAuthorityCloudRootComparison.Validate(candidate, candidateHierarchy, item,
                itemHierarchy, label);
            if (!validation.IsValid)
                throw new InvalidOperationException(validation.Describe());
        }
    }

    /// <summary>
    /// True only when the carried candidate pair is the exact pair being registered and its own endpoint for
    /// the registration's compatibility side names this exact provider, namespace and folder. A candidate for
    /// another pair, side or root is not bound and cannot waive the overlap.
    /// </summary>
    private static async Task<bool> CandidateExactlyBoundAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncPair candidatePair, string registeringPairId,
        SyncAuthorityCloudRootIdentity candidate, CancellationToken cancellationToken)
    {
        if (!string.Equals(candidatePair.Id, registeringPairId, StringComparison.Ordinal)) return false;
        // The carried candidate's own policy revision must be the exact configuration revision retained for
        // this pair at this registration; a foreign or stale candidate revision is not bound.
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT configuration_revision FROM sync_pair WHERE pair_id=$id AND state<>'retired';";
            command.Parameters.AddWithValue("$id", registeringPairId);
            var revision = await command.ExecuteScalarAsync(cancellationToken);
            if (revision is null || revision is DBNull ||
                Convert.ToInt64(revision, System.Globalization.CultureInfo.InvariantCulture) !=
                candidatePair.PolicyRevision) return false;
        }
        // The carried candidate's policy must be the exact retained immutable configuration for this
        // revision; the same id/revision with different deletion, retention or selection policy is not bound.
        if (!await CandidateConfigurationMatchesAsync(connection, transaction, registeringPairId,
                candidatePair.PolicyRevision, candidatePair, cancellationToken)) return false;
        // When a published pair_json already exists for this pair, the carried candidate's endpoint and its
        // partition-sensitive policy (selection rules, default inclusion, ignore rules) must agree with that
        // published definition. An initial preparation where pair_json is still NULL keeps the already
        // validated typed candidate plus the config/endpoint binding above; a published definition is never
        // silently overridden by a different carried policy.
        await using (var published = connection.CreateCommand())
        {
            published.Transaction = transaction;
            published.CommandText =
                "SELECT pair_json FROM sync_pair WHERE pair_id=$id AND state<>'retired';";
            published.Parameters.AddWithValue("$id", registeringPairId);
            var publishedJson = await published.ExecuteScalarAsync(cancellationToken) as string;
            if (!string.IsNullOrWhiteSpace(publishedJson))
            {
                var publishedPair = ReadRetainedPair(publishedJson!, registeringPairId);
                if (publishedPair is null) return false;
                if (publishedPair.SelectionDefaultIncluded != candidatePair.SelectionDefaultIncluded) return false;
                if (!SyncSelectionPolicy.Normalize(publishedPair.SelectionRules)
                        .SequenceEqual(SyncSelectionPolicy.Normalize(candidatePair.SelectionRules)))
                    return false;
                if (!SyncIgnorePolicy.Normalize(publishedPair.IgnoreRules)
                        .SequenceEqual(SyncIgnorePolicy.Normalize(candidatePair.IgnoreRules)))
                    return false;
                var publishedEndpoints = SyncCloudPairConfiguration.SavedEndpoints(publishedPair);
                if (publishedEndpoints is null) return false;
                var publishedEndpoint = CloudEndpointAtPort(publishedEndpoints, candidate.CompatibilitySide);
                if (publishedEndpoint is null || !CloudEndpointMatchesRoot(publishedPair, publishedEndpoint, candidate))
                    return false;
            }
        }
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(candidatePair);
        if (endpoints is null) return false;
        var endpoint = CloudEndpointAtPort(endpoints, candidate.CompatibilitySide);
        return endpoint is not null && CloudEndpointMatchesRoot(candidatePair, endpoint, candidate);
    }

    private static SyncEndpoint? CloudEndpointAtPort(SyncPairEndpoints endpoints, string port) =>
        new[] { (endpoints.Left, SyncPairSide.Left), (endpoints.Right, SyncPairSide.Right) }
            .Where(item => !item.Item1.IsLocal &&
                SyncEndpointContext.CompatibilityPort(endpoints.Left.Kind, endpoints.Right.Kind, item.Item2) == port)
            .Select(item => item.Item1).SingleOrDefault();

    private static bool CloudEndpointMatchesRoot(SyncPair pair, SyncEndpoint endpoint,
        SyncAuthorityCloudRootIdentity identity) =>
        !endpoint.IsLocal &&
        string.Equals(SyncEndpoints.ProviderLiteral(endpoint.Kind), identity.Provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(endpoint.FolderId, identity.FolderId, StringComparison.Ordinal) &&
        (string.Equals(endpoint.NamespaceId, identity.NamespaceId, StringComparison.Ordinal) ||
         pair.EndpointDocument is null && endpoint.NamespaceId is null && !string.IsNullOrWhiteSpace(identity.NamespaceId));

    private static SyncAuthorityCloudHierarchySnapshot? ExactCloudHierarchy(SyncAuthorityCloudRootIdentity identity,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchies)
    {
        var matches = hierarchies.Where(item => item.BindsTo(identity)).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException("More than one supplied hierarchy binds the same cloud authority; ambiguous evidence is refused.");
        return matches.SingleOrDefault();
    }

    private static async Task<bool> CandidateConfigurationMatchesAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, long revision, SyncPair candidatePair,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT deletion_policy,deletion_grace_seconds,required_absence_observations,conflict_policy,
                   metadata_policy,recursive_mode,automatic_pruning,recovery_retention_days
            FROM pair_config_revision WHERE pair_id=$id AND revision=$revision;
            """;
        command.Parameters.AddWithValue("$id", pairId);
        command.Parameters.AddWithValue("$revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return false;
        var expected = SyncAuthorityPairConfiguration.FromPair(candidatePair);
        long Number(int index) => Convert.ToInt64(reader.GetValue(index),
            System.Globalization.CultureInfo.InvariantCulture);
        return string.Equals(reader.GetString(0), expected.DeletionPolicy, StringComparison.Ordinal) &&
               Number(1) == Convert.ToInt64(expected.DeletionGraceSeconds) &&
               Number(2) == Convert.ToInt64(expected.RequiredAbsenceObservations) &&
               string.Equals(reader.GetString(3), expected.ConflictPolicy, StringComparison.Ordinal) &&
               string.Equals(reader.GetString(4), expected.MetadataPolicy, StringComparison.Ordinal) &&
               (Number(5) != 0) == expected.RecursiveMode &&
               (Number(6) != 0) == expected.AutomaticPruning &&
               Number(7) == Convert.ToInt64(expected.RecoveryRetentionDays);
    }

    /// <summary>
    /// The shared partition allowance for one already-proven Nested cloud relation. The retained pair is
    /// resolved from the EXACT retained root-binding row, so a recovery boundary (which has no pair root row)
    /// and any stale or unrepresented row fall back to the strict refusal.
    /// </summary>
    private static async Task<bool> NestedCloudPartitionAllowedAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncPair candidatePair, SyncAuthorityCloudRootIdentity candidateIdentity,
        SyncAuthorityCloudRootIdentity retainedIdentity,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchies, CancellationToken cancellationToken,
        IReadOnlyList<SyncAuthorityCloudRootIdentity>? preparedAuthorities = null)
    {
        // Only an active pair root may take the nested-partition allowance; a recovery boundary is never
        // waived and has no pair root row.
        if (!string.Equals(retainedIdentity.AuthorityKind,
                SyncAuthorityCloudRootIdentity.PairRootAuthority, StringComparison.Ordinal)) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT pair_id FROM root_binding WHERE root_binding_id=$id AND state IN ('active','blocked');";
        command.Parameters.AddWithValue("$id", retainedIdentity.AuthorityId);
        var retainedPairId = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(retainedPairId)) return false;
        var retainedPair = await LoadRetainedPairDefinitionAsync(connection, transaction, retainedPairId!,
            cancellationToken);
        if (retainedPair is null) return false;
        // The operation-local comparison copy supplies a document-less retained side's OWN already-verified
        // namespace, read in this same transaction, so an already-degraded enclosing pair is compared with the
        // facts it was published with. The raw retained definition still owns every revision and definition
        // check, and the copy never enters persistence or identity derivation.
        var retainedComparison = await ProjectRetainedPairForComparisonAsync(connection, transaction, retainedPair,
            cancellationToken);
        if (retainedComparison is null) return false;
        // The proof is built from the EXACT DB-held typed identities and the COMPLETE fresh supplied hierarchies,
        // never from a snapshot's own self-declared account/root. Exactly one snapshot must bind each identity and
        // carry a complete observed-name chain; the shared policy then decides the whole pair with evidence.
        var authorities = await LoadActiveCloudAuthoritiesAsync(connection, transaction, "-", cancellationToken);
        // Only atomic publication supplies these two exact DB-held own candidates, after its account,
        // configuration, blocked-root preimage and private-staging checks. The retained peer continues to
        // resolve active roots only; a caller snapshot or another pair's blocked root grants no authority.
        var candidateAuthorities = authorities.Concat(preparedAuthorities ??
            Array.Empty<SyncAuthorityCloudRootIdentity>()).Distinct().ToArray();
        var candidateProofs = await ReadPairOpaqueProofsAsync(connection, transaction, candidatePair,
            retainedComparison, candidateIdentity, candidateAuthorities, hierarchies, cancellationToken,
            preparedAuthorities);
        var retainedProofs = await ReadPairOpaqueProofsAsync(connection, transaction, retainedComparison,
            candidatePair, null, authorities, hierarchies, cancellationToken);
        if (candidateProofs is null || retainedProofs is null) return false;
        if (SyncPairPartitionPolicy.DecideWithHierarchy(candidatePair, candidateProofs, retainedComparison,
                retainedProofs) == SyncPairPartitionRelation.NestedAllowed) return true;
        // A corresponding INCLUDED cloud nesting is a distinct admitted relation that still requires a complete
        // provider-verified hierarchy proof on both endpoints; without that proof it stays refused.
        return SyncPairPartitionPolicy.DecideCorrespondingIncluded(candidatePair, retainedComparison,
            candidateProofs, retainedProofs) == SyncPairPartitionRelation.CorrespondingIncluded;
    }

    // Resolve every comparable opaque side through this pair's own retained root row. The registering side
    // alone may use the independently validated candidate identity. A snapshot never supplies an account,
    // namespace or root identity, and another side's account is never a fallback.
    private static async Task<IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>?> ReadPairOpaqueProofsAsync(
        SqliteConnection connection, SqliteTransaction transaction, SyncPair pair, SyncPair otherPair,
        SyncAuthorityCloudRootIdentity? registeringIdentity,
        IReadOnlyList<SyncAuthorityCloudRootIdentity> authorities,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchies, CancellationToken cancellationToken,
        IReadOnlyList<SyncAuthorityCloudRootIdentity>? preparedAuthorities = null)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
        var other = SyncCloudPairConfiguration.SavedEndpoints(otherPair);
        if (endpoints is null || other is null) return null;
        var proofs = new List<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>();
        foreach (var (endpoint, orderedSide) in new[]
                 { (endpoints.Left, SyncPairSide.Left), (endpoints.Right, SyncPairSide.Right) })
        {
            if (endpoint.IsLocal || SyncEndpointProviders.Get(endpoint.Kind).PathsDescribeAncestry ||
                !new[] { other.Left, other.Right }.Any(item => !item.IsLocal && item.Kind == endpoint.Kind)) continue;
            var port = SyncEndpointContext.CompatibilityPort(endpoints.Left.Kind, endpoints.Right.Kind, orderedSide);
            SyncAuthorityCloudRootIdentity? identity = null;
            if (registeringIdentity?.CompatibilitySide == port && CloudEndpointMatchesRoot(pair, endpoint, registeringIdentity))
                identity = registeringIdentity;
            else
            {
                var ownPrepared = registeringIdentity is null ? null : preparedAuthorities?.SingleOrDefault(item =>
                    item.CompatibilitySide == port && CloudEndpointMatchesRoot(pair, endpoint, item));
                await using var root = connection.CreateCommand();
                root.Transaction = transaction;
                root.CommandText = """
                    SELECT root_binding_id FROM root_binding WHERE pair_id=$pair AND side=$side
                      AND (state='active' OR (state='blocked' AND root_binding_id=$prepared));
                    """;
                root.Parameters.AddWithValue("$pair", pair.Id);
                root.Parameters.AddWithValue("$side", port);
                root.Parameters.AddWithValue("$prepared", (object?)ownPrepared?.AuthorityId ?? DBNull.Value);
                var ids = new List<string>();
                await using (var reader = await root.ExecuteReaderAsync(cancellationToken))
                    while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
                if (ids.Count != 1) return null;
                identity = authorities.SingleOrDefault(item => item.AuthorityKind == SyncAuthorityCloudRootIdentity.PairRootAuthority &&
                    item.AuthorityId == ids[0] && item.CompatibilitySide == port);
            }
            if (identity is null || !CloudEndpointMatchesRoot(pair, endpoint, identity) ||
                string.IsNullOrWhiteSpace(identity.ProviderAccountId)) return null;
            var snapshot = ExactCloudHierarchy(identity, hierarchies);
            if (snapshot is null || !snapshot.IsCompleteFor(identity)) return null;
            proofs.Add(new SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof(snapshot,
                identity.ProviderAccountId!, identity.AuthorityId, identity));
        }
        return proofs;
    }

    // Every other active pair root and active recovery boundary that can carry a comparable cloud identity.
    private static async Task<IReadOnlyList<SyncAuthorityCloudRootIdentity>> LoadActiveCloudAuthoritiesAsync(
        SqliteConnection connection, SqliteTransaction transaction, string excludedAuthorityId,
        CancellationToken cancellationToken)
    {
        var result = new List<SyncAuthorityCloudRootIdentity>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT r.root_binding_id,r.side,r.display_path,r.identity_json,
                       p.provider,p.provider_account_id,p.account_id,p.root_namespace_id,p.home_namespace_id
                FROM root_binding r
                LEFT JOIN provider_account p
                  ON p.provider_account_id=json_extract(r.identity_json,'$.providerAccountId')
                 AND p.status='active'
                WHERE r.state='active' AND r.root_binding_id<>$excluded;
                """;
            command.Parameters.AddWithValue("$excluded", excludedAuthorityId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var identity = BuildRetainedCloudRootIdentity(reader.GetString(0),
                    SyncAuthorityCloudRootIdentity.PairRootAuthority, reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), ReadCloudProviderAccountRow(reader, 4));
                if (identity is not null) result.Add(identity);
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT r.recovery_root_binding_id,r.side,r.display_path,r.identity_json,
                       p.provider,p.provider_account_id,p.account_id,p.root_namespace_id,p.home_namespace_id
                FROM recovery_root_binding r
                LEFT JOIN provider_account p ON p.provider_account_id=r.provider_account_id AND p.status='active'
                WHERE r.status='active' AND r.recovery_root_binding_id<>$excluded;
                """;
            command.Parameters.AddWithValue("$excluded", excludedAuthorityId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var identity = BuildRetainedCloudRootIdentity(reader.GetString(0),
                    SyncAuthorityCloudRootIdentity.RecoveryRootAuthority, reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), ReadCloudProviderAccountRow(reader, 4));
                if (identity is not null) result.Add(identity);
            }
        }
        return result;
    }

    private static CloudProviderAccountRow? ReadCloudProviderAccountRow(Microsoft.Data.Sqlite.SqliteDataReader reader,
        int offset)
    {
        if (reader.IsDBNull(offset) || reader.IsDBNull(offset + 1)) return null;
        var provider = reader.IsDBNull(offset) ? null : reader.GetString(offset);
        if (string.IsNullOrWhiteSpace(provider)) return null;
        return new CloudProviderAccountRow(provider!,
            reader.IsDBNull(offset + 1) ? string.Empty : reader.GetString(offset + 1),
            reader.IsDBNull(offset + 2) ? string.Empty : reader.GetString(offset + 2),
            reader.IsDBNull(offset + 3) ? string.Empty : reader.GetString(offset + 3),
            reader.IsDBNull(offset + 4) ? string.Empty : reader.GetString(offset + 4));
    }

    // One retained authority row as a comparable cloud root identity. An explicit cloud identity must agree
    // with its active provider account; a legacy row is compared through the provider it retains (or the
    // legacy Dropbox default) with an unknown account/namespace left unknown, never guessed. A physical
    // local root is not a cloud comparison peer and contributes nothing.
    private static SyncAuthorityCloudRootIdentity? BuildRetainedCloudRootIdentity(string authorityId,
        string authorityKind, string side, string displayPath, string identityJson,
        CloudProviderAccountRow? account)
    {
        if (SyncAuthorityCloudRootPersistence.TryRead(identityJson, out var provider, out var providerAccountId,
                out var namespaceId, out var folderId))
        {
            if (account is null || !string.Equals(account.Provider, provider, StringComparison.Ordinal) ||
                !string.Equals(account.ProviderAccountId, providerAccountId, StringComparison.Ordinal) ||
                !SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(provider!, account.AccountId,
                    account.RootNamespaceId, account.HomeNamespaceId, namespaceId))
                throw new InvalidOperationException(
                    "A retained cloud root does not agree with its active provider account; the cloud root comparison is refused rather than guessed.");
            return new SyncAuthorityCloudRootIdentity(provider!, providerAccountId, namespaceId, folderId!,
                displayPath, authorityId, authorityKind, side);
        }
        if (account is not null)
            return new SyncAuthorityCloudRootIdentity(account.Provider, account.ProviderAccountId,
                SafeRetainedNamespace(account), LegacyFolderId(identityJson), displayPath, authorityId,
                authorityKind, side);
        if (side == SyncAuthorityCloudSides.LogicalRight)
            return new SyncAuthorityCloudRootIdentity(SyncProviders.Dropbox, null, null,
                LegacyFolderId(identityJson), displayPath, authorityId, authorityKind, side);
        return null;
    }

    private static string? SafeRetainedNamespace(CloudProviderAccountRow account)
    {
        if (string.IsNullOrWhiteSpace(account.AccountId) || string.IsNullOrWhiteSpace(account.RootNamespaceId))
            return null;
        try
        {
            return SyncAuthorityProviderAccountIdentity.RetainedEndpointNamespace(account.Provider, account.AccountId,
                account.RootNamespaceId, account.HomeNamespaceId);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string LegacyFolderId(string identityJson)
    {
        try
        {
            return JsonDocument.Parse(identityJson).RootElement.TryGetProperty("folderId", out var folderId) &&
                   folderId.ValueKind == JsonValueKind.String
                ? folderId.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static async Task<CloudProviderAccountRow?> LoadCloudProviderAccountAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string providerAccountId, string installationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT provider,provider_account_id,account_id,root_namespace_id,home_namespace_id
            FROM provider_account
            WHERE provider_account_id=$id AND installation_id=$installation AND status='active';
            """;
        command.Parameters.AddWithValue("$id", providerAccountId);
        command.Parameters.AddWithValue("$installation", installationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var provider = reader.IsDBNull(0) ? null : reader.GetString(0);
        if (string.IsNullOrWhiteSpace(provider)) return null;
        return new CloudProviderAccountRow(provider!, reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4));
    }

    /// <summary>
    /// The retained cloud root facts of one scan root, or null when the root binding is a legacy
    /// physical/Dropbox root and keeps its existing identity behaviour.
    /// </summary>
    private static async Task<CloudScanRoot?> LoadCloudScanRootAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string rootBindingId, string pairId, string side,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT identity_json FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair AND side=$side AND state='active';";
        command.Parameters.AddWithValue("$root", rootBindingId);
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$side", side);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var identityJson = reader.GetString(0);
        return SyncAuthorityCloudRootPersistence.TryRead(identityJson, out var provider, out var providerAccountId,
            out var namespaceId, out var folderId)
            ? new CloudScanRoot(provider!, providerAccountId!, namespaceId!, folderId!)
            : null;
    }

    /// <summary>
    /// Resolves the logical object of one observation through the identity kind the identity actually
    /// carries. An explicit cloud identity on a cloud root is resolved by provider, retained provider
    /// account, native namespace and provider file identity; a legacy identity keeps its exact existing
    /// resolution. A cloud identity on a legacy root, a legacy identity on a cloud root, or a cloud identity
    /// that does not match its retained root is refused before any row is written.
    /// </summary>
    private static async Task<string?> ResolveObservationObjectIdCoreAsync(SqliteConnection connection,
        SqliteTransaction? transaction, SyncAuthorityScanGeneration scan, SyncAuthorityObservationInput entry,
        CloudScanRoot? cloudRoot, CancellationToken cancellationToken)
    {
        var isCloudIdentity = SyncAuthorityCloudObservationIdentity.TryParse(entry.IdentityJson, out var identity);
        if (cloudRoot is null)
        {
            if (isCloudIdentity)
                throw new InvalidDataException(
                    "An explicit cloud observation identity on a root that is not a retained cloud root is refused without a write.");
            return await ResolveObjectIdForPhysicalIdentityCoreAsync(connection, transaction, scan.PairId,
                scan.Side, entry.IdentityJson, cancellationToken);
        }
        if (!isCloudIdentity || identity is null)
            throw new InvalidDataException(
                "A retained cloud root requires an explicit cloud observation identity; a missing or legacy identity is refused without a write.");
        if (!string.Equals(identity.Provider, cloudRoot.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.ProviderAccountId, cloudRoot.ProviderAccountId, StringComparison.Ordinal) ||
            !string.Equals(identity.NamespaceId, cloudRoot.NamespaceId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The cloud observation identity does not match the retained provider, account and native namespace of its root; it is refused without a write.");
        return await ResolveObjectIdForCloudIdentityCoreAsync(connection, transaction, scan.PairId, scan.Side,
            identity, cancellationToken);
    }

    // One cloud identity resolves only against retained cloud versions of its own side, provider, retained
    // account and namespace. Different providers, accounts or namespaces therefore never alias, and several
    // matches are a real conflict rather than a silent choice.
    private static async Task<string?> ResolveObjectIdForCloudIdentityCoreAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string pairId, string side,
        SyncAuthorityCloudObservationIdentity identity, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidate(object_id,identity_json) AS (
                SELECT v.object_id,v.identity_json
                FROM object_version v
                JOIN effective_observation o ON o.object_version_id=v.object_version_id
                JOIN scan_generation s ON s.scan_generation_id=o.scan_generation_id
                WHERE s.scan_generation_id=(
                      SELECT latest.scan_generation_id FROM scan_generation latest
                      WHERE latest.pair_id=$pair AND latest.side=$side AND latest.status='complete'
                      ORDER BY latest.generation DESC LIMIT 1)
                  AND v.side=$side
                UNION
                SELECT v.object_id,v.identity_json
                FROM synchronization_point p
                JOIN object_version v ON v.object_version_id=CASE WHEN $side='local'
                        THEN p.local_object_version_id ELSE p.dropbox_object_version_id END
                WHERE p.pair_id=$pair AND p.authority_generation=(
                      SELECT max(latest.authority_generation) FROM synchronization_point latest
                      WHERE latest.pair_id=p.pair_id AND latest.object_id=p.object_id)
            )
            SELECT DISTINCT object_id FROM candidate
            WHERE json_extract(identity_json,'$.identityKind')='cloud'
              AND json_extract(identity_json,'$.provider')=$provider
              AND json_extract(identity_json,'$.providerAccountId')=$account
              AND json_extract(identity_json,'$.namespaceId')=$namespace
              AND json_extract(identity_json,'$.fileId')=$file;
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$side", side);
        command.Parameters.AddWithValue("$provider", identity.Provider);
        command.Parameters.AddWithValue("$account", identity.ProviderAccountId);
        command.Parameters.AddWithValue("$namespace", identity.NamespaceId);
        command.Parameters.AddWithValue("$file", identity.FileId);
        var matches = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) matches.Add(reader.GetString(0));
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException(
                "One cloud identity is bound to multiple logical objects; the observation is refused.")
        };
    }

    // ----- CC2E: exact recovery scope, verified legacy enrichment and scoped registration ---------

    /// <summary>
    /// The exact recovery lookup the later Host/effect binding consumes. A retained ACTIVE recovery root
    /// is returned only when the registered provider, the retained provider account, the verified native
    /// endpoint namespace, the root's own stable provider folder identity and the ACTUAL corpus all match
    /// exactly. A wrong provider, account, namespace, root or corpus returns null and changes nothing, and
    /// a retained row whose corpus is still NULL never matches a requested corpus.
    /// </summary>
    public async Task<SyncAuthorityCloudRecoveryRoot?> FindCloudRecoveryRootAsync(string installationId,
        string provider, string providerAccountId, string namespaceId, string folderId, string corpusId,
        CancellationToken cancellationToken = default)
    {
        foreach (var value in new[] { installationId, providerAccountId, namespaceId, folderId, corpusId })
            RequireIdentifier(value, nameof(installationId));
        var providerLiteral = RequireRegisteredCloudProviderLiteral(provider);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var account = await LoadCloudProviderAccountAsync(connection, null, providerAccountId, installationId,
                           cancellationToken)
                       ?? throw new InvalidOperationException(
                           "The recovery lookup names no active provider account of this installation.");
        if (!string.Equals(account.Provider, providerLiteral, StringComparison.Ordinal) ||
            !SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(providerLiteral, account.AccountId,
                account.RootNamespaceId, account.HomeNamespaceId, namespaceId))
            return null;
        var rows = await QueryCloudRecoveryRootsAsync(connection, null, installationId, providerAccountId,
            null, activeOnly: true, cancellationToken);
        var matches = rows.Where(row => string.Equals(row.FolderId, folderId, StringComparison.Ordinal) &&
                                        string.Equals(row.NamespaceId, namespaceId, StringComparison.Ordinal) &&
                                        row.ProviderCorpusId is not null &&
                                        string.Equals(row.ProviderCorpusId, corpusId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException(
                "More than one retained active recovery root matches this exact provider, account, namespace, root and corpus; the lookup is refused.");
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Scoped recovery registration (CC2E-2), consumed by the later Host binding packet. The scope is one
    /// exact installation, provider, provider account, verified endpoint namespace, provider folder
    /// identity and ACTUAL corpus. It reuses the one shared root comparison against every other retained
    /// pair root and recovery boundary with the caller's fresh alias-covered hierarchy snapshots, so an
    /// overlap or an unverified ancestry is refused and surfaced rather than normalised into two copy
    /// targets. It authorises no effect, reads no provider, issues no credential and branches on no
    /// provider literal.
    ///
    /// A retained row for this exact binding id is never rewritten into another scope: an exact same-id
    /// replay keeps its verified corpus, and a retained row whose corpus is still NULL is enriched in
    /// place with the verified corpus and nothing else. Creating another recovery binding for an account
    /// that still has an active recovery root with no verified corpus is refused, because a NULL corpus
    /// is never a harmless non-overlap.
    ///
    /// Hand-off obligation (CC2E to CC4A): a Host/effect consumer must still call
    /// SyncAuthorityCloudObjectOverlapGuard.RequireDistinctInventories on both completed raw listings and
    /// refresh this retained hierarchy before it authorises any copy, overwrite or delete. This method
    /// decides only whether the retained recovery SCOPE may exist; it never authorises an effect.
    /// </summary>
    public Task<SyncAuthorityCloudRecoveryRoot> RegisterCloudRecoveryRootAsync(string recoveryRootBindingId,
        string installationId, string provider, string providerAccountId, string namespaceId, string folderId,
        string corpusId, string displayPath, long generation, CancellationToken cancellationToken = default,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? hierarchies = null) =>
        ApplyCloudRecoveryRootAsync(recoveryRootBindingId, installationId, provider, providerAccountId,
            namespaceId, folderId, corpusId, displayPath, generation, mustAlreadyBeRetained: false,
            cancellationToken, hierarchies);

    /// <summary>
    /// Verified legacy enrichment (CC2E-2). An already-retained ACTIVE recovery row whose corpus is still
    /// NULL is enriched in place ONLY after the exact account, the verified endpoint namespace, the
    /// provider folder identity and a freshly verified corpus chain for that exact root all agree. The
    /// row keeps its binding id, display path, normalized path, identity JSON, generation, status and
    /// verification time exactly; only the corpus column changes. A nonnull scope is never changed by a
    /// different request, and a missing or unbound chain is refused rather than becoming a corpus.
    /// </summary>
    public Task<SyncAuthorityCloudRecoveryRoot> EnrichCloudRecoveryRootCorpusAsync(
        string recoveryRootBindingId, string installationId, string provider, string providerAccountId,
        string namespaceId, string folderId, string corpusId, CancellationToken cancellationToken = default,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? hierarchies = null) =>
        ApplyCloudRecoveryRootAsync(recoveryRootBindingId, installationId, provider, providerAccountId,
            namespaceId, folderId, corpusId, displayPath: null, generation: 0, mustAlreadyBeRetained: true,
            cancellationToken, hierarchies);

    private async Task<SyncAuthorityCloudRecoveryRoot> ApplyCloudRecoveryRootAsync(
        string recoveryRootBindingId, string installationId, string provider, string providerAccountId,
        string namespaceId, string folderId, string corpusId, string? displayPath, long generation,
        bool mustAlreadyBeRetained, CancellationToken cancellationToken,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? hierarchies)
    {
        foreach (var value in new[]
                 {
                     recoveryRootBindingId, installationId, providerAccountId, namespaceId, folderId, corpusId
                 })
            RequireIdentifier(value, nameof(recoveryRootBindingId));
        if (!mustAlreadyBeRetained && generation < 1)
            throw new ArgumentOutOfRangeException(nameof(generation));
        var providerLiteral = RequireRegisteredCloudProviderLiteral(provider);
        var side = SyncAuthorityCloudSides.LogicalRight;
        var normalizedDisplayPath = SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(displayPath);
        var identityJson = SyncAuthorityCloudRootPersistence.Serialize(providerLiteral, providerAccountId,
            namespaceId, folderId);
        var candidate = new SyncAuthorityCloudRootIdentity(providerLiteral, providerAccountId, namespaceId,
            folderId, normalizedDisplayPath, recoveryRootBindingId,
            SyncAuthorityCloudRootIdentity.RecoveryRootAuthority, side);
        var supplied = (hierarchies ?? Array.Empty<SyncAuthorityCloudHierarchySnapshot>())
            .Select(item => item ?? throw new ArgumentException(
                "A supplied cloud hierarchy snapshot is null.", nameof(hierarchies))).ToArray();
        RequireVerifiedRecoveryCorpusChain(candidate, supplied);
        await WriteAsync(async (connection, transaction) =>
        {
            var account = await LoadCloudProviderAccountAsync(connection, transaction, providerAccountId,
                              installationId, cancellationToken)
                          ?? throw new InvalidOperationException(
                              "The recovery root is not bound to an active provider account of this installation.");
            RequireRecoveryAccountAgreement(providerLiteral, account, namespaceId);
            var byId = await QueryCloudRecoveryRootsAsync(connection, transaction, installationId, null,
                recoveryRootBindingId, activeOnly: false, cancellationToken);
            if (byId.Count > 1)
                throw new InvalidDataException(
                    "More than one retained recovery root carries this binding id; the request is refused.");
            var existing = byId.Count == 1 ? byId[0] : null;
            if (mustAlreadyBeRetained && existing is null)
                throw new InvalidOperationException(
                    "There is no retained recovery root with this binding id to enrich; no corpus was written.");
            if (existing is not null && !string.Equals(existing.Status, "active", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The retained recovery root is not active; it is never revived or rewritten by a new request.");
            if (existing is not null)
                RequireRecoveryRootIdentityAgreement(existing, installationId, providerAccountId, namespaceId,
                    folderId);
            if (existing?.ProviderCorpusId is { } retainedCorpus &&
                !string.Equals(retainedCorpus, corpusId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The retained recovery root already carries a different verified corpus; a nonnull recovery scope is never changed by a different request.");
            if (existing is null)
                await RequireNoUnclassifiedRecoveryRootAsync(connection, transaction, installationId,
                    providerAccountId, recoveryRootBindingId, supplied, cancellationToken);
            await RequireNoRecoveryScopeCollisionAsync(connection, transaction, installationId, side,
                providerAccountId, corpusId, recoveryRootBindingId, cancellationToken);
            await RequireCloudRootIdentityAvailableAsync(connection, transaction, candidate, supplied,
                cancellationToken);
            if (existing is null)
            {
                await InsertCloudRecoveryRootAsync(connection, transaction, recoveryRootBindingId,
                    installationId, providerAccountId, side, normalizedDisplayPath, identityJson, generation,
                    corpusId, cancellationToken);
            }
            else if (existing.ProviderCorpusId is null)
            {
                await EnrichCloudRecoveryRootInPlaceAsync(connection, transaction, recoveryRootBindingId,
                    installationId, providerAccountId, corpusId, cancellationToken);
            }
            else
            {
                await RefreshCloudRecoveryRootReplayAsync(connection, transaction, recoveryRootBindingId,
                    installationId, providerAccountId, existing.IdentityJson, normalizedDisplayPath,
                    corpusId, cancellationToken);
            }
        }, cancellationToken);
        return await ReadCloudRecoveryRootByIdAsync(installationId, recoveryRootBindingId, cancellationToken)
               ?? throw new InvalidOperationException("The recovery root was not retained.");
    }

    private static string RequireRegisteredCloudProviderLiteral(string? provider)
    {
        if (!SyncEndpointProviders.TryGet(provider, out var definition) || definition!.ProviderId is null)
            throw new ArgumentException(
                "This provider is not a registered cloud provider, so no recovery authority is written.",
                nameof(provider));
        return definition.ProviderId;
    }

    // The retained account must be this exact provider's active account of this installation, and the
    // presented endpoint namespace must be exactly the retained one. No label, path, wildcard or provider
    // literal branch can satisfy this.
    private static void RequireRecoveryAccountAgreement(string providerLiteral, CloudProviderAccountRow account,
        string namespaceId)
    {
        if (!string.Equals(account.Provider, providerLiteral, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained provider account does not belong to this provider; the recovery root is refused.");
        if (!SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(providerLiteral, account.AccountId,
                account.RootNamespaceId, account.HomeNamespaceId, namespaceId))
            throw new InvalidOperationException(
                "The recovery endpoint namespace is not the retained namespace of this provider account; the recovery root is refused.");
    }

    // The ACTUAL corpus of a recovery root is verified only by a freshly read, complete provider chain
    // bound to exactly this provider, account, namespace and root. A missing, incomplete or unbound chain
    // proves nothing and is never turned into a corpus.
    private static void RequireVerifiedRecoveryCorpusChain(SyncAuthorityCloudRootIdentity candidate,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchies)
    {
        var bound = hierarchies.FirstOrDefault(item => item.BindsTo(candidate));
        if (bound is null || !bound.CompleteToCorpusRoot || string.IsNullOrWhiteSpace(bound.CorpusRootId))
            throw new InvalidDataException(
                "The actual corpus of this recovery root is not verified: a complete provider hierarchy chain, freshly read and bound to exactly this provider, account, namespace and root, is required. A missing, incomplete or unbound chain never becomes a corpus.");
    }

    private static void RequireRecoveryRootIdentityAgreement(SyncAuthorityCloudRecoveryRoot existing,
        string installationId, string providerAccountId, string namespaceId, string folderId)
    {
        if (!string.Equals(existing.InstallationId, installationId, StringComparison.Ordinal) ||
            !string.Equals(existing.ProviderAccountId, providerAccountId, StringComparison.Ordinal) ||
            !string.Equals(existing.NamespaceId, namespaceId, StringComparison.Ordinal) ||
            !string.Equals(existing.FolderId, folderId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained recovery root does not carry this exact installation, account, namespace and folder identity; it is never rewritten into another scope.");
    }

    private static async Task RequireNoUnclassifiedRecoveryRootAsync(SqliteConnection connection,
        SqliteTransaction transaction, string installationId, string providerAccountId, string excludedRootId,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> supplied, CancellationToken cancellationToken)
    {
        var rows = await QueryCloudRecoveryRootsAsync(connection, transaction, installationId, providerAccountId,
            null, activeOnly: true, cancellationToken);
        foreach (var row in rows)
        {
            if (string.Equals(row.RecoveryRootBindingId, excludedRootId, StringComparison.Ordinal)) continue;
            if (row.ProviderCorpusId is not null) continue;
            var identity = row.ToRootIdentity();
            if (identity is not null && supplied.Any(item => item.BindsTo(identity))) continue;
            throw new InvalidOperationException(
                "A retained active recovery root of this account still has no verified corpus (" +
                row.RecoveryRootBindingId +
                "); a NULL corpus never means a harmless non-overlap, so another recovery binding is refused until the caller supplies that root's fresh verified hierarchy.");
        }
    }

    private static async Task RequireNoRecoveryScopeCollisionAsync(SqliteConnection connection,
        SqliteTransaction transaction, string installationId, string side, string providerAccountId,
        string corpusId, string excludedRootId, CancellationToken cancellationToken)
    {
        var rows = await QueryCloudRecoveryRootsAsync(connection, transaction, installationId, providerAccountId,
            null, activeOnly: true, cancellationToken);
        foreach (var row in rows)
        {
            if (string.Equals(row.RecoveryRootBindingId, excludedRootId, StringComparison.Ordinal)) continue;
            if (!string.Equals(row.CompatibilitySide, side, StringComparison.Ordinal)) continue;
            if (!string.Equals(row.ProviderCorpusId, corpusId, StringComparison.Ordinal)) continue;
            throw new InvalidOperationException(
                "Another active recovery root of this account already covers exactly this corpus (" +
                row.RecoveryRootBindingId +
                "); a second recovery root for one account and corpus is a collision and never a silent retirement of the retained root.");
        }
    }

    private static async Task InsertCloudRecoveryRootAsync(SqliteConnection connection,
        SqliteTransaction transaction, string recoveryRootBindingId, string installationId,
        string providerAccountId, string side, string normalizedDisplayPath, string identityJson,
        long generation, string corpusId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO recovery_root_binding(recovery_root_binding_id,installation_id,provider_account_id,side,
                display_path,normalized_path_key,identity_json,generation,status,verified_utc,provider_corpus_id)
            VALUES($id,$installation,$provider,$side,$path,$path,$identity,$generation,'active',$utc,$corpus);
            """,
            cancellationToken, ("$id", recoveryRootBindingId), ("$installation", installationId),
            ("$provider", providerAccountId), ("$side", side), ("$path", normalizedDisplayPath),
            ("$identity", identityJson), ("$generation", generation), ("$corpus", corpusId),
            ("$utc", Utc(DateTimeOffset.UtcNow)));
        var exact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id=$id AND installation_id=$installation AND provider_account_id=$provider AND side=$side AND normalized_path_key=$path AND identity_json=$identity AND generation=$generation AND status='active' AND provider_corpus_id=$corpus;",
            cancellationToken, ("$id", recoveryRootBindingId), ("$installation", installationId),
            ("$provider", providerAccountId), ("$side", side), ("$path", normalizedDisplayPath),
            ("$identity", identityJson), ("$generation", generation), ("$corpus", corpusId));
        if (exact != 1)
            throw new InvalidOperationException(
                "The deterministic recovery-root binding collides with different authority.");
    }

    // Verified legacy enrichment writes EXACTLY ONE column. The binding id, display path, normalized path
    // key, identity JSON, generation, status and verification time of the retained row are untouched.
    private static async Task EnrichCloudRecoveryRootInPlaceAsync(SqliteConnection connection,
        SqliteTransaction transaction, string recoveryRootBindingId, string installationId,
        string providerAccountId, string corpusId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            """
            UPDATE recovery_root_binding SET provider_corpus_id=$corpus
            WHERE recovery_root_binding_id=$id AND installation_id=$installation
              AND provider_account_id=$provider AND status='active' AND provider_corpus_id IS NULL;
            """,
            cancellationToken, ("$id", recoveryRootBindingId), ("$installation", installationId),
            ("$provider", providerAccountId), ("$corpus", corpusId));
        var exact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id=$id AND installation_id=$installation AND provider_account_id=$provider AND status='active' AND provider_corpus_id=$corpus;",
            cancellationToken, ("$id", recoveryRootBindingId), ("$installation", installationId),
            ("$provider", providerAccountId), ("$corpus", corpusId));
        if (exact != 1)
            throw new InvalidOperationException(
                "The verified corpus was not applied to exactly the one retained recovery root; no other value was written.");
    }

    // An exact same-id replay refreshes only presentation and verification time. It can never reset the
    // verified corpus, adopt another scope or overwrite a different retained root.
    private static async Task RefreshCloudRecoveryRootReplayAsync(SqliteConnection connection,
        SqliteTransaction transaction, string recoveryRootBindingId, string installationId,
        string providerAccountId, string identityJson, string normalizedDisplayPath, string corpusId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            """
            UPDATE recovery_root_binding SET display_path=$path, normalized_path_key=$path, verified_utc=$utc
            WHERE recovery_root_binding_id=$id AND installation_id=$installation
              AND provider_account_id=$provider AND status='active' AND identity_json=$identity
              AND provider_corpus_id=$corpus;
            """,
            cancellationToken, ("$id", recoveryRootBindingId), ("$installation", installationId),
            ("$provider", providerAccountId), ("$path", normalizedDisplayPath), ("$identity", identityJson),
            ("$corpus", corpusId), ("$utc", Utc(DateTimeOffset.UtcNow)));
        var exact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id=$id AND installation_id=$installation AND provider_account_id=$provider AND status='active' AND identity_json=$identity AND provider_corpus_id=$corpus;",
            cancellationToken, ("$id", recoveryRootBindingId), ("$installation", installationId),
            ("$provider", providerAccountId), ("$identity", identityJson), ("$corpus", corpusId));
        if (exact != 1)
            throw new InvalidOperationException(
                "The exact same-id recovery replay did not retain its verified corpus and identity; it is refused.");
    }

    private async Task<SyncAuthorityCloudRecoveryRoot?> ReadCloudRecoveryRootByIdAsync(string installationId,
        string recoveryRootBindingId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await QueryCloudRecoveryRootsAsync(connection, null, installationId, null,
            recoveryRootBindingId, activeOnly: false, cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    private static async Task<IReadOnlyList<SyncAuthorityCloudRecoveryRoot>> QueryCloudRecoveryRootsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? installationId,
        string? providerAccountId, string? recoveryRootBindingId, bool activeOnly,
        CancellationToken cancellationToken)
    {
        var rows = new List<SyncAuthorityCloudRecoveryRoot>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.recovery_root_binding_id,r.installation_id,r.provider_account_id,r.side,r.display_path,
                   r.normalized_path_key,r.identity_json,r.generation,r.status,r.provider_corpus_id,
                   p.provider,p.provider_account_id,p.account_id,p.root_namespace_id,p.home_namespace_id
            FROM recovery_root_binding r
            LEFT JOIN provider_account p ON p.provider_account_id=r.provider_account_id
            WHERE ($installation IS NULL OR r.installation_id=$installation)
              AND ($account IS NULL OR r.provider_account_id=$account)
              AND ($root IS NULL OR r.recovery_root_binding_id=$root)
              AND ($activeOnly=0 OR r.status='active')
            ORDER BY r.recovery_root_binding_id;
            """;
        command.Parameters.AddWithValue("$installation", (object?)installationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$account", (object?)providerAccountId ?? DBNull.Value);
        command.Parameters.AddWithValue("$root", (object?)recoveryRootBindingId ?? DBNull.Value);
        command.Parameters.AddWithValue("$activeOnly", activeOnly ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadCloudRecoveryRoot(reader));
        return rows;
    }

    // A retained recovery row keeps its retained meaning. An explicit cloud identity must still agree with
    // its active provider account; a legacy row keeps the provider and namespace it retains (or the
    // retained legacy Dropbox default) and its recorded folder id, and never has a display path promoted
    // into an identity. A row that cannot be read truthfully is refused rather than guessed.
    private static SyncAuthorityCloudRecoveryRoot ReadCloudRecoveryRoot(SqliteDataReader reader)
    {
        var account = ReadCloudProviderAccountRow(reader, 10);
        var side = reader.GetString(3);
        var identityJson = reader.GetString(6);
        var explicitIdentity = SyncAuthorityCloudRootPersistence.TryRead(identityJson, out var provider,
            out var identityAccountId, out var namespaceId, out var folderId);
        if (explicitIdentity)
        {
            if (account is null || !string.Equals(account.Provider, provider, StringComparison.Ordinal) ||
                !string.Equals(account.ProviderAccountId, identityAccountId, StringComparison.Ordinal) ||
                !SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(provider!, account.AccountId,
                    account.RootNamespaceId, account.HomeNamespaceId, namespaceId))
                throw new InvalidOperationException(
                    "A retained recovery root does not agree with its active provider account; the recovery root is refused rather than guessed.");
        }
        else
        {
            provider = account?.Provider ?? (string.Equals(side, SyncAuthorityCloudSides.LogicalRight,
                StringComparison.Ordinal) ? SyncProviders.Dropbox : null);
            namespaceId = account is null ? null : SafeRetainedNamespace(account);
            folderId = LegacyFolderId(identityJson);
        }
        return new SyncAuthorityCloudRecoveryRoot(reader.GetString(0), reader.GetString(1), side, provider,
            reader.IsDBNull(2) ? null : reader.GetString(2), namespaceId, folderId ?? string.Empty,
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(4), reader.GetString(5),
            reader.GetInt64(7), reader.GetString(8), identityJson, explicitIdentity);
    }

    /// <summary>
    /// CFC-R3: one pair side's retained root authority as a read-only consumer needs it. It reads the ACTIVE
    /// root binding row of that exact pair and side together with the ACTIVE provider-account row that row's
    /// own identity names, and parses only the members those rows were written with. It allocates nothing,
    /// mutates nothing, performs no provider call and authorizes no effect; a missing or inactive root returns
    /// null rather than a guess. This is the production read-only path: no ForTest accessor is used, and this
    /// method never becomes an authority for a write.
    /// </summary>
    public async Task<SyncAuthorityRetainedRootAuthority?> LoadRetainedRootAuthorityAsync(string pairId,
        string side, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadRetainedRootAuthorityCoreAsync(connection, null, pairId, side, cancellationToken);
    }

    /// <summary>
    /// The transaction-scoped core of the retained root authority read: the SAME rows and members as the
    /// public read, over the caller's own connection and transaction, so a comparison built from it cannot
    /// race a registration that is deciding in the same transaction and no detached read is introduced. It
    /// allocates nothing, mutates nothing and authorises no effect.
    /// </summary>
    private static async Task<SyncAuthorityRetainedRootAuthority?> LoadRetainedRootAuthorityCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairId, string side,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.root_binding_id,r.generation,r.display_path,r.identity_json,
                   p.provider,p.account_id,p.root_namespace_id,p.home_namespace_id
            FROM root_binding r
            LEFT JOIN provider_account p
              ON p.provider_account_id=json_extract(r.identity_json,'$.providerAccountId')
             AND p.status='active'
            WHERE r.pair_id=$pair AND r.side=$side AND r.state='active'
            ORDER BY r.generation DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$side", side);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var identityJson = reader.IsDBNull(3) ? "{}" : reader.GetString(3);
        var explicitCloud = SyncAuthorityCloudRootPersistence.TryRead(identityJson, out var provider,
            out var identityAccountId, out var namespaceId, out var identityFolderId);
        using var identityDocument = JsonDocument.Parse(identityJson);
        var identityRoot = identityDocument.RootElement;
        if (identityRoot.ValueKind != JsonValueKind.Object ||
            identityRoot.TryGetProperty("identityKind", out _) && !explicitCloud)
            throw new InvalidDataException("The retained root declares an invalid explicit cloud identity; " +
                "it cannot be reinterpreted as a legacy root.");
        var identityAccount = identityAccountId ?? JsonMember(identityJson, "providerAccountId");
        var folder = identityFolderId ?? JsonMember(identityJson, "folderId");
        return new SyncAuthorityRetainedRootAuthority(reader.GetString(0), pairId, side, reader.GetInt64(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2), identityJson, explicitCloud, provider,
            namespaceId, identityAccount, folder,
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    /// <summary>
    /// The operation-scoped COMPARISON copy of one retained pair for the nested-registration consumers. Each
    /// document-less cloud side obtains the namespace the product already verified for that exact pair, port
    /// and folder through the ONE shared projector, using that pair's OWN active root binding and provider
    /// account read inside the caller's transaction. A bare <see cref="SyncPairPartitionPolicy"/> caller
    /// receives no comparison whenever any required fact is missing or mismatched. An unchecked connection
    /// must never masquerade as a distinct cloud account. The copy never enters persistence, identity
    /// derivation, runtime configuration or the emitted saved pair set; the RAW retained definition keeps
    /// owning every revision, definition and immutable-authority check.
    /// </summary>
    internal static async Task<SyncPair?> ProjectRetainedPairForComparisonAsync(SqliteConnection connection,
        SqliteTransaction? transaction, SyncPair pair, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (pair.EndpointDocument is not null) return pair;
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
        if (endpoints is null) return null;
        var ordered = new[] { endpoints.Left, endpoints.Right };
        var projected = new SyncEndpoint?[2];
        var changed = false;
        for (var index = 0; index < ordered.Length; index++)
        {
            var side = ordered[index];
            projected[index] = side;
            if (side.IsLocal || SyncEndpointProviders.Get(side.Kind).ProviderId is null) continue;
            if (!string.IsNullOrWhiteSpace(side.NamespaceId)) continue;
            if (string.IsNullOrWhiteSpace(side.ConnectionId) || string.IsNullOrWhiteSpace(side.FolderId))
                return null;
            var port = SyncEndpointContext.CompatibilityPort(endpoints.Left.Kind, endpoints.Right.Kind,
                index == 0 ? SyncPairSide.Left : SyncPairSide.Right);
            var authority = await LoadRetainedRootAuthorityCoreAsync(connection, transaction, pair.Id, port,
                cancellationToken);
            if (authority is null) return null;
            var endpoint = SyncAuthorityRetainedEndpointProjection.Project(pair.Id, port, side, authority,
                authority.AccountProvider ?? string.Empty, authority.AccountId, authority.AccountRootNamespaceId,
                authority.AccountHomeNamespaceId);
            if (endpoint is null) return null;
            projected[index] = endpoint;
            changed = true;
        }
        if (!changed) return pair;
        var document = new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints,
            SyncEndpointRecord.From(projected[0]!), SyncEndpointRecord.From(projected[1]!));
        return pair with
        {
            EndpointSchemaVersion = document.Version,
            LeftEndpoint = document.Left,
            RightEndpoint = document.Right
        };
    }

    /// <summary>One plain string member of a retained identity document, or null when it is absent.</summary>
    private static string? JsonMember(string identityJson, string member)
    {
        try
        {
            return JsonNode.Parse(identityJson) is JsonObject root &&
                   root.TryGetPropertyValue(member, out var node) && node is JsonValue value &&
                   value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// CFC-R3: one pair side's retained, still-active root authority: the root binding row's own identity and
/// generation plus its active provider-account row when the identity names one. It is read-only retained
/// evidence for a preview, never an authority for a write.
/// </summary>
public sealed record SyncAuthorityRetainedRootAuthority(
    string RootBindingId,
    string PairId,
    string Side,
    long Generation,
    string DisplayPath,
    string IdentityJson,
    bool IsExplicitCloudIdentity,
    string? IdentityProvider,
    string? IdentityNamespaceId,
    string? IdentityProviderAccountId,
    string? IdentityFolderId,
    string? AccountProvider,
    string? AccountId,
    string? AccountRootNamespaceId,
    string? AccountHomeNamespaceId)
{
    /// <summary>True when an active provider account was retained for this root's own named account identity.</summary>
    public bool HasRetainedProviderAccount => !string.IsNullOrWhiteSpace(AccountProvider) &&
        !string.IsNullOrWhiteSpace(AccountId) && !string.IsNullOrWhiteSpace(AccountRootNamespaceId) &&
        !string.IsNullOrWhiteSpace(AccountHomeNamespaceId);
}

/// <summary>
/// One retained cloud recovery root as the authority layer exposes it: its exact binding id, the
/// installation and compatibility side it is scoped to, the native namespace and provider folder identity
/// it retains, the corpus it actually carries (null until verified) and the retained identity JSON a write
/// must never silently rewrite. Its provider family is read from the registered provider account and never
/// branched on here.
/// </summary>
public sealed record SyncAuthorityCloudRecoveryRoot(
    string RecoveryRootBindingId,
    string InstallationId,
    string CompatibilitySide,
    string? Provider,
    string? ProviderAccountId,
    string? NamespaceId,
    string FolderId,
    string? ProviderCorpusId,
    string DisplayPath,
    string NormalizedPathKey,
    long Generation,
    string Status,
    string IdentityJson,
    bool IsExplicitCloudIdentity)
{
    /// <summary>
    /// This retained row as a comparable cloud root identity, or null when the row retains no verifiable
    /// provider, account or namespace. A display path is never promoted into an identity.
    /// </summary>
    public SyncAuthorityCloudRootIdentity? ToRootIdentity() =>
        Provider is null || NamespaceId is null || string.IsNullOrWhiteSpace(FolderId)
            ? null
            : new SyncAuthorityCloudRootIdentity(Provider, ProviderAccountId, NamespaceId, FolderId,
                DisplayPath, RecoveryRootBindingId, SyncAuthorityCloudRootIdentity.RecoveryRootAuthority,
                CompatibilitySide);
}

/// <summary>
/// The ONE shared, pure own-retained-authority endpoint projection used by every nested-registration
/// consumer (the authoring Host and the central database comparison). It validates one retained pair's OWN
/// ordered cloud side against that side's own active root-binding row and provider account and returns the
/// operation-scoped comparison endpoint carrying the namespace the product already verified for this exact
/// pair, compatibility port and folder, or null when any required fact is missing or mismatched. It reads no
/// display path, borrows no candidate or foreign account, invents no namespace, rewrites no retained
/// definition and adds no second hash algorithm: the provider-account id and the endpoint connection id are
/// exactly the existing derivations.
/// </summary>
internal static class SyncAuthorityRetainedEndpointProjection
{
    internal static SyncEndpoint? Project(string pairId, string port, SyncEndpoint side,
        SyncAuthorityRetainedRootAuthority? authority, string expectedProvider, string? expectedAccountId,
        string? expectedRootNamespaceId, string? expectedHomeNamespaceId)
    {
        if (side.IsLocal) return null;
        if (SyncEndpointProviders.Get(side.Kind).ProviderId is not { } provider) return null;
        if (!string.IsNullOrWhiteSpace(side.NamespaceId)) return null;
        if (string.IsNullOrWhiteSpace(side.ConnectionId) || string.IsNullOrWhiteSpace(side.FolderId)) return null;
        if (authority is null) return null;
        if (!string.Equals(authority.PairId, pairId, StringComparison.Ordinal) ||
            !string.Equals(authority.Side, port, StringComparison.Ordinal) || authority.Generation < 1 ||
            !authority.HasRetainedProviderAccount) return null;
        if (string.IsNullOrWhiteSpace(expectedProvider) || string.IsNullOrWhiteSpace(expectedAccountId) ||
            string.IsNullOrWhiteSpace(expectedRootNamespaceId) ||
            string.IsNullOrWhiteSpace(expectedHomeNamespaceId)) return null;
        if (!string.Equals(provider, expectedProvider, StringComparison.OrdinalIgnoreCase)) return null;
        // The retained account row must be this provider's own account for the exact expected tuple, and its
        // retained id must be exactly the existing provider-account derivation over that tuple.
        if (!string.Equals(authority.AccountProvider, provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(authority.AccountId, expectedAccountId, StringComparison.Ordinal) ||
            !string.Equals(authority.AccountRootNamespaceId, expectedRootNamespaceId, StringComparison.Ordinal) ||
            !string.Equals(authority.AccountHomeNamespaceId, expectedHomeNamespaceId, StringComparison.Ordinal))
            return null;
        if (!SyncAuthorityProviderAccountIdentity.RetainedRowMatchesDerivation(provider,
                authority.IdentityProviderAccountId, expectedAccountId, expectedRootNamespaceId,
                expectedHomeNamespaceId)) return null;
        // The retained root must be this exact side's own folder identity.
        if (!string.Equals(authority.IdentityFolderId, side.FolderId, StringComparison.Ordinal)) return null;
        string connectionId;
        string namespaceId;
        try
        {
            connectionId = StandardConnectionId(side.Kind, expectedAccountId, expectedRootNamespaceId);
            namespaceId = SyncAuthorityProviderAccountIdentity.RetainedEndpointNamespace(provider,
                expectedAccountId, expectedRootNamespaceId, expectedHomeNamespaceId);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or
            InvalidOperationException)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(namespaceId)) return null;
        // The pair's own stored connection id must be exactly the existing standard derivation for this
        // provider and this account; an unknown or foreign connection route is never projected.
        if (!string.Equals(side.ConnectionId, connectionId, StringComparison.Ordinal)) return null;
        // An explicit root identity must agree with the retained provider and namespace it names.
        if (authority.IsExplicitCloudIdentity &&
            (!string.Equals(authority.IdentityProvider, provider, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(authority.IdentityNamespaceId, namespaceId, StringComparison.Ordinal))) return null;
        return SyncEndpoint.Cloud(side.Kind, side.ConnectionId!, namespaceId, side.FolderId, side.FolderPath);
    }

    // The EXISTING standard connection-id derivations of the registered providers. No new algorithm and no
    // provider-pair matrix: an unregistered provider yields no projection rather than a guessed route.
    private static string StandardConnectionId(SyncEndpointKind kind, string accountId, string rootNamespaceId) =>
        kind switch
        {
            SyncEndpointKind.Dropbox => DropboxConnectionBinding.CreateConnectionId(accountId),
            SyncEndpointKind.GoogleDrive => GoogleDriveConnectionBinding.CreateConnectionId(rootNamespaceId),
            _ => string.Empty
        };
}
    /// <summary>
