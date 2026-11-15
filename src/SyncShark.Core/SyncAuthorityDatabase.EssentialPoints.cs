using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private const string EssentialJournalPointReferences=
        "SELECT json_extract(preconditions_json,'$.synchronizationPointId') AS point_id,pair_id,object_id "+
        "FROM operation WHERE operation_id IN ("+EssentialOperations+") "+
        "AND json_extract(preconditions_json,'$.synchronizationPointId') IS NOT NULL UNION "+
        "SELECT json_extract(j.value,'$.SynchronizationPointId') AS point_id,"+
        "json_extract(j.value,'$.PairId') AS pair_id,json_extract(j.value,'$.ObjectId') AS object_id "+
        "FROM operation o,json_each(o.preconditions_json,'$.planningAuthorities') j "+
        "WHERE o.operation_id IN ("+EssentialOperations+") AND json_extract(j.value,'$.SynchronizationPointId') IS NOT NULL";
    private const string EssentialPoints="SELECT synchronization_point_id FROM current_synchronization_point "+
        "UNION SELECT point_id FROM "+EssentialCopyCurrent+" UNION SELECT point_id FROM "+EssentialCopyPredecessor+" UNION SELECT point_id FROM ("+EssentialJournalPointReferences+") "+
        "UNION SELECT synchronization_point_id FROM reconciliation_decision WHERE reconciliation_decision_id IN ("+EssentialDecisions+")"+" UNION "+EssentialAbsencePoints+
        " UNION SELECT json_extract(completion_state_json,'$.pointId') FROM operation WHERE operation_id IN ("+
        EssentialOperations+") AND closure_json IS NULL AND json_extract(completion_state_json,'$.kind')='synchronization_point'";
    private const string EssentialPointMembers=
        "SELECT local_membership_id FROM synchronization_point WHERE synchronization_point_id IN ("+EssentialPoints+") "+
        "UNION SELECT dropbox_membership_id FROM synchronization_point WHERE synchronization_point_id IN ("+EssentialPoints+")";
    private const string EssentialPointVersions=
        "SELECT local_object_version_id FROM synchronization_point WHERE synchronization_point_id IN ("+EssentialPoints+") "+
        "UNION SELECT dropbox_object_version_id FROM synchronization_point WHERE synchronization_point_id IN ("+EssentialPoints+")";
    private static async Task RequireEssentialJournalPointsAsync(SqliteConnection source,SqliteTransaction snapshot,CancellationToken token)
    {
        if(await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,
            "SELECT count(*) FROM ("+EssentialJournalPointReferences+") r "+
            "LEFT JOIN synchronization_point p ON p.synchronization_point_id=r.point_id "+
            "WHERE p.synchronization_point_id IS NULL OR p.pair_id IS NOT r.pair_id OR p.object_id IS NOT r.object_id;"),token)!=0)
            throw new InvalidDataException("Retained journal baseline reference is missing or belongs to another pair/object.");
    }
}
