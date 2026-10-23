using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private const string EssentialExplorerColumns="pair_id,relative_path_key,object_kind,local_present,"+
        "dropbox_present,online_only,local_stable_identity,dropbox_stable_identity,included,"+
        "local_scan_generation_id,dropbox_scan_generation_id,subtree_folder_count,subtree_included_folder_count,"+
        "subtree_local_items,subtree_online_only_items,subtree_mixed_items,subtree_excluded_items,updated_utc";

    // Excluded paths can retain last-known local presence from a cut no longer needed by
    // authority. Preserve that display knowledge and timestamp, but never relabel it as
    // observed by a newer cut. Nullable display links are retained only for selected cuts.
    // Recompute aggregates from scalar rows; do not inherit stale cached folder counts.
    private static string BuildEssentialExplorerSql()
    {
        var sums=new[]{
            ("subtree_folder_count","p.object_kind='directory'",1),
            ("subtree_included_folder_count","p.object_kind='directory' AND p.included=1",1),
            ("subtree_local_items","p.local_present=1 AND p.dropbox_present=0 AND p.online_only=0",0),
            ("subtree_online_only_items","p.online_only=1",0),
            ("subtree_mixed_items","p.local_present=1 AND p.dropbox_present=1 AND p.online_only=0",0),
            ("subtree_excluded_items","p.included=0",0)
        };
        var aggregates=sums.Select(x=>
            "CASE WHEN i.object_kind='directory' THEN (SELECT count(*) FROM sync_path_index p "+
            "WHERE p.pair_id=i.pair_id AND (p.relative_path_key=i.relative_path_key OR "+
            "(p.relative_path_key>=i.relative_path_key||'/' COLLATE NOCASE AND "+
            "p.relative_path_key<i.relative_path_key||'0' COLLATE NOCASE)) AND ("+x.Item2+")) "+
            "ELSE "+x.Item3+" END AS "+x.Item1);
        return "SELECT i.pair_id,i.relative_path_key,i.object_kind,i.local_present,i.dropbox_present,"+
            "i.online_only,i.local_stable_identity,i.dropbox_stable_identity,i.included,"+
            "CASE WHEN i.local_scan_generation_id IN ("+EssentialScans+") THEN i.local_scan_generation_id END AS local_scan_generation_id,"+
            "CASE WHEN i.dropbox_scan_generation_id IN ("+EssentialScans+") THEN i.dropbox_scan_generation_id END AS dropbox_scan_generation_id,"+
            string.Join(",",aggregates)+",i.updated_utc FROM sync_path_index i";
    }

    private static readonly EssentialConfigurationTable[] EssentialExplorerTables=[
        new("sync_path_index",EssentialExplorerColumns,"pair_id,relative_path_key",BuildEssentialExplorerSql())
    ];

    private static async Task RequireEssentialExplorerAsync(SqliteConnection source,SqliteTransaction snapshot,
        CancellationToken token)
    {
        if(await ScalarLongAsync(source,snapshot,"""
            SELECT count(*) FROM sync_path_index i
            LEFT JOIN sync_pair p ON p.pair_id=i.pair_id
            LEFT JOIN scan_generation l ON l.scan_generation_id=i.local_scan_generation_id
            LEFT JOIN scan_generation r ON r.scan_generation_id=i.dropbox_scan_generation_id
            WHERE p.pair_id IS NULL OR
                (i.local_scan_generation_id IS NOT NULL AND
                 (l.scan_generation_id IS NULL OR l.pair_id IS NOT i.pair_id OR l.side<>'local' OR l.status<>'complete')) OR
                (i.dropbox_scan_generation_id IS NOT NULL AND
                 (r.scan_generation_id IS NULL OR r.pair_id IS NOT i.pair_id OR r.side<>'dropbox' OR r.status<>'complete'));
            """,token)!=0)
            throw new InvalidDataException("Explorer display references require their own pair and complete original side cuts.");
        await using var q=source.CreateCommand();q.Transaction=snapshot;
        q.CommandText="SELECT relative_path_key FROM sync_path_index ORDER BY pair_id,relative_path_key;";
        await using var rows=await q.ExecuteReaderAsync(token);
        while(await rows.ReadAsync(token))
        {
            var path=rows.GetString(0);
            if(path.Length==0 || !PilotPath.IsWindowsTransferPath(path) || PilotPath.NormalizeRelative(path)!=path)
                throw new InvalidDataException("Explorer display path is not a normalized supported path.");
        }
    }
}
