using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal const string NarrowingDigest = "narrowing";
    internal Action? NarrowingBatchForTest { get; set; }

    // Retire authorization FIRST. A crash at any batch boundary leaves an explicit stale header (also blocking legacy JSON reimport).
    // Keep the row generation; old lazy views are bound to the header digest as well.
    internal void SavePairNarrowed(PairDef previous, PairDef updated, PrepareHeader header,
        Func<string, bool, bool> excluded, Func<string, bool> scopeExcluded, string oldScope, string newScope, string digest,
        Func<int, string> metadata)
    {
        var previousJson = JsonSerializer.Serialize(previous, RowCodec.Json);
        InStore(() =>
        {
            using var tx = _connection.BeginTransaction();
            var current = StoreSchema.Scalar(_connection, tx,
                "SELECT def_json FROM pair WHERE pair_id=$p;", ("$p", previous.PairId)) as string;
            if (current is null || JsonSerializer.Serialize(Deserialize(current), RowCodec.Json) != previousJson ||
                StoreSchema.Scalar(_connection, tx,
                    "SELECT generation FROM prepare_header WHERE pair_id=$p AND digest=$d AND metadata_json=$m;",
                    ("$p", previous.PairId), ("$d", header.Digest), ("$m", header.MetadataJson)) as string != header.Generation)
                throw new PreparePublicationException("The pair or its prepare changed during narrowing.");
            lock (PrepareWritersGate) ActivePrepareGenerations.Add(header.Generation);
            StoreSchema.Execute(_connection, tx, "UPDATE prepare_header SET digest=$d WHERE pair_id=$p;",
                ("$d", NarrowingDigest), ("$p", previous.PairId));
            tx.Commit();
        });
        try
        {
            InStore(() =>
            {
                _connection.CreateFunction<string, bool, bool>("prepare_excluded", excluded);
                _connection.CreateFunction<string, bool>("prepare_scope_excluded", scopeExcluded);
                _connection.CreateFunction<string, string>("prepare_key", PathKey.Of);
                _connection.CreateFunction<string, string>("prepare_parent", path =>
                    path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : "");
                StoreSchema.Execute(_connection, null,
                    "CREATE TEMP TABLE IF NOT EXISTS prepare_blocked(path TEXT PRIMARY KEY) WITHOUT ROWID; DELETE FROM prepare_blocked;" +
                    "CREATE TEMP TABLE IF NOT EXISTS prepare_protected(path TEXT PRIMARY KEY) WITHOUT ROWID; DELETE FROM prepare_protected;" +
                    "CREATE TEMP TABLE IF NOT EXISTS narrow_rows(id INTEGER PRIMARY KEY); DELETE FROM narrow_rows;");
                foreach (var path in updated.Excluded.Where(path =>
                    !previous.Excluded.Any(parent => PairPath.IsSelfOrUnder(path, parent))))
                    StoreSchema.Execute(_connection, null,
                        "INSERT OR IGNORE INTO prepare_protected(path) VALUES($path);", ("$path", PathKey.Of(path)));
            });
            const string rowPath = "CASE kind WHEN 'matched' THEN lookup_key " +
                "WHEN 'heldRules' THEN json_extract(json,'$.path') ELSE substr(lookup_key,3) END";
            // Closed-reader keyset pages. No store lock spans a page, and only temp tables grow here.
            WalkRows(() => StoreSchema.Execute(_connection, null,
                "INSERT OR IGNORE INTO prepare_protected(path) SELECT prepare_key(" + rowPath + ") " +
                "FROM prepare_row WHERE rowid IN (SELECT id FROM narrow_rows) AND (prepare_scope_excluded(" + rowPath + ") OR " +
                "(kind='matched' AND (prepare_excluded(lookup_key,substr(json_extract(json,'$.a'),-2)='|d') OR " +
                "prepare_excluded(lookup_key,substr(json_extract(json,'$.b'),-2)='|d'))));"));
            foreach (var rootId in new[] { previous.A.RootId, previous.B.RootId })
            {
                var root = InStore(() => RootNo(rootId));
                if (root is null) continue;
                long directory = -1;
                var key = "";
                var name = "";
                while (true)
                {
                    var page = InStore(() =>
                    {
                        using var command = StoreSchema.Command(_connection, null,
                            "SELECT f.dir_no,f.name_key,f.name,CASE WHEN d.path='' THEN f.name ELSE d.path||'/'||f.name END,f.kind " +
                            "FROM file_state f JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$r " +
                            "AND (f.dir_no,f.name_key,f.name)>($dir,$key,$name) ORDER BY f.dir_no,f.name_key,f.name LIMIT 256;");
                        command.Parameters.AddWithValue("$r", root.Value);
                        command.Parameters.AddWithValue("$dir", directory);
                        command.Parameters.AddWithValue("$key", key);
                        command.Parameters.AddWithValue("$name", name);
                        var result = new List<(long Dir, string Key, string Name, string Path, bool Folder)>();
                        using var reader = command.ExecuteReader();
                        while (reader.Read()) result.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                            reader.GetString(3), reader.GetInt32(4) == (int)ItemKind.Folder));
                        return result;
                    });
                    if (page.Count == 0) break;
                    InStore(() =>
                    {
                        foreach (var item in page)
                            if (excluded(item.Path, item.Folder)) StoreSchema.Execute(_connection, null,
                                "INSERT OR IGNORE INTO prepare_protected(path) VALUES($p);", ("$p", PathKey.Of(item.Path)));
                    });
                    (directory, key, name) = (page[^1].Dir, page[^1].Key, page[^1].Name);
                }
            }
            // Ancestor expansion is also bounded by protected-path pages, not the whole inventory.
            var after = "";
            while (true)
            {
                var paths = InStore(() =>
                {
                    using var command = StoreSchema.Command(_connection, null,
                        "SELECT path FROM prepare_protected WHERE path>$after ORDER BY path LIMIT 256;");
                    command.Parameters.AddWithValue("$after", after);
                    var result = new List<string>();
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) result.Add(reader.GetString(0));
                    return result;
                });
                if (paths.Count == 0) break;
                InStore(() =>
                {
                    foreach (var path in paths)
                        StoreSchema.Execute(_connection, null,
                            "WITH RECURSIVE ancestors(path) AS (SELECT $p UNION ALL " +
                            "SELECT prepare_parent(path) FROM ancestors WHERE path<>'') " +
                            "INSERT OR IGNORE INTO prepare_blocked(path) SELECT path FROM ancestors;", ("$p", path));
                });
                after = paths[^1];
            }
            WalkRows(() =>
            {
                using var tx = _connection.BeginTransaction();
                StoreSchema.Execute(_connection, tx,
                    "DELETE FROM prepare_row WHERE rowid IN (SELECT id FROM narrow_rows) AND (prepare_scope_excluded(" + rowPath + ") OR " +
                    "prepare_key(" + rowPath + ") IN (SELECT path FROM prepare_protected) OR " +
                    "(kind IN ('deleteVictims','reviewed','heldDeletions','heldRules','carriedIgnoredDeletes') AND " +
                    "prepare_key(" + rowPath + ") IN (SELECT path FROM prepare_blocked)));" );
                tx.Commit();
                // Checkpoint each bounded write; do not grow an unbounded WAL behind a long reader.
                using var checkpoint = StoreSchema.Command(_connection, null, "PRAGMA wal_checkpoint(PASSIVE);");
                using var result = checkpoint.ExecuteReader();
                if (result.Read() && result.GetInt64(1) - result.GetInt64(2) > 4096)
                    throw new PreparePublicationException("Narrowing paused behind a database reader; the prepare is stale and must be rebuilt.");
            }, notify: true);
            InStore(() =>
            {
                using var tx = _connection.BeginTransaction();
                var current = StoreSchema.Scalar(_connection, tx,
                    "SELECT def_json FROM pair WHERE pair_id=$p;", ("$p", previous.PairId)) as string;
                if (current is null || JsonSerializer.Serialize(Deserialize(current), RowCodec.Json) != previousJson ||
                    StoreSchema.Scalar(_connection, tx, "SELECT generation FROM prepare_header WHERE pair_id=$p AND digest=$d;",
                        ("$p", previous.PairId), ("$d", NarrowingDigest)) as string != header.Generation)
                    throw new PreparePublicationException("The pair changed during narrowing; re-prepare its latest settings.");
                var count = checked((int)(long)StoreSchema.Scalar(_connection, tx,
                    "SELECT COUNT(*) FROM prepare_row WHERE generation=$g AND kind='deleteVictims';", ("$g", header.Generation))!);
                var json = metadata(count);
                using (JsonDocument.Parse(json)) { }
                StoreSchema.Execute(_connection, tx,
                    "UPDATE prepare_cursor SET cursor=CASE WHEN json_valid(cursor) THEN CASE " +
                    "WHEN json_extract(cursor,'$.scope')=$old THEN json_set(cursor,'$.scope',$scope) ELSE NULL END ELSE NULL END WHERE generation=$g;",
                    ("$g", header.Generation), ("$old", oldScope), ("$scope", newScope));
                StoreSchema.Execute(_connection, tx,
                    "UPDATE prepare_header SET metadata_json=$m,digest=$d WHERE pair_id=$p AND generation=$g;",
                    ("$p", previous.PairId), ("$g", header.Generation), ("$m", json), ("$d", digest));
                StoreSchema.Execute(_connection, tx, "UPDATE pair SET def_json=$j WHERE pair_id=$p;",
                    ("$j", JsonSerializer.Serialize(updated, RowCodec.Json)), ("$p", previous.PairId));
                // Narrowing removes coverage only. The old complete inventory/cursor still covers
                // every retained path; predicates filter retired paths without a provider relist.
                tx.Commit();
            });
        }
        finally
        {
            InStore(() =>
            {
                _connection.CreateFunction<string, bool, bool>("prepare_excluded", null);
                _connection.CreateFunction<string, bool>("prepare_scope_excluded", null);
                _connection.CreateFunction<string, string>("prepare_parent", null);
                _connection.CreateFunction<string, string>("prepare_key", null);
                lock (PrepareWritersGate) ActivePrepareGenerations.Remove(header.Generation);
            });
        }

        void WalkRows(Action work, bool notify = false)
        {
            long afterId = 0;
            while (true)
            {
                var last = InStore(() =>
                {
                    StoreSchema.Execute(_connection, null,
                        "DELETE FROM narrow_rows; INSERT INTO narrow_rows SELECT rowid FROM prepare_row NOT INDEXED " +
                        "WHERE generation=$g AND rowid>$after ORDER BY rowid LIMIT 256;",
                        ("$g", header.Generation), ("$after", afterId));
                    var value = StoreSchema.Scalar(_connection, null, "SELECT MAX(id) FROM narrow_rows;");
                    if (value is not long id) return (long?)null;
                    work();
                    return id;
                });
                if (last is null) break;
                afterId = last.Value;
                if (notify) NarrowingBatchForTest?.Invoke();
            }
        }
    }

    public bool AnyLiveIgnoredUnder(string rootId, string path, IReadOnlyList<SyncIgnoreRule> rules) => InStore(() =>
    {
        if (rules.Count == 0 || RootNo(rootId) is not { } root) return false;
        // The SQL reader streams; LIMIT 1 stops on the first protected descendant.
        _connection.CreateFunction<string, long, bool>("narrow_ignored", (candidate, kind) =>
            PairPath.IsSelfOrUnder(candidate, path) &&
            SyncIgnorePolicy.IsIgnored(candidate, kind == (long)ItemKind.Folder, rules));
        try
        {
            return StoreSchema.Scalar(_connection, null,
                "SELECT 1 FROM file_state f JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$r " +
                (path.Length == 0 ? "" : "AND f.dir_no IN (SELECT dir_no FROM dir WHERE path_key=$path OR " +
                "(path_key >= $prefix AND path_key < $end)) ") +
                "AND narrow_ignored(CASE WHEN d.path='' THEN f.name ELSE d.path||'/'||f.name END,f.kind) LIMIT 1;",
                ("$r", root), ("$path", PathKey.Of(path)), ("$prefix", PathKey.Of(path) + "/"),
                ("$end", PathKey.Of(path) + "0")) is not null;
        }
        finally { _connection.CreateFunction<string, long, bool>("narrow_ignored", null); }
    });
}
