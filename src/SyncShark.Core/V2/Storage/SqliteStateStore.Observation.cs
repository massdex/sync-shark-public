using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    public bool DeferIndexObservation(string rootId, Side side, ItemState observed, ItemState? expectedPrior, string reason) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null || observed.Kind != ItemKind.File) return false;
        using var batch = new StoreBatch(_connection, int.MaxValue);
        var atPath = _reads.AtKey(root.Value, observed.Path, batch.Transaction);
        if (expectedPrior is null ? atPath.Count != 0 : atPath.Count != 1 || atPath[0].Item != expectedPrior) return false;
        if (expectedPrior is not null && expectedPrior.ObjectId != observed.ObjectId) return false;
        var protectedItem = observed with { Hash = null, Flags = observed.Flags | ItemFlags.NeedsRehash };
        _files.AdmitIndexObservation(root.Value, protectedItem, batch);
        MarkRehash(root.Value, protectedItem, reason, batch.Transaction);
        MarkPlanningDirty(batch.Transaction, root.Value, [(protectedItem.ObjectId, protectedItem.Path)]);
        batch.Flush();
        return true;
    });

    internal InventoryObservation? ReadInventoryObservation(RootDef root) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT scope_json,cursor FROM observation_checkpoint WHERE root_no=$r AND root_binding=$b;");
        command.Parameters.AddWithValue("$r", RootNo(root.RootId));
        command.Parameters.AddWithValue("$b", JsonSerializer.Serialize(root));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new InventoryObservation(JsonSerializer.Deserialize<InventoryCoverage>(reader.GetString(0))!, reader.GetString(1)) : null;
    });

    internal void SaveInventoryObservation(InventoryCoverage coverage, string cursor) => InStore(() =>
        StoreSchema.Execute(_connection, null,
            "INSERT INTO observation_checkpoint VALUES($r,$b,$s,$c) ON CONFLICT(root_no) DO UPDATE SET root_binding=$b,scope_json=$s,cursor=$c;",
            ("$r", RootNo(coverage.Root.RootId)), ("$b", JsonSerializer.Serialize(coverage.Root)), ("$s", coverage.Json), ("$c", cursor)));

    internal void ForgetInventoryObservation(string root) => InStore(() =>
        StoreSchema.Execute(_connection, null, "DELETE FROM observation_checkpoint WHERE root_no=$r;", ("$r", RootNo(root))));

    internal void AbandonPrefixHydration(RootDef root) => InStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var number = RootNo(root.RootId);
        StoreSchema.Execute(_connection, transaction, "DELETE FROM observation_checkpoint WHERE root_no=$r;", ("$r", number));
        StoreSchema.Execute(_connection, transaction, "DELETE FROM deletion_classification WHERE root_no=$r;", ("$r", number));
        StoreSchema.Execute(_connection, transaction, "UPDATE root SET cursor=NULL WHERE root_no=$r;", ("$r", number));
        transaction.Commit();
    });

    internal void BeginPrefixHydration(string root, CancellationToken ct = default)
    {
        using var admission = FairStoreGate.WithCancellation(ct);
        InStore(() =>
        {
            StoreSchema.Execute(_connection, null, "CREATE TEMP TABLE IF NOT EXISTS hydration_seen(root_no INTEGER NOT NULL,path_key TEXT NOT NULL,path TEXT NOT NULL,object_id TEXT,json TEXT,deleted INTEGER NOT NULL,PRIMARY KEY(root_no,path_key));");
            StoreSchema.Execute(_connection, null, "CREATE INDEX IF NOT EXISTS temp.hydration_object ON hydration_seen(root_no,object_id) WHERE json IS NOT NULL;");
        });
        while (InStore(() =>
        {
            ct.ThrowIfCancellationRequested();
            StoreSchema.Execute(_connection, null, "DELETE FROM hydration_seen WHERE root_no=$r AND path_key IN (SELECT path_key FROM hydration_seen WHERE root_no=$r LIMIT 256);", ("$r", RootNo(root)));
            return StoreSchema.Changes(_connection, null!) != 0;
        })) { }
    }

    internal void StagePrefixHydration(string root, IReadOnlyList<(string Path, ItemState? State)> rows) => InStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var row in rows)
        {
            var item = row.State;
            if (item is not null && (long)StoreSchema.Scalar(_connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM hydration_seen WHERE root_no=$r AND path_key=$k AND json IS NOT NULL AND object_id<>$o);",
                ("$r", RootNo(root)), ("$k", RowCodec.Key(row.Path)), ("$o", item.ObjectId))! != 0)
                throw new InvalidDataException("Dropbox subtree contains colliding live paths.");
            StoreSchema.Execute(_connection, transaction,
                "INSERT INTO hydration_seen VALUES($r,$k,$p,$o,$j,$d) ON CONFLICT(root_no,path_key) DO UPDATE SET path=$p,object_id=COALESCE($o,object_id),json=COALESCE($j,json),deleted=MAX(deleted,$d);",
                ("$r", RootNo(root)), ("$k", RowCodec.Key(row.Path)), ("$p", row.Path),
                ("$o", item?.ObjectId), ("$j", item is null ? null : JsonSerializer.Serialize(item)), ("$d", item is null ? 1 : 0));
        }
        transaction.Commit();
    });

    // Staging is disposable, not a new observation framework. Publish no expanded coverage until
    // this bounded reconciliation and the root race feed have both completed.
    internal void InstallPrefixHydration(RootDef root, Side side, long gen, IReadOnlyList<string> prefixes,
        Func<string, bool> include, string oldCursor, CancellationToken ct = default,
        Action<string>? changedObject = null)
    {
        using var admission = FairStoreGate.WithCancellation(ct);
        ct.ThrowIfCancellationRequested();
        // A scoped repair cannot turn an interrupted/missing root classification into complete authority.
        var hadCompleteClassification = ReadDeletionClassification(root) is not null;
        var number = BeginClassificationWrite(root);
        {
            long afterDir = -1;
            string afterKey = "", afterName = "";
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var rows = InStore(() =>
                {
                    var result = new List<(long Dir, string Name, string Path, string Key)>();
                    using var command = StoreSchema.Command(_connection, null,
                        "SELECT f.dir_no,f.name,CASE WHEN d.path='' THEN f.name ELSE d.path||'/'||f.name END,f.name_key FROM file_state f JOIN dir d ON d.dir_no=f.dir_no WHERE f.root_no=$r AND (f.dir_no,f.name_key,f.name)>($d,$k,$n) ORDER BY f.dir_no,f.name_key,f.name LIMIT 256;");
                    command.Parameters.AddWithValue("$r", number);
                    command.Parameters.AddWithValue("$d", afterDir);
                    command.Parameters.AddWithValue("$k", afterKey);
                    command.Parameters.AddWithValue("$n", afterName);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) result.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                    return result;
                });
                if (rows.Count == 0) break;
                (afterDir, afterKey, afterName) = (rows[^1].Dir, rows[^1].Key, rows[^1].Name);
                InStore(() =>
                {
                    var missing = new List<(long Dir, string Name)>();
                    foreach (var row in rows.Where(row => include(row.Path) && prefixes.Any(prefix => PairPath.IsSelfOrUnder(row.Path, prefix))))
                    {
                        var seen = (long)StoreSchema.Scalar(_connection, null,
                            "SELECT EXISTS(SELECT 1 FROM hydration_seen WHERE root_no=$r AND path_key=$k AND json IS NOT NULL);",
                            ("$r", number), ("$k", RowCodec.Key(row.Path)))!;
                        if (seen == 0) missing.Add((row.Dir, row.Name));
                    }
                    _files.RemoveHydrationRows(number, missing);
                });
            }
        }
        foreach (var prefix in prefixes)
        {
            while (InStore(() =>
            {
                ct.ThrowIfCancellationRequested();
                StoreSchema.Execute(_connection, null,
                    "DELETE FROM deletion_classification_path WHERE root_no=$r AND path_key IN (SELECT path_key FROM deletion_classification_path WHERE root_no=$r AND path_key=$p UNION ALL SELECT path_key FROM deletion_classification_path WHERE root_no=$r AND path_key >= $lo AND path_key < $hi LIMIT 256);",
                    ("$r", number), ("$p", RowCodec.Key(prefix)), ("$lo", RowCodec.Key(prefix) + "/"), ("$hi", RowCodec.Key(prefix) + "0"));
                return StoreSchema.Changes(_connection, null!);
            }) != 0) { }
        }
        string key = "";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = InStore(() =>
            {
                var result = new List<(string Key, string Path, string? Json, bool Deleted)>();
                using var command = StoreSchema.Command(_connection, null,
                    "SELECT path_key,path,json,deleted FROM hydration_seen WHERE root_no=$r AND path_key>$a ORDER BY path_key LIMIT 256;");
                command.Parameters.AddWithValue("$r", number);
                command.Parameters.AddWithValue("$a", key);
                using var reader = command.ExecuteReader();
                while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3) != 0));
                return result;
            });
            if (rows.Count == 0) break;
            key = rows[^1].Key;
            var changes = rows.Where(row => row.Json is not null).Select(row => JsonSerializer.Deserialize<ItemState>(row.Json!)!)
                .Select(item => new Change(ChangeKind.Upsert, item.ObjectId, item.Path, item, null)).ToArray();
            // Every installed identity is part of this observation; the caller's dirty set only widens.
            if (changedObject is not null)
                foreach (var change in changes)
                    if (include(change.Path) && change.ObjectId.Length > 0) changedObject(change.ObjectId);
            WriteDelta(new Delta(root.RootId, side, gen, oldCursor, oldCursor, changes, false, [])
                { IncludesPath = include, ChangedObject = changedObject });
            WriteClassificationRows(number, rows.Where(row => row.Deleted).Select(row => (row.Path, row.Json is null)), true);
        }
        ct.ThrowIfCancellationRequested();
        if (hadCompleteClassification) PublishClassification(root, number, oldCursor);
    }
}
