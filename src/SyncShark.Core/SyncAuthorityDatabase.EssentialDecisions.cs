using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private const string EssentialDecisions=
        "SELECT reconciliation_decision_id FROM reconciliation_decision WHERE "+
        "dirty_item_id IN (SELECT dirty_item_id FROM dirty_item WHERE status<>'closed') "+
        "OR json_extract(decision_json,'$.operationId') IN ("+EssentialOperations+") "+
        "OR json_extract(decision_json,'$.conflictId') IN ("+EssentialConflicts+") "+
        "OR json_extract(decision_json,'$.tombstoneId') IN ("+EssentialTombstones+") "+
        "UNION SELECT reconciliation_decision_id FROM (SELECT reconciliation_decision_id,decision_kind,"+
        "row_number() OVER(PARTITION BY pair_id,object_id ORDER BY decided_utc DESC,reconciliation_decision_id DESC) AS ordinal "+
        "FROM reconciliation_decision WHERE object_id IS NOT NULL) WHERE ordinal=1 AND decision_kind='blocked'";
    private const string EssentialDirtyItems="SELECT dirty_item_id FROM dirty_item WHERE status<>'closed' "+
        "OR dirty_item_id IN (SELECT dirty_item_id FROM reconciliation_decision WHERE reconciliation_decision_id IN ("+EssentialDecisions+"))";
    private const string EssentialDecisionColumns=
        "reconciliation_decision_id,dirty_item_id,pair_id,object_id,local_scan_generation_id,dropbox_scan_generation_id,"+
        "synchronization_point_id,decision_kind,decision_json,decided_utc";
    private const string EssentialDecisionScans=
        "SELECT local_scan_generation_id FROM reconciliation_decision WHERE reconciliation_decision_id IN ("+EssentialDecisions+") "+
        "UNION SELECT dropbox_scan_generation_id FROM reconciliation_decision WHERE reconciliation_decision_id IN ("+EssentialDecisions+")";
    private static readonly EssentialConfigurationTable[] EssentialDecisionTables=
    [
        EssentialJournalTable("reconciliation_decision",EssentialDecisionColumns,"reconciliation_decision_id",
            "reconciliation_decision_id IN ("+EssentialDecisions+")")
    ];
    private static async Task RequireEssentialDecisionsAsync(SqliteConnection source,SqliteTransaction snapshot,CancellationToken token)
    {
        // Recognize the existing planner and explicit legacy-display producers.
        // Unknown rows are not silently treated as disposable history.
        if(await ScalarLongAsync(source,snapshot,"""
            SELECT count(*) FROM reconciliation_decision WHERE CASE WHEN
                json_type(decision_json,'$.path')='text' AND json_type(decision_json,'$.reason')='text'
                AND ((object_id IS NOT NULL AND local_scan_generation_id IS NOT NULL AND dropbox_scan_generation_id IS NOT NULL
                      AND lower(replace(json_extract(decision_json,'$.kind'),'_',''))=replace(decision_kind,'_',''))
                  OR (object_id IS NULL AND dirty_item_id IS NULL AND local_scan_generation_id IS NULL
                      AND dropbox_scan_generation_id IS NULL AND synchronization_point_id IS NULL
                      AND json_extract(decision_json,'$.migrated')=1 AND json_type(decision_json,'$.legacyOutcome')='text'))
                THEN 1 ELSE 0 END<>1;
            """,token)!=0)
            throw new InvalidDataException("A reconciliation decision has no supported planner or legacy-display disposition.");
        if(await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,"""
            SELECT count(*) FROM reconciliation_decision d
            LEFT JOIN scan_generation l ON l.scan_generation_id=d.local_scan_generation_id
            LEFT JOIN scan_generation r ON r.scan_generation_id=d.dropbox_scan_generation_id
            LEFT JOIN synchronization_point p ON p.synchronization_point_id=d.synchronization_point_id
            WHERE d.reconciliation_decision_id IN (
            """+EssentialDecisions+"""
            ) AND
              (l.status IS NOT 'complete' OR r.status IS NOT 'complete'
               OR l.side IS NOT 'local' OR r.side IS NOT 'dropbox' OR l.pair_id IS NOT d.pair_id OR r.pair_id IS NOT d.pair_id
               OR (d.synchronization_point_id IS NOT NULL AND
                   (p.synchronization_point_id IS NULL OR p.pair_id IS NOT d.pair_id OR p.object_id IS NOT d.object_id))
               OR (d.dirty_item_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dirty_item q
                   WHERE q.dirty_item_id=d.dirty_item_id AND q.pair_id=d.pair_id)));
            """),token)!=0)
            throw new InvalidDataException("Selected reconciliation decision requires exact own-pair complete cuts, baseline and dirty binding.");
        foreach(var endpoint in new[]{("operation","operationId","operation_id"),("conflict","conflictId","conflict_id"),("tombstone","tombstoneId","tombstone_id")})
        {
            var reference="json_extract(d.decision_json,'$."+endpoint.Item2+"')";
            var sql="SELECT count(*) FROM reconciliation_decision d WHERE d.reconciliation_decision_id IN ("+EssentialDecisions+") "+
                "AND "+reference+" IS NOT NULL AND NOT EXISTS(SELECT 1 FROM "+endpoint.Item1+" x WHERE x."+endpoint.Item3+"="+reference+
                " AND x.pair_id=d.pair_id AND x.object_id IS d.object_id);";
            if(await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,sql),token)!=0)
                throw new InvalidDataException("Selected reconciliation decision points to missing or foreign operational evidence.");
        }
    }
}
