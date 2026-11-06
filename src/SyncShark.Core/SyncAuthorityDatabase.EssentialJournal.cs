using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

// The seven existing INSERT checks retain their ordinary predicates. Only the
// explicit source-bound offline importer can install a one-row matching permit.
internal static class SyncEssentialJournalGuards
{
    internal sealed record Guard(string Name,string Table,string Columns,string OriginalSql);
    private static Guard Bind(string name,string table,string columns,string schema)
    {
        var matches=Regex.Matches(schema,@"(?ms)^CREATE TRIGGER IF NOT EXISTS "+Regex.Escape(name)+@"\b.*?(?:^END;|(?<=;) END;)");
        if(matches.Count!=1)throw new InvalidDataException("Essential import guard predecessor is not exact: "+name);
        return new(name,table,columns,matches[0].Value);
    }
    internal const string MemberColumns="operation_member_id,operation_id,pair_id,object_id,role,root_binding_id,membership_id,object_version_id,expected_membership_generation,expected_path_key,expected_state_json";
    internal const string DependencyColumns="operation_id,depends_on_operation_id,dependency_kind,retired_dependency_id,terminal_proof_json,terminal_proof_sha256";
    internal const string AllocationColumns="allocation_id,operation_id,control_namespace_id,resource_kind,deterministic_name,intended_parent_identity,owner_nonce_hash,status,bound_identity,content_hash,size_bytes,transition_lease_id,transition_fencing_token,created_utc";
    internal const string AttemptColumns="attempt_id,operation_id,attempt_no,lease_id,fencing_token,transition_lease_id,transition_fencing_token,request_fingerprint,effect_kind,status,started_utc,ended_utc";
    internal const string ReceiptColumns="effect_receipt_id,attempt_id,recorded_lease_id,recorded_fencing_token,certainty,source_identity_json,destination_identity_json,provider_metadata_json,evidence_digest,observed_utc";
    internal const string UploadColumns="allocation_id,operation_id,session_id,confirmed_offset_bytes,source_object_version_id,source_size_bytes,source_content_hash,status,transition_lease_id,transition_fencing_token,updated_utc";
    internal static readonly Guard[] Guards=
    [
        Bind("operation_member_insert_only_while_planned","operation_member",MemberColumns,SyncAuthoritySchema.Sql),
        Bind("operation_dependency_insert_only_while_planned","operation_dependency",DependencyColumns,SyncAuthoritySchemaMigration15.Sql),
        Bind("allocation_insert_only_while_planned","resource_allocation",AllocationColumns,SyncAuthoritySchema.Sql),
        Bind("no_new_attempt_while_unreconciled","operation_attempt",AttemptColumns,SyncAuthoritySchema.Sql),
        Bind("attempt_requires_current_fence","operation_attempt",AttemptColumns,SyncAuthoritySchema.Sql),
        Bind("receipt_requires_current_fence","effect_receipt",ReceiptColumns,SyncAuthoritySchema.Sql),
        Bind("upload_session_checkpoint_requires_current_fence_insert","upload_session_checkpoint",UploadColumns,SyncAuthoritySchemaMigration3.Sql)
    ];
    internal static readonly Guard[] ConsentGuards=
    [
        Bind("consent_item_no_retired_reinsertion","consent_item",SyncAuthorityDatabase.EssentialConsentItemColumns,SyncConsentLifetimeSchema.Sql),
        Bind("consent_new_header_accounted","consent_header",SyncAuthorityDatabase.EssentialConsentHeaderColumns,SyncConsentLifetimeSchema.Sql),
        Bind("consent_new_item_accounted","consent_item",SyncAuthorityDatabase.EssentialConsentItemColumns,SyncConsentLifetimeSchema.Sql),
        Bind("consent_command_active_insert","command_request",SyncAuthorityDatabase.EssentialCommandColumns,SyncConsentLifetimeSchema.Sql),
        Bind("consent_operation_reference_insert","operation",SyncAuthorityDatabase.EssentialOperationColumns,SyncConsentLifetimeSchema.Sql),
        Bind("operation_consent_item_issue_from_attempt","operation_attempt",AttemptColumns,SyncAuthoritySchemaMigration12.Sql),
        Bind("history_lifecycle_producer_guard","history_event",SyncAuthorityDatabase.EssentialHistoryColumns,SyncAuthoritySchemaMigration15.Sql)
    ];
    internal static readonly Guard[] AbsenceGuards=[
        Bind("tombstone_both_absent_insert_guard","tombstone",SyncAuthorityDatabase.EssentialTombstoneColumns,SyncBothAbsentLifetimeSchema.Sql)
    ];
    internal static string WrappedSql(Guard g)
    {
        var sql=g.OriginalSql;var body=sql.IndexOf("\nBEGIN",StringComparison.Ordinal);
        if(body<0)throw new InvalidDataException("Essential guard body is absent.");
        var header=sql[..body];var when=header.IndexOf("WHEN ",StringComparison.Ordinal);
        var permit="syncshark_successor_row_allowed('"+g.Table+"',json_array("+
            string.Join(",",g.Columns.Split(',').Select(x=>"NEW."+x))+"))<>1";
        header=when<0?header+"\nWHEN "+permit:header[..when]+"WHEN "+permit+" AND ("+header[(when+5)..]+")";
        return "DROP TRIGGER "+g.Name+";\n"+header+sql[body..];
    }
}

public sealed partial class SyncAuthorityDatabase
{

    private const string EssentialOperationLegacyColumns="operation_id,pair_id,object_id,kind,status,priority,"+
        "configuration_revision,capability_profile_id,idempotency_key,preconditions_json,current_lease_id,current_fencing_token,"+
        "last_transition_lease_id,last_transition_fencing_token,created_utc,ready_utc,terminal_utc,block_code,block_detail,operation_group_id";
    internal const string EssentialOperationColumns=EssentialOperationLegacyColumns+
        ",origin_command_id,execution_command_id,consent_id,consent_item_ordinal,registration_generation,registration_sequence,"+
        "registration_intent_sha256,closure_utc,closure_json,closure_sha256,closure_encoded_bytes,completion_state_json,closure_kind";
    private const string EssentialCommandLegacyColumns="command_request_id,command_kind,pair_id,target_id,expected_generation,"+
        "payload_json,requested_utc,status,terminal_utc,result_json";
    internal const string EssentialCommandColumns=EssentialCommandLegacyColumns+
        ",admission_generation,admission_purpose,admission_sequence,consent_id,next_activity_ordinal,activity_opened,activity_closed,"+
        "activity_incomplete,single_action_id,ordinary_issuance_closed_utc,consent_detail_expired_utc";
    private const string EssentialLeaseLegacyColumns="lease_id,scope_kind,scope_id,worker_id,fencing_token,acquired_utc,expires_utc,status";

    // Unresolved effects and unreleased resources are roots; dependencies add
    // their prerequisites and exact group members. Historical completed cohorts are not roots.
    private const string EssentialOperations="""
        WITH RECURSIVE required(id) AS (
            SELECT o.operation_id FROM operation o
            WHERE o.operation_id IN (SELECT operation_id FROM rewind_path_progress WHERE session_id IN
                (SELECT session_id FROM rewind_session WHERE status<>'resumed') AND operation_id IS NOT NULL)
               OR o.operation_group_id IN (SELECT operation_group_id FROM rewind_session WHERE status<>'resumed' AND operation_group_id IS NOT NULL)
               OR o.operation_group_id IN (SELECT operation_group_id FROM conflict_resolution WHERE status NOT IN ('completed','stale','cancelled'))
               OR o.operation_id IN (SELECT preserve_action_id FROM conflict_resolution WHERE status NOT IN ('completed','stale','cancelled'))
               OR o.operation_id IN (SELECT resolution_action_id FROM conflict_resolution WHERE status NOT IN ('completed','stale','cancelled'))
               OR o.status NOT IN ('completed','cancelled','superseded')
               OR EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id
                   AND a.status NOT IN ('verified_success','verified_not_applied'))
               OR EXISTS(SELECT 1 FROM resource_allocation r WHERE r.operation_id=o.operation_id AND r.status<>'released')
               OR EXISTS(SELECT 1 FROM namespace_allocation n WHERE n.operation_id=o.operation_id
                   AND (n.status<>'released' OR n.namespace_allocation_id IN (SELECT namespace_allocation_id FROM control_namespace)))
               OR EXISTS(SELECT 1 FROM recovery_artifact a WHERE a.operation_id=o.operation_id AND a.status<>'released')
               OR EXISTS(SELECT 1 FROM resource_allocation r JOIN recovery_artifact a ON a.allocation_id=r.allocation_id
                   WHERE r.operation_id=o.operation_id AND a.status<>'released')
            UNION
            SELECT d.depends_on_operation_id FROM operation_dependency d JOIN required r ON r.id=d.operation_id
            WHERE d.depends_on_operation_id IS NOT NULL
            UNION
            SELECT sibling.operation_id FROM operation sibling
            JOIN operation member ON member.operation_group_id=sibling.operation_group_id
            JOIN required r ON r.id=member.operation_id
            WHERE sibling.operation_group_id IS NOT NULL
        ) SELECT id FROM required
        """;
    private const string EssentialDirectCommands="SELECT command_request_id FROM command_request WHERE status NOT IN ('completed','cancelled','rejected') "+
        "UNION SELECT origin_command_id FROM operation WHERE operation_id IN ("+EssentialOperations+") AND origin_command_id IS NOT NULL "+
        "UNION SELECT execution_command_id FROM operation WHERE operation_id IN ("+EssentialOperations+") AND execution_command_id IS NOT NULL "+
        "UNION SELECT command_request_id FROM lease WHERE scope_kind='operation' AND scope_id IN ("+EssentialOperations+") AND command_request_id IS NOT NULL "+
        "UNION SELECT command_request_id FROM conflict_resolution WHERE status NOT IN ('completed','stale','cancelled') AND command_request_id IS NOT NULL";
    private const string EssentialBaseCommands=EssentialDirectCommands+" UNION SELECT command_id FROM _essential_reviewed_lineage";
    private const string EssentialCommands=EssentialBaseCommands+
        " UNION SELECT originating_command_id FROM consent_header WHERE consent_id IN ("+EssentialConsents+") AND originating_command_id IS NOT NULL"+
        " UNION SELECT prepared_command_id FROM consent_header WHERE consent_id IN ("+EssentialConsents+") AND prepared_command_id IS NOT NULL";
    private static string EssentialJournalLegacySql(string sql)
    {
        string Shape(string name,string columns)
        {
            var projected="(SELECT "+columns+" FROM "+name+")";
            return Regex.Replace(sql,@"\b(FROM|JOIN)\s+"+Regex.Escape(name)+@"\b",
                m=>m.Groups[1].Value+" "+projected,RegexOptions.CultureInvariant);
        }
        sql=Shape("scan_generation","scan_generation_id,pair_id,root_binding_id,side,generation,trigger_kind,scope_json,started_utc,completed_utc,status,entry_count,NULL AS ended_utc");
        sql=Shape("operation",EssentialOperationLegacyColumns+
            ",NULL AS origin_command_id,NULL AS execution_command_id,NULL AS consent_id,NULL AS consent_item_ordinal,"+
            "NULL AS registration_generation,NULL AS registration_sequence,NULL AS registration_intent_sha256,"+
            "NULL AS closure_utc,NULL AS closure_json,NULL AS closure_sha256,NULL AS closure_encoded_bytes,"+
            "NULL AS completion_state_json,NULL AS closure_kind");
        sql=Shape("command_request",EssentialCommandLegacyColumns+
            ",NULL AS admission_generation,NULL AS admission_purpose,NULL AS admission_sequence,NULL AS consent_id,"+
            "1 AS next_activity_ordinal,0 AS activity_opened,0 AS activity_closed,0 AS activity_incomplete,"+
            "NULL AS single_action_id,NULL AS ordinary_issuance_closed_utc,NULL AS consent_detail_expired_utc");
        sql=Shape("lease",EssentialLeaseLegacyColumns+",NULL AS command_request_id,0 AS dispatch_allowed");
        sql=Shape("conflict_resolution",EssentialResolutionLegacyColumns+",NULL AS preserve_action_id,NULL AS resolution_action_id");
        sql=Shape("tombstone",EssentialTombstoneLegacyColumns+",NULL AS both_absent_basis_json,NULL AS both_absent_closed_utc");
        sql=Shape("history_event","rowid,history_event_id,operation_id,event_kind,detail_json,occurred_utc,"+
            "NULL AS activity_context_key,NULL AS producer_command_id,NULL AS producer_ordinal,'safety' AS retention_class,"+
            "0 AS encoded_bytes,NULL AS expires_utc,'legacy' AS producer_kind");
        sql=Shape("operation_dependency","operation_id,depends_on_operation_id,dependency_kind,"+
            "NULL AS retired_dependency_id,NULL AS terminal_proof_json,NULL AS terminal_proof_sha256");
        foreach(var table in new[]{("consent_header",EssentialConsentHeaderColumns),("consent_item",EssentialConsentItemColumns),
            ("rewind_session",EssentialRewindHeaderColumns),("rewind_path_progress",EssentialRewindProgressColumns)})
            sql=Regex.Replace(sql,@"\b(FROM|JOIN)\s+"+table.Item1+@"\b",
                m=>m.Groups[1].Value+" (SELECT "+string.Join(",",table.Item2.Split(',').Select(x=>"NULL AS "+x))+" WHERE 0)",
                RegexOptions.CultureInvariant);
        sql=Regex.Replace(sql,@"\b(FROM|JOIN)\s+activity_context\b",
            m=>m.Groups[1].Value+" (SELECT "+string.Join(",",EssentialHistoryContextColumns.Split(',').Select(x=>"NULL AS "+x))+" WHERE 0)",
            RegexOptions.CultureInvariant);
        return sql;
    }
    private static readonly EssentialConfigurationTable[] EssentialJournalHeaders=
    [
        new("command_request",EssentialCommandColumns,"command_request_id",
            "SELECT "+EssentialCommandColumns+" FROM command_request WHERE command_request_id IN ("+EssentialCommands+")"),
        new("operation_group","operation_group_id,kind,status,created_utc,terminal_utc,detail_json","operation_group_id",
            "SELECT operation_group_id,kind,status,created_utc,terminal_utc,detail_json FROM operation_group "+
            "WHERE operation_group_id IN (SELECT operation_group_id FROM operation WHERE operation_id IN ("+EssentialOperations+")) "+
            "OR operation_group_id IN (SELECT operation_group_id FROM rewind_session WHERE status<>'resumed' AND operation_group_id IS NOT NULL) "+
            "OR operation_group_id IN (SELECT operation_group_id FROM conflict_resolution WHERE status NOT IN ('completed','stale','cancelled') AND operation_group_id IS NOT NULL)"),
        new("lease",EssentialLeaseLegacyColumns+",command_request_id,dispatch_allowed","lease_id",
            "SELECT lease_id,scope_kind,scope_id,worker_id,fencing_token,acquired_utc,expires_utc,"+
            "CASE WHEN status='active' THEN 'revoked' ELSE status END AS status,command_request_id,CASE WHEN status='active' THEN 0 ELSE dispatch_allowed END AS dispatch_allowed "+
            "FROM lease WHERE (scope_kind='operation' AND scope_id IN ("+EssentialOperations+")) "+
            "OR lease_id IN (SELECT lease_id FROM dirty_item WHERE dirty_item_id IN ("+EssentialDirtyItems+") AND lease_id IS NOT NULL)")
    ];
    private static EssentialConfigurationTable EssentialJournalTable(string name,string columns,string keys,string selector)=>
        new(name,columns,keys,"SELECT "+columns+" FROM "+name+" WHERE "+selector);
    private static readonly EssentialConfigurationTable[] EssentialJournalBodies=
    [
        EssentialJournalTable("operation",EssentialOperationColumns,"operation_id","operation_id IN ("+EssentialOperations+")"),
        EssentialJournalTable("namespace_allocation","namespace_allocation_id,operation_id,side,parent_identity,deterministic_name,"+
            "owner_nonce_hash,status,bound_identity,transition_lease_id,transition_fencing_token,created_utc","namespace_allocation_id",
            "operation_id IN ("+EssentialOperations+")"),
        EssentialJournalTable("control_namespace","control_namespace_id,namespace_allocation_id,side,identity_json,"+
            "marker_identity_json,owner_marker_hash,status,verified_utc","control_namespace_id","1=1"),
        EssentialJournalTable("pair_control_namespace","pair_id,side,control_namespace_id,binding_generation,status",
            "pair_id,side,binding_generation","1=1"),
        EssentialJournalTable("operation_member",SyncEssentialJournalGuards.MemberColumns,"operation_member_id","operation_id IN ("+EssentialOperations+")"),
        EssentialJournalTable("operation_dependency",SyncEssentialJournalGuards.DependencyColumns,"operation_id,depends_on_operation_id,retired_dependency_id","operation_id IN ("+EssentialOperations+")"),
        EssentialJournalTable("resource_allocation",SyncEssentialJournalGuards.AllocationColumns,"allocation_id","operation_id IN ("+EssentialOperations+")"),
        EssentialJournalTable("recovery_artifact","recovery_artifact_id,operation_id,object_id,allocation_id,side,identity_json,"+
            "content_hash,size_bytes,status,retained_utc,retain_until_utc,released_utc","recovery_artifact_id","status<>'released'"),
        EssentialJournalTable("operation_attempt",SyncEssentialJournalGuards.AttemptColumns,"attempt_id","operation_id IN ("+EssentialOperations+")"),
        EssentialJournalTable("effect_receipt",SyncEssentialJournalGuards.ReceiptColumns,"effect_receipt_id",
            "attempt_id IN (SELECT attempt_id FROM operation_attempt WHERE operation_id IN ("+EssentialOperations+"))"),
        EssentialJournalTable("operation_step","operation_step_id,operation_id,ordinal,step_kind,status,preconditions_json,created_utc,terminal_utc",
            "operation_step_id","operation_id IN ("+EssentialOperations+")"),
        EssentialJournalTable("upload_session_checkpoint",SyncEssentialJournalGuards.UploadColumns,"allocation_id","operation_id IN ("+EssentialOperations+")")
    ];
    // The pending run's presentation remains visible, without importing its
    // unbounded historical chatter or transferring any command admission.
    internal const string EssentialHistoryColumns="rowid,history_event_id,operation_id,event_kind,detail_json,occurred_utc,"+
        "activity_context_key,producer_command_id,producer_ordinal,retention_class,encoded_bytes,expires_utc,producer_kind";
    private const string EssentialPendingHistoryPredicate="producer_kind='run' AND retention_class='safety' "+
        "AND event_kind='presentation' AND operation_id IS NULL AND activity_context_key IS NULL "+
        "AND producer_command_id IS NULL AND producer_ordinal IS NULL "+
        "AND json_extract(detail_json,'$.RunId') IN ("+EssentialCommands+")";
    private static readonly EssentialConfigurationTable[] EssentialPendingHistoryTables=
    [
        new("history_event",EssentialHistoryColumns,"rowid",
            EssentialSelectedHistorySql)
    ];
    private static Task<long> EssentialNextHistoryAsync(SqliteConnection c,SqliteTransaction? tx,CancellationToken token)=>
        EssentialLegacySource(c)?ScalarLongAsync(c,tx,"SELECT coalesce(max(rowid),0)+1 FROM history_event;",token):
            ScalarLongAsync(c,tx,"SELECT next_history_sequence FROM installation;",token);
    private const string EssentialJournalMembers="SELECT membership_id FROM operation_member WHERE operation_id IN ("+EssentialOperations+") AND membership_id IS NOT NULL";
    private const string EssentialJournalVersions="SELECT object_version_id FROM operation_member WHERE operation_id IN ("+EssentialOperations+") AND object_version_id IS NOT NULL "+
        "UNION SELECT source_object_version_id FROM upload_session_checkpoint WHERE operation_id IN ("+EssentialOperations+")";
    private const string EssentialJournalScans=
        "SELECT json_extract(j.value,'$.LocalScanGenerationId') FROM operation o,json_each(o.preconditions_json,'$.planningAuthorities') j "+
        "WHERE o.operation_id IN ("+EssentialOperations+") UNION "+
        "SELECT json_extract(j.value,'$.DropboxScanGenerationId') FROM operation o,json_each(o.preconditions_json,'$.planningAuthorities') j "+
        "WHERE o.operation_id IN ("+EssentialOperations+") UNION "+
        "SELECT json_extract(preconditions_json,'$.localScanGenerationId') FROM operation WHERE operation_id IN ("+EssentialOperations+") UNION "+
        "SELECT json_extract(preconditions_json,'$.dropboxScanGenerationId') FROM operation WHERE operation_id IN ("+EssentialOperations+")";
    private sealed class EssentialInsertPermit
    {
        internal string? Table;
        internal object?[]? Row;
        internal bool Allows(string? table,string? json)
        {
            if(Table!=table||Row is null||json is null)return false;
            try
            {
                using var document=JsonDocument.Parse(json);
                if(document.RootElement.ValueKind!=JsonValueKind.Array)return false;
                var values=document.RootElement.EnumerateArray().ToArray();
                if(values.Length!=Row.Length)return false;
                for(var i=0;i<values.Length;i++)
                {
                    var value=values[i];var expected=Row[i];
                    if(expected is null){if(value.ValueKind!=JsonValueKind.Null)return false;}
                    else if(expected is string text){if(value.ValueKind!=JsonValueKind.String||value.GetString()!=text)return false;}
                    else if(expected is long number){if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt64(out var actual)||actual!=number)return false;}
                    else return false;
                }
                return true;
            }
            catch(JsonException){return false;}
        }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SqliteConnection,EssentialInsertPermit>
        EssentialInsertPermits=new();

    private static void ConfigureEssentialInsertFunction(SqliteConnection connection,EssentialInsertPermit? permit=null)
    {
        var code=SQLitePCL.raw.sqlite3_create_function(connection.Handle!,"syncshark_successor_row_allowed",2,
            SQLitePCL.raw.SQLITE_UTF8|0x00200000,null,(context,state,values)=>
            {
                var allowed=permit is not null&&values.Length==2&&permit.Allows(
                    SQLitePCL.raw.sqlite3_value_text(values[0]).utf8_to_string(),
                    SQLitePCL.raw.sqlite3_value_text(values[1]).utf8_to_string());
                SQLitePCL.raw.sqlite3_result_int(context,allowed?1:0);
            });
        if(code!=SQLitePCL.raw.SQLITE_OK)throw new InvalidDataException("Essential row permit registration failed.");
    }

    private static async Task ConfigureEssentialJournalImportAsync(SqliteConnection target,CancellationToken token)
    {
        var receipt=await ReadEssentialReceiptAsync(target,token);
        if(receipt.Complete)throw new InvalidDataException("Completed successors cannot receive a new import permit.");
        var permit=new EssentialInsertPermit();
        EssentialInsertPermits.Add(target,permit);ConfigureEssentialInsertFunction(target,permit);
    }

    private static async Task<List<(string Name,string Sql)>> OpenEssentialRowGuardsAsync(
        SqliteConnection target,SqliteTransaction tx,EssentialConfigurationTable table,CancellationToken token)
    {
        var originals=new List<(string,string)>();
        foreach(var guard in SyncEssentialJournalGuards.Guards.Concat(SyncEssentialJournalGuards.ConsentGuards).Concat(SyncEssentialJournalGuards.AbsenceGuards).Where(x=>x.Table==table.Name))
        {
            if(guard.Columns!=table.Columns)throw new InvalidDataException("The private import row shape differs.");
            var sql=await ScalarStringAsync(target,tx,"SELECT sql FROM sqlite_schema WHERE type='trigger' AND name=$name;",
                token,("$name",guard.Name));
            static string Normalize(string s)=>s.Replace(" IF NOT EXISTS","",StringComparison.Ordinal).Trim().TrimEnd(';');
            if(sql is null||Normalize(sql)!=Normalize(guard.OriginalSql))
                throw new InvalidDataException("The target journal INSERT guard differs from current production.");
            originals.Add((guard.Name,sql));
            await ExecuteAsync(target,tx,SyncEssentialJournalGuards.WrappedSql(guard),token);
        }
        return originals;
    }

    private static async Task CloseEssentialRowGuardsAsync(SqliteConnection target,SqliteTransaction tx,
        IReadOnlyList<(string Name,string Sql)> originals,CancellationToken token)
    {
        foreach(var (name,sql) in originals)
        {
            await ExecuteAsync(target,tx,"DROP TRIGGER "+name+"; "+sql+";",token);
            if(await ScalarStringAsync(target,tx,"SELECT sql FROM sqlite_schema WHERE type='trigger' AND name=$name;",
                token,("$name",name))!=sql)throw new InvalidDataException("Original INSERT guard was not restored exactly.");
        }
    }
}
