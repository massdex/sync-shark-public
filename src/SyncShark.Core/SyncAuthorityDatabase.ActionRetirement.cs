using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

internal sealed record SyncActionRetentionPolicy(int Days = 7, long MaximumActions = 20_000,
    long MaximumEncodedBytes = 33_554_432);
public sealed record SyncActionRetirementResult(int ClosedActions, int RetiredActions,
    long RetiredEncodedBytes, int HeldActions, string Disposition, double ElapsedMilliseconds, long FreePages)
{
    public long ProcessedEncodedBytes { get; init; }
}

public sealed partial class SyncAuthorityDatabase
{
    private sealed class ActionRetirementScope
    {
        internal string? OperationId, ClosureSha256;
        internal readonly HashSet<(string Table, string Id)> Rows = [];
        internal void Clear() { OperationId = null; ClosureSha256 = null; Rows.Clear(); }
    }
    private static readonly ConditionalWeakTable<SqliteConnection, ActionRetirementScope> ActionRetirementScopes = new();
    internal SyncActionRetentionPolicy? ActionRetentionPolicyForTest { get; set; }
    internal Action<string>? ActionRetirementBoundaryForTest { get; set; }

    private static void ConfigureActionRetirementFunction(SqliteConnection connection)
    {
        ConfigureConsentRetirementFunction(connection);
        ConfigureRunRetirementFunction(connection);
        ActionRetirementScopes.Remove(connection);
        var scope = new ActionRetirementScope();
        ActionRetirementScopes.Add(connection, scope);
        const int innocuous = 0x00200000;
        var result = SQLitePCL.raw.sqlite3_create_function(connection.Handle!,
            "syncshark_action_retirement_allowed", 3, SQLitePCL.raw.SQLITE_UTF8 | innocuous, scope,
            (context, state, values) =>
            {
                // The certificate fingerprint is bound in private connection
                // state only after same-transaction graph/hash verification.
                // SQL cannot mint, modify or transfer this capability.
                var allowed = values.Length == 3 && state is ActionRetirementScope permission &&
                    permission.ClosureSha256 is { Length: 64 } &&
                    permission.OperationId == SQLitePCL.raw.sqlite3_value_text(values[2]).utf8_to_string() &&
                    permission.Rows.Contains((SQLitePCL.raw.sqlite3_value_text(values[0]).utf8_to_string() ?? "",
                        SQLitePCL.raw.sqlite3_value_text(values[1]).utf8_to_string() ?? ""));
                SQLitePCL.raw.sqlite3_result_int(context, allowed ? 1 : 0);
            });
        if (result != SQLitePCL.raw.SQLITE_OK)
            throw new InvalidDataException("The action retirement predicate could not be registered.");
        result = SQLitePCL.raw.sqlite3_create_function(connection.Handle!,
            "syncshark_terminal_prerequisite_valid", 3, SQLitePCL.raw.SQLITE_UTF8 | innocuous, null,
            (context, state, values) =>
            {
                var valid = values.Length == 3 && ValidateTerminalPrerequisite(
                    SQLitePCL.raw.sqlite3_value_text(values[0]).utf8_to_string(),
                    SQLitePCL.raw.sqlite3_value_text(values[1]).utf8_to_string(),
                    SQLitePCL.raw.sqlite3_value_text(values[2]).utf8_to_string());
                SQLitePCL.raw.sqlite3_result_int(context, valid ? 1 : 0);
            });
        if (result != SQLitePCL.raw.SQLITE_OK)
            throw new InvalidDataException("The terminal prerequisite verifier could not be registered.");
    }

    private static bool ValidateTerminalPrerequisite(string? json, string? hash, string? id)
    {
        if (json is null || json.Length > MaximumActionBatchBytes || hash is not { Length: 64 } ||
            string.IsNullOrWhiteSpace(id) || HashActionText(json) != hash) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var value = doc.RootElement;
            if (value.GetProperty("operationId").GetString() != id ||
                string.IsNullOrWhiteSpace(value.GetProperty("kind").GetString()) ||
                value.GetProperty("configurationRevision").GetInt64() <= 0 ||
                value.GetProperty("planningAuthority").ValueKind != JsonValueKind.Array ||
                value.GetProperty("registrationSequence").GetInt64() <= 0 ||
                string.IsNullOrWhiteSpace(value.GetProperty("registrationGeneration").GetString()) ||
                value.GetProperty("intentSha256").GetString() is not { Length: 64 } ||
                value.GetProperty("immutableGraphSha256").GetString() is not { Length: 64 } ||
                !value.GetProperty("workerDrained").GetBoolean() ||
                !value.GetProperty("allocationsReleased").GetBoolean()) return false;
            var status = value.GetProperty("status").GetString();
            var outcome = value.GetProperty("outcome").GetString();
            return outcome == "known_success" && status == "completed" &&
                    value.GetProperty("completion").ValueKind == JsonValueKind.Object ||
                outcome == "unissued" && status is "cancelled" or "superseded" &&
                    value.GetProperty("completion").ValueKind == JsonValueKind.Null ||
                outcome == "known_nonapplication" && status is "blocked" or "cancelled" or "superseded" &&
                    value.GetProperty("completion").ValueKind == JsonValueKind.Null &&
                    value.GetProperty("nonapplicationVerified").GetBoolean();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException) { return false; }
    }

    internal async Task<SyncActionRetirementResult> RetireSettledActionBatchAsync(
        CancellationToken token = default)
    {
        if (_executionConnectionScope.Value is { Active: true })
            throw new InvalidOperationException("Action maintenance cannot borrow an effect transaction.");
        var policy = ActionRetentionPolicyForTest ?? new();
        if (policy.Days <= 0 || policy.MaximumActions <= 0 || policy.MaximumEncodedBytes <= 0)
            throw new InvalidDataException("The action retention window is invalid.");
        await _writer.WaitAsync(token);
        var timer = Stopwatch.StartNew();
        var closed = 0; var retired = 0; var held = 0; long bytes = 0;
        var disposition = "within_window";
        long processedBytes = 0;
        try
        {
            await using var connection = await OpenConnectionAsync(token, reuseConnection: false);
            var scope = ActionRetirementScopes.TryGetValue(connection, out var value) ? value :
                throw new InvalidDataException("The action retirement scope is missing.");
            try
            {
                await using var transaction = connection.BeginTransaction(deferred: false);
                await MeasurePredecessorActionJournalCoreAsync(connection, transaction, timer, token);
                if (await ScalarLongAsync(connection, transaction,
                        "SELECT history_retention_enabled FROM installation;", token) != 1)
                {
                    var free = await ScalarLongAsync(connection, transaction, "PRAGMA freelist_count;", token);
                    await transaction.CommitAsync(token);
                    return new(0, 0, 0, 0, "disabled", timer.Elapsed.TotalMilliseconds, free);
                }
                var now = ActivityRetentionNow;
                // The indexed keyset resumes through held rows; one failed item
                // cannot starve unrelated actions. Wrap happens on a later tick.
                // The cooperative clock limits additional work, not the first
                // bounded item. Slow checkout/preflight must not starve every tick.
                ActionRetirementBoundaryForTest?.Invoke("after-action-maintenance-preflight");
                for (var examined = 0; examined < 200 && (examined == 0 || timer.ElapsedMilliseconds < 50) &&
                     processedBytes < MaximumActionBatchBytes; examined++)
                {
                    var id = await ScalarStringAsync(connection, transaction, """
                        SELECT o.operation_id FROM operation o,installation i
                        WHERE o.closure_utc IS NULL AND o.status IN ('completed','cancelled','superseded','blocked')
                          AND (coalesce(o.terminal_utc,o.created_utc),o.operation_id)>
                              (i.action_closure_frontier_utc,i.action_closure_frontier_id)
                        ORDER BY coalesce(o.terminal_utc,o.created_utc),o.operation_id LIMIT 1;
                        """, token);
                    if (id is null)
                    {
                        await ExecuteAsync(connection, transaction, """
                            UPDATE installation SET action_closure_frontier_utc='',action_closure_frontier_id='';
                            """, token);
                        break;
                    }
                    var outcome = await CloseSettledActionCoreAsync(connection, transaction, id, now, token, MaximumActionBatchBytes - processedBytes);
                    if (outcome.Closed) { closed++; processedBytes += outcome.EncodedBytes; } else held++;
                    await ExecuteAsync(connection, transaction, """
                        UPDATE installation SET action_closure_frontier_utc=
                            (SELECT coalesce(terminal_utc,created_utc) FROM operation WHERE operation_id=$id),
                            action_closure_frontier_id=$id;
                        """, token, ("$id", id));
                }
                var examinedRetirement = 0;
                while (closed + retired + held < 200 && examinedRetirement < 200 &&
                       (examinedRetirement == 0 || timer.ElapsedMilliseconds < 50) &&
                       processedBytes < MaximumActionBatchBytes)
                {
                    examinedRetirement++;
                    var id = await ScalarStringAsync(connection, transaction, """
                        SELECT operation_id FROM (
                            SELECT o.operation_id,o.closure_utc FROM operation o,installation i
                            WHERE o.closure_kind='known_success'
                              AND (o.closure_utc<$expiry OR i.action_closed_count>$maximum
                                  OR i.action_closed_bytes>$bytes)
                              AND (o.closure_utc,o.operation_id)>
                                  (i.action_retire_frontier_utc,i.action_retire_frontier_id)
                            UNION ALL
                            SELECT o.operation_id,o.closure_utc FROM operation o,installation i
                            WHERE o.closure_kind='known_nonapplication'
                              AND (o.closure_utc<$resolved OR i.action_closed_count>$maximum
                                  OR i.action_closed_bytes>$bytes)
                              AND (o.closure_utc,o.operation_id)>
                                  (i.action_retire_frontier_utc,i.action_retire_frontier_id)
                            UNION ALL
                            SELECT o.operation_id,o.closure_utc FROM operation o,installation i
                            WHERE o.closure_kind='unissued' AND o.closure_utc<$unissued
                              AND (o.closure_utc,o.operation_id)>
                                  (i.action_retire_frontier_utc,i.action_retire_frontier_id)
                        ) ORDER BY closure_utc,operation_id LIMIT 1;
                        """, token, ("$expiry", Utc(now.AddDays(-policy.Days))),
                        ("$unissued", Utc(now.AddDays(-1))), ("$resolved", Utc(now.AddDays(-90))),
                        ("$maximum", policy.MaximumActions),
                        ("$bytes", policy.MaximumEncodedBytes));
                    if (id is null)
                    {
                        await ExecuteAsync(connection, transaction, """
                            UPDATE installation SET action_retire_frontier_utc='',action_retire_frontier_id='';
                            """, token);
                        break;
                    }
                    var closureTime = await ScalarStringAsync(connection, transaction,
                        "SELECT closure_utc FROM operation WHERE operation_id=$id;", token, ("$id", id));
                    var result = await RetireClosedActionCoreAsync(connection, transaction, scope,
                        id, MaximumActionBatchBytes - processedBytes, token);
                    if (result.Retired) { retired++; bytes += result.Bytes; processedBytes += result.Bytes + result.CopiedBytes; }
                    else { held++; disposition = result.Reason; }
                    await ExecuteAsync(connection, transaction, """
                        UPDATE installation SET action_retire_frontier_utc=$utc,action_retire_frontier_id=$id;
                        """, token, ("$utc", closureTime), ("$id", id));
                }
                ActionRetirementBoundaryForTest?.Invoke("before-action-retirement-commit");
                await transaction.CommitAsync(token);
            }
            finally { scope.Clear(); }
            return new(closed, retired, bytes, held,
                timer.ElapsedMilliseconds >= 50 ? "cooperative_time_budget" : disposition,
                timer.Elapsed.TotalMilliseconds,
                await ScalarLongAsync(connection, null, "PRAGMA freelist_count;", token)) { ProcessedEncodedBytes = processedBytes };
        }
        finally { _writer.Release(); }
    }

    private async Task<(bool Retired, long Bytes, string Reason, long CopiedBytes)> RetireClosedActionCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, ActionRetirementScope scope,
        string id, long remainingBytes, CancellationToken token)
    {
        var certificate = await ScalarStringAsync(connection, transaction,
            "SELECT closure_json FROM operation WHERE operation_id=$id;", token, ("$id", id));
        var hash = await ScalarStringAsync(connection, transaction,
            "SELECT closure_sha256 FROM operation WHERE operation_id=$id;", token, ("$id", id));
        if (certificate is null || hash != HashActionText(certificate))
            return (false, 0, "closure_fingerprint_mismatch", 0);
        var charge = await ScalarLongAsync(connection, transaction,
            "SELECT closure_encoded_bytes FROM operation WHERE operation_id=$id;", token, ("$id", id));
        if (charge > remainingBytes) return (false, 0, "payload_budget", 0);
        using var proof = JsonDocument.Parse(certificate);
        var graph = await ReadActionGraphCoreAsync(connection, transaction, id, token);
        if (graph is null || graph.Sha256 != proof.RootElement.GetProperty("immutableGraphSha256").GetString())
            return (false, 0, "closed_graph_changed", 0);
        // References created after closure are checked again under this writer
        // transaction. Dependency compaction is separate; missing is never success.
        if (await ScalarLongAsync(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM operation_dependency d JOIN operation child
                        ON child.operation_id=d.operation_id
                        WHERE d.depends_on_operation_id=$id AND child.closure_utc IS NOT NULL)
                    OR EXISTS(SELECT 1 FROM history_event WHERE operation_id=$id)
                    OR EXISTS(SELECT 1 FROM recovery_artifact WHERE operation_id=$id)
                    OR EXISTS(SELECT 1 FROM conflict_resolution c JOIN operation o
                        ON o.operation_group_id=c.operation_group_id WHERE o.operation_id=$id)
                    OR EXISTS(SELECT 1 FROM conflict c JOIN operation o
                        ON o.pair_id=c.pair_id AND o.object_id=c.object_id
                        WHERE o.operation_id=$id AND c.status IN ('open','resolution_requested','resolving'))
                    OR EXISTS(SELECT 1 FROM control_namespace c JOIN namespace_allocation n
                        ON n.namespace_allocation_id=c.namespace_allocation_id WHERE n.operation_id=$id);
                """, token, ("$id", id)) != 0) return (false, 0, "exact_surviving_reference", 0);
        using var rows = JsonDocument.Parse(graph.Json);
        scope.Clear(); scope.OperationId = id; scope.ClosureSha256 = hash;
        try
        {
            foreach (var table in rows.RootElement.EnumerateObject())
                foreach (var row in table.Value.EnumerateArray())
                {
                    var key = table.Name switch {
                        "operation" => row.GetProperty("operation_id").GetString()!,
                        "operation_member" => row.GetProperty("operation_member_id").GetString()!,
                        "operation_dependency" => row.GetProperty("operation_id").GetString() + "\u001f" +
                            (row.GetProperty("depends_on_operation_id").GetString() ?? row.GetProperty("retired_dependency_id").GetString()),
                        "operation_step" => row.GetProperty("operation_step_id").GetString()!,
                        "resource_allocation" or "upload_session_checkpoint" => row.GetProperty("allocation_id").GetString()!,
                        "namespace_allocation" => row.GetProperty("namespace_allocation_id").GetString()!,
                        "operation_attempt" => row.GetProperty("attempt_id").GetString()!,
                        "effect_receipt" => row.GetProperty("effect_receipt_id").GetString()!,
                        "lease" => row.GetProperty("lease_id").GetString()!,
                        "lease_fence" => row.GetProperty("scope_kind").GetString() + "\u001f" +
                            row.GetProperty("scope_id").GetString(),
                        _ => throw new InvalidDataException("An unmapped action graph member cannot retire.")
                    };
                    scope.Rows.Add((table.Name, key));
                }
            if (!ValidateTerminalPrerequisite(certificate, hash, id))
                return (false, 0, "terminal_certificate_not_supported", 0);
            var dependents = new List<string>();
            await using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT operation_id FROM operation_dependency WHERE depends_on_operation_id=$id ORDER BY operation_id LIMIT 201;";
                query.Parameters.AddWithValue("$id", id);
                await using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) dependents.Add(reader.GetString(0));
            }
            // Proof fan-out is charged to the same transaction payload, never
            // silently multiplied outside the one-MiB budget.
            var proofBytes = System.Text.Encoding.UTF8.GetByteCount(certificate) +
                System.Text.Encoding.UTF8.GetByteCount(hash!) + System.Text.Encoding.UTF8.GetByteCount(id);
            var compactBytes = checked((long)proofBytes * dependents.Count);
            if (dependents.Count > 200 || charge + compactBytes > remainingBytes)
                return (false, 0, "dependency_fanout_budget_held", 0);
            foreach (var dependent in dependents)
            {
                scope.Rows.Add(("dependency_terminalization", dependent + "\u001f" + id));
                await ExecuteAsync(connection, transaction, """
                    UPDATE operation_dependency SET retired_dependency_id=depends_on_operation_id,
                        depends_on_operation_id=NULL,terminal_proof_json=$proof,terminal_proof_sha256=$hash
                    WHERE operation_id=$dependent AND depends_on_operation_id=$id;
                    """, token, ("$proof", certificate), ("$hash", hash), ("$id", id), ("$dependent", dependent));
                if (await ScalarLongAsync(connection, transaction, "SELECT changes();", token) != 1)
                    throw new InvalidDataException("The exact prerequisite edge changed during compaction.");
            }
            ActionRetirementBoundaryForTest?.Invoke("exact-action-bound-before-delete");
            // Child receipts precede attempts; operation releases its lease FKs
            // before owned lease/fence rows. Physical files are never touched.
            await ExecuteAsync(connection, transaction, """
                DELETE FROM effect_receipt WHERE attempt_id IN
                    (SELECT attempt_id FROM operation_attempt WHERE operation_id=$id);
                DELETE FROM upload_session_checkpoint WHERE operation_id=$id;
                DELETE FROM operation_attempt WHERE operation_id=$id;
                DELETE FROM operation_step WHERE operation_id=$id;
                DELETE FROM operation_dependency WHERE operation_id=$id;
                DELETE FROM operation_member WHERE operation_id=$id;
                DELETE FROM resource_allocation WHERE operation_id=$id;
                DELETE FROM namespace_allocation WHERE operation_id=$id;
                DELETE FROM operation WHERE operation_id=$id;
                DELETE FROM lease WHERE scope_kind='operation' AND scope_id=$id;
                DELETE FROM lease_fence WHERE scope_kind='operation' AND scope_id=$id;
                """, token, ("$id", id));
            return (true, charge, "retired", compactBytes);
        }
        finally { scope.Clear(); }
    }
}

