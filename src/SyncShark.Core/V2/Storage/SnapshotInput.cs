using System.Collections;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>A disk-backed ordered inventory. Owns only its staging database, never live rows.
/// Reuse it for replay; the object index includes every page and permits hard-link aliases.</summary>
internal sealed class SnapshotInput : IReadOnlyList<ItemState>, IDisposable
{
    private readonly SqliteConnection _owner;
    private readonly SqliteConnection _stage;
    private readonly Func<string, bool>? _include;
    private readonly string _path;
    public int Count { get; private set; }

    private SnapshotInput(SqliteConnection owner, Func<string, bool>? include)
    {
        _owner = owner;
        _include = include;
        _path = Path.Combine(Path.GetTempPath(), "syncshark-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
        _stage = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        _stage.Open();
        StoreSchema.Execute(_stage, null,
            "PRAGMA journal_mode=DELETE; PRAGMA cache_size=-2048; " +
            "CREATE TABLE incoming(dir_no INTEGER NOT NULL,name_key TEXT NOT NULL,name TEXT NOT NULL," +
            "parent TEXT NOT NULL,object_id TEXT NOT NULL,json TEXT NOT NULL," +
            "PRIMARY KEY(dir_no,name_key,name)) WITHOUT ROWID; " +
            "CREATE INDEX incoming_object ON incoming(object_id);");
    }

    internal bool IsFor(SqliteConnection owner, Func<string, bool>? include) =>
        ReferenceEquals(owner, _owner) && Equals(include, _include);

    internal static SnapshotInput Create(SqliteConnection owner, DirTable dirs,
        IEnumerable<ItemState> items, Func<string, bool>? include = null)
    {
        var result = new SnapshotInput(owner, include);
        try
        {
            using var batch = new StoreBatch(result._stage, 1024);
            using var directoryBatch = new StoreBatch(owner, 1024);
            foreach (var item in items)
            {
                if (!(include?.Invoke(item.Path) ?? true)) continue;
                var (parent, name) = RowCodec.Split(item.Path);
                var dir = dirs.Intern(parent, directoryBatch.Transaction);
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
                result.Count++;
                batch.Step();
                directoryBatch.Step();
            }
            batch.Flush();
            directoryBatch.Flush();
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
    internal const string PageSql = "SELECT name,parent,json FROM incoming " +
        "WHERE dir_no=$d AND (name_key,name)>($k,$n) ORDER BY name_key,name LIMIT 256;";
    internal const string ObjectSql = "SELECT EXISTS(SELECT 1 FROM incoming WHERE object_id=$o);";

    internal bool ContainsObject(string id) =>
        (long)StoreSchema.Scalar(_stage, null, ObjectSql, ("$o", id))! != 0;

    internal IEnumerable<FileStateReader.Entry> Entries()
    {
        long dir = -1;
        while (StoreSchema.Scalar(_stage, null, DirectorySql, ("$d", dir)) is long next)
        {
            dir = next;
            var key = "";
            var name = "";
            var first = true;
            while (true)
            {
                var page = new List<FileStateReader.Entry>(256);
                using (var command = StoreSchema.Command(_stage, null, first ? PageSql.Replace(")>(", ")>=(", StringComparison.Ordinal) : PageSql))
                {
                    command.Parameters.AddWithValue("$d", dir);
                    command.Parameters.AddWithValue("$k", key);
                    command.Parameters.AddWithValue("$n", name);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) page.Add(FileStateReader.Entry.Of(reader.GetString(1), reader.GetString(0),
                        JsonSerializer.Deserialize<ItemState>(reader.GetString(2), RowCodec.Json)!, dir));
                }
                if (page.Count == 0) break;
                first = false;
                key = RowCodec.Key(page[^1].Name);
                name = page[^1].Name;
                foreach (var entry in page) yield return entry;
            }
        }
    }

    internal IReadOnlyList<string> Plans() =>
        StoreSchema.QueryPlan(_stage, "IncomingDirectory", DirectorySql, "$d")
            .Concat(StoreSchema.QueryPlan(_stage, "IncomingPage", PageSql, "$d", "$k", "$n"))
            .Concat(StoreSchema.QueryPlan(_stage, "IncomingObject", ObjectSql, "$o")).ToArray();

    internal static int Compare(FileStateReader.Entry left, FileStateReader.Entry right)
    {
        var order = left.DirNo.CompareTo(right.DirNo);
        if (order != 0) return order;
        order = PathOrder.Instance.Compare(RowCodec.Key(left.Name), RowCodec.Key(right.Name));
        return order != 0 ? order : PathOrder.Instance.Compare(left.Name, right.Name);
    }

    public ItemState this[int index] => index < 0 || index >= Count
        ? throw new ArgumentOutOfRangeException(nameof(index)) : this.Skip(index).First();
    public IEnumerator<ItemState> GetEnumerator() => Entries().Select(entry => entry.Item).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        _stage.Dispose();
        File.Delete(_path);
    }
}
