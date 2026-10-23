using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed record SyncConsentRetirementResult(int RetiredItems, long RetiredEncodedBytes,
    int HeldItems, string Disposition, double ElapsedMilliseconds)
{
    public long FreePages { get; init; }
}

public sealed record SyncConsentLifetimeStatus(long InactiveEligibleBytes, long RetiredItems,
    long RetiredEncodedBytes, bool AccountingPending)
{
    public long ExpiredHeaders { get; init; }
    public string Describe() => $"Original selections retain {InactiveEligibleBytes:N0} encoded bytes of inactive unissued choices; " +
        $"{RetiredItems:N0} completed or expired choice details have been retired; {ExpiredHeaders:N0} closed selection headers expired." +
        (AccountingPending ? " Older selection accounting is still being measured." : "");
}

public sealed partial class SyncAuthorityDatabase
{
    internal long? ConsentEligibleBudgetForTest { get; set; }

    private static async Task<SyncConsentLifetimeStatus> LoadConsentLifetimeStatusCoreAsync(
        SqliteConnection connection, SqliteTransaction? tx, CancellationToken token)
    {
        await using var q=connection.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT consent_inactive_eligible_bytes,consent_retired_items,consent_retired_bytes,
                EXISTS(SELECT 1 FROM consent_header WHERE commands_accounted=0)
                OR EXISTS(SELECT 1 FROM consent_item WHERE eligible_bytes_accounted=0),consent_expired_headers
            FROM installation;
            """;
        await using var rows=await q.ExecuteReaderAsync(token);
        if(!await rows.ReadAsync(token)) throw new InvalidDataException("Consent accounting has no installation.");
        return new(rows.GetInt64(0),rows.GetInt64(1),rows.GetInt64(2),rows.GetInt64(3)!=0)
            { ExpiredHeaders=rows.GetInt64(4) };
    }

    private static async Task<int> MeasurePredecessorConsentAccountingCoreAsync(SqliteConnection connection,
        SqliteTransaction tx, Stopwatch timer, CancellationToken token)
    {
        var measured=0;
        for(;measured<100 && (measured==0 || timer.ElapsedMilliseconds<50);measured++)
        {
            var header=await ScalarStringAsync(connection,tx,
                "SELECT consent_id FROM consent_header WHERE commands_accounted=0 ORDER BY consent_id LIMIT 1;",token);
            if(header is not null)
            {
                await ExecuteAsync(connection,tx,"""
                    UPDATE consent_header SET commands_accounted=1,active_effect_commands=(
                        SELECT count(*) FROM command_request c WHERE c.consent_id=consent_header.consent_id
                          AND c.status IN ('accepted','executing') AND c.ordinary_issuance_closed_utc IS NULL
                          AND c.admission_purpose IN ('normal_effect','control_allocation')),
                        normalized_origin_accounted=EXISTS(SELECT 1 FROM command_request c
                            WHERE c.command_request_id=consent_header.originating_command_id
                              AND c.consent_id=consent_header.consent_id
                              AND json_extract(c.payload_json,'$.consentId')=consent_header.consent_id
                              AND json_type(c.payload_json,'$.reviewedPlan') IS NULL)
                    WHERE consent_id=$id AND commands_accounted=0;
                    """,token,("$id",header));
                continue;
            }
            string? id=null;long ordinal=-1;
            await using(var q=connection.CreateCommand())
            {
                q.Transaction=tx;q.CommandText="""
                    SELECT consent_id,ordinal FROM consent_item WHERE eligible_bytes_accounted=0
                    ORDER BY consent_id,ordinal LIMIT 1;
                    """;
                await using var rows=await q.ExecuteReaderAsync(token);
                if(await rows.ReadAsync(token)){id=rows.GetString(0);ordinal=rows.GetInt64(1);}
            }
            if(id is null) break;
            await ExecuteAsync(connection,tx,"""
                UPDATE consent_item SET eligible_bytes_accounted=1,retained_operation_count=(
                    SELECT count(*) FROM operation o WHERE o.consent_id=consent_item.consent_id
                        AND o.consent_item_ordinal=consent_item.ordinal)
                WHERE consent_id=$id AND ordinal=$ordinal AND eligible_bytes_accounted=0;
                """,token,("$id",id),("$ordinal",ordinal));
        }
        return measured;
    }

    private sealed class ConsentRetirementScope
    {
        internal string? ConsentId;
        internal int Ordinal = -2;
        internal void Clear() { ConsentId = null; Ordinal = -2; }
    }
    private static readonly ConditionalWeakTable<SqliteConnection, ConsentRetirementScope> ConsentRetirementScopes = new();
    internal Action<string>? ConsentRetirementBoundaryForTest { get; set; }

    private static void ConfigureConsentRetirementFunction(SqliteConnection connection)
    {
        ConsentRetirementScopes.Remove(connection);
        var permission = new ConsentRetirementScope();
        ConsentRetirementScopes.Add(connection, permission);
        var rc = SQLitePCL.raw.sqlite3_create_function(connection.Handle!,
            "syncshark_consent_retirement_allowed", 2, SQLitePCL.raw.SQLITE_UTF8 | 0x00200000, permission,
            (context, state, values) =>
            {
                var ordinal = values.Length == 2 ? SQLitePCL.raw.sqlite3_value_int64(values[1]) : -2;
                var allowed = values.Length == 2 && state is ConsentRetirementScope scope &&
                    scope.ConsentId is not null &&
                    scope.ConsentId == SQLitePCL.raw.sqlite3_value_text(values[0]).utf8_to_string() &&
                    (ordinal == -1 || ordinal == scope.Ordinal);
                SQLitePCL.raw.sqlite3_result_int(context, allowed ? 1 : 0);
            });
        if (rc != SQLitePCL.raw.SQLITE_OK)
            throw new InvalidDataException("The exact consent retirement predicate could not be registered.");
    }

    private static async Task<SyncConsentTerminalLayout> ReadConsentTerminalLayoutCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string consentId, CancellationToken token)
    {
        await using var q = connection.CreateCommand(); q.Transaction = transaction;
        q.CommandText = """
            SELECT original_choice_count,completed_prefix,terminal_ranges_json,retired_choice_count
            FROM consent_header WHERE consent_id=$id;
            """;
        q.Parameters.AddWithValue("$id", consentId);
        await using var rows = await q.ExecuteReaderAsync(token);
        if (!await rows.ReadAsync(token)) throw new InvalidDataException("The exact original consent is absent.");
        return SyncConsentTerminalLayout.Read(checked((int)rows.GetInt64(0)), checked((int)rows.GetInt64(1)),
            rows.GetString(2), rows.GetInt64(3));
    }

    // One existing idle opportunity, no timer or background authority of its own.
    // Seven-day age and 64 MiB inactive eligible-payload pressure govern only
    // proved unissued choices. Neither accounting nor size gates new effects.
    internal async Task<SyncConsentRetirementResult> RetireInactiveConsentBatchAsync(CancellationToken token = default)
    {
        if (_executionConnectionScope.Value is { Active: true })
            throw new InvalidOperationException("Consent maintenance cannot borrow an effect transaction.");
        await _writer.WaitAsync(token);
        var timer = Stopwatch.StartNew();
        var retired = 0; var held = 0; long bytes = 0;
        try
        {
            await using var connection = await OpenConnectionAsync(token, reuseConnection: false);
            var scope = ConsentRetirementScopes.TryGetValue(connection, out var found) ? found :
                throw new InvalidDataException("The exact consent retirement scope is missing.");
            try
            {
                await using var tx = connection.BeginTransaction(deferred: false);
                ConsentRetirementBoundaryForTest?.Invoke("after-consent-maintenance-preflight");
                var measured = await MeasurePredecessorConsentAccountingCoreAsync(connection, tx, timer, token);
                if (await ScalarLongAsync(connection, tx, "SELECT history_retention_enabled FROM installation;", token) != 1)
                {
                    var free = await ScalarLongAsync(connection, tx, "PRAGMA freelist_count;", token);
                    await tx.CommitAsync(token);
                    return new(0, 0, 0, "disabled", timer.Elapsed.TotalMilliseconds) { FreePages = free };
                }
                var eligibleBudget = ConsentEligibleBudgetForTest ?? 67_108_864;
                if (eligibleBudget <= 0) throw new InvalidDataException("The inactive original-selection budget is invalid.");
                var now = ActivityRetentionNow;
                var cutoff = now.AddDays(-7);
                for (var examined = 0; examined + measured < 200 && (examined == 0 || timer.ElapsedMilliseconds < 50) && bytes < MaximumActionBatchBytes; examined++)
                {
                    string? id = null; var ordinal = -1;
                    await using (var q = connection.CreateCommand())
                    {
                        q.Transaction = tx;
                        q.CommandText = """
                            SELECT c.consent_id,c.ordinal FROM consent_item c,installation i
                            WHERE (c.consent_id,c.ordinal)>(i.consent_retire_frontier_id,i.consent_retire_frontier_ordinal)
                            ORDER BY c.consent_id,c.ordinal LIMIT 1;
                            """;
                        await using var rows = await q.ExecuteReaderAsync(token);
                        if (await rows.ReadAsync(token)) { id = rows.GetString(0); ordinal = checked((int)rows.GetInt64(1)); }
                    }
                    if (id is null)
                    {
                        await ExecuteAsync(connection, tx, "UPDATE installation SET consent_retire_frontier_id='',consent_retire_frontier_ordinal=-1;", token);
                        break;
                    }
                    await ExecuteAsync(connection, tx,
                        "UPDATE installation SET consent_retire_frontier_id=$id,consent_retire_frontier_ordinal=$ordinal;",
                        token, ("$id", id), ("$ordinal", ordinal));
                    var item = await ReadConsentItemCoreAsync(connection, tx, id, ordinal, token)
                        ?? throw new InvalidDataException("The exact consent candidate disappeared within its transaction.");
                    var header = await ReadConsentHeaderCoreAsync(connection, tx, id, token)
                        ?? throw new InvalidDataException("The consent candidate lost its header.");
                    var eligibleState = item.Lifecycle is SyncAuthorityConsentItemLifecycle.Eligible or
                        SyncAuthorityConsentItemLifecycle.Completed or SyncAuthorityConsentItemLifecycle.Expired;
                    var lastUse = item.Lifecycle == SyncAuthorityConsentItemLifecycle.Completed ? item.CompletedUtc
                        : header.LastUseUtc ?? header.SealedUtc ?? header.CreatedUtc;
                    var overBudget = item.Lifecycle == SyncAuthorityConsentItemLifecycle.Eligible &&
                        await ScalarLongAsync(connection, tx, "SELECT consent_inactive_eligible_bytes FROM installation;", token) > eligibleBudget;
                    if (!eligibleState || header.Lifecycle is SyncAuthorityConsentLifecycle.Building or SyncAuthorityConsentLifecycle.Sealed ||
                        !DateTimeOffset.TryParse(lastUse, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var used) || (!overBudget && used > cutoff) ||
                        await ScalarLongAsync(connection, tx, """
                            SELECT count(*) FROM operation WHERE consent_id=$id AND consent_item_ordinal=$ordinal;
                            """, token, ("$id", id), ("$ordinal", ordinal)) != 0 ||
                        await ScalarLongAsync(connection, tx, """
                            SELECT count(*) FROM command_request WHERE consent_id=$id
                              AND status IN ('accepted','executing') AND ordinary_issuance_closed_utc IS NULL
                              AND admission_purpose IN ('normal_effect','control_allocation');
                            """, token, ("$id", id)) != 0)
                    { held++; continue; }
                    // Legacy inline ancestry still depends on its complete choice
                    // array. Only a positively normalized original can compact;
                    // the essential importer owns any legacy conversion.
                    if (await ScalarLongAsync(connection, tx, """
                            SELECT count(*) FROM command_request c WHERE c.command_request_id=$origin
                              AND c.consent_id=$id AND json_extract(c.payload_json,'$.consentId')=$id
                              AND json_type(c.payload_json,'$.reviewedPlan') IS NULL;
                            """, token, ("$origin", header.OriginatingCommandId), ("$id", id)) != 1)
                    { held++; continue; }
                    var layout = await ReadConsentTerminalLayoutCoreAsync(connection, tx, id, token);
                    var state = item.Lifecycle == SyncAuthorityConsentItemLifecycle.Completed ? "completed" : "expired";
                    var next = layout.Add(ordinal, state);
                    var json = JsonSerializer.Serialize(next.Ranges);
                    var charge = checked(item.EncodedBytes + Encoding.UTF8.GetByteCount(json));
                    if (charge > MaximumActionBatchBytes - bytes) { held++; continue; }
                    scope.ConsentId = id; scope.Ordinal = ordinal;
                    try
                    {
                        var newlyExpired = item.Lifecycle == SyncAuthorityConsentItemLifecycle.Eligible ? 1 : 0;
                        if (newlyExpired != 0)
                            await ExecuteAsync(connection, tx, """
                                UPDATE consent_item SET lifecycle='expired',expired_utc=$now
                                WHERE consent_id=$id AND ordinal=$ordinal AND lifecycle='eligible';
                                """, token, ("$now", Utc(now)), ("$id", id), ("$ordinal", ordinal));
                        await ExecuteAsync(connection, tx, """
                            UPDATE consent_header SET completed_prefix=$prefix,terminal_ranges_json=$ranges,
                                retired_choice_count=$count,retired_choice_bytes=retired_choice_bytes+$bytes,
                                expired_choice_count=expired_choice_count+$expired,
                                lifecycle=CASE WHEN $expired>0 AND lifecycle<>'closed' THEN 'partially_expired' ELSE lifecycle END
                            WHERE consent_id=$id;
                            """, token, ("$prefix", next.CompletedPrefix), ("$ranges", json),
                            ("$count", next.RetiredCount), ("$bytes", item.EncodedBytes),
                            ("$expired", newlyExpired), ("$id", id));
                        if (await ExecuteCountAsync(connection, tx,
                                "DELETE FROM consent_item WHERE consent_id=$id AND ordinal=$ordinal;", token,
                                ("$id", id), ("$ordinal", ordinal)) != 1)
                            throw new InvalidDataException("The exact consent item was not retired once.");
                        retired++; bytes = checked(bytes + charge);
                    }
                    finally { scope.Clear(); }
                }
                ConsentRetirementBoundaryForTest?.Invoke("before-commit");
                var freePages = await ScalarLongAsync(connection, tx, "PRAGMA freelist_count;", token);
                await tx.CommitAsync(token);
                ConsentRetirementBoundaryForTest?.Invoke("after-commit");
                return new(retired, bytes, held, "bounded_age_or_payload_pass", timer.Elapsed.TotalMilliseconds)
                { FreePages = freePages };
            }
            finally { scope.Clear(); }
        }
        finally { _writer.Release(); }
    }
}
