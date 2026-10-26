using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

internal sealed record SyncActionClosureResult(string OperationId, bool Closed, string Disposition,
    long EncodedBytes = 0);

public sealed partial class SyncAuthorityDatabase
{
    private const long MaximumActionBatchBytes = 1_048_576;

    // Captured in the same fenced transaction that installs the exact successor.
    // The old point can later become obsolete; this immutable value still states
    // what this action actually committed, never what the current path suggests.
    private static async Task BindCommittedPointCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, string pointId, string pairId,
        string objectId, string? localVersion, string? remoteVersion, string? localMembership,
        string? remoteMembership, long generation, CancellationToken token)
    {
        var value = await ScalarStringAsync(connection, transaction, """
            SELECT json_object('kind','synchronization_point','pointId',synchronization_point_id,
                'pairId',pair_id,'objectId',object_id,'localVersionId',local_object_version_id,
                'remoteVersionId',dropbox_object_version_id,'localMembershipId',local_membership_id,
                'remoteMembershipId',dropbox_membership_id,'generation',authority_generation,
                'acceptedUtc',accepted_utc)
            FROM synchronization_point WHERE synchronization_point_id=$point AND pair_id=$pair
                AND object_id=$object AND local_object_version_id IS $localVersion
                AND dropbox_object_version_id IS $remoteVersion AND local_membership_id IS $localMember
                AND dropbox_membership_id IS $remoteMember AND authority_generation=$generation;
            """, token, ("$point", pointId), ("$pair", pairId), ("$object", objectId),
            ("$localVersion", localVersion), ("$remoteVersion", remoteVersion),
            ("$localMember", localMembership), ("$remoteMember", remoteMembership), ("$generation", generation));
        if (value is null)
            throw new InvalidDataException("The committed point differs from this action's exact successor.");
        await BindCommittedOutcomeCoreAsync(connection, transaction, operationId, value, token);
    }

    private static async Task BindCommittedOutcomeCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, string value, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, """
            UPDATE operation SET completion_state_json=$value
            WHERE operation_id=$id AND status='committed'
              AND (completion_state_json IS NULL OR completion_state_json=$value);
            """, token, ("$value", value), ("$id", operationId));
        if (await ScalarLongAsync(connection, transaction, "SELECT changes();", token) != 1)
            throw new InvalidDataException("A different committed outcome is already bound to this action.");
    }

    private static async Task BindCommittedDeletionCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, string tombstoneId,
        string pairId, string objectId, CancellationToken token)
    {
        var value = await ScalarStringAsync(connection, transaction, """
            SELECT json_object('kind','resolved_tombstone','id',t.tombstone_id,
                'pairId',t.pair_id,'objectId',t.object_id,'absentSide',t.absent_side,
                'priorMembershipId',t.prior_membership_id,'priorVersionId',t.prior_object_version_id,
                'observedAbsentUtc',t.observed_absent_utc,'completeObservations',t.complete_observation_count,
                'status',t.status)
            FROM tombstone t JOIN operation o ON o.operation_id=$operation
            WHERE t.tombstone_id=$tombstone AND t.pair_id=$pair AND t.object_id=$object
                AND t.status='resolved' AND o.pair_id=t.pair_id AND o.object_id=t.object_id
                AND o.kind IN ('delete_local','delete_remote');
            """, token, ("$operation",operationId), ("$tombstone",tombstoneId),
            ("$pair",pairId), ("$object",objectId));
        if (value is null) throw new InvalidDataException("The committed deletion has no exact resolved tombstone.");
        await BindCommittedOutcomeCoreAsync(connection,transaction,operationId,value,token);
    }

    private static async Task BindCommittedManualRestoreCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, string attemptId,
        string commandId, string artifactId, string path, string fileId,
        string revision, long size, string hash, CancellationToken token)
    {
        // Called only after the manual-restore commit verified exact destination
        // identity/revision/hash/size and its original command/member binding.
        var value = await ScalarStringAsync(connection,transaction,"""
            SELECT json_object('kind','manual_restore','attemptId',a.attempt_id,
                'commandId',$command,'artifactId',$artifact,'receiptId',r.effect_receipt_id,
                'destination',json(r.destination_identity_json),'evidenceDigest',r.evidence_digest)
            FROM operation_attempt a JOIN effect_receipt r ON r.attempt_id=a.attempt_id
            WHERE a.operation_id=$operation AND a.attempt_id=$attempt AND a.status='verified_success'
                AND r.certainty='known_success'
                AND json_extract(r.destination_identity_json,'$.path')=$path
                AND json_extract(r.destination_identity_json,'$.Id')=$file
                AND json_extract(r.destination_identity_json,'$.Revision')=$revision
                AND json_extract(r.destination_identity_json,'$.Size')=$size
                AND lower(json_extract(r.destination_identity_json,'$.ContentHash'))=lower($hash)
            ORDER BY r.observed_utc DESC,r.effect_receipt_id DESC LIMIT 1;
            """,token,("$operation",operationId),("$attempt",attemptId),
            ("$command",commandId),("$artifact",artifactId),("$path",path),("$file",fileId),
            ("$revision",revision),("$size",size),("$hash",hash));
        if(value is null) throw new InvalidDataException("The committed manual restore has no exact successful receipt.");
        await BindCommittedOutcomeCoreAsync(connection,transaction,operationId,value,token);
    }

    internal async Task<SyncActionClosureResult> CloseSettledActionAsync(string operationId,
        CancellationToken token = default)
    {
        SyncActionClosureResult? result = null;
        await WriteAsync(async (connection, transaction) =>
            result = await CloseSettledActionCoreAsync(connection, transaction, operationId,
                ActivityRetentionNow, token), token);
        return result!;
    }

    private sealed record ActionGraph(string Json, string Sha256, long Bytes);
    private static readonly (string Table, string Predicate, string Order)[] ActionGraphTables =
    [
        ("operation", "operation_id=$id", "operation_id"),
        ("operation_member", "operation_id=$id", "operation_member_id"),
        ("operation_dependency", "operation_id=$id", "coalesce(depends_on_operation_id,retired_dependency_id)"),
        ("operation_step", "operation_id=$id", "operation_step_id"),
        ("resource_allocation", "operation_id=$id", "allocation_id"),
        ("namespace_allocation", "operation_id=$id", "namespace_allocation_id"),
        ("operation_attempt", "operation_id=$id", "attempt_no"),
        ("effect_receipt", "attempt_id IN (SELECT attempt_id FROM operation_attempt WHERE operation_id=$id)",
            "effect_receipt_id"),
        ("upload_session_checkpoint", "operation_id=$id", "allocation_id"),
        ("lease", "scope_kind='operation' AND scope_id=$id", "lease_id"),
        ("lease_fence", "scope_kind='operation' AND scope_id=$id", "scope_kind,scope_id")
    ];

    // Fixed owner schema only. Bounded rows/payload, deterministic column order;
    // neither caller SQL nor a provider-supplied table/identifier is accepted.
    private static async Task<ActionGraph?> ReadActionGraphCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, CancellationToken token)
    {
        var tables = new SortedDictionary<string, List<SortedDictionary<string, object?>>>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var spec in ActionGraphTables)
        {
            var rows = new List<SortedDictionary<string, object?>>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT * FROM {spec.Table} WHERE {spec.Predicate} ORDER BY {spec.Order} LIMIT 2001;";
            command.Parameters.AddWithValue("$id", operationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (rows.Count == 2000) return null;
                var row = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    if (spec.Table == "operation" && (name.StartsWith("closure_", StringComparison.Ordinal) ||
                        name.StartsWith("journal_", StringComparison.Ordinal))) continue;
                    row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(row)));
                if (bytes > MaximumActionBatchBytes) return null;
                rows.Add(row);
            }
            tables.Add(spec.Table, rows);
        }
        var json = JsonSerializer.Serialize(tables);
        bytes = Encoding.UTF8.GetByteCount(json);
        return bytes > MaximumActionBatchBytes ? null : new(json, HashActionText(json), bytes);
    }

    private static string HashActionText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<SyncActionClosureResult> CloseSettledActionCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, string id,
        DateTimeOffset now, CancellationToken token, long maximumBytes = MaximumActionBatchBytes)
    {
        string generation, intent, status, origin, execution, kind;
        string? pairId, objectId, planning;
        long sequence, revision;
        string? completion, existing;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT registration_generation,registration_sequence,registration_intent_sha256,status,
                    origin_command_id,execution_command_id,completion_state_json,closure_sha256,
                    kind,pair_id,object_id,configuration_revision,json_extract(preconditions_json,'$.planningAuthorities')
                FROM operation WHERE operation_id=$id;
                """;
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return new(id, false, "missing_action");
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) ||
                reader.IsDBNull(4) || reader.IsDBNull(5)) return new(id, false, "legacy_or_unmapped_authority");
            generation = reader.GetString(0); sequence = reader.GetInt64(1);
            intent = reader.GetString(2); status = reader.GetString(3);
            origin = reader.GetString(4); execution = reader.GetString(5);
            completion = reader.IsDBNull(6) ? null : reader.GetString(6);
            existing = reader.IsDBNull(7) ? null : reader.GetString(7);
            kind = reader.GetString(8); pairId = reader.IsDBNull(9) ? null : reader.GetString(9);
            objectId = reader.IsDBNull(10) ? null : reader.GetString(10);
            revision = reader.GetInt64(11); planning = reader.IsDBNull(12) ? null : reader.GetString(12);
        }
        if (existing is not null) return new(id, true, "already_closed");
        if (status is not ("completed" or "cancelled" or "superseded" or "blocked"))
            return new(id, false, "unfinished_action");
        async Task<long> Count(string sql) => await ScalarLongAsync(connection, transaction, sql, token, ("$id", id));
        if (await Count("""
                SELECT count(*) FROM lease WHERE scope_kind='operation' AND scope_id=$id
                    AND status='active' AND julianday(expires_utc)>julianday('now');
                """) != 0) return new(id, false, "worker_not_drained");
        if (await Count("SELECT count(*) FROM operation WHERE operation_id=$id AND current_lease_id IS NOT NULL;") != 0)
            return new(id, false, "lease_binding_not_released");
        if (await Count("""
                SELECT count(*) FROM resource_allocation WHERE operation_id=$id AND status<>'released';
                """) != 0 || await Count("""
                SELECT count(*) FROM namespace_allocation WHERE operation_id=$id AND status<>'released';
                """) != 0) return new(id, false, "allocation_or_independent_owner_held");
        if (await Count("SELECT count(*) FROM recovery_artifact WHERE operation_id=$id;") != 0)
            return new(id, false, "recovery_reference_held");
        if (await Count("""
                SELECT count(*) FROM upload_session_checkpoint WHERE operation_id=$id AND status<>'completed';
                """) != 0) return new(id, false, "upload_or_cleanup_uncertain");
        if (await Count("""
                SELECT count(*) FROM history_event WHERE operation_id=$id;
                """) != 0) return new(id, false, "legacy_history_reference");
        if (await Count("""
                SELECT count(*) FROM conflict_resolution c JOIN operation o ON
                    o.operation_group_id=c.operation_group_id WHERE o.operation_id=$id;
                """) != 0) return new(id, false, "conflict_reference_held");
        if (await Count("""
                SELECT count(*) FROM conflict c JOIN operation o
                    ON o.pair_id=c.pair_id AND o.object_id=c.object_id
                WHERE o.operation_id=$id AND c.status IN ('open','resolution_requested','resolving');
                """) != 0) return new(id, false, "unresolved_object_conflict");
        var attempts = await Count("SELECT count(*) FROM operation_attempt WHERE operation_id=$id;");
        var outcome = "unissued";
        if (attempts != 0 && status is "blocked" or "cancelled" or "superseded")
        {
            // Every issued attempt must be positively resolved as not applied.
            // A later refusal never erases an earlier success or uncertainty.
            if (await Count("""
                    SELECT count(*) FROM operation_attempt a WHERE a.operation_id=$id AND (
                      a.status NOT IN ('blocked','verified_not_applied')
                      OR NOT EXISTS(SELECT 1 FROM effect_receipt r WHERE r.attempt_id=a.attempt_id AND r.certainty='known_not_applied')
                      OR EXISTS(SELECT 1 FROM effect_receipt r WHERE r.attempt_id=a.attempt_id AND r.certainty='known_success')
                      OR (a.status='blocked' AND EXISTS(SELECT 1 FROM effect_receipt r
                        WHERE r.attempt_id=a.attempt_id AND r.certainty<>'known_not_applied')));
                    """) != 0) return new(id, false, "nonapplication_not_proved");
            if (await Count("""
                    SELECT count(*) FROM operation o JOIN command_request c ON c.command_request_id=o.execution_command_id
                    WHERE o.operation_id=$id AND c.ordinary_issuance_closed_utc IS NOT NULL;
                    """) != 1) return new(id, false, "nonapplication_command_not_closed");
            // An issued original choice still needs its exact refusal record for
            // another selected retry. Completed/expired consent no longer does.
            // Ordinary approved-pair work has independent current-state authority;
            // expiry of old optional failure detail never grants new permission.
            if (await Count("""
                    SELECT count(*) FROM operation o JOIN command_request c ON c.command_request_id=o.origin_command_id
                    WHERE o.operation_id=$id AND (
                      (o.consent_id IS NOT NULL AND EXISTS(SELECT 1 FROM consent_item ci
                        WHERE ci.consent_id=o.consent_id AND ci.ordinal=o.consent_item_ordinal
                          AND ci.lifecycle IN ('completed','expired')))
                      OR (o.consent_id IS NULL AND c.command_kind IN ('run_pair','run_all')
                        AND json_type(c.payload_json,'$.reviewedPlan') IS NULL
                        AND json_type(c.payload_json,'$.reviewedConsent') IS NULL
                        AND json_type(c.payload_json,'$.consentId') IS NULL
                        AND EXISTS(SELECT 1 FROM sync_pair p WHERE p.pair_id=o.pair_id
                          AND json_extract(p.pair_json,'$.InitialReviewApproved')=1)));
                    """) != 1) return new(id, false, "original_retry_consumer_held");
            if (completion is not null) return new(id, false, "contradictory_nonapplication_completion");
            outcome = "known_nonapplication";
        }
        else if (attempts != 0)
        {
            if (status != "completed" || completion is null)
                return new(id, false, "committed_state_not_bound");
            if (await Count("""
                    SELECT count(*) FROM operation_attempt a WHERE a.operation_id=$id AND
                        (a.status NOT IN ('verified_success','verified_not_applied')
                         OR NOT EXISTS(SELECT 1 FROM effect_receipt r WHERE r.attempt_id=a.attempt_id
                           AND r.certainty=CASE a.status WHEN 'verified_success' THEN 'known_success'
                               ELSE 'known_not_applied' END));
                    """) != 0) return new(id, false, "attempt_outcome_unresolved");
            if (await Count("""
                    SELECT count(*) FROM operation_attempt a WHERE a.operation_id=$id AND a.status='verified_success';
                    """) == 0) return new(id, false, "missing_committed_success");
            outcome = "known_success";
        }
        else
        {
            if (status == "completed") return new(id, false, "completion_without_effect_proof");
            if (status == "blocked") return new(id, false, "blocked_without_recorded_nonapplication");
            if (await Count("""
                    SELECT count(*) FROM operation o JOIN command_request c ON c.command_request_id=o.execution_command_id
                    WHERE o.operation_id=$id AND c.ordinary_issuance_closed_utc IS NOT NULL;
                    """) != 1) return new(id, false, "unissued_command_not_closed");
        }
        if (await Count("""
                SELECT count(*) FROM operation_dependency_state d
                WHERE d.operation_id=$id AND
                    ((d.dependency_kind='must_complete' AND d.prerequisite_status<>'completed')
                     OR (d.dependency_kind='must_commit' AND d.prerequisite_status NOT IN ('committed','cleanup_pending','completed'))
                     OR (d.dependency_kind='must_resolve' AND d.prerequisite_status NOT IN
                        ('committed','cleanup_pending','completed','blocked','cancelled','superseded')));
                """) != 0) return new(id, false, "prerequisite_unresolved");
        var graph = await ReadActionGraphCoreAsync(connection, transaction, id, token);
        if (graph is null) return new(id, false, "single_action_budget_held");
        var commands = await ScalarStringAsync(connection, transaction, """
            SELECT json_group_array(json_object('id',command_request_id,'generation',admission_generation,
                'sequence',admission_sequence,'purpose',admission_purpose))
            FROM (SELECT * FROM command_request WHERE command_request_id IN ($origin,$execution)
                AND admission_sequence IS NOT NULL ORDER BY command_request_id);
            """, token, ("$origin", origin), ("$execution", execution));
        using var commandFacts = JsonDocument.Parse(commands ?? "[]");
        if (commandFacts.RootElement.GetArrayLength() != (origin == execution ? 1 : 2))
            return new(id, false, "command_authority_missing");
        var certificate = JsonSerializer.Serialize(new {
            operationId = id, registrationGeneration = generation, registrationSequence = sequence,
            intentSha256 = intent, originCommand = origin, executionCommand = execution,
            commandAuthority = commandFacts.RootElement, kind, pairId, objectId, configurationRevision = revision,
            planningAuthority = JsonSerializer.Deserialize<JsonElement>(planning ?? "[]"), status, outcome,
            completion = completion is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(completion),
            immutableGraphSha256 = graph.Sha256, detailBytes = graph.Bytes,
            workerDrained = true, allocationsReleased = true,
            nonapplicationVerified = outcome == "known_nonapplication", closureUtc = Utc(now)
        });
        var charge = checked(graph.Bytes + Encoding.UTF8.GetByteCount(certificate));
        if (charge > maximumBytes) return new(id, false, "payload_budget_held");
        await ExecuteAsync(connection, transaction, """
            UPDATE operation SET closure_utc=$utc,closure_json=$json,closure_sha256=$hash,
                closure_encoded_bytes=$bytes,closure_kind=$kind
            WHERE operation_id=$id AND closure_utc IS NULL;
            """, token, ("$utc", Utc(now)), ("$json", certificate), ("$hash", HashActionText(certificate)),
            ("$bytes", charge), ("$kind", outcome), ("$id", id));
        if (await ScalarLongAsync(connection, transaction, "SELECT changes();", token) != 1)
            throw new InvalidDataException("The exact action closure was not committed.");
        return new(id, true, outcome, charge);
    }
}

