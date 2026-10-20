using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Writes the live rows of file_state and the local-deletion tombstones.</summary>
internal sealed class FileStateTable
{
    private readonly SqliteConnection _connection;
    private readonly DirTable _dir;
    private readonly FileStateReader _reader;
    private readonly V2Settings _settings;
    private readonly TimeProvider _clock;
    private readonly Func<long, string, SqliteTransaction, bool> _baselined;

    /// <summary>
    /// Decision 59: called inside the same transaction as each written link row, so the baselines under a link are
    /// forgotten atomically with its discovery (no window in which the link is stored but its baselines survive).
    /// </summary>
    internal Action<SqliteTransaction, long, string>? OnLinkWritten { get; set; }

    /// <param name="baselined">Whether an object of a root is a side of some baseline .</param>
    public FileStateTable(SqliteConnection connection, DirTable dir, FileStateReader reader, V2Settings settings,
        TimeProvider clock, Func<long, string, SqliteTransaction, bool> baselined)
    {
        _connection = connection;
        _dir = dir;
        _reader = reader;
        _settings = settings;
        _clock = clock;
        _baselined = baselined;
    }

    /// <summary>Replaces the live set with the snapshot's items, closing anything absent outside incomplete subtrees.</summary>
    public void WriteSnapshot(Snapshot snapshot, long rootNo)
    {
        var incoming = Order(snapshot.Items.Where(item => snapshot.IncludesPath?.Invoke(item.Path) ?? true).ToArray());
        var live = _reader.ReadLive(rootNo);
        var suppressed = incoming.Select(entry => entry.Item.ObjectId).ToHashSet(StringComparer.Ordinal);
        var incomplete = snapshot.IncompleteSubtrees;
        var now = _clock.GetUtcNow();
        using var batch = new StoreBatch(_connection, _settings.StoreBatchRows);
        var left = 0;
        var right = 0;
        while (left < incoming.Count && right < live.Count)
        {
            var order = FileStateReader.Entry.CompareByKey(incoming[left], live[right]);
            if (order == 0)
            {
                var merged = RowCodec.CarryHash(incoming[left].Item, live[right].Item);
                // Keep a moving held entity's old row until its new held row is stored, even if
                // another identity has taken the old name. This survives arbitrary sort/batch order.
                var movingHold = live[right].Item.Flags.HasFlag(ItemFlags.Quarantined) &&
                    live[right].Item.ObjectId != merged.ObjectId && suppressed.Contains(live[right].Item.ObjectId);
                if (!movingHold && !RowCodec.Same(merged, live[right].Item))
                    Step(batch, Replace(live[right], merged, rootNo, suppressed, now, batch));
                left++;
                right++;
            }
            else if (order < 0)
            {
                Step(batch, Insert(incoming[left], rootNo, batch));
                left++;
            }
            else
            {
                if ((snapshot.IncludesPath?.Invoke(live[right].Item.Path) ?? true) &&
                    !InsideIncomplete(live[right].Item.Path, incomplete))
                    Step(batch, RemoveAt(rootNo, live[right].DirNo, live[right].Name, now, suppressed, batch));
                right++;
            }
        }
        for (; left < incoming.Count; left++) Step(batch, Insert(incoming[left], rootNo, batch));
        for (; right < live.Count; right++)
            if ((snapshot.IncludesPath?.Invoke(live[right].Item.Path) ?? true) &&
                    !InsideIncomplete(live[right].Item.Path, incomplete))
                Step(batch, RemoveAt(rootNo, live[right].DirNo, live[right].Name, now, suppressed, batch));
        RetireMovedQuarantineAliases(live, incoming, snapshot, rootNo, suppressed, now, batch);
        batch.Flush();
        // Only after every snapshot batch is durable: a move may remove its old name before inserting the new.
        if (incomplete.Count == 0)
            StoreSchema.Execute(_connection, null,
                "DELETE FROM local_rehash WHERE root_no=$r AND NOT EXISTS " +
                "(SELECT 1 FROM file_state f WHERE f.root_no=local_rehash.root_no AND f.object_id=local_rehash.object_id);",
                ("$r", rootNo));
    }

    // IEnumerable keeps this late pass compatible with paged prior rows when snapshot reads stream.
    // No root-sized hold set: the existing observed IDs and ordered incoming rows prove the move.
    private void RetireMovedQuarantineAliases(IEnumerable<FileStateReader.Entry> previous,
        List<FileStateReader.Entry> incoming, Snapshot snapshot, long rootNo, HashSet<string> observedIds,
        DateTimeOffset now, StoreBatch batch)
    {
        var order = Comparer<FileStateReader.Entry>.Create(FileStateReader.Entry.CompareByKey);
        foreach (var old in previous)
        {
            if (!old.Item.Flags.HasFlag(ItemFlags.Quarantined) || !observedIds.Contains(old.Item.ObjectId) ||
                !(snapshot.IncludesPath?.Invoke(old.Item.Path) ?? true) ||
                InsideIncomplete(old.Item.Path, snapshot.IncompleteSubtrees)) continue;
            var at = incoming.BinarySearch(old, order);
            if (at >= 0 && incoming[at].Item.ObjectId == old.Item.ObjectId) continue;
            // The replacement held row must already exist before the original hold can disappear.
            if (StoreSchema.Scalar(_connection, batch.Transaction,
                "SELECT 1 FROM file_state WHERE root_no=$r AND object_id=$o AND (flags & 128)!=0 " +
                "AND NOT(dir_no=$d AND name=$n) LIMIT 1;", ("$r", rootNo), ("$o", old.Item.ObjectId),
                ("$d", old.DirNo), ("$n", old.Name)) is null ||
                _reader.ReadObjectId(rootNo, old.DirNo, old.Name, batch.Transaction) != old.Item.ObjectId) continue;
            if (at < 0)
                Step(batch, RetireHeldAlias(rootNo, old.DirNo, old.Name, old.Item.ObjectId, batch));
            else
                // Only a proven moved hold stops guarding its old name. Ordinary scan replacements
                // without that complete-snapshot proof retain the existing sticky path protection.
                Step(batch, Replace(old, RowCodec.CarryHash(incoming[at].Item,
                    old.Item with { Flags = old.Item.Flags & ~ItemFlags.Quarantined }), rootNo, observedIds, now, batch));
        }
    }

    /// <summary>Applies a delta's deletes then upserts, each in list order, tombstoning every real deletion.</summary>
    public void WriteDelta(Delta delta, long rootNo)
    {
        var suppressed = delta.Changes.Where(change => change.Kind == ChangeKind.Upsert &&
                (delta.IncludesPath?.Invoke(change.Path) ?? true))
            .Select(change => change.ObjectId).ToHashSet(StringComparer.Ordinal);
        using var batch = new StoreBatch(_connection, _settings.StoreBatchRows);
        foreach (var change in delta.Changes)
            if (change.Kind == ChangeKind.Delete) Delete(change, rootNo, suppressed, batch, delta.IncludesPath);
        var now = _clock.GetUtcNow();
        foreach (var change in delta.Changes)
            if (change.Kind == ChangeKind.Upsert && (delta.IncludesPath?.Invoke(change.Path) ?? true))
                Step(batch, Upsert(change, rootNo, suppressed, now, batch, delta.IncludesPath));
        batch.Flush();
    }

    internal void HoldQuarantine(SqliteTransaction transaction, long rootNo, ItemState item)
    {
        var (parent, name) = RowCodec.Split(item.Path);
        var dir = _dir.Intern(parent, transaction);
        // Retain the observed row when antivirus removal raced inventory. A replacement already at this path
        // retains its own identity/content and is conservatively held, never overwritten by stale metadata.
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,version_id,hash,size,mtime,flags,identity_json) " +
            "VALUES($r,$d,$nk,$n,$k,$o,$v,$h,$s,$m,$f,$i) " +
            "ON CONFLICT(root_no,dir_no,name_key,name) DO UPDATE SET flags=file_state.flags | $hold;",
            ("$r",rootNo), ("$d",dir), ("$nk",RowCodec.Key(name)), ("$n",name), ("$k",RowCodec.KindCode(item.Kind)),
            ("$o",item.ObjectId), ("$v",item.VersionId), ("$h",RowCodec.HashBlob(item.Hash)), ("$s",item.Size),
            ("$m",RowCodec.MtimeTicks(item.Mtime)), ("$f",(long)(item.Flags | ItemFlags.Quarantined)),
            ("$hold",(long)ItemFlags.Quarantined), ("$i",item.IdentityJson));
        StoreSchema.Execute(_connection, transaction, "DELETE FROM tombstone WHERE root_no=$r AND object_id=$o;",
            ("$r",rootNo), ("$o",item.ObjectId));
    }

    // The refusal belongs to the existing item row, and commits atomically with retiring its intent.
    internal bool MarkProviderNameRefused(SqliteTransaction transaction, long rootNo, ItemState source,
        ItemFlags flag = ItemFlags.ProviderNameRefused)
    {
        var (parent, name) = RowCodec.Split(source.Path);
        var dirNo = _dir.Find(parent, transaction);
        if (dirNo is null) return false;
        using var command = StoreSchema.Command(_connection, transaction,
            "UPDATE file_state SET flags=flags | $f WHERE root_no=$r AND dir_no=$d AND name_key=$k AND name=$n AND object_id=$o;");
        command.Parameters.AddWithValue("$f", (long)flag);
        command.Parameters.AddWithValue("$r", rootNo);
        command.Parameters.AddWithValue("$d", dirNo.Value);
        command.Parameters.AddWithValue("$k", RowCodec.Key(name));
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$o", source.ObjectId);
        return command.ExecuteNonQuery() == 1;
    }

    // Inventory still names the pre-write entity until refresh. Clear only the accepted destination,
    // bound to either that expected entity or the returned entity; never clear a moved-away source.
    internal void ClearProviderNameRefused(SqliteTransaction transaction, long rootNo,
        ItemState written, ItemState? expected)
    {
        var (parent, name) = RowCodec.Split(written.Path);
        var dirNo = _dir.Find(parent, transaction);
        if (dirNo is null) return;
        var oldId = expected is not null && string.Equals(expected.Path, written.Path, StringComparison.Ordinal)
            ? expected.ObjectId : written.ObjectId;
        StoreSchema.Execute(_connection, transaction,
            "UPDATE file_state SET flags=flags & ~$f WHERE root_no=$r AND dir_no=$d AND name_key=$k " +
            "AND name=$n AND object_id IN ($o,$old) AND (flags & $f) != 0;",
            ("$f", (long)ItemFlags.ProviderNameRefused), ("$r", rootNo), ("$d", dirNo.Value),
            ("$k", RowCodec.Key(name)), ("$n", name), ("$o", written.ObjectId), ("$old", oldId));
    }

    // A successful folder effect also carries ignored descendants, which have no individual actions.
    // Retire the exact identity-bound subtree in the result transaction, not by a full-root scan.
    internal void RetireDeletedFolder(SqliteTransaction transaction, long rootNo, ItemState expected)
    {
        var deletedAt = _clock.GetUtcNow();
        var change = new Change(ChangeKind.Delete, expected.ObjectId, expected.Path, null, deletedAt);
        foreach (var row in ExactRows(rootNo, change, transaction))
        {
            StoreSchema.Execute(_connection, transaction,
                "DELETE FROM file_state WHERE root_no=$r AND dir_no=$d AND name_key=$k AND name=$n;",
                ("$r", rootNo), ("$d", row.DirNo), ("$k", RowCodec.Key(row.Name)), ("$n", row.Name));
            InsertTombstone(rootNo, row.DirNo, row.Name, row.ObjectId, deletedAt, transaction);
        }
    }

    // A case clash a full scan recorded (Docs/a.txt and Docs/A.txt both live) is kept until evidence for each
    // removes it (RQ-039). Every Delete acts on its exact spelling only, so applying it twice equals applying it
    // once (a replay after a crash never reaches another spelling, B-126): a Delete naming its object removes the
    // row at its exact path only while that row still carries the object (a reused id elsewhere, or a new object
    // at the path, is another incarnation and is untouched), plus a folder's exactly spelled subtree; a Delete
    // without an object id removes the exact spelling and its exact subtree. Choosing among other spellings is
    // the inventory's job, on evidence for that object. Move suppression applies after selection: a row whose
    // object is upserted later in the delta is a move written as Delete(old) + Upsert(new) and is left for that
    // Upsert, which removes it in the same step as the insertion. One step per removed row.
    private void Delete(Change change, long rootNo, HashSet<string> suppressed, StoreBatch batch,
        Func<string, bool>? include)
    {
        var deletedAt = change.DeletedUtc ?? _clock.GetUtcNow();
        foreach (var row in ExactRows(rootNo, change, batch.Transaction))
            if (!suppressed.Contains(row.ObjectId) && (include?.Invoke(RowCodec.Join(row.Path, row.Name)) ?? true))
                Step(batch, RemoveAt(rootNo, row.DirNo, row.Name, deletedAt, suppressed, batch));
    }

    private List<RowRef> ExactRows(long rootNo, Change change, SqliteTransaction transaction)
    {
        var at = _reader.AtKey(rootNo, change.Path, transaction).Where(entry =>
            string.Equals(entry.Item.Path, change.Path, StringComparison.Ordinal) &&
            (change.ObjectId.Length == 0 ||
                string.Equals(entry.Item.ObjectId, change.ObjectId, StringComparison.Ordinal))).ToList();
        // A Delete naming its object reaches under its path only when that object is the folder there. Descendants
        // come first and the folder row last (B-127): batches commit between rows, and a crash after a boundary
        // must leave the folder live so the replayed Delete still finds it and removes the rest.
        var rows = change.ObjectId.Length == 0 || at.Any(entry => entry.Item.Kind == ItemKind.Folder)
            ? Subtree(rootNo, change.Path, transaction).Where(row => IsExactUnder(row.Path, row.Name, change.Path)).ToList()
            : new List<RowRef>();
        rows.AddRange(at.Select(entry => new RowRef(entry.DirNo, entry.Parent, entry.Name, entry.Item.ObjectId)));
        return rows;
    }

    // Every live row in a folder at the path or under it, ignoring case (callers keep the exact spelling).
    private IEnumerable<RowRef> Subtree(long rootNo, string path, SqliteTransaction transaction) =>
        _dir.SelfOrUnder(path, transaction).SelectMany(dir => _reader.ReadObjectIds(rootNo, dir.DirNo, transaction)
            .Select(row => new RowRef(dir.DirNo, dir.Path, row.Name, row.ObjectId)));

    private readonly record struct RowRef(long DirNo, string Path, string Name, string ObjectId);

    // Whether a row (its folder path and name) is the path itself or under it, spelled exactly.
    private static bool IsExactUnder(string dirPath, string name, string path)
    {
        var rowPath = RowCodec.Join(dirPath, name);
        return string.Equals(rowPath, path, StringComparison.Ordinal) ||
            rowPath.StartsWith(path + "/", StringComparison.Ordinal);
    }

    // One step (one transaction) per upsert. The object's old row (a move, or a case-only rename) goes. An existing
    // row at the exact path is updated or replaced in place; other spellings are never touched then. Without one, a
    // single other-spelling row of another object is a replacement that changed case (Docs/file.txt by
    // docs/File.txt) and is displaced with its evidence; a known clash (two or more spellings) is kept (RQ-039).
    private bool Upsert(Change change, long rootNo, HashSet<string> suppressed, DateTimeOffset now, StoreBatch batch,
        Func<string, bool>? include)
    {
        var state = change.State
            ?? throw new ArgumentException("An upsert change must carry its state.", nameof(change));
        if (!string.Equals(state.Path, change.Path, StringComparison.Ordinal))
            throw new ArgumentException("An upsert change's state must be at its path.", nameof(change));
        state = ReconcileDebt(state, rootNo, batch.Transaction);
        var (parent, name) = RowCodec.Split(state.Path);
        var target = _dir.Find(parent, batch.Transaction);
        var atKey = _reader.AtKey(rootNo, state.Path, batch.Transaction);
        var wrote = false;
        var objectRows = _reader.ReadObject(rootNo, state.ObjectId, batch.Transaction);
        // Multiple local file names may be hard-link aliases, not successive positions of a move.
        // Provider objects and folders still use the existing move cleanup semantics.
        var aliases = objectRows.Count > 1 && state.Kind == ItemKind.File &&
            state.ObjectId.StartsWith("id:local:", StringComparison.Ordinal);
        foreach (var moved in objectRows)
            if (!aliases && !IsExact(moved.DirNo, moved.Name, target, name) && (include?.Invoke(moved.Path) ?? true))
            {
                DeleteRow(rootNo, moved.DirNo, moved.Name, batch);
                wrote = true;
            }
        var exactAt = atKey.FindIndex(at => IsExact(at.DirNo, at.Name, target, name));
        FileStateReader.Entry? live = exactAt < 0 ? null : atKey[exactAt];
        if (live is null && atKey.Count == 1 && atKey[0].Item.ObjectId != state.ObjectId)
            wrote |= RemoveAt(rootNo, atKey[0].DirNo, atKey[0].Name, now, suppressed, batch);
        if (live is null) return Insert(FileStateReader.Entry.Of(parent, name, state), rootNo, batch, debtReconciled: true);
        var merged = RowCodec.CarryHash(state, live.Value.Item);
        if (RowCodec.Same(merged, live.Value.Item)) return wrote;
        return Replace(live.Value, merged, rootNo, suppressed, now, batch);
    }

    private static bool IsExact(long dirNo, string rowName, long? target, string name) =>
        target == dirNo && string.Equals(rowName, name, StringComparison.Ordinal);

    private static void Step(StoreBatch batch, bool wrote)
    {
        if (wrote) batch.Step();
    }

    private bool RemoveAt(long rootNo, long dirNo, string name, DateTimeOffset deletedAt, HashSet<string> suppressed,
        StoreBatch batch)
    {
        if (StoreSchema.Scalar(_connection, batch.Transaction,
            "SELECT object_id FROM file_state WHERE root_no=$r AND dir_no=$d AND name_key=$k AND name=$n AND (flags & $f)=0;",
            ("$r", rootNo), ("$d", dirNo), ("$k", RowCodec.Key(name)), ("$n", name),
            ("$f", (long)ItemFlags.Quarantined)) is not string objectId) return false;
        DeleteRow(rootNo, dirNo, name, batch);
        if (!suppressed.Contains(objectId)) InsertTombstone(rootNo, dirNo, name, objectId, deletedAt, batch);
        return true;
    }

    // Only called after a held alias of this identity is durably present at its observed name.
    private bool RetireHeldAlias(long rootNo, long dirNo, string name, string objectId, StoreBatch batch)
    {
        StoreSchema.Execute(_connection, batch.Transaction,
            "DELETE FROM file_state WHERE root_no=$r AND dir_no=$d AND name=$n AND object_id=$o AND (flags & $f)!=0;",
            ("$r", rootNo), ("$d", dirNo), ("$n", name), ("$o", objectId), ("$f", (long)ItemFlags.Quarantined));
        return StoreSchema.Changes(_connection, batch.Transaction) != 0;
    }

    internal void RemoveHydrationRows(long rootNo, IEnumerable<(long Dir, string Name)> rows)
    {
        using var batch = new StoreBatch(_connection, _settings.StoreBatchRows);
        foreach (var (dirNo, name) in rows)
        {
            var objectId = _reader.ReadObjectId(rootNo, dirNo, name, batch.Transaction);
            if (objectId is not null && (long)StoreSchema.Scalar(_connection, batch.Transaction,
                "SELECT EXISTS(SELECT 1 FROM hydration_seen WHERE root_no=$r AND object_id=$o AND json IS NOT NULL);",
                ("$r", rootNo), ("$o", objectId))! == 0)
                Step(batch, RemoveAt(rootNo, dirNo, name, _clock.GetUtcNow(), [], batch));
        }
        batch.Flush();
    }

    // A different object displacing the live one is a deletion of the displaced object: keep its evidence.
    private bool Replace(FileStateReader.Entry live, ItemState item, long rootNo, HashSet<string> suppressed,
        DateTimeOffset deletedAt, StoreBatch batch)
    {
        item = ReconcileDebt(item, rootNo, batch.Transaction);
        DeleteRow(rootNo, live.DirNo, live.Name, batch);
        var displaced = live.Item.ObjectId;
        if (!string.Equals(displaced, item.ObjectId, StringComparison.Ordinal) && !suppressed.Contains(displaced))
            InsertTombstone(rootNo, live.DirNo, live.Name, displaced, deletedAt, batch);
        return Insert(FileStateReader.Entry.Of(live.Parent, live.Name, item), rootNo, batch, debtReconciled: true);
    }

    private ItemState ReconcileDebt(ItemState item, long rootNo, SqliteTransaction tx)
    {
        // Both protections belong to the root/object, not its old name. Read before move cleanup.
        var protection = Convert.ToInt64(StoreSchema.Scalar(_connection, tx,
            "SELECT EXISTS(SELECT 1 FROM local_rehash WHERE root_no=$r AND object_id=$o) | " +
            "(128 * EXISTS(SELECT 1 FROM file_state WHERE root_no=$r AND object_id=$o AND (flags & 128)!=0));",
            ("$r", rootNo), ("$o", item.ObjectId)));
        if ((protection & 128) != 0) item = item with { Flags = item.Flags | ItemFlags.Quarantined };
        var debt = (protection & 1) != 0;
        // Only current exact-path identity evidence can release metadata-preserving-write debt.
        // If a rename already removed that row, retain the debt for the after-write verification pass.
        if (debt && _reader.AtKey(rootNo, item.Path, tx).Any(row => row.Item.Path == item.Path &&
            row.Item.ObjectId == item.ObjectId && (row.Item.Size != item.Size || row.Item.Mtime != item.Mtime)))
        {
            StoreSchema.Execute(_connection, tx,
                "UPDATE file_state SET flags=flags & ~256,hash=NULL WHERE root_no=$r AND object_id=$o;",
                ("$r", rootNo), ("$o", item.ObjectId));
            // Keep the queue obligation until the changed entity has been planned and settled.
            debt = false;
            item = item with { Hash = null };
        }
        if (debt && (StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM file_state WHERE root_no=$r AND object_id=$o LIMIT 1;",
            ("$r", rootNo), ("$o", item.ObjectId)) is null || StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM file_state WHERE root_no=$r AND object_id=$o AND (flags & 256)!=0 LIMIT 1;",
            ("$r", rootNo), ("$o", item.ObjectId)) is not null))
            return item with { Hash = null, Flags = item.Flags | ItemFlags.NeedsRehash };
        return (item.Flags & ItemFlags.NeedsRehash) == 0 ? item :
            item with { Hash = null, Flags = item.Flags & ~ItemFlags.NeedsRehash };
    }

    private bool Insert(FileStateReader.Entry entry, long rootNo, StoreBatch batch, bool debtReconciled = false)
    {
        var dirNo = _dir.Intern(entry.Parent, batch.Transaction);
        if (!debtReconciled)
            entry = FileStateReader.Entry.Of(entry.Parent, entry.Name, ReconcileDebt(entry.Item, rootNo, batch.Transaction));
        StoreSchema.Execute(_connection, batch.Transaction,
            "INSERT INTO file_state(root_no, dir_no, name_key, name, kind, object_id, version_id, hash, size, mtime, flags, " +
            "identity_json) VALUES($r, $d, $nk, $n, $k, $o, $v, $h, $s, $m, $f, $i);",
            ("$r", rootNo), ("$d", dirNo), ("$nk", RowCodec.Key(entry.Name)), ("$n", entry.Name),
            ("$k", RowCodec.KindCode(entry.Item.Kind)),
            ("$o", entry.Item.ObjectId), ("$v", entry.Item.VersionId),
            ("$h", RowCodec.HashBlob(entry.Item.Hash)), ("$s", entry.Item.Size),
            ("$m", RowCodec.MtimeTicks(entry.Item.Mtime)), ("$f", (long)entry.Item.Flags),
            ("$i", entry.Item.IdentityJson));
        ClearTombstone(rootNo, entry.Item.ObjectId, batch);
        if ((entry.Item.Flags & ItemFlags.Link) != 0)
            OnLinkWritten?.Invoke(batch.Transaction, rootNo, RowCodec.Join(entry.Parent, entry.Name));
        return true;
    }

    private void DeleteRow(long rootNo, long dirNo, string name, StoreBatch batch) =>
        StoreSchema.Execute(_connection, batch.Transaction,
            "DELETE FROM file_state WHERE root_no=$r AND dir_no=$d AND name_key=$k AND name=$n;",
            ("$r", rootNo), ("$d", dirNo), ("$k", RowCodec.Key(name)), ("$n", name));

    // Keyed by object: a later deletion at the same path never overwrites an unresolved tombstone (decision 19).
    // Written only for an object that has a baseline: the Planner's deletion evidence has no other reader (49).
    private void InsertTombstone(long rootNo, long dirNo, string name, string objectId, DateTimeOffset deletedAt,
        StoreBatch batch) => InsertTombstone(rootNo, dirNo, name, objectId, deletedAt, batch.Transaction);

    private void InsertTombstone(long rootNo, long dirNo, string name, string objectId, DateTimeOffset deletedAt,
        SqliteTransaction transaction)
    {
        if (!_baselined(rootNo, objectId, transaction)) return;
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO tombstone(root_no, object_id, dir_no, name, deleted_at) VALUES($r, $o, $d, $n, $t) " +
            "ON CONFLICT(root_no, object_id) DO UPDATE SET dir_no=excluded.dir_no, name=excluded.name, " +
            "deleted_at=excluded.deleted_at;",
            ("$r", rootNo), ("$d", dirNo), ("$n", name), ("$o", objectId), ("$t", deletedAt.ToUnixTimeMilliseconds()));
    }

    private void ClearTombstone(long rootNo, string objectId, StoreBatch batch) =>
        StoreSchema.Execute(_connection, batch.Transaction,
            "DELETE FROM tombstone WHERE root_no=$r AND object_id=$o;", ("$r", rootNo), ("$o", objectId));

    private static List<FileStateReader.Entry> Order(IReadOnlyList<ItemState> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<FileStateReader.Entry>(items.Count);
        foreach (var item in items)
        {
            if (!seen.Add(item.Path)) throw new ArgumentException($"Duplicate snapshot path '{item.Path}'.", nameof(items));
            var (parent, name) = RowCodec.Split(item.Path);
            entries.Add(FileStateReader.Entry.Of(parent, name, item));
        }
        entries.Sort(FileStateReader.Entry.CompareByKey);
        return entries;
    }

    // "" is the whole root; otherwise the shared self-or-under rule (standards audit B2: no local copy of it).
    private static bool InsideIncomplete(string path, IReadOnlyList<string> subtrees) =>
        subtrees.Any(prefix => prefix.Length == 0 || PairPath.IsSelfOrUnder(path, prefix));
}
