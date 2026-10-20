using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record SyncRunRetryItem(string Path, string State, string Reason, bool CanRetry,
    string? OperationId = null);
public sealed record SyncRunRetryPlan(string RunId, string? PairId, string? UnavailableReason,
    IReadOnlyList<SyncRunRetryItem> Items, long? PolicyRevision = null);

internal sealed record SyncRetryOperationEvidence(string OperationId, string? PairId,
    SyncAuthorityOperationStatus Status, string Path, bool Unissued, bool KnownNoEffectRefusal);
internal sealed record SyncRunOperationHistory(string OperationId, string? FailureReason);

internal sealed record SyncManualLocalRestoreCommand(string Id, string PairId, string RecoveryArtifactId,
    string Status, string RequestJson, string? ResultJson);

public sealed partial class SyncAuthorityDatabase
{
    internal static bool IsCompletedRetryOutcome(SyncDecisionKind? kind)=>kind is
        SyncDecisionKind.Uploaded or SyncDecisionKind.Downloaded or SyncDecisionKind.Identical or
        SyncDecisionKind.Baseline or SyncDecisionKind.Recovered or SyncDecisionKind.Archived or SyncDecisionKind.OnlineOnly;

    // List, exact lookup and run/retry navigation must classify the same retained commands.
    private const string RetainedRunCommandKindsSql =
        "'run_pair','run_all','recover_pair','recover_all','reconcile','resolve_conflict'";
    // Retry only needs state, authoritative member paths and the SAME safety predicates
    // used by individual admission checks. Do not reconstruct hundreds of full execution
    // snapshots (including allocations/attempts) or reopen the database for every row.
    internal async Task<IReadOnlyList<SyncRetryOperationEvidence>> LoadRetryOperationEvidenceAsync(
        IReadOnlyList<string> operationIds, CancellationToken cancellationToken = default)
    {
        if (operationIds.Count == 0) return [];
        foreach (var id in operationIds) RequireIdentifier(id, nameof(operationIds));
        var ids = operationIds.Distinct(StringComparer.Ordinal).ToArray();
        var serializedIds = JsonSerializer.Serialize(ids);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        var rows = new Dictionary<string, (string? Pair, SyncAuthorityOperationKind Kind,
            SyncAuthorityOperationStatus Status, bool Unissued, bool NoEffect)>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // The persisted schema has no operation-id allocation index and its
            // active-lease index starts with scope_kind. Materialize those two
            // read-only relations once so SQLite can build statement-local seek
            // indexes. The shared predicates below stay byte-for-byte unchanged,
            // including their cross-scope lease guard and partial-stage exception.
            // Qualifying the base tables avoids a circular CTE reference.
            command.CommandText = $"""
                WITH resource_allocation AS MATERIALIZED (SELECT * FROM main.resource_allocation),
                     lease AS MATERIALIZED (SELECT * FROM main.lease)
                SELECT o.operation_id,o.pair_id,o.kind,o.status,
                  CASE WHEN {UnissuedOperationPredicate} THEN 1 ELSE 0 END,
                  CASE WHEN {KnownNoEffectRefusalPredicate} THEN 1 ELSE 0 END
                FROM operation o WHERE o.operation_id IN (SELECT value FROM json_each($ids));
                """;
            command.Parameters.AddWithValue("$ids", serializedIds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(reader.GetString(0), (reader.IsDBNull(1) ? null : reader.GetString(1),
                    SyncAuthorityNames.OperationKind(reader.GetString(2)),
                    SyncAuthorityNames.OperationStatus(reader.GetString(3)), reader.GetInt64(4) == 1,
                    reader.GetInt64(5) == 1));
        }
        if (rows.Count != ids.Length) throw new InvalidDataException("The operation is not retained.");
        var members = ids.ToDictionary(id => id, _ => new List<SyncAuthorityMember>(), StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // No ORDER BY on member ID: path selection has its own shared deterministic
            // role/path ordering. Read this table once, not a full PK-index scan per operation.
            command.CommandText = """
                SELECT operation_id,operation_member_id,pair_id,object_id,role,root_binding_id,
                  membership_id,object_version_id,expected_membership_generation,expected_path_key,expected_state_json
                FROM operation_member WHERE operation_id IN (SELECT value FROM json_each($ids));
                """;
            command.Parameters.AddWithValue("$ids", serializedIds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                members[reader.GetString(0)].Add(ReadMember(reader, 1));
        }
        transaction.Commit();
        // The retained history order remains decisive if several operations name one path.
        return operationIds.Select(id => new SyncRetryOperationEvidence(id, rows[id].Pair, rows[id].Status,
            SyncAuthorityPairRunner.RecoveryDecisionPath(rows[id].Kind, members[id]), rows[id].Unissued,
            rows[id].NoEffect)).ToArray();
    }

    // Retry needs exact run/operation membership, not presentation formatting. Read
    // that retained relation once instead of rebuilding the whole activity projection
    // for every 500-row page. Last-observed ordering matches the former cursor walk.
    public async Task<IReadOnlyList<string>> LoadRunOperationIdsAsync(string runId, string pairId,
        CancellationToken cancellationToken = default)
        => await LoadRunOperationIdsForRunsAsync([runId], pairId, cancellationToken);

    internal async Task<IReadOnlyList<string>> LoadRunOperationIdsForRunsAsync(IReadOnlyList<string> runIds,
        string pairId, CancellationToken cancellationToken = default)
        => (await LoadRunOperationHistoryForRunsAsync(runIds, pairId, cancellationToken))
            .Select(item => item.OperationId).ToArray();

    internal async Task<IReadOnlyList<SyncRunOperationHistory>> LoadRunOperationHistoryForRunsAsync(
        IReadOnlyList<string> runIds, string pairId, CancellationToken cancellationToken = default)
    {
        if (runIds.Count is < 1 or > 101) throw new ArgumentException("Use the original run and its bounded retained retry lineage.", nameof(runIds));
        foreach (var runId in runIds) RequireIdentifier(runId, nameof(runIds));
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadRunOperationHistoryCoreAsync(connection, null, runIds, pairId, cancellationToken);
    }

    private static async Task<IReadOnlyList<SyncRunOperationHistory>> LoadRunOperationHistoryCoreAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        IReadOnlyList<string> runIds, string pairId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH commands AS MATERIALIZED (
                SELECT *,json_extract(payload_json,'$.policyRevision') AS policy_revision,
                  json_extract(payload_json,'$.reviewedPlan.PairId') AS reviewed_pair,
                  json_extract(payload_json,'$.reviewedPlan.LocalScanGenerationId') AS local_scan,
                  json_extract(payload_json,'$.reviewedPlan.DropboxScanGenerationId') AS remote_scan
                FROM command_request WHERE command_request_id IN (SELECT value FROM json_each($runs))
                  AND pair_id=$pair AND command_kind='run_pair' AND json_valid(payload_json)
            ), choices AS MATERIALIZED (
                SELECT c.command_request_id,json_extract(j.value,'$.RelativePath') AS path,
                  json_extract(j.value,'$.Action') AS action
                FROM commands c,json_each(c.payload_json,'$.reviewedPlan.Choices') j
            ), owned_groups AS MATERIALIZED (
                SELECT g.operation_group_id,c.command_request_id,c.policy_revision,c.local_scan,c.remote_scan,
                  json_extract(g.detail_json,'$.runId') IS NOT NULL AS explicit_owner
                FROM commands c JOIN operation_group g ON json_valid(g.detail_json)
                  AND json_extract(g.detail_json,'$.pairId')=$pair
                WHERE json_extract(g.detail_json,'$.runId')=c.command_request_id OR (
                  json_extract(g.detail_json,'$.runId') IS NULL AND g.status='planned'
                  AND json_extract(g.detail_json,'$.purpose')='reviewed_first_sync'
                  AND c.reviewed_pair=$pair
                  AND c.terminal_utc IS NOT NULL
                  AND g.created_utc>=c.requested_utc AND g.created_utc<=c.terminal_utc
                  AND NOT EXISTS(SELECT 1 FROM command_request other
                    WHERE other.command_request_id<>c.command_request_id AND other.pair_id=$pair
                      AND other.command_kind IN ('run_pair','recover_pair','reconcile','resolve_conflict')
                      AND other.requested_utc<=g.created_utc
                      AND (other.terminal_utc IS NULL OR other.terminal_utc>=g.created_utc))
                )
            ), group_operations AS (
                SELECT o.operation_id FROM owned_groups g JOIN operation o
                  ON o.operation_group_id=g.operation_group_id AND o.pair_id=$pair
                  AND o.configuration_revision=g.policy_revision
                WHERE g.explicit_owner OR (
                  o.status IN ('ready','superseded') AND json_valid(o.preconditions_json)
                  AND NOT EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id)
                  AND json_extract(o.preconditions_json,'$.localScanGenerationId')=g.local_scan
                  AND json_extract(o.preconditions_json,'$.dropboxScanGenerationId')=g.remote_scan
                  AND EXISTS(SELECT 1 FROM choices ch WHERE ch.command_request_id=g.command_request_id
                    AND ch.path=json_extract(o.preconditions_json,'$.path') COLLATE NOCASE
                    AND ((ch.action='Upload' AND o.kind='create_remote')
                      OR (ch.action='Download' AND o.kind='create_local')))
                )
            ), events AS MATERIALIZED (
                SELECT rowid AS sequence,detail_json FROM history_event
                WHERE event_kind='presentation' AND json_valid(detail_json)
                  AND json_extract(detail_json,'$.RunId') IN (SELECT value FROM json_each($runs))
                  AND json_extract(detail_json,'$.PairId')=$pair
                  AND coalesce(json_extract(detail_json,'$.OperationId'),'')<>''
            ), operations AS (
                SELECT json_extract(detail_json,'$.OperationId') AS operation_id,
                  max(sequence) AS last_sequence,
                  max(CASE WHEN json_extract(detail_json,'$.Phase')='Blocked' THEN sequence END) AS failure_sequence
                FROM events GROUP BY json_extract(detail_json,'$.OperationId')
            ), identities AS (
                SELECT operation_id FROM operations UNION SELECT operation_id FROM group_operations
            )
            SELECT ids.operation_id,json_extract(failure.detail_json,'$.Message')
            FROM identities ids JOIN operation o ON o.operation_id=ids.operation_id
              LEFT JOIN operations h ON h.operation_id=ids.operation_id
              LEFT JOIN events failure ON failure.sequence=h.failure_sequence
            ORDER BY o.created_utc,coalesce(h.last_sequence,0),ids.operation_id;
            """;
        // Group ownership is retained before planning starts, independently of UI
        // progress events. Pre-linkage legacy first-sync groups are admitted only
        // for one unambiguous command window AND exact original scans, revision,
        // approved path/direction and no issued attempt. Unknown lineage stays out.
        // history_event also has a physical operation_id column, NULL on all
        // presentation rows. SQLite resolves GROUP BY operation_id to that
        // column, not the SELECT alias; use the exact JSON expression above.
        command.Parameters.AddWithValue("$runs", JsonSerializer.Serialize(runIds.Distinct(StringComparer.Ordinal)));
        command.Parameters.AddWithValue("$pair", pairId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SyncRunOperationHistory>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    public async Task<IReadOnlyList<SyncAuthorityRunHistory>> LoadRunRetriesAsync(string runId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(runId, nameof(runId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE linked(id) AS (
                SELECT command_request_id FROM command_request
                WHERE json_valid(payload_json) AND json_extract(payload_json,'$.retryOfRunId')=$id
                UNION
                SELECT child.command_request_id FROM command_request child JOIN linked parent
                ON json_valid(child.payload_json) AND json_extract(child.payload_json,'$.retryOfRunId')=parent.id
                LIMIT 100
            )
            SELECT command_request_id,command_kind,requested_utc,terminal_utc,status,payload_json,result_json
            FROM command_request JOIN linked ON linked.id=command_request_id ORDER BY requested_utc DESC;
            """;
        command.Parameters.AddWithValue("$id", runId);
        var rows = new List<SyncAuthorityRunHistory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return rows;
    }

    public async Task<SyncAuthorityRunHistory?> LoadRunByIdAsync(string runId,
        CancellationToken cancellationToken = default, Action<string>? onReadProgress = null)
        => await LoadRunByIdCoreAsync(runId, cancellationToken, onReadProgress, inspectionOnly: false);

    internal Task<SyncAuthorityRunHistory?> InspectRetryOriginAsync(string runId,
        CancellationToken cancellationToken, Action<string>? onReadProgress)
        => LoadRunByIdCoreAsync(runId, cancellationToken, onReadProgress, inspectionOnly: true);

    private async Task<SyncAuthorityRunHistory?> LoadRunByIdCoreAsync(string runId,
        CancellationToken cancellationToken, Action<string>? onReadProgress, bool inspectionOnly)
    {
        RequireIdentifier(runId, nameof(runId));
        onReadProgress?.Invoke("Recovery: opening retained state for run " + runId +
            (inspectionOnly || TentativeCommandInstallation is not null
                ? "; read-only approval lookup, not health or effect admission. No file transfer is running."
                : "; a cold connection must pass database health before this read. No file transfer is running."));
        // Preview discovers the exact target before startup admission. Reuse the
        // existing schema-checked, native read-only inspection route; do not set
        // _healthVerified or treat this row as permission for any effect. Fresh
        // command admission must still check all retained state.
        await using var connection = inspectionOnly
            ? await OpenReadOnlyConnectionAsync(cancellationToken)
            : await OpenConnectionAsync(cancellationToken, onReadProgress);
        onReadProgress?.Invoke("Recovery: reading the exact retained command_request row for run " + runId + ".");
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT command_request_id,command_kind,requested_utc,terminal_utc,status,payload_json,result_json FROM command_request WHERE command_request_id=$id AND command_kind IN ({RetainedRunCommandKindsSql});";
        command.Parameters.AddWithValue("$id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetString(0), reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)) : null;
    }

    internal async Task<SyncManualLocalRestoreCommand?> LoadManualLocalRestoreCommandAsync(string commandId,
        CancellationToken cancellationToken = default, Action<string>? onReadProgress = null,
        bool inspectionOnly = false)
    {
        RequireIdentifier(commandId, nameof(commandId));
        onReadProgress?.Invoke("Recovery: opening retained state for run " + commandId +
            (inspectionOnly || TentativeCommandInstallation is not null
                ? "; read-only command-kind lookup, not health or effect admission. No file transfer is running."
                : "; a cold connection must pass database health before checking its command kind. No file transfer is running."));
        await using var connection = inspectionOnly
            ? await OpenReadOnlyConnectionAsync(cancellationToken)
            : await OpenConnectionAsync(cancellationToken, onReadProgress);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT command_request_id,pair_id,target_id,status,payload_json,result_json
            FROM command_request WHERE command_request_id=$id AND command_kind='reconcile'
              AND pair_id IS NOT NULL AND target_id IS NOT NULL
              AND json_extract(payload_json,'$.action')='restore_recovery'
              AND json_extract(payload_json,'$.source')='LocalArchive';
            """;
        command.Parameters.AddWithValue("$id", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)) : null;
    }

    // Exact accepted-command/source join: verification must not materialize the full recovery history.
    internal async Task<SyncRecoveryArtifactProjection?> LoadManualLocalRestoreSourceAsync(string commandId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(commandId, nameof(commandId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ra.recovery_artifact_id,ra.operation_id,o.pair_id,ra.object_id,ra.side,
                   coalesce((SELECT expected_path_key FROM operation_member m
                             WHERE m.operation_id=ra.operation_id AND expected_path_key IS NOT NULL
                             ORDER BY CASE role WHEN 'affected' THEN 0 WHEN 'source' THEN 1 ELSE 2 END
                             LIMIT 1),json_extract(ra.identity_json,'$.originalPath'),'<unknown>'),
                   ra.identity_json,ra.content_hash,ra.size_bytes,ra.status,ra.retained_utc
            FROM command_request c
            JOIN recovery_artifact ra ON ra.recovery_artifact_id=c.target_id
            JOIN operation o ON o.operation_id=ra.operation_id AND o.pair_id=c.pair_id
            WHERE c.command_request_id=$id AND c.command_kind='reconcile'
              AND json_extract(c.payload_json,'$.action')='restore_recovery'
              AND json_extract(c.payload_json,'$.source')='LocalArchive' AND ra.side='local';
            """;
        command.Parameters.AddWithValue("$id", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt64(8), reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10),
                    System.Globalization.CultureInfo.InvariantCulture)) : null;
    }
}

public sealed partial class SyncAuthorityRuntimeHost
{
    public async Task<SyncAuthorityBatchResult> VerifyRunOutcomesAsync(string runId,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null, CancellationToken cancellationToken = default,
        Action<string>? onReadProgress = null)
    {
        stateAuthority.VerifyAuthority();
        var manual = await _authority.LoadManualLocalRestoreCommandAsync(runId, cancellationToken, onReadProgress);
        if (manual is not null)
        {
            // Cold startup blocks abandoned commands without performing any file transfer.
            await LoadPairsAsync(cancellationToken, onReadProgress);
            return await VerifyManualLocalRestoreAccessAsync(runId, onProgress, cancellationToken,
                onReadProgress);
        }
        var run = await _authority.LoadRunByIdAsync(runId, cancellationToken, onReadProgress)
            ?? throw new InvalidDataException("The exact requested run was not retained.");
        if (run.CompletedUtc is null) throw new InvalidOperationException("The run is still active. Stop it safely or wait for its outcome first.");
        using var request = JsonDocument.Parse(run.RequestJson);
        var ids = request.RootElement.TryGetProperty("pairIds", out var pairIds)
            ? pairIds.Deserialize<string[]>() ?? []
            : request.RootElement.TryGetProperty("pairId", out var pairId) && pairId.GetString() is { Length: > 0 } id
                ? [id] : Array.Empty<string>();
        if (!await _runGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another sync command is already active.");
        using var preparationOwnership = new PairConfigurationMutationLease(_runGate);
        var admissionStartedUtc = DateTimeOffset.UtcNow;
        var admissionStages = new List<SyncComparisonProgress>();
        void ReportAdmission(string message)
        {
            admissionStages.Add(new("Preparing Command", "", 0, 0, admissionStartedUtc,
                DateTimeOffset.UtcNow, message, "preparation steps"));
            onReadProgress?.Invoke(message);
        }
        ReportAdmission("Admission: verifying retained operations without changing Explorer storage choices or scheduling new synchronization.");
        ReportAdmission("Recovery: loading the current folder-pair bindings named by the retained run; no transfer has started.");
        var current = (await LoadPairsAsync(cancellationToken, ReportAdmission)).Pairs;
        var pairs = ids.Select(id => current.SingleOrDefault(pair => pair.Id == id)
            ?? throw new InvalidDataException("An original pair is retired; its recovery requires an explicit current binding.")).ToArray();
        if (pairs.Length == 0) throw new InvalidDataException("This run has no retained pair identity to verify.");
        var retries = await _authority.LoadRunRetriesAsync(runId, cancellationToken);
        if (retries.Any(item => item.CompletedUtc is null))
            throw new InvalidOperationException("A linked retry is still active. Stop it safely before verification.");
        var lineage = new[] { runId }.Concat(retries.Select(item => item.Id)).ToArray();
        var operationSets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var pair in pairs)
            operationSets[pair.Id] = await _authority.LoadRunOperationIdsForRunsAsync(lineage, pair.Id, cancellationToken);
        var commandId = await CreateAcceptedCommandAsync("reconcile",
            pairs.Length == 1 ? pairs[0].Id : null, null,
            JsonSerializer.Serialize(new { verificationOfRunId = runId, pairIds = ids, operationSets }),
            cancellationToken, admissionProgress: ReportAdmission);
        var commandAdmission = await RequireRetainedCommandAdmissionAsync(commandId, cancellationToken);
        PilotRunControl? control = null;
        try
        {
            ReportAdmission("Admission: command " + commandId + " accepted; acquiring its run ownership before outcome verification.");
            control = CreateRunControl(commandId, "reconcile",
                pairs.Length == 1 ? pairs[0].Id : null);
            preparationOwnership.TransferOwnershipToRun();
            foreach (var stage in admissionStages)
                await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pairs[0],
                    "Checking", null, null, null, Comparison: stage));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, control.StopToken);
            cancellationToken = linked.Token;
            await using var databaseConnections = _authority.BeginConnectionReuseScope();
            await _authority.TransitionCommandRequestAsync(commandId, "executing", cancellationToken: cancellationToken);
            var results = new List<SyncAuthorityBatchItemResult>();
            foreach (var pair in pairs)
            {
                try
                {
                    onReadProgress?.Invoke("Verification: reading retained Rewind ownership for " + pair.Name + ".");
                    if (await _rewindStore.LoadActiveAsync(pair.Id, cancellationToken) is not null)
                        throw new InvalidOperationException("Resolve this pair's retained Rewind checkpoint before verifying sync outcomes.");
                    var prepared = await PreparePairWithProgressAsync(pair, cancellationToken, onProgress);
                    var runner = CreatePairRunner(pair, prepared, control, commandAdmission, prepared.CloudFiles,
                        onProgress: onProgress);
                    var result = await runner.VerifyAsync(operationSets[pair.Id], cancellationToken);
                    results.Add(new(pair, result, null, null));
                    await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair,
                        result.NeedsVerification > 0 || result.Blocked > 0 ? "Needs attention" : "Completed",
                        result, null, null));
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    var failed = new SyncAuthorityBatchItemResult(pair, null, error.GetType().Name, SyncActivityText.Failure(error));
                    results.Add(failed);
                    await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair, "Needs attention",
                        null, failed.FailureType, failed.FailureMessage));
                }
            }
            var batch = new SyncAuthorityBatchResult(results, control.IsStopRequested);
            await _authority.TransitionCommandRequestAsync(commandId, "completed",
                JsonSerializer.Serialize(new { verificationOfRunId = runId,
                    pairs = RetainedRunSummaries(results), batch.Stopped }),
                cancellationToken: CancellationToken.None);
            return batch;
        }
        catch (Exception error)
        {
            await RetainRunCommandFailureAsync(commandId, pairs, "verification_failed",
                "Verifying the selected run's retained operations", error);
            throw;
        }
        finally
        {
            if (control is not null) { await FlushActivityProgressAsync(); EndRun(control); }
        }
    }

    private sealed record SyncRetryOriginNode(SyncAuthorityRunHistory Run, SyncBootstrapActionPlan? Plan,
        SyncReviewedConsentReference? Consent, long? Revision);

    private async Task<(SyncAuthorityRunHistory Run, SyncBootstrapActionPlan? Plan, long? Revision)> ReadRetryOriginAsync(
        string runId, CancellationToken token, Action<string>? onReadProgress = null, bool inspectionOnly = false,
        IReadOnlySet<string>? materializePaths = null)
    {
        var lineage = new List<SyncRetryOriginNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!visited.Add(runId) || visited.Count > 100)
                throw new InvalidDataException("The reviewed retry ancestry is cyclic or exceeds its bounded history. No file action was authorized.");
            var run = await (inspectionOnly
                ? _authority.InspectRetryOriginAsync(runId, token, onReadProgress)
                : _authority.LoadRunByIdAsync(runId, token, onReadProgress))
                ?? throw new InvalidDataException("The exact requested retry ancestor was not retained.");
            using var request = JsonDocument.Parse(run.RequestJson);
            var root = request.RootElement;
            var plan = root.TryGetProperty("reviewedPlan", out var retainedPlan) &&
                retainedPlan.ValueKind != JsonValueKind.Null
                ? retainedPlan.Deserialize<SyncBootstrapActionPlan>() : null;
            // A new retry keeps no second copy of the plan. It retains one compact
            // reference to the original terminal approval plus its own selection
            // filter. Both representations in one command contradict each other, and
            // a present-but-malformed reference is never treated as an ordinary run.
            SyncReviewedConsentReference? consent = null;
            if (root.TryGetProperty(SyncReviewedConsentReference.PayloadPropertyName, out var retainedConsent) &&
                retainedConsent.ValueKind != JsonValueKind.Null)
                consent = SyncReviewedConsentReference.ReadRequired(retainedConsent);
            // A consent-backed original keeps no inline plan. Only the deliberately selected ordinals are
            // decoded from the normalized original tables; an unselected original choice body is never read
            // merely to resolve one retry, and a missing, expired or completed selected item refuses.
            var normalizedConsentId = root.TryGetProperty(
                    SyncReviewedConsentReference.ConsentIdPropertyName, out var retainedConsentId) &&
                retainedConsentId.ValueKind == JsonValueKind.String ? retainedConsentId.GetString() : null;
            if (plan is null && !string.IsNullOrWhiteSpace(normalizedConsentId))
                plan = await _authority.MaterializeRetainedOriginalConsentPlanAsync(normalizedConsentId!,
                    materializePaths, token);
            if (plan is not null && consent is not null)
                throw new InvalidDataException("A retry command retained both an inline plan and a compact consent reference. No file action was authorized.");
            long? revision = root.TryGetProperty("policyRevision", out var retainedRevision)
                ? retainedRevision.GetInt64() : null;
            if (plan is null && consent is null)
            {
                if (lineage.Count != 0)
                    throw new InvalidDataException("A reviewed retry ancestor has no original file approval.");
                return (run, null, revision);
            }
            if (root.TryGetProperty("retryOfRunId", out var parent) && parent.ValueKind == JsonValueKind.String)
            {
                if (consent is not null && (parent.GetString() ?? string.Empty).Length == 0)
                    throw new InvalidDataException("A compact retry consent reference must name its exact immediate ancestor.");
                lineage.Add(new SyncRetryOriginNode(run, plan, consent, revision));
                runId = parent.GetString() ?? throw new InvalidDataException("A retry has no exact parent identity.");
                continue;
            }
            if (consent is not null)
                throw new InvalidDataException("A compact retry consent reference must name its exact immediate ancestor.");
            lineage.Add(new SyncRetryOriginNode(run, plan, consent, revision));
            break;
        }
        var original = lineage[^1];
        var originalPlan = original.Plan
            ?? throw new InvalidDataException("The retained retry ancestry has no original file approval.");
        var approved = originalPlan.Choices.ToDictionary(
            item => PilotPath.NormalizeRelative(item.RelativePath), StringComparer.OrdinalIgnoreCase);
        foreach (var child in lineage)
        {
            // A compact retry names the exact terminal original approval and keeps
            // only its own selection filter. Retained inline children keep every
            // existing plan equality and skip-filter check.
            if (child.Consent is not null)
            {
                if (!string.Equals(child.Consent.CommandId, original.Run.Id, StringComparison.Ordinal))
                    throw new InvalidDataException("A retry consent reference does not name its actual terminal reviewed origin.");
                if (!string.Equals(child.Consent.RequestSha256,
                        SyncReviewedConsentReference.LowercaseRequestSha256(original.Run.RequestJson), StringComparison.Ordinal))
                    throw new InvalidDataException("A retry consent reference does not match the retained original request.");
                if (child.Revision != original.Revision || !NamesReviewedPair(child.Run.RequestJson, originalPlan.PairId))
                    throw new InvalidDataException("A retry consent reference changed the original pair, policy or reviewed inventories.");
                SyncReviewedConsentReference.RequireSelectionAgainstRoot(child.Consent, originalPlan);
                continue;
            }
            if (child.Revision != original.Revision || child.Plan!.PairId != originalPlan.PairId ||
                child.Plan.LocalScanGenerationId != originalPlan.LocalScanGenerationId ||
                child.Plan.DropboxScanGenerationId != originalPlan.DropboxScanGenerationId ||
                child.Plan.Choices.Count != approved.Count)
                throw new InvalidDataException("The retry ancestry changed the original pair, policy or reviewed inventories.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var choice in child.Plan.Choices)
            {
                var path = PilotPath.NormalizeRelative(choice.RelativePath);
                if (!seen.Add(path) || !approved.TryGetValue(path, out var rootChoice) ||
                    choice.ExpectedGroup != rootChoice.ExpectedGroup || choice.Move != rootChoice.Move ||
                    (choice.Action != "Skip for now" && choice.Action != rootChoice.Action))
                    throw new InvalidDataException("A retry ancestor changed an originally reviewed file action.");
            }
        }
        // A child's Skip is an effect filter, not new user consent. Read the
        // complete original plan and its already-recursive descendant receipts
        // so retry-of-retry can finish without replaying successful ancestors.
        return (original.Run, originalPlan, original.Revision);
    }

    /// <summary>The exact pair a retained request names itself, never a borrowed root choice.</summary>
    private static bool NamesReviewedPair(string requestJson, string pairId)
    {
        using var request = JsonDocument.Parse(requestJson);
        var root = request.RootElement;
        if (root.TryGetProperty("pairIds", out var pairIds) && pairIds.ValueKind == JsonValueKind.Array)
            return pairIds.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String &&
                string.Equals(item.GetString(), pairId, StringComparison.Ordinal));
        return root.TryGetProperty("pairId", out var single) && single.ValueKind == JsonValueKind.String &&
            string.Equals(single.GetString(), pairId, StringComparison.Ordinal);
    }

    public async Task<SyncRunRetryPlan> LoadRunRetryAsync(string runId, CancellationToken cancellationToken = default,
        string? selectedPairId = null, Action<string>? onReadProgress = null)
    {
        using var preparation = _authority.BeginColdCommandPreparation(
            await InspectColdCommandPreparationAsync(cancellationToken));
        return await LoadRunRetryCoreAsync(runId, cancellationToken, selectedPairId, onReadProgress);
    }

    private async Task<SyncRunRetryPlan> LoadRunRetryCoreAsync(string runId, CancellationToken cancellationToken,
        string? selectedPairId, Action<string>? onReadProgress, string? executingRetryId = null)
    {
        stateAuthority.VerifyAuthority();
        onReadProgress?.Invoke("Reading the retained approval for run " + runId +
            "; read-only target lookup does not authorize effects; fresh command admission is still required. No file transfer is running.");
        if (await _authority.LoadExpiredConsentPairAsync(runId, cancellationToken) is { } expiredPair)
            return new(runId, expiredPair,
                "Original selection details have expired. Review a new selection; expired choices cannot authorize retry.", []);
        var (run, plan, revision) = await ReadRetryOriginAsync(runId, cancellationToken, onReadProgress,
            inspectionOnly: true);
        var manual = await _authority.LoadManualLocalRestoreCommandAsync(runId, cancellationToken, onReadProgress,
            inspectionOnly: true);
        if (manual is not null)
        {
            if (selectedPairId is not null && selectedPairId != manual.PairId)
                return new(runId, manual.PairId, "The selected pair does not belong to this manual restore.", []);
            if (manual.Status != "blocked")
                return new(runId, manual.PairId, manual.Status == "completed"
                    ? "This manual restore completed. Its retained effects cannot be replayed as a sync retry."
                    : "This manual restore is still active or is not eligible for interrupted-access verification.", []);
            using var manualRequest = JsonDocument.Parse(manual.RequestJson);
            var path = manualRequest.RootElement.TryGetProperty("TargetRelativePath", out var target) &&
                       target.ValueKind == JsonValueKind.String ? target.GetString() : null;
            if (string.IsNullOrWhiteSpace(path))
                return new(runId, manual.PairId, "The original manual restore has no exact retained destination.", []);
            return new(runId, manual.PairId, null,
                [new SyncRunRetryItem(PilotPath.NormalizeRelative(path), "Verify first",
                    "Verify the original command's isolated local access only. No restore or sync effect is retried; completion stays unresolved.", false)]);
        }
        if (run.CompletedUtc is null)
            return new(runId, plan?.PairId, "This run is still active. Wait for its current file or stop it safely first.", []);
        if (run.Kind == "resolve_conflict")
            return new(runId, selectedPairId,
                "This run approved exact conflict generations and choices, not a normal sync retry. Open Conflicts to review the remaining original targets; verify uncertain operation receipts first. Completed resolutions must not be replayed.", []);
        // A scan is retained as a run for diagnostics, not as consent to copy files.
        // Its pair identity is useful for navigation only; never infer retry authority from it.
        using (var request = JsonDocument.Parse(run.RequestJson))
        {
            var scan = request.RootElement;
            if (plan is null && scan.TryGetProperty("trigger", out var trigger) &&
                trigger.ValueKind == JsonValueKind.String && trigger.GetString() == "First sync scan")
            {
                var scanPairs = scan.TryGetProperty("pairIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                    ? ids.Deserialize<string[]>() ?? [] : [];
                var scanPairId = scanPairs.Length == 1 ? scanPairs[0] : null;
                if (selectedPairId is not null && selectedPairId != scanPairId)
                    return new(runId, scanPairId, "The selected pair does not belong to this scan.", []);
                return new(runId, scanPairId,
                    "This run only scanned the folder inventory; it did not approve or start any file actions. Open First Sync for this pair: use Scan Again to retry the scan, or review and confirm your file choices before syncing.", []);
            }
        }
        var outcomes = new Dictionary<string, SyncDecision>(StringComparer.OrdinalIgnoreCase);
        string? pairId = plan?.PairId;
        if (run.ResultJson is { } resultJson)
        {
            using var result = JsonDocument.Parse(resultJson);
            var scope = result.RootElement;
            if (plan is null && scope.TryGetProperty("pairs", out var pairs))
            {
                var summaries = pairs.EnumerateArray().ToArray();
                if (selectedPairId is null && summaries.Length != 1)
                    return new(runId, null, "This run contains several pairs. Select a file row for the pair to retry, or supply --pair in the CLI.", []);
                scope = summaries.SingleOrDefault(item => selectedPairId is null || item.GetProperty("pairId").GetString() == selectedPairId);
                if (scope.ValueKind == JsonValueKind.Undefined)
                    return new(runId, selectedPairId, "That pair is not part of the retained run.", []);
                pairId = scope.GetProperty("pairId").GetString();
                revision = scope.TryGetProperty("policyRevision", out var retainedRevision) ? retainedRevision.GetInt64() : null;
            }
            if (scope.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                foreach (var item in files.Deserialize<SyncDecision[]>() ?? []) outcomes[item.RelativePath] = item;
        }
        if (pairId is null)
            return new(runId, null, "This run has no retained per-file approval or result. Its log remains available; review current first-sync choices or start a fresh already-approved pair. No approval is inferred.", []);
        if (selectedPairId is not null && selectedPairId != pairId)
            return new(runId, pairId, "The selected pair does not belong to this approval.", []);
        onReadProgress?.Invoke("Checking the retained pair identity and policy revision for " + pairId + ".");
        var pair = (await LoadPairsAsync(cancellationToken, onReadProgress)).Pairs.SingleOrDefault(item => item.Id == pairId);
        if (pair is null) return new(runId, pairId, "The original folder pair has been retired.", []);
        if (plan is not null && pair.InitialReviewApproved != false)
            return new(runId, pairId, "First sync is already complete. Run a fresh normal sync; completed transfers will not be replayed.", []);
        if (plan is null && pair.InitialReviewApproved == false)
            return new(runId, pairId, "This pair still needs first-sync approval. A normal-run retry cannot approve new files.", []);
        if (revision != pair.PolicyRevision)
            return new(runId, pairId, "The run's policy revision is missing or changed. Review the current pair settings before starting a new sync.", []);
        onReadProgress?.Invoke("Reading linked retry outcomes; completed files remain excluded from retry.");
        var retries = await _authority.LoadRunRetriesAsync(run.Id, cancellationToken);
        if (retries.Count == 100)
            return new(runId, pairId, "This run has at least 100 linked retries. Open the latest retry to avoid making decisions from an incomplete lineage.", []);
        if (retries.Any(retry => retry.CompletedUtc is null && retry.Id != executingRetryId))
            return new(runId, pairId, "A retry of this run is still active. Open that retry to see its progress.", []);
        var completedByRetry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var retry in retries)
        {
            if (retry.ResultJson is not { } retryJson) continue;
            using var retryDocument = JsonDocument.Parse(retryJson);
            var retryRoot = retryDocument.RootElement;
            var retryScopes = retryRoot.TryGetProperty("pairId", out var rootPairId) && rootPairId.GetString() == pairId
                ? new[] { retryRoot } : retryRoot.TryGetProperty("pairs", out var retryPairs)
                    ? retryPairs.EnumerateArray().ToArray() : [];
            foreach (var retryPair in retryScopes)
            {
                if (!retryPair.TryGetProperty("pairId", out var id) || id.GetString() != pairId ||
                    !retryPair.TryGetProperty("files", out var retryFiles)) continue;
                foreach (var file in retryFiles.Deserialize<SyncDecision[]>() ?? [])
                    if (SyncAuthorityDatabase.IsCompletedRetryOutcome(file.Kind))
                        completedByRetry.TryAdd(file.RelativePath, retry.Id);
            }
        }
        var operations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var retainedOperations = new Dictionary<string, SyncRetryOperationEvidence>();
        onReadProgress?.Invoke("Reading exact operation identities recorded for run " + runId +
            " and its " + retries.Count + " linked retries, including failed retries without terminal file summaries.");
        var operationHistory = await _authority.LoadRunOperationHistoryForRunsAsync(
            new[] { run.Id }.Concat(retries.Select(retry => retry.Id)).ToArray(), pairId, cancellationToken);
        var operationIds = operationHistory.Select(item => item.OperationId).ToArray();
        var failureReasons = operationHistory.Where(item => !string.IsNullOrWhiteSpace(item.FailureReason))
            .ToDictionary(item => item.OperationId, item => item.FailureReason!, StringComparer.Ordinal);
        onReadProgress?.Invoke($"Checking retained paths, operation states, leases and effect receipts for {operationIds.Length:N0} operations.");
        foreach (var operation in await _authority.LoadRetryOperationEvidenceAsync(operationIds, cancellationToken))
        {
            retainedOperations[operation.OperationId] = operation;
            // The operation's own retained member path is authoritative. A progress
            // row naming a different prerequisite must never mark it completed.
            if (operation.PairId == pairId)
                operations[operation.Path] = operation.OperationId;
        }
        var consentStates = new Dictionary<string, SyncAuthorityConsentItemLifecycle>(StringComparer.OrdinalIgnoreCase);
        if (plan?.ConsentLayout is { } consentLayout)
        {
            if (consentLayout.SurvivingStates.Count != plan.Choices.Count)
                throw new InvalidDataException("The retained original choice states are incomplete.");
            for (var i = 0; i < plan.Choices.Count; i++)
                consentStates.Add(plan.Choices[i].RelativePath, consentLayout.SurvivingStates[i]);
        }
        var items = new List<SyncRunRetryItem>();
        var paths = plan is not null ? plan.Choices.Where(item => item.Action != "Skip for now").Select(item => item.RelativePath)
            : outcomes.Keys.Concat(operations.Keys).Where(path => !path.StartsWith('<')).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            outcomes.TryGetValue(path, out var outcome);
            operations.TryGetValue(path, out var operationId);
            var operation = operationId is null ? null : retainedOperations[operationId];
            // The latest retained operation is decisive even when its enclosing
            // retry stopped before it could write a per-file terminal summary.
            // A later uncertain effect must not be hidden by an older success.
            var unissued = operation?.Unissued == true;
            if (operation is not null && operation.Status != SyncAuthorityOperationStatus.Completed &&
                !unissued && !operation.KnownNoEffectRefusal)
            {
                items.Add(new(path, "Verify first", outcome?.Reason ??
                    "The retained operation may have an effect or unfinished cleanup. Verify its outcome before a new attempt.", false, operationId));
                continue;
            }
            if (consentStates.TryGetValue(path, out var consentState))
            {
                if (consentState == SyncAuthorityConsentItemLifecycle.Completed)
                { items.Add(new(path, "Completed", "The original choice is completed; retained history cannot authorize replay.", false, operationId)); continue; }
                if (consentState == SyncAuthorityConsentItemLifecycle.Expired)
                { items.Add(new(path, "Expired", "The original choice has expired. Review a new selection before issuing work.", false, operationId)); continue; }
                if (consentState == SyncAuthorityConsentItemLifecycle.Issued && operation is null)
                { items.Add(new(path, "Verify first", "The original choice was issued but its exact outcome is not available here.", false, operationId)); continue; }
            }
            if (completedByRetry.TryGetValue(path, out var completedRun))
            { items.Add(new(path, "Completed", "Completed by linked retry " + completedRun + "; the original failure history is unchanged.", false, operationId)); continue; }
            var complete = operation?.Status == SyncAuthorityOperationStatus.Completed ||
                SyncAuthorityDatabase.IsCompletedRetryOutcome(outcome?.Kind);
            if (complete)
            { items.Add(new(path, "Completed", "Already completed; never replayed by retry.", false, operationId)); continue; }
            // A deadline can prevent the enclosing command's terminal file summary.
            // Retained, already-redacted per-operation failure events still explain
            // the failure. They affect presentation only: current operation/receipt
            // guards above remain decisive for uncertainty, completion and retry.
            if (outcome?.Kind is not (SyncDecisionKind.Skipped or SyncDecisionKind.Conflict) &&
                operation is { KnownNoEffectRefusal: true } &&
                failureReasons.TryGetValue(operation.OperationId, out var retainedFailure))
            { items.Add(new(path, "Failed", retainedFailure, true, operationId)); continue; }
            if (outcome?.Kind == SyncDecisionKind.Skipped)
            { items.Add(new(path, "Skipped", outcome.Reason, false, operationId)); continue; }
            var conflict = outcome?.Kind == SyncDecisionKind.Conflict;
            items.Add(new(path, conflict ? "Conflict" : !unissued && outcome is { Kind: SyncDecisionKind.Blocked, NotAttempted: false } ? "Failed" : "Not attempted",
                outcome?.Reason ?? "No file effect was recorded for this approved item.", !conflict, operationId));
        }
        onReadProgress?.Invoke($"Retry preview ready: {items.Count:N0} file choices checked; {items.Count(item => item.CanRetry):N0} may be retried. Completed and uncertain effects will not be replayed.");
        // A compact normal-run summary is scope/count presentation only. Once
        // its last exact operation is gone, no path is reconstructed from current
        // files or expired history. A newly approved normal run remains separate.
        return new(runId, pairId, plan is null && items.Count == 0
            ? "No retained operation detail remains for this run. Older details may have expired; start a fresh normal sync for the approved pair."
            : null, items, revision);
    }

    public async Task<SyncBootstrapActionResult> RetryRunAsync(string runId, IReadOnlyCollection<string>? selectedPaths,
        string mode, Func<SyncAuthorityBatchProgress, Task>? onProgress = null, CancellationToken cancellationToken = default,
        string? selectedPairId = null, Action<string>? onPreparationProgress = null)
    {
        if (mode is not ("failed" or "unattempted" or "all")) throw new ArgumentException("Choose failed, unattempted or all remaining work.");
        using var preparation = _authority.BeginColdCommandPreparation(
            await InspectColdCommandPreparationAsync(cancellationToken));
        var preview = await LoadRunRetryAsync(runId, cancellationToken, selectedPairId, onPreparationProgress);
        if (preview.UnavailableReason is not null) throw new InvalidOperationException(preview.UnavailableReason);
        var allowed = preview.Items.Where(item => item.CanRetry && (mode == "all" ||
            mode == "failed" && item.State == "Failed" || mode == "unattempted" && item.State == "Not attempted"))
            .Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = selectedPaths is null ? allowed : selectedPaths.Select(PilotPath.NormalizeRelative)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (_, original, _) = await ReadRetryOriginAsync(runId, cancellationToken, onPreparationProgress,
            materializePaths: selected);
        var finishRetainedApproval = mode == "all" && selectedPaths is null && original is not null &&
            selected.Count == 0 && preview.Items.Count > 0 &&
            preview.Items.All(item => item.State == "Completed") &&
            original.Choices.All(item => item.Action != "Skip for now");
        if ((!finishRetainedApproval && selected.Count == 0) || !selected.IsSubsetOf(allowed))
            throw new InvalidOperationException("Select retained retryable failed/unattempted items. Completed, conflicting or uncertain items cannot be replayed.");
        if (finishRetainedApproval)
            onPreparationProgress?.Invoke("Every originally reviewed action is already completed. Rechecking the original approval and current pair before saving first-sync completion; no file transfer will be replayed.");
        if (original is null)
            return await RetryApprovedPathsAsync(runId, preview.PairId!, preview.PolicyRevision!.Value,
                selected, onProgress, cancellationToken);
        var retry = original! with { Choices = original!.Choices.Select(item => selected.Contains(item.RelativePath)
            ? item : item with { Action = "Skip for now" }).ToArray() };
        return await RunBootstrapActionPlanCoreAsync(retry, cancellationToken: cancellationToken,
            onProgress: onProgress, retryOfRunId: runId, expectedPolicyRevision: preview.PolicyRevision,
            onPreparationProgress: onPreparationProgress, preparationPreview: preview);
    }

    /// <summary>The exact original operation ids this retry selected, keyed by the preview's own path - a
    /// reservation is never selected by path alone.</summary>
    private static IReadOnlyCollection<(string Path, string OperationId)> RetryTargets(
        SyncRunRetryPlan preview, IReadOnlySet<string> selected) =>
        preview.Items
            .Where(item => item.OperationId is not null && selected.Contains(PilotPath.NormalizeRelative(item.Path)))
            .Select(item => (Path: PilotPath.NormalizeRelative(item.Path), OperationId: item.OperationId!))
            .ToArray();

    private async Task HandoffSelectedRetryAsync(string origin, string commandId, SyncPair pair,
        IReadOnlySet<string> paths, CancellationToken token,
        IReadOnlySet<string>? fulfilledPaths = null)
    {
        // Repeat eligibility AFTER acquiring execution, not from a stale UI preview.
        var preview = await LoadRunRetryCoreAsync(origin, token, pair.Id, null, commandId);
        if (preview.UnavailableReason is not null || preview.PolicyRevision != pair.PolicyRevision ||
            !paths.IsSubsetOf(preview.Items.Where(item => item.CanRetry)
                .Select(item => PilotPath.NormalizeRelative(item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase)) ||
            fulfilledPaths is not null && !fulfilledPaths.IsSubsetOf(preview.Items
                .Where(item => item.State == "Completed").Select(item => PilotPath.NormalizeRelative(item.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected retry authority changed before execution.");
        await _authority.SupersedeRetryReadyOperationsAsync(origin, commandId, pair.Id,
            pair.PolicyRevision, RetryTargets(preview, paths), token);
    }

    private async Task<SyncBootstrapActionResult> RetryApprovedPathsAsync(string origin, string pairId, long retainedRevision,
        IReadOnlySet<string> paths,
        Func<SyncAuthorityBatchProgress, Task>? onProgress, CancellationToken token)
    {
        var pair = (await LoadPairsAsync(token)).Pairs.Single(item => item.Id == pairId);
        if (pair.PolicyRevision != retainedRevision)
            throw new InvalidOperationException("The pair settings changed after the retry preview. Read the current run eligibility again.");
        if (NewSyncAdmissionReason(pair) is { } admissionReason)
            throw new InvalidOperationException(admissionReason);
        if (await _rewindStore.LoadActiveAsync(pairId, token) is not null) throw new InvalidOperationException("Resolve this pair's retained Rewind checkpoint first.");
        var commandId = await CreateAcceptedCommandAsync("run_pair", pairId, null,
            JsonSerializer.Serialize(new { trigger = "Retry remaining files", retryOfRunId = origin,
                pairIds = new[] { pairId }, policyRevision = pair.PolicyRevision, retryPaths = paths }), token);
        var commandAdmission = await RequireRetainedCommandAdmissionAsync(commandId, token);
        PilotRunControl? control = null;
        try
        {
            control = await BeginRunAsync(token, commandId, "run_pair", pairId);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, control.StopToken);
            token = linked.Token;
            pair = (await LoadPairsAsync(token)).Pairs.Single(item => item.Id == pairId);
            if (pair.PolicyRevision != retainedRevision || pair.InitialReviewApproved == false ||
                NewSyncAdmissionReason(pair) is not null)
                throw new InvalidOperationException("The pair's approval, settings or pause state changed before retry acquired execution. Inspect the current pair before trying again.");
            await _authority.TransitionCommandRequestAsync(commandId, "executing", cancellationToken: token);
            await _authority.EnsureReviewedRunCanStartAsync(pairId, token);
            // Retry-owned handoff: release the reservation held by this retry's OWN retained, still-ready
            // operations for exactly the selected paths, after the guarded preconditions, so the fresh planner
            // can plan them. Unrelated Ready operations keep their reservation.
            await HandoffSelectedRetryAsync(origin, commandId, pair, paths, token);
            // Reuse the one provider-neutral runner and its fresh source/destination
            // checks. The filter is a subset of retained failures, never new consent.
            var prepared = await PreparePairAsync(pair, token);
            var group = "bootstrap-" + Guid.NewGuid().ToString("N");
            await _authority.RegisterOperationGroupAsync(group, "compound",
                JsonSerializer.Serialize(new { purpose = "retry_retained_paths", runId = commandId,
                    pairId, retryOfRunId = origin }), token);
            var result = await CreatePairRunner(pair, prepared, control, commandAdmission, prepared.CloudFiles,
                    onInventory: step => NotifyProgressAsync(onProgress,
                        new SyncAuthorityBatchProgress(pair, "Checking", null, null, null, Comparison: step)),
                    onProgress: onProgress)
                .RunReviewedAsync("Retry remaining files", paths, group, _ => Task.CompletedTask, token,
                    LiveActionProgress(pair, onProgress, null), continueAfterBlockedPaths: true);
            await FinalizeReviewedOperationGroupAsync(group, token);
            await _authority.TransitionCommandRequestAsync(commandId, "completed", JsonSerializer.Serialize(new
            { pairs = RetainedRunSummaries([new(pair, result, null, null)]) }), cancellationToken: CancellationToken.None);
            await NotifyProgressAsync(onProgress, new SyncAuthorityBatchProgress(pair,
                result.Blocked > 0 || result.Conflicts > 0 ? "Needs attention" : result.Stopped ? "Stopped" : "Completed", result, null, null));
            return new(pair, result, result.AppliedItems, 0, true);
        }
        catch (Exception error)
        {
            await RetainRunCommandFailureAsync(commandId, [pair], "retry_failed",
                control is null ? "Acquiring retry ownership" : "Retrying retained file paths", error);
            throw;
        }
        finally { if (control is not null) { await FlushActivityProgressAsync(); EndRun(control); } }
    }
}
