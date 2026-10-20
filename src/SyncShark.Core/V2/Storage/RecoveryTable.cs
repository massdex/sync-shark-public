using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Owns the recovery table: one durable row per archived copy (RQ-060).</summary>
internal sealed class RecoveryTable
{
    private readonly SqliteConnection _connection;

    public RecoveryTable(SqliteConnection connection) => _connection = connection;

    /// <summary>Records an archived copy once; a repeat for the same action id is idempotent.</summary>
    public void Insert(SqliteTransaction transaction, long pairNo, string pairId, SyncAction action, RecoveryRef recovery,
        long now, TimeSpan retention)
    {
        var recoveryId = ShortId.Make("rc", pairId, action.ActionId);
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO recovery(recovery_id, pair_no, action_id, side, path, location, version_id, hash, size, created, expires) " +
            "VALUES($rid, $p, $aid, $side, $path, $loc, $vid, $hash, $size, $created, $expires) ON CONFLICT(recovery_id) DO NOTHING;",
            ("$rid", recoveryId), ("$p", pairNo), ("$aid", action.ActionId), ("$side", recovery.Side.ToString()),
            ("$path", action.Path), ("$loc", recovery.Location), ("$vid", recovery.VersionId), ("$hash", recovery.Hash),
            ("$size", recovery.Size), ("$created", now), ("$expires", now + (long)retention.TotalMilliseconds));
        if (StoreSchema.Changes(_connection, transaction) != 0) return;
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT pair_no, action_id, location FROM recovery WHERE recovery_id=$rid;");
        command.Parameters.AddWithValue("$rid", recoveryId);
        using var reader = command.ExecuteReader();
        if (reader.Read() && reader.GetInt64(0) == pairNo &&
            string.Equals(reader.GetString(1), action.ActionId, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(2), recovery.Location, StringComparison.Ordinal))
            return;
        throw new InvalidDataException($"Short id collision: {recoveryId}");
    }

    /// <summary>The recorded copies of a pair, oldest first.</summary>
    public IReadOnlyList<RecoveryRecord> Read(long pairNo, string pairId)
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT recovery_id, action_id, side, path, location, version_id, hash, size, created, expires " +
            "FROM recovery WHERE pair_no=$p ORDER BY created, recovery_id;");
        command.Parameters.AddWithValue("$p", pairNo);
        var records = new List<RecoveryRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            records.Add(new RecoveryRecord(reader.GetString(0), pairId, reader.GetString(1),
                Enum.Parse<Side>(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9))));
        return records;
    }
}
