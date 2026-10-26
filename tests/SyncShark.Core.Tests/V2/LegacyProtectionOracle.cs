using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

// Test-only ground truth copied from 940b56be SqliteStateStore.Protection.cs.
// Keep its query construction independent of the optimized product SQL. Only connection,
// root and gap access are adapted here; callers use a quiescent synthetic fixture.
internal static class LegacyProtectionOracle
{
    internal static IReadOnlyList<ProtectionMatch> Read(SqliteConnection connection,
        string rootId, ItemFlags flags, IReadOnlyList<string> paths)
    {
        if (paths.Count > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
        var result = new ProtectionMatch[paths.Count];
        var root = StoreSchema.Scalar(connection, null,
            "SELECT root_no FROM root WHERE root_id=$id;", ("$id", rootId)) as long?;
        if (root is null || paths.Count == 0) return result;
        var queries = new List<object>();
        var ancestors = new List<object>();
        for (var n = 0; n < paths.Count; n++)
        {
            var key = RowCodec.Key(paths[n]);
            var split = key.Length == 0 ? (Parent: "", Name: "") : RowCodec.Split(key);
            queries.Add(new { n, key, dir = split.Parent, name = split.Name,
                lo = key.Length == 0 ? "" : key + "/", hi = key.Length == 0 ? "￿" : key + "0" });
            for (var current = paths[n]; current.Length > 0;)
            {
                var parts = RowCodec.Split(RowCodec.Key(current));
                ancestors.Add(new { n, dir = parts.Parent, name = parts.Name });
                var slash = current.LastIndexOf('/');
                current = slash < 0 ? "" : current[..slash];
            }
        }
        // CROSS JOIN fixes candidate-first loop order: otherwise SQLite can scan every held row
        // once per candidate. dir_key and the WITHOUT ROWID file key support bounded indexed probes.
        // Root containment is a separate EXISTS (no Unicode sentinel and no materialized root subtree).
        // Preserve the old HasGapProtection gate, not the new reader's looser gap-row test.
        (bool DiscoveryDone, string AfterObject)? gap = null;
        if ((flags & ItemFlags.NeedsRehash) != 0)
        {
            using var gapCommand = StoreSchema.Command(connection, null,
                "SELECT discovery_done,after_object FROM local_gap WHERE root_no=$r AND (discovery_done=0 OR EXISTS(" +
                "SELECT 1 FROM local_gap_item i JOIN file_state f ON f.root_no=i.root_no AND f.object_id=i.object_id WHERE i.root_no=$r)) LIMIT 1;");
            gapCommand.Parameters.AddWithValue("$r", root.Value);
            using var gapReader = gapCommand.ExecuteReader();
            if (gapReader.Read()) gap = (gapReader.GetInt64(0) != 0, gapReader.GetString(1));
        }
        var sql = """
            WITH q AS (SELECT json_extract(value,'$.n') n,json_extract(value,'$.key') k,
              json_extract(value,'$.dir') d,json_extract(value,'$.name') name,
              json_extract(value,'$.lo') lo,json_extract(value,'$.hi') hi FROM json_each($q)),
            a AS (SELECT json_extract(value,'$.n') n,json_extract(value,'$.dir') d,
              json_extract(value,'$.name') name FROM json_each($a)),
            matches AS (
              SELECT a.n n,1 kind FROM a CROSS JOIN dir d INDEXED BY dir_key ON d.path_key=a.d
                CROSS JOIN file_state f NOT INDEXED ON f.dir_no=d.dir_no AND f.name_key=a.name
                WHERE f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0
              UNION ALL
              SELECT q.n,2 FROM q CROSS JOIN dir d INDEXED BY dir_key ON d.path_key=q.d
                CROSS JOIN file_state f NOT INDEXED ON f.dir_no=d.dir_no AND f.name_key=q.name
                WHERE f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0
              UNION ALL
              SELECT q.n,2 FROM q WHERE q.k!='' AND EXISTS (
                SELECT 1 FROM dir d INDEXED BY dir_key
                CROSS JOIN file_state f NOT INDEXED ON f.dir_no=d.dir_no
                WHERE (d.path_key=q.k OR (d.path_key>=q.lo AND d.path_key<q.hi))
                  AND f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0)
              UNION ALL
              SELECT q.n,2 FROM q WHERE q.k='' AND EXISTS (
                SELECT 1 FROM file_state f INDEXED BY fs_flagged
                WHERE f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0))
            SELECT n,MAX(kind=1),MAX(kind=2) FROM matches GROUP BY n;
            """.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (gap is not null)
            sql = sql.Replace("SELECT 1 FROM file_state f INDEXED BY fs_flagged\n                WHERE f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0",
                "SELECT 1 WHERE EXISTS(SELECT 1 FROM file_state rf INDEXED BY fs_flagged WHERE rf.root_no=$r AND rf.flags!=0 AND (rf.flags & $flags)!=0) " +
                "OR ($done=0 AND EXISTS(SELECT 1 FROM file_state uf INDEXED BY fs_obj WHERE uf.root_no=$r AND uf.object_id>$after AND uf.kind=0)) " +
                "OR EXISTS(SELECT 1 FROM local_gap_item gi CROSS JOIN file_state gf INDEXED BY fs_obj " +
                "WHERE gi.root_no=$r AND gf.root_no=gi.root_no AND gf.object_id=gi.object_id)", StringComparison.Ordinal)
                .Replace("f.flags!=0 AND (f.flags & $flags)!=0",
                "((f.flags!=0 AND (f.flags & $flags)!=0) OR (f.kind=0 AND " +
                "(($done=0 AND f.object_id>$after) OR EXISTS(SELECT 1 FROM local_gap_item gi " +
                "WHERE gi.root_no=f.root_no AND gi.object_id=f.object_id))))", StringComparison.Ordinal)
                .Replace("file_state f INDEXED BY fs_flagged", "file_state f INDEXED BY fs_obj", StringComparison.Ordinal);
        using var command = StoreSchema.Command(connection, null, sql);
        if (gap is not null)
        {
            command.Parameters.AddWithValue("$done", gap.Value.DiscoveryDone ? 1 : 0);
            command.Parameters.AddWithValue("$after", gap.Value.AfterObject);
        }
        command.Parameters.AddWithValue("$r", root.Value); command.Parameters.AddWithValue("$flags", (long)flags);
        command.Parameters.AddWithValue("$q", System.Text.Json.JsonSerializer.Serialize(queries));
        command.Parameters.AddWithValue("$a", System.Text.Json.JsonSerializer.Serialize(ancestors));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result[reader.GetInt32(0)] = new(reader.GetInt64(1) != 0, reader.GetInt64(2) != 0);
        return result;
    }
}
