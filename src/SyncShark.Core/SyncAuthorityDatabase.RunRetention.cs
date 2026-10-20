using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed record SyncRunSummaryStatus(long EligibleRuns, long EncodedBytes, long ExpiredRuns,
    bool AccountingPending, bool SafetyOrLegacyHeld)
{
    public string Describe() => $"Run summaries: {EligibleRuns:N0} eligible, {EncodedBytes:N0} encoded bytes; " +
        $"{ExpiredRuns:N0} expired. Window: 90 days, 10,000 rows or 32 MiB; no-op checks: 30 days or 1,000 rows." +
        (AccountingPending ? " Older summary accounting is pending." : "") +
        (SafetyOrLegacyHeld ? " Required or unclassified run authority remains retained outside that window." : "");
}
public sealed record SyncRunSummaryRetirementResult(int RetiredRuns, long RetiredBytes, int HeldRuns,
    string Disposition, double ElapsedMilliseconds, long FreePages);
internal sealed record SyncRunSummaryPolicy(int Days=90, long MaximumRuns=10_000,
    long MaximumBytes=33_554_432, int NoopDays=30, long MaximumNoops=1_000);

public sealed partial class SyncAuthorityDatabase
{
    private sealed class RunRetirementScope { internal string? CommandId; }
    private static readonly ConditionalWeakTable<SqliteConnection,RunRetirementScope> RunRetirementScopes=new();
    internal SyncRunSummaryPolicy? RunSummaryPolicyForTest { get; set; }
    internal Action<string>? RunSummaryRetirementBoundaryForTest { get; set; }
    private static void ConfigureRunRetirementFunction(SqliteConnection connection)
    {
        ConfigureClosedConsentRetirementFunction(connection);
        RunRetirementScopes.Remove(connection);
        var scope=new RunRetirementScope(); RunRetirementScopes.Add(connection,scope);
        var code=SQLitePCL.raw.sqlite3_create_function(connection.Handle!,
            "syncshark_run_retirement_allowed",1,SQLitePCL.raw.SQLITE_UTF8|0x00200000,scope,
            (context,state,values)=>SQLitePCL.raw.sqlite3_result_int(context,
                values.Length==1 && state is RunRetirementScope permission && permission.CommandId is not null &&
                permission.CommandId==SQLitePCL.raw.sqlite3_value_text(values[0]).utf8_to_string()?1:0));
        if(code!=SQLitePCL.raw.SQLITE_OK) throw new InvalidDataException("The exact run retirement guard could not be registered.");
    }

    // This first ordinary-run class has no remaining normalized selection,
    // operation, lease or retained retry consumer. Other command families and
    // legacy admissions remain held for their separately proved owner lifetimes.
    // A closed admission can never be replayed after its generation has changed.
    // Within the current generation the monotonic closed-command floor is still
    // required. Missing legacy admission fields remain unproved and retained.
    private const string ClosedCommandAdmissionSpent="""
        (c.admission_generation IS NOT NULL AND c.admission_sequence IS NOT NULL AND
          (c.admission_generation<>(SELECT database_generation FROM installation) OR
           (c.admission_generation=(SELECT database_generation FROM installation) AND
            c.admission_sequence<=(SELECT ordinary_closed_command_floor FROM installation))))
        """;
    private static readonly string RunSummaryEligible = $"""
        c.command_kind IN ('run_pair','run_all') AND c.status IN ('completed','rejected','stale','blocked','cancelled')
        AND c.terminal_utc IS NOT NULL AND c.ordinary_issuance_closed_utc IS NOT NULL
        AND {ClosedCommandAdmissionSpent}
        AND c.admission_purpose IN ('normal_effect','read_only')
        AND (c.activity_opened=0 OR c.activity_closed=1)
        AND c.consent_id IS NULL
        AND NOT EXISTS(SELECT 1 FROM consent_header h WHERE h.originating_command_id=c.command_request_id)
        AND NOT EXISTS(SELECT 1 FROM consent_header h WHERE h.prepared_command_id=c.command_request_id)
        AND NOT EXISTS(SELECT 1 FROM operation o WHERE o.origin_command_id=c.command_request_id)
        AND NOT EXISTS(SELECT 1 FROM operation o WHERE o.execution_command_id=c.command_request_id)
        AND NOT EXISTS(SELECT 1 FROM lease l WHERE l.command_request_id=c.command_request_id)
        AND NOT EXISTS(SELECT 1 FROM command_request child
            WHERE json_extract(child.payload_json,'$.retryOfRunId')=c.command_request_id)
        AND NOT EXISTS(SELECT 1 FROM command_request child
            WHERE json_extract(child.payload_json,'$.reviewedConsent.commandId')=c.command_request_id)
        """;
    private const string RunSummaryNoop = """
        c.status='completed' AND json_type(c.result_json,'$.pairs')='array'
        AND json_array_length(c.result_json,'$.pairs')>0
        AND NOT EXISTS(SELECT 1 FROM json_each(c.result_json,'$.pairs') p
            WHERE CASE WHEN p.type<>'object' THEN 1 ELSE (json_type(p.value,'$.uploaded') IS NOT 'integer'
              OR json_type(p.value,'$.downloaded') IS NOT 'integer'
              OR json_type(p.value,'$.blocked') IS NOT 'integer'
              OR json_type(p.value,'$.conflicts') IS NOT 'integer'
              OR json_extract(p.value,'$.uploaded') IS NULL
              OR json_extract(p.value,'$.downloaded') IS NULL
              OR json_extract(p.value,'$.blocked') IS NULL
              OR json_extract(p.value,'$.conflicts') IS NULL
              OR coalesce(json_extract(p.value,'$.failureType'),'')<>''
              OR coalesce(json_extract(p.value,'$.failureMessage'),'')<>''
              OR coalesce(json_extract(p.value,'$.stopped'),1)<>0
              OR json_extract(p.value,'$.uploaded')<>0 OR json_extract(p.value,'$.downloaded')<>0
              OR json_extract(p.value,'$.blocked')<>0 OR json_extract(p.value,'$.conflicts')<>0
              OR coalesce(json_extract(p.value,'$.failed'),-1)<>0
              OR coalesce(json_extract(p.value,'$.notAttempted'),-1)<>0
              OR coalesce(json_extract(p.value,'$.needsVerification'),-1)<>0
              OR coalesce(json_extract(p.value,'$.skipped'),-1)<>0
              OR coalesce(json_extract(p.value,'$.applied'),0)<>0
              OR coalesce(json_extract(p.value,'$.recovered'),0)<>0
              OR coalesce(json_extract(p.value,'$.onlineOnly'),0)<>0) END)
        """;

    private static async Task<SyncRunSummaryStatus> LoadRunSummaryStatusCoreAsync(
        SqliteConnection connection,SqliteTransaction? transaction,CancellationToken token)
    {
        await using var q=connection.CreateCommand();q.Transaction=transaction;
        q.CommandText="""
            SELECT run_summary_count,run_summary_bytes,run_summary_expired,
                EXISTS(SELECT 1 FROM command_request WHERE command_kind IN ('run_pair','run_all') AND summary_accounted=0),
                EXISTS(SELECT 1 FROM command_request WHERE command_kind IN ('run_pair','run_all')
                    AND terminal_utc IS NOT NULL AND summary_eligible=0)
            FROM installation;
            """;
        await using var rows=await q.ExecuteReaderAsync(token);
        if(!await rows.ReadAsync(token))throw new InvalidDataException("Run summary accounting has no installation.");
        return new(rows.GetInt64(0),rows.GetInt64(1),rows.GetInt64(2),rows.GetInt64(3)!=0,rows.GetInt64(4)!=0);
    }

    internal async Task<SyncRunSummaryRetirementResult> RetireRunSummaryBatchAsync(CancellationToken token=default)
    {
        if(_executionConnectionScope.Value is {Active:true})
            throw new InvalidOperationException("Run maintenance cannot borrow an effect transaction.");
        var policy=RunSummaryPolicyForTest??new();
        if(policy.Days<=0 || policy.MaximumRuns<=0 || policy.MaximumBytes<=0 ||
            policy.NoopDays<=0 || policy.MaximumNoops<=0)throw new InvalidDataException("Invalid run summary policy.");
        await _writer.WaitAsync(token);
        var timer=Stopwatch.StartNew();var retired=0;var held=0;long bytes=0;var examined=0;long touched=0;
        try
        {
            await using var connection=await OpenConnectionAsync(token,reuseConnection:false);
            var permission=RunRetirementScopes.TryGetValue(connection,out var scope)?scope:
                throw new InvalidDataException("The run retirement scope is missing.");
            try
            {
                await using var tx=connection.BeginTransaction(deferred:false);
                RunSummaryRetirementBoundaryForTest?.Invoke("after-run-maintenance-preflight");
                while(examined<200 && (examined==0 || timer.ElapsedMilliseconds<50) && touched<1_048_576)
                {
                    var id=await ScalarStringAsync(connection,tx,"""
                        SELECT command_request_id FROM command_request
                        WHERE summary_accounted=0 AND command_kind IN ('run_pair','run_all')
                        ORDER BY command_request_id LIMIT 1;
                        """,token);
                    if(id is null)break;
                    var size=await ScalarLongAsync(connection,tx,
                        "SELECT "+SyncRunRetentionSchema.EncodedBytes+" FROM command_request WHERE command_request_id=$id;",
                        token,("$id",id));
                    await ExecuteAsync(connection,tx,"""
                        UPDATE command_request SET summary_accounted=1,summary_bytes=$bytes
                        WHERE command_request_id=$id;
                        """,token,("$bytes",size),("$id",id));
                    touched+=size;examined++;
                }
                var enabled=await ScalarLongAsync(connection,tx,"SELECT history_retention_enabled FROM installation;",token)==1;
                if(enabled)
                {
                    var closedSelections = await RetireClosedConsentsCoreAsync(connection, tx, timer,
                        Math.Min(80,200-examined), 1_048_576-touched, token);
                    examined += closedSelections.Rows; touched += closedSelections.Bytes;
                    // Keyset classification makes progress past safety pins. A
                    // new pin invalidates eligibility immediately; collection
                    // below always repeats the real references in this transaction.
                    var classified=0;
                    while(examined<100 && (classified==0 || timer.ElapsedMilliseconds<25) && touched<1_048_576)
                    {
                        classified++;
                        string? id=null;string utc="";long size=0;
                        await using(var q=connection.CreateCommand())
                        {
                            q.Transaction=tx;q.CommandText="""
                                SELECT c.command_request_id,c.terminal_utc,c.summary_bytes
                                FROM command_request c,installation i
                                WHERE c.command_kind IN ('run_pair','run_all') AND c.terminal_utc IS NOT NULL
                                  AND c.summary_accounted=1
                                  AND (c.terminal_utc,c.command_request_id)>(i.run_summary_frontier_utc,i.run_summary_frontier_id)
                                ORDER BY c.terminal_utc,c.command_request_id LIMIT 1;
                                """;
                            await using var rows=await q.ExecuteReaderAsync(token);
                            if(await rows.ReadAsync(token)){id=rows.GetString(0);utc=rows.GetString(1);size=rows.GetInt64(2);}
                        }
                        if(id is null)
                        {
                            await ExecuteAsync(connection,tx,"UPDATE installation SET run_summary_frontier_utc='',run_summary_frontier_id='';",token);
                            break;
                        }
                        // Oversized old rows stay explicit holds; one cannot spend
                        // the whole maintenance budget repeatedly or block siblings.
                        var eligible=size<=1_048_576 && await ScalarLongAsync(connection,tx,
                            "SELECT count(*) FROM command_request c WHERE c.command_request_id=$id AND "+RunSummaryEligible+";",
                            token,("$id",id))==1;
                        await ExecuteAsync(connection,tx,
                            "UPDATE command_request AS c SET summary_eligible=$eligible,summary_noop=CASE WHEN "+
                            RunSummaryNoop+" THEN 1 ELSE 0 END WHERE command_request_id=$id;",
                            token,("$eligible",eligible?1:0),("$id",id));
                        if(!eligible)held++;
                        await ExecuteAsync(connection,tx,"""
                            UPDATE installation SET run_summary_frontier_utc=$utc,run_summary_frontier_id=$id;
                            """,token,("$utc",utc),("$id",id));
                        examined++;touched+=size;
                    }
                    var examinedRetirement=0;
                    while(examined<200 && (examinedRetirement==0 || timer.ElapsedMilliseconds<50) && touched<1_048_576)
                    {
                        examinedRetirement++;
                        var id=await ScalarStringAsync(connection,tx,"""
                            SELECT c.command_request_id FROM command_request c,installation i
                            WHERE c.summary_eligible=1 AND (c.terminal_utc<$expiry
                              OR i.run_summary_count>$maximum OR i.run_summary_bytes>$bytes
                              OR (c.summary_noop=1 AND (c.terminal_utc<$noopExpiry OR i.run_noop_count>$noops)))
                            ORDER BY c.terminal_utc,c.command_request_id LIMIT 1;
                            """,token,("$expiry",Utc(ActivityRetentionNow.AddDays(-policy.Days))),
                            ("$maximum",policy.MaximumRuns),("$bytes",policy.MaximumBytes),
                            ("$noopExpiry",Utc(ActivityRetentionNow.AddDays(-policy.NoopDays))),("$noops",policy.MaximumNoops));
                        if(id is null)break;
                        var size=await ScalarLongAsync(connection,tx,
                            "SELECT summary_bytes FROM command_request WHERE command_request_id=$id;",token,("$id",id));
                        if(await ScalarLongAsync(connection,tx,
                            "SELECT count(*) FROM command_request c WHERE c.command_request_id=$id AND "+RunSummaryEligible+";",
                            token,("$id",id))!=1)
                        {
                            await ExecuteAsync(connection,tx,"UPDATE command_request SET summary_eligible=0 WHERE command_request_id=$id;",token,("$id",id));
                            held++;examined++;continue;
                        }
                        if(touched+size>1_048_576)break;
                        permission.CommandId=id;
                        RunSummaryRetirementBoundaryForTest?.Invoke("exact-run-before-delete");
                        await ExecuteAsync(connection,tx,"DELETE FROM command_request WHERE command_request_id=$id;",token,("$id",id));
                        permission.CommandId=null;
                        retired++;examined++;bytes+=size;touched+=size;
                    }
                }
                RunSummaryRetirementBoundaryForTest?.Invoke("before-commit");
                var free=await ScalarLongAsync(connection,tx,"PRAGMA freelist_count;",token);
                await tx.CommitAsync(token);
                RunSummaryRetirementBoundaryForTest?.Invoke("after-commit");
                return new(retired,bytes,held,!enabled?"disabled":examined>=200?"row_budget":
                    timer.ElapsedMilliseconds>=50?"cooperative_time_budget":"within_window",timer.Elapsed.TotalMilliseconds,free);
            }
            finally{permission.CommandId=null;}
        }
        finally{_writer.Release();}
    }
}
