using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Keyset paging releases the connection between batches. No million-row retry snapshot.
    public IEnumerable<SyncRetry> ReadSyncRetries(string pairId)
    {
        var after = "";
        var first = true;
        while (true)
        {
            var batch = InStore(() =>
            {
                var rows = new List<(string Key, SyncRetry Retry)>();
                using var command = StoreSchema.Command(_connection, null,
                    "SELECT path_key,json FROM sync_retry WHERE pair_no=$p AND path_key" + (first ? ">=" : ">") + "$after ORDER BY path_key LIMIT 256;");
                command.Parameters.AddWithValue("$p", RequirePair(pairId));
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetString(0), JsonSerializer.Deserialize<SyncRetry>(reader.GetString(1), RowCodec.Json)!));
                return rows;
            });
            if (batch.Count == 0) yield break;
            foreach (var row in batch) yield return row.Retry;
            after = batch[^1].Key;
            first = false;
        }
    }

    public IEnumerable<string> ReadSyncRetryPaths(string pairId)
    {
        var after = "";
        var first = true;
        while (true)
        {
            var batch = InStore(() =>
            {
                var rows = new List<string>();
                using var command = StoreSchema.Command(_connection, null,
                    "SELECT path_key FROM sync_retry WHERE pair_no=$p AND path_key" + (first ? ">=" : ">") + "$after ORDER BY path_key LIMIT 256;");
                command.Parameters.AddWithValue("$p", RequirePair(pairId));
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add(reader.GetString(0));
                return rows;
            });
            if (batch.Count == 0) yield break;
            foreach (var path in batch) yield return path;
            after = batch[^1];
            first = false;
        }
    }

    public bool SyncRetryExists(string pairId, string path) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM sync_retry WHERE pair_no=$p AND path_key=$k;",
            ("$p", RequirePair(pairId)), ("$k", RowCodec.Key(path))) is not null);
    public void RequestSyncRetryNow(string pairId, string path) => InStore(() =>
    {
        // Read/modify/write under the same store gate: never resurrect a row completed by a worker.
        if (SyncRetryAt(pairId, path) is { } retry)
            SaveSyncRetry(pairId, retry with { RetryAfter = DateTimeOffset.MinValue });
    });

    public void SaveSyncRetry(string pairId, SyncRetry retry) => InStore(() =>
        StoreSchema.Execute(_connection, null,
            "INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES($p,$key,$json,$due) " +
            "ON CONFLICT(pair_no,path_key) DO UPDATE SET json=excluded.json,retry_after=excluded.retry_after;",
            ("$p", RequirePair(pairId)), ("$key", RowCodec.Key(retry.Path)), ("$due", retry.RetryAfter.ToUnixTimeMilliseconds()),
            ("$json", JsonSerializer.Serialize(retry, RowCodec.Json))));

    public SyncRetry? SyncRetryAt(string pairId, string path) => InStore(() =>
    {
        var json = StoreSchema.Scalar(_connection, null,
            "SELECT json FROM sync_retry WHERE pair_no=$p AND path_key=$k;",
            ("$p", RequirePair(pairId)), ("$k", RowCodec.Key(path))) as string;
        return json is null ? null : JsonSerializer.Deserialize<SyncRetry>(json, RowCodec.Json);
    });

    public IReadOnlyList<SyncRetry> DueSyncRetries(string pairId, DateTimeOffset now, int limit) => InStore(() =>
    {
        var rows = new List<SyncRetry>();
        using var command = StoreSchema.Command(_connection, null,
            "SELECT json FROM sync_retry WHERE pair_no=$p AND retry_after<=$now ORDER BY retry_after,path_key LIMIT $limit;");
        command.Parameters.AddWithValue("$p", RequirePair(pairId));
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 256));
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<SyncRetry>(reader.GetString(0), RowCodec.Json)!);
        return (IReadOnlyList<SyncRetry>)rows;
    });

    public int CountSyncRetries(string pairId) => InStore(() => (int)Math.Min(int.MaxValue,
        (long)StoreSchema.Scalar(_connection, null, "SELECT COUNT(*) FROM sync_retry WHERE pair_no=$p AND retry_after<$held;",
            ("$p", RequirePair(pairId)), ("$held", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()))!));

    public bool RetryProtected(string pairId, string path, IReadOnlySet<string> active, bool descendants, bool restoredOnly = false) => InStore(() =>
    {
        var pairNo = RequirePair(pairId);
        var key = RowCodec.Key(path);
        var filter = " AND NOT (retry_after=" + DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() +
            " AND COALESCE(json_extract(json,'$.ReprobeEvidence'),0)=1)" +
            (restoredOnly ? " AND json_extract(json,'$.ReprobeEvidence')=1" : "");
        if (!descendants)
        {
            foreach (var parent in new[] { path }.Concat(PairPath.Ancestors(path)))
                if (!active.Contains(parent) && StoreSchema.Scalar(_connection, null,
                    "SELECT 1 FROM sync_retry WHERE pair_no=$p AND path_key=$k" + filter + ";",
                    ("$p", pairNo), ("$k", RowCodec.Key(parent))) is not null) return true;
            return false;
        }
        // At most active.Count rows can be ignored. Read only one more than that, never the whole subtree.
        using var command = StoreSchema.Command(_connection, null,
            "SELECT path_key FROM sync_retry WHERE pair_no=$p AND path_key >= $lo AND path_key < $hi " +
            filter + " ORDER BY path_key LIMIT $limit;");
        command.Parameters.AddWithValue("$p", pairNo);
        command.Parameters.AddWithValue("$lo", key.Length == 0 ? "" : key + "/");
        command.Parameters.AddWithValue("$hi", key.Length == 0 ? "￿" : key + "0");
        command.Parameters.AddWithValue("$limit", active.Count + 1);
        using var reader = command.ExecuteReader();
        while (reader.Read()) if (!active.Contains(reader.GetString(0))) return true;
        return false;
    });

    public bool HasActiveRestoredRetries(string pairId) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT 1 FROM sync_retry WHERE pair_no=$p AND retry_after<$held " +
            "AND json_extract(json,'$.ReprobeEvidence')=1 LIMIT 1;",
            ("$p", RequirePair(pairId)), ("$held", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())) is not null);

    public void SuspendHeldRetry(string pairId, SyncRetry retry) => InStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        var dormant = new SyncRetry(retry.Path, "deletion-evidence", 0, retry.LastError,
            DateTimeOffset.MaxValue, Held: retry.Held, ReprobeEvidence: true);
        StoreSchema.Execute(_connection, transaction,
            "INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES($p,$k,$j,$due) " +
            "ON CONFLICT(pair_no,path_key) DO UPDATE SET json=excluded.json,retry_after=excluded.retry_after;",
            ("$p", RequirePair(pairId)), ("$k", RowCodec.Key(retry.Path)),
            ("$j", JsonSerializer.Serialize(dormant, RowCodec.Json)), ("$due", dormant.RetryAfter.ToUnixTimeMilliseconds()));
        // Close the derived notice ids exactly as ClearSyncRetry does, so the update uses nt_open and
        // never rewrites the choice of an already-closed notice.
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='closed',choice='exclude' WHERE notice_id=$id AND state='open';",
            ("$id", ShortId.Make("n", pairId, "40", RowCodec.Key(retry.Path), "sync-retry")));
        foreach (var rule in new[] { "4", "19" })
            StoreSchema.Execute(_connection, transaction,
                "UPDATE notice SET state='closed',choice='exclude' WHERE notice_id=$id AND state='open';",
                ("$id", ShortId.Make("n", pairId, rule, RowCodec.Key(retry.Path), "deletion-evidence-pending")));
        transaction.Commit();
    });

    public void ClearSyncRetry(string pairId, string path) => InStore(() =>
    {
        using var transaction = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, transaction, "DELETE FROM sync_retry WHERE pair_no=$p AND path_key=$key;",
            ("$p", RequirePair(pairId)), ("$key", RowCodec.Key(path)));
        StoreSchema.Execute(_connection, transaction,
            "UPDATE notice SET state='closed',choice='resolved' WHERE notice_id=$id;",
            ("$id", ShortId.Make("n", pairId, "40", RowCodec.Key(path), "sync-retry")));
        foreach (var rule in new[] { "4", "19" })
            StoreSchema.Execute(_connection, transaction,
                "UPDATE notice SET state='closed',choice='resolved' WHERE notice_id=$id;",
                ("$id", ShortId.Make("n", pairId, rule, RowCodec.Key(path), "deletion-evidence-pending")));
        transaction.Commit();
    });
}
