using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>A disk-backed ordered inventory. Owns only its staging database, never live rows.
/// The object index includes every page and permits hard-link aliases.</summary>
internal sealed class SnapshotInput : IDisposable
{
    private readonly SqliteConnection _stage;
    private readonly SnapshotTemporary _temporary;
    private readonly IReadOnlyList<ItemState>? _items;
    private bool _objectIndex;

    private SnapshotInput(SqliteConnection owner, IEnumerable<ItemState> items)
    {
        // These concrete scanner containers have constant-time indexing. Keep their existing immutable
        // item references; a genuinely streamed source instead gets a fully disk-backed typed payload.
        _items = items switch { ItemState[] array => array, List<ItemState> list => list, _ => null };
        _temporary = new SnapshotTemporary(owner);
        _stage = _temporary.Connection;
        try
        {
            StoreSchema.Execute(_stage, null,
            "CREATE TABLE provisional(path TEXT PRIMARY KEY,dir_no INTEGER NOT NULL); " +
            "CREATE TABLE incoming(dir_no INTEGER NOT NULL,name_key TEXT NOT NULL,name TEXT NOT NULL," +
            "parent TEXT NOT NULL,object_id TEXT NOT NULL,kind INTEGER NOT NULL DEFAULT 0,version TEXT,hash TEXT," +
            "size INTEGER NOT NULL DEFAULT 0,mtime INTEGER NOT NULL DEFAULT 0,flags INTEGER NOT NULL DEFAULT 0," +
            "identity_json TEXT,retained INTEGER NOT NULL DEFAULT 0,ordinal INTEGER NOT NULL DEFAULT -1," +
            "PRIMARY KEY(dir_no,name_key,name)) WITHOUT ROWID;");
        }
        catch { _temporary.Dispose(); throw; }
    }

    internal static SnapshotInput Create(SqliteConnection owner, DirTable dirs,
        IEnumerable<ItemState> items, Func<string, bool>? include = null, SqliteTransaction? reading = null)
    {
        var result = new SnapshotInput(owner, items);
        try
        {
            using var transaction = result._stage.BeginTransaction();
            var references = result._items is not null;
            using var insert = StoreSchema.Command(result._stage, transaction, references
                ? "INSERT INTO incoming(dir_no,name_key,name,parent,object_id,ordinal) VALUES($d,$k,$n,$p,$o,$ordinal);"
                : "INSERT INTO incoming VALUES($d,$k,$n,$p,$o,$kind,$v,$h,$s,$m,$f,$i,$retained,-1);");
            foreach (var parameter in references ? new[] { "$d", "$k", "$n", "$p", "$o", "$ordinal" }
                : new[] { "$d", "$k", "$n", "$p", "$o", "$kind", "$v", "$h", "$s", "$m", "$f", "$i", "$retained" })
                insert.Parameters.AddWithValue(parameter, DBNull.Value);
            insert.Prepare();
            var nextDir = (long)StoreSchema.Scalar(owner, reading, "SELECT COALESCE(MAX(dir_no),0) FROM dir;")!;
            var folders = new Dictionary<string, long>(StringComparer.Ordinal);
            var ordinal = -1;
            foreach (var item in items)
            {
                ordinal++;
                if (!(include?.Invoke(item.Path) ?? true)) continue;
                var (parent, name) = RowCodec.Split(item.Path);
                if (!folders.TryGetValue(parent, out var dir))
                {
                    var existing = dirs.Find(parent, reading) ?? StoreSchema.Scalar(result._stage, transaction,
                        "SELECT dir_no FROM provisional WHERE path=$p;", ("$p", parent)) as long?;
                    dir = existing ?? ++nextDir;
                    if (existing is null)
                        StoreSchema.Execute(result._stage, transaction,
                            "INSERT INTO provisional VALUES($p,$d);", ("$p", parent), ("$d", dir));
                    if (folders.Count == 1024) folders.Clear();
                    folders[parent] = dir;
                }
                insert.Parameters[0].Value = dir;
                insert.Parameters[1].Value = RowCodec.Key(name);
                insert.Parameters[2].Value = name;
                insert.Parameters[3].Value = parent;
                insert.Parameters[4].Value = item.ObjectId;
                if (references) insert.Parameters[5].Value = ordinal;
                else
                {
                    insert.Parameters[5].Value = RowCodec.KindCode(item.Kind);
                    insert.Parameters[6].Value = (object?)item.VersionId ?? DBNull.Value;
                    insert.Parameters[7].Value = (object?)item.Hash ?? DBNull.Value;
                    insert.Parameters[8].Value = item.Size;
                    insert.Parameters[9].Value = item.Mtime.UtcTicks;
                    insert.Parameters[10].Value = (long)item.Flags;
                    insert.Parameters[11].Value = (object?)item.IdentityJson ?? DBNull.Value;
                    insert.Parameters[12].Value = item.ProviderRefusalRetained ? 1 : 0;
                }
                try { insert.ExecuteNonQuery(); }
                catch (SqliteException e) when (e.SqliteErrorCode == 19)
                {
                    throw new ArgumentException($"Duplicate snapshot path '{item.Path}'.", nameof(items), e);
                }
            }
            transaction.Commit();
            return result;
        }
        catch
        {
            dirs.ClearCache();
            result.Dispose();
            throw;
        }
    }

    private const string Columns = "name,parent,name_key,dir_no,object_id,kind,version,hash,size,mtime,flags,identity_json,retained,ordinal";
    internal const string StreamSql = "SELECT " + Columns + " FROM incoming ORDER BY dir_no,name_key,name;";
    internal const string ObjectSql = "SELECT EXISTS(SELECT 1 FROM incoming WHERE object_id=$o);";

    internal void PrepareObjectLookup()
    {
        if (_objectIndex) return;
        StoreSchema.Execute(_stage, null, "CREATE INDEX incoming_object ON incoming(object_id);");
        _objectIndex = true;
    }

    internal bool ContainsObject(string id)
    {
        PrepareObjectLookup();
        return (long)StoreSchema.Scalar(_stage, null, ObjectSql, ("$o", id))! != 0;
    }

    internal ItemState? At(FileStateReader.Entry row)
    {
        using var command = StoreSchema.Command(_stage, null,
            "SELECT " + Columns + " FROM incoming WHERE dir_no=$d AND name_key=$k AND name=$n;");
        command.Parameters.AddWithValue("$d", row.DirNo);
        command.Parameters.AddWithValue("$k", RowCodec.Key(row.Name));
        command.Parameters.AddWithValue("$n", row.Name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Decode(reader).Item : null;
    }

    private FileStateReader.Entry Decode(SqliteDataReader reader)
    {
        var parent = reader.GetString(1);
        var name = reader.GetString(0);
        var path = RowCodec.Join(parent, name);
        ItemState item;
        if (_items is not null)
        {
            var ordinal = reader.GetInt32(13);
            if ((uint)ordinal >= (uint)_items.Count) throw new InvalidOperationException("Snapshot input changed during publication.");
            item = _items[ordinal];
            // A caller must keep its collection stable during this synchronous call. Fail atomically
            // rather than publish a different path/entity if it replaced or reordered its input.
            if (item.Path != path || item.ObjectId != reader.GetString(4))
                throw new InvalidOperationException("Snapshot input changed during publication.");
        }
        else item = new ItemState(path, RowCodec.KindFrom(reader.GetInt64(5)),
            reader.GetString(4), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8), RowCodec.MtimeFrom(reader.GetInt64(9)),
            (ItemFlags)reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetString(11))
            { ProviderRefusalRetained = reader.GetInt64(12) != 0 };
        return FileStateReader.Entry.Of(parent, name, item, reader.GetInt64(3)) with { NameKey = reader.GetString(2) };
    }

    internal IEnumerable<FileStateReader.Entry> Entries() => SnapshotRows.Read(_stage, null, StreamSql, Decode);

    internal void PreparePathOrder() => StoreSchema.Execute(_stage, null,
        "CREATE INDEX incoming_path ON incoming(parent,name);");

    // An empty live root needs no second changes database. Keep the same raw-path replay order.
    internal IEnumerable<FileStateReader.Entry> PathEntries() => SnapshotRows.Read(_stage, null,
        "SELECT " + Columns + " FROM incoming ORDER BY parent,name;", Decode);

    internal IReadOnlyList<string> Plans()
    {
        PrepareObjectLookup();
        return StoreSchema.QueryPlan(_stage, "IncomingStream", StreamSql)
            .Concat(StoreSchema.QueryPlan(_stage, "IncomingObject", ObjectSql, "$o")).ToArray();
    }

    internal static int Compare(FileStateReader.Entry left, FileStateReader.Entry right)
    {
        var order = left.DirNo.CompareTo(right.DirNo);
        if (order != 0) return order;
        order = PathOrder.Instance.Compare(left.NameKey, right.NameKey);
        return order != 0 ? order : PathOrder.Instance.Compare(left.Name, right.Name);
    }

    public void Dispose()
    {
        _temporary.Dispose();
    }
}
