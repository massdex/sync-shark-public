using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Reads and decodes live file_state rows and deletion tombstones.</summary>
internal sealed class FileStateReader
{
    private const string Columns =
        "f.dir_no, d.path, f.name, f.kind, f.object_id, f.version_id, f.hash, f.size, f.mtime, f.flags, f.identity_json, f.name_key";

    // Every live row at one path ignoring case the folder spellings through dir_key, then the
    // primary key (root_no, dir_no, name_key, ...) down to the leaf key.
    private const string AtKeySql =
        $"SELECT {Columns} FROM file_state f JOIN dir d ON d.dir_no = f.dir_no " +
        "WHERE f.root_no=$r AND f.dir_no IN (SELECT dir_no FROM dir WHERE path_key = $pk) AND f.name_key = $nk;";

    // Driven from the temporary prefix tables (CROSS JOIN fixes the loop order): one primary-key range per folder,
    // one primary-key point lookup per prefix item; never a scan of the root.
    private const string LiveUnderSql =
        $"SELECT {Columns} FROM tmp_dirs t CROSS JOIN file_state f JOIN dir d ON d.dir_no = f.dir_no " +
        "WHERE f.root_no=$r AND f.dir_no = t.dir_no " +
        $"UNION ALL SELECT {Columns} FROM tmp_items ti CROSS JOIN file_state f JOIN dir d ON d.dir_no = f.dir_no " +
        "WHERE f.root_no=$r AND f.dir_no = ti.dir_no AND f.name_key = ti.name_key ORDER BY 2, 3;";

    // The explicit nonzero predicate matches the partial index; the bit mask alone cannot imply it to SQLite.
    private const string LiveFlaggedSql =
        $"SELECT {Columns} FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no = f.dir_no " +
        "WHERE f.root_no=$r AND f.flags != 0 AND (f.flags & $f) != 0 ORDER BY d.path, f.name;";

    private const string FlaggedPredicate = "f.root_no=$r AND f.flags != 0 AND (f.flags & $f) != 0";
    private const string LiveFlaggedCountSql =
        "SELECT COUNT(*) FROM file_state f INDEXED BY fs_flagged WHERE " + FlaggedPredicate + ";";
    // No sort: examples may be in index order, so LIMIT bounds decoded rows and avoids a root-sized sort.
    private const string LiveFlaggedExamplesSql =
        $"SELECT {Columns} FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no=f.dir_no " +
        "WHERE " + FlaggedPredicate + " LIMIT $limit;";
    private const string LiveFlaggedFoldersSql =
        $"SELECT {Columns} FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no=f.dir_no " +
        "WHERE " + FlaggedPredicate + " AND f.kind=$kind;";

    internal bool StorageEnabled { get; set; }

    private string FlaggedSql(string sql, ItemFlags flags) => StorageEnabled && flags == ItemFlags.OwnedPointer
        ? sql.Replace("INDEXED BY fs_flagged", "INDEXED BY fs_owned").Replace("f.flags != 0", "(f.flags & 1024) != 0")
        : flags == ItemFlags.ProviderNameRefused
        ? sql.Replace("INDEXED BY fs_flagged", "INDEXED BY fs_refused")
            .Replace("f.flags != 0 AND (f.flags & $f) != 0", "(f.flags & 64) != 0 AND (f.flags & $f) != 0")
        : flags == ItemFlags.Quarantined
            ? sql.Replace("INDEXED BY fs_flagged", "INDEXED BY fs_quarantine")
                .Replace("f.flags != 0 AND (f.flags & $f) != 0", "(f.flags & 128) != 0 AND (f.flags & $f) != 0")
            : StorageEnabled && (flags & ItemFlags.OwnedPointer) == 0
                ? sql.Replace("INDEXED BY fs_flagged", "INDEXED BY fs_nonpointer").Replace("f.flags != 0", "(f.flags & ~1024) != 0")
                : sql;

    private static string BatchedPathsSql(string values) =>
        $"WITH requested(i,pk,nk) AS (VALUES {values}) SELECT {Columns}, q.i AS requested_index " +
        "FROM requested q CROSS JOIN file_state f JOIN dir d ON d.dir_no=f.dir_no " +
        "WHERE f.root_no=$r AND f.dir_no IN (SELECT dir_no FROM dir WHERE path_key=q.pk) AND f.name_key=q.nk;";

    private readonly SqliteConnection _connection;
    private readonly DirTable _dir;

    public FileStateReader(SqliteConnection connection, DirTable dir)
    {
        _connection = connection;
        _dir = dir;
    }

    internal List<(long No, string Path)> QuarantineDirectories(string scope, string after, long afterNo)
    {
        var key = RowCodec.Key(scope);
        using var command = StoreSchema.Command(_connection, null,
            "SELECT dir_no,path FROM dir WHERE (path_key,dir_no)>($after,$afterNo) " +
            (scope.Length == 0 ? "" : "AND path_key >= $scope AND path_key < $hi AND (path_key=$scope OR path_key >= $lo) ") +
            "ORDER BY path_key,dir_no LIMIT 512;");
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$afterNo", afterNo);
        command.Parameters.AddWithValue("$scope", key);
        command.Parameters.AddWithValue("$lo", key + "/");
        command.Parameters.AddWithValue("$hi", key + "0");
        var page = new List<(long, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) page.Add((reader.GetInt64(0), reader.GetString(1)));
        return page;
    }

    internal List<Entry> QuarantineBaselinePage(long root, long pair, Side side, Entry? after, long? directory = null)
    {
        var prefix = side == Side.A ? "a" : "b";
        using var command = StoreSchema.Command(_connection, null,
            $"SELECT {Columns} FROM file_state f JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$r " +
            (directory is null ? "" : "AND f.dir_no=$onlydir ") +
            "AND (f.dir_no,f.name_key,f.name)>($dir,$key,$name) " +
            $"AND EXISTS (SELECT 1 FROM sync_point s WHERE s.pair_no=$p AND s.{prefix}_object_id=f.object_id) " +
            "ORDER BY f.dir_no,f.name_key,f.name LIMIT 512;");
        command.Parameters.AddWithValue("$r", root);
        command.Parameters.AddWithValue("$p", pair);
        if (directory is not null) command.Parameters.AddWithValue("$onlydir", directory.Value);
        command.Parameters.AddWithValue("$dir", after?.DirNo ?? -1);
        command.Parameters.AddWithValue("$key", after is { } row ? RowCodec.Key(row.Name) : "");
        command.Parameters.AddWithValue("$name", after?.Name ?? "");
        var page = new List<Entry>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) page.Add(Decode(reader));
        return page;
    }

    /// <summary>All live rows for a root in key order, or just those at the given paths (matched ignoring case).</summary>
    public IReadOnlyList<ItemState> Live(long rootNo, IEnumerable<string>? paths)
    {
        if (paths is null)
            return QueryItems($"SELECT {Columns} FROM file_state f JOIN dir d ON d.dir_no = f.dir_no " +
                "WHERE f.root_no=$r ORDER BY d.path, f.name;", [("$r", rootNo)]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Entry>();
        // Bounded indexed batches, retaining Lookup's exact-spelling preference and first-request semantics.
        foreach (var batch in paths.Where(path => path.Length != 0 && seen.Add(path)).Chunk(128))
        {
            var values = string.Join(",", batch.Select((_, i) => $"({i},$p{i},$n{i})"));
            using var command = StoreSchema.Command(_connection, null,
                BatchedPathsSql(values));
            command.Parameters.AddWithValue("$r", rootNo);
            for (var i = 0; i < batch.Length; i++)
            {
                var (parent, name) = RowCodec.Split(batch[i]);
                command.Parameters.AddWithValue($"$p{i}", RowCodec.Key(parent));
                command.Parameters.AddWithValue($"$n{i}", RowCodec.Key(name));
            }
            var selected = new Entry?[batch.Length];
            using var reader = command.ExecuteReader();
            var requestedIndex = reader.GetOrdinal("requested_index");
            while (reader.Read())
            {
                var entry = Decode(reader);
                var i = reader.GetInt32(requestedIndex);
                if (selected[i] is null || string.Equals(entry.Item.Path, batch[i], StringComparison.Ordinal))
                    selected[i] = entry;
            }
            foreach (var entry in selected)
                if (entry is { } found) result.Add(found);
        }
        result.Sort(Entry.CompareByKey);
        return result.Select(entry => entry.Item).ToArray();
    }

    /// <summary>The live row at a path, matching the folder and the leaf ignoring case, with its stored spelling.</summary>
    public Entry? Lookup(long rootNo, string path)
    {
        var rows = AtKey(rootNo, path, null);
        if (rows.Count <= 1) return rows.Count == 0 ? null : rows[0];
        return rows.FirstOrDefault(row => string.Equals(row.Item.Path, path, StringComparison.Ordinal), rows[0]);
    }

    /// <summary>Every live row at a path ignoring case; more than one only when spellings differ in one folder.</summary>
    public List<Entry> AtKey(long rootNo, string path, SqliteTransaction? transaction)
    {
        var (parent, name) = RowCodec.Split(path);
        using var command = StoreSchema.Command(_connection, transaction, AtKeySql);
        command.Parameters.AddWithValue("$r", rootNo);
        command.Parameters.AddWithValue("$pk", RowCodec.Key(parent));
        command.Parameters.AddWithValue("$nk", RowCodec.Key(name));
        var rows = new List<Entry>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Decode(reader));
        return rows;
    }

    /// <summary>Live rows at each prefix or under it, distinct and in key order.</summary>
    public IReadOnlyList<ItemState> LiveUnder(long rootNo, IReadOnlyList<string> prefixes)
    {
        if (prefixes.Count == 0) return Array.Empty<ItemState>();
        _dir.PopulatePrefixes(null, prefixes);
        return QueryItems(LiveUnderSql, [("$r", rootNo)], distinctPaths: true);
    }

    /// <summary>Only one directory's children, using dir.path_key and file_state's root/directory key.</summary>
    public IReadOnlyList<ItemState> LiveChildren(long rootNo, string directory) =>
        QueryItems($"SELECT {Columns} FROM file_state f JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND f.dir_no IN (SELECT dir_no FROM dir WHERE path_key=$pk);",
            [("$r", rootNo), ("$pk", RowCodec.Key(directory))]);

    public IReadOnlyList<ItemState> LiveChildrenPage(long rootNo, string directory, int offset, int limit) =>
        QueryItems($"SELECT {Columns} FROM file_state f JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND f.dir_no IN (SELECT dir_no FROM dir WHERE path_key=$pk) " +
            "ORDER BY f.dir_no,f.name_key,f.name LIMIT $limit OFFSET $offset;",
            [("$r", rootNo), ("$pk", RowCodec.Key(directory)), ("$limit", limit), ("$offset", offset)]);

    /// <summary>Bounded name-filtered folder paths under one prefix; the LIMIT is applied before any row is decoded.</summary>
    public IReadOnlyList<string> RebuildableFoldersUnder(long rootNo, string prefix, IReadOnlyList<string> names,
        int offset, int limit)
    {
        var prefixKey = RowCodec.Key(prefix);
        var nameFilter = names.Count == 0 ? "1" :
            "f.name_key IN (" + string.Join(",", names.Select((_, i) => "$n" + i)) + ")";
        var parameters = new List<(string, object?)>
        {
            ("$r", rootNo), ("$kind", RowCodec.KindCode(ItemKind.Folder)),
            ("$empty", prefix.Length == 0 ? 1 : 0), ("$prefix", prefixKey), ("$plen", prefixKey.Length),
            ("$limit", limit), ("$offset", offset)
        };
        for (var i = 0; i < names.Count; i++) parameters.Add(("$n" + i, RowCodec.Key(names[i])));
        var rows = new List<string>(limit);
        using var command = StoreSchema.Command(_connection, null,
            "SELECT d.path, f.name FROM file_state f JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND f.kind=$kind AND " + nameFilter + " AND ($empty=1 " +
            "OR d.path_key=$prefix " +
            "OR (length(d.path_key)>$plen AND substr(d.path_key,1,$plen)=$prefix AND substr(d.path_key,$plen+1,1)='/') " +
            "OR ((CASE WHEN d.path_key='' THEN '' ELSE d.path_key || '/' END) || f.name_key)=$prefix) " +
            "ORDER BY d.path,f.name LIMIT $limit OFFSET $offset;");
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(RowCodec.Join(reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    public IReadOnlyList<ItemState> LiveFlaggedPage(long rootNo, ItemFlags flags, ItemState? after, int limit,
        long? afterDirNo = null, SqliteTransaction? transaction = null)
    {
        var (parent, name) = after is null ? ("", "") : RowCodec.Split(after.Path);
        // The cursor carries its folder number, so a folder deleted since the previous page still seeks from its key
        // instead of silently ending the side. An older cursor without it falls back to the path lookup.
        var dir = after is null ? -1L : afterDirNo ?? (long?)StoreSchema.Scalar(_connection, transaction,
            "SELECT dir_no FROM dir WHERE path=$path;", ("$path", parent)) ?? -1L;
        if (after is not null && dir < 0) return Array.Empty<ItemState>();
        // fs_refused is ordered by root, kind, then the WITHOUT ROWID primary key. Seek rather
        // than OFFSET: enumerating a million refusals must not revisit every earlier page.
        var sql = $"SELECT {Columns} FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE " + FlaggedPredicate + " AND (f.kind,f.dir_no,f.name_key,f.name)>($kind,$dir,$key,$name) " +
            "ORDER BY f.kind,f.dir_no,f.name_key,f.name LIMIT $limit;";
        return QueryItems(FlaggedSql(sql, flags), [("$r", rootNo), ("$f", (long)flags), ("$limit", limit),
            ("$kind", after is null ? -1L : RowCodec.KindCode(after.Kind)), ("$dir", dir),
            ("$key", RowCodec.Key(name)), ("$name", name)], transaction: transaction);
    }

    /// <summary>Live rows carrying any requested flags, scanning only the root's indexed nonzero-flag rows.</summary>
    public IReadOnlyList<ItemState> LiveFlagged(long rootNo, ItemFlags flags) =>
        QueryItems(FlaggedSql(LiveFlaggedSql, flags), [("$r", rootNo), ("$f", (long)flags)]);

    /// <summary>Filters flagged folders in SQLite, without materializing flagged files.</summary>
    public IReadOnlyList<ItemState> LiveFlaggedFolders(long rootNo, ItemFlags flags) =>
        QueryItems(FlaggedSql(LiveFlaggedFoldersSql, flags),
            [("$r", rootNo), ("$f", (long)flags), ("$kind", RowCodec.KindCode(ItemKind.Folder))]);

    /// <summary>Counts indexed flagged rows and decodes only the bounded examples.</summary>
    public (long Count, IReadOnlyList<ItemState> Examples) LiveFlaggedSummary(long rootNo, ItemFlags flags, int limit)
    {
        if (flags == ItemFlags.None) return (0, Array.Empty<ItemState>());
        var count = (long)StoreSchema.Scalar(_connection, null, FlaggedSql(LiveFlaggedCountSql, flags),
            ("$r", rootNo), ("$f", (long)flags))!;
        IReadOnlyList<ItemState> examples = count == 0 || limit <= 0 ? Array.Empty<ItemState>() :
            QueryItems(FlaggedSql(LiveFlaggedExamplesSql, flags), [("$r", rootNo), ("$f", (long)flags), ("$limit", limit)]);
        return (count, examples);
    }

    // Pinned to the object index: SQLite otherwise plans a root-wide primary-key range for one object,
    // which the per-change and per-subtree-file callers multiply by the root size.
    internal const string ByObjectSql =
        $"SELECT {Columns} FROM file_state f INDEXED BY fs_obj JOIN dir d ON d.dir_no = f.dir_no " +
        "WHERE f.root_no=$r AND f.object_id=$o LIMIT 1;";

    /// <summary>The live row for one object id, or null.</summary>
    public ItemState? ByObject(long rootNo, string objectId) => ByObject(rootNo, objectId, null);

    public ItemState? ByObject(long rootNo, string objectId, SqliteTransaction? transaction)
    {
        var rows = Query(ByObjectSql, [("$r", rootNo), ("$o", objectId)], transaction);
        return rows.Count == 0 ? null : rows[0].Item;
    }

    internal IReadOnlyList<string> StorageFlagPlans(ItemFlags flags) =>
        new[] { LiveFlaggedSql, LiveFlaggedCountSql, LiveFlaggedExamplesSql, LiveFlaggedFoldersSql }
            .Select(sql => string.Join(" ", StoreSchema.QueryPlan(_connection, "flags", FlaggedSql(sql, flags), "$r", "$f", "$limit", "$kind"))).ToArray();

    /// <summary>The query plans behind indexed Live(paths), LiveUnder and LiveFlagged reads.</summary>
    public IReadOnlyList<string> Plans() =>
        StoreSchema.QueryPlan(_connection, "Live", AtKeySql, "$r", "$pk", "$nk")
            .Concat(StoreSchema.QueryPlan(_connection, "LiveBatch", BatchedPathsSql("(0,$p0,$n0),(1,$p1,$n1)"),
                "$r", "$p0", "$n0", "$p1", "$n1"))
            .Concat(StoreSchema.QueryPlan(_connection, "LiveUnder", LiveUnderSql, "$r"))
            .Concat(StoreSchema.QueryPlan(_connection, "LiveFlagged", LiveFlaggedSql, "$r", "$f"))
            .Concat(StoreSchema.QueryPlan(_connection, "LiveFlaggedCount", FlaggedSql(LiveFlaggedCountSql, ItemFlags.ProviderNameRefused), "$r", "$f"))
            .Concat(StoreSchema.QueryPlan(_connection, "LiveFlaggedExamples", FlaggedSql(LiveFlaggedExamplesSql, ItemFlags.ProviderNameRefused), "$r", "$f", "$limit"))
            .Concat(StoreSchema.QueryPlan(_connection, "LiveFlaggedFolders", FlaggedSql(LiveFlaggedFoldersSql, ItemFlags.ProviderNameRefused), "$r", "$f", "$kind")).ToArray();

    /// <summary>The recorded deletion tombstones for a root, in key order.</summary>
    public IReadOnlyList<(string Path, string ObjectId, DateTimeOffset DeletedAt)> Tombstones(long rootNo)
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT d.path, t.name, t.object_id, t.deleted_at FROM tombstone t JOIN dir d ON d.dir_no = t.dir_no " +
            "WHERE t.root_no=$r ORDER BY d.path, t.name, t.object_id;");
        command.Parameters.AddWithValue("$r", rootNo);
        var result = new List<(string, string, DateTimeOffset)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add((RowCodec.Join(reader.GetString(0), reader.GetString(1)), reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
        return result;
    }


    /// <summary>Every live row for a root in key order.</summary>
    public List<Entry> ReadLive(long rootNo) =>
        Query($"SELECT {Columns} FROM file_state f JOIN dir d ON d.dir_no = f.dir_no " +
            "WHERE f.root_no=$r ORDER BY d.path, f.name;", [("$r", rootNo)]);

    internal const string SnapshotStreamSql =
        $"SELECT {Columns} FROM file_state f JOIN dir d ON d.dir_no=f.dir_no " +
        "WHERE f.root_no=$r ORDER BY f.dir_no,f.name_key,f.name;";

    internal IEnumerable<Entry> SnapshotEntries(long rootNo, Func<SqliteTransaction> transaction) =>
        SnapshotRows.Read(_connection, transaction(), SnapshotStreamSql, Decode, rootNo);

    /// <summary>The keys of live rows carrying one object id (its old path after a move).</summary>
    public List<(long DirNo, string Name, string Path)> ReadObject(long rootNo, string objectId, SqliteTransaction? transaction)
    {
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT f.dir_no, f.name, d.path FROM file_state f JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$r AND f.object_id=$o;");
        command.Parameters.AddWithValue("$r", rootNo);
        command.Parameters.AddWithValue("$o", objectId);
        var rows = new List<(long, string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1), RowCodec.Join(reader.GetString(2), reader.GetString(1))));
        return rows;
    }

    /// <summary>Every live row in one folder.</summary>
    public List<(string Name, string ObjectId)> ReadObjectIds(long rootNo, long dirNo, SqliteTransaction? transaction)
    {
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT name, object_id FROM file_state WHERE root_no=$r AND dir_no=$d;");
        command.Parameters.AddWithValue("$r", rootNo);
        command.Parameters.AddWithValue("$d", dirNo);
        var rows = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    /// <summary>The object id of the live row at one key, or null.</summary>
    public string? ReadObjectId(long rootNo, long dirNo, string name, SqliteTransaction? transaction) =>
        StoreSchema.Scalar(_connection, transaction,
            "SELECT object_id FROM file_state WHERE root_no=$r AND dir_no=$d AND name_key=$k AND name=$n;",
            ("$r", rootNo), ("$d", dirNo), ("$k", RowCodec.Key(name)), ("$n", name)) as string;

    private List<Entry> Query(string sql, (string Name, object? Value)[] arguments,
        SqliteTransaction? transaction = null)
    {
        using var command = StoreSchema.Command(_connection, transaction, sql);
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var rows = new List<Entry>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Decode(reader));
        return rows;
    }

    // Live callers need only item states, not a second root-sized buffer of keys and states.
    private List<ItemState> QueryItems(string sql, (string Name, object? Value)[] arguments,
        bool distinctPaths = false, SqliteTransaction? transaction = null)
    {
        using var command = StoreSchema.Command(_connection, transaction, sql);
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        HashSet<string>? seen = distinctPaths ? new(StringComparer.Ordinal) : null;
        var rows = new List<ItemState>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = DecodeItem(reader);
            if (seen is null || seen.Add(item.Path)) rows.Add(item);
        }
        return rows;
    }

    private static Entry Decode(SqliteDataReader reader) => Entry.Of(
        reader.GetString(1), reader.GetString(2), DecodeItem(reader), reader.GetInt64(0)) with { NameKey = reader.GetString(11) };

    private static ItemState DecodeItem(SqliteDataReader reader) => new(
            RowCodec.Join(reader.GetString(1), reader.GetString(2)),
            RowCodec.KindFrom(reader.GetInt64(3)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : RowCodec.HashHex((byte[])reader.GetValue(6)),
            reader.GetInt64(7),
            RowCodec.MtimeFrom(reader.GetInt64(8)),
            (ItemFlags)reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));

    /// <summary>One live row: its key, its parent path and its decoded state.</summary>
    public readonly record struct Entry(string Parent, string Name, ItemState Item, long DirNo)
    {
        public string NameKey { get; init; } = RowCodec.Key(Name);

        /// <summary>Builds an entry for an incoming item (its folder number is assigned on insert).</summary>
        public static Entry Of(string parent, string name, ItemState item, long dirNo = 0) => new(parent, name, item, dirNo);

        /// <summary>Orders entries by (parent, name) in UTF-8 byte order.</summary>
        public static int CompareByKey(Entry left, Entry right)
        {
            var byParent = PathOrder.Instance.Compare(left.Parent, right.Parent);
            return byParent != 0 ? byParent : PathOrder.Instance.Compare(left.Name, right.Name);
        }
    }
}
