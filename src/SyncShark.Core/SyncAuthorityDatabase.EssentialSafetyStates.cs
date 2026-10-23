using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private const string EssentialTombstoneLegacyColumns="tombstone_id,pair_id,object_id,absent_side,prior_membership_id,"+
        "prior_object_version_id,observed_absent_utc,complete_observation_count,status";
    internal const string EssentialTombstoneColumns=EssentialTombstoneLegacyColumns+",both_absent_basis_json,both_absent_closed_utc";
    private const string EssentialTombstones="SELECT tombstone_id FROM tombstone WHERE status NOT IN ('resolved','superseded') "+
        "OR tombstone_id IN (SELECT json_extract(preconditions_json,'$.tombstoneId') FROM operation WHERE operation_id IN ("+EssentialOperations+"))"+
        " UNION SELECT json_extract(completion_state_json,'$.id') FROM operation WHERE operation_id IN ("+
        EssentialOperations+") AND closure_json IS NULL AND json_extract(completion_state_json,'$.kind')='resolved_tombstone'";
    private const string EssentialConflicts="SELECT conflict_id FROM conflict WHERE status NOT IN ('resolved','superseded') "+
        "OR conflict_id IN (SELECT json_extract(preconditions_json,'$.conflictId') FROM operation WHERE operation_id IN ("+EssentialOperations+"))"+
        " UNION SELECT conflict_id FROM conflict_resolution WHERE status NOT IN ('completed','stale','cancelled')";
    private static readonly EssentialConfigurationTable[] EssentialSafetyTables=
    [
        EssentialJournalTable("conflict","conflict_id,pair_id,object_id,conflict_generation,reason,status,created_utc,resolved_utc",
            "conflict_id","conflict_id IN ("+EssentialConflicts+")"),
        EssentialJournalTable("conflict_version","conflict_id,side,object_version_id,membership_id","conflict_id,side",
            "conflict_id IN ("+EssentialConflicts+")"),
        new("tombstone",EssentialTombstoneColumns,"tombstone_id",
            "SELECT "+EssentialTombstoneLegacyColumns+
            ",CASE WHEN both_absent_basis_json IS NOT NULL THEN json_set(both_absent_basis_json,'$.DatabaseGeneration',"+
            "__essential_successor_generation__) END AS both_absent_basis_json,both_absent_closed_utc "+
            "FROM tombstone WHERE tombstone_id IN ("+EssentialTombstones+")"),
        EssentialJournalTable("tombstone_observation","tombstone_id,local_scan_generation_id,dropbox_scan_generation_id,observed_utc",
            "tombstone_id,local_scan_generation_id,dropbox_scan_generation_id","tombstone_id IN ("+EssentialTombstones+")")
    ];
    private const string EssentialSafetyScans="SELECT local_scan_generation_id FROM tombstone_observation WHERE tombstone_id IN ("+EssentialTombstones+") "+
        "UNION SELECT dropbox_scan_generation_id FROM tombstone_observation WHERE tombstone_id IN ("+EssentialTombstones+")";
    private const string EssentialSafetyMembers="SELECT prior_membership_id FROM tombstone WHERE tombstone_id IN ("+EssentialTombstones+") "+
        "UNION SELECT membership_id FROM conflict_version WHERE conflict_id IN ("+EssentialConflicts+")";
    private const string EssentialSafetyVersions="SELECT prior_object_version_id FROM tombstone WHERE tombstone_id IN ("+EssentialTombstones+") "+
        "UNION SELECT object_version_id FROM conflict_version WHERE conflict_id IN ("+EssentialConflicts+")";
    private static async Task RequireEssentialSafetyStateAsync(SqliteConnection source,SqliteTransaction snapshot,CancellationToken token)
    {
        await RequireEssentialAbsenceAsync(source,snapshot,token);
        if(await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,"""
            SELECT count(*) FROM tombstone_observation o JOIN tombstone t ON t.tombstone_id=o.tombstone_id
            LEFT JOIN scan_generation l ON l.scan_generation_id=o.local_scan_generation_id
            LEFT JOIN scan_generation r ON r.scan_generation_id=o.dropbox_scan_generation_id
            WHERE o.tombstone_id IN (
            """+EssentialTombstones+") AND (l.status IS NOT 'complete' OR r.status IS NOT 'complete' "+
            "OR l.pair_id IS NOT t.pair_id OR r.pair_id IS NOT t.pair_id OR l.side IS NOT 'local' OR r.side IS NOT 'dropbox');"),token)!=0)
            throw new InvalidDataException("Retained tombstone observations require exact complete cuts of their own pair and sides.");
    }
}
