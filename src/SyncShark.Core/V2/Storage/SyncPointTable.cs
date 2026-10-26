using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Reads and writes baselines in the sync_point table.</summary>
internal sealed class SyncPointTable
{
    private const string Columns =
        "sp.object_key, sp.gen, sp.kind, sp.hash, sp.size, " +
        "da.path AS a_path, sp.a_name, sp.a_object_id, sp.a_version_id, sp.a_mtime, " +
        "db.path AS b_path, sp.b_name, sp.b_object_id, sp.b_version_id, sp.b_mtime";

    private const string Joins = "LEFT JOIN dir da ON da.dir_no = sp.a_dir LEFT JOIN dir db ON db.dir_no = sp.b_dir";

    private const string From = "sync_point sp " + Joins;

    private readonly SqliteConnection _connection;
    private readonly DirTable _dir;

    public SyncPointTable(SqliteConnection connection, DirTable dir)
    {
        _connection = connection;
        _dir = dir;
    }

    /// <summary>Baselines of the pair in object-key order, or one point query per requested key.</summary>
    public IReadOnlyList<Baseline> Read(string pairId, long pairNo, IEnumerable<string>? objectKeys)
    {
        if (objectKeys is null)
            return Baselines(pairId, Query(null,
                $"SELECT {Columns} FROM {From} WHERE sp.pair_no=$p ORDER BY sp.object_key;", [("$p", pairNo)]));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<Row>();
        foreach (var key in objectKeys)
            if (seen.Add(key))
                rows.AddRange(Query(null, $"SELECT {Columns} FROM {From} WHERE sp.pair_no=$p AND sp.object_key=$k;",
                    [("$p", pairNo), ("$k", key)]));
        rows.Sort((x, y) => PathOrder.Instance.Compare(x.ObjectKey, y.ObjectKey));
        return Baselines(pairId, rows);
    }

    internal IReadOnlyList<Baseline> DateRepairPage(string pairId, long pairNo, long localRootNo, long cloudRootNo,
        string after, int limit, Side side, long start, long end, bool mismatch)
    {
        var local = side == Side.A ? "a" : "b";
        var cloud = side == Side.A ? "b" : "a";
        string Dir(string s) => s == "a" ? "sp.a_dir" : "COALESCE(sp.b_dir,sp.a_dir)";
        string Name(string s) => s == "a" ? "sp.a_name" : "COALESCE(sp.b_name,sp.a_name)";
        string Key(string s) => s == "a" ? "sp.a_name_key" : "COALESCE(sp.b_name_key,sp.a_name_key)";
        var gap = $"lf.mtime-COALESCE(cf.mtime,sp.{cloud}_mtime)";
        if (mismatch) gap = $"abs({gap})";
        // Indexed path joins: the baseline supplies correspondence, not the current local download date.
        return Baselines(pairId, Query(null,
            $"SELECT {Columns} FROM {From} " +
            $"JOIN file_state lf ON lf.root_no=$lr AND lf.dir_no={Dir(local)} AND lf.name_key={Key(local)} AND lf.name={Name(local)} " +
            $"LEFT JOIN file_state cf ON cf.root_no=$cr AND cf.dir_no={Dir(cloud)} AND cf.name_key={Key(cloud)} AND cf.name={Name(cloud)} " +
            $"WHERE sp.pair_no=$p AND sp.object_key>$after AND sp.kind=0 AND lf.kind=0 " +
            $"AND {gap}>=$gap AND lf.mtime>=$start AND lf.mtime<$end " +
            "ORDER BY sp.object_key LIMIT $limit;", [("$p", pairNo), ("$lr", localRootNo), ("$cr", cloudRootNo),
                ("$after", after), ("$limit", limit), ("$gap", TimeSpan.TicksPerDay), ("$start", start), ("$end", end)]));
    }

    /// <summary>Exact indexed path lookups, never descendants; includes compact same-path B baselines.</summary>
    public IReadOnlyList<Baseline> At(string pairId, long pairNo, Side side, IEnumerable<string> paths)
    {
        var found = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (parent, name) = RowCodec.Split(path);
            foreach (var (column, extra) in side == Side.A
                ? new[] { ("a", "") } : new[] { ("b", ""), ("a", " AND sp.b_dir IS NULL") })
                foreach (var row in Query(null,
                    $"SELECT {Columns} FROM sync_point sp INDEXED BY sp_{column}_dir {Joins} " +
                    $"WHERE sp.pair_no=$p AND sp.{column}_dir IN (SELECT dir_no FROM dir WHERE path_key=$d) " +
                    $"AND sp.{column}_name_key=$n{extra};",
                    [("$p", pairNo), ("$d", RowCodec.Key(parent)), ("$n", RowCodec.Key(name))]))
                    found[row.ObjectKey] = row;
        }
        return Baselines(pairId, found.Values.ToList());
    }

    /// <summary>Baselines whose side object id is listed or whose side path is at a prefix or under it.</summary>
    public IReadOnlyList<Baseline> Touching(string pairId, long pairNo, Side side, IEnumerable<string> objectIds,
        IEnumerable<string> pathPrefixes)
    {
        var found = new Dictionary<string, Row>(StringComparer.Ordinal);
        var column = side == Side.A ? "a_object_id" : "b_object_id";
        // INDEXED BY: without ANALYZE statistics SQLite picks the (pair_no, object_key) primary key and scans the whole
        // pair for every id (measured 144 ms vs 0.05 ms per lookup on a 133k-baseline pair), which a folder delete
        // expanding tens of thousands of descendants turns into hours.
        var index = side == Side.A ? "sp_a_obj" : "sp_b_obj";
        foreach (var id in objectIds)
            foreach (var row in Query(null,
                $"SELECT {Columns} FROM sync_point sp INDEXED BY {index} {Joins} WHERE sp.pair_no=$p AND sp.{column}=$i;",
                [("$p", pairNo), ("$i", id)]))
                found[row.ObjectKey] = row;
        var prefixes = pathPrefixes.ToArray();
        if (prefixes.Length > 0)
        {
            _dir.PopulatePrefixes(null, prefixes);
            foreach (var row in Query(null, PrefixSql(side), [("$p", pairNo)]))
                found[row.ObjectKey] = row;
        }
        var rows = found.Values.ToList();
        rows.Sort((x, y) => PathOrder.Instance.Compare(x.ObjectKey, y.ObjectKey));
        return Baselines(pairId, rows);
    }

    /// <summary>Removes every baseline of the pair whose side path is at the path or under it (decision 59).</summary>
    public void ForgetUnder(SqliteTransaction transaction, long pairNo, Side side, string path, long rootNoA,
        long rootNoB, Func<long, string, bool> baselined)
    {
        _dir.PopulatePrefixes(transaction, [path]);
        // Select keys once in SQLite, then consume bounded batches. Restarting PrefixSql per batch would
        // repeatedly probe already-empty directories (quadratic for one file per directory).
        StoreSchema.Execute(_connection, transaction,
            "CREATE TEMP TABLE IF NOT EXISTS tmp_forget_keys(object_key TEXT PRIMARY KEY) WITHOUT ROWID;");
        StoreSchema.Execute(_connection, transaction, "DELETE FROM tmp_forget_keys;");
        StoreSchema.Execute(_connection, transaction,
            "INSERT OR IGNORE INTO tmp_forget_keys SELECT object_key FROM (" + PrefixSql(side).TrimEnd(';') + ");",
            ("$p", pairNo));
        while (true)
        {
            var keys = new List<string>(256);
            using (var command = StoreSchema.Command(_connection, transaction,
                "SELECT object_key FROM tmp_forget_keys ORDER BY object_key LIMIT 256;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) keys.Add(reader.GetString(0));
            if (keys.Count == 0) break;
            foreach (var key in keys)
            {
                Remove(transaction, pairNo, rootNoA, rootNoB, key, baselined);
                StoreSchema.Execute(_connection, transaction, "DELETE FROM tmp_forget_keys WHERE object_key=$k;", ("$k", key));
            }
        }
    }

    /// <summary>Applies the baseline part of a Done result inside the caller's transaction.</summary>
    /// <param name="baselined">Whether any baseline still names an object of a root (read after the change).</param>
    public void CommitDone(SqliteTransaction transaction, long pairNo, string pairId, long gen, long rootNoA,
        long rootNoB, SyncAction action, ActionResult result, Func<long, string, bool> baselined)
    {
        if (action.Kind is ActionKind.Delete or ActionKind.Forget)
        {
            Remove(transaction, pairNo, rootNoA, rootNoB, action.ObjectKey, baselined);
            return;
        }
        if (action.ObjectKey is null) throw new ArgumentException("A done action needs an object key.", nameof(action));
        var existing = ReadOne(transaction, pairNo, action.ObjectKey);
        var newTarget = action.Target == Side.A ? result.NewA : result.NewB;
        if (action.Kind != ActionKind.Adopt && newTarget is null)
            throw new InvalidOperationException($"Action {action.ActionId} needs a new {action.Target} state.");
        var stateTarget = newTarget ??
            (action.Kind == ActionKind.Adopt ? action.ExpectedTarget : null) ?? existing?.For(action.Target);
        var other = action.Target == Side.A ? Side.B : Side.A;
        var stateOther = (action.Target == Side.A ? result.NewB : result.NewA) ?? action.Source ?? existing?.For(other);
        if (stateTarget is null || stateOther is null)
            throw new InvalidOperationException($"Cannot form baseline side {action.Target} for {action.ActionId}.");
        if (stateTarget.Kind != stateOther.Kind)
            throw new InvalidOperationException($"Baseline kinds differ for {action.ActionId}.");
        if (Content.SameContent(stateTarget, stateOther) is null)
            throw new InvalidOperationException($"Baseline content is unconfirmed for {action.ActionId}.");
        if (stateTarget.Kind == ItemKind.File && !Matching(stateTarget, stateOther))
        {
            if (action.Kind != ActionKind.Move)
                throw new InvalidOperationException($"Baseline file details differ for {action.ActionId}.");
            stateOther = MovedContent(stateTarget, stateOther, existing?.For(other));
        }
        var baseline = action.Target == Side.A
            ? new Baseline(pairId, action.ObjectKey, stateTarget, stateOther, gen)
            : new Baseline(pairId, action.ObjectKey, stateOther, stateTarget, gen);
        Upsert(transaction, pairNo, baseline);
        if (action.Kind == ActionKind.Move && action.ExpectedTarget is { Kind: ItemKind.Folder } &&
            action.FromPath is { } from && stateTarget.Kind == ItemKind.Folder &&
            action.ExpectedTarget.ObjectId == stateTarget.ObjectId)
            RebaseMovedDescendants(transaction, pairNo, pairId, gen, action, existing?.For(other), stateTarget, stateOther, from);
        // An identity this baseline no longer names may be resolved: see Resolve .
        if (existing is { } before && !SameId(before.A, baseline.A))
            Resolve(transaction, rootNoA, before.A.ObjectId, baselined);
        if (existing is { } prior && !SameId(prior.B, baseline.B))
            Resolve(transaction, rootNoB, prior.B.ObjectId, baselined);
    }

    // Folder effects carry descendant names without emitting a result per implied child Move.
    // Select their keys once through the indexed prefix query, then rebase bounded pages in
    // the same Done transaction. Rewrites preserve child content/version/identity evidence.
    private void RebaseMovedDescendants(SqliteTransaction tx, long pairNo, string pairId, long gen,
        SyncAction action, ItemState? otherBefore, ItemState targetAfter, ItemState otherAfter, string from)
    {
        _dir.PopulatePrefixes(tx, [from]);
        StoreSchema.Execute(_connection, tx,
            "CREATE TEMP TABLE IF NOT EXISTS tmp_move_baselines(object_key TEXT PRIMARY KEY) WITHOUT ROWID; " +
            "DELETE FROM tmp_move_baselines;");
        StoreSchema.Execute(_connection, tx,
            "INSERT OR IGNORE INTO tmp_move_baselines SELECT object_key FROM (" + PrefixSql(action.Target).TrimEnd(';') + ");",
            ("$p", pairNo));
        var after = "";
        while (true)
        {
            var rows = Query(tx, $"SELECT {Columns} FROM tmp_move_baselines k CROSS JOIN {From} " +
                "WHERE k.object_key>$after AND sp.pair_no=$p AND sp.object_key=k.object_key ORDER BY k.object_key LIMIT 256;",
                [("$p", pairNo), ("$after", after)]);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                var child = new Baseline(pairId, row.ObjectKey, row.A, row.B, row.Gen);
                var rebased = RebaseMovedDescendant(child, action, otherBefore, targetAfter, otherAfter, gen);
                if (rebased != child) Upsert(tx, pairNo, rebased);
            }
            after = rows[^1].ObjectKey;
        }
        StoreSchema.Execute(_connection, tx, "DELETE FROM tmp_move_baselines;");
    }

    // Shared with the in-memory contract implementation: this is a path transition, never
    // an Adopt of a child's possibly edited bytes. Only the caller's successful parent result admits it.
    internal static Baseline RebaseMovedDescendant(Baseline child, SyncAction action,
        ItemState? otherBefore, ItemState targetAfter, ItemState otherAfter, long gen)
    {
        if (action.Kind != ActionKind.Move || action.ExpectedTarget is not { Kind: ItemKind.Folder } expected ||
            targetAfter.Kind != ItemKind.Folder || expected.ObjectId != targetAfter.ObjectId || action.FromPath is not { } from)
            return child;
        var target = action.Target == Side.A ? child.A : child.B;
        if (!PairPath.IsUnder(target.Path, from)) return child;
        var other = action.Target == Side.A ? child.B : child.A;
        target = target with { Path = PairPath.Rebase(target.Path, from, targetAfter.Path) };
        if (otherBefore is { Kind: ItemKind.Folder } && otherBefore.ObjectId == otherAfter.ObjectId &&
            PairPath.IsUnder(other.Path, otherBefore.Path))
            other = other with { Path = PairPath.Rebase(other.Path, otherBefore.Path, otherAfter.Path) };
        return action.Target == Side.A ? child with { A = target, B = other, Gen = gen } :
            child with { A = other, B = target, Gen = gen };
    }

    // A tombstone is evidence for every baseline naming its (root, object): it goes only when, after this change,
    // no baseline names that object on that root any more.
    private void Resolve(SqliteTransaction transaction, long rootNo, string objectId, Func<long, string, bool> baselined)
    {
        if (!baselined(rootNo, objectId)) DropTombstone(transaction, rootNo, objectId);
    }

    /// <summary>Whether an object of a root is a side of some baseline; <paramref name="sides"/> are the root's.</summary>
    public bool HasBaseline(SqliteTransaction? transaction, IReadOnlyList<(long PairNo, Side Side)> sides,
        string objectId)
    {
        foreach (var (pairNo, side) in sides)
        {
            var column = side == Side.A ? "a_object_id" : "b_object_id";
            if (StoreSchema.Scalar(_connection, transaction,
                    $"SELECT 1 FROM sync_point WHERE pair_no=$p AND {column}=$o LIMIT 1;",
                    ("$p", pairNo), ("$o", objectId)) is not null)
                return true;
        }
        return false;
    }

    private static bool SameId(ItemState x, ItemState y) => string.Equals(x.ObjectId, y.ObjectId, StringComparison.Ordinal);

    // A folder delete's expansion also deletes descendants that never had a baseline: no key.
    private void Remove(SqliteTransaction transaction, long pairNo, long rootNoA, long rootNoB, string? objectKey,
        Func<long, string, bool> baselined)
    {
        if (objectKey is null) return;
        var removed = ReadOne(transaction, pairNo, objectKey);
        StoreSchema.Execute(_connection, transaction, "DELETE FROM sync_point WHERE pair_no=$p AND object_key=$k;",
            ("$p", pairNo), ("$k", objectKey));
        if (removed is not { } gone) return;
        Resolve(transaction, rootNoA, gone.A.ObjectId, baselined);
        Resolve(transaction, rootNoB, gone.B.ObjectId, baselined);
    }

    /// <summary>The query plans of BaselinesTouching's path-prefix lookups for both sides.</summary>
    public IReadOnlyList<string> Plans() =>
        StoreSchema.QueryPlan(_connection, "BaselinesTouching A", PrefixSql(Side.A), "$p")
            .Concat(StoreSchema.QueryPlan(_connection, "BaselinesTouching B", PrefixSql(Side.B), "$p")).ToArray();

    // Driven from the temporary prefix tables (CROSS JOIN fixes the loop order) into sp_a_dir / sp_b_dir, which carry
    // the leaf key: one range per folder and one point lookup per prefix item, never a scan of the pair. INDEXED BY
    // because without ANALYZE statistics SQLite prefers the covering (pair_no, object_key) key, a scan of the pair.
    // Side B's path is (b_dir, b_name), or the A path when b_dir is NULL (the same path on both sides).
    private static string PrefixSql(Side side)
    {
        var legs = side == Side.A
            ? new[] { PrefixLegs("sp_a_dir", "a", "") }
            : new[] { PrefixLegs("sp_b_dir", "b", ""), PrefixLegs("sp_a_dir", "a", " AND sp.b_dir IS NULL") };
        return string.Join(" UNION ALL ", legs) + ";";
    }

    private static string PrefixLegs(string index, string side, string extra) =>
        $"SELECT {Columns} FROM tmp_dirs t CROSS JOIN sync_point sp INDEXED BY {index} {Joins} " +
        $"WHERE sp.pair_no=$p AND sp.{side}_dir = t.dir_no{extra} " +
        $"UNION ALL SELECT {Columns} FROM tmp_items ti CROSS JOIN sync_point sp INDEXED BY {index} {Joins} " +
        $"WHERE sp.pair_no=$p AND sp.{side}_dir = ti.dir_no AND sp.{side}_name_key = ti.name_key{extra}";

    private void Upsert(SqliteTransaction transaction, long pairNo, Baseline baseline)
    {
        var samePath = string.Equals(baseline.A.Path, baseline.B.Path, StringComparison.Ordinal);
        var (aParent, aName) = RowCodec.Split(baseline.A.Path);
        var aDir = _dir.Intern(aParent, transaction);
        long? bDir = null;
        string? bName = null;
        if (!samePath)
        {
            var (bParent, leaf) = RowCodec.Split(baseline.B.Path);
            bDir = _dir.Intern(bParent, transaction);
            bName = leaf;
        }
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO sync_point(pair_no, object_key, gen, kind, hash, size, a_dir, a_name, a_name_key, a_object_id, " +
            "a_version_id, a_mtime, b_dir, b_name, b_name_key, b_object_id, b_version_id, b_mtime) " +
            "VALUES($p, $k, $g, $kind, $hash, $size, $ad, $an, $ak, $ao, $av, $am, $bd, $bn, $bk, $bo, $bv, $bm) " +
            "ON CONFLICT(pair_no, object_key) DO UPDATE SET gen=excluded.gen, kind=excluded.kind, hash=excluded.hash, " +
            "size=excluded.size, a_dir=excluded.a_dir, a_name=excluded.a_name, a_name_key=excluded.a_name_key, " +
            "a_object_id=excluded.a_object_id, a_version_id=excluded.a_version_id, a_mtime=excluded.a_mtime, " +
            "b_dir=excluded.b_dir, b_name=excluded.b_name, b_name_key=excluded.b_name_key, " +
            "b_object_id=excluded.b_object_id, b_version_id=excluded.b_version_id, b_mtime=excluded.b_mtime;",
            ("$p", pairNo), ("$k", baseline.ObjectKey), ("$g", baseline.Gen), ("$kind", RowCodec.KindCode(baseline.A.Kind)),
            ("$hash", RowCodec.HashBlob(baseline.A.Hash ?? baseline.B.Hash)), ("$size", baseline.A.Size),
            ("$ad", aDir), ("$an", aName), ("$ak", RowCodec.Key(aName)), ("$ao", baseline.A.ObjectId),
            ("$av", baseline.A.VersionId), ("$am", RowCodec.MtimeTicks(baseline.A.Mtime)),
            ("$bd", bDir), ("$bn", bName), ("$bk", bName is null ? null : RowCodec.Key(bName)),
            ("$bo", baseline.B.ObjectId), ("$bv", baseline.B.VersionId),
            ("$bm", RowCodec.MtimeTicks(baseline.B.Mtime)));
    }

    private Sides? ReadOne(SqliteTransaction transaction, long pairNo, string objectKey)
    {
        var rows = Query(transaction, $"SELECT {Columns} FROM {From} WHERE sp.pair_no=$p AND sp.object_key=$k;",
            [("$p", pairNo), ("$k", objectKey)]);
        return rows.Count == 0 ? null : new Sides(rows[0].A, rows[0].B);
    }

    private List<Row> Query(SqliteTransaction? transaction, string sql, (string Name, object? Value)[] arguments)
    {
        using var command = StoreSchema.Command(_connection, transaction, sql);
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var rows = new List<Row>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var kind = RowCodec.KindFrom(reader.GetInt64(2));
            var hash = reader.IsDBNull(3) ? null : RowCodec.HashHex((byte[])reader.GetValue(3));
            var size = reader.GetInt64(4);
            var aPath = RowCodec.Join(reader.GetString(5), reader.GetString(6));
            var a = new ItemState(aPath, kind, reader.GetString(7), Text(reader, 8), hash, size,
                RowCodec.MtimeFrom(reader.GetInt64(9)), ItemFlags.None);
            var bPath = reader.IsDBNull(10) ? aPath : RowCodec.Join(reader.GetString(10), reader.GetString(11));
            var b = new ItemState(bPath, kind, reader.GetString(12), Text(reader, 13), hash, size,
                RowCodec.MtimeFrom(reader.GetInt64(14)), ItemFlags.None);
            // The endpoint spellings already stored on the link retain this mapping without another schema field.
            if (!PairPath.Same(aPath, bPath))
            {
                if (PairPath.Same(DropboxNameMapping.Map(aPath), bPath)) a = a with { IdentityJson = DropboxNameMapping.Fact(aPath) };
                if (PairPath.Same(DropboxNameMapping.Map(bPath), aPath)) b = b with { IdentityJson = DropboxNameMapping.Fact(bPath) };
            }
            rows.Add(new Row(reader.GetString(0), reader.GetInt64(1), a, b));
        }
        return rows;
    }

    private static IReadOnlyList<Baseline> Baselines(string pairId, List<Row> rows) =>
        rows.Select(row => new Baseline(pairId, row.ObjectKey, row.A, row.B, row.Gen)).ToArray();

    private static string? Text(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static bool Matching(ItemState x, ItemState y) =>
        Content.SameContent(x, y) == true;

    // A move carries only the moved item's content; when the opposite side already holds newer content (the move
    // before its update, planner rule 28), the baseline keeps the moved content and the opposite side's previous
    // version, so a failed update is detected again as an edit of that side.
    private static ItemState MovedContent(ItemState moved, ItemState other, ItemState? previous) =>
        other with
        {
            Hash = moved.Hash, Size = moved.Size, VersionId = previous?.VersionId, Mtime = previous?.Mtime ?? other.Mtime,
        };

    private void DropTombstone(SqliteTransaction transaction, long rootNo, string objectId) =>
        StoreSchema.Execute(_connection, transaction, "DELETE FROM tombstone WHERE root_no=$r AND object_id=$o;",
            ("$r", rootNo), ("$o", objectId));

    private readonly record struct Row(string ObjectKey, long Gen, ItemState A, ItemState B);

    private readonly record struct Sides(ItemState A, ItemState B)
    {
        public ItemState For(Side side) => side == Side.A ? A : B;
    }
}
