using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Disk-backed changed metadata only. Each rehearsal session owns a fresh file inside its isolated data root.</summary>
internal sealed class RehearsalOverlay : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private long _version;
    internal long Version { get { lock (_gate) return _version; } }
    internal RehearsalOverlay(string path)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE overlay(root TEXT NOT NULL,path TEXT COLLATE NOCASE NOT NULL,state TEXT,seq INTEGER NOT NULL,display TEXT NOT NULL,objectid TEXT NOT NULL,PRIMARY KEY(root,path)) WITHOUT ROWID; CREATE INDEX overlay_seq ON overlay(root,seq);";
        command.ExecuteNonQuery();
    }
    internal bool Try(string root, string path, out ItemState? state)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT state FROM overlay WHERE root=$root AND path=$path";
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$path", PathKey.Of(path));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { state = null; return false; }
            state = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<ItemState>(reader.GetString(0));
            return true;
        }
    }
    internal void Save(string root, string path, ItemState? state, ItemState? victim = null)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO overlay(root,path,state,seq,display,objectid) VALUES($root,$path,$state,$seq,$display,$object) ON CONFLICT(root,path) DO UPDATE SET state=excluded.state,seq=excluded.seq,display=excluded.display,objectid=excluded.objectid";
            command.Parameters.AddWithValue("$seq", ++_version);
            command.Parameters.AddWithValue("$display", path);
            command.Parameters.AddWithValue("$object", state?.ObjectId ?? victim?.ObjectId ?? "");
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$path", PathKey.Of(path));
            command.Parameters.AddWithValue("$state", state is null ? DBNull.Value : JsonSerializer.Serialize(state));
            command.ExecuteNonQuery();
        }
    }
    internal IEnumerable<ItemState> Items(string root, string? subtree = null)
    {
        var prefix = subtree is null ? null : PathKey.Of(subtree);
        string? after = null;
        while (true)
        {
            var page = new List<(string Path, ItemState? State)>();
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = prefix is null or ""
                    ? "SELECT path,state FROM overlay WHERE root=$root AND ($after IS NULL OR path>$after COLLATE NOCASE) ORDER BY path COLLATE NOCASE LIMIT 256"
                    : "SELECT path,state FROM overlay WHERE root=$root AND path >= $prefix COLLATE NOCASE AND path < $upper COLLATE NOCASE AND (path=$prefix COLLATE NOCASE OR path >= $lower COLLATE NOCASE) AND ($after IS NULL OR path>$after COLLATE NOCASE) ORDER BY path COLLATE NOCASE LIMIT 256";
                if (prefix is not null and not "")
                {
                    command.Parameters.AddWithValue("$prefix", prefix);
                    command.Parameters.AddWithValue("$lower", prefix + "/");
                    command.Parameters.AddWithValue("$upper", prefix + "0");
                }
                command.Parameters.AddWithValue("$root", root);
                command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
                using var reader = command.ExecuteReader();
                while (reader.Read()) page.Add((reader.GetString(0), reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<ItemState>(reader.GetString(1))));
            }
            foreach (var item in page) if (item.State is not null) yield return item.State;
            if (page.Count < 256) yield break;
            after = page[^1].Path;
        }
    }
    internal IEnumerable<Change> Since(string root, long after, long through)
    {
        while (true)
        {
            var page = new List<(long Sequence, Change Change)>();
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT seq,display,objectid,state FROM overlay WHERE root=$root AND seq>$after AND seq<=$through ORDER BY seq LIMIT 256";
                command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$through", through);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var state = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<ItemState>(reader.GetString(3));
                    page.Add((reader.GetInt64(0), new Change(state is null ? ChangeKind.Delete : ChangeKind.Upsert,
                        reader.GetString(2), reader.GetString(1), state, null)));
                }
            }
            foreach (var row in page) yield return row.Change;
            if (page.Count < 256) yield break;
            after = page[^1].Sequence;
        }
    }
    public void Dispose() => _connection.Dispose();
}
