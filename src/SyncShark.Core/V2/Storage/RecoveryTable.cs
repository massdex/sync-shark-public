using System.Text;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Owns the recovery table: one durable row per archived copy (RQ-060).</summary>
internal sealed class RecoveryTable
{
    private readonly SqliteConnection _connection;

    public RecoveryTable(SqliteConnection connection) => _connection = connection;

    /// <summary>Records an archived copy once; a repeat for the same action id is idempotent.</summary>
    public void Insert(SqliteTransaction transaction, long pairNo, string pairId, SyncAction action, RecoveryRef recovery,
        long now, TimeSpan retention) =>
        Insert(transaction, pairNo, pairId, action.ActionId, action.Path, recovery, now, retention);

    /// <summary>Records one archived copy by its owner ids; used before an original is removed.</summary>
    public void Insert(SqliteTransaction transaction, long pairNo, string pairId, string actionId, string path,
        RecoveryRef recovery, long now, TimeSpan retention)
    {
        var recoveryId = ShortId.Make("rc", pairId, actionId);
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO recovery(recovery_id, pair_no, action_id, side, path, path_key, location, version_id, hash, size, created, expires) " +
            "VALUES($rid, $p, $aid, $side, $path, $path_key, $loc, $vid, $hash, $size, $created, $expires) ON CONFLICT(recovery_id) DO NOTHING;",
            ("$rid", recoveryId), ("$p", pairNo), ("$aid", actionId), ("$side", recovery.Side.ToString()),
            ("$path", path), ("$path_key", PathKey.Of(path)), ("$loc", recovery.Location), ("$vid", recovery.VersionId), ("$hash", recovery.Hash),
            ("$size", recovery.Size), ("$created", now), ("$expires", now + (long)retention.TotalMilliseconds));
        if (StoreSchema.Changes(_connection, transaction) != 0) return;
        using var command = StoreSchema.Command(_connection, transaction,
            "SELECT pair_no, action_id, location FROM recovery WHERE recovery_id=$rid;");
        command.Parameters.AddWithValue("$rid", recoveryId);
        using var reader = command.ExecuteReader();
        if (reader.Read() && reader.GetInt64(0) == pairNo &&
            string.Equals(reader.GetString(1), actionId, StringComparison.Ordinal) &&
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

    /// <summary>
    /// One bounded keyset page of a pair's rows in recovery-id order. The cursor is exclusive and every filter is a
    /// parameter; the path prefix is a literal self-or-under test, never a LIKE pattern.
    /// </summary>
    public IReadOnlyList<RecoveryRecord> ReadPage(long pairNo, string pairId, RecoveryQuery query)
    {
        var (sql, parameters) = Filter(pairNo, query, applyAfter: true, exactPath: null);
        sql.Append(" ORDER BY recovery_id LIMIT $limit;");
        parameters.Add(("$limit", query.Limit));
        return Read(sql, parameters, pairId);
    }

    /// <summary>
    /// The newest filtered row for one exact path (finding 1). AfterId and Limit are ignored on purpose: the winner
    /// may live on another page. Newest is Created descending, then recovery id descending, so the answer is
    /// deterministic. The path comparison uses the same case-insensitive key as every other path predicate.
    /// </summary>
    public RecoveryRecord? ReadNewestForPath(long pairNo, string pairId, RecoveryQuery query, string path)
    {
        var (sql, parameters) = Filter(pairNo, query, applyAfter: false, exactPath: path);
        sql.Append(" ORDER BY created DESC, recovery_id DESC LIMIT 1;");
        return Read(sql, parameters, pairId).FirstOrDefault();
    }

    private static (StringBuilder Sql, List<(string Name, object? Value)> Parameters) Filter(long pairNo,
        RecoveryQuery query, bool applyAfter, string? exactPath)
    {
        var sql = new StringBuilder(
            "SELECT recovery_id, action_id, side, path, location, version_id, hash, size, created, expires " +
            "FROM recovery WHERE pair_no=$p");
        var parameters = new List<(string Name, object? Value)> { ("$p", pairNo) };
        if (query.Id is { } id)
        {
            sql.Append(" AND recovery_id=$id");
            parameters.Add(("$id", id));
        }
        if (query.SinceMillisecondsCeiling() is { } since)
        {
            sql.Append(" AND created>=$since");
            parameters.Add(("$since", since));
        }
        if (query.PathPrefix is { } prefix)
        {
            // Equality or "prefix/" at the start. substr/length keep '%' and '_' ordinary characters, unlike LIKE.
            sql.Append(" AND (choice_key(path)=choice_key($prefix) OR " +
                "substr(choice_key(path),1,length(choice_key($prefix))+1)=choice_key($prefix)||'/')");
            parameters.Add(("$prefix", prefix));
        }
        if (query.Side is { } side)
        {
            sql.Append(" AND side=$side");
            parameters.Add(("$side", side.ToString()));
        }
        if (query.ActionId is { } actionId)
        {
            sql.Append(" AND action_id=$action");
            parameters.Add(("$action", actionId));
        }
        if (exactPath is not null)
        {
            // Indexed by (pair_no, path_key); the key is the same case-insensitive identity as the path predicates.
            sql.Append(" AND path_key=$exact");
            parameters.Add(("$exact", PathKey.Of(exactPath)));
        }
        if (applyAfter && query.AfterId is { } afterId)
        {
            sql.Append(" AND recovery_id>$after");
            parameters.Add(("$after", afterId));
        }
        return (sql, parameters);
    }

    private IReadOnlyList<RecoveryRecord> Read(StringBuilder sql,
        List<(string Name, object? Value)> parameters, string pairId)
    {
        using var command = StoreSchema.Command(_connection, null, sql.ToString());
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
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
