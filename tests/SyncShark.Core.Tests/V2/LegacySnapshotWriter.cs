using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

// Frozen 82a47890 snapshot writer, used only as the differential oracle.
internal sealed class LegacySnapshotWriter
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
    public LegacySnapshotWriter(SqliteConnection connection, DirTable dir, FileStateReader reader, V2Settings settings,
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
                if (!RowCodec.Same(merged, live[right].Item))
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
        batch.Flush();
        // Only after every snapshot batch is durable: a move may remove its old name before inserting the new.
        if (incomplete.Count == 0)
            StoreSchema.Execute(_connection, null,
                "DELETE FROM local_rehash WHERE root_no=$r AND NOT EXISTS " +
                "(SELECT 1 FROM file_state f WHERE f.root_no=local_rehash.root_no AND f.object_id=local_rehash.object_id);",
                ("$r", rootNo));
    }

    private static void Step(StoreBatch batch, bool wrote)
    {
        if (wrote) batch.Step();
    }

    private bool RemoveAt(long rootNo, long dirNo, string name, DateTimeOffset deletedAt, HashSet<string> suppressed,
        StoreBatch batch)
    {
        if (_reader.ReadObjectId(rootNo, dirNo, name, batch.Transaction) is not string objectId) return false;
        DeleteRow(rootNo, dirNo, name, batch);
        if (!suppressed.Contains(objectId)) InsertTombstone(rootNo, dirNo, name, objectId, deletedAt, batch);
        return true;
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
        var debt = StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM local_rehash WHERE root_no=$r AND object_id=$o;",
            ("$r", rootNo), ("$o", item.ObjectId)) is not null;
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
