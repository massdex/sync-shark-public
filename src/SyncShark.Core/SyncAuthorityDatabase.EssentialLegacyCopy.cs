using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private const string EssentialLegacyCandidateColumns=
        "object_id,prior_point_id,current_point_id,old_path,new_path,old_version_id,new_version_id,"+
        "old_remote_version_id,new_remote_version_id,old_membership_id,old_membership_generation,"+
        "new_membership_id,new_membership_generation,copy_operation_id";
    private const string EssentialCopyCurrent="_essential_copy_current";
    private const string EssentialCopyPredecessor="_essential_copy_predecessor";
    private static bool EssentialDerivedCopyTable(string name)=>name is "current_copy_completion" or "copy_predecessor";

    private static string EssentialLegacyCandidates(SqliteConnection source)
    {
        var sql=LegacyCompletedCopyMoveSql(!EssentialLegacySource(source)).Trim().TrimEnd(';');
        foreach(var name in new[]{"$pair","$object","$prior","$current","$copy"})
            sql=sql.Replace(name,"NULL",StringComparison.Ordinal);
        return sql.Replace("$limit","-1",StringComparison.Ordinal);
    }
    private static string EssentialLegacyCopyProjection(bool predecessor)
    {
        var id=predecessor?"prior_point_id":"current_point_id";
        var op=predecessor?"NULL":"p.copy_operation_id";
        var alias=predecessor?"NULL AS copy_operation_id":"l.copy_operation_id";
        var seed=$"""
            SELECT DISTINCT s.pair_id,s.object_id,s.synchronization_point_id AS point_id,
                s.authority_generation AS point_generation,s.local_object_version_id AS local_version_id,
                s.dropbox_object_version_id AS remote_version_id,s.local_membership_id,
                s.dropbox_membership_id AS remote_membership_id,
                a.root_binding_id AS local_root_id,b.root_binding_id AS remote_root_id,
                a.relative_path_key AS path_key,{alias}
            FROM _essential_legacy_copy l JOIN synchronization_point s ON s.synchronization_point_id=l.{id}
                AND s.pair_id=l.pair_id AND s.object_id=l.object_id
            JOIN membership a ON a.membership_id=s.local_membership_id
            JOIN membership b ON b.membership_id=s.dropbox_membership_id
            WHERE a.relative_path_key=b.relative_path_key
            """;
        var scalar=string.Join(",",CopySnapshotColumns.Split(',').Take(11));
        var receipt=string.Join(",",CopySnapshotColumns.Split(',').Skip(11)
            .Select((name,i)=>$"json_extract(receipt,'$[{i}]') AS {name}"));
        return $"SELECT {scalar},{receipt}"+(predecessor?"":",1 AS coverage_verified")+
            $" FROM (SELECT p.*,({CopyReceiptSelect("p",predecessor,op,true)}) AS receipt FROM ({seed}) p)";
    }
    private static string ExpandEssentialCopySql(SqliteConnection source,string sql)
    {
        if(!sql.Contains("_essential_",StringComparison.Ordinal) ||
            !(sql.Contains(EssentialCopyCurrent,StringComparison.Ordinal) ||
              sql.Contains(EssentialCopyPredecessor,StringComparison.Ordinal) ||
              sql.Contains("_essential_legacy_copy",StringComparison.Ordinal)))return sql;
        var current=EssentialLegacyCopyProjection(false);
        var prior=EssentialLegacyCopyProjection(true);
        if(!EssentialLegacySource(source))
        {
            current="SELECT "+CopySnapshotColumns+",coverage_verified FROM current_copy_completion UNION ALL "+current;
            // A legacy materialization replaces only that object's unverified old
            // predecessor cache; all other compact consumer state stays exact.
            prior="SELECT "+CopySnapshotColumns+" FROM copy_predecessor p WHERE NOT EXISTS "+
                "(SELECT 1 FROM _essential_legacy_copy l WHERE l.pair_id=p.pair_id AND l.object_id=p.object_id) UNION ALL "+prior;
        }
        return "WITH _essential_legacy_copy AS MATERIALIZED ("+EssentialLegacyCandidates(source)+"),"+
            EssentialCopyCurrent+" AS MATERIALIZED ("+current+"),"+
            EssentialCopyPredecessor+" AS MATERIALIZED ("+prior+") "+sql;
    }
    private static async Task RequireEssentialLegacyCopyAsync(SqliteConnection source,SqliteTransaction snapshot,CancellationToken token)
    {
        foreach(var predicate in new[]{
            "SELECT pair_id,object_id FROM _essential_legacy_copy GROUP BY pair_id,object_id "+
                "HAVING count(*)>4096 OR count(DISTINCT current_point_id)<>1 OR count(DISTINCT copy_operation_id)<>1",
            "SELECT pair_id,object_id,old_membership_id FROM _essential_legacy_copy "+
                "GROUP BY pair_id,object_id,old_membership_id HAVING count(*)<>1"})
            if(await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,
                "SELECT count(*) FROM ("+predicate+");"),token)!=0)
                throw new InvalidDataException("Legacy copy candidates exceed the existing bounded materializer or have ambiguous current/predecessor identity.");
    }
    private static Task<long> EssentialPhysicalCopyRowsAsync(SqliteConnection source,SqliteTransaction snapshot,string table,CancellationToken token)
    {
        if(!EssentialSourceHasTable(source,table))return Task.FromResult(0L);
        var sql="SELECT count(*) FROM "+table;
        if(table=="copy_predecessor")
            sql+=" p WHERE NOT EXISTS(SELECT 1 FROM _essential_legacy_copy l WHERE l.pair_id=p.pair_id AND l.object_id=p.object_id)";
        return ScalarLongAsync(source,snapshot,EssentialSourceSql(source,sql),token);
    }
}
