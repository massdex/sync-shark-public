using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Only changed rows, on disk. Replays the legacy (parent,name) order because hard-link
/// rehash reconciliation and last-tombstone selection have observable cross-row effects.
/// The live/input comparison still traverses their PKs; no root is sorted per page.</summary>
internal sealed class SnapshotChanges : IDisposable
{
    internal sealed record Change(FileStateReader.Entry Entry, ItemState? Incoming, bool WasLive);
    private SqliteConnection? _connection;
    private StoreBatch? _batch;
    private string? _path;

    internal bool HasChanges => _connection is not null;

    internal void Add(FileStateReader.Entry entry, ItemState? incoming, bool wasLive)
    {
        if (_connection is null)
        {
            _path = Path.Combine(Path.GetTempPath(), "syncshark-snapshot-changes-" + Guid.NewGuid().ToString("N") + ".db");
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
            _connection.Open();
            StoreSchema.Execute(_connection, null, "PRAGMA cache_size=-2048;" +
                "CREATE TABLE changes(parent TEXT NOT NULL,name TEXT NOT NULL,json TEXT NOT NULL," +
                "PRIMARY KEY(parent,name)) WITHOUT ROWID;");
            _batch = new StoreBatch(_connection, 1024);
        }
        StoreSchema.Execute(_connection, _batch!.Transaction, "INSERT INTO changes VALUES($p,$n,$j);",
            ("$p", entry.Parent), ("$n", entry.Name),
            ("$j", JsonSerializer.Serialize(new Change(entry, incoming, wasLive), RowCodec.Json)));
        _batch.Step();
    }

    internal IEnumerable<Change> Read()
    {
        if (_connection is null) yield break;
        _batch!.Flush();
        _batch.Dispose();
        _batch = null;
        var parent = "";
        var name = "";
        while (true)
        {
            var page = new List<Change>(256);
            using (var command = StoreSchema.Command(_connection, null,
                "SELECT json FROM changes WHERE (parent,name)>($p,$n) ORDER BY parent,name LIMIT 256;"))
            {
                command.Parameters.AddWithValue("$p", parent);
                command.Parameters.AddWithValue("$n", name);
                using var reader = command.ExecuteReader();
                while (reader.Read()) page.Add(JsonSerializer.Deserialize<Change>(reader.GetString(0), RowCodec.Json)!);
            }
            if (page.Count == 0) yield break;
            parent = page[^1].Entry.Parent;
            name = page[^1].Entry.Name;
            foreach (var change in page) yield return change;
        }
    }

    public void Dispose()
    {
        _batch?.Dispose();
        _connection?.Dispose();
        if (_path is not null) File.Delete(_path);
    }
}
