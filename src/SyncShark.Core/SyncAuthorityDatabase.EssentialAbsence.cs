using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private const string EssentialClosedAbsence="SELECT both_absent_basis_json FROM tombstone WHERE tombstone_id IN ("+
        EssentialTombstones+") AND both_absent_basis_json IS NOT NULL";
    private const string EssentialAbsencePoints="SELECT json_extract(both_absent_basis_json,'$.PointId') FROM ("+EssentialClosedAbsence+")";
    private const string EssentialAbsenceScans=
        "SELECT json_extract(both_absent_basis_json,'$.LocalClosureScan') FROM ("+EssentialClosedAbsence+") UNION "+
        "SELECT json_extract(both_absent_basis_json,'$.RemoteClosureScan') FROM ("+EssentialClosedAbsence+") UNION "+
        "SELECT scan_generation_id FROM scan_generation s WHERE s.root_binding_id IN "+
        "(SELECT json_extract(both_absent_basis_json,'$.LocalRoot') FROM ("+EssentialClosedAbsence+") UNION "+
        "SELECT json_extract(both_absent_basis_json,'$.RemoteRoot') FROM ("+EssentialClosedAbsence+")) "+
        "AND s.generation IN (SELECT a.generation FROM scan_generation a WHERE a.root_binding_id=s.root_binding_id "+
        "ORDER BY a.generation DESC LIMIT 2)";

    private static string ExpandEssentialGenerationSql(SqliteConnection source,string sql)
    {
        if(!sql.Contains("__essential_successor_generation__",StringComparison.Ordinal))return sql;
        if(!EssentialHistorySelections.TryGetValue(source,out var selection))
            throw new InvalidDataException("Successor generation projection is not receipt-bound.");
        return sql.Replace("__essential_successor_generation__","'"+selection.TargetGeneration+"'",StringComparison.Ordinal);
    }

    private static async Task RequireEssentialAbsenceAsync(SqliteConnection source,SqliteTransaction snapshot,CancellationToken token)
    {
        if(EssentialLegacySource(source))return;
        ConfigureAbsencePathComparison(source);
        var generation=await ScalarStringAsync(source,snapshot,"SELECT database_generation FROM installation;",token);
        await using var q=source.CreateCommand();q.Transaction=snapshot;
        q.CommandText="SELECT tombstone_id,pair_id,object_id,absent_side,both_absent_basis_json,both_absent_closed_utc "+
            "FROM tombstone WHERE tombstone_id IN ("+EssentialTombstones+") "+
            "AND (both_absent_basis_json IS NOT NULL OR both_absent_closed_utc IS NOT NULL);";
        await using var rows=await q.ExecuteReaderAsync(token);
        while(await rows.ReadAsync(token))
        {
            if(rows.IsDBNull(4)||rows.IsDBNull(5)||!DateTimeOffset.TryParse(rows.GetString(5),out var closed))
                throw new InvalidDataException("Closed absence has no original proof and closure time.");
            var basis=JsonSerializer.Deserialize<BothAbsentBasis>(rows.GetString(4));
            if(basis is null || basis.DatabaseGeneration!=generation || basis.PairId!=rows.GetString(1) ||
                basis.ObjectId!=rows.GetString(2) || rows.GetString(3)!="both" ||
                string.IsNullOrEmpty(basis.Path) || PilotPath.NormalizeRelative(basis.Path)!=basis.Path ||
                basis.LocalRoot==basis.RemoteRoot)
                throw new InvalidDataException("Closed absence proof identity differs from its source generation or tombstone.");
            if(await ScalarLongAsync(source,snapshot,"""
                SELECT count(*) FROM synchronization_point p
                JOIN membership l ON l.membership_id=p.local_membership_id
                JOIN membership r ON r.membership_id=p.dropbox_membership_id
                WHERE p.synchronization_point_id=$point AND p.pair_id=$pair AND p.object_id=$object
                    AND p.authority_generation=$generation AND l.root_binding_id=$local AND r.root_binding_id=$remote
                    AND l.relative_path_key=$path COLLATE syncshark_absence_path
                    AND r.relative_path_key=$path COLLATE syncshark_absence_path;
                """,token,("$point",basis.PointId),("$pair",basis.PairId),("$object",basis.ObjectId),
                ("$generation",basis.AuthorityGeneration),("$local",basis.LocalRoot),("$remote",basis.RemoteRoot),("$path",basis.Path))!=1 ||
                basis.DeletionPolicyJson!=await ScalarStringAsync(source,snapshot,BothAbsentPolicySql,token,
                    ("$pair",basis.PairId),("$revision",basis.ConfigurationRevision)))
                throw new InvalidDataException("Closed absence original accepted point, roots, path or policy is unavailable.");
            foreach(var (root,scan,side) in new[]{(basis.LocalRoot,basis.LocalClosureScan,"local"),(basis.RemoteRoot,basis.RemoteClosureScan,"dropbox")})
                if(await ScalarLongAsync(source,snapshot,"""
                    SELECT count(*) FROM scan_generation g JOIN root_binding r ON r.root_binding_id=g.root_binding_id
                    WHERE g.scan_generation_id=$scan AND g.pair_id=$pair AND g.root_binding_id=$root
                      AND r.pair_id=$pair AND r.side=$side AND g.side=$side AND g.status='complete'
                      AND g.lifetime_retiring=0 AND g.completed_utc IS NOT NULL
                      AND julianday(g.completed_utc)<=julianday($closed) AND json_extract(g.scope_json,'$.scope')='complete'
                      AND NOT EXISTS(SELECT 1 FROM effective_observation o LEFT JOIN membership m ON m.membership_id=o.membership_id
                        WHERE o.scan_generation_id=g.scan_generation_id AND
                          (m.membership_id IS NULL OR m.object_id=$object OR m.relative_path_key=$path COLLATE syncshark_absence_path
                            OR substr(m.relative_path_key,1,length($path)+1)=($path||'/') COLLATE syncshark_absence_path));
                    """,token,("$scan",scan),("$pair",basis.PairId),("$root",root),("$side",side),("$closed",Utc(closed)),
                    ("$object",basis.ObjectId),("$path",basis.Path))!=1)
                    throw new InvalidDataException("Closed absence requires its complete original absence observations.");
        }
    }
}
