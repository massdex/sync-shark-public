using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private async Task<IReadOnlyList<(string Id, string State)>> LoadUnfinishedPairOperationsAsync(string pairId, CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id,status FROM operation o
            WHERE o.status NOT IN ('completed','cancelled','superseded')
              AND (o.pair_id=$pair OR EXISTS(SELECT 1 FROM json_each(o.preconditions_json,'$.planningAuthorities') retained
                    WHERE json_extract(retained.value,'$.PairId')=$pair));
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        var result = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    internal async Task EnsureReviewedRunCanStartAsync(string pairId, CancellationToken token,
        Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Retiring interrupted reviewed plans and abandoned leases that never issued an effect; no file transfer has started.");
        // Retire conclusively unissued reviewed rows BEFORE resolving recovered control
        // ownership. The strict ownership resolver rightly rejects queued work, so doing
        // this after it trapped a cancelled first-sync plan in an admission loop.
        // The existing reconciler still preserves every attempt, advanced allocation,
        // active holder and cross-pair operation; no uncertain effect is retired.
        await ReconcileUnissuedOperationLeasesAsync(pairId, token, includeReviewedReadyPlans: true);
        onProgress?.Invoke("Abandoned-operation lease checks finished. Reading the unfinished operation identities for this pair.");
        // This is execution admission, not permission to edit a pair. Queued but
        // unissued choices are re-planned from the original consent; old known
        // refusals remain immutable history. Issued/uncertain work must reconcile.
        var unfinished = await LoadUnfinishedPairOperationsAsync(pairId, token);
        // Recovery deliberately preserves the original blocked collision as history.
        // Resolve its exact current ownership just as ordinary pair publication does;
        // never broaden the general no-effect predicate or rewrite that history.
        await using var recoveryConnection = await OpenConnectionAsync(token);
        var recoveredCollision = await ResolveRecoveredCollisionForPairAsync(
            recoveryConnection, null, pairId, token);
        onProgress?.Invoke($"Checking dispatch safety for {unfinished.Count:N0} unfinished operations: attempts, allocations and active leases.");
        foreach (var operation in await LoadRetryOperationEvidenceAsync(unfinished.Select(item => item.Id).ToArray(), token))
            if (operation.OperationId != recoveredCollision && !operation.Unissued && !operation.KnownNoEffectRefusal)
            {
                throw new InvalidOperationException("Verify the retained outcome before retrying: " +
                    operation.Path + " — " + operation.OperationId +
                    " (" + SyncAuthorityNames.OperationStatus(operation.Status) + "). No completed or uncertain effect was replayed.");
            }
        onProgress?.Invoke($"Admission checks completed for {unfinished.Count:N0} retained operations. Fresh folder observation is next.");
    }

    // Lease acquisition and writing a reviewed plan are not dispatch. An interrupted
    // planning pass may leave ready rows before its group is sealed. Only reviewed-run
    // admission retires those rows; ordinary startup recovery keeps its leased-only
    // behavior. Retain every immutable plan and supersede only conclusively unissued
    // standard operations through the existing sealed graph. Current consent, a fresh
    // planner and per-effect checks must authorize replacements; this never replays work.
    internal async Task ReconcileUnissuedOperationLeasesAsync(string pairId, CancellationToken token,
        bool includeReviewedReadyPlans = false, IReadOnlyCollection<string>? operationIds = null)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (operationIds is { Count: 0 }) return;
        if (operationIds is not null)
            foreach (var operationId in operationIds) RequireIdentifier(operationId, nameof(operationIds));
        await WriteAsync(async (connection, transaction) =>
        {
            var ids = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    WITH advanced_allocations AS MATERIALIZED (
                        SELECT DISTINCT operation_id FROM resource_allocation WHERE status<>'authorized'
                    )
                    SELECT o.operation_id FROM operation o
                    WHERE o.pair_id=$pair
                      AND ($selected IS NULL OR o.operation_id IN (SELECT value FROM json_each($selected)))
                      AND (o.status='leased' OR ($reviewed=1 AND o.status='ready' AND o.current_lease_id IS NULL))
                      AND o.kind IN ('create_local','create_remote','update_local','update_remote',
                                     'delete_local','delete_remote','move_local','move_remote')
                      AND NOT EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id)
                      AND NOT EXISTS(SELECT 1 FROM advanced_allocations a WHERE a.operation_id=o.operation_id)
                      AND NOT EXISTS(SELECT 1 FROM lease l WHERE l.scope_kind='operation' AND l.scope_id=o.operation_id
                            AND l.status='active' AND julianday(l.expires_utc)>julianday('now'));
                    """;
                command.Parameters.AddWithValue("$pair", pairId);
                command.Parameters.AddWithValue("$reviewed", includeReviewedReadyPlans ? 1 : 0);
                command.Parameters.AddWithValue("$selected", operationIds is null
                    ? DBNull.Value : JsonSerializer.Serialize(operationIds));
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
            }
            foreach (var id in ids)
            {
                await ExecuteAsync(connection, transaction,
                    "UPDATE operation SET status='superseded',terminal_utc=$utc,current_lease_id=NULL,current_fencing_token=NULL WHERE operation_id=$id;",
                    token, ("$id", id), ("$utc", Utc(DateTimeOffset.UtcNow)));
                await AppendOperationLifecycleAsync(connection, transaction,
                id, "unissued_lease_recovered", "{\"reason\":\"The interrupted plan had no attempt, advanced allocation or active holder. Its immutable unissued plan was superseded; a fresh planner and current approval are required. No file effect was replayed.\"}",
                Utc(DateTimeOffset.UtcNow), Guid.NewGuid().ToString("N"), token);
            }
        }, token);
    }

    internal async Task EnsureFirstSyncCanFinalizeAsync(string pairId, CancellationToken token)
    {
        // Completing first-sync approval is not an endpoint/settings edit. An
        // earlier conclusively refused attempt remains history after a later
        // successful retry, but every unresolved/queued effect still blocks it.
        await using var connection = await OpenConnectionAsync(token);
        var recoveredCollision = await ResolveRecoveredCollisionForPairAsync(connection, null, pairId, token);
        foreach (var operation in await LoadUnfinishedPairOperationsAsync(pairId, token))
            if (operation.Id != recoveredCollision &&
                (operation.State != "blocked" || !await IsKnownNoEffectRefusalAsync(operation.Id, token)))
                throw new InvalidOperationException("First sync cannot finish while retained work remains unresolved: " + operation.Id);
    }

    // Consent, its policy revision and the detailed terminal run result are one existing-table
    // transaction. A completed finite review is not a claim that the live folder has stopped changing.
    internal Action<string>? FirstSyncFinalizationBoundaryForTest { get; set; }

    internal async Task FinalizeReviewedFirstSyncAsync(SyncPair expectedPair, string commandId,
        string localScanId, string remoteScanId, string resultJson)
    {
        expectedPair = SyncPairStore.NormalizePair(expectedPair);
        if (expectedPair.InitialReviewApproved != false)
            throw new InvalidOperationException("First sync has already been reviewed for this pair.");
        var approved = expectedPair with
        {
            InitialReviewApproved = true,
            PolicyRevision = checked(expectedPair.PolicyRevision + 1)
        };
        await WriteAsync(async (connection, transaction) =>
        {
            var retainedJson = await ScalarStringAsync(connection, transaction,
                "SELECT pair_json FROM sync_pair WHERE pair_id=$id AND configuration_revision=$revision AND state<>'retired';",
                CancellationToken.None, ("$id", expectedPair.Id), ("$revision", expectedPair.PolicyRevision));
            var retained = retainedJson is null ? null : JsonSerializer.Deserialize<SyncPair>(retainedJson);
            if (retained is null || JsonSerializer.Serialize(SyncPairStore.NormalizePair(retained)) !=
                JsonSerializer.Serialize(expectedPair))
                throw new InvalidOperationException("The reviewed pair settings or endpoint bindings changed before approval.");
            var scans = await ScalarLongAsync(connection, transaction, """
                SELECT count(*) FROM scan_generation WHERE pair_id=$pair AND status='complete'
                  AND ((side='local' AND scan_generation_id=$local)
                       OR (side='dropbox' AND scan_generation_id=$remote));
                """, CancellationToken.None, ("$pair", expectedPair.Id), ("$local", localScanId),
                ("$remote", remoteScanId));
            if (scans != 2)
                throw new InvalidOperationException("The exact complete reviewed inventories are unavailable for approval.");
            // Identical scope/predicate to EnsureFirstSyncCanFinalizeAsync, including cross-pair participants.
            var recoveredCollision = await ResolveRecoveredCollisionForPairAsync(
                connection, transaction, expectedPair.Id, CancellationToken.None);
            var unresolved = await ScalarStringAsync(connection, transaction, $"""
                SELECT o.operation_id FROM operation o
                WHERE o.status NOT IN ('completed','cancelled','superseded')
                  AND (o.pair_id=$pair OR EXISTS(SELECT 1 FROM json_each(o.preconditions_json,'$.planningAuthorities') retained
                        WHERE json_extract(retained.value,'$.PairId')=$pair))
                  AND NOT(o.status='blocked' AND {KnownNoEffectRefusalPredicate})
                  AND ($recovered IS NULL OR o.operation_id<>$recovered) LIMIT 1;
                """, CancellationToken.None, ("$pair", expectedPair.Id),
                ("$recovered", (object?)recoveredCollision ?? DBNull.Value));
            if (unresolved is not null)
                throw new InvalidOperationException("First sync cannot finish while retained work remains unresolved: " + unresolved);
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM command_request WHERE command_request_id=$id AND pair_id=$pair AND command_kind='run_pair' AND status='executing';",
                    CancellationToken.None, ("$id", commandId), ("$pair", expectedPair.Id)) != 1)
                throw new InvalidOperationException("The executing reviewed command does not belong to this pair.");
            var selected = await ScalarLongAsync(connection, transaction,
                "SELECT selected FROM sync_pair WHERE pair_id=$id;", CancellationToken.None,
                ("$id", expectedPair.Id)) == 1;
            await RegisterPairConfigurationCoreAsync(connection, transaction, approved.Id,
                approved.PolicyRevision, SyncAuthorityPairConfiguration.FromPair(approved), CancellationToken.None);
            await RetainPairDefinitionCoreAsync(connection, transaction, approved,
                JsonSerializer.Serialize(approved), selected, CancellationToken.None);
            FirstSyncFinalizationBoundaryForTest?.Invoke("approval-written-before-terminal-result");
            await TransitionCommandRequestCoreAsync(connection, transaction, commandId, "completed", resultJson,
                CancellationToken.None);
            FirstSyncFinalizationBoundaryForTest?.Invoke("terminal-written-before-commit");
        }, CancellationToken.None);
    }

    internal async Task<bool> IsUnissuedOperationAsync(string operationId, CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token);
        return await ScalarLongAsync(connection, null,
            $"SELECT count(*) FROM operation o WHERE o.operation_id=$id AND {UnissuedOperationPredicate};",
            token, ("$id", operationId)) == 1;
    }

    internal async Task SupersedeReleasedLocalPreparationAsync(SyncAuthorityLease lease,
        string pairId, CancellationToken token)
    {
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireFenceAsync(connection, transaction, lease.OperationId, lease, token);
            var eligible = await ScalarLongAsync(connection, transaction, """
                SELECT count(*) FROM operation o WHERE o.operation_id=$id AND o.pair_id=$pair
                  AND o.kind='create_local' AND o.status='leased'
                  AND NOT EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id)
                  AND NOT EXISTS(SELECT 1 FROM resource_allocation a WHERE a.operation_id=o.operation_id
                                 AND a.status NOT IN ('authorized','released'));
                """, token, ("$id", lease.OperationId), ("$pair", pairId));
            if (eligible != 1)
                throw new InvalidOperationException("Prepared work cannot retire while an attempt or unreconciled allocation remains.");
            var utc = Utc(DateTimeOffset.UtcNow);
            const string detail = "{\"reason\":\"The exact empty private preparation was reconciled and released before any attempt. Its immutable plan was superseded; fresh observation and consent are required. No user file or provider effect was replayed.\"}";
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='superseded',terminal_utc=$utc,last_transition_lease_id=$lease,last_transition_fencing_token=$fence WHERE operation_id=$id;",
                token, ("$id", lease.OperationId), ("$utc", utc), ("$lease", lease.LeaseId), ("$fence", lease.FencingToken));
            await AppendOperationLifecycleAsync(connection, transaction,
                lease.OperationId, "unissued_preparation_recovered", detail,
                utc, Guid.NewGuid().ToString("N"), token);
        }, token);
    }

    private const string UnissuedOperationPredicate = """
            o.status IN ('planned','ready')
              AND NOT EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id)
              AND NOT EXISTS(SELECT 1 FROM resource_allocation a WHERE a.operation_id=o.operation_id AND a.status NOT IN ('authorized','released'))
              AND NOT EXISTS(SELECT 1 FROM lease l WHERE l.scope_id=o.operation_id AND l.status='active' AND julianday(l.expires_utc)>julianday('now'))
            """;

    internal async Task<string?> LoadUnfinishedDependencyAsync(string operationId, CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token);
        return await ScalarStringAsync(connection, null, """
            SELECT coalesce((SELECT expected_path_key FROM operation_member m WHERE m.operation_id=d.depends_on_operation_id AND expected_path_key IS NOT NULL LIMIT 1),d.depends_on_operation_id) || ' (' || d.prerequisite_status || ')'
            FROM operation_dependency_state d
            WHERE d.operation_id=$id AND
              ((d.dependency_kind='must_complete' AND d.prerequisite_status<>'completed')
               OR (d.dependency_kind='must_commit' AND d.prerequisite_status NOT IN ('committed','cleanup_pending','completed'))
               OR (d.dependency_kind='must_resolve' AND d.prerequisite_status NOT IN ('committed','cleanup_pending','completed','blocked','cancelled','superseded')))
            ORDER BY d.depends_on_operation_id LIMIT 1;
            """, token, ("$id", operationId));
    }

    // Shared by retry admission, configuration registration, replacement and retirement. Old failed
    // history must not prevent successful first-sync approval, but an issued or
    // uncertain outcome must never be waved through by a second weaker check.
    // A removed partial stage stays Blocked (never falsely Verified); only its
    // released owned parent and exact no-effect receipt discharge that artifact.
    private const string KnownNoEffectRefusalPredicate = """
            o.status IN ('blocked','superseded','ready')
              AND NOT EXISTS(SELECT 1 FROM resource_allocation a WHERE a.operation_id=o.operation_id
                AND a.status NOT IN ('authorized','released')
                AND NOT(o.kind='create_local' AND a.resource_kind='stage_file' AND a.status='blocked'
                    AND a.bound_identity IS NOT NULL
                    AND EXISTS(SELECT 1 FROM resource_allocation parent WHERE parent.operation_id=o.operation_id
                        AND parent.allocation_id=a.intended_parent_identity
                        AND parent.resource_kind='transaction_directory' AND parent.status='released')
                    AND EXISTS(SELECT 1 FROM operation_attempt attempt JOIN effect_receipt receipt
                        ON receipt.attempt_id=attempt.attempt_id WHERE attempt.operation_id=o.operation_id
                        AND attempt.effect_kind='local-create-from-dropbox-revision'
                        AND receipt.certainty='known_not_applied'
                        AND json_extract(receipt.provider_metadata_json,'$.reconciliation')=
                            'target absent; owned partial stage removed')))
              AND NOT EXISTS(SELECT 1 FROM lease l WHERE l.scope_id=o.operation_id AND l.status='active' AND julianday(l.expires_utc)>julianday('now'))
              AND ((NOT EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id)
                    AND NOT EXISTS(SELECT 1 FROM resource_allocation a WHERE a.operation_id=o.operation_id AND a.status NOT IN ('authorized','released'))
                    AND NOT EXISTS(SELECT 1 FROM lease l WHERE l.scope_id=o.operation_id AND l.status='active' AND julianday(l.expires_utc)>julianday('now')))
                OR EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id
                  AND a.attempt_no=(SELECT max(attempt_no) FROM operation_attempt WHERE operation_id=o.operation_id)
                  AND a.status IN ('blocked','verified_not_applied')
                  AND EXISTS(SELECT 1 FROM effect_receipt r WHERE r.attempt_id=a.attempt_id AND r.certainty='known_not_applied')
                  AND NOT EXISTS(SELECT 1 FROM effect_receipt r WHERE r.attempt_id=a.attempt_id AND r.certainty='known_success')
                  AND (a.status='verified_not_applied' OR NOT EXISTS(SELECT 1 FROM effect_receipt r
                    WHERE r.attempt_id=a.attempt_id AND r.certainty<>'known_not_applied'))))
            """;

    internal async Task<bool> IsKnownNoEffectRefusalAsync(string operationId, CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token);
        return await ScalarLongAsync(connection, null,
            $"SELECT count(*) FROM operation o WHERE o.operation_id=$id AND {KnownNoEffectRefusalPredicate};",
            token, ("$id", operationId)) == 1;
    }

    // An exception cannot prove that an issued effect did not happen. A fresh
    // fence protects this record after the executor has released its own lease.
    internal async Task<SyncAuthorityExecutionResult> RetainExecutionFailureAsync(
        string operationId, string workerId, Exception error, CancellationToken token)
    {
        var retained = await LoadOperationAsync(operationId, token);
        if (retained.Status is SyncAuthorityOperationStatus.Completed or
            SyncAuthorityOperationStatus.Cancelled or SyncAuthorityOperationStatus.Superseded)
            return new(operationId, retained.Status, retained.Attempts.Count > 0,
                SyncActivityText.Safe(error.GetType().Name + ": " + error.Message));

        await using var renewal = await this.BeginFencedOperationLeaseRenewalAsync(operationId,
            workerId, SyncAuthorityLeasePolicy.LeaseDuration, token);
        var lease = renewal.Lease;
        var reason = SyncActivityText.Failure(error);
        var resultStatus = SyncAuthorityOperationStatus.Blocked;
        var issued = false;
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireFenceAsync(connection, transaction, operationId, lease, token);
            var current = await ScalarStringAsync(connection, transaction,
                "SELECT status FROM operation WHERE operation_id=$id;", token, ("$id", operationId));
            issued = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation_attempt WHERE operation_id=$id AND status<>'verified_not_applied';",
                token, ("$id", operationId)) != 0;
            var cleanUnissued = !issued && current is ("ready" or "leased") &&
                await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM resource_allocation WHERE operation_id=$id AND status<>'authorized';",
                    token, ("$id", operationId)) == 0;
            // leased -> blocked is deliberately absent from the sealed graph.
            // A conclusively unissued failed plan can be superseded, not reset or
            // declared applied. Its user-facing disposition remains a safe refusal.
            var state = cleanUnissued ? "superseded"
                : current is "committed" or "cleanup_pending" ? "cleanup_pending"
                : !issued && current == "leased" ? "leased"
                : issued ? "effect_unknown" : "blocked";
            resultStatus = state == "cleanup_pending" ? SyncAuthorityOperationStatus.CleanupPending
                : issued ? SyncAuthorityOperationStatus.EffectUnknown : SyncAuthorityOperationStatus.Blocked;
            var detail = JsonSerializer.Serialize(new { reason, failureType = error.GetType().Name,
                effect = state == "cleanup_pending" ? "committed; cleanup required"
                    : issued ? "outcome must be verified; do not reissue" : "no user effect was dispatched" });
            // M6/G-3: recording a failure is not a state transition. When the exact failure leaves the
            // operation in the status it already had (the common case is a second failure while the outcome is
            // still effect_unknown), the transition-authority columns are deliberately left untouched: the
            // sealed graph rejects a same-status authority write, and rewriting them would replace the
            // original retained uncertainty with a database error. Only the diagnostic fields and the exact
            // failure history are updated, so the original transition authority and uncertainty survive.
            if (string.Equals(state, current, StringComparison.Ordinal))
                await ExecuteAsync(connection, transaction,
                    "UPDATE operation SET block_detail=$detail WHERE operation_id=$id;",
                    token, ("$detail", detail), ("$id", operationId));
            else
                await ExecuteAsync(connection, transaction,
                    "UPDATE operation SET status=$status,block_detail=$detail,last_transition_lease_id=$lease,last_transition_fencing_token=$fence,terminal_utc=CASE WHEN $status='superseded' THEN $utc ELSE terminal_utc END WHERE operation_id=$id;",
                    token, ("$status", state), ("$detail", detail), ("$lease", lease.LeaseId),
                    ("$fence", lease.FencingToken), ("$id", operationId), ("$utc", Utc(DateTimeOffset.UtcNow)));
            // An exception after request dispatch is an explicitly UNKNOWN receipt,
            // not proof of application or non-application. Retain that boundary in
            // the same fenced transaction as the uncertainty transition. Recovery
            // still has to verify the exact effect; never replace an existing receipt.
            if (issued && state == "effect_unknown")
                await ExecuteAsync(connection, transaction,
                    """
                    INSERT INTO effect_receipt(effect_receipt_id,attempt_id,recorded_lease_id,
                        recorded_fencing_token,certainty,source_identity_json,destination_identity_json,
                        provider_metadata_json,evidence_digest,observed_utc)
                    SELECT $receipt || '-' || a.attempt_id,a.attempt_id,$lease,$fence,'unknown','{}','{}',
                        $detail,$digest,$utc FROM operation_attempt a
                    WHERE a.operation_id=$id AND a.status IN ('request_sent','effect_unknown')
                      AND NOT EXISTS(SELECT 1 FROM effect_receipt r WHERE r.attempt_id=a.attempt_id);
                    """, token, ("$receipt", Guid.NewGuid().ToString("N")), ("$id", operationId),
                    ("$lease", lease.LeaseId), ("$fence", lease.FencingToken), ("$detail", detail),
                    ("$digest", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(detail)))
                        .ToLowerInvariant()), ("$utc", Utc(DateTimeOffset.UtcNow)));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status='effect_unknown',transition_lease_id=$lease,transition_fencing_token=$fence WHERE operation_id=$id AND status IN ('started','request_sent');",
                token, ("$lease", lease.LeaseId), ("$fence", lease.FencingToken), ("$id", operationId));
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "execution_failed", detail,
                Utc(DateTimeOffset.UtcNow), Guid.NewGuid().ToString("N"), token);
        }, renewal.WorkToken);
        return new(operationId, resultStatus, issued, reason + (resultStatus switch
        {
            SyncAuthorityOperationStatus.EffectUnknown => " The outcome must be verified before retry; no replacement effect was issued.",
            SyncAuthorityOperationStatus.CleanupPending => " The file effect is committed; only its retained cleanup remains.",
            _ => " No user file effect was dispatched. Independent files can continue."
        }));
    }
}
