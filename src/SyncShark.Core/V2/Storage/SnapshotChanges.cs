using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Only changed rows, on disk. Replays the legacy (parent,name) order because hard-link
/// rehash reconciliation and last-tombstone selection have observable cross-row effects.
/// The live/input comparison still traverses their PKs; no root is sorted per page.</summary>
internal sealed class SnapshotChanges(SqliteConnection owner) : IDisposable
{
    internal sealed record Change(FileStateReader.Entry Entry, ItemState? Incoming, bool WasLive);
    private SqliteConnection? _connection;
    private StoreBatch? _batch;
    private SnapshotTemporary? _temporary;
    private SqliteCommand? _insert;

    internal bool HasChanges => _connection is not null;

    internal void Add(FileStateReader.Entry entry, ItemState? incoming, bool wasLive)
    {
        if (_connection is null)
        {
            _temporary = new SnapshotTemporary(owner);
            _connection = _temporary.Connection;
            StoreSchema.Execute(_connection, null,
                "CREATE TABLE changes(parent TEXT NOT NULL,name TEXT NOT NULL,json TEXT NOT NULL," +
                "PRIMARY KEY(parent,name)) WITHOUT ROWID;");
            _batch = new StoreBatch(_connection, int.MaxValue);
            _insert = StoreSchema.Command(_connection, _batch!.Transaction, "INSERT OR IGNORE INTO changes VALUES($p,$n,$j);");
            foreach (var parameter in new[] { "$p", "$n", "$j" }) _insert.Parameters.AddWithValue(parameter, DBNull.Value);
            _insert.Prepare();
        }
        _insert!.Parameters[0].Value = entry.Parent;
        _insert.Parameters[1].Value = entry.Name;
        _insert.Parameters[2].Value = JsonSerializer.Serialize(new Change(entry, wasLive ? incoming : null, wasLive), RowCodec.Json);
        if (_insert.ExecuteNonQuery() == 0)
        {
            var previous = JsonSerializer.Deserialize<Change>((string)StoreSchema.Scalar(_connection, _batch!.Transaction,
                "SELECT json FROM changes WHERE parent=$p AND name=$n;", ("$p", entry.Parent), ("$n", entry.Name))!, RowCodec.Json)!;
            // Runtime case-table changes may order one exact path as an insert plus a removal.
            // Collapse them to a replacement, retaining the old stored key for the delete.
            var live = wasLive ? entry : previous.Entry;
            var replacement = incoming ?? (previous.WasLive ? previous.Incoming : previous.Entry.Item);
            var combined = new Change(live, replacement is null ? null : RowCodec.CarryHash(replacement, live.Item), true);
            StoreSchema.Execute(_connection, _batch!.Transaction,
                "UPDATE changes SET json=$j WHERE parent=$p AND name=$n;", ("$p", entry.Parent), ("$n", entry.Name),
                ("$j", JsonSerializer.Serialize(combined, RowCodec.Json)));
        }

    }

    internal IEnumerable<Change> Read()
    {
        if (_connection is null) yield break;
        _batch!.Flush();
        _batch.Dispose();
        _batch = null;
        using var command = StoreSchema.Command(_connection, null, "SELECT json FROM changes ORDER BY parent,name;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var change = JsonSerializer.Deserialize<Change>(reader.GetString(0), RowCodec.Json)!;
            yield return change.WasLive ? change : change with { Incoming = change.Entry.Item };
        }
    }

    public void Dispose()
    {
        _insert?.Dispose();
        _batch?.Dispose();
        _temporary?.Dispose();
    }
}
