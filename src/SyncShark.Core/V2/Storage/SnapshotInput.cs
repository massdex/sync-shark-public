using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>A disk-backed ordered inventory. Owns only its staging database, never live rows.
/// The object index includes every page and permits hard-link aliases.</summary>
internal sealed class SnapshotInput : IDisposable
{
    private readonly SqliteConnection _stage;
    private readonly SnapshotTemporary _temporary;

    private SnapshotInput(SqliteConnection owner)
    {
        _temporary = new SnapshotTemporary(owner);
        _stage = _temporary.Connection;
        try
        {
            StoreSchema.Execute(_stage, null,
            "CREATE TABLE provisional(path TEXT PRIMARY KEY,dir_no INTEGER NOT NULL); " +
            "CREATE TABLE incoming(dir_no INTEGER NOT NULL,name_key TEXT NOT NULL,name TEXT NOT NULL," +
            "parent TEXT NOT NULL,object_id TEXT NOT NULL,json TEXT NOT NULL," +
            "PRIMARY KEY(dir_no,name_key,name)) WITHOUT ROWID; " +
            "CREATE INDEX incoming_object ON incoming(object_id);");
        }
        catch { _temporary.Dispose(); throw; }
    }

    internal static SnapshotInput Create(SqliteConnection owner, DirTable dirs,
        IEnumerable<ItemState> items, Func<string, bool>? include = null)
    {
        var result = new SnapshotInput(owner);
        try
        {
            using var batch = new StoreBatch(result._stage, 1024);
            var nextDir = (long)StoreSchema.Scalar(owner, null, "SELECT COALESCE(MAX(dir_no),0) FROM dir;")!;

            foreach (var item in items)
            {
                if (!(include?.Invoke(item.Path) ?? true)) continue;
                var (parent, name) = RowCodec.Split(item.Path);
                var dir = dirs.Find(parent);
                if (dir is null)
                {
                    dir = StoreSchema.Scalar(result._stage, batch.Transaction,
                        "SELECT dir_no FROM provisional WHERE path=$p;", ("$p", parent)) as long?;
                    if (dir is null)
                    {
                        dir = ++nextDir;
                        StoreSchema.Execute(result._stage, batch.Transaction,
                            "INSERT INTO provisional VALUES($p,$d);", ("$p", parent), ("$d", dir));
                    }
                }
                try
                {
                    StoreSchema.Execute(result._stage, batch.Transaction,
                        "INSERT INTO incoming VALUES($d,$k,$n,$p,$o,$j);",
                        ("$d", dir), ("$k", RowCodec.Key(name)), ("$n", name), ("$p", parent),
                        ("$o", item.ObjectId), ("$j", JsonSerializer.Serialize(item, RowCodec.Json)));
                }
                catch (SqliteException e) when (e.SqliteErrorCode == 19)
                {
                    throw new ArgumentException($"Duplicate snapshot path '{item.Path}'.", nameof(items), e);
                }
                batch.Step();
            }
            batch.Flush();
            return result;
        }
        catch
        {
            dirs.ClearCache();
            result.Dispose();
            throw;
        }
    }

    internal const string DirectorySql = "SELECT dir_no FROM incoming WHERE dir_no>$d ORDER BY dir_no LIMIT 1;";
    internal const string PageSql = "SELECT name,parent,json,name_key FROM incoming " +
        "WHERE dir_no=$d AND (name_key,name)>($k,$n) ORDER BY name_key,name LIMIT 256;";
    internal const string ObjectSql = "SELECT EXISTS(SELECT 1 FROM incoming WHERE object_id=$o);";

    internal bool ContainsObject(string id) =>
        (long)StoreSchema.Scalar(_stage, null, ObjectSql, ("$o", id))! != 0;

    internal ItemState? At(FileStateReader.Entry row)
    {
        var json = StoreSchema.Scalar(_stage, null,
            "SELECT json FROM incoming WHERE dir_no=$d AND name_key=$k AND name=$n;",
            ("$d", row.DirNo), ("$k", RowCodec.Key(row.Name)), ("$n", row.Name)) as string;
        return json is null ? null : JsonSerializer.Deserialize<ItemState>(json, RowCodec.Json);
    }

    internal IEnumerable<FileStateReader.Entry> Entries() => SnapshotPager.Read(_stage, () => null,
        DirectorySql, PageSql, reader => FileStateReader.Entry.Of(reader.GetString(1), reader.GetString(0),
            JsonSerializer.Deserialize<ItemState>(reader.GetString(2), RowCodec.Json)!) with { NameKey = reader.GetString(3) });

    internal IReadOnlyList<string> Plans() =>
        StoreSchema.QueryPlan(_stage, "IncomingDirectory", DirectorySql, "$d")
            .Concat(StoreSchema.QueryPlan(_stage, "IncomingPage", PageSql, "$d", "$k", "$n"))
            .Concat(StoreSchema.QueryPlan(_stage, "IncomingObject", ObjectSql, "$o")).ToArray();

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
