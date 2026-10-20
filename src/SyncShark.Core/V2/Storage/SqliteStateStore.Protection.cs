namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
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
        using var command = StoreSchema.Command(_connection, null, """
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
                SELECT 1 FROM file_state INDEXED BY fs_flagged
                WHERE root_no=$r AND flags!=0 AND (flags & $flags)!=0))
            SELECT n,MAX(kind=1),MAX(kind=2) FROM matches GROUP BY n;
            """);
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
        using var command = StoreSchema.Command(_connection, null,
            "SELECT d.path,f.name FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0 LIMIT $limit;");
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
