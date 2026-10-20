namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private static string StorageProtectionSql(string sql, ItemFlags flags) => flags == ItemFlags.OwnedPointer
        ? sql.Replace("INDEXED BY fs_flagged", "INDEXED BY fs_owned").Replace("(f.flags & ~512)!=0", "(f.flags & 512) != 0")
        : (flags & ItemFlags.OwnedPointer) != 0
            ? sql.Replace("INDEXED BY fs_flagged", "INDEXED BY fs_obj").Replace("(f.flags & ~512)!=0", "f.flags!=0") : sql;

    internal long ProtectionBatchQueries { get; private set; }

    public IReadOnlyList<ProtectionMatch> ProtectionBatch(string rootId, ItemFlags flags, IReadOnlyList<string> paths) => InStore(() =>
    {
        if (paths.Count > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
        var result = new ProtectionMatch[paths.Count];
        var root = RootNo(rootId);
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
        var gap = (flags & ItemFlags.NeedsRehash) != 0 && HasGapProtection(root) ? ReadGap(root) : null;
        var sql = """
            WITH q AS (SELECT json_extract(value,'$.n') n,json_extract(value,'$.key') k,
              json_extract(value,'$.dir') d,json_extract(value,'$.name') name,
              json_extract(value,'$.lo') lo,json_extract(value,'$.hi') hi FROM json_each($q)),
            a AS (SELECT json_extract(value,'$.n') n,json_extract(value,'$.dir') d,
              json_extract(value,'$.name') name FROM json_each($a)),
            matches AS (
              SELECT a.n n,1 kind FROM a CROSS JOIN dir d INDEXED BY dir_key ON d.path_key=a.d
                CROSS JOIN file_state f NOT INDEXED ON f.dir_no=d.dir_no AND f.name_key=a.name
                WHERE f.root_no=$r AND (f.flags & ~512)!=0 AND (f.flags & $flags)!=0
              UNION ALL
              SELECT q.n,2 FROM q CROSS JOIN dir d INDEXED BY dir_key ON d.path_key=q.d
                CROSS JOIN file_state f NOT INDEXED ON f.dir_no=d.dir_no AND f.name_key=q.name
                WHERE f.root_no=$r AND (f.flags & ~512)!=0 AND (f.flags & $flags)!=0
              UNION ALL
              SELECT q.n,2 FROM q WHERE q.k!='' AND EXISTS (
                SELECT 1 FROM dir d INDEXED BY dir_key
                CROSS JOIN file_state f NOT INDEXED ON f.dir_no=d.dir_no
                WHERE (d.path_key=q.k OR (d.path_key>=q.lo AND d.path_key<q.hi))
                  AND f.root_no=$r AND (f.flags & ~512)!=0 AND (f.flags & $flags)!=0)
              UNION ALL
              SELECT q.n,2 FROM q WHERE q.k='' AND EXISTS (
                SELECT 1 FROM file_state f INDEXED BY fs_flagged
                WHERE f.root_no=$r AND (f.flags & ~512)!=0 AND (f.flags & $flags)!=0))
            SELECT n,MAX(kind=1),MAX(kind=2) FROM matches GROUP BY n;
            """.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (gap is not null)
            sql = sql.Replace("SELECT 1 FROM file_state f INDEXED BY fs_flagged\n                WHERE f.root_no=$r AND (f.flags & ~512)!=0 AND (f.flags & $flags)!=0",
                "SELECT 1 WHERE EXISTS(SELECT 1 FROM file_state rf INDEXED BY fs_flagged WHERE rf.root_no=$r AND (rf.flags & ~512)!=0 AND (rf.flags & $flags)!=0) " +
                "OR ($done=0 AND EXISTS(SELECT 1 FROM file_state uf INDEXED BY fs_obj WHERE uf.root_no=$r AND uf.object_id>$after AND uf.kind=0)) " +
                "OR EXISTS(SELECT 1 FROM local_gap_item gi CROSS JOIN file_state gf INDEXED BY fs_obj " +
                "WHERE gi.root_no=$r AND gf.root_no=gi.root_no AND gf.object_id=gi.object_id)", StringComparison.Ordinal)
                .Replace("(f.flags & ~512)!=0 AND (f.flags & $flags)!=0",
                "(((f.flags & ~512)!=0 AND (f.flags & $flags)!=0) OR (f.kind=0 AND " +
                "(($done=0 AND f.object_id>$after) OR EXISTS(SELECT 1 FROM local_gap_item gi " +
                "WHERE gi.root_no=f.root_no AND gi.object_id=f.object_id))))", StringComparison.Ordinal)
                .Replace("file_state f INDEXED BY fs_flagged", "file_state f INDEXED BY fs_obj", StringComparison.Ordinal);
        sql = StorageProtectionSql(sql, flags);
        using var command = StoreSchema.Command(_connection, null, sql);
        if (gap is not null)
        {
            command.Parameters.AddWithValue("$done", gap.DiscoveryDone ? 1 : 0);
            command.Parameters.AddWithValue("$after", gap.AfterObject);
        }
        command.Parameters.AddWithValue("$r", root.Value); command.Parameters.AddWithValue("$flags", (long)flags);
        command.Parameters.AddWithValue("$q", System.Text.Json.JsonSerializer.Serialize(queries));
        command.Parameters.AddWithValue("$a", System.Text.Json.JsonSerializer.Serialize(ancestors));
        ProtectionBatchQueries++;
        using var reader = command.ExecuteReader();
        while (reader.Read()) result[reader.GetInt32(0)] = new(reader.GetInt64(1) != 0, reader.GetInt64(2) != 0);
        return result;
    });

    /// <summary>One indexed streaming query builds the bounded snapshot; no ItemState decoding.</summary>
    public PathProtectionIndex? ProtectionIndex(string rootId, ItemFlags flags,
        int pathLimit = PathProtectionIndex.DefaultPathLimit,
        int characterLimit = PathProtectionIndex.DefaultCharacterLimit) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return PathProtectionIndex.TryCreate(Array.Empty<string>(), pathLimit, characterLimit);
        // Lazy discovery protects the unvisited indexed suffix without materializing a root-sized set.
        if ((flags & ItemFlags.NeedsRehash) != 0 && HasGapProtection(root)) return null;
        using var command = StoreSchema.Command(_connection, null,
            StorageProtectionSql("SELECT d.path,f.name FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND (f.flags & ~512)!=0 AND (f.flags & $flags)!=0 LIMIT $limit;", flags));
        command.Parameters.AddWithValue("$r", root.Value);
        command.Parameters.AddWithValue("$flags", (long)flags);
        command.Parameters.AddWithValue("$limit", (long)pathLimit + 1);
        using var reader = command.ExecuteReader();
        var rows = 0;
        IEnumerable<string> Paths()
        {
            while (reader.Read())
            {
                rows++;
                yield return RowCodec.Join(reader.GetString(0), reader.GetString(1));
            }
        }
        var index = PathProtectionIndex.TryCreate(Paths(), pathLimit, characterLimit);
        // Case-clashing spellings can collapse in the index; a truncated query is still overflow.
        return rows > pathLimit ? null : index;
    });
}
