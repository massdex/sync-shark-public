namespace Massdex.SyncShark.V2;

// Decisions 87/88: the contents of a folder an explicit conflict choice removes are frozen once, inside SQLite, and
// then read and settled in bounded pages. Nothing here materializes the subtree in application memory.
public sealed partial class SqliteStateStore
{
    private const string VictimColumns =
        "0, dir_path, name, kind, object_id, version_id, hash, size, mtime, flags, identity_json";

    public long FreezeChoiceVictims(string pairId, string operationId, string rootId, string folderPath) => InStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var count = FreezeChoiceVictims(transaction, pairId, operationId, rootId, folderPath);
        transaction.Commit();
        return count;
    });

    private long FreezeChoiceVictims(Microsoft.Data.Sqlite.SqliteTransaction transaction, string pairId,
        string operationId, string rootId, string folderPath)
    {
        if (PairNo(pairId) is not { } pairNo) throw new InvalidOperationException("The pair is unavailable.");
        if (RootNo(rootId) is not { } rootNo) throw new InvalidOperationException("The folder's root is unavailable.");
        if (folderPath.Length == 0) throw new InvalidOperationException("A pair root is never removed by a conflict choice.");
        // The sentinel row (depth -1) marks a completed freeze, so an empty folder is never frozen a second time.
        if (StoreSchema.Scalar(_connection, transaction, "SELECT 1 FROM choice_victim WHERE op_id=$op LIMIT 1;",
                ("$op", operationId)) is null)
        {
            _dir.PopulatePrefixes(transaction, [folderPath]);
            // Only the folders at or under the prefix (tmp_dirs): the folder's own row lives in its parent and is
            // removed by the choice's own action after its contents.
            StoreSchema.Execute(_connection, transaction,
                "INSERT INTO choice_victim(op_id,pair_no,state,depth,dir_path,name,kind,object_id,version_id,hash,size,mtime,flags,identity_json) " +
                "SELECT $op,$p,0,LENGTH(d.path)-LENGTH(REPLACE(d.path,'/',''))+(CASE WHEN d.path='' THEN 0 ELSE 1 END)," +
                "d.path,f.name,f.kind,f.object_id,f.version_id,f.hash,f.size,f.mtime,f.flags,f.identity_json " +
                "FROM tmp_dirs t CROSS JOIN file_state f JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$r AND f.dir_no=t.dir_no;",
                ("$op", operationId), ("$p", pairNo), ("$r", rootNo));
            StoreSchema.Execute(_connection, transaction,
                "INSERT INTO choice_victim(op_id,pair_no,state,depth,dir_path,name,kind,object_id,size,mtime,flags) " +
                "VALUES($op,$p,$state,-1,'','',0,'',0,0,0);",
                ("$op", operationId), ("$p", pairNo), ("$state", (long)ChoiceVictimState.Removed));
        }
        var count = (long)StoreSchema.Scalar(_connection, transaction,
            "SELECT COUNT(*) FROM choice_victim WHERE op_id=$op AND depth>=0;", ("$op", operationId))!;
        return count;
    }

    public IReadOnlyList<(long Ord, ItemState Item)> ChoiceVictims(string operationId, ChoiceVictimState state, int limit,
        bool deepestFirst, long afterOrd = 0, bool ordinalOrder = false) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            $"SELECT ord, {VictimColumns} FROM choice_victim WHERE op_id=$op AND state=$s AND depth>=0 AND ord>$after " +
            (!ordinalOrder ? $"ORDER BY depth {(deepestFirst ? "DESC" : "ASC")}, ord" : "ORDER BY ord") + " LIMIT $limit;");
        command.Parameters.AddWithValue("$after", Math.Max(afterOrd, 0));
        command.Parameters.AddWithValue("$op", operationId);
        command.Parameters.AddWithValue("$s", (long)state);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 4096));
        var page = new List<(long, ItemState)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            page.Add((reader.GetInt64(0), new ItemState(
                RowCodec.Join(reader.GetString(2), reader.GetString(3)),
                RowCodec.KindFrom(reader.GetInt64(4)),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : RowCodec.HashHex((byte[])reader.GetValue(7)),
                reader.GetInt64(8),
                RowCodec.MtimeFrom(reader.GetInt64(9)),
                (ItemFlags)reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11))));
        return page;
    });

    public void MarkChoiceVictims(string operationId, IReadOnlyList<(long Ord, ChoiceVictimState State)> marks) => InStore(() =>
    {
        if (marks.Count == 0) return;
        using var transaction = _connection.BeginTransaction();
        foreach (var (ord, state) in marks)
            StoreSchema.Execute(_connection, transaction,
                "UPDATE choice_victim SET state=$s WHERE ord=$o AND op_id=$op AND depth>=0;",
                ("$s", (long)state), ("$o", ord), ("$op", operationId));
        transaction.Commit();
    });

    public long CountChoiceVictims(string operationId, ChoiceVictimState state) => InStore(() =>
        (long)StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM choice_victim WHERE op_id=$op AND state=$s AND depth>=0;",
            ("$op", operationId), ("$s", (long)state))!);

    public long CountChoiceGrowth(string operationId, string rootId, string folderPath) => InStore(() =>
    {
        if (RootNo(rootId) is not { } rootNo) return 0L;
        _dir.PopulatePrefixes(null, [folderPath]);
        return (long)StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM (SELECT 1 FROM tmp_dirs t CROSS JOIN file_state f JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND f.dir_no=t.dir_no AND NOT EXISTS (SELECT 1 FROM choice_victim cv " +
            "WHERE cv.op_id=$op AND cv.dir_path=d.path AND cv.name=f.name AND cv.object_id=f.object_id) LIMIT 1000);",
            ("$r", rootNo), ("$op", operationId))!;
    });

    public bool ChoiceRemovalProtected(PairDef pair, Side side, string path, bool folder) =>
        InStore(() => ChoiceRemovalProtectedCore(pair, side, path, folder, null));

    public IReadOnlyList<bool> ChoiceRemovalProtectedBatch(PairDef pair, Side side, IReadOnlyList<ItemState> items) => InStore(() =>
    {
        if (items.Count > 4096) throw new ArgumentOutOfRangeException(nameof(items));
        var ancestors = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return (IReadOnlyList<bool>)items.Select(item =>
            ChoiceRemovalProtectedCore(pair, side, item.Path, item.Kind == ItemKind.Folder, ancestors)).ToArray();
    });

    private bool ChoiceRemovalProtectedCore(PairDef pair, Side side, string path, bool folder, Dictionary<string, bool>? ancestors)
    {
        var root = RootNo(side == Side.A ? pair.A.RootId : pair.B.RootId);
        var other = RootNo(side == Side.A ? pair.B.RootId : pair.A.RootId);
        var pairNo = PairNo(pair.PairId);
        if (root is null || other is null || pairNo is null) return true;
        var own = side == Side.A ? "a" : "b";
        var opposite = side == Side.A ? "b" : "a";
        // Starting at a candidate entity, both joins use object indexes. No root-wide refused list is built.
        var protectedSql = "((f.flags & 64) != 0 OR (f.kind=1 AND EXISTS (" +
            $"SELECT 1 FROM sync_point sp JOIN file_state src INDEXED BY fs_obj ON src.root_no=$other AND src.object_id=sp.{opposite}_object_id " +
            $"WHERE sp.pair_no=$pair AND sp.{own}_object_id=f.object_id AND src.kind=1 AND (src.flags & 64)!=0)))";
        var current = path;
        while (current.Length > 0)
        {
            var slash = current.LastIndexOf('/');
            var directory = slash < 0 ? "" : current[..slash];
            var name = current[(slash + 1)..];
            var cacheKey = (current == path ? "self:" : "ancestor:") + current;
            if (ancestors is null || !ancestors.TryGetValue(cacheKey, out var protectedHere))
            {
                protectedHere = StoreSchema.Scalar(_connection, null,
                "SELECT 1 FROM dir d CROSS JOIN file_state f WHERE d.path_key=$dir AND f.root_no=$root " +
                "AND f.dir_no=d.dir_no AND f.name_key=$name AND ($self=1 OR f.kind=1) AND " + protectedSql + " LIMIT 1;",
                ("$dir", PathKey.Of(directory)), ("$name", PathKey.Of(name)), ("$root", root.Value),
                ("$other", other.Value), ("$pair", pairNo.Value), ("$self", current == path ? 1 : 0)) is not null;
                if (ancestors is not null) ancestors[cacheKey] = protectedHere;
            }
            if (protectedHere) return true;
            current = directory;
        }
        if (!folder) return false;
        _dir.PopulatePrefixes(null, [path]);
        return StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM tmp_dirs t CROSS JOIN file_state f WHERE f.root_no=$root AND f.dir_no=t.dir_no AND " +
            protectedSql + " LIMIT 1;", ("$root", root.Value), ("$other", other.Value), ("$pair", pairNo.Value)) is not null;
    }

    public bool ChoiceCarriedChanged(string operationId, string rootId) => InStore(() =>
    {
        return StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM choice_victim cv JOIN dir d ON d.path=cv.dir_path " +
            "JOIN file_state f ON f.root_no=$root AND f.dir_no=d.dir_no AND f.name_key=choice_key(cv.name) " +
            "WHERE cv.op_id=$op AND cv.state=$carried AND cv.depth>=0 AND " +
            "(f.object_id IS NOT cv.object_id OR f.kind IS NOT cv.kind OR (cv.kind=0 AND ((cv.hash IS NOT NULL AND f.hash IS NOT cv.hash) OR " +
            "f.size IS NOT cv.size OR f.version_id IS NOT cv.version_id OR f.mtime IS NOT cv.mtime))) LIMIT 1;",
            ("$root", RootNo(rootId)), ("$op", operationId), ("$carried", (long)ChoiceVictimState.Carried)) is not null;
    });

    public bool HasSyncableChoiceGrowth(string operationId, string rootId, string folderPath, Func<ItemState, bool> syncable)
    {
        string? directory = folderPath;
        while (directory is not null)
        {
            var afterKey = "";
            var afterName = "";
            while (true)
            {
                // One indexed directory and at most 256 children per lock. Predicates run outside it.
                var page = InStore(() =>
                {
                    var rows = new List<(string Key, string Name, ItemState Item, bool Frozen)>();
                    if (RootNo(rootId) is not { } rootNo) return rows;
                    using var command = StoreSchema.Command(_connection, null,
                        "SELECT f.name_key,f.name,f.kind,f.object_id,f.flags," +
                        "EXISTS(SELECT 1 FROM choice_victim cv WHERE cv.op_id=$op AND cv.dir_path=d.path AND cv.name=f.name AND cv.object_id=f.object_id) " +
                        "FROM dir d CROSS JOIN file_state f WHERE d.path=$dir AND f.root_no=$r AND f.dir_no=d.dir_no " +
                        "AND (f.name_key,f.name)>($key,$name) ORDER BY f.name_key,f.name LIMIT 256;");
                    command.Parameters.AddWithValue("$r", rootNo);
                    command.Parameters.AddWithValue("$op", operationId);
                    command.Parameters.AddWithValue("$dir", directory);
                    command.Parameters.AddWithValue("$key", afterKey);
                    command.Parameters.AddWithValue("$name", afterName);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        rows.Add((reader.GetString(0), reader.GetString(1),
                            new ItemState(RowCodec.Join(directory, reader.GetString(1)), RowCodec.KindFrom(reader.GetInt64(2)),
                                reader.GetString(3), null, null, 0, DateTimeOffset.UnixEpoch, (ItemFlags)reader.GetInt64(4)), reader.GetBoolean(5)));
                    return rows;
                });
                if (page.Count == 0) break;
                foreach (var row in page)
                    if (!row.Frozen && (row.Item.Flags & (ItemFlags.Ignored | ItemFlags.ProviderNameRefused)) == 0 && syncable(row.Item)) return true;
                (afterKey, afterName) = (page[^1].Key, page[^1].Name);
            }
            var key = directory;
            var prefix = folderPath + "/";
            directory = InStore(() => StoreSchema.Scalar(_connection, null,
                "SELECT path FROM dir WHERE path>$after AND path>=$lo AND path<$hi ORDER BY path LIMIT 1;",
                ("$after", key), ("$lo", prefix), ("$hi", folderPath + "0")) as string);
        }
        return false;
    }

    public bool VerifyCarriedChoice(string pairId, string operationId, ItemState current) => InStore(() =>
    {
        var (parent, name) = RowCodec.Split(current.Path);
        using var command = StoreSchema.Command(_connection, null,
            "SELECT ord,kind,object_id,version_id,hash,size,mtime FROM choice_victim WHERE op_id=$op AND dir_path=$dir AND name=$name AND state=$carried;");
        command.Parameters.AddWithValue("$op", operationId);
        command.Parameters.AddWithValue("$dir", parent);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$carried", (long)ChoiceVictimState.Carried);
        long? ord = null;
        var matches = false;
        using (var reader = command.ExecuteReader())
            if (reader.Read())
            {
                ord = reader.GetInt64(0);
                matches = RowCodec.KindFrom(reader.GetInt64(1)) == current.Kind && reader.GetString(2) == current.ObjectId &&
                    (current.Kind == ItemKind.Folder || ((reader.IsDBNull(3) ? null : reader.GetString(3)) == current.VersionId &&
                    (reader.IsDBNull(4) || string.Equals(RowCodec.HashHex((byte[])reader.GetValue(4)), current.Hash, StringComparison.OrdinalIgnoreCase)) &&
                    reader.GetInt64(5) == current.Size && RowCodec.MtimeFrom(reader.GetInt64(6)) == current.Mtime));
            }
        if (matches) return true;
        using var transaction = _connection.BeginTransaction();
        if (ord is { } row)
            StoreSchema.Execute(_connection, transaction, "UPDATE choice_victim SET state=$kept WHERE ord=$ord AND op_id=$op;",
                ("$kept", (long)ChoiceVictimState.Kept), ("$ord", row), ("$op", operationId));
        _notices.Add(transaction, RequirePair(pairId), new Notice(pairId, 87, NoticeKind.Info, current.Path,
            "A carried ignored item changed after this choice. It and its folder were kept; earlier removals remain in Recovery.",
            "conflict-carried-changed"), NowMs());
        transaction.Commit();
        return false;
    });

    public bool HasChoiceSnapshot(string operationId) => InStore(() =>
        StoreSchema.Scalar(_connection, null, "SELECT 1 FROM choice_victim WHERE op_id=$op AND depth=-1 LIMIT 1;",
            ("$op", operationId)) is not null);

    public void ForgetChoiceVictims(string operationId)
    {
        // Bounded batches; no store gate spans a batch boundary.
        while (InStore(() =>
        {
            StoreSchema.Execute(_connection, null,
                "DELETE FROM choice_victim WHERE ord IN (SELECT ord FROM choice_victim WHERE op_id=$op LIMIT 2048);",
                ("$op", operationId));
            return StoreSchema.Changes(_connection, null);
        }) > 0) { }
    }
}
