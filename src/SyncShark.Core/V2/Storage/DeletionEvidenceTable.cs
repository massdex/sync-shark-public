using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Cursor-bound probe results; both positive and negative rows require feed validation before reuse.</summary>
internal sealed class DeletionEvidenceTable
{
    private readonly SqliteConnection _connection;
    private readonly int _batchRows;

    public DeletionEvidenceTable(SqliteConnection connection, int batchRows)
    {
        _connection = connection;
        _batchRows = batchRows;
    }

    // Cursor text is opaque. Return oldest checkpoints first so validation drains one oldest feed,
    // conservatively covering later checkpoints without issuing a request per cursor group.
    public IReadOnlyList<DeletionEvidenceRecord> ReadAll(long pairNo, long rootNo)
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT path, deleted_at, basis_cursor, content_hash, failure_json FROM deletion_evidence WHERE pair_no=$p AND root_no=$r " +
            "ORDER BY probed_at, path_key;");
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$r", rootNo);
        using var reader = command.ExecuteReader();
        var rows = new List<DeletionEvidenceRecord>();
        while (reader.Read()) rows.Add(new(reader.GetString(0),
            reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : System.Text.Json.JsonSerializer.Deserialize<DeletionEvidenceFailure>(reader.GetString(4))));
        return rows;
    }

    public void Remove(long pairNo, long rootNo, IReadOnlyList<string> paths)
    {
        using var batch = new StoreBatch(_connection, _batchRows);
        foreach (var path in paths)
        {
            StoreSchema.Execute(_connection, batch.Transaction,
                "DELETE FROM deletion_evidence WHERE pair_no=$p AND root_no=$r AND path_key=$k;",
                ("$p", pairNo), ("$r", rootNo), ("$k", PathKey.Of(path)));
            batch.Step();
        }
        batch.Flush();
    }

    /// <summary>Stores a durable checkpoint in bounded transactions; later probes replace earlier rows.</summary>
    public void Store(long pairNo, long rootNo, IReadOnlyList<DeletionEvidenceRecord> results, long now)
    {
        // Millisecond clock ties (or a clock adjustment) must not order a newer cursor before an
        // older checkpoint. Give every store operation a strictly increasing per-root checkpoint age.
        using (var age = StoreSchema.Command(_connection, null,
            "SELECT MAX(probed_at) FROM deletion_evidence WHERE pair_no=$p AND root_no=$r;"))
        {
            age.Parameters.AddWithValue("$p", pairNo);
            age.Parameters.AddWithValue("$r", rootNo);
            if (age.ExecuteScalar() is long previous) now = Math.Max(now, checked(previous + 1));
        }
        using var batch = new StoreBatch(_connection, _batchRows);
        foreach (var record in results)
        {
            StoreSchema.Execute(_connection, batch.Transaction,
                "INSERT INTO deletion_evidence(pair_no, root_no, path, path_key, deleted_at, probed_at, basis_cursor, content_hash, failure_json) " +
                "VALUES($p, $r, $path, $key, $deleted, $now, $cursor, $hash, $failure) " +
                "ON CONFLICT(pair_no, root_no, path_key) DO UPDATE SET path=$path, deleted_at=$deleted, probed_at=$now, basis_cursor=$cursor, content_hash=$hash, failure_json=$failure;",
                ("$p", pairNo), ("$r", rootNo), ("$path", record.Path), ("$key", PathKey.Of(record.Path)),
                ("$deleted", record.DeletedUtc?.ToUnixTimeMilliseconds()), ("$now", now), ("$cursor", record.BasisCursor),
                ("$hash", record.ContentHash),
                ("$failure", record.Failure is null ? null : System.Text.Json.JsonSerializer.Serialize(record.Failure)));
            batch.Step();
        }
        batch.Flush();
    }
}
