using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    internal const string EssentialConsentHeaderColumns=ConsentHeaderColumns+
        ",completed_prefix,terminal_ranges_json,retired_choice_count,retired_choice_bytes,normalized_origin_accounted,"+
        "eligible_payload_bytes,active_effect_commands,commands_accounted,retirement_started_utc";
    internal const string EssentialConsentItemColumns=ConsentItemColumns+",retained_operation_count,eligible_bytes_accounted";

    // A live original approval or exact journal reference is retained once. Closed
    // unrelated approvals remain in the unchanged original, not a new history graph.
    private const string EssentialConsents="SELECT consent_id FROM consent_header WHERE lifecycle<>'closed' "+
        "OR consent_id IN (SELECT consent_id FROM operation WHERE operation_id IN ("+EssentialOperations+")) "+
        "OR consent_id IN (SELECT consent_id FROM command_request WHERE command_request_id IN ("+EssentialBaseCommands+"))";
    private const string EssentialConsentScans=
        "SELECT json_extract(header_json,'$.plan.LocalScanGenerationId') FROM consent_header WHERE consent_id IN ("+EssentialConsents+") "+
        "UNION SELECT json_extract(header_json,'$.plan.DropboxScanGenerationId') FROM consent_header WHERE consent_id IN ("+EssentialConsents+")";
    private const string EssentialConsentOperationCount="(SELECT count(*) FROM operation o WHERE o.consent_id=i.consent_id "+
        "AND o.consent_item_ordinal=i.ordinal AND o.operation_id IN ("+EssentialOperations+"))";
    private const string EssentialConsentOriginAccounted="CASE WHEN EXISTS(SELECT 1 FROM command_request c "+
        "WHERE c.command_request_id=h.originating_command_id AND c.command_request_id IN ("+EssentialCommands+") "+
        "AND c.consent_id=h.consent_id AND json_extract(c.payload_json,'$.consentId')=h.consent_id "+
        "AND json_type(c.payload_json,'$.reviewedPlan') IS NULL) THEN 1 ELSE 0 END";
    private const string EssentialConsentActiveCommands="(SELECT count(*) FROM command_request c "+
        "WHERE c.consent_id=h.consent_id AND c.command_request_id IN ("+EssentialCommands+") "+
        "AND c.status IN ('accepted','executing') AND c.ordinary_issuance_closed_utc IS NULL "+
        "AND c.admission_purpose IN ('normal_effect','control_allocation'))";
    private const string EssentialConsentEligibleBytes="(SELECT coalesce(sum(i.encoded_bytes),0) FROM consent_item i "+
        "WHERE i.consent_id=h.consent_id AND i.lifecycle='eligible' AND "+EssentialConsentOperationCount+"=0)";
    private static readonly EssentialConfigurationTable[] EssentialConsentTables=
    [
        new("consent_header",EssentialConsentHeaderColumns,"consent_id",
            "SELECT "+ConsentHeaderColumns+",completed_prefix,terminal_ranges_json,retired_choice_count,retired_choice_bytes,"+
            EssentialConsentOriginAccounted+" AS normalized_origin_accounted,"+
            EssentialConsentEligibleBytes+" AS eligible_payload_bytes,"+
            EssentialConsentActiveCommands+" AS active_effect_commands,1 AS commands_accounted,retirement_started_utc "+
            "FROM consent_header h WHERE consent_id IN ("+EssentialConsents+")"),
        new("consent_item",EssentialConsentItemColumns,"consent_id,ordinal",
            "SELECT "+ConsentItemColumns+","+EssentialConsentOperationCount+
            " AS retained_operation_count,1 AS eligible_bytes_accounted FROM consent_item i WHERE consent_id IN ("+EssentialConsents+")")
    ];

    private static async Task SetEssentialConsentAccountingAsync(SqliteConnection target,CancellationToken token)
    {
        await ExecuteAsync(target,null,"""
            UPDATE installation SET
                consent_inactive_eligible_bytes=(SELECT coalesce(sum(eligible_payload_bytes),0) FROM consent_header
                    WHERE commands_accounted=1 AND normalized_origin_accounted=1 AND active_effect_commands=0
                      AND lifecycle IN ('active','inactive','partially_expired','closed')),
                consent_retired_items=(SELECT coalesce(sum(retired_choice_count),0) FROM consent_header),
                consent_retired_bytes=(SELECT coalesce(sum(retired_choice_bytes),0) FROM consent_header);
            """,token);
    }
    private static async Task RequireEssentialConsentAsync(SqliteConnection source,SqliteTransaction snapshot,CancellationToken token)
    {
        await RequireEssentialLegacyConsentLineageAsync(source,snapshot,token);
        if(!EssentialSourceHasTable(source,"consent_header"))return;
        if(await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,"""
            SELECT count(*) FROM consent_header h
            LEFT JOIN scan_generation l ON l.scan_generation_id=json_extract(h.header_json,'$.plan.LocalScanGenerationId')
            LEFT JOIN scan_generation r ON r.scan_generation_id=json_extract(h.header_json,'$.plan.DropboxScanGenerationId')
            WHERE h.consent_id IN (
            """+EssentialConsents+") AND (json_extract(h.header_json,'$.plan.PairId') IS NOT h.pair_id "+
            "OR l.status IS NOT 'complete' OR r.status IS NOT 'complete' OR l.pair_id IS NOT h.pair_id "+
            "OR r.pair_id IS NOT h.pair_id OR l.side IS NOT 'local' OR r.side IS NOT 'dropbox');"),token)!=0)
            throw new InvalidDataException("Original consent requires its exact complete planning cuts.");
    }
}
