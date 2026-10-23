using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private sealed class ClosedConsentRetirementScope
    { internal string? HeaderId,CommandId; internal void Clear(){HeaderId=null;CommandId=null;} }
    private static readonly ConditionalWeakTable<SqliteConnection,ClosedConsentRetirementScope> ClosedConsentScopes=new();
    private static void ConfigureClosedConsentRetirementFunction(SqliteConnection connection)
    {
        ClosedConsentScopes.Remove(connection);
        var scope=new ClosedConsentRetirementScope();ClosedConsentScopes.Add(connection,scope);
        var code=SQLitePCL.raw.sqlite3_create_function(connection.Handle!,
            "syncshark_closed_consent_retirement_allowed",2,SQLitePCL.raw.SQLITE_UTF8|0x00200000,scope,
            (context,state,values)=>{
                var allow=false;
                if(values.Length==2 && state is ClosedConsentRetirementScope permission)
                {
                    var kind=SQLitePCL.raw.sqlite3_value_text(values[0]).utf8_to_string();
                    var id=SQLitePCL.raw.sqlite3_value_text(values[1]).utf8_to_string();
                    allow=kind=="header" && permission.HeaderId is not null && permission.HeaderId==id ||
                        kind=="command" && permission.CommandId is not null && permission.CommandId==id;
                }
                SQLitePCL.raw.sqlite3_result_int(context,allow?1:0);
            });
        if(code!=SQLitePCL.raw.SQLITE_OK)throw new InvalidDataException("The closed selection retirement guard could not be registered.");
    }

    internal async Task<string?> LoadExpiredConsentPairAsync(string commandId,CancellationToken token)
    {
        RequireIdentifier(commandId,nameof(commandId));
        await using var connection=await OpenConnectionAsync(token);
        return await ScalarStringAsync(connection,null,"""
            SELECT pair_id FROM command_request WHERE command_request_id=$id AND consent_detail_expired_utc IS NOT NULL;
            """,token,("$id",commandId));
    }

    // Reuses the run collector's writer, transaction, row/byte/time budget. The
    // irreversible marker permits bounded reference detachment across passes;
    // no new command can bind that header after the marker commits.
    private async Task<(int Rows,long Bytes,int Headers)> RetireClosedConsentsCoreAsync(
        SqliteConnection connection,SqliteTransaction tx,Stopwatch timer,int maximumRows,
        long maximumBytes,CancellationToken token)
    {
        var permission=ClosedConsentScopes.TryGetValue(connection,out var value)?value:
            throw new InvalidDataException("The closed selection scope is missing.");
        var rows=0;long bytes=0;var headers=0;
        try
        {
            while(rows<maximumRows && bytes<maximumBytes && (rows==0 || timer.ElapsedMilliseconds<20))
            {
                var id=await ScalarStringAsync(connection,tx,"""
                    SELECT h.consent_id FROM consent_header h,installation i
                    WHERE h.original_choice_count=h.retired_choice_count AND h.lifecycle NOT IN ('building','sealed')
                      AND h.consent_id>i.closed_consent_frontier_id
                    ORDER BY h.consent_id LIMIT 1;
                    """,token);
                if(id is null)
                {
                    await ExecuteAsync(connection,tx,"UPDATE installation SET closed_consent_frontier_id='';",token);
                    break;
                }
                var marked=await ScalarLongAsync(connection,tx,
                    "SELECT retirement_started_utc IS NOT NULL FROM consent_header WHERE consent_id=$id;",token,("$id",id))==1;
                var safe=await ScalarLongAsync(connection,tx,$"""
                    SELECT count(*) FROM consent_header h WHERE h.consent_id=$id
                      AND h.original_choice_count=h.retired_choice_count
                      AND h.commands_accounted=1 AND h.active_effect_commands=0
                      AND NOT EXISTS(SELECT 1 FROM consent_item WHERE consent_id=h.consent_id)
                      AND NOT EXISTS(SELECT 1 FROM operation WHERE consent_id=h.consent_id)
                      AND (h.retirement_started_utc IS NOT NULL OR (
                        h.normalized_origin_accounted=1 AND h.last_use_utc<$cutoff
                        AND EXISTS(SELECT 1 FROM command_request c WHERE c.command_request_id=h.originating_command_id
                          AND c.consent_id=h.consent_id AND c.ordinary_issuance_closed_utc<$cutoff
                          AND {ClosedCommandAdmissionSpent})
                        AND NOT EXISTS(SELECT 1 FROM command_request c WHERE c.consent_id=h.consent_id
                          AND (c.ordinary_issuance_closed_utc IS NULL OR c.status IN ('requested','accepted','executing')
                            OR (c.activity_opened=1 AND c.activity_closed=0)))
                        AND NOT EXISTS(SELECT 1 FROM command_request c WHERE c.consent_id=h.consent_id
                          AND c.ordinary_issuance_closed_utc>=$cutoff)));
                    """,token,("$id",id),("$cutoff",Utc(ActivityRetentionNow.AddDays(-30))))==1;
                rows++;
                if(safe)
                {
                    var layout=await ReadConsentTerminalLayoutCoreAsync(connection,tx,id,token);
                    if(layout.RetiredCount!=layout.OriginalCount)throw new InvalidDataException("The closed selection has unaccounted choices.");
                    var size=await ScalarLongAsync(connection,tx,"""
                        SELECT length(CAST(header_json AS BLOB))+length(CAST(terminal_ranges_json AS BLOB))
                          +length(CAST(consent_id AS BLOB))+length(CAST(original_choice_digest AS BLOB))
                        FROM consent_header WHERE consent_id=$id;
                        """,token,("$id",id));
                    if(size<=maximumBytes-bytes)
                    {
                        permission.HeaderId=id;
                        if(!marked)
                            await ExecuteAsync(connection,tx,"""
                                UPDATE consent_header SET retirement_started_utc=$utc,lifecycle='closed' WHERE consent_id=$id;
                                """,token,("$id",id),("$utc",Utc(ActivityRetentionNow)));
                        // Each detach is separately exact. A large closed lineage
                        // continues next pass without exceeding this pass's budget.
                        var detached=0;
                        while(rows<maximumRows && (detached==0 || timer.ElapsedMilliseconds<20))
                        {
                            var command=await ScalarStringAsync(connection,tx,"""
                                SELECT c.command_request_id FROM command_request c
                                WHERE c.consent_id=$id
                                  AND c.status IN ('completed','blocked','cancelled','rejected','stale')
                                  AND c.ordinary_issuance_closed_utc IS NOT NULL
                                  AND (c.activity_opened=0 OR c.activity_closed=1)
                                  AND NOT EXISTS(SELECT 1 FROM operation o WHERE o.origin_command_id=c.command_request_id)
                                  AND NOT EXISTS(SELECT 1 FROM operation o WHERE o.execution_command_id=c.command_request_id)
                                  AND NOT EXISTS(SELECT 1 FROM lease l WHERE l.command_request_id=c.command_request_id)
                                ORDER BY c.command_request_id LIMIT 1;
                                """,token,("$id",id));
                            if(command is null)break;
                            var commandBytes=await ScalarLongAsync(connection,tx,
                                "SELECT summary_bytes FROM command_request WHERE command_request_id=$command;",
                                token,("$command",command));
                            if(size+bytes+commandBytes>maximumBytes)break;
                            permission.CommandId=command;
                            await ExecuteAsync(connection,tx,"""
                                UPDATE command_request SET consent_id=NULL,consent_detail_expired_utc=$utc
                                WHERE command_request_id=$command AND consent_id=$id;
                                """,token,("$command",command),("$id",id),("$utc",Utc(ActivityRetentionNow)));
                            permission.CommandId=null;rows++;detached++;bytes+=commandBytes;
                        }
                        if(await ScalarLongAsync(connection,tx,
                            "SELECT EXISTS(SELECT 1 FROM command_request WHERE consent_id=$id);",token,("$id",id))==0)
                        {
                            await ExecuteAsync(connection,tx,"DELETE FROM consent_header WHERE consent_id=$id;",token,("$id",id));
                            await ExecuteAsync(connection,tx,
                                "UPDATE installation SET consent_expired_headers=consent_expired_headers+1;",token);
                            headers++;
                        }
                        bytes+=size;permission.Clear();
                    }
                }
                await ExecuteAsync(connection,tx,"UPDATE installation SET closed_consent_frontier_id=$id;",token,("$id",id));
            }
            return(rows,bytes,headers);
        }
        finally{permission.Clear();}
    }
}
